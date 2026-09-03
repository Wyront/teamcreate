namespace Editor.TeamCreate;

public sealed class TeamCreateClient
{
	private ClientWebSocket? _ws;
	private CancellationTokenSource? _cts;
	private string? _passwordHash;

	public bool IsConnected => _ws?.State == WebSocketState.Open;
	public string? MyId { get; private set; }

	public event Action<string>? OnLog;
	public event Action<List<PeerInfo>>? OnWelcome;
	public event Action<PeerInfo>? OnPeerJoined;
	public event Action<PeerInfo>? OnPeerLeft;
	public event Action<Message>? OnMessage;
	public event Action<string>? OnAuthRejected;

	public async Task ConnectAsync( string address, string name, string color, string room, string? passwordHash = null )
	{
		if ( IsConnected ) return;

		_passwordHash = passwordHash;

		// Always create fresh socket — ClientWebSocket cannot be reused
		_ws?.Dispose();
		_ws = new ClientWebSocket();
		_cts = new CancellationTokenSource();
		var uri = new Uri( $"ws://{address}/teamcreate" );

		OnLog?.Invoke( $"Подключение к {address}..." );
		await _ws.ConnectAsync( uri, _cts.Token );

		Send( new Message { Type = "join", Name = name, Color = color, Room = room, PasswordHash = passwordHash } );
		_ = Task.Run( () => ReceiveLoopAsync( _ws, _cts.Token ) );
	}

	public async Task DisconnectAsync()
	{
		_cts?.Cancel();
		if ( _ws != null && _ws.State == WebSocketState.Open )
		{
			try
			{
				await _ws.CloseAsync( WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None );
			}
			catch { }
		}
		_ws?.Dispose();
		_ws = null;
	}

	public void Send( Message msg )
	{
		if ( !IsConnected || _ws == null ) return;
		try
		{
			msg.PasswordHash = _passwordHash;
			var bytes = Encoding.UTF8.GetBytes( msg.ToJson() );
			_ = _ws.SendAsync( bytes, WebSocketMessageType.Text, true, CancellationToken.None );
		}
		catch { }
	}

	private async Task ReceiveLoopAsync( ClientWebSocket ws, CancellationToken ct )
	{
		var buf = new byte[512 * 1024];
		try
		{
			while ( ws.State == WebSocketState.Open && !ct.IsCancellationRequested )
			{
				using var ms = new MemoryStream();
				WebSocketReceiveResult result;
				do
				{
					result = await ws.ReceiveAsync( buf, ct );
					if ( result.MessageType == WebSocketMessageType.Close )
					{
						OnLog?.Invoke( "Соединение закрыто сервером" );
						return;
					}
					ms.Write( buf, 0, result.Count );
				}
				while ( !result.EndOfMessage );

				var json = Encoding.UTF8.GetString( ms.GetBuffer(), 0, unchecked( (int)ms.Length ) );
				var msg = Message.FromJson( json );
				if ( msg == null ) continue;

				switch ( msg.Type )
				{
					case "welcome":
						MyId = msg.From;
						OnWelcome?.Invoke( msg.Peers ?? new() );
						break;
					case "peer-joined":
						if ( msg.Peer != null ) OnPeerJoined?.Invoke( msg.Peer );
						break;
					case "peer-left":
						if ( msg.Peer != null ) OnPeerLeft?.Invoke( msg.Peer );
						break;
					case "auth-rejected":
						OnAuthRejected?.Invoke( msg.Reason ?? "Authentication failed" );
						return;
					default:
						OnMessage?.Invoke( msg );
						break;
				}
			}
		}
		catch ( WebSocketException ) { }
		catch ( OperationCanceledException ) { }
		catch ( Exception ex )
		{
			OnLog?.Invoke( $"Ошибка: {ex.Message}" );
		}
	}
}
