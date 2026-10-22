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
		registry.Add( "controller-fixes", "slot looks: script, document, turnaround shading, vmat export", () =>
		{
			var dir = Path.Combine( Path.GetTempPath(), "effigy-slotlook-" + Guid.NewGuid().ToString( "N" ) );
			var studio = new Effigy.PartStudio();
			var run = Effigy.BuildScript.Run( studio,
				"add Blob voxel=0.1 shapes=\"sphere 0,0,0 1 mat=1; sphere 2.5,0,0 0.6 mat=2\"\n" +
				"material 1 preset=pcb_green\n" +
				"material 2 materials/pad/led.vmat preset=led_emissive color=#00ff40 emit=#00ff40\n" +
				$"export models/pad assets={dir}", dir );
			Check( run.Ok, "script failed: " + run );
			Check( studio.MaterialLooks[1].Preset == "pcb_green" && studio.MaterialLooks[2].Glows, "looks not stored" );
			var bad = Effigy.BuildScript.Run( new Effigy.PartStudio(), "material 1 preset=chrome_moon" );
			Check( !bad.Ok && bad.Error.Contains( "pcb_green" ), "bad preset does not list presets: " + bad.Error );

			var text = Effigy.StudioDocument.Write( studio );
			Check( text.Contains( "materiallook 1 color=#1f5c32" ), "look not saved:\n" + text );
			var back = Effigy.StudioDocument.Read( text );
			Check( Effigy.StudioDocument.Write( back ) == text, "look does not round-trip" );
			Check( !Effigy.StudioDocument.Write( new Effigy.PartStudio() ).Contains( "materiallook" ), "empty document gained a line" );

			var log = new List<string>();
			var scene = Effigy.Render.Turnaround.Scene( studio, false, null, null, log );
			Check( scene.FaceMaterials.Count == 1 && scene.FaceMaterials.Values.First().Count == 2, "faces not shaded per slot" );
			Check( log.Any( l => l.Contains( "slot looks" ) ) && !log.Any( l => l.Contains( "clay" ) ), "log: " + string.Join( " | ", log ) );

			var vmat = File.ReadAllText( Path.Combine( dir, "materials/pad/led.vmat" ) );
			Check( vmat.Contains( "F_SELF_ILLUM 1" ) && vmat.Contains( "g_flMetalness" ), "vmat: " + vmat );
			Check( run.Log.Any( l => l.Contains( "slot 1 has a look but no .vmat path" ) ), "pathless look not reported" );
			Directory.Delete( dir, true );

			var clay = new Effigy.PartStudio();
			Check( Effigy.BuildScript.Run( clay, "add Blob voxel=0.1 shapes=\"sphere 0,0,0 1\"\nadd Blob voxel=0.1 shapes=\"sphere 3,0,0 1\"" ).Ok, "clay build" );
			var clayLog = new List<string>();
			Effigy.Render.Turnaround.Scene( clay, false, null, null, clayLog );
			Check( clayLog.Count( l => l.Contains( "clay" ) ) == 1, "clay log not one line: " + string.Join( " | ", clayLog ) );
		} );
	}
}
