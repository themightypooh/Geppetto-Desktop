using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

internal static class Anim2DTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "anim2d", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Near( float actual, float expected, string what )
		{
			Require( MathF.Abs( actual - expected ) < 1e-4f, $"{what}: got {actual}, want {expected}" );
		}

		Anim2DClip Clip()
		{
			var clip = new Anim2DClip { Name = "walk", FrameRate = 30, FrameCount = 31 };
			var draw = new Anim2DDrawingTrack { Element = "hero" };
			draw.Exposures.Add( new Anim2DExposure { Frame = 0, DrawingId = "hero-a", Hold = 2 } );
			draw.Exposures.Add( new Anim2DExposure { Frame = 2, DrawingId = "hero-b", Hold = 0 } );
			clip.DrawingTracks.Add( draw );
			var props = new Anim2DPropertyTrack { Element = "hero" };
			props.Keys.Add( new Anim2DPropertyKey { Frame = 0, X = 0, Y = 0, RotationDegrees = 0, ScaleX = 1, ScaleY = 1, Opacity = 1, DrawOrder = 1, SpriteFrame = 0, Ease = RigEase.Linear } );
			props.Keys.Add( new Anim2DPropertyKey { Frame = 10, X = 10, Y = 20, RotationDegrees = 90, ScaleX = 2, ScaleY = 0.5f, Opacity = 0.5f, DrawOrder = 2, SpriteFrame = 3, Ease = RigEase.Linear } );
			clip.PropertyTracks.Add( props );
			var events = new Anim2DEventTrack { Element = "hero" };
			events.Events.Add( new Anim2DEvent { Frame = 5, Kind = Anim2DEventKind.Particle, Target = "dust", Name = "poof", Value = 1f } );
			events.Events.Add( new Anim2DEvent { Frame = 5, Kind = Anim2DEventKind.Sound, Name = "step" } );
			clip.EventTracks.Add( events );
			return clip;
		}

		Check( "exposure holds a drawing for its hold, then the next exposure holds on", () =>
		{
			var clip = Clip();
			Require( clip.Sample( 0 ).Elements[0].DrawingId == "hero-a" );
			Require( clip.Sample( 1 ).Elements[0].DrawingId == "hero-a" );
			Require( clip.Sample( 2 ).Elements[0].DrawingId == "hero-b" );
			Require( clip.Sample( 30 ).Elements[0].DrawingId == "hero-b", "Hold 0 must hold until the next exposure" );
		} );

		Check( "no drawing before the first exposure", () =>
		{
			var clip = new Anim2DClip();
			var draw = new Anim2DDrawingTrack { Element = "ghost" };
			draw.Exposures.Add( new Anim2DExposure { Frame = 4, DrawingId = "g1", Hold = 2 } );
			clip.DrawingTracks.Add( draw );
			Require( clip.Sample( 3 ).Elements[0].DrawingId is null );
			Require( clip.Sample( 4 ).Elements[0].DrawingId == "g1" );
			Require( clip.Sample( 6 ).Elements[0].DrawingId is null, "an expired hold leaves no drawing, not the old one" );
		} );

		Check( "linear property keys interpolate at the midpoint and hold past the last key", () =>
		{
			var state = Clip().Sample( 5 ).Elements[0];
			Near( state.X, 5f, "x" );
			Near( state.Y, 10f, "y" );
			Near( state.RotationDegrees, 45f, "rotation" );
			Near( state.ScaleX, 1.5f, "scaleX" );
			Near( state.ScaleY, 0.75f, "scaleY" );
			Near( state.Opacity, 0.75f, "opacity" );
			var held = Clip().Sample( 30 ).Elements[0];
			Near( held.X, 10f, "held x" );
			Near( held.Opacity, 0.5f, "held opacity" );
		} );

		Check( "smooth ease is not linear off-centre", () =>
		{
			var clip = new Anim2DClip();
			var props = new Anim2DPropertyTrack { Element = "e" };
			props.Keys.Add( new Anim2DPropertyKey { Frame = 0, X = 0, Ease = RigEase.Smooth } );
			props.Keys.Add( new Anim2DPropertyKey { Frame = 10, X = 10, Ease = RigEase.Smooth } );
			clip.PropertyTracks.Add( props );
			Near( clip.Sample( 2.5f ).Elements[0].X, 1.25f, "smooth at t=0.25 is 0.125 of the span" );
			Near( clip.Sample( 5 ).Elements[0].X, 5f, "smooth at t=0.5 is still the midpoint" );
		} );

		Check( "stepped property keys hold the outgoing value", () =>
		{
			var clip = new Anim2DClip();
			var props = new Anim2DPropertyTrack { Element = "e" };
			props.Keys.Add( new Anim2DPropertyKey { Frame = 0, X = 0, Ease = RigEase.Stepped } );
			props.Keys.Add( new Anim2DPropertyKey { Frame = 10, X = 10, Ease = RigEase.Stepped } );
			clip.PropertyTracks.Add( props );
			Near( clip.Sample( 9 ).Elements[0].X, 0f, "stepped x" );
			Near( clip.Sample( 10 ).Elements[0].X, 10f, "stepped x at the key" );
		} );

		Check( "monotone segments never leave the segment endpoints", () =>
		{
			var clip = new Anim2DClip();
			var props = new Anim2DPropertyTrack { Element = "e" };
			props.Keys.Add( new Anim2DPropertyKey { Frame = 0, X = 0, Ease = RigEase.Monotone } );
			props.Keys.Add( new Anim2DPropertyKey { Frame = 10, X = 10, Ease = RigEase.Monotone } );
			clip.PropertyTracks.Add( props );
			for ( var f = 0; f <= 10; f++ )
			{
				var x = clip.Sample( f ).Elements[0].X;
				Require( x >= -1e-4f && x <= 10f + 1e-4f, $"monotone escaped the segment at frame {f}: {x}" );
			}
		} );

		Check( "draw order and sprite frame are discrete, never interpolated", () =>
		{
			var state = Clip().Sample( 5 ).Elements[0];
			Require( state.DrawOrder == 1, $"draw order interpolated to {state.DrawOrder}" );
			Require( state.SpriteFrame == 0, $"sprite frame interpolated to {state.SpriteFrame}" );
			var later = Clip().Sample( 10 ).Elements[0];
			Require( later.DrawOrder == 2 && later.SpriteFrame == 3 );
		} );

		Check( "events fire on their exact frame only, and a particle event is just a cue record", () =>
		{
			var at5 = Clip().Sample( 5 ).Elements[0].Events;
			Require( at5.Count == 2, $"frame 5 fired {at5.Count} events" );
			var poof = at5.First( e => e.Kind == Anim2DEventKind.Particle );
			Require( poof.Name == "poof" && poof.Target == "dust" );
			Require( Clip().Sample( 4 ).Elements[0].Events.Count == 0 );
			Require( Clip().Sample( 6 ).Elements[0].Events.Count == 0 );
		} );

		Check( "sampled elements sort by draw order, then name", () =>
		{
			var clip = new Anim2DClip();
			foreach ( var (element, order) in new[] { ("b", 2), ("a", 2), ("c", 0) } )
			{
				var props = new Anim2DPropertyTrack { Element = element };
				props.Keys.Add( new Anim2DPropertyKey { Frame = 0, DrawOrder = order } );
				clip.PropertyTracks.Add( props );
			}
			var names = clip.Sample( 0 ).Elements.Select( s => s.Element ).ToArray();
			Require( names.SequenceEqual( new[] { "c", "a", "b" } ), string.Join( ",", names ) );
		} );

		Check( "empty clip samples to an empty frame without throwing", () =>
		{
			var frame = new Anim2DClip().Sample( 0 );
			Require( frame.Elements.Count == 0 );
			Require( new Anim2DClip().LastFrame == -1 );
		} );

		Check( "opacity clamps to 0..1 on sample", () =>
		{
			var clip = new Anim2DClip();
			var props = new Anim2DPropertyTrack { Element = "e" };
			props.Keys.Add( new Anim2DPropertyKey { Frame = 0, Opacity = 2f } );
			clip.PropertyTracks.Add( props );
			Near( clip.Sample( 0 ).Elements[0].Opacity, 1f, "clamped opacity" );
		} );

		Check( "converter maps x/y, z-rotation and scale onto a Bone track and skips unmapped elements", () =>
		{
			var map = new Dictionary<string, string> { ["hero"] = "hero_bone" };
			var result = Anim2DConverter.ToRigAnim( Clip(), map );
			Require( result.Clip.Tracks.Count == 1, $"converter made {result.Clip.Tracks.Count} tracks" );
			var track = result.Clip.Tracks[0];
			Require( track.Name == "hero_bone" && track.Kind == RigTrackKind.Bone );
			Require( track.Keys.Count == 2 && track.Keys[1].Frame == 10 );
			var pose = track.Keys[1].Local;
			Near( pose.Position.x, 10f, "bone x" );
			Near( pose.Position.y, 20f, "bone y" );
			Near( pose.Position.z, 0f, "bone z is the 2D plane" );
			Near( pose.Scale.x, 2f, "bone scaleX" );
			Near( pose.Scale.y, 0.5f, "bone scaleY" );
			Near( pose.Scale.z, 1f, "bone scaleZ" );
			Require( track.Keys[0].Ease == RigEase.Linear, "ease preserved onto the bone track" );
			// A 90-degree Z rotation has |x|,|y| near 0 and |z|,|w| near sqrt(0.5).
			Near( MathF.Abs( pose.Rotation.z ), 0.70710678f, "bone rotation z" );
			Near( MathF.Abs( pose.Rotation.w ), 0.70710678f, "bone rotation w" );
			Require( result.Clip.FrameRate == 30 && result.Clip.FrameCount == 31 );

			var clip2 = Clip();
			clip2.PropertyTracks.Add( new Anim2DPropertyTrack { Element = "unmapped",
				Keys = { new Anim2DPropertyKey { Frame = 0 } } } );
			var result2 = Anim2DConverter.ToRigAnim( clip2, map );
			Require( result2.SkippedElements.SequenceEqual( new[] { "unmapped" } ), string.Join( ",", result2.SkippedElements ) );
			Require( result2.Clip.Tracks.Count == 1, "unmapped elements must not invent bone tracks" );
		} );

		Check( "converted clip evaluates through RigTrack like the 2D clip does", () =>
		{
			var map = new Dictionary<string, string> { ["hero"] = "hero_bone" };
			var rig = Anim2DConverter.ToRigAnim( Clip(), map ).Clip;
			var pose = rig.Tracks[0].Evaluate( 5 );
			Near( pose.Position.x, 5f, "converted x at midpoint" );
			Near( pose.Position.y, 10f, "converted y at midpoint" );
		} );

		Check( "json round-trip preserves exposures, properties and events", () =>
		{
			var clip = Clip();
			var back = Anim2DClip.ParseJson( clip.WriteJson() );
			Require( back.Name == "walk" && back.FrameRate == 30 && back.FrameCount == 31 );
			Require( back.Sample( 1 ).Elements[0].DrawingId == "hero-a" );
			Require( back.Sample( 2 ).Elements[0].DrawingId == "hero-b" );
			Near( back.Sample( 5 ).Elements[0].X, 5f, "round-trip x" );
			Near( back.Sample( 5 ).Elements[0].RotationDegrees, 45f, "round-trip rotation" );
			Require( back.Sample( 5 ).Elements[0].Events.Count == 2, "round-trip events" );
			Require( back.Sample( 5 ).Elements[0].Events.Any( e => e.Kind == Anim2DEventKind.Particle && e.Name == "poof" ) );
		} );

		Check( "json uses the riganim shape: x,y strings and ease names", () =>
		{
			var json = Clip().WriteJson();
			Require( json.Contains( "\"Position\": \"0,0\"" ), "position is not an x,y string" );
			Require( json.Contains( "\"Interpolation\": \"Linear\"" ), "ease is not by name" );
			Require( json.Contains( "\"AnimationSpeed\": 30" ), "frame rate field missing" );
		} );

		Check( "unsorted keys still sample deterministically", () =>
		{
			var clip = new Anim2DClip();
			var props = new Anim2DPropertyTrack { Element = "e" };
			props.Keys.Add( new Anim2DPropertyKey { Frame = 10, X = 10, Ease = RigEase.Linear } );
			props.Keys.Add( new Anim2DPropertyKey { Frame = 0, X = 0, Ease = RigEase.Linear } );
			clip.PropertyTracks.Add( props );
			Near( clip.Sample( 5 ).Elements[0].X, 5f, "unsorted midpoint" );
		} );
	}
}
