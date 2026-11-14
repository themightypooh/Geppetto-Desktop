using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// IMM / NanoMesh-style insert-and-repeat, kernel side.
///
/// A sculpted detail (a rivet, a scale, a stitch, a horn) becomes a reusable
/// <see cref="InsertBrush"/>: one source mesh — or up to three parts, the Tri-Parts
/// pattern, each with its own local offset — that is stamped onto a target as live
/// <see cref="InsertInstance"/>s. Live means the instances hold the brush, not a copy
/// of it: edit the source mesh once and every instance's realised geometry follows,
/// because <see cref="InsertInstance.Realized"/> re-transforms the current source
/// on every call. Nothing is baked into the target until <see cref="InsertMeshSession.Commit"/>
/// merges the instances in, as one undoable operation.
///
/// The session is the interactive form. The feature form is
/// <see cref="InsertMeshFeature"/>: it rebuilds the same placements from the source
/// body on every rebuild, so editing the source body propagates through the document.
/// </summary>
public sealed class InsertBrush
{
	public string Name;
	public string[] Tags = Array.Empty<string>();

	/// <summary>The source parts. One part is the common case; three is Tri-Parts.</summary>
	public List<InsertPart> Parts = new();

	public InsertBrush( string name, PolyMesh source, string[] tags = null )
	{
		Name = name ?? throw new ArgumentNullException( nameof( name ) );
		Tags = tags ?? Array.Empty<string>();
		if ( source is not null )
			Parts.Add( new InsertPart( source, Xform.Identity ) );
	}

	public int PartCount => Parts.Count;

	public int VertexCount => Parts.Sum( p => p.Mesh.VertexCount );

	public int FaceCount => Parts.Sum( p => p.Mesh.FaceCount );

	public void AddPart( PolyMesh mesh, Xform local )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		Parts.Add( new InsertPart( mesh, local ) );
	}

	public void AddPart( PolyMesh mesh ) => AddPart( mesh, Xform.Identity );
}

/// <summary>One part of an insert brush: a source mesh plus its offset inside the brush.</summary>
public sealed class InsertPart
{
	public PolyMesh Mesh;
	public Xform Local;

	public InsertPart( PolyMesh mesh, Xform local )
	{
		Mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		Local = local;
	}
}

/// <summary>
/// One live placement of an insert brush: the brush plus the transform that puts it
/// on the target. The transform is a full <see cref="Xform"/> (basis + origin), so a
/// placement carries position, surface frame, uniform scale and spin in one value.
/// </summary>
public sealed class InsertInstance
{
	public InsertBrush Brush;
	public Xform Placement;

	public InsertInstance( InsertBrush brush, Xform placement )
	{
		Brush = brush ?? throw new ArgumentNullException( nameof( brush ) );
		Placement = placement;
	}

	/// <summary>The source parts as placed, merged into one mesh. Recomputed from the
	/// brush's CURRENT source meshes, so editing the source updates every instance.</summary>
	public PolyMesh Realized()
	{
		var merged = new PolyMesh();
		foreach ( var part in Brush.Parts )
			MeshTransform.Append( merged, MeshTransform.Transformed( part.Mesh, Placement * part.Local ) );
		return merged;
	}
}

/// <summary>
/// The undo token for one <see cref="InsertMeshSession.Commit"/>. It holds a deep
/// snapshot of the target from before the commit, so <see cref="Restore"/> puts the
/// target back exactly — positions, faces, colours, skin, morphs and UV2 — whether or
/// not the commit was followed by a decimation.
/// </summary>
public sealed class InsertCommit
{
	readonly PolyMesh _before;

	public int CommittedVertices { get; }
	public int CommittedFaces { get; }
	public bool Decimated { get; internal set; }
	public int DecimatedTriangles { get; internal set; }

	internal InsertCommit( PolyMesh before, int vertices, int faces )
	{
		_before = before;
		CommittedVertices = vertices;
		CommittedFaces = faces;
	}

	public void Restore( PolyMesh target )
	{
		if ( target is null ) throw new ArgumentNullException( nameof( target ) );
		target.Positions.Clear();
		target.Positions.AddRange( _before.Positions );
		target.Faces.Clear();
		foreach ( var f in _before.Faces )
			target.Faces.Add( new Face( (int[])f.Indices.Clone(), f.UVs is null ? null : (Vec2[])f.UVs.Clone(), f.Material ) );
		target.VertexColors = _before.VertexColors?.ToArray();
		target.VertexEmission = _before.VertexEmission?.ToArray();
		target.Skin = _before.Skin;
		target.Morphs.Clear();
		target.Morphs.AddRange( _before.Morphs.Select( m => m.Clone() ) );
		target.UV2 ??= new List<Vec2>();
		target.UV2.Clear();
		target.UV2.AddRange( _before.UV2 );
		target.FaceUV2 = _before.FaceUV2?.Select( row => row is null ? null : (int[])row.Clone() ).ToArray();
		target.Seams ??= new HashSet<EdgeKey>();
		target.Seams.Clear();
		foreach ( var s in _before.Seams )
			target.Seams.Add( s );
		target.InvalidateVertexCache();
	}
}

/// <summary>
/// Live insert-and-repeat over one brush: place instances, scatter them, commit them.
/// </summary>
public sealed class InsertMeshSession
{
	public InsertBrush Brush { get; }

	readonly List<InsertInstance> _instances = new();

	public IReadOnlyList<InsertInstance> Instances => _instances;

	/// <summary>Vertices the last <see cref="Commit"/> merged, across all instances.</summary>
	public int LastCommittedVertices { get; private set; }

	/// <summary>Faces the last <see cref="Commit"/> merged, across all instances.</summary>
	public int LastCommittedFaces { get; private set; }

	public InsertMeshSession( InsertBrush brush )
	{
		Brush = brush ?? throw new ArgumentNullException( nameof( brush ) );
	}

	/// <summary>Build a surface frame from a normal and an up hint — the same rule
	/// <see cref="VdmStamp.Apply"/> uses, so a stamp and an insert aimed the same way
	/// agree on which way is up.</summary>
	public static void Frame( Vec3 normal, Vec3 upHint, out Vec3 tangent, out Vec3 bitangent )
	{
		var n = normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 );
		var t = upHint - n * Vec3.Dot( upHint, n );
		if ( t.LengthSquared < 1e-8f )
			t = MathF.Abs( n.x ) < 0.9f ? Vec3.Cross( n, new Vec3( 1, 0, 0 ) ) : Vec3.Cross( n, new Vec3( 0, 1, 0 ) );
		tangent = t.Normal;
		bitangent = Vec3.Cross( n, tangent );
	}

	/// <summary>Place one instance by click: at a point, facing a surface normal, at a
	/// uniform scale, spun about the normal. Zero normal means +Z; zero up hint means +Y.</summary>
	public InsertInstance Place( Vec3 position, Vec3 normal, float scale = 1f, float rotationRadians = 0f, Vec3 upHint = default )
	{
		if ( scale <= 0f ) throw new ArgumentOutOfRangeException( nameof( scale ), "Insert scale must be positive." );
		var n = normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 );
		var up = upHint.LengthSquared > 1e-12f ? upHint : new Vec3( 0, 1, 0 );
		Frame( n, up, out var tangent, out var bitangent );
		var placement = Xform.Translate( position )
			* Xform.Rotate( n, rotationRadians )
			* Xform.Scale( new Vec3( scale, scale, scale ) )
			* new Xform( tangent, bitangent, n, Vec3.Zero );
		var instance = new InsertInstance( Brush, placement );
		_instances.Add( instance );
		return instance;
	}

	/// <summary>Place instances along a polyline, every <paramref name="spacing"/> units
	/// from the start, each facing along the curve with its up from
	/// <paramref name="upHint"/> (default +Y). The first point and the last point are
	/// always stamped, so a curve's ends are never dropped to rounding. Returns the
	/// number placed.</summary>
	public int PlaceAlongCurve( IReadOnlyList<Vec3> points, float spacing, float scale = 1f, Vec3 upHint = default )
	{
		if ( points is null ) throw new ArgumentNullException( nameof( points ) );
		if ( points.Count < 2 ) throw new ArgumentException( "A curve needs at least two points.", nameof( points ) );
		if ( spacing <= 0f ) throw new ArgumentOutOfRangeException( nameof( spacing ), "Curve spacing must be positive." );
		if ( scale <= 0f ) throw new ArgumentOutOfRangeException( nameof( scale ), "Insert scale must be positive." );

		var up = upHint.LengthSquared > 1e-12f ? upHint : new Vec3( 0, 1, 0 );
		var lengths = new float[points.Count - 1];
		var total = 0f;
		for ( var i = 0; i < lengths.Length; i++ )
		{
			lengths[i] = (points[i + 1] - points[i]).Length;
			total += lengths[i];
		}

		var placed = 0;
		var d = 0f;
		while ( d <= total + 1e-6f )
		{
			PlaceAtDistance( points, lengths, MathF.Min( d, total ), scale, up );
			placed++;
			d += spacing;
		}
		// The walk above lands on the end only when the length divides evenly; stamp it
		// explicitly so the curve's end is never dropped.
		if ( placed == 0 || (placed - 1) * spacing < total - 1e-6f )
		{
			PlaceAtDistance( points, lengths, total, scale, up );
			placed++;
		}
		return placed;
	}

	void PlaceAtDistance( IReadOnlyList<Vec3> points, float[] lengths, float distance, float scale, Vec3 up )
	{
		var remaining = distance;
		for ( var i = 0; i < lengths.Length; i++ )
		{
			if ( remaining <= lengths[i] || i == lengths.Length - 1 )
			{
				var t = lengths[i] > 1e-9f ? MathF.Min( remaining / lengths[i], 1f ) : 0f;
				var position = points[i] + (points[i + 1] - points[i]) * t;
				var tangent = (points[i + 1] - points[i]).LengthSquared > 1e-12f
					? (points[i + 1] - points[i]).Normal
					: new Vec3( 1, 0, 0 );
				// The instance's normal is the surface direction: perpendicular to the curve
				// in the plane the up hint picks out.
				var normal = up - tangent * Vec3.Dot( up, tangent );
				if ( normal.LengthSquared < 1e-8f )
					normal = MathF.Abs( tangent.x ) < 0.9f ? Vec3.Cross( tangent, new Vec3( 1, 0, 0 ) ) : Vec3.Cross( tangent, new Vec3( 0, 1, 0 ) );
				Place( position, normal.Normal, scale, 0f, up );
				return;
			}
			remaining -= lengths[i];
		}
	}

	/// <summary>Scatter <paramref name="count"/> instances in a disc of
	/// <paramref name="radius"/> around <paramref name="center"/>, facing
	/// <paramref name="normal"/> (default +Z). Scale, spin and in-plane jitter are drawn
	/// from <c>System.Random(seed)</c>, so the same seed and the same inputs place the
	/// same instances, every run. Returns the number placed.</summary>
	public int Scatter( int count, Vec3 center, float radius, int seed, float scaleMin = 0.8f, float scaleMax = 1.2f, float rotationRangeRadians = MathF.PI, float jitter = 0f, Vec3 normal = default )
	{
		if ( count < 0 ) throw new ArgumentOutOfRangeException( nameof( count ), "Scatter count cannot be negative." );
		if ( radius < 0f ) throw new ArgumentOutOfRangeException( nameof( radius ), "Scatter radius cannot be negative." );
		if ( scaleMin <= 0f || scaleMax < scaleMin ) throw new ArgumentOutOfRangeException( nameof( scaleMin ), "Scatter scale range must be positive and ordered." );
		if ( jitter < 0f ) throw new ArgumentOutOfRangeException( nameof( jitter ), "Scatter jitter cannot be negative." );

		var n = normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 );
		Frame( n, new Vec3( 0, 1, 0 ), out var tangent, out var bitangent );
		var rnd = new Random( seed );

		for ( var i = 0; i < count; i++ )
		{
			var angle = (float)(rnd.NextDouble() * 2.0 * Math.PI);
			var r = radius * MathF.Sqrt( (float)rnd.NextDouble() );
			var disc = tangent * (MathF.Cos( angle ) * r) + bitangent * (MathF.Sin( angle ) * r);
			var jAngle = (float)(rnd.NextDouble() * 2.0 * Math.PI);
			var jR = jitter * (float)rnd.NextDouble();
			var position = center + disc + tangent * (MathF.Cos( jAngle ) * jR) + bitangent * (MathF.Sin( jAngle ) * jR);
			var scale = scaleMin + (float)rnd.NextDouble() * (scaleMax - scaleMin);
			var rotation = ((float)rnd.NextDouble() * 2f - 1f) * rotationRangeRadians;
			Place( position, n, scale, rotation );
		}
		return count;
	}

	/// <summary>All live instances merged into one mesh, without touching any target.
	/// A preview: the same merge <see cref="Commit"/> performs.</summary>
	public PolyMesh RealizedMesh()
	{
		var merged = new PolyMesh();
		foreach ( var instance in _instances )
			MeshTransform.Append( merged, instance.Realized() );
		return merged;
	}

	/// <summary>One instance's source as placed, from the brush's CURRENT meshes.</summary>
	public PolyMesh RealizedInstance( int index ) => _instances[index].Realized();

	public void Clear() => _instances.Clear();

	/// <summary>Merge every live instance into <paramref name="target"/> as one undoable
	/// operation. The instances stay live afterwards — committing does not consume them,
	/// so the same session can commit again after the source is edited. With
	/// <paramref name="decimateAfterCommit"/>, the merged mesh is reduced to
	/// <paramref name="decimateTargetTriangles"/> triangles first (see
	/// <see cref="Decimate"/>); that reduction drops vertex colours, skin weights and
	/// morphs, which the decimator does not carry.</summary>
	public InsertCommit Commit( PolyMesh target, bool decimateAfterCommit = false, int decimateTargetTriangles = 0 )
	{
		if ( target is null ) throw new ArgumentNullException( nameof( target ) );
		if ( _instances.Count == 0 ) throw new InvalidOperationException( "Nothing to commit: no instances are placed." );

		var before = target.Clone();
		var vertices = 0;
		var faces = 0;
		foreach ( var instance in _instances )
		{
			var realized = instance.Realized();
			vertices += realized.VertexCount;
			faces += realized.FaceCount;
			MeshTransform.Append( target, realized );
		}
		target.InvalidateVertexCache();

		var commit = new InsertCommit( before, vertices, faces );
		if ( decimateAfterCommit )
		{
			var decimated = Decimate.ToTriangles( target, decimateTargetTriangles );
			target.Positions.Clear();
			target.Positions.AddRange( decimated.Positions );
			target.Faces.Clear();
			foreach ( var f in decimated.Faces )
				target.Faces.Add( new Face( (int[])f.Indices.Clone(), f.UVs is null ? null : (Vec2[])f.UVs.Clone(), f.Material ) );
			target.VertexColors = null;
			target.VertexEmission = null;
			target.Skin = null;
			target.Morphs.Clear();
			target.InvalidateVertexCache();
			commit.Decimated = true;
			commit.DecimatedTriangles = Decimate.TriangleCount( target );
		}

		LastCommittedVertices = vertices;
		LastCommittedFaces = faces;
		return commit;
	}
}
