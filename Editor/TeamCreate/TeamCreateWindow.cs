namespace Editor.TeamCreate;

[Dock( "Editor", "Team Create", "group_work" )]
public sealed class TeamCreateWindow : Widget, Sandbox.ResourceLibrary.IEventListener
{
	private readonly TeamCreateClient _client = new();
	private readonly FileSyncService _fileSync = new();
	private readonly SceneDeltaService _sceneDelta = new();
	private readonly AvatarOverlay _avatars = new();

	// UI elements
	private Label _statusLabel;
	private LineEdit _addressEdit;
	private LineEdit _relayEdit;
	private LineEdit _nameEdit;
	private LineEdit _roomEdit;
	private LineEdit _passwordEdit;
	private LineEdit _remoteEdit;
	private Button _connectBtn;
	private Layout _peerListLayout;
	private TextEdit _logText;
	private Widget _colorPreview;
	private ComboBox _langCombo;
	private ComboBox _historyCombo;
	private readonly List<string> _history = new();

	// State
	private Color _myColor = Color.Red;
	private readonly Dictionary<string, PeerInfo> _peers = new();
	private string _lang = "en";
	private Process? _hubProcess;
	private bool _suppressEvents;

	// Protocol v2: presence + scene locks
	private sealed class PeerPresence
	{
		public string Scene = "";
		public List<string> Selection = new();
		public float[]? CamPos;
		public float[]? CamDir;
		public DateTime SeenUtc = DateTime.UtcNow;
	}
	private readonly Dictionary<string, PeerPresence> _peerPresence = new();
	private readonly Dictionary<string, SceneLockInfo> _locks = new( StringComparer.OrdinalIgnoreCase );
	private string _peerListSig = "";
	private CancellationTokenSource? _presenceCts;
	private CancellationTokenSource? _lockRefreshCts;
	private CancellationTokenSource? _reconnectCts;
	private bool _needsResync;
	private string? _myLockPath;
	private Button? _lockBtn;
	private Label? _lockBanner;

	private static string NormLockPath( string path ) =>
		path.Replace( '\\', '/' ).Trim( '/' ).ToLowerInvariant();

	private static string ProjectRoot => Project.Current.GetRootPath();

	public TeamCreateWindow( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 4;

		_client.OnLog += msg => MainThread.Queue( () => AppendLog( msg ) );
		_client.OnWelcome += msg => MainThread.Queue( () => OnWelcome( msg ) );
		_client.OnPeerJoined += peer => MainThread.Queue( () => OnPeerJoined( peer ) );
		_client.OnPeerLeft += peer => MainThread.Queue( () => OnPeerLeft( peer ) );
		_client.OnMessage += msg => MainThread.Queue( () => OnFileMessage( msg ) );
		_client.OnAuthRejected += reason => MainThread.Queue( () => OnAuthRejected( reason ) );
		_client.OnDropped += () => MainThread.Queue( OnConnectionDropped );

		_fileSync.OnLog += msg => MainThread.Queue( () => AppendLog( msg ) );
		_sceneDelta.OnLog += msg => MainThread.Queue( () => AppendLog( msg ) );
		_sceneDelta.OnSceneApplied += rel => MainThread.Queue( () => RefreshSceneSession( rel ) );
		GitService.OnLog += msg => MainThread.Queue( () => AppendLog( msg ) );

		RebuildUI();
		StartLogFile();
		AppendLog( $"Team Create init (log: {_logFilePath ?? "off"})" );
	}

	private readonly List<string> _logLines = new();

	private void RebuildUI()
	{
		var wasConnected = _client.IsConnected;

		Layout.Clear( true );

		BuildSettingsSection();
		BuildConnectionUI();
		BuildPeerList();
		BuildActions();
		BuildLog();

		LoadSettings();

		foreach ( var line in _logLines )
			_logText?.AppendPlainText( line );
		UpdateLockUI();

		if ( wasConnected )
		{
			_connectBtn.Text = T("disconnect");
			_connectBtn.Tint = Color.Green;
			_statusLabel.Text = T("connected");
			_statusLabel.Color = Color.Green;
			_addressEdit.Enabled = false;
			_nameEdit.Enabled = false;
			_roomEdit.Enabled = false;
			RefreshPeerList();
		}
	}

	// ──── Settings ────

	private void BuildSettingsSection()
	{
		var header = Layout.AddRow();
		header.Add( new Label( $"<b>{T("settings")}</b>", this ), 1 );

		// Language
		var rowLang = Layout.AddRow();
		rowLang.Add( new Label( $"{T("language")}:" , this ) { FixedWidth = 80 } );
		_langCombo = rowLang.Add( new ComboBox( this ) );
		_langCombo.AddItem( "Русский", null, null, null, _lang == "ru" );
		_langCombo.AddItem( "English", null, null, null, _lang == "en" );
		_langCombo.ItemChanged += () =>
		{
			if ( _suppressEvents ) return;
			_lang = _langCombo.CurrentIndex == 0 ? "ru" : "en";
			SaveSettings();
			RebuildUI();
		};

		// Remote (GitHub)
		var rowRemote = Layout.AddRow();
		rowRemote.Add( new Label( "Remote:", this ) { FixedWidth = 80 } );
		_remoteEdit = rowRemote.Add( new LineEdit( this ) { PlaceholderText = "https://github.com/user/repo.git" } );

		// Color
		var rowColor = Layout.AddRow();
		rowColor.Add( new Label( $"{T("my_color")}:", this ) { FixedWidth = 80 } );
		_colorPreview = new Widget( this ) { FixedWidth = 20, FixedHeight = 20 };
		_colorPreview.OnPaintOverride = () =>
		{
			Paint.ClearPen();
			Paint.SetBrush( _myColor );
			Paint.DrawRect( _colorPreview.LocalRect, 4 );
			return true;
		};
		_colorPreview.MouseClick = () => OpenColorPicker();
		rowColor.Add( _colorPreview );

		var spacer = new Widget( this ) { FixedHeight = 8 };
		Layout.Add( spacer );

		// Hub
		var hubHeader = Layout.AddRow();
		hubHeader.Add( new Label( $"<b>{T("hub_settings")}</b>", this ), 1 );

		// Hub buttons: vertical column
		var hubSettingsBtn = Layout.Add( new Button( T("hub_settings"), this ) );
		hubSettingsBtn.Clicked = () =>
		{
			var win = new HubSettingsWindow( this, _lang );
			win.Show();
		};

		var hubStartBtn = Layout.Add( new Button( T("start_hub"), this ) );
		hubStartBtn.Clicked = () =>
		{
			if ( _hubProcess != null && !_hubProcess.HasExited )
			{
				AppendLog( T("hub_already_running") );
				return;
			}
			StartHub();
		};

		var hubStopBtn = Layout.Add( new Button( T("stop_hub"), this ) );
		hubStopBtn.Clicked = () =>
		{
			StopHub();
		};

		AddSpacer();
	}

	// ──── Color Picker ────

	private void OpenColorPicker()
	{
		var savedR = (int)( _myColor.r * 255 );
		var savedG = (int)( _myColor.g * 255 );
		var savedB = (int)( _myColor.b * 255 );

		var popup = new Dialog( this );
		popup.Window.WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.WindowTitle | WindowFlags.CloseButton;
		popup.Window.WindowTitle = T("pick_color");
		popup.Window.Size = new( 320, 160 );
		popup.Layout = Layout.Column();
		popup.Layout.Margin = 8;
		popup.Layout.Spacing = 4;

		// RGB input
		var rgbRow = popup.Layout.AddRow();
		rgbRow.Add( new Label( "RGB:", this ) { FixedWidth = 30 } );
		var rgbEdit = rgbRow.Add( new LineEdit( this ) { PlaceholderText = "161, 224, 51" } );
		rgbEdit.Text = $"{savedR}, {savedG}, {savedB}";

		// Preview + buttons
		var bottomRow = popup.Layout.AddRow();

		var preview = new Widget( this ) { FixedWidth = 24, FixedHeight = 24 };
		preview.OnPaintOverride = () =>
		{
			Paint.ClearPen();
			Paint.SetBrush( _myColor );
			Paint.DrawRect( preview.LocalRect, 4 );
			return true;
		};
		bottomRow.Add( preview );

		var randomBtn = bottomRow.Add( new Button( T("random"), this ) );
		randomBtn.Clicked = () =>
		{
			_myColor = RandomColor();
			rgbEdit.Text = $"{(int)( _myColor.r * 255 )}, {(int)( _myColor.g * 255 )}, {(int)( _myColor.b * 255 )}";
			preview.Update();
			_colorPreview.Update();
		};

		bottomRow.Add( new Widget( this ), 1 );

		var resetBtn = bottomRow.Add( new Button( T("reset"), this ) );
		resetBtn.Clicked = () =>
		{
			_myColor = new Color( savedR / 255f, savedG / 255f, savedB / 255f );
			rgbEdit.Text = $"{savedR}, {savedG}, {savedB}";
			preview.Update();
			_colorPreview.Update();
		};

		var saveBtn = bottomRow.Add( new Button( T("save"), this ) );
		saveBtn.Clicked = () =>
		{
			SaveSettings();
			popup.Close();
		};

		// Parse RGB on text edit
		rgbEdit.TextEdited += ( text ) =>
		{
			var cleaned = text.Replace( "rgb(", "" ).Replace( ")", "" ).Trim();
			var parts = cleaned.Split( ',', ' ' , StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length == 3
				&& int.TryParse( parts[0].Trim(), out var r )
				&& int.TryParse( parts[1].Trim(), out var g )
				&& int.TryParse( parts[2].Trim(), out var b ) )
			{
				_myColor = new Color( r / 255f, g / 255f, b / 255f );
				preview.Update();
				_colorPreview.Update();
			}
		};

		popup.Show();
	}

	// ──── Commit Panel ────

	private void OpenCommitPanel()
	{
		var popup = new Dialog( this );
		popup.Window.WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.WindowTitle | WindowFlags.CloseButton;
		popup.Window.WindowTitle = T("commit_push");
		popup.Window.Size = new( 350, 180 );
		popup.Layout = Layout.Column();
		popup.Layout.Margin = 8;
		popup.Layout.Spacing = 4;

		// Title
		var titleRow = popup.Layout.AddRow();
		titleRow.Add( new Label( $"{T("commit_name")}: ", this ) { FixedWidth = 80 } );
		var titleEdit = titleRow.Add( new LineEdit( this ) { PlaceholderText = T("commit_name_hint") } );

		// Description
		var descRow = popup.Layout.AddRow();
		descRow.Add( new Label( $"{T("commit_desc")}: ", this ) { FixedWidth = 80 } );
		var descEdit = descRow.Add( new LineEdit( this ) { PlaceholderText = T("commit_desc_hint") } );

		// Buttons
		var btnRow = popup.Layout.AddRow();

		var cancelBtn = btnRow.Add( new Button( T("cancel"), this ) );
		cancelBtn.Clicked = () => popup.Close();

		btnRow.Add( new Widget( this ), 1 );

		var commitBtn = btnRow.Add( new Button( T("commit_push"), this ) );
		commitBtn.Tint = Color.Green;
		commitBtn.Clicked = async () =>
		{
			if ( string.IsNullOrEmpty( ProjectRoot ) ) { popup.Close(); return; }

			var name = titleEdit.Text.Trim();
			var desc = descEdit.Text.Trim();

			string message;
			if ( !string.IsNullOrEmpty( name ) && !string.IsNullOrEmpty( desc ) )
				message = $"{name}\n\n{desc}";
			else if ( !string.IsNullOrEmpty( name ) )
				message = name;
			else
				message = $"Team Create save - {DateTime.Now:HH:mm:ss}";

			popup.Close();

			await GitService.CommitAndPushAsync( ProjectRoot, message );
		};

		popup.Show();
	}

	// ──── Connection UI ────

	private void BuildConnectionUI()
	{
		var titleRow = Layout.AddRow();
		titleRow.Add( new Label( $"<b>{T("connection")}</b>", this ), 1 );

		var row1 = Layout.AddRow();
		row1.Add( new Label( $"{T("address")}:", this ) { FixedWidth = 80 } );
		_addressEdit = row1.Add( new LineEdit( this ) { PlaceholderText = "127.0.0.1:4877" } );

		var rowRelay = Layout.AddRow();
		rowRelay.Add( new Label( $"{T("relay")}:", this ) { FixedWidth = 80 } );
		_relayEdit = rowRelay.Add( new LineEdit( this ) { PlaceholderText = "relay.example.com:443" } );

		var rowHist = Layout.AddRow();
		rowHist.Add( new Label( "", this ) { FixedWidth = 80 } );
		_historyCombo = rowHist.Add( new ComboBox( this ) );
		_historyCombo.ItemChanged += () =>
		{
			if ( _suppressEvents ) return;
			var i = _historyCombo.CurrentIndex;
			if ( i >= 0 && i < _history.Count )
			{
				_addressEdit.Text = _history[i];
				SaveSettings();
			}
		};

		var row2 = Layout.AddRow();
		row2.Add( new Label( $"{T("nickname")}:", this ) { FixedWidth = 80 } );
		_nameEdit = row2.Add( new LineEdit( this ) { PlaceholderText = T("nickname_hint") } );

		var row3 = Layout.AddRow();
		row3.Add( new Label( $"{T("room")}:", this ) { FixedWidth = 80 } );
		_roomEdit = row3.Add( new LineEdit( this ) { PlaceholderText = "default" } );

		var row4 = Layout.AddRow();
		row4.Add( new Label( $"{T("password")}:", this ) { FixedWidth = 80 } );
		_passwordEdit = row4.Add( new LineEdit( this ) { PlaceholderText = T("password_hint") } );

		var row5 = Layout.AddRow();
		_connectBtn = row5.Add( new Button( T("connect"), this ) );
		_connectBtn.Clicked = OnConnectClicked;

		var statusRow = Layout.AddRow();
		_statusLabel = statusRow.Add( new Label( T("disconnected"), this ), 1 );
		_statusLabel.Color = Theme.Text;

		AddSpacer();
	}

	// ──── Peers ────

	private void BuildPeerList()
	{
		var header = Layout.AddRow();
		header.Add( new Label( $"<b>{T("peers")}</b>", this ), 1 );
		_peerListLayout = Layout.AddRow( 1 );

		var lockRow = Layout.AddRow();
		_lockBtn = lockRow.Add( new Button( T( "lock_scene" ), this ) );
		_lockBtn.Clicked = OnLockButtonClicked;

		_peerListSig = ""; // layout was rebuilt

		_lockBanner = Layout.Add( new Label( "", this ) );
		_lockBanner.Color = Color.Yellow;
		_lockBanner.Visible = false;

		AddSpacer();
	}

	// ──── Actions ────

	private void BuildActions()
	{
		var remoteHeader = Layout.AddRow();
		remoteHeader.Add( new Label( $"<b>{T("git_remote")}</b>", this ), 1 );

		var remoteRow = Layout.AddRow();

		var initRemoteBtn = remoteRow.Add( new Button( T("init"), this ) );
		initRemoteBtn.Clicked = OnInitRemoteClicked;
		initRemoteBtn.ToolTip = T("init_remote_hint");

		var commitRemoteBtn = remoteRow.Add( new Button( T("commit_push"), this ) );
		commitRemoteBtn.Clicked = () => OpenCommitPanel();

		var pullRemoteBtn = remoteRow.Add( new Button( T("pull"), this ) );
		pullRemoteBtn.Clicked = OnPullRemoteClicked;

		AddSpacer();
	}

	// ──── Log file ────

	private static string? _logFilePath;
	private static readonly object _logFileLock = new();

	private static void StartLogFile()
	{
		if ( _logFilePath != null ) return;
		try
		{
			var dir = Path.Combine( ProjectRoot, "Libraries", "teamcreate", "logs" );
			Directory.CreateDirectory( dir );

			// Roll: keep 10 newest
			foreach ( var f in Directory.GetFiles( dir, "tc-*.log" ).OrderByDescending( f => f ).Skip( 9 ) )
			{
				try { File.Delete( f ); } catch { }
			}

			_logFilePath = Path.Combine( dir, $"tc-{DateTime.Now:yyyyMMdd-HHmmss}.log" );
		}
		catch { }
	}

	private static void WriteToLogFile( string line )
	{
		var path = _logFilePath;
		if ( path == null ) return;
		lock ( _logFileLock )
		{
			try { File.AppendAllText( path, line + Environment.NewLine ); }
			catch { }
		}
	}

	// ──── Log ────

	private void BuildLog()
	{
		var logHeader = Layout.AddRow();
		logHeader.Add( new Label( $"<b>{T("log")}</b>", this ), 1 );

		_logText = Layout.Add( new TextEdit( this ) { ReadOnly = true }, 1 );
		_logText.MinimumHeight = 100;
	}

	// ──── Actions ────

	private async void OnConnectClicked()
	{
		if ( _client.IsConnected || _reconnectCts != null )
		{
			StopReconnectLoop();
			await _client.DisconnectAsync();
			_fileSync.Stop();
			_sceneDelta.Stop();
			StopPresenceLoop();
			// live watch disabled: scene sync is file-event driven
			_avatars.Clear();
			_avatars.Stop();
			_peers.Clear();
			RefreshPeerList();
			UpdateLockUI();
			SetConnected( false );
			return;
		}

		var address = _addressEdit.Text.Trim();
		var relay = _relayEdit.Text.Trim();
		var name = _nameEdit.Text.Trim();
		var room = _roomEdit.Text.Trim();
		var password = _passwordEdit.Text.Trim();

		if ( string.IsNullOrEmpty( address ) ) { AppendLog( T("enter_address") ); return; }
		if ( string.IsNullOrEmpty( name ) ) name = "Player";

		SaveSettings();

		try
		{
			if ( !string.IsNullOrEmpty( ProjectRoot ) && Directory.Exists( ProjectRoot ) )
			{
				AppendLog( $"{T( "connect" )} {address}..." );
				// Initial hash scan runs on a background thread (can take seconds on big projects)
				await _fileSync.StartAsync( ProjectRoot, SendFile, SendFileDelete, SendManifest, SendChunk );

				var scenesDir = Path.Combine( ProjectRoot, "Assets", "scenes" );
				if ( Directory.Exists( scenesDir ) )
					_sceneDelta.Start( ProjectRoot, scenesDir, SendFile, SendFileDelete );
			}

			// Hash password if provided
			string? passwordHash = null;
			if ( !string.IsNullOrEmpty( password ) )
			{
				passwordHash = HashPassword( password );
				AppendLog( $"Password hash: ***" );
			}
			else
			{
				AppendLog( "No password provided" );
			}

			await _client.ConnectAsync( address, name, ColorToHex( _myColor ), room, passwordHash,
				string.IsNullOrEmpty( relay ) ? null : relay );
		}
		catch ( Exception ex )
		{
			AppendLog( $"Connect error: {ex.Message}" );
			AppendLog( "Проверь: адрес/порт хаба, firewall, VPN (split-tunnel / доступ к LAN)" );
			try { _fileSync.Stop(); } catch { }
			try { _sceneDelta.Stop(); } catch { }
			SetConnected( false );
		}
	}

	private async void OnInitRemoteClicked()
	{
		if ( string.IsNullOrEmpty( ProjectRoot ) ) return;

		var remote = _remoteEdit.Text.Trim();
		if ( string.IsNullOrEmpty( remote ) )
		{
			AppendLog( T("enter_remote_url") );
			return;
		}

		var gitDir = Path.Combine( ProjectRoot, ".git" );
		if ( Directory.Exists( gitDir ) )
		{
			var log = await GitService.RunAsync( ProjectRoot, "log --oneline -1" );
			if ( !string.IsNullOrWhiteSpace( log ) && !log.Contains( "git not found" ) )
			{
				AppendLog( T("already_initialized") );
				return;
			}
		}

		await GitService.InitRemoteAsync( ProjectRoot, remote );
	}

	private void OnPullRemoteClicked()
	{
		if ( string.IsNullOrEmpty( ProjectRoot ) ) return;
		_ = GitService.PullRemoteAsync( ProjectRoot );
	}

	// ──── Network ────

	private void SendFile( string relPath, byte[] data )
	{
		_client.Send( new Message
		{
			Type = "file",
			Path = relPath,
			ContentB64 = Convert.ToBase64String( data ),
		} );
	}

	private void SendFileDelete( string relPath )
	{
		_client.Send( new Message { Type = "file-delete", Path = relPath } );
	}

	private void SendManifest( string relPath, long size, string hash, int total )
	{
		_client.Send( new Message
		{
			Type = "file-manifest",
			Path = relPath,
			FileSize = size,
			FileHash = hash,
			ChunkTotal = total,
		} );
	}

	private void SendChunk( string relPath, int index, int total, byte[] chunk, bool final )
	{
		_client.Send( new Message
		{
			Type = "file-chunk",
			Path = relPath,
			ChunkIndex = index,
			ChunkTotal = total,
			ContentB64 = Convert.ToBase64String( chunk ),
			Final = final,
		} );
	}

	/// <summary>Remember successful addresses (oldest first, mirrors the history combo).</summary>
	private void PushHistory( string address )
	{
		if ( string.IsNullOrWhiteSpace( address ) || _history.Contains( address ) ) return;
		_history.Add( address );
		try { _historyCombo.AddItem( address, null, null, null, false ); }
		catch { }
		SaveSettings();
	}

	private void OnWelcome( Message msg )
	{
		var peers = msg.Peers ?? new List<PeerInfo>();
		_peers.Clear();
		foreach ( var p in peers ) _peers[p.Id] = p;

		// Add self to peers
		if ( _client.MyId != null )
		{
			_peers[_client.MyId] = new PeerInfo
			{
				Id = _client.MyId,
				Name = _nameEdit.Text,
				Color = ColorToHex( _myColor ),
			};
		}

		// Check color uniqueness
		var usedColors = peers.Select( p => p.Color ).ToList();
		if ( usedColors.Contains( ColorToHex( _myColor ) ) )
		{
			_myColor = RandomColor();
			_colorPreview.Update();
			AppendLog( T("color_taken") );
		}

		RefreshPeerList();
		SetConnected( true );
		AppendLog( "Подключено!" );
		AppendLog( $"{T("peers_online")}: {peers.Count + 1}" );

		PushHistory( _addressEdit.Text.Trim() );

		StopReconnectLoop();

		if ( _needsResync )
		{
			_needsResync = false;
			// Offline edits were hashed but never sent — full resync what peers missed
			_fileSync.Resync();
			_sceneDelta.Resync();
			AppendLog( T( "resynced" ) );
		}

		if ( _myLockPath != null && _client.IsConnected )
			_client.Send( new Message { Type = "lock", Path = _myLockPath } );

		_locks.Clear();
		if ( msg.Locks is { Count: > 0 } )
		{
			foreach ( var l in msg.Locks )
				_locks[NormLockPath( l.Path )] = l;
			AppendLog( $"[lock] active: {_locks.Count}" );
		}
		UpdateLockUI();
		StartPresenceLoop();
		_avatars.Start();
		StartLiveWatch();

		// Snapshots sent in Start() died on a not-yet-connected socket —
		// resync NOW that we're actually in the room so peers/hub-cache get our state.
		_fileSync.Resync();
		_sceneDelta.Resync();
		AppendLog( "Отправляю состояние проекта пирам..." );
	}

	private void OnPeerJoined( PeerInfo peer )
	{
		_peers[peer.Id] = peer;
		RefreshPeerList();
		AppendLog( $"{peer.Name} {T("joined")}" );
	}

	/// <summary>Teleport my editor camera to a peer (Roblox-style "follow").</summary>
	private void FollowPeer( string peerId )
	{
		try
		{
			float[]? camPos = null;
			string peerName = "?";
			lock ( _peerPresence )
			{
				if ( _peerPresence.TryGetValue( peerId, out var pres ) )
					camPos = pres.CamPos;
			}
			if ( _peers.TryGetValue( peerId, out var peer ) )
				peerName = peer.Name;

			if ( camPos is not { Length: 3 } )
			{
				AppendLog( $"{peerName}: {T( "no_cam_data" )}" );
				return;
			}

			var session = SceneEditorSession.Active;
			if ( session == null )
			{
				AppendLog( T( "no_active_scene" ) );
				return;
			}

			var pos = new Vector3( camPos[0], camPos[1], camPos[2] );
			var ext = new Vector3( 60, 60, 60 );
			session.FrameTo( new BBox( pos - ext, pos + ext ) );
			AppendLog( $"{T( "follow" )}: {peerName}" );
		}
		catch ( Exception ex )
		{
			AppendLog( $"Follow failed: {ex.Message}" );
		}
	}

	private void OnPeerLeft( PeerInfo peer )
	{
		_peers.Remove( peer.Id );
		lock ( _peerPresence ) _peerPresence.Remove( peer.Id );
		_avatars.Remove( peer.Id );
		foreach ( var key in _locks.Keys.ToList() )
		{
			if ( _locks[key].Owner == peer.Id ) _locks.Remove( key );
		}
		RefreshPeerList();
		UpdateLockUI();
		AppendLog( $"{peer.Name} {T("left")}" );
	}

	private void OnFileMessage( Message msg )
	{
		switch ( msg.Type )
		{
			case "file" when msg.Path != null && msg.ContentB64 != null:
				var data = Convert.FromBase64String( msg.ContentB64 );

				if ( msg.Path.StartsWith( "__patches__/", StringComparison.OrdinalIgnoreCase ) && msg.Path.EndsWith( ".patch", StringComparison.OrdinalIgnoreCase ) )
				{
					// Patch goes ONLY into the open scene session (memory) — NOT the file.
					// The recipient's editor never sees a write, so no reload prompt, no rollback.
					// If the scene isn't open on this side, we do nothing (peer gets it when they open).
					_ = TryApplyPatchToLiveScene( Encoding.UTF8.GetString( data ) );
				}
				else if ( msg.Path.EndsWith( ".scene", StringComparison.OrdinalIgnoreCase ) ||
						  msg.Path.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) )
				{
					// Full scene/prefab snapshot (late-join sync) — owned by SceneDeltaService
					_sceneDelta.ApplyRemoteFull( msg.Path, data );
				}
				else
				{
					_fileSync.ApplyRemoteFile( msg.Path, data );
					AppendLog( $"[in] {T("file")}: {msg.Path} ({data.Length} {T("bytes")})" );
				}
				break;
			case "file-delete" when msg.Path != null:
				if ( msg.Path.EndsWith( ".scene", StringComparison.OrdinalIgnoreCase ) ||
					 msg.Path.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) )
				{
					// Scene/prefab deletes come from SceneDeltaService watcher; FileSync ignores them
					var fullPath = Path.Combine( ProjectRoot, msg.Path );
					try { if ( File.Exists( fullPath ) ) File.Delete( fullPath ); } catch { }
					AppendLog( $"[in] {T("deleted")}: {msg.Path}" );
				}
				else
				{
					_fileSync.ApplyRemoteDelete( msg.Path );
					AppendLog( $"[in] {T("deleted")}: {msg.Path}" );
				}
				break;
			case "file-manifest" when msg.Path != null && msg.ChunkTotal > 0 && msg.FileHash != null:
				_fileSync.ApplyManifest( msg.Path, msg.ChunkTotal.Value, msg.FileHash );
				break;
			case "file-chunk" when msg.Path != null && msg.ChunkIndex >= 0 && msg.ContentB64 != null:
				_fileSync.ApplyChunk( msg.Path, msg.ChunkIndex.Value, Convert.FromBase64String( msg.ContentB64 ) );
				break;
			case "presence":
				if ( msg.From != null ) OnPresence( msg );
				break;
			case "lock" when msg.Path != null:
				OnLockMessage( msg );
				break;
			case "unlock" when msg.Path != null:
				_locks.Remove( NormLockPath( msg.Path ) );
				UpdateLockUI();
				RefreshPeerList();
				break;
			case "lock-denied" when msg.Path != null:
				if ( _myLockPath != null && NormLockPath( _myLockPath ) == NormLockPath( msg.Path ) )
				{
					_myLockPath = null;
					_lockRefreshCts?.Cancel();
				}
				AppendLog( $"[lock] denied: {msg.Path} (held by {msg.Reason ?? "?"})" );
				UpdateLockUI();
				break;
			case "sync-end":
				AppendLog( T( "sync_complete" ) );
				break;
		}
	}

	private void OnAuthRejected( string reason )
	{
		AppendLog( $"{T("auth_failed")}: {reason}" );
		SetConnected( false );
		_fileSync.Stop();
		_sceneDelta.Stop();
		StopPresenceLoop();
		// live watch disabled: scene sync is file-event driven
		_avatars.Clear();
		_avatars.Stop();
		_peers.Clear();
		RefreshPeerList();
		UpdateLockUI();
	}

	// ──── Engine hooks (Phase 2: events instead of pure polling) ────

	// Fired by the editor when the user saves a scene (Ctrl+S). Diff immediately
	// instead of waiting for the next poll tick. Polling stays as a fallback.
	[Event( "scene.saved" )]
	private void OnSceneSavedHook( Scene scene )
	{
		if ( !_client.IsConnected ) return;
		AppendLog( "[watch] scene.saved → проверяю дифф" );
		_sceneDelta.ForceCheck();
	}

	void Sandbox.ResourceLibrary.IEventListener.OnRegister( GameResource resource ) { }
	void Sandbox.ResourceLibrary.IEventListener.OnUnregister( GameResource resource ) { }
	void Sandbox.ResourceLibrary.IEventListener.OnExternalChanges( GameResource resource ) { }
	void Sandbox.ResourceLibrary.IEventListener.OnExternalChangesPostLoad( GameResource resource ) { }

	// Fired for every resource save (belt & suspenders alongside scene.saved).
	void Sandbox.ResourceLibrary.IEventListener.OnSave( GameResource resource )
	{
		if ( !_client.IsConnected ) return;
		if ( resource is SceneFile )
			_sceneDelta.ForceCheck();
	}

	/// <summary>
	/// A remote patch/snapshot was written to disk. Push it into the open
	/// editor session so the viewport actually updates. Never clobbers
	/// unsaved local work — dirty sessions are left alone with a warning.
	/// For prefabs, live instances in open scenes are refreshed too.
	/// </summary>
	private void RefreshSceneSession( string relPath )
	{
		try
		{
			var resourcePath = relPath;
			if ( resourcePath.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) )
				resourcePath = resourcePath.Substring( "Assets/".Length );

			if ( relPath.EndsWith( ".prefab", StringComparison.OrdinalIgnoreCase ) )
			{
				RefreshPrefab( resourcePath, relPath );
				return;
			}

			SceneFile? sceneFile = null;
			try { sceneFile = SceneFile.Load( resourcePath ); }
			catch { }
			if ( sceneFile == null )
			{
				try { sceneFile = SceneFile.Load( relPath ); }
				catch { }
			}
			if ( sceneFile == null )
			{
				AppendLog( $"[in] {relPath}: SceneFile не найден (диск обновлён, открой сцену сам)" );
				return;
			}

			SceneEditorSession? session = null;
			try { session = SceneEditorSession.Resolve( sceneFile ); }
			catch { }
			if ( session == null )
			{
				AppendLog( $"[in] {relPath}: сцена не открыта в редакторе — диск обновлён" );
				return;
			}
			if ( session.IsMounted || session.IsPrefabSession ) return;

			if ( session.HasUnsavedChanges )
			{
				AppendLog( $"[in] {relPath}: есть несохранённые изменения — перезагрузи сцену вручную (Ctrl+R)" );
				return;
			}

			session.Reload();

			// Re-baseline from the RELOADED live scene, not the disk bytes —
			// live serialize formats differently than the file, so without this
			// the next live tick sees phantom diffs (the A↔B ping-pong).
			try
			{
				var liveJson = session.Scene?.Serialize( new GameObject.SerializeOptions() )?.ToJsonString();
				if ( liveJson != null )
				{
					var fullPath = Path.Combine( ProjectRoot, relPath );
					_sceneDelta.RebaselineLive( fullPath, liveJson );
				}
			}
			catch { }

			AppendLog( $"[in] сцена обновлена: {relPath}" );
		}
		catch ( Exception ex )
		{
			AppendLog( $"[in] reload failed: {ex.Message}" );
		}
	}

	private void RefreshPrefab( string resourcePath, string relPath )
	{
		try
		{
			PrefabFile? prefab = null;
			try { prefab = PrefabFile.Load( resourcePath ); }
			catch { }
			if ( prefab == null )
			{
				try { prefab = PrefabFile.Load( relPath ); }
				catch { }
			}
			if ( prefab == null ) return;

			// Push new prefab state into live instances in open scenes
			try { EditorScene.UpdatePrefabInstances( prefab ); }
			catch ( Exception ex ) { AppendLog( $"[in] prefab instances: {ex.Message}" ); }

			SceneEditorSession? session = null;
			try { session = SceneEditorSession.Resolve( prefab ); }
			catch { }
			if ( session == null ) return; // prefab not open — disk + instances are enough
			if ( session.IsMounted ) return;

			if ( session.HasUnsavedChanges )
			{
				AppendLog( $"[in] {relPath}: есть несохранённые изменения — перезагрузи префаб вручную" );
				return;
			}

			session.Reload();
			AppendLog( $"[in] префаб обновлён: {relPath}" );
		}
		catch ( Exception ex )
		{
			AppendLog( $"[in] prefab reload failed: {ex.Message}" );
		}
	}

	// ──── Protocol v2: presence + scene locks ────

	private void StartPresenceLoop()
	{
		StopPresenceLoop();
		_presenceCts = new CancellationTokenSource();
		var token = _presenceCts.Token;
		_ = Task.Run( async () =>
		{
			string lastPayload = "";
			var lastHeartbeat = DateTime.UtcNow;
			try
			{
				while ( !token.IsCancellationRequested )
				{
					await Task.Delay( 100, token );
					if ( token.IsCancellationRequested || !_client.IsConnected ) continue;
					try
					{
						// Engine APIs must run on the MainThread — round-trip with a timeout
						var state = await ReadPresenceStateAsync();
						if ( state == null ) continue;
						var (scene, sel, camPos, camDir) = state.Value;

						var payload = (scene ?? "") + "|" + string.Join( ",", sel ) + "|" +
							(camPos != null ? $"{camPos[0]:F1},{camPos[1]:F1},{camPos[2]:F1}" : "-") + "|" +
							(camDir != null ? $"{camDir[0]:F2},{camDir[1]:F2},{camDir[2]:F2}" : "-");
						var heartbeatDue = (DateTime.UtcNow - lastHeartbeat).TotalSeconds >= 5;
						if ( payload != lastPayload || heartbeatDue )
						{
							lastPayload = payload;
							if ( heartbeatDue ) lastHeartbeat = DateTime.UtcNow;
							_client.Send( new Message
							{
								Type = "presence",
								Scene = scene != null ? Path.GetFileName( scene ) : null,
								Selection = sel,
								CamPos = camPos,
								CamRot = camDir, // repurposed: view direction vector, not a quaternion
							} );
						}
						SweepPresence();
					}
					catch { }
				}
			}
			catch ( OperationCanceledException ) { }
		}, token );
	}

	/// <summary>Reads editor state on the MainThread. Null = UI busy, skip this tick.</summary>
	private async Task<(string? Scene, List<string> Selection, float[]? CamPos, float[]? CamDir)?> ReadPresenceStateAsync()
	{
		var tcs = new TaskCompletionSource<(string?, List<string>, float[]?, float[]?)>( TaskCreationOptions.RunContinuationsAsynchronously );
		try
		{
			MainThread.Queue( () =>
			{
				try { tcs.TrySetResult( (GetActiveSceneRelCached(), GetSelectionGuids(), GetMyCamPos(), GetMyCamDir()) ); }
				catch ( Exception ex ) { tcs.TrySetException( ex ); }
			} );
		}
		catch { return null; }

		var winner = await Task.WhenAny( tcs.Task, Task.Delay( 2000 ) );
		if ( winner != tcs.Task ) return null;
		try { return await tcs.Task; }
		catch { return null; }
	}

	private static float[]? GetMyCamPos()
	{
		try
		{
			var t = SceneEditorSession.Active?.Scene?.Camera?.GameObject.Transform;
			if ( t == null ) return null;
			var p = t.Position;
			return new[] { p.x, p.y, p.z };
		}
		catch { return null; }
	}

	private static float[]? GetMyCamDir()
	{
		try
		{
			var t = SceneEditorSession.Active?.Scene?.Camera?.GameObject.Transform;
			if ( t == null ) return null;
			var f = t.Rotation.Forward;
			return new[] { f.x, f.y, f.z };
		}
		catch { return null; }
	}

	private void StopPresenceLoop()
	{
		_presenceCts?.Cancel();
		_presenceCts = null;
		_lockRefreshCts?.Cancel();
		_lockRefreshCts = null;
		_myLockPath = null;
		lock ( _peerPresence ) _peerPresence.Clear();
		_locks.Clear();
	}

	private void OnPresence( Message msg )
	{
		lock ( _peerPresence )
		{
			_peerPresence[msg.From!] = new PeerPresence
			{
				Scene = msg.Scene ?? "",
				Selection = msg.Selection ?? new List<string>(),
				CamPos = msg.CamPos,
				CamDir = msg.CamRot,
				SeenUtc = DateTime.UtcNow,
			};
		}
		SweepPresence();

		_peers.TryGetValue( msg.From!, out var peer );
		_avatars.Update( msg.From!, peer?.Name ?? "?", peer?.Color ?? "#ffffff",
			msg.CamPos, msg.CamRot, msg.Selection, msg.Scene );

		MainThread.Queue( RefreshPeerList );
	}

	private void SweepPresence()
	{
		bool changed = false;
		var cutoff = DateTime.UtcNow.AddSeconds( -30 );
		lock ( _peerPresence )
		{
			foreach ( var kv in _peerPresence.ToList() )
			{
				if ( kv.Value.SeenUtc < cutoff )
				{
					_peerPresence.Remove( kv.Key );
					changed = true;
				}
			}
		}
		if ( changed ) MainThread.Queue( RefreshPeerList );
	}

	private void OnLockMessage( Message msg )
	{
		var key = NormLockPath( msg.Path! );
		if ( msg.Locks is { Count: > 0 } )
		{
			foreach ( var l in msg.Locks )
				_locks[NormLockPath( l.Path )] = l;
		}
		else
		{
			_locks[key] = new SceneLockInfo
			{
				Path = key,
				Owner = msg.From ?? "",
				OwnerName = msg.From != null && _peers.TryGetValue( msg.From, out var p ) ? p.Name : "?",
			};
		}
		if ( msg.From == _client.MyId ) _myLockPath = key;
		AppendLog( $"[lock] {key} — {_locks[key].OwnerName}" );
		UpdateLockUI();
		RefreshPeerList();
	}

	private void OnLockButtonClicked()
	{
		if ( !_client.IsConnected ) return;

		if ( _myLockPath != null )
		{
			_client.Send( new Message { Type = "unlock", Path = _myLockPath } );
			_locks.Remove( _myLockPath );
			_myLockPath = null;
			_lockRefreshCts?.Cancel();
			UpdateLockUI();
			RefreshPeerList();
			return;
		}

		var scene = GetActiveSceneRel();
		if ( scene == null ) { AppendLog( T( "no_active_scene" ) ); return; }

		_client.Send( new Message { Type = "lock", Path = scene } );
		_myLockPath = NormLockPath( scene ); // optimistic; server confirms or denies
		StartLockRefresh();
		UpdateLockUI();
	}

	private void StartLockRefresh()
	{
		_lockRefreshCts?.Cancel();
		_lockRefreshCts = new CancellationTokenSource();
		var token = _lockRefreshCts.Token;
		_ = Task.Run( async () =>
		{
			try
			{
				while ( !token.IsCancellationRequested )
				{
					await Task.Delay( TimeSpan.FromSeconds( 15 ), token );
					if ( token.IsCancellationRequested ) continue;
					if ( _myLockPath != null && _client.IsConnected )
						_client.Send( new Message { Type = "lock", Path = _myLockPath } );
				}
			}
			catch ( OperationCanceledException ) { }
		}, token );
	}

	private string? GetActiveSceneRel()
	{
		try
		{
			var active = SceneEditorSession.Active;
			if ( active == null || active.IsPrefabSession || active.IsMounted ) return null;

			foreach ( var rel in _sceneDelta.GetTrackedScenes() )
			{
				SceneFile? sf = null;
				try { sf = SceneFile.Load( StripAssetsPrefix( rel ) ); } catch { continue; }
				if ( sf == null ) continue;
				SceneEditorSession? s = null;
				try { s = SceneEditorSession.Resolve( sf ); } catch { continue; }
				if ( s == active ) return rel;
			}
		}
		catch { }
		return null;
	}

	// SceneFile.Load + Resolve on every presence tick (10Hz) hitches the editor —
	// cache the active scene, re-resolve on session switch or every 5s.
	private SceneEditorSession? _cachedSession;
	private string? _cachedSceneRel;
	private DateTime _cachedSceneAt;

	private string? GetActiveSceneRelCached()
	{
		try
		{
			var active = SceneEditorSession.Active;
			if ( active != null && active == _cachedSession && _cachedSceneRel != null &&
				 (DateTime.UtcNow - _cachedSceneAt).TotalSeconds < 5 )
				return _cachedSceneRel;

			var rel = GetActiveSceneRel();
			_cachedSession = active;
			_cachedSceneRel = rel;
			_cachedSceneAt = DateTime.UtcNow;
			return rel;
		}
		catch { return _cachedSceneRel; }
	}

	private static string StripAssetsPrefix( string rel ) =>
		rel.StartsWith( "Assets/", StringComparison.OrdinalIgnoreCase ) ? rel.Substring( "Assets/".Length ) : rel;

	private static List<string> GetSelectionGuids()
	{
		var list = new List<string>();
		try
		{
			var sel = SceneEditorSession.Active?.Selection;
			if ( sel == null ) return list;
			foreach ( var item in sel )
			{
				if ( item is GameObject go )
				{
					try { list.Add( go.Id.ToString() ); }
					catch { }
				}
			}
		}
		catch { }
		return list;
	}

	private void UpdateLockUI()
	{
		MainThread.Queue( () =>
		{
			try
			{
				if ( _lockBtn != null )
				{
					_lockBtn.Text = _myLockPath != null
						? $"{T( "unlock_scene" )} ({Path.GetFileName( _myLockPath )})"
						: T( "lock_scene" );
				}

				if ( _lockBanner != null )
				{
					var active = GetActiveSceneRel();
					string? banner = null;
					if ( active != null )
					{
						var key = NormLockPath( active );
						if ( _locks.TryGetValue( key, out var l ) && l.Owner != _client.MyId )
							banner = $"{T( "locked_by" )}: {l.OwnerName}";
					}
					_lockBanner.Text = banner ?? "";
					_lockBanner.Visible = banner != null;
				}
			}
			catch { }
		} );
	}

	// ──── Reconnect with resync ────

	private void StopReconnectLoop()
	{
		_reconnectCts?.Cancel();
		_reconnectCts = null;
	}

	/// <summary>
	/// Connection dropped (not a manual disconnect). Services keep running so
	/// local edits accumulate; on rejoin we full-resync what peers missed.
	/// </summary>
	private void OnConnectionDropped()
	{
		if ( _reconnectCts != null ) return; // already looping
		_needsResync = true;

		// Capture on the MainThread (OnConnectionDropped runs there via MainThread.Queue)
		var address = _addressEdit.Text.Trim();
		var relayText = _relayEdit.Text.Trim();
		var relay = string.IsNullOrEmpty( relayText ) ? null : relayText;
		var steamJoinCode = _relayEdit.Text.Trim();
		var name = _nameEdit.Text.Trim();
		if ( string.IsNullOrEmpty( name ) ) name = "Player";
		var room = _roomEdit.Text.Trim();
		var password = _passwordEdit.Text.Trim();
		string? passwordHash = string.IsNullOrEmpty( password ) ? null : HashPassword( password );
		var colorHex = ColorToHex( _myColor );

		_reconnectCts = new CancellationTokenSource();
		var token = _reconnectCts.Token;

		_ = Task.Run( async () =>
		{
			int attempt = 0;
			try
			{
				while ( !token.IsCancellationRequested )
				{
					attempt++;
					var delay = TimeSpan.FromSeconds( Math.Min( 5 * attempt, 30 ) );

					MainThread.Queue( () =>
					{
						_statusLabel.Text = $"{T( "reconnecting" )}… ({attempt})";
						_statusLabel.Color = Color.Yellow;
						AppendLog( $"{T( "reconnecting" )}… ({attempt})" );
					} );

					_client.Abort();

					try
					{
						await _client.ConnectAsync( address, name, colorHex, room, passwordHash, relay );
						return; // OnWelcome finishes the job (peers, locks, resync)
					}
					catch ( Exception ex )
					{
						MainThread.Queue( () => AppendLog( $"Reconnect failed: {ex.Message}" ) );
					}

					try { await Task.Delay( delay, token ); }
					catch ( OperationCanceledException ) { break; }
				}
			}
			catch ( OperationCanceledException ) { }
		}, token );
	}

	// ──── Live apply: patches land directly in the open editor scene (no disk, no reload) ────

	/// <summary>
	/// Applies an incoming patch straight into the open SceneEditorSession via GameObject
	/// API. Returns false when the scene isn't open/suitable — caller falls back to disk.
	/// Must run on the MainThread.
	/// </summary>
	private bool TryApplyPatchToLiveScene( string patchJson )
	{
		try
		{
			var patch = System.Text.Json.Nodes.JsonNode.Parse( patchJson )?.AsObject();
			if ( patch == null ) return false;
			if ( patch["scene"]?.GetValue<string>() is not string sceneName ) return false;

			SceneFile? sceneFile = null;
			try { sceneFile = SceneFile.Load( $"scenes/{sceneName}" ); } catch { }
			if ( sceneFile == null ) return false;

			SceneEditorSession? session = null;
			try { session = SceneEditorSession.Resolve( sceneFile ); } catch { }
			if ( session == null || session.IsMounted || session.IsPrefabSession || session.IsPlaying ) return false;

			var scene = session.Scene;
			if ( scene == null || !scene.IsValid ) return false;

			var dir = scene.Directory;

			GameObject? FindGo( string? guid )
			{
				if ( string.IsNullOrEmpty( guid ) ) return null;
				if ( !Guid.TryParse( guid, out var g ) ) return null;
				try { return dir.FindByGuid( g ); } catch { return null; }
			}

			int applied = 0;

			// removed first (delete wins)
			if ( patch["removed"] is System.Text.Json.Nodes.JsonArray removed )
			{
				foreach ( var n in removed )
				{
					var go = FindGo( n?.GetValue<string>() );
					if ( go == null || !go.IsValid ) continue;
					try
					{
						go.Destroy();
						applied++;
					}
					catch { }
				}
			}

			// added (full subtrees). GameObject must be created INSIDE the scene:
			// Scene : GameObject, so root objects parent straight to the scene.
			if ( patch["added"] is System.Text.Json.Nodes.JsonArray added )
			{
				foreach ( var item in added )
				{
					if ( item is not System.Text.Json.Nodes.JsonObject a ) continue;
					var node = a["object"] as System.Text.Json.Nodes.JsonObject;
					if ( node == null ) continue;
					var parent = FindGo( a["parent"]?.GetValue<string>() );

					try
					{
						// Create with parent from the start — no cross-scene parenting
						var go = new GameObject( parent ?? scene, true );
						go.Deserialize( node );
						applied++;
					}
					catch ( Exception ex ) { AppendLog( $"[in-live] add fail: {ex.Message}" ); }
				}
			}

			// moved
			if ( patch["moved"] is System.Text.Json.Nodes.JsonArray moved )
			{
				foreach ( var item in moved )
				{
					if ( item is not System.Text.Json.Nodes.JsonObject m ) continue;
					var go = FindGo( m["guid"]?.GetValue<string>() );
					if ( go == null || !go.IsValid ) continue;
					var to = m["to"]?.GetValue<string>();
					var target = to == null ? scene : FindGo( to );
					if ( target == null ) continue;
					try
					{
						go.SetParent( target, true );
						applied++;
					}
					catch { }
				}
			}

			// updated (components + gameobject fields) — full-object deserialize of the
			// merged current+patch state; Children kept from the LIVE object
			if ( patch["updated"] is System.Text.Json.Nodes.JsonArray updated )
			{
				foreach ( var item in updated )
				{
					if ( item is not System.Text.Json.Nodes.JsonObject u ) continue;
					var go = FindGo( u["guid"]?.GetValue<string>() );
					var props = u["props"] as System.Text.Json.Nodes.JsonObject;
					if ( go == null || !go.IsValid || props == null ) continue;

					try
					{
						// Merge onto current serialization; keep Children as they are
						var current = go.Serialize( new GameObject.SerializeOptions() );
						foreach ( var p in props )
						{
							if ( p.Key == "Children" ) continue;
							if ( p.Value is System.Text.Json.Nodes.JsonObject tombstone &&
								 tombstone.Count == 1 && tombstone["__deleted"]?.GetValue<bool>() == true )
								current.Remove( p.Key );
							else
								current[p.Key] = p.Value is null ? null : System.Text.Json.Nodes.JsonNode.Parse( p.Value.ToJsonString() );
						}
						current.Remove( "Children" ); // structure handled by added/moved/removed
						go.Deserialize( current );
						applied++;
					}
					catch { }
				}
			}

			if ( applied == 0 ) return true; // converged — still handled
			AppendLog( $"[in-live] {sceneName}: {applied} изменений в живой сцене" );

			// Rebaseline from the CURRENT live scene so our live watcher stays quiet (no echo/pong)
			try
			{
				var json = scene.Serialize( new GameObject.SerializeOptions() )?.ToJsonString();
				if ( json != null )
				{
					var fullPath = Path.Combine( ProjectRoot, "Assets", "scenes", sceneName );
					_sceneDelta.RebaselineLive( fullPath, json );
				}
			}
			catch { }

			return true;
		}
		catch ( Exception ex )
		{
			AppendLog( $"[in-live] patch error: {ex.Message}" );
			return false;
		}
	}

	// ──── Live scene diffing (real-time sync without Ctrl+S) ────

	private CancellationTokenSource? _liveWatchCts;

	private void StartLiveWatch()
	{
		// live watch disabled: scene sync is file-event driven
		_liveWatchCts = new CancellationTokenSource();
		var token = _liveWatchCts.Token;
		AppendLog( "Live watch: запущен" );
		_ = Task.Run( async () =>
		{
			bool firstPatchLogged = false;
			string lastStatus = "";
			bool queueDbLogged = false;
			var startTime = DateTime.UtcNow;

			void LogStatus( string s )
			{
				if ( s == lastStatus ) return;
				lastStatus = s;
				MainThread.Queue( () => AppendLog( $"[live] {s}" ) );
			}

			try
			{
				while ( !token.IsCancellationRequested )
				{
					await Task.Delay( 500, token );
					if ( token.IsCancellationRequested ) break;

					if ( !_client.IsConnected )
					{
						LogStatus( "ждёт коннекта..." );
						continue;
					}

					// ВСЁ что трогает движок — только на MainThread
					var tcs = new TaskCompletionSource<(string? Rel, string? Json, string Status)>( TaskCreationOptions.RunContinuationsAsynchronously );

					MainThread.Queue( () =>
					{
						try
						{
							var session = SceneEditorSession.Active;
							if ( session == null )
							{
								tcs.TrySetResult( (null, null, "нет активной сессии (открой сцену)") );
								return;
							}

							var rel = GetActiveSceneRelCached();
							if ( rel == null )
							{
								tcs.TrySetResult( (null, null, $"сессия есть, но сцена не отслеживается (session={session})" ) );
								return;
							}

							var scene = session.Scene;
							if ( scene == null || !scene.IsValid )
							{
								tcs.TrySetResult( (null, null, "Scene null/invalid" ) );
								return;
							}

							var json = scene.Serialize( new GameObject.SerializeOptions() );
							tcs.TrySetResult( (rel, json?.ToJsonString(), json == null ? "Serialize вернул null" : "ok" ) );
						}
						catch ( Exception ex )
						{
							tcs.TrySetResult( (null, null, $"MainThread error: {ex.Message}") );
						}
					} );

					var winner = await Task.WhenAny( tcs.Task, Task.Delay( 1500 ) );
					if ( winner != tcs.Task )
					{
						if ( !queueDbLogged )
						{
							queueDbLogged = true;
							MainThread.Queue( () => AppendLog( "[live] MainThread.Queue не отвечает (UI-поток занят?)" ) );
						}
						continue;
					}

					var state = await tcs.Task;

					if ( state.Status != "ok" )
					{
						LogStatus( state.Status );
						continue;
					}

					if ( state.Rel == null || state.Json == null ) continue;

					LogStatus( $"слежу за {state.Rel}" );

					await Task.Run( () => _sceneDelta.LiveUpdate( state.Rel, state.Json ), token );

					if ( !firstPatchLogged )
					{
						firstPatchLogged = true;
					}
				}
			}
			catch ( OperationCanceledException ) { }
			catch ( Exception ex )
			{
				MainThread.Queue( () => AppendLog( $"[live] цикл умер: {ex.Message}" ) );
			}
		}, token );
	}

	private void StopLiveWatch()
	{
		_liveWatchCts?.Cancel();
		_liveWatchCts = null;
	}

	// ──── UI helpers ────

	private void SetConnected( bool connected )
	{
		if ( connected )
		{
			_connectBtn.Text = T("disconnect");
			_connectBtn.Tint = Color.Green;
			_statusLabel.Text = T("connected");
			_statusLabel.Color = Color.Green;
			_addressEdit.Enabled = false;
			_relayEdit.Enabled = false;
			_nameEdit.Enabled = false;
			_roomEdit.Enabled = false;
		}
		else
		{
			_connectBtn.Text = T("connect");
			_connectBtn.Tint = Theme.ControlBackground;
			_statusLabel.Text = T("disconnected");
			_statusLabel.Color = Theme.Text;
			_addressEdit.Enabled = true;
			_relayEdit.Enabled = true;
			_nameEdit.Enabled = true;
			_roomEdit.Enabled = true;
			_peers.Clear();
			RefreshPeerList();
		}
	}

	private void RefreshPeerList()
	{
		// Thrash guard: presence ticks every second — rebuild widgets only when visible content changed
		var sig = new System.Text.StringBuilder();
		foreach ( var (_, peer) in _peers )
		{
			sig.Append( peer.Id ).Append( '|' ).Append( peer.Name ).Append( '|' ).Append( peer.Color ).Append( ';' );
			lock ( _peerPresence )
			{
				if ( _peerPresence.TryGetValue( peer.Id, out var pres ) )
					sig.Append( pres.Scene ).Append( '#' ).Append( pres.Selection.Count ).Append( ';' );
			}
		}
		foreach ( var l in _locks.Values )
			sig.Append( l.Path ).Append( '>' ).Append( l.Owner ).Append( ';' );
		sig.Append( _client.MyId );

		var sigStr = sig.ToString();
		if ( sigStr == _peerListSig ) return;
		_peerListSig = sigStr;

		_peerListLayout.Clear( true );

		if ( _peers.Count == 0 )
		{
			_peerListLayout.Add( new Label( $"  {T("no_peers")}", this ) { Color = Theme.Text } );
			return;
		}

		foreach ( var (_, peer) in _peers )
		{
			var row = _peerListLayout.AddRow();
			var peerColor = Color.Parse( peer.Color ) ?? Color.Gray;
			var dot = new Widget( this ) { FixedWidth = 10, FixedHeight = 10 };
			dot.OnPaintOverride = () =>
			{
				Paint.ClearPen();
				Paint.SetBrush( peerColor );
				Paint.DrawRect( dot.LocalRect, 5 );
				return true;
			};
			row.Add( dot );

			var suffix = "";
			if ( _client.MyId == peer.Id )
				suffix = $" ({T("you")})";
			else
			{
				lock ( _peerPresence )
				{
					if ( _peerPresence.TryGetValue( peer.Id, out var pres ) &&
						 ( !string.IsNullOrEmpty( pres.Scene ) || pres.Selection.Count > 0 ) )
						suffix = $" — {pres.Scene} ({pres.Selection.Count})";
				}
				foreach ( var l in _locks.Values )
				{
					if ( l.Owner == peer.Id )
					{
						suffix += $" 🔒{Path.GetFileName( l.Path )}";
						break;
					}
				}
			}
			row.Add( new Label( $" {peer.Name}{suffix}", this ), 1 );

			if ( _client.MyId != peer.Id )
			{
				var followBtn = row.Add( new Button( T( "follow" ), this ) );
				var peerId = peer.Id;
				followBtn.Clicked = () => FollowPeer( peerId );
			}
		}
	}

	private void AppendLog( string msg )
	{
		var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
		_logLines.Add( line );
		if ( _logLines.Count > 200 )
			_logLines.RemoveAt( 0 );
		_logText?.AppendPlainText( line );
		WriteToLogFile( line );
	}

	private static Color RandomColor()
	{
		var colors = new[] { Color.Red, Color.Blue, Color.Green, Color.Yellow, Color.Cyan, Color.Magenta, Color.Orange };
		return colors[Math.Abs( Environment.TickCount ) % colors.Length];
	}

	private static string ColorToHex( Color c )
	{
		return $"#{(byte)(c.r * 255):X2}{(byte)(c.g * 255):X2}{(byte)(c.b * 255):X2}";
	}

	private static string HashPassword( string password )
	{
		var bytes = System.Text.Encoding.UTF8.GetBytes( password );
		var hash = System.Security.Cryptography.SHA256.HashData( bytes );
		return Convert.ToHexString( hash ).ToLowerInvariant();
	}

	private void AddSpacer()
	{
		var spacer = new Widget( this ) { FixedHeight = 12 };
		Layout.Add( spacer );
	}

	// ──── Localization ────

	private static readonly Dictionary<string, Dictionary<string, string>> Loc = new()
	{
		["ru"] = new()
		{
		["settings"] = "Настройки",
		["language"] = "Язык",
		["my_color"] = "Мой цвет",
			["random"] = "Случайный",
			["pick_color"] = "Выбрать цвет",
			["save"] = "Сохранить",
			["reset"] = "Сброс",
			["auto_start_hub"] = "Автозапуск хаба",
			["start_hub"] = "Запустить",
			["stop_hub"] = "Остановить",
			["hub_already_running"] = "Хаб уже запущен",
			["hub_not_running"] = "Хаб не запущен",
			["hub_settings"] = "Настройки хаба",
			["connection"] = "Подключение",
			["address"] = "Адрес",
			["relay"] = "Ретранслятор",
			["nickname"] = "Ник",
			["nickname_hint"] = "Игрок",
			["room"] = "Комната",
			["password"] = "Пароль",
			["password_hint"] = "пароль сервера",
			["connect"] = "Подключиться",
			["disconnect"] = "Отключиться",
			["connected"] = "Подключено",
			["disconnected"] = "Отключён",
			["peers"] = "Участники:",
			["no_peers"] = "нет участников",
		["you"] = "вы",
		["log"] = "Лог:",
		["git_remote"] = "Удалённый git (GitHub)",
			["init"] = "Init",
			["commit"] = "Коммит",
			["commit_push"] = "Коммит & Push",
			["pull"] = "Pull",
			["cancel"] = "Отмена",
			["commit_name"] = "Имя",
			["commit_name_hint"] = "название коммита",
			["commit_desc"] = "Описание",
			["commit_desc_hint"] = "описание коммита",
		["init_remote_hint"] = "Инициализировать удалённый git",
		["enter_remote_url"] = "Укажите URL репозитория",
		["enter_address"] = "Укажите адрес сервера",
			["color_taken"] = "Цвет уже занят, выбран случайный",
			["peers_online"] = "В комнате участников",
			["joined"] = "подключился",
			["left"] = "отключился",
			["file"] = "файл",
			["deleted"] = "удалён",
			["bytes"] = "байт",
			["already_initialized"] = "Проект уже инициализирован",
			["auth_failed"] = "Ошибка авторизации",
			["follow"] = "К игроку",
			["no_cam_data"] = "нет данных камеры",
			["reconnecting"] = "Переподключение",
			["resynced"] = "Ресинк завершён",
			["sync_complete"] = "Синхронизация завершена",
			["lock_scene"] = "Лок сцены",
			["unlock_scene"] = "Снять лок",
			["locked_by"] = "Залочено",
			["no_active_scene"] = "Нет активной сцены для лока",
		},
		["en"] = new()
		{
			["settings"] = "Settings",
			["language"] = "Language",
			["git_path"] = "Git path",
			["git_path_hint"] = "project folder path",
			["my_color"] = "My color",
			["random"] = "Random",
			["pick_color"] = "Pick color",
			["save"] = "Save",
			["reset"] = "Reset",
			["auto_start_hub"] = "Auto-start hub",
			["start_hub"] = "Start",
			["stop_hub"] = "Stop",
			["hub_already_running"] = "Hub is already running",
			["hub_not_running"] = "Hub is not running",
			["hub_settings"] = "Hub Settings",
			["connection"] = "Connection",
			["address"] = "Address",
			["relay"] = "Relay",
			["nickname"] = "Nickname",
			["nickname_hint"] = "Player",
			["room"] = "Room",
			["password"] = "Password",
			["password_hint"] = "server password",
			["connect"] = "Connect",
			["disconnect"] = "Disconnect",
			["connected"] = "Connected",
			["disconnected"] = "Disconnected",
			["peers"] = "Peers:",
			["no_peers"] = "no peers",
		["you"] = "you",
		["log"] = "Log:",
		["git_remote"] = "Remote git (GitHub)",
			["init"] = "Init",
			["commit"] = "Commit",
			["commit_push"] = "Commit & Push",
			["pull"] = "Pull",
			["cancel"] = "Cancel",
			["commit_name"] = "Name",
			["commit_name_hint"] = "commit name",
			["commit_desc"] = "Description",
			["commit_desc_hint"] = "commit description",
		["init_remote_hint"] = "Initialize remote git",
		["enter_remote_url"] = "Enter repository URL",
		["enter_address"] = "Enter server address",
			["color_taken"] = "Color taken, switched to random",
			["peers_online"] = "Peers in room",
			["joined"] = "joined",
			["left"] = "left",
			["file"] = "file",
			["deleted"] = "deleted",
			["bytes"] = "bytes",
			["already_initialized"] = "Project already initialized",
			["auth_failed"] = "Authentication failed",
			["follow"] = "Follow",
			["no_cam_data"] = "no camera data",
			["reconnecting"] = "Reconnecting",
			["resynced"] = "Resync complete",
			["sync_complete"] = "Sync complete",
			["lock_scene"] = "Lock scene",
			["unlock_scene"] = "Unlock",
			["locked_by"] = "Locked by",
			["no_active_scene"] = "No active scene to lock",
		},
	};

	private string T( string key )
	{
		if ( Loc.TryGetValue( _lang, out var dict ) && dict.TryGetValue( key, out var val ) )
			return val;
		if ( Loc.TryGetValue( "ru", out var fallback ) && fallback.TryGetValue( key, out var fb ) )
			return fb;
		return key;
	}

	// ──── Settings persistence ────

	private void SaveSettings()
	{
		var cookie = Sandbox.Internal.GlobalToolsNamespace.EditorCookie;
		cookie.Set( "tc.address", _addressEdit.Text );
		cookie.Set( "tc.relay", _relayEdit.Text );
		cookie.Set( "tc.name", _nameEdit.Text );
		cookie.Set( "tc.room", _roomEdit.Text );
		cookie.Set( "tc.password", _passwordEdit.Text );
		cookie.Set( "tc.remote", _remoteEdit.Text );
		cookie.Set( "tc.color", ColorToHex( _myColor ) );
		cookie.Set( "tc.lang", _lang );
		cookie.Set( "tc.history", string.Join( ";", _history.TakeLast( 8 ) ) );
	}

	private void LoadSettings()
	{
		_suppressEvents = true;
		var cookie = Sandbox.Internal.GlobalToolsNamespace.EditorCookie;
		_addressEdit.Text = cookie.Get( "tc.address", "127.0.0.1:4877" );
		_relayEdit.Text = cookie.Get( "tc.relay", "" );
		_history.Clear();
		foreach ( var h in cookie.Get( "tc.history", "" ).Split( ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			if ( _history.Contains( h ) ) continue;
			_history.Add( h );
			try { _historyCombo.AddItem( h, null, null, null, false ); }
			catch { }
		}
		_nameEdit.Text = cookie.Get( "tc.name", "" );
		_roomEdit.Text = cookie.Get( "tc.room", "default" );
		_passwordEdit.Text = cookie.Get( "tc.password", "" );
		_remoteEdit.Text = cookie.Get( "tc.remote", "" );

		var hex = cookie.Get( "tc.color", "" );
		if ( !string.IsNullOrEmpty( hex ) )
			_myColor = Color.Parse( hex ) ?? RandomColor();
		else
			_myColor = RandomColor();

		_lang = cookie.Get( "tc.lang", "en" );

		// Set language combo
		_suppressEvents = true;
		if ( _lang == "en" )
			_langCombo.CurrentIndex = 1;
		else
			_langCombo.CurrentIndex = 0;
		_suppressEvents = false;
	}

	// ──── Hub start ────

	private async void StartHub()
	{
		try
		{
			var (hubPath, hubPassword, hubPort, _) = HubSettingsWindow.LoadFromCookie();

			if ( string.IsNullOrEmpty( hubPath ) )
			{
				hubPath = Path.Combine( ProjectRoot, "Libraries", "teamcreate", "Hub" );
			}

			var publishExe = Path.Combine( hubPath, "publish", "TeamCreateHub.exe" );
			var binExe = Path.Combine( hubPath, "bin", "Release", "net10.0", "win-x64", "TeamCreateHub.exe" );
			var dllPath = Path.Combine( hubPath, "bin", "Release", "net10.0", "TeamCreateHub.dll" );
			var exePath = Path.Combine( hubPath, "TeamCreateHub.exe" );

			string fileName;
			string fileArgs;

			if ( File.Exists( publishExe ) )
			{
				fileName = publishExe;
				fileArgs = "";
			}
			else if ( File.Exists( binExe ) )
			{
				fileName = binExe;
				fileArgs = "";
			}
			else if ( File.Exists( dllPath ) )
			{
				fileName = "dotnet";
				fileArgs = $"\"{dllPath}\"";
			}
			else if ( File.Exists( exePath ) )
			{
				fileName = exePath;
				fileArgs = "";
			}
			else
			{
				AppendLog( $"Hub not found: {publishExe}" );
				return;
			}

			// Kill old hub process in background to avoid deadlock with OutputDataReceived
			var oldProcess = _hubProcess;
			_hubProcess = null;
			if ( oldProcess != null )
			{
				_ = Task.Run( () =>
				{
					try
					{
						oldProcess.Kill();
						oldProcess.Dispose();
					}
					catch { }
				} );
			}

			// Wait for port to free up
			await Task.Delay( 500 );

			var hubPwd = hubPassword.Trim().Replace( "\"", "" );
			var hasPassword = !string.IsNullOrEmpty( hubPwd );
			var portArg = hubPort != "4877" ? $"--port {hubPort}" : "";
			var pwdArg = hasPassword ? $"--password \"{hubPwd}\"" : "";
			var args = $"{portArg} {pwdArg}".Trim();

			var psi = new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = $"{fileArgs} {args}".Trim(),
				WorkingDirectory = hubPath,
				CreateNoWindow = true,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = false,
				RedirectStandardInput = false,
				StandardOutputEncoding = System.Text.Encoding.UTF8,
				ErrorDialog = false,
			};

			AppendLog( $"Starting: {fileName}" );
			_hubProcess = Process.Start( psi );
			AppendLog( $"Hub started (PID {_hubProcess.Id}), password: {(hasPassword ? "YES" : "NO")}" );

			_hubProcess.OutputDataReceived += ( _, e ) =>
			{
				if ( e.Data != null )
					MainThread.Queue( () => AppendLog( $"[hub] {e.Data}" ) );
			};
			_hubProcess.BeginOutputReadLine();
		}
		catch ( Exception ex )
		{
			AppendLog( $"Hub start error: {ex.Message}" );
		}
	}

	protected override bool OnClose()
	{
		StopHub();
		return true;
	}

	private void StopHub()
	{
		var proc = _hubProcess;
		_hubProcess = null;
		AppendLog( proc != null ? "Hub detached" : "Hub not running" );
	}
}
