using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy.Map;

// Hammer Desktop P1: VMF + .map (Valve 220) import/export with plane fidelity.
//
// DEPENDENCY STATUS (2026-11-13, verified against the board + the tree):
//   The four tasks this ticket depends on have NOT landed yet:
//     - hammer-brush-core-plane-based-brushes (in-progress, muse-3-desktop-hammer-01):
//       kernel/Effigy/Map/MapBrush.cs does not exist on disk.
//     - hammer-displacement-data-model (in-progress, muse-3-desktop-hammer-08):
//       kernel/Effigy/Map/Displacement.cs does not exist on disk.
//     - hammer-entity-io-logic-list-model (todo): kernel/Effigy/Map/EntityIo.cs
//       does not exist on disk.
//     - hammer-selection-grouping-layers-visgroups-model (todo):
//       kernel/Effigy/Map/MapLayers.cs does not exist on disk.
//   kernel/Effigy/Map/MapDocument.cs (hammer-map-document-model, in-progress)
//   does not exist either. So this file deliberately references NONE of those
//   types: it defines its own transfer model (VmfDocument and friends) that
//   mirrors the filed specs 1:1, and the final integrator adds thin adapters
//   (VmfIo.ToMapDocument / FromMapDocument) once those types land, instead of
//   this file guessing at APIs that do not compile yet.
//
//   Filed-spec mapping (adapter seams, no invented API):
//     VmfFace.PlanePoints[3] + Normal/Distance  <->  MapBrush face plane
//       (normal + distance) + per-face material + UV offset/scale/rotation
//       + lightmap scale + smoothing group. MapBrush.FromPlanes /
//       FromVertices, Validate() are the brush-core spec.
//     VmfDisplacement (power 2/3/4 grid bound to one quad face, offsets,
//       alphas, sew edges)  <->  Displacement.Create(face, power) / Sew /
//       Subdivide / Validate().
//     VmfOutput (event -> target, input, parameter, delay, fire-count)
//       <->  EntityIo output rows + ResolveTargets() (missing/ambiguous).
//       VmfDocument.ResolveOutputs() implements the same contract here.
//     VmfVisGroup (name, colour, id) + brush/entity editor visgroup id
//       <->  MapLayers VisGroup / Layer (eye toggle, compile-visible flag).
//
// PLANE FIDELITY: a VMF side stores its plane as three points. VmfFace keeps
// those three points exactly as parsed and the writer emits them verbatim.
//
// HAMMER DESKTOP P2 — BRUSH ACCURACY (2026-11-13, hammer-brush-accuracy-
// incremental-save): exact-precision vertex serialisation + incremental save.
//   VmfFace.StoredVertices: the face polygon's vertices, stored ALONGSIDE the
//     plane points (never instead of them) and written verbatim with the same
//     round-trip ("R") precision the planes already use. The writer emits them
//     as a "geppetto_vertices" key inside the side block; Hammer ignores
//     unknown keys, and files that never had vertices write no such key, so
//     legacy VMF stays byte-identical. VmfDocument.VerifyStoredVertices /
//     VmfIo.ParseAndVerify check every stored vertex against its plane on
//     load; VmfBrush.ComputeAndStoreVertices derives the polygon from the
//     brush planes (triple intersections inside every half-space) when the
//     caller explicitly wants the no-drift record attached.
//   VmfIo.IncrementalSave(doc, path, keep): rotates the current file into a
//     numbered .bak chain (room.vmf.001.bak = most recent prior version) in
//     the map folder, then writes the new version atomically (same-dir temp +
//     rename), so the only copy is never overwritten and a failed save never
//     leaves a half-written map.
// Planes are NEVER rebuilt from triangulated vertices: a face built from a
// MapBrush must hand its plane points (or normal+distance -> three points on
// the plane) to VmfFace, and the writer writes what the face holds. This is
// what keeps compilers from drifting vertices.
//
// VMF is a FILE FORMAT ONLY here: no Hammer app, no shelling out to
// Hammer/Hammer++/VBSP/VVIS/VRAD. No Desktop UI in this ticket.
// Units: Source/Hammer inches, Z-up, same as the kernel convention.

/// <summary>Named parse failure for VMF and Valve 220 .map input. Carries the 1-based line.</summary>
public sealed class MapParseException : Exception
{
	public string Format { get; }
	public int Line { get; }
	public int LineNumber => Line;

	public MapParseException( string format, int line, string message )
		: base( $"[{format}] line {line}: {message}" )
	{
		Format = format;
		Line = line;
	}
}

/// <summary>A 3D point/vector in Hammer inches, stored as doubles so a parsed plane survives a round-trip exactly.</summary>
public readonly struct VmfVec : IEquatable<VmfVec>
{
	public readonly double X, Y, Z;

	public VmfVec( double x, double y, double z ) { X = x; Y = y; Z = z; }

	public static VmfVec Zero => new VmfVec( 0, 0, 0 );

	public double Length => Math.Sqrt( X * X + Y * Y + Z * Z );
	public VmfVec Normalized => Length < 1e-12 ? Zero : new VmfVec( X / Length, Y / Length, Z / Length );

	public static double Dot( VmfVec a, VmfVec b ) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
	public static VmfVec Cross( VmfVec a, VmfVec b ) => new VmfVec(
		a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X );

	public static VmfVec operator +( VmfVec a, VmfVec b ) => new VmfVec( a.X + b.X, a.Y + b.Y, a.Z + b.Z );
	public static VmfVec operator -( VmfVec a, VmfVec b ) => new VmfVec( a.X - b.X, a.Y - b.Y, a.Z - b.Z );
	public static VmfVec operator *( VmfVec a, double s ) => new VmfVec( a.X * s, a.Y * s, a.Z * s );

	public bool Equals( VmfVec other ) => X == other.X && Y == other.Y && Z == other.Z;
	public override bool Equals( object obj ) => obj is VmfVec v && Equals( v );
	public override int GetHashCode() => HashCode.Combine( X, Y, Z );
	public bool ApproxEquals( VmfVec other, double tolerance = 1e-6 ) =>
		Math.Abs( X - other.X ) <= tolerance && Math.Abs( Y - other.Y ) <= tolerance && Math.Abs( Z - other.Z ) <= tolerance;

	/// <summary>Parse "(x y z)", "[x y z]" or bare "x y z". Throws MapParseException on bad input.</summary>
	public static VmfVec Parse( string text, string format, int line )
	{
		var t = text.Trim().TrimStart( '(', '[' ).TrimEnd( ')', ']' ).Trim();
		var parts = t.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 )
			throw new MapParseException( format, line, $"expected a 3-component vector, got '{text}'" );
		return new VmfVec(
			MapIoText.ParseDouble( parts[0], format, line ),
			MapIoText.ParseDouble( parts[1], format, line ),
			MapIoText.ParseDouble( parts[2], format, line ) );
	}

	public string ToVmfString() => $"( {MapIoText.Num( X )} {MapIoText.Num( Y )} {MapIoText.Num( Z )} )";
	public string ToBracketString() => $"[ {MapIoText.Num( X )} {MapIoText.Num( Y )} {MapIoText.Num( Z )} ]";
	public override string ToString() => ToVmfString();
}

/// <summary>Shared number/text helpers for the VMF and .map readers/writers. Invariant culture throughout.</summary>
internal static class MapIoText
{
	public static string Num( double value )
	{
		if ( double.IsNaN( value ) || double.IsInfinity( value ) ) return "0";
		// "R" prints integral doubles as "64", not "64.0" — what Hammer writes.
		return value.ToString( "R", CultureInfo.InvariantCulture );
	}

	public static double ParseDouble( string text, string format, int line )
	{
		if ( double.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) ) return v;
		throw new MapParseException( format, line, $"expected a number, got '{text}'" );
	}

	public static int ParseInt( string text, string format, int line )
	{
		if ( int.TryParse( text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v ) ) return v;
		if ( double.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d ) ) return (int)Math.Round( d );
		throw new MapParseException( format, line, $"expected an integer, got '{text}'" );
	}

	public static string Quote( string value ) => "\"" + value.Replace( "\\", "\\\\" ).Replace( "\"", "\\\"" ) + "\"";
	public static string Unquote( string value )
	{
		if ( value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"' )
			return value.Substring( 1, value.Length - 2 ).Replace( "\\\"", "\"" ).Replace( "\\\\", "\\" );
		return value;
	}
}

/// <summary>One explicit UV axis: a 3D axis vector + offset, with a scale. VMF "[x y z off] scale" and Valve 220 .map use the same shape.</summary>
public sealed class VmfUvAxis
{
	public VmfVec Axis;
	public double Offset;
	public double Scale;

	public VmfUvAxis() { Axis = new VmfVec( 1, 0, 0 ); Offset = 0; Scale = 0.25; }
	public VmfUvAxis( VmfVec axis, double offset, double scale ) { Axis = axis; Offset = offset; Scale = scale; }

	public string ToVmfString() => $"[ {MapIoText.Num( Axis.X )} {MapIoText.Num( Axis.Y )} {MapIoText.Num( Axis.Z )} {MapIoText.Num( Offset )} ] {MapIoText.Num( Scale )}";

	/// <summary>Bracket only, "[x y z off]" — the Valve 220 .map form, where the scale follows both brackets instead.</summary>
	public string ToBracketString() => $"[ {MapIoText.Num( Axis.X )} {MapIoText.Num( Axis.Y )} {MapIoText.Num( Axis.Z )} {MapIoText.Num( Offset )} ]";

	/// <summary>Parse "[x y z off] scale".</summary>
	public static VmfUvAxis Parse( string text, string format, int line )
	{
		var close = text.IndexOf( ']' );
		if ( !text.TrimStart().StartsWith( "[" ) || close < 0 )
			throw new MapParseException( format, line, $"expected a UV axis '[x y z off] scale', got '{text}'" );
		var inner = text.Substring( 1, close - 1 ).Trim().Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( inner.Length != 4 )
			throw new MapParseException( format, line, $"expected a UV axis '[x y z off] scale', got '{text}'" );
		var axis = new VmfVec(
			MapIoText.ParseDouble( inner[0], format, line ),
			MapIoText.ParseDouble( inner[1], format, line ),
			MapIoText.ParseDouble( inner[2], format, line ) );
		var offset = MapIoText.ParseDouble( inner[3], format, line );
		var scaleText = text.Substring( close + 1 ).Trim();
		var scale = MapIoText.ParseDouble( scaleText, format, line );
		return new VmfUvAxis( axis, offset, scale );
	}

	public VmfUvAxis Clone() => new VmfUvAxis( Axis, Offset, Scale );
	public bool ApproxEquals( VmfUvAxis other, double tolerance = 1e-6 ) =>
		other != null && Axis.ApproxEquals( other.Axis, tolerance )
		&& Math.Abs( Offset - other.Offset ) <= tolerance && Math.Abs( Scale - other.Scale ) <= tolerance;
}

/// <summary>
/// Displacement grid bound to one quad brush face (VMF dispinfo), per the
/// displacement-data-model spec: power 2/3/4, per-vertex offsets along the
/// face normal, per-vertex blend alphas, sew topology via shared edges.
/// Grid size is (2^power + 1) per side. A displacement does NOT seal the map
/// (validation rule, recorded here, not a compile assumption).
/// </summary>
public sealed class VmfDisplacement
{
	public int Power;
	public VmfVec StartPosition;
	public double Elevation;
	public bool Subdiv;
	public double[,] Offsets;
	public int[,] Alphas;
	public VmfVec[,] Normals;       // null when the source omits normals
	public double[,] Distances;     // null when the source omits distances
	public VmfVec[,] OffsetNormals; // null when the source omits offset_normals
	public int[,] TriangleTags;     // null when the source omits triangle_tags
	public bool[] AllowedVerts;     // null when omitted; 10 entries when present

	public int GridSize => ( 1 << Power ) + 1;

	public VmfDisplacement( int power )
	{
		Power = power;
		StartPosition = VmfVec.Zero;
		Offsets = new double[GridSize, GridSize];
		Alphas = new int[GridSize, GridSize];
	}

	public static VmfDisplacement Create( int power, VmfVec startPosition, double elevation = 0 )
	{
		if ( power < 2 || power > 4 ) throw new ArgumentException( $"displacement power must be 2, 3 or 4, got {power}" );
		return new VmfDisplacement( power ) { StartPosition = startPosition, Elevation = elevation };
	}

	/// <summary>Named defects; empty means the grid is consistent. Never silently fixes.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		if ( Power < 2 || Power > 4 ) defects.Add( $"displacement power {Power} out of range 2..4" );
		else
		{
			if ( Offsets == null || Offsets.GetLength( 0 ) != GridSize || Offsets.GetLength( 1 ) != GridSize )
				defects.Add( $"displacement offsets grid is not {GridSize}x{GridSize} for power {Power}" );
			if ( Alphas == null || Alphas.GetLength( 0 ) != GridSize || Alphas.GetLength( 1 ) != GridSize )
				defects.Add( $"displacement alphas grid is not {GridSize}x{GridSize} for power {Power}" );
			if ( Normals != null && ( Normals.GetLength( 0 ) != GridSize || Normals.GetLength( 1 ) != GridSize ) )
				defects.Add( $"displacement normals grid is not {GridSize}x{GridSize} for power {Power}" );
			if ( OffsetNormals != null && ( OffsetNormals.GetLength( 0 ) != GridSize || OffsetNormals.GetLength( 1 ) != GridSize ) )
				defects.Add( $"displacement offset_normals grid is not {GridSize}x{GridSize} for power {Power}" );
			if ( Distances != null && ( Distances.GetLength( 0 ) != GridSize || Distances.GetLength( 1 ) != GridSize ) )
				defects.Add( $"displacement distances grid is not {GridSize}x{GridSize} for power {Power}" );
		}
		if ( AllowedVerts != null && AllowedVerts.Length != 10 ) defects.Add( $"displacement allowed_verts has {AllowedVerts.Length} entries, expected 10" );
		return defects;
	}

	public VmfDisplacement Clone()
	{
		var c = new VmfDisplacement( Power )
		{
			StartPosition = StartPosition, Elevation = Elevation, Subdiv = Subdiv,
			Offsets = (double[,])Offsets.Clone(), Alphas = (int[,])Alphas.Clone(),
			Normals = Normals == null ? null : (VmfVec[,])Normals.Clone(),
			Distances = Distances == null ? null : (double[,])Distances.Clone(),
			OffsetNormals = OffsetNormals == null ? null : (VmfVec[,])OffsetNormals.Clone(),
			TriangleTags = TriangleTags == null ? null : (int[,])TriangleTags.Clone(),
			AllowedVerts = AllowedVerts == null ? null : (bool[])AllowedVerts.Clone(),
		};
		return c;
	}
}

/// <summary>
/// One face (side) of a brush: a plane as three points, a material, explicit
/// UV axes, rotation, lightmap scale and smoothing group, plus an optional
/// displacement. The three plane points are the face's identity — they are
/// stored exactly as parsed and written exactly as stored.
/// </summary>
public sealed class VmfFace
{
	public int Id;
	public VmfVec[] PlanePoints = new VmfVec[3];
	public string Material = "";
	public VmfUvAxis UAxis = new VmfUvAxis( new VmfVec( 1, 0, 0 ), 0, 0.25 );
	public VmfUvAxis VAxis = new VmfUvAxis( new VmfVec( 0, -1, 0 ), 0, 0.25 );
	public double Rotation;
	public int LightmapScale = 16;
	public int SmoothingGroups;
	public VmfDisplacement Displacement;
	/// <summary>True when the UVs came from a legacy (non-220) .map face line, so Map220Io writes the legacy form back.</summary>
	public bool UvFromStandardMap;

	/// <summary>Exact polygon vertices stored alongside the plane points (Hammer
	/// Desktop P2: brush accuracy). The plane stays authoritative for the
	/// compiler; these vertices are the no-drift record: written verbatim with
	/// full round-trip precision and verified against the plane on load.
	/// Empty when the source never carried them (plain Hammer VMF).</summary>
	public List<VmfVec> StoredVertices = new List<VmfVec>();

	/// <summary>True when this face carries stored vertices to verify.</summary>
	public bool HasStoredVertices => StoredVertices.Count > 0;

	/// <summary>Verify the stored vertices against this face's plane: at least
	/// three vertices, every vertex on the plane within <paramref name="tolerance"/>
	/// inches, and a non-degenerate polygon. An empty list means the face verifies
	/// (or carries no stored vertices, which is legal legacy input).</summary>
	public List<string> VerifyStoredVertices( double tolerance = 1e-6 )
	{
		var issues = new List<string>();
		if ( StoredVertices.Count == 0 ) return issues;
		if ( StoredVertices.Count < 3 )
		{
			issues.Add( $"face #{Id} stores {StoredVertices.Count} vertices, a polygon needs at least 3" );
			return issues;
		}
		var (n, d) = ToPlane();
		if ( n.Equals( VmfVec.Zero ) )
		{
			issues.Add( $"face #{Id} has a degenerate plane, stored vertices cannot be verified" );
			return issues;
		}
		for ( int i = 0; i < StoredVertices.Count; i++ )
		{
			var gap = Math.Abs( VmfVec.Dot( n, StoredVertices[i] ) - d );
			if ( gap > tolerance ) issues.Add( $"face #{Id} stored vertex {i} {StoredVertices[i].ToVmfString()} is {gap:G17} off the plane (tolerance {tolerance:G17})" );
		}
		var area2 = VmfVec.Zero;
		for ( int i = 0; i < StoredVertices.Count; i++ )
		{
			var a = StoredVertices[i];
			var b = StoredVertices[( i + 1 ) % StoredVertices.Count];
			area2 = area2 + VmfVec.Cross( a, b );
		}
		if ( area2.Length < 1e-9 ) issues.Add( $"face #{Id} stored vertices form a degenerate (zero-area) polygon" );
		return issues;
	}

	/// <summary>Unit plane normal from the three plane points (right-hand rule).</summary>
	public VmfVec Normal
	{
		get
		{
			var n = VmfVec.Cross( PlanePoints[1] - PlanePoints[0], PlanePoints[2] - PlanePoints[0] );
			return n.Length < 1e-12 ? VmfVec.Zero : n.Normalized;
		}
	}

	/// <summary>Plane distance d in n.x = d.</summary>
	public double Distance => VmfVec.Dot( Normal, PlanePoints[0] );

	/// <summary>True when the three plane points are collinear / coincident (no plane).</summary>
	public bool IsDegeneratePlane => VmfVec.Cross( PlanePoints[1] - PlanePoints[0], PlanePoints[2] - PlanePoints[0] ).Length < 1e-9;

	public (VmfVec Normal, double Distance) ToPlane() => (Normal, Distance);

	public bool PlanePointsEqual( VmfFace other, double tolerance = 1e-9 ) =>
		other != null
		&& PlanePoints[0].ApproxEquals( other.PlanePoints[0], tolerance )
		&& PlanePoints[1].ApproxEquals( other.PlanePoints[1], tolerance )
		&& PlanePoints[2].ApproxEquals( other.PlanePoints[2], tolerance );

	public VmfFace Clone() => new VmfFace
	{
		Id = Id,
		PlanePoints = (VmfVec[])PlanePoints.Clone(),
		Material = Material,
		UAxis = UAxis.Clone(),
		VAxis = VAxis.Clone(),
		Rotation = Rotation,
		LightmapScale = LightmapScale,
		SmoothingGroups = SmoothingGroups,
		Displacement = Displacement == null ? null : Displacement.Clone(),
		UvFromStandardMap = UvFromStandardMap,
		StoredVertices = StoredVertices.Select( v => new VmfVec( v.X, v.Y, v.Z ) ).ToList(),
	};
}

/// <summary>A convex brush solid: a list of plane faces. Mirrors the filed MapBrush spec (faces as planes + material + UV; validity via VmfDocument.Validate).</summary>
public sealed class VmfBrush
{
	public int Id;
	public List<VmfFace> Faces = new List<VmfFace>();
	public string EditorColor = "";
	public int VisGroupId;
	public int GroupId;
	public bool VisGroupShown = true;
	public bool VisGroupAutoShown = true;

	public bool HasDisplacement => Faces.Any( f => f.Displacement != null );

	/// <summary>Derive each face's polygon vertices from the brush planes
	/// (triple-plane intersections kept only when they satisfy every face's
	/// half-space) and store them on the faces as the no-drift record.
	/// Call explicitly before writing: plain Write() never invents vertices,
	/// so a brush that never had them stays a plain Hammer brush. Faces on a
	/// brush with fewer than 4 faces, or whose polygon cannot be solved, keep
	/// whatever stored vertices they already had (usually none).</summary>
	public void ComputeAndStoreVertices()
	{
		if ( Faces.Count < 4 ) return;
		foreach ( var face in Faces )
		{
			var verts = SolveFacePolygon( face );
			if ( verts != null ) face.StoredVertices = verts;
		}
	}

	/// <summary>All stored-vertex issues across this brush's faces, prefixed with the brush id.</summary>
	public List<string> VerifyStoredVertices( double tolerance = 1e-6 )
	{
		var issues = new List<string>();
		foreach ( var f in Faces )
			foreach ( var issue in f.VerifyStoredVertices( tolerance ) )
				issues.Add( $"brush #{Id} {issue}" );
		return issues;
	}

	/// <summary>Solve one face's polygon from the brush planes, or null when it
	/// cannot be solved (degenerate plane, or no triple intersection survives
	/// the half-space test). Vertices are ordered counter-clockwise seen from
	/// outside the brush (from the tip of the face normal).</summary>
	static List<VmfVec> SolveFacePolygon( VmfFace face, VmfBrush brush )
	{
		var n = face.Normal;
		if ( n.Equals( VmfVec.Zero ) ) return null;
		var found = new List<VmfVec>();
		for ( int gi = 0; gi < brush.Faces.Count; gi++ )
		for ( int hi = gi + 1; hi < brush.Faces.Count; hi++ )
		{
			var g = brush.Faces[gi];
			var h = brush.Faces[hi];
			if ( ReferenceEquals( g, face ) || ReferenceEquals( h, face ) ) continue;
			var p = IntersectThreePlanes( face, g, h );
			if ( p == null ) continue;
			var point = p.Value;
			bool inside = true;
			foreach ( var other in brush.Faces )
			{
				var (on, od) = other.ToPlane();
				if ( on.Equals( VmfVec.Zero ) ) { inside = false; break; }
				if ( VmfVec.Dot( on, point ) > od + 1e-6 ) { inside = false; break; }
			}
			if ( !inside ) continue;
			bool dup = false;
			foreach ( var q in found ) if ( ( q - point ).Length < 1e-6 ) { dup = true; break; }
			if ( !dup ) found.Add( point );
		}
		if ( found.Count < 3 ) return null;
		var centroid = VmfVec.Zero;
		foreach ( var v in found ) centroid = centroid + v;
		centroid = centroid * ( 1.0 / found.Count );
		var u = ( face.PlanePoints[1] - face.PlanePoints[0] ).Normalized;
		if ( u.Equals( VmfVec.Zero ) ) return null;
		var w = VmfVec.Cross( n, u );
		found.Sort( ( a, b ) =>
		{
			var aa = Math.Atan2( VmfVec.Dot( a - centroid, w ), VmfVec.Dot( a - centroid, u ) );
			var bb = Math.Atan2( VmfVec.Dot( b - centroid, w ), VmfVec.Dot( b - centroid, u ) );
			return aa.CompareTo( bb );
		} );
		return found;
	}

	List<VmfVec> SolveFacePolygon( VmfFace face ) => SolveFacePolygon( face, this );

	/// <summary>Intersection of three face planes (n.x = d each), or null when
	/// the planes are parallel / coincident (determinant near zero).</summary>
	static VmfVec? IntersectThreePlanes( VmfFace a, VmfFace b, VmfFace c )
	{
		var (n1, d1) = a.ToPlane();
		var (n2, d2) = b.ToPlane();
		var (n3, d3) = c.ToPlane();
		var det = VmfVec.Dot( n1, VmfVec.Cross( n2, n3 ) );
		if ( Math.Abs( det ) < 1e-12 ) return null;
		var p = VmfVec.Cross( n2, n3 ) * d1 + VmfVec.Cross( n3, n1 ) * d2 + VmfVec.Cross( n1, n2 ) * d3;
		return p * ( 1.0 / det );
	}

	public VmfBrush Clone()
	{
		var c = new VmfBrush { Id = Id, EditorColor = EditorColor, VisGroupId = VisGroupId, GroupId = GroupId, VisGroupShown = VisGroupShown, VisGroupAutoShown = VisGroupAutoShown };
		foreach ( var f in Faces ) c.Faces.Add( f.Clone() );
		return c;
	}
}

/// <summary>
/// One entity I/O row: when <see cref="Event"/> fires on the source entity,
/// send <see cref="Input"/> to <see cref="Target"/> after <see cref="Delay"/>
/// seconds. Mirrors the filed EntityIo spec (Output/Input row, delay,
/// fire-once / fire-count). FireCount -1 means fire every time (VMF "-1").
/// </summary>
public sealed class VmfOutput
{
	public string Event = "";
	public string Target = "";
	public string Input = "";
	public string Parameter = "";
	public double Delay;
	public int FireCount = -1;

	/// <summary>VMF connections value: "target,input,parameter,delay,firecount".</summary>
	public string ToVmfString() =>
		$"{Target},{Input},{Parameter},{MapIoText.Num( Delay )},{FireCount}";

	/// <summary>Parse a VMF connections value. Throws MapParseException naming the line.</summary>
	public static VmfOutput Parse( string evt, string value, int line )
	{
		var parts = value.Split( ',' );
		if ( parts.Length != 5 )
			throw new MapParseException( "vmf", line, $"output '{evt}' value '{value}' is not 'target,input,parameter,delay,firecount'" );
		return new VmfOutput
		{
			Event = evt,
			Target = parts[0],
			Input = parts[1],
			Parameter = parts[2],
			Delay = MapIoText.ParseDouble( parts[3], "vmf", line ),
			FireCount = MapIoText.ParseInt( parts[4], "vmf", line ),
		};
	}

	public VmfOutput Clone() => new VmfOutput { Event = Event, Target = Target, Input = Input, Parameter = Parameter, Delay = Delay, FireCount = FireCount };
}

/// <summary>An entity: classname + ordered keyvalues + I/O outputs + optional tied brushes (brush entities). Point entities have no brushes.</summary>
public sealed class VmfEntity
{
	public int Id;
	public string ClassName = "";
	public List<KeyValuePair<string, string>> KeyValues = new List<KeyValuePair<string, string>>();
	public List<VmfOutput> Outputs = new List<VmfOutput>();
	public List<VmfBrush> Brushes = new List<VmfBrush>();
	public string EditorColor = "";
	public int VisGroupId;
	public int GroupId;

	public bool IsPointEntity => Brushes.Count == 0;

	public string Get( string key )
	{
		foreach ( var kv in KeyValues ) if ( kv.Key == key ) return kv.Value;
		return null;
	}

	public string TargetName => Get( "targetname" );

	public void Set( string key, string value )
	{
		for ( int i = 0; i < KeyValues.Count; i++ )
			if ( KeyValues[i].Key == key ) { KeyValues[i] = new KeyValuePair<string, string>( key, value ); return; }
		KeyValues.Add( new KeyValuePair<string, string>( key, value ) );
	}

	public VmfEntity Clone()
	{
		var c = new VmfEntity { Id = Id, ClassName = ClassName, EditorColor = EditorColor, VisGroupId = VisGroupId, GroupId = GroupId };
		c.KeyValues.AddRange( KeyValues );
		foreach ( var o in Outputs ) c.Outputs.Add( o.Clone() );
		foreach ( var b in Brushes ) c.Brushes.Add( b.Clone() );
		return c;
	}
}

/// <summary>A named, coloured visgroup (MapLayers spec: name, colour, id; visibility/compile-visible live on the layer model, not in the VMF visgroup block).</summary>
public sealed class VmfVisGroup
{
	public int Id;
	public string Name = "";
	public string Color = "255 255 255";

	public VmfVisGroup Clone() => new VmfVisGroup { Id = Id, Name = Name, Color = Color };
}

public sealed class VmfCamera
{
	public VmfVec Position;
	public VmfVec Look;

	public VmfCamera Clone() => new VmfCamera { Position = Position, Look = Look };
}

/// <summary>A generic VMF block preserved verbatim through a round-trip (viewsettings, and anything a future Hammer version adds). Known blocks are typed; unknown ones ride here.</summary>
public sealed class VmfBlock
{
	public string Name = "";
	public List<KeyValuePair<string, string>> Props = new List<KeyValuePair<string, string>>();
	public List<VmfBlock> Children = new List<VmfBlock>();
	public int Line;

	public VmfBlock Clone()
	{
		var c = new VmfBlock { Name = Name, Line = Line };
		c.Props.AddRange( Props );
		foreach ( var ch in Children ) c.Children.Add( ch.Clone() );
		return c;
	}
}

/// <summary>
/// The in-memory map for VMF / Valve 220 .map exchange: worldspawn brushes +
/// keyvalues, entities (point + brush, with I/O), visgroups, cameras, cordon,
/// and any unrecognised top-level blocks preserved verbatim. This is the
/// transfer model both readers produce and both writers consume, so a map
/// can round-trip VMF -&gt; VmfDocument -&gt; VMF and cross-convert VMF &lt;-&gt; .map.
/// </summary>
public sealed class VmfDocument
{
	public string EditorVersion = "400";
	public string EditorBuild = "8245";
	public int MapVersion = 1;
	public int FormatVersion = 100;
	public bool Prefab;

	public List<KeyValuePair<string, string>> WorldspawnKeyValues = new List<KeyValuePair<string, string>>();
	public List<VmfBrush> WorldBrushes = new List<VmfBrush>();
	public List<VmfEntity> Entities = new List<VmfEntity>();
	public List<VmfVisGroup> VisGroups = new List<VmfVisGroup>();
	public List<VmfCamera> Cameras = new List<VmfCamera>();
	public int ActiveCamera;
	public bool HasCordon;
	public VmfVec CordonMins;
	public VmfVec CordonMaxs;
	public bool CordonActive;
	public List<VmfBlock> OtherBlocks = new List<VmfBlock>();

	public string WorldspawnGet( string key )
	{
		foreach ( var kv in WorldspawnKeyValues ) if ( kv.Key == key ) return kv.Value;
		return null;
	}

	public void WorldspawnSet( string key, string value )
	{
		for ( int i = 0; i < WorldspawnKeyValues.Count; i++ )
			if ( WorldspawnKeyValues[i].Key == key ) { WorldspawnKeyValues[i] = new KeyValuePair<string, string>( key, value ); return; }
		WorldspawnKeyValues.Add( new KeyValuePair<string, string>( key, value ) );
	}

	public IEnumerable<VmfBrush> AllBrushes()
	{
		foreach ( var b in WorldBrushes ) yield return b;
		foreach ( var e in Entities ) foreach ( var b in e.Brushes ) yield return b;
	}

	/// <summary>Entities whose targetname is <paramref name="name"/> (the I/O resolution set).</summary>
	public List<VmfEntity> FindEntitiesByTargetName( string name )
	{
		var found = new List<VmfEntity>();
		if ( string.IsNullOrEmpty( name ) ) return found;
		foreach ( var e in Entities ) if ( e.TargetName == name ) found.Add( e );
		return found;
	}

	/// <summary>
	/// EntityIo ResolveTargets() contract at document level: every output's
	/// target must resolve to exactly one entity. Engine keywords ("!self",
	/// "!player", "!activator", ...) are not targetnames and are skipped.
	/// Returns (missing, ambiguous) as human-readable descriptions.
	/// </summary>
	public (List<string> Missing, List<string> Ambiguous) ResolveOutputs()
	{
		var missing = new List<string>();
		var ambiguous = new List<string>();
		foreach ( var e in Entities )
			foreach ( var o in e.Outputs )
			{
				if ( string.IsNullOrEmpty( o.Target ) ) { missing.Add( $"'{e.ClassName}'#{e.Id} output '{o.Event}' has an empty target" ); continue; }
				if ( o.Target.StartsWith( "!", StringComparison.Ordinal ) ) continue;
				var targets = FindEntitiesByTargetName( o.Target );
				if ( targets.Count == 0 ) missing.Add( $"'{e.ClassName}'#{e.Id} output '{o.Event}' -> target '{o.Target}' not found" );
				else if ( targets.Count > 1 ) ambiguous.Add( $"'{e.ClassName}'#{e.Id} output '{o.Event}' -> target '{o.Target}' matches {targets.Count} entities" );
			}
		return (missing, ambiguous);
	}

	/// <summary>Structural defects; empty means the document is internally consistent. Never silently fixes.</summary>
	public List<string> Validate()
	{
		var defects = new List<string>();
		var ids = new HashSet<int>();
		foreach ( var b in AllBrushes() )
		{
			if ( !ids.Add( b.Id ) ) defects.Add( $"duplicate brush id {b.Id}" );
			if ( b.Faces.Count < 4 ) defects.Add( $"brush #{b.Id} has {b.Faces.Count} faces, a convex solid needs at least 4" );
			foreach ( var f in b.Faces )
			{
				if ( f.IsDegeneratePlane ) defects.Add( $"brush #{b.Id} face #{f.Id} has a degenerate plane (collinear points)" );
				if ( string.IsNullOrEmpty( f.Material ) ) defects.Add( $"brush #{b.Id} face #{f.Id} has no material" );
				if ( f.Displacement != null ) defects.AddRange( f.Displacement.Validate().Select( d => $"brush #{b.Id} face #{f.Id}: {d}" ) );
			}
		}
		var entityIds = new HashSet<int>();
		foreach ( var e in Entities )
		{
			if ( !entityIds.Add( e.Id ) ) defects.Add( $"duplicate entity id {e.Id}" );
			if ( string.IsNullOrEmpty( e.ClassName ) ) defects.Add( $"entity #{e.Id} has no classname" );
		}
		var vgIds = new HashSet<int>();
		foreach ( var v in VisGroups ) if ( !vgIds.Add( v.Id ) ) defects.Add( $"duplicate visgroup id {v.Id}" );
		return defects;
	}

	/// <summary>Derive and store exact polygon vertices for every brush in the
	/// document (see <see cref="VmfBrush.ComputeAndStoreVertices"/>). Call before
	/// writing when the no-drift record is wanted; a document that never calls
	/// this writes plain Hammer VMF.</summary>
	public void AttachStoredVertices()
	{
		foreach ( var b in AllBrushes() ) b.ComputeAndStoreVertices();
	}

	/// <summary>Verify every stored vertex in the document against its face
	/// plane. Empty list means everything verifies (or nothing is stored).</summary>
	public List<string> VerifyStoredVertices( double tolerance = 1e-6 )
	{
		var issues = new List<string>();
		foreach ( var b in AllBrushes() ) issues.AddRange( b.VerifyStoredVertices( tolerance ) );
		return issues;
	}

	public VmfDocument Clone()
	{
		var c = new VmfDocument
		{
			EditorVersion = EditorVersion, EditorBuild = EditorBuild, MapVersion = MapVersion,
			FormatVersion = FormatVersion, Prefab = Prefab, ActiveCamera = ActiveCamera,
			HasCordon = HasCordon, CordonMins = CordonMins, CordonMaxs = CordonMaxs, CordonActive = CordonActive,
		};
		c.WorldspawnKeyValues.AddRange( WorldspawnKeyValues );
		foreach ( var b in WorldBrushes ) c.WorldBrushes.Add( b.Clone() );
		foreach ( var e in Entities ) c.Entities.Add( e.Clone() );
		foreach ( var v in VisGroups ) c.VisGroups.Add( v.Clone() );
		foreach ( var cam in Cameras ) c.Cameras.Add( cam.Clone() );
		foreach ( var b in OtherBlocks ) c.OtherBlocks.Add( b.Clone() );
		return c;
	}
}

/// <summary>VMF reader/writer. Plane-based: sides are read and written as three plane points; nothing is rebuilt from triangulated vertices.</summary>
public static class VmfIo
{
	public static VmfDocument Parse( string text )
	{
		if ( text == null ) throw new ArgumentNullException( nameof( text ) );
		var blocks = new VmfLexer( text ).ParseTopLevel();
		return BuildDocument( blocks );
	}

	public static VmfDocument ParseFile( string path ) => Parse( File.ReadAllText( path ) );

	public static string Write( VmfDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		var sb = new StringBuilder();
		WriteVersionInfo( sb, doc );
		WriteVisGroups( sb, doc );
		WriteBlock( sb, doc.OtherBlocks, 0 );
		WriteWorld( sb, doc );
		foreach ( var e in doc.Entities ) WriteEntity( sb, e );
		WriteCameras( sb, doc );
		WriteCordon( sb, doc );
		return sb.ToString();
	}

	public static void WriteFile( VmfDocument doc, string path ) => File.WriteAllText( path, Write( doc ) );

	/// <summary>Parse, then verify every stored vertex against its face plane.
	/// The issues list is empty when the file verifies (or stores no vertices);
	/// a non-empty list is a report, never an exception, so a caller can decide
	/// whether drift is fatal or just a warning.</summary>
	public static (VmfDocument Document, List<string> Issues) ParseAndVerify( string text, double tolerance = 1e-6 )
	{
		var doc = Parse( text );
		return (doc, doc.VerifyStoredVertices( tolerance ));
	}

	/// <summary>Parse a file, then verify its stored vertices (see <see cref="ParseAndVerify"/>).</summary>
	public static (VmfDocument Document, List<string> Issues) ParseFileAndVerify( string path, double tolerance = 1e-6 ) =>
		ParseAndVerify( File.ReadAllText( path ), tolerance );

	/// <summary>Backup name for the <paramref name="number"/>-th prior version
	/// of a map file: <c>room.vmf.001.bak</c> is the most recent prior version,
	/// <c>.002.bak</c> the one before it, and so on. Lives in the map folder,
	/// next to the map itself.</summary>
	public static string BackupPath( string path, int number ) => $"{path}.{number:D3}.bak";

	/// <summary>Existing numbered backups of a map file, oldest first
	/// (highest number first in the list order is avoided: index 0 is the
	/// OLDEST version, the last entry the most recent prior version).</summary>
	public static List<string> ListIncrementalBackups( string path )
	{
		var found = new List<(int Number, string Path)>();
		var dir = Path.GetDirectoryName( Path.GetFullPath( path ) );
		var stem = Path.GetFileName( path ) + ".";
		if ( Directory.Exists( dir ) )
			foreach ( var file in Directory.GetFiles( dir, stem + "*.bak" ) )
			{
				var tail = Path.GetFileName( file ).Substring( stem.Length );
				if ( tail.Length == 7 && tail.EndsWith( ".bak", StringComparison.Ordinal )
					&& int.TryParse( tail.Substring( 0, 3 ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n ) )
					found.Add( (n, file) );
			}
		found.Sort( ( a, b ) => b.Number.CompareTo( a.Number ) );
		return found.Select( f => f.Path ).ToList();
	}

	/// <summary>Incremental save (3ds Max / JACK style): the current file, when
	/// it exists, is rotated into the numbered <c>.bak</c> chain in the map
	/// folder before the new version is written, so the only copy is never
	/// overwritten. <paramref name="keep"/> is how many prior versions are
	/// kept (default 5); 0 keeps no backups and just overwrites atomically.
	/// The new version itself is written via a same-directory temp file plus
	/// atomic rename, so a failed save never leaves a half-written map and
	/// never clears the previous version.</summary>
	public static IncrementalSaveResult IncrementalSave( VmfDocument doc, string path, int keep = 5 )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "A map path is required.", nameof( path ) );
		if ( keep < 0 ) throw new ArgumentOutOfRangeException( nameof( keep ), "Keep must be 0 (no backups) or positive." );
		var full = Path.GetFullPath( path );
		var dir = Path.GetDirectoryName( full );
		Directory.CreateDirectory( dir );
		bool createdBackup = false;
		if ( keep > 0 && File.Exists( full ) )
		{
			var oldest = BackupPath( full, keep );
			if ( File.Exists( oldest ) ) File.Delete( oldest );
			for ( int i = keep - 1; i >= 1; i-- )
			{
				var from = BackupPath( full, i );
				if ( File.Exists( from ) ) File.Move( from, BackupPath( full, i + 1 ) );
			}
			File.Copy( full, BackupPath( full, 1 ) );
			createdBackup = true;
		}
		var temp = full + ".tmp-" + Guid.NewGuid().ToString( "N" );
		try
		{
			File.WriteAllText( temp, Write( doc ) );
			File.Move( temp, full, overwrite: true );
		}
		finally
		{
			if ( File.Exists( temp ) ) File.Delete( temp );
		}
		return new IncrementalSaveResult
		{
			Path = full,
			Backups = ListIncrementalBackups( full ),
			CreatedBackup = createdBackup,
		};
	}

	/// <summary>Result of <see cref="VmfIo.IncrementalSave"/>: the saved path,
	/// the backup chain that now exists (oldest first), and whether a prior
	/// version was rotated into the chain by this save.</summary>
	public sealed class IncrementalSaveResult
	{
		public string Path = "";
		public IReadOnlyList<string> Backups = Array.Empty<string>();
		public bool CreatedBackup;
		public int BackupsKept => Backups.Count;
	}

	// ----- reading -----

	static VmfDocument BuildDocument( List<VmfBlock> blocks )
	{
		var doc = new VmfDocument();
		foreach ( var b in blocks )
		{
			switch ( b.Name )
			{
				case "versioninfo":
					doc.EditorVersion = Prop( b, "editorversion", doc.EditorVersion );
					doc.EditorBuild = Prop( b, "editorbuild", doc.EditorBuild );
					doc.MapVersion = IntProp( b, "mapversion", doc.MapVersion );
					doc.FormatVersion = IntProp( b, "formatversion", doc.FormatVersion );
					doc.Prefab = Prop( b, "prefab", "0" ) != "0";
					break;
				case "visgroups":
					foreach ( var vg in b.Children )
					{
						if ( vg.Name != "visgroup" ) throw new MapParseException( "vmf", vg.Line, $"unexpected block '{vg.Name}' inside visgroups" );
						doc.VisGroups.Add( new VmfVisGroup
						{
							Name = Prop( vg, "name", "" ),
							Id = IntProp( vg, "visgroupid", 0 ),
							Color = Prop( vg, "color", "255 255 255" ),
						} );
					}
					break;
				case "world":
					ParseWorld( doc, b );
					break;
				case "entity":
					doc.Entities.Add( ParseEntity( b ) );
					break;
				case "cameras":
					doc.ActiveCamera = IntProp( b, "activecamera", 0 );
					foreach ( var cam in b.Children )
					{
						if ( cam.Name != "camera" ) throw new MapParseException( "vmf", cam.Line, $"unexpected block '{cam.Name}' inside cameras" );
						doc.Cameras.Add( new VmfCamera
						{
							Position = VmfVec.Parse( Prop( cam, "position", "[ 0 0 0 ]" ), "vmf", cam.Line ),
							Look = VmfVec.Parse( Prop( cam, "look", "[ 0 0 0 ]" ), "vmf", cam.Line ),
						} );
					}
					break;
				case "cordon":
					doc.HasCordon = true;
					doc.CordonMins = VmfVec.Parse( Prop( b, "mins", "( 0 0 0 )" ), "vmf", b.Line );
					doc.CordonMaxs = VmfVec.Parse( Prop( b, "maxs", "( 0 0 0 )" ), "vmf", b.Line );
					doc.CordonActive = Prop( b, "active", "0" ) != "0";
					break;
				default:
					doc.OtherBlocks.Add( b );
					break;
			}
		}
		return doc;
	}

	static void ParseWorld( VmfDocument doc, VmfBlock world )
	{
		foreach ( var kv in world.Props )
		{
			if ( kv.Key == "id" || kv.Key == "mapversion" || kv.Key == "classname" ) continue;
			doc.WorldspawnKeyValues.Add( kv );
		}
		foreach ( var child in world.Children )
		{
			if ( child.Name == "solid" ) doc.WorldBrushes.Add( ParseSolid( child ) );
			else if ( child.Name == "group" )
			{
				// A group in world holds solids; flatten it, keeping each brush.
				foreach ( var g in child.Children ) if ( g.Name == "solid" ) doc.WorldBrushes.Add( ParseSolid( g ) );
			}
			else throw new MapParseException( "vmf", child.Line, $"unexpected block '{child.Name}' inside world" );
		}
	}

	static VmfBrush ParseSolid( VmfBlock solid )
	{
		var brush = new VmfBrush { Id = IntProp( solid, "id", 0 ) };
		foreach ( var child in solid.Children )
		{
			if ( child.Name == "side" ) brush.Faces.Add( ParseSide( child ) );
			else if ( child.Name == "editor" ) ParseEditor( child, brush );
			else throw new MapParseException( "vmf", child.Line, $"unexpected block '{child.Name}' inside solid" );
		}
		if ( brush.Faces.Count == 0 ) throw new MapParseException( "vmf", solid.Line, $"solid #{brush.Id} has no sides" );
		return brush;
	}

	static void ParseEditor( VmfBlock editor, VmfBrush brush )
	{
		brush.EditorColor = Prop( editor, "color", brush.EditorColor );
		brush.VisGroupId = IntProp( editor, "visgroupid", brush.VisGroupId );
		brush.GroupId = IntProp( editor, "groupid", brush.GroupId );
		brush.VisGroupShown = Prop( editor, "visgroupshown", "1" ) != "0";
		brush.VisGroupAutoShown = Prop( editor, "visgroupautoshown", "1" ) != "0";
	}

	static void ParseEditor( VmfBlock editor, VmfEntity entity )
	{
		entity.EditorColor = Prop( editor, "color", entity.EditorColor );
		entity.VisGroupId = IntProp( editor, "visgroupid", entity.VisGroupId );
		entity.GroupId = IntProp( editor, "groupid", entity.GroupId );
	}

	static VmfFace ParseSide( VmfBlock side )
	{
		var face = new VmfFace { Id = IntProp( side, "id", 0 ) };
		var plane = Prop( side, "plane", null );
		if ( plane == null ) throw new MapParseException( "vmf", side.Line, $"side #{face.Id} has no plane" );
		face.PlanePoints = ParsePlanePoints( plane, side.Line );
		face.Material = Prop( side, "material", "" );
		var uaxis = Prop( side, "uaxis", null );
		if ( uaxis != null ) face.UAxis = VmfUvAxis.Parse( uaxis, "vmf", side.Line );
		var vaxis = Prop( side, "vaxis", null );
		if ( vaxis != null ) face.VAxis = VmfUvAxis.Parse( vaxis, "vmf", side.Line );
		face.Rotation = DoubleProp( side, "rotation", 0 );
		var storedVerts = Prop( side, "geppetto_vertices", null );
		if ( storedVerts != null ) face.StoredVertices = ParseVecList( storedVerts, side.Line );
		face.LightmapScale = IntProp( side, "lightmapscale", 16 );
		face.SmoothingGroups = IntProp( side, "smoothing_groups", 0 );
		foreach ( var child in side.Children )
		{
			if ( child.Name == "dispinfo" ) face.Displacement = ParseDispInfo( child );
			else throw new MapParseException( "vmf", child.Line, $"unexpected block '{child.Name}' inside side" );
		}
		return face;
	}

	static VmfVec[] ParsePlanePoints( string text, int line )
	{
		// "( x y z ) ( x y z ) ( x y z )" — exactly three points, kept verbatim.
		var points = ParseVecList( text, line );
		if ( points.Count != 3 )
			throw new MapParseException( "vmf", line, $"a plane needs exactly 3 points, got {points.Count} in '{text}'" );
		return points.ToArray();
	}

	/// <summary>Parse a run of "( x y z )" vectors (plane points, or the stored
	/// vertex list in a "geppetto_vertices" key). Empty text parses to no vectors.</summary>
	static List<VmfVec> ParseVecList( string text, int line )
	{
		var points = new List<VmfVec>();
		int i = 0;
		while ( i < text.Length )
		{
			while ( i < text.Length && char.IsWhiteSpace( text[i] ) ) i++;
			if ( i >= text.Length ) break;
			if ( text[i] != '(' ) throw new MapParseException( "vmf", line, $"vector must start with '(', got '{text}'" );
			var close = text.IndexOf( ')', i );
			if ( close < 0 ) throw new MapParseException( "vmf", line, $"unterminated vector in '{text}'" );
			points.Add( VmfVec.Parse( text.Substring( i, close - i + 1 ), "vmf", line ) );
			i = close + 1;
		}
		return points;
	}

	static VmfDisplacement ParseDispInfo( VmfBlock disp )
	{
		var power = IntProp( disp, "power", 0 );
		if ( power < 2 || power > 4 )
			throw new MapParseException( "vmf", disp.Line, $"displacement power must be 2, 3 or 4, got {power}" );
		var d = new VmfDisplacement( power )
		{
			StartPosition = VmfVec.Parse( Prop( disp, "startposition", "[ 0 0 0 ]" ), "vmf", disp.Line ),
			Elevation = DoubleProp( disp, "elevation", 0 ),
			Subdiv = Prop( disp, "subdiv", "0" ) != "0",
		};
		foreach ( var child in disp.Children )
		{
			switch ( child.Name )
			{
				case "normals": d.Normals = ParseVecGrid( child, d.GridSize, "vmf" ); break;
				case "distances": d.Distances = ParseDoubleGrid( child, d.GridSize, "vmf" ); break;
				case "offsets": d.Offsets = ParseDoubleGrid( child, d.GridSize, "vmf" ); break;
				case "offset_normals": d.OffsetNormals = ParseVecGrid( child, d.GridSize, "vmf" ); break;
				case "alphas": d.Alphas = ParseIntGrid( child, d.GridSize, "vmf" ); break;
				case "triangle_tags": d.TriangleTags = ParseIntGrid( child, d.GridSize, "vmf" ); break;
				case "allowed_verts":
					d.AllowedVerts = new bool[10];
					foreach ( var kv in child.Props )
					{
						var idx = MapIoText.ParseInt( kv.Key, "vmf", child.Line );
						if ( idx < 0 || idx > 9 ) throw new MapParseException( "vmf", child.Line, $"allowed_verts index {idx} out of range 0..9" );
						d.AllowedVerts[idx] = kv.Value != "0";
					}
					break;
				default: throw new MapParseException( "vmf", child.Line, $"unexpected block '{child.Name}' inside dispinfo" );
			}
		}
		var defects = d.Validate();
		if ( defects.Count > 0 ) throw new MapParseException( "vmf", disp.Line, "invalid displacement: " + string.Join( "; ", defects ) );
		return d;
	}

	static VmfVec[,] ParseVecGrid( VmfBlock block, int size, string format )
	{
		var grid = new VmfVec[size, size];
		foreach ( var kv in block.Props )
		{
			var row = RowIndex( kv.Key, block.Line, format );
			if ( row < 0 || row >= size ) throw new MapParseException( format, block.Line, $"row '{kv.Key}' out of range for a {size}x{size} grid" );
			var parts = kv.Value.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length != size * 3 ) throw new MapParseException( format, block.Line, $"row '{kv.Key}' has {parts.Length} numbers, expected {size * 3}" );
			for ( int c = 0; c < size; c++ )
				grid[row, c] = new VmfVec(
					MapIoText.ParseDouble( parts[c * 3], format, block.Line ),
					MapIoText.ParseDouble( parts[c * 3 + 1], format, block.Line ),
					MapIoText.ParseDouble( parts[c * 3 + 2], format, block.Line ) );
		}
		return grid;
	}

	static double[,] ParseDoubleGrid( VmfBlock block, int size, string format )
	{
		var grid = new double[size, size];
		foreach ( var kv in block.Props )
		{
			var row = RowIndex( kv.Key, block.Line, format );
			if ( row < 0 || row >= size ) throw new MapParseException( format, block.Line, $"row '{kv.Key}' out of range for a {size}x{size} grid" );
			var parts = kv.Value.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length != size ) throw new MapParseException( format, block.Line, $"row '{kv.Key}' has {parts.Length} numbers, expected {size}" );
			for ( int c = 0; c < size; c++ ) grid[row, c] = MapIoText.ParseDouble( parts[c], format, block.Line );
		}
		return grid;
	}

	static int[,] ParseIntGrid( VmfBlock block, int size, string format )
	{
		var grid = new int[size, size];
		foreach ( var kv in block.Props )
		{
			var row = RowIndex( kv.Key, block.Line, format );
			if ( row < 0 || row >= size ) throw new MapParseException( format, block.Line, $"row '{kv.Key}' out of range for a {size}x{size} grid" );
			var parts = kv.Value.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length != size ) throw new MapParseException( format, block.Line, $"row '{kv.Key}' has {parts.Length} numbers, expected {size}" );
			for ( int c = 0; c < size; c++ ) grid[row, c] = MapIoText.ParseInt( parts[c], format, block.Line );
		}
		return grid;
	}

	static int RowIndex( string key, int line, string format )
	{
		if ( !key.StartsWith( "row", StringComparison.Ordinal ) ) throw new MapParseException( format, line, $"expected a 'rowN' key, got '{key}'" );
		return MapIoText.ParseInt( key.Substring( 3 ), format, line );
	}

	static VmfEntity ParseEntity( VmfBlock block )
	{
		var e = new VmfEntity { Id = IntProp( block, "id", 0 ), ClassName = Prop( block, "classname", "" ) };
		foreach ( var kv in block.Props )
		{
			if ( kv.Key == "id" || kv.Key == "classname" ) continue;
			e.KeyValues.Add( kv );
		}
		foreach ( var child in block.Children )
		{
			if ( child.Name == "solid" ) e.Brushes.Add( ParseSolid( child ) );
			else if ( child.Name == "editor" ) ParseEditor( child, e );
			else if ( child.Name == "connections" )
			{
				foreach ( var kv in child.Props ) e.Outputs.Add( VmfOutput.Parse( kv.Key, kv.Value, child.Line ) );
			}
			else throw new MapParseException( "vmf", child.Line, $"unexpected block '{child.Name}' inside entity" );
		}
		return e;
	}

	static string Prop( VmfBlock block, string key, string fallback )
	{
		foreach ( var kv in block.Props ) if ( kv.Key == key ) return kv.Value;
		return fallback;
	}

	static int IntProp( VmfBlock block, string key, int fallback )
	{
		var raw = Prop( block, key, null );
		return raw == null ? fallback : MapIoText.ParseInt( raw, "vmf", block.Line );
	}

	static double DoubleProp( VmfBlock block, string key, double fallback )
	{
		var raw = Prop( block, key, null );
		return raw == null ? fallback : MapIoText.ParseDouble( raw, "vmf", block.Line );
	}

	// ----- writing -----

	static void WriteVersionInfo( StringBuilder sb, VmfDocument doc )
	{
		sb.AppendLine( "versioninfo" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\t\"editorversion\" \"{doc.EditorVersion}\"" );
		sb.AppendLine( $"\t\"editorbuild\" \"{doc.EditorBuild}\"" );
		sb.AppendLine( $"\t\"mapversion\" \"{doc.MapVersion}\"" );
		sb.AppendLine( $"\t\"formatversion\" \"{doc.FormatVersion}\"" );
		sb.AppendLine( $"\t\"prefab\" \"{( doc.Prefab ? "1" : "0" )}\"" );
		sb.AppendLine( "}" );
	}

	static void WriteVisGroups( StringBuilder sb, VmfDocument doc )
	{
		if ( doc.VisGroups.Count == 0 ) return;
		sb.AppendLine( "visgroups" );
		sb.AppendLine( "{" );
		foreach ( var v in doc.VisGroups )
		{
			sb.AppendLine( "\tvisgroup" );
			sb.AppendLine( "\t{" );
			sb.AppendLine( $"\t\t\"name\" {MapIoText.Quote( v.Name )}" );
			sb.AppendLine( $"\t\t\"visgroupid\" \"{v.Id}\"" );
			sb.AppendLine( $"\t\t\"color\" \"{v.Color}\"" );
			sb.AppendLine( "\t}" );
		}
		sb.AppendLine( "}" );
	}

	static void WriteWorld( StringBuilder sb, VmfDocument doc )
	{
		sb.AppendLine( "world" );
		sb.AppendLine( "{" );
		sb.AppendLine( "\t\"id\" \"1\"" );
		sb.AppendLine( $"\t\"mapversion\" \"{doc.MapVersion}\"" );
		sb.AppendLine( "\t\"classname\" \"worldspawn\"" );
		foreach ( var kv in doc.WorldspawnKeyValues )
			sb.AppendLine( $"\t{MapIoText.Quote( kv.Key )} {MapIoText.Quote( kv.Value )}" );
		foreach ( var b in doc.WorldBrushes ) WriteSolid( sb, b, 1 );
		sb.AppendLine( "}" );
	}

	static void WriteEntity( StringBuilder sb, VmfEntity e )
	{
		sb.AppendLine( "entity" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\t\"id\" \"{e.Id}\"" );
		sb.AppendLine( $"\t\"classname\" {MapIoText.Quote( e.ClassName )}" );
		foreach ( var kv in e.KeyValues )
			sb.AppendLine( $"\t{MapIoText.Quote( kv.Key )} {MapIoText.Quote( kv.Value )}" );
		foreach ( var b in e.Brushes ) WriteSolid( sb, b, 1 );
		if ( e.Outputs.Count > 0 )
		{
			sb.AppendLine( "\tconnections" );
			sb.AppendLine( "\t{" );
			foreach ( var o in e.Outputs )
				sb.AppendLine( $"\t\t{MapIoText.Quote( o.Event )} {MapIoText.Quote( o.ToVmfString() )}" );
			sb.AppendLine( "\t}" );
		}
		WriteEditor( sb, e.EditorColor, e.VisGroupId, e.GroupId, 1 );
		sb.AppendLine( "}" );
	}

	static void WriteSolid( StringBuilder sb, VmfBrush b, int depth )
	{
		var ind = new string( '\t', depth );
		sb.AppendLine( ind + "solid" );
		sb.AppendLine( ind + "{" );
		sb.AppendLine( $"{ind}\t\"id\" \"{b.Id}\"" );
		foreach ( var f in b.Faces ) WriteSide( sb, f, depth + 1 );
		WriteEditor( sb, b.EditorColor, b.VisGroupId, b.GroupId, depth + 1, b.VisGroupShown, b.VisGroupAutoShown );
		sb.AppendLine( ind + "}" );
	}

	static void WriteSide( StringBuilder sb, VmfFace f, int depth )
	{
		var ind = new string( '\t', depth );
		sb.AppendLine( ind + "side" );
		sb.AppendLine( ind + "{" );
		sb.AppendLine( $"{ind}\t\"id\" \"{f.Id}\"" );
		// PLANE FIDELITY: the plane is written from the face's stored plane
		// points, never from triangulated vertices.
		sb.AppendLine( $"{ind}\t\"plane\" \"{f.PlanePoints[0].ToVmfString()} {f.PlanePoints[1].ToVmfString()} {f.PlanePoints[2].ToVmfString()}\"" );
		sb.AppendLine( $"{ind}\t\"material\" {MapIoText.Quote( f.Material )}" );
		sb.AppendLine( $"{ind}\t\"uaxis\" \"{f.UAxis.ToVmfString()}\"" );
		sb.AppendLine( $"{ind}\t\"vaxis\" \"{f.VAxis.ToVmfString()}\"" );
		sb.AppendLine( $"{ind}\t\"rotation\" \"{MapIoText.Num( f.Rotation )}\"" );
		sb.AppendLine( $"{ind}\t\"lightmapscale\" \"{f.LightmapScale}\"" );
		sb.AppendLine( $"{ind}\t\"smoothing_groups\" \"{f.SmoothingGroups}\"" );
		if ( f.Displacement != null ) WriteDispInfo( sb, f.Displacement, depth + 1 );
		// BRUSH ACCURACY: stored vertices ride alongside the plane, verbatim
		// (full round-trip precision). Plain Hammer files never had them and
		// still write no such key, so old files stay byte-identical.
		if ( f.StoredVertices.Count > 0 )
		{
			var verts = string.Join( " ", f.StoredVertices.Select( v => v.ToVmfString() ) );
			sb.AppendLine( $"{ind}\t\"geppetto_vertices\" \"{verts}\"" );
		}
		sb.AppendLine( ind + "}" );
	}

	static void WriteDispInfo( StringBuilder sb, VmfDisplacement d, int depth )
	{
		var ind = new string( '\t', depth );
		sb.AppendLine( ind + "dispinfo" );
		sb.AppendLine( ind + "{" );
		sb.AppendLine( $"{ind}\t\"power\" \"{d.Power}\"" );
		sb.AppendLine( $"{ind}\t\"startposition\" \"{d.StartPosition.ToBracketString()}\"" );
		sb.AppendLine( $"{ind}\t\"elevation\" \"{MapIoText.Num( d.Elevation )}\"" );
		sb.AppendLine( $"{ind}\t\"subdiv\" \"{( d.Subdiv ? "1" : "0" )}\"" );
		if ( d.Normals != null ) WriteVecGrid( sb, ind, "normals", d.Normals );
		if ( d.Distances != null ) WriteDoubleGrid( sb, ind, "distances", d.Distances );
		WriteDoubleGrid( sb, ind, "offsets", d.Offsets );
		if ( d.OffsetNormals != null ) WriteVecGrid( sb, ind, "offset_normals", d.OffsetNormals );
		if ( d.Alphas != null ) WriteIntGrid( sb, ind, "alphas", d.Alphas );
		if ( d.TriangleTags != null ) WriteIntGrid( sb, ind, "triangle_tags", d.TriangleTags );
		if ( d.AllowedVerts != null )
		{
			sb.AppendLine( $"{ind}\tallowed_verts" );
			sb.AppendLine( $"{ind}\t{{" );
			for ( int i = 0; i < 10; i++ ) sb.AppendLine( $"{ind}\t\t\"{i}\" \"{( d.AllowedVerts[i] ? "1" : "0" )}\"" );
			sb.AppendLine( $"{ind}\t}}" );
		}
		sb.AppendLine( ind + "}" );
	}

	static void WriteVecGrid( StringBuilder sb, string ind, string name, VmfVec[,] grid )
	{
		int size = grid.GetLength( 0 );
		sb.AppendLine( $"{ind}\t{name}" );
		sb.AppendLine( $"{ind}\t{{" );
		for ( int r = 0; r < size; r++ )
		{
			var row = new StringBuilder();
			for ( int c = 0; c < size; c++ ) { if ( c > 0 ) row.Append( ' ' ); row.Append( MapIoText.Num( grid[r, c].X ) ).Append( ' ' ).Append( MapIoText.Num( grid[r, c].Y ) ).Append( ' ' ).Append( MapIoText.Num( grid[r, c].Z ) ); }
			sb.AppendLine( $"{ind}\t\t\"row{r}\" \"{row}\"" );
		}
		sb.AppendLine( $"{ind}\t}}" );
	}

	static void WriteDoubleGrid( StringBuilder sb, string ind, string name, double[,] grid )
	{
		int size = grid.GetLength( 0 );
		sb.AppendLine( $"{ind}\t{name}" );
		sb.AppendLine( $"{ind}\t{{" );
		for ( int r = 0; r < size; r++ )
		{
			var row = new StringBuilder();
			for ( int c = 0; c < size; c++ ) { if ( c > 0 ) row.Append( ' ' ); row.Append( MapIoText.Num( grid[r, c] ) ); }
			sb.AppendLine( $"{ind}\t\t\"row{r}\" \"{row}\"" );
		}
		sb.AppendLine( $"{ind}\t}}" );
	}

	static void WriteIntGrid( StringBuilder sb, string ind, string name, int[,] grid )
	{
		int size = grid.GetLength( 0 );
		sb.AppendLine( $"{ind}\t{name}" );
		sb.AppendLine( $"{ind}\t{{" );
		for ( int r = 0; r < size; r++ )
		{
			var row = new StringBuilder();
			for ( int c = 0; c < size; c++ ) { if ( c > 0 ) row.Append( ' ' ); row.Append( grid[r, c].ToString( CultureInfo.InvariantCulture ) ); }
			sb.AppendLine( $"{ind}\t\t\"row{r}\" \"{row}\"" );
		}
		sb.AppendLine( $"{ind}\t}}" );
	}

	static void WriteEditor( StringBuilder sb, string color, int visGroupId, int groupId, int depth, bool shown = true, bool autoShown = true )
	{
		var ind = new string( '\t', depth );
		sb.AppendLine( ind + "editor" );
		sb.AppendLine( ind + "{" );
		sb.AppendLine( $"{ind}\t\"color\" \"{color}\"" );
		sb.AppendLine( $"{ind}\t\"visgroupid\" \"{visGroupId}\"" );
		sb.AppendLine( $"{ind}\t\"groupid\" \"{groupId}\"" );
		sb.AppendLine( $"{ind}\t\"visgroupshown\" \"{( shown ? "1" : "0" )}\"" );
		sb.AppendLine( $"{ind}\t\"visgroupautoshown\" \"{( autoShown ? "1" : "0" )}\"" );
		sb.AppendLine( ind + "}" );
	}

	static void WriteCameras( StringBuilder sb, VmfDocument doc )
	{
		if ( doc.Cameras.Count == 0 ) return;
		sb.AppendLine( "cameras" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\t\"activecamera\" \"{doc.ActiveCamera}\"" );
		foreach ( var cam in doc.Cameras )
		{
			sb.AppendLine( "\tcamera" );
			sb.AppendLine( "\t{" );
			sb.AppendLine( $"\t\t\"position\" \"{cam.Position.ToBracketString()}\"" );
			sb.AppendLine( $"\t\t\"look\" \"{cam.Look.ToBracketString()}\"" );
			sb.AppendLine( "\t}" );
		}
		sb.AppendLine( "}" );
	}

	static void WriteCordon( StringBuilder sb, VmfDocument doc )
	{
		if ( !doc.HasCordon ) return;
		sb.AppendLine( "cordon" );
		sb.AppendLine( "{" );
		sb.AppendLine( $"\t\"mins\" \"{doc.CordonMins.ToVmfString()}\"" );
		sb.AppendLine( $"\t\"maxs\" \"{doc.CordonMaxs.ToVmfString()}\"" );
		sb.AppendLine( $"\t\"active\" \"{( doc.CordonActive ? "1" : "0" )}\"" );
		sb.AppendLine( "}" );
	}

	static void WriteBlock( StringBuilder sb, List<VmfBlock> blocks, int depth )
	{
		foreach ( var b in blocks ) WriteOneBlock( sb, b, depth );
	}

	static void WriteOneBlock( StringBuilder sb, VmfBlock b, int depth )
	{
		var ind = new string( '\t', depth );
		sb.AppendLine( ind + b.Name );
		sb.AppendLine( ind + "{" );
		foreach ( var kv in b.Props )
			sb.AppendLine( $"{ind}\t{MapIoText.Quote( kv.Key )} {MapIoText.Quote( kv.Value )}" );
		foreach ( var child in b.Children ) WriteOneBlock( sb, child, depth + 1 );
		sb.AppendLine( ind + "}" );
	}

	// ----- lexer: VMF is brace blocks + quoted key/value pairs, // comments -----

	sealed class VmfLexer
	{
		readonly string _text;
		int _pos;
		int _line = 1;

		public VmfLexer( string text ) { _text = text; }

		public List<VmfBlock> ParseTopLevel()
		{
			var blocks = new List<VmfBlock>();
			while ( true )
			{
				SkipSpaceAndComments();
				if ( _pos >= _text.Length ) break;
				var name = NextToken();
				if ( name.Kind != TokKind.Word ) throw new MapParseException( "vmf", name.Line, $"expected a block name, got '{name.Value}'" );
				blocks.Add( ParseBlock( name.Value, name.Line ) );
			}
			return blocks;
		}

		VmfBlock ParseBlock( string name, int line )
		{
			var open = NextToken();
			if ( open.Kind != TokKind.OpenBrace )
				throw new MapParseException( "vmf", open.Line, $"expected '{{' after block '{name}', got '{open.Value}'" );
			var block = new VmfBlock { Name = name, Line = line };
			while ( true )
			{
				SkipSpaceAndComments();
				if ( _pos >= _text.Length ) throw new MapParseException( "vmf", _line, $"unexpected end of file inside block '{name}' (opened line {line})" );
				var tok = NextToken();
				if ( tok.Kind == TokKind.CloseBrace ) return block;
				if ( tok.Kind != TokKind.Word ) throw new MapParseException( "vmf", tok.Line, $"expected a key or '}}' in block '{name}', got '{tok.Value}'" );
				var key = tok.Value;
				SkipSpaceAndComments();
				if ( _pos >= _text.Length ) throw new MapParseException( "vmf", _line, $"unexpected end of file after key '{key}' in block '{name}'" );
				if ( PeekIsOpenBrace() ) { NextToken(); block.Children.Add( ParseBlockBody( key, tok.Line ) ); }
				else
				{
					var value = NextToken();
					if ( value.Kind != TokKind.Word ) throw new MapParseException( "vmf", value.Line, $"expected a value for key '{key}' in block '{name}', got '{value.Value}'" );
					block.Props.Add( new KeyValuePair<string, string>( key, value.Value ) );
				}
			}
		}

		// After consuming the '{' for a child block.
		VmfBlock ParseBlockBody( string name, int line )
		{
			var block = new VmfBlock { Name = name, Line = line };
			while ( true )
			{
				SkipSpaceAndComments();
				if ( _pos >= _text.Length ) throw new MapParseException( "vmf", _line, $"unexpected end of file inside block '{name}' (opened line {line})" );
				var tok = NextToken();
				if ( tok.Kind == TokKind.CloseBrace ) return block;
				if ( tok.Kind != TokKind.Word ) throw new MapParseException( "vmf", tok.Line, $"expected a key or '}}' in block '{name}', got '{tok.Value}'" );
				var key = tok.Value;
				SkipSpaceAndComments();
				if ( _pos >= _text.Length ) throw new MapParseException( "vmf", _line, $"unexpected end of file after key '{key}' in block '{name}'" );
				if ( PeekIsOpenBrace() ) { NextToken(); block.Children.Add( ParseBlockBody( key, tok.Line ) ); }
				else
				{
					var value = NextToken();
					if ( value.Kind != TokKind.Word ) throw new MapParseException( "vmf", value.Line, $"expected a value for key '{key}' in block '{name}', got '{value.Value}'" );
					block.Props.Add( new KeyValuePair<string, string>( key, value.Value ) );
				}
			}
		}

		bool PeekIsOpenBrace()
		{
			int i = _pos;
			while ( i < _text.Length && char.IsWhiteSpace( _text[i] ) ) i++;
			return i < _text.Length && _text[i] == '{';
		}

		void SkipSpaceAndComments()
		{
			while ( _pos < _text.Length )
			{
				char c = _text[_pos];
				if ( c == '\n' ) { _line++; _pos++; }
				else if ( char.IsWhiteSpace( c ) ) _pos++;
				else if ( c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/' )
				{
					while ( _pos < _text.Length && _text[_pos] != '\n' ) _pos++;
				}
				else break;
			}
		}

		enum TokKind { Word, OpenBrace, CloseBrace }

		sealed class Tok
		{
			public TokKind Kind; public string Value = ""; public int Line;
		}

		Tok NextToken()
		{
			SkipSpaceAndComments();
			if ( _pos >= _text.Length ) throw new MapParseException( "vmf", _line, "unexpected end of file" );
			int line = _line;
			char c = _text[_pos];
			if ( c == '{' ) { _pos++; return new Tok { Kind = TokKind.OpenBrace, Value = "{", Line = line }; }
			if ( c == '}' ) { _pos++; return new Tok { Kind = TokKind.CloseBrace, Value = "}", Line = line }; }
			if ( c == '"' )
			{
				_pos++;
				var sb = new StringBuilder();
				while ( _pos < _text.Length )
				{
					char ch = _text[_pos];
					if ( ch == '\n' ) throw new MapParseException( "vmf", _line, "unterminated quoted string" );
					if ( ch == '\\' && _pos + 1 < _text.Length && ( _text[_pos + 1] == '"' || _text[_pos + 1] == '\\' ) )
					{
						sb.Append( _text[_pos + 1] );
						_pos += 2;
						continue;
					}
					if ( ch == '"' ) { _pos++; return new Tok { Kind = TokKind.Word, Value = sb.ToString(), Line = line }; }
					sb.Append( ch );
					_pos++;
				}
				throw new MapParseException( "vmf", line, "unterminated quoted string" );
			}
			int start = _pos;
			while ( _pos < _text.Length && !char.IsWhiteSpace( _text[_pos] ) && _text[_pos] != '{' && _text[_pos] != '}' ) _pos++;
			return new Tok { Kind = TokKind.Word, Value = _text.Substring( start, _pos - start ), Line = line };
		}
	}
}
