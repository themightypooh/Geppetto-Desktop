#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;
using Effigy.Map;

// Hammer Desktop P2: greybox workflow defaults — dev textures, nodraw-first, func_detail taught early.
// GreyboxDefaults (dev mode flag + dev material set + nodraw-first creation + SuggestFuncDetail)
// + GreyboxChecklist (seal / spawn / light) + the Greybox*Rule engine rules.
//
// MapBrush (hammer-brush-core-plane-based-brushes) and MapBrushPrimitives
// (hammer-brush-primitives-block-tool) had NOT landed when this was written —
// no MapBrush type exists in the tree yet — so the brush checks build against
// the landed IssueBrush shape (faces as planes + polygon vertices, mirroring
// the MapBrush face shape from its filed spec), not against MapBrush itself.
// FaceMaterial (nodraw default) and IssueEngine (engine + LeakProbe) HAVE
// landed and are used directly.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned
// and the final integrator adds the one GreyboxDefaultsTests.Register(registry, settings); line.
internal static class GreyboxDefaultsTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }
	static bool Near( double a, double b, double eps = 1e-3 ) => Math.Abs( a - b ) <= eps;

	/// <summary>One face of an axis-aligned box: outward normal + distance + outward-wound quad.</summary>
	static IssueFace BoxFace( Vec3 normal, Vec3 a, Vec3 b, Vec3 c, Vec3 d, string material ) =>
		new IssueFace( normal, Vec3.Dot( normal, a ), material, new[] { a, b, c, d } );

	/// <summary>
	/// An axis-aligned box brush from min/max corners: 6 outward-wound faces
	/// (verified by the analytic-volume check below). Material defaults to nodraw.
	/// </summary>
	static IssueBrush BoxBrush( int id, Vec3 min, Vec3 max, bool isDetail = false, string? material = null )
	{
		var m = material ?? ToolTexture.NoDrawName;
		var (x0, y0, z0) = (min.x, min.y, min.z);
		var (x1, y1, z1) = (max.x, max.y, max.z);
		return new IssueBrush( id, new[]
		{
			BoxFace( new Vec3( -1, 0, 0 ), new Vec3( x0, y0, z0 ), new Vec3( x0, y0, z1 ), new Vec3( x0, y1, z1 ), new Vec3( x0, y1, z0 ), m ),
			BoxFace( new Vec3( 1, 0, 0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y0, z1 ), m ),
			BoxFace( new Vec3( 0, -1, 0 ), new Vec3( x0, y0, z0 ), new Vec3( x1, y0, z0 ), new Vec3( x1, y0, z1 ), new Vec3( x0, y0, z1 ), m ),
			BoxFace( new Vec3( 0, 1, 0 ), new Vec3( x0, y1, z0 ), new Vec3( x0, y1, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x1, y1, z0 ), m ),
			BoxFace( new Vec3( 0, 0, -1 ), new Vec3( x0, y0, z0 ), new Vec3( x0, y1, z0 ), new Vec3( x1, y1, z0 ), new Vec3( x1, y0, z0 ), m ),
			BoxFace( new Vec3( 0, 0, 1 ), new Vec3( x0, y0, z1 ), new Vec3( x1, y0, z1 ), new Vec3( x1, y1, z1 ), new Vec3( x0, y1, z1 ), m ),
		}, isDetail );
	}

	/// <summary>The sealed 512x512x256 greybox room: floor, ceiling, 4 walls, spawn, light unless told otherwise.</summary>
	static IssueDocument SealedRoom( bool withLight = true, Vec3? spawnOrigin = null )
	{
		var doc = new IssueDocument { Name = "greybox-room" };
		doc.Brushes.Add( BoxBrush( 1, new Vec3( 0, 0, 0 ), new Vec3( 512, 512, 16 ) ) );
		doc.Brushes.Add( BoxBrush( 2, new Vec3( 0, 0, 240 ), new Vec3( 512, 512, 256 ) ) );
		doc.Brushes.Add( BoxBrush( 3, new Vec3( 0, 0, 0 ), new Vec3( 16, 512, 256 ) ) );
		doc.Brushes.Add( BoxBrush( 4, new Vec3( 496, 0, 0 ), new Vec3( 512, 512, 256 ) ) );
		doc.Brushes.Add( BoxBrush( 5, new Vec3( 0, 0, 0 ), new Vec3( 512, 16, 256 ) ) );
		doc.Brushes.Add( BoxBrush( 6, new Vec3( 0, 496, 0 ), new Vec3( 512, 512, 256 ) ) );
		doc.Entities.Add( new IssueEntity( 1, "info_player_start", spawnOrigin ?? new Vec3( 256, 256, 64 ) ) );
		if ( withLight )
			doc.Entities.Add( new IssueEntity( 2, "light", new Vec3( 256, 256, 200 ) ) );
		return doc;
	}

	static GreyboxChecklistItem Item( IReadOnlyList<GreyboxChecklistItem> items, string checkId ) =>
		items.First( i => i.CheckId == checkId );

	static string? FindFixture( string rel )
	{
		var candidates = new[]
		{
			Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", rel ) ),
			Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", rel ) ),
		};
		foreach ( var candidate in candidates )
			if ( File.Exists( candidate ) ) return candidate;
		var dir = new DirectoryInfo( Directory.GetCurrentDirectory() );
		for ( var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent )
		{
			var probe = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", rel );
			if ( File.Exists( probe ) ) return probe;
		}
		return null;
	}

	static Vec3 ReadVec3( JsonArray pair )
	{
		if ( pair.Count != 3 ) throw new Exception( $"Expected a [x, y, z] triple, got {pair.Count} values." );
		return new Vec3( (float)pair[0]!.GetValue<double>(), (float)pair[1]!.GetValue<double>(), (float)pair[2]!.GetValue<double>() );
	}

	/// <summary>Load a room fixture (brushes as min/max boxes, entities as class + origin) into an IssueDocument.</summary>
	static IssueDocument LoadRoomFixture( string rel )
	{
		var path = FindFixture( rel ) ?? throw new Exception( $"Fixture not found: {rel}" );
		if ( JsonNode.Parse( File.ReadAllText( path ) ) is not JsonObject json )
			throw new Exception( $"Fixture is not a JSON object: {path}" );
		var doc = new IssueDocument { Name = Path.GetFileNameWithoutExtension( path ) };
		if ( json["brushes"] is JsonArray brushes )
		{
			foreach ( var node in brushes )
			{
				if ( node is not JsonObject b ) continue;
				var id = b["id"]?.GetValue<int>() ?? 0;
				var min = ReadVec3( b["min"]!.AsArray() );
				var max = ReadVec3( b["max"]!.AsArray() );
				var isDetail = b["isDetail"]?.GetValue<bool>() ?? false;
				var material = b["material"]?.GetValue<string>();
				doc.Brushes.Add( BoxBrush( id, min, max, isDetail, material ) );
			}
		}
		if ( json["entities"] is JsonArray entities )
		{
			foreach ( var node in entities )
			{
				if ( node is not JsonObject e ) continue;
				var id = e["id"]?.GetValue<int>() ?? 0;
				var cls = e["class"]?.GetValue<string>() ?? "";
				var origin = ReadVec3( e["origin"]!.AsArray() );
				doc.Entities.Add( new IssueEntity( id, cls, origin ) );
			}
		}
		return doc;
	}

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "greyboxdefaults", "defaults are dev mode on and nodraw first", () =>
		{
			var defaults = new GreyboxDefaults();
			Check( defaults.DevTextureMode, "DevTextureMode should default on for the greybox phase." );
			Check( defaults.NodrawFirst, "NodrawFirst should default on: new brushes start nodraw." );
			Check( GreyboxDefaults.Default.DevTextureMode && GreyboxDefaults.Default.NodrawFirst,
				"GreyboxDefaults.Default should be dev mode on + nodraw first." );
		} );

		registry.Add( "greyboxdefaults", "a new brush is nodraw on all faces by default", () =>
		{
			var faces = new GreyboxDefaults().NewBrushFaceMaterials( 6 );
			Check( faces.Count == 6, $"new brush face count: {faces.Count}" );
			Check( faces.All( f => f.IsNoDraw ), "every new face should be nodraw by default." );
			Check( faces.All( f => f.MaterialName == ToolTexture.NoDrawName ),
				$"new face material: {faces[0].MaterialName}" );
			Check( faces.All( f => !f.ContributesToBakedMesh ),
				"nodraw faces contribute nothing to the baked mesh." );
			Check( FaceMaterial.BakedFaceIndices( faces ).Count == 0,
				"a fresh brush bakes zero faces — but the brush itself stays solid." );
		} );

		registry.Add( "greyboxdefaults", "nodraw-first off gives dev measure faces", () =>
		{
			var defaults = new GreyboxDefaults { NodrawFirst = false };
			var faces = defaults.NewBrushFaceMaterials( 6 );
			Check( faces.Count == 6, $"face count: {faces.Count}" );
			Check( faces.All( f => f.MaterialName == DevMaterialSet.MeasureMaterial ),
				$"visible greybox face material: {faces[0].MaterialName}" );
			Check( faces.All( f => f.ContributesToBakedMesh ),
				"dev-measure faces contribute to the baked mesh." );
			Check( FaceMaterial.BakedFaceIndices( faces ).Count == 6, "all 6 faces bake." );
		} );

		registry.Add( "greyboxdefaults", "dev mode off with nodraw-first off stays nodraw", () =>
		{
			var defaults = new GreyboxDefaults { NodrawFirst = false, DevTextureMode = false };
			var faces = defaults.NewBrushFaceMaterials( 4 );
			Check( faces.All( f => f.IsNoDraw ),
				"with dev mode off there is no visible default to fall back to, so faces stay nodraw." );
		} );

		registry.Add( "greyboxdefaults", "apply nodraw-first keeps UVs", () =>
		{
			var uv = new FaceUv( new Vec2( 0.25f, 0f ), new Vec2( 0.5f, 0.5f ), 90f, 16f, 1 );
			var faces = new FaceMaterial[]
			{
				new FaceMaterial( "brick/brickwall001", uv ),
				FaceMaterial.NoDraw,
			};
			var result = GreyboxDefaults.ApplyNodrawFirst( faces );
			Check( result.Count == 2, $"result count: {result.Count}" );
			Check( result[0].IsNoDraw && result[0].Uv.Equals( uv ),
				$"re-nodrawed face keeps its UV: {result[0]}" );
			Check( faces[0].MaterialName == "brick/brickwall001",
				"ApplyNodrawFirst must not mutate the input faces." );
			try { GreyboxDefaults.ApplyNodrawFirst( null! ); Check( false, "null list should throw." ); }
			catch ( ArgumentNullException ) { }
			try { GreyboxDefaults.ApplyNodrawFirst( new FaceMaterial?[] { null }! ); Check( false, "null face should throw." ); }
			catch ( ArgumentException ) { }
			try { new GreyboxDefaults().NewBrushFaceMaterials( -1 ); Check( false, "negative face count should throw." ); }
			catch ( ArgumentOutOfRangeException ) { }
			Check( new GreyboxDefaults().NewBrushFaceMaterials( 0 ).Count == 0, "zero faces returns an empty list." );
		} );

		registry.Add( "greyboxdefaults", "dev material set has a measure texture per role", () =>
		{
			var set = DevMaterialSet.Default;
			Check( set.Measure == "dev/dev_measuregeneric01", $"measure texture: {set.Measure}" );
			Check( set.MaterialFor( DevMaterialRole.Floor ) == "dev/dev_measuregeneric01", $"floor: {set.Floor}" );
			Check( set.MaterialFor( DevMaterialRole.Wall ) == "dev/dev_measuregeneric01b", $"wall: {set.Wall}" );
			Check( set.Floor != set.Wall, "floor and wall should use the two measure tones, not one flat colour." );
			foreach ( var name in set.DistinctNames )
				Check( name.StartsWith( "dev/", StringComparison.Ordinal ), $"dev set name should live under dev/: {name}" );
			var reskinned = set.With( DevMaterialRole.Wall, "dev/dev_measuregeneric01" );
			Check( reskinned.Wall == "dev/dev_measuregeneric01" && set.Wall == "dev/dev_measuregeneric01b",
				"With() returns a new set; the original is unchanged." );
			try { new DevMaterialSet( floor: "  " ); Check( false, "blank role material should throw." ); }
			catch ( ArgumentException ) { }
			try { set.MaterialFor( (DevMaterialRole)99 ); Check( false, "unknown role should throw." ); }
			catch ( ArgumentException ) { }
		} );

		registry.Add( "greyboxdefaults", "dev material lookup follows dev mode", () =>
		{
			var defaults = new GreyboxDefaults();
			Check( defaults.TryMaterialForRole( DevMaterialRole.Wall, out var wall ) && wall == "dev/dev_measuregeneric01b",
				$"wall material with dev mode on: {wall}" );
			defaults.DevTextureMode = false;
			Check( !defaults.TryMaterialForRole( DevMaterialRole.Wall, out _ ),
				"with dev mode off there is no dev material to apply." );
		} );

		registry.Add( "greyboxdefaults", "defaults survive a JSON round trip", () =>
		{
			var defaults = new GreyboxDefaults
			{
				DevTextureMode = false,
				NodrawFirst = false,
				DevMaterials = DevMaterialSet.Default.With( DevMaterialRole.Wall, "brick/brickwall001" ),
			};
			var back = GreyboxDefaults.FromJsonString( defaults.ToJsonString() );
			Check( back.DevTextureMode == false && back.NodrawFirst == false, "flags should round-trip." );
			Check( back.DevMaterials.Wall == "brick/brickwall001", $"wall should round-trip: {back.DevMaterials.Wall}" );
			Check( back.DevMaterials.Floor == DevMaterialSet.MeasureMaterial, "untouched roles keep their defaults." );
			var empty = GreyboxDefaults.FromJsonString( "{}" );
			Check( empty.DevTextureMode && empty.NodrawFirst, "missing fields fall back to the defaults." );
			Check( empty.DevMaterials.Measure == DevMaterialSet.MeasureMaterial, "missing dev set falls back to defaults." );
		} );

		registry.Add( "greyboxdefaults", "box brush helper has the analytic volume", () =>
		{
			var box = BoxBrush( 1, new Vec3( 0, 0, 0 ), new Vec3( 128, 64, 32 ) );
			Check( Near( box.Volume(), 128 * 64 * 32, 1.0 ),
				$"box volume should be 128*64*32=262144, got {box.Volume()}" );
			Check( box.Centroid().AlmostEquals( new Vec3( 64, 32, 16 ) ), $"box centroid: {box.Centroid()}" );
		} );

		registry.Add( "greyboxdefaults", "suggest func_detail marks trim as detail", () =>
		{
			var trim = GreyboxDefaults.SuggestFuncDetail( BoxBrush( 1, new Vec3( 0, 0, 0 ), new Vec3( 64, 4, 8 ) ) );
			Check( trim.IsDetailCandidate, $"64x4x8 trim should be a detail candidate: {trim}" );
			var strip = GreyboxDefaults.SuggestFuncDetail( BoxBrush( 2, new Vec3( 0, 0, 0 ), new Vec3( 256, 4, 8 ) ) );
			Check( strip.IsDetailCandidate, $"256x4x8 trim strip should be a detail candidate: {strip}" );
			var cube = GreyboxDefaults.SuggestFuncDetail( BoxBrush( 3, new Vec3( 0, 0, 0 ), new Vec3( 32, 32, 32 ) ) );
			Check( cube.IsDetailCandidate, $"32in cube should be a detail candidate: {cube}" );
			Check( trim.Reason.Length > 0 && strip.Reason.Length > 0, "a suggestion must carry its reason." );
		} );

		registry.Add( "greyboxdefaults", "suggest func_detail keeps sealing walls structural", () =>
		{
			var wall = GreyboxDefaults.SuggestFuncDetail( BoxBrush( 1, new Vec3( 0, 0, 0 ), new Vec3( 512, 16, 256 ) ) );
			Check( wall.Verdict == FuncDetailVerdict.Structural, $"512x16x256 sealing wall must stay structural: {wall}" );
			var slab = GreyboxDefaults.SuggestFuncDetail( BoxBrush( 2, new Vec3( 0, 0, 0 ), new Vec3( 256, 256, 16 ) ) );
			Check( slab.Verdict == FuncDetailVerdict.Structural, $"256x256x16 floor slab must stay structural: {slab}" );
			var none = GreyboxDefaults.SuggestFuncDetail( null! );
			Check( none.Verdict == FuncDetailVerdict.Structural, "a null brush is structural by default, never detail." );
			var empty = GreyboxDefaults.SuggestFuncDetail( new IssueBrush() );
			Check( empty.Verdict == FuncDetailVerdict.Structural, "a brush with no geometry is structural by default." );
			var flat = GreyboxDefaults.SuggestFuncDetail( BoxBrush( 3, new Vec3( 0, 0, 0 ), new Vec3( 128, 128, 0 ) ) );
			Check( flat.Verdict == FuncDetailVerdict.Structural, $"a zero-volume brush is structural: {flat}" );
			var alreadyDetail = GreyboxDefaults.SuggestFuncDetail( BoxBrush( 4, new Vec3( 0, 0, 0 ), new Vec3( 512, 16, 256 ), isDetail: true ) );
			Check( alreadyDetail.IsDetailCandidate, "an already-flagged detail wall stays a candidate." );
		} );

		registry.Add( "greyboxdefaults", "checklist flags a map with no light", () =>
		{
			var items = GreyboxChecklist.Check( SealedRoom( withLight: false ) );
			Check( items.Count == 3, $"checklist should always return 3 items, got {items.Count}" );
			Check( Item( items, "greybox-seal" ).Passed, "sealed room should pass the seal check." );
			Check( Item( items, "greybox-spawn" ).Passed, "room with a spawn should pass the spawn check." );
			var light = Item( items, "greybox-light" );
			Check( !light.Passed, "a map with no light must fail the light check." );
			Check( light.Severity == IssueSeverity.Warning, "a failed checklist check is a warning, not an error." );
			Check( light.FixHint.Length > 0, "a failed check must name its fix." );
		} );

		registry.Add( "greyboxdefaults", "checklist passes a sealed room with spawn and light", () =>
		{
			var items = GreyboxChecklist.Check( SealedRoom() );
			Check( items.All( i => i.Passed ), "sealed room + spawn + light should pass all three checks: " +
				string.Join( "; ", items.Select( i => i.ToString() ) ) );
			Check( items.Select( i => i.CheckId ).SequenceEqual( new[] { "greybox-seal", "greybox-spawn", "greybox-light" } ),
				"checklist order is seal / spawn / light." );
		} );

		registry.Add( "greyboxdefaults", "checklist fails an empty map three ways", () =>
		{
			var items = GreyboxChecklist.Check( new IssueDocument() );
			Check( items.All( i => !i.Passed ), "an empty map fails all three checks." );
			Check( items.Select( i => i.CheckId ).Distinct().Count() == 3, "each failure is a distinct check." );
			Check( items.All( i => i.Severity == IssueSeverity.Warning ), "checklist failures are warnings." );
			Check( Item( items, "greybox-seal" ).Message.Contains( "No brushes" ), $"seal message: {Item( items, "greybox-seal" ).Message}" );
			try { GreyboxChecklist.Check( null! ); Check( false, "null document should throw." ); }
			catch ( ArgumentNullException ) { }
		} );

		registry.Add( "greyboxdefaults", "checklist fails a spawn buried in solid", () =>
		{
			var items = GreyboxChecklist.Check( SealedRoom( spawnOrigin: new Vec3( 256, 256, 8 ) ) );
			var seal = Item( items, "greybox-seal" );
			Check( !seal.Passed, "a spawn inside the floor brush must fail the seal check." );
			Check( seal.Message.Contains( "inside solid brush" ), $"seal message: {seal.Message}" );
			Check( Item( items, "greybox-spawn" ).Passed, "the spawn entity exists, so the spawn check passes." );
			Check( Item( items, "greybox-light" ).Passed, "the light check is unaffected." );
		} );

		registry.Add( "greyboxdefaults", "greybox rules register on the engine and report", () =>
		{
			var engine = new IssueEngine();
			Check( engine.Rules.Count == 8, $"engine should start with 8 default rules, got {engine.Rules.Count}" );
			engine.Register( new GreyboxSealRule() );
			engine.Register( new GreyboxSpawnRule() );
			engine.Register( new GreyboxLightRule() );
			Check( engine.Rules.Count == 11, $"engine should have 11 rules after registering the greybox three, got {engine.Rules.Count}" );

			var good = engine.Run( SealedRoom() );
			Check( !good.Any( i => i.Code.StartsWith( "GREYBOX_", StringComparison.Ordinal ) ),
				"good room should report no greybox issue: " + string.Join( "; ", good.Select( i => i.ToString() ) ) );
			Check( !good.Any( i => i.Code == "LEAK" || i.Code == "UNSEALED_MAP" ),
				"good room should report no leak / unsealed issue: " + string.Join( "; ", good.Select( i => i.ToString() ) ) );

			var noLight = engine.Run( SealedRoom( withLight: false ) );
			Check( noLight.Count( i => i.Code == "GREYBOX_NO_LIGHT" ) == 1,
				"no-light room should report exactly one GREYBOX_NO_LIGHT: " + string.Join( "; ", noLight.Select( i => i.ToString() ) ) );
			Check( !noLight.Any( i => i.Code == "GREYBOX_UNSEALED" || i.Code == "GREYBOX_NO_SPAWN" ),
				"no-light room should report no other greybox issue." );

			var empty = engine.Run( new IssueDocument() );
			Check( empty.Count( i => i.Code == "GREYBOX_UNSEALED" ) == 1, "empty map should report GREYBOX_UNSEALED." );
			Check( empty.Count( i => i.Code == "GREYBOX_NO_SPAWN" ) == 1, "empty map should report GREYBOX_NO_SPAWN." );
			Check( empty.Count( i => i.Code == "GREYBOX_NO_LIGHT" ) == 1, "empty map should report GREYBOX_NO_LIGHT." );
			Check( empty.Count == 3, $"empty map should report exactly the three greybox issues, got {empty.Count}: " +
				string.Join( "; ", empty.Select( i => i.ToString() ) ) );
		} );

		registry.Add( "greyboxdefaults", "fixture room passes the checklist", () =>
		{
			var room = LoadRoomFixture( "hammer-greybox-workflow-defaults/greybox-room.json" );
			Check( room.Brushes.Count == 6 && room.Entities.Count == 2,
				$"greybox-room fixture should hold 6 brushes + 2 entities, got {room.Brushes.Count} + {room.Entities.Count}" );
			var items = GreyboxChecklist.Check( room );
			Check( items.All( i => i.Passed ), "fixture room should pass all three checks: " +
				string.Join( "; ", items.Select( i => i.ToString() ) ) );

			var dark = LoadRoomFixture( "hammer-greybox-workflow-defaults/no-light-room.json" );
			var darkItems = GreyboxChecklist.Check( dark );
			Check( Item( darkItems, "greybox-seal" ).Passed && Item( darkItems, "greybox-spawn" ).Passed,
				"no-light fixture should pass seal and spawn." );
			Check( !Item( darkItems, "greybox-light" ).Passed, "no-light fixture must fail the light check." );
		} );

		registry.Add( "greyboxdefaults", "fixture defaults load", () =>
		{
			var path = FindFixture( "hammer-greybox-workflow-defaults/defaults.json" )
				?? throw new Exception( "Fixture not found: hammer-greybox-workflow-defaults/defaults.json" );
			var defaults = GreyboxDefaults.FromJsonString( File.ReadAllText( path ) );
			Check( defaults.DevTextureMode == false, "fixture defaults should have dev mode off." );
			Check( defaults.NodrawFirst, "fixture defaults should have nodraw-first on." );
			Check( defaults.DevMaterials.Wall == "dev/dev_measuregeneric01",
				$"fixture wall role: {defaults.DevMaterials.Wall}" );
			Check( defaults.DevMaterials.Floor == DevMaterialSet.MeasureMaterial,
				"a role missing from the fixture JSON falls back to the default." );
		} );
	}
}
