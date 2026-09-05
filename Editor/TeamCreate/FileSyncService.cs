using System.Security.Cryptography;

namespace Editor.TeamCreate;

public sealed class FileSyncService : IDisposable
{
	private readonly Dictionary<string, string> _knownHashes = new();
	private readonly Dictionary<string, DateTime> _knownWriteTimes = new();
	private readonly Dictionary<string, DateTime> _missingSince = new( StringComparer.OrdinalIgnoreCase );
	private readonly HashSet<string> _applyingChanges = new();
	private FileSystemWatcher? _watcher;
	private readonly Queue<string> _pendingChanges = new();
	private readonly object _lock = new();
	private CancellationTokenSource? _debounceCts;
	private CancellationTokenSource? _scanCts;

	public event Action<string>? OnLog;

	private string _rootPath = "";
	private Action<string, byte[]>? _sendFile;
	private Action<string>? _sendDelete;
	private Action<string, long, string, int>? _sendManifest;
	private Action<string, int, int, byte[], bool>? _sendChunk;

	private sealed class IncomingTransfer
	{
		public int Total;
		public string Hash = "";
		public byte[][]? Chunks;
		public int Received;
	}

	private readonly Dictionary<string, IncomingTransfer> _incoming = new();

	public const int ChunkThreshold = 256 * 1024;
	public const int ChunkSize = 64 * 1024;

	public async Task StartAsync( string rootPath, Action<string, byte[]> sendFile, Action<string> sendDelete,
		Action<string, long, string, int>? sendManifest = null,
		Action<string, int, int, byte[], bool>? sendChunk = null )
	{
		Stop(); // prevent double-start leaks (old watcher/scan tasks)

		_rootPath = rootPath;
		_sendFile = sendFile;
		_sendDelete = sendDelete;
		_sendManifest = sendManifest;
		_sendChunk = sendChunk;

		// Initial hash scan can take seconds on big projects (GBs of assets) —
		// never block the editor UI thread. Watcher + periodic scan start after it.
		await Task.Run( () => ScanAll( rootPath ) ).ConfigureAwait( false );

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

		// Periodic scan to catch changes that FileSystemWatcher misses (e.g. S&box scene saves)
		_scanCts = new CancellationTokenSource();
		_ = PeriodicScanAsync( _scanCts.Token );

		OnLog?.Invoke( $"Файловый синк: {rootPath}" );
	}

	/// <summary>
	/// Forget all known state so the next scan resends everything (reconnect resync:
	/// offline edits were hashed but never reached peers).
	/// </summary>
	public void Resync()
	{
		_knownHashes.Clear();
		_knownWriteTimes.Clear();
		_incoming.Clear();
	}

	public void Stop()
	{
		_watcher?.Dispose();
		_watcher = null;
		_debounceCts?.Cancel();
		_scanCts?.Cancel();
	}

	private async Task PeriodicScanAsync( CancellationToken ct )
	{
		while ( !ct.IsCancellationRequested )
		{
			try { await Task.Delay( 200, ct ); }
			catch ( OperationCanceledException ) { break; }

			try
			{
				var currentFiles = EnumerateFilesSafe( _rootPath )
					.Where( f => !ShouldIgnore( f ) )
					.ToDictionary( f => NormalizePath( GetRelativePath( f, _rootPath ) ), f => f );

				// Check for modified files
				foreach ( var (norm, fullPath) in currentFiles )
				{
					lock ( _applyingChanges )
					{
						if ( _applyingChanges.Contains( norm ) ) continue;
					}

					try
					{
						var fileInfo = new FileInfo( fullPath );
						var lastWrite = fileInfo.LastWriteTimeUtc;

						if ( _knownWriteTimes.TryGetValue( norm, out var prevWrite ) && prevWrite == lastWrite )
							continue;

						var data = File.ReadAllBytes( fullPath );
						var h = Hash( data );

						if ( _knownHashes.TryGetValue( norm, out var prevHash ) && prevHash == h )
						{
							_knownWriteTimes[norm] = lastWrite;
							continue;
						}

						_knownHashes[norm] = h;
						_knownWriteTimes[norm] = lastWrite;
						var rel = GetRelativePath( fullPath, _rootPath );
						SendFileSmart( rel, data );
					}
					catch { }
				}

				// Check for deleted files

				// Check for deleted files — with a 2s grace period. One transient scan
				// hiccup must never broadcast a delete (it once wiped real scene files).
				var now = DateTime.UtcNow;
				foreach ( var norm in _knownHashes.Keys.ToList() )
				{
					if ( currentFiles.ContainsKey( norm ) )
					{
						_missingSince.Remove( norm );
						continue;
					}

					// Never delete files that SceneDeltaService owns (scenes/prefabs)
					// — they live on their own watcher's lifecycle.
					if ( norm.EndsWith( ".scene" ) || norm.EndsWith( ".scene_c" ) ||
						 norm.EndsWith( ".scene_d" ) || norm.EndsWith( ".prefab" ) ||
						 norm.StartsWith( "__patches__/" ) )
						continue;

					if ( !_missingSince.TryGetValue( norm, out var since ) )
					{
						_missingSince[norm] = now;
						continue;
					}
					if ( now - since < TimeSpan.FromSeconds( 2 ) ) continue;

					_missingSince.Remove( norm );
					var rel = norm.Replace( '\\', '/' );
					if ( File.Exists( Path.Combine( _rootPath, rel ) ) ) continue; // final proof
					_knownHashes.Remove( norm );
					_knownWriteTimes.Remove( norm );
					_sendDelete?.Invoke( rel );
					OnLog?.Invoke( $"[out] удалён: {rel}" );
				}
			}
			catch { }
		}
	}

	public void ApplyRemoteFile( string relPath, byte[] data )
	{
		if ( ShouldIgnore( relPath ) ) return; // e.g. .scene is owned by SceneDeltaService

		var fullPath = Path.Combine( _rootPath, relPath );
		var dir = Path.GetDirectoryName( fullPath );
		if ( dir != null ) Directory.CreateDirectory( dir );

		var isNew = !File.Exists( fullPath );
		var norm = NormalizePath( relPath );
		lock ( _applyingChanges ) _applyingChanges.Add( norm );
		try
		{
			File.WriteAllBytes( fullPath, data );
			_knownHashes[norm] = Hash( data );
			_knownWriteTimes[norm] = DateTime.UtcNow;
		}
		finally
		{
			lock ( _applyingChanges ) _applyingChanges.Remove( norm );
		}

		// New asset from a peer: register it so the Asset Browser / compilers pick it up.
		// Code hotloads on its own; scenes are handled via SceneEditorSession reload.
		if ( isNew )
		{
			var ext = Path.GetExtension( fullPath );
			if ( !ext.Equals( ".cs", StringComparison.OrdinalIgnoreCase ) &&
				 !ext.Equals( ".razor", StringComparison.OrdinalIgnoreCase ) &&
				 !ext.Equals( ".scene", StringComparison.OrdinalIgnoreCase ) )
			{
				try { AssetSystem.RegisterFile( fullPath ); }
				catch { }
			}
		}
	}

	public void ApplyRemoteDelete( string relPath )
	{
		if ( ShouldIgnore( relPath ) ) return;

		var fullPath = Path.Combine( _rootPath, relPath );
		var norm = NormalizePath( relPath );
		_incoming.Remove( norm );
		lock ( _applyingChanges ) _applyingChanges.Add( norm );
		try
		{
			if ( File.Exists( fullPath ) ) File.Delete( fullPath );
			_knownHashes.Remove( norm );
			_knownWriteTimes.Remove( norm );
		}
		finally
		{
			lock ( _applyingChanges ) _applyingChanges.Remove( norm );
		}
	}

	public void ApplyManifest( string relPath, int total, string hash )
	{
		if ( ShouldIgnore( relPath ) ) return;
		if ( total < 1 || total > 256 ) return;

		var norm = NormalizePath( relPath );
		_incoming[norm] = new IncomingTransfer
		{
			Total = total,
			Hash = hash,
			Chunks = new byte[total][],
			Received = 0,
		};
	}

	public void ApplyChunk( string relPath, int index, byte[] chunk )
	{
		if ( ShouldIgnore( relPath ) ) return;

		var norm = NormalizePath( relPath );
		if ( !_incoming.TryGetValue( norm, out var transfer ) ) return;
		if ( transfer.Chunks == null || index < 0 || index >= transfer.Total ) return;
		if ( transfer.Chunks[index] != null ) return; // duplicate

		transfer.Chunks[index] = chunk;
		transfer.Received++;

		if ( transfer.Received < transfer.Total ) return;

		_incoming.Remove( norm );

		var totalLen = transfer.Chunks.Sum( c => c.Length );
		var full = new byte[totalLen];
		var offset = 0;
		foreach ( var c in transfer.Chunks )
		{
			Buffer.BlockCopy( c, 0, full, offset, c.Length );
			offset += c.Length;
		}

		if ( !Hash( full ).Equals( transfer.Hash, StringComparison.OrdinalIgnoreCase ) )
		{
			OnLog?.Invoke( $"[in] chunk hash mismatch: {relPath}" );
			return;
		}

		ApplyRemoteFile( relPath, full );
		OnLog?.Invoke( $"[in] {relPath} ({full.Length} байт, {transfer.Total} чанков)" );
	}

	/// <summary>Big files go out chunked (manifest + 64KB chunks), small ones as a single message.</summary>
	private void SendFileSmart( string relPath, byte[] data )
	{
		if ( data.Length > ChunkThreshold && _sendManifest != null && _sendChunk != null )
		{
			var total = (data.Length + ChunkSize - 1) / ChunkSize;
			_sendManifest( relPath, data.Length, Hash( data ), total );
			for ( int i = 0; i < total; i++ )
			{
				var len = Math.Min( ChunkSize, data.Length - i * ChunkSize );
				var chunk = new byte[len];
				Buffer.BlockCopy( data, i * ChunkSize, chunk, 0, len );
				_sendChunk( relPath, i, total, chunk, i == total - 1 );
			}
			OnLog?.Invoke( $"[out] отправлен чанками: {relPath} ({data.Length} байт, {total} чанков)" );
		}
		else
		{
			_sendFile?.Invoke( relPath, data );
			OnLog?.Invoke( $"[out] отправлен: {relPath} ({data.Length} байт)" );
		}
	}
	private void ScanAll( string rootPath )
	{
		foreach ( var file in EnumerateFilesSafe( rootPath ) )
		{
			if ( ShouldIgnore( file ) ) continue;
			var rel = GetRelativePath( file, rootPath );
			var norm = NormalizePath( rel );
			lock ( _applyingChanges )
			{
				if ( _applyingChanges.Contains( norm ) ) continue; // remotely applied right now
			}
			try
			{
				var data = File.ReadAllBytes( file );
				_knownHashes[norm] = Hash( data );
				_knownWriteTimes[norm] = new FileInfo( file ).LastWriteTimeUtc;
			}
			catch { }
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
					_knownWriteTimes.Remove( norm );
					_sendDelete?.Invoke( rel );
					OnLog?.Invoke( $"[out] удалён: {rel}" );
				}
				continue;
			}

			lock ( _applyingChanges )
			{
				if ( _applyingChanges.Contains( norm ) ) continue;
			}

			try
			{
				var fileInfo = new FileInfo( fullPath );
				var lastWrite = fileInfo.LastWriteTimeUtc;

				if ( _knownWriteTimes.TryGetValue( norm, out var prevWrite ) && prevWrite == lastWrite )
					continue;

				var data = File.ReadAllBytes( fullPath );
				var h = Hash( data );

				if ( _knownHashes.TryGetValue( norm, out var prevHash ) && prevHash == h )
				{
					_knownWriteTimes[norm] = lastWrite;
					continue;
				}

				_knownHashes[norm] = h;
				_knownWriteTimes[norm] = lastWrite;
				SendFileSmart( rel, data );
			}
			catch { }
		}
	}

	private static bool ShouldIgnore( string path )
	{
		var parts = path.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
		var fileName = Path.GetFileName( path );

		// Ignore directories
		if ( parts.Any( p =>
			p.Equals( ".sbox", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( "obj", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( ".git", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( ".vs", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( ".vscode", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( "node_modules", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( "Properties", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( "Libraries", StringComparison.OrdinalIgnoreCase ) ||
			p.Equals( "__patches__", StringComparison.OrdinalIgnoreCase ) ) )
			return true;

		// Ignore project-specific files
		if ( fileName.EndsWith( ".csproj", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.EndsWith( ".csproj.user", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.EndsWith( ".slnx", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.EndsWith( ".sbproj", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.EndsWith( ".scene", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.EndsWith( ".scene_c", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.EndsWith( ".scene_d", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.Equals( ".editorconfig", StringComparison.OrdinalIgnoreCase ) ||
			 fileName.Equals( "launchSettings.json", StringComparison.OrdinalIgnoreCase ) )
			return true;

		return false;
	}

	private static string NormalizePath( string path ) =>
		path.Replace( '\\', '/' ).Trim( '/' ).ToLowerInvariant();

	private static string GetRelativePath( string fullPath, string rootPath ) =>
		Path.GetRelativePath( rootPath, fullPath ).Replace( '\\', '/' );

	private static string Hash( byte[] data ) =>
		Convert.ToHexString( MD5.HashData( data ) );

	/// <summary>
	/// Directory.EnumerateFiles(all-directories) dies entirely on one denied
	/// folder (UnauthorizedAccessException) — killing scans and even the connect
	/// flow. Manual stack traversal skips bad folders instead.
	/// </summary>
	private static IEnumerable<string> EnumerateFilesSafe( string root )
	{
		var stack = new Stack<string>();
		stack.Push( root );
		while ( stack.Count > 0 )
		{
			var dir = stack.Pop();
			string[] subdirs = Array.Empty<string>();
			string[] files = Array.Empty<string>();
			try { subdirs = Directory.GetDirectories( dir ); }
			catch { continue; }
			try { files = Directory.GetFiles( dir ); }
			catch { }
			foreach ( var f in files ) yield return f;
			foreach ( var d in subdirs ) stack.Push( d );
		}
	}

	public void Dispose() => Stop();
}
