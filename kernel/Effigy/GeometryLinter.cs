using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Effigy;

/// <summary>
/// S&amp;box Lane 2a — deterministic geometry linter (kernel side).
///
/// Checks one model (one or more named parts, each a triangle mesh):
///   1. Watertightness: BoundaryEdges == 0 and Euler characteristic
///      V - E + F == 2 per closed genus-0 component. A boundary edge is
///      an undirected edge used by exactly one triangle; a non-manifold
///      edge (used by more than two) is also an error.
///   2. Disjoint / floating parts: inside one part, face connectivity
///      over shared vertices gives the body count (more than one body
///      in a part is an error); across parts, every part after the
///      first (the root) must touch at least one other part, or it is
///      reported floating with its AABB gap distance.
///   3. Inverted normals: the spec's face-winding sign check,
///      Dot(Nf, Cf - O) &gt; 0, where Nf is the face normal from winding,
///      Cf the face centroid, and O the mesh centroid (mean of the
///      vertices the faces actually use).
///
/// Proportions (spec JSON shape): when a head height and a total height
/// are supplied, HeadRatio = head / total and Status is "nominal" for
/// the spec's 0.13..0.16 head band, else "off". With no heights the
/// result is "unknown" — the linter never invents a head measurement
/// from a bare mesh. This is deliberately NOT Proportions.cs: that file
/// owns the canonical scaffold bands; this file only reports the one
/// head_ratio / status pair the lint JSON shape carries.
///
/// Honest limits (read before trusting a green run):
///   - Touch is an AABB-distance proxy (gap &lt;= tolerance), NOT the
///     spec's ray-BVH test: two parts whose boxes touch but whose
///     surfaces do not (e.g. diagonal neighbours inside one box gap)
///     can pass, and a part touching only at a single shared vertex
///     counts as one body. Both are cheap, deterministic, and linear.
///   - The centroid winding test is exact for convex / star-shaped
///     meshes and a heuristic elsewhere: a correctly wound face on a
///     strongly non-convex mesh can point away from the centroid and
///     be flagged. The flag names the face indices so a human can tell.
///   - Euler uses USED vertices only (a vertex no face references does
///     not change the topology, and is not counted as a body).
///   - Timing is linear in faces + vertices (edge dictionary +
///     union-find); the &lt;5 ms spec figure is for small meshes, and
///     the measured milliseconds ride along in the result so the claim
///     can be checked instead of believed.
///   - Dependency-free apart from the kernel's own Vec3 (Effigy/Vec.cs):
///     no engine types, no JSON library (ToJson is hand-rolled).
/// </summary>
public sealed class LintMesh
{
	public Vec3[] Vertices;
	public int[][] Triangles;

	public LintMesh( Vec3[] vertices, int[][] triangles )
	{
		Vertices = vertices ?? Array.Empty<Vec3>();
		Triangles = triangles ?? Array.Empty<int[]>();
	}
}

public sealed class LintPart
{
	public string Name;
	public LintMesh Mesh;

	public LintPart( string name, LintMesh mesh )
	{
		Name = name ?? "(unnamed)";
		Mesh = mesh;
	}
}

public sealed class LintModel
{
	public List<LintPart> Parts;

	public LintModel( params LintPart[] parts )
	{
		Parts = new List<LintPart>();
		if ( parts is null )
			return;
		foreach ( var part in parts )
			Parts.Add( part );
	}
}

/// <summary>One head_ratio / status pair, exactly the proportions object
/// in the spec's lint JSON.</summary>
public sealed class LintProportions
{
	public double HeadRatio;
	public string Status;
	public bool HasValue;

	public static LintProportions Unknown() =>
		new LintProportions { HeadRatio = double.NaN, Status = "unknown", HasValue = false };

	public static LintProportions FromHeights( double headHeight, double totalHeight )
	{
		if ( !( headHeight > 0 ) || !( totalHeight > 0 ) )
			return Unknown();
		var ratio = headHeight / totalHeight;
		return new LintProportions
		{
			HeadRatio = ratio,
			Status = ratio >= 0.13 && ratio <= 0.16 ? "nominal" : "off",
			HasValue = true,
		};
	}
}

public sealed class LintResult
{
	public bool Ok;
	public List<string> Errors = new List<string>();
	public int BoundaryEdges;
	public int NonManifoldEdges;
	public int Euler;
	public int VertexCount;
	public int FaceCount;
	public int EdgeCount;
	public int InvertedFaces;
	public int DegenerateFaces;
	public int Components;
	public int DisjointBodies;
	public List<string> FloatingParts = new List<string>();
	public double ElapsedMilliseconds;
	public LintProportions Proportions = LintProportions.Unknown();

	/// <summary>Spec JSON shape first (ok / errors / proportions), then
	/// the diagnostic counts. Hand-rolled: no JSON dependency.</summary>
	public string ToJson()
	{
		var sb = new StringBuilder();
		sb.Append( "{\"ok\": " ).Append( Ok ? "true" : "false" );
		sb.Append( ", \"errors\": [" );
		for ( var i = 0; i < Errors.Count; i++ )
		{
			if ( i > 0 )
				sb.Append( ", " );
			AppendJsonString( sb, Errors[i] );
		}
		sb.Append( "], \"proportions\": {" );
		if ( Proportions is null || !Proportions.HasValue )
			sb.Append( "\"head_ratio\": null, \"status\": \"unknown\"" );
		else
			sb.Append( "\"head_ratio\": " )
				.Append( Proportions.HeadRatio.ToString( "0.####", CultureInfo.InvariantCulture ) )
				.Append( ", \"status\": " )
				.Append( "\"" ).Append( Proportions.Status ).Append( "\"" );
		sb.Append( "}, \"boundary_edges\": " ).Append( BoundaryEdges );
		sb.Append( ", \"euler\": " ).Append( Euler );
		sb.Append( ", \"inverted_faces\": " ).Append( InvertedFaces );
		sb.Append( ", \"disjoint_bodies\": " ).Append( DisjointBodies );
		sb.Append( ", \"floating_parts\": [" );
		for ( var i = 0; i < FloatingParts.Count; i++ )
		{
			if ( i > 0 )
				sb.Append( ", " );
			AppendJsonString( sb, FloatingParts[i] );
		}
		sb.Append( "] }" );
		return sb.ToString();
	}

	private static void AppendJsonString( StringBuilder sb, string value )
	{
		sb.Append( '"' );
		if ( value is null )
		{
			sb.Append( '"' );
			return;
		}
		foreach ( var c in value )
		{
			switch ( c )
			{
				case '"': sb.Append( "\\\"" ); break;
				case '\\': sb.Append( "\\\\" ); break;
				case '\n': sb.Append( "\\n" ); break;
				case '\r': sb.Append( "\\r" ); break;
				case '\t': sb.Append( "\\t" ); break;
				default:
					if ( c < 0x20 )
						sb.Append( "\\u" ).Append( ( (int) c ).ToString( "x4", CultureInfo.InvariantCulture ) );
					else
						sb.Append( c );
					break;
			}
		}
		sb.Append( '"' );
	}
}

public static class GeometryLinter
{
	/// <summary>Lint a whole model. headHeight / totalHeight are in the
	/// same units (inches for the canonical 69-inch scaffold); pass 0
	/// for either to leave proportions "unknown".</summary>
	public static LintResult Lint( LintModel model, double headHeight = 0, double totalHeight = 0, double touchTolerance = 1e-4 )
	{
		var sw = Stopwatch.StartNew();
		var result = new LintResult { Proportions = LintProportions.FromHeights( headHeight, totalHeight ) };
		if ( model is null || model.Parts is null || model.Parts.Count == 0 )
		{
			result.Errors.Add( "Model has no parts to lint" );
			result.Ok = false;
			result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
			return result;
		}

		var boxes = new List<(Vec3 min, Vec3 max, bool has)>();
		var expectedEuler = 0;
		foreach ( var part in model.Parts )
		{
			if ( part is null || part.Mesh is null )
			{
				result.Errors.Add( "Model contains a null part or mesh" );
				boxes.Add( ( Vec3.Zero, Vec3.Zero, false ) );
				continue;
			}
			LintPartMesh( part, result, out var partComponents, out var box );
			boxes.Add( box );
			expectedEuler += 2 * Math.Max( partComponents, 0 );
			if ( partComponents > 1 )
			{
				result.DisjointBodies += partComponents - 1;
				result.Errors.Add( $"Mesh '{part.Name}' has {partComponents} disjoint bodies" );
			}
		}

		// Floating parts: every part after the first must touch at least
		// one other part (AABB gap <= tolerance). The first part is the
		// root and is never floating.
		for ( var i = 1; i < model.Parts.Count; i++ )
		{
			if ( !boxes[i].has )
				continue;
			var nearest = double.MaxValue;
			for ( var j = 0; j < model.Parts.Count; j++ )
			{
				if ( j == i || !boxes[j].has )
					continue;
				var gap = AabbGap( boxes[i].min, boxes[i].max, boxes[j].min, boxes[j].max );
				if ( gap < nearest )
					nearest = gap;
			}
			if ( nearest > touchTolerance )
			{
				var name = model.Parts[i]?.Name ?? "(unnamed)";
				result.FloatingParts.Add( name );
				result.Errors.Add( $"Body '{name}' is floating {nearest.ToString( "0.##", CultureInfo.InvariantCulture )} units from the nearest body" );
			}
		}

		if ( result.BoundaryEdges > 0 )
			result.Errors.Add( $"Model has {result.BoundaryEdges} boundary edges (not watertight)" );
		if ( result.NonManifoldEdges > 0 )
			result.Errors.Add( $"Model has {result.NonManifoldEdges} non-manifold edges" );
		if ( result.Euler != expectedEuler )
			result.Errors.Add( $"Model Euler characteristic is {result.Euler}, expected {expectedEuler} for {result.Components} closed bodies" );

		result.Ok = result.Errors.Count == 0;
		result.ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds;
		return result;
	}

	/// <summary>Convenience: lint a single mesh as one part named "Body".</summary>
	public static LintResult Lint( LintMesh mesh, double headHeight = 0, double totalHeight = 0, double touchTolerance = 1e-4 ) =>
		Lint( new LintModel( new LintPart( "Body", mesh ) ), headHeight, totalHeight, touchTolerance );

	private static void LintPartMesh( LintPart part, LintResult result, out int components, out (Vec3 min, Vec3 max, bool has) box )
	{
		components = 0;
		box = ( Vec3.Zero, Vec3.Zero, false );
		var mesh = part.Mesh;
		result.VertexCount += mesh.Vertices.Length;
		if ( mesh.Triangles.Length == 0 )
		{
			result.Errors.Add( $"Mesh '{part.Name}' has no faces" );
			return;
		}

		// First pass: validate faces, collect used vertices, face data.
		var used = new bool[mesh.Vertices.Length];
		var faces = new List<(int a, int b, int c)>();
		var faceIndex = new List<int>();
		for ( var i = 0; i < mesh.Triangles.Length; i++ )
		{
			var tri = mesh.Triangles[i];
			if ( tri is null || tri.Length != 3 )
			{
				result.Errors.Add( $"Mesh '{part.Name}' face {i} has {( tri is null ? 0 : tri.Length )} indices, expected 3" );
				continue;
			}
			if ( tri[0] < 0 || tri[0] >= mesh.Vertices.Length ||
				tri[1] < 0 || tri[1] >= mesh.Vertices.Length ||
				tri[2] < 0 || tri[2] >= mesh.Vertices.Length )
			{
				result.Errors.Add( $"Mesh '{part.Name}' face {i} references a vertex index outside 0..{mesh.Vertices.Length - 1}" );
				continue;
			}
			used[tri[0]] = true;
			used[tri[1]] = true;
			used[tri[2]] = true;
			faces.Add( ( tri[0], tri[1], tri[2] ) );
			faceIndex.Add( i );
		}
		result.FaceCount += faces.Count;

		// Centroid over used vertices only (spec's O).
		var centroid = Vec3.Zero;
		var usedCount = 0;
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		for ( var i = 0; i < mesh.Vertices.Length; i++ )
		{
			if ( !used[i] )
				continue;
			usedCount++;
			centroid += mesh.Vertices[i];
			var v = mesh.Vertices[i];
			min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
			max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
		}
		if ( usedCount > 0 )
		{
			centroid /= usedCount;
			box = ( min, max, true );
		}

		// Edges + connectivity (union-find over shared vertices).
		var edgeUse = new Dictionary<long, int>();
		var parent = new int[mesh.Vertices.Length];
		for ( var i = 0; i < mesh.Vertices.Length; i++ )
			parent[i] = i;
		var inverted = new List<int>();
		var degenerate = 0;
		for ( var f = 0; f < faces.Count; f++ )
		{
			var (a, b, c) = faces[f];
			CountEdge( edgeUse, a, b );
			CountEdge( edgeUse, b, c );
			CountEdge( edgeUse, c, a );
			Union( parent, a, b );
			Union( parent, b, c );

			var va = mesh.Vertices[a];
			var vb = mesh.Vertices[b];
			var vc = mesh.Vertices[c];
			var normal = Vec3.Cross( vb - va, vc - va );
			if ( normal.LengthSquared <= 1e-12f )
			{
				degenerate++;
				continue;
			}
			var faceCentroid = ( va + vb + vc ) / 3f;
			if ( Vec3.Dot( normal, faceCentroid - centroid ) < 0 )
				inverted.Add( faceIndex[f] );
		}

		var partBoundary = 0;
		var partNonManifold = 0;
		foreach ( var use in edgeUse.Values )
		{
			if ( use == 1 )
				partBoundary++;
			else if ( use > 2 )
				partNonManifold++;
		}
		result.BoundaryEdges += partBoundary;
		result.NonManifoldEdges += partNonManifold;
		result.EdgeCount += edgeUse.Count;
		// Euler on used vertices: V - E + F for this part.
		result.Euler += usedCount - edgeUse.Count + faces.Count;

		var roots = new HashSet<int>();
		for ( var i = 0; i < mesh.Vertices.Length; i++ )
			if ( used[i] )
				roots.Add( Find( parent, i ) );
		components = roots.Count;
		result.Components += components;

		if ( inverted.Count > 0 )
		{
			result.InvertedFaces += inverted.Count;
			var first = inverted.Count <= 5 ? inverted : inverted.GetRange( 0, 5 );
			result.Errors.Add( $"Mesh '{part.Name}' has {inverted.Count} inverted faces (first: {string.Join( ", ", first )})" );
		}
		if ( degenerate > 0 )
		{
			result.DegenerateFaces += degenerate;
			result.Errors.Add( $"Mesh '{part.Name}' has {degenerate} degenerate (zero-area) faces" );
		}
	}

	private static void CountEdge( Dictionary<long, int> edgeUse, int a, int b )
	{
		var lo = Math.Min( a, b );
		var hi = Math.Max( a, b );
		var key = ( (long) lo << 32 ) | (uint) hi;
		edgeUse.TryGetValue( key, out var use );
		edgeUse[key] = use + 1;
	}

	private static int Find( int[] parent, int x )
	{
		while ( parent[x] != x )
		{
			parent[x] = parent[parent[x]];
			x = parent[x];
		}
		return x;
	}

	private static void Union( int[] parent, int a, int b )
	{
		var ra = Find( parent, a );
		var rb = Find( parent, b );
		if ( ra != rb )
			parent[ra] = rb;
	}

	private static double AabbGap( Vec3 aMin, Vec3 aMax, Vec3 bMin, Vec3 bMax )
	{
		var gx = Math.Max( Math.Max( bMin.x - aMax.x, aMin.x - bMax.x ), 0f );
		var gy = Math.Max( Math.Max( bMin.y - aMax.y, aMin.y - bMax.y ), 0f );
		var gz = Math.Max( Math.Max( bMin.z - aMax.z, aMin.z - bMax.z ), 0f );
		return Math.Sqrt( gx * gx + gy * gy + gz * gz );
	}
}
