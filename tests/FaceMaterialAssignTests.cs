using System.Numerics;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

// Selected faces → material slot N on the bench (`facematerial slot=N faces=…`), the s&box face-menu
// way: FaceMaterialEdit.Assign per face, one FaceMaterialFeature per slot, slot 0 detaches, one undo.
internal static class FaceMaterialAssignTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }

	static Body A( Bench.Session bench ) => bench.Studio.Bodies.Single( b => b.Name == "A" );

	/// <summary>The faces token SubSelection would send for the face whose centre a ray down `dir` hits.</summary>
	static string Token( Bench.Session bench, Vector3 from, Vector3 dir, bool add = false, SubSelection? sel = null )
	{
		if ( sel is null || !add )
		{
			sel ??= new SubSelection();
			sel.Load( bench.Studio.Bodies );
			sel.SetMode( SubMode.Face );
		}
		var c = Centre( bench );
		var hit = sel.Pick( from + c, dir );
		Check( hit is { Kind: SubMode.Face }, $"no face hit from {from} along {dir}" );
		sel.Click( hit, add: add );
		return sel.ToJson()["faces"]!.GetValue<string>();
	}

	static Vector3 Centre( Bench.Session bench )
	{
		var ps = A( bench ).Mesh.Positions;
		return new Vector3( (ps.Min( p => p.x ) + ps.Max( p => p.x )) / 2, (ps.Min( p => p.y ) + ps.Max( p => p.y )) / 2, (ps.Min( p => p.z ) + ps.Max( p => p.z )) / 2 );
	}

	static List<int> OnSlot( Bench.Session bench, int slot )
	{
		var m = A( bench ).Mesh;
		return Enumerable.Range( 0, m.FaceCount ).Where( f => m.Faces[f].Material == slot ).ToList();
	}

	static bool IsTop( Bench.Session bench, int f ) { var m = A( bench ).Mesh; return m.FaceNormal( m.Faces[f] ).z > .99f; }

	static int Features( Bench.Session bench, int slot ) =>
		bench.Studio.Features.OfType<FaceMaterialFeature>().Count( f => f.Material.Clamped == slot && f.Faces.Count > 0 );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "facematerial", "selected faces → slot through the bench, one undo step each", () =>
		{
			var events = new List<JsonObject>();
			var dir = Path.Combine( Path.GetTempPath(), "effigy-facemat-" + Guid.NewGuid().ToString( "N" ) );
			var bench = new Bench.Session( dir, new PartStudio(), "box", events.Add );
			Check( bench.Apply( "add Primitive name=A width=20 depth=20 height=20" ), "box add failed: " + events[^1].ToJsonString() );

			var down = new Vector3( 0, 0, -1 );
			var top = Token( bench, new Vector3( 0, 0, 200 ), down );
			Check( OnSlot( bench, 3 ).Count == 0, "slot 3 in use before the test" );

			// top → slot 3
			bench.Command( "facematerial slot=3 faces=" + top );
			Check( events[^1]["ev"]!.GetValue<string>() == "applied" && events[^1]["revision"] is not null, "slot 3: " + events[^1].ToJsonString() );
			var s3 = OnSlot( bench, 3 );
			Check( s3.Count == 1 && IsTop( bench, s3[0] ), $"slot 3 faces: {string.Join( ",", s3 )}" );
			Check( Features( bench, 3 ) == 1, "slot 3 features: " + Features( bench, 3 ) );
			long rev3 = bench.Revision;

			// reassign → slot 5: the slot 3 assignment is gone, not left empty or shadowed
			bench.Command( "facematerial slot=5 faces=" + top );
			Check( events[^1]["ev"]!.GetValue<string>() == "applied", "slot 5: " + events[^1].ToJsonString() );
			var s5 = OnSlot( bench, 5 );
			Check( s5.Count == 1 && IsTop( bench, s5[0] ) && OnSlot( bench, 3 ).Count == 0, $"slot 5 faces {string.Join( ",", s5 )}, slot 3 {OnSlot( bench, 3 ).Count}" );
			Check( bench.Studio.Features.OfType<FaceMaterialFeature>().All( f => f.Material.Clamped != 3 ), "a slot 3 feature survived the reassign" );
			Check( Features( bench, 5 ) == 1, "slot 5 features: " + Features( bench, 5 ) );

			// an unresolvable face fails the whole line and changes nothing
			long before = bench.Revision;
			bench.Command( "facematerial slot=7 faces=" + top + ";Nope:0,0,10:0,0,1" );
			Check( events[^1]["ev"]!.GetValue<string>() == "failed" && events[^1]["error"]!.GetValue<string>().Contains( "Nope" ), "bad face: " + events[^1].ToJsonString() );
			Check( bench.Revision == before && OnSlot( bench, 7 ).Count == 0 && OnSlot( bench, 5 ).Count == 1, "a failed line changed the model" );
			bench.Command( "facematerial slot=7 faces=A:0,0,10:0.7,0.7,0" );
			Check( events[^1]["ev"]!.GetValue<string>() == "failed" && bench.Revision == before, "unresolvable face: " + events[^1].ToJsonString() );
			bench.Command( "facematerial faces=" + top );
			Check( events[^1]["ev"]!.GetValue<string>() == "failed", "missing slot accepted" );

			// slot 0 detaches: no assignment left at all
			bench.Command( "facematerial slot=0 faces=" + top );
			Check( events[^1]["ev"]!.GetValue<string>() == "applied", "slot 0: " + events[^1].ToJsonString() );
			Check( !bench.Studio.Features.OfType<FaceMaterialFeature>().Any(), "detach left a FaceMaterial feature" );
			Check( OnSlot( bench, 5 ).Count == 0 && OnSlot( bench, 0 ).Count == A( bench ).Mesh.FaceCount, "detach left a painted face" );

			// undo walks back one command at a time
			Check( bench.Undo() && OnSlot( bench, 5 ).Count == 1 && IsTop( bench, OnSlot( bench, 5 )[0] ), "undo did not bring slot 5 back" );
			Check( bench.Undo() && OnSlot( bench, 3 ).Count == 1 && OnSlot( bench, 5 ).Count == 0 && Features( bench, 3 ) == 1, "undo did not bring slot 3 back" );
			Check( bench.Undo() && OnSlot( bench, 3 ).Count == 0 && !bench.Studio.Features.OfType<FaceMaterialFeature>().Any(), "undo did not clear slot 3" );

			// two faces in one line: both land, one undo takes both back
			var sel = new SubSelection();
			Token( bench, new Vector3( 0, 0, 200 ), down, sel: sel );
			var both = Token( bench, new Vector3( 200, 0, 0 ), new Vector3( -1, 0, 0 ), add: true, sel: sel );
			Check( both.Split( ';', StringSplitOptions.RemoveEmptyEntries ).Length == 2, "two-face token: " + both );
			bench.Command( "facematerial slot=4 faces=" + both );
			Check( OnSlot( bench, 4 ).Count == 2 && Features( bench, 4 ) == 1, $"two faces → slot 4: {OnSlot( bench, 4 ).Count} faces, {Features( bench, 4 )} features" );
			// again: already there, no new revision
			long rev4 = bench.Revision;
			bench.Command( "facematerial slot=4 faces=" + both );
			Check( events[^1]["ev"]!.GetValue<string>() == "applied" && bench.Revision == rev4, "a no-op made a revision" );
			Check( bench.Undo() && OnSlot( bench, 4 ).Count == 0, "one undo did not take both faces back" );
			Check( bench.Script.Any( l => l.StartsWith( "facematerial slot=4 " ) ), "the line is not in the bench script" );
			// the cabinet sends it as `apply facematerial …` (host.benchApply): same command
			bench.Command( "apply facematerial slot=6 faces=" + both );
			Check( events[^1]["ev"]!.GetValue<string>() == "applied" && OnSlot( bench, 6 ).Count == 2, "apply facematerial: " + events[^1].ToJsonString() );
			_ = rev3;
			bench.Close();
		} );
	}
}
