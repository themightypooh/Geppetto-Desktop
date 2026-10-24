#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>What a morph sculpt stroke does to the active target's deltas.</summary>
public enum MorphBrushOp
{
	/// <summary>Grab: add the dab's offset, weighted by falloff measured on the shape at stroke start.</summary>
	Move,

	/// <summary>Push along the shaped vertex normal by <see cref="MorphBrushSettings.InflateDistance"/> (signed, inches).</summary>
	Inflate,

	/// <summary>Relax deltas toward the average of their edge neighbours' deltas (positions are untouched).</summary>
	Smooth,

	/// <summary>Blend deltas toward zero, i.e. back to the neutral base shape.</summary>
	Erase
}

/// <summary>
/// Settings shared by every dab of one stroke. Radius and distances are kernel inches.
/// Strength is in [0, 1]. MirrorX applies the same dab mirrored across the X = 0 origin plane.
/// </summary>
public sealed record MorphBrushSettings(
	MorphBrushOp Op,
	float Radius,
	float Strength = 1f,
	BrushFalloff Falloff = BrushFalloff.Smooth,
	bool MirrorX = false,
	float InflateDistance = 0f );

/// <summary>
/// Shape key (morph target) authoring on a retained <see cref="PolyMesh"/>.
///
/// REPRESENTATION. Deltas live in <see cref="PolyMesh.Morphs"/> as one offset per SOURCE vertex,
/// exactly what <see cref="RetainedMeshMorphs.FromMesh"/> / <see cref="MorphEvaluator"/> (A2) and
/// the kernel FBX/DMX writers consume. Render corners (UV / hard-normal duplicates) receive the
/// delta through <see cref="CornerVertexMap"/>, so corner seams cannot diverge. Source vertices
/// that are separate indices but coincident in space (an imported mesh split along a seam) are
/// grouped at construction; every brush op computes one change per group and applies it to all
/// members, so a split seam stays closed.
///
/// UNDO. Every structural op and every committed stroke is one <see cref="EditHistory"/> entry.
/// Strokes record only the touched vertices (index, before, after) and undo/redo restore the
/// exact stored floats.
///
/// THREADING. Single-threaded: call from the thread that owns the document/session.
/// The base positions and faces must not change while this service is in use; every call checks
/// the vertex/face count and throws if they did.
/// </summary>
public sealed class MorphEditService
{
	/// <summary>Coincident-vertex grouping tolerance (inches) used for split-seam welding.</summary>
	public const float DefaultWeldTolerance = 1e-5f;

	readonly int _vertexCount;
	readonly int _faceCount;
	readonly int[] _groupOf;
	readonly int[][] _groupMembers;
	readonly int[][] _groupNeighbours;
	MeshMorph? _active;
	RetainedMeshMorphs? _cachePositions;
	RetainedMeshMorphs? _cacheWithNormals;
	MorphStroke? _openStroke;

	public PolyMesh Mesh { get; }
	public EditHistory History { get; }

	/// <summary>Incremented on every change to the morph set or any delta, including undo/redo.</summary>
	public int Revision { get; private set; }

	public int VertexCount => _vertexCount;

	/// <summary>Number of coincident-position vertex groups (equals VertexCount for an unsplit mesh).</summary>
	public int GroupCount => _groupMembers.Length;

	public MorphEditService( PolyMesh mesh, EditHistory history, float weldTolerance = DefaultWeldTolerance )
	{
		Mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		History = history ?? throw new ArgumentNullException( nameof( history ) );
		if ( !float.IsFinite( weldTolerance ) || weldTolerance < 0f )
			throw new ArgumentOutOfRangeException( nameof( weldTolerance ), "Weld tolerance must be finite and >= 0." );
		if ( mesh.Positions.Count == 0 )
			throw new ArgumentException( "Mesh has no vertices.", nameof( mesh ) );

		_vertexCount = mesh.Positions.Count;
		_faceCount = mesh.FaceCount;

		for ( int fi = 0; fi < mesh.FaceCount; fi++ )
			foreach ( var vi in mesh.Faces[fi].Indices )
				if ( vi < 0 || vi >= _vertexCount )
					throw new ArgumentException( $"Face {fi} references vertex {vi} outside 0..{_vertexCount - 1}.", nameof( mesh ) );

		(_groupOf, _groupMembers) = BuildGroups( mesh.Positions, weldTolerance );
		_groupNeighbours = BuildGroupNeighbours( mesh, _groupOf, _groupMembers.Length );
	}

	// --- Queries ---------------------------------------------------------------------------------

	/// <summary>Names of the editable (valid, not recording) targets in mesh order.</summary>
	public IReadOnlyList<string> TargetNames => Mesh.ValidMorphs.Select( m => m.Name ).ToList();

	public bool HasTarget( string name ) => name != null && FindValid( name ) != null;

	/// <summary>The target strokes edit when none is named; null when unset or deleted.</summary>
	public string? ActiveTarget => _active != null && IsLiveValid( _active ) ? _active.Name : null;

	/// <summary>Selects the active target. Selection is not an undoable edit.</summary>
	public void SetActiveTarget( string? name )
	{
		if ( name == null ) { _active = null; return; }
		_active = FindValid( name ) ?? throw new ArgumentException( $"No editable morph target named '{name}'.", nameof( name ) );
	}

	/// <summary>A copy of a target's per-source-vertex deltas.</summary>
	public Vec3[] GetDeltas( string name ) => (Vec3[])Require( name ).Deltas.Clone();

	/// <summary>Group index of a source vertex (coincident vertices share one).</summary>
	public int GroupOf( int vertex ) => _groupOf[vertex];

	// --- Structural ops (each one undo entry) ----------------------------------------------------

	/// <summary>Creates a target. Null deltas = neutral (all zero). Becomes active if none is.</summary>
	public void CreateTarget( string name, IReadOnlyList<Vec3>? deltas = null )
	{
		EnsureUsable();
		ValidateNewName( name, nameof( name ) );
		Vec3[] data;
		if ( deltas == null ) data = new Vec3[_vertexCount];
		else
		{
			if ( deltas.Count != _vertexCount )
				throw new ArgumentException( $"Deltas have {deltas.Count} entries, the mesh has {_vertexCount} vertices.", nameof( deltas ) );
			data = deltas.ToArray();
			for ( int i = 0; i < data.Length; i++ )
				if ( !IsFinite( data[i] ) )
					throw new ArgumentException( $"Delta {i} is not finite.", nameof( deltas ) );
		}

		AddTargetCommand( $"Create morph '{name}'", new MeshMorph { Name = name, Deltas = data } );
	}

	public void RenameTarget( string oldName, string newName )
	{
		EnsureUsable();
		var morph = Require( oldName );
		if ( string.Equals( oldName, newName, StringComparison.Ordinal ) ) return;
		ValidateNewName( newName, nameof( newName ) );

		History.Execute( new DelegateEditCommand( $"Rename morph '{oldName}' to '{newName}'",
			() => { morph.Name = newName; Touch(); },
			() => { morph.Name = oldName; Touch(); } ) );
	}

	public void DeleteTarget( string name )
	{
		EnsureUsable();
		var morph = Require( name );
		int index = Mesh.Morphs.IndexOf( morph );

		History.Execute( new DelegateEditCommand( $"Delete morph '{name}'",
			() => { Mesh.Morphs.Remove( morph ); Touch(); },
			() => { Mesh.Morphs.Insert( Math.Min( index, Mesh.Morphs.Count ), morph ); Touch(); } ) );
	}

	public void DuplicateTarget( string sourceName, string newName )
	{
		EnsureUsable();
		var source = Require( sourceName );
		ValidateNewName( newName, nameof( newName ) );
		AddTargetCommand( $"Duplicate morph '{sourceName}' as '{newName}'",
			new MeshMorph { Name = newName, Deltas = (Vec3[])source.Deltas.Clone(), Driver = source.Driver?.Clone() } );
	}

	/// <summary>
	/// Creates a new target whose deltas are the weighted sum of existing targets, i.e. the shape
	/// <see cref="Preview"/> shows at these weights minus the base. Weights pass through the policy
	/// (default clamp 0..1) exactly as preview does.
	/// </summary>
	public void BakeMix( string newName, IReadOnlyDictionary<string, float> weights, MorphPolicy? policy = null )
	{
		EnsureUsable();
		ValidateNewName( newName, nameof( newName ) );
		var applied = ResolveWeights( weights, policy );
		var data = new Vec3[_vertexCount];
		foreach ( var (morph, w) in applied )
			for ( int i = 0; i < _vertexCount; i++ )
				data[i] += morph.Deltas[i] * w;

		AddTargetCommand( $"Bake morph mix as '{newName}'", new MeshMorph { Name = newName, Deltas = data } );
	}

	// --- Sculpting --------------------------------------------------------------------------------

	/// <summary>
	/// Opens a stroke on <paramref name="target"/> (or the active target). Dabs edit the deltas live;
	/// <see cref="MorphStroke.Commit"/> records one undo entry; <see cref="MorphStroke.Cancel"/>
	/// restores the exact pre-stroke deltas. Only one stroke may be open at a time.
	/// </summary>
	public MorphStroke BeginStroke( MorphBrushSettings settings, string? target = null )
	{
		EnsureUsable();
		ArgumentNullException.ThrowIfNull( settings );
		ValidateSettings( settings );
		if ( _openStroke != null ) throw new InvalidOperationException( "A morph stroke is already open." );

		MeshMorph morph;
		if ( target != null ) morph = Require( target );
		else morph = (_active != null && IsLiveValid( _active )) ? _active : throw new InvalidOperationException( "No active morph target to sculpt." );

		_openStroke = new MorphStroke( this, morph, settings );
		return _openStroke;
	}

	/// <summary>Convenience: one complete stroke. Returns false if nothing changed (no undo entry).</summary>
	public bool ApplyStroke( MorphBrushSettings settings, IEnumerable<(Vec3 Center, Vec3 Offset)> dabs, string? target = null )
	{
		ArgumentNullException.ThrowIfNull( dabs );
		var stroke = BeginStroke( settings, target );
		try
		{
			foreach ( var (c, o) in dabs ) stroke.Dab( c, o );
		}
		catch
		{
			stroke.Cancel();
			throw;
		}
		return stroke.Commit();
	}

	// --- Preview ----------------------------------------------------------------------------------

	/// <summary>The current targets as A2's retained representation (cached until the next edit).</summary>
	public RetainedMeshMorphs Retained( bool withNormalDeltas = false )
	{
		EnsureTopology();
		if ( withNormalDeltas )
			return _cacheWithNormals ??= RetainedMeshMorphs.FromMesh( Mesh, computeNormalDeltas: true );
		return _cachePositions ??= RetainedMeshMorphs.FromMesh( Mesh, computeNormalDeltas: false );
	}

	/// <summary>A2 evaluator over the current targets, for viewport integration.</summary>
	public MorphEvaluator CreateEvaluator( CornerVertexMap? cornerMap = null, MorphPolicy? policy = null ) =>
		new( Retained( withNormalDeltas: cornerMap != null ), cornerMap, policy );

	/// <summary>
	/// Base + weighted combination of targets, through <see cref="MorphEvaluator.DeformPositions(ReadOnlySpan{Vec3}, IReadOnlyDictionary{string, float}?, RetainedMeshMorphs?, Span{Vec3})"/>.
	/// Unknown names, non-finite weights and active-target overflow throw.
	/// </summary>
	public void Preview( IReadOnlyDictionary<string, float> weights, Span<Vec3> outPositions, MorphPolicy? policy = null )
	{
		EnsureTopology();
		if ( outPositions.Length < _vertexCount ) throw new ArgumentException( "Output buffer is smaller than the vertex count.", nameof( outPositions ) );
		var applied = AppliedWeightMap( weights, policy );
		MorphEvaluator.DeformPositions( Mesh.Positions.ToArray(), applied, Retained(), outPositions );
	}

	/// <summary>Corner (render-vertex) preview with normal deltas through <see cref="MorphEvaluator.DeformCorners(CornerVertexMap, IReadOnlyDictionary{string, float}?, RetainedMeshMorphs?, Span{Vec3}, Span{Vec3}, bool)"/>.</summary>
	public void PreviewCorners( CornerVertexMap map, IReadOnlyDictionary<string, float> weights, Span<Vec3> outPositions, Span<Vec3> outNormals, MorphPolicy? policy = null )
	{
		ArgumentNullException.ThrowIfNull( map );
		EnsureTopology();
		if ( map.SourceVertexCount != _vertexCount )
			throw new ArgumentException( $"Corner map is for {map.SourceVertexCount} source vertices, the mesh has {_vertexCount}.", nameof( map ) );
		var applied = AppliedWeightMap( weights, policy );
		MorphEvaluator.DeformCorners( map, applied, Retained( withNormalDeltas: true ), outPositions, outNormals );
	}

	// --- Internals --------------------------------------------------------------------------------

	internal void Touch()
	{
		Revision++;
		_cachePositions = null;
		_cacheWithNormals = null;
	}

	internal void StrokeClosed( MorphStroke stroke )
	{
		if ( ReferenceEquals( _openStroke, stroke ) ) _openStroke = null;
	}

	void AddTargetCommand( string description, MeshMorph morph )
	{
		bool makeActive = ActiveTarget == null;
		var previousActive = _active;
		History.Execute( new DelegateEditCommand( description,
			() =>
			{
				Mesh.Morphs.Add( morph );
				if ( makeActive ) _active = morph;
				Touch();
			},
			() =>
			{
				Mesh.Morphs.Remove( morph );
				if ( makeActive ) _active = previousActive;
				Touch();
			} ) );
	}

	List<(MeshMorph Morph, float Weight)> ResolveWeights( IReadOnlyDictionary<string, float> weights, MorphPolicy? policy )
	{
		ArgumentNullException.ThrowIfNull( weights );
		policy ??= MorphPolicy.Default;
		var list = new List<(MeshMorph, float)>();
		foreach ( var (name, raw) in weights )
		{
			if ( name == null ) throw new ArgumentException( "Weight map contains a null name.", nameof( weights ) );
			var morph = FindValid( name ) ?? throw new ArgumentException( $"No morph target named '{name}'.", nameof( weights ) );
			if ( !float.IsFinite( raw ) ) throw new ArgumentException( $"Weight for '{name}' is not finite.", nameof( weights ) );
			float w = policy.Apply( raw );
			if ( MathF.Abs( w ) < 1e-6f ) continue;
			list.Add( (morph, w) );
		}
		if ( list.Count > policy.MaxActiveTargets )
			throw new InvalidOperationException( $"Active morph targets ({list.Count}) exceed the supported maximum ({policy.MaxActiveTargets})." );
		return list;
	}

	Dictionary<string, float> AppliedWeightMap( IReadOnlyDictionary<string, float> weights, MorphPolicy? policy )
	{
		var map = new Dictionary<string, float>( StringComparer.Ordinal );
		foreach ( var (m, w) in ResolveWeights( weights, policy ) ) map[m.Name] = w;
		return map;
	}

	void ValidateNewName( string name, string param )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) throw new ArgumentException( "Morph name must not be empty.", param );
		if ( name.Trim() != name ) throw new ArgumentException( "Morph name must not start or end with whitespace.", param );
		if ( name.Any( c => char.IsControl( c ) || c == '"' ) )
			throw new ArgumentException( "Morph name must not contain control characters or double quotes (FBX/DMX text).", param );
		if ( Mesh.Morphs.Any( m => m != null && string.Equals( m.Name, name, StringComparison.Ordinal ) ) )
			throw new ArgumentException( $"A morph named '{name}' already exists.", param );
	}

	static void ValidateSettings( MorphBrushSettings s )
	{
		if ( !Enum.IsDefined( s.Op ) ) throw new ArgumentOutOfRangeException( nameof( s.Op ) );
		if ( !Enum.IsDefined( s.Falloff ) ) throw new ArgumentOutOfRangeException( nameof( s.Falloff ) );
		if ( !float.IsFinite( s.Radius ) || s.Radius <= 0f ) throw new ArgumentOutOfRangeException( nameof( s.Radius ), "Radius must be finite and > 0." );
		if ( !float.IsFinite( s.Strength ) || s.Strength < 0f || s.Strength > 1f ) throw new ArgumentOutOfRangeException( nameof( s.Strength ), "Strength must be in [0, 1]." );
		if ( !float.IsFinite( s.InflateDistance ) ) throw new ArgumentOutOfRangeException( nameof( s.InflateDistance ), "Inflate distance must be finite." );
	}

	MeshMorph? FindValid( string name ) =>
		Mesh.ValidMorphs.FirstOrDefault( m => string.Equals( m.Name, name, StringComparison.Ordinal ) );

	MeshMorph Require( string name )
	{
		if ( name == null ) throw new ArgumentNullException( nameof( name ) );
		return FindValid( name ) ?? throw new ArgumentException( $"No editable morph target named '{name}'.", nameof( name ) );
	}

	bool IsLiveValid( MeshMorph m ) => Mesh.Morphs.Contains( m ) && m is { Recording: false } && m.Deltas?.Length == _vertexCount;

	void EnsureTopology()
	{
		if ( Mesh.Positions.Count != _vertexCount || Mesh.FaceCount != _faceCount )
			throw new InvalidOperationException( "Mesh topology changed since this MorphEditService was created; create a new one." );
	}

	void EnsureUsable()
	{
		EnsureTopology();
		if ( _openStroke != null ) throw new InvalidOperationException( "Finish or cancel the open morph stroke first." );
		if ( History.CurrentTransaction != null ) throw new InvalidOperationException( "Another edit transaction is active." );
	}

	internal static bool IsFinite( Vec3 v ) => float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );

	// --- Brush maths (called by MorphStroke) -------------------------------------------------------

	/// <summary>Applies one dab to <paramref name="deltas"/> in place; reports touched source vertices.</summary>
	internal int ApplyDab( MeshMorph morph, MorphBrushSettings s, Vec3 center, Vec3 offset, Vec3[] strokeStartShaped, HashSet<int> touched )
	{
		EnsureTopology();
		var deltas = morph.Deltas;
		int groups = _groupMembers.Length;

		// Shape positions the falloff is measured on: stroke start for Move (grab), current otherwise.
		Vec3[] shaped;
		if ( s.Op == MorphBrushOp.Move ) shaped = strokeStartShaped;
		else
		{
			shaped = new Vec3[_vertexCount];
			for ( int i = 0; i < _vertexCount; i++ ) shaped[i] = Mesh.Positions[i] + deltas[i];
		}

		var mirroredCenter = new Vec3( -center.x, center.y, center.z );
		var w1 = new float[groups];
		var w2 = new float[groups];
		bool any = false;
		for ( int g = 0; g < groups; g++ )
		{
			var p = GroupPosition( shaped, g );
			w1[g] = Weight( p, center, s );
			if ( s.MirrorX ) w2[g] = Weight( p, mirroredCenter, s );
			any |= w1[g] > 0f || w2[g] > 0f;
		}
		if ( !any ) return 0;

		Vec3[]? groupNormals = null;
		if ( s.Op == MorphBrushOp.Inflate )
		{
			var vn = SkinStrain.VertexNormals( shaped, Mesh.Faces );
			groupNormals = new Vec3[groups];
			for ( int g = 0; g < groups; g++ )
			{
				var sum = Vec3.Zero;
				foreach ( var v in _groupMembers[g] ) sum += vn[v];
				groupNormals[g] = sum.LengthSquared > 1e-20f ? sum.Normal : Vec3.Zero;
			}
		}

		// Smooth reads neighbour deltas from a snapshot (Jacobi), so dab order inside the mesh is irrelevant.
		Vec3[]? groupDelta = null;
		if ( s.Op == MorphBrushOp.Smooth )
		{
			groupDelta = new Vec3[groups];
			for ( int g = 0; g < groups; g++ ) groupDelta[g] = GroupPosition( deltas, g );
		}

		var mirroredOffset = new Vec3( -offset.x, offset.y, offset.z );
		int count = 0;
		for ( int g = 0; g < groups; g++ )
		{
			float a = w1[g], b = w2[g];
			if ( a <= 0f && b <= 0f ) continue;

			switch ( s.Op )
			{
				case MorphBrushOp.Move:
				{
					var change = offset * (s.Strength * a) + mirroredOffset * (s.Strength * b);
					if ( change.LengthSquared == 0f ) continue;
					foreach ( var v in _groupMembers[g] ) { deltas[v] += change; touched.Add( v ); }
					break;
				}
				case MorphBrushOp.Inflate:
				{
					var change = groupNormals![g] * (s.InflateDistance * s.Strength * (a + b));
					if ( change.LengthSquared == 0f ) continue;
					foreach ( var v in _groupMembers[g] ) { deltas[v] += change; touched.Add( v ); }
					break;
				}
				case MorphBrushOp.Erase:
				{
					float f = Math.Clamp( s.Strength * (a + b), 0f, 1f );
					if ( f <= 0f ) continue;
					foreach ( var v in _groupMembers[g] )
					{
						// f == 1 writes an exact zero rather than relying on d - d.
						deltas[v] = f >= 1f ? Vec3.Zero : deltas[v] * (1f - f);
						touched.Add( v );
					}
					break;
				}
				case MorphBrushOp.Smooth:
				{
					float f = Math.Clamp( s.Strength * (a + b), 0f, 1f );
					var nbrs = _groupNeighbours[g];
					if ( f <= 0f || nbrs.Length == 0 ) continue;
					var avg = Vec3.Zero;
					foreach ( var n in nbrs ) avg += groupDelta![n];
					avg /= nbrs.Length;
					foreach ( var v in _groupMembers[g] ) { deltas[v] = Vec3.Lerp( deltas[v], avg, f ); touched.Add( v ); }
					break;
				}
			}
			count += _groupMembers[g].Length;
		}

		if ( count > 0 ) Touch();
		return count;
	}

	Vec3 GroupPosition( Vec3[] values, int g )
	{
		var members = _groupMembers[g];
		if ( members.Length == 1 ) return values[members[0]];
		var sum = Vec3.Zero;
		foreach ( var v in members ) sum += values[v];
		return sum / members.Length;
	}

	static float Weight( Vec3 p, Vec3 center, MorphBrushSettings s )
	{
		float d = (p - center).Length;
		if ( d >= s.Radius ) return 0f;
		return Brush.Falloff( d / s.Radius, s.Falloff );
	}

	static (int[] GroupOf, int[][] Members) BuildGroups( List<Vec3> positions, float tolerance )
	{
		int n = positions.Count;
		var groupOf = new int[n];
		var members = new List<List<int>>();
		if ( tolerance <= 0f )
		{
			for ( int i = 0; i < n; i++ ) { groupOf[i] = i; members.Add( new List<int> { i } ); }
			return (groupOf, members.Select( m => m.ToArray() ).ToArray());
		}

		float cell = tolerance * 2f;
		var grid = new Dictionary<(long, long, long), List<int>>();
		(long, long, long) Key( Vec3 p ) => ((long)MathF.Floor( p.x / cell ), (long)MathF.Floor( p.y / cell ), (long)MathF.Floor( p.z / cell ));
		float tol2 = tolerance * tolerance;

		for ( int i = 0; i < n; i++ )
		{
			var p = positions[i];
			var (kx, ky, kz) = Key( p );
			int found = -1;
			for ( long dx = -1; dx <= 1 && found < 0; dx++ )
				for ( long dy = -1; dy <= 1 && found < 0; dy++ )
					for ( long dz = -1; dz <= 1 && found < 0; dz++ )
						if ( grid.TryGetValue( (kx + dx, ky + dy, kz + dz), out var bucket ) )
							foreach ( var j in bucket )
								if ( (positions[j] - p).LengthSquared <= tol2 ) { found = groupOf[j]; break; }

			if ( found < 0 ) { found = members.Count; members.Add( new List<int>() ); }
			groupOf[i] = found;
			members[found].Add( i );
			if ( !grid.TryGetValue( (kx, ky, kz), out var own ) ) grid[(kx, ky, kz)] = own = new List<int>();
			own.Add( i );
		}
		return (groupOf, members.Select( m => m.ToArray() ).ToArray());
	}

	static int[][] BuildGroupNeighbours( PolyMesh mesh, int[] groupOf, int groupCount )
	{
		var sets = new HashSet<int>[groupCount];
		for ( int g = 0; g < groupCount; g++ ) sets[g] = new HashSet<int>();
		foreach ( var face in mesh.Faces )
		{
			var idx = face.Indices;
			if ( idx.Length < 2 ) continue;
			for ( int c = 0; c < idx.Length; c++ )
			{
				int a = groupOf[idx[c]], b = groupOf[idx[(c + 1) % idx.Length]];
				if ( a == b ) continue;
				sets[a].Add( b );
				sets[b].Add( a );
			}
		}
		return sets.Select( s => s.OrderBy( x => x ).ToArray() ).ToArray();
	}
}

/// <summary>
/// One open sculpt stroke on one morph target. Dabs apply immediately (live preview);
/// Commit records one undo entry with the exact before/after values of every touched vertex.
/// </summary>
public sealed class MorphStroke
{
	readonly MorphEditService _service;
	readonly MeshMorph _morph;
	readonly MorphBrushSettings _settings;
	readonly Vec3[] _before;
	readonly Vec3[] _startShaped;
	readonly HashSet<int> _touched = new();
	bool _finished;

	public string TargetName => _morph.Name;
	public MorphBrushSettings Settings => _settings;
	public bool IsOpen => !_finished;
	public int TouchedVertexCount => _touched.Count;

	internal MorphStroke( MorphEditService service, MeshMorph morph, MorphBrushSettings settings )
	{
		_service = service;
		_morph = morph;
		_settings = settings;
		_before = (Vec3[])morph.Deltas.Clone();
		_startShaped = new Vec3[_before.Length];
		for ( int i = 0; i < _before.Length; i++ ) _startShaped[i] = service.Mesh.Positions[i] + _before[i];
	}

	/// <summary>
	/// One brush sample at <paramref name="center"/> (model space, inches). <paramref name="offset"/>
	/// is the Move displacement for this dab and is ignored by other ops. Returns vertices affected.
	/// </summary>
	public int Dab( Vec3 center, Vec3 offset = default )
	{
		if ( _finished ) throw new InvalidOperationException( "Stroke is already finished." );
		if ( !MorphEditService.IsFinite( center ) ) throw new ArgumentException( "Dab center must be finite.", nameof( center ) );
		if ( !MorphEditService.IsFinite( offset ) ) throw new ArgumentException( "Dab offset must be finite.", nameof( offset ) );
		return _service.ApplyDab( _morph, _settings, center, offset, _startShaped, _touched );
	}

	/// <summary>Records the stroke as one undo entry. Returns false (no entry) if nothing changed.</summary>
	public bool Commit()
	{
		if ( _finished ) throw new InvalidOperationException( "Stroke is already finished." );
		_finished = true;
		_service.StrokeClosed( this );

		var idx = _touched.Where( i => !_before[i].Equals( _morph.Deltas[i] ) ).OrderBy( i => i ).ToArray();
		if ( idx.Length == 0 ) return false;

		var before = idx.Select( i => _before[i] ).ToArray();
		var after = idx.Select( i => _morph.Deltas[i] ).ToArray();
		var morph = _morph;
		var service = _service;

		void Write( Vec3[] values )
		{
			var d = morph.Deltas;
			for ( int k = 0; k < idx.Length; k++ ) d[idx[k]] = values[k];
			service.Touch();
		}

		// Execute re-writes the already-applied final values (idempotent), so the live stroke and redo agree.
		_service.History.Execute( new DelegateEditCommand( $"Sculpt morph '{morph.Name}' ({_settings.Op})",
			() => Write( after ), () => Write( before ) ) );
		return true;
	}

	/// <summary>Restores the exact pre-stroke deltas; no undo entry.</summary>
	public void Cancel()
	{
		if ( _finished ) return;
		_finished = true;
		_service.StrokeClosed( this );
		foreach ( var i in _touched ) _morph.Deltas[i] = _before[i];
		if ( _touched.Count > 0 ) _service.Touch();
	}
}
