#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;

// Hammer Desktop P1: per-face material / UV model (Face Edit data, nodraw, tool textures).
// FaceMaterial (material name + FaceUv) + ToolTexture (registry with compile flags).
// MapBrush (hammer-brush-core-plane-based-brushes) had NOT landed when this was written —
// no MapBrush type exists in the tree yet — so these checks build against the per-face
// record its filed spec describes (material id + UV offset/scale/rotation + lightmap scale
// + smoothing group) and against plain face geometry, not against MapBrush itself. When
// MapBrush lands it stores one FaceMaterial per face and these ops apply unchanged.
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and the
// final integrator adds the one FaceMaterialTests.Register(registry, settings); line.
internal static class FaceMaterialTests
{
	static void Check( bool ok, string message ) { if ( !ok ) throw new Exception( message ); }
	static bool Near( float a, float b, float eps = 1e-5f ) => MathF.Abs( a - b ) <= eps;
	static bool Near( Vec2 a, Vec2 b, float eps = 1e-5f ) => Near( a.x, b.x, eps ) && Near( a.y, b.y, eps );

	/// <summary>A 2-wide, 1-deep quad in the z=0 plane, normal +Z — the face Fit is measured on.</summary>
	static Vec3[] Quad() => new[]
	{
		new Vec3( 0f, 0f, 0f ), new Vec3( 2f, 0f, 0f ),
		new Vec3( 2f, 1f, 0f ), new Vec3( 0f, 1f, 0f ),
	};

	static string? FindFixture()
	{
		const string rel = "hammer-face-material-uv-model/box-faces.json";
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

	public static void Register( TestRegistry registry, TestSettings settings )
	{
		registry.Add( "facematerial", "fit maps UVs 0..1 across a quad face", () =>
		{
			var verts = Quad();
			var normal = new Vec3( 0f, 0f, 1f );
			var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( verts, normal );
			Check( Near( uMin, 0f ) && Near( uMax, 2f ) && Near( vMin, 0f ) && Near( vMax, 1f ),
				$"projected bounds: u {uMin}..{uMax}, v {vMin}..{vMax}" );
			var fit = FaceUv.AlignFit( uMin, uMax, vMin, vMax );
			Check( Near( fit.Scale, new Vec2( 0.5f, 1f ) ), $"fit scale: {fit.Scale}" );
			Check( Near( fit.Offset, Vec2.Zero ), $"fit offset: {fit.Offset}" );
			Check( Near( fit.RotationDegrees, 0f ), $"fit rotation: {fit.RotationDegrees}" );
			var uvs = fit.ComputeUvs( verts, normal );
			Check( uvs.Length == 4, $"fit UV count: {uvs.Length}" );
			Check( Near( uvs[0], new Vec2( 0f, 0f ) ), $"corner 0: {uvs[0]}" );
			Check( Near( uvs[1], new Vec2( 1f, 0f ) ), $"corner 1: {uvs[1]}" );
			Check( Near( uvs[2], new Vec2( 1f, 1f ) ), $"corner 2: {uvs[2]}" );
			Check( Near( uvs[3], new Vec2( 0f, 1f ) ), $"corner 3: {uvs[3]}" );
		} );

		registry.Add( "facematerial", "rotation 90deg swaps UV axes", () =>
		{
			var rot90 = new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 90f, 16f, 0 );
			Check( Near( rot90.Transform( new Vec2( 1f, 0f ) ), new Vec2( 0f, 1f ) ),
				$"90deg of (1,0): {rot90.Transform( new Vec2( 1f, 0f ) )}" );
			Check( Near( rot90.Transform( new Vec2( 0f, 1f ) ), new Vec2( -1f, 0f ) ),
				$"90deg of (0,1): {rot90.Transform( new Vec2( 0f, 1f ) )}" );
			// Scale applies before rotation: (2,0) at half U scale lands on (0,1), not (0,2).
			var scaled = new FaceUv( Vec2.Zero, new Vec2( 0.5f, 1f ), 90f, 16f, 0 );
			Check( Near( scaled.Transform( new Vec2( 2f, 0f ) ), new Vec2( 0f, 1f ) ),
				$"scaled 90deg of (2,0): {scaled.Transform( new Vec2( 2f, 0f ) )}" );
			// And on a real face: a fitted quad rotated 90deg keeps the 0..1 range, swapped.
			var fit = FaceUv.AlignFit( 0f, 2f, 0f, 1f );
			var rotated = new FaceUv( fit.Offset, fit.Scale, 90f, fit.LightmapScale, fit.SmoothingGroup );
			var uvs = rotated.ComputeUvs( Quad(), new Vec3( 0f, 0f, 1f ) );
			Check( Near( uvs[1], new Vec2( 0f, 1f ) ), $"rotated corner 1: {uvs[1]}" );
			Check( Near( uvs[2], new Vec2( -1f, 1f ) ), $"rotated corner 2: {uvs[2]}" );
		} );

		registry.Add( "facematerial", "nodraw face is excluded from the baked mesh but kept in the brush", () =>
		{
			var faces = new[]
			{
				FaceMaterial.ForMaterial( "brick/brickwall001" ),
				FaceMaterial.ForMaterial( "metal/metalwall001" ),
				FaceMaterial.NoDraw,
				FaceMaterial.ForMaterial( "tools/toolsskybox" ),
			};
			Check( !faces[2].ContributesToBakedMesh && faces[2].IsNoDraw, "nodraw face must not bake" );
			Check( faces[0].ContributesToBakedMesh && faces[1].ContributesToBakedMesh,
				"ordinary faces must bake" );
			Check( faces[3].ContributesToBakedMesh, "skybox is a tool texture but not nodraw, so it bakes" );
			var baked = FaceMaterial.BakedFaceIndices( faces );
			Check( baked.Count == 3 && baked.SequenceEqual( new[] { 0, 1, 3 } ),
				$"baked indices: {string.Join( ",", baked )}" );
			Check( faces.Length == 4, "the nodraw face stays in the brush list — exclusion is bake-only" );
		} );

		registry.Add( "facematerial", "SetMaterial replaces the material and keeps the UV", () =>
		{
			var uv = new FaceUv( new Vec2( 0.25f, -0.5f ), new Vec2( 2f, 0.5f ), 45f, 8f, 3 );
			var face = new FaceMaterial( "brick/brickwall001", uv );
			var retargeted = FaceMaterial.SetMaterial( face, "metal/metalwall001" );
			Check( retargeted.MaterialName == "metal/metalwall001", $"new material: {retargeted.MaterialName}" );
			Check( retargeted.Uv.Equals( uv ), $"UV changed by SetMaterial: {retargeted.Uv}" );
			Check( face.MaterialName == "brick/brickwall001" && face.Uv.Equals( uv ),
				"SetMaterial must not mutate the input face" );
			Check( face.WithMaterial( "concrete/concretefloor001" ).MaterialName == "concrete/concretefloor001",
				"WithMaterial must agree with SetMaterial" );
		} );

		registry.Add( "facematerial", "ApplyCurrentTexture paints every face of a brush", () =>
		{
			var faces = new[]
			{
				new FaceMaterial( "brick/brickwall001", new FaceUv( new Vec2( 0.1f, 0f ), new Vec2( 1f, 1f ), 0f, 16f, 1 ) ),
				FaceMaterial.NoDraw,
				new FaceMaterial( "metal/metalwall001", new FaceUv( Vec2.Zero, new Vec2( 2f, 2f ), 90f, 8f, 2 ) ),
			};
			var painted = FaceMaterial.ApplyCurrentTexture( faces, "concrete/concretefloor001" );
			Check( painted.Count == 3 && painted.All( f => f.MaterialName == "concrete/concretefloor001" ),
				$"painted materials: {string.Join( ",", painted.Select( f => f.MaterialName ) )}" );
			Check( Near( painted[0].Uv.Offset, new Vec2( 0.1f, 0f ) ) && Near( painted[2].Uv.Scale, new Vec2( 2f, 2f ) )
				&& Near( painted[2].Uv.RotationDegrees, 90f ),
				"ApplyCurrentTexture must keep each face's own UV" );
			Check( faces[1].IsNoDraw, "the input list is unchanged by ApplyCurrentTexture" );
		} );

		registry.Add( "facematerial", "tool texture registry resolves every named tool texture", () =>
		{
			var expected = new (string name, ToolTextureKind kind, ToolCompileFlags flags)[]
			{
				("tools/toolsnodraw", ToolTextureKind.NoDraw, ToolCompileFlags.NoDraw),
				("tools/toolsskybox", ToolTextureKind.Skybox, ToolCompileFlags.Sky | ToolCompileFlags.NonSolid),
				("tools/toolshint", ToolTextureKind.Hint, ToolCompileFlags.Hint | ToolCompileFlags.NonSolid),
				("tools/toolsskip", ToolTextureKind.Skip, ToolCompileFlags.Skip | ToolCompileFlags.NonSolid),
				("tools/toolstrigger", ToolTextureKind.Trigger, ToolCompileFlags.Trigger | ToolCompileFlags.NonSolid),
				("tools/toolsclip", ToolTextureKind.Clip, ToolCompileFlags.Clip | ToolCompileFlags.NonSolid),
				("tools/toolsplayerclip", ToolTextureKind.PlayerClip, ToolCompileFlags.Clip | ToolCompileFlags.NonSolid),
				("tools/toolsnpcclip", ToolTextureKind.NpcClip, ToolCompileFlags.Clip | ToolCompileFlags.NonSolid),
				("tools/toolsareaportal", ToolTextureKind.AreaPortal, ToolCompileFlags.AreaPortal | ToolCompileFlags.NonSolid),
				("tools/toolsoccluder", ToolTextureKind.Occluder, ToolCompileFlags.Occluder | ToolCompileFlags.NonSolid),
				("tools/toolsblocklight", ToolTextureKind.BlockLight, ToolCompileFlags.BlockLight),
				("tools/toolsblock_los", ToolTextureKind.BlockLos, ToolCompileFlags.BlockLos),
				("tools/toolsorigin", ToolTextureKind.Origin, ToolCompileFlags.Origin | ToolCompileFlags.NonSolid),
				("tools/toolsladder", ToolTextureKind.Ladder, ToolCompileFlags.NonSolid),
				("tools/toolsinvisible", ToolTextureKind.Invisible, ToolCompileFlags.NonSolid),
				("tools/toolsinvisibleladder", ToolTextureKind.Ladder, ToolCompileFlags.NonSolid),
				("tools/toolsfog", ToolTextureKind.Fog, ToolCompileFlags.NonSolid),
			};
			Check( ToolTexture.All.Count == expected.Length,
				$"registry size: {ToolTexture.All.Count}, expected {expected.Length}" );
			foreach ( var (name, kind, flags) in expected )
			{
				var tool = ToolTexture.Lookup( name );
				Check( tool is not null, $"missing tool texture: {name}" );
				Check( tool!.Kind == kind, $"{name} kind: {tool.Kind}, expected {kind}" );
				Check( tool.CompileFlags == flags, $"{name} flags: {tool.CompileFlags}, expected {flags}" );
			}
			var nodraw = ToolTexture.Lookup( "tools/toolsnodraw" )!;
			Check( nodraw.DropsFromBakedMesh && nodraw.IsSolid,
				"nodraw must be NoDraw but still solid — the brush stays solid, only the face drops" );
			Check( ToolTexture.Lookup( "tools/toolsblocklight" )!.IsSolid,
				"blocklight must stay solid" );
			Check( ToolTexture.IsTool( "tools/toolstrigger" ) && !ToolTexture.IsTool( "brick/brickwall001" ),
				"IsTool must separate tool textures from ordinary materials" );
		} );

		registry.Add( "facematerial", "tool texture lookup is case-insensitive and rejects unknown names", () =>
		{
			Check( ToolTexture.Lookup( "Tools/ToolsNoDraw" )?.Kind == ToolTextureKind.NoDraw,
				"lookup must ignore case" );
			Check( ToolTexture.Lookup( @"tools\toolsskybox" )?.Kind == ToolTextureKind.Skybox,
				"lookup must tolerate backslashes" );
			Check( ToolTexture.Lookup( "materials/tools/toolsnodraw" )?.Kind == ToolTextureKind.NoDraw,
				"lookup must tolerate a leading materials/ prefix" );
			Check( ToolTexture.Lookup( "brick/brickwall001" ) is null, "ordinary material is not a tool texture" );
			Check( ToolTexture.Lookup( "tools/toolsbanana" ) is null, "unknown tools/ name is not a tool texture" );
			Check( ToolTexture.Lookup( "" ) is null && ToolTexture.Lookup( "   " ) is null
				&& ToolTexture.Lookup( null! ) is null, "blank names are not tool textures" );
			Check( !ToolTexture.IsNoDraw( "brick/brickwall001" ) && ToolTexture.IsNoDraw( "tools/toolsnodraw" ),
				"IsNoDraw must agree with the registry" );
		} );

		registry.Add( "facematerial", "FaceUv validation names zero scale and bad lightmap scale", () =>
		{
			Check( FaceUv.Default.IsValid && FaceUv.Default.Validate().Count == 0,
				$"default UV must be valid: {string.Join( "; ", FaceUv.Default.Validate() )}" );
			var zeroU = new FaceUv( Vec2.Zero, new Vec2( 0f, 1f ), 0f, 16f, 0 );
			Check( zeroU.Validate().Any( d => d.Contains( "scale X", StringComparison.Ordinal ) ),
				$"zero U scale defects: {string.Join( "; ", zeroU.Validate() )}" );
			var zeroV = new FaceUv( Vec2.Zero, new Vec2( 1f, 0f ), 0f, 16f, 0 );
			Check( zeroV.Validate().Any( d => d.Contains( "scale Y", StringComparison.Ordinal ) ),
				$"zero V scale defects: {string.Join( "; ", zeroV.Validate() )}" );
			var badLight = new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 0f, 0 );
			Check( badLight.Validate().Any( d => d.Contains( "Lightmap", StringComparison.Ordinal ) ),
				$"zero lightmap defects: {string.Join( "; ", badLight.Validate() )}" );
			var badGroup = new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 16f, -1 );
			Check( badGroup.Validate().Any( d => d.Contains( "Smoothing", StringComparison.Ordinal ) ),
				$"negative smoothing defects: {string.Join( "; ", badGroup.Validate() )}" );
		} );

		registry.Add( "facematerial", "AlignJustify anchors land on the matching UV edge or centre", () =>
		{
			var uv = new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 16f, 0 );
			// Bounds u 0..4, v 0..2: each anchor point must map to its target UV.
			Check( Near( uv.AlignJustify( UvJustify.Left, 0f, 4f, 0f, 2f ).Transform( new Vec2( 0f, 1f ) ), new Vec2( 0f, 0.5f ) ),
				"Left: face left edge must land on U 0, V centred" );
			Check( Near( uv.AlignJustify( UvJustify.Right, 0f, 4f, 0f, 2f ).Transform( new Vec2( 4f, 1f ) ), new Vec2( 1f, 0.5f ) ),
				"Right: face right edge must land on U 1, V centred" );
			Check( Near( uv.AlignJustify( UvJustify.Top, 0f, 4f, 0f, 2f ).Transform( new Vec2( 2f, 2f ) ), new Vec2( 0.5f, 1f ) ),
				"Top: face top edge must land on V 1, U centred" );
			Check( Near( uv.AlignJustify( UvJustify.Bottom, 0f, 4f, 0f, 2f ).Transform( new Vec2( 2f, 0f ) ), new Vec2( 0.5f, 0f ) ),
				"Bottom: face bottom edge must land on V 0, U centred" );
			Check( Near( uv.AlignJustify( UvJustify.Center, 0f, 4f, 0f, 2f ).Transform( new Vec2( 2f, 1f ) ), new Vec2( 0.5f, 0.5f ) ),
				"Center: face centre must land on (0.5, 0.5)" );
			Check( uv.AlignJustify( UvJustify.Fit, 0f, 4f, 0f, 2f ).Equals( FaceUv.AlignFit( 0f, 4f, 0f, 2f ) ),
				"Justify Fit must be exactly AlignFit" );
			// Exact with rotation too: the offset is solved, not guessed.
			var rotated = new FaceUv( Vec2.Zero, new Vec2( 2f, 2f ), 90f, 16f, 0 );
			Check( Near( rotated.AlignJustify( UvJustify.Left, 0f, 4f, 0f, 2f ).Transform( new Vec2( 0f, 1f ) ), new Vec2( 0f, 0.5f ) ),
				"Left must stay exact with a 90deg rotation and scale 2" );
		} );

		registry.Add( "facematerial", "FaceMaterial survives a JSON round trip", () =>
		{
			var uv = new FaceUv( new Vec2( 0.25f, -0.5f ), new Vec2( 2f, 0.5f ), 45f, 8f, 3 );
			var face = new FaceMaterial( "brick/brickwall001", uv );
			var back = FaceMaterial.FromJsonString( face.ToJsonString() );
			Check( back.Equals( face ), $"round trip changed the face: {back}" );
			Check( back.MaterialName == "brick/brickwall001" && Near( back.Uv.RotationDegrees, 45f )
				&& Near( back.Uv.LightmapScale, 8f ) && back.Uv.SmoothingGroup == 3
				&& Near( back.Uv.Offset, new Vec2( 0.25f, -0.5f ) ) && Near( back.Uv.Scale, new Vec2( 2f, 0.5f ) ),
				$"round-tripped fields: {back}" );
			var nodrawBack = FaceMaterial.FromJsonString( FaceMaterial.NoDraw.ToJsonString() );
			Check( nodrawBack.IsNoDraw && nodrawBack.Uv.Equals( FaceUv.Default ),
				$"nodraw round trip: {nodrawBack}" );
			var noUv = FaceMaterial.FromJson( new JsonObject { ["material"] = "metal/metalwall001" } );
			Check( noUv.Uv.Equals( FaceUv.Default ), "a missing uv block must fall back to the default UV" );
		} );

		registry.Add( "facematerial", "Fit rejects a degenerate face with a named error", () =>
		{
			var exU = AssertThrows<ArgumentException>( () => FaceUv.AlignFit( 1f, 1f, 0f, 2f ) );
			Check( exU.Message.Contains( "U extent", StringComparison.Ordinal ), $"zero-U error: {exU.Message}" );
			var exV = AssertThrows<ArgumentException>( () => FaceUv.AlignFit( 0f, 2f, 1f, 1f ) );
			Check( exV.Message.Contains( "V extent", StringComparison.Ordinal ), $"zero-V error: {exV.Message}" );
			var exN = AssertThrows<ArgumentException>( () =>
				FaceUv.ProjectedBounds( Quad(), new Vec3( 0f, 0f, 0f ) ) );
			Check( exN.Message.Contains( "normal", StringComparison.Ordinal ), $"zero-normal error: {exN.Message}" );
			var exE = AssertThrows<ArgumentException>( () =>
				FaceUv.ProjectedBounds( Array.Empty<Vec3>(), new Vec3( 0f, 0f, 1f ) ) );
			Check( exE.Message.Contains( "no vertices", StringComparison.Ordinal ), $"empty-face error: {exE.Message}" );
		} );

		registry.Add( "facematerial", "SetMaterial rejects a blank material name", () =>
		{
			var face = FaceMaterial.ForMaterial( "brick/brickwall001" );
			var ex = AssertThrows<ArgumentException>( () => FaceMaterial.SetMaterial( face, "   " ) );
			Check( ex.Message.Contains( "blank", StringComparison.Ordinal ), $"blank-name error: {ex.Message}" );
			_ = AssertThrows<ArgumentNullException>( () => FaceMaterial.SetMaterial( null!, "metal/metalwall001" ) );
			_ = AssertThrows<ArgumentException>( () => new FaceMaterial( "" ) );
			_ = AssertThrows<ArgumentException>( () => FaceMaterial.FromJsonString( "{}" ) );
			_ = AssertThrows<ArgumentException>( () => FaceMaterial.ApplyCurrentTexture( new[] { face }, "" ) );
		} );

		registry.Add( "facematerial", "default face material is nodraw", () =>
		{
			Check( FaceMaterial.Default.MaterialName == ToolTexture.NoDrawName,
				$"default material: {FaceMaterial.Default.MaterialName}" );
			Check( FaceMaterial.Default.IsNoDraw && !FaceMaterial.Default.ContributesToBakedMesh,
				"the default face must be nodraw and must not bake" );
			Check( FaceMaterial.Default.Uv.Equals( FaceUv.Default ), "the default face carries the default UV" );
			Check( Near( FaceUv.Default.LightmapScale, 16f ) && FaceUv.Default.SmoothingGroup == 0,
				"Hammer default lightmap scale is 16, smoothing group 0" );
		} );

		registry.Add( "facematerial", "box fixture round-trips: 6 faces, 5 baked",
			() =>
			{
				var path = FindFixture();
				Check( path is not null, "box-faces.json fixture missing" );
				var root = JsonNode.Parse( File.ReadAllText( path! ) ) as JsonObject;
				Check( root is not null, "fixture is not a JSON object" );
				var faces = root!["faces"]!.AsArray()
					.Select( n => FaceMaterial.FromJson( (JsonObject)n! ) ).ToList();
				Check( faces.Count == root!["expectedFaceCount"]!.GetValue<int>(),
					$"fixture faces: {faces.Count}" );
				var baked = FaceMaterial.BakedFaceIndices( faces );
				Check( baked.Count == root["expectedBakedFaces"]!.GetValue<int>(),
					$"fixture baked faces: {baked.Count}" );
				Check( faces[^1].IsNoDraw && faces[^1].MaterialName == "tools/toolsnodraw",
					$"fixture bottom face: {faces[^1]}" );
				Check( Near( faces[2].Uv.RotationDegrees, 90f ) && Near( faces[2].Uv.LightmapScale, 8f )
					&& faces[2].Uv.SmoothingGroup == 2,
					$"fixture east wall UV did not survive: {faces[2].Uv}" );
				Check( faces[0].Uv.SmoothingGroup == 1, "fixture top smoothing group did not survive" );
				// The fixture itself round-trips through the JSON form unchanged.
				var again = faces.Select( f => FaceMaterial.FromJsonString( f.ToJsonString() ) ).ToList();
				Check( again.SequenceEqual( faces ), "fixture faces must survive a JSON round trip" );
			},
			missing: () => FindFixture() is null ? "box-faces.json fixture missing" : null );
	}

	static T AssertThrows<T>( Action action ) where T : Exception
	{
		try { action(); }
		catch ( T ex ) { return ex; }
		throw new Exception( $"Expected {typeof( T ).Name} but no exception was thrown." );
	}
}
