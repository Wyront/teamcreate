using System.Security.Cryptography;

namespace Editor.TeamCreate;

public sealed class FileSyncService : IDisposable
{
	private readonly Dictionary<string, string> _knownHashes = new();
	private readonly HashSet<string> _applyingChanges = new();
	private FileSystemWatcher? _watcher;
	private readonly Queue<string> _pendingChanges = new();
	private readonly object _lock = new();
	private CancellationTokenSource? _debounceCts;

	public event Action<string>? OnLog;

	private string _rootPath = "";
	private Action<string, byte[]>? _sendFile;
	private Action<string>? _sendDelete;

	public void Start( string rootPath, Action<string, byte[]> sendFile, Action<string> sendDelete )
	{
		_rootPath = rootPath;
		_sendFile = sendFile;
		_sendDelete = sendDelete;

		ScanAll( rootPath );

		_watcher = new FileSystemWatcher( rootPath )
		{
			IncludeSubdirectories = true,
			EnableRaisingEvents = true,
			NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.Size,
		};

		_watcher.Changed += ( _, e ) => Enqueue( e.FullPath );
		_watcher.Created += ( _, e ) => Enqueue( e.FullPath );
		_watcher.Renamed += ( _, e ) => { Enqueue( e.FullPath ); Enqueue( e.OldFullPath ); };
		_watcher.Deleted += ( _, e ) => Enqueue( e.FullPath );

		OnLog?.Invoke( $"Файловый синк: {rootPath}" );
	}

	public void Stop()
	{
		_watcher?.Dispose();
		_watcher = null;
		_debounceCts?.Cancel();
	}

	public void ApplyRemoteFile( string relPath, byte[] data )
	{
		var fullPath = Path.Combine( _rootPath, relPath );
		var dir = Path.GetDirectoryName( fullPath );
		if ( dir != null ) Directory.CreateDirectory( dir );

		var norm = NormalizePath( relPath );
		_applyingChanges.Add( norm );
		try
		{
			File.WriteAllBytes( fullPath, data );
			_knownHashes[norm] = Hash( data );
		}
		finally
		{
			_applyingChanges.Remove( norm );
		}
	}

	public void ApplyRemoteDelete( string relPath )
	{
		var fullPath = Path.Combine( _rootPath, relPath );
		var norm = NormalizePath( relPath );
		_applyingChanges.Add( norm );
		try
		{
			if ( File.Exists( fullPath ) ) File.Delete( fullPath );
			_knownHashes.Remove( norm );
		}
		finally
		{
			_applyingChanges.Remove( norm );
		}
	}

	private void ScanAll( string rootPath )
	{
		foreach ( var file in Directory.EnumerateFiles( rootPath, "*", SearchOption.AllDirectories ) )
		{
			if ( ShouldIgnore( file ) ) continue;
			var rel = GetRelativePath( file, rootPath );
			var data = File.ReadAllBytes( file );
			_knownHashes[NormalizePath( rel )] = Hash( data );
		}
	}

	private void Enqueue( string fullPath )
	{
		if ( ShouldIgnore( fullPath ) ) return;

		lock ( _lock )
		{
			if ( _pendingChanges.Contains( fullPath ) ) return;
			_pendingChanges.Enqueue( fullPath );
		}

		_debounceCts?.Cancel();
		_debounceCts = new CancellationTokenSource();
		var token = _debounceCts.Token;
		_ = Task.Delay( 200, token ).ContinueWith( _ => FlushChanges(), token );
	}

	private void FlushChanges()
	{
		List<string> changes;
		lock ( _lock )
		{
			changes = _pendingChanges.Distinct().ToList();
			_pendingChanges.Clear();
		}

		foreach ( var fullPath in changes )
		{
			var rel = GetRelativePath( fullPath, _rootPath );
			var norm = NormalizePath( rel );

			if ( !File.Exists( fullPath ) || ShouldIgnore( fullPath ) )
			{
				if ( _knownHashes.ContainsKey( norm ) )
				{
					_knownHashes.Remove( norm );
					_sendDelete?.Invoke( rel );
					OnLog?.Invoke( $"[out] удалён: {rel}" );
				}
				continue;
			}

			if ( _applyingChanges.Contains( norm ) ) continue;

			try
			{
				var data = File.ReadAllBytes( fullPath );
				var h = Hash( data );

				if ( _knownHashes.TryGetValue( norm, out var prev ) && prev == h )
					continue;

				_knownHashes[norm] = h;
				_sendFile?.Invoke( rel, data );
				OnLog?.Invoke( $"[out] отправлен: {rel} ({data.Length} байт)" );
			}
			catch { }
		}
	}

	private static bool ShouldIgnore( string path )
	{
		var parts = path.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
		return parts.Any( p =>
			p.Equals( ".sbox", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( "obj", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( ".git", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( ".vs", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( ".vscode", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( "node_modules", StringComparison.OrdinalIgnoreCase ) ||
			p.EndsWith( ".csproj.user", StringComparison.OrdinalIgnoreCase ) );
	}

	private static string NormalizePath( string path ) =>
		path.Replace( '\\', '/' ).Trim( '/' ).ToLowerInvariant();

	private static string GetRelativePath( string fullPath, string rootPath ) =>
		Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );

	private static string Hash( byte[] data ) =>
		Convert.ToHexString( MD5.HashData( data ) );

	public void Dispose() => Stop();
}
