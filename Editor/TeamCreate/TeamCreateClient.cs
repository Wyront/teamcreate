using System.Threading.Channels;

namespace Editor.TeamCreate;

public sealed class TeamCreateClient
{
	public const int MaxMessageBytes = 9 * 1024 * 1024;

	private ClientWebSocket? _ws;
	private CancellationTokenSource? _cts;
	private string? _passwordHash;
	private Channel<Message>? _sendQueue;
	private bool _manual;

	public event Action? OnDropped;

	public bool IsConnected => _ws?.State == WebSocketState.Open;
	public string? MyId { get; private set; }

	public event Action<string>? OnLog;
	public event Action<Message>? OnWelcome;
	public event Action<PeerInfo>? OnPeerJoined;
	public event Action<PeerInfo>? OnPeerLeft;
	public event Action<Message>? OnMessage;
	public event Action<string>? OnAuthRejected;

	public async Task ConnectAsync( string address, string name, string color, string room, string? passwordHash = null, string? relay = null )
	{
		if ( IsConnected ) return;

		_manual = false;
		_passwordHash = passwordHash;

		// Always create fresh state — ClientWebSocket cannot be reused
		_ws?.Dispose();
		_ws = null;
		_cts = new CancellationTokenSource();

		// Heterogeneous happy eyeballs: direct first, public relay ~1s later,
		// first winner wins. Direct covers same-PC/LAN; relay covers NAT/VPN/proxy.
		var uris = new List<(string Uri, TimeSpan Timeout)>
		{
			(DirectToWsUri( address ), TimeSpan.FromSeconds( 4 )),
		};
		var relayUri = RelayToWsUri( relay );
		if ( relayUri != null )
			uris.Add( (relayUri, TimeSpan.FromSeconds( 8 )) );

		OnLog?.Invoke( $"Подключение к {address}..." + (relayUri != null ? " (+relay)" : "") );
		var ws = await RaceConnectAsync( uris, _cts.Token );

		_ws = ws;
		// FIFO send pump: preserves message order (manifest before chunks!) and
		// serializes SendAsync (ClientWebSocket allows only one at a time).
		_sendQueue = Channel.CreateUnbounded<Message>( new UnboundedChannelOptions { SingleReader = true } );
		_ = Task.Run( () => SendPumpAsync( _ws, _sendQueue, _cts.Token ) );

		Send( new Message { Type = "join", Name = name, Color = color, Room = room, PasswordHash = passwordHash } );
		_ = Task.Run( () => ReceiveLoopAsync( _ws, _cts.Token ) );
	}

	private static string DirectToWsUri( string address )
	{
		// Detect tunnel addresses (trycloudflare.com, bore.pub, etc.)
		if ( address.Contains( "trycloudflare.com" ) || address.Contains( "bore.pub" ) )
			return $"wss://{address.TrimEnd( '/' )}/teamcreate";
		return $"ws://{address.TrimEnd( '/' )}/teamcreate";
	}

	private static string? RelayToWsUri( string? relay )
	{
		if ( string.IsNullOrWhiteSpace( relay ) ) return null;
		relay = relay.Trim().TrimEnd( '/' );
		if ( relay.StartsWith( "wss://", StringComparison.OrdinalIgnoreCase ) ||
			 relay.StartsWith( "ws://", StringComparison.OrdinalIgnoreCase ) )
			return relay + "/teamcreate";
		// Bare host[:port]: :443 (or no port) => TLS, the DPI-proof path
		var colon = relay.LastIndexOf( ':' );
		int p = 0;
		var hasPort = colon > 0 && int.TryParse( relay.Substring( colon + 1 ), out p );
		if ( !hasPort )
			return $"wss://{relay}/teamcreate";
		return p == 443 ? $"wss://{relay}/teamcreate" : $"ws://{relay}/teamcreate";
	}

	private static async Task<ClientWebSocket> RaceConnectAsync( List<(string Uri, TimeSpan Timeout)> uris, CancellationToken ct )
	{
		using var raceCts = CancellationTokenSource.CreateLinkedTokenSource( ct );
		var attempts = new List<Task<ClientWebSocket?>>();
		for ( int i = 0; i < uris.Count; i++ )
		{
			if ( i > 0 )
			{
				try { await Task.Delay( TimeSpan.FromSeconds( 1 ), raceCts.Token ); }
				catch ( OperationCanceledException ) { break; }
				if ( raceCts.IsCancellationRequested ) break;
			}
			attempts.Add( TryConnectOneAsync( uris[i].Uri, uris[i].Timeout, raceCts.Token ) );
		}

		while ( attempts.Count > 0 )
		{
			var done = await Task.WhenAny( attempts );
			attempts.Remove( done );
			ClientWebSocket? winner = null;
			try { winner = await done; }
			catch { }
			if ( winner != null && winner.State == WebSocketState.Open )
			{
				raceCts.Cancel();
				foreach ( var t in attempts )
				{
					_ = t.ContinueWith( async tt =>
					{
						try
						{
							var s = await tt;
							try { s?.Dispose(); } catch { }
						}
						catch { }
					}, TaskScheduler.Default ).Unwrap();
				}
				return winner;
			}
		}
		throw new TimeoutException( "all connection attempts failed" );
	}

	private static async Task<ClientWebSocket?> TryConnectOneAsync( string uriStr, TimeSpan timeout, CancellationToken ct )
	{
		var ws = new ClientWebSocket();
		ws.Options.KeepAliveInterval = TimeSpan.FromSeconds( 15 );
		try
		{
			// Same proxy policy as before: local targets bypass any system proxy
			if ( IsLocalTarget( new Uri( uriStr ).Host ) )
				ws.Options.Proxy = new System.Net.WebProxy();
		}
		catch { }

		using var cts = CancellationTokenSource.CreateLinkedTokenSource( ct );
		cts.CancelAfter( timeout );
		try
		{
			await ws.ConnectAsync( new Uri( uriStr ), cts.Token );
			if ( ws.State == WebSocketState.Open )
				return ws;
		}
		catch { }
		try { ws.Dispose(); } catch { }
		return null;
	}

	public async Task DisconnectAsync()
	{
		_manual = true;
		_cts?.Cancel();
		try { _sendQueue?.Writer.TryComplete(); } catch { }
		_sendQueue = null;
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

	/// <summary>Drop the dead socket without a close handshake (pre-reconnect cleanup).</summary>
	public void Abort()
	{
		_manual = false;
		_cts?.Cancel();
		try { _sendQueue?.Writer.TryComplete(); } catch { }
		_sendQueue = null;
		try { _ws?.Dispose(); } catch { }
		_ws = null;
	}

	public void Send( Message msg )
	{
		var queue = _sendQueue;
		if ( !IsConnected || queue == null ) return;
		try
		{
			msg.PasswordHash = _passwordHash;
			queue.Writer.TryWrite( msg ); // unbounded: never blocks the caller
		}
		catch { }
	}

	private static async Task SendPumpAsync( ClientWebSocket ws, Channel<Message> queue, CancellationToken ct )
	{
		try
		{
			await foreach ( var msg in queue.Reader.ReadAllAsync( ct ) )
			{
				if ( ws.State != WebSocketState.Open ) return;
				try
				{
					var bytes = Encoding.UTF8.GetBytes( msg.ToJson() );
					await ws.SendAsync( bytes, WebSocketMessageType.Text, true, CancellationToken.None );
				}
				catch { return; }
			}
		}
		catch ( OperationCanceledException ) { }
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
						if ( !_manual ) OnDropped?.Invoke();
						return;
					}
					if ( ms.Length + result.Count > MaxMessageBytes )
					{
						OnLog?.Invoke( "Сообщение слишком большое — пропуск" );
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
						OnWelcome?.Invoke( msg );
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
		catch ( WebSocketException )
		{
			if ( !_manual ) OnDropped?.Invoke();
		}
		catch ( OperationCanceledException ) { }
		catch ( Exception ex )
		{
			OnLog?.Invoke( $"Ошибка: {ex.Message}" );
		}
	}

	/// <summary>Loopback / LAN / single-label hosts must bypass any system proxy.</summary>
	private static bool IsLocalTarget( string address )
	{
		var host = address.Trim();
		if ( host.StartsWith( "[" ) )
		{
			var end = host.IndexOf( ']' );
			host = end > 0 ? host.Substring( 1, end - 1 ) : host;
		}
		else
		{
			host = host.Split( ':' )[0];
		}
		host = host.Trim().ToLowerInvariant();

		if ( host == "localhost" ) return true;
		if ( !host.Contains( '.' ) ) return true; // single-label LAN names
		if ( System.Net.IPAddress.TryParse( host, out var ip ) )
		{
			if ( System.Net.IPAddress.IsLoopback( ip ) ) return true;
			var b = ip.GetAddressBytes();
			if ( b.Length == 4 )
			{
				if ( b[0] == 10 ) return true;
				if ( b[0] == 172 && b[1] >= 16 && b[1] <= 31 ) return true;
				if ( b[0] == 192 && b[1] == 168 ) return true;
				if ( b[0] == 169 && b[1] == 254 ) return true;
			}
		}
		return false;
	}
}
