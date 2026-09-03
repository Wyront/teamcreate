using System.Security.Cryptography;
using System.Text;
using TeamCreate.Hub;

Console.OutputEncoding = Encoding.UTF8;
Console.SetOut( new StreamWriter( Console.OpenStandardOutput() ) { AutoFlush = true } );

var cts = new CancellationTokenSource();
try { Console.CancelKeyPress += ( _, e ) => { e.Cancel = true; cts.Cancel(); }; } catch { }

var port = 4877;
string? passwordHash = null;
string? rawPassword = null;

for ( int i = 0; i < args.Length; i++ )
{
	if ( args[i] == "--port" && i + 1 < args.Length && int.TryParse( args[i + 1], out var p ) )
		port = p;
	else if ( args[i] == "--password" && i + 1 < args.Length )
	{
		rawPassword = args[i + 1];
		var bytes = Encoding.UTF8.GetBytes( rawPassword );
		var hash = SHA256.HashData( bytes );
		passwordHash = Convert.ToHexString( hash ).ToLowerInvariant();
		Console.WriteLine( "Пароль: принят" );
	}
}

if ( passwordHash != null )
	Console.WriteLine( "Пароль: установлен" );
else
	Console.WriteLine( "Пароль: не установлен (любой может подключиться)" );

var safeArgs = args.Where( a => a != "--password" && a != rawPassword ).ToArray();
Console.WriteLine( $"Аргументы: [{string.Join( ", ", safeArgs )}]" );

await new HubServer( passwordHash ).RunAsync( port, cts.Token );
