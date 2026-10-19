using System.Diagnostics;
using System.IO;

internal static class InstalledArtifactSmokeTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		string root = new[]
		{
			Directory.GetCurrentDirectory(),
			Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "../../../../" ) ),
			"/home/poo/games/projects/geppetto-desktop"
		}.FirstOrDefault( dir => File.Exists( Path.Combine( dir, "packaging", "package-linux.sh" ) ) )
			?? throw new FileNotFoundException( "Cannot locate packaging scripts" );
		string package = Path.Combine( root, "packaging", "package-linux.sh" );
		string smoke = Path.Combine( root, "packaging", "smoke-test.sh" );
		string dist = Path.Combine( settings.WorkDirectory, "D3 distribution with spaces" );
		string evidence = Path.Combine( settings.WorkDirectory, "D3 installed evidence" );
		bool ran = false;

		void Run( string script, params string[] arguments )
		{
			var psi = new ProcessStartInfo( "/bin/bash" )
			{
				RedirectStandardOutput = true, RedirectStandardError = true,
				UseShellExecute = false, WorkingDirectory = settings.WorkDirectory
			};
			psi.ArgumentList.Add( script );
			foreach ( string argument in arguments ) psi.ArgumentList.Add( argument );
			psi.Environment.Remove( "DISPLAY" );
			psi.Environment.Remove( "WAYLAND_DISPLAY" );
			using var process = Process.Start( psi ) ?? throw new IOException( "Could not run " + script );
			string stdout = process.StandardOutput.ReadToEnd();
			string stderr = process.StandardError.ReadToEnd();
			process.WaitForExit();
			if ( process.ExitCode != 0 ) throw new Exception( $"{script} exited {process.ExitCode}: {stdout}\n{stderr}" );
		}

		void EnsureSmoke()
		{
			if ( ran ) return;
			Run( package, dist );
			Run( smoke, Path.Combine( dist, "geppetto-desktop-linux-x64.tar.gz" ), evidence );
			ran = true;
		}

		registry.Add( "installed-artifact", "smoke script exists and installed archive renders", () =>
		{
			if ( !File.Exists( smoke ) ) throw new Exception( "Missing installed-artifact smoke script" );
			EnsureSmoke();
			if ( !File.Exists( Path.Combine( evidence, "pixels.sha256" ) ) )
				throw new Exception( "Installed rendering did not validate decoded pixels" );
		} );
		registry.Add( "installed-artifact", "native clip and bad input have recorded diagnostics", () =>
		{
			EnsureSmoke();
			string clip = File.ReadAllText( Path.Combine( evidence, "clip.log" ) );
			string bad = File.ReadAllText( Path.Combine( evidence, "bad-input.log" ) );
			if ( !clip.Contains( "loaded   :" ) || !bad.Contains( "Model file not found" ) )
				throw new Exception( "Installed clip success or invalid-input diagnostic missing" );
		} );
		registry.Add( "installed-artifact", "repeat capture is identical and installation stays untouched", () =>
		{
			EnsureSmoke();
			if ( !File.ReadAllBytes( Path.Combine( evidence, "render-1.png" ) )
				.SequenceEqual( File.ReadAllBytes( Path.Combine( evidence, "render-2.png" ) ) ) )
				throw new Exception( "Installed captures differ" );
			if ( !File.Exists( Path.Combine( evidence, "dependencies.log" ) ) ||
				!File.Exists( Path.Combine( evidence, "host.log" ) ) )
				throw new Exception( "Host/dependency evidence missing" );
		} );
	}
}
