namespace TeamCreate.Hub;

public sealed class ClientConnection
{
	private readonly WsConnection _socket;
	private readonly HubServer _server;
	private string? _authenticatedHash;

	public string Id { get; } = Guid.NewGuid().ToString( "N" )[..8];
	public PeerInfo? Info { get; private set; }
	public string? Room { get; private set; }
	public bool IsAlive => _socket.IsOpen;

	public ClientConnection( WsConnection socket, HubServer server )
	{
		_socket = socket;
		_server = server;
	}

	public async Task RunAsync( CancellationToken ct )
	{
		try
		{
			while ( _socket.IsOpen && !ct.IsCancellationRequested )
			{
				var text = await _socket.ReceiveTextAsync( ct );
				if ( text == null ) break;

				var msg = Message.FromJson( text );
				if ( msg == null ) continue;

				await HandleMessageAsync( msg );
			}
		}
		catch ( OperationCanceledException ) { }
		finally
		{
			_server.Disconnect( this );
			await _socket.DisposeAsync();
		}
	}

	private async Task HandleMessageAsync( Message msg )
	{
		switch ( msg.Type )
		{
			case "join":
				Console.WriteLine( $"[{Id}] Join: name={msg.Name}, room={msg.Room}" );
				// Verify password on join
				if ( !_server.VerifyPassword( msg.PasswordHash ) )
				{
					Console.WriteLine( $"[{Id}] Auth REJECTED" );
					await SendAsync( new Message { Type = "auth-rejected", Reason = "Invalid password" } );
					await _socket.DisposeAsync();
					return;
				}

				_authenticatedHash = msg.PasswordHash;
				Info = new PeerInfo { Id = Id, Name = msg.Name ?? $"user-{Id}", Color = msg.Color ?? "#ffffff" };
				Room = string.IsNullOrWhiteSpace( msg.Room ) ? "default" : msg.Room!;
				Console.WriteLine( $"[{Id}] Auth OK, joining room {Room}" );
				await _server.OnJoined( this );
				break;

			case "file":
			case "file-delete":
			case "presence":
			case "transform":
			case "chat":
				// Verify password on EVERY message
				if ( !_server.VerifyPassword( msg.PasswordHash ) || msg.PasswordHash != _authenticatedHash )
				{
					Console.WriteLine( $"[{Id}] Message auth REJECTED" );
					await SendAsync( new Message { Type = "auth-rejected", Reason = "Invalid password" } );
					await _socket.DisposeAsync();
					return;
				}

				msg.From = Id;
				await _server.RelayToRoomAsync( this, msg );
				break;
		}
	}

	public async Task SendAsync( Message msg )
	{
		if ( !IsAlive ) return;
		try { await _socket.SendTextAsync( msg.ToJson() ); }
		catch { }
	}
}
