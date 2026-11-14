using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Effigy;

// Hammer texture scale / UV locks (no UI).
// Board task: hammer-texture-scale-uv-locks (worker muse-3-desktop-hammer-46).
// Group: texturelock. Register line (coordinator-owned TestRegistry.cs):
//     TextureLockTests.Register( registry, settings );
// The fixture below is tests/fixtures/hammer/hammer-texture-scale-uv-locks/locks.json.
//
// What is tested here:
//   * LockFlags on FaceUv (+ FaceMaterial): set/query, equality, JSON round-trip.
//   * TextureLock.ApplyScaleLock: the texture's world size survives a resize.
//   * TextureLock.ApplyUvLock: corner UVs survive an edit — exact where the
//     FaceUv form allows it (translation, uniform resize, 90-degree rotation),
//     a bounded approximation for a shear (centre exact) — see ApplyUvLock.
//   * MapTransform.ApplyUvLock / ApplyScaleLock / TransformFaces: the wired
//     transform path on the world-space MapUvMapping model (shear exact).
//   * TextureLock.CheckLockPersistence: the helper the clip/CSG paths call.
//   * The clip wiring contract at the helper level (kept faces keep locks).
//
// DEFERRED, not faked: the end-to-end "a clip of a locked brush preserves the
// lock on the kept faces" check against MapClip.Clip itself. MapClip.cs is
// owned by worker 06 (unclaimed) and was written against an assumed MapFace
// API the LANDED MapBrush does not have, so the clip wiring lands as a
// follow-up; the helper contract it will call is tested here instead
// ("clip wiring contract: kept faces keep locks (helper-level)").

internal static class TextureLockTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "texturelock", name, test );
		void CheckMissing( string name, Action test, Func<string?> missing ) => registry.Add( "texturelock", name, test, missing );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof( T ).Name}, got silent success" );
		}
		void Near( double actual, double expected, double tolerance, string message )
		{
			if ( Math.Abs( actual - expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}
		void NearVec2( Vec2 actual, Vec2 expected, float tolerance, string message )
		{
			if ( (actual - expected).Length > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}
		void NearVec2Sys( Vector2 actual, Vector2 expected, float tolerance, string message )
		{
			if ( Vector2.Distance( actual, expected ) > tolerance )
				throw new Exception( $"{message}: expected {expected}, got {actual}" );
		}

		// The +Z quad used by the FaceUv tests: 4 wide (U) by 2 tall (V).
		static Vec3[] Quad() => new Vec3[]
		{
			new( 0, 0, 0 ), new( 4, 0, 0 ), new( 4, 2, 0 ), new( 0, 2, 0 ),
		};
		var topNormal = new Vec3( 0, 0, 1 );

		// 1. Lock flags are distinct bits.
		Check( "lock flags are distinct bits", () =>
		{
			Require( (int)LockFlags.None == 0, "None is 0" );
			Require( (int)LockFlags.ScaleLock == 1, "ScaleLock is 1" );
			Require( (int)LockFlags.UvLock == 2, "UvLock is 2" );
			Require( (int)LockFlags.All == 3, "All is 3" );
			Require( (LockFlags.ScaleLock & LockFlags.UvLock) == LockFlags.None, "ScaleLock and UvLock share no bit" );
			Require( (LockFlags.All & LockFlags.ScaleLock) != 0 && (LockFlags.All & LockFlags.UvLock) != 0, "All covers both flags" );
		} );

		// 2. FaceUv carries locks; FaceMaterial exposes them; equality sees them.
		Check( "FaceUv carries locks and equality sees them", () =>
		{
			Require( FaceUv.Default.Locks == LockFlags.None, "default FaceUv is unlocked" );
			var uv = new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 16f, 0, LockFlags.UvLock );
			Require( uv.Locks == LockFlags.UvLock, "6-arg ctor sets the locks" );
			var relocked = uv.WithLocks( LockFlags.All );
			Require( relocked.Locks == LockFlags.All, "WithLocks sets All" );
			Require( uv.Locks == LockFlags.UvLock, "WithLocks does not mutate the input" );
			Require( relocked.Offset.Equals( uv.Offset ) && relocked.Scale.Equals( uv.Scale ) &&
				relocked.RotationDegrees == uv.RotationDegrees, "WithLocks keeps the transform" );
			Require( !uv.Equals( relocked ), "locked and differently-locked UVs are not equal" );
			Require( uv.Equals( new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 16f, 0, LockFlags.UvLock ) ), "identical locked UVs are equal" );
			Require( uv.GetHashCode() == new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 16f, 0, LockFlags.UvLock ).GetHashCode(),
				"equal UVs hash equally" );
			var face = new FaceMaterial( "brick/brickwall001", uv );
			Require( face.Uv.Locks == LockFlags.UvLock, "FaceMaterial exposes the locks" );
			var relockedFace = face.WithLocks( LockFlags.ScaleLock );
			Require( relockedFace.Uv.Locks == LockFlags.ScaleLock, "FaceMaterial.WithLocks sets the locks" );
			Require( face.Uv.Locks == LockFlags.UvLock, "FaceMaterial.WithLocks does not mutate the input" );
			Require( relockedFace.MaterialName == face.MaterialName, "FaceMaterial.WithLocks keeps the material" );
		} );

		// 3. AlignJustify keeps the locks (both branches).
		Check( "AlignJustify preserves locks", () =>
		{
			var uv = new FaceUv( Vec2.Zero, new Vec2( 0.5f, 0.5f ), 0f, 16f, 0, LockFlags.UvLock );
			Require( uv.AlignJustify( UvJustify.Left, 0f, 4f, 0f, 2f ).Locks == LockFlags.UvLock, "Left keeps the locks" );
			Require( uv.AlignJustify( UvJustify.Fit, 0f, 4f, 0f, 2f ).Locks == LockFlags.UvLock, "Fit keeps the locks" );
			Require( uv.AlignJustify( UvJustify.Center, 0f, 4f, 0f, 2f ).Scale.Equals( uv.Scale ), "Justify still keeps the scale" );
		} );

		// 4. Locks survive JSON round-trips; missing locks load as None.
		Check( "locks survive JSON round-trips", () =>
		{
			var uv = new FaceUv( new Vec2( 0.25f, -0.5f ), new Vec2( 2f, 0.5f ), 45f, 8f, 3, LockFlags.All );
			var round = FaceUv.FromJson( uv.ToJson() );
			Require( round.Equals( uv ), "FaceUv JSON round-trip preserves locks" );
			Require( round.Locks == LockFlags.All, "round-tripped locks are All" );
			var noLocks = FaceUv.FromJson( System.Text.Json.Nodes.JsonNode.Parse( "{\"offset\":[0,0],\"scale\":[1,1],\"rotation\":0,\"lightmapScale\":16,\"smoothingGroup\":0}" )!.AsObject() );
			Require( noLocks.Locks == LockFlags.None, "a document without locks loads as unlocked" );
			var named = FaceUv.FromJson( System.Text.Json.Nodes.JsonNode.Parse( "{\"offset\":[0,0],\"scale\":[1,1],\"rotation\":0,\"lightmapScale\":16,\"smoothingGroup\":0,\"locks\":\"ScaleLock,UvLock\"}" )!.AsObject() );
			Require( named.Locks == LockFlags.All, "locks parse from names too" );
			var face = new FaceMaterial( "brick/brickwall001", uv );
			var faceRound = FaceMaterial.FromJsonString( face.ToJsonString() );
			Require( faceRound.Equals( face ), "FaceMaterial JSON round-trip preserves locks" );
		} );

		// 5. The document-level default applies to new faces only.
		Check( "document default locks apply to new faces only", () =>
		{
			var saved = LockDefaults.Default;
			try
			{
				Require( LockDefaults.Default == LockFlags.None, "document default starts at None" );
				LockDefaults.Default = LockFlags.UvLock;
				Require( LockDefaults.ForNewFace( FaceUv.Default ).Locks == LockFlags.UvLock, "ForNewFace takes the default" );
				var face = LockDefaults.ForNewFace( new FaceMaterial( "brick/brickwall001" ) );
				Require( face.Uv.Locks == LockFlags.UvLock, "ForNewFace applies to a FaceMaterial" );
				Require( FaceUv.Default.Locks == LockFlags.None, "setting the default does not rewrite existing faces" );
			}
			finally { LockDefaults.Default = saved; }
			Require( LockDefaults.Default == saved, "default restored after the test" );
		} );

		// 6. Spec case: scale lock keeps the texture's world size after a resize.
		Check( "scale lock keeps texture world size after a resize", () =>
		{
			var uv = new FaceUv( Vec2.Zero, new Vec2( 0.5f, 0.25f ), 0f, 16f, 0, LockFlags.ScaleLock );
			var doubled = new Vec3[] { new( 0, 0, 0 ), new( 8, 0, 0 ), new( 8, 2, 0 ), new( 0, 2, 0 ) };
			var locked = TextureLock.ApplyScaleLock( uv, Quad(), topNormal, doubled, topNormal );
			NearVec2( locked.Scale, new Vec2( 0.25f, 0.25f ), 1e-6f, "U extent doubled, so U scale halves; V unchanged" );
			Require( locked.Offset.Equals( uv.Offset ) && locked.RotationDegrees == uv.RotationDegrees,
				"scale lock keeps offset and rotation" );
			Require( locked.Locks == LockFlags.ScaleLock, "scale lock keeps the locks" );
			// The UV range across the face — the texture's world size — is unchanged.
			var before = uv.ComputeUvs( Quad(), topNormal );
			var after = locked.ComputeUvs( doubled, topNormal );
			Near( UvRange( before, 0 ), UvRange( after, 0 ), 1e-6, "U range across the face unchanged" );
			Near( UvRange( before, 1 ), UvRange( after, 1 ), 1e-6, "V range across the face unchanged" );
			// The extents overload agrees with the geometry overload.
			var byExtents = TextureLock.ApplyScaleLock( uv, 4f, 2f, 8f, 2f );
			Require( byExtents.Equals( locked ), "extents overload matches the geometry overload" );
		} );

		// 7. Scale lock rejects degenerate extents.
		Check( "scale lock rejects degenerate extents", () =>
		{
			var uv = FaceUv.Default;
			Throws<ArgumentException>( () => TextureLock.ApplyScaleLock( uv, 4f, 2f, 0f, 2f ) );
			Throws<ArgumentException>( () => TextureLock.ApplyScaleLock( uv, 4f, 2f, 8f, 0f ) );
			Throws<ArgumentException>( () => TextureLock.ApplyScaleLock( uv, 0f, 2f, 8f, 2f ) );
			Throws<ArgumentException>( () => TextureLock.ApplyScaleLock( uv, 4f, 0f, 8f, 2f ) );
			Throws<ArgumentNullException>( () => TextureLock.ApplyScaleLock( uv, null!, topNormal, Quad(), topNormal ) );
		} );

		// 8. UvLock keeps corner UVs after a uniform resize (exact).
		Check( "UV lock keeps corner UVs after a uniform resize", () =>
		{
			var uv = new FaceUv( Vec2.Zero, new Vec2( 0.5f, 0.25f ), 0f, 16f, 0, LockFlags.UvLock );
			var resized = new Vec3[] { new( 0, 0, 0 ), new( 8, 0, 0 ), new( 8, 4, 0 ), new( 0, 4, 0 ) };
			var locked = TextureLock.ApplyUvLock( uv, Quad(), topNormal, resized, topNormal );
			NearVec2( locked.Scale, new Vec2( 0.25f, 0.125f ), 1e-6f, "uniform 2x resize halves both scales" );
			Near( TextureLock.UvLockResidual( locked, Quad(), topNormal, resized, topNormal, uv ), 0.0, 1e-4, "residual after a uniform resize" );
			var before = uv.ComputeUvs( Quad(), topNormal );
			var after = locked.ComputeUvs( resized, topNormal );
			for ( int i = 0; i < 4; i++ )
				NearVec2( after[i], before[i], 1e-4f, $"corner {i} UV preserved" );
		} );

		// 9. UvLock keeps corner UVs after a 90-degree in-plane rotation (exact, anisotropic scale).
		Check( "UV lock keeps corner UVs after a 90-degree rotation", () =>
		{
			var uv = new FaceUv( Vec2.Zero, new Vec2( 0.5f, 2f ), 0f, 16f, 0, LockFlags.UvLock );
			var rotated = new Vec3[] { new( 0, 0, 0 ), new( 0, 4, 0 ), new( -2, 4, 0 ), new( -2, 0, 0 ) };
			var locked = TextureLock.ApplyUvLock( uv, Quad(), topNormal, rotated, topNormal );
			Near( TextureLock.UvLockResidual( locked, Quad(), topNormal, rotated, topNormal, uv ), 0.0, 1e-4, "residual after a 90-degree rotation" );
			var before = uv.ComputeUvs( Quad(), topNormal );
			var after = locked.ComputeUvs( rotated, topNormal );
			for ( int i = 0; i < 4; i++ )
				NearVec2( after[i], before[i], 1e-4f, $"corner {i} UV preserved" );
		} );

		// 10. Spec case: a face with UV lock keeps its UVs after a shear — FaceUv
		// form: the centre is exact, the corners are a bounded approximation.
		Check( "UV lock keeps UVs after a shear (centre exact, corners bounded)", () =>
		{
			var uv = new FaceUv( Vec2.Zero, new Vec2( 1f, 1f ), 0f, 16f, 0, LockFlags.UvLock );
			var sheared = new Vec3[] { new( 0, 0, 0 ), new( 4, 0, 0 ), new( 5, 2, 0 ), new( 1, 2, 0 ) };
			var locked = TextureLock.ApplyUvLock( uv, Quad(), topNormal, sheared, topNormal );
			Require( float.IsFinite( locked.Scale.x ) && float.IsFinite( locked.Scale.y ) &&
				locked.Scale.x != 0f && locked.Scale.y != 0f && float.IsFinite( locked.RotationDegrees ),
				"shear fit yields a usable transform" );
			var residual = TextureLock.UvLockResidual( locked, Quad(), topNormal, sheared, topNormal, uv );
			Require( residual < 0.75f, $"shear residual is bounded (measured {residual:0.###}, bound 0.75)" );
			// The face-centre UV is exact even though the corners are approximate.
			var oldCentre = CentroidUv( uv, FaceUv.ProjectedCoords( Quad(), topNormal ) );
			var newCentre = CentroidUv( locked, FaceUv.ProjectedCoords( sheared, topNormal ) );
			NearVec2( newCentre, oldCentre, 1e-5f, "face-centre UV preserved by the shear fit" );
		} );

		// 11. Known FaceUv limit: a 45-degree in-plane rotation with an anisotropic
		// scale cannot be represented by offset + per-axis scale + rotation, so
		// the fit is a bounded approximation. This pins the measured behaviour.
		Check( "UV lock 45-degree rotation with anisotropic scale is bounded (known limit)", () =>
		{
			var uv = new FaceUv( Vec2.Zero, new Vec2( 0.5f, 2f ), 0f, 16f, 0, LockFlags.UvLock );
			var c = MathF.Cos( MathF.PI / 4f ); var s = MathF.Sin( MathF.PI / 4f );
			Vec3 rot45( Vec3 p ) => new( p.x * c - p.y * s, p.x * s + p.y * c, 0 );
			var rotated = new Vec3[] { rot45( Quad()[0] ), rot45( Quad()[1] ), rot45( Quad()[2] ), rot45( Quad()[3] ) };
			var locked = TextureLock.ApplyUvLock( uv, Quad(), topNormal, rotated, topNormal );
			var residual = TextureLock.UvLockResidual( locked, Quad(), topNormal, rotated, topNormal, uv );
			Require( residual < 1.8f, $"45-degree anisotropic residual is bounded (measured {residual:0.###}, bound 1.8)" );
			NearVec2( locked.Scale, new Vec2( 1.25f, 1.25f ), 1e-3f, "fit scale for the 45-degree case" );
			Near( locked.RotationDegrees, -45.0, 1e-3, "fit rotation for the 45-degree case" );
		} );

		// 12. UvLock rejects bad input, naming the problem.
		Check( "UV lock rejects bad input", () =>
		{
			var uv = FaceUv.Default;
			var quad = Quad();
			var three = new Vec3[] { new( 0, 0, 0 ), new( 4, 0, 0 ), new( 4, 2, 0 ) };
			Throws<ArgumentException>( () => TextureLock.ApplyUvLock( uv, quad, topNormal, three, topNormal ) );
			Throws<ArgumentException>( () => TextureLock.ApplyUvLock( uv, new Vec3[] { new( 0, 0, 0 ), new( 4, 0, 0 ) }, topNormal,
				new Vec3[] { new( 0, 0, 0 ), new( 8, 0, 0 ) }, topNormal ) );
			Throws<ArgumentException>( () => TextureLock.ApplyUvLock( uv, null!, topNormal, quad, topNormal ) );
			// Collinear corners project to a line: no plane to fit on.
			var line = new Vec3[] { new( 0, 0, 0 ), new( 2, 0, 0 ), new( 4, 0, 0 ), new( 6, 0, 0 ) };
			Throws<ArgumentException>( () => TextureLock.ApplyUvLock( uv, quad, topNormal, line, topNormal ) );
		} );

		// 13. The dispatcher reads the flags: None is untouched, ScaleLock
		// adjusts the scale, UvLock preserves the corners, both means UvLock.
		Check( "ApplyLocksToFace dispatches on the flags", () =>
		{
			var resized = new Vec3[] { new( 0, 0, 0 ), new( 8, 0, 0 ), new( 8, 4, 0 ), new( 0, 4, 0 ) };
			var plain = new FaceUv( Vec2.Zero, new Vec2( 0.5f, 0.25f ), 0f, 16f, 0, LockFlags.None );
			Require( TextureLock.ApplyLocksToFace( plain, Quad(), topNormal, resized, topNormal ).Equals( plain ),
				"no lock returns the UV unchanged" );
			var scaleOnly = plain.WithLocks( LockFlags.ScaleLock );
			var viaScale = TextureLock.ApplyLocksToFace( scaleOnly, Quad(), topNormal, resized, topNormal );
			Require( viaScale.Equals( TextureLock.ApplyScaleLock( scaleOnly, Quad(), topNormal, resized, topNormal ) ),
				"ScaleLock dispatches to ApplyScaleLock" );
			var uvOnly = plain.WithLocks( LockFlags.UvLock );
			var viaUv = TextureLock.ApplyLocksToFace( uvOnly, Quad(), topNormal, resized, topNormal );
			Require( viaUv.Equals( TextureLock.ApplyUvLock( uvOnly, Quad(), topNormal, resized, topNormal ) ),
				"UvLock dispatches to ApplyUvLock" );
			var both = plain.WithLocks( LockFlags.All );
			var viaBoth = TextureLock.ApplyLocksToFace( both, Quad(), topNormal, resized, topNormal );
			Require( viaBoth.Scale.Equals( viaUv.Scale ) && viaBoth.Offset.Equals( viaUv.Offset ) &&
				viaBoth.RotationDegrees == viaUv.RotationDegrees,
				"both flags apply the UvLock transform" );
			Require( viaBoth.Locks == LockFlags.All, "both flags keep both locks on the result" );
		} );

		// 14. Spec case, world-space model: a face with UV lock keeps its UVs
		// after a shear — exact, because MapUvMapping carries free axes.
		Check( "UV lock keeps UVs after a shear (world-space, exact)", () =>
		{
			var mapping = MapUvMapping.DefaultForNormal( new Vector3( 0f, 0f, 1f ) );
			var shear = new Matrix4x4( 1, 0.5f, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 );
			var locked = MapTransform.ApplyUvLock( mapping, shear );
			foreach ( var p in new[] { new Vector3( 2f, 3f, 32f ), new Vector3( -1f, 0f, 5f ), new Vector3( 0f, 0f, 0f ), new Vector3( 7f, -2f, 1f ) } )
				NearVec2Sys( locked.Sample( Vector3.Transform( p, shear ) ), mapping.Sample( p ), 1e-4f, $"locked UV at sheared point {p}" );
		} );

		// 15. Spec case: scale lock keeps scale after a resize (world-space).
		Check( "scale lock keeps scale after a resize (world-space)", () =>
		{
			var mapping = new MapUvMapping(
				new Vector3( 1f, 0f, 0f ), new Vector3( 0f, 1f, 0f ),
				new Vector2( 5f, -2f ), new Vector2( 0.5f, 2f ), 30f );
			var aboutOrigin = MapTransform.ApplyScaleLock( mapping, MapTransform.ScaleMatrix( 2f, Vector3.Zero ) );
			NearVec2Sys( aboutOrigin.Scale, mapping.Scale, 1e-5f, "scale about the origin keeps the stored scale" );
			NearVec2Sys( aboutOrigin.Offset, mapping.Offset, 1e-4f, "scale about the origin keeps the offset" );
			// Scale about a pivot, anchored at the pivot: the scale survives,
			// the pivot's UV survives, and the UV gradient (UV per world inch)
			// is unchanged — the texture keeps its world size.
			var pivot = new Vector3( 32f, 0f, 0f );
			var aboutPivot = MapTransform.ApplyScaleLock( mapping, MapTransform.ScaleMatrix( 2f, pivot ), pivot );
			NearVec2Sys( aboutPivot.Scale, mapping.Scale, 1e-5f, "scale about a pivot keeps the stored scale" );
			NearVec2Sys( aboutPivot.Sample( pivot ), mapping.Sample( pivot ), 1e-4f, "pivot UV preserved" );
			var oneInch = mapping.Sample( pivot + new Vector3( 1f, 0f, 0f ) ) - mapping.Sample( pivot );
			var twoInch = aboutPivot.Sample( pivot + new Vector3( 2f, 0f, 0f ) ) - aboutPivot.Sample( pivot );
			NearVec2Sys( twoInch, oneInch * 2f, 1e-4f, "UV per world inch unchanged by the scale lock" );
			// A pure translation changes no scale: scale lock is a no-op.
			var moved = MapTransform.ApplyScaleLock( mapping, MapTransform.TranslationMatrix( new Vector3( 10f, 0f, 0f ) ) );
			Require( moved.Equals( mapping ), "scale lock does nothing for a pure translation" );
			Throws<ArgumentException>( () => MapTransform.ApplyScaleLock( mapping, Matrix4x4.CreateScale( 0f, 1f, 1f ) ) );
		} );

		// 16. The wired transform path: planes move, locks apply per face.
		Check( "TransformFaces wires planes and locks", () =>
		{
			var planes = BoxPlanes();
			var mappings = new MapUvMapping[6];
			for ( int i = 0; i < 6; i++ ) mappings[i] = MapUvMapping.DefaultForNormal( planes[i].Normal );
			var locks = new LockFlags[] { LockFlags.UvLock, LockFlags.None, LockFlags.None, LockFlags.None, LockFlags.ScaleLock, LockFlags.None };
			var matrix = MapTransform.TranslationMatrix( new Vector3( 10f, 0f, 0f ) );
			var (movedPlanes, movedMappings) = MapTransform.TransformFaces( planes, mappings, locks, matrix );
			Near( movedPlanes[0].D, -42f, 1e-5, "+X face D after +10 X" );
			Near( movedPlanes[1].D, -22f, 1e-5, "-X face D after +10 X" );
			// +X face is UvLock: the UV at the moved material point is unchanged.
			NearVec2Sys( movedMappings[0].Sample( new Vector3( 42f, 3f, 5f ) ), mappings[0].Sample( new Vector3( 32f, 3f, 5f ) ), 1e-4f,
				"UvLock face keeps its UV at the moved point" );
			// +Z face is ScaleLock under a pure translation: untouched.
			Require( movedMappings[4].Equals( mappings[4] ), "ScaleLock face unchanged by a pure translation" );
			// Unlocked faces are untouched (world-anchored).
			Require( movedMappings[1].Equals( mappings[1] ), "unlocked face unchanged" );
			// Inputs untouched.
			Near( planes[0].D, -32f, 1e-6, "input planes unchanged" );
			Require( mappings[0].Equals( MapUvMapping.DefaultForNormal( planes[0].Normal ) ), "input mappings unchanged" );
			Throws<ArgumentException>( () => MapTransform.TransformFaces( planes, mappings,
				new LockFlags[] { LockFlags.None }, matrix ) );
			Throws<ArgumentNullException>( () => MapTransform.TransformFaces( null!, mappings, locks, matrix ) );
		} );

		// 17. The persistence check helper names lost locks.
		Check( "persistence check helper names lost locks", () =>
		{
			var before = new List<FaceMaterial>
			{
				new( "brick/brickwall001", FaceUv.Default.WithLocks( LockFlags.UvLock ) ),
				new( "metal/metalwall001", FaceUv.Default.WithLocks( LockFlags.ScaleLock ) ),
				new( "tools/toolsnodraw", FaceUv.Default ),
			};
			var same = new List<FaceMaterial>
			{
				new( "brick/brickwall001", FaceUv.Default.WithLocks( LockFlags.UvLock ) ),
				new( "metal/metalwall001", FaceUv.Default.WithLocks( LockFlags.ScaleLock ) ),
				new( "tools/toolsnodraw", FaceUv.Default ),
			};
			Require( TextureLock.CheckLockPersistence( before, same ).Count == 0, "identical lists have no defects" );
			var lost = new List<FaceMaterial>( same );
			lost[1] = new FaceMaterial( "metal/metalwall001", FaceUv.Default );
			var defects = TextureLock.CheckLockPersistence( before, lost );
			Require( defects.Count == 1, $"one lost lock is one defect, got {defects.Count}" );
			Require( defects[0].Contains( "face 1" ) && defects[0].Contains( "ScaleLock" ) && defects[0].Contains( "None" ),
				$"defect names the face and both lock sets: {defects[0]}" );
			Require( TextureLock.CheckLockPersistence( before, same.GetRange( 0, 2 ) ).Count == 1,
				"a count mismatch is a defect" );
			// Indexed form (clip/CSG): kept faces map to their source indices.
			var kept = new List<FaceMaterial> { same[0], same[2] };
			Require( TextureLock.CheckLockPersistence( before, kept, new[] { 0, 2 } ).Count == 0,
				"kept faces with matching locks have no defects" );
			var keptLost = new List<FaceMaterial> { same[0], new FaceMaterial( "tools/toolsnodraw", FaceUv.Default.WithLocks( LockFlags.UvLock ) ) };
			var indexed = TextureLock.CheckLockPersistence( before, keptLost, new[] { 0, 2 } );
			Require( indexed.Count == 1 && indexed[0].Contains( "kept face 1" ) && indexed[0].Contains( "source 2" ),
				$"indexed defect names kept and source indices: {string.Join( "; ", indexed )}" );
			Require( TextureLock.CheckLockPersistence( before, kept, new[] { 0, 9 } ).Count == 1,
				"an out-of-range source index is a defect" );
			Require( TextureLock.CheckLockPersistence( before, kept, new[] { 0 } ).Count == 1,
				"a short index map is a defect" );
		} );

		// 18. Fixture record: locks.json matches the in-code cases.
		CheckMissing( "fixture locks.json matches the in-code cases", () =>
		{
			var path = FindFixture()!;
			using var doc = JsonDocument.Parse( File.ReadAllText( path ) );
			var root = doc.RootElement;
			var flags = root.GetProperty( "lockFlags" );
			Require( flags.GetProperty( "none" ).GetInt32() == 0, "fixture None is 0" );
			Require( flags.GetProperty( "scaleLock" ).GetInt32() == (int)LockFlags.ScaleLock, "fixture ScaleLock" );
			Require( flags.GetProperty( "uvLock" ).GetInt32() == (int)LockFlags.UvLock, "fixture UvLock" );
			Require( flags.GetProperty( "all" ).GetInt32() == (int)LockFlags.All, "fixture All" );
			var face = root.GetProperty( "face" );
			var uv = new FaceUv(
				ReadVec2( face.GetProperty( "uv" ).GetProperty( "offset" ) ),
				ReadVec2( face.GetProperty( "uv" ).GetProperty( "scale" ) ),
				face.GetProperty( "uv" ).GetProperty( "rotation" ).GetSingle(),
				face.GetProperty( "uv" ).GetProperty( "lightmapScale" ).GetSingle(),
				face.GetProperty( "uv" ).GetProperty( "smoothingGroup" ).GetInt32(),
				(LockFlags)face.GetProperty( "uv" ).GetProperty( "locks" ).GetInt32() );
			var scaleLock = root.GetProperty( "scaleLock" );
			var locked = TextureLock.ApplyScaleLock( uv,
				scaleLock.GetProperty( "oldExtents" )[0].GetSingle(), scaleLock.GetProperty( "oldExtents" )[1].GetSingle(),
				scaleLock.GetProperty( "newExtents" )[0].GetSingle(), scaleLock.GetProperty( "newExtents" )[1].GetSingle() );
			NearVec2( locked.Scale, ReadVec2( scaleLock.GetProperty( "expectedScale" ) ), 1e-6f, "fixture expected scale" );
			var shear = root.GetProperty( "uvLockShear" );
			var sheared = new List<Vec3>();
			foreach ( var v in shear.GetProperty( "newVertices" ).EnumerateArray() ) sheared.Add( ReadVec3( v ) );
			var shearLocked = TextureLock.ApplyUvLock( uv.WithLocks( LockFlags.UvLock ),
				Quad(), topNormal, sheared, topNormal );
			var residual = TextureLock.UvLockResidual( shearLocked, Quad(), topNormal, sheared, topNormal, uv.WithLocks( LockFlags.UvLock ) );
			Require( residual < shear.GetProperty( "maxResidual" ).GetSingle(),
				$"fixture shear residual {residual:0.###} is under the recorded bound" );
		}, () => FindFixture() is null ? "fixture locks.json not found under the repo root" : null );

		// 19. Clip wiring contract (helper-level): a clip of a locked brush
		// preserves the lock on the kept faces. This tests the exact helper +
		// index-map contract MapClip.BuildSide's follow-up will call — NOT a
		// MapClip.Clip run, which is DEFERRED until the clip wiring lands
		// (see the file header). A clip reuses the kept faces' FaceMaterial
		// objects, so their locks survive by construction; the cap face is new
		// and takes the document default locks.
		Check( "clip wiring contract: kept faces keep locks (helper-level)", () =>
		{
			LockDefaults.Default = LockFlags.None;
			var source = new List<FaceMaterial>
			{
				new( "brick/brickwall001", FaceUv.Default.WithLocks( LockFlags.UvLock ) ),
				new( "metal/metalwall001", FaceUv.Default.WithLocks( LockFlags.ScaleLock ) ),
				new( "dev/brick", FaceUv.Default ),
				new( "brick/brickwall001", FaceUv.Default.WithLocks( LockFlags.UvLock ) ),
			};
			// Simulated clip: faces 0 and 2 survive (same objects, as
			// BuildSide reuses them); faces 1 and 3 are cut away.
			var kept = new List<FaceMaterial> { source[0], source[2] };
			var keptSourceIndices = new[] { 0, 2 };
			Require( TextureLock.CheckLockPersistence( source, kept, keptSourceIndices ).Count == 0,
				"kept faces keep their source locks" );
			var cap = new FaceMaterial( "tools/toolsnodraw", FaceUv.Default.WithLocks( LockDefaults.Default ) );
			Require( cap.Uv.Locks == LockFlags.None, "the new cap face takes the document default locks" );
			Require( cap.IsNoDraw, "the cap face is nodraw" );
		} );

		static float UvRange( Vec2[] uvs, int axis )
		{
			var min = float.MaxValue; var max = float.MinValue;
			foreach ( var u in uvs )
			{
				var v = axis == 0 ? u.x : u.y;
				if ( v < min ) min = v;
				if ( v > max ) max = v;
			}
			return max - min;
		}

		static Vec2 CentroidUv( FaceUv uv, Vec2[] projected )
		{
			var sum = Vec2.Zero;
			foreach ( var p in projected ) sum += uv.Transform( p );
			return sum / projected.Length;
		}

		// The -32..32 box, same face order as MapTransformTests.
		static Plane[] BoxPlanes() => new Plane[]
		{
			new( new Vector3( 1, 0, 0 ), -32f ),
			new( new Vector3( -1, 0, 0 ), -32f ),
			new( new Vector3( 0, 1, 0 ), -32f ),
			new( new Vector3( 0, -1, 0 ), -32f ),
			new( new Vector3( 0, 0, 1 ), -32f ),
			new( new Vector3( 0, 0, -1 ), -32f ),
		};

		static string? FindFixture()
		{
			var dir = new DirectoryInfo( AppContext.BaseDirectory );
			for ( int i = 0; i < 8 && dir != null; i++ )
			{
				var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-texture-scale-uv-locks", "locks.json" );
				if ( File.Exists( candidate ) ) return candidate;
				dir = dir.Parent;
			}
			return null;
		}

		Vec2 ReadVec2( JsonElement element )
		{
			var values = new List<float>();
			foreach ( var item in element.EnumerateArray() ) values.Add( item.GetSingle() );
			Require( values.Count == 2, "expected a 2-component vector in the fixture" );
			return new Vec2( values[0], values[1] );
		}

		Vec3 ReadVec3( JsonElement element )
		{
			var values = new List<float>();
			foreach ( var item in element.EnumerateArray() ) values.Add( item.GetSingle() );
			Require( values.Count == 3, "expected a 3-component vector in the fixture" );
			return new Vec3( values[0], values[1], values[2] );
		}
	}
}
