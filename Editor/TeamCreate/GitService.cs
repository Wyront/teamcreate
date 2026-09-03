using System.Diagnostics;

namespace Editor.TeamCreate;

public static class GitService
{
	public static event Action<string>? OnLog;

	public static async Task<string> RunAsync( string rootPath, string args, bool silent = false )
	{
		var psi = new ProcessStartInfo( "git", args )
		{
			WorkingDirectory = rootPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = System.Text.Encoding.UTF8,
			StandardErrorEncoding = System.Text.Encoding.UTF8,
		};

		using var proc = Process.Start( psi );
		if ( proc == null ) return "git not found";

		var stdout = await proc.StandardOutput.ReadToEndAsync();
		var stderr = await proc.StandardError.ReadToEndAsync();
		await proc.WaitForExitAsync();

		var output = string.IsNullOrWhiteSpace( stderr ) ? stdout : $"{stdout}\n{stderr}";
		if ( !silent )
			OnLog?.Invoke( $"git {args}: {output.Trim()}" );
		return output.Trim();
	}

	public static async Task InitLocalAsync( string projectRoot )
	{
		if ( !Directory.Exists( Path.Combine( projectRoot, ".git" ) ) )
		{
			await RunAsync( projectRoot, "init" );

			await File.WriteAllTextAsync( Path.Combine( projectRoot, ".gitignore" ),
				".vs/\n.vscode/\nobj/\nbin/\nnode_modules/\n*.user\n*.csproj.user\nLibraries/teamcreate/\n.sbox/\n" );
			OnLog?.Invoke( "Created .gitignore" );
		}

		await RunAsync( projectRoot, "add -A", true );
		await RunAsync( projectRoot, "commit -m \"Initial commit\"" );
		OnLog?.Invoke( "Local repository initialized!" );
	}

	public static async Task InitRemoteAsync( string projectRoot, string remoteUrl )
	{
		if ( !Directory.Exists( Path.Combine( projectRoot, ".git" ) ) )
		{
			await RunAsync( projectRoot, "init" );

			await File.WriteAllTextAsync( Path.Combine( projectRoot, ".gitignore" ),
				".vs/\n.vscode/\nobj/\nbin/\nnode_modules/\n*.user\n*.csproj.user\nLibraries/teamcreate/\n.sbox/\n" );
			OnLog?.Invoke( "Created .gitignore" );
		}

		var remotes = await RunAsync( projectRoot, "remote", true );
		if ( string.IsNullOrWhiteSpace( remotes ) || !remotes.Contains( "origin" ) )
			await RunAsync( projectRoot, $"remote add origin {EscapeArg( remoteUrl )}" );
		else
			OnLog?.Invoke( "Remote origin already configured" );

		await RunAsync( projectRoot, "add -A", true );

		var status = await RunAsync( projectRoot, "status --porcelain" );
		if ( !string.IsNullOrWhiteSpace( status ) )
			await RunAsync( projectRoot, "commit -m \"Initial commit\"" );
		else
			OnLog?.Invoke( "No new changes to commit" );

		var pushResult = await RunAsync( projectRoot, "push -u origin master --force" );
		OnLog?.Invoke( $"Push result: {pushResult}" );
	}

	public static async Task CommitAsync( string rootPath, string message )
	{
		await RunAsync( rootPath, "add -A", true );

		var status = await RunAsync( rootPath, "status --porcelain", true );
		if ( string.IsNullOrWhiteSpace( status ) )
		{
			OnLog?.Invoke( "No changes to commit" );
			return;
		}

		await RunAsync( rootPath, $"commit -m \"{EscapeArg( message )}\"" );
		OnLog?.Invoke( "Committed!" );
	}

	public static async Task CommitAndPushAsync( string rootPath, string message )
	{
		await RunAsync( rootPath, "add -A", true );

		var status = await RunAsync( rootPath, "status --porcelain", true );
		if ( !string.IsNullOrWhiteSpace( status ) )
		{
			await RunAsync( rootPath, $"commit -m \"{EscapeArg( message )}\"" );
			OnLog?.Invoke( "Committed!" );
		}
		else
		{
			OnLog?.Invoke( "No changes to commit, trying push..." );
		}

		var pullResult = await RunAsync( rootPath, "pull --rebase origin master" );
		OnLog?.Invoke( $"Pull: {pullResult}" );

		var pushResult = await RunAsync( rootPath, "push -u origin master" );
		OnLog?.Invoke( $"Push result: {pushResult}" );
	}

	public static async Task PullLocalAsync( string rootPath )
	{
		await RunAsync( rootPath, "fetch" );
		var result = await RunAsync( rootPath, "reset --hard @{u}" );
		OnLog?.Invoke( $"Pull local result: {result}" );
	}

	public static async Task PullRemoteAsync( string rootPath )
	{
		await RunAsync( rootPath, "fetch origin" );
		var result = await RunAsync( rootPath, "reset --hard origin/master" );
		OnLog?.Invoke( $"Pull remote result: {result}" );
	}

	private static string EscapeArg( string s ) => s.Replace( "\"", "\\\"" );

	public static int CopyProjectFiles( string source, string target )
	{
		int count = 0;

		foreach ( var file in Directory.GetFiles( source, "*", SearchOption.AllDirectories ) )
		{
			var rel = Path.GetRelativePath( source, file );
			var parts = rel.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );

			if ( parts.Any( p => p.Equals( ".git", StringComparison.OrdinalIgnoreCase ) ||
								 p.Equals( ".vs", StringComparison.OrdinalIgnoreCase ) ||
								 p.Equals( ".vscode", StringComparison.OrdinalIgnoreCase ) ||
								 p.Equals( "obj", StringComparison.OrdinalIgnoreCase ) ||
								 p.Equals( "bin", StringComparison.OrdinalIgnoreCase ) ||
								 p.Equals( "node_modules", StringComparison.OrdinalIgnoreCase ) ||
								 p.EndsWith( ".slnx", StringComparison.OrdinalIgnoreCase ) ) )
				continue;

			if ( parts.Length > 1 && parts[0].Equals( "Libraries", StringComparison.OrdinalIgnoreCase ) )
			{
				if ( parts.Length > 2 && parts[1].Equals( "teamcreate", StringComparison.OrdinalIgnoreCase ) )
					continue;
			}

			var dest = Path.Combine( target, rel );
			var dir = Path.GetDirectoryName( dest );
			if ( dir != null ) Directory.CreateDirectory( dir );
			File.Copy( file, dest, true );
			count++;
		}

		return count;
	}
}
