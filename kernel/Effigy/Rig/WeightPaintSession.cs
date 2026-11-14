using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>One committed weight stroke, held so it can be undone and redone.</summary>
public sealed class WeightEdit
{
	readonly Dictionary<int, BoneWeight[]> _before;
	readonly Dictionary<int, BoneWeight[]> _after;

	public WeightEdit( Dictionary<int, BoneWeight[]> before, Dictionary<int, BoneWeight[]> after )
	{
		_before = before;
		_after = after;
	}

	/// <summary>How many vertices this stroke actually changed.</summary>
	public int Count => _before.Count;

	internal void Apply( SkinWeights weights, bool forward )
	{
		var source = forward ? _after : _before;

		foreach ( var (vertex, value) in source )
		{
			if ( vertex >= 0 && vertex < weights.Count )
				weights[vertex] = value;
		}
	}

	internal IEnumerable<int> Vertices => _before.Keys;

	/// <summary>
	/// Scriptable rigid-island binding (roadmap J2): bind every small disconnected component
	/// of <paramref name="mesh"/> 100% to its nearest bone, and hand back the undo entry for it.
	/// The rule is <see cref="AutoWeights.RigidIslandPlan"/> with no name list: a component
	/// smaller than <paramref name="maxFraction"/> of the mesh, and smaller than the largest
	/// component, is rigid. A single-component mesh changes nothing and returns null, the same
	/// convention <see cref="WeightPaintSession.EndStroke"/> uses for a no-op stroke.
	/// This is the helper a future `weight` script verb calls. BuildScriptRig has no weight
	/// verb today, so that wiring is a Lane P follow-up, deliberately not faked here.
	/// </summary>
	public static WeightEdit Run( PolyMesh mesh, SkinWeights weights, Skeleton skeleton, float maxFraction = 0.10f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( weights is null )
			throw new ArgumentNullException( nameof( weights ) );

		if ( weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );

		var options = new AutoWeightOptions { RigidIslandMaxFraction = maxFraction };
		var plan = AutoWeights.RigidIslandPlan( mesh, skeleton, options, null );

		if ( plan.Count == 0 )
			return null;

		var before = new Dictionary<int, BoneWeight[]>();
		var after = new Dictionary<int, BoneWeight[]>();

		foreach ( var (bone, vertices) in plan )
		{
			var single = new[] { new BoneWeight( bone, 1f ) };

			foreach ( var v in vertices )
			{
				before[v] = weights[v];
				weights[v] = single;
				after[v] = single;
			}
		}

		return new WeightEdit( before, after );
	}
}

/// <summary>
/// Everything about weight painting that is arithmetic rather than widgets.
///
/// SAME SPLIT AS SculptSession, AND FOR THE SAME REASON: the half that can be tested without s&amp;box
/// is worth having as its own object, and it turns out to be nearly all of it. The editor's job
/// reduces to turning a mouse into a ray and drawing a ring.
///
/// A STROKE IS ONE UNDO ENTRY. The brush is applied live so the viewport can colour the weights as
/// the cursor moves, and the whole gesture is committed once at <see cref="EndStroke"/> — because
/// one dab is not what a user means by "undo".
///
/// **The layer is written at the same moment.** A stroke that changed the weights and did not reach
/// <see cref="Layer"/> would look correct until the next rebuild and then vanish, which is the
/// single worst failure this tool can have: it is invisible at the moment it happens and blamed on
/// something else later. So committing a stroke and recording it as paint are one operation, and
/// undo takes both back.
/// </summary>
public sealed class WeightPaintSession
{
	readonly PolyMesh _mesh;
	readonly SkinWeights _weights;
	readonly Skeleton _skeleton;
	readonly MeshBVH _bvh;

	readonly Stack<WeightEdit> _done = new();
	readonly Stack<WeightEdit> _undone = new();

	// Live only between BeginStroke and EndStroke.
	WeightUndo _undo;
	Vec3 _lastSample;
	bool _stroking;

	public WeightPaintSession( PolyMesh mesh, SkinWeights weights, Skeleton skeleton,
		WeightPaintLayer layer = null )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		_weights = weights ?? throw new ArgumentNullException( nameof( weights ) );
		_skeleton = skeleton ?? throw new ArgumentNullException( nameof( skeleton ) );

		if ( weights.Count != mesh.VertexCount )
			throw new ArgumentException( $"weights ({weights.Count}) and mesh ({mesh.VertexCount}) disagree" );

		// Built once. The mesh does not move while weights are painted, which is the one thing that
		// makes this cheaper than the sculpt session - there is no working copy and no refit.
		_bvh = MeshBVH.Build( mesh );

		// Reuse the document's layer when its topology still matches, so paint survives leaving
		// the tool and coming back. A stale layer is kept (see WeightPaintLayer) and a fresh one
		// is started rather than misapplied.
		if ( layer is not null && layer.CanApply( mesh, out _ ) )
			Layer = layer;
		else
			Layer = new WeightPaintLayer( mesh );
	}

	/// <summary>
	/// The heat-map atlas for the painted bone, rasterised through the mesh UVs. Not vertex
	/// colours — see <see cref="WeightRamp"/>.
	/// </summary>
	public PaintCanvas Ramp( int resolution = 512 ) =>
		WeightRamp.Bake( _mesh, Influence( Bone ), resolution );

	public PolyMesh Mesh => _mesh;
	public SkinWeights Weights => _weights;
	public Skeleton Skeleton => _skeleton;

	/// <summary>What was painted, in the form that survives a rebuild. See WeightPaintLayer.</summary>
	public WeightPaintLayer Layer { get; private set; }

	public WeightBrushKind Brush = WeightBrushKind.Add;
	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>Which bone the brush paints. -1 is nothing, and every kind but Smooth refuses.</summary>
	public int Bone = -1;

	/// <summary>
	/// Bones the brush must not change (roadmap J2, Backlog 5.5 MUST). Distinct from the
	/// single-influence vertices <see cref="LockedUnder"/> counts: those cannot move because
	/// they have nowhere to put the weight, while these are a deliberate user lock. A locked
	/// bone keeps its pre-stroke value on every vertex it touches, and the other influences
	/// rescale to fill what is left, so the partition of unity survives. Painting the locked
	/// bone itself is a no-op (see <see cref="Dab"/>).
	/// </summary>
	public HashSet<int> LockedBones = new();

	public void LockBone( int bone ) => LockedBones.Add( bone );

	public void UnlockBone( int bone ) => LockedBones.Remove( bone );

	public bool IsLocked( int bone ) => LockedBones.Contains( bone );

	/// <summary>Brush radius in world units, not pixels — the kernel has no screen.</summary>
	public float Radius = 0.25f;

	/// <summary>
	/// A starting radius that means something on THIS model. Effigy's units are dimensionless, so a
	/// constant is a brush that covers a whole prop and a pixel of a room - same reasoning as
	/// SculptSession's.
	/// </summary>
	public float SuggestedRadius => MathF.Max( _mesh.BoundsDiagonal * 0.08f, 1e-4f );

	/// <summary>
	/// How much one dab moves the weight. Low on purpose: weight painting is built out of many light
	/// passes, and a brush that takes a vertex to 1.0 in one dab cannot be used to blend anything.
	/// </summary>
	public float Strength = 0.15f;

	/// <summary>Where Set is heading, when the brush is Set.</summary>
	public float Target = 1f;

	/// <summary>Which origin plane every sample mirrors across, or none. Shared enum — see
	/// <see cref="MirrorAxis"/>.</summary>
	public MirrorAxis Mirror;

	/// <summary>Optional per-vertex paint mask: 0 skips a vertex, 1 paints it fully.</summary>
	public float[] Mask;

	/// <summary>Fraction of the radius the cursor must travel to earn another sample.</summary>
	public float Spacing = 0.25f;

	/// <summary>Most samples one pointer move may become, so a flick across the model cannot stall
	/// the tool.</summary>
	public int MaxSamplesPerMove = 64;

	public bool IsStroking => _stroking;

	/// <summary>Where the cursor sits on the surface, or null if the ray missed. The editor draws
	/// its ring here; nothing about it changes the weights.</summary>
	public MeshHit? Hover( Vec3 origin, Vec3 direction ) =>
		_bvh.Raycast( _mesh, origin, direction.Normal );

	public bool CanUndo => _done.Count > 0;
	public bool CanRedo => _undone.Count > 0;

	/// <summary>
	/// How much of the painted bone each vertex carries, for a viewport that colours the model by
	/// weight. Nothing else in the kernel can produce this, and every tool that paints weights shows
	/// it — painting blind is not painting.
	/// </summary>
	public float[] Influence( int bone )
	{
		var values = new float[_mesh.VertexCount];

		if ( bone < 0 )
			return values;

		for ( var v = 0; v < _mesh.VertexCount; v++ )
		{
			foreach ( var w in _weights[v] )
			{
				if ( w.Bone != bone )
					continue;

				values[v] = w.Weight;
				break;
			}
		}

		return values;
	}

	/// <summary>
	/// Start a stroke at whatever the ray hits. False when it misses, which is an ordinary outcome
	/// and not an error.
	/// </summary>
	public bool BeginStroke( Vec3 origin, Vec3 direction )
	{
		if ( _stroking )
			throw new InvalidOperationException( "A stroke is already running; end it before starting another." );

		if ( Radius <= 0f )
			throw new InvalidOperationException( $"A brush needs a radius; this one is {Radius}." );

		if ( Brush != WeightBrushKind.Smooth && Bone < 0 )
			throw new InvalidOperationException( "Pick a bone to paint before painting." );

		var hit = _bvh.Raycast( _mesh, origin, direction.Normal );

		if ( hit is null )
			return false;

		_stroking = true;
		_undo = new WeightUndo();
		_lastSample = hit.Value.Point;

		Dab( hit.Value.Point );
		return true;
	}

	/// <summary>
	/// Carry the stroke to a new pointer position, filling in the gap the pointer skipped. Returns
	/// how many samples that took.
	///
	/// A ray that misses does NOT end the stroke: dragging off the silhouette and back on is
	/// ordinary, and ending there makes the tool feel like it drops the gesture.
	/// </summary>
	public int MoveTo( Vec3 origin, Vec3 direction )
	{
		if ( !_stroking )
			throw new InvalidOperationException( "No stroke is running." );

		var hit = _bvh.Raycast( _mesh, origin, direction.Normal );

		if ( hit is null )
			return 0;

		var target = hit.Value.Point;
		var travelled = (target - _lastSample).Length;
		var spacing = MathF.Max( Radius * Spacing, 1e-6f );

		if ( travelled < spacing )
			return 0;

		var steps = Math.Min( (int)(travelled / spacing), MaxSamplesPerMove );
		var from = _lastSample;

		for ( var i = 1; i <= steps; i++ )
			Dab( from + (target - from) * ((float)i / steps) );

		_lastSample = target;
		return steps;
	}

	/// <summary>
	/// Finish the stroke, commit it as one edit, and write what it painted into the layer. Null when
	/// the stroke changed nothing — a click at zero strength is not worth an undo entry.
	/// </summary>
	public WeightEdit EndStroke()
	{
		if ( !_stroking )
			throw new InvalidOperationException( "No stroke is running." );

		var undo = _undo;

		_stroking = false;
		_undo = null;

		if ( undo.Count == 0 )
			return null;

		var before = new Dictionary<int, BoneWeight[]>( undo.Count );
		var after = new Dictionary<int, BoneWeight[]>( undo.Count );

		foreach ( var (vertex, previous) in undo.Previous )
		{
			before[vertex] = previous;
			after[vertex] = _weights[vertex];

			Layer.Capture( vertex, _weights[vertex], _skeleton );
		}

		var edit = new WeightEdit( before, after );

		_done.Push( edit );
		_undone.Clear();

		return edit;
	}

	public bool Undo() => Step( _done, _undone, forward: false );

	public bool Redo() => Step( _undone, _done, forward: true );

	bool Step( Stack<WeightEdit> from, Stack<WeightEdit> to, bool forward )
	{
		if ( _stroking || from.Count == 0 )
			return false;

		var edit = from.Pop();

		edit.Apply( _weights, forward );

		// THE LAYER GOES BACK TOO. Undoing the weights and leaving the paint recorded would look
		// right until the next rebuild put the undone stroke back on - a bug that only appears
		// several actions later and gets blamed on the rebuild.
		foreach ( var vertex in edit.Vertices )
			Layer.Capture( vertex, _weights[vertex], _skeleton );

		to.Push( edit );
		return true;
	}

	/// <summary>
	/// Take the paint off a set of vertices so the auto weights show through again — the "reset this
	/// bit" every paint tool needs, and the only way back to the binder's own answer short of
	/// clearing everything.
	///
	/// It is NOT an undo: the auto weights are recomputed by the caller, so this only forgets the
	/// paint. Committed as an edit so it is undoable like a stroke.
	/// </summary>
	public WeightEdit ClearPaint( IEnumerable<int> vertices, SkinWeights auto )
	{
		if ( _stroking )
			throw new InvalidOperationException( "A stroke is running." );

		if ( auto is null )
			throw new ArgumentNullException( nameof( auto ) );

		if ( auto.Count != _weights.Count )
			throw new ArgumentException( $"auto weights ({auto.Count}) and the mesh ({_weights.Count}) disagree" );

		var before = new Dictionary<int, BoneWeight[]>();
		var after = new Dictionary<int, BoneWeight[]>();

		foreach ( var vertex in vertices )
		{
			if ( vertex < 0 || vertex >= _weights.Count || !Layer.Clear( vertex ) )
				continue;

			before[vertex] = _weights[vertex];
			_weights[vertex] = auto[vertex];
			_weights[vertex] = RestoreLocked( before[vertex], _weights[vertex], LockedBones );
			after[vertex] = _weights[vertex];
		}

		if ( before.Count == 0 )
			return null;

		var edit = new WeightEdit( before, after );

		_done.Push( edit );
		_undone.Clear();

		return edit;
	}

	/// <summary>
	/// How many vertices under the current brush cannot move because the painted bone is their only
	/// influence. Asked before painting, so the tool can say why a stroke is about to do nothing.
	/// </summary>
	public int LockedUnder( Vec3 point )
	{
		var stroke = NewStroke();
		stroke.Samples.Add( new WeightSample( point, Radius, Strength ) );

		return WeightBrush.CountLocked( _mesh, _weights, stroke, _bvh );
	}

	void Dab( Vec3 point )
	{
		// A locked bone is not painted at all: painting it and undoing it would still move
		// the other bones shares through renormalisation, so the stroke is skipped outright.
		if ( Brush != WeightBrushKind.Smooth && LockedBones.Contains( Bone ) )
			return;

		var stroke = NewStroke();
		stroke.Samples.Add( new WeightSample( point, Radius, Strength ) );

		var sampleUndo = WeightBrush.Apply( _mesh, _weights, stroke, mask: Mask, _bvh );

		_undo.Absorb( sampleUndo );

		// Smooth has no one bone, so it can move a locked bone's share; put it back.
		if ( LockedBones.Count > 0 )
			foreach ( var (vertex, previous) in sampleUndo.Previous )
				_weights[vertex] = RestoreLocked( previous, _weights[vertex], LockedBones );
	}

	/// <summary>
	/// A vertex's influences with every locked bone reset to its pre-stroke value (0 when it
	/// had none) and the rest rescaled to fill what is left. The partition of unity survives
	/// because the rescale is proportional, the same invariant <see cref="WeightBrush"/> keeps.
	/// </summary>
	static BoneWeight[] RestoreLocked( BoneWeight[] before, BoneWeight[] current, HashSet<int> locked )
	{
		if ( locked is null || locked.Count == 0 )
			return current;

		var lockedBefore = new Dictionary<int, float>();

		foreach ( var w in before )
			if ( locked.Contains( w.Bone ) )
				lockedBefore[w.Bone] = w.Weight;

		var result = new Dictionary<int, float>();
		var lockedSum = 0f;

		foreach ( var w in current )
		{
			if ( !locked.Contains( w.Bone ) )
				continue;

			var v = lockedBefore.TryGetValue( w.Bone, out var pv ) ? pv : 0f;

			if ( v > 0f )
			{
				result[w.Bone] = v;
				lockedSum += v;
			}
		}

		foreach ( var (bone, v) in lockedBefore )
		{
			if ( v > 0f && !result.ContainsKey( bone ) )
			{
				result[bone] = v;
				lockedSum += v;
			}
		}

		if ( lockedSum >= 1f )
			return SkinWeights.Finish( result );

		var rest = 0f;

		foreach ( var w in current )
			if ( !locked.Contains( w.Bone ) )
				rest += w.Weight;

		var scale = rest > 0f ? (1f - lockedSum) / rest : 0f;

		foreach ( var w in current )
		{
			if ( locked.Contains( w.Bone ) || w.Weight <= 0f )
				continue;

			result[w.Bone] = w.Weight * scale;
		}

		return SkinWeights.Finish( result );
	}

	WeightStroke NewStroke() => new()
	{
		Kind = Brush,
		Falloff = Falloff,
		Bone = Bone,
		Target = Target,
		Mirror = Mirror,
	};
}
