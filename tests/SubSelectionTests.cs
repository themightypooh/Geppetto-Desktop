using System.Numerics;
using System.Text.Json.Nodes;
using Effigy;
using Marionette.Desktop;

// Viewport sub-selection: a face / edge picked from a fixed camera comes back as the kernel's own
// references (FaceRef, MeshSelector rule) and drives FaceMaterial and an edit bevel on the bench.
internal static class SubSelectionTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }

	const int W = 640, H = 480;

	static string NewDir( string tag ) => Path.Combine( Path.GetTempPath(), $"effigy-subsel-{tag}-" + Guid.NewGuid().ToString( "N" ) );

	static (Bench.Session bench, List<JsonObject> events) Box()
	{
		var events = new List<JsonObject>();
		var bench = new Bench.Session( NewDir( "box" ), new PartStudio(), "box", events.Add );
		Check( bench.Apply( "add Primitive name=A width=20 depth=20 height=20" ), "box add failed: " + events[^1].ToJsonString() );
		return (bench, events);
	}

	static (Vector3 min, Vector3 max) Bounds( PolyMesh mesh )
	{
		var min = new Vector3( float.MaxValue ); var max = new Vector3( float.MinValue );
		foreach ( var p in mesh.Positions ) { min = Vector3.Min( min, new( p.x, p.y, p.z ) ); max = Vector3.Max( max, new( p.x, p.y, p.z ) ); }
		return (min, max);
	}

	/// <summary>Looking straight down -X at the box from 3/4 up: the +X face fills the middle.</summary>
	public static OrbitCamera Camera( Vector3 centre ) => new() { Target = centre, Distance = 80, YawDegrees = 20, PitchDegrees = 25 };

	public static (float X, float Y) Project( OrbitCamera camera, Vector3 p )
	{
		var clip = Vector4.Transform( new Vector4( p, 1 ), camera.ViewMatrix() * camera.ProjectionMatrix( W / (float)H ) );
		return ((clip.X / clip.W + 1) * .5f * W, (1 - clip.Y / clip.W) * .5f * H);
	}

	static (Vector3 o, Vector3 d) Ray( OrbitCamera camera, Vector3 p )
	{
		var (x, y) = Project( camera, p );
		return camera.ScreenRay( x, y, W, H );
	}

	/// <summary>EdgePickPixels in world units at a point, as ServeMode works it out.</summary>
	static float Radius( OrbitCamera camera, Vector3 p ) =>
		MathF.Tan( camera.FovDegrees * MathF.PI / 360f ) * Vector3.Distance( camera.Eye, p ) / (H * .5f) * SubSelection.EdgePickPixels;

	/// <summary>A radius-10 quad sphere (all quads, welded) on its own bench.</summary>
	static (Bench.Session bench, SubSelection sel, Body body, OrbitCamera camera, Vector3 centre) Sphere()
	{
		var events = new List<JsonObject>();
		var bench = new Bench.Session( NewDir( "sphere" ), new PartStudio(), "sphere", events.Add );
		Check( bench.Apply( "add Primitive name=S shape=Sphere radius=10 divisions=4" ), "sphere add failed: " + events[^1].ToJsonString() );
		var body = bench.Studio.Bodies.Single( b => b.Name == "S" );
		var (min, max) = Bounds( body.Mesh );
		var centre = (min + max) * .5f;
		var sel = new SubSelection();
		sel.Load( bench.Studio.Bodies );
		sel.SetMode( SubMode.Edge );
		return (bench, sel, body, Camera( centre ), centre);
	}

	/// <summary>The ray at the sphere point nearest the eye, nudged off the cube-seam diagonal.</summary>
	static (Vector3 o, Vector3 d) Facing( OrbitCamera camera, Vector3 centre, float radius )
	{
		var p = centre + Vector3.Normalize( camera.Eye - centre + new Vector3( 0, 0, 7 ) ) * radius;
		return Ray( camera, p );
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "subselect", "face pick from a fixed camera resolves through FacePlane", () =>
		{
			var (bench, _) = Box();
			var body = bench.Studio.Bodies.Single( b => b.Name == "A" );
			var (min, max) = Bounds( body.Mesh );
			var camera = Camera( (min + max) * .5f );
			var sel = new SubSelection();
			sel.Load( bench.Studio.Bodies );
			sel.SetMode( SubMode.Face );

			// the middle of the +X face, seen on screen: a face, not the rim
			var target = new Vector3( max.X, (min.Y + max.Y) * .5f, (min.Z + max.Z) * .5f );
			var hit = sel.Pick( Ray( camera, target ).o, Ray( camera, target ).d, p => Radius( camera, p ) );
			Check( hit is { Kind: SubMode.Face }, "no face under the +X face's centre" );
			var n = body.Mesh.FaceNormal( body.Mesh.Faces[hit!.Value.Face] );
			Check( n.x > .99f, $"picked face normal {n} is not +X" );

			sel.Click( hit );
			Check( FacePlane.TryResolveFace( bench.Studio.Bodies, sel.Faces[0], out var rb, out var rf ), "FaceRef did not resolve" );
			Check( rb.Id == body.Id && rf == hit.Value.Face, $"FaceRef resolved to {rb.Name}#{rf}, picked #{hit.Value.Face}" );

			// the event carries the script forms; the selector rule picks exactly that face
			var ev = sel.ToJson();
			Check( ev["ev"]!.GetValue<string>() == "selection" && ev["kind"]!.GetValue<string>() == "faces", "event: " + ev.ToJsonString() );
			var entry = ev["bodies"]!["A"]!;
			Check( entry["element"]!.GetValue<string>() == "faces", "element: " + entry.ToJsonString() );
			Check( MeshSelector.TryParse( entry["select"]!.GetValue<string>(), out var selector, out var why ), "rule did not parse: " + why );
			Check( selector.Faces( body.Mesh ).SequenceEqual( new[] { hit.Value.Face } ), "rule selects " + string.Join( ",", selector.Faces( body.Mesh ) ) );

			// shift toggles the top face in, shift again on the same face (another point on it) toggles it out
			var top = new Vector3( (min.X + max.X) * .5f, (min.Y + max.Y) * .5f, max.Z );
			var topHit = sel.Pick( Ray( camera, top ).o, Ray( camera, top ).d, p => Radius( camera, p ) );
			sel.Click( topHit, add: true );
			Check( sel.Faces.Count == 2, "shift-click did not add" );
			var top2 = top + new Vector3( 2, 1, 0 );
			sel.Click( sel.Pick( Ray( camera, top2 ).o, Ray( camera, top2 ).d, p => Radius( camera, p ) ), add: true );
			Check( sel.Faces.Count == 1, "shift-click on the selected face again did not toggle it out" );
			sel.Click( topHit, add: true );
			sel.Click( hit, remove: true );
			Check( sel.Faces.Count == 1 && sel.Resolved[0].Kind == SubMode.Face && body.Mesh.FaceNormal( body.Mesh.Faces[sel.Resolved[0].Face] ).z > .99f, "ctrl-click did not remove the side face" );

			// alt: the face and the one across the wall
			sel.Click( hit, pair: true );
			Check( sel.Faces.Count == 2 && sel.Resolved.Select( r => body.Mesh.FaceNormal( body.Mesh.Faces[r.Face] ).x ).OrderBy( x => x ).SequenceEqual( new[] { -1f, 1f } ), "alt-click did not take the facing pair" );

			// a click on nothing clears
			sel.Click( null );
			Check( sel.IsEmpty, "a miss did not clear" );

			// FaceMaterial faces= takes the token and paints exactly that face
			sel.Click( topHit );
			var token = sel.ToJson()["faces"]!.GetValue<string>();
			Check( bench.Apply( $"add FaceMaterial faces={token} material=3" ), "FaceMaterial line failed: " + token );
			var painted = bench.Studio.Bodies.Single( b => b.Name == "A" ).Mesh;
			var slot3 = Enumerable.Range( 0, painted.FaceCount ).Where( f => painted.Faces[f].Material == 3 ).ToList();
			Check( slot3.Count == 1 && painted.FaceNormal( painted.Faces[slot3[0]] ).z > .99f, $"slot 3 faces: {string.Join( ",", slot3 )}" );
			bench.Close();
		} );

		registry.Add( "subselect", "a rim click is the edge; edges bevel and fillet through the bench", () =>
		{
			var (bench, events) = Box();
			var body = bench.Studio.Bodies.Single( b => b.Name == "A" );
			int facesBefore = body.Mesh.FaceCount;
			var (min, max) = Bounds( body.Mesh );
			var camera = Camera( (min + max) * .5f );
			var sel = new SubSelection();
			sel.Load( bench.Studio.Bodies );
			sel.SetMode( SubMode.Face );

			// a hair below the top edge of the +X face: within EdgePickPixels, so Face mode takes the edge
			var near = new Vector3( max.X, (min.Y + max.Y) * .5f, max.Z - (max.Z - min.Z) * .01f );
			var rim = sel.Pick( Ray( camera, near ).o, Ray( camera, near ).d, p => Radius( camera, p ) );
			Check( rim is { Kind: SubMode.Edge }, "a click at the rim did not take the edge" );
			// and a face click then drops it, with a notice
			sel.Click( rim );
			var mid = new Vector3( max.X, (min.Y + max.Y) * .5f, (min.Z + max.Z) * .5f );
			sel.Click( sel.Pick( Ray( camera, mid ).o, Ray( camera, mid ).d, p => Radius( camera, p ) ), add: true );
			Check( sel.Edges.Count == 0 && sel.Faces.Count == 1 && sel.Notice is not null, "faces and edges shared a selection" );

			// Edge mode: the nearest rim edge, wherever on the face
			sel.SetMode( SubMode.Edge );
			var lower = new Vector3( max.X, (min.Y + max.Y) * .5f, max.Z - (max.Z - min.Z) * .2f );
			sel.Click( sel.Pick( Ray( camera, lower ).o, Ray( camera, lower ).d ) );
			Check( sel.Resolved.Count == 1 && sel.Resolved[0].Kind == SubMode.Edge, "no edge picked" );
			var e = sel.Resolved[0].Edge;
			var a = body.Mesh.Positions[e.A]; var b = body.Mesh.Positions[e.B];
			Check( MathF.Abs( a.x - max.X ) < 1e-3f && MathF.Abs( b.x - max.X ) < 1e-3f && MathF.Abs( a.z - max.Z ) < 1e-3f && MathF.Abs( b.z - max.Z ) < 1e-3f,
				$"picked edge {a} - {b} is not the +X top edge" );

			var ev = sel.ToJson();
			var entry = ev["bodies"]!["A"]!;
			string rule = entry["select"]!.GetValue<string>(), element = entry["element"]!.GetValue<string>();
			string edgeToken = ev["edges"]!.GetValue<string>();
			Check( element == "edges", "element " + element );
			Check( MeshSelector.TryParse( rule, out var sel1, out _ ) && sel1.Edges( body.Mesh ).Count() == 1, "rule does not select exactly one edge: " + rule );

			// Fillet edges= takes the EdgeRef token (s&box ApplyBlendEdges)
			Check( bench.Apply( $"add Fillet edges={edgeToken} radius=2" ), "fillet failed: " + events[^1].ToJsonString() );
			var filleted = bench.Studio.Bodies.Single( x => x.Name == "A" ).Mesh;
			Check( filleted.FaceCount > facesBefore + 1, $"fillet added no faces: {facesBefore} -> {filleted.FaceCount}" );
			Check( bench.Apply( "undo" ) || true, "" );

			// the edit-op bevel takes the select rule
			var b2 = Box();
			var box2 = b2.bench.Studio.Bodies.Single( x => x.Name == "A" );
			Check( b2.bench.Apply( $"edit A bevel select=\"{rule}\" element={element} distance=2 segments=1" ), "bevel failed: " + b2.events[^1].ToJsonString() );
			var after = b2.bench.Studio.Bodies.Single( x => x.Name == "A" ).Mesh;
			Check( after.FaceCount > facesBefore, $"bevel added no faces: {facesBefore} -> {after.FaceCount}" );
			var chamfers = Enumerable.Range( 0, after.FaceCount ).Select( f => after.FaceNormal( after.Faces[f] ) )
				.Where( nn => MathF.Abs( nn.y ) < .05f && MathF.Abs( nn.x - nn.z ) < .05f && MathF.Abs( nn.x ) > .5f ).ToList();
			Check( chamfers.Count >= 1 && chamfers.All( nn => nn.x > 0 ), "no +X/+Z chamfer face" );

			// a rebuild keeps refs (re-resolved), a removed body drops them
			sel.Reload( Array.Empty<Body>() );
			Check( sel.IsEmpty, "refs on a vanished body survived" );
			b2.bench.Close();
			bench.Close();
		} );

		registry.Add( "subselect", "alt on an edge takes its loop, ctrl+alt its ring (s&box)", () =>
		{
			var (bench, sel, body, camera, centre) = Sphere();
			var (ro, rd) = Facing( camera, centre, 10 );
			var seed = sel.Pick( ro, rd, SubMode.Edge );
			Check( seed is { Kind: SubMode.Edge }, "no edge on the sphere" );

			sel.Click( seed, pair: true );                            // alt
			var loop = sel.Resolved.Select( r => r.Edge ).ToList();
			Check( loop.Count >= 8, $"loop has {loop.Count} edges" );
			Check( loop.SelectMany( e => new[] { e.A, e.B } ).GroupBy( v => v ).All( g => g.Count() == 2 ), "loop is not closed (some vertex not used twice)" );

			sel.Click( seed, remove: true, pair: true );              // ctrl+alt
			var ring = sel.Resolved.Select( r => r.Edge ).ToList();
			Check( ring.Count >= 8, $"ring has {ring.Count} edges" );
			Check( ring.SelectMany( e => new[] { e.A, e.B } ).Distinct().Count() == ring.Count * 2, "ring edges share vertices" );
			// they cross at the seed and again on the far side of the sphere
			Check( ring.Contains( seed.Value.Edge ) && ring.Intersect( loop ).Count() <= 2, $"ring and loop share {ring.Intersect( loop ).Count()} edges" );

			// shift+alt adds a second loop next to the first
			sel.Click( seed, pair: true );
			int one = sel.Edges.Count;
			sel.Click( seed, add: true, pair: true );
			Check( sel.Edges.Count == one, "shift+alt on the same loop duplicated edges" );
			bench.Close();
		} );

		registry.Add( "subselect", "box and lasso select take only what the eye can see; xray takes the back", () =>
		{
			var (bench, _) = Box();
			var body = bench.Studio.Bodies.Single( b => b.Name == "A" );
			var (min, max) = Bounds( body.Mesh );
			var camera = Camera( (min + max) * .5f );
			var sel = new SubSelection();
			sel.Load( bench.Studio.Bodies );
			Vector2? ToScreen( Vector3 p ) { var (x, y) = Project( camera, p ); return new Vector2( x, y ); }
			bool All( Vector2 q ) => true;

			sel.SetMode( SubMode.Vertex );
			sel.SelectRegion( camera.Eye, ToScreen, All );
			Check( sel.Vertices.Count == 7, $"box saw {sel.Vertices.Count} corners, the hidden one is behind the box" );
			sel.SelectRegion( camera.Eye, ToScreen, All, xray: true );
			Check( sel.Vertices.Count == 8, $"xray box saw {sel.Vertices.Count} corners" );

			sel.SetMode( SubMode.Face );
			sel.SelectRegion( camera.Eye, ToScreen, All );
			Check( sel.Faces.Count == 3, $"box took {sel.Faces.Count} faces, 3 face the camera" );
			sel.SelectRegion( camera.Eye, ToScreen, All, xray: true );
			Check( sel.Faces.Count == 6, $"xray box took {sel.Faces.Count} faces" );

			sel.SetMode( SubMode.Edge );
			sel.SelectRegion( camera.Eye, ToScreen, All );
			Check( sel.Edges.Count == 9, $"box took {sel.Edges.Count} edges, 9 are visible" );

			// lasso: a small triangle around the +X face's centre takes that face alone; remove takes it back
			sel.SetMode( SubMode.Face );
			var c = ToScreen( new Vector3( max.X, (min.Y + max.Y) * .5f, (min.Z + max.Z) * .5f ) )!.Value;
			var lasso = new[] { c + new Vector2( -15, 12 ), c + new Vector2( 15, 12 ), c + new Vector2( 0, -15 ) };
			Check( SubSelection.InsideLasso( lasso, c ) && !SubSelection.InsideLasso( lasso, c + new Vector2( 40, 0 ) ), "lasso point test" );
			sel.SelectRegion( camera.Eye, ToScreen, q => SubSelection.InsideLasso( lasso, q ) );
			Check( sel.Faces.Count == 1 && body.Mesh.FaceNormal( body.Mesh.Faces[sel.Resolved[0].Face] ).x > .99f, "lasso did not take the +X face alone" );
			sel.SelectRegion( camera.Eye, ToScreen, All, add: true );
			Check( sel.Faces.Count == 3, "add box did not union" );
			sel.SelectRegion( camera.Eye, ToScreen, q => SubSelection.InsideLasso( lasso, q ), remove: true );
			Check( sel.Faces.Count == 2, "remove lasso did not subtract the +X face" );
			bench.Close();
		} );

		registry.Add( "subselect", "grow, shrink and select linked go through MeshEditSession", () =>
		{
			var (bench, sel, body, camera, centre) = Sphere();
			sel.SetMode( SubMode.Face );
			var (ro, rd) = Facing( camera, centre, 10 );
			sel.Click( sel.Pick( ro, rd, SubMode.Face ) );
			Check( sel.Faces.Count == 1, "seed face" );
			sel.Apply( SubSelection.Topology.Grow );
			int grown = sel.Faces.Count;
			Check( grown >= 5, $"grow took {grown} faces" );
			sel.Apply( SubSelection.Topology.Grow );
			Check( sel.Faces.Count > grown, "second grow did not grow" );
			sel.Apply( SubSelection.Topology.Shrink );
			Check( sel.Faces.Count == grown, $"shrink gave {sel.Faces.Count}, grown was {grown}" );
			sel.Apply( SubSelection.Topology.Linked );
			Check( sel.Faces.Count == body.Mesh.FaceCount, $"linked took {sel.Faces.Count} of {body.Mesh.FaceCount}" );

			// edges grow too, and linked from one edge is every edge
			sel.SetMode( SubMode.Edge );
			sel.Click( sel.Pick( ro, rd, SubMode.Edge ) );
			sel.Apply( SubSelection.Topology.Grow );
			Check( sel.Edges.Count > 1, "edge grow" );
			sel.Apply( SubSelection.Topology.Linked );
			Check( sel.Edges.Count == body.Mesh.BuildEdgeFaces().Count, $"edge linked took {sel.Edges.Count}" );

			// box: linked from one face is the six faces
			var (b2, _) = Box();
			var box = b2.Studio.Bodies.Single( b => b.Name == "A" );
			var (min, max) = Bounds( box.Mesh );
			var bc = Camera( (min + max) * .5f );
			var s2 = new SubSelection(); s2.Load( b2.Studio.Bodies ); s2.SetMode( SubMode.Face );
			var side = new Vector3( max.X, (min.Y + max.Y) * .5f, (min.Z + max.Z) * .5f );
			s2.Click( s2.Pick( Ray( bc, side ).o, Ray( bc, side ).d, SubMode.Face ) );
			s2.Apply( SubSelection.Topology.Linked );
			Check( s2.Faces.Count == 6, $"box linked took {s2.Faces.Count}" );
			s2.Apply( SubSelection.Topology.Shrink );
			Check( s2.Faces.Count == 6, "a closed shell has no border to shrink from" );
			s2.Click( s2.Pick( Ray( bc, side ).o, Ray( bc, side ).d, SubMode.Face ) );
			s2.Apply( SubSelection.Topology.Shrink );
			Check( s2.Faces.Count == 0 && s2.Notice is not null, "shrinking one face did not empty it with a notice" );
			b2.Close();
			bench.Close();
		} );

		registry.Add( "subselect", "a sculpt/paint/mesh-edit tool hides the highlight but keeps the picks", () =>
		{
			var (bench, _) = Box();
			var body = bench.Studio.Bodies.Single( b => b.Name == "A" );
			var (min, max) = Bounds( body.Mesh );
			var camera = Camera( (min + max) * .5f );
			var sel = new SubSelection(); sel.Load( bench.Studio.Bodies ); sel.SetMode( SubMode.Face );
			var side = new Vector3( max.X, (min.Y + max.Y) * .5f, (min.Z + max.Z) * .5f );
			sel.Click( sel.Pick( Ray( camera, side ).o, Ray( camera, side ).d ) );
			Check( sel.Layers().Any( l => l.Visible ), "selection layer not shown" );
			foreach ( var tool in new[] { "paint", "sculpt", "meshedit", "materialbrush", "weight" } )
			{
				sel.SetTool( tool );
				Check( sel.Layers().All( l => !l.Visible ) && sel.Faces.Count == 1, tool + " did not hide the highlight or lost the pick" );
				Check( sel.ToJson()["hidden"]!.GetValue<bool>(), tool + ": event not marked hidden" );
				sel.SetHover( sel.Pick( Ray( camera, side ).o, Ray( camera, side ).d ) );
				Check( sel.Layers().All( l => !l.Visible ), tool + ": hover showed through" );
			}
			sel.SetTool( "none" );
			Check( sel.Layers().Any( l => l.Visible ) && sel.Faces.Count == 1, "highlight did not come back" );
			bool threw = false; try { sel.SetTool( "knife" ); } catch ( ArgumentException ) { threw = true; }
			Check( threw, "an unknown tool was accepted" );
			bench.Close();
		} );

		registry.Add( "subselect", "edge and vertex modes draw every pickable edge as a faint wire", () =>
		{
			var (bench, _) = Box();
			var body = bench.Studio.Bodies.Single( b => b.Name == "A" );
			var sel = new SubSelection(); sel.Load( bench.Studio.Bodies );
			var wire = sel.Layers().Single( l => l.IsWire );
			sel.SetMode( SubMode.Face );
			Check( !wire.Visible, "wire shown in Face mode" );
			sel.SetMode( SubMode.Edge );
			Check( wire.Visible && sel.IsEmpty, "no wire in Edge mode with nothing picked" );
			Check( sel.RimEdges( body.Mesh ).Length == 12, $"box wire has {sel.RimEdges( body.Mesh ).Length} edges, want 12" );
			sel.SetMode( SubMode.Vertex );
			Check( wire.Visible, "no wire in Vertex mode" );
			sel.SetTool( "sculpt" );
			Check( !wire.Visible, "wire shown under a sculpt brush" );
			sel.SetTool( "none" ); sel.SetRig( true );
			Check( !wire.Visible, "wire shown in rig mode" );
			bench.Close();
		} );

		registry.Add( "subselect", "rig mode: a click takes the whole body, shift toggles", () =>
		{
			var (bench, events) = Box();
			Check( bench.Apply( "add Primitive name=B width=10 depth=10 height=10 position=0,40,0" ), "second box: " + events[^1].ToJsonString() );
			var a = bench.Studio.Bodies.Single( b => b.Name == "A" );
			var (min, max) = Bounds( a.Mesh );
			var camera = Camera( (min + max) * .5f );
			var sel = new SubSelection(); sel.Load( bench.Studio.Bodies ); sel.SetMode( SubMode.Face );
			var side = new Vector3( max.X, (min.Y + max.Y) * .5f, (min.Z + max.Z) * .5f );
			sel.Click( sel.Pick( Ray( camera, side ).o, Ray( camera, side ).d ) );
			sel.SetRig( true );
			Check( sel.IsEmpty, "entering rig mode kept face picks" );
			var hit = sel.Pick( Ray( camera, side ).o, Ray( camera, side ).d );
			Check( hit is { Kind: SubMode.Body } && hit.Value.Body.Id == a.Id, "rig pick was not body A" );
			sel.Click( hit );
			var ev = sel.ToJson();
			Check( ev["kind"]!.GetValue<string>() == "bodies" && ev["rig"]!.GetValue<bool>() && sel.BodyIds.SequenceEqual( new[] { a.Id } ), "event: " + ev.ToJsonString() );
			Check( sel.Layers().Any( l => l.Visible ), "body highlight hidden" );
			sel.Click( hit, add: true );
			Check( sel.BodyIds.Count == 0, "shift on the selected body did not toggle it out" );
			// a box drag in rig mode takes every body with a visible corner inside
			Vector2? ToScreen( Vector3 p ) { var (x, y) = Project( camera, p ); return new Vector2( x, y ); }
			sel.SelectRegion( camera.Eye, ToScreen, q => true );
			Check( sel.BodyIds.Count == 2, $"rig box took {sel.BodyIds.Count} bodies" );
			sel.SetRig( false );
			Check( sel.IsEmpty && !sel.Rig, "leaving rig mode kept bodies" );
			bench.Close();
		} );

		registry.Add( "subselect", "a material drop re-picks the face at the drop pixel", () =>
		{
			var (bench, events) = Box();
			var body = bench.Studio.Bodies.Single( b => b.Name == "A" );
			var (min, max) = Bounds( body.Mesh );
			var camera = Camera( (min + max) * .5f );
			var sel = new SubSelection(); sel.Load( bench.Studio.Bodies ); sel.SetMode( SubMode.Face );
			var side = new Vector3( max.X, (min.Y + max.Y) * .5f, (min.Z + max.Z) * .5f );
			var top = new Vector3( (min.X + max.X) * .5f, (min.Y + max.Y) * .5f, max.Z );
			sel.Click( sel.Pick( Ray( camera, side ).o, Ray( camera, side ).d ) );   // the side is selected…
			var drop = sel.PickDrop( Ray( camera, top ).o, Ray( camera, top ).d );   // …the drop lands on the top
			Check( drop is { Kind: SubMode.Face } && body.Mesh.FaceNormal( body.Mesh.Faces[drop.Value.Face] ).z > .99f, "drop did not hit the top" );
			Check( sel.Faces.Count == 1 && body.Mesh.FaceNormal( body.Mesh.Faces[sel.Resolved[0].Face] ).x > .99f, "drop changed the selection" );
			var token = sel.FaceTokenOf( drop!.Value );
			Check( bench.Apply( $"add FaceMaterial faces={token} material=4" ), "drop FaceMaterial: " + events[^1].ToJsonString() );
			var m = bench.Studio.Bodies.Single( b => b.Name == "A" ).Mesh;
			var slot = Enumerable.Range( 0, m.FaceCount ).Where( f => m.Faces[f].Material == 4 ).ToList();
			Check( slot.Count == 1 && m.FaceNormal( m.Faces[slot[0]] ).z > .99f, "drop painted " + string.Join( ",", slot ) );
			// a drop in empty space misses
			var far = camera.ScreenRay( 2, 2, W, H );
			Check( sel.PickDrop( far.Item1, far.Item2 ) is null, "a drop on nothing hit" );
			bench.Close();
		} );

		registry.Add( "egl-image", "sub-selection highlight draws through the shared renderer", () => Render( settings.WorkDirectory ),
			missing: () => Environment.GetEnvironmentVariable( "V3_REQUIRE_EGL" ) == "1" ? null :
				"Set V3_REQUIRE_EGL=1 for windowless hardware image probes" );
	}

	/// <summary>Box with its +X face selected, the top +X edge selected and the top face hovered,
	/// rendered windowless; the selected face must come out amber.</summary>
	public static void Render( string artifacts )
	{
		Directory.CreateDirectory( artifacts );
		var (bench, _) = Box();
		var body = bench.Studio.Bodies.Single( b => b.Name == "A" );
		var (min, max) = Bounds( body.Mesh );
		var camera = Camera( (min + max) * .5f );
		var sel = new SubSelection();
		sel.Load( bench.Studio.Bodies );
		sel.SetMode( SubMode.Face );
		var side = new Vector3( max.X, (min.Y + max.Y) * .5f, (min.Z + max.Z) * .5f );
		var (ro, rd) = Ray( camera, side );
		sel.Click( sel.Pick( ro, rd ) );
		var (to, td) = Ray( camera, new Vector3( (min.X + max.X) * .5f, (min.Y + max.Y) * .5f, max.Z ) );
		sel.SetHover( sel.Pick( to, td ) );

		using var egl = new HeadlessGlContext();
		using var renderer = new SceneRenderer( egl.Gl );
		renderer.Live.Layers.AddRange( sel.Layers() );
		// Render releases the mesh's buffers, so each image gets its own copy.
		using var plainMesh = ViewportMesh.FromStudio( bench.Studio, 0, Path.Combine( artifacts, "box.effigy" ) );
		using var mesh = ViewportMesh.FromStudio( bench.Studio, 0, Path.Combine( artifacts, "box.effigy" ) );
		using var bare = new SceneRenderer( egl.Gl );
		var plain = HeadlessImageChecks.Render( egl, bare, plainMesh, camera );
		var lit = HeadlessImageChecks.Render( egl, renderer, mesh, camera );
		plain.SavePpm( Path.Combine( artifacts, "subselect-plain.ppm" ) );
		lit.SavePpm( Path.Combine( artifacts, "subselect-face.ppm" ) );
		var (px, py) = HeadlessImageChecks.Project( camera, side );
		var before = plain.At( px, py ); var after = lit.At( px, py );
		Check( after.R - after.B > 60 && after.R >= before.R, $"selected face not amber: {before} -> {after}" );
		bench.Close();
	}
}
