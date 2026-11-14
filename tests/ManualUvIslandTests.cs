using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Marionette.Desktop;

// Manual UV island edits + the automatic unwrap they sit on.
//
// NOT YET REGISTERED in tests/TestRegistry.cs (coordinator-owned; the final
// integrator adds the one ManualUvIslandTests.Register line, as with
// KernelParityTests). Written for the final build/test gate, not run here:
// this ticket is edits-only, no full compile (PO workflow override 2026-11-13).
//
// What is NOT covered here, and why: the studio round-trip (island edit
// survives rebuild, undo and save/reopen) needs the persistence hooks in
// PartStudio.cs / StudioDocument.cs, which are owned by other in-flight
// workers (assembly-mate owns PartStudio.cs, cad-configurations owns
// StudioDocument.cs). The exact hook patch ships with this ticket as
// handoffs/manual-uv-island-persistence-hooks.patch; the integrator adds the
// rebuild/save round-trip check once it lands. Everything below runs on the
// ported code alone: the edit record, its line format, the mesh-level
// transform, pins, seams, the orientation checker, the xatlas route, the
// topology cache and the dense-mesh proxy.
internal static class ManualUvIslandTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "manual-uv", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float tol = 1e-5f )
		{
			Require( MathF.Abs( actual - expected ) <= tol, $"expected {expected}, got {actual}" );
		}
		void NearUv( Vec2 actual, Vec2 expected, float tol = 1e-5f )
		{
			Near( actual.x, expected.x, tol );
			Near( actual.y, expected.y, tol );
		}

		// Two disconnected quads, each its own island, UVs 0..1 on both.
		PolyMesh TwoQuads()
		{
			var m = new PolyMesh();
			foreach ( var p in new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
				new Vec3( 2, 0, 0 ), new Vec3( 3, 0, 0 ), new Vec3( 3, 1, 0 ), new Vec3( 2, 1, 0 ) } )
				m.Positions.Add( p );
			foreach ( var idx in new[] { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 } } )
			{
				var f = new Face( idx );
				f.UVs[0] = new Vec2( 0, 0 ); f.UVs[1] = new Vec2( 1, 0 );
				f.UVs[2] = new Vec2( 1, 1 ); f.UVs[3] = new Vec2( 0, 1 );
				m.Faces.Add( f );
			}
			return m;
		}

		ManualUVIslandEdit MoveA( float dx, float dy ) => new()
		{
			BodyId = "b1", FaceKey = "0:0,1,2,3",
			Offset = new Vec2( dx, dy ), RotationDegrees = 0f, Scale = new Vec2( 1, 1 ),
		};

		Vec2[] SnapshotUvs( PolyMesh m ) => m.Faces.SelectMany( f => f.UVs ).ToArray();
		Vec3[] SnapshotPos( PolyMesh m ) => m.Positions.ToArray();

		// Asymmetric checker: every fan triangle must keep its winding. A mirrored
		// island (or a packer turn that flipped instead of rotating) shows up here
		// as a UV signed area with the opposite sign to the 3D one. Also bounds.
		void RequireOrientationAndBounds( PolyMesh m )
		{
			for ( var fi = 0; fi < m.FaceCount; fi++ )
			{
				var face = m.Faces[fi];
				var n = m.FaceNormal( face );
				for ( var c = 0; c < face.UVs.Length; c++ )
				{
					var uv = face.UVs[c];
					Require( float.IsFinite( uv.x ) && float.IsFinite( uv.y ), $"face {fi} corner {c} UV is not finite" );
					Require( uv.x >= -1e-4f && uv.x <= 1f + 1e-4f && uv.y >= -1e-4f && uv.y <= 1f + 1e-4f,
						$"face {fi} corner {c} UV {uv.x},{uv.y} outside the unit square" );
				}
				for ( var c = 1; c + 1 < face.Count; c++ )
				{
					var a = m.Positions[face.Indices[0]];
					var b = m.Positions[face.Indices[c]];
					var d = m.Positions[face.Indices[c + 1]];
					var d3 = Vec3.Dot( n, Vec3.Cross( b - a, d - a ) );
					var ua = face.UVs[0]; var ub = face.UVs[c]; var uc = face.UVs[c + 1];
					var dUv = (ub.x - ua.x) * (uc.y - ua.y) - (ub.y - ua.y) * (uc.x - ua.x);
					Require( d3 * dUv > 0f, $"face {fi} triangle {c} flipped: 3D {d3} vs UV {dUv}" );
				}
			}
		}

		Check( "move shifts one island only", () =>
		{
			var m = TwoQuads();
			var posBefore = SnapshotPos( m );
			Require( UvEditService.TryApplyIslandEdit( m, MoveA( 0.5f, 0.25f ), out var error ), error ?? "apply failed" );
			NearUv( m.Faces[0].UVs[0], new Vec2( 0.5f, 0.25f ) );
			NearUv( m.Faces[0].UVs[1], new Vec2( 1.5f, 0.25f ) );
			NearUv( m.Faces[0].UVs[2], new Vec2( 1.5f, 1.25f ) );
			NearUv( m.Faces[0].UVs[3], new Vec2( 0.5f, 1.25f ) );
			NearUv( m.Faces[1].UVs[0], new Vec2( 0, 0 ) );
			NearUv( m.Faces[1].UVs[2], new Vec2( 1, 1 ) );
			for ( var i = 0; i < posBefore.Length; i++ )
				Require( m.Positions[i].Equals( posBefore[i] ), "island edit moved geometry" );
		} );

		Check( "rotate and scale about island centre", () =>
		{
			var m = TwoQuads();
			var edit = MoveA( 0, 0 );
			edit.RotationDegrees = 90f;
			edit.Scale = new Vec2( 2, 2 );
			Require( UvEditService.TryApplyIslandEdit( m, edit, out var error ), error ?? "apply failed" );
			NearUv( m.Faces[0].UVs[0], new Vec2( 1.5f, -0.5f ) );
			NearUv( m.Faces[0].UVs[1], new Vec2( 1.5f, 1.5f ) );
			NearUv( m.Faces[0].UVs[2], new Vec2( -0.5f, 1.5f ) );
			NearUv( m.Faces[0].UVs[3], new Vec2( -0.5f, -0.5f ) );
			NearUv( m.Faces[1].UVs[0], new Vec2( 0, 0 ) );
		} );

		Check( "apply twice with already-applied set is a no-op", () =>
		{
			var m = TwoQuads();
			var body = new Body( "b1", "b1", m );
			var applied = new HashSet<string>();
			ManualUVIslandEdit.Apply( new[] { body }, new[] { MoveA( 1, 0 ) }, applied );
			NearUv( m.Faces[0].UVs[0], new Vec2( 1, 0 ) );
			ManualUVIslandEdit.Apply( new[] { body }, new[] { MoveA( 1, 0 ) }, applied );
			NearUv( m.Faces[0].UVs[0], new Vec2( 1, 0 ) );
		} );

		Check( "stale key is refused and leaves mesh untouched", () =>
		{
			var m = TwoQuads();
			var before = SnapshotUvs( m );
			var stale = MoveA( 1, 0 );
			stale.FaceKey = "99:9,9,9,9";
			Require( UvEditService.ValidateIslandEdit( m, stale ) is not null, "stale key validated clean" );
			Require( !UvEditService.TryApplyIslandEdit( m, stale, out var error ), "stale key applied" );
			Require( error is not null && error.Contains( "no island" ), $"unexpected error: {error}" );
			var after = SnapshotUvs( m );
			for ( var i = 0; i < before.Length; i++ )
				Require( before[i].Equals( after[i] ), "refused edit touched the mesh" );
			Require( UvEditService.FindIslandFaces( m, "99:9,9,9,9" ) is null, "bogus key resolved" );
			Require( UvEditService.FindIslandFaces( m, "0:0,1,2,3" ) is { Count: 1 }, "real key did not resolve" );
		} );

		Check( "malformed edits report errors", () =>
		{
			var m = TwoQuads();
			Require( UvEditService.ValidateIslandEdit( null, MoveA( 0, 0 ) ) is not null, "null mesh passed" );
			Require( UvEditService.ValidateIslandEdit( m, null ) is not null, "null edit passed" );
			var noBody = MoveA( 0, 0 ); noBody.BodyId = "";
			Require( UvEditService.ValidateIslandEdit( m, noBody ) is not null, "empty body id passed" );
			var noKey = MoveA( 0, 0 ); noKey.FaceKey = " ";
			Require( UvEditService.ValidateIslandEdit( m, noKey ) is not null, "empty key passed" );
			var nanOff = MoveA( 0, 0 ); nanOff.Offset = new Vec2( float.NaN, 0 );
			Require( UvEditService.ValidateIslandEdit( m, nanOff ) is not null, "NaN offset passed" );
			var nanRot = MoveA( 0, 0 ); nanRot.RotationDegrees = float.PositiveInfinity;
			Require( UvEditService.ValidateIslandEdit( m, nanRot ) is not null, "infinite rotation passed" );
			var nanScale = MoveA( 0, 0 ); nanScale.Scale = new Vec2( 1, float.NaN );
			Require( UvEditService.ValidateIslandEdit( m, nanScale ) is not null, "NaN scale passed" );
			var zeroX = MoveA( 0, 0 ); zeroX.Scale = new Vec2( 0, 1 );
			Require( UvEditService.ValidateIslandEdit( m, zeroX ) is not null, "zero X scale passed" );
			var zeroY = MoveA( 0, 0 ); zeroY.Scale = new Vec2( 1, 0 );
			Require( UvEditService.ValidateIslandEdit( m, zeroY ) is not null, "zero Y scale passed" );
			Require( UvEditService.ValidateIslandEdit( m, MoveA( 0, 0 ) ) is null, "valid edit reported an error" );
			var mirror = MoveA( 0, 0 ); mirror.Scale = new Vec2( -1, 1 );
			Require( UvEditService.ValidateIslandEdit( m, mirror ) is null, "deliberate mirror refused" );
		} );

		Check( "write/read line round-trips, bad lines return null", () =>
		{
			var edit = new ManualUVIslandEdit
			{
				BodyId = "body-7", FaceKey = "0:0,1,2,3",
				Offset = new Vec2( -1.5f, 2.25f ), RotationDegrees = 33.5f, Scale = new Vec2( 0.5f, 2f ),
			};
			var line = edit.WriteLine();
			Require( line.StartsWith( "manualuv body-7 ", StringComparison.Ordinal ), $"unexpected line: {line}" );
			var back = ManualUVIslandEdit.ReadLine( line );
			Require( back is not null, "round-trip returned null" );
			Require( back.BodyId == edit.BodyId && back.FaceKey == edit.FaceKey, "round-trip changed identity" );
			Near( back.Offset.x, -1.5f ); Near( back.Offset.y, 2.25f );
			Near( back.RotationDegrees, 33.5f );
			Near( back.Scale.x, 0.5f ); Near( back.Scale.y, 2f );
			Require( ManualUVIslandEdit.ReadLine( "manualuv a b" ) is null, "short line parsed" );
			Require( ManualUVIslandEdit.ReadLine( "manualuv a !!! 1 2 3 4 5" ) is null, "bad base64 parsed" );
			Require( ManualUVIslandEdit.ReadLine( "" ) is null, "empty line parsed" );
			Require( ManualUVIslandEdit.ReadLine( null ) is null, "null line parsed" );
		} );

		Check( "fingerprint is order-independent and sensitive", () =>
		{
			var a = MoveA( 1, 0 );
			var b = MoveA( 0, 1 ); b.FaceKey = "1:4,5,6,7";
			Require( ManualUVIslandEdit.Fingerprint( new[] { a, b } ) == ManualUVIslandEdit.Fingerprint( new[] { b, a } ),
				"fingerprint depends on order" );
			var changed = MoveA( 2, 0 );
			Require( ManualUVIslandEdit.Fingerprint( new[] { a, b } ) != ManualUVIslandEdit.Fingerprint( new[] { changed, b } ),
				"fingerprint missed a change" );
		} );

		Check( "seam split uvs keep islands apart", () =>
		{
			var m = new PolyMesh();
			foreach ( var p in new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
				new Vec3( 2, 0, 0 ), new Vec3( 2, 1, 0 ) } )
				m.Positions.Add( p );
			var fa = new Face( new[] { 0, 1, 2, 3 } );
			fa.UVs[0] = new Vec2( 0, 0 ); fa.UVs[1] = new Vec2( 1, 0 ); fa.UVs[2] = new Vec2( 1, 1 ); fa.UVs[3] = new Vec2( 0, 1 );
			var fb = new Face( new[] { 1, 4, 5, 2 } );
			fb.UVs[0] = new Vec2( 2, 0 ); fb.UVs[1] = new Vec2( 3, 0 ); fb.UVs[2] = new Vec2( 3, 1 ); fb.UVs[3] = new Vec2( 2, 1 );
			m.Faces.Add( fa ); m.Faces.Add( fb );
			Require( UVIslands.Find( m ).Count == 2, "shared edge with split UVs is one island" );
			var edit = new ManualUVIslandEdit
			{
				BodyId = "b1", FaceKey = ManualUVIslandEdit.Key( m, new[] { 1 } ),
				Offset = new Vec2( 10, 0 ), RotationDegrees = 0f, Scale = new Vec2( 1, 1 ),
			};
			Require( UvEditService.TryApplyIslandEdit( m, edit, out var error ), error ?? "apply failed" );
			NearUv( m.Faces[1].UVs[0], new Vec2( 12, 0 ) );
			NearUv( m.Faces[0].UVs[1], new Vec2( 1, 0 ) );
		} );

		Check( "marked seam splits unwrap charts", () =>
		{
			PolyMesh Strip()
			{
				var m = new PolyMesh();
				foreach ( var p in new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
					new Vec3( 2, 0, 0 ), new Vec3( 2, 1, 0 ) } )
					m.Positions.Add( p );
				m.Faces.Add( new Face( new[] { 0, 1, 2, 3 } ) );
				m.Faces.Add( new Face( new[] { 1, 4, 5, 2 } ) );
				return m;
			}
			Require( UVUnwrap.Unwrap( Strip() ).Charts == 1, "flat strip unwrapped as more than one chart" );
			var seamed = Strip();
			seamed.Seams.Add( new EdgeKey( 1, 2 ) );
			Require( UVUnwrap.Unwrap( seamed ).Charts == 2, "marked seam did not split the charts" );
		} );

		Check( "relax pins hold after an island move", () =>
		{
			var m = TwoQuads();
			Require( UvEditService.TryApplyIslandEdit( m, MoveA( 2, 3 ), out var error ), error ?? "apply failed" );
			var r = UvEditService.Relax( m, new UvEditService.Options { Iterations = 3, Pinned = new[] { 1 } } );
			NearUv( r.Faces[0].UVs[1], new Vec2( 3, 3 ) );
		} );

		Check( "unwrap keeps orientation on grid and box", () =>
		{
			var grid = new PolyMesh();
			const int n = 8;
			for ( var j = 0; j <= n; j++ )
				for ( var i = 0; i <= n; i++ )
				{
					var x = (float)i / n; var y = (float)j / n;
					grid.Positions.Add( new Vec3( x, y, 0.1f * MathF.Sin( MathF.PI * x ) * MathF.Sin( MathF.PI * y ) ) );
				}
			for ( var j = 0; j < n; j++ )
				for ( var i = 0; i < n; i++ )
				{
					var a = j * (n + 1) + i;
					grid.Faces.Add( new Face( new[] { a, a + 1, a + n + 2, a + n + 1 } ) );
				}
			var rg = UVUnwrap.Unwrap( grid );
			Require( rg.Faces == n * n && rg.SkippedFaces == 0, $"grid unwrap: {rg}" );
			RequireOrientationAndBounds( grid );

			var box = new PolyMesh();
			foreach ( var p in new[] { new Vec3( 0, 0, 0 ), new Vec3( 1, 0, 0 ), new Vec3( 1, 1, 0 ), new Vec3( 0, 1, 0 ),
				new Vec3( 0, 0, 1 ), new Vec3( 1, 0, 1 ), new Vec3( 1, 1, 1 ), new Vec3( 0, 1, 1 ) } )
				box.Positions.Add( p );
			foreach ( var idx in new[] { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
				new[] { 3, 7, 6, 2 }, new[] { 0, 4, 7, 3 }, new[] { 1, 2, 6, 5 } } )
				box.Faces.Add( new Face( idx ) );
			var rb = UVUnwrap.Unwrap( box );
			Require( rb.Faces == 6 && rb.SkippedFaces == 0, $"box unwrap: {rb}" );
			RequireOrientationAndBounds( box );
		} );

		Check( "xatlas hook drives unwrap when installed", () =>
		{
			var m = TwoQuads();
			var posBefore = SnapshotPos( m );
			var runs0 = UVUnwrap.XAtlasRuns;
			UVUnwrap.XAtlas = ( pos, idx, sizes, padding ) =>
			{
				var uv = new float[idx.Length * 2];
				for ( var k = 0; k < idx.Length; k++ ) { uv[k * 2] = (k % 8) / 8f; uv[k * 2 + 1] = (k / 8) / 8f; }
				return (uv, 3);
			};
			try
			{
				var r = UVUnwrap.Unwrap( m );
				Require( r.Charts == 3, $"hook charts not honoured: {r}" );
				Require( r.Faces == 2 && r.SkippedFaces == 0, $"hook report wrong: {r}" );
				Require( UVUnwrap.XAtlasRuns == runs0 + 1, "hook run not counted" );
				NearUv( m.Faces[0].UVs[0], new Vec2( 0, 0 ) );
				NearUv( m.Faces[0].UVs[1], new Vec2( 1f / 8f, 0 ) );
				NearUv( m.Faces[1].UVs[0], new Vec2( 4f / 8f, 0 ) );
				var posAfter = SnapshotPos( m );
				for ( var i = 0; i < posBefore.Length; i++ )
					Require( posBefore[i].Equals( posAfter[i] ), "xatlas path moved geometry" );
			}
			finally { UVUnwrap.XAtlas = null; }
		} );

		Check( "xatlas failure falls back to kernel path", () =>
		{
			var runs0 = UVUnwrap.XAtlasRuns;
			UVUnwrap.XAtlas = ( pos, idx, sizes, padding ) => throw new InvalidOperationException( "native boom" );
			try
			{
				var r = UVUnwrap.Unwrap( TwoQuads() );
				Require( r.Charts == 2, $"throwing hook did not fall back: {r}" );
				Require( UVUnwrap.XAtlasRuns == runs0, "throwing hook counted as a run" );
			}
			finally { UVUnwrap.XAtlas = null; }
			UVUnwrap.XAtlas = ( pos, idx, sizes, padding ) => (new float[4], 1);
			try
			{
				var r = UVUnwrap.Unwrap( TwoQuads() );
				Require( r.Charts == 2, $"short hook result did not fall back: {r}" );
				Require( UVUnwrap.XAtlasRuns == runs0, "short hook result counted as a run" );
			}
			finally { UVUnwrap.XAtlas = null; }
			var plain = UVUnwrap.Unwrap( TwoQuads() );
			Require( plain.Charts == 2 && UVUnwrap.XAtlasRuns == runs0, "null hook took the native path" );
		} );

		Check( "topology cache reuses layout", () =>
		{
			var m = TwoQuads();
			UVUnwrap.ClearCache();
			var hits0 = UVUnwrap.CacheHits; var miss0 = UVUnwrap.CacheMisses;
			try
			{
				var r1 = UVUnwrap.UnwrapAll( new[] { m } );
				Require( UVUnwrap.LastPath == "full", $"first unwrap path was {UVUnwrap.LastPath}, not full" );
				Require( UVUnwrap.CacheMisses == miss0 + 1, "first unwrap did not miss the cache" );
				var uvs1 = SnapshotUvs( m );
				var r2 = UVUnwrap.UnwrapAll( new[] { m } );
				Require( UVUnwrap.LastPath == "cache", $"second unwrap path was {UVUnwrap.LastPath}, not cache" );
				Require( UVUnwrap.CacheHits == hits0 + 1, "second unwrap did not hit the cache" );
				Require( r2.Faces == r1.Faces && r2.Charts == r1.Charts, "cached report differs" );
				var uvs2 = SnapshotUvs( m );
				for ( var i = 0; i < uvs1.Length; i++ )
					Require( uvs1[i].Equals( uvs2[i] ), "cached layout differs from the first" );
			}
			finally { UVUnwrap.ClearCache(); }
		} );

		Check( "dense fixture takes proxy path and is measured", () =>
		{
			const int n = 130;
			var m = new PolyMesh();
			for ( var j = 0; j <= n; j++ )
				for ( var i = 0; i <= n; i++ )
					m.Positions.Add( new Vec3( (float)i / n, (float)j / n, 0 ) );
			for ( var j = 0; j < n; j++ )
				for ( var i = 0; i < n; i++ )
				{
					var a = j * (n + 1) + i;
					m.Faces.Add( new Face( new[] { a, a + 1, a + n + 2, a + n + 1 } ) );
				}
			Require( m.FaceCount == n * n, "dense fixture face count wrong" );
			UVUnwrap.ClearCache();
			try
			{
				var r = UVUnwrap.Unwrap( m );
				Require( UVUnwrap.LastPath == "proxy", $"dense unwrap path was {UVUnwrap.LastPath}, not proxy" );
				Require( UVUnwrap.LastMs > 0, "dense unwrap was not timed" );
				Require( r.Faces == n * n && r.SkippedFaces == 0, $"dense unwrap: {r}" );
				RequireOrientationAndBounds( m );
			}
			finally { UVUnwrap.ClearCache(); }
		} );
	}
}
