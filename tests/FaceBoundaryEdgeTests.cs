using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

// Two s&box-port gaps on the bench, both through the build script so script.txt replays them:
// 1. faces on Fillet/Chamfer = each face's surface rim (Feature.ApplyGeometrySelection → FacePlane.CaptureBoundary);
// 2. `facematerial slot=N faces=…` is a BuildScript verb (FaceMaterialEdit on the history), so a saved bench
//    script rebuilds with the same per-face slots.
internal static class FaceBoundaryEdgeTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }

	static Body A( PartStudio studio ) => studio.Bodies.Single( b => b.Name == "A" );

	static (Vec3 Min, Vec3 Max) Box( PartStudio studio )
	{
		var ps = A( studio ).Mesh.Positions;
		return (new Vec3( ps.Min( p => p.x ), ps.Min( p => p.y ), ps.Min( p => p.z ) ), new Vec3( ps.Max( p => p.x ), ps.Max( p => p.y ), ps.Max( p => p.z ) ));
	}

	static string F( float v ) => v.ToString( "0.###", System.Globalization.CultureInfo.InvariantCulture );
	static string P( float x, float y, float z ) => $"{F( x )},{F( y )},{F( z )}";

	static Bench.Session NewBench( string root, List<JsonObject> events )
	{
		var bench = new Bench.Session( root, new PartStudio(), "box", events.Add );
		Check( bench.Apply( "add Primitive name=A width=20 depth=20 height=20" ), "box add failed: " + events[^1].ToJsonString() );
		return bench;
	}

	// a quantised, order-free fingerprint of a mesh's vertices, to compare two builds
	static string Shape( PartStudio studio ) => string.Join( ";", A( studio ).Mesh.Positions
		.Select( p => P( MathF.Round( p.x, 2 ), MathF.Round( p.y, 2 ), MathF.Round( p.z, 2 ) ) ).Distinct().OrderBy( s => s, StringComparer.Ordinal ) );

	static int[] Slots( PartStudio studio ) => A( studio ).Mesh.Faces.Select( f => f.Material ).ToArray();

	static PartStudio Replay( string scriptPath, string baseDir )
	{
		var studio = new PartStudio();
		var result = BuildScript.Run( studio, File.ReadAllText( scriptPath ), baseDir );
		Check( result.Ok, $"replay failed at line {result.ErrorLine}: {result.Error}" );
		return studio;
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		string NewDir( string tag ) => Path.Combine( settings.WorkDirectory, $"face-boundary-{tag}-" + Guid.NewGuid().ToString( "N" ) );

		registry.Add( "face-boundary", "top face token → Fillet rounds exactly its 4 rim edges, and replays", () =>
		{
			var events = new List<JsonObject>();
			var root = NewDir( "fillet" );
			var bench = NewBench( root, events );
			var (lo, hi) = Box( bench.Studio );
			float cx = (lo.x + hi.x) / 2, cy = (lo.y + hi.y) / 2, r = 2;
			string top = $"A:{P( cx, cy, hi.z )}:0,0,1";   // what a viewport face pick sends (SubSelection faces token)

			Check( bench.Apply( $"add Fillet name=Round faces={top} radius={F( r )}" ), "fillet via faces failed: " + events[^1].ToJsonString() );

			// the feature holds the top face's rim: four horizontal edges at the top, one per side
			var fillet = (FilletFeature)bench.Studio.Features.Single( f => f.Name == "Round" );
			Check( fillet.Edges.Count == 4, "fillet edges: " + fillet.Edges.Count );
			Check( fillet.Edges.All( e => MathF.Abs( e.Point.z - hi.z ) < 1e-3f && MathF.Abs( e.Direction.z ) < 1e-3f ),
				"an edge is not on the top rim: " + string.Join( " | ", fillet.Edges.Select( e => $"{e.Point} {e.Direction}" ) ) );
			var sides = fillet.Edges.Select( e => MathF.Abs( e.Point.x - lo.x ) < 1e-3f ? "-x" : MathF.Abs( e.Point.x - hi.x ) < 1e-3f ? "+x"
				: MathF.Abs( e.Point.y - lo.y ) < 1e-3f ? "-y" : MathF.Abs( e.Point.y - hi.y ) < 1e-3f ? "+y" : "?" ).OrderBy( s => s, StringComparer.Ordinal ).ToArray();
			Check( string.Join( ",", sides ) == "+x,+y,-x,-y", "rim sides: " + string.Join( ",", sides ) );
			Check( fillet.Bodies.BodyIds.SequenceEqual( new[] { A( bench.Studio ).Id } ), "fillet bodies: " + string.Join( ",", fillet.Bodies.BodyIds ) );

			// geometry: the top rim is rounded; the bottom rim and the vertical edges stay sharp
			var ps = A( bench.Studio ).Mesh.Positions;
			Check( !ps.Any( p => MathF.Abs( p.z - hi.z ) < 1e-3f && (MathF.Abs( p.x - hi.x ) < 1e-3f || MathF.Abs( p.x - lo.x ) < 1e-3f) ), "a top-rim vertex survived the fillet" );
			Check( ps.Any( p => MathF.Abs( p.z - lo.z ) < 1e-3f && MathF.Abs( p.x - hi.x ) < 1e-3f && MathF.Abs( p.y - hi.y ) < 1e-3f ), "the bottom corner was rounded too" );
			Check( MathF.Abs( ps.Where( p => MathF.Abs( p.x - hi.x ) < 1e-3f && MathF.Abs( p.y - hi.y ) < 1e-3f ).Max( p => p.z ) - (hi.z - r) ) < 1e-2f,
				"the vertical corner edge should stay sharp up to the fillet's start" );

			// same result as picking those four edges directly
			var direct = new PartStudio();
			Check( BuildScript.Run( direct, "add Primitive name=A width=20 depth=20 height=20" ).Ok, "direct box" );
			string edges = string.Join( ";", fillet.Edges.Select( e => $"A:{P( e.Point.x, e.Point.y, e.Point.z )}:{P( e.Direction.x, e.Direction.y, e.Direction.z )}" ) );
			var res = BuildScript.Run( direct, $"add Fillet edges={edges} radius={F( r )}" );
			Check( res.Ok, "direct fillet: " + res.Error );
			Check( Shape( direct ) == Shape( bench.Studio ), "faces= and edges= fillets differ" );

			// the bench's script.txt replays to the same shape
			var script = File.ReadAllText( Path.Combine( root, "script.txt" ) );
			Check( script.Contains( "add Fillet name=Round faces=" ), "script.txt: " + script );
			Check( Shape( Replay( Path.Combine( root, "script.txt" ), root ) ) == Shape( bench.Studio ), "replayed fillet differs" );
		} );

		registry.Add( "face-boundary", "Chamfer takes a face as its rim; bad face fails; edges= wins over faces=", () =>
		{
			var studio = new PartStudio();
			Check( BuildScript.Run( studio, "add Primitive name=A width=20 depth=20 height=20" ).Ok, "box" );
			var (lo, hi) = Box( studio );
			float cx = (lo.x + hi.x) / 2, cy = (lo.y + hi.y) / 2;

			var bad = BuildScript.Run( studio, $"add Chamfer faces=A:{P( cx, cy, hi.z )}:0.7071,0.7071,0 distance=1" );
			Check( !bad.Ok && (bad.Error ?? "").Contains( "faces" ), "a face that isn't there should fail: " + bad.Error );
			Check( !studio.Features.OfType<ChamferFeature>().Any(), "a failed add left a chamfer" );

			var side = BuildScript.Run( studio, $"add Chamfer name=Side faces=A:{P( hi.x, cy, (lo.z + hi.z) / 2 )}:1,0,0 distance=1" );
			Check( side.Ok, "chamfer via face: " + side.Error );
			var chamfer = (ChamferFeature)studio.Features.Single( f => f.Name == "Side" );
			Check( chamfer.Edges.Count == 4 && chamfer.Edges.All( e => MathF.Abs( e.Point.x - hi.x ) < 1e-3f ), "chamfer rim: " + chamfer.Edges.Count );

			// both given: the picked edge list is used as-is, the s&box ApplyBlendEdges order
			var one = $"A:{P( lo.x, cy, lo.z )}:0,1,0";
			var both = BuildScript.Run( studio, $"add Fillet name=Both faces=A:{P( cx, cy, hi.z )}:0,0,1 edges={one} radius=1" );
			Check( both.Ok, "faces+edges fillet: " + both.Error );
			Check( ((FilletFeature)studio.Features.Single( f => f.Name == "Both" )).Edges.Count == 1, "edges= should win over faces=" );
		} );

		registry.Add( "face-boundary", "bench facematerial lines replay to the same per-face slots", () =>
		{
			var events = new List<JsonObject>();
			var root = NewDir( "facemat" );
			var bench = NewBench( root, events );
			var (lo, hi) = Box( bench.Studio );
			float cx = (lo.x + hi.x) / 2, cy = (lo.y + hi.y) / 2, cz = (lo.z + hi.z) / 2;
			string top = $"A:{P( cx, cy, hi.z )}:0,0,1", px = $"A:{P( hi.x, cy, cz )}:1,0,0", ny = $"A:{P( cx, lo.y, cz )}:0,-1,0";

			void Slot( string args )
			{
				bench.Command( "facematerial " + args );
				Check( events[^1]["ev"]!.GetValue<string>() == "applied" && events[^1]["revision"] is not null, args + ": " + events[^1].ToJsonString() );
			}
			Slot( "slot=3 faces=" + top );
			Slot( $"slot=5 faces={px};{ny}" );
			Slot( "slot=5 faces=" + top );     // reassign: the slot 3 feature goes, slot 5's grows
			Slot( "slot=0 faces=" + ny );      // detach
			Check( bench.Apply( "material 5 preset=anodised_metal" ), "material look: " + events[^1].ToJsonString() );

			var want = Slots( bench.Studio );
			Check( want.Count( s => s == 5 ) == 2 && !want.Contains( 3 ), "bench slots: " + string.Join( ",", want ) );

			var scriptPath = Path.Combine( root, "script.txt" );
			var lines = File.ReadAllLines( scriptPath );
			Check( lines.Count( l => l.StartsWith( "facematerial ", StringComparison.Ordinal ) ) == 4, "script.txt: " + string.Join( " | ", lines ) );

			var replay = Replay( scriptPath, root );
			Check( Slots( replay ).SequenceEqual( want ), $"replayed slots {string.Join( ",", Slots( replay ) )} != bench {string.Join( ",", want )}" );
			string Features( PartStudio s ) => string.Join( ",", s.Features.OfType<FaceMaterialFeature>().Select( f => $"{f.Material.Clamped}x{f.Faces.Count}" ) );
			Check( Features( replay ) == Features( bench.Studio ), $"replayed features {Features( replay )} != bench {Features( bench.Studio )}" );
			Check( replay.MaterialLooks.ContainsKey( 5 ), "replay lost the slot look" );

			// a no-op line stays out of the script, and the wheel's FACE MATERIAL key still describes the feature
			int count = File.ReadAllLines( scriptPath ).Length;
			bench.Command( "facematerial slot=5 faces=" + top );
			Check( events[^1]["ev"]!.GetValue<string>() == "applied" && events[^1]["revision"] is null && File.ReadAllLines( scriptPath ).Length == count, "no-op: " + events[^1].ToJsonString() );
			var d = Bench.Describe( "facematerial" );
			Check( d["kind"]!.GetValue<string>() == "add" && d["type"]!.GetValue<string>() == "FaceMaterial", "describe facematerial: " + d.ToJsonString() );
		} );

		registry.Add( "face-boundary", "facematerial verb rejects bad input and changes nothing", () =>
		{
			var studio = new PartStudio();
			Check( BuildScript.Run( studio, "add Primitive name=A width=20 depth=20 height=20" ).Ok, "box" );
			var (lo, hi) = Box( studio );
			string top = $"A:{P( (lo.x + hi.x) / 2, (lo.y + hi.y) / 2, hi.z )}:0,0,1";
			foreach ( var line in new[] { "facematerial faces=" + top, "facematerial slot=-1 faces=" + top, "facematerial slot=2",
				"facematerial slot=2 faces=" + top + " colour=red", $"facematerial slot=2 faces={top};Nope:0,0,10:0,0,1" } )
			{
				var r = BuildScript.Run( studio, line );
				Check( !r.Ok && !string.IsNullOrEmpty( r.Error ), "should fail: " + line );
			}
			Check( !studio.Features.OfType<FaceMaterialFeature>().Any() && Slots( studio ).All( s => s == 0 ), "a rejected line changed the model" );
			Check( BuildScript.Mutates( "facematerial" ), "facematerial should publish a revision in live runs" );
		} );
	}
}
