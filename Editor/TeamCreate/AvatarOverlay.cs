namespace Editor.TeamCreate;

/// <summary>
/// Roblox-style peer avatars in the editor viewport: a translucent sphere in the
/// peer's color, name tag, view-direction line, and boxes around the objects the
/// peer has selected. Drawn via DebugOverlay (x-ray), never touches the scene —
/// no dirty state, nothing leaks into .scene files or sync.
/// </summary>
public sealed class AvatarOverlay : IDisposable
{
	private sealed class RemotePeer
	{
		public string Name = "?";
		public string ColorHex = "#ffffff";
		public Vector3 CamPos;
		public Vector3 CamDir = new( 1, 0, 0 );
		public bool HasCam;
		public List<string> Selection = new();
		public string Scene = "";
		public DateTime SeenUtc = DateTime.UtcNow;
		public Vector3 RenderPos;
		public bool HasRender;
	}

	private readonly Dictionary<string, RemotePeer> _peers = new();
	private readonly object _lock = new();
	private CancellationTokenSource? _cts;

	public void Start()
	{
		Stop();
		_cts = new CancellationTokenSource();
		var token = _cts.Token;
		_ = Task.Run( async () =>
		{
			try
			{
				while ( !token.IsCancellationRequested )
				{
					await Task.Delay( 33, token );
					try { MainThread.Queue( Draw ); }
					catch { }
				}
			}
			catch ( OperationCanceledException ) { }
		}, token );
	}

	public void Stop()
	{
		_cts?.Cancel();
		_cts = null;
	}

	public void Dispose() => Stop();

	public void Update( string id, string name, string colorHex, float[]? camPos, float[]? camDir, List<string>? selection, string? scene )
	{
		lock ( _lock )
		{
			if ( !_peers.TryGetValue( id, out var p ) )
			{
				p = new RemotePeer();
				_peers[id] = p;
			}
			p.Name = name;
			p.ColorHex = colorHex;
			if ( camPos is { Length: 3 } )
			{
				p.CamPos = new Vector3( camPos[0], camPos[1], camPos[2] );
				p.HasCam = true;
			}
			if ( camDir is { Length: 3 } )
			{
				var d = new Vector3( camDir[0], camDir[1], camDir[2] );
				if ( d.Length > 0.001f ) p.CamDir = d.Normal;
			}
			p.Selection = selection ?? new List<string>();
			p.Scene = scene ?? "";
			p.SeenUtc = DateTime.UtcNow;
		}
	}

	public void Remove( string id )
	{
		lock ( _lock ) _peers.Remove( id );
	}

	public void Clear()
	{
		lock ( _lock ) _peers.Clear();
	}

	private void Draw()
	{
		try
		{
			var scene = SceneEditorSession.Active?.Scene;
			if ( scene == null || !scene.IsValid ) return;

			// Anchor: any object of the active scene lends its world-space debug overlay
			DebugOverlaySystem? overlay = null;
			foreach ( var o in Sandbox.Scene.All )
			{
				if ( o is not GameObject go || !go.IsValid ) continue;
				try
				{
					if ( go.Scene != scene ) continue;
					overlay = go.DebugOverlay;
					break;
				}
				catch { }
			}
			if ( overlay == null ) return; // empty scene — nothing to anchor to

			GameObjectDirectory? directory = null;
			try { directory = scene.Directory; }
			catch { }

			var now = DateTime.UtcNow;
			List<RemotePeer> snapshot;
			lock ( _lock ) snapshot = _peers.Values.ToList();

			foreach ( var p in snapshot )
			{
				if ( (now - p.SeenUtc).TotalSeconds > 35 ) continue;

				var color = Color.Parse( p.ColorHex ) ?? Color.Gray;

				if ( p.HasCam )
				{
					// Smooth follow; snap on teleports
					if ( !p.HasRender || (p.RenderPos - p.CamPos).Length > 400 )
					{
						p.RenderPos = p.CamPos;
						p.HasRender = true;
					}
					else
					{
						p.RenderPos = p.RenderPos + (p.CamPos - p.RenderPos) * 0.4f;
					}

					var body = new Color( color.r, color.g, color.b, 0.45f );
					overlay.Sphere( new Sphere( p.RenderPos, 20 ), body, 0.15f, Transform.Zero, true );
					overlay.Line( p.RenderPos, p.RenderPos + p.CamDir * 150, color, 0.15f, Transform.Zero, true );
					overlay.Text( p.RenderPos + new Vector3( 0, 0, 42 ), p.Name, 48, TextFlag.LeftTop, color, 0.15f );
				}

				if ( directory != null )
				{
					foreach ( var g in p.Selection )
					{
						try
						{
							if ( !Guid.TryParse( g, out var guid ) ) continue;
							var go = directory.FindByGuid( guid );
							if ( go == null || !go.IsValid ) continue;
							overlay.Box( go.GetBounds(), color, 0.25f, Transform.Zero, true );
						}
						catch { }
					}
				}
			}
		}
		catch { }
	}
}
