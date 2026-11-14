using MapBrushFace = Effigy.MapBrushFace;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;
using Effigy.Map;
using Marionette.Desktop;

/// <summary>
/// Overlays, decals + path_track chains (hammer-overlays-decals-paths, group "overlaysdecals").
///
/// Built against the landed APIs: MapBrush / MapBrushFace (hammer-brush-core),
/// FaceMaterial / FaceUv (hammer-face-material-uv-model), MapClip / MapClipResult
/// (hammer-brush-clip-two-point), MapVertexBrush / MapVertexEdit
/// (hammer-brush-vertex-edit-invalid-face). The cases in
/// tests/fixtures/hammer/hammer-overlays-decals-paths/overlays.json are the same
/// overlay / decal / path chain as data.
///
/// Not yet registered in tests/TestRegistry.cs: that file is coordinator-owned and
/// the final integrator adds the one OverlaysDecalsTests.Register(registry, settings);
/// line.
/// </summary>
internal static class OverlaysDecalsTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "overlaysdecals", name, test );
		void CheckMissing( string name, Action test, Func<string> missing ) => suite.Add( "overlaysdecals", name, test, missing );
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
		void Approx( double actual, double expected, double tol, string what )
		{
			Require( Math.Abs( actual - expected ) <= tol, $"{what}: got {actual}, expected {expected} (tol {tol})" );
		}
		bool HasCode( IReadOnlyList<OverlayDecalIssue> issues, string code ) => issues.Any( i => i.Code == code );

		// Same box as the clip tests: face order 0:-X brick, 1:+X metal, 2:-Y wood,
		// 3:+Y glass, 4:-Z concrete, 5:+Z plaster — distinct materials per face so a
		// test can tell exactly which faces survived a clip and with what data.
		static MapBrushFace Face( Vec3 n, float d, string material, Vec2 uvOffset, Vec2 uvScale = default, float uvRotation = 0f, float lightmapScale = 16f, int smoothingGroup = 0 )
			=> new MapBrushFace( n, d, material,
				new FaceUv( uvOffset, uvScale.x == 0 && uvScale.y == 0 ? new Vec2( 1, 1 ) : uvScale, uvRotation, lightmapScale, smoothingGroup ) );

		static MapBrush Box( double x0, double y0, double z0, double x1, double y1, double z1 )
			=> MapBrush.FromPlanes( new[]
			{
				Face( new Vec3( -1, 0, 0 ), (float)-x0, "brick/brick", new Vec2( 0, 0 ) ),
				Face( new Vec3( 1, 0, 0 ), (float)x1, "metal/metal", new Vec2( 1, 0 ) ),
				Face( new Vec3( 0, -1, 0 ), (float)-y0, "wood/wood", new Vec2( 2, 0 ) ),
				Face( new Vec3( 0, 1, 0 ), (float)y1, "glass/glass", new Vec2( 3, 0 ), new Vec2( 2, 2 ), 15f, 32f, 1 ),
				Face( new Vec3( 0, 0, -1 ), (float)-z0, "concrete/concrete", new Vec2( 4, 0 ) ),
				Face( new Vec3( 0, 0, 1 ), (float)z1, "plaster/plaster", new Vec2( 5, 0 ) ),
			} );

		static MapBrush Box64() => Box( 0, 0, 0, 64, 64, 64 );

		// World-space planar projection: U = world X / 64, V = world Z / 64.
		static MapOverlay ProjectionOverlay( string id, MapBrush brush, params int[] faces )
			=> new MapOverlay( id, "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 0, 1 ), new Vec2( 1f / 64f, 1f / 64f ), FaceUv.Default,
				faces.Select( f => OverlayFaceRef.For( brush, "room", f ) ) );

		static string FindFixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-overlays-decals-paths" );
					if ( File.Exists( Path.Combine( candidate, "overlays.json" ) ) )
						return candidate;
					dir = dir.Parent;
				}
			}
			return null;
		}

		Check( "overlay-projects-world-space-uvs-onto-two-faces", () =>
		{
			var box = Box64();
			var overlay = ProjectionOverlay( "marking", box, 5, 1 );
			Require( overlay.Validate().Count == 0, "the projection overlay must validate clean: " +
				string.Join( "; ", overlay.Validate().Select( i => i.ToString() ) ) );
			var top = overlay.ProjectOnto( box, 5 );
			Require( top.Length == 4, $"top face projected {top.Length} UVs, expected 4" );
			foreach ( var uv in top )
				Approx( uv.y, 1.0, 1e-6, "top face V (z=64, so V=1 everywhere)" );
			Require( top.Any( uv => Math.Abs( uv.x ) < 1e-6 ) && top.Any( uv => Math.Abs( uv.x - 1 ) < 1e-6 ),
				"top face U must span 0..1 (x=0..64)" );
			var side = overlay.ProjectOnto( box, 1 );
			Require( side.Length == 4, $"side face projected {side.Length} UVs, expected 4" );
			foreach ( var uv in side )
				Approx( uv.x, 1.0, 1e-6, "side face U (x=64, so U=1 everywhere)" );
			Require( side.Any( uv => Math.Abs( uv.y ) < 1e-6 ) && side.Any( uv => Math.Abs( uv.y - 1 ) < 1e-6 ),
				"side face V must span 0..1 (z=0..64)" );
		} );

		Check( "overlay-survives-clip-of-one-face", () =>
		{
			var box = Box64();
			var overlay = ProjectionOverlay( "marking", box, 2, 1 );
			var before = overlay.ProjectOnto( box, 2 );
			Approx( before.Max( uv => uv.x ), 1.0, 1e-6, "wood face max U before the clip (x=0..64)" );
			var set = new OverlayDecalSet();
			set.AddOverlay( overlay );
			// Front-view vertical cut at x=32, keep the x<=32 half: the +X (metal) face
			// is cut away entirely, the -Y (wood) face is halved.
			var result = MapClip.Clip( box, new Vec3( 32, 0, 0 ), new Vec3( 32, 0, 64 ), ClipViewAxis.Y );
			Require( result.Outcome == ClipOutcome.Cut, $"outcome was {result.Outcome}, expected Cut" );
			Require( result.Kept is not null && result.Kept.IsValid, "kept brush must exist and be valid" );
			var report = set.ApplyClip( "room", result );
			Require( !report.Unchanged, "a real cut must not report Unchanged" );
			Require( report.Survived.Count == 1, $"expected 1 surviving target, got {report.Survived.Count}" );
			var survivor = report.Survived[0];
			Require( survivor.OverlayId == "marking", $"survivor overlay was '{survivor.OverlayId}', expected 'marking'" );
			Require( survivor.Side == "kept", $"survivor side was '{survivor.Side}', expected 'kept'" );
			Require( survivor.Target.FaceIndex == 2, $"survivor was face {survivor.Target.FaceIndex}, expected the wood face 2" );
			Require( survivor.NewFaceIndex == 1, $"survivor sits at face index {survivor.NewFaceIndex} in the kept brush, expected 1 (-X, -Y, +Y, -Z, +Z, cap)" );
			Approx( survivor.Uvs.Max( uv => uv.x ), 0.5, 1e-6, "surviving wood face max U after the clip (x=0..32, re-projected, not carried over)" );
			Approx( survivor.Uvs.Min( uv => uv.x ), 0.0, 1e-6, "surviving wood face min U after the clip" );
			Require( report.Lost.Count == 1, $"expected 1 lost target, got {report.Lost.Count}" );
			Require( report.Lost[0].Code == OverlayDecalIssue.OverlayFaceLost,
				$"lost target code was {report.Lost[0].Code}, expected OverlayFaceLost" );
			Require( report.Lost[0].Message.Contains( "face 1", StringComparison.Ordinal ),
				$"lost target must name the metal face 1, got: {report.Lost[0].Message}" );
			Require( set.Overlays.Count == 1, "the set keeps the overlay after a partial loss — the caller decides whether to delete it" );
			Require( report.OrphanedDecals.Count == 0, "no decals were in this set" );
		} );

		Check( "overlay-split-resolves-to-one-side-each", () =>
		{
			var box = Box64();
			var set = new OverlayDecalSet();
			set.AddOverlay( ProjectionOverlay( "marking", box, 1, 0 ) );
			var result = MapClip.Clip( box, new Vec3( 0, 0, 0 ), new Vec3( 64, 64, 0 ), ClipViewAxis.Z,
				new MapClipOptions { Mode = ClipMode.Split } );
			Require( result.Outcome == ClipOutcome.Split, $"outcome was {result.Outcome}, expected Split" );
			var report = set.ApplyClip( "room", result );
			Require( report.Survived.Count == 2, $"expected 2 surviving targets, got {report.Survived.Count}" );
			var sides = new HashSet<string>( report.Survived.Select( s => s.Side ) );
			Require( sides.SetEquals( new[] { "kept", "second" } ),
				"the two targets must survive on opposite sides of the split: " + string.Join( ",", sides ) );
			Require( report.Lost.Count == 0, "no target may be lost in this split: " +
				string.Join( "; ", report.Lost.Select( i => i.ToString() ) ) );
		} );

		Check( "overlay-whole-brush-cut-away-loses-all-targets", () =>
		{
			var box = Box64();
			var set = new OverlayDecalSet();
			set.AddOverlay( ProjectionOverlay( "marking", box, 5, 1 ) );
			set.AddDecal( MapDecal.For( "poster", "decals/poster01", box, "room", 5, new Vec3( 32, 32, 64 ), 8 ) );
			var result = MapClip.Clip( box, new Vec3( 100, 0, 0 ), new Vec3( 100, 64, 0 ), ClipViewAxis.Z );
			Require( result.Outcome == ClipOutcome.WholeBrushCutAway, $"outcome was {result.Outcome}, expected WholeBrushCutAway" );
			var report = set.ApplyClip( "room", result );
			Require( report.Survived.Count == 0, "nothing survives a whole-brush cut-away" );
			Require( report.Lost.Count == 2, $"expected 2 lost targets, got {report.Lost.Count}" );
			Require( report.Lost.All( i => i.Code == OverlayDecalIssue.OverlayFaceLost ), "every loss must be OverlayFaceLost" );
			Require( report.OrphanedDecals.Count == 1, $"expected 1 orphaned decal, got {report.OrphanedDecals.Count}" );
			Require( report.OrphanedDecals[0].Code == OverlayDecalIssue.DecalFaceLost, "the orphaned decal must be DecalFaceLost" );
			Require( report.OrphanedDecals[0].Target == "poster", "the orphaned decal issue must name the decal id" );
		} );

		Check( "overlay-noop-clip-changes-nothing", () =>
		{
			var box = Box64();
			var set = new OverlayDecalSet();
			set.AddOverlay( ProjectionOverlay( "marking", box, 5, 1 ) );
			var result = MapClip.Clip( box, new Vec3( 100, 0, 0 ), new Vec3( 100, 64, 0 ), ClipViewAxis.Z,
				new MapClipOptions { KeepFront = false } );
			Require( result.Outcome == ClipOutcome.NoOpPlaneOutside, $"outcome was {result.Outcome}, expected NoOpPlaneOutside" );
			var report = set.ApplyClip( "room", result );
			Require( report.Unchanged, "a no-op clip must report Unchanged" );
			Require( report.Survived.Count == 0 && report.Lost.Count == 0 && report.OrphanedDecals.Count == 0,
				"a no-op clip must re-project nothing and lose nothing" );
		} );

		Check( "overlay-validation-names-defects", () =>
		{
			var box = Box64();
			var blank = new MapOverlay( "blank", "", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 1, 1 ), FaceUv.Default,
				new[] { OverlayFaceRef.For( box, "room", 5 ) } );
			Require( HasCode( blank.Validate(), OverlayDecalIssue.OverlayBlankMaterial ), "blank material must be OverlayBlankMaterial" );
			var noTargets = new MapOverlay( "empty", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 1, 1 ), FaceUv.Default,
				Array.Empty<OverlayFaceRef>() );
			Require( HasCode( noTargets.Validate(), OverlayDecalIssue.OverlayNoTargetFaces ), "no targets must be OverlayNoTargetFaces" );
			var skewed = new MapOverlay( "skewed", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ).Normal, new Vec2( 1, 1 ), FaceUv.Default,
				new[] { OverlayFaceRef.For( box, "room", 5 ) } );
			Require( HasCode( skewed.Validate(), OverlayDecalIssue.OverlayDegenerateProjection ), "non-perpendicular axes must be OverlayDegenerateProjection" );
			var zeroScale = new MapOverlay( "flat", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 0, 1 ), FaceUv.Default,
				new[] { OverlayFaceRef.For( box, "room", 5 ) } );
			Require( HasCode( zeroScale.Validate(), OverlayDecalIssue.OverlayDegenerateProjection ), "zero scale must be OverlayDegenerateProjection" );
			var badUv = new MapOverlay( "baduv", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 1, 1 ),
				new FaceUv( Vec2.Zero, new Vec2( 0, 1 ), 0, 16, 0 ),
				new[] { OverlayFaceRef.For( box, "room", 5 ) } );
			Require( HasCode( badUv.Validate(), OverlayDecalIssue.OverlayBadUv ), "zero UV scale must be OverlayBadUv" );
			var dup = new MapOverlay( "dup", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 1, 1 ), FaceUv.Default,
				new[] { OverlayFaceRef.For( box, "room", 5 ), OverlayFaceRef.For( box, "room", 5 ) } );
			Require( HasCode( dup.Validate(), OverlayDecalIssue.OverlayDuplicateTarget ), "targeting one face twice must be OverlayDuplicateTarget" );
			Throws<ArgumentException>( () => new MapOverlay( "", "decals/roadline", Vec3.Zero,
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 1, 1 ), FaceUv.Default,
				Array.Empty<OverlayFaceRef>() ), "must have an id" );
		} );

		Check( "overlay-and-decal-json-round-trip", () =>
		{
			var box = Box64();
			var overlay = ProjectionOverlay( "marking", box, 5, 1 );
			var overlay2 = MapOverlay.FromJson( overlay.ToJson() );
			Require( overlay2.Id == overlay.Id && overlay2.MaterialName == overlay.MaterialName, "overlay id/material must round-trip" );
			Require( overlay2.Origin.Equals( overlay.Origin ) && overlay2.UAxis.Equals( overlay.UAxis ) && overlay2.VAxis.Equals( overlay.VAxis ),
				"overlay projection basis must round-trip" );
			Require( overlay2.Scale.Equals( overlay.Scale ) && overlay2.Uv.Equals( overlay.Uv ), "overlay scale/UV must round-trip" );
			Require( overlay2.Targets.Count == 2 && overlay2.Targets[0].Equals( overlay.Targets[0] ) && overlay2.Targets[1].Equals( overlay.Targets[1] ),
				"overlay targets must round-trip" );
			var decal = MapDecal.For( "poster", "decals/poster01", box, "room", 5, new Vec3( 32, 32, 64 ), 8, 15 );
			var decal2 = MapDecal.FromJson( decal.ToJson() );
			Require( decal2.Id == decal.Id && decal2.MaterialName == decal.MaterialName, "decal id/material must round-trip" );
			Require( decal2.Face.Equals( decal.Face ), "decal face ref must round-trip" );
			Require( decal2.Position.Equals( decal.Position ) && decal2.Radius == 8f && decal2.RotationDegrees == 15f,
				"decal position/radius/rotation must round-trip" );
			Throws<ArgumentException>( () => MapOverlay.FromJson( new System.Text.Json.Nodes.JsonObject() ), "missing 'id'" );
		} );

		Check( "decal-on-face-validates", () =>
		{
			var box = Box64();
			var decal = MapDecal.For( "poster", "decals/poster01", box, "room", 5, new Vec3( 32, 32, 64 ), 8 );
			Require( decal.ValidateAgainst( box ).Count == 0, "a centred decal on the top face must validate clean: " +
				string.Join( "; ", decal.ValidateAgainst( box ).Select( i => i.ToString() ) ) );
			var edge = MapDecal.For( "edge", "decals/poster01", box, "room", 5, new Vec3( 0, 32, 64 ), 8 );
			Require( edge.ValidateAgainst( box ).Count == 0, "a stamp exactly on the face edge counts as on the face" );
		} );

		Check( "decal-off-face-and-outside-polygon-named", () =>
		{
			var box = Box64();
			var floating = MapDecal.For( "float", "decals/poster01", box, "room", 5, new Vec3( 32, 32, 65 ), 8 );
			var issues = floating.ValidateAgainst( box );
			Require( HasCode( issues, OverlayDecalIssue.DecalOffFace ), "a stamp 1 inch above its face must be DecalOffFace" );
			var outside = MapDecal.For( "outside", "decals/poster01", box, "room", 5, new Vec3( 100, 32, 64 ), 8 );
			Require( HasCode( outside.ValidateAgainst( box ), OverlayDecalIssue.DecalOutsideFace ),
				"a stamp on the face plane but outside the polygon must be DecalOutsideFace" );
			var flat = MapDecal.For( "flat", "decals/poster01", box, "room", 5, new Vec3( 32, 32, 64 ), 0 );
			Require( HasCode( flat.Validate(), OverlayDecalIssue.DecalBadRadius ), "a zero radius must be DecalBadRadius" );
			var blank = MapDecal.For( "blank", "", box, "room", 5, new Vec3( 32, 32, 64 ), 8 );
			Require( HasCode( blank.Validate(), OverlayDecalIssue.DecalBlankMaterial ), "a blank material must be DecalBlankMaterial" );
			Require( HasCode( floating.ValidateAgainst( null ), OverlayDecalIssue.DecalFaceLost ), "a null brush is DecalFaceLost, not a throw" );
		} );

		Check( "decal-stamp-corners-on-face-plane", () =>
		{
			var box = Box64();
			var decal = MapDecal.For( "poster", "decals/poster01", box, "room", 5, new Vec3( 32, 32, 64 ), 8 );
			var corners = decal.StampCorners( box );
			Require( corners.Length == 4, $"a stamp has 4 corners, got {corners.Length}" );
			foreach ( var c in corners )
			{
				Approx( c.z, 64.0, 1e-6, "stamp corner must lie on the face plane z=64" );
				Approx( ( c - new Vec3( 32, 32, 64 ) ).Length, 8 * Math.Sqrt( 2 ), 1e-4, "stamp corner distance from the centre" );
			}
			Require( corners[0].Equals( new Vec3( 40, 40, 64 ) ), $"corner 0 was {corners[0]}, expected (40, 40, 64)" );
			var rotated = MapDecal.For( "rot", "decals/poster01", box, "room", 5, new Vec3( 32, 32, 64 ), 8, 90 );
			var rc = rotated.StampCorners( box );
			Require( rc[0].Equals( new Vec3( 24, 40, 64 ) ), $"rotated corner 0 was {rc[0]}, expected (24, 40, 64) after a 90-degree turn" );
		} );

		Check( "decal-survives-clip-or-orphaned-by-shrunken-face", () =>
		{
			var box = Box64();
			var set = new OverlayDecalSet();
			set.AddDecal( MapDecal.For( "inside", "decals/poster01", box, "room", 2, new Vec3( 16, 0, 32 ), 4 ) );
			set.AddDecal( MapDecal.For( "past-edge", "decals/poster01", box, "room", 2, new Vec3( 48, 0, 32 ), 4 ) );
			set.AddDecal( MapDecal.For( "gone-face", "decals/poster01", box, "room", 1, new Vec3( 64, 32, 32 ), 4 ) );
			var result = MapClip.Clip( box, new Vec3( 32, 0, 0 ), new Vec3( 32, 0, 64 ), ClipViewAxis.Y );
			Require( result.Outcome == ClipOutcome.Cut, $"outcome was {result.Outcome}, expected Cut" );
			var report = set.ApplyClip( "room", result );
			Require( report.OrphanedDecals.Count == 2, $"expected 2 orphaned decals, got {report.OrphanedDecals.Count}" );
			var byId = report.OrphanedDecals.ToDictionary( i => i.Target, i => i.Code );
			Require( byId.TryGetValue( "past-edge", out var pastCode ) && pastCode == OverlayDecalIssue.DecalOutsideFace,
				"the decal past the shrunken edge must be DecalOutsideFace" );
			Require( byId.TryGetValue( "gone-face", out var goneCode ) && goneCode == OverlayDecalIssue.DecalFaceLost,
				"the decal on the removed face must be DecalFaceLost" );
			Require( !byId.ContainsKey( "inside" ), "the decal still inside the halved face must survive" );
		} );

		Check( "path-chain-valid-and-route", () =>
		{
			var chain = new PathTrackChain( "train", new[]
			{
				new PathTrackNode( "track_a", new Vec3( 0, 0, 32 ), 64, "track_b" ),
				new PathTrackNode( "track_b", new Vec3( 64, 0, 32 ), 64, "track_c" ),
				new PathTrackNode( "track_c", new Vec3( 64, 64, 32 ), 0 ),
			} );
			Require( chain.IsValid, "a connected chain must validate clean: " +
				string.Join( "; ", chain.Validate().Select( i => i.ToString() ) ) );
			var route = chain.RouteFromStart().Select( n => n.Name ).ToArray();
			Require( route.SequenceEqual( new[] { "track_a", "track_b", "track_c" } ),
				"the route must follow the Next links: " + string.Join( "->", route ) );
			Require( chain.NodeByName( "track_b" ) is not null && chain.NodeByName( "track_b" ).Position.Equals( new Vec3( 64, 0, 32 ) ),
				"NodeByName must find the middle node" );
			Require( chain.NodeByName( "nope" ) is null, "NodeByName must return null for an unknown name" );
		} );

		Check( "path-missing-node-is-validation-issue", () =>
		{
			var chain = new PathTrackChain( "train", new[]
			{
				new PathTrackNode( "track_a", new Vec3( 0, 0, 32 ), 64, "track_b" ),
				new PathTrackNode( "track_b", new Vec3( 64, 0, 32 ), 64, "missing-node" ),
				new PathTrackNode( "track_c", new Vec3( 64, 64, 32 ), 64 ),
			} );
			var issues = chain.Validate();
			Require( HasCode( issues, OverlayDecalIssue.PathMissingNode ),
				"a Next naming a node that is not in the chain must be PathMissingNode: " +
				string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			var missing = issues.First( i => i.Code == OverlayDecalIssue.PathMissingNode );
			Require( missing.Message.Contains( "missing-node", StringComparison.Ordinal ) &&
				missing.Message.Contains( "'track_b'", StringComparison.Ordinal ),
				$"the issue must name the broken link (track_b -> missing-node), got: {missing.Message}" );
			Require( !chain.IsValid, "a chain with a missing node is not valid" );
		} );

		Check( "path-disconnected-node-named", () =>
		{
			var chain = new PathTrackChain( "train", new[]
			{
				new PathTrackNode( "track_a", new Vec3( 0, 0, 32 ), 64, "track_b" ),
				new PathTrackNode( "track_b", new Vec3( 64, 0, 32 ), 64 ),
				new PathTrackNode( "orphan", new Vec3( 0, 64, 32 ), 64 ),
			} );
			var issues = chain.Validate();
			Require( HasCode( issues, OverlayDecalIssue.PathDisconnected ),
				"an unreachable node must be PathDisconnected: " +
				string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			Require( issues.First( i => i.Code == OverlayDecalIssue.PathDisconnected ).Message.Contains( "'orphan'", StringComparison.Ordinal ),
				"the issue must name the orphan node" );
		} );

		Check( "path-duplicate-node-and-bad-speed-named", () =>
		{
			var dup = new PathTrackChain( "dup", new[]
			{
				new PathTrackNode( "a", new Vec3( 0, 0, 0 ), 64, "b" ),
				new PathTrackNode( "b", new Vec3( 1, 0, 0 ), 64 ),
				new PathTrackNode( "a", new Vec3( 2, 0, 0 ), 64 ),
			} );
			Require( HasCode( dup.Validate(), OverlayDecalIssue.PathDuplicateNode ), "two nodes named 'a' must be PathDuplicateNode" );
			var slow = new PathTrackChain( "slow", new[]
			{
				new PathTrackNode( "a", new Vec3( 0, 0, 0 ), -5, "b" ),
				new PathTrackNode( "b", new Vec3( 1, 0, 0 ), 64 ),
			} );
			var issues = slow.Validate();
			Require( HasCode( issues, OverlayDecalIssue.PathBadSpeed ), "a negative speed must be PathBadSpeed" );
			Require( issues.First( i => i.Code == OverlayDecalIssue.PathBadSpeed ).Target == "a",
				"the bad-speed issue must name the node" );
			var empty = new PathTrackChain( "empty", Array.Empty<PathTrackNode>() );
			Require( HasCode( empty.Validate(), OverlayDecalIssue.PathEmpty ), "a chain with no nodes must be PathEmpty" );
			Throws<ArgumentException>( () => new PathTrackNode( "", new Vec3( 0, 0, 0 ), 64 ), "must have a name" );
		} );

		Check( "path-loop-covering-all-nodes-valid", () =>
		{
			var chain = new PathTrackChain( "loop", new[]
			{
				new PathTrackNode( "a", new Vec3( 0, 0, 32 ), 64, "b" ),
				new PathTrackNode( "b", new Vec3( 64, 0, 32 ), 64, "c" ),
				new PathTrackNode( "c", new Vec3( 64, 64, 32 ), 64, "a" ),
			} );
			Require( chain.IsValid, "a loop covering every node is a legal train loop, not a defect: " +
				string.Join( "; ", chain.Validate().Select( i => i.ToString() ) ) );
			var route = chain.RouteFromStart().Select( n => n.Name ).ToArray();
			Require( route.SequenceEqual( new[] { "a", "b", "c" } ),
				"the route must visit each node once and stop at the repeat: " + string.Join( "->", route ) );
		} );

		Check( "path-json-round-trip", () =>
		{
			var chain = new PathTrackChain( "train", new[]
			{
				new PathTrackNode( "track_a", new Vec3( 0, 0, 32 ), 64, "track_b" ),
				new PathTrackNode( "track_b", new Vec3( 64, 0, 32 ), 64, "track_c" ),
				new PathTrackNode( "track_c", new Vec3( 64, 64, 32 ), 0 ),
			} );
			var chain2 = PathTrackChain.FromJson( chain.ToJson() );
			Require( chain2.Name == "train" && chain2.Nodes.Count == 3, "chain name/node count must round-trip" );
			Require( chain2.Nodes[1].Next == "track_c" && chain2.Nodes[2].Next is null,
				"node links must round-trip (last node has no next)" );
			Require( chain2.RouteFromStart().Select( n => n.Name ).SequenceEqual( new[] { "track_a", "track_b", "track_c" } ),
				"the round-tripped route must match" );
			var set = new OverlayDecalSet();
			set.AddPath( chain );
			var set2 = OverlayDecalSet.FromJson( set.ToJson() );
			Require( set2.Paths.Count == 1 && set2.Paths[0].Name == "train", "a set with one chain must round-trip" );
		} );

		Check( "vertex-edit-reprojects-touched-face-only", () =>
		{
			// Centred 64-inch box; face order matches MapVertexBrush.Box: bottom, top,
			// front(-Y), back(+Y), left(-X), right(+X).
			var brush = MapBrush.FromPlanes( new[]
			{
				Face( new Vec3( 0, 0, -1 ), 32, "concrete/concrete", new Vec2( 0, 0 ) ),
				Face( new Vec3( 0, 0, 1 ), 32, "plaster/plaster", new Vec2( 1, 0 ) ),
				Face( new Vec3( 0, -1, 0 ), 32, "wood/wood", new Vec2( 2, 0 ) ),
				Face( new Vec3( 0, 1, 0 ), 32, "glass/glass", new Vec2( 3, 0 ) ),
				Face( new Vec3( -1, 0, 0 ), 32, "brick/brick", new Vec2( 4, 0 ) ),
				Face( new Vec3( 1, 0, 0 ), 32, "metal/metal", new Vec2( 5, 0 ) ),
			} );
			Require( brush.IsValid, "the centred box fixture must be valid" );
			var materials = new[] { "concrete/concrete", "plaster/plaster", "wood/wood", "glass/glass", "brick/brick", "metal/metal" };
			var vertexBrush = new MapVertexBrush( MapVertexBrush.Box( 32 ).Vertices, MapVertexBrush.Box( 32 ).Faces, materials );
			var overlay = new MapOverlay( "marking", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 1f / 64f, 1f / 64f ), FaceUv.Default,
				new[] { OverlayFaceRef.For( brush, "box", 1 ), OverlayFaceRef.For( brush, "box", 4 ) } );
			var set = new OverlayDecalSet();
			set.AddOverlay( overlay );
			// Move vertex 6 (32,32,32) to (48,32,32): the top face stretches in +X,
			// the left face is untouched.
			var edit = MapVertexEdit.MoveVertex( vertexBrush, 6, new Vec3( 48, 32, 32 ) );
			Require( edit.AffectedFaces.Contains( 1 ), "the move must affect the top face" );
			var report = set.ApplyVertexEdit( "box", vertexBrush, edit.Brush, edit.AffectedFaces );
			Require( report.Lost.Count == 0 && report.NeedsRetarget.Count == 0,
				"no target may be lost or split by a plain vertex move" );
			Require( report.Reprojected.Count == 1, $"expected 1 re-projected target, got {report.Reprojected.Count}" );
			Require( report.Reprojected[0].OverlayId == "marking" && report.Reprojected[0].FaceIndex == 1,
				"the re-projected target must be the top face" );
			Approx( report.Reprojected[0].Uvs.Max( uv => uv.x ), 0.75, 1e-6,
				"re-projected top-face max U must follow the moved vertex (48/64)" );
			Require( report.Unchanged.Count == 1, $"expected 1 unchanged target, got {report.Unchanged.Count}" );
			Require( report.Unchanged[0].FaceIndex == 4, "the unchanged target must be the left face" );
			foreach ( var uv in report.Unchanged[0].Uvs )
				Approx( uv.x, -0.5, 1e-6, "left face U is unchanged (x=-32 everywhere)" );
		} );

		Check( "vertex-edit-split-face-needs-retarget", () =>
		{
			var materials = new[] { "concrete/concrete", "plaster/plaster", "wood/wood", "glass/glass", "brick/brick", "metal/metal" };
			var brush = MapBrush.FromPlanes( new[]
			{
				Face( new Vec3( 0, 0, -1 ), 32, "concrete/concrete", new Vec2( 0, 0 ) ),
				Face( new Vec3( 0, 0, 1 ), 32, "plaster/plaster", new Vec2( 1, 0 ) ),
				Face( new Vec3( 0, -1, 0 ), 32, "wood/wood", new Vec2( 2, 0 ) ),
				Face( new Vec3( 0, 1, 0 ), 32, "glass/glass", new Vec2( 3, 0 ) ),
				Face( new Vec3( -1, 0, 0 ), 32, "brick/brick", new Vec2( 4, 0 ) ),
				Face( new Vec3( 1, 0, 0 ), 32, "metal/metal", new Vec2( 5, 0 ) ),
			} );
			var vertexBrush = new MapVertexBrush( MapVertexBrush.Box( 32 ).Vertices, MapVertexBrush.Box( 32 ).Faces, materials );
			var set = new OverlayDecalSet();
			set.AddOverlay( new MapOverlay( "marking", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 1f / 64f, 1f / 64f ), FaceUv.Default,
				new[] { OverlayFaceRef.For( brush, "box", 1 ), OverlayFaceRef.For( brush, "box", 4 ) } ) );
			// Split the top face (loop 4,5,6,7) between vertices 4 and 6.
			var edit = MapVertexEdit.SplitFace( vertexBrush, 1, 4, 6 );
			Require( edit.Brush.FaceCount == 7, $"a split adds one face, got {edit.Brush.FaceCount}" );
			var report = set.ApplyVertexEdit( "box", vertexBrush, edit.Brush, edit.AffectedFaces );
			Require( report.NeedsRetarget.Count == 1, $"expected 1 split target needing re-targeting, got {report.NeedsRetarget.Count}" );
			Require( report.NeedsRetarget[0].Code == OverlayDecalIssue.OverlayFaceSplit,
				"a split target must be OverlayFaceSplit, not a silent re-point at the first child face" );
			Require( report.Unchanged.Count == 1 && report.Unchanged[0].FaceIndex == 4,
				"the untouched left face must still report unchanged" );
			Require( report.Reprojected.Count == 0 && report.Lost.Count == 0,
				"a split must neither re-project nor lose the target — it asks for re-targeting" );
		} );

		Check( "set-duplicate-id-throws", () =>
		{
			var box = Box64();
			var set = new OverlayDecalSet();
			set.AddOverlay( ProjectionOverlay( "marking", box, 5 ) );
			Throws<ArgumentException>( () => set.AddOverlay( ProjectionOverlay( "marking", box, 1 ) ), "already exists" );
			set.AddDecal( MapDecal.For( "poster", "decals/poster01", box, "room", 5, new Vec3( 32, 32, 64 ), 8 ) );
			Throws<ArgumentException>( () => set.AddDecal( MapDecal.For( "poster", "decals/poster01", box, "room", 1, new Vec3( 64, 32, 32 ), 8 ) ), "already exists" );
			set.AddPath( new PathTrackChain( "train", new[] { new PathTrackNode( "a", Vec3.Zero, 64 ) } ) );
			Throws<ArgumentException>( () => set.AddPath( new PathTrackChain( "train", new[] { new PathTrackNode( "b", Vec3.Zero, 64 ) } ) ), "already exists" );
			Throws<ArgumentNullException>( () => set.AddOverlay( null ) );
		} );

		Check( "set-validate-all-missing-brush-named", () =>
		{
			var set = new OverlayDecalSet();
			set.AddOverlay( new MapOverlay( "marking", "decals/roadline", new Vec3( 0, 0, 0 ),
				new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec2( 1, 1 ), FaceUv.Default,
				new[] { new OverlayFaceRef( "ghost", 0, new Vec3( 0, 0, 1 ), 64, "plaster/plaster" ) } ) );
			var issues = set.ValidateAll( new Dictionary<string, MapBrush>() );
			Require( HasCode( issues, OverlayDecalIssue.OverlayFaceLost ), "a target on a missing brush must be OverlayFaceLost" );
			Require( issues.First( i => i.Code == OverlayDecalIssue.OverlayFaceLost ).Message.Contains( "ghost", StringComparison.Ordinal ),
				"the issue must name the missing brush" );
			Throws<ArgumentNullException>( () => set.ValidateAll( null ) );
		} );

		CheckMissing( "fixture-json-loads-and-validates", () =>
		{
			var dir = FindFixtureDir();
			Require( dir is not null, "fixture dir vanished between the missing-check and the run" );
			var set = OverlayDecalSet.FromJsonString( File.ReadAllText( Path.Combine( dir, "overlays.json" ) ) );
			Require( set.Overlays.Count == 1 && set.Decals.Count == 1 && set.Paths.Count == 1,
				$"fixture must hold 1 overlay, 1 decal, 1 path chain; got {set.Overlays.Count}/{set.Decals.Count}/{set.Paths.Count}" );
			Require( set.Overlays[0].Id == "road-marking" && set.Decals[0].Id == "poster" && set.Paths[0].Name == "train-loop",
				"fixture ids must be road-marking / poster / train-loop" );
			var issues = set.ValidateAll( new Dictionary<string, MapBrush> { ["room"] = Box64() } );
			Require( issues.Count == 0, "the fixture set must validate clean against the 64-inch room box: " +
				string.Join( "; ", issues.Select( i => i.ToString() ) ) );
			var set2 = OverlayDecalSet.FromJson( set.ToJson() );
			Require( set2.Overlays[0].Id == "road-marking" && set2.Decals[0].Id == "poster" && set2.Paths[0].Name == "train-loop",
				"the fixture set must survive a JSON round-trip" );
			Require( set2.Overlays[0].Targets.Count == 2, "the round-tripped overlay must keep both target faces" );
		}, () => FindFixtureDir() is null ? "fixture overlays.json not found under tests/fixtures/hammer/hammer-overlays-decals-paths" : null );
	}
}
