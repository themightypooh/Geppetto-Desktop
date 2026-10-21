#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Effigy;
using Marionette.Desktop;

internal static class LiveIkBufferTests
{
	public static void Register( TestRegistry suite, TestSettings settings )
	{
		suite.Add( "live-ik", "kernel parity and warmed allocations", Parity );
			suite.Add( "live-ik", "unsupported solver fallback", Fallback );
			suite.Add( "live-ik", "stacked constraints and kernel baseline", Stacked );
			suite.Add( "live-ik", "invalid frame and empty goals", Boundaries );
			suite.Add( "live-ik", "player Step warm fast path and fallback", PlayerStep );
	}

	static (Skeleton, RigAnimFile) Fixture( int length, bool fabrik = false, bool spline = false )
	{
		var rig = new Skeleton();
		rig.AddBone( "root", -1, Xform.Identity, 3f );
		for ( int i = 1; i <= length; i++ )
			rig.AddBone( i == length ? "tip" : $"joint{i}", i - 1, Xform.Translate( new Vec3( 0, 3, 0 ) ), 3f );
		rig.AddBone( "child", length, Xform.Translate( new Vec3( 0, 1, 0 ) ), 1f );
		var clip = new RigAnimFile { FrameRate = 30, FrameCount = 31 };
		clip.Ik = new List<RigIk> { new() { EndBone = "tip", ChainLength = length,
			Fabrik = fabrik, Spline = spline, MaxStretch = 1.5f, Iterations = 12 } };
		var root = new RigTrack { Name = "root", Kind = RigTrackKind.Bone };
		root.Keys.Add( new RigKey { Frame = 0, Local = RigPose.FromXform( Xform.Identity ) } );
		root.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( 1, 0, 0 ), Quat.Identity, Vec3.One ) } );
		clip.Tracks.Add( root );
		var goal = new RigTrack { Name = "tip", Kind = RigTrackKind.IkGoal };
		goal.Keys.Add( new RigKey { Frame = 0, Local = new RigPose( new Vec3( 2, length * 2f, 1 ), Quat.Identity, Vec3.One ) } );
		goal.Keys.Add( new RigKey { Frame = 30, Local = new RigPose( new Vec3( 1, length * 4f, 0 ), Quat.FromAngles( 10, 20, 30 ), Vec3.One ) } );
		clip.Tracks.Add( goal );
		var weight = new RigTrack { Name = "tip", Kind = RigTrackKind.IkWeight };
		weight.Keys.Add( new RigKey { Frame = 0, Local = RigPose.Identity with { Scale = Vec3.Zero } } );
		weight.Keys.Add( new RigKey { Frame = 30, Local = RigPose.Identity } );
		clip.Tracks.Add( weight );
		return (rig, clip);
	}

	static void Compare( Skeleton rig, RigAnimFile clip, LiveIkSolverBuffers buffers, float frame, float tolerance = 0.003f )
	{
		buffers.Sample( frame );
		var expected = clip.SampleLocal( rig, frame );
		var world = RigAnimFile.ModelSpace( rig, expected );
		for ( int b = 0; b < rig.Count; b++ )
		{
			AssertXform( buffers.Local[b], expected[b], tolerance, $"frame {frame}, local {b}" );
			AssertXform( buffers.World[b], world[b], tolerance, $"frame {frame}, world {b}" );
		}
	}

	static void AssertXform( Xform a, Xform b, float tolerance, string where )
	{
		if ( (a.Origin - b.Origin).Length > tolerance || (a.X - b.X).Length > tolerance ||
			(a.Y - b.Y).Length > tolerance || (a.Z - b.Z).Length > tolerance )
			throw new Exception( $"{where}: kernel disagreement" );
	}

	static void Parity()
	{
		foreach ( var (length, fabrik) in new[] { (1, false), (2, false), (3, false), (4, true) } )
		{
			var (rig, clip) = Fixture( length, fabrik );
			var buffers = new LiveIkSolverBuffers( rig, clip );
			if ( buffers.UsesKernelFallback ) throw new Exception( "Expected fast path" );
			var random = new Random( 41 );
			foreach ( var frame in new[] { 0f, 0.5f, 7.25f, 15f, 29.5f, 30f } ) Compare( rig, clip, buffers, frame );
			for ( int i = 0; i < 40; i++ ) Compare( rig, clip, buffers, (float)random.NextDouble() * 30f );

			// JIT, generic caches and lazy track internals warmed before allocation sampling.
			for ( int i = 0; i < 100; i++ ) buffers.Sample( i * 0.31f );
			var clock = Stopwatch.StartNew();
			long before = GC.GetAllocatedBytesForCurrentThread();
			for ( int i = 0; i < 2000; i++ ) buffers.Sample( (i % 96) * 0.3125f );
			clock.Stop();
			long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
			if ( bytes != 0 ) throw new Exception( $"{length}-bone {(fabrik ? "FABRIK" : "CCD/two-bone")} allocated {bytes} bytes/2000 samples" );
			Console.WriteLine( $"live IK {length}-bone {(fabrik ? "FABRIK" : "CCD/two-bone")}: {bytes} B / 2000 samples, {clock.Elapsed.TotalMilliseconds:0.###} ms" );
		}
	}

	static void Fallback()
	{
		var (rig, clip) = Fixture( 4, spline: true );
		var buffers = new LiveIkSolverBuffers( rig, clip );
		if ( !buffers.UsesKernelFallback ) throw new Exception( "Spline must fall back" );
		Compare( rig, clip, buffers, 5.5f, 0f );
		clip.Ik![0].Spline = false;
		clip.PlayInPlace = true;
		clip.RootMotion = new RootMotionSettings { Enabled = true, Bone = "root" };
		buffers = new LiveIkSolverBuffers( rig, clip );
		if ( !buffers.UsesKernelFallback ) throw new Exception( "In-place root motion must fall back" );
		Compare( rig, clip, buffers, 5.5f, 0f );
		clip.PlayInPlace = false;
		clip.Limits = new List<RigLimit> { new() { Bone = "joint1", Enabled = true } };
		buffers = new LiveIkSolverBuffers( rig, clip );
		if ( !buffers.UsesKernelFallback ) throw new Exception( "Limits must fall back" );
		Compare( rig, clip, buffers, 5.5f, 0f );
		clip.Limits = null;
		clip.GoalSpaces["tip"] = (RigGoalSpace.Object, "missing");
		buffers = new LiveIkSolverBuffers( rig, clip );
		if ( !buffers.UsesKernelFallback ) throw new Exception( "Object-space goals must fall back" );
		Compare( rig, clip, buffers, 5.5f, 0f );
	}

	static void Stacked()
	{
		var (rig, clip) = Fixture( 3 );
		clip.Ik!.Add( new RigIk { EndBone = "tip", ChainLength = 2, Fabrik = true, Weight = 0.65f, GoalRotation = false } );
		var buffers = new LiveIkSolverBuffers( rig, clip );
		for ( int i = 0; i <= 60; i++ ) Compare( rig, clip, buffers, i * 0.5f );
		// Compare the old sampler's actual allocation and elapsed time on the same fixture.
		for ( int i = 0; i < 20; i++ ) clip.SampleLocal( rig, i * 0.5f );
		var timer = Stopwatch.StartNew();
		long before = GC.GetAllocatedBytesForCurrentThread();
		for ( int i = 0; i < 200; i++ ) clip.SampleLocal( rig, (i % 60) * 0.5f );
		long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
		timer.Stop();
		if ( bytes <= 0 ) throw new Exception( "Kernel baseline unexpectedly allocated nothing" );
		Console.WriteLine( $"kernel stacked CCD/FABRIK: {bytes} B / 200 samples, {timer.Elapsed.TotalMilliseconds:0.###} ms" );
	}

	static void Boundaries()
	{
		var (rig, clip) = Fixture( 2 );
		var buffers = new LiveIkSolverBuffers( rig, clip );
		foreach ( var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity } )
		{
			try { buffers.Sample( bad ); throw new Exception( "Non-finite frame accepted" ); }
			catch ( ArgumentOutOfRangeException ) { }
		}
		clip.Tracks.RemoveAll( t => t.Kind == RigTrackKind.IkGoal );
		buffers = new LiveIkSolverBuffers( rig, clip );
		Compare( rig, clip, buffers, 7.25f );
	}

	static void PlayerStep()
	{
		foreach ( var fabrik in new[] { false, true } )
		{
			var (rig, clip) = Fixture( fabrik ? 4 : 2, fabrik );
			var player = new MarionettePlayer( rig );
			player.LoadClip( clip );
			player.Playing = true;
			if ( !player.UsesKernelSolver ) throw new Exception( "Live goal was not selected by player" );
			for ( int i = 0; i < 100; i++ ) { player.Time = i * 0.002f; player.Step( 0 ); }
			long before = GC.GetAllocatedBytesForCurrentThread();
			for ( int i = 0; i < 2000; i++ )
			{
				player.Time = (i % 96) * 0.002f;
				player.Step( 0 );
			}
			long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
			if ( bytes != 0 ) throw new Exception( $"Player Step allocated {bytes} bytes/2000 {(fabrik ? "FABRIK" : "two-bone")} frames" );
			Console.WriteLine( $"player {(fabrik ? "FABRIK" : "two-bone")}: {bytes} B / 2000 warm Step calls" );
			foreach ( var frame in new[] { 0f, 0.5f, 7.25f, 29.5f, 30f } )
			{
				player.Playing = false;
				player.Time = frame / clip.FrameRate;
				player.Step( 0 );
				var expected = RigAnimFile.ModelSpace( rig, clip.SampleLocal( rig, frame ) );
				for ( int b = 0; b < rig.Count; b++ )
					AssertXform( player.WorldPose[b], expected[b], 0.003f, $"player frame {frame}, bone {b}" );
			}
		}
		var (fallbackRig, fallbackClip) = Fixture( 4, spline: true );
		var fallbackPlayer = new MarionettePlayer( fallbackRig );
		fallbackPlayer.LoadClip( fallbackClip );
		fallbackPlayer.Time = 5.5f / fallbackClip.FrameRate;
		fallbackPlayer.Step( 0 );
		var oracle = RigAnimFile.ModelSpace( fallbackRig, fallbackClip.SampleLocal( fallbackRig, 5.5f ) );
		for ( int b = 0; b < fallbackRig.Count; b++ )
			AssertXform( fallbackPlayer.WorldPose[b], oracle[b], 0.003f, $"player spline fallback bone {b}" );
	}
}
