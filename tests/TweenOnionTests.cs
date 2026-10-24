using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

internal static class TweenOnionTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "tween-onion", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, float eps = 1e-4f )
		{
			if ( MathF.Abs( actual - expected ) > eps ) throw new Exception( $"expected {expected}, got {actual}" );
		}
		void Throws<T>( Action action ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"Expected {typeof( T ).Name}, got {ex.GetType().Name}: {ex.Message}" ); }
			throw new Exception( $"Expected {typeof( T ).Name}, got silent success" );
		}

		TweenTrack Linear( int f0, float v0, int f1, float v1 )
		{
			var t = new TweenTrack();
			t.Add( new TweenKey( f0, v0 ) { Out = TweenEase.Linear } );
			t.Add( new TweenKey( f1, v1 ) );
			return t;
		}

		// ---- scalar tracks ----
		Check( "linear track holds outside and lerps inside", () =>
		{
			var t = Linear( 0, 0f, 10, 10f );
			Near( t.Evaluate( -5 ), 0f );
			Near( t.Evaluate( 0 ), 0f );
			Near( t.Evaluate( 5 ), 5f );
			Near( t.Evaluate( 10 ), 10f );
			Near( t.Evaluate( 99 ), 10f );
		} );

		Check( "same-frame key replaces instead of stacking", () =>
		{
			var t = Linear( 0, 0f, 10, 10f );
			t.Add( new TweenKey( 0, 7f ) { Out = TweenEase.Linear } );
			Near( t.Evaluate( 0 ), 7f );
			Require( t.Keys.Count == 2, "replaced key added a third key" );
		} );

		Check( "easein and smooth reuse rig ease numbers", () =>
		{
			var easeIn = new TweenTrack();
			easeIn.Add( new TweenKey( 0, 0f ) { Out = TweenEase.EaseIn } );
			easeIn.Add( new TweenKey( 10, 10f ) );
			Near( easeIn.Evaluate( 5 ), 2.5f ); // RigKey EaseIn = t*t: 0.25 * 10
			var smooth = new TweenTrack();
			smooth.Add( new TweenKey( 0, 0f ) { Out = TweenEase.Smooth } );
			smooth.Add( new TweenKey( 10, 10f ) );
			Near( smooth.Evaluate( 2.5f ), 1.25f ); // Smooth at 0.25 = 0.125 * 10
		} );

		Check( "stepped and constant hold the left key", () =>
		{
			foreach ( var ease in new[] { TweenEase.Stepped, TweenEase.Constant } )
			{
				var t = new TweenTrack();
				t.Add( new TweenKey( 0, 3f ) { Out = ease } );
				t.Add( new TweenKey( 10, 9f ) );
				Near( t.Evaluate( 9.9f ), 3f );
				Near( t.Evaluate( 10 ), 9f );
			}
		} );

		Check( "bezier symmetric handles give symmetric ease", () =>
		{
			var t = new TweenTrack();
			t.Add( new TweenKey( 0, 0f ) { Out = TweenEase.Bezier, OutX = 0.42f, OutY = 0f } );
			t.Add( new TweenKey( 10, 10f ) { InX = 0.58f, InY = 1f } );
			Near( t.Evaluate( 5 ), 5f, 1e-3f );
			var early = t.Evaluate( 2.5f );
			Require( early < 2.5f, $"bezier ease-in-out should lag at t=0.25, got {early}" );
		} );

		Check( "tcb through collinear keys is linear on interior segments", () =>
		{
			var t = new TweenTrack();
			for ( var i = 0; i < 4; i++ )
				t.Add( new TweenKey( i * 10, i * 10f ) { Out = TweenEase.Tcb } );
			Near( t.Evaluate( 15 ), 15f );
			Near( t.Evaluate( 25 ), 25f );
		} );

		Check( "clamped tcb never leaves the segment, unclamped can", () =>
		{
			TweenTrack Track( TweenEase ease )
			{
				var t = new TweenTrack();
				t.Add( new TweenKey( 0, 0f ) { Out = ease } );
				t.Add( new TweenKey( 10, 1f ) { Out = ease } );
				t.Add( new TweenKey( 20, 100f ) );
				return t;
			}
			var wild = Track( TweenEase.Tcb );
			Require( wild.Evaluate( 5 ) < 0f, $"unclamped TCB should overshoot below 0 here, got {wild.Evaluate( 5 )}" );
			var calm = Track( TweenEase.Clamped );
			for ( var f = 0f; f <= 10f; f += 0.5f )
			{
				var v = calm.Evaluate( f );
				Require( v >= -1e-4f && v <= 1f + 1e-4f, $"clamped TCB left [0,1] at frame {f}: {v}" );
			}
		} );

		Check( "bake samples every integer frame", () =>
		{
			var baked = Linear( 0, 0f, 10, 10f ).Bake();
			Require( baked.Count == 11, $"bake gave {baked.Count} frames, expected 11" );
			Near( baked[5], 5f );
			Near( baked[10], 10f );
		} );

		// ---- transform tracks ----
		Check( "transform track evaluates all channels", () =>
		{
			var t = new TweenTransformTrack();
			t.Key( 0, new TweenTransform( 0f, 0f, 0f, 1f, 1f, 1f ) );
			t.Key( 10, new TweenTransform( 10f, 20f, 90f, 2f, 0.5f, 0f ) );
			var mid = t.Evaluate( 5 );
			Near( mid.X, 5f ); Near( mid.Y, 10f ); Near( mid.RotationDeg, 45f );
			Near( mid.ScaleX, 1.5f ); Near( mid.ScaleY, 0.75f ); Near( mid.Opacity, 0.5f );
		} );

		Check( "rotation takes the shortest arc", () =>
		{
			var t = new TweenTransformTrack();
			t.Key( 0, new TweenTransform( 0f, 0f, 350f, 1f, 1f, 1f ) );
			t.Key( 10, new TweenTransform( 0f, 0f, 10f, 1f, 1f, 1f ) );
			Near( t.Evaluate( 5 ).RotationDeg, 360f ); // 350 -> 360/0 -> 10, not back through 180
		} );

		Check( "opacity clamps to 0..1 on evaluation", () =>
		{
			var t = new TweenTransformTrack();
			t.Key( 0, new TweenTransform( 0f, 0f, 0f, 1f, 1f, 2f ) );
			Near( t.Evaluate( 0 ).Opacity, 1f );
		} );

		Check( "empty transform track is identity", () =>
		{
			var mid = new TweenTransformTrack().Evaluate( 42 );
			Near( mid.X, 0f ); Near( mid.ScaleX, 1f ); Near( mid.Opacity, 1f );
		} );

		// ---- shape in-betweening ----
		Check( "same-count stroke interpolates point by point", () =>
		{
			var a = new[] { new Vec2( 0, 0 ), new Vec2( 10, 0 ) };
			var b = new[] { new Vec2( 0, 10 ), new Vec2( 10, 10 ) };
			var mid = TweenShape.Interpolate( a, b, 0.5f );
			Near( mid[0].y, 5f ); Near( mid[1].x, 10f ); Near( mid[1].y, 5f );
			var widths = TweenShape.InterpolateWidths( new[] { 1f, 2f }, new[] { 3f, 4f }, 0.5f );
			Near( widths[0], 2f ); Near( widths[1], 3f );
		} );

		Check( "different-count strokes are rejected, not resampled", () =>
		{
			var a = new[] { new Vec2( 0, 0 ), new Vec2( 10, 0 ) };
			var b = new[] { new Vec2( 0, 10 ) };
			Throws<ArgumentException>( () => TweenShape.Interpolate( a, b, 0.5f ) );
			Throws<ArgumentException>( () => TweenShape.InterpolateWidths( new[] { 1f }, new[] { 1f, 2f }, 0.5f ) );
		} );

		// ---- cutout puppet proof: flat bones + Bone track + tween agree ----
		Check( "cutout puppet: flat skeleton, bone track and tween agree", () =>
		{
			var rig = new Skeleton();
			rig.AddBone( "root", -1, Xform.Translate( new Vec3( 0, 0, 0 ) ) );
			rig.AddBone( "arm", 0, Xform.Translate( new Vec3( 5, 0, 0 ) ) );
			Require( rig.Count == 2, "puppet rig should have exactly root + arm" );
			foreach ( var bone in rig.Bones )
				Near( bone.Local.Origin.z, 0f ); // flat: no depth in the bind pose

			var boneTrack = new RigTrack { Name = "arm", Kind = RigTrackKind.Bone };
			boneTrack.Keys.Add( new RigKey { Frame = 0, Ease = RigEase.Linear, Local = new RigPose( new Vec3( 5, 0, 0 ), Quat.Identity, Vec3.One ) } );
			boneTrack.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( 5, 10, 0 ), Quat.Identity, Vec3.One ) } );
			var boneMid = boneTrack.Evaluate( 15 );

			var tween = new TweenTransformTrack();
			tween.Key( 0, new TweenTransform( 5f, 0f, 0f, 1f, 1f, 1f ) );
			tween.Key( 30, new TweenTransform( 5f, 10f, 0f, 1f, 1f, 1f ) );
			var tweenMid = tween.Evaluate( 15 );
			Near( boneMid.Position.x, tweenMid.X );
			Near( boneMid.Position.y, tweenMid.Y );
			// No new engine was built to prove this: the existing Bone track and the
			// new 2D tween drive the same flat part to the same mid pose.
		} );

		// ---- onion skin ----
		Check( "onion keys mode picks nearest keys with linear falloff", () =>
		{
			var s = new OnionSettings { Mode = OnionMode.Keys, BeforeCount = 2, AfterCount = 1, MasterOpacity = 0.5f };
			var frames = OnionSkin.Compute( s, 20, 31, keyFrames: new[] { 0, 10, 20, 30 } );
			Require( frames.Count == 3, $"expected 3 ghosts, got {frames.Count}" );
			Require( frames[0].Distance == 20, "farthest ghost must draw first" );
			var near = frames.First( f => f.Frame == 10 );
			var far = frames.First( f => f.Frame == 0 );
			var after = frames.First( f => f.Frame == 30 );
			Near( near.Opacity, 0.5f );   // distance 10, factor 1
			Near( far.Opacity, 0.25f );   // distance 20, factor 1 - 1/2
			Near( after.Opacity, 0.5f );
			Require( near.IsBefore && !after.IsBefore, "before/after sides flipped" );
			Require( near.Tint.x == 1f && near.Tint.y == 0f, "before tint must default red" );
			Require( after.Tint.y == 1f && after.Tint.x == 0f, "after tint must default green" );
		} );

		Check( "onion frames mode ghosts every nearby frame", () =>
		{
			var s = new OnionSettings { Mode = OnionMode.Frames, BeforeCount = 2, AfterCount = 2, MasterOpacity = 0.5f };
			var frames = OnionSkin.Compute( s, 5, 10 );
			Require( frames.Select( f => f.Frame ).OrderBy( f => f ).SequenceEqual( new[] { 3, 4, 6, 7 } ),
				"frames mode should ghost 3,4,6,7 around frame 5" );
		} );

		Check( "onion selected mode ghosts only picked frames", () =>
		{
			var s = new OnionSettings { Mode = OnionMode.Selected, BeforeCount = 5, AfterCount = 5 };
			var frames = OnionSkin.Compute( s, 5, 10, selectedFrames: new[] { 2, 8 } );
			Require( frames.Count == 2 && frames.Any( f => f.Frame == 2 ) && frames.Any( f => f.Frame == 8 ),
				"selected mode must ghost exactly the picked frames" );
		} );

		Check( "per-offset opacity replaces falloff for listed distances", () =>
		{
			var s = new OnionSettings { Mode = OnionMode.Frames, BeforeCount = 3, AfterCount = 0, MasterOpacity = 1f, PerOffsetOpacity = new[] { 0.8f, 0.4f } };
			var frames = OnionSkin.Compute( s, 5, 10 );
			Near( frames.First( f => f.Distance == 1 ).Opacity, 0.8f );
			Near( frames.First( f => f.Distance == 2 ).Opacity, 0.4f );
			Near( frames.First( f => f.Distance == 3 ).Opacity, 1f - 2f / 3f, 1e-4f ); // falloff resumes past the list
		} );

		Check( "onion colours swap and tint mixes toward silhouette", () =>
		{
			var s = new OnionSettings();
			s.SwapColors();
			Require( s.BeforeColor.y == 1f && s.AfterColor.x == 1f, "swap must flip red/green" );
			var ghost = new OnionFrame( 4, true, 1, 0.5f, new Vec4( 1f, 0f, 0f, 1f ), 1f );
			var sil = ghost.Silhouette();
			Near( sil.x, 1f ); Near( sil.w, 0.5f );
			var mixed = ghost.Mix( new Vec4( 0f, 0f, 1f, 1f ) );
			Near( mixed.x, 1f ); Near( mixed.z, 0f ); Near( mixed.w, 0.5f );
		} );

		Check( "loop show-start-frame adds frame zero without eating a slot", () =>
		{
			var s = new OnionSettings { Mode = OnionMode.Keys, BeforeCount = 1, AfterCount = 1, ShowStartFrame = true };
			var frames = OnionSkin.Compute( s, 25, 30, keyFrames: new[] { 0, 10 }, loop: true );
			var start = frames.FirstOrDefault( f => f.Frame == 0 );
			Require( start is not null && start.IsStartFrame, "loop must surface frame 0 as the start ghost" );
			Require( frames.Any( f => f.Frame == 10 ), "start frame must not displace the real before ghost" );
			var noLoop = OnionSkin.Compute( s, 25, 30, keyFrames: new[] { 0, 10 }, loop: false );
			Require( !noLoop.Any( f => f.IsStartFrame ), "start ghost must not appear when not looping" );
		} );

		Check( "disabled onion and zero counts ghost nothing", () =>
		{
			var off = new OnionSettings { Enabled = false };
			Require( OnionSkin.Compute( off, 5, 10, keyFrames: new[] { 0, 10 } ).Count == 0, "disabled onion must be empty" );
			var zero = new OnionSettings { Mode = OnionMode.Frames, BeforeCount = 0, AfterCount = 0 };
			Require( OnionSkin.Compute( zero, 5, 10 ).Count == 0, "zero counts must be empty" );
		} );

		Check( "per-layer onion falls back to default and honours disabled layers", () =>
		{
			var set = new OnionSkinSet();
			set.Default = new OnionSettings { Mode = OnionMode.Frames, BeforeCount = 1, AfterCount = 1 };
			set.SetLayer( "bg", new OnionSettings { Mode = OnionMode.Frames, BeforeCount = 0, AfterCount = 2 } );
			set.SetLayer( "fx", new OnionSettings { Enabled = false } );
			Require( set.ComputeForLayer( "bg", 5, 10 ).Count == 2, "bg layer should show 2 after ghosts only" );
			Require( set.ComputeForLayer( "fx", 5, 10 ).Count == 0, "disabled layer must ghost nothing" );
			Require( set.ComputeForLayer( "unknown", 5, 10 ).Count == 2, "unknown layer must fall back to default" );
		} );

		Check( "onion validation fails loudly on bad numbers", () =>
		{
			Throws<ArgumentOutOfRangeException>( () => OnionSkin.Compute( new OnionSettings { BeforeCount = -1 }, 5, 10 ) );
			Throws<ArgumentOutOfRangeException>( () => OnionSkin.Compute( new OnionSettings { MasterOpacity = 2f }, 5, 10 ) );
			Throws<ArgumentOutOfRangeException>( () => OnionSkin.Compute( new OnionSettings { TintStrength = -0.5f }, 5, 10 ) );
		} );
	}
}
