#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Effigy;
using Effigy.Map;

/// <summary>
/// Prop placement helpers (hammer-prop-placement-helpers): Move-to-Floor/Ceiling,
/// align-by-normal, drop-to-rest pose, over the Quick Play map/bake collision and
/// displacement meshes. Fixtures live in
/// tests/fixtures/hammer/hammer-prop-placement-helpers/.
/// NOT yet registered in tests/TestRegistry.cs — that file is coordinator-owned;
/// the final integrator adds the one PropPlacementTests.Register(registry, settings);
/// line, as with MaterialBridgeTests.
/// </summary>
internal static class PropPlacementTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "propplacement", name, test );
		void Require( bool condition, string message )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tolerance, string what )
		{
			if ( Math.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{what}: expected {expected}, got {actual}" );
		}
		void NearVec( Vec3 actual, Vec3 expected, float tolerance, string what )
		{
			Near( actual.x, expected.x, tolerance, what + ".x" );
			Near( actual.y, expected.y, tolerance, what + ".y" );
			Near( actual.z, expected.z, tolerance, what + ".z" );
		}
		void RequireSeated( PropPlacementResult result, string what )
		{
			Require( result.Seated, $"{what}: expected seated, got not-seated ({result.Reason})" );
			Require( result.Hit is not null, $"{what}: seated but no hit recorded" );
		}
		void RequireNotSeated( PropPlacementResult result, string reason, string what )
		{
			Require( !result.Seated, $"{what}: expected not-seated ({reason}), got seated at {result.Pose}" );
			Require( result.Reason == reason, $"{what}: expected reason {reason}, got {result.Reason}" );
		}

		// The sloped displacement used by the code-built scenes: 256x256 patch at
		// (-128,-128), 5x5 grid, heights rise with +X at dz/dx = 0.5 (26.57 deg).
		// HeightAt(0,0) = 64; surface normal = (-0.4472, 0, 0.8944).
		// The steep variant rises at dz/dx = 2.0 (63.43 deg) — too steep to rest on.
		Vec3 SlopeNormal() => new Vec3( -0.4472136f, 0f, 0.8944272f );
		QuickPlayDisplacement SlopeDisplacement( float risePerColumn )
		{
			var heights = new float[5, 5];
			for ( int r = 0; r < 5; r++ )
				for ( int c = 0; c < 5; c++ )
					heights[r, c] = c * risePerColumn;
			return new QuickPlayDisplacement( new Vec3( -128f, -128f, 0f ), 256f, 256f, 5, 5, heights );
		}
		QuickPlayMap RoomMap( bool withCeiling, QuickPlayDisplacement? disp = null )
		{
			var map = new QuickPlayMap();
			map.Brushes.Add( new QuickPlayBrush( "floor", new Vec3( -144f, -144f, -16f ), new Vec3( 144f, 144f, 0f ) ) );
			if ( withCeiling )
				map.Brushes.Add( new QuickPlayBrush( "ceiling", new Vec3( -144f, -144f, 128f ), new Vec3( 144f, 144f, 144f ) ) );
			map.Entities.Add( new QuickPlayEntity( "info_player_start", new Vec3( 0f, 0f, 0f ) ) );
			if ( disp is not null ) map.Displacements.Add( disp );
			return map;
		}
		PropPlacementSurface FlatFloor() => PropPlacementSurface.FromQuickPlayMap( RoomMap( withCeiling: false ) );
		PropPlacementSurface SlopeSurface() => PropPlacementSurface.FromQuickPlayMap( RoomMap( withCeiling: false, disp: SlopeDisplacement( 32f ) ) );

		string FixtureText( string name )
		{
			var candidates = new[]
			{
				Path.Combine( AppContext.BaseDirectory, "hammer-prop-placement-helpers", name ),
				Path.GetFullPath( Path.Combine( AppContext.BaseDirectory, "..", "..", "..", "fixtures", "hammer", "hammer-prop-placement-helpers", name ) ),
				Path.GetFullPath( Path.Combine( "tests", "fixtures", "hammer", "hammer-prop-placement-helpers", name ) ),
			};
			foreach ( var candidate in candidates )
				if ( File.Exists( candidate ) ) return File.ReadAllText( candidate );
			throw new Exception( "prop-placement fixture missing: " + name + " (probed " + string.Join( ", ", candidates ) + ")" );
		}
		Vec3 JsonVec( JsonElement el ) => new Vec3( el[0].GetSingle(), el[1].GetSingle(), el[2].GetSingle() );
		QuickPlayMap LoadFixtureMap( string name )
		{
			using var doc = JsonDocument.Parse( FixtureText( name ) );
			var root = doc.RootElement;
			var map = new QuickPlayMap();
			foreach ( var b in root.GetProperty( "brushes" ).EnumerateArray() )
				map.Brushes.Add( new QuickPlayBrush(
					b.GetProperty( "id" ).GetString()!,
					JsonVec( b.GetProperty( "min" ) ),
					JsonVec( b.GetProperty( "max" ) ),
					b.TryGetProperty( "detail", out var detail ) && detail.GetBoolean(),
					b.TryGetProperty( "material", out var material ) ? material.GetString() : null ) );
			foreach ( var e in root.GetProperty( "entities" ).EnumerateArray() )
				map.Entities.Add( new QuickPlayEntity(
					e.GetProperty( "class" ).GetString()!,
					JsonVec( e.GetProperty( "origin" ) ),
					e.TryGetProperty( "yaw", out var yaw ) ? yaw.GetSingle() : 0f ) );
			foreach ( var d in root.GetProperty( "displacements" ).EnumerateArray() )
			{
				int columns = d.GetProperty( "columns" ).GetInt32();
				int rows = d.GetProperty( "rows" ).GetInt32();
				var heights = new float[rows, columns];
				int r = 0;
				foreach ( var row in d.GetProperty( "heights" ).EnumerateArray() )
				{
					int c = 0;
					foreach ( var h in row.EnumerateArray() ) heights[r, c++] = h.GetSingle();
					r++;
				}
				map.Displacements.Add( new QuickPlayDisplacement(
					JsonVec( d.GetProperty( "origin" ) ),
					d.GetProperty( "sizeX" ).GetSingle(),
					d.GetProperty( "sizeY" ).GetSingle(),
					columns, rows, heights ) );
			}
			return map;
		}
		(MapEntities Entities, MapEntity Prop) PlaceProp( QuickPlayEntity spec )
		{
			var registry = EntityRegistry.LoadFromJson( FixtureText( "registry.json" ), "registry.json" );
			var entities = new MapEntities( registry );
			var keyvalues = new Dictionary<string, string> { { "model", "props/crate.mdl" } };
			var prop = entities.PlacePointEntity( spec.ClassName, spec.Origin, keyvalues, new Vec3( 0f, spec.Yaw, 0f ) );
			return ( entities, prop );
		}

		Check( "floor-flat-seats-prop", () =>
		{
			var surface = FlatFloor();
			var result = PropPlacement.MoveToFloor( surface, PropPose.Upright( new Vec3( 0f, 0f, 200f ) ) );
			RequireSeated( result, "flat floor" );
			Near( result.Pose.Origin.z, 0f, 0.01f, "seated z" );
			NearVec( result.Pose.Up, new Vec3( 0f, 0f, 1f ), 0.001f, "seated up" );
			Near( result.Hit!.Value.Distance, 200f, 0.01f, "hit distance" );
			NearVec( result.Hit.Value.Point, new Vec3( 0f, 0f, 0f ), 0.01f, "hit point" );
		} );

		Check( "floor-slope-normal-matches", () =>
		{
			var surface = SlopeSurface();
			var result = PropPlacement.MoveToFloor( surface, PropPose.Upright( new Vec3( 0f, 0f, 200f ) ) );
			RequireSeated( result, "sloped displacement" );
			Near( result.Pose.Origin.z, 64f, 0.01f, "seated z is the displacement height at (0,0), not the flat floor" );
			NearVec( result.Pose.Up, SlopeNormal(), 0.01f, "seated up matches the slope normal" );
			Near( result.Pose.Angles.x, -26.57f, 0.5f, "pitch from slope normal" );
		} );

		Check( "fixture-sloped-room-lands-on-slope", () =>
		{
			var map = LoadFixtureMap( "sloped-room.json" );
			var surface = PropPlacementSurface.FromQuickPlayMap( map );
			Require( surface.TriangleCount > 0, "fixture surface is empty" );
			var crate = map.Entities[1];
			Require( crate.ClassName == "prop_static", "fixture entity 1 is the crate, got " + crate.ClassName );
			var (entities, prop) = PlaceProp( crate );
			var result = PropPlacement.SeatOnFloor( entities, prop, surface );
			RequireSeated( result, "fixture crate" );
			Near( prop.Origin.z, 64f, 0.01f, "entity origin z after seating" );
			NearVec( result.Pose.Up, SlopeNormal(), 0.01f, "entity up matches the slope normal" );
			Near( prop.Angles.x, -26.57f, 0.5f, "entity pitch after seating" );
		} );

		Check( "fixture-flat-room-ceiling", () =>
		{
			var map = LoadFixtureMap( "flat-room.json" );
			var surface = PropPlacementSurface.FromQuickPlayMap( map );
			var lamp = map.Entities[2];
			Require( lamp.ClassName == "prop_static", "fixture entity 2 is the lamp, got " + lamp.ClassName );
			var (entities, prop) = PlaceProp( lamp );
			var result = PropPlacement.SeatOnCeiling( entities, prop, surface );
			RequireSeated( result, "fixture lamp" );
			Near( prop.Origin.z, 128f, 0.01f, "entity origin z hangs at the ceiling underside" );
			NearVec( result.Pose.Up, new Vec3( 0f, 0f, -1f ), 0.001f, "hanging prop is upside down" );
			Near( prop.Angles.y, 45f, 0.001f, "yaw preserved while hanging" );
		} );

		Check( "ceiling-seats-prop-upside-down", () =>
		{
			var surface = PropPlacementSurface.FromQuickPlayMap( RoomMap( withCeiling: true ) );
			var result = PropPlacement.MoveToCeiling( surface, PropPose.Upright( new Vec3( 0f, 0f, 64f ) ) );
			RequireSeated( result, "ceiling" );
			Near( result.Pose.Origin.z, 128f, 0.01f, "seated z is the ceiling underside" );
			NearVec( result.Pose.Up, new Vec3( 0f, 0f, -1f ), 0.001f, "seated up is flipped" );
			NearVec( result.Hit!.Value.Normal, new Vec3( 0f, 0f, -1f ), 0.001f, "hit normal faces down" );
			Near( result.Hit.Value.Distance, 64f, 0.01f, "hit distance" );
		} );

		Check( "drop-rests-on-floor", () =>
		{
			var surface = FlatFloor();
			var result = PropPlacement.DropToRest( surface, PropPose.Upright( new Vec3( 0f, 0f, 300f ) ), new PropPlacementOptions { StepSize = 8f } );
			RequireSeated( result, "drop" );
			Near( result.Pose.Origin.z, 0f, 0.01f, "dropped z" );
			Require( result.StepsTaken > 0, "drop took steps, got " + result.StepsTaken );
			Near( result.DistanceDropped, 300f, 0.01f, "distance dropped" );
		} );

		Check( "drop-already-resting-exact", () =>
		{
			// Regression: a prop whose origin sits exactly on the floor (hit distance
			// 0) must seat at once, not fall through it — the probe starts RestGap
			// above the origin so the t == 0 hit is not missed.
			var surface = FlatFloor();
			var result = PropPlacement.DropToRest( surface, PropPose.Upright( new Vec3( 0f, 0f, 0f ) ) );
			RequireSeated( result, "already resting" );
			Near( result.Pose.Origin.z, 0f, 0.01f, "stayed on the floor" );
			Require( result.StepsTaken == 0, "no steps taken, got " + result.StepsTaken );
			Near( result.DistanceDropped, 0f, 0.01f, "nothing dropped" );
		} );

		Check( "drop-no-floor-void", () =>
		{
			var surface = FlatFloor();
			var start = PropPose.Upright( new Vec3( 0f, 0f, 500f ) );
			var result = PropPlacement.DropToRest( surface, start, new PropPlacementOptions { MaxDrop = 100f, StepSize = 4f } );
			RequireNotSeated( result, PropPlacementError.NoRestFound, "drop over the void" );
			Require( result.Pose.Origin.Equals( start.Origin ), "failed drop leaves the pose where it started" );
			Require( result.StepsTaken == 25, "100 units at step 4 is 25 steps, got " + result.StepsTaken );
			Near( result.DistanceDropped, 100f, 0.01f, "fell the full MaxDrop before giving up" );
		} );

		Check( "drop-too-steep-stops", () =>
		{
			var surface = PropPlacementSurface.FromQuickPlayMap( RoomMap( withCeiling: false, disp: SlopeDisplacement( 128f ) ) );
			var result = PropPlacement.DropToRest( surface, PropPose.Upright( new Vec3( 0f, 0f, 300f ) ) );
			RequireNotSeated( result, PropPlacementError.TooSteep, "drop onto a 63-degree slope" );
			Require( result.Hit is not null, "too-steep reports the hit it refused" );
			Near( result.Hit!.Value.SlopeDegrees, 63.43f, 0.5f, "hit slope" );
		} );

		Check( "drop-lands-on-slope", () =>
		{
			var surface = SlopeSurface();
			var result = PropPlacement.DropToRest( surface, PropPose.Upright( new Vec3( 0f, 0f, 300f ) ), new PropPlacementOptions { StepSize = 8f } );
			RequireSeated( result, "drop onto slope" );
			Near( result.Pose.Origin.z, 64f, 0.01f, "dropped z is the displacement height" );
			NearVec( result.Pose.Up, SlopeNormal(), 0.01f, "dropped up matches the slope normal" );
			Near( result.DistanceDropped, 236f, 0.01f, "distance dropped" );
		} );

		Check( "align-to-normal-tilts-prop", () =>
		{
			var surface = SlopeSurface();
			var start = PropPose.Upright( new Vec3( 0f, 0f, 200f ) );
			var result = PropPlacement.AlignToNormal( surface, start );
			RequireSeated( result, "align" );
			NearVec( result.Pose.Origin, start.Origin, 0.001f, "align does not move the origin" );
			NearVec( result.Pose.Up, SlopeNormal(), 0.01f, "align sets up to the slope normal" );
		} );

		Check( "align-preserves-yaw-on-slope", () =>
		{
			var surface = SlopeSurface();
			var result = PropPlacement.AlignToNormal( surface, PropPose.Upright( new Vec3( 0f, 0f, 200f ), yawDegrees: 90f ) );
			RequireSeated( result, "align with yaw 90" );
			Near( result.Pose.YawDegrees, 90f, 0.001f, "yaw preserved" );
			Near( result.Pose.Angles.x, 0f, 0.5f, "pitch is 0 when facing across the slope" );
			NearVec( result.Pose.Up, SlopeNormal(), 0.01f, "up still matches the slope normal" );
		} );

		Check( "floor-no-floor-below", () =>
		{
			var surface = FlatFloor();
			var start = PropPose.Upright( new Vec3( 500f, 500f, 200f ) );
			var result = PropPlacement.MoveToFloor( surface, start );
			RequireNotSeated( result, PropPlacementError.NoFloorBelow, "prop past the floor edge" );
			Require( result.Pose.Origin.Equals( start.Origin ), "failed move leaves the pose where it started" );
		} );

		Check( "ceiling-no-ceiling-above", () =>
		{
			var surface = FlatFloor();
			var result = PropPlacement.MoveToCeiling( surface, PropPose.Upright( new Vec3( 0f, 0f, 64f ) ) );
			RequireNotSeated( result, PropPlacementError.NoCeilingAbove, "open sky has no ceiling" );
		} );

		Check( "bake-hulls-seat-prop", () =>
		{
			// The Quick Play bake's CollisionHulls (ConvexHull per brush, ConvexDecomp
			// per displacement) seat a prop too — the baked-collision path, not just
			// the exact-map path.
			var map = RoomMap( withCeiling: false, disp: SlopeDisplacement( 32f ) );
			var bake = QuickPlay.Bake( map );
			Require( bake.CollisionHulls.Count > 0, "bake produced no collision hulls" );
			Require( bake.DisplacementHullCount > 0, "bake produced no displacement hulls" );
			var surface = PropPlacementSurface.FromQuickPlayBake( bake );
			Require( surface.TriangleCount > 0, "bake surface is empty" );
			var result = PropPlacement.MoveToFloor( surface, PropPose.Upright( new Vec3( 0f, 0f, 200f ) ) );
			RequireSeated( result, "bake hulls" );
			Near( result.Pose.Origin.z, 64f, 2f, "seated z is the displacement hull top at (0,0)" );
			Require( result.Pose.Up.z > 0.85f, "seated up is mostly vertical, got " + result.Pose.Up );
		} );

		Check( "convex-decomp-surface-seats-prop", () =>
		{
			var corners = new List<Vec3>();
			foreach ( float x in new[] { -64f, 64f } )
				foreach ( float y in new[] { -64f, 64f } )
					foreach ( float z in new[] { 0f, 64f } )
						corners.Add( new Vec3( x, y, z ) );
			var decomp = ConvexDecomp.Decompose( corners );
			Require( decomp.Reason is null, "decomp failed: " + decomp.Reason );
			Require( decomp.Hulls.Count > 0, "decomp produced no hulls" );
			var surface = PropPlacementSurface.FromConvexDecomp( decomp );
			var result = PropPlacement.MoveToFloor( surface, PropPose.Upright( new Vec3( 0f, 0f, 200f ) ) );
			RequireSeated( result, "convex-decomp box" );
			Near( result.Pose.Origin.z, 64f, 0.01f, "seated z is the box top" );
			NearVec( result.Pose.Up, new Vec3( 0f, 0f, 1f ), 0.001f, "seated up" );
		} );

		Check( "displacement-mesh-seats-prop", () =>
		{
			// A bound Displacement (Effigy.Map): power 2, 5x5 grid, offsets rising
			// with +X at 16 per column — the same 0.5 slope as the heightfield tests,
			// but through Displacement.ToMesh() instead of QuickPlayDisplacement.
			var face = Effigy.Map.MapBrushFace.FromCorners( new[]
			{
				new Vec3( 0f, 0f, 0f ), new Vec3( 128f, 0f, 0f ),
				new Vec3( 128f, 128f, 0f ), new Vec3( 0f, 128f, 0f ),
			} );
			var displacement = Displacement.Create( face, power: 2 );
			for ( int r = 0; r < 5; r++ )
				for ( int c = 0; c < 5; c++ )
					displacement.SetOffset( r, c, c * 16f );
			var surface = PropPlacementSurface.FromDisplacement( displacement );
			Require( surface.TriangleCount == 32, "5x5 grid is 16 quads / 32 triangles, got " + surface.TriangleCount );
			var result = PropPlacement.MoveToFloor( surface, PropPose.Upright( new Vec3( 64f, 64f, 200f ) ) );
			RequireSeated( result, "displacement mesh" );
			Near( result.Pose.Origin.z, 32f, 0.01f, "seated z is the displaced height at (64,64)" );
			NearVec( result.Pose.Up, SlopeNormal(), 0.01f, "seated up matches the slope normal" );
		} );

		Check( "entity-seat-writes-origin-and-angles", () =>
		{
			var surface = FlatFloor();
			var registry = EntityRegistry.LoadFromJson( FixtureText( "registry.json" ), "registry.json" );
			var entities = new MapEntities( registry );
			var prop = entities.PlacePointEntity( "prop_static", new Vec3( 0f, 0f, 200f ),
				new Dictionary<string, string> { { "model", "props/crate.mdl" }, { "targetname", "crate" } },
				new Vec3( 0f, 90f, 0f ) );
			var result = PropPlacement.SeatOnFloor( entities, prop, surface );
			RequireSeated( result, "entity seat" );
			Near( prop.Origin.z, 0f, 0.01f, "entity origin z after seating" );
			Near( prop.Angles.y, 90f, 0.001f, "entity yaw preserved" );
			Require( MapEntities.TryParseAngles( entities.GetKeyvalue( prop, "angles" ), out var angles ),
				"angles keyvalue does not parse back" );
			NearVec( angles, new Vec3( 0f, 90f, 0f ), 0.001f, "angles keyvalue matches the seated pose" );
		} );

		Check( "origin-height-offset", () =>
		{
			var surface = FlatFloor();
			var options = new PropPlacementOptions { OriginHeightAboveBase = 16f };
			var result = PropPlacement.MoveToFloor( surface, PropPose.Upright( new Vec3( 0f, 0f, 200f ) ), options );
			RequireSeated( result, "offset seat" );
			Near( result.Pose.Origin.z, 16f, 0.01f, "origin sits 16 above the floor" );
		} );

		Check( "align-on-land-false-keeps-tilt", () =>
		{
			var surface = FlatFloor();
			var start = PropPose.FromAngles( new Vec3( 0f, 0f, 200f ), new Vec3( 30f, 0f, 0f ) );
			var result = PropPlacement.MoveToFloor( surface, start, new PropPlacementOptions { AlignOnLand = false } );
			RequireSeated( result, "no-align seat" );
			Near( result.Pose.Origin.z, 0f, 0.01f, "origin still seats on the floor" );
			Near( result.Pose.Angles.x, 30f, 0.01f, "pitch kept when AlignOnLand is false" );
		} );

		Check( "invalid-options-rejected", () =>
		{
			var surface = FlatFloor();
			var pose = PropPose.Upright( new Vec3( 0f, 0f, 200f ) );
			foreach ( var bad in new[]
			{
				new PropPlacementOptions { StepSize = 0f },
				new PropPlacementOptions { MaxSlopeDegrees = 91f },
				new PropPlacementOptions { MaxDistance = 0f },
				new PropPlacementOptions { OriginHeightAboveBase = -1f },
			} )
			{
				try { PropPlacement.MoveToFloor( surface, pose, bad ); }
				catch ( PropPlacementException ex ) when ( ex.Code == PropPlacementError.InvalidOptions ) { continue; }
				throw new Exception( "invalid options were accepted instead of raising invalid-options" );
			}
		} );

		Check( "empty-and-null-surface", () =>
		{
			var pose = PropPose.Upright( new Vec3( 0f, 0f, 200f ) );
			var empty = PropPlacement.MoveToFloor( PropPlacementSurface.Empty(), pose );
			RequireNotSeated( empty, PropPlacementError.NoSurface, "empty surface" );
			try { PropPlacement.MoveToFloor( null!, pose ); }
			catch ( ArgumentNullException ) { return; }
			throw new Exception( "null surface was accepted instead of throwing ArgumentNullException" );
		} );

		Check( "bad-triangle-indices-rejected", () =>
		{
			var vertices = new List<Vec3> { new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 0f, 1f, 0f ) };
			bool threw = false;
			try { PropPlacementSurface.FromTriangles( vertices, new List<int> { 0, 1 } ); }
			catch ( ArgumentException ) { threw = true; }
			Require( threw, "odd index count was accepted" );
			threw = false;
			try { PropPlacementSurface.FromTriangles( vertices, new List<int> { 0, 1, 9 } ); }
			catch ( ArgumentException ) { threw = true; }
			Require( threw, "out-of-range index was accepted" );
		} );

		Check( "raycast-down-up-helpers", () =>
		{
			var surface = PropPlacementSurface.FromQuickPlayMap( RoomMap( withCeiling: true ) );
			Require( PropPlacement.TryRaycastDown( surface, new Vec3( 0f, 0f, 200f ), 4096f, out var down ),
				"raycast down found nothing" );
			// From above the ceiling the first surface below is the ceiling roof (144).
			Near( down.Point.z, 144f, 0.01f, "down hit is the ceiling roof from above" );
			Require( PropPlacement.TryRaycastDown( surface, new Vec3( 0f, 0f, 100f ), 4096f, out down ),
				"raycast down inside the room found nothing" );
			Near( down.Point.z, 0f, 0.01f, "down hit inside the room is the floor" );
			NearVec( down.Normal, new Vec3( 0f, 0f, 1f ), 0.001f, "down normal" );
			Require( PropPlacement.TryRaycastUp( surface, new Vec3( 0f, 0f, 64f ), 4096f, out var up ),
				"raycast up found nothing" );
			Near( up.Point.z, 128f, 0.01f, "up hit is the ceiling underside" );
			NearVec( up.Normal, new Vec3( 0f, 0f, -1f ), 0.001f, "up normal" );
			Near( up.Distance, 64f, 0.01f, "up distance" );
		} );

		Check( "pose-angles-round-trip", () =>
		{
			var pose = PropPose.FromAngles( new Vec3( 1f, 2f, 3f ), new Vec3( 30f, 45f, 10f ) );
			Near( pose.Angles.x, 30f, 0.01f, "pitch round-trips" );
			Near( pose.Angles.y, 45f, 0.01f, "yaw round-trips" );
			Near( pose.Angles.z, 10f, 0.01f, "roll round-trips" );
			var upright = PropPose.Upright( Vec3.Zero, yawDegrees: 270f );
			NearVec( upright.Angles, new Vec3( 0f, 270f, 0f ), 0.001f, "upright angles" );
		} );
	}
}
