using Marionette.Desktop;

// Fixes found while modelling a hard-surface controller live (headless camera, Blob, materials, ...).
internal static class ControllerFixTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "controller-fixes", "headless options resolve paths and camera flags", () =>
		{
			string cwd = Directory.GetCurrentDirectory();
			var o = DesktopOptions.Parse( new[] { "--headless", "--model", "a/b.effigy", "--screenshot", "shot.png", "--yaw", "90", "--pitch", "-20", "--turntable", "--wireframe" } );
			Check( o.Model == Path.Combine( cwd, "a", "b.effigy" ), $"model not absolute: {o.Model}" );
			Check( o.Screenshot == Path.Combine( cwd, "shot.png" ), $"screenshot not absolute: {o.Screenshot}" );
			var cam = new OrbitCamera();
			cam.ApplyStartView( o.Model, o.Iso, o.Yaw, o.Pitch );
			Check( cam.YawDegrees == 90 && cam.PitchDegrees == -20, $"yaw/pitch not applied: {cam.YawDegrees},{cam.PitchDegrees}" );
			cam.ApplyStartView( "x.fbx", true, null, null );
			Check( MathF.Abs( cam.YawDegrees + 38.66f ) < 1e-3f, "iso lost" );
			Check( HeadlessRender.TurntablePath( "/t/s.png", 3 ) == "/t/s_003.png", "turntable frame name" );
		} );
		registry.Add( "controller-fixes", "blob thin shell survives default cut blend; big blend names the fix", () =>
		{
			const string shell = "capsule 0,0,0 0,0,4 0.7; -capsule 0,0,0 0,0,4 0.62";
			var ok = Effigy.BuildScript.Run( new Effigy.PartStudio(), $"add Blob voxel=0.05 shapes=\"{shell}\"" );
			Check( ok.Ok, "thin shell failed at default cut blend: " + ok );
			var bad = Effigy.BuildScript.Run( new Effigy.PartStudio(), $"add Blob voxel=0.05 cutblend=0.5 shapes=\"{shell}\"" );
			Check( !bad.Ok && bad.Error.Contains( "Cut blend" ) && bad.Error.Contains( "cutblend=" ), "diagnostic does not name cut blend: " + bad.Error );
		} );
	}
}
