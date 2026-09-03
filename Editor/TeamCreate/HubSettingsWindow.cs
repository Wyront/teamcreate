namespace Editor.TeamCreate;

public sealed class HubSettingsWindow
{
	private Dialog _dialog;
	private LineEdit _hubPathEdit;
	private LineEdit _hubPasswordEdit;
	private LineEdit _portEdit;
	private LineEdit _roomEdit;
	private string _lang;

	public HubSettingsWindow( Widget parent, string lang )
	{
		_lang = lang;

		_dialog = new Dialog( parent );
		_dialog.Window.WindowFlags = WindowFlags.Dialog | WindowFlags.Customized | WindowFlags.WindowTitle | WindowFlags.CloseButton;
		_dialog.Window.WindowTitle = T( "hub_settings" );
		_dialog.Window.Size = new( 380, 220 );
		_dialog.Layout = Layout.Column();
		_dialog.Layout.Margin = 8;
		_dialog.Layout.Spacing = 4;

		// Hub path
		var rowPath = _dialog.Layout.AddRow();
		rowPath.Add( new Label( $"{T( "hub_path" )}:", parent ) { FixedWidth = 100 } );
		_hubPathEdit = rowPath.Add( new LineEdit( parent ) { PlaceholderText = T( "hub_path_hint" ) } );

		// Hub password
		var rowPwd = _dialog.Layout.AddRow();
		rowPwd.Add( new Label( $"{T( "hub_password" )}:", parent ) { FixedWidth = 100 } );
		_hubPasswordEdit = rowPwd.Add( new LineEdit( parent ) { PlaceholderText = T( "hub_password_hint" ) } );

		// Port
		var rowPort = _dialog.Layout.AddRow();
		rowPort.Add( new Label( $"{T( "hub_port" )}:", parent ) { FixedWidth = 100 } );
		_portEdit = rowPort.Add( new LineEdit( parent ) { PlaceholderText = "4877" } );

		// Room
		var rowRoom = _dialog.Layout.AddRow();
		rowRoom.Add( new Label( $"{T( "hub_room" )}:", parent ) { FixedWidth = 100 } );
		_roomEdit = rowRoom.Add( new LineEdit( parent ) { PlaceholderText = "default" } );

		// Buttons
		var btnRow = _dialog.Layout.AddRow();

		var resetBtn = btnRow.Add( new Button( T( "reset" ), parent ) );
		resetBtn.Clicked = OnReset;

		btnRow.Add( new Widget( parent ), 1 );

		var saveBtn = btnRow.Add( new Button( T( "save" ), parent ) );
		saveBtn.Clicked = OnSave;

		LoadSettings();
	}

	public void Show() => _dialog.Show();

	private void OnSave()
	{
		if ( string.IsNullOrWhiteSpace( _hubPasswordEdit.Text ) )
		{
			_hubPasswordEdit.Text = "";
			_hubPasswordEdit.PlaceholderText = T( "hub_password_required" );
			return;
		}

		SaveSettings();
		_dialog.Close();
	}

	private void OnReset()
	{
		_hubPathEdit.Text = "";
		_hubPasswordEdit.Text = "";
		_portEdit.Text = "4877";
		_roomEdit.Text = "default";
	}

	private void SaveSettings()
	{
		var cookie = Sandbox.Internal.GlobalToolsNamespace.EditorCookie;
		cookie.Set( "tc.hubpath", _hubPathEdit.Text );
		cookie.Set( "tc.hubpassword", _hubPasswordEdit.Text );
		cookie.Set( "tc.hubport", _portEdit.Text );
		cookie.Set( "tc.hubroom", _roomEdit.Text );
	}

	private void LoadSettings()
	{
		var cookie = Sandbox.Internal.GlobalToolsNamespace.EditorCookie;
		_hubPathEdit.Text = cookie.Get( "tc.hubpath", "" );
		_hubPasswordEdit.Text = cookie.Get( "tc.hubpassword", "" );
		_portEdit.Text = cookie.Get( "tc.hubport", "4877" );
		_roomEdit.Text = cookie.Get( "tc.hubroom", "default" );
	}

	private string T( string key )
	{
		var loc = new Dictionary<string, Dictionary<string, string>>
		{
			["ru"] = new()
			{
				["hub_settings"] = "Настройки хаба",
				["hub_path"] = "Путь к хабу",
				["hub_path_hint"] = "путь к папке Hub",
				["hub_password"] = "Пароль хаба",
				["hub_password_hint"] = "пароль для доступа",
				["hub_password_required"] = "пароль обязателен",
				["hub_port"] = "Порт",
				["hub_room"] = "Комната",
				["reset"] = "Сброс",
				["save"] = "Сохранить",
			},
			["en"] = new()
			{
				["hub_settings"] = "Hub Settings",
				["hub_path"] = "Hub path",
				["hub_path_hint"] = "path to Hub folder",
				["hub_password"] = "Hub password",
				["hub_password_hint"] = "password for access",
				["hub_password_required"] = "password is required",
				["hub_port"] = "Port",
				["hub_room"] = "Room",
				["reset"] = "Reset",
				["save"] = "Save",
			},
		};

		if ( loc.TryGetValue( _lang, out var dict ) && dict.TryGetValue( key, out var val ) )
			return val;
		if ( loc.TryGetValue( "ru", out var fallback ) && fallback.TryGetValue( key, out var fb ) )
			return fb;
		return key;
	}

	public static void SaveToCookie( string hubPath, string hubPassword, string port, string room )
	{
		var cookie = Sandbox.Internal.GlobalToolsNamespace.EditorCookie;
		cookie.Set( "tc.hubpath", hubPath );
		cookie.Set( "tc.hubpassword", hubPassword );
		cookie.Set( "tc.hubport", port );
		cookie.Set( "tc.hubroom", room );
	}

	public static (string hubPath, string hubPassword, string port, string room) LoadFromCookie()
	{
		var cookie = Sandbox.Internal.GlobalToolsNamespace.EditorCookie;
		return (
			cookie.Get( "tc.hubpath", "" ),
			cookie.Get( "tc.hubpassword", "" ),
			cookie.Get( "tc.hubport", "4877" ),
			cookie.Get( "tc.hubroom", "default" )
		);
	}
}
