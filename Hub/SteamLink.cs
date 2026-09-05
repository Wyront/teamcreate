using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;
using Steamworks.Data;
using SteamConnInfo = Steamworks.Data.ConnectionInfo;

namespace TeamCreate.Hub;

/// <summary>
/// Our Porthole-analog: carries the hub Message protocol over Steam Datagram
/// Relay (Valve's backbone) instead of raw TCP. No port forwarding, no public IP,
/// no extra software beyond Steam itself. Editor clients keep talking plain
/// WebSocket to 127.0.0.1 — the Steam fabric lives entirely inside hub.exe.
///
/// Framing over SDR reliable channel: [4-byte LE length][UTF8 JSON], split into
/// chunks (SDR reliable messages cap at ~512KB). Ordering is guaranteed per
/// connection, so a length-prefix stream reassembles exactly.
/// </summary>
public static class SteamLink
{
	public const int VirtualPort = 0;
	public const int ChunkSize = 200 * 1024;

	private static bool _steamInit;
	private static readonly object _steamLock = new();

	/// <summary>Init Steamworks once per process (hub.exe owns its Steam context).</summary>
	public static bool Init( uint appid, out string error )
	{
		error = "";
		lock ( _steamLock )
		{
			if ( _steamInit ) return true;
			try
			{
				SteamClient.Init( appid );
			}
			catch ( Exception ex )
			{
				error = $"Steam init failed (is Steam running and logged in?): {ex.Message}";
				return false;
			}
			try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
			catch { }
			_steamInit = true;
			Console.WriteLine( $"Steam: logged in as {SteamClient.Name} ({SteamClient.SteamId.Value})" );
			return true;
		}
	}

	public static void Shutdown()
	{
		lock ( _steamLock )
		{
			if ( !_steamInit ) return;
			_steamInit = false;
			try { SteamClient.Shutdown(); }
			catch { }
		}
	}

	/// <summary>Split a payload into [len][data] chunks for SDR reliable sends.</summary>
	public static List<byte[]> EncodeFrame( byte[] payload )
	{
		var out_ = new List<byte[]>();
		var len = BitConverter.GetBytes( payload.Length );
		var firstRoom = ChunkSize - 4;
		var firstTake = Math.Min( firstRoom, payload.Length );
		var first = new byte[4 + firstTake];
		Buffer.BlockCopy( len, 0, first, 0, 4 );
		Buffer.BlockCopy( payload, 0, first, 4, firstTake );
		out_.Add( first );

		var offset = firstTake;
		while ( offset < payload.Length )
		{
			var take = Math.Min( ChunkSize, payload.Length - offset );
			var chunk = new byte[take];
			Buffer.BlockCopy( payload, offset, chunk, 0, take );
			out_.Add( chunk );
			offset += take;
		}
		return out_;
	}

	/// <summary>Reassembles length-prefixed frames from an ordered byte stream.</summary>
	public sealed class Decoder
	{
		private readonly List<byte> _buf = new();

		public void Push( byte[] chunk, out List<byte[]> messages )
		{
			messages = new List<byte[]>();
			_buf.AddRange( chunk );
			while ( _buf.Count >= 4 )
			{
				var len = BitConverter.ToInt32( _buf.ToArray(), 0 );
				if ( len < 0 || len > TeamCreateHub.MaxMessageBytes )
				{
					// Bad length: resync by scanning for the next plausible payload
					// start ('{') and crawl back 4 bytes to its header. A poisoned or
					// desynced stream must not kill the link forever.
					var brace = _buf.IndexOf( (byte)'{', 1 );
					var cut = brace > 4 ? brace - 4 : 1;
					if ( brace < 0 ) cut = _buf.Count; // no candidate: drop everything
					_buf.RemoveRange( 0, cut );
					continue;
				}
				if ( _buf.Count < 4 + len ) return; // payload not complete yet
				var msg = new byte[len];
				_buf.CopyTo( 4, msg, 0, len );
				_buf.RemoveRange( 0, 4 + len );
				messages.Add( msg );
			}
		}
	}

	// ──── Host side: SDR listen socket feeding HubCore ────

	public sealed class Host : IDisposable
	{
		private sealed class HubSocket : SocketManager
		{
			public Host? Owner;
			public override void OnConnected( Connection c, SteamConnInfo info )
			{
				base.OnConnected( c, info );
				Owner?.OnPeerConnected( c );
			}
			public override void OnDisconnected( Connection c, SteamConnInfo info )
			{
				base.OnDisconnected( c, info );
				Owner?.OnPeerDisconnected( c );
			}
			public override void OnMessage( Connection c, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel )
			{
				var b = new byte[size];
				Marshal.Copy( data, b, 0, size );
				Owner?.OnBytes( c, b );
			}
		}

		private sealed class ServerLink : TeamCreateHub.ILink
		{
			private readonly Connection _conn;
			public volatile bool Open = true;
			public ServerLink( Connection conn ) { _conn = conn; }
			public bool IsOpen => Open;
			public Task SendAsync( byte[] utf8Json, CancellationToken ct )
			{
				foreach ( var chunk in EncodeFrame( utf8Json ) )
				{
					var r = _conn.SendMessage( chunk, SendType.Reliable );
					if ( r != Result.OK )
						throw new IOException( $"Steam send failed: {r}" );
				}
				return Task.CompletedTask;
			}
			public Task CloseAsync() => Task.CompletedTask;
		}

		private readonly TeamCreateHub _hub;
		private readonly CancellationToken _ct;
		private HubSocket? _socket;
		private readonly ConcurrentDictionary<uint, (Connection Conn, TeamCreateHub.Client Client, ServerLink Link, Decoder Dec)> _peers = new();
		private readonly ConcurrentQueue<(uint ConnId, byte[] Chunk)> _inbox = new();
		private Task? _pump;
		private Lobby? _lobby;

		public Host( TeamCreateHub hub, CancellationToken ct )
		{
			_hub = hub;
			_ct = ct;
		}

		/// <summary>Create a public lobby and return the join code (lobby SteamID64).</summary>
		public async Task<string?> CreateLobbyAsync()
		{
			var lobby = await SteamMatchmaking.CreateLobbyAsync( 10 );
			if ( lobby == null ) return null;
			var l = lobby.Value;
			l.SetPublic();
			l.SetData( "tc", "1" );
			l.SetData( "host", SteamClient.SteamId.Value.ToString() );
			l.SetData( "name", "teamcreate" );
			_lobby = l;
			return l.Id.Value.ToString();
		}

		public void Start()
		{
			var socket = SteamNetworkingSockets.CreateRelaySocket<HubSocket>( VirtualPort );
			socket.Owner = this;
			_socket = socket;
			_pump = Task.Run( PumpLoop );
			Console.WriteLine( "Steam: P2P listen socket open (virtual port 0)" );
		}

		private async Task PumpLoop()
		{
			while ( !_ct.IsCancellationRequested )
			{
				try
				{
					_socket?.Receive( 32 );
					while ( _inbox.TryDequeue( out var item ) )
						await DispatchAsync( item.ConnId, item.Chunk );
				}
				catch { }
				try { await Task.Delay( 8, _ct ); }
				catch ( OperationCanceledException ) { break; }
			}
		}

		private void OnPeerConnected( Connection c )
		{
			var link = new ServerLink( c );
			var client = _hub.AddLinkClient( link );
			_peers[c.Id] = (c, client, link, new Decoder());
			Console.WriteLine( $"Steam: peer connected, conn={c.Id}" );
		}

		private void OnPeerDisconnected( Connection c )
		{
			if ( _peers.TryRemove( c.Id, out var p ) )
			{
				p.Link.Open = false;
				_hub.RemoveLinkClient( p.Client );
			}
			Console.WriteLine( $"Steam: peer disconnected, conn={c.Id}" );
		}

		private void OnBytes( Connection c, byte[] chunk ) => _inbox.Enqueue( (c.Id, chunk) );

		private async Task DispatchAsync( uint connId, byte[] chunk )
		{
			if ( !_peers.TryGetValue( connId, out var p ) ) return;
			p.Dec.Push( chunk, out var messages );
			foreach ( var bytes in messages )
			{
				Message? msg;
				try { msg = Message.FromJson( Encoding.UTF8.GetString( bytes ) ); }
				catch { continue; }
				if ( msg == null ) continue;
				await _hub.HandleLinkMessage( p.Client, msg, _ct );
			}
		}

		public void Dispose()
		{
			try { _lobby?.Leave(); } catch { }
			_lobby = null;
			try { _socket?.Close(); } catch { }
			_socket = null;
		}
	}

	// ──── Guest side: one SDR connection per local editor WS (transparent pipe) ────

	public sealed class GuestConn : ConnectionManager
	{
		public readonly ConcurrentQueue<byte[]> Inbox = new();
		public readonly TaskCompletionSource ConnectedTcs = new( TaskCreationOptions.RunContinuationsAsynchronously );
		public volatile bool Gone;

		public override void OnConnected( SteamConnInfo info )
		{
			base.OnConnected( info );
			ConnectedTcs.TrySetResult();
		}
		public override void OnDisconnected( SteamConnInfo info )
		{
			base.OnDisconnected( info );
			Gone = true;
			ConnectedTcs.TrySetResult();
		}
		public override void OnMessage( IntPtr data, int size, long messageNum, long recvTime, int channel )
		{
			var b = new byte[size];
			Marshal.Copy( data, b, 0, size );
			Inbox.Enqueue( b );
		}
	}

	public static async Task<SteamId> ResolveHostAsync( string code, CancellationToken ct )
	{
		if ( !ulong.TryParse( code.Trim(), out var raw ) )
			throw new ArgumentException( "lobby code must be numeric (SteamID64)" );
		var lobby = await SteamMatchmaking.JoinLobbyAsync( (SteamId)raw );
		if ( lobby == null )
			throw new IOException( "lobby not found (wrong code?)" );
		var hostStr = lobby.Value.GetData( "host" );
		if ( !ulong.TryParse( hostStr, out var hostRaw ) )
			throw new IOException( "lobby has no host" );
		return (SteamId)hostRaw;
	}

	/// <summary>
	/// Pipe one local editor WebSocket to the host over SDR.
	/// Join/welcome/snapshot flow through verbatim — the host owns all client ids,
	/// so the guest hub keeps zero HubCore state.
	/// </summary>
	public static async Task PipeEditorAsync( System.Net.WebSockets.WebSocket ws, SteamId hostId, CancellationToken ct )
	{
		var conn = SteamNetworkingSockets.ConnectRelay<GuestConn>( hostId, VirtualPort );
		try
		{
			using var connectCts = CancellationTokenSource.CreateLinkedTokenSource( ct );
			connectCts.CancelAfter( TimeSpan.FromSeconds( 20 ) );
			try { await conn.ConnectedTcs.Task.WaitAsync( connectCts.Token ); }
			catch ( OperationCanceledException ) { throw new TimeoutException( "Steam P2P handshake timed out" ); }
			if ( conn.Gone || !conn.Connected )
				throw new IOException( "Steam P2P connection failed" );

			Console.WriteLine( "Steam: P2P link established" );

			var dec = new Decoder();
			var wsSendLock = new SemaphoreSlim( 1, 1 );
			var buf = new byte[64 * 1024];

			// editor -> host
			var up = Task.Run( async () =>
			{
				try
				{
					while ( ws.State == System.Net.WebSockets.WebSocketState.Open && !conn.Gone && !ct.IsCancellationRequested )
					{
						using var ms = new MemoryStream();
						System.Net.WebSockets.WebSocketReceiveResult r;
						do
						{
							r = await ws.ReceiveAsync( buf, ct );
							if ( r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close ) return;
							if ( ms.Length + r.Count > TeamCreateHub.MaxMessageBytes ) return;
							ms.Write( buf, 0, r.Count );
						}
						while ( !r.EndOfMessage );
						if ( r.MessageType != System.Net.WebSockets.WebSocketMessageType.Text ) continue;

						var payload = ms.ToArray();
						foreach ( var chunk in EncodeFrame( payload ) )
						{
							var res = conn.Connection.SendMessage( chunk, SendType.Reliable );
							if ( res != Result.OK ) return;
						}
					}
				}
				catch ( OperationCanceledException ) { }
				catch { }
			}, ct );

			// host -> editor
			var down = Task.Run( async () =>
			{
				try
				{
					while ( ws.State == System.Net.WebSockets.WebSocketState.Open && !conn.Gone && !ct.IsCancellationRequested )
					{
						conn.Receive( 32 );
						while ( conn.Inbox.TryDequeue( out var chunk ) )
						{
							dec.Push( chunk, out var messages );
							foreach ( var m in messages )
							{
								await wsSendLock.WaitAsync( ct );
								try
								{
									if ( ws.State != System.Net.WebSockets.WebSocketState.Open ) return;
									await ws.SendAsync( m, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None );
								}
								finally { wsSendLock.Release(); }
							}
						}
						try { await Task.Delay( 8, ct ); }
						catch ( OperationCanceledException ) { break; }
					}
				}
				catch ( OperationCanceledException ) { }
				catch { }
			}, ct );

			await Task.WhenAny( up, down );
		}
		finally
		{
			try { conn.Close(); } catch { }
		}
	}
}
