namespace Editor.TeamCreate;

[Dock( "Editor", "Team Create", "group_work" )]
public sealed class TeamCreateWindow : Widget
{
	private readonly TeamCreateClient _client = new();
	private readonly FileSyncService _fileSync = new();

	// UI elements
	private Label _statusLabel;
	private LineEdit _addressEdit;
	private LineEdit _nameEdit;
	private LineEdit _roomEdit;
	private LineEdit _passwordEdit;
	private LineEdit _gitPathEdit;
	private LineEdit _remoteEdit;
	private Button _connectBtn;
	private Layout _peerListLayout;
	private TextEdit _logText;
	private Widget _colorPreview;
	private bool _autoCommitLocalEnabled;
	private bool _autoCommitRemoteEnabled;
	private LineEdit _autoCommitLocalInterval;
	private LineEdit _autoCommitRemoteInterval;
	private Button _autoCommitLocalBtn;
	private Button _autoCommitRemoteBtn;
	private ComboBox _langCombo;

	// State
	private Color _myColor = Color.Red;
	private readonly Dictionary<string, PeerInfo> _peers = new();
	private CancellationTokenSource? _autoCommitLocalCts;
	private CancellationTokenSource? _autoCommitRemoteCts;
	private string _lang = "en";
	private Process? _hubProcess;
	private bool _suppressEvents;

	private static string ProjectRoot => Project.Current.GetRootPath();

	public TeamCreateWindow( Widget parent ) : base( parent )
	{
		Layout = Layout.Column();
		Layout.Margin = 8;
		Layout.Spacing = 4;

		_client.OnLog += msg => MainThread.Queue( () => AppendLog( msg ) );
		_client.OnWelcome += peers => MainThread.Queue( () => OnWelcome( peers ) );
		_client.OnPeerJoined += peer => MainThread.Queue( () => OnPeerJoined( peer ) );
		_client.OnPeerLeft += peer => MainThread.Queue( () => OnPeerLeft( peer ) );
		_client.OnMessage += msg => MainThread.Queue( () => OnFileMessage( msg ) );
		_client.OnAuthRejected += reason => MainThread.Queue( () => OnAuthRejected( reason ) );

		_fileSync.OnLog += msg => MainThread.Queue( () => AppendLog( msg ) );
		GitService.OnLog += msg => MainThread.Queue( () => AppendLog( msg ) );

		RebuildUI();
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
		BuildAutoCommitSection();
		BuildLog();

		LoadSettings();

		foreach ( var line in _logLines )
			_logText?.AppendPlainText( line );

		UpdateAutoCommitButton( _autoCommitLocalBtn, _autoCommitLocalEnabled );
		UpdateAutoCommitButton( _autoCommitRemoteBtn, _autoCommitRemoteEnabled );
		UpdateAutoCommit();

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

		// Git path
		var row1 = Layout.AddRow();
		row1.Add( new Label( $"{T("git_path")}:", this ) { FixedWidth = 80 } );
		_gitPathEdit = row1.Add( new LineEdit( this ) { PlaceholderText = T("git_path_hint") } );

		// Remote
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

	private void OpenCommitPanel( bool pushToRemote )
	{
		var popup = new Dialog( this );
		popup.Window.WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.WindowTitle | WindowFlags.CloseButton;
		popup.Window.WindowTitle = pushToRemote ? T("commit_push") : T("commit");
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

		var commitBtn = btnRow.Add( new Button( pushToRemote ? T("commit_push") : T("commit"), this ) );
		commitBtn.Tint = Color.Green;
		commitBtn.Clicked = async () =>
		{
			if ( string.IsNullOrEmpty( ProjectRoot ) ) { AppendLog( T("enter_git_path") ); popup.Close(); return; }

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

			if ( pushToRemote )
				await GitService.CommitAndPushAsync( ProjectRoot, message );
			else
				await GitService.CommitAsync( ProjectRoot, message );
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

		AddSpacer();
	}

	// ──── Actions ────

	private void BuildActions()
	{
		// Local
		var localHeader = Layout.AddRow();
		localHeader.Add( new Label( $"<b>{T("git_local")}</b>", this ), 1 );

		var localRow = Layout.AddRow();

		var initLocalBtn = localRow.Add( new Button( T("init"), this ) );
		initLocalBtn.Clicked = OnInitLocalClicked;
		initLocalBtn.ToolTip = T("init_local_hint");

		var commitLocalBtn = localRow.Add( new Button( T("commit"), this ) );
		commitLocalBtn.Clicked = () => OpenCommitPanel( false );

		var pullLocalBtn = localRow.Add( new Button( T("pull"), this ) );
		pullLocalBtn.Clicked = OnPullLocalClicked;

		// Remote
		var remoteHeader = Layout.AddRow();
		remoteHeader.Add( new Label( $"<b>{T("git_remote")}</b>", this ), 1 );

		var remoteRow = Layout.AddRow();

		var initRemoteBtn = remoteRow.Add( new Button( T("init"), this ) );
		initRemoteBtn.Clicked = OnInitRemoteClicked;
		initRemoteBtn.ToolTip = T("init_remote_hint");

		var commitRemoteBtn = remoteRow.Add( new Button( T("commit_push"), this ) );
		commitRemoteBtn.Clicked = () => OpenCommitPanel( true );

		var pullRemoteBtn = remoteRow.Add( new Button( T("pull"), this ) );
		pullRemoteBtn.Clicked = OnPullRemoteClicked;

		AddSpacer();
	}

	// ──── Auto-commit ────

	private void BuildAutoCommitSection()
	{
		var header = Layout.AddRow();
		header.Add( new Label( $"<b>{T("auto_commit")}</b>", this ), 1 );

		// Local
		var localRow = Layout.AddRow();
		_autoCommitLocalBtn = localRow.Add( new Button( T("auto_commit_local_off"), this ) );
		_autoCommitLocalBtn.Clicked = () =>
		{
			_autoCommitLocalEnabled = !_autoCommitLocalEnabled;
			UpdateAutoCommitButton( _autoCommitLocalBtn, _autoCommitLocalEnabled );
			SaveSettings();
			UpdateAutoCommit();
		};

		_autoCommitLocalInterval = localRow.Add( new LineEdit( this ) { FixedWidth = 40, PlaceholderText = "3" } );
		_autoCommitLocalInterval.Text = "3";
		localRow.Add( new Label( T("minutes"), this ) );

		// Remote
		var remoteRow = Layout.AddRow();
		_autoCommitRemoteBtn = remoteRow.Add( new Button( T("auto_commit_remote_off"), this ) );
		_autoCommitRemoteBtn.Clicked = () =>
		{
			_autoCommitRemoteEnabled = !_autoCommitRemoteEnabled;
			UpdateAutoCommitButton( _autoCommitRemoteBtn, _autoCommitRemoteEnabled );
			SaveSettings();
			UpdateAutoCommit();
		};

		_autoCommitRemoteInterval = remoteRow.Add( new LineEdit( this ) { FixedWidth = 40, PlaceholderText = "3" } );
		_autoCommitRemoteInterval.Text = "3";
		remoteRow.Add( new Label( T("minutes"), this ) );

		AddSpacer();
	}

	private void UpdateAutoCommitButton( Button btn, bool enabled )
	{
		btn.Text = enabled ? T("auto_commit_on") : ( btn == _autoCommitLocalBtn ? T("auto_commit_local_off") : T("auto_commit_remote_off") );
		btn.Tint = enabled ? Color.Green : Theme.ControlBackground;
	}

	private void UpdateAutoCommit()
	{
		// Local
		_autoCommitLocalCts?.Cancel();
		_autoCommitLocalCts = null;

		if ( _autoCommitLocalEnabled )
		{
			if ( int.TryParse( _autoCommitLocalInterval.Text, out var mins ) && mins > 0 )
			{
				_autoCommitLocalCts = new CancellationTokenSource();
				var token = _autoCommitLocalCts.Token;
				_ = Task.Run( async () =>
				{
					while ( !token.IsCancellationRequested )
					{
						await Task.Delay( TimeSpan.FromMinutes( mins ), token );
						if ( token.IsCancellationRequested ) break;
						if ( !string.IsNullOrEmpty( ProjectRoot ) )
							await GitService.CommitAsync( ProjectRoot, $"Team Create: auto save ({DateTime.Now:HH:mm:ss})" );
					}
				}, token );
				AppendLog( $"{T("auto_commit_local_on")} {mins} {T("minutes")}" );
			}
		}

		// Remote
		_autoCommitRemoteCts?.Cancel();
		_autoCommitRemoteCts = null;

		if ( _autoCommitRemoteEnabled )
		{
			if ( int.TryParse( _autoCommitRemoteInterval.Text, out var mins ) && mins > 0 )
			{
				_autoCommitRemoteCts = new CancellationTokenSource();
				var token = _autoCommitRemoteCts.Token;
				_ = Task.Run( async () =>
				{
					while ( !token.IsCancellationRequested )
					{
						await Task.Delay( TimeSpan.FromMinutes( mins ), token );
						if ( token.IsCancellationRequested ) break;
						if ( !string.IsNullOrEmpty( ProjectRoot ) )
							await GitService.CommitAndPushAsync( ProjectRoot, $"Team Create: auto save ({DateTime.Now:HH:mm:ss})" );
					}
				}, token );
				AppendLog( $"{T("auto_commit_remote_on")} {mins} {T("minutes")}" );
			}
		}

		SaveSettings();
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
		if ( _client.IsConnected )
		{
			_autoCommitLocalCts?.Cancel();
			_autoCommitRemoteCts?.Cancel();
			await _client.DisconnectAsync();
			_fileSync.Stop();
			_peers.Clear();
			RefreshPeerList();
			SetConnected( false );
			return;
		}

		var address = _addressEdit.Text.Trim();
		var name = _nameEdit.Text.Trim();
		var room = _roomEdit.Text.Trim();
		var password = _passwordEdit.Text.Trim();

		if ( string.IsNullOrEmpty( address ) ) { AppendLog( T("enter_address") ); return; }
		if ( string.IsNullOrEmpty( name ) ) name = "Player";

		SaveSettings();

		if ( !string.IsNullOrEmpty( ProjectRoot ) && Directory.Exists( ProjectRoot ) )
			_fileSync.Start( ProjectRoot, SendFile, SendFileDelete );

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

		await _client.ConnectAsync( address, name, ColorToHex( _myColor ), room, passwordHash );
	}

	private async void OnInitLocalClicked()
	{
		if ( string.IsNullOrEmpty( ProjectRoot ) ) { AppendLog( T("enter_git_path") ); return; }

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

		await GitService.InitLocalAsync( ProjectRoot );

		var gitPath = _gitPathEdit.Text.Trim();
		if ( !string.IsNullOrEmpty( gitPath ) && gitPath != ProjectRoot )
		{
			Directory.CreateDirectory( gitPath );
			var copied = GitService.CopyProjectFiles( ProjectRoot, gitPath );
			AppendLog( $"Backup copied {copied} files to {gitPath}" );
		}
	}

	private async void OnInitRemoteClicked()
	{
		if ( string.IsNullOrEmpty( ProjectRoot ) ) { AppendLog( T("enter_git_path") ); return; }

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

	private void OnPullLocalClicked()
	{
		if ( string.IsNullOrEmpty( ProjectRoot ) ) { AppendLog( T("enter_git_path") ); return; }
		_ = GitService.PullLocalAsync( ProjectRoot );
	}

	private void OnPullRemoteClicked()
	{
		if ( string.IsNullOrEmpty( ProjectRoot ) ) { AppendLog( T("enter_git_path") ); return; }
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

	private void OnWelcome( List<PeerInfo> peers )
	{
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

		UpdateAutoCommit();
	}

	private void OnPeerJoined( PeerInfo peer )
	{
		_peers[peer.Id] = peer;
		RefreshPeerList();
		AppendLog( $"{peer.Name} {T("joined")}" );
	}

	private void OnPeerLeft( PeerInfo peer )
	{
		_peers.Remove( peer.Id );
		RefreshPeerList();
		AppendLog( $"{peer.Name} {T("left")}" );
	}

	private void OnFileMessage( Message msg )
	{
		switch ( msg.Type )
		{
			case "file" when msg.Path != null && msg.ContentB64 != null:
				var data = Convert.FromBase64String( msg.ContentB64 );
				_fileSync.ApplyRemoteFile( msg.Path, data );
				AppendLog( $"[in] {T("file")}: {msg.Path} ({data.Length} {T("bytes")})" );
				break;
			case "file-delete" when msg.Path != null:
				_fileSync.ApplyRemoteDelete( msg.Path );
				AppendLog( $"[in] {T("deleted")}: {msg.Path}" );
				break;
		}
	}

	private void OnAuthRejected( string reason )
	{
		AppendLog( $"{T("auth_failed")}: {reason}" );
		SetConnected( false );
		_fileSync.Stop();
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
			_nameEdit.Enabled = true;
			_roomEdit.Enabled = true;
			_peers.Clear();
			RefreshPeerList();
		}
	}

	private void RefreshPeerList()
	{
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
			row.Add( new Label( $" {peer.Name}", this ), 1 );

			if ( _client.MyId == peer.Id )
				row.Add( new Label( $"({T("you")})", this ) { Color = Theme.Text } );
		}
	}

	private void AppendLog( string msg )
	{
		var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
		_logLines.Add( line );
		if ( _logLines.Count > 200 )
			_logLines.RemoveAt( 0 );
		_logText?.AppendPlainText( line );
	}

	private static Color RandomColor()
	{
		var colors = new[] { Color.Red, Color.Blue, Color.Green, Color.Yellow, Color.Cyan, Color.Magenta, Color.Orange };
		return colors[Environment.TickCount % colors.Length];
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
			["git_path"] = "Git-путь",
			["git_path_hint"] = "путь к папке проекта",
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
			["auto_commit"] = "Автокоммит",
			["auto_commit_enabled"] = "Автокоммит: ВКЛ",
			["auto_commit_disabled"] = "Автокоммит: ВЫКЛ",
			["auto_commit"] = "Автокоммит",
			["auto_commit_local_off"] = "Локальный: ВЫКЛ",
			["auto_commit_remote_off"] = "Удалённый: ВЫКЛ",
			["auto_commit_on"] = "ВКЛ",
			["auto_commit_local_on"] = "Локальный автокоммит:",
			["auto_commit_remote_on"] = "Удалённый автокоммит:",
			["minutes"] = "мин",
			["log"] = "Лог:",
			["git_local"] = "Локальный git",
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
			["init_local_hint"] = "Инициализировать локальный git",
			["init_remote_hint"] = "Инициализировать удалённый git",
			["enter_remote_url"] = "Укажите URL репозитория",
			["enter_address"] = "Укажите адрес сервера",
			["enter_git_path"] = "Укажите путь к git-проекту в настройках",
			["color_taken"] = "Цвет уже занят, выбран случайный",
			["peers_online"] = "В комнате участников",
			["joined"] = "подключился",
			["left"] = "отключился",
			["file"] = "файл",
			["deleted"] = "удалён",
			["bytes"] = "байт",
			["already_initialized"] = "Проект уже инициализирован",
			["auth_failed"] = "Ошибка авторизации",
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
			["auto_commit"] = "Auto-commit",
			["auto_commit_local_off"] = "Local: OFF",
			["auto_commit_remote_off"] = "Remote: OFF",
			["auto_commit_on"] = "ON",
			["auto_commit_local_on"] = "Local auto-commit:",
			["auto_commit_remote_on"] = "Remote auto-commit:",
			["minutes"] = "min",
			["log"] = "Log:",
			["git_local"] = "Local git",
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
			["init_local_hint"] = "Initialize local git",
			["init_remote_hint"] = "Initialize remote git",
			["enter_remote_url"] = "Enter repository URL",
			["init_hint"] = "Initialize git and create first commit",
			["commit_hint"] = "Commit and push all changes",
			["pull_hint"] = "Pull changes from git",
			["enter_address"] = "Enter server address",
			["enter_git_path"] = "Enter git project path in settings",
			["color_taken"] = "Color taken, switched to random",
			["peers_online"] = "Peers in room",
			["joined"] = "joined",
			["left"] = "left",
			["file"] = "file",
			["deleted"] = "deleted",
			["bytes"] = "bytes",
			["already_initialized"] = "Project already initialized",
			["auth_failed"] = "Authentication failed",
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
		cookie.Set( "tc.name", _nameEdit.Text );
		cookie.Set( "tc.room", _roomEdit.Text );
		cookie.Set( "tc.password", _passwordEdit.Text );
		cookie.Set( "tc.gitpath", _gitPathEdit.Text );
		cookie.Set( "tc.remote", _remoteEdit.Text );
		cookie.Set( "tc.color", ColorToHex( _myColor ) );
		cookie.Set( "tc.lang", _lang );
		cookie.Set( "tc.autocommit.local", _autoCommitLocalEnabled );
		cookie.Set( "tc.autocommit.local.interval", _autoCommitLocalInterval.Text );
		cookie.Set( "tc.autocommit.remote", _autoCommitRemoteEnabled );
		cookie.Set( "tc.autocommit.remote.interval", _autoCommitRemoteInterval.Text );
	}

	private void LoadSettings()
	{
		var cookie = Sandbox.Internal.GlobalToolsNamespace.EditorCookie;
		_addressEdit.Text = cookie.Get( "tc.address", "127.0.0.1:4877" );
		_nameEdit.Text = cookie.Get( "tc.name", "" );
		_roomEdit.Text = cookie.Get( "tc.room", "default" );
		_passwordEdit.Text = cookie.Get( "tc.password", "" );
		_gitPathEdit.Text = cookie.Get( "tc.gitpath", "" );
		_remoteEdit.Text = cookie.Get( "tc.remote", "" );
		_autoCommitLocalInterval.Text = cookie.Get( "tc.autocommit.local.interval", "3" );
		_autoCommitRemoteInterval.Text = cookie.Get( "tc.autocommit.remote.interval", "3" );

		var hex = cookie.Get( "tc.color", "" );
		if ( !string.IsNullOrEmpty( hex ) )
			_myColor = Color.Parse( hex ) ?? RandomColor();
		else
			_myColor = RandomColor();

		_lang = cookie.Get( "tc.lang", "en" );
		_autoCommitLocalEnabled = cookie.Get( "tc.autocommit.local", false );
		_autoCommitRemoteEnabled = cookie.Get( "tc.autocommit.remote", false );

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

			var hubPwd = hubPassword.Trim();
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
