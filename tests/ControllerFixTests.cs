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
			Check( run.Log.Any( l => l.Contains( "slot 1 has no explicit .vmat path; using a model-local look sidecar" ) ), "pathless look not reported" );
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
		registry.Add( "controller-fixes", "spline band and strap part follow a path, face turning with it", () =>
		{
			const string pts = "0,-0.8,5;0,-1.8,4.6;0,-2,3;0,-1.7,1.4;0,-0.6,0.8";
			var studio = new Effigy.PartStudio();
			var run = Effigy.BuildScript.Run( studio,
				$"add Spline name=Path tube=0 points={pts}\n" +
				$"add Spline name=Band profile=band width=0.8 thickness=0.1 points={pts}\n" +
				$"add Spline name=Edgeon profile=band width=0.8 thickness=0.1 bandfaces=1,0,0 points={pts}\n" +
				"add Part name=Strap part=Strap along=Path size=0.8 thickness=0.1 position=50,0,0" );
			Check( run.Ok, "band script failed: " + run );

			Effigy.PolyMesh Body( string n ) => studio.Bodies.First( b => b.Name == n ).Mesh;
			foreach ( var n in new[] { "Band", "Edgeon", "Strap" } )
			{
				var v = Effigy.MeshValidator.Validate( Body( n ) );
				Check( v.NonManifoldEdges == 0 && v.BoundaryEdges == 0, $"{n} not closed: {v.NonManifoldEdges}/{v.BoundaryEdges}" );
			}

			// A curve in the YZ plane: the band's width lies across it (x), its face looks along the bend.
			var band = Body( "Band" );
			var wx = band.Positions.Max( p => MathF.Abs( p.x ) );
			Check( MathF.Abs( wx - 0.4f ) < 1e-3f, $"band width not across the plane: |x| max {wx}" );
			Check( band.Positions.Min( p => p.y ) < -1.9f && band.Positions.Min( p => p.y ) > -2.15f, "band left its path" );

			var edge = Body( "Edgeon" );
			var ex = edge.Positions.Max( p => MathF.Abs( p.x ) );
			Check( MathF.Abs( ex - 0.05f ) < 1e-3f, $"band faces=1,0,0 should put the thickness on x: |x| max {ex}" );

			var strap = Body( "Strap" );
			Check( strap.Positions.Count == band.Positions.Count && strap.Positions.Zip( band.Positions ).All( q => (q.First - q.Second).Length < 1e-4f ),
				"strap part along a path is not the band (Position must not move it)" );

			var bad = Effigy.BuildScript.Run( new Effigy.PartStudio(), $"add Spline name=P tube=0 points={pts}\nadd Part part=Cog along=P" );
			Check( !bad.Ok && bad.Error.Contains( "Strap" ), "cog along a path not refused: " + bad.Error );
		} );
		registry.Add( "controller-fixes", "clearance gives the gap, or the shared volume even with one part sunk inside", () =>
		{
			var studio = new Effigy.PartStudio();
			Check( Effigy.BuildScript.Run( studio,
				"add Primitive name=A shape=Box sizex=2 sizey=2 sizez=2\n" +
				"add Primitive name=Far shape=Box sizex=2 sizey=2 sizez=2 position=3,0,0\n" +
				"add Primitive name=Half shape=Box sizex=2 sizey=2 sizez=2 position=1.5,0,0.5\n" +
				"add Primitive name=Sunk shape=Box sizex=0.5 sizey=0.5 sizez=0.5 position=-0.3,0.2,0" ).Ok, "boxes" );
			Effigy.Body B( string n ) => Effigy.ModelMeasure.FindBody( studio, n );

			var far = Effigy.Clearance.Measure( B( "A" ), B( "Far" ) );
			Check( !far.Overlaps && MathF.Abs( far.Gap - 1f ) < 1e-3f, $"gap A-Far {far.Gap}" );

			var half = Effigy.Clearance.Measure( B( "A" ), B( "Half" ) );
			Check( half.Crossings > 0 && MathF.Abs( half.Overlap - 0.5f * 2f * 1.5f ) < 0.03f, $"overlap A-Half {half.Overlap}" );

			var sunk = Effigy.Clearance.Measure( B( "A" ), B( "Sunk" ) );
			Check( sunk.Crossings == 0 && MathF.Abs( sunk.Overlap - 0.125f ) < 0.005f && sunk.Describe().Contains( "wholly inside" ), $"sunk: {sunk.Describe()}" );

			// Flat on A's top face: crossings on the contact, nothing shared. That is touching.
			Check( Effigy.BuildScript.Run( studio, "add Primitive name=OnTop shape=Box sizex=0.5 sizey=0.5 sizez=0.5 position=0,0,1.25" ).Ok, "on top" );
			var onTop = Effigy.Clearance.Measure( B( "A" ), B( "OnTop" ) );
			Check( !onTop.Overlaps && onTop.Describe().Contains( "touching" ), "flat contact: " + onTop.Describe() );
			Check( Effigy.BuildScript.Run( studio, "remove OnTop" ).Ok, "remove" );

			var run = Effigy.BuildScript.Run( studio, "clearance A Far min=1.5\nclearance" );
			Check( run.Ok, "clearance script: " + run.Error );
			Check( run.Log[0].StartsWith( "A x Far!" ) && run.Log[0].Contains( "under min=1.5" ), "min not flagged: " + run.Log[0] );
			Check( run.Log.Count == 1 + 6, $"every pair of 4 bodies is 6 lines, got {run.Log.Count - 1}" );
			Check( !Effigy.BuildScript.Run( studio, "clearance A" ).Ok && !Effigy.BuildScript.Run( studio, "clearance A A" ).Ok, "one name / same body accepted" );
		} );
		registry.Add( "controller-fixes", "live script run: a revision per add/set/edit line, paced, cancellable, cabinet-readable", () =>
		{
			string Root() => Path.Combine( Path.GetTempPath(), "effigy-scriptlive-" + Guid.NewGuid().ToString( "N" ) );
			const string script =
				"# a comment, then three changes with a measure between\n" +
				"add Primitive shape=Box sizex=2 sizey=2 sizez=2 name=Base\n" +
				"describe\n" +
				"set Base sizex=3\n" +
				"add Primitive shape=Sphere name=Ball position=3,0,0\n";
			Check( Effigy.BuildScript.Mutates( "add" ) && Effigy.BuildScript.Mutates( "SET" ) && !Effigy.BuildScript.Mutates( "describe" ), "verb classes" );
			Check( Effigy.BuildScript.VerbOf( "  edit Base x=1" ) == "edit" && Effigy.BuildScript.VerbOf( "# no" ) is null, "VerbOf" );

			// Every non-caption operation is saved, including describe; only mutations are paced.
			var root = Root();
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var done = ScriptLive.Execute( new Effigy.PartStudio(), script, root, root, 0.25, new CancellationTokenSource() );
			clock.Stop();
			Check( done.State == "completed" && done.Result.Ok, $"run: {done.State} {done.Result}" );
			Check( done.Revisions == 5, $"initial + 4 non-caption operations = 5 revisions, got {done.Revisions}" );
			Check( clock.Elapsed.TotalSeconds >= 0.45, $"pace 0.25 not honoured between 3 changes: {clock.Elapsed.TotalSeconds:0.00}s" );
			var revisions = Directory.GetFiles( Path.Combine( root, ".live/revisions" ), "*.effigy" ).OrderBy( p => p ).ToArray();
			Check( revisions.Length == 5, $"revision files {revisions.Length}" );
			var counts = revisions.Select( p => Effigy.StudioDocument.Read( File.ReadAllText( p ) ).Features.Count ).ToArray();
			Check( string.Join( ",", counts ) == "0,1,1,1,2", $"features per revision {string.Join( ",", counts )}" );
			var latest = AiLiveRun.ReadLatest( root )!;
			Check( latest["state"]!.GetValue<string>() == "completed" && latest["snapshot"]!["revision"]!.GetValue<long>() == 5, "journal end: " + latest.ToJsonString() );
			var lines = File.ReadAllLines( Path.Combine( root, ".live/events.jsonl" ) );
			Check( lines.Count( l => l.Contains( "\"state\":\"revision\"" ) ) == 5, "revision events" );
			Check( lines.Any( l => l.Contains( "line 4: set Base" ) ), "operation names the line" );
			Check( lines.Any( l => l.Contains( "\"paceSeconds\":0.25" ) ), "starting pace not published for the cabinet's pace control" );

			// A failing line ends the run failed, keeping the revisions before it.
			var bad = Root();
			var failed = ScriptLive.Execute( new Effigy.PartStudio(), "add Primitive shape=Box name=A\nset Nope x=1\n", bad, bad, 0, new CancellationTokenSource() );
			Check( failed.State == "failed" && failed.Revisions == 2 && failed.Result.ErrorLine == 2, $"failed run: {failed.State} {failed.Revisions} {failed.Result}" );
			Check( AiLiveRun.ReadLatest( bad )!["state"]!.GetValue<string>() == "failed", "failed state" );

			// The cabinet's cancel (a command file) stops a slow run at its next change.
			var slow = Root();
			var cancel = new CancellationTokenSource();
			var task = Task.Run( () => ScriptLive.Execute( new Effigy.PartStudio(), script, slow, slow, 5, cancel ) );
			string? runId = null;
			for ( int i = 0; i < 400 && runId is null; i++ )
			{
				var s = AiLiveRun.ReadLatest( slow );
				if ( s?["snapshot"]?["revision"]?.GetValue<long>() >= 2 ) runId = s["runId"]!.GetValue<string>();
				else Thread.Sleep( 10 );
			}
			Check( runId is not null, "slow run never committed its first change" );
			var body = new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1, ["runId"] = runId, ["id"] = "test-cancel", ["command"] = "cancel" };
			Directory.CreateDirectory( Path.Combine( slow, ".live/commands" ) );
			AiLiveRun.AtomicWrite( Path.Combine( slow, ".live/commands/test-cancel.json" ), body.ToJsonString() );
			Check( task.Wait( 3000 ), "cancel did not stop the paced run" );
			Check( task.Result.State == "cancelled" && task.Result.Revisions is 2 or 3
				&& task.Result.Studio.Features.Count == 1 && task.Result.Studio.Features[0].Name == "Base",
				$"cancelled run must keep Base but never execute the next mutation: {task.Result.State} {task.Result.Revisions}" );
			Check( AiLiveRun.ReadLatest( slow )!["state"]!.GetValue<string>() == "cancelled", "cancelled state" );
		} );
		registry.Add( "controller-fixes", "open script run: stays up for more lines, a bad line holds it, fix/skip/rewind/close, idle close", () =>
		{
			string Root() => Path.Combine( Path.GetTempPath(), "effigy-scriptopen-" + Guid.NewGuid().ToString( "N" ) );
			string Answer( string root, string name )
			{
				string path = Path.Combine( root, ".live/answers", name + ".txt" );
				for ( int i = 0; i < 1000 && !File.Exists( path ); i++ ) Thread.Sleep( 10 );
				Check( File.Exists( path ), "no answer to " + name );
				return File.ReadAllText( path );
			}
			string State( string root ) => AiLiveRun.ReadLatest( root )!["state"]!.GetValue<string>();
			string More( string root, string kind, string text = "" ) => Answer( root, Inbox.Put( root, kind, text ) );

			var root = Root();
			var task = Task.Run( () => ScriptLive.Execute( new Effigy.PartStudio(),
				"add Primitive shape=Box name=Base\nadd Primitive shape=Sphere name=B position=3,0,0\n", root, root, 0, new CancellationTokenSource(), open: true, idleMinutes: 1 ) );
			string start = Answer( root, Inbox.Start );
			Check( start.Contains( "added Sphere 'B'" ) && start.Contains( "run is open" ), "start answer: " + start );
			Check( State( root ) == "open" && !task.IsCompleted, "the run should wait, not end: " + State( root ) );

			// More lines go into the same document, numbered after the script's own.
			string set = More( root, "txt", "set Base sizex=5" );
			Check( set.Contains( "set Base" ) && set.Contains( "run is open" ), "set answer: " + set );
			Check( File.ReadAllLines( Path.Combine( root, ".live/events.jsonl" ) ).Any( l => l.Contains( "line 3: set Base" ) ), "line 3 not named" );

			// A bad line holds the run; the line before it stays, the line after it waits.
			string held = More( root, "txt", "add Primitive shape=Box name=C\nset Nope x=1\nadd Primitive shape=Sphere name=D\n" );
			Check( held.Contains( "ERROR line 5" ) && held.Contains( "held at line 5" ) && held.Contains( "1 line(s)" ), "held answer: " + held );
			Check( State( root ) == "held" && !task.IsCompleted, "held state: " + State( root ) );

			// The fix runs first, then the waiting line.
			string fixd = More( root, "txt", "add Primitive shape=Sphere name=E" );
			Check( fixd.Contains( "'E'" ) && fixd.Contains( "'D'" ) && fixd.IndexOf( "'E'" ) < fixd.IndexOf( "'D'" ) && fixd.Contains( "run is open" ), "fix answer: " + fixd );

			// skip drops the waiting lines.
			Check( More( root, "txt", "set Nope y=1\nadd Primitive shape=Box name=Z" ).Contains( "held" ), "second hold" );
			Check( More( root, "skip" ).Contains( "run is open" ), "skip answer" );

			string closed = More( root, "close" );
			Check( closed.Contains( "run completed" ) && task.Wait( 3000 ), "close: " + closed );
			var names = task.Result.Studio.Features.Select( f => f.Name ).ToArray();
			Check( string.Join( ",", names ) == "Base,B,C,E,D", "features: " + string.Join( ",", names ) );
			Check( task.Result.State == "completed" && State( root ) == "completed", "closed state " + task.Result.State );
			var last = Effigy.StudioDocument.Read( File.ReadAllText( Directory.GetFiles( Path.Combine( root, ".live/revisions" ), "*.effigy" ).OrderBy( p => p ).Last() ) );
			Check( last.Features.Count == 5, "last revision has " + last.Features.Count );
			Check( File.ReadAllText( Path.Combine( root, "script.txt" ) ).Split( '\n' ).Length >= 9, "script.txt keeps every line given" );

			// A script that fails at once still answers, and an open run left alone closes itself.
			var idle = Root();
			var lonely = Task.Run( () => ScriptLive.Execute( new Effigy.PartStudio(), "set Nope x=1\n", idle, idle, 0, new CancellationTokenSource(), open: true, idleMinutes: 0.01 ) );
			Check( Answer( idle, Inbox.Start ).Contains( "held at line 1" ), "held at start" );
			Check( lonely.Wait( 5000 ) && lonely.Result.State == "failed", "idle close: " + (lonely.IsCompleted ? lonely.Result.State : "still open") );

			// Rewind: an earlier step becomes the model again (as a new revision), held lines are dropped,
			// and lines sent after it build on that step.
			var back = Root();
			var rewound = Task.Run( () => ScriptLive.Execute( new Effigy.PartStudio(),
				"add Primitive shape=Box name=A\nadd Primitive shape=Box name=B position=3,0,0\nadd Primitive shape=Box name=C position=6,0,0\n", back, back, 0, new CancellationTokenSource(), open: true, idleMinutes: 1 ) );
			Answer( back, Inbox.Start );
			Check( More( back, "txt", "set Nope x=1\nadd Primitive shape=Box name=W" ).Contains( "held" ), "held before rewind" );
			string rw = More( back, "rewind", "3" );       // revision 1 is the empty start, 3 is after B
			Check( rw.Contains( "rewound to step 3" ) && rw.Contains( "run is open" ), "rewind answer: " + rw );
			Check( State( back ) == "open", "rewind clears the hold: " + State( back ) );
			Check( More( back, "rewind", "99" ).Contains( "no step '99'" ), "a missing step rewinds nothing" );
			More( back, "txt", "add Primitive shape=Sphere name=F position=0,4,0" );
			More( back, "close" );
			Check( rewound.Wait( 3000 ), "rewind run closes" );
			var after = rewound.Result.Studio.Features.Select( f => f.Name ).ToArray();
			Check( string.Join( ",", after ) == "A,B,F", "after rewind: " + string.Join( ",", after ) );
			Check( File.ReadAllText( Path.Combine( back, ".live/events.jsonl" ) ).Contains( "rewind to step 3" ), "the rewind is a revision of its own" );

			// Pause mid-script holds the unrun tail. An edit sent while held runs before that tail, not after it.
			var heldRun = Root();
			var pausing = Task.Run( () => ScriptLive.Execute( new Effigy.PartStudio(),
				"add Primitive shape=Box name=Base\nadd Primitive shape=Box name=Tail position=6,0,0\n", heldRun, heldRun, 0.6, new CancellationTokenSource(), open: true, idleMinutes: 1 ) );
			for ( int i = 0; i < 400 && (AiLiveRun.ReadLatest( heldRun )?["snapshot"]?["revision"]?.GetValue<long>() ?? 0) < 2; i++ ) Thread.Sleep( 25 );
			string runId = AiLiveRun.ReadLatest( heldRun )!["runId"]!.GetValue<string>();
			string pauseId = Guid.NewGuid().ToString( "N" );
			Directory.CreateDirectory( Path.Combine( heldRun, ".live/commands" ) );
			AiLiveRun.AtomicWrite( Path.Combine( heldRun, ".live/commands", pauseId + ".json" ),
				new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1, ["runId"] = runId, ["id"] = pauseId, ["command"] = "pause" }.ToJsonString() );
			for ( int i = 0; i < 400 && AiLiveRun.ReadLatest( heldRun )!["control"]?["paused"]?.GetValue<bool>() != true; i++ ) Thread.Sleep( 25 );
			Check( AiLiveRun.ReadLatest( heldRun )!["control"]?["paused"]?.GetValue<bool>() == true, "pause did not hold the script" );
			Check( !Directory.GetFiles( Path.Combine( heldRun, ".live/revisions" ) ).Any( p => File.ReadAllText( p ).Contains( "name=Tail" ) ), "tail ran through the pause" );
			string edit = Inbox.Put( heldRun, "txt", "add Primitive shape=Sphere name=Edit position=0,3,0\n" );
			Thread.Sleep( 400 );
			Check( !File.Exists( Path.Combine( heldRun, ".live/answers", edit + ".txt" ) ), "the edit ran before resume, ahead of the hold" );
			string resumeId = Guid.NewGuid().ToString( "N" );
			AiLiveRun.AtomicWrite( Path.Combine( heldRun, ".live/commands", resumeId + ".json" ),
				new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1, ["runId"] = runId, ["id"] = resumeId, ["command"] = "resume" }.ToJsonString() );
			string editAnswer = Answer( heldRun, edit );
			Check( editAnswer.IndexOf( "'Edit'" ) >= 0 && editAnswer.IndexOf( "'Edit'" ) < editAnswer.IndexOf( "'Tail'" ), "edit did not land before the paused tail: " + editAnswer );
			Inbox.Put( heldRun, "close" );
			Check( pausing.Wait( 5000 ), "paused run did not close" );

			// Without --open nothing changed: a failure ends the run.
			var once = Root();
			Check( ScriptLive.Execute( new Effigy.PartStudio(), "set Nope x=1\n", once, once, 0, new CancellationTokenSource() ).State == "failed"
				&& !Directory.Exists( Path.Combine( once, ".live/answers" ) ), "a closed run answers nothing" );
		} );
		registry.Add( "controller-fixes", "clip plane cuts renders open: welded section, shaded half, script render and turnaround", () =>
		{
			Check( Effigy.ClipPlane.TryParse( "y>0", out var keepLeft, out _ ) && keepLeft.Keeps( new( 0, 1, 0 ) ) && !keepLeft.Keeps( new( 0, -1, 0 ) ), "y>0" );
			Check( Effigy.ClipPlane.TryParse( "x <= 1.5", out var below, out _ ) && below.Keeps( new( 1, 0, 0 ) ) && !below.Keeps( new( 2, 0, 0 ) ) && below.Keeps( new( 1.5f, 0, 0 ) ), "x<=1.5" );
			Check( !Effigy.ClipPlane.TryParse( "q>0", out _, out var badAxis ) && badAxis.Contains( "y>0" ), "bad axis: " + badAxis );
			Check( !Effigy.ClipPlane.TryParse( "z>abc", out _, out var badNum ) && badNum.Contains( "not a number" ), "bad number: " + badNum );

			var studio = new Effigy.PartStudio();
			Check( Effigy.BuildScript.Run( studio, "add Blob name=Ball voxel=0.1 shapes=\"sphere 0,0,1 1\"" ).Ok, "ball" );
			var ball = Effigy.ModelMeasure.FindBody( studio, "Ball" ).Mesh;
			var half = Effigy.MeshClip.Section( ball, keepLeft );
			Check( half.Positions.All( p => p.y >= -1e-4f ), "a kept vertex is on the cut side" );
			Check( half.Positions.Count < ball.Positions.Count * 0.7f && half.Faces.Count > ball.Faces.Count * 0.4f, $"not welded: {half.Positions.Count} verts for {half.Faces.Count} faces" );
			var open = Effigy.MeshValidator.Validate( half );
			Check( open.NonManifoldEdges == 0 && open.BoundaryEdges > 0, $"section should be one open rim: {open}" );

			// Shaded: from the front (+x) the +y half is on the right of the frame, so y>0 leaves the left empty.
			int Covered( Effigy.ClipPlane? clip, bool left )
			{
				var scene = new Effigy.Render.ShadeScene { Floor = false, Clip = clip, Coverage = 1 };
				scene.Add( ball, Effigy.Render.ShadeMaterial.Clay() );
				scene.StudioLights( new Effigy.Vec3( -1, 0, 0 ) );
				var empty = new Effigy.Render.ShadeScene { Floor = false, Coverage = 1 };
				var camera = new Effigy.Render.ShadeCamera( new Effigy.Vec3( 6, 0, 1 ), new Effigy.Vec3( 0, 0, 1 ), 30f );
				var img = Effigy.Render.ShadedRender.Render( scene, camera, 64, 64 );
				var bg = Effigy.Render.ShadedRender.Render( empty, camera, 64, 64 );
				var n = 0;
				for ( var y = 0; y < 64; y++ )
					for ( var x = left ? 0 : 34; x < (left ? 30 : 64); x++ )
						if ( img.Pixels[y * 64 + x] != bg.Pixels[y * 64 + x] ) n++;
				return n;
			}
			Check( Covered( null, true ) > 200 && Covered( keepLeft, true ) == 0, $"left half not cut away: {Covered( keepLeft, true )} px" );
			Check( Covered( keepLeft, false ) > 200, "kept half not drawn" );

			var dir = Path.Combine( Path.GetTempPath(), "effigy-clip-test" );
			var run = Effigy.BuildScript.Run( studio, $"render {dir}/shaded.png shade=clay views=1 size=64 clip=y>0\nrender {dir}/wire.png view=front size=64 clip=y>0\nturnaround {dir}/turn size=64 views=front clip=z<1" );
			Check( run.Ok, "script clip: " + run.Error );
			Check( !Effigy.BuildScript.Run( studio, $"render {dir}/bad.png shade=clay clip=q>0" ).Ok, "bad clip accepted" );
			Check( Effigy.ModelMeasure.FindBody( studio, "Ball" ).Mesh == ball, "render clip changed the body" );
			var opts = DesktopOptions.Parse( new[] { "--headless", "--section", "x<0.5" } );
			Check( opts.Section is { } sec && sec.Keeps( new( 0, 0, 0 ) ) && !sec.Keeps( new( 1, 0, 0 ) ), "--section" );
		} );
		registry.Add( "controller-fixes", "surface landmark finds the skin and its normal from any side", () =>
		{
			var studio = new Effigy.PartStudio();
			Check( Effigy.BuildScript.Run( studio, "add Blob name=Shell voxel=0.04 shapes=\"sphere 0,0,0 2\"" ).Ok, "shell build" );
			bool Land( string text, out Effigy.Vec3 p ) => Effigy.BuildScript.Point( studio, text, out p, out _ );

			Check( Land( "@Shell.surface(1,0,+z)", out var top ) && MathF.Abs( top.x - 1f ) < 1e-3f && MathF.Abs( top.z - MathF.Sqrt( 3f ) ) < 0.03f, $"top hit {top}" );
			Check( Land( "@Shell.surface(1,0,+z).normal", out var n ) && (n - new Effigy.Vec3( 0.5f, 0f, 0.866f )).Length < 0.06f, $"top normal {n}" );
			Check( Land( "@Shell.surface(0,-y,0.5)", out var side ) && MathF.Abs( side.y + MathF.Sqrt( 3.75f ) ) < 0.03f, $"-y hit {side}" );
			Check( Land( "@Shell.surface(0,-y,0).normal", out var sn ) && sn.y < -0.98f, $"-y normal faces outward: {sn}" );
			Check( Land( "@Shell.surface(+x,0,0)+0,0,1", out var off ) && MathF.Abs( off.x - 2f ) < 0.03f && MathF.Abs( off.z - 1f ) < 1e-3f, $"offset after brackets {off}" );

			Check( !Effigy.BuildScript.Point( studio, "@Shell.surface(5,0,+z)", out _, out var miss ) && miss.Contains( "misses" ) && miss.Contains( "bounds" ), "miss: " + miss );
			Check( !Effigy.BuildScript.Point( studio, "@Shell.surface(0,0,0)", out _, out var noSide ) && noSide.Contains( "side" ), "no side: " + noSide );
			Check( !Effigy.BuildScript.Point( studio, "@Shell.surface(+y,0,0)", out _, out _ ), "+y in the x place accepted" );

			// Script commands take it like any landmark.
			var run = Effigy.BuildScript.Run( studio, "measure @Shell.surface(0,0,+z)" );
			Check( run.Ok, "measure surface: " + run.Error );
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
