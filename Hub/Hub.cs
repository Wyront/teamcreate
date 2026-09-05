using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace TeamCreate.Hub;

/// <summary>
/// Sequenced relay hub: rooms, presence/file/patch fan-out, versioned
/// snapshots for late joiners, per-socket send serialization, message caps.
/// Transport (handshake, fragmentation, ping/pong) is owned by Kestrel.
/// </summary>
public sealed class TeamCreateHub
{
	public const int MaxMessageBytes = 8 * 1024 * 1024;
	public const long MaxCacheBytesPerRoom = 200L * 1024 * 1024;
	public const int MaxCacheFilesPerRoom = 1000;
	private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds( 5 );

	private sealed class Room
	{
		public readonly ConcurrentDictionary<string, Client> Clients = new( StringComparer.Ordinal );
		public readonly ConcurrentDictionary<string, byte[]> FileCache = new( StringComparer.OrdinalIgnoreCase );
		public long CacheBytes;
		public readonly ConcurrentDictionary<string, LockEntry> Locks = new( StringComparer.OrdinalIgnoreCase );
		public readonly ConcurrentDictionary<string, ChunkTransfer> ChunkCache = new( StringComparer.OrdinalIgnoreCase );
	}

	private sealed class LockEntry
	{
		public string OwnerId = "";
		public string OwnerName = "";
		public DateTime ExpiresUtc;
	}

	private sealed class ChunkTransfer
	{
		public string SenderId = "";
		public int Total;
		public string Hash = "";
		public long Size;
		public byte[][]? Chunks;
		public int Received;
	}

	/// <summary>Transport-agnostic send endpoint (WebSocket, Steam P2P, ...).</summary>
	public interface ILink
	{
		bool IsOpen { get; }
		Task SendAsync( byte[] utf8Json, CancellationToken ct );
		Task CloseAsync();
	}

	public sealed class WsLink : ILink
	{
		private readonly WebSocket _ws;
		public WsLink( WebSocket ws ) { _ws = ws; }
		public bool IsOpen => _ws.State == WebSocketState.Open;
		public Task SendAsync( byte[] data, CancellationToken ct ) =>
			_ws.SendAsync( data, WebSocketMessageType.Text, endOfMessage: true, ct );
		public async Task CloseAsync()
		{
			try { await _ws.CloseOutputAsync( WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None ); }
			catch { }
		}
	}

	public sealed class Client
	{
		public string Id = Convert.ToHexString( RandomNumberGenerator.GetBytes( 4 ) ).ToLowerInvariant();
		public required ILink Link;
		public readonly SemaphoreSlim SendLock = new( 1, 1 );
		public PeerInfo? Info;
		public string RoomName = "default";
		public string? AuthHash;
	}

	private readonly ConcurrentDictionary<string, Room> _rooms = new( StringComparer.OrdinalIgnoreCase );
	private readonly byte[]? _passwordHashBytes;

	public TeamCreateHub( string? passwordHashHex, CancellationToken shutdown = default )
	{
		if ( passwordHashHex != null )
		{
			try { _passwordHashBytes = Convert.FromHexString( passwordHashHex ); }
			catch { _passwordHashBytes = null; }
		}
		Console.WriteLine( $"Hub: password {(_passwordHashBytes != null ? "установлен" : "не установлен")}" );

		// Expired scene locks are released lazily on request + by this sweep
		_shutdown = shutdown;
		_ = Task.Run( LockSweepLoop );
	}

	private readonly CancellationToken _shutdown;

	private async Task LockSweepLoop()
	{
		while ( !_shutdown.IsCancellationRequested )
		{
			try { await Task.Delay( TimeSpan.FromSeconds( 10 ), _shutdown ); }
			catch ( OperationCanceledException ) { break; }
			catch { break; }

			var now = DateTime.UtcNow;
			foreach ( var (roomName, room) in _rooms.ToArray() )
			{
				foreach ( var (path, entry) in room.Locks.ToArray() )
				{
					if ( entry.ExpiresUtc < now &&
						 room.Locks.TryRemove( KeyValuePair.Create( path, entry ) ) )
					{
						Console.WriteLine( $"Hub: [{roomName}] lock expired: {path} ({entry.OwnerName})" );
						_ = BroadcastAsync( room, new Message
						{
							Type = "unlock",
							Path = path,
							From = entry.OwnerId,
						}, exceptId: null, CancellationToken.None );
					}
				}
			}
		}
	}

	public async Task HandleAsync( WebSocket ws, CancellationToken ct )
	{
		var client = AddLinkClient( new WsLink( ws ) );

		var buf = new byte[64 * 1024];
		try
		{
			while ( ws.State == WebSocketState.Open && !ct.IsCancellationRequested )
			{
				Message? msg;
				using ( var ms = new MemoryStream() )
				{
					WebSocketReceiveResult result;
					do
					{
						result = await ws.ReceiveAsync( buf, ct );
					if ( result.MessageType == WebSocketMessageType.Close )
					{
						try { await client.Link.CloseAsync(); } catch { }
						return;
					}
						if ( ms.Length + result.Count > MaxMessageBytes )
						{
							Console.WriteLine( $"Hub: [{client.Id}] message too large, closing" );
							try { await ws.CloseOutputAsync( WebSocketCloseStatus.MessageTooBig, "too big", CancellationToken.None ); } catch { }
							return;
						}
						ms.Write( buf, 0, result.Count );
					}
					while ( !result.EndOfMessage );

					if ( result.MessageType != WebSocketMessageType.Text ) continue;

					string text;
					try { text = Encoding.UTF8.GetString( ms.GetBuffer(), 0, unchecked( (int)ms.Length ) ); }
					catch { continue; }

					msg = Message.FromJson( text );
				}

				if ( msg == null ) continue;
				await HandleLinkMessage( client, msg, ct );
			}
		}
		catch ( OperationCanceledException ) { }
		catch ( WebSocketException ) { }
		finally
		{
			LeaveRoom( client, broadcastLeft: true );
		}
	}

	public Client AddLinkClient( ILink link )
	{
		var client = new Client { Link = link };
		Console.WriteLine( $"Hub: new connection {client.Id}" );
		return client;
	}

	public async Task HandleLinkMessage( Client client, Message msg, CancellationToken ct )
	{
		try
		{
			await HandleMessageAsync( client, msg, ct );
		}
		catch ( Exception ex )
		{
			Console.WriteLine( $"Hub: [{client.Id}] handler error: {ex.GetType().Name}: {ex.Message}" );
		}
	}

	public void RemoveLinkClient( Client client ) => LeaveRoom( client, broadcastLeft: true );

	private bool VerifyPassword( string? clientHashHex )
	{
		if ( _passwordHashBytes == null ) return true;
		if ( string.IsNullOrEmpty( clientHashHex ) ) return false;
		byte[] clientBytes;
		try { clientBytes = Convert.FromHexString( clientHashHex ); }
		catch { return false; }
		return CryptographicOperations.FixedTimeEquals( _passwordHashBytes, clientBytes );
	}

	private async Task HandleMessageAsync( Client client, Message msg, CancellationToken ct )
	{
		if ( msg.Type == "join" )
		{
			if ( !VerifyPassword( msg.PasswordHash ) )
			{
				Console.WriteLine( $"Hub: [{client.Id}] auth REJECTED" );
				await SendAsync( client, new Message { Type = "auth-rejected", Reason = "Invalid password" }, ct );
				try { await client.Link.CloseAsync(); } catch { }
				return;
			}

			// Re-join = leave old room first (no ghosts, no double membership)
			if ( client.Info != null )
				LeaveRoom( client, broadcastLeft: true );

			client.AuthHash = msg.PasswordHash;
			client.RoomName = string.IsNullOrWhiteSpace( msg.Room ) ? "default" : msg.Room!;
			client.Info = new PeerInfo
			{
				Id = client.Id,
				Name = string.IsNullOrWhiteSpace( msg.Name ) ? $"user-{client.Id}" : msg.Name!,
				Color = msg.Color ?? "#ffffff",
			};

			var room = _rooms.GetOrAdd( client.RoomName, _ => new Room() );
			room.Clients[client.Id] = client;

			var others = room.Clients.Values
				.Where( c => c.Id != client.Id && c.Info != null )
				.Select( c => c.Info! )
				.ToList();

			Console.WriteLine( $"Hub: [{client.RoomName}] {client.Info.Name} joined (online: {room.Clients.Count})" );

			// Atomic snapshot: welcome -> cached files -> cached chunk transfers -> sync-end barrier
			await SendAsync( client, new Message
			{
				Type = "welcome",
				From = client.Id,
				Peers = others,
				Locks = room.Locks.Select( kv => new SceneLockInfo
				{
					Path = kv.Key,
					Owner = kv.Value.OwnerId,
					OwnerName = kv.Value.OwnerName,
				} ).ToList(),
			}, ct );

			var snapshot = room.FileCache.ToArray();
			foreach ( var (path, data) in snapshot )
			{
				await SendAsync( client, new Message
				{
					Type = "file",
					Path = path,
					ContentB64 = Convert.ToBase64String( data ),
				}, ct );
			}

			foreach ( var (path, transfer) in room.ChunkCache.ToArray() )
			{
				byte[][]? chunks;
				int received;
				lock ( transfer )
				{
					chunks = transfer.Chunks?.ToArray();
					received = transfer.Received;
				}
				// Replay only complete transfers: a partial one would stall the
				// newcomer forever (sender only resends on local change).
				if ( chunks == null || received != transfer.Total ) continue;

				await SendAsync( client, new Message
				{
					Type = "file-manifest",
					Path = path,
					FileSize = transfer.Size,
					FileHash = transfer.Hash,
					ChunkTotal = transfer.Total,
				}, ct );

				for ( int i = 0; i < chunks.Length; i++ )
				{
					if ( chunks[i] == null ) break; // incomplete transfer — skip rest
					await SendAsync( client, new Message
					{
						Type = "file-chunk",
						Path = path,
						ChunkIndex = i,
						ChunkTotal = transfer.Total,
						ContentB64 = Convert.ToBase64String( chunks[i] ),
						Final = i == chunks.Length - 1,
					}, ct );
				}
			}
			await SendAsync( client, new Message { Type = "sync-end" }, ct );

			await BroadcastAsync( room, new Message { Type = "peer-joined", Peer = client.Info }, exceptId: client.Id, ct );
			return;
		}

		// Everything else requires a completed join + valid auth on every message
		if ( client.Info == null ) return;
		if ( !VerifyPassword( msg.PasswordHash ) || msg.PasswordHash != client.AuthHash )
		{
			Console.WriteLine( $"Hub: [{client.Id}] message auth REJECTED" );
			await SendAsync( client, new Message { Type = "auth-rejected", Reason = "Invalid password" }, ct );
			try { await client.Link.CloseAsync(); } catch { }
			LeaveRoom( client, broadcastLeft: false );
			return;
		}

		var r = _rooms.GetOrAdd( client.RoomName, _ => new Room() );

		switch ( msg.Type )
		{
			case "lock" when msg.Path != null:
			{
				await HandleLockAsync( r, client, msg.Path, ct );
				return;
			}

			case "unlock" when msg.Path != null:
			{
				var key = NormPath( msg.Path );
				if ( r.Locks.TryGetValue( key, out var entry ) && entry.OwnerId == client.Id )
				{
					r.Locks.TryRemove( key, out _ );
					Console.WriteLine( $"Hub: [{client.RoomName}] unlock: {key} ({client.Info!.Name})" );
					msg.From = client.Id;
					msg.Path = key;
					await BroadcastAsync( r, msg, exceptId: null, ct );
				}
				return;
			}

			case "file" when msg.Path != null && msg.ContentB64 != null:
				if ( IsLockedByOther( r, msg.Path, client.Id, out var locker ) )
				{
					await SendAsync( client, new Message { Type = "lock-denied", Path = msg.Path, Reason = locker }, ct );
					return;
				}
				// Patches are live-only deltas, never cached (stale patches corrupt newcomers)
				if ( !msg.Path.StartsWith( "__patches__/", StringComparison.OrdinalIgnoreCase ) )
					CacheFile( r, msg.Path, msg.ContentB64 );
				break;

			case "file-delete" when msg.Path != null:
				if ( IsLockedByOther( r, msg.Path, client.Id, out var lockerDel ) )
				{
					await SendAsync( client, new Message { Type = "lock-denied", Path = msg.Path, Reason = lockerDel }, ct );
					return;
				}
				if ( r.FileCache.TryRemove( msg.Path, out var removed ) )
					Interlocked.Add( ref r.CacheBytes, -removed.Length );
				DropChunkTransfer( r, msg.Path );
				break;

			case "file-manifest" when msg.Path != null && msg.ChunkTotal > 0 && msg.ChunkTotal <= 512 && msg.FileHash != null:
				if ( IsLockedByOther( r, msg.Path, client.Id, out var lockerM ) )
				{
					await SendAsync( client, new Message { Type = "lock-denied", Path = msg.Path, Reason = lockerM }, ct );
					return;
				}
				DropChunkTransfer( r, msg.Path ); // new transfer supersedes the old one
				r.ChunkCache[msg.Path] = new ChunkTransfer
				{
					SenderId = client.Id,
					Total = msg.ChunkTotal.Value,
					Hash = msg.FileHash,
					Size = msg.FileSize ?? 0,
					Chunks = new byte[msg.ChunkTotal.Value][],
					Received = 0,
				};
				break;

			case "file-chunk" when msg.Path != null && msg.ChunkIndex >= 0 && msg.ContentB64 != null:
				if ( IsLockedByOther( r, msg.Path, client.Id, out var lockerC ) )
				{
					await SendAsync( client, new Message { Type = "lock-denied", Path = msg.Path, Reason = lockerC }, ct );
					return;
				}
				if ( r.ChunkCache.TryGetValue( msg.Path, out var transfer ) &&
					 transfer.SenderId == client.Id &&
					 msg.ChunkIndex < transfer.Total )
				{
					byte[] bytes;
					try { bytes = Convert.FromBase64String( msg.ContentB64 ); }
					catch { return; }
					if ( bytes.Length > MaxMessageBytes ) return;
					lock ( transfer )
					{
						if ( transfer.Chunks![msg.ChunkIndex.Value] == null )
						{
							// Chunk bytes count toward the room cache budget
							var projected = Interlocked.Read( ref r.CacheBytes ) + bytes.Length;
							if ( projected > MaxCacheBytesPerRoom )
							{
								Console.WriteLine( $"Hub: cache full, dropping chunk: {msg.Path}#{msg.ChunkIndex}" );
								return;
							}
							transfer.Chunks[msg.ChunkIndex.Value] = bytes;
							transfer.Received++;
							Interlocked.Add( ref r.CacheBytes, bytes.Length );
						}
					}
				}
				break;

			case "file":
			case "file-delete":
			case "file-manifest":
			case "file-chunk":
				// malformed (missing path/content) — drop
				return;

			default:
				// presence / transform / chat / future types: relay as-is (no caching)
				break;
		}

		msg.From = client.Id;
		await BroadcastAsync( r, msg, exceptId: client.Id, ct );
	}

	private static string NormPath( string path ) =>
		path.Replace( '\\', '/' ).Trim( '/' ).ToLowerInvariant();

	/// <summary>Scene-level soft lock check. Patch paths (__patches__/X.scene.patch) map to their scene file.</summary>
	private static bool IsLockedByOther( Room room, string path, string clientId, out string lockerName )
	{
		lockerName = "";
		var now = DateTime.UtcNow;

		string key = NormPath( path );
		string? fileName = null;
		if ( key.StartsWith( "__patches__/" ) && key.EndsWith( ".patch" ) )
			fileName = key.Substring( "__patches__/".Length, key.Length - "__patches__/".Length - ".patch".Length );

		foreach ( var (lockPath, entry) in room.Locks.ToArray() )
		{
			if ( entry.ExpiresUtc < now ) continue;
			if ( entry.OwnerId == clientId ) continue;

			if ( lockPath == key || (fileName != null && lockPath.EndsWith( "/" + fileName )) )
			{
				lockerName = entry.OwnerName;
				return true;
			}
		}
		return false;
	}

	private async Task HandleLockAsync( Room room, Client client, string path, CancellationToken ct )
	{
		var key = NormPath( path );
		var now = DateTime.UtcNow;

		if ( room.Locks.TryGetValue( key, out var existing ) &&
			 existing.ExpiresUtc >= now && existing.OwnerId != client.Id )
		{
			await SendAsync( client, new Message { Type = "lock-denied", Path = key, Reason = existing.OwnerName }, ct );
			return;
		}

		room.Locks[key] = new LockEntry
		{
			OwnerId = client.Id,
			OwnerName = client.Info!.Name,
			ExpiresUtc = now.AddSeconds( 30 ),
		};

		Console.WriteLine( $"Hub: [{client.RoomName}] lock: {key} ({client.Info.Name})" );
		await BroadcastAsync( room, new Message
		{
			Type = "lock",
			Path = key,
			From = client.Id,
			Locks = new List<SceneLockInfo> { new() { Path = key, Owner = client.Id, OwnerName = client.Info.Name } },
		}, exceptId: null, ct );
	}

	private static void CacheFile( Room room, string path, string contentB64 )
	{
		byte[] data;
		try { data = Convert.FromBase64String( contentB64 ); }
		catch { return; }

		if ( data.Length > MaxMessageBytes ) return;

		// A full file supersedes any chunked transfer of the same path
		if ( room.ChunkCache.TryRemove( path, out var oldTransfer ) && oldTransfer.Chunks != null )
		{
			lock ( oldTransfer )
			{
				foreach ( var c in oldTransfer.Chunks )
				{
					if ( c != null ) Interlocked.Add( ref room.CacheBytes, -c.Length );
				}
			}
		}

		// Bounded cache: evict nothing fancy — stop caching when full (relay continues)
		var current = room.FileCache.TryGetValue( path, out var old ) ? old.Length : 0;
		var projected = Interlocked.Read( ref room.CacheBytes ) - current + data.Length;
		if ( projected > MaxCacheBytesPerRoom || (room.FileCache.Count >= MaxCacheFilesPerRoom && current == 0) )
		{
			Console.WriteLine( $"Hub: cache full, relaying without caching: {path}" );
			return;
		}

		room.FileCache[path] = data;
		Interlocked.Add( ref room.CacheBytes, data.Length - current );
	}

	private static void DropChunkTransfer( Room room, string path )
	{
		if ( room.ChunkCache.TryRemove( path, out var transfer ) && transfer.Chunks != null )
		{
			lock ( transfer )
			{
				foreach ( var c in transfer.Chunks )
				{
					if ( c != null ) Interlocked.Add( ref room.CacheBytes, -c.Length );
				}
			}
		}
	}

	private static async Task BroadcastAsync( Room room, Message msg, string? exceptId, CancellationToken ct )
	{
		var targets = room.Clients.Values
			.Where( c => c.Id != exceptId && c.Info != null && c.Link.IsOpen )
			.ToList();

		// Parallel fan-out: one slow client must not stall the room (head-of-line blocking)
		await Task.WhenAll( targets.Select( c => SendAsync( c, msg, ct ) ) );
	}

	private static async Task SendAsync( Client client, Message msg, CancellationToken ct )
	{
		if ( !client.Link.IsOpen ) return;

		var bytes = Encoding.UTF8.GetBytes( msg.ToJson() );

		using var cts = CancellationTokenSource.CreateLinkedTokenSource( ct );
		cts.CancelAfter( SendTimeout );

		await client.SendLock.WaitAsync( cts.Token );
		try
		{
			if ( !client.Link.IsOpen ) return;
			await client.Link.SendAsync( bytes, cts.Token );
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception ) { } // transport-agnostic: WS, Steam, ... must never kill the hub
		finally
		{
			client.SendLock.Release();
		}
	}

	private void LeaveRoom( Client client, bool broadcastLeft )
	{
		if ( client.Info == null ) return;

		if ( _rooms.TryGetValue( client.RoomName, out var room ) )
		{
			room.Clients.TryRemove( client.Id, out _ );

			// Release scene locks held by the leaver
			foreach ( var (path, entry) in room.Locks.ToArray() )
			{
				if ( entry.OwnerId == client.Id && room.Locks.TryRemove( path, out _ ) )
				{
					_ = BroadcastAsync( room, new Message
					{
						Type = "unlock",
						Path = path,
						From = client.Id,
					}, exceptId: null, CancellationToken.None );
				}
			}

			if ( broadcastLeft )
			{
				Console.WriteLine( $"Hub: [{client.RoomName}] {client.Info.Name} left" );
				_ = BroadcastAsync( room,
					new Message { Type = "peer-left", Peer = client.Info },
					exceptId: client.Id, CancellationToken.None );
			}

			// Free the snapshot cache when the room empties
			if ( room.Clients.IsEmpty )
				_rooms.TryRemove( client.RoomName, out _ );
		}

		client.Info = null;
	}
}
