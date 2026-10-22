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
		registry.Add( "controller-fixes", "blob surface nets stay manifold on a thin hollow shell", () =>
		{
			const string shapes = "tube 0.0000,0.0000,0.6693|0.3937,0.0000,2.0866|0.7874,0.0000,3.5827|0.9843,0.0000,4.2126 0.6693|0.7283|0.6693|0.6496; ellipsoid 1.4173,0.0000,4.7638 1.2992,0.9055,0.4724; ellipsoid 0.5512,0.0000,4.4488 0.6299,0.7480,0.4724; ellipsoid 2.4016,0.0000,5.0000 0.3937,0.4724,0.2165 mat=3; ellipsoid 0.3543,-0.3543,2.2835 0.5512,0.3937,1.1811 mat=1; ellipsoid 0.0000,0.0000,0.6299 0.5906,0.5906,0.2756 mat=1; -ellipsoid 1.1811,0.1181,5.2439 0.4921,0.4921,0.1969; -capsule 1.8110,-0.1575,4.9702 1.8110,-0.1575,5.4427 0.2087; -capsule 1.5354,-0.4724,4.9283 1.5354,-0.4724,5.4007 0.2087; -capsule 0.6693,0.2756,4.8861 0.6693,0.2756,5.3585 0.1299; -capsule 2.2441,0.0000,4.3307 2.2835,0.0000,4.5669 0.4724; -capsule -0.0394,-0.1339,0.1181 -0.0394,0.1339,0.1181 0.0827; -capsule 0.0000,0.0000,0.6693 0.3937,0.0000,2.0866 0.5906; -capsule 0.3937,0.0000,2.0866 0.7874,0.0000,3.5827 0.5906; -capsule 0.7874,0.0000,3.5827 0.9843,0.0000,4.2126 0.5709; -ellipsoid 1.4173,0.0000,4.7638 1.2205,0.8268,0.3937; -ellipsoid 0.5512,0.0000,4.4488 0.5512,0.6693,0.3937; -ellipsoid 2.4016,0.0000,5.0000 0.3150,0.3937,0.1378; -sphere 0.0000,39.3701,2.3622 39.3701";
			var studio = new Effigy.PartStudio();
			var run = Effigy.BuildScript.Run( studio, $"add Blob name=Shell voxel=0.0236 blend=0.1575 cutblend=0.0118 shapes=\"{shapes}\"" );
			Check( run.Ok, "shell failed: " + run );
			var check = Effigy.MeshValidator.Validate( studio.Bodies[0].Mesh );
			Check( check.NonManifoldEdges == 0 && check.BoundaryEdges == 0, $"shell: {check.NonManifoldEdges} non-manifold, {check.BoundaryEdges} open edges" );
		} );
		registry.Add( "controller-fixes", "blob box, cylinder and torus are exact and turn with rot=", () =>
		{
			static bool Near( float a, float b ) => MathF.Abs( a - b ) < 1e-3f;
			var turn = Effigy.Xform.FromEulerXyz( new Effigy.Vec3( 0, 0, MathF.PI / 2f ) );
			var box = new Effigy.SdfBox( Effigy.Vec3.Zero, new Effigy.Vec3( 4, 2, 2 ), 0f, turn );
			Check( Near( box.Eval( new( 0, 2, 0 ) ), 0f ) && Near( box.Eval( new( 3, 0, 0 ) ), 2f ), "box rotation" );
			var rounded = new Effigy.SdfBox( Effigy.Vec3.Zero, new Effigy.Vec3( 4, 4, 4 ), 0.5f );
			Check( Near( rounded.Eval( new( 2, 0, 0 ) ), 0f ) && rounded.Eval( new( 2, 2, 2 ) ) > 0.3f, "rounded box keeps its size, rounds corners" );
			var cyl = new Effigy.SdfCylinder( Effigy.Vec3.Zero, new Effigy.Vec3( 0, 0, 3 ), 2f );
			Check( Near( cyl.Eval( new( 0, 0, 4 ) ), 1f ) && Near( cyl.Eval( new( 3, 0, 1.5f ) ), 1f ) && Near( cyl.Eval( new( 0, 0, 1.5f ) ), -1.5f ), "cylinder caps are flat" );
			var ring = new Effigy.SdfTorus( Effigy.Vec3.Zero, 4f, 0.5f, Effigy.Xform.FromEulerXyz( new Effigy.Vec3( MathF.PI / 2f, 0, 0 ) ) );
			Check( Near( ring.Eval( new( 0, 0, 4 ) ), -0.5f ) && Near( ring.Eval( Effigy.Vec3.Zero ), 3.5f ), "torus rotation" );

			var studio = new Effigy.PartStudio();
			var run = Effigy.BuildScript.Run( studio, "add Blob voxel=0.1 blend=0.2 shapes=\"box 0,0,0 4,2,1 r=0.2 rot=0,0,30; cylinder 0,0,0 0,0,2 0.5; torus 0,0,2 1 0.2 rot=90,0,0\"" );
			Check( run.Ok, "new shapes failed: " + run );
			var mesh = studio.Bodies[0].Mesh;
			var v = Effigy.MeshValidator.Validate( mesh );
			Check( v.NonManifoldEdges == 0 && v.BoundaryEdges == 0, $"new shapes not closed: {v.NonManifoldEdges}/{v.BoundaryEdges}" );
			var top = mesh.Positions.Max( p => p.z );
			Check( MathF.Abs( top - 3.2f ) < 0.15f, $"torus stood up on its edge should reach z=3.2, reached {top}" );

			var sphereRot = Effigy.BuildScript.Run( new Effigy.PartStudio(), "add Blob shapes=\"sphere 0,0,0 1 rot=0,0,10\"" );
			Check( !sphereRot.Ok && sphereRot.Error.Contains( "rot=" ), "rot= on a sphere not refused: " + sphereRot.Error );
		} );
		registry.Add( "controller-fixes", "blob paint shapes give thin overlays their own slot", () =>
		{
			// A thin lens on a shell under a deep blend keeps its own slot (it used to fall back to slot 0).
			var lensed = new Effigy.PartStudio();
			Check( Effigy.BuildScript.Run( lensed, "add Blob voxel=0.05 blend=0.6 shapes=\"sphere 0,0,0 2 mat=1; cylinder 0,0,1.95 0,0,2.03 0.6 mat=2\"" ).Ok, "lens build" );
			bool OnTop( Effigy.PolyMesh m, Effigy.Face f, float radius )
			{
				var c = Effigy.Vec3.Zero;
				foreach ( var i in f.Indices ) c += m.Positions[i];
				c /= f.Indices.Length;
				return c.z > 1.9f && MathF.Sqrt( c.x * c.x + c.y * c.y ) < radius;
			}

			var lm = lensed.Bodies[0].Mesh;
			var lensTop = lm.Faces.Where( f => OnTop( lm, f, 0.4f ) ).ToList();
			Check( lensTop.Count > 20 && lensTop.All( f => f.Material == 2 ), $"lens lost its slot: {string.Join( " ", lensTop.GroupBy( f => f.Material ).Select( g => $"{g.Key}x{g.Count()}" ) )}" );

			// A window flush with the shell has no shape of its own: only paint can give it a slot.
			var plain = new Effigy.PartStudio();
			Check( Effigy.BuildScript.Run( plain, "add Blob voxel=0.05 shapes=\"sphere 0,0,0 2 mat=1\"" ).Ok, "plain build" );
			var painted = new Effigy.PartStudio();
			var run = Effigy.BuildScript.Run( painted, "add Blob voxel=0.05 shapes=\"sphere 0,0,0 2 mat=1; paint cylinder 0,0,1.5 0,0,2.5 0.4 mat=2; paint sphere 0,0,2 0.15 mat=4\"" );
			Check( run.Ok, "paint build failed: " + run );
			var pm = plain.Bodies[0].Mesh;
			var m = painted.Bodies[0].Mesh;
			var window = m.Faces.Where( f => OnTop( m, f, 0.35f ) ).ToList();
			Check( window.Count > 20 && window.All( f => f.Material is 2 or 4 ), $"paint left {window.Count( f => f.Material is not (2 or 4) )} window faces unpainted" );
			Check( window.Any( f => f.Material == 4 ), "later paint did not win" );
			Check( m.Faces.Count( f => f.Material == 1 ) > m.FaceCount * 9 / 10, "paint spread past its shape" );
			Check( pm.FaceCount == m.FaceCount && Effigy.MeshValidator.Validate( m ).BoundaryEdges == 0, "paint changed the skin" );

			// A deep blend used to leave its fillet unlabelled, so slot 0 showed through as a fringe.
			var deep = new Effigy.PartStudio();
			Check( Effigy.BuildScript.Run( deep, "add Blob voxel=0.04 blend=0.6 shapes=\"box 0,0,0 6,4,1 r=0.3 mat=1; torus 1.4,0,0.6 0.7 0.15 mat=3\"" ).Ok, "deep blend build" );
			var stray = deep.Bodies[0].Mesh.Faces.Count( f => f.Material == 0 );
			Check( stray == 0, $"{stray} faces of a deep blend fell back to slot 0" );

			var noMat = Effigy.BuildScript.Run( new Effigy.PartStudio(), "add Blob shapes=\"sphere 0,0,0 1; paint sphere 0,0,1 0.3\"" );
			Check( !noMat.Ok && noMat.Error.Contains( "mat=" ), "paint without mat= not refused: " + noMat.Error );
		} );
	}
}
