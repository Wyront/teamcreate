using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Open.Nat;
using TeamCreate.Hub;

Console.OutputEncoding = Encoding.UTF8;

var cts = new CancellationTokenSource();
try { Console.CancelKeyPress += ( _, e ) => { e.Cancel = true; cts.Cancel(); }; } catch { }

var port = 4877;
string? passwordHash = null;
bool useTunnel = false;
string? tunnelToken = null;
string? logFile = null;
int tlsPort = 0;
string? tlsCert = null;
string? tlsKey = null;
bool steamHostMode = false;
string? steamJoinCode = null;
uint steamAppId = 480; // Spacewar test AppID for dev; ship with your own (--steam-appid)

for ( int i = 0; i < args.Length; i++ )
{
	if ( args[i] == "--port" && i + 1 < args.Length && int.TryParse( args[i + 1], out var p ) )
		port = p;
	else if ( args[i] == "--password" && i + 1 < args.Length )
	{
		var bytes = Encoding.UTF8.GetBytes( args[i + 1] );
		passwordHash = Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant();
		Console.WriteLine( "Password: accepted" );
	}
	else if ( args[i] == "--tunnel" )
		useTunnel = true;
	else if ( args[i] == "--tunnel-token" && i + 1 < args.Length )
		tunnelToken = args[i + 1];
	else if ( args[i] == "--log" && i + 1 < args.Length )
		logFile = args[i + 1];
	else if ( args[i] == "--tls-port" && i + 1 < args.Length && int.TryParse( args[i + 1], out var tp ) )
		tlsPort = tp;
	else if ( args[i] == "--tls-cert" && i + 1 < args.Length )
		tlsCert = args[i + 1];
	else if ( args[i] == "--tls-key" && i + 1 < args.Length )
		tlsKey = args[i + 1];
	else if ( args[i] == "--steam-host" )
		steamHostMode = true;
	else if ( args[i] == "--steam-join" && i + 1 < args.Length )
		steamJoinCode = args[i + 1];
	else if ( args[i] == "--steam-appid" && i + 1 < args.Length && uint.TryParse( args[i + 1], out var appid ) )
		steamAppId = appid;
}

if ( logFile != null )
{
	try
	{
		var fs = new FileStream( logFile, FileMode.Append, FileAccess.Write, FileShare.Read );
		var tw = new StreamWriter( fs, Encoding.UTF8 ) { AutoFlush = true };
		Console.SetOut( TextWriter.Synchronized( new TeeWriter( Console.Out, tw ) ) );
		Console.WriteLine( $"Logging to {logFile}" );
	}
	catch ( Exception ex )
	{
		Console.WriteLine( $"Log file failed: {ex.Message}" );
	}
}

Console.WriteLine( "=== Team Create Hub (Kestrel) ===" );
Console.WriteLine( $"Порт: {port}" );
Console.WriteLine( $"Пароль: {(passwordHash != null ? "установлен" : "не установлен")}" );

// UPnP auto port-forward (best effort)
try
{
	Console.WriteLine( "UPnP: discovering..." );
	var discoverer = new NatDiscoverer();
	using var ctsUpnp = new CancellationTokenSource( 5000 );
	var device = await discoverer.DiscoverDeviceAsync( PortMapper.Upnp, ctsUpnp );
	await device.CreatePortMapAsync( new Mapping( Protocol.Tcp, port, port, "TeamCreateHub" ) );
	Console.WriteLine( $"UPnP: port {port} forwarded" );
}
catch ( Exception ex )
{
	Console.WriteLine( $"UPnP: failed ({ex.Message})" );
}

// Local LAN address (no external traffic)
try
{
	using var socket = new System.Net.Sockets.Socket(
		System.Net.Sockets.AddressFamily.InterNetwork,
		System.Net.Sockets.SocketType.Dgram,
		System.Net.Sockets.ProtocolType.Udp );
	socket.Connect( "8.8.8.8", 53 );
	var ip = (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address.ToString();
	if ( ip != null ) Console.WriteLine( $"Локальная сеть: ws://{ip}:{port}/teamcreate" );
}
catch { }

// Public IP (best effort)
try
{
	using var http = new HttpClient { Timeout = TimeSpan.FromSeconds( 5 ) };
	var publicIp = (await http.GetStringAsync( "https://api.ipify.org", cts.Token )).Trim();
	Console.WriteLine( $"Public IP: {publicIp}:{port}" );
}
catch
{
	Console.WriteLine( "Could not detect public IP" );
}

// cloudflared tunnel (best effort). Two modes:
//   --tunnel               quick tunnel, random address each run (zero setup)
//   --tunnel-token <token>  named tunnel, STABLE address forever (one-time
//                           Cloudflare setup: dashboard -> Networks -> Tunnels)
if ( tunnelToken != null || useTunnel )
{
	var cloudflaredExe = await EnsureCloudflaredAsync();
	if ( cloudflaredExe == null )
	{
		Console.WriteLine( "Tunnel: cloudflared unavailable (install: winget install Cloudflare.cloudflared)" );
	}
	else
	{
		try
		{
			Console.WriteLine( tunnelToken != null
				? "Tunnel: starting named tunnel (stable address)..."
				: "Tunnel: starting cloudflared (quick tunnel)..." );
			var psi = new ProcessStartInfo( cloudflaredExe )
			{
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			// NB: token passed as separate arg value is still visible in the process
			// table while running — same as any tunnel client, acceptable.
			if ( tunnelToken != null )
			{
				psi.ArgumentList.Add( "tunnel" );
				psi.ArgumentList.Add( "--no-autoupdate" );
				psi.ArgumentList.Add( "run" );
				psi.ArgumentList.Add( "--token" );
				psi.ArgumentList.Add( tunnelToken );
			}
			else
			{
				psi.ArgumentList.Add( "tunnel" );
				psi.ArgumentList.Add( "--url" );
				psi.ArgumentList.Add( $"http://localhost:{port}" );
			}

			var proc = Process.Start( psi );

			if ( proc != null )
			{
				proc.ErrorDataReceived += ( _, e ) =>
				{
					if ( e.Data == null ) return;
					var match = Regex.Match( e.Data, @"https?://([a-zA-Z0-9-]+\.trycloudflare\.com)" );
					if ( match.Success )
					{
						var host = match.Groups[1].Value;
						Console.WriteLine( $"Tunnel: {host}" );
						Console.WriteLine( $"Friend connect to: {host}" );
					}
					if ( e.Data.Contains( "Registered tunnel connection", StringComparison.OrdinalIgnoreCase ) )
						Console.WriteLine( "Tunnel: named tunnel connected (stable address active)" );
				};
				proc.BeginErrorReadLine();
				proc.BeginOutputReadLine();
			}
		}
		catch ( Exception ex )
		{
			Console.WriteLine( $"Tunnel: failed ({ex.Message})" );
		}
	}
}

/// <summary>
/// Locate cloudflared (PATH or next to the exe), auto-downloading the official
/// Windows build on first use so --tunnel works with zero manual setup.
/// </summary>
static async Task<string?> EnsureCloudflaredAsync()
{
	// 1. Same dir as the hub exe (self-provisioned earlier or copied manually)
	try
	{
		var local = Path.Combine( AppContext.BaseDirectory, "cloudflared.exe" );
		if ( File.Exists( local ) ) return local;
	}
	catch { }

	// 2. PATH
	try
	{
		var proc = Process.Start( new ProcessStartInfo( "where", "cloudflared" )
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardOutput = true,
		} );
		if ( proc != null )
		{
			var path = (await proc.StandardOutput.ReadToEndAsync()).Split( '\n', '\r' )
				.Select( s => s.Trim() ).FirstOrDefault( s => s.EndsWith( ".exe", StringComparison.OrdinalIgnoreCase ) );
			await proc.WaitForExitAsync();
			if ( !string.IsNullOrEmpty( path ) && File.Exists( path ) ) return path;
		}
	}
	catch { }

	// 3. Auto-download official build
	try
	{
		Console.WriteLine( "Tunnel: cloudflared not found, downloading official build..." );
		var dest = Path.Combine( AppContext.BaseDirectory, "cloudflared.exe" );
		using var http = new HttpClient { Timeout = TimeSpan.FromMinutes( 5 ) };
		using var resp = await http.GetAsync(
			"https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" );
		resp.EnsureSuccessStatusCode();
		await using var fs = File.Create( dest );
		await resp.Content.CopyToAsync( fs );
		Console.WriteLine( "Tunnel: cloudflared downloaded" );
		return dest;
	}
	catch ( Exception ex )
	{
		Console.WriteLine( $"Tunnel: auto-download failed ({ex.Message})" );
		return null;
	}
}

var builder = WebApplication.CreateSlimBuilder( args );

// Local plain listener is always on (same-PC / LAN direct path, and the
// guest-side endpoint for the local editor in --steam-join mode).
builder.WebHost.UseUrls( $"http://127.0.0.1:{port}" );

// Optional public TLS listener for the internet relay path
// (e.g. --tls-port 443 --tls-cert fullchain.pem --tls-key privkey.pem).
// Plain outbound TLS/443 traverses NAT, VPN and DPI where raw ports die.
if ( tlsPort > 0 )
{
	if ( tlsCert == null || tlsKey == null )
	{
		Console.WriteLine( "TLS: --tls-port needs --tls-cert and --tls-key, TLS disabled" );
	}
	else
	{
		var certPath = tlsCert;
		var keyPath = tlsKey;
		var httpsPort = tlsPort;
		builder.WebHost.ConfigureKestrel( o =>
		{
			o.ListenAnyIP( httpsPort, lo => lo.UseHttps( certPath, keyPath ) );
		} );
		Console.WriteLine( $"TLS: wss://0.0.0.0:{httpsPort}/teamcreate" );
	}
}
builder.Logging.SetMinimumLevel( LogLevel.Warning );

var app = builder.Build();

// Transport-level heartbeat: protocol ping every 30s, kill silently-dead
// sockets after 15s without pong. Cloudflare also drops idle connections,
// so this keeps NAT/proxy mappings alive.
app.UseWebSockets( new WebSocketOptions
{
	KeepAliveInterval = TimeSpan.FromSeconds( 30 ),
	KeepAliveTimeout = TimeSpan.FromSeconds( 15 ),
} );

var hub = null as TeamCreateHub;
SteamLink.Host? steamHost = null;
Steamworks.SteamId steamHostId = default;
bool steamGuest = false;

// ──── Steam link mode (our Porthole-analog, no port forwarding) ────
// Host:   hub.exe --steam-host [--steam-appid 480]
//         -> prints a lobby code, serves local editor + Steam guests
// Guest:  hub.exe --steam-join CODE [--steam-appid 480]
//         -> local editor connects to 127.0.0.1:port as usual; traffic
//            is piped to the host over Steam Datagram Relay
if ( steamHostMode || steamJoinCode != null )
{
	if ( !SteamLink.Init( steamAppId, out var steamErr ) )
	{
		Console.WriteLine( steamErr );
		return;
	}

	if ( steamHostMode )
	{
		hub = new TeamCreateHub( passwordHash, cts.Token );
		steamHost = new SteamLink.Host( hub, cts.Token );
		steamHost.Start();
		var code = await steamHost.CreateLobbyAsync();
		if ( code == null )
		{
			Console.WriteLine( "Steam: lobby creation failed" );
			return;
		}
		Console.WriteLine( $"Steam: hosting, lobby code: {code}" );
		Console.WriteLine( "Guest runs: hub.exe --steam-join " + code );
	}
	else
	{
		steamGuest = true;
		try
		{
			steamHostId = await SteamLink.ResolveHostAsync( steamJoinCode!, cts.Token );
			Console.WriteLine( $"Steam: host found: {steamHostId.Value}" );
		}
		catch ( Exception ex )
		{
			Console.WriteLine( $"Steam: {ex.Message}" );
			return;
		}
	}
}

if ( hub == null && !steamGuest )
	hub = new TeamCreateHub( passwordHash, cts.Token );

app.Map( "/teamcreate", async ( HttpContext ctx ) =>
{
	if ( !ctx.WebSockets.IsWebSocketRequest )
	{
		ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
		return;
	}

	using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
	if ( steamGuest )
		await SteamLink.PipeEditorAsync( ws, steamHostId, ctx.RequestAborted );
	else
		await hub!.HandleAsync( ws, ctx.RequestAborted );
} );

Console.WriteLine( $"Локально: ws://127.0.0.1:{port}/teamcreate" );
Console.WriteLine( "Для завершения — Ctrl+C\n" );

try
{
	await app.RunAsync( cts.Token );
}
finally
{
	if ( steamGuest || steamHostMode )
	{
		try { steamHost?.Dispose(); } catch { }
		SteamLink.Shutdown();
	}
}

/// <summary>Writes to two outputs at once (console + log file).</summary>
internal sealed class TeeWriter : TextWriter
{
	private readonly TextWriter _a;
	private readonly TextWriter _b;

	public TeeWriter( TextWriter a, TextWriter b )
	{
		_a = a;
		_b = b;
	}

	public override Encoding Encoding => Encoding.UTF8;

	public override void WriteLine( string? value )
	{
		try { _a.WriteLine( value ); } catch { }
		try { _b.WriteLine( value ); } catch { }
	}

	public override void Write( string? value )
	{
		try { _a.Write( value ); } catch { }
		try { _b.Write( value ); } catch { }
	}

	protected override void Dispose( bool disposing )
	{
		if ( disposing )
		{
			try { _b.Dispose(); } catch { }
		}
	}
}
