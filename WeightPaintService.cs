#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

public enum WeightPaintTool
{
	/// <summary>Raise the selected bone by falloff × strength.</summary>
	Add,
	/// <summary>Lower the selected bone by falloff × strength.</summary>
	Subtract,
	/// <summary>Ease the selected bone toward <see cref="WeightPaintService.Value"/>; strength 1 with Constant falloff sets it exactly.</summary>
	Replace,
	/// <summary>Move every unlocked influence toward the neighbour average (blur/smooth). Bone independent.</summary>
	Blur,
}

/// <summary>Where a paint ray met the mesh, in bind-pose world space (kernel inches, Z-up).</summary>
public readonly record struct WeightPaintHit( Vector3 Point, Vector3 Normal, int Face, float Distance );

/// <summary>
/// Weight painting backend: brush/fill/gradient on per-vertex skin weights for one selected bone,
/// auto-normalised across the other bones with locked bones preserved, optional X mirror with L/R
/// bone-name mapping, and one <see cref="EditHistory"/> command per stroke that restores the exact
/// prior weights.
///
/// Operates on <c>mesh.Skin</c> in place (the same object the exporters and binders read). The mesh
/// geometry is never written. Picking is done in the BIND pose against the kernel BVH, the same
/// triangle test MeshPicker performs; posed-mesh painting is not supported (see limitations in the
/// ticket report).
///
/// Normalisation rule (one definition, <see cref="SetWeight"/>): locked bones keep their exact
/// weight L; the painted bone is clamped to [0, 1−L]; the remaining unlocked bones are rescaled
/// proportionally to fill 1−L−w. A vertex whose only unlocked influence is the painted bone cannot
/// be lowered (there is nowhere to put the weight) and is left unchanged and counted in
/// <see cref="LastStuckVertices"/> — the kernel <see cref="WeightBrush"/> posture.
/// </summary>
public sealed class WeightPaintService
{
	readonly PolyMesh _mesh;
	readonly Skeleton _skeleton;
	readonly EditHistory _history;
	readonly MeshBVH _bvh;
	readonly List<EdgeKey>[] _neighbours;
	readonly List<int> _found = new();
	int[]? _mirrorVertex;

	// Live stroke state.
	Dictionary<int, BoneWeight[]>? _strokeBefore;
	WeightPaintTool _strokeTool;
	int _strokeStuck;

	int _bone;
	float _radius = 1f, _strength = 0.5f, _value = 1f;

	public WeightPaintService( PolyMesh mesh, Skeleton skeleton, EditHistory history )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		_skeleton = skeleton ?? throw new ArgumentNullException( nameof( skeleton ) );
		_history = history ?? throw new ArgumentNullException( nameof( history ) );
		if ( skeleton.Count == 0 ) throw new ArgumentException( "Weight painting needs a skeleton with bones.", nameof( skeleton ) );
		if ( mesh.VertexCount == 0 ) throw new ArgumentException( "Weight painting needs a mesh with vertices.", nameof( mesh ) );
		if ( mesh.Skin is null ) throw new ArgumentException( "The mesh has no skin weights; bind it first (AutoWeightService).", nameof( mesh ) );
		if ( mesh.Skin.Count != mesh.VertexCount ) throw new ArgumentException( $"Skin weights ({mesh.Skin.Count}) and mesh ({mesh.VertexCount}) disagree.", nameof( mesh ) );
		foreach ( var set in mesh.Skin.Vertices )
			foreach ( var w in set ?? Array.Empty<BoneWeight>() )
				if ( w.Bone < 0 || w.Bone >= skeleton.Count )
					throw new ArgumentException( $"Skin references bone {w.Bone}; the skeleton has {skeleton.Count}.", nameof( mesh ) );

		_bvh = MeshBVH.Build( mesh );
		_neighbours = mesh.BuildVertexEdges();
	}

	public PolyMesh Mesh => _mesh;
	public SkinWeights Weights => _mesh.Skin;

	/// <summary>Selected bone index. Setting it outside the skeleton throws.</summary>
	public int Bone
	{
		get => _bone;
		set
		{
			if ( value < 0 || value >= _skeleton.Count ) throw new ArgumentOutOfRangeException( nameof( Bone ), value, $"Bone must be in [0, {_skeleton.Count})." );
			_bone = value;
		}
	}

	public WeightPaintTool Tool = WeightPaintTool.Add;
	public BrushFalloff Falloff = BrushFalloff.Smooth;

	/// <summary>Brush radius in world units (inches).</summary>
	public float Radius
	{
		get => _radius;
		set { if ( !float.IsFinite( value ) || value <= 0f ) throw new ArgumentOutOfRangeException( nameof( Radius ), value, "Radius must be positive and finite." ); _radius = value; }
	}

	/// <summary>Per-dab amount, 0..1.</summary>
	public float Strength
	{
		get => _strength;
		set { if ( !float.IsFinite( value ) || value < 0f || value > 1f ) throw new ArgumentOutOfRangeException( nameof( Strength ), value, "Strength must be in [0, 1]." ); _strength = value; }
	}

	/// <summary>Target weight for Replace and Fill, 0..1.</summary>
	public float Value
	{
		get => _value;
		set { if ( !float.IsFinite( value ) || value < 0f || value > 1f ) throw new ArgumentOutOfRangeException( nameof( Value ), value, "Value must be in [0, 1]." ); _value = value; }
	}

	/// <summary>Bones whose weights painting/normalising must not change.</summary>
	public HashSet<int> LockedBones { get; } = new();

	/// <summary>Mirror every operation across the X = 0 plane, painting the L/R counterpart bone (by name).</summary>
	public bool MirrorX;

	/// <summary>Distance within which a mirrored vertex position counts as the counterpart vertex.</summary>
	public float MirrorTolerance = 1e-3f;

	public bool IsStroking => _strokeBefore is not null;

	/// <summary>Vertices the last committed operation could not change because the painted bone was
	/// their only unlocked influence (or every other influence is locked).</summary>
	public int LastStuckVertices { get; private set; }

	/// <summary>Bones the last operation painted (selected bone, plus its mirror when mirroring).</summary>
	public IReadOnlyList<int> LastPaintedBones { get; private set; } = Array.Empty<int>();

	// ---------------------------------------------------------------- picking

	/// <summary>Nearest bind-pose triangle hit along a ray, or null on a miss.</summary>
	public WeightPaintHit? Hit( Vector3 origin, Vector3 direction )
	{
		RequireFinite( origin, nameof( origin ) );
		RequireFinite( direction, nameof( direction ) );
		if ( direction.LengthSquared() < 1e-20f ) throw new ArgumentException( "Ray direction is zero.", nameof( direction ) );
		var d = Vector3.Normalize( direction );
		var hit = _bvh.Raycast( _mesh, ToVec( origin ), ToVec( d ) );
		if ( hit is not { } h ) return null;
		return new WeightPaintHit( ToVector( h.Point ), ToVector( h.Normal ), h.FaceIndex, h.Distance );
	}

	// ---------------------------------------------------------------- strokes

	public void BeginStroke()
	{
		if ( IsStroking ) throw new InvalidOperationException( "A stroke is already running." );
		if ( _history.CurrentTransaction is not null ) throw new InvalidOperationException( "An edit transaction is active." );
		if ( !Enum.IsDefined( Tool ) ) throw new InvalidOperationException( $"Unknown weight tool {(int)Tool}." );
		if ( Tool != WeightPaintTool.Blur ) RequirePaintableBone();
		_strokeBefore = new Dictionary<int, BoneWeight[]>();
		_strokeTool = Tool;
		_strokeStuck = 0;
	}

	/// <summary>One brush dab at a world-space point (usually <see cref="Hit"/>.Point). Returns vertices changed.</summary>
	public int Dab( Vector3 center )
	{
		if ( _strokeBefore is null ) throw new InvalidOperationException( "No stroke is running; call BeginStroke first." );
		RequireFinite( center, nameof( center ) );
		int changed = DabSide( ToVec( center ), Bone );
		int? mirror = !MirrorX ? null : _strokeTool == WeightPaintTool.Blur ? Bone : MirrorBone( Bone );
		if ( mirror is int m )
			changed += DabSide( new Vec3( -center.X, center.Y, center.Z ), m );
		return changed;
	}

	/// <summary>Commit the stroke as one history command. False (and no command) when nothing changed.</summary>
	public bool EndStroke()
	{
		if ( _strokeBefore is null ) throw new InvalidOperationException( "No stroke is running." );
		var before = _strokeBefore;
		_strokeBefore = null;
		LastStuckVertices = _strokeStuck;
		LastPaintedBones = PaintedBones( _strokeTool );
		return Commit( $"Weight paint ({_strokeTool})", before );
	}

	/// <summary>Abandon the stroke, restoring the exact weights from before it began. No history entry.</summary>
	public void CancelStroke()
	{
		if ( _strokeBefore is null ) return;
		foreach ( var (v, w) in _strokeBefore ) _mesh.Skin[v] = (BoneWeight[])w.Clone();
		_strokeBefore = null;
	}

	/// <summary>Begin, dab at every point, end. Returns whether a command was committed.</summary>
	public bool Stroke( IEnumerable<Vector3> centers )
	{
		ArgumentNullException.ThrowIfNull( centers );
		BeginStroke();
		try { foreach ( var c in centers ) Dab( c ); }
		catch { CancelStroke(); throw; }
		return EndStroke();
	}

	// ---------------------------------------------------------------- whole-set tools

	/// <summary>Set the selected bone to <paramref name="value"/> on the given vertices (all when null). One command.</summary>
	public bool Fill( float value, IEnumerable<int>? vertices = null )
	{
		if ( !float.IsFinite( value ) || value < 0f || value > 1f ) throw new ArgumentOutOfRangeException( nameof( value ), value, "Fill value must be in [0, 1]." );
		RequireIdle();
		RequirePaintableBone();
		var list = VertexList( vertices );
		var before = new Dictionary<int, BoneWeight[]>();
		int stuck = 0;
		foreach ( int v in list ) stuck += SetVertex( v, Bone, value, before );
		if ( MirrorX && MirrorBone( Bone ) is int mirror )
		{
			var map = MirrorVertices();
			foreach ( int v in list )
				if ( map[v] >= 0 ) stuck += SetVertex( map[v], mirror, value, before );
		}
		LastStuckVertices = stuck;
		LastPaintedBones = PaintedBones( WeightPaintTool.Replace );
		return Commit( "Weight fill", before );
	}

	/// <summary>
	/// Linear gradient: each vertex's selected-bone weight becomes lerp(valueA, valueB, t), t the
	/// clamped projection of the vertex onto segment a→b. Applied to the given vertices (all when null). One command.
	/// </summary>
	public bool Gradient( Vector3 a, Vector3 b, float valueA = 0f, float valueB = 1f, IEnumerable<int>? vertices = null )
	{
		RequireFinite( a, nameof( a ) );
		RequireFinite( b, nameof( b ) );
		if ( (b - a).LengthSquared() < 1e-12f ) throw new ArgumentException( "Gradient start and end are the same point." );
		if ( !float.IsFinite( valueA ) || valueA < 0f || valueA > 1f ) throw new ArgumentOutOfRangeException( nameof( valueA ) );
		if ( !float.IsFinite( valueB ) || valueB < 0f || valueB > 1f ) throw new ArgumentOutOfRangeException( nameof( valueB ) );
		RequireIdle();
		RequirePaintableBone();
		var list = VertexList( vertices );
		var before = new Dictionary<int, BoneWeight[]>();
		int stuck = 0;
		foreach ( int v in list ) stuck += SetVertex( v, Bone, GradientValue( _mesh.Positions[v], ToVec( a ), ToVec( b ), valueA, valueB ), before );
		if ( MirrorX && MirrorBone( Bone ) is int mirror )
		{
			var map = MirrorVertices();
			Vec3 ma = new( -a.X, a.Y, a.Z ), mb = new( -b.X, b.Y, b.Z );
			foreach ( int v in list )
				if ( map[v] >= 0 ) stuck += SetVertex( map[v], mirror, GradientValue( _mesh.Positions[map[v]], ma, mb, valueA, valueB ), before );
		}
		LastStuckVertices = stuck;
		LastPaintedBones = PaintedBones( WeightPaintTool.Replace );
		return Commit( "Weight gradient", before );
	}

	/// <summary>Renormalise the given vertices (all when null) so unlocked bones fill 1 − locked. One command.</summary>
	public bool Normalize( IEnumerable<int>? vertices = null )
	{
		RequireIdle();
		var before = new Dictionary<int, BoneWeight[]>();
		foreach ( int v in VertexList( vertices ) )
		{
			var current = _mesh.Skin[v];
			var after = NormalizeWeights( current, LockedBones );
			if ( after is null || Same( current, after ) ) continue;
			before.TryAdd( v, current );
			_mesh.Skin[v] = after;
		}
		LastStuckVertices = 0;
		LastPaintedBones = Array.Empty<int>();
		return Commit( "Normalize weights", before );
	}

	// ---------------------------------------------------------------- display

	/// <summary>Selected-bone weight per vertex (0 when the bone does not influence it).</summary>
	public float[] Influence( int bone ) => Influence( _mesh.Skin, bone );

	public static float[] Influence( SkinWeights weights, int bone )
	{
		ArgumentNullException.ThrowIfNull( weights );
		var values = new float[weights.Count];
		for ( int v = 0; v < values.Length; v++ ) values[v] = WeightOf( weights[v], bone );
		return values;
	}

	/// <summary>Per-vertex RGBA (4 floats per vertex, 0..1, alpha 1), blue at 0 → cyan → green → yellow → red at 1,
	/// for the given bone. Ready to upload as a vertex colour stream (GPU vertex = mesh vertex).</summary>
	public float[] Colors( int bone ) => Colors( _mesh.Skin, bone );

	public static float[] Colors( SkinWeights weights, int bone )
	{
		ArgumentNullException.ThrowIfNull( weights );
		var rgba = new float[weights.Count * 4];
		for ( int v = 0; v < weights.Count; v++ )
		{
			var c = WeightColor( WeightOf( weights[v], bone ) );
			rgba[v * 4] = c.X; rgba[v * 4 + 1] = c.Y; rgba[v * 4 + 2] = c.Z; rgba[v * 4 + 3] = 1f;
		}
		return rgba;
	}

	/// <summary>The kernel weight ramp (<see cref="WeightRamp.Color"/>) as 0..1 floats.</summary>
	public static Vector3 WeightColor( float weight )
	{
		WeightRamp.Color( float.IsFinite( weight ) ? weight : 0f, out var r, out var g, out var b );
		return new Vector3( r / 255f, g / 255f, b / 255f );
	}

	// ---------------------------------------------------------------- mirror

	/// <summary>The L/R counterpart of a bone name, or null when it has no side marker.
	/// Handles _L/_R, .L/.R, -L/-R, L_/R_ prefixes, and Left/Right words (case kept).</summary>
	public static string? MirrorBoneName( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return null;
		foreach ( var (l, r) in new[] { ("Left", "Right"), ("left", "right"), ("LEFT", "RIGHT") } )
		{
			if ( name.Contains( l, StringComparison.Ordinal ) ) return name.Replace( l, r, StringComparison.Ordinal );
			if ( name.Contains( r, StringComparison.Ordinal ) ) return name.Replace( r, l, StringComparison.Ordinal );
		}
		static char Swap( char c ) => c switch { 'L' => 'R', 'R' => 'L', 'l' => 'r', 'r' => 'l', _ => c };
		static bool Side( char c ) => c is 'L' or 'R' or 'l' or 'r';
		static bool Sep( char c ) => c is '_' or '.' or '-' or ' ';
		int n = name.Length;
		if ( n >= 2 && Side( name[n - 1] ) && Sep( name[n - 2] ) ) return name[..(n - 1)] + Swap( name[n - 1] );
		if ( n >= 2 && Side( name[0] ) && Sep( name[1] ) ) return Swap( name[0] ) + name[1..];
		return null;
	}

	/// <summary>The bone to paint on the mirrored side: the L/R counterpart if it exists, else the same bone
	/// (a centre bone such as a spine). Null when the counterpart is locked.</summary>
	public int? MirrorBone( int bone )
	{
		if ( bone < 0 || bone >= _skeleton.Count ) throw new ArgumentOutOfRangeException( nameof( bone ) );
		int target = bone;
		var other = MirrorBoneName( _skeleton.Bones[bone].Name );
		if ( other is not null )
		{
			int index = _skeleton.IndexOf( other );
			if ( index >= 0 ) target = index;
		}
		return LockedBones.Contains( target ) ? null : target;
	}

	/// <summary>For each vertex, the vertex at its X-mirrored position (within <see cref="MirrorTolerance"/>), or -1.</summary>
	public int[] MirrorVertices()
	{
		if ( _mirrorVertex is not null ) return _mirrorVertex;
		int n = _mesh.VertexCount;
		var map = new int[n];
		float cell = Math.Max( MirrorTolerance, 1e-6f ) * 2f;
		var grid = new Dictionary<(long, long, long), List<int>>();
		(long, long, long) Key( Vec3 p ) => ((long)MathF.Floor( p.x / cell ), (long)MathF.Floor( p.y / cell ), (long)MathF.Floor( p.z / cell ));
		for ( int v = 0; v < n; v++ )
		{
			var k = Key( _mesh.Positions[v] );
			if ( !grid.TryGetValue( k, out var list ) ) grid[k] = list = new List<int>();
			list.Add( v );
		}
		for ( int v = 0; v < n; v++ )
		{
			var p = _mesh.Positions[v];
			var q = new Vec3( -p.x, p.y, p.z );
			var (kx, ky, kz) = Key( q );
			int best = -1; float bestD = MirrorTolerance * MirrorTolerance;
			for ( long dx = -1; dx <= 1; dx++ ) for ( long dy = -1; dy <= 1; dy++ ) for ( long dz = -1; dz <= 1; dz++ )
			{
				if ( !grid.TryGetValue( (kx + dx, ky + dy, kz + dz), out var list ) ) continue;
				foreach ( int c in list )
				{
					float d = (_mesh.Positions[c] - q).LengthSquared;
					if ( d <= bestD ) { bestD = d; best = c; }
				}
			}
			map[v] = best;
		}
		return _mirrorVertex = map;
	}

	// ---------------------------------------------------------------- normalisation (pure)

	/// <summary>
	/// <paramref name="weights"/> with <paramref name="bone"/> moved to <paramref name="target"/>:
	/// locked bones keep their exact weight, the bone is clamped to [0, 1 − locked], the other
	/// unlocked bones are rescaled to fill the rest. The result sums to 1 (given locked ≤ 1).
	/// Null when it cannot be done (lowering a bone that has no unlocked partner to take the weight).
	/// Throws when <paramref name="bone"/> itself is locked.
	/// </summary>
	public static BoneWeight[]? SetWeight( BoneWeight[]? weights, int bone, float target, IReadOnlySet<int>? locked = null )
	{
		if ( bone < 0 ) throw new ArgumentOutOfRangeException( nameof( bone ) );
		if ( !float.IsFinite( target ) ) throw new ArgumentOutOfRangeException( nameof( target ) );
		if ( locked is not null && locked.Contains( bone ) ) throw new InvalidOperationException( $"Bone {bone} is locked." );
		weights ??= Array.Empty<BoneWeight>();

		var acc = Merge( weights );
		float lockedSum = 0f, others = 0f;
		foreach ( var (b, w) in acc )
		{
			if ( b == bone ) continue;
			if ( locked is not null && locked.Contains( b ) ) lockedSum += w;
			else others += w;
		}
		float available = Math.Max( 0f, 1f - lockedSum );
		target = Math.Clamp( target, 0f, available );

		if ( acc.Count == 0 )
			return target > 1e-6f ? new[] { new BoneWeight( bone, 1f ) } : null;
		if ( others <= 1e-6f )
		{
			// Nothing unlocked to give to or take from: only "bone fills the whole remainder" is valid.
			if ( target < available - 1e-6f ) return null;
			target = available;
		}

		var result = new List<BoneWeight>( acc.Count + 1 );
		float scale = others > 1e-9f ? (available - target) / others : 0f;
		if ( target > 1e-6f ) result.Add( new BoneWeight( bone, target ) );
		foreach ( var (b, w) in acc )
		{
			if ( b == bone ) continue;
			bool isLocked = locked is not null && locked.Contains( b );
			float value = isLocked ? w : w * scale;
			if ( isLocked || value > 1e-6f ) result.Add( new BoneWeight( b, value ) );
		}
		if ( result.Count == 0 ) return null;
		result.Sort( ( x, y ) => y.Weight != x.Weight ? y.Weight.CompareTo( x.Weight ) : x.Bone.CompareTo( y.Bone ) );
		return result.ToArray();
	}

	/// <summary>Rescale unlocked influences to 1 − locked; locked keep exact values. Null when impossible (no unlocked weight).</summary>
	public static BoneWeight[]? NormalizeWeights( BoneWeight[]? weights, IReadOnlySet<int>? locked = null )
	{
		if ( weights is null || weights.Length == 0 ) return null;
		var acc = Merge( weights );
		float lockedSum = 0f, others = 0f;
		foreach ( var (b, w) in acc )
			if ( locked is not null && locked.Contains( b ) ) lockedSum += w; else others += w;
		if ( others <= 1e-9f ) return null;
		float scale = Math.Max( 0f, 1f - lockedSum ) / others;
		var result = new List<BoneWeight>( acc.Count );
		foreach ( var (b, w) in acc )
		{
			bool isLocked = locked is not null && locked.Contains( b );
			float value = isLocked ? w : w * scale;
			if ( isLocked || value > 1e-6f ) result.Add( new BoneWeight( b, value ) );
		}
		result.Sort( ( x, y ) => y.Weight != x.Weight ? y.Weight.CompareTo( x.Weight ) : x.Bone.CompareTo( y.Bone ) );
		return result.ToArray();
	}

	// ---------------------------------------------------------------- internals

	int DabSide( Vec3 center, int bone )
	{
		_bvh.VerticesInRadius( _mesh, center, _radius, _found );
		if ( _found.Count == 0 ) return 0;

		var targets = new List<(int Vertex, BoneWeight[] After)>( _found.Count );
		foreach ( int v in _found )
		{
			float t = (_mesh.Positions[v] - center).Length / _radius;
			float amount = Brush.Falloff( t, Falloff ) * _strength;
			if ( amount <= 1e-7f ) continue;
			var current = _mesh.Skin[v];
			BoneWeight[]? after;
			if ( _strokeTool == WeightPaintTool.Blur )
				after = BlurOne( v, amount );
			else
			{
				float cur = WeightOf( current, bone );
				float target = _strokeTool switch
				{
					WeightPaintTool.Add => cur + amount,
					WeightPaintTool.Subtract => cur - amount,
					_ => cur + (_value - cur) * amount,
				};
				after = SetWeight( current, bone, target, LockedBones );
				if ( after is null && MathF.Abs( target - cur ) > 1e-6f ) _strokeStuck++;
			}
			if ( after is null || Same( current, after ) ) continue;
			targets.Add( (v, after) );
		}
		// Jacobi: blur reads the pre-dab neighbours, so the result does not depend on vertex order.
		foreach ( var (v, after) in targets )
		{
			_strokeBefore!.TryAdd( v, _mesh.Skin[v] );
			_mesh.Skin[v] = after;
		}
		return targets.Count;
	}

	BoneWeight[]? BlurOne( int v, float amount )
	{
		var edges = _neighbours[v];
		if ( edges.Count == 0 ) return null;
		amount = Math.Clamp( amount, 0f, 1f );
		var current = _mesh.Skin[v];
		var acc = new Dictionary<int, float>();
		foreach ( var (b, w) in Merge( current ) ) acc[b] = w * (1f - amount);
		float share = amount / edges.Count;
		foreach ( var e in edges )
			foreach ( var (b, w) in Merge( _mesh.Skin[e.A == v ? e.B : e.A] ) )
				acc[b] = acc.GetValueOrDefault( b ) + w * share;

		// Locked bones keep exactly what this vertex had.
		var mixed = new List<BoneWeight>();
		var own = Merge( current );
		foreach ( int b in LockedBones )
			if ( own.TryGetValue( b, out var w ) ) mixed.Add( new BoneWeight( b, w ) );
		foreach ( var (b, w) in acc )
			if ( !LockedBones.Contains( b ) && w > 0f ) mixed.Add( new BoneWeight( b, w ) );
		return NormalizeWeights( mixed.ToArray(), LockedBones );
	}

	int SetVertex( int v, int bone, float value, Dictionary<int, BoneWeight[]> before )
	{
		var current = _mesh.Skin[v];
		var after = SetWeight( current, bone, value, LockedBones );
		if ( after is null ) return MathF.Abs( WeightOf( current, bone ) - value ) > 1e-6f ? 1 : 0;
		if ( Same( current, after ) ) return 0;
		before.TryAdd( v, current );
		_mesh.Skin[v] = after;
		return 0;
	}

	bool Commit( string description, Dictionary<int, BoneWeight[]> before )
	{
		if ( before.Count == 0 ) return false;
		var undo = before.ToDictionary( kv => kv.Key, kv => (BoneWeight[])kv.Value.Clone() );
		var redo = before.Keys.ToDictionary( v => v, v => (BoneWeight[])_mesh.Skin[v].Clone() );
		var mesh = _mesh;
		void Write( Dictionary<int, BoneWeight[]> source )
		{
			if ( mesh.Skin is null || mesh.Skin.Count != mesh.VertexCount )
				throw new InvalidOperationException( "The mesh's skin was replaced or resized; this weight edit no longer applies." );
			foreach ( var (v, w) in source ) mesh.Skin[v] = (BoneWeight[])w.Clone();
		}
		_history.Execute( new DelegateEditCommand( description, () => Write( redo ), () => Write( undo ) ) );
		return true;
	}

	IReadOnlyList<int> PaintedBones( WeightPaintTool tool )
	{
		if ( tool == WeightPaintTool.Blur ) return Array.Empty<int>();
		if ( MirrorX && MirrorBone( Bone ) is int m && m != Bone ) return new[] { Bone, m };
		return new[] { Bone };
	}

	List<int> VertexList( IEnumerable<int>? vertices )
	{
		if ( vertices is null ) return Enumerable.Range( 0, _mesh.VertexCount ).ToList();
		var list = vertices.Distinct().ToList();
		foreach ( int v in list )
			if ( v < 0 || v >= _mesh.VertexCount ) throw new ArgumentOutOfRangeException( nameof( vertices ), v, $"Vertex index out of range [0, {_mesh.VertexCount})." );
		return list;
	}

	void RequireIdle()
	{
		if ( IsStroking ) throw new InvalidOperationException( "A stroke is running." );
		if ( _history.CurrentTransaction is not null ) throw new InvalidOperationException( "An edit transaction is active." );
	}

	void RequirePaintableBone()
	{
		if ( LockedBones.Contains( Bone ) ) throw new InvalidOperationException( $"Bone '{_skeleton.Bones[Bone].Name}' is locked; unlock it to paint." );
	}

	static float GradientValue( Vec3 p, Vec3 a, Vec3 b, float va, float vb )
	{
		var ab = b - a;
		float t = Math.Clamp( Vec3.Dot( p - a, ab ) / ab.LengthSquared, 0f, 1f );
		return va + (vb - va) * t;
	}

	static Dictionary<int, float> Merge( BoneWeight[] weights )
	{
		var acc = new Dictionary<int, float>( weights.Length );
		foreach ( var w in weights )
			if ( w.Weight > 0f && float.IsFinite( w.Weight ) )
				acc[w.Bone] = acc.GetValueOrDefault( w.Bone ) + w.Weight;
		return acc;
	}

	static float WeightOf( BoneWeight[]? weights, int bone )
	{
		if ( weights is null ) return 0f;
		float sum = 0f;
		foreach ( var w in weights ) if ( w.Bone == bone ) sum += w.Weight;
		return sum;
	}

	static bool Same( BoneWeight[]? a, BoneWeight[]? b )
	{
		if ( a is null || b is null || a.Length != b.Length ) return false;
		for ( int i = 0; i < a.Length; i++ )
			if ( a[i].Bone != b[i].Bone || MathF.Abs( a[i].Weight - b[i].Weight ) > 1e-7f ) return false;
		return true;
	}

	static void RequireFinite( Vector3 v, string name )
	{
		if ( !float.IsFinite( v.X ) || !float.IsFinite( v.Y ) || !float.IsFinite( v.Z ) ) throw new ArgumentException( "Vector is not finite.", name );
	}

	static Vec3 ToVec( Vector3 v ) => new( v.X, v.Y, v.Z );
	static Vector3 ToVector( Vec3 v ) => new( v.x, v.y, v.z );
}
