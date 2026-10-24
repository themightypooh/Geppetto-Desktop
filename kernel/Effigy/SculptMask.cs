using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// Per-vertex protection for one sculpt level. 1 means "brush me normally", 0 means "leave me
/// alone", and everything between is a soft edge.
///
/// THE SENSE IS THE WAY IT IS BECAUSE <see cref="Brush"/> ALREADY MULTIPLIES BY IT. A stroke's
/// weight is falloff x strength x mask, so an all-ones mask is the same as no mask at all and a
/// freshly made one changes nothing. Storing "how protected" instead would invert every stroke in
/// the tool the moment a mask existed, which is the sort of thing that reads as the brush being
/// broken.
///
/// It is deliberately NOT persisted. A mask is a working aid — the sculpting equivalent of a
/// selection — and it belongs to the session that painted it. Saving one would mean a format
/// change and a promise to keep it correct across a cage edit, for something whose whole use is
/// "hold this bit still while I do the next ten strokes".
/// </summary>
public sealed class SculptMask
{
	readonly float[] _values;

	public SculptMask( int count )
	{
		if ( count < 0 )
			throw new ArgumentOutOfRangeException( nameof( count ) );

		_values = new float[count];
		Clear();
	}

	public int Count => _values.Length;

	/// <summary>Bumped by every change. A display cache keyed on the sculpt's revision alone cannot
	/// see a mask move, and hide-by-mask would serve the mesh from before the last stroke.</summary>
	public int Revision { get; private set; }

	/// <summary>The array <see cref="Brush.Apply"/> takes. Live, not a copy.</summary>
	public float[] Values => _values;

	public float this[int index]
	{
		get => _values[index];
		set
		{
			_values[index] = Math.Clamp( value, 0f, 1f );
			Revision++;
		}
	}

	/// <summary>Nothing protected — the state in which the mask does not exist as far as a brush
	/// is concerned.</summary>
	public void Clear()
	{
		Array.Fill( _values, 1f );
		Revision++;
	}

	/// <summary>Protect everything, which is where "mask all but this" starts.</summary>
	public void Protect()
	{
		Array.Fill( _values, 0f );
		Revision++;
	}

	public void Invert()
	{
		for ( var i = 0; i < _values.Length; i++ )
			_values[i] = 1f - _values[i];

		Revision++;
	}

	/// <summary>
	/// Push the held region out by one ring of vertices (Blender's Grow Mask), or with
	/// <paramref name="shrink"/> pull it in by one. Each vertex takes the most-held (or least-held)
	/// value among itself and its neighbours, so a soft edge stays soft.
	/// </summary>
	public void Grow( PolyMesh mesh, bool shrink = false )
	{
		if ( mesh is null || mesh.VertexCount != _values.Length )
			return;

		var next = (float[])_values.Clone();
		foreach ( var face in mesh.Faces )
		{
			var n = face.Indices.Length;
			for ( var i = 0; i < n; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[(i + 1) % n];
				if ( shrink )
				{
					next[a] = MathF.Max( next[a], _values[b] );
					next[b] = MathF.Max( next[b], _values[a] );
				}
				else
				{
					next[a] = MathF.Min( next[a], _values[b] );
					next[b] = MathF.Min( next[b], _values[a] );
				}
			}
		}

		Array.Copy( next, _values, _values.Length );
		Revision++;
	}

	/// <summary>
	/// Hold everything except the connected piece <paramref name="seed"/> is on — a face set by
	/// topology: an ear, a hand, a separate eye, whatever the cursor is over, isolated for
	/// sculpting without a mask brush ever being picked up.
	/// </summary>
	public void ProtectAllButLinked( PolyMesh mesh, int seed )
	{
		if ( mesh is null || mesh.VertexCount != _values.Length || seed < 0 || seed >= _values.Length )
			return;

		var neighbours = new List<int>[mesh.VertexCount];
		foreach ( var face in mesh.Faces )
		{
			var n = face.Indices.Length;
			for ( var i = 0; i < n; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[(i + 1) % n];
				(neighbours[a] ??= new List<int>()).Add( b );
				(neighbours[b] ??= new List<int>()).Add( a );
			}
		}

		Array.Fill( _values, 0f );
		var stack = new Stack<int>();
		stack.Push( seed );
		_values[seed] = 1f;
		while ( stack.Count > 0 )
		{
			var v = stack.Pop();
			if ( neighbours[v] is null )
				continue;

			foreach ( var w in neighbours[v] )
			{
				if ( _values[w] >= 1f )
					continue;

				_values[w] = 1f;
				stack.Push( w );
			}
		}

		Revision++;
	}

	/// <summary>Whether anything is protected at all, so a UI can say so and a brush can skip the
	/// multiply.</summary>
	public bool Any
	{
		get
		{
			foreach ( var v in _values )
			{
				if ( v < 0.999f )
					return true;
			}

			return false;
		}
	}

	/// <summary>How much of the mesh is protected, 0 to 1 — what a readout shows.</summary>
	public float ProtectedFraction
	{
		get
		{
			if ( _values.Length == 0 )
				return 0f;

			var sum = 0f;

			foreach ( var v in _values )
				sum += 1f - v;

			return sum / _values.Length;
		}
	}

	/// <summary>
	/// Paint one dab, in the same shape a brush stroke has: a point, a radius, a falloff and a
	/// strength. Positive strength protects, negative releases, so one control does both ways round.
	/// </summary>
	public void Paint( PolyMesh mesh, MeshBVH bvh, Vec3 point, float radius, float strength, BrushFalloff falloff )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( mesh.VertexCount != _values.Length )
			throw new ArgumentException(
				$"This mask has {_values.Length} values and the mesh has {mesh.VertexCount} vertices." );

		if ( radius <= 0f )
			return;

		var found = new System.Collections.Generic.List<int>();

		if ( bvh is not null )
		{
			bvh.VerticesInRadius( mesh, point, radius, found );
		}
		else
		{
			var r2 = radius * radius;

			for ( var i = 0; i < mesh.VertexCount; i++ )
			{
				if ( (mesh.Positions[i] - point).LengthSquared <= r2 )
					found.Add( i );
			}
		}

		foreach ( var vi in found )
		{
			var t = (mesh.Positions[vi] - point).Length / radius;
			var w = Brush.Falloff( t, falloff ) * strength;

			// Protecting SUBTRACTS, because 1 is unprotected. Getting this backwards is the one
			// mistake this whole file's comment header exists to prevent.
			_values[vi] = Math.Clamp( _values[vi] - w, 0f, 1f );
		}

		Revision++;
	}

	/// <summary>
	/// A copy of <paramref name="mesh"/> with the fully protected parts dropped — "hide by mask".
	///
	/// A face goes only when EVERY corner of it is protected past the threshold. Dropping a face
	/// because one corner was masked would eat the boundary of every mask, so the visible edge would
	/// creep inward each time it was used.
	///
	/// Vertices are kept in place rather than compacted, so a vertex index still means the same
	/// vertex — a hidden mesh is for looking at, and anything else here is indexed per vertex.
	/// </summary>
	public PolyMesh Hide( PolyMesh mesh, float threshold = 0.5f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( mesh.VertexCount != _values.Length )
			throw new ArgumentException(
				$"This mask has {_values.Length} values and the mesh has {mesh.VertexCount} vertices." );

		var result = new PolyMesh();

		foreach ( var p in mesh.Positions )
			result.Positions.Add( p );

		foreach ( var face in mesh.Faces )
		{
			var allProtected = true;

			foreach ( var index in face.Indices )
			{
				if ( _values[index] > 1f - threshold )
				{
					allProtected = false;
					break;
				}
			}

			if ( allProtected )
				continue;

			result.Faces.Add( new Face( (int[])face.Indices.Clone(), (Vec2[])face.UVs.Clone(), face.Material ) );
		}

		return result;
	}
}

/// <summary>
/// Face sets: which faces belong to which sculptable region, and the selections that
/// isolate one.
///
/// A face set is an <c>int[]</c>, one entry per face. It is auto-populated from whatever
/// already partitions the mesh — <see cref="FromMaterials"/> reads <see cref="Face.Material"/>
/// slots, so a head whose lips, eyelids and plates already live in different slots gets
/// sets for free. Profile station ranges and MeshBranch child ids are the same shape
/// (one int per face) and feed <see cref="MaskForSet"/> directly; there is no second
/// representation to keep in sync.
///
/// Mask sense follows <see cref="SculptMask"/>: 1 means "brush me", 0 means "leave me
/// alone". A set mask therefore keeps the stroke on the set and off everything else —
/// a stroke on the upper jaw never pulls vertices on the lower jaw across a closed
/// mouth seam, because the lower jaw's vertices are masked to zero.
/// </summary>
public static class FaceSets
{
	/// <summary>One set id per face, taken from the face's material slot.</summary>
	public static int[] FromMaterials( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var sets = new int[mesh.FaceCount];

		for ( var f = 0; f < sets.Length; f++ )
			sets[f] = mesh.Faces[f].Material;

		return sets;
	}

	/// <summary>
	/// Per-vertex brush mask for one set: vertices of faces in the set are 1, the rest 0.
	///
	/// With <paramref name="boundaryFalloff"/> rings (0 = a hard edge), vertices outside
	/// the set but within that many rings of it get partial values decaying with distance
	/// — ring 1 gets <c>1 - 1/(rings+1)</c>, ring 2 gets <c>1 - 2/(rings+1)</c> — so the
	/// stroke feathers across the seam instead of stopping dead at it.
	/// </summary>
	public static float[] MaskForSet( PolyMesh mesh, int[] faceSets, int setId, float boundaryFalloff = 0f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( faceSets is null || faceSets.Length != mesh.FaceCount )
			throw new ArgumentException(
				$"Face sets need one entry per face: {faceSets?.Length ?? 0} for {mesh.FaceCount} faces." );

		var mask = new float[mesh.VertexCount];
		var inSet = new bool[mesh.VertexCount];

		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			if ( faceSets[f] != setId )
				continue;

			foreach ( var v in mesh.Faces[f].Indices )
				inSet[v] = true;
		}

		for ( var v = 0; v < mask.Length; v++ )
			mask[v] = inSet[v] ? 1f : 0f;

		var rings = (int)MathF.Ceiling( boundaryFalloff );

		if ( rings > 0 )
		{
			var neighbours = mesh.BuildVertexEdges();
			var distance = new int[mesh.VertexCount];
			Array.Fill( distance, -1 );

			for ( var v = 0; v < distance.Length; v++ )
				if ( inSet[v] )
					distance[v] = 0;

			for ( var ring = 1; ring <= rings; ring++ )
			{
				for ( var v = 0; v < distance.Length; v++ )
				{
					if ( distance[v] != -1 )
						continue;

					foreach ( var key in neighbours[v] )
					{
						var other = key.A == v ? key.B : key.A;

						if ( distance[other] == ring - 1 )
						{
							distance[v] = ring;
							break;
						}
					}
				}

				for ( var v = 0; v < distance.Length; v++ )
					if ( distance[v] == ring )
						mask[v] = 1f - ring / (float)( rings + 1 );
			}
		}

		return mask;
	}

	/// <summary>Write a face-set mask into a session mask, replacing what it held.</summary>
	public static void ApplyToMask( SculptMask mask, PolyMesh mesh, int[] faceSets, int setId, float boundaryFalloff = 0f )
	{
		var values = MaskForSet( mesh, faceSets, setId, boundaryFalloff );

		if ( mask is null || mask.Count != values.Length )
			throw new ArgumentException( "The mask does not match this mesh's vertex count." );

		for ( var v = 0; v < values.Length; v++ )
			mask[v] = values[v];
	}
}

/// <summary>
/// Box and lasso selections as masks. A lasso in the kernel is a polygon on a plane —
/// the editor projects its screen polygon onto the surface plane and hands that polygon
/// here — because the kernel has no screen and must not learn what one is. Mask sense
/// is the same as <see cref="SculptMask"/>: 1 inside the selection, 0 outside.
/// </summary>
public static class MaskSelection
{
	/// <summary>1 for vertices inside the axis-aligned box (inclusive), 0 outside it.</summary>
	public static float[] Box( PolyMesh mesh, Vec3 min, Vec3 max )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var mask = new float[mesh.VertexCount];

		for ( var v = 0; v < mask.Length; v++ )
		{
			var p = mesh.Positions[v];
			mask[v] = p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y && p.z >= min.z && p.z <= max.z ? 1f : 0f;
		}

		return mask;
	}

	/// <summary>
	/// 1 for vertices whose projection onto the plane perpendicular to <paramref name="axis"/>
	/// (0 = x, 1 = y, 2 = z) falls inside <paramref name="polygon"/>, 0 outside. Ray casting
	/// in 2D: a point is inside when a ray from it crosses the polygon an odd number of times.
	/// </summary>
	public static float[] Polygon( PolyMesh mesh, Vec2[] polygon, int axis = 2 )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( polygon is null || polygon.Length < 3 )
			throw new ArgumentException( "A lasso polygon needs at least 3 points." );

		var mask = new float[mesh.VertexCount];

		for ( var v = 0; v < mask.Length; v++ )
			mask[v] = Inside( Project( mesh.Positions[v], axis ), polygon ) ? 1f : 0f;

		return mask;
	}

	static Vec2 Project( Vec3 p, int axis ) => axis switch
	{
		0 => new Vec2( p.y, p.z ),
		1 => new Vec2( p.x, p.z ),
		_ => new Vec2( p.x, p.y ),
	};

	static bool Inside( Vec2 point, Vec2[] polygon )
	{
		var inside = false;

		for ( int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++ )
		{
			var a = polygon[i];
			var b = polygon[j];

			if ( ( a.y > point.y ) != ( b.y > point.y )
				&& point.x < ( b.x - a.x ) * ( point.y - a.y ) / ( b.y - a.y ) + a.x )
				inside = !inside;
		}

		return inside;
	}
}
