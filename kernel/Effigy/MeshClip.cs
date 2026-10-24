using System;
using System.Collections.Generic;
using System.Globalization;

namespace Effigy;

/// <summary>
/// Keep the half of a mesh on one side of a plane, clipping faces that straddle it.
///
/// THIS IS A VIEW, NOT A FEATURE. Shell builds interiors nobody can look at; a cutaway hides the
/// near half while looking, without writing anything into the history. Faces that cross the plane
/// are clipped in place so the cut reads as a hard line rather than as missing polygons. There is
/// no cap — an open cut is the point of a section view, and capping would pretend the kernel had
/// booleaned a solid it has not.
/// </summary>
public static class MeshClip
{
	/// <summary>
	/// Everything on the positive side of the plane through <paramref name="origin"/> with
	/// <paramref name="normal"/>, including the plane itself. Vertices on the negative side are
	/// dropped; edges that cross emit a new vertex at the intersection.
	/// </summary>
	public static PolyMesh Keep( PolyMesh mesh, Vec3 origin, Vec3 normal, float tolerance = 1e-5f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var n = normal.Normal;

		if ( n.LengthSquared < 0.5f )
			return mesh.Clone();

		var result = new PolyMesh();

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 )
				continue;

			var clipped = ClipFace( mesh, face, origin, n, tolerance );

			if ( clipped.Count < 3 )
				continue;

			var indices = new int[clipped.Count];
			var uvs = new Vec2[clipped.Count];

			for ( var i = 0; i < clipped.Count; i++ )
			{
				indices[i] = result.AddVertex( clipped[i].Position );
				uvs[i] = clipped[i].Uv;
			}

			result.AddFace( indices, uvs, face.Material );
		}

		return result;
	}

	/// <summary>
	/// <see cref="Keep(PolyMesh, Vec3, Vec3, float)"/> for looking at: the kept half stays welded, so it shades
	/// as smoothly as the whole did. Untouched vertices keep their index's weights and colour; a vertex made where
	/// an edge crosses the plane is shared by both faces on that edge and blends its ends' UVs, colour and
	/// weights. Paint rides along (it is read by UV). Morphs and seams are dropped: a section is a view.
	/// </summary>
	public static PolyMesh Section( PolyMesh mesh, ClipPlane plane )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var dist = new float[mesh.Positions.Count];
		for ( var i = 0; i < dist.Length; i++ )
			dist[i] = plane.Distance( mesh.Positions[i] );

		var result = new PolyMesh { Paint = mesh.Paint };
		var skin = mesh.IsRigged ? new SkinWeights() : null;
		var colours = mesh.HasVertexColors ? new List<Vec4>() : null;
		var kept = new Dictionary<int, int>();
		var cut = new Dictionary<(int, int), int>();

		int Add( Vec3 p, BoneWeight[] w, Vec4 c )
		{
			skin?.Vertices.Add( w );
			colours?.Add( c );
			return result.AddVertex( p );
		}

		int Kept( int i )
		{
			if ( kept.TryGetValue( i, out var at ) ) return at;
			return kept[i] = Add( mesh.Positions[i], skin is null ? null : mesh.Skin[i], colours is null ? default : mesh.VertexColors[i] );
		}

		int Cut( int a, int b )
		{
			var key = a < b ? (a, b) : (b, a);
			if ( cut.TryGetValue( key, out var at ) ) return at;
			var (i, j) = key;
			var t = Math.Clamp( dist[i] / (dist[i] - dist[j]), 0f, 1f );
			var w = skin is null ? null : SkinWeights.Blend( (mesh.Skin[i], 1f - t), (mesh.Skin[j], t) );
			var c = colours is null ? default : mesh.VertexColors[i] + (mesh.VertexColors[j] - mesh.VertexColors[i]) * t;
			return cut[key] = Add( mesh.Positions[i] + (mesh.Positions[j] - mesh.Positions[i]) * t, w, c );
		}

		var indices = new List<int>();
		var uvs = new List<Vec2>();

		foreach ( var face in mesh.Faces )
		{
			var n = face.Count;
			if ( n < 3 ) continue;
			indices.Clear();
			uvs.Clear();

			for ( var k = 0; k < n; k++ )
			{
				var a = face.Indices[(k + n - 1) % n];
				var b = face.Indices[k];
				var inA = dist[a] >= 0f;
				var inB = dist[b] >= 0f;
				var uvA = face.UVs[(k + n - 1) % n];
				var uvB = face.UVs[k];

				if ( inA != inB )
				{
					indices.Add( Cut( a, b ) );
					var t = Math.Clamp( dist[a] / (dist[a] - dist[b]), 0f, 1f );
					uvs.Add( uvA + (uvB - uvA) * t );
				}

				if ( inB )
				{
					indices.Add( Kept( b ) );
					uvs.Add( uvB );
				}
			}

			if ( indices.Count >= 3 )
				result.AddFace( indices.ToArray(), uvs.ToArray(), face.Material );
		}

		if ( skin is not null ) result.Skin = skin;
		if ( colours is not null ) result.VertexColors = colours.ToArray();
		return result;
	}

	/// <summary>
	/// <see cref="Section(PolyMesh, ClipPlane)"/> under its roadmap name: keep the half of
	/// the mesh on the plane's kept side, faces clipped in place, no cap.
	/// </summary>
	public static PolyMesh Clip( PolyMesh mesh, ClipPlane plane ) => Section( mesh, plane );

	/// <summary>
	/// A planar trim, not a section view: keep the plane's kept half and close the cut
	/// with a watertight cap, so the result is a solid again rather than an open shell.
	/// The cap is a fan over each boundary loop of the clipped mesh, oriented to face
	/// away from the kept side. A mesh whose cut boundary is not a set of simple loops
	/// (a non-manifold input, or a plane that only grazes it) gets no cap for that loop
	/// rather than a fan over garbage — the clipped mesh is still returned.
	/// </summary>
	public static PolyMesh TrimPlane( PolyMesh mesh, ClipPlane plane, int capMaterial = 0 )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var result = Section( mesh, plane );
		Cap( result, plane, capMaterial );
		return result;
	}

	/// <summary>
	/// Trim a mesh to an axis-aligned box, capping every cut face, for crisp stylized
	/// rock/armor bevel cuts. Sequential planar trims: each trim caps its own cut, and
	/// the next trim cuts the previous caps too, so the final solid is the box
	/// intersection with caps on all six cut faces.
	/// </summary>
	public static PolyMesh BoxTrim( PolyMesh mesh, Vec3 min, Vec3 max, int capMaterial = 0 )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var result = mesh.Clone();
		result = TrimPlane( result, new ClipPlane( new Vec3( 1, 0, 0 ), min.x ), capMaterial );
		result = TrimPlane( result, new ClipPlane( new Vec3( -1, 0, 0 ), -max.x ), capMaterial );
		result = TrimPlane( result, new ClipPlane( new Vec3( 0, 1, 0 ), min.y ), capMaterial );
		result = TrimPlane( result, new ClipPlane( new Vec3( 0, -1, 0 ), -max.y ), capMaterial );
		result = TrimPlane( result, new ClipPlane( new Vec3( 0, 0, 1 ), min.z ), capMaterial );
		result = TrimPlane( result, new ClipPlane( new Vec3( 0, 0, 1 ) * -1f, -max.z ), capMaterial );
		return result;
	}

	static void Cap( PolyMesh mesh, ClipPlane plane, int capMaterial )
	{
		var edgeUse = new Dictionary<(int, int), int>();
		var edgeDir = new Dictionary<(int, int), (int, int)>();

		foreach ( var face in mesh.Faces )
		{
			for ( var i = 0; i < face.Count; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[( i + 1 ) % face.Count];
				var key = a < b ? (a, b) : (b, a);
				edgeUse[key] = edgeUse.TryGetValue( key, out var n ) ? n + 1 : 1;
				edgeDir[key] = (a, b);
			}
		}

		// Boundary edges, chained into loops. Each vertex on a simple loop has exactly
		// two boundary neighbours; anything else means the loop is not simple, and that
		// loop is skipped rather than capped wrongly.
		var next = new Dictionary<int, int>();

		foreach ( var (key, use) in edgeUse )
		{
			if ( use != 1 )
				continue;

			var (a, b) = edgeDir[key];

			if ( next.ContainsKey( a ) )
				return;

			next[a] = b;
		}

		var seen = new HashSet<int>();

		foreach ( var start in new List<int>( next.Keys ) )
		{
			if ( seen.Contains( start ) )
				continue;

			var loop = new List<int>();
			var v = start;

			while ( next.TryGetValue( v, out var w ) && !seen.Contains( v ) )
			{
				seen.Add( v );
				loop.Add( v );
				v = w;
			}

			if ( loop.Count < 3 || v != start )
				continue;

			// Newell normal of the loop; the cap faces away from the kept side.
			var normal = Vec3.Zero;

			for ( var i = 0; i < loop.Count; i++ )
			{
				var a = mesh.Positions[loop[i]];
				var b = mesh.Positions[loop[( i + 1 ) % loop.Count]];
				normal += new Vec3(
					( a.y - b.y ) * ( a.z + b.z ),
					( a.z - b.z ) * ( a.x + b.x ),
					( a.x - b.x ) * ( a.y + b.y ) );
			}

			if ( Vec3.Dot( normal, plane.Normal ) > 0f )
				loop.Reverse();

			var centre = Vec3.Zero;

			foreach ( var i in loop )
				centre += mesh.Positions[i];

			centre /= loop.Count;
			var centreIndex = mesh.AddVertex( centre );
			var uvs = new Vec2[3];

			for ( var i = 0; i < loop.Count; i++ )
				mesh.AddFace( new[] { centreIndex, loop[i], loop[( i + 1 ) % loop.Count] }, uvs, capMaterial );
		}
	}

	readonly struct ClipVert
	{
		public readonly Vec3 Position;
		public readonly Vec2 Uv;

		public ClipVert( Vec3 position, Vec2 uv )
		{
			Position = position;
			Uv = uv;
		}
	}

	static List<ClipVert> ClipFace( PolyMesh mesh, Face face, Vec3 origin, Vec3 normal, float tolerance )
	{
		var polygon = new List<ClipVert>( face.Count );

		for ( var i = 0; i < face.Count; i++ )
		{
			var uv = face.UVs is not null && i < face.UVs.Length ? face.UVs[i] : default;
			polygon.Add( new ClipVert( mesh.Positions[face.Indices[i]], uv ) );
		}

		// Sutherland–Hodgman against one plane: walk each edge, emit the intersection when the
		// edge crosses, and keep the vertex when it sits on the kept side.
		var output = new List<ClipVert>( polygon.Count + 2 );

		for ( var i = 0; i < polygon.Count; i++ )
		{
			var current = polygon[i];
			var previous = polygon[( i + polygon.Count - 1 ) % polygon.Count];
			var currentDist = Vec3.Dot( current.Position - origin, normal );
			var previousDist = Vec3.Dot( previous.Position - origin, normal );
			var currentIn = currentDist >= -tolerance;
			var previousIn = previousDist >= -tolerance;

			if ( currentIn )
			{
				if ( !previousIn )
					output.Add( Intersect( previous, current, previousDist, currentDist ) );

				output.Add( current );
			}
			else if ( previousIn )
			{
				output.Add( Intersect( previous, current, previousDist, currentDist ) );
			}
		}

		return output;
	}

	static ClipVert Intersect( ClipVert a, ClipVert b, float da, float db )
	{
		var denom = da - db;

		// Both on the plane, or an edge so close the numbers vanish: pick B rather than divide.
		var t = MathF.Abs( denom ) < 1e-12f ? 1f : da / denom;
		t = Math.Clamp( t, 0f, 1f );

		return new ClipVert( a.Position + ( b.Position - a.Position ) * t, a.Uv + ( b.Uv - a.Uv ) * t );
	}
}

/// <summary>
/// A cut for looking inside: keep what lies on one side of an axis-aligned plane. Written the way it is typed,
/// <c>y&gt;0</c> keeps everything with y above 0, <c>x&lt;1.5</c> everything with x below 1.5 (Effigy is Z-up,
/// front +x, left +y). <c>&gt;=</c> and <c>&lt;=</c> read the same; the plane itself is always kept.
/// </summary>
public readonly struct ClipPlane
{
	public readonly Vec3 Normal;
	public readonly float Offset;
	public readonly string Spec;

	public ClipPlane( Vec3 normal, float offset, string spec = null )
	{
		Normal = normal.Normal;
		Offset = offset;
		Spec = spec ?? $"{normal}·p>={offset}";
	}

	/// <summary>Signed distance from the plane, positive on the side kept.</summary>
	public float Distance( Vec3 p ) => Vec3.Dot( p, Normal ) - Offset;

	public bool Keeps( Vec3 p ) => Distance( p ) >= 0f;

	public static bool TryParse( string spec, out ClipPlane plane, out string error )
	{
		plane = default;
		error = null;
		var s = (spec ?? "").Replace( " ", "" ).ToLowerInvariant();
		var op = s.IndexOfAny( new[] { '<', '>' } );

		if ( op != 1 || s[0] is not ('x' or 'y' or 'z') )
		{
			error = $"clip '{spec}': expected an axis, < or >, and a number, like y>0 or x<1.5";
			return false;
		}

		var greater = s[op] == '>';
		var rest = s[(op + 1)..].TrimStart( '=' );

		if ( !float.TryParse( rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var value ) || !float.IsFinite( value ) )
		{
			error = $"clip '{spec}': '{rest}' is not a number";
			return false;
		}

		var axis = s[0] switch { 'x' => new Vec3( 1, 0, 0 ), 'y' => new Vec3( 0, 1, 0 ), _ => new Vec3( 0, 0, 1 ) };
		plane = greater ? new ClipPlane( axis, value, s ) : new ClipPlane( -axis, -value, s );
		return true;
	}

	public static ClipPlane Parse( string spec ) =>
		TryParse( spec, out var plane, out var error ) ? plane : throw new FormatException( error );

	public override string ToString() => Spec;
}
