#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;

// Hammer Desktop P2: hotspot texturing — match faces to pre-defined rectangles
// (trims / panels) from material metadata, apply the matching rect's UVs.
// HotspotRect (name, material, rect in the 0..1 texture square) + Match(face)
// aspect scoring + ApplyHotspot(face, rect) + MatchHotspots batch + deterministic
// randomise-shift, over the FaceMaterial / FaceUv model (hammer-face-material-uv-model)
// and the VMT bridge (hammer-material-bridge-pbr-vmt-gltf: $hotspot_<name> keys,
// preserved as unknown keys). MapBrush has NOT landed, so faces arrive as
// HotspotFace (FaceMaterial + corners + normal), not as brush faces.
// Fixtures live in tests/fixtures/hammer/hammer-hotspot-texturing/.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
// final integrator adds the one HotspotTexturingTests.Register(registry, settings); line.
internal static class HotspotTexturingTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }
	static bool Near( float a, float b, float eps = 1e-5f ) => MathF.Abs( a - b ) <= eps;
	static bool Near( Vec2 a, Vec2 b, float eps = 1e-5f ) => Near( a.x, b.x, eps ) && Near( a.y, b.y, eps );

	static void Expect<T>( Action action, string contains ) where T : Exception
	{
		try { action(); }
		catch ( T ex )
		{
			Check( ex.Message.Contains( contains ), $"expected {typeof( T ).Name} naming '{contains}', got: {ex.Message}" );
			return;
		}
		catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
		throw new Exception( $"expected {typeof( T ).Name} naming '{contains}', no exception was thrown" );
	}

	/// <summary>A w-wide, h-deep quad in the z=0 plane, normal +Z — projected U is X, V is Y.</summary>
	static Vec3[] Quad( float w, float h ) => new[]
	{
		new Vec3( 0f, 0f, 0f ), new Vec3( w, 0f, 0f ),
		new Vec3( w, h, 0f ), new Vec3( 0f, h, 0f ),
	};

	static readonly Vec3 Up = new( 0f, 0f, 1f );

	static string Fixture( string name )
	{
		const string dir = "hammer-hotspot-texturing";
		var candidates = new[]
		{
			Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", dir, name ) ),
			Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", dir, name ) ),
		};
		foreach ( var candidate in candidates )
			if ( File.Exists( candidate ) ) return File.ReadAllText( candidate );
		var walk = new DirectoryInfo( Directory.GetCurrentDirectory() );
		for ( var i = 0; i < 6 && walk is not null; i++, walk = walk.Parent )
		{
			var probe = Path.Combine( walk.FullName, "tests", "fixtures", "hammer", dir, name );
			if ( File.Exists( probe ) ) return File.ReadAllText( probe );
		}
		throw new Exception( "hotspot fixture missing: " + name );
	}

	static HotspotSet TrimSet() => HotspotSet.FromJsonString( Fixture( "trim-hotspots.json" ) );
	static HotspotSet PanelSet() => HotspotSet.FromJsonString( Fixture( "panel-hotspots.json" ) );

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "hotspottexturing", "trim face matches its rectangle with UVs 0..1 across it", () =>
		{
			var set = TrimSet();
			var verts = Quad( 128f, 16f );
			var face = FaceMaterial.ForMaterial( "trim/trim001" );
			var match = HotspotTexturing.Match( set.Rects, verts, Up );
			Check( match.Found, "128x16 trim face must match: " + match.Reason );
			Check( match.Rect!.Name == "trim_horizontal", "winner must be trim_horizontal, got " + match.Rect.Name );
			Check( !match.Rotated, "an 8:1 face against an 8:1 rect must not rotate" );
			Check( Near( match.Score, 1f ), $"exact aspect match must score 1, got {match.Score}" );
			var applied = HotspotTexturing.ApplyHotspot( face, verts, Up, match.Rect );
			Check( applied.MaterialName == "trim/trim001", "applied face must keep the trim material: " + applied.MaterialName );
			var uvs = applied.Uv.ComputeUvs( verts, Up );
			Check( uvs.Length == 4, $"UV count: {uvs.Length}" );
			Check( Near( uvs[0], new Vec2( 0f, 0.875f ) ), $"corner 0: {uvs[0]}" );
			Check( Near( uvs[1], new Vec2( 1f, 0.875f ) ), $"corner 1: {uvs[1]}" );
			Check( Near( uvs[2], new Vec2( 1f, 1f ) ), $"corner 2: {uvs[2]}" );
			Check( Near( uvs[3], new Vec2( 0f, 1f ) ), $"corner 3: {uvs[3]}" );
		} );

		registry.Add( "hotspottexturing", "panel face matches the square panel rect", () =>
		{
			var set = TrimSet();
			var verts = Quad( 64f, 64f );
			var match = HotspotTexturing.Match( set.Rects, verts, Up );
			Check( match.Found, "64x64 panel face must match: " + match.Reason );
			Check( match.Rect!.Name == "panel_square", "winner must be panel_square, got " + match.Rect.Name );
			var applied = HotspotTexturing.ApplyHotspot( FaceMaterial.ForMaterial( "trim/trim001" ), verts, Up, match.Rect );
			var uvs = applied.Uv.ComputeUvs( verts, Up );
			Check( Near( uvs[0], new Vec2( 0f, 0f ) ), $"corner 0: {uvs[0]}" );
			Check( Near( uvs[1], new Vec2( 0.5f, 0f ) ), $"corner 1: {uvs[1]}" );
			Check( Near( uvs[2], new Vec2( 0.5f, 0.5f ) ), $"corner 2: {uvs[2]}" );
			Check( Near( uvs[3], new Vec2( 0f, 0.5f ) ), $"corner 3: {uvs[3]}" );
		} );

		registry.Add( "hotspottexturing", "face with no fitting rectangle is left unchanged with a named result", () =>
		{
			var set = TrimSet();
			var verts = Quad( 100f, 30f );
			var original = FaceMaterial.ForMaterial( "brick/brickwall001" );
			var match = HotspotTexturing.Match( set.Rects, verts, Up );
			Check( !match.Found, "100x30 face (3.33:1) must fit nothing: " + match.Reason );
			Check( match.Outcome == HotspotMatchOutcome.NoFit, "outcome must be NoFit, got " + match.Outcome );
			Check( match.Reason.Contains( "No fit" ), "reason must name the NoFit outcome: " + match.Reason );
			Check( match.Reason.Contains( "panel_wide" ), "reason must name the closest rect: " + match.Reason );
			var results = HotspotTexturing.MatchHotspots(
				new[] { new HotspotFace( original, verts, Up ) }, set.Rects );
			Check( results.Count == 1, $"batch must return one result, got {results.Count}" );
			Check( !results[0].Changed, "a non-matching face must not change" );
			Check( ReferenceEquals( results[0].Face, original ),
				"a non-matching face must keep its original FaceMaterial instance, unchanged" );
		} );

		registry.Add( "hotspottexturing", "empty hotspot list reports NoHotspots by name", () =>
		{
			var match = HotspotTexturing.Match( new List<HotspotRect>(), 10f, 10f );
			Check( !match.Found, "an empty list must match nothing" );
			Check( match.Outcome == HotspotMatchOutcome.NoHotspots, "outcome must be NoHotspots, got " + match.Outcome );
			Check( match.Reason.Contains( "No hotspots" ), "reason must name the empty list: " + match.Reason );
		} );

		registry.Add( "hotspottexturing", "best of several rects wins by aspect", () =>
		{
			var set = TrimSet();
			var match = HotspotTexturing.Match( set.Rects, 120f, 16f );
			Check( match.Found, "120x16 face (7.5:1) must match: " + match.Reason );
			Check( match.Rect!.Name == "trim_horizontal", "winner must be trim_horizontal, got " + match.Rect.Name );
			Check( Near( match.Score, 0.9394f, 0.001f ), $"score for aspect error ln(8/7.5) must be ~0.9394, got {match.Score}" );
			var square = HotspotTexturing.Match( set.Rects, 64f, 64f );
			Check( square.Rect!.Name == "panel_square", "square face must pick panel_square, got " + square.Rect.Name );
		} );

		registry.Add( "hotspottexturing", "vertical trim face matches rotated and stays inside the rect", () =>
		{
			var horizontal = TrimSet().Find( "trim_horizontal" )!;
			var verts = Quad( 16f, 128f );
			var match = HotspotTexturing.Match( new[] { horizontal }, verts, Up );
			Check( match.Found, "16x128 face must match the 8:1 rect rotated: " + match.Reason );
			Check( match.Rotated, "a vertical face against a horizontal rect must rotate" );
			Check( Near( match.Score, 1f ), $"rotated exact aspect match must score 1, got {match.Score}" );
			var applied = HotspotTexturing.ApplyHotspot( FaceMaterial.ForMaterial( "trim/trim001" ), verts, Up, match.Rect, match.Rotated );
			Check( Near( applied.Uv.RotationDegrees, 90f ), $"rotated apply must bake a 90deg rotation, got {applied.Uv.RotationDegrees}" );
			var uvs = applied.Uv.ComputeUvs( verts, Up );
			Check( Near( uvs[0], new Vec2( 1f, 0.875f ) ), $"corner 0: {uvs[0]}" );
			Check( Near( uvs[1], new Vec2( 1f, 1f ) ), $"corner 1: {uvs[1]}" );
			Check( Near( uvs[2], new Vec2( 0f, 1f ) ), $"corner 2: {uvs[2]}" );
			Check( Near( uvs[3], new Vec2( 0f, 0.875f ) ), $"corner 3: {uvs[3]}" );
			foreach ( var uv in uvs )
				Check( horizontal.Contains( uv.x, uv.y ), $"rotated UV {uv} must stay inside trim_horizontal" );
		} );

		registry.Add( "hotspottexturing", "manual hotspot assignment applies the chosen rect", () =>
		{
			var set = TrimSet();
			var verts = Quad( 64f, 64f );
			var face = FaceMaterial.ForMaterial( "trim/trim001" );
			var applied = HotspotTexturing.ApplyNamedHotspot( face, verts, Up, "panel_wide", set.Rects );
			var uvs = applied.Uv.ComputeUvs( verts, Up );
			Check( Near( uvs[0], new Vec2( 0.5f, 0f ) ), $"corner 0: {uvs[0]}" );
			Check( Near( uvs[1], new Vec2( 1f, 0f ) ), $"corner 1: {uvs[1]}" );
			Check( Near( uvs[2], new Vec2( 1f, 0.25f ) ), $"corner 2: {uvs[2]}" );
			Check( Near( uvs[3], new Vec2( 0.5f, 0.25f ) ), $"corner 3: {uvs[3]}" );
			Expect<ArgumentException>( () =>
				HotspotTexturing.ApplyNamedHotspot( face, verts, Up, "nope", set.Rects ), "No hotspot named 'nope'" );
		} );

		registry.Add( "hotspottexturing", "two-argument ApplyHotspot maps the fit unit square onto the rect", () =>
		{
			var rect = TrimSet().Find( "trim_horizontal" )!;
			var fitted = new FaceMaterial( "trim/trim001", FaceUv.AlignFit( 0f, 128f, 0f, 16f ) );
			var applied = HotspotTexturing.ApplyHotspot( fitted, rect );
			Check( Near( applied.Uv.Offset, new Vec2( 0f, 0.875f ) ), $"offset: {applied.Uv.Offset}" );
			Check( Near( applied.Uv.Scale, new Vec2( 1f, 0.125f ) ), $"scale: {applied.Uv.Scale}" );
			Check( Near( applied.Uv.Transform( new Vec2( 0f, 0f ) ), new Vec2( 0f, 0.875f ) ), "unit (0,0) must land on the rect min" );
			Check( Near( applied.Uv.Transform( new Vec2( 1f, 1f ) ), new Vec2( 1f, 1f ) ), "unit (1,1) must land on the rect max" );
			Check( Near( applied.Uv.LightmapScale, 16f ), "lightmap scale must survive the apply" );
		} );

		registry.Add( "hotspottexturing", "randomise-shift varies UV offset per face deterministically by seed", () =>
		{
			var set = TrimSet();
			HotspotFace[] Faces() => new[]
			{
				new HotspotFace( FaceMaterial.NoDraw, Quad( 128f, 16f ), Up ),
				new HotspotFace( FaceMaterial.NoDraw, Quad( 128f, 16f ), Up ),
			};
			var shifted = HotspotTexturing.MatchHotspots( Faces(), set.Rects, HotspotOptions.Randomised( 7 ) );
			Check( shifted[0].Changed && shifted[1].Changed, "both trim faces must match" );
			var off0 = shifted[0].Face.Uv.Offset;
			var off1 = shifted[1].Face.Uv.Offset;
			Check( !Near( off0, off1 ), $"seed 7 must shift the two identical faces differently: {off0} vs {off1}" );
			var again = HotspotTexturing.MatchHotspots( Faces(), set.Rects, HotspotOptions.Randomised( 7 ) );
			Check( Near( again[0].Face.Uv.Offset, off0 ) && Near( again[1].Face.Uv.Offset, off1 ),
				"the same seed must reproduce the same offsets" );
			var other = HotspotTexturing.MatchHotspots( Faces(), set.Rects, HotspotOptions.Randomised( 8 ) );
			Check( !Near( other[0].Face.Uv.Offset, off0 ) || !Near( other[1].Face.Uv.Offset, off1 ),
				"a different seed must vary the offsets" );
			var plain = HotspotTexturing.MatchHotspots( Faces(), set.Rects );
			Check( Near( plain[0].Face.Uv.Offset, new Vec2( 0f, 0.875f ) ), $"shift off must land exactly on the rect, got {plain[0].Face.Uv.Offset}" );
			var shift = new Vec2( off0.x - 0f, off0.y - 0.875f );
			Check( MathF.Abs( shift.x ) <= 0.1f + 1e-5f, $"shift U must stay within MaxShift x rect width (0.1), got {shift.x}" );
			Check( MathF.Abs( shift.y ) <= 0.1f * 0.125f + 1e-5f, $"shift V must stay within MaxShift x rect height (0.0125), got {shift.y}" );
		} );

		registry.Add( "hotspottexturing", "trim fixture loads: names, materials and rects", () =>
		{
			var set = TrimSet();
			Check( set.MaterialName == "trim/trim001", "set material must be trim/trim001, got " + set.MaterialName );
			Check( set.Rects.Count == 4, $"trim set must hold 4 rects, got {set.Rects.Count}" );
			var horizontal = set.Find( "trim_horizontal" );
			Check( horizontal is not null, "trim_horizontal must be in the set" );
			Check( Near( horizontal!.Aspect, 8f ), $"trim_horizontal aspect must be 8, got {horizontal.Aspect}" );
			Check( Near( set.Find( "panel_square" )!.Aspect, 1f ), "panel_square aspect must be 1" );
			Check( Near( set.Find( "panel_wide" )!.Aspect, 2f ), "panel_wide aspect must be 2" );
			Check( set.Find( "TRIM_HORIZONTAL" ) is not null, "Find must be case-insensitive" );
		} );

		registry.Add( "hotspottexturing", "panel fixture loads with material defaulted from the set", () =>
		{
			var set = PanelSet();
			Check( set.Rects.Count == 3, $"panel set must hold 3 rects, got {set.Rects.Count}" );
			foreach ( var rect in set.Rects )
				Check( rect.MaterialName == "panel/panel001",
					$"rect '{rect.Name}' must default its material from the set, got {rect.MaterialName}" );
			var match = HotspotTexturing.Match( set.Rects, Quad( 64f, 64f ), Up );
			Check( match.Found && match.Rect!.Name == "panel_a",
				"a square face against two equal 1:1 rects must take panel_a on the name-order tie-break: " + match.Reason );
		} );

		registry.Add( "hotspottexturing", "VMT hotspot keys round-trip through the bridge", () =>
		{
			var vmt = VmtMaterial.Parse( Fixture( "trim.vmt" ) );
			Check( vmt.Get( "$custom_vendor_key" ) == "keepme", "unknown VMT key must survive parsing" );
			var set = HotspotSet.FromVmt( vmt, "trim/trim001" );
			Check( set.Rects.Count == 3, $"VMT must carry 3 hotspots, got {set.Rects.Count}" );
			var horizontal = set.Find( "trim_horizontal" );
			Check( horizontal is not null, "trim_horizontal must come out of the VMT" );
			Check( Near( horizontal!.UMin, 0f ) && Near( horizontal.VMin, 0.875f ) &&
				Near( horizontal.UMax, 1f ) && Near( horizontal.VMax, 1f ),
				"trim_horizontal rect must round-trip through the VMT: " + horizontal );
			var rewritten = new VmtMaterial();
			set.WriteToVmt( rewritten );
			var back = HotspotSet.FromVmt( VmtMaterial.Parse( rewritten.ToText() ), "trim/trim001" );
			Check( back.Rects.Count == 3, $"rewritten VMT must carry 3 hotspots, got {back.Rects.Count}" );
			foreach ( var rect in set.Rects )
			{
				var again = back.Find( rect.Name );
				Check( again is not null && again.Equals( rect ), $"rect '{rect.Name}' must survive the VMT rewrite" );
			}
		} );

		registry.Add( "hotspottexturing", "invalid rects and nulls are named, never silent", () =>
		{
			Expect<ArgumentException>( () => new HotspotRect( "x", "trim/trim001", 0.5f, 0f, 0.25f, 1f ), "UMax (0.25) must be greater than UMin (0.5)" );
			Expect<ArgumentException>( () => new HotspotRect( "x", "trim/trim001", 0f, 0f, 1f, 1.5f ), "must sit inside the 0..1 texture square" );
			Expect<ArgumentException>( () => new HotspotRect( "  ", "trim/trim001", 0f, 0f, 1f, 1f ), "needs a name" );
			Expect<ArgumentException>( () => new HotspotRect( "x", "trim/trim001", 0f, 0f, 0f, 1f ), "UMax (0) must be greater than UMin (0)" );
			Expect<ArgumentNullException>( () => HotspotTexturing.ApplyHotspot( null!, TrimSet().Find( "trim_horizontal" )! ), "face" );
			Expect<ArgumentNullException>( () => HotspotTexturing.Match( null!, 1f, 1f ), "hotspots" );
			Expect<ArgumentException>( () =>
				HotspotTexturing.ApplyHotspot( FaceMaterial.ForMaterial( "trim/trim001" ),
					TrimSet().Find( "trim_horizontal" )!, 0f, 10f, 5f, 5f ), "zero V extent" );
			Expect<ArgumentException>( () =>
				HotspotTexturing.Match( TrimSet().Rects, 0f, 10f ), "face width is 0" );
		} );

		registry.Add( "hotspottexturing", "MatchHotspots batch: matched faces change, non-matching faces stay", () =>
		{
			var set = TrimSet();
			var nodrawTrim = FaceMaterial.NoDraw;
			var trimPanel = FaceMaterial.ForMaterial( "trim/trim001" );
			var brickWide = FaceMaterial.ForMaterial( "brick/brickwall001" );
			var faces = new[]
			{
				new HotspotFace( nodrawTrim, Quad( 128f, 16f ), Up ),
				new HotspotFace( trimPanel, Quad( 64f, 64f ), Up ),
				new HotspotFace( brickWide, Quad( 100f, 30f ), Up ),
			};
			var results = HotspotTexturing.MatchHotspots( faces, set.Rects );
			Check( results.Count == 3, $"batch must return one result per face, got {results.Count}" );
			Check( results[0].Changed && results[0].Face.MaterialName == "trim/trim001",
				"the nodraw trim face must be textured by its hotspot: " + results[0].Match.Reason );
			Check( results[1].Changed && results[1].Match.Rect!.Name == "panel_square",
				"the panel face must match panel_square: " + results[1].Match.Reason );
			Check( !results[2].Changed, "the 100x30 face must not change: " + results[2].Match.Reason );
			Check( ReferenceEquals( results[2].Face, brickWide ), "the non-matching face must keep its original instance" );
			Check( results.Count( r => r.Changed ) == 2, "exactly 2 of 3 faces must change" );
		} );

		registry.Add( "hotspottexturing", "JSON round-trip preserves a hotspot set", () =>
		{
			var set = TrimSet();
			var back = HotspotSet.FromJsonString( set.ToJsonString() );
			Check( back.MaterialName == set.MaterialName, "material must round-trip" );
			Check( back.Rects.Count == set.Rects.Count, "rect count must round-trip" );
			for ( var i = 0; i < set.Rects.Count; i++ )
				Check( back.Rects[i].Equals( set.Rects[i] ), $"rect {i} must round-trip: {set.Rects[i]} vs {back.Rects[i]}" );
			var single = HotspotRect.FromJson( (JsonObject)JsonNode.Parse( "{\"name\":\"t\",\"material\":\"m/m\",\"rect\":[0,0.5,1,0.75]}" )! );
			Check( single.Name == "t" && single.MaterialName == "m/m", "single rect JSON must parse" );
			Check( Near( single.Aspect, 4f ), $"single rect aspect must be 4, got {single.Aspect}" );
		} );
	}
}
