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

		var pushResult = await RunAsync( projectRoot, "push -u origin HEAD --force" );
		OnLog?.Invoke( $"Push result: {pushResult}" );
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

		var pullResult = await RunAsync( rootPath, "pull --rebase" );
		OnLog?.Invoke( $"Pull: {pullResult}" );

		var pushResult = await RunAsync( rootPath, "push -u origin HEAD" );
		OnLog?.Invoke( $"Push result: {pushResult}" );
	}

	public static async Task PullRemoteAsync( string rootPath )
	{
		await RunAsync( rootPath, "fetch origin" );
		var branch = await CurrentBranchAsync( rootPath );
		var result = await RunAsync( rootPath, $"reset --hard origin/{branch}" );
		OnLog?.Invoke( $"Pull remote result: {result}" );
	}

	private static async Task<string> CurrentBranchAsync( string rootPath )
	{
		var branch = (await RunAsync( rootPath, "branch --show-current", true )).Trim();
		if ( string.IsNullOrWhiteSpace( branch ) ) return "master";
		return branch.Split( '\n', '\r' )[0].Trim();
	}

	private static string EscapeArg( string s ) => s.Replace( "\"", "\\\"" );
}
