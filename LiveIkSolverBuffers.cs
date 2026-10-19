using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Reusable, single-thread-owned clip sampler. Construct again after changing tracks, rig or
/// constraints. Unsupported configurations deliberately use the kernel's authoritative sampler.
/// The fast path covers model/world goals, one/two-bone, CCD and FABRIK, including stretch,
/// goal rotation, keyed weights and stacked constraints. Spline, object anchors, limits and
/// in-place root motion currently fall back; they must never be silently omitted.
/// </summary>
public sealed class LiveIkSolverBuffers
{
	readonly Skeleton _rig;
	readonly RigAnimFile _clip;
	readonly RigTrack[] _bones;
	readonly Entry[] _entries;
	readonly Xform[] _local, _world, _scratch;
	readonly bool _fallback;

	sealed class Entry
	{
		public RigIk Ik;
		public RigTrack Goal, Weight;
		public int[] Chain;
		public Xform[] Poses;
		public Vec3[] Points;
		public float[] Lengths;
	}

	public bool UsesKernelFallback => _fallback;
	public ReadOnlySpan<Xform> Local => _local;
	public ReadOnlySpan<Xform> World => _world;

	public LiveIkSolverBuffers( Skeleton rig, RigAnimFile clip )
	{
		_rig = rig ?? throw new ArgumentNullException( nameof( rig ) );
		_clip = clip ?? throw new ArgumentNullException( nameof( clip ) );
		_bones = new RigTrack[rig.Count];
		_local = new Xform[rig.Count];
		_world = new Xform[rig.Count];
		_scratch = new Xform[rig.Count];
		var entries = new List<Entry>();
		bool fallback = clip.PlayInPlace && clip.RootMotion is { Enabled: true };
		fallback |= clip.Limits is { Count: > 0 }; // kernel applies these to both FK and IK

		for ( int b = 0; b < rig.Count; b++ )
		{
			if ( rig.Bones[b].Parent >= b || rig.Bones[b].Parent < -1 )
				throw new ArgumentException( "Skeleton must be parent-before-child.", nameof( rig ) );
			foreach ( var track in clip.Tracks )
				if ( track.Kind == RigTrackKind.Bone && track.Name == rig.Bones[b].Name ) { _bones[b] = track; break; }
		}

		bool hasGoals = false;
		foreach ( var track in clip.Tracks )
			if ( track.Kind == RigTrackKind.IkGoal ) { hasGoals = true; break; }
		if ( hasGoals )
		{
			if ( clip.Ik is null )
			{
				foreach ( var track in clip.Tracks )
					if ( track.Kind == RigTrackKind.IkGoal ) Add( new RigIk { Name = track.Name, EndBone = track.Name } );
			}
			else
				foreach ( var ik in clip.Ik )
					if ( ik.Enabled && ik.Live ) Add( ik );
		}
		_entries = entries.ToArray();
		_fallback = fallback;

		void Add( RigIk ik )
		{
			RigTrack goal = null, weight = null;
			foreach ( var track in clip.Tracks )
			{
				if ( track.Name != ik.EndBone ) continue;
				if ( track.Kind == RigTrackKind.IkGoal && goal is null ) goal = track;
				if ( track.Kind == RigTrackKind.IkWeight && weight is null && track.Keys.Count > 0 ) weight = track;
			}
			if ( goal is null ) return;
			if ( ik.Spline || clip.GoalSpaces.TryGetValue( ik.EndBone, out var anchor ) && anchor.Space == RigGoalSpace.Object ) fallback = true;
			int end = rig.IndexOf( ik.EndBone );
			if ( end < 0 || ik.ChainLength < 1 ) return; // same as ChainIk.Apply returning null
			var chain = new int[ik.ChainLength + 1];
			chain[^1] = end;
			for ( int k = chain.Length - 2; k >= 0; k-- )
			{
				chain[k] = rig.Bones[chain[k + 1]].Parent;
				if ( chain[k] < 0 ) return;
			}
			entries.Add( new Entry { Ik = ik, Goal = goal, Weight = weight, Chain = chain,
				Poses = new Xform[chain.Length], Points = new Vec3[chain.Length], Lengths = new float[chain.Length - 1] } );
		}
	}

	/// <summary>Writes into owned buffers; callers must consume/copy before the next Sample.</summary>
	public void Sample( float frame )
	{
		if ( !float.IsFinite( frame ) ) throw new ArgumentOutOfRangeException( nameof( frame ) );
		if ( _fallback )
		{
			_clip.SampleLocal( _rig, frame ).CopyTo( _local, 0 );
			Forward();
			return;
		}
		for ( int b = 0; b < _rig.Count; b++ )
			_local[b] = _bones[b] is { Keys.Count: > 0 } t ? t.Evaluate( frame ).ToXform() : _rig.Bones[b].Local;
		Forward();
		foreach ( var entry in _entries )
		{
			float keyed = entry.Weight is null ? 1f : Math.Clamp( entry.Weight.Evaluate( frame ).Scale.x, 0f, 1f );
			if ( keyed <= 0 ) continue;
			var goal = entry.Goal.Evaluate( frame );
			if ( Solve( entry, goal, Math.Clamp( entry.Ik.Weight * keyed, 0f, 1f ) ) )
			{
				// Convert the entire solved model pose, including descendants, back to local exactly
				// as ChainIk.Apply/SolveIk do. Rebuild to match their forward composition order.
				for ( int b = 0; b < _rig.Count; b++ )
				{
					int parent = _rig.Bones[b].Parent;
					_scratch[b] = parent >= 0 ? _world[parent].Inverse * _world[b] : _world[b];
				}
				Array.Copy( _scratch, _local, _local.Length );
				Forward();
			}
		}
	}

	void Forward()
	{
		for ( int b = 0; b < _rig.Count; b++ )
		{
			int parent = _rig.Bones[b].Parent;
			_world[b] = parent >= 0 ? _world[parent] * _local[b] : _local[b];
		}
	}

	bool Solve( Entry e, RigPose goal, float weight )
	{
		var chain = e.Chain;
		var w = e.Poses;
		int n = chain.Length - 1;
		for ( int k = 0; k <= n; k++ ) w[k] = _world[chain[k]];
		bool stretched = ChainIk.Stretch( w, goal.Position, e.Ik.MaxStretch );
		if ( n == 2 )
		{
			var a = w[0].Origin; var b = w[1].Origin; var c = w[2].Origin;
			if ( !TwoBoneIk.TrySolve( a, b, c, goal.Position, e.Ik.Pole, 0f, out var s ) ) return false;
			w[0] = About( a, s.UpperDelta ) * w[0];
			w[1] = Xform.Translate( s.Middle - b ) * About( b, s.LowerDelta ) * w[1];
			w[2] = Xform.Translate( s.End - c ) * w[2];
		}
		else if ( n >= 3 && (e.Ik.Fabrik || stretched) )
		{
			Fabrik( e, goal.Position );
			w[n] = Xform.Translate( w[n].Origin - _world[chain[n]].Origin ) * _world[chain[n]];
		}
		else
		{
			int passes = n == 1 ? 1 : Math.Max( e.Ik.Iterations, 1 );
			for ( int pass = 0; pass < passes; pass++ )
			{
				for ( int j = n - 1; j >= 0; j-- )
				{
					var pivot = w[j].Origin;
					var delta = DeltaBetween( w[n].Origin - pivot, goal.Position - pivot );
					for ( int k = j; k <= n; k++ ) w[k] = About( pivot, delta ) * w[k];
				}
				if ( (w[n].Origin - goal.Position).Length < 0.01f ) break;
			}
			w[n] = Xform.Translate( w[n].Origin - _world[chain[n]].Origin ) * _world[chain[n]];
		}
		if ( e.Ik.GoalRotation ) w[n] = goal.Rotation.ToXform( w[n].Origin, Quat.ScaleOf( _world[chain[n]] ) );
		if ( weight < 1f )
			for ( int k = 0; k <= n; k++ )
			{
				var from = _world[chain[k]];
				w[k] = Quat.Slerp( Quat.FromXform( from ), Quat.FromXform( w[k] ), weight )
					.ToXform( Vec3.Lerp( from.Origin, w[k].Origin, weight ), Quat.ScaleOf( from ) );
			}

		// ChainIk.Apply writes new locals using the solved parent, then carries all children.
		for ( int b = 0; b < _rig.Count; b++ )
		{
			int parent = _rig.Bones[b].Parent;
			_scratch[b] = parent >= 0 ? _world[parent].Inverse * _world[b] : _world[b];
		}
		for ( int k = 0; k <= n; k++ )
		{
			int parent = _rig.Bones[chain[k]].Parent;
			var parentWorld = k > 0 ? w[k - 1] : parent >= 0 ? _world[parent] : Xform.Identity;
			_scratch[chain[k]] = parent >= 0 ? parentWorld.Inverse * w[k] : w[k];
		}
		for ( int b = 0; b < _rig.Count; b++ )
		{
			int parent = _rig.Bones[b].Parent;
			_world[b] = parent >= 0 ? _world[parent] * _scratch[b] : _scratch[b];
		}
		return true;
	}

	void Fabrik( Entry e, Vec3 target )
	{
		var w = e.Poses; var p = e.Points; var d = e.Lengths;
		int n = d.Length; float total = 0;
		for ( int k = 0; k <= n; k++ ) p[k] = w[k].Origin;
		for ( int k = 0; k < n; k++ ) { d[k] = (p[k + 1] - p[k]).Length; total += d[k]; }
		var root = p[0];
		if ( (target - root).Length >= total )
		{
			var dir = (target - root).Normal;
			for ( int k = 0; k < n; k++ ) p[k + 1] = p[k] + dir * d[k];
		}
		else
			for ( int pass = 0; pass < Math.Max( e.Ik.Iterations, 1 ); pass++ )
			{
				p[n] = target;
				for ( int k = n - 1; k >= 0; k-- ) p[k] = p[k + 1] + Toward( p[k] - p[k + 1] ) * d[k];
				p[0] = root;
				for ( int k = 0; k < n; k++ ) p[k + 1] = p[k] + Toward( p[k + 1] - p[k] ) * d[k];
				if ( (p[n] - target).Length < 0.01f ) break;
			}
		for ( int j = 0; j < n; j++ )
		{
			var pivot = w[j].Origin;
			var delta = DeltaBetween( w[j + 1].Origin - pivot, p[j + 1] - pivot );
			for ( int k = j; k <= n; k++ ) w[k] = About( pivot, delta ) * w[k];
		}
	}

	static Vec3 Toward( Vec3 v ) => v.Length > 1e-6f ? v.Normal : new Vec3( 0, 0, 1 );
	// Kernel's TwoBoneIk helpers are internal to its assembly; preserve their exact maths here.
	static Xform About( Vec3 point, Xform rotation ) =>
		Xform.Translate( point ) * rotation * Xform.Translate( -point );

	static Xform DeltaBetween( Vec3 from, Vec3 to )
	{
		if ( from.Length < 0.001f || to.Length < 0.001f ) return Xform.Identity;
		from = from.Normal;
		to = to.Normal;
		var dot = Math.Clamp( Vec3.Dot( from, to ), -1f, 1f );
		if ( dot > 0.99999f ) return Xform.Identity;
		if ( dot < -0.99999f )
		{
			var perpendicular = Vec3.Cross( from, MathF.Abs( from.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) );
			return Xform.Rotate( perpendicular.Normal, MathF.PI );
		}
		return Xform.Rotate( Vec3.Cross( from, to ).Normal, MathF.Acos( dot ) );
	}
}
