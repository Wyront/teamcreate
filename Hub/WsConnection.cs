using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TeamCreate.Hub;

public sealed class WsConnection : IAsyncDisposable
{
	private readonly TcpClient _tcp;
	private readonly NetworkStream _stream;

	public bool IsOpen { get; private set; }

	private WsConnection( TcpClient tcp )
	{
		_tcp = tcp;
		_stream = tcp.GetStream();
		IsOpen = true;
	}

	public static async Task<WsConnection?> AcceptAsync( TcpClient tcp, CancellationToken ct )
	{
		var stream = tcp.GetStream();
		var request = await ReadHttpHeadersAsync( stream, ct );
		if ( request == null ) return null;

		string? key = null;
		foreach ( var line in request.Split( "\r\n", StringSplitOptions.RemoveEmptyEntries ) )
		{
			var idx = line.IndexOf( ':' );
			if ( idx < 0 ) continue;
			if ( line[..idx].Trim().Equals( "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase ) )
				key = line[(idx + 1)..].Trim();
		}
		if ( key == null ) return null;

		var accept = Convert.ToBase64String(
			SHA1.HashData( Encoding.ASCII.GetBytes( key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11" ) ) );

		var response = "HTTP/1.1 101 Switching Protocols\r\n" +
					   "Upgrade: websocket\r\n" +
					   "Connection: Upgrade\r\n" +
					   $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
		await stream.WriteAsync( Encoding.ASCII.GetBytes( response ), ct );
		return new WsConnection( tcp );
	}

	private static async Task<string?> ReadHttpHeadersAsync( NetworkStream stream, CancellationToken ct )
	{
		var sb = new StringBuilder();
		var buf = new byte[1];
		while ( sb.Length < 32 * 1024 )
		{
			int read;
			try { read = await stream.ReadAsync( buf, ct ); }
			catch { return null; }
			if ( read == 0 ) return null;
			sb.Append( (char)buf[0] );
			if ( sb.Length >= 4 && sb.ToString( sb.Length - 4, 4 ) == "\r\n\r\n" )
				return sb.ToString();
		}
		return null;
	}

	public async Task<string?> ReceiveTextAsync( CancellationToken ct )
	{
		using var ms = new MemoryStream();
		while ( true )
		{
			var header = await ReadExactAsync( 2, ct );
			if ( header == null ) return null;

			bool fin = (header[0] & 0x80) != 0;
			int opcode = header[0] & 0x0F;
			bool masked = (header[1] & 0x80) != 0;
			long length = header[1] & 0x7F;

			if ( length == 126 )
			{
				var ext = await ReadExactAsync( 2, ct );
				if ( ext == null ) return null;
				length = (ext[0] << 8) | ext[1];
			}
			else if ( length == 127 )
			{
				var ext = await ReadExactAsync( 8, ct );
				if ( ext == null ) return null;
				length = 0;
				for ( int i = 0; i < 8; i++ ) length = (length << 8) | ext[i];
			}

			byte[]? mask = null;
			if ( masked )
			{
				mask = await ReadExactAsync( 4, ct );
				if ( mask == null ) return null;
			}

			if ( length > 256 * 1024 * 1024 ) return null;

			var payload = await ReadExactAsync( (int)length, ct );
			if ( payload == null ) return null;

			if ( mask != null )
				for ( int i = 0; i < payload.Length; i++ )
					payload[i] ^= mask[i % 4];

			switch ( opcode )
			{
				case 0x8: IsOpen = false; return null;
				case 0x9: await SendFrameAsync( 0xA, payload, ct ); continue;
				case 0xA: continue;
				default:
					ms.Write( payload, 0, payload.Length );
					if ( fin ) return Encoding.UTF8.GetString( ms.GetBuffer(), 0, unchecked((int)ms.Length) );
					break;
			}
		}
	}

	public async Task SendTextAsync( string text, CancellationToken ct = default )
	{
		await SendFrameAsync( 0x1, Encoding.UTF8.GetBytes( text ), ct );
	}

	private async Task SendFrameAsync( byte opcode, byte[] payload, CancellationToken ct )
	{
		var header = new List<byte>( 10 ) { (byte)(0x80 | opcode) };
		if ( payload.Length < 126 ) header.Add( (byte)payload.Length );
		else if ( payload.Length <= ushort.MaxValue )
		{
			header.Add( 126 );
			header.Add( (byte)(payload.Length >> 8) );
			header.Add( (byte)payload.Length );
		}
		else
		{
			header.Add( 127 );
			for ( int i = 7; i >= 0; i-- ) header.Add( (byte)((long)payload.Length >> (8 * i)) );
		}

		try
		{
			await _stream.WriteAsync( header.ToArray(), ct );
			await _stream.WriteAsync( payload, ct );
		}
		catch { IsOpen = false; }
	}

	private async Task<byte[]?> ReadExactAsync( int count, CancellationToken ct )
	{
		var buf = new byte[count];
		int offset = 0;
		while ( offset < count )
		{
			int read;
			try { read = await _stream.ReadAsync( buf.AsMemory( offset, count - offset ), ct ); }
			catch { IsOpen = false; return null; }
			if ( read == 0 ) { IsOpen = false; return null; }
			offset += read;
		}
		return buf;
	}

	public ValueTask DisposeAsync()
	{
		IsOpen = false;
		try { _tcp.Close(); } catch { }
		return ValueTask.CompletedTask;
	}
}
