using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace TeamCreate.Hub;

public sealed class HubServer
{
	private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte[]>> _fileCache = new();
	private readonly string? _passwordHash;

	public HubServer( string? passwordHash )
	{
		_passwordHash = passwordHash;
		Console.WriteLine( $"HubServer created, password: {(passwordHash != null ? "YES" : "NO")}" );
	}

	public async Task RunAsync( int port, CancellationToken ct )
	{
		var listener = new TcpListener( IPAddress.Any, port );
		try
		{
			listener.Start();
		}
		catch ( System.Net.Sockets.SocketException ex )
		{
			Console.WriteLine( $"ERROR: Port {port} is already in use. Kill the old Hub process first." );
			Console.WriteLine( $"Details: {ex.Message}" );
			return;
		}

		Console.WriteLine( "=== Team Create Hub ===" );
		Console.WriteLine( $"Порт: {port}" );
		Console.WriteLine( $"Пароль: {(_passwordHash != null ? "установлен" : "не установлен")}" );

		try
		{
			using var socket = new Socket( AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp );
			socket.Connect( "8.8.8.8", 53 );
			var ip = (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
			if ( ip != null ) Console.WriteLine( $"Локальная сеть: ws://{ip}:{port}" );
		}
		catch { }

		Console.WriteLine( $"Локально: ws://127.0.0.1:{port}" );
		Console.WriteLine( "Для завершения — Ctrl+C\n" );

		while ( !ct.IsCancellationRequested )
		{
			TcpClient tcp;
			try { tcp = await listener.AcceptTcpClientAsync(); }
			catch when ( ct.IsCancellationRequested ) { break; }

			var wsConn = await WsConnection.AcceptAsync( tcp, ct );
			if ( wsConn == null ) { tcp.Close(); continue; }

			var client = new ClientConnection( wsConn, this );
			_clients[client.Id] = client;
			Console.WriteLine( $"New client connected: {client.Id}" );
			_ = Task.Run( () => client.RunAsync( ct ), ct );
		}

		listener.Stop();
	}

	public async Task OnJoined( ClientConnection client )
	{
		var room = _clients.Values.Where( c => c.Room == client.Room && c.Info != null ).ToList();
		var others = room.Where( c => c.Id != client.Id ).ToList();

		Console.WriteLine( $"[{client.Room}] {client.Info!.Name} подключился (онлайн: {room.Count})" );

		await client.SendAsync( new Message
		{
			Type = "welcome",
			From = client.Id,
			Peers = others.Select( c => c.Info! ).ToList(),
		} );

		if ( _fileCache.TryGetValue( client.Room!, out var cache ) )
		{
			foreach ( var (path, data) in cache )
			{
				await client.SendAsync( new Message
				{
					Type = "file",
					Path = path,
					ContentB64 = Convert.ToBase64String( data ),
				} );
			}
		}

		await BroadcastAsync( client.Room!, new Message { Type = "peer-joined", Peer = client.Info }, client.Id );
	}

	public bool VerifyPassword( string? clientHash )
	{
		if ( _passwordHash == null )
			return true;

		if ( clientHash == null )
		{
			Console.WriteLine( "VerifyPassword: REJECTED (no hash)" );
			return false;
		}

		var match = string.Equals( _passwordHash, clientHash, StringComparison.OrdinalIgnoreCase );
		if ( !match )
			Console.WriteLine( "VerifyPassword: REJECTED (wrong password)" );
		return match;
	}

	public async Task RelayToRoomAsync( ClientConnection sender, Message msg )
	{
		if ( sender.Room == null ) return;

		if ( msg.Type == "file" && msg.Path != null && msg.ContentB64 != null )
		{
			_fileCache.GetOrAdd( sender.Room, _ => new() )[msg.Path] = Convert.FromBase64String( msg.ContentB64 );
		}
		else if ( msg.Type == "file-delete" && msg.Path != null )
		{
			if ( _fileCache.TryGetValue( sender.Room, out var cache ) )
				cache.TryRemove( msg.Path, out _ );
		}

		await BroadcastAsync( sender.Room, msg, sender.Id );
	}

	public void Disconnect( ClientConnection client )
	{
		if ( !_clients.TryRemove( client.Id, out _ ) ) return;
		if ( client.Info == null || client.Room == null ) return;

		Console.WriteLine( $"[{client.Room}] {client.Info.Name} отключился" );
		_ = BroadcastAsync( client.Room, new Message { Type = "peer-left", Peer = client.Info }, client.Id );
	}

	private async Task BroadcastAsync( string room, Message msg, string? exceptId )
	{
		foreach ( var client in _clients.Values )
		{
			if ( client.Room != room || client.Id == exceptId ) continue;
			await client.SendAsync( msg );
		}
	}
}
