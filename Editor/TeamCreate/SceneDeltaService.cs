using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor.TeamCreate;

/// <summary>
/// Watches *.scene (scenes dir) and *.prefab (Assets recursive) JSON files and sends
/// tree patches instead of whole files. Both formats are trees of game objects keyed
/// by __guid, so one engine handles both: the tree is flattened to
/// guid -> props (+ parent map), diffed per-field, and merged back with tree surgery.
///
/// Patch ops:
/// - added: [{parent: guid|null, object: full subtree}] (parent null = forest root)
/// - moved: [{guid, to: parent guid|null}] (reparent, incl. order changes ignored)
/// - updated: [{guid, props: {key: value}}] (per-field LWW, tombstone deletions)
/// - removed: [guid] (delete wins over concurrent update)
///
/// Full snapshots go out once on Start so late joiners get current state
/// via the Hub file cache; live edits go out as small patches.
///
/// Conflict policy (convergent, last-writer-wins per FIELD):
/// - same field changed on both sides  -> last received patch wins for that field
/// - different fields of one object    -> both survive (merge)
/// - update vs delete                  -> delete wins (update to a missing object is ignored)
/// - delete vs local edits             -> delete wins (both sides converge to deleted)
/// - move vs move                      -> last received parent wins
/// </summary>
public sealed class SceneDeltaService : IDisposable
{
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	private sealed class TreeState
	{
		public string FullPath = "";
		public string RelPath = "";
		public bool IsPrefab;
		public DateTime LastWrite = DateTime.MinValue;
		// guid -> (prop name -> canonical json value); "Children" excluded (structural)
		public Dictionary<string, Dictionary<string, string>> Objects = new( StringComparer.Ordinal );
		// guid -> full subtree canonical json (for sending brand-new objects)
		public Dictionary<string, string> Full = new( StringComparer.Ordinal );
		// guid -> parent guid (null = forest root: GameObjects[] element / RootObject)
		public Dictionary<string, string?> Parents = new( StringComparer.Ordinal );
		public Dictionary<string, string> TopLevel = new( StringComparer.Ordinal );
	}

	private readonly Dictionary<string, TreeState> _scenes = new( StringComparer.OrdinalIgnoreCase );
	private readonly HashSet<string> _suppress = new( StringComparer.OrdinalIgnoreCase );
	private readonly object _lock = new();

	private string _scenesDir = "";
	private string _assetsDir = "";
	private string _projectRoot = "";
	private Action<string, byte[]>? _sendFile;
	private Action<string>? _sendDelete;
	private CancellationTokenSource? _cts;
	private readonly HashSet<string> _parseFailLogged = new( StringComparer.OrdinalIgnoreCase );
	private TaskCompletionSource _poke = new( TaskCreationOptions.RunContinuationsAsynchronously );
	// Delete safety: file must be missing from many consecutive scans before we dare
	// broadcast a delete (a single transient scan hiccup must never kill a scene file).
	private readonly Dictionary<string, DateTime> _missingSince = new( StringComparer.OrdinalIgnoreCase );

	public event Action<string>? OnLog;

	/// <summary>Fired (on a background thread) after a remote patch/snapshot was written to disk.</summary>
	public event Action<string>? OnSceneApplied;

	/// <summary>Wake the watch loop immediately (e.g. on scene.saved) instead of waiting for the next poll tick.</summary>
	public void ForceCheck()
	{
		var old = Interlocked.Exchange( ref _poke, new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously ) );
		old.TrySetResult();
	}

	/// <summary>Relative paths (Assets/...) of all currently tracked scene/prefab files.</summary>
	public List<string> GetTrackedScenes()
	{
		lock ( _lock )
			return _scenes.Values
				.Select( s => string.IsNullOrEmpty( s.RelPath ) ? ToRelPath( s.FullPath ) : s.RelPath )
				.Distinct()
				.ToList();
	}

	public void Start( string projectRoot, string scenesDir, Action<string, byte[]> sendFile, Action<string> sendDelete )
	{
		Stop(); // prevent double-start leaks

		_projectRoot = projectRoot;
		_scenesDir = scenesDir;
		_assetsDir = Path.Combine( projectRoot, "Assets" );
		_sendFile = sendFile;
		_sendDelete = sendDelete;

		lock ( _lock )
		{
			_scenes.Clear();
			_suppress.Clear();
		}

		if ( Directory.Exists( _scenesDir ) )
		{
			foreach ( var file in Directory.GetFiles( _scenesDir, "*.scene" ) )
				TrackFile( file );
		}

		if ( Directory.Exists( _assetsDir ) )
		{
			foreach ( var file in EnumerateFilesSafe( _assetsDir ).Where( f => f.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) ) )
				TrackFile( file );
		}

		_cts = new CancellationTokenSource();
		_ = WatchAsync( _cts.Token );

		lock ( _lock )
			OnLog?.Invoke( $"SceneDelta: watching {_scenesDir} + prefabs (tracked: {_scenes.Count})" );
	}

	private void TrackFile( string file )
	{
		file = NormFull( file );
		var state = ReadState( file );
		if ( state == null )
		{
			OnLog?.Invoke( $"[delta] не смог распарсить: {file}" );
			return;
		}

		lock ( _lock ) _scenes[file] = state;

		// Full snapshot so late joiners get current state via Hub cache
		try
		{
			var data = File.ReadAllBytes( file );
			_sendFile?.Invoke( ToRelPath( file ), data );
		}
		catch { }
	}

	public void Stop()
	{
		_cts?.Cancel();
		_cts = null;
	}

	/// <summary>
	/// Forget all known state so the next tick sends full snapshots
	/// (reconnect resync: offline edits were hashed but never reached peers).
	/// </summary>
	public void Resync()
	{
		lock ( _lock )
		{
			_scenes.Clear();
			_liveBaseline.Clear();
		}
		OnLog?.Invoke( "[delta] resync: baseline сброшен" );
	}

	private string ToRelPath( string fullPath ) =>
		Path.GetRelativePath( _projectRoot, fullPath ).Replace( '\\', '/' );

	private static string NormFull( string path ) =>
		path.Replace( '/', '\\' );

	private static string Canon( JsonNode? node ) =>
		JsonSerializer.Serialize( node );

	private static bool IsSceneFile( string fullPath ) =>
		fullPath.EndsWith( ".scene", StringComparison.OrdinalIgnoreCase );

	/// <summary>Sync read for Start() (called on UI thread, single attempt, never blocks long).</summary>
	private static TreeState? ReadState( string fullPath )
	{
		try
		{
			var text = File.ReadAllText( fullPath, Encoding.UTF8 );
			return ParseState( fullPath, text );
		}
		catch { return null; }
	}

	/// <summary>Async read with retries — never blocks the calling thread.</summary>
	private static async Task<TreeState?> ReadStateAsync( string fullPath, CancellationToken ct )
	{
		for ( int attempt = 0; attempt < 4; attempt++ )
		{
			try
			{
				// File IO on threadpool so the UI thread is never blocked
				var text = await Task.Run( () => File.ReadAllText( fullPath, Encoding.UTF8 ), ct ).ConfigureAwait( false );
				var state = ParseState( fullPath, text );
				if ( state != null ) return state;
			}
			catch ( OperationCanceledException ) { return null; }
			catch { }

			try { await Task.Delay( 30, ct ).ConfigureAwait( false ); }
			catch ( OperationCanceledException ) { return null; }
		}

		return null;
	}

	private static void FlattenNode( JsonObject node, string? parentGuid, TreeState state )
	{
		if ( node["__guid"]?.GetValue<string>() is not string guid ) return;

		var props = new Dictionary<string, string>( StringComparer.Ordinal );
		foreach ( var p in node )
		{
			if ( p.Key == "Children" ) continue;
			props[p.Key] = Canon( p.Value );
		}
		state.Objects[guid] = props;
		state.Full[guid] = Canon( node );
		state.Parents[guid] = parentGuid;

		if ( node["Children"] is JsonArray children )
		{
			foreach ( var child in children )
			{
				if ( child is JsonObject co )
					FlattenNode( co, guid, state );
			}
		}
	}

	private static TreeState? ParseState( string fullPath, string text )
	{
		try
		{
			DateTime lastWrite;
			try { lastWrite = new FileInfo( fullPath ).LastWriteTimeUtc; }
			catch { lastWrite = DateTime.MinValue; }
			return ParseStateText( fullPath, text, lastWrite );
		}
		catch { return null; }
	}

	/// <summary>Parse raw JSON text into a TreeState (fullPath decides scene/prefab shape; stamp supplied by caller).</summary>
	private static TreeState? ParseStateText( string fullPath, string text, DateTime lastWrite )
	{
		try
		{
			var root = JsonNode.Parse( text )?.AsObject();
			if ( root == null ) return null;

			var isPrefab = !IsSceneFile( fullPath );
			var state = new TreeState
			{
				FullPath = fullPath,
				IsPrefab = isPrefab,
				LastWrite = lastWrite,
			};

			if ( isPrefab )
			{
				if ( root["RootObject"] is not JsonObject rootObj ) return null;
				FlattenNode( rootObj, null, state );
				foreach ( var kv in root )
				{
					if ( kv.Key == "RootObject" ) continue;
					state.TopLevel[kv.Key] = Canon( kv.Value );
				}
			}
			else
			{
				if ( root["GameObjects"] is JsonArray arr )
				{
					foreach ( var obj in arr )
					{
						if ( obj is JsonObject o )
							FlattenNode( o, null, state );
					}
				}
				foreach ( var kv in root )
				{
					if ( kv.Key == "GameObjects" ) continue;
					state.TopLevel[kv.Key] = Canon( kv.Value );
				}
			}

			return state;
		}
		catch ( JsonException ) { return null; }
		catch ( IOException ) { return null; }
	}

	private List<string> CollectFiles()
	{
		var files = new List<string>();
		try
		{
			if ( Directory.Exists( _scenesDir ) )
				files.AddRange( Directory.GetFiles( _scenesDir, "*.scene" ) );
		}
		catch { }
		try
		{
			if ( Directory.Exists( _assetsDir ) )
			{
				foreach ( var file in EnumerateFilesSafe( _assetsDir ) )
				{
					if ( file.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) )
						files.Add( file );
				}
			}
		}
		catch { }
		return files;
	}

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

	private async Task WatchAsync( CancellationToken ct )
	{
		int tick = 0;
		while ( !ct.IsCancellationRequested )
		{
			// 10ms poll tick OR immediate wake via ForceCheck() (scene.saved hook)
			try { await Task.WhenAny( Task.Delay( 10, ct ), _poke.Task ).ConfigureAwait( false ); }
			catch ( OperationCanceledException ) { break; }

			try
			{
				tick++;
				var files = CollectFiles();
				var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

				foreach ( var file in files )
				{
					try
					{
						await ProcessFileAsync( file, seen, ct ).ConfigureAwait( false );
					}
					catch ( OperationCanceledException ) { break; }
					catch { /* one bad file must never kill the watch loop */ }
				}

				// Deleted files — with a 2s grace period. One failed directory read must
				// NEVER trigger a delete broadcast (that once nuked real scene files).
				List<(string Key, string Rel)> gone = new();
				lock ( _lock )
				{
					foreach ( var kv in _scenes )
					{
						if ( seen.Contains( kv.Value.FullPath ) )
						{
							_missingSince.Remove( kv.Key );
							continue;
						}
						if ( !_missingSince.TryGetValue( kv.Key, out var since ) )
						{
							_missingSince[kv.Key] = DateTime.UtcNow; // first miss — note, don't act
							continue;
						}
						if ( DateTime.UtcNow - since < TimeSpan.FromSeconds( 2 ) ) continue;
						gone.Add( (kv.Key, ToRelPath( kv.Value.FullPath )) );
					}
					foreach ( var (key, _) in gone )
					{
						_scenes.Remove( key );
						_missingSince.Remove( key );
					}
				}
				foreach ( var (_, rel) in gone )
				{
					// Final proof: really gone, and gone for a while
					if ( File.Exists( Path.Combine( _projectRoot, rel ) ) ) continue;
					_sendDelete?.Invoke( rel );
					OnLog?.Invoke( $"[out] удалён: {rel}" );
				}

				// Heartbeat so we never again debug a silently-dead loop
				if ( tick % 300 == 0 )
				{
					lock ( _lock )
						OnLog?.Invoke( $"[delta] тик {tick}, tracked: {_scenes.Count}, files seen: {files.Count}" );
				}
			}
			catch ( OperationCanceledException ) { break; }
			catch ( Exception ex )
			{
				OnLog?.Invoke( $"[delta] итерация упала: {ex.GetType().Name}: {ex.Message}" );
			}
		}
	}

	private async Task ProcessFileAsync( string file, HashSet<string> seen, CancellationToken ct )
	{
		file = NormFull( file );
		seen.Add( file );

		if ( IsSuppressed( file ) ) return;

		DateTime lastWrite;
		try { lastWrite = new FileInfo( file ).LastWriteTimeUtc; }
		catch { return; }

		TreeState? prev;
		lock ( _lock ) _scenes.TryGetValue( file, out prev );

		if ( prev != null && prev.LastWrite == lastWrite ) return;

		// Wait for S&box to finish writing, then read (retries inside)
		await Task.Delay( 50, ct ).ConfigureAwait( false );

		var next = await ReadStateAsync( file, ct ).ConfigureAwait( false );
		if ( next == null )
		{
			if ( _parseFailLogged.Add( file ) )
				OnLog?.Invoke( $"[delta] parse fail: {file} (файл залочен?)" );
			return; // will retry on next tick; LastWrite NOT advanced
		}
		next.RelPath = ToRelPath( file );

		if ( prev == null )
		{
			// New file: send full snapshot
			lock ( _lock ) _scenes[file] = next;
			try
			{
				var data = await Task.Run( () => File.ReadAllBytes( file ), ct ).ConfigureAwait( false );
				_sendFile?.Invoke( next.RelPath, data );
				OnLog?.Invoke( $"[out] snapshot: {next.RelPath} ({data.Length} байт)" );
			}
			catch ( OperationCanceledException ) { throw; }
			catch { }
			return;
		}

		var patch = BuildPatch( prev, next, file, out var counts );

		lock ( _lock ) _scenes[file] = next;
		if ( patch == null ) return;

		var patchJson = patch.ToJsonString();
		_sendFile?.Invoke( $"__patches__/{Path.GetFileName( file )}.patch", Encoding.UTF8.GetBytes( patchJson ) );
		OnLog?.Invoke( $"[out] patch {Path.GetFileName( file )}: +{counts.Added} >{counts.Moved} ~{counts.Updated} -{counts.Removed}" );
	}

	private struct PatchCounts { public int Added, Moved, Updated, Removed; }

	/// <summary>Build a patch from two states. Null = converged (no outgoing changes needed).</summary>
	private static JsonObject? BuildPatch( TreeState prev, TreeState next, string file, out PatchCounts counts )
	{
		counts = default;

		var added = new List<JsonObject>();   // { parent: guid|null, object: {...} }
		var moved = new List<JsonObject>();   // { guid, to: parent guid|null }
		var updated = new List<JsonObject>(); // { guid, props: { key: value } }
		var removed = new List<string>();

		foreach ( var kv in next.Objects )
		{
			if ( !prev.Objects.TryGetValue( kv.Key, out var oldProps ) )
			{
				// Brand-new object: send whole subtree
				var node = JsonNode.Parse( next.Full[kv.Key] )?.AsObject();
				if ( node == null ) continue;
				next.Parents.TryGetValue( kv.Key, out var parent );
				added.Add( new JsonObject
				{
					["parent"] = parent != null ? JsonValue.Create( parent ) : JsonValue.Create( (string?)null ),
					["object"] = node,
				} );
				continue;
			}

			// Reparent?
			prev.Parents.TryGetValue( kv.Key, out var oldParent );
			next.Parents.TryGetValue( kv.Key, out var newParent );
			if ( oldParent != newParent )
			{
				moved.Add( new JsonObject
				{
					["guid"] = kv.Key,
					["to"] = newParent != null ? JsonValue.Create( newParent ) : JsonValue.Create( (string?)null ),
				} );
			}

			var changedProps = new JsonObject();
			foreach ( var p in kv.Value )
			{
				if ( !oldProps.TryGetValue( p.Key, out var oldCanon ) || oldCanon != p.Value )
					changedProps[p.Key] = JsonNode.Parse( p.Value );
			}
			// properties deleted from the object — explicit tombstone
			// (plain null is ambiguous: files contain legit nulls like "Author": null)
			foreach ( var oldKey in oldProps.Keys )
			{
				if ( !kv.Value.ContainsKey( oldKey ) )
					changedProps[oldKey] = new JsonObject { ["__deleted"] = true };
			}

			if ( changedProps.Count > 0 )
			{
				updated.Add( new JsonObject
				{
					["guid"] = kv.Key,
					["props"] = changedProps,
				} );
			}
		}

		foreach ( var guid in prev.Objects.Keys )
		{
			if ( !next.Objects.ContainsKey( guid ) )
				removed.Add( guid );
		}

		JsonObject? topChanged = null;
		foreach ( var kv in next.TopLevel )
		{
			if ( !prev.TopLevel.TryGetValue( kv.Key, out var oldCanon ) || oldCanon != kv.Value )
			{
				topChanged ??= new JsonObject();
				topChanged[kv.Key] = JsonNode.Parse( kv.Value );
			}
		}

		counts = new PatchCounts
		{
			Added = added.Count,
			Moved = moved.Count,
			Updated = updated.Count,
			Removed = removed.Count,
		};

		if ( counts.Added == 0 && counts.Moved == 0 && counts.Updated == 0 && counts.Removed == 0 && topChanged == null )
			return null;

		var patch = new JsonObject
		{
			["type"] = next.IsPrefab ? "prefab_patch" : "scene_patch",
			["kind"] = next.IsPrefab ? "prefab" : "scene",
			["file"] = Path.GetFileName( file ),
			["scene"] = Path.GetFileName( file ), // compat alias
			["added"] = new JsonArray( added.ToArray() ),
			["moved"] = new JsonArray( moved.ToArray() ),
			["updated"] = new JsonArray( updated.ToArray() ),
			["removed"] = new JsonArray( removed.Select( g => JsonValue.Create( g ) ).ToArray() ),
		};
		if ( topChanged != null ) patch["top"] = topChanged;
		return patch;
	}

	// ──── Live (in-memory) scene diffing — real-time sync without Ctrl+S ────

	private readonly Dictionary<string, TreeState> _liveBaseline = new( StringComparer.OrdinalIgnoreCase );
	// Anti-ping-pong: after applying a remote patch we recompute the baseline from
	// the post-reload live scene — live serialize formats differently than files,
	// so the next tick MUST see the reloaded state, not what we wrote to disk.
	private readonly Dictionary<string, DateTime> _suppressUntil = new( StringComparer.OrdinalIgnoreCase );
	private bool _liveFormatWarned;

	/// <summary>While a remote patch is being applied (write + reload), this side must not send.</summary>
	private void Suppress( string fullPath, TimeSpan duration )
	{
		lock ( _lock )
		{
			_suppress.Add( fullPath );
			_suppressUntil[fullPath] = DateTime.UtcNow + duration;
		}
	}

	private bool IsSuppressed( string fullPath )
	{
		lock ( _lock )
		{
			if ( _suppress.Contains( fullPath ) ) return true;
			if ( _suppressUntil.TryGetValue( fullPath, out var until ) )
				return DateTime.UtcNow < until;
			return false;
		}
	}

	private void EndSuppress( string fullPath )
	{
		lock ( _lock ) _suppress.Remove( fullPath );
	}

	/// <summary>Force live baseline to a given serialized scene (used right after applying a remote patch).</summary>
	public void RebaselineLive( string fullPath, string liveJson )
	{
		fullPath = NormFull( fullPath );
		var state = ParseStateText( fullPath, liveJson, DateTime.MinValue );
		if ( state == null ) return;
		state.RelPath = ToRelPath( fullPath );
		lock ( _lock )
		{
			_liveBaseline[fullPath] = state;
			_scenes[fullPath] = state;
		}
	}

	/// <summary>
	/// Feed a serialized LIVE scene (from the editor session, not the disk file).
	/// Sends a patch if anything changed since the last baseline. Also advances the
	/// file-watcher baseline so a later Ctrl+S on the same content produces no echo.
	/// Scenes only (prefabs stay file-driven).
	/// </summary>
		public void LiveUpdate( string relPath, string liveJson )
	{
		if ( !relPath.EndsWith( ".scene", StringComparison.OrdinalIgnoreCase ) ) return;

		// Safety: if Scene.Serialize shape is unexpected, refuse to diff —
		// a format without GameObjects would look like "everything deleted".
		if ( !liveJson.Contains( "\"GameObjects\"" ) )
		{
			if ( !_liveFormatWarned )
			{
				_liveFormatWarned = true;
				OnLog?.Invoke( "[live] Scene.Serialize вернул неожиданный формат — live sync выключен" );
			}
			return;
		}

		var fullPath = NormFull( Path.Combine( _projectRoot, relPath ) );

		// Anti-ping-pong: during/shortly after applying a remote patch, don't send
		if ( IsSuppressed( fullPath ) ) return;

		DateTime fileStamp = DateTime.MinValue;
		try { fileStamp = new FileInfo( fullPath ).LastWriteTimeUtc; } catch { }

		var next = ParseStateText( fullPath, liveJson, fileStamp );
		if ( next == null ) return;
		next.RelPath = relPath;

		TreeState? prev;
		lock ( _lock )
		{
			if ( !_liveBaseline.TryGetValue( fullPath, out prev ) )
			{
				// Seed from file baseline so the first live tick diffs against disk state
				_scenes.TryGetValue( fullPath, out prev );
			}
		}

		if ( prev != null )
		{
			var patch = BuildPatch( prev, next, fullPath, out var counts );

			// Never send empty patches (counts all zero → just churn/noise)
			if ( patch != null && counts.Added == 0 && counts.Moved == 0 && counts.Updated == 0 && counts.Removed == 0 )
				patch = null;

			if ( patch != null )
			{
				var patchJson = patch.ToJsonString();
				_sendFile?.Invoke( $"__patches__/{Path.GetFileName( fullPath )}.patch", Encoding.UTF8.GetBytes( patchJson ) );
				OnLog?.Invoke( $"[out] live patch {Path.GetFileName( fullPath )}: +{counts.Added} >{counts.Moved} ~{counts.Updated} -{counts.Removed}" );
			}
		}

		lock ( _lock )
		{
			_liveBaseline[fullPath] = next;

			// Advance the file baseline content WITHOUT its stamp: after the next real
			// Ctrl+S, the watcher diffs the saved file against this state and emits nothing.
			// (Stamp stays at the file's actual LastWriteTime so we don't skip it.)
			if ( _scenes.TryGetValue( fullPath, out var fileState ) )
			{
				fileState.Objects = next.Objects;
				fileState.Full = next.Full;
				fileState.Parents = next.Parents;
				fileState.TopLevel = next.TopLevel;
			}
			else
			{
				_scenes[fullPath] = next;
			}
		}
	}

	// (live baseline kept in sync inline at apply sites; no extra hook needed)

	/// <summary>Full snapshot from a peer (late-join sync). Writes file without echo.</summary>
	public async void ApplyRemoteFull( string relPath, byte[] data )
	{
		var fullPath = NormFull( Path.Combine( _projectRoot, relPath ) );

		Suppress( fullPath, TimeSpan.FromSeconds( 1.5 ) );
		try
		{
			await Task.Run( () =>
			{
				var dir = Path.GetDirectoryName( fullPath );
				if ( dir != null ) Directory.CreateDirectory( dir );
				File.WriteAllBytes( fullPath, data );
			} ).ConfigureAwait( false );

			var state = await ReadStateAsync( fullPath, CancellationToken.None ).ConfigureAwait( false );
			if ( state != null )
			{
				state.RelPath = relPath;
				lock ( _lock ) _scenes[fullPath] = state;
				lock ( _lock ) _liveBaseline[fullPath] = state; // no echo: live watcher must see it as known
			}
			OnLog?.Invoke( $"[in] snapshot: {relPath} ({data.Length} байт)" );
			OnSceneApplied?.Invoke( relPath );
		}
		catch ( Exception ex )
		{
			OnLog?.Invoke( $"[in] snapshot error: {ex.Message}" );
		}
		finally
		{
			EndSuppress( fullPath );
		}
	}

	private string? FindTrackedFile( string fileName )
	{
		lock ( _lock )
		{
			foreach ( var kv in _scenes )
			{
				if ( Path.GetFileName( kv.Value.FullPath ).Equals( fileName, StringComparison.OrdinalIgnoreCase ) )
					return kv.Value.FullPath;
			}
		}

		// Fallbacks: scenes dir, then recursive search under Assets
		var probe = Path.Combine( _scenesDir, fileName );
		if ( File.Exists( probe ) ) return probe;

		try
		{
			if ( Directory.Exists( _assetsDir ) )
			{
				foreach ( var f in EnumerateFilesSafe( _assetsDir ) )
				{
					if ( Path.GetFileName( f ).Equals( fileName, StringComparison.OrdinalIgnoreCase ) )
						return f;
				}
			}
		}
		catch { }
		return null;
	}

	private static bool IsTombstone( JsonNode? node ) =>
		node is JsonObject o && o.Count == 1 && o["__deleted"]?.GetValue<bool>() == true;

	/// <summary>Forest root array: GameObjects[] for scenes, RootObject.Children for prefabs.</summary>
	private static JsonArray GetRootArray( JsonObject root, bool isPrefab )
	{
		if ( isPrefab )
		{
			var rootObj = root["RootObject"]?.AsObject();
			if ( rootObj == null ) return new JsonArray(); // detached; caller skips
			return EnsureChildren( rootObj );
		}
		if ( root["GameObjects"] is not JsonArray arr )
		{
			arr = new JsonArray();
			root["GameObjects"] = arr;
		}
		return arr;
	}

	private static JsonArray EnsureChildren( JsonObject node )
	{
		if ( node["Children"] is not JsonArray arr )
		{
			arr = new JsonArray();
			node["Children"] = arr;
		}
		return arr;
	}

	private static void MergeProps( JsonObject target, JsonObject props )
	{
		foreach ( var p in props )
		{
			if ( p.Key == "Children" ) continue; // structural; handled via moved/added/removed
			if ( IsTombstone( p.Value ) )
				target.Remove( p.Key );
			else
				target[p.Key] = JsonNode.Parse( p.Value?.ToJsonString() ?? "null" );
		}
	}

	/// <summary>Incremental patch from a peer. Merges per-property changes without echo.</summary>
	public async void ApplyPatch( string patchJson )
	{
		string? fileName = null;
		try
		{
			var patch = JsonNode.Parse( patchJson )?.AsObject();
			if ( patch == null ) return;

			fileName = patch["file"]?.GetValue<string>() ?? patch["scene"]?.GetValue<string>();
			if ( string.IsNullOrEmpty( fileName ) ) return;

			var fullPath = FindTrackedFile( fileName );
			if ( fullPath != null ) fullPath = NormFull( fullPath );
			if ( fullPath == null || !File.Exists( fullPath ) ) return;
			var isPrefab = !IsSceneFile( fullPath );

			var text = await Task.Run( () => File.ReadAllText( fullPath, Encoding.UTF8 ) ).ConfigureAwait( false );
			var root = JsonNode.Parse( text )?.AsObject();
			if ( root == null ) return;

			// Index current tree: guid -> (parent array, index)
			var index = new Dictionary<string, (JsonArray Parent, int At)>( StringComparer.Ordinal );
			void IndexSubtree( JsonObject node, JsonArray parent, int at )
			{
				if ( node["__guid"]?.GetValue<string>() is string g )
					index[g] = (parent, at);
				if ( node["Children"] is JsonArray ch )
				{
					for ( int i = 0; i < ch.Count; i++ )
					{
						if ( ch[i] is JsonObject co )
							IndexSubtree( co, ch, i );
					}
				}
			}
			if ( isPrefab )
			{
				if ( root["RootObject"] is JsonObject rootObj )
				{
					if ( rootObj["Children"] is JsonArray rch )
					{
						for ( int i = 0; i < rch.Count; i++ )
						{
							if ( rch[i] is JsonObject co )
								IndexSubtree( co, rch, i );
						}
					}
				}
			}
			else if ( root["GameObjects"] is JsonArray arr )
			{
				for ( int i = 0; i < arr.Count; i++ )
				{
					if ( arr[i] is JsonObject o )
						IndexSubtree( o, arr, i );
				}
			}

			int applied = 0;
			int skipped = 0;

			// 1. Deletes first (delete wins over concurrent update/move)
			var doomed = new HashSet<string>( StringComparer.Ordinal );
			if ( patch["removed"] is JsonArray removed )
			{
				foreach ( var n in removed )
				{
					var g = n?.GetValue<string>();
					if ( !string.IsNullOrEmpty( g ) ) doomed.Add( g! );
				}
			}
			// Delete bottom-up: sort by depth would be ideal; repeated passes are simpler
			for ( int pass = 0; pass < 2 && doomed.Count > 0; pass++ )
			{
				foreach ( var g in doomed.ToList() )
				{
					if ( !index.TryGetValue( g, out var loc ) ) { doomed.Remove( g ); continue; }
					var dead = loc.Parent[loc.At] as JsonObject;
					loc.Parent.RemoveAt( loc.At );
					index.Remove( g );
					doomed.Remove( g );
					applied++;
					ReindexAfterRemove( index, loc.Parent, loc.At );
					// Descendants die with the subtree: purge stale index/doomed entries
					// (otherwise a later op could scribble into the detached branch)
					if ( dead != null ) PurgeSubtree( index, doomed, dead );
				}
			}
			skipped += doomed.Count; // already gone or unresolvable

			// Helper: attach a node under a parent guid (null = forest root)
			JsonArray ResolveParent( string? parentGuid, out bool fallback )
			{
				fallback = false;
				if ( parentGuid != null && index.TryGetValue( parentGuid, out var ploc ) &&
					 ploc.Parent[ploc.At] is JsonObject pnode )
					return EnsureChildren( pnode );

				if ( parentGuid != null )
					fallback = true; // parent gone: delete wins for structure, keep node at forest root
				return GetRootArray( root, isPrefab );
			}

			// 2. Moves (last received parent wins)
			if ( patch["moved"] is JsonArray moved )
			{
				foreach ( var item in moved )
				{
					if ( item is not JsonObject m ) continue;
					if ( m["guid"]?.GetValue<string>() is not string guid ) continue;
					var to = m["to"]?.GetValue<string>(); // null = forest root

					if ( doomed.Contains( guid ) ) { skipped++; continue; }
					if ( !index.TryGetValue( guid, out var loc ) ) { skipped++; continue; }
					if ( loc.Parent[loc.At] is not JsonObject node ) continue;

					// Cycle guard: moving under itself or its own descendant would
					// corrupt the tree (and hang the serializer in infinite recursion)
					if ( to == guid || (to != null && SubtreeContains( node, to )) ) { skipped++; continue; }

					loc.Parent.RemoveAt( loc.At );
					ReindexAfterRemove( index, loc.Parent, loc.At );

					var target = ResolveParent( to, out var fb );
					if ( fb ) skipped++;
					target.Add( node );
					ReindexArray( index, target );
					applied++;
				}
			}

			// 3. Brand-new objects (whole subtrees)
			if ( patch["added"] is JsonArray added )
			{
				foreach ( var item in added )
				{
					if ( item is not JsonObject a ) continue;
					if ( a["object"] is not JsonObject obj ) continue;
					if ( obj["__guid"]?.GetValue<string>() is not string guid ) continue;

					if ( index.ContainsKey( guid ) || doomed.Contains( guid ) ) { skipped++; continue; }

					var parentGuid = a["parent"]?.GetValue<string>();
					// A prefab has exactly one root: a parentless add is corrupt input, skip it
					// (for scenes, null parent = legit top-level object).
					if ( parentGuid == null && isPrefab ) { skipped++; continue; }

					var target = ResolveParent( parentGuid, out var fb );
					if ( fb ) skipped++;
					var clone = JsonNode.Parse( obj.ToJsonString() );
					target.Add( clone );
					ReindexArray( index, target );
					applied++;
				}
			}

			// Root object guid (prefab RootObject has no parent array — handled separately below)
			var rootGuid = isPrefab
				? (root["RootObject"] as JsonObject)?["__guid"]?.GetValue<string>()
				: null;

			// 4. Changed properties: merge per-field, last-writer-wins per field
			if ( patch["updated"] is JsonArray updated )
			{
				foreach ( var item in updated )
				{
					if ( item is not JsonObject u ) continue;
					if ( u["guid"]?.GetValue<string>() is not string guid ) continue;
					if ( u["props"] is not JsonObject props ) continue;

					if ( guid == rootGuid ) continue; // merged in the root block below
					if ( doomed.Contains( guid ) ) { skipped++; continue; } // update vs delete: delete wins
					if ( !index.TryGetValue( guid, out var loc ) ) { skipped++; continue; }
					if ( loc.Parent[loc.At] is not JsonObject target ) continue;

					MergeProps( target, props );
					applied++;
				}
			}

			if ( rootGuid != null && patch["updated"] is JsonArray upd2 )
			{
				foreach ( var item in upd2 )
				{
					if ( item is not JsonObject u ) continue;
					if ( u["guid"]?.GetValue<string>() != rootGuid ) continue;
					if ( u["props"] is not JsonObject props ) continue;
					if ( root["RootObject"] is JsonObject rootObj )
					{
						MergeProps( rootObj, props );
						applied++;
					}
				}
			}

			if ( patch["top"] is JsonObject top )
			{
				foreach ( var kv in top )
				{
					root[kv.Key] = JsonNode.Parse( kv.Value?.ToJsonString() ?? "null" );
					applied++;
				}
			}

			if ( applied == 0 )
			{
				if ( skipped > 0 ) OnLog?.Invoke( $"[in] patch {fileName}: пропущено {skipped} (конфликт с удалением)" );
				return;
			}

			Suppress( fullPath, TimeSpan.FromSeconds( 1.5 ) );
			try
			{
				var newText = root.ToJsonString( Indented );
				await Task.Run( () => File.WriteAllText( fullPath, newText, Encoding.UTF8 ) ).ConfigureAwait( false );

				var state = await ReadStateAsync( fullPath, CancellationToken.None ).ConfigureAwait( false );
				if ( state != null )
				{
					state.RelPath = ToRelPath( fullPath );
					lock ( _lock ) _scenes[fullPath] = state;
					lock ( _lock ) _liveBaseline[fullPath] = state; // no echo: live watcher sees it as known
				}
			}
			finally
			{
				EndSuppress( fullPath );
			}

			OnLog?.Invoke( $"[in] patch {fileName}: {applied} изменений" + (skipped > 0 ? $", пропущено {skipped}" : "") );
			OnSceneApplied?.Invoke( ToRelPath( fullPath ) );
		}
		catch ( Exception ex )
		{
			OnLog?.Invoke( $"[in] patch error{(fileName != null ? $" {fileName}" : "")}: {ex.Message}" );
		}
	}

	private static void ReindexAfterRemove( Dictionary<string, (JsonArray Parent, int At)> index, JsonArray parent, int removedAt )
	{
		foreach ( var key in index.Keys.ToList() )
		{
			var loc = index[key];
			if ( loc.Parent == parent && loc.At > removedAt )
				index[key] = (loc.Parent, loc.At - 1);
		}
	}

	/// <summary>Remove a detached subtree's guids from the live index and the doom set.</summary>
	private static void PurgeSubtree( Dictionary<string, (JsonArray Parent, int At)> index, HashSet<string> doomed, JsonObject node )
	{
		if ( node["Children"] is not JsonArray children ) return;
		foreach ( var child in children )
		{
			if ( child is not JsonObject co ) continue;
			if ( co["__guid"]?.GetValue<string>() is string g )
			{
				index.Remove( g );
				doomed.Remove( g );
			}
			PurgeSubtree( index, doomed, co );
		}
	}

	/// <summary>True if the subtree rooted at node contains an object with the given guid (excluding node itself).</summary>
	private static bool SubtreeContains( JsonObject node, string guid )
	{
		if ( node["Children"] is not JsonArray children ) return false;
		foreach ( var child in children )
		{
			if ( child is not JsonObject co ) continue;
			if ( co["__guid"]?.GetValue<string>() == guid ) return true;
			if ( SubtreeContains( co, guid ) ) return true;
		}
		return false;
	}

	private static void ReindexArray( Dictionary<string, (JsonArray Parent, int At)> index, JsonArray arr )
	{
		for ( int i = 0; i < arr.Count; i++ )
		{
			if ( arr[i] is JsonObject o && o["__guid"]?.GetValue<string>() is string g )
				index[g] = (arr, i);
		}
	}

	public void Dispose() => Stop();
}
