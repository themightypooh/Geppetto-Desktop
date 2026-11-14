#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

// Hammer Desktop P2: water, ladders, clip / invisible geometry via tool textures
// (hammer-water-ladders-clip-tool-textures, group "waterladders").
//
// Built against the landed APIs: MapBrush (FromPlanes / Box / Faces /
// FaceVertices / Volume / Centroid / Validate), FaceMaterial / FaceUv,
// ToolTexture (registry + NoDrawName + DropsFromBakedMesh), MapEntities /
// EntityRegistry (Load / LoadFromJson / Merge / Contains), MapBakeExport.Bake.
//
// Fixtures: tests/fixtures/hammer/hammer-water-ladders-clip-tool-textures/
//   water-cases.json      — water-brush classify cases (valid + named defects)
//   func-water-analog.json — the func_water_analog registry fragment (same JSON
//                           as FuncWaterAnalog.RegistryFragmentJson)
//   README.md             — what each file is
//
// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned
// and the final integrator adds the one WaterLaddersTests.Register line.
internal static class WaterLaddersTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "waterladders", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string mustContain = null ) where T : Exception
		{
			try { action(); }
			catch ( T ex )
			{
				if ( mustContain is not null && !ex.Message.Contains( mustContain, StringComparison.Ordinal ) )
					throw new Exception( $"expected error naming '{mustContain}', got: {ex.Message}" );
				return;
			}
			catch ( Exception ex ) { throw new Exception( $"expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"expected {typeof( T ).Name}, got silent success" );
		}
		void Approx( float actual, float expected, float tol, string what )
		{
			Require( MathF.Abs( actual - expected ) <= tol, $"{what}: got {actual}, expected {expected} (tol {tol})" );
		}
		void HasDefect( IReadOnlyList<string> defects, string code )
		{
			Require( defects.Any( d => d.StartsWith( code + ":", StringComparison.Ordinal ) ),
				$"expected a '{code}' defect, got: {( defects.Count == 0 ? "(none)" : string.Join( "; ", defects ) )}" );
		}

		string RepoRoot()
		{
			string? dir = AppContext.BaseDirectory;
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
				dir = Directory.GetParent( dir )?.FullName;
			}
			dir = Directory.GetCurrentDirectory();
			while ( dir is not null )
			{
				if ( File.Exists( Path.Combine( dir, "kernel", "Effigy", "Map", "registry", "core-source1.json" ) ) ) return dir;
				dir = Directory.GetParent( dir )?.FullName;
			}
			throw new Exception( "cannot locate repo root: kernel/Effigy/Map/registry/core-source1.json not found" );
		}
		string Fixture( string name ) => Path.Combine( RepoRoot(), "tests", "fixtures", "hammer", "hammer-water-ladders-clip-tool-textures", name );
		EntityRegistry LoadCore() => EntityRegistry.Load( Path.Combine( RepoRoot(), "kernel", "Effigy", "Map", "registry", "core-source1.json" ) );

		static MapBrush Box( double x0, double y0, double z0, double x1, double y1, double z1, string material = "brick/brick" )
			=> MapBrush.Box( new Vec3( (float)x0, (float)y0, (float)z0 ), new Vec3( (float)x1, (float)y1, (float)z1 ), material );

		// A box with per-face materials, in MapBrush.Box face order:
		// -X, +X, -Y, +Y, -Z(bottom), +Z(top).
		static MapBrush BoxFaces( Vec3 min, Vec3 max, string minusX, string plusX, string minusY, string plusY, string bottom, string top )
			=> MapBrush.FromPlanes( new[]
			{
				new Effigy.MapBrushFace( new Vec3( -1, 0, 0 ), -min.x, new FaceMaterial( minusX ) ),
				new Effigy.MapBrushFace( new Vec3( 1, 0, 0 ), max.x, new FaceMaterial( plusX ) ),
				new Effigy.MapBrushFace( new Vec3( 0, -1, 0 ), -min.y, new FaceMaterial( minusY ) ),
				new Effigy.MapBrushFace( new Vec3( 0, 1, 0 ), max.y, new FaceMaterial( plusY ) ),
				new Effigy.MapBrushFace( new Vec3( 0, 0, -1 ), -min.z, new FaceMaterial( bottom ) ),
				new Effigy.MapBrushFace( new Vec3( 0, 0, 1 ), max.z, new FaceMaterial( top ) ),
			} );

		static MapBrush WaterBox( double x0 = 0, double y0 = 0, double z0 = 0, double x1 = 128, double y1 = 128, double z1 = 64, string water = "nature/water" )
			=> WaterBrush.Create( new Vec3( (float)x0, (float)y0, (float)z0 ), new Vec3( (float)x1, (float)y1, (float)z1 ), water );

		// ---- Water: build ----

		Check( "water-create-top-face-water-other-faces-nodraw", () =>
		{
			var brush = WaterBox();
			Require( brush.FaceCount == 6, $"water box must have 6 faces, got {brush.FaceCount}" );
			Require( brush.IsValid, "water box must be a valid brush: " + string.Join( "; ", brush.Validate() ) );
			for ( var i = 0; i < 5; i++ )
				Require( brush.Faces[i].MaterialId == ToolTexture.NoDrawName,
					$"face {i} must be {ToolTexture.NoDrawName}, got '{brush.Faces[i].MaterialId}'" );
			Require( brush.Faces[5].MaterialId == "nature/water", $"top face must be 'nature/water', got '{brush.Faces[5].MaterialId}'" );
			Require( brush.Faces[5].Normal.AlmostEquals( new Vec3( 0, 0, 1 ) ), "top face normal must be +Z" );
		} );

		Check( "water-create-rejects-blank-tool-and-non-water-materials", () =>
		{
			var min = new Vec3( 0, 0, 0 ); var max = new Vec3( 64, 64, 32 );
			Throws<ArgumentException>( () => WaterBrush.Create( min, max, "" ), "blank" );
			Throws<ArgumentException>( () => WaterBrush.Create( min, max, "tools/toolsnodraw" ), "tool texture" );
			Throws<ArgumentException>( () => WaterBrush.Create( min, max, "brick/brickwall001" ), "does not name water" );
		} );

		Check( "water-create-rejects-inverted-box", () =>
		{
			Throws<ArgumentException>( () => WaterBrush.Create( new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 32 ), "nature/water" ), "min.x" );
			Throws<ArgumentException>( () => WaterBrush.Create( new Vec3( 0, 0, 32 ), new Vec3( 64, 64, 0 ), "nature/water" ), "min.z" );
		} );

		// ---- Water: classify + measure ----

		Check( "water-classify-valid-measurements", () =>
		{
			var (volume, defects) = WaterBrush.Classify( WaterBox() );
			Require( defects.Count == 0, "valid water brush must classify clean: " + string.Join( "; ", defects ) );
			Require( volume is not null, "valid water brush must produce a volume" );
			Require( volume!.SurfaceMaterial == "nature/water", $"surface material must be 'nature/water', got '{volume.SurfaceMaterial}'" );
			Approx( volume.SurfaceLevel, 64f, 1e-4f, "surface level" );
			Approx( volume.Depth, 64f, 1e-4f, "depth" );
			Approx( volume.SurfaceArea, 128f * 128f, 1e-2f, "surface area" );
			Approx( volume.Volume, 128f * 128f * 64f, 1f, "volume" );
			Require( volume.Settings.Lod == WaterLod.Full && volume.Settings.LodDistance == 0f, "default LOD must be Full with no distance cull" );
		} );

		Check( "water-is-water-material-name-rule", () =>
		{
			Require( WaterLadders.IsWaterMaterial( "nature/water" ), "'nature/water' must be a water material" );
			Require( WaterLadders.IsWaterMaterial( "Nature/Water_Canal_02" ), "matching must be case-insensitive" );
			Require( !WaterLadders.IsWaterMaterial( "brick/brickwall001" ), "brick must not be a water material" );
			Require( !WaterLadders.IsWaterMaterial( "tools/toolsnodraw" ), "a tool texture must not be a water material" );
			Require( !WaterLadders.IsWaterMaterial( "" ), "blank must not be a water material" );
			Require( !WaterLadders.IsWaterMaterial( null! ), "null must not be a water material" );
		} );

		Check( "water-bake-exports-top-face-only", () =>
		{
			// The ticket's acceptance in miniature: the bake drops the five
			// nodraw faces and emits exactly the water surface.
			var baked = MapBakeExport.Bake( new[] { WaterBox() }, null );
			Require( baked.ExcludedNoDrawFaces == 5, $"bake must exclude 5 nodraw faces, excluded {baked.ExcludedNoDrawFaces}" );
			Require( baked.FaceCount == 1, $"bake must emit exactly 1 face (the water surface), emitted {baked.FaceCount}" );
			Require( baked.Materials.Count == 1 && baked.Materials[0] == "nature/water",
				$"baked material must be exactly 'nature/water', got [{string.Join( ", ", baked.Materials )}]" );
			Require( baked.FaceInfo.Count == 1 && baked.FaceInfo[0].FaceIndex == 5,
				"the one baked face must be the top (+Z) face, index 5" );
		} );

		Check( "water-underwater-points", () =>
		{
			var (volume, _) = WaterBrush.Classify( WaterBox() );
			Require( volume!.IsUnderwater( new Vec3( 64, 64, 32 ) ), "a point mid-pool must be underwater" );
			Require( volume.IsUnderwater( new Vec3( 64, 64, 0.5f ) ), "a point just above the pool bottom must be underwater" );
			Require( !volume.IsUnderwater( new Vec3( 64, 64, 64 ) ), "a point exactly on the surface is at the waterline, not under water" );
			Require( !volume.IsUnderwater( new Vec3( 64, 64, 100 ) ), "a point above the surface must not be underwater" );
			Require( !volume.IsUnderwater( new Vec3( 200, 64, 32 ) ), "a point outside the pool must not be underwater" );
			Require( volume.Contains( new Vec3( 64, 64, 64 ) ), "the surface point itself is inside the brush" );
		} );

		Check( "water-lod-settings", () =>
		{
			var simple = new WaterSettings( WaterLod.Simple, 2048f );
			Require( simple.Lod == WaterLod.Simple && simple.LodDistance == 2048f, "LOD settings must round-trip" );
			var (volume, defects) = WaterBrush.Classify( WaterBox(), settings: simple );
			Require( defects.Count == 0 && volume!.Settings.Lod == WaterLod.Simple, "classify must carry the caller's LOD settings" );
			Throws<ArgumentException>( () => new WaterSettings( WaterLod.Full, -1f ), "negative" );
		} );

		Check( "water-fixture-cases", () =>
		{
			var path = Fixture( "water-cases.json" );
			Require( File.Exists( path ), $"fixture missing: {path}" );
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var cases = doc.RootElement.GetProperty( "cases" );
			var seen = 0;
			foreach ( var c in cases.EnumerateArray() )
			{
				seen++;
				var name = c.GetProperty( "name" ).GetString()!;
				var min = new Vec3( (float)c.GetProperty( "min" )[0].GetDouble(), (float)c.GetProperty( "min" )[1].GetDouble(), (float)c.GetProperty( "min" )[2].GetDouble() );
				var max = new Vec3( (float)c.GetProperty( "max" )[0].GetDouble(), (float)c.GetProperty( "max" )[1].GetDouble(), (float)c.GetProperty( "max" )[2].GetDouble() );
				var top = c.GetProperty( "top" ).GetString()!;
				var sides = c.GetProperty( "sides" ).GetString()!;
				var bottom = c.GetProperty( "bottom" ).GetString()!;
				var plusX = c.TryGetProperty( "plusX", out var px ) ? px.GetString()! : sides;
				var brush = BoxFaces( min, max, sides, plusX, sides, sides, bottom, top );
				var (_, defects) = WaterBrush.Classify( brush );
				if ( c.GetProperty( "expectValid" ).GetBoolean() )
				{
					Require( defects.Count == 0, $"fixture '{name}' must classify valid, got: {string.Join( "; ", defects )}" );
				}
				else
				{
					var expect = c.GetProperty( "expectDefect" ).GetString()!;
					HasDefect( defects, expect );
				}
			}
			Require( seen >= 5, $"fixture must carry at least 5 cases, saw {seen}" );
		} );

		// ---- Clip ----

		Check( "clip-blocks-players-and-npcs-not-bullets", () =>
		{
			var (clip, defects) = ClipVolume.FromBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), "tools/toolsclip" ) );
			Require( defects.Count == 0, "all-clip brush must classify clean: " + string.Join( "; ", defects ) );
			Require( clip!.Kind == ToolTextureKind.Clip, "kind must be Clip" );
			Require( clip.Layers == CollisionLayer.AllMovement, $"clip layers must be Player|Npc, got {clip.Layers}" );
			Require( ( clip.Layers & CollisionLayer.Bullet ) == 0, "clip must never block bullets" );
			var inside = new Vec3( 32, 32, 64 );
			Require( clip.BlocksPoint( inside, MoverKind.Player ), "clip must block a player inside it" );
			Require( clip.BlocksPoint( inside, MoverKind.Npc ), "clip must block an NPC inside it" );
			Require( !clip.BlocksPoint( inside, MoverKind.Bullet ), "clip must NOT block a bullet inside it" );
			Require( !clip.BlocksPoint( new Vec3( 200, 32, 64 ), MoverKind.Player ), "clip must not block a player outside it" );
		} );

		Check( "clip-playerclip-blocks-players-only-npcclip-npcs-only", () =>
		{
			var (player, d1) = ClipVolume.FromBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), "tools/toolsplayerclip" ) );
			var (npc, d2) = ClipVolume.FromBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), "tools/toolsnpcclip" ) );
			Require( d1.Count == 0 && d2.Count == 0, "playerclip / npcclip brushes must classify clean" );
			var inside = new Vec3( 32, 32, 64 );
			Require( player!.BlocksPoint( inside, MoverKind.Player ) && !player.BlocksPoint( inside, MoverKind.Npc ),
				"playerclip must block players only" );
			Require( npc!.BlocksPoint( inside, MoverKind.Npc ) && !npc.BlocksPoint( inside, MoverKind.Player ),
				"npcclip must block NPCs only" );
			Require( !player.BlocksPoint( inside, MoverKind.Bullet ) && !npc.BlocksPoint( inside, MoverKind.Bullet ),
				"neither clip kind may block bullets" );
		} );

		Check( "clip-create-boxes", () =>
		{
			var clip = ClipVolume.Create( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), ToolTextureKind.Clip );
			Require( clip.Kind == ToolTextureKind.Clip && clip.Layers == CollisionLayer.AllMovement, "Create(clip) must produce a Clip volume" );
			var pc = ClipVolume.Create( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), ToolTextureKind.PlayerClip );
			Require( pc.Layers == CollisionLayer.Player, "Create(playerclip) must block players only" );
			Throws<ArgumentException>( () => ClipVolume.MaterialName( ToolTextureKind.Ladder ), "not a clip kind" );
			Throws<ArgumentException>( () => ClipVolume.Create( new Vec3( 64, 0, 0 ), new Vec3( 0, 64, 128 ), ToolTextureKind.Clip ), "min.x" );
		} );

		Check( "clip-from-brush-defects", () =>
		{
			var (_, noClip) = ClipVolume.FromBrush( Box( 0, 0, 0, 64, 64, 128, "brick/brick" ) );
			HasDefect( noClip, "NonClipFace" );
			var mixed = BoxFaces( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ),
				"tools/toolsclip", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip", "tools/toolsplayerclip" );
			var (_, mixedDefects) = ClipVolume.FromBrush( mixed );
			HasDefect( mixedDefects, "MixedClipKinds" );
			var halfBrick = BoxFaces( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ),
				"tools/toolsclip", "brick/brick", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip" );
			var (_, brickDefects) = ClipVolume.FromBrush( halfBrick );
			HasDefect( brickDefects, "NonClipFace" );
		} );

		// ---- Collision / walk validation ----

		Check( "walk-validation-clip-blocks-player-not-bullet", () =>
		{
			var volumes = CollisionVolumes.Build( new[] { ClipVolume.Create( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), ToolTextureKind.Clip ).Brush } );
			Require( volumes.Defects.Count == 0, "clip build must be defect-free: " + string.Join( "; ", volumes.Defects ) );
			var from = new Vec3( -100, 32, 64 ); var to = new Vec3( 200, 32, 64 );
			Require( volumes.BlocksWalk( from, to, MoverKind.Player, out var hit ),
				"a player walking straight through a clip volume must be blocked" );
			Require( hit is not null && hit.Volume.Role == BrushRole.Clip, "the hit must name the clip volume" );
			Approx( hit!.Point.x, 0f, 1e-2f, "player walk must stop at the clip volume's near face (x=0)" );
			Require( !volumes.BlocksWalk( from, to, MoverKind.Bullet, out _ ),
				"the same walk as a bullet must pass straight through the clip volume" );
			Require( !volumes.BlocksWalk( new Vec3( -100, 200, 64 ), new Vec3( 200, 200, 64 ), MoverKind.Player, out _ ),
				"a walk missing the clip volume must pass" );
			Require( volumes.BlocksWalk( new Vec3( 32, 32, 64 ), new Vec3( 200, 32, 64 ), MoverKind.Player, out var insideHit ),
				"a walk starting inside a clip volume is blocked at its start" );
			Require( insideHit is not null && insideHit.T == 0f, "a walk starting inside must hit at t=0" );
		} );

		Check( "walk-validation-solid-wall-blocks-everyone-including-bullets", () =>
		{
			var volumes = CollisionVolumes.Build( new[] { Box( 0, 0, 0, 64, 64, 128, "brick/brick" ) } );
			Require( volumes.Blocking.Count == 1 && volumes.Blocking[0].Layers == CollisionLayer.All,
				"a solid wall must block every layer" );
			var from = new Vec3( -100, 32, 64 ); var to = new Vec3( 200, 32, 64 );
			foreach ( var mover in new[] { MoverKind.Player, MoverKind.Npc, MoverKind.Bullet } )
				Require( volumes.BlocksWalk( from, to, mover, out _ ), $"a solid wall must block {mover}" );
		} );

		Check( "walk-validation-water-does-not-block", () =>
		{
			var volumes = CollisionVolumes.Build( new[] { WaterBox() } );
			Require( volumes.Waters.Count == 1 && volumes.Blocking.Count == 0,
				"water must be listed as water, never as a blocking volume" );
			var from = new Vec3( -100, 64, 32 ); var to = new Vec3( 200, 64, 32 );
			foreach ( var mover in new[] { MoverKind.Player, MoverKind.Npc, MoverKind.Bullet } )
				Require( !volumes.BlocksWalk( from, to, mover, out _ ), $"swimming through water must not block {mover}" );
			Require( volumes.IsUnderwater( new Vec3( 64, 64, 32 ) ), "the built set must know the point is underwater" );
		} );

		Check( "collision-volumes-build-mixed-scene", () =>
		{
			var brushes = new MapBrush[]
			{
				Box( 0, 0, 0, 64, 64, 128, "brick/brick" ),
				ClipVolume.Create( new Vec3( 128, 0, 0 ), new Vec3( 192, 64, 128 ), ToolTextureKind.Clip ).Brush,
				WaterBox( 256, 0, 0, 384, 128, 64 ),
				LadderVolume.Create( new Vec3( 512, 0, 0 ), new Vec3( 528, 16, 192 ) ).Brush,
				MapBrush.Box( new Vec3( 640, 0, 0 ), new Vec3( 704, 64, 128 ), "tools/toolstrigger" ),
				BoxFaces( new Vec3( 768, 0, 0 ), new Vec3( 832, 64, 128 ),
					"tools/toolsclip", "brick/brick", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip" ),
			};
			var volumes = CollisionVolumes.Build( brushes );
			Require( volumes.Blocking.Count == 2, $"mixed scene must have 2 blocking volumes (wall + clip), got {volumes.Blocking.Count}" );
			Require( volumes.Waters.Count == 1, $"mixed scene must have 1 water volume, got {volumes.Waters.Count}" );
			Require( volumes.Ladders.Count == 1, $"mixed scene must have 1 ladder volume, got {volumes.Ladders.Count}" );
			Require( volumes.IgnoredNonSolidTool == 1, $"the trigger brush must be counted as ignored NonSolid, got {volumes.IgnoredNonSolidTool}" );
			Require( volumes.Defects.Count == 1 && volumes.Defects[0].Contains( "brush 5", StringComparison.Ordinal ),
				"the mixed clip/brick brush must be reported as a defect on brush 5, got: " + string.Join( "; ", volumes.Defects ) );
		} );

		// ---- Ladders ----

		Check( "ladder-climbable-not-blocking", () =>
		{
			var (ladder, defects) = LadderVolume.FromBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 16, 16, 192 ), "tools/toolsladder" ) );
			Require( defects.Count == 0, "all-ladder brush must classify clean: " + string.Join( "; ", defects ) );
			Approx( ladder!.Bottom, 0f, 1e-4f, "ladder bottom" );
			Approx( ladder.Top, 192f, 1e-4f, "ladder top" );
			Approx( ladder.Height, 192f, 1e-4f, "ladder height" );
			var inside = new Vec3( 8, 8, 96 );
			Require( ladder.IsClimbableAt( inside ), "a point inside the ladder must be climbable" );
			Require( !ladder.IsClimbableAt( new Vec3( 8, 8, 200 ) ), "a point above the ladder must not be climbable" );
			foreach ( var mover in new[] { MoverKind.Player, MoverKind.Npc, MoverKind.Bullet } )
				Require( !ladder.BlocksPoint( inside, mover ), $"a ladder must never block {mover}" );
			var created = LadderVolume.Create( new Vec3( 0, 0, 0 ), new Vec3( 16, 16, 192 ) );
			Require( created.Height == 192f, "Create must build the same ladder" );
			var (_, brickDefects) = LadderVolume.FromBrush( Box( 0, 0, 0, 16, 16, 192, "brick/brick" ) );
			HasDefect( brickDefects, "NonLadderFace" );
		} );

		// ---- BlockLight / BlockLos ----

		Check( "blocklight-stops-light-through-that-face", () =>
		{
			// A solid brush whose +X face is toolsblocklight: light crossing
			// that face is stopped, light passing beside it is not.
			var brush = BoxFaces( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ),
				"brick/brick", "tools/toolsblocklight", "brick/brick", "brick/brick", "brick/brick", "brick/brick" );
			var blockers = VisibilityBlockers.FromBrushes( new[] { brush } );
			Require( blockers.Defects.Count == 0, "blocklight collection must be defect-free: " + string.Join( "; ", blockers.Defects ) );
			Require( blockers.BlockLightFaceCount == 1 && blockers.BlockLosFaceCount == 0,
				$"expected exactly 1 blocklight face, got light={blockers.BlockLightFaceCount} los={blockers.BlockLosFaceCount}" );
			Require( blockers.BlocksLight( new Vec3( -100, 32, 64 ), new Vec3( 200, 32, 64 ) ),
				"light crossing the blocklight face must be stopped" );
			Require( !blockers.BlocksLight( new Vec3( -100, 200, 64 ), new Vec3( 200, 200, 64 ) ),
				"light passing beside the blocklight face must not be stopped" );
			Require( !blockers.BlocksLineOfSight( new Vec3( -100, 32, 64 ), new Vec3( 200, 32, 64 ) ),
				"a blocklight face must not block AI line of sight" );
			var plain = VisibilityBlockers.FromBrushes( new[] { Box( 0, 0, 0, 64, 64, 128, "brick/brick" ) } );
			Require( plain.Faces.Count == 0 && !plain.BlocksLight( new Vec3( -100, 32, 64 ), new Vec3( 200, 32, 64 ) ),
				"an ordinary brick brush blocks neither light nor sight" );
		} );

		Check( "blocklos-stops-sight-not-light", () =>
		{
			var brush = BoxFaces( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ),
				"brick/brick", "tools/toolsblock_los", "brick/brick", "brick/brick", "brick/brick", "brick/brick" );
			var blockers = VisibilityBlockers.FromBrushes( new[] { brush } );
			Require( blockers.BlockLightFaceCount == 0 && blockers.BlockLosFaceCount == 1,
				$"expected exactly 1 blocklos face, got light={blockers.BlockLightFaceCount} los={blockers.BlockLosFaceCount}" );
			Require( blockers.BlocksLineOfSight( new Vec3( -100, 32, 64 ), new Vec3( 200, 32, 64 ) ),
				"AI sight crossing the blocklos face must be stopped" );
			Require( !blockers.BlocksLight( new Vec3( -100, 32, 64 ), new Vec3( 200, 32, 64 ) ),
				"a blocklos face must not block light" );
		} );

		// ---- func_water_analog ----

		Check( "func-water-analog-place-and-validate", () =>
		{
			var entities = new MapEntities( LoadCore() );
			Require( !entities.Registry.Contains( FuncWaterAnalog.ClassName ),
				"the core registry must not already contain func_water_analog (that is why this ticket ships a fragment)" );
			var water = WaterBox();
			var entity = FuncWaterAnalog.Place( entities, new[] { water } );
			Require( entity.ClassName == FuncWaterAnalog.ClassName, $"placed class must be {FuncWaterAnalog.ClassName}, got '{entity.ClassName}'" );
			Require( entity.IsBrushEntity, "func_water_analog must be a brush entity" );
			Require( entities.Registry.Contains( FuncWaterAnalog.ClassName ), "placing must merge the fragment class into the registry" );
			Require( FuncWaterAnalog.Validate( entity ).Count == 0, "a placed water entity must validate clean" );
			var (brushes, defects) = FuncWaterAnalog.WaterBrushes( entity );
			Require( defects.Count == 0 && brushes.Count == 1 && ReferenceEquals( brushes[0], water ),
				"the entity must tie exactly the water brush it was placed with" );
			Approx( entity.Origin.x, 64f, 1e-4f, "entity origin defaults to the water brush centroid (x)" );
			// Placing a second water entity on the same registry must work:
			// EnsureClass is idempotent, the registry merge must not DuplicateClass.
			var second = FuncWaterAnalog.Place( entities, new[] { WaterBox( 256, 0, 0, 384, 128, 64 ) } );
			Require( second.Id != entity.Id && entities.Count == 2, "a second func_water_analog on the same registry must place cleanly" );
		} );

		Check( "func-water-analog-rejects-non-water-brush", () =>
		{
			var entities = new MapEntities( LoadCore() );
			Throws<ArgumentException>( () => FuncWaterAnalog.Place( entities, new[] { Box( 0, 0, 0, 64, 64, 128, "brick/brick" ) } ), "not a water brush" );
			Throws<ArgumentException>( () => FuncWaterAnalog.Place( entities, Array.Empty<MapBrush>() ), "at least one tied brush" );
		} );

		Check( "func-water-analog-fragment-fixture-loads", () =>
		{
			var path = Fixture( "func-water-analog.json" );
			Require( File.Exists( path ), $"fixture missing: {path}" );
			var json = File.ReadAllText( path );
			Require( json == FuncWaterAnalog.RegistryFragmentJson,
				"the shipped fixture must be byte-identical to FuncWaterAnalog.RegistryFragmentJson" );
			var registry = EntityRegistry.LoadFromJson( json, "func-water-analog.json" );
			Require( registry.Contains( FuncWaterAnalog.ClassName ), "the fragment must define func_water_analog" );
			var cls = registry.Get( FuncWaterAnalog.ClassName );
			Require( cls.Kind == EntityKind.Brush, "func_water_analog must be a brush class" );
			var duplicateRejected = false;
			try { registry.Merge( EntityRegistry.LoadFromJson( json, "again" ) ); }
			catch ( EntityRegistryException ex )
			{
				duplicateRejected = true;
				Require( ex.Code == EntityRegistry.CodeDuplicateClass,
					$"merging the fragment twice must fail with DuplicateClass, got code '{ex.Code}': {ex.Message}" );
			}
			Require( duplicateRejected, "merging the fragment twice must fail, got silent success" );
		} );

		// ---- Bake-face rule ----

		Check( "bake-face-rule-drops-invisible-faces-keeps-visible-ones", () =>
		{
			var faces = new[]
			{
				new FaceMaterial( "brick/brickwall001" ),
				new FaceMaterial( "nature/water" ),
				new FaceMaterial( "tools/toolsnodraw" ),
				new FaceMaterial( "tools/toolsclip" ),
				new FaceMaterial( "tools/toolsplayerclip" ),
				new FaceMaterial( "tools/toolsladder" ),
				new FaceMaterial( "tools/toolsblocklight" ),
				new FaceMaterial( "tools/toolsblock_los" ),
				new FaceMaterial( "tools/toolstrigger" ),
				new FaceMaterial( "tools/toolsskybox" ),
			};
			var baked = WaterLadders.BakedFaceIndices( faces );
			Require( baked.Count == 2 && baked[0] == 0 && baked[1] == 1,
				$"only the brick and water faces may bake, got indices [{string.Join( ", ", baked )}]" );
			Require( WaterLadders.ShouldBakeFace( faces[0] ) && WaterLadders.ShouldBakeFace( faces[1] ),
				"ordinary and water faces must bake" );
			Require( !WaterLadders.ShouldBakeFace( faces[2] ) && !WaterLadders.ShouldBakeFace( faces[3] ),
				"nodraw and clip faces must not bake" );
			// Cross-check against the landed rule: nodraw is the only overlap
			// the bake pass drops today — every other invisible face here is
			// exactly the divergence the integrator must wire (see WaterLadders docs).
			var landed = FaceMaterial.BakedFaceIndices( faces );
			Require( landed.Count == 9, $"landed FaceMaterial rule bakes everything except nodraw (9 faces), got {landed.Count}" );
		} );

		// ---- Brush-role classification ----

		Check( "brush-role-classification", () =>
		{
			Require( WaterLadders.ClassifyBrush( Box( 0, 0, 0, 64, 64, 128, "brick/brick" ) ).Role == BrushRole.Solid, "brick box must be Solid" );
			Require( WaterLadders.ClassifyBrush( WaterBox() ).Role == BrushRole.Water, "water box must be Water" );
			Require( WaterLadders.ClassifyBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), "tools/toolsclip" ) ).Role == BrushRole.Clip, "clip box must be Clip" );
			Require( WaterLadders.ClassifyBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), "tools/toolsnpcclip" ) ).Role == BrushRole.NpcClip, "npcclip box must be NpcClip" );
			Require( WaterLadders.ClassifyBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 16, 16, 192 ), "tools/toolsladder" ) ).Role == BrushRole.Ladder, "ladder box must be Ladder" );
			Require( WaterLadders.ClassifyBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), "tools/toolstrigger" ) ).Role == BrushRole.NonSolidTool, "trigger box must be NonSolidTool" );
			var mixed = BoxFaces( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ),
				"tools/toolsclip", "brick/brick", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip", "tools/toolsclip" );
			var (role, defects) = WaterLadders.ClassifyBrush( mixed );
			Require( role == BrushRole.MixedTool, $"clip+brick must be MixedTool, got {role}" );
			Require( defects.Count > 0, "MixedTool must carry a named defect" );
			var blocklightBrush = BoxFaces( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ),
				"brick/brick", "tools/toolsblocklight", "brick/brick", "brick/brick", "brick/brick", "brick/brick" );
			Require( WaterLadders.ClassifyBrush( blocklightBrush ).Role == BrushRole.Solid,
				"a blocklight face does not change a solid brush's role — it is a face flag, collected by VisibilityBlockers" );
		} );

		// ---- Invisible geometry (spec SOURCE: toolsinvisible / invisibleladder) ----

		Check( "invisible-textures-are-nonsolid-never-bake-never-block", () =>
		{
			var invisible = ToolTexture.Lookup( "tools/toolsinvisible" );
			Require( invisible is not null && invisible.Kind == ToolTextureKind.Invisible, "toolsinvisible must resolve as Invisible" );
			Require( !invisible!.IsSolid, "toolsinvisible must not be solid" );
			Require( !WaterLadders.ShouldBakeFace( new FaceMaterial( "tools/toolsinvisible" ) ), "an invisible face must not bake" );
			var invisibleLadder = ToolTexture.Lookup( "tools/toolsinvisibleladder" );
			Require( invisibleLadder is not null && invisibleLadder.Kind == ToolTextureKind.Ladder, "toolsinvisibleladder must resolve as Ladder" );
			Require( !invisibleLadder!.IsSolid, "toolsinvisibleladder must not be solid" );
			Require( WaterLadders.ClassifyBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), "tools/toolsinvisible" ) ).Role == BrushRole.NonSolidTool, "an invisible box must be NonSolidTool, not a wall" );
			Require( WaterLadders.ClassifyBrush( MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 16, 16, 192 ), "tools/toolsinvisibleladder" ) ).Role == BrushRole.Ladder, "an invisible-ladder box must be Ladder" );
			var scene = CollisionVolumes.Build( new[]
			{
				MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 128 ), "tools/toolsinvisible" ),
				MapBrush.Box( new Vec3( 0, 0, 0 ), new Vec3( 16, 16, 192 ), "tools/toolsinvisibleladder" ),
			} );
			Require( scene.Blocking.Count == 0, "invisible volumes must add no blocking collision" );
			Require( scene.IgnoredNonSolidTool == 1, "the invisible box is counted as ignored NonSolid tool" );
			Require( scene.Ladders.Count == 1 && scene.IsOnLadder( new Vec3( 8, 8, 100 ) ), "the invisible ladder must still be climbable" );
			Require( !scene.BlocksWalk( new Vec3( -100, 32, 64 ), new Vec3( 200, 32, 64 ), MoverKind.Player, out _ ), "a walk through invisible geometry must not be blocked" );
		} );
	}
}
