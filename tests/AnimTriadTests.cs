using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Effigy;
using Marionette.Desktop;

internal static class AnimTriadTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "anim-triad", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws( Action action )
		{
			try { action(); }
			catch ( Exception ) { return; }
			throw new Exception( "expected an error, got silent success" );
		}
		void Near( float actual, float expected, float tolerance, string message )
		{
			Require( MathF.Abs( actual - expected ) <= tolerance, $"{message}: got {actual}, want {expected} ± {tolerance}" );
		}

		RigPose At( float y ) => new( new Vec3( 0, y, 0 ), Quat.Identity, Vec3.One );

		// Base clip: root rises 0 -> 10 -> 20 over frames 0/30/60, linear; arm stays flat.
		RigAnimFile MakeClip()
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 60 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( 0 ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( 10 ), Ease = RigEase.Linear },
				  new RigKey { Frame = 60, Local = At( 20 ), Ease = RigEase.Linear } } } );
			clip.Tracks.Add( new RigTrack { Name = "arm", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( 0 ), Ease = RigEase.Linear },
				  new RigKey { Frame = 60, Local = At( 0 ), Ease = RigEase.Linear } } } );
			return clip;
		}

		RigAnimFile FlatClip( float y )
		{
			var clip = new RigAnimFile { FrameRate = 30, FrameCount = 60 };
			clip.Tracks.Add( new RigTrack { Name = "root", Kind = RigTrackKind.Bone, Keys =
				{ new RigKey { Frame = 0, Local = At( y ), Ease = RigEase.Linear },
				  new RigKey { Frame = 30, Local = At( y ), Ease = RigEase.Linear },
				  new RigKey { Frame = 60, Local = At( y ), Ease = RigEase.Linear } } } );
			return clip;
		}

		float QuatDot( Quat a, Quat b ) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

		// ---- Graph: curve evaluation ----

		Check( "bezier ease-in-out is a smoothstep: endpoints exact, midpoint halfway", () =>
		{
			var curve = AnimCurve.EaseInOut( 0, 10, 0f, 1f );
			Near( curve.Evaluate( 0 ), 0f, 1e-6f, "start" );
			Near( curve.Evaluate( 10 ), 1f, 1e-6f, "end" );
			Near( curve.Evaluate( 5 ), 0.5f, 1e-4f, "midpoint" );
		} );

		Check( "ease-in starts slow and ease-out starts fast", () =>
		{
			var easeIn = AnimCurve.EaseIn( 0, 10, 0f, 1f );
			Near( easeIn.Evaluate( 2.5f ), 0.0625f, 1e-3f, "ease-in at 2.5" );
			Near( easeIn.Evaluate( 5 ), 0.25f, 1e-3f, "ease-in at 5" );

			var easeOut = AnimCurve.EaseOut( 0, 10, 0f, 1f );
			Near( easeOut.Evaluate( 2.5f ), 0.4375f, 1e-3f, "ease-out at 2.5" );
			Near( easeOut.Evaluate( 5 ), 0.75f, 1e-3f, "ease-out at 5" );
		} );

		Check( "linear curve interpolates exactly and constant/stepped curves hold", () =>
		{
			var linear = AnimCurve.Linear( 0, 10, 0f, 10f );
			Near( linear.Evaluate( 5 ), 5f, 1e-6f, "linear midpoint" );
			Near( linear.Evaluate( 2.5f ), 2.5f, 1e-6f, "linear quarter" );

			foreach ( var tangent in new[] { AnimTangent.Constant, AnimTangent.Stepped } )
			{
				var held = new AnimCurve { Keys =
					{ new AnimCurveKey { Frame = 0, Value = 0f, Tangent = tangent },
					  new AnimCurveKey { Frame = 10, Value = 10f, Tangent = tangent } } };
				Near( held.Evaluate( 5 ), 0f, 1e-6f, $"{tangent} must hold the left value" );
				Near( held.Evaluate( 10 ), 10f, 1e-6f, $"{tangent} must reach the right value at its key" );
			}
		} );

		Check( "weighted handles change the curve shape", () =>
		{
			// Ease-in shape (slopes 0 -> 0.2) with short vs long handles: at frame 7.5
			// the short-handled curve reads 0.6464, the long-handled one 0.5625
			// (verified against the Bezier math before the test was written).
			AnimCurve Weighted( float w ) => new()
			{
				Keys =
				{
					new AnimCurveKey { Frame = 0, Value = 0f, Tangent = AnimTangent.Bezier, TangentOut = 0f, OutWeight = w },
					new AnimCurveKey { Frame = 10, Value = 1f, Tangent = AnimTangent.Bezier, TangentIn = 0.2f, InWeight = w },
				},
			};

			var shortHandles = Weighted( 1f / 6f ).Evaluate( 7.5f );
			var longHandles = Weighted( 1f / 3f ).Evaluate( 7.5f );
			Require( shortHandles > 0.6f && shortHandles < 0.7f, $"short handles at 7.5: {shortHandles}" );
			Require( longHandles > 0.5f && longHandles < 0.6f, $"long handles at 7.5: {longHandles}" );
			Require( shortHandles > longHandles, "shorter handles must read higher here" );
		} );

		Check( "f-curve modifiers warp time and add value like the track sampler", () =>
		{
			var wave = AnimCurve.Linear( 0, 10, 0f, 10f );
			wave.Modifiers.Add( new FCurveModifier( FCurveModifierKind.SinWave, 1f, 10f ) );
			Near( wave.Evaluate( 2.5f ), 3.5f, 1e-4f, "linear 2.5 + sin(pi/2)" );

			var cycle = AnimCurve.Linear( 0, 20, 0f, 20f );
			cycle.Modifiers.Add( new FCurveModifier( FCurveModifierKind.Cycle, 0f, 10f ) );
			Near( cycle.Evaluate( 15 ), 5f, 1e-4f, "cycle wraps frame 15 to frame 5" );
		} );

		Check( "smooth pins endpoints and flattens a spike; despike replaces an outlier", () =>
		{
			var spiky = new AnimCurve { Keys =
				{ new AnimCurveKey { Frame = 0, Value = 0f },
				  new AnimCurveKey { Frame = 1, Value = 0f },
				  new AnimCurveKey { Frame = 2, Value = 10f },
				  new AnimCurveKey { Frame = 3, Value = 0f },
				  new AnimCurveKey { Frame = 4, Value = 0f } } };
			spiky.Smooth( 3 );
			Near( spiky.Keys[0].Value, 0f, 1e-6f, "smooth must pin the first key" );
			Near( spiky.Keys[4].Value, 0f, 1e-6f, "smooth must pin the last key" );
			Require( spiky.Keys[2].Value < 5f, $"smooth must flatten the spike, got {spiky.Keys[2].Value}" );

			var outlier = new AnimCurve { Keys =
				{ new AnimCurveKey { Frame = 0, Value = 0f },
				  new AnimCurveKey { Frame = 1, Value = 0f },
				  new AnimCurveKey { Frame = 2, Value = 100f },
				  new AnimCurveKey { Frame = 3, Value = 0f },
				  new AnimCurveKey { Frame = 4, Value = 0f } } };
			outlier.Despike( 10f );
			Near( outlier.Keys[2].Value, 0f, 1e-6f, "despike must replace the outlier with the neighbour median" );
		} );

		Check( "moving 300 keys in one op keeps every key and lands them at +10", () =>
		{
			var curve = new AnimCurve();

			for ( var i = 0; i < 300; i++ )
				curve.Keys.Add( new AnimCurveKey { Frame = i, Value = i * 0.1f, Tangent = AnimTangent.Linear } );

			var clock = Stopwatch.StartNew();
			var moved = curve.MoveKeys( Enumerable.Range( 0, 300 ).ToList(), 10 );
			clock.Stop();

			Require( moved == 10, $"applied delta {moved}" );
			Require( curve.Keys.Count == 300, $"key count {curve.Keys.Count}" );
			Require( curve.Keys[0].Frame == 10, $"first frame {curve.Keys[0].Frame}" );
			Require( curve.Keys[^1].Frame == 309, $"last frame {curve.Keys[^1].Frame}" );
			Near( curve.Keys[0].Value, 0f, 1e-6f, "first value moved" );
			Near( curve.Keys[^1].Value, 29.9f, 1e-4f, "last value moved" );
			Require( clock.ElapsedMilliseconds < 1000, $"300-key move took {clock.ElapsedMilliseconds} ms" );
		} );

		Check( "a curve round-trips through scalar keys and reports bad curves", () =>
		{
			var keys = new List<ScalarKey> { new( 0, 0f ), new( 10, 5f ), new( 20, 3f ) };
			var roundTripped = AnimCurve.FromScalarKeys( keys ).ToScalarKeys();
			Require( roundTripped.Select( k => k.Frame ).SequenceEqual( new[] { 0, 10, 20 } ), "frames did not round-trip" );
			Require( roundTripped.Select( k => k.Value ).SequenceEqual( new[] { 0f, 5f, 3f } ), "values did not round-trip" );

			var good = AnimCurve.Linear( 0, 10, 0f, 1f );
			Require( good.Validate().Count == 0, "a clean curve must validate clean" );

			var bad = new AnimCurve { Keys =
				{ new AnimCurveKey { Frame = 10, Value = 0f },
				  new AnimCurveKey { Frame = 10, Value = 1f } } };
			Require( bad.Validate().Count > 0, "duplicate frames must be reported" );
		} );

		// ---- Layers: live stack + Smart Bake ----

		Check( "overlay weight 0 is base, 1 is target, 0.5 is halfway", () =>
		{
			var stack = new AnimLayerStack { Base = FlatClip( 0f ) };
			var layer = new AnimLayer { Name = "lift", Clip = FlatClip( 10f ), Mode = RigLayerMode.Overlay, Weight = 0.5f };
			stack.Layers.Add( layer );

			layer.Weight = 0f;
			Near( stack.Evaluate( 30 )["root"].Position.y, 0f, 1e-6f, "weight 0 must be exactly base" );
			layer.Weight = 1f;
			Near( stack.Evaluate( 30 )["root"].Position.y, 10f, 1e-6f, "weight 1 must be exactly target" );
			layer.Weight = 0.5f;
			Near( stack.Evaluate( 30 )["root"].Position.y, 5f, 1e-6f, "weight 0.5 must be halfway" );
		} );

		Check( "additive layers add the delta from their first key", () =>
		{
			var stack = new AnimLayerStack { Base = FlatClip( 10f ) };
			var layer = new AnimLayer { Name = "sway", Clip = MakeClip(), Mode = RigLayerMode.Additive, Weight = 1f };
			stack.Layers.Add( layer );

			// Layer root rises 0 -> 10 -> 20; reference is its first key (0), so at frame 30
			// the delta is +10 on top of the base's 10.
			Near( stack.Evaluate( 30 )["root"].Position.y, 20f, 1e-4f, "additive at weight 1" );
			layer.Weight = 0.5f;
			Near( stack.Evaluate( 30 )["root"].Position.y, 15f, 1e-4f, "additive at weight 0.5" );
		} );

		Check( "muted layers contribute nothing and solo isolates one layer", () =>
		{
			var stack = new AnimLayerStack { Base = FlatClip( 0f ) };
			var a = new AnimLayer { Name = "a", Clip = FlatClip( 10f ), Mode = RigLayerMode.Overlay };
			var b = new AnimLayer { Name = "b", Clip = FlatClip( 20f ), Mode = RigLayerMode.Overlay };
			stack.Layers.Add( a );
			stack.Layers.Add( b );

			b.Muted = true;
			Near( stack.Evaluate( 30 )["root"].Position.y, 10f, 1e-6f, "muted b must not apply" );
			a.Muted = true;
			b.Muted = false;
			Near( stack.Evaluate( 30 )["root"].Position.y, 20f, 1e-6f, "muted a must not apply" );
			a.Muted = false;
			b.Solo = true;
			Near( stack.Evaluate( 30 )["root"].Position.y, 20f, 1e-6f, "solo b must isolate b" );
			a.Solo = true;
			b.Solo = false;
			Near( stack.Evaluate( 30 )["root"].Position.y, 10f, 1e-6f, "solo a must isolate a" );
		} );

		Check( "ghost layers report their own poses without applying", () =>
		{
			var stack = new AnimLayerStack { Base = FlatClip( 0f ) };
			var ghost = new AnimLayer { Name = "ghost", Clip = FlatClip( 10f ), Mode = RigLayerMode.Overlay, Ghost = true };
			var plain = new AnimLayer { Name = "plain", Clip = FlatClip( 20f ), Mode = RigLayerMode.Overlay };
			stack.Layers.Add( ghost );
			stack.Layers.Add( plain );

			var ghosts = stack.GhostPoses( 30 );
			Require( ghosts.Count == 1 && ghosts.ContainsKey( "root" ), "only the ghost layer must report" );
			Near( ghosts["root"].Position.y, 10f, 1e-6f, "ghost pose must be the layer's own pose" );
			Near( stack.Evaluate( 30 )["root"].Position.y, 20f, 1e-6f, "ghosting must not change the evaluated pose" );
		} );

		Check( "smart bake merges without changing the evaluated pose and leaves sources untouched", () =>
		{
			var baseClip = FlatClip( 0f );
			var lift = FlatClip( 10f );
			var stack = new AnimLayerStack { Base = baseClip };
			stack.Layers.Add( new AnimLayer { Name = "lift", Clip = lift, Mode = RigLayerMode.Overlay, Weight = 0.5f } );

			var (baked, notes) = stack.SmartBake();
			Require( notes.Count == 0, "clean bake must report no notes: " + string.Join( "; ", notes ) );

			foreach ( var frame in new[] { 0, 15, 30, 45, 60 } )
			{
				var live = stack.Evaluate( frame )["root"];
				var bakedPose = baked.Tracks.First( t => t.Name == "root" ).Evaluate( frame );
				Near( bakedPose.Position.y, live.Position.y, 1e-3f, $"baked pose must match live at frame {frame}" );
				Near( bakedPose.Position.y, 5f, 1e-3f, $"baked pose must be halfway at frame {frame}" );
				Require( MathF.Abs( QuatDot( bakedPose.Rotation, live.Rotation ) ) > 0.999f, "baked rotation must match live" );
			}

			Require( baseClip.Tracks[0].Keys.Select( k => k.Local.Position.y ).All( y => y == 0f ), "bake must not alter the base clip" );
			Require( lift.Tracks[0].Keys.Select( k => k.Local.Position.y ).All( y => y == 10f ), "bake must not alter the layer clip" );
			Require( baseClip.FrameCount == 60, "bake must not alter the base length" );
		} );

		// ---- Timeline: playhead, keying sets, playblast ----

		Check( "playhead steps, loops and reports seconds", () =>
		{
			var playhead = new AnimPlayhead { Frame = 59, FrameRate = 30f, StartFrame = 0, EndFrame = 60, StepFrames = 2 };
			playhead.StepForward();
			Near( playhead.Frame, 60f, 1e-6f, "step must clamp at the end without looping" );
			playhead.StepForward();
			Near( playhead.Frame, 60f, 1e-6f, "step must stay clamped" );

			playhead.Loop = true;
			playhead.StepForward();
			Near( playhead.Frame, 2f, 1e-6f, "looping step must wrap past the end" );
			playhead.StepBack();
			Near( playhead.Frame, 0f, 1e-6f, "looping step back must land on the start" );

			playhead.Seek( 30 );
			Near( playhead.TimeSeconds, 1f, 1e-6f, "frame 30 at 30 fps is 1 second" );
			Near( playhead.FrameAt( 2.5f ), 75f, 1e-6f, "2.5 seconds at 30 fps is frame 75" );
		} );

		Check( "keying set keys every entry at the playhead frame and reports missing tracks", () =>
		{
			var clip = MakeClip();
			var set = new AnimKeyingSet { Name = "arms" };
			set.Entries.Add( new AnimKeyingSet.KeyingEntry( "root", null ) );
			set.Entries.Add( new AnimKeyingSet.KeyingEntry( "arm", null ) );
			set.Entries.Add( new AnimKeyingSet.KeyingEntry( "root", RigChannel.PosY ) );
			set.Entries.Add( new AnimKeyingSet.KeyingEntry( "ghost", null ) );

			var result = set.KeyAt( 15, clip );
			Require( result.KeysWritten == 3, $"keys written {result.KeysWritten}" );
			Require( result.Notes.Count == 1 && result.Notes[0].Contains( "ghost" ), "missing track must be reported" );

			var root = clip.Tracks.First( t => t.Name == "root" );
			var keyed = root.Keys.FirstOrDefault( k => k.Frame == 15 );
			Require( keyed is not null, "root must gain a key at frame 15" );
			Near( keyed.Local.Position.y, 5f, 1e-4f, "keyed pose must be the pose already there" );
			Require( clip.Tracks.First( t => t.Name == "arm" ).Keys.Any( k => k.Frame == 15 ), "arm must gain a key at frame 15" );

			var channel = root.ChannelCurves[RigChannel.PosY];
			var channelKey = channel.FirstOrDefault( k => k.Frame == 15 );
			Require( channelKey is not null, "root PosY must gain a channel key at frame 15" );
			Near( channelKey.Value, 5f, 1e-4f, "channel key must hold the channel's current value" );
		} );

		Check( "playblast plan for 10 s at 24 fps is 240 frames at 720p; bad plans are refused", () =>
		{
			var plan = PlayblastPlan.TenSeconds720p( 24f );
			Require( plan.FrameCount == 240, $"frame count {plan.FrameCount}" );
			Near( plan.DurationSeconds, 10f, 1e-6f, "duration" );
			Require( plan.Width == 1280 && plan.Height == 720, "720p size" );
			Require( plan.Validate().Count == 0, "a clean plan must validate clean" );
			var frames = plan.Frames().ToList();
			Require( frames.Count == 240 && frames[0] == 0 && frames[^1] == 239, "frames must run 0..239 exactly once" );
			Near( plan.FrameTime( 24 ), 1f, 1e-6f, "frame 24 plays at 1 second" );

			var badFps = PlayblastPlan.TenSeconds720p( 25f );
			Require( badFps.Validate().Any( n => n.Contains( "fps" ) ), "25 fps must be refused" );
			var oddSize = new PlayblastPlan { Fps = 24f, Width = 1279, Height = 720, StartFrame = 0, EndFrame = 10 };
			Require( oddSize.Validate().Any( n => n.Contains( "odd" ) ), "odd width must be refused" );
			var empty = new PlayblastPlan { Fps = 24f, StartFrame = 10, EndFrame = 5 };
			Require( empty.Validate().Count > 0, "an empty range must be refused" );
		} );

		Check( "autosave default interval meets the 5-minute bar", () =>
		{
			var autosave = new DesktopPreferences().Autosave;
			Require( autosave.Enabled, "autosave must be on by default" );
			Require( autosave.IntervalSeconds <= 300.0, $"autosave interval {autosave.IntervalSeconds}s is slower than the 5-minute bar" );
		} );
	}
}
