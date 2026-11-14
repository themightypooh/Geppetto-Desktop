using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// A Vector Displacement Mesh (VDM) stamp: a grid of VECTORS, not heights.
///
/// A heightfield alpha (see <see cref="BrushAlpha"/>) can only push along the surface normal, so
/// everything it makes is a bump — it can never make an overhang, because no point of the result
/// ever moves sideways past where it started. A VDM field stores a full vector per texel: the
/// stamp can push a vertex sideways, curl it back over the surface it came from, and leave faces
/// whose normals point back at the surface — the underside of a hook, a scale lip, a wound edge.
/// That sideways component is the whole point, and it is what the tests measure: a VDM stamp
/// with zero tangential displacement is a heightfield wearing a different name, and the tests
/// say so.
///
/// FIELD SPACE. A field lives on a disc: u, v in 0..1 across the grid map to cx, cy in -1..1, and
/// the vector at a texel is in stamp-local space — x along the stamp's tangent, y along its
/// bitangent, z along the stamp normal. <see cref="Apply"/> builds that frame from the normal it
/// is given. Every built-in field fades to exactly 0 at the disc edge, so a stamp never leaves
/// a hard rim where it stops.
///
/// NAMES. The roadmap names stamps <c>vdm:ear</c>, <c>vdm:nose</c>, <c>vdm:horn_hook</c>,
/// <c>vdm:scale_overhang</c>, <c>vdm:wound_lip</c>. <see cref="VdmStampLibrary"/> resolves those
/// names (with or without the <c>vdm:</c> prefix). The fields are procedural and deterministic —
/// the same name on another machine is the same stamp — because a stamp that changes between
/// runs is not a stamp, it is a surprise.
/// </summary>
public sealed class VdmField
{
	public int Width, Height;
	public Vec3[] Vectors;

	/// <summary>The vector at (u, v), 0..1 across from the field's left and up from its bottom, bilinear; Zero outside it.</summary>
	public Vec3 Sample( float u, float v )
	{
		if ( Vectors is null || u < 0f || v < 0f || u > 1f || v > 1f ) return Vec3.Zero;
		var x = u * (Width - 1); var y = v * (Height - 1);
		int x0 = (int)x, y0 = (int)y, x1 = Math.Min( x0 + 1, Width - 1 ), y1 = Math.Min( y0 + 1, Height - 1 );
		float fx = x - x0, fy = y - y0;
		Vec3 At( int xi, int yi ) => Vectors[yi * Width + xi];
		return (At( x0, y0 ) * (1 - fx) + At( x1, y0 ) * fx) * (1 - fy)
			+ (At( x0, y1 ) * (1 - fx) + At( x1, y1 ) * fx) * fy;
	}

	/// <summary>File formats for a field outside the built-in library.
	/// JSON: {"width":W,"height":H,"vectors":[[x,y,z],...]} row-major, row 0 = v 0 (the field's
	/// bottom, matching Sample's y). PNG: 3-channel RGB (8- or 16-bit, no EXR reader exists in
	/// the kernel), R = tangent x, G = bitangent y, B = normal z, each mapped 0..1 to -1..1;
	/// PNG row 0 (the top) is the field's top row (v 1), so it is flipped on load.
	/// Load dispatches on extension: .json or .png, anything else is an error.</summary>
	public static VdmField Load( string path )
	{
		var ext = Path.GetExtension( path ?? "" ).ToLowerInvariant();
		return ext switch
		{
			".json" => FromJson( File.ReadAllText( path ) ),
			".png" => FromPng( File.ReadAllBytes( path ) ),
			_ => throw new ArgumentException( $"A VDM field file must be .json or .png, not '{ext}'." ),
		};
	}

	/// <summary>Write this field to a .json file (see Load for the format). Overwrites.</summary>
	public void Save( string path )
	{
		File.WriteAllText( path, ToJson() );
	}

	public string ToJson()
	{
		var sb = new StringBuilder();
		sb.Append( "{\"width\":" ).Append( Width ).Append( ",\"height\":" ).Append( Height ).Append( ",\"vectors\":[" );
		for ( var i = 0; i < Vectors.Length; i++ )
		{
			if ( i > 0 ) sb.Append( ',' );
			sb.Append( '[' )
				.Append( Vectors[i].x.ToString( "R", CultureInfo.InvariantCulture ) ).Append( ',' )
				.Append( Vectors[i].y.ToString( "R", CultureInfo.InvariantCulture ) ).Append( ',' )
				.Append( Vectors[i].z.ToString( "R", CultureInfo.InvariantCulture ) ).Append( ']' );
		}
		sb.Append( "]}" );
		return sb.ToString();
	}

	public static VdmField FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new FormatException( "Empty VDM field JSON." );
		int WidthOf( string key )
		{
			var at = json.IndexOf( key, StringComparison.Ordinal );
			if ( at < 0 ) throw new FormatException( $"VDM field JSON has no '{key}'." );
			var colon = json.IndexOf( ':', at + key.Length );
			var i = colon + 1;
			while ( i < json.Length && char.IsWhiteSpace( json[i] ) ) i++;
			var j = i;
			while ( j < json.Length && (char.IsDigit( json[j] ) || json[j] == '-') ) j++;
			if ( !int.TryParse( json[i..j], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v ) )
				throw new FormatException( $"VDM field JSON '{key}' is not an integer." );
			return v;
		}
		var w = WidthOf( "\"width\"" );
		var h = WidthOf( "\"height\"" );
		var vecAt = json.IndexOf( "\"vectors\"", StringComparison.Ordinal );
		if ( vecAt < 0 ) throw new FormatException( "VDM field JSON has no 'vectors'." );
		// Every number after the vectors key, in order; the format holds exactly w*h*3.
		var nums = new System.Collections.Generic.List<float>();
		var k = vecAt + "\"vectors\"".Length;
		while ( k < json.Length )
		{
			var c = json[k];
			if ( char.IsDigit( c ) || c == '-' || c == '+' || c == '.' )
			{
				var s = k;
				while ( k < json.Length && (char.IsDigit( json[k] ) || json[k] == '-' || json[k] == '+' || json[k] == '.' || json[k] == 'e' || json[k] == 'E') ) k++;
				if ( !float.TryParse( json[s..k], NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) )
					throw new FormatException( $"VDM field JSON has a bad number near '{json[s..Math.Min( k, s + 12 )]}'." );
				nums.Add( v );
			}
			else k++;
		}
		if ( nums.Count != w * h * 3 )
			throw new FormatException( $"VDM field JSON holds {nums.Count} numbers, but {w}x{h} needs {w * h * 3}." );
		var vectors = new Vec3[w * h];
		for ( var i = 0; i < vectors.Length; i++ )
			vectors[i] = new Vec3( nums[i * 3], nums[i * 3 + 1], nums[i * 3 + 2] );
		return new VdmField { Width = w, Height = h, Vectors = vectors };
	}

	public static VdmField FromPng( byte[] png )
	{
		var (rgba, w, h) = PngReader.Read16( png );
		var vectors = new Vec3[w * h];
		for ( var r = 0; r < h; r++ )
			for ( var x = 0; x < w; x++ )
			{
				var i = (r * w + x) * 4;
				// PNG row 0 is the top; field row 0 is the bottom (v 0).
				vectors[(h - 1 - r) * w + x] = new Vec3(
					rgba[i] / 65535f * 2f - 1f,
					rgba[i + 1] / 65535f * 2f - 1f,
					rgba[i + 2] / 65535f * 2f - 1f );
			}
		return new VdmField { Width = w, Height = h, Vectors = vectors };
	}

	/// <summary>Bake a mesh region into a field: for each vertex inside the disc, its displacement
	/// from the base plane (through origin, facing normal) expressed in the stamp frame, averaged
	/// per grid cell, then faded with the same cosine edge fade the built-in library uses, so a
	/// captured stamp re-stamped elsewhere blends out at its rim instead of stepping. Cells with
	/// no vertex stay zero. The field it returns is what VdmStampFeature's VDM file loads.</summary>
	public static VdmField Capture( PolyMesh mesh, Vec3 origin, Vec3 normal, Vec3 upHint, float radius, int size )
	{
		if ( mesh is null || radius <= 0f || size < 2 )
			throw new ArgumentException( "Capture needs a mesh, a positive radius and a grid of at least 2." );
		var n = normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 );
		var tangent = upHint - n * Vec3.Dot( upHint, n );
		if ( tangent.LengthSquared < 1e-8f )
			tangent = MathF.Abs( n.x ) < 0.9f ? Vec3.Cross( n, new Vec3( 1, 0, 0 ) ) : Vec3.Cross( n, new Vec3( 0, 1, 0 ) );
		tangent = tangent.Normal;
		var bitangent = Vec3.Cross( n, tangent );
		var sums = new Vec3[size * size];
		var counts = new int[size * size];
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var rel = mesh.Positions[i] - origin;
			var u = Vec3.Dot( rel, tangent ) / radius;
			var v = Vec3.Dot( rel, bitangent ) / radius;
			if ( u * u + v * v > 1f )
				continue;
			var xi = (int)MathF.Round( (u + 1f) * 0.5f * (size - 1) );
			var yi = (int)MathF.Round( (v + 1f) * 0.5f * (size - 1) );
			var cell = yi * size + xi;
			var basePoint = origin + tangent * (u * radius) + bitangent * (v * radius);
			var disp = mesh.Positions[i] - basePoint;
			sums[cell] += new Vec3( Vec3.Dot( disp, tangent ), Vec3.Dot( disp, bitangent ), Vec3.Dot( disp, n ) );
			counts[cell]++;
		}
		var vectors = new Vec3[size * size];
		for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
			var cell = y * size + x;
			if ( counts[cell] == 0 )
				continue;
			var cx = x / (float)(size - 1) * 2f - 1f;
			var cy = y / (float)(size - 1) * 2f - 1f;
			var r = MathF.Sqrt( cx * cx + cy * cy );
			var fade = r >= 1f ? 0f : 0.5f + 0.5f * MathF.Cos( MathF.PI * r );
			vectors[cell] = sums[cell] * (fade / counts[cell]);
		}
		return new VdmField { Width = size, Height = size, Vectors = vectors };
	}
}

/// <summary>What one <see cref="VdmStamp.Apply"/> did, measured rather than claimed.</summary>
public readonly struct VdmStampResult
{
	public readonly int Affected;
	/// <summary>Largest sideways (tangent-plane) displacement, in model units. Zero means the stamp was a heightfield.</summary>
	public readonly float MaxTangential;
	/// <summary>Largest along-normal displacement, in model units (absolute value).</summary>
	public readonly float MaxNormal;

	public VdmStampResult( int affected, float maxTangential, float maxNormal )
	{
		Affected = affected;
		MaxTangential = maxTangential;
		MaxNormal = maxNormal;
	}
}

/// <summary>
/// Applies a <see cref="VdmField"/> to a mesh, and measures what it did.
///
/// The stamp is a disc of <paramref name="radius"/> around <paramref name="origin"/>, facing
/// <paramref name="normal"/>. A vertex inside the disc moves by the field vector at its disc
/// position, times <paramref name="strength"/>: tangential field components move it sideways,
/// the normal component moves it out. Vertices outside the disc do not move at all, and the
/// field's own edge fade means the rim blends out rather than stepping.
/// </summary>
public static class VdmStamp
{
	public static VdmStampResult Apply( PolyMesh mesh, VdmField field, Vec3 origin, Vec3 normal, Vec3 upHint, float radius, float strength )
	{
		if ( mesh is null || field is null || radius <= 0f || strength == 0f )
			return new VdmStampResult( 0, 0f, 0f );

		var n = normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 );
		var tangent = upHint - n * Vec3.Dot( upHint, n );
		if ( tangent.LengthSquared < 1e-8f )
			tangent = MathF.Abs( n.x ) < 0.9f ? Vec3.Cross( n, new Vec3( 1, 0, 0 ) ) : Vec3.Cross( n, new Vec3( 0, 1, 0 ) );
		tangent = tangent.Normal;
		var bitangent = Vec3.Cross( n, tangent );

		var affected = 0;
		var maxTangential = 0f;
		var maxNormal = 0f;

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var rel = mesh.Positions[i] - origin;
			var u = Vec3.Dot( rel, tangent ) / radius;
			var v = Vec3.Dot( rel, bitangent ) / radius;
			if ( u * u + v * v > 1f )
				continue;

			var vec = field.Sample( u * 0.5f + 0.5f, v * 0.5f + 0.5f );
			if ( vec.LengthSquared < 1e-12f )
				continue;

			var offset = (tangent * vec.x + bitangent * vec.y + n * vec.z) * strength;
			mesh.Positions[i] = mesh.Positions[i] + offset;
			affected++;
			maxTangential = MathF.Max( maxTangential, MathF.Sqrt( vec.x * vec.x + vec.y * vec.y ) * MathF.Abs( strength ) );
			maxNormal = MathF.Max( maxNormal, MathF.Abs( vec.z * strength ) );
		}

		return new VdmStampResult( affected, maxTangential, maxNormal );
	}

	/// <summary>
	/// Faces whose normal points back against <paramref name="axis"/> (dot &lt; -0.1) — the
	/// underside of an overhang. A heightfield bump on a flat sheet scores exactly 0 here: its
	/// sides are perpendicular to the axis, never opposed to it. A hook that curls back over
	/// its own base scores &gt; 0, and that is the measurement that separates a VDM stamp from
	/// a tall alpha.
	/// </summary>
	public static int UndercutFaceCount( PolyMesh mesh, Vec3 axis )
	{
		if ( mesh is null )
			return 0;

		var count = 0;
		foreach ( var f in mesh.Faces )
		{
			if ( f.Count < 3 )
				continue;
			var a = mesh.Positions[f.Indices[0]];
			var b = mesh.Positions[f.Indices[1]];
			var c = mesh.Positions[f.Indices[2]];
			var normal = Vec3.Cross( b - a, c - a ).Normal;
			if ( Vec3.Dot( normal, axis ) < -0.1f )
				count++;
		}
		return count;
	}
}

/// <summary>
/// The built-in VDM stamp library: the roadmap's five named stamps as procedural vector fields.
///
/// Each field is built on a 64x64 grid over the disc, in stamp-local space (x = tangent, y =
/// bitangent, z = normal), and multiplied by a smooth edge fade so the rim is exactly 0. The
/// fields are analytic, not sculpted: a horn hook is a direction that rotates from +normal at
/// the base to past-horizontal at the tip, so the tip points back down over its own base; a
/// scale is a plate whose free edge juts sideways and dips down; a wound is a depressed centre
/// with a raised, outward-curling rim. They are stylised — a horn_hook is a hook, not a
/// zoologically exact horn — and the tests assert the structural property (sideways motion,
/// underside faces, rim/depression), not a likeness.
/// </summary>
/// <summary>One library entry: a stamp's name, its search tags, where it came from,
/// and its thumbnail when one was supplied. Built-ins ship no thumbnail art — a
/// thumbnail arrives with a registered stamp, not with the library.</summary>
public sealed class VdmStampInfo
{
	public string Name;
	public string[] Tags = Array.Empty<string>();

	/// <summary>"builtin", "registered" or "shared".</summary>
	public string Source;

	/// <summary>Thumbnail image path, when one was supplied at registration. Null for built-ins.</summary>
	public string ThumbnailPath;

	/// <summary>Shared folder the stamp was loaded from, when one applies.</summary>
	public string Folder;
}

public static class VdmStampLibrary
{
	public static readonly string[] Names = { "ear", "nose", "horn_hook", "scale_overhang", "wound_lip" };

	/// <summary>A built-in stamp by name (<c>vdm:ear</c> or <c>ear</c>), or null when unknown.</summary>
	public static VdmField Get( string name )
	{
		var key = (name ?? "").Trim().ToLowerInvariant();
		if ( key.StartsWith( "vdm:" ) )
			key = key[4..];

		if ( _registered.TryGetValue( key, out var custom ) )
			return custom;

		return key switch
		{
			"ear" => Build( Ear ),
			"nose" => Build( Nose ),
			"horn_hook" => Build( HornHook ),
			"scale_overhang" => Build( ScaleOverhang ),
			"wound_lip" => Build( WoundLip ),
			_ => null,
		};
	}

	public static bool IsKnown( string name ) => Get( name ) is not null;

	/// <summary>Tags the built-ins carry, for <see cref="Search"/>.</summary>
	static string[] BuiltinTags( string name ) => name switch
	{
		"ear" => new[] { "ear", "face", "head", "detail" },
		"nose" => new[] { "nose", "face", "head", "detail" },
		"horn_hook" => new[] { "horn", "hook", "curl", "detail" },
		"scale_overhang" => new[] { "scale", "overhang", "plate", "reptile", "armour", "armor" },
		"wound_lip" => new[] { "wound", "lip", "rim", "injury", "scar" },
		_ => Array.Empty<string>(),
	};

	static readonly Dictionary<string, VdmField> _registered = new();
	static readonly Dictionary<string, VdmStampInfo> _registeredInfo = new();
	static readonly List<string> _sharedFolders = new();

	/// <summary>Shared folders previously added with <see cref="AddSharedFolder"/>.</summary>
	public static IReadOnlyList<string> SharedFolders => _sharedFolders;

	/// <summary>Library search: stamp names whose name or tags contain the query
	/// (case-insensitive). An empty query lists everything — built-ins in
	/// <see cref="Names"/> order, then registered stamps in registration order.</summary>
	public static IReadOnlyList<string> Search( string query )
	{
		var q = (query ?? "").Trim().ToLowerInvariant();
		var results = new List<string>();
		void Consider( string name, string[] tags )
		{
			if ( q.Length == 0
				|| name.Contains( q, StringComparison.Ordinal )
				|| tags.Any( t => t.ToLowerInvariant().Contains( q, StringComparison.Ordinal ) ) )
				results.Add( name );
		}
		foreach ( var name in Names )
			Consider( name, BuiltinTags( name ) );
		foreach ( var (name, info) in _registeredInfo )
			Consider( name, info.Tags );
		return results;
	}

	/// <summary>Tags for a stamp, by name (<c>vdm:ear</c> or <c>ear</c>). Empty when unknown.</summary>
	public static string[] Tags( string name )
	{
		var key = (name ?? "").Trim().ToLowerInvariant();
		if ( key.StartsWith( "vdm:" ) )
			key = key[4..];
		if ( _registeredInfo.TryGetValue( key, out var info ) )
			return info.Tags;
		return BuiltinTags( key );
	}

	/// <summary>Library entry for a stamp, or null when unknown.</summary>
	public static VdmStampInfo Info( string name )
	{
		var key = (name ?? "").Trim().ToLowerInvariant();
		if ( key.StartsWith( "vdm:" ) )
			key = key[4..];
		if ( _registeredInfo.TryGetValue( key, out var info ) )
			return info;
		if ( Array.IndexOf( Names, key ) < 0 )
			return null;
		return new VdmStampInfo { Name = key, Tags = BuiltinTags( key ), Source = "builtin" };
	}

	/// <summary>Register a stamp of your own: a field built in code or captured with
	/// <c>add VdmCapture</c>. A registered name shadows nothing — a later
	/// <see cref="Get"/> prefers it over a built-in of the same name — and
	/// <see cref="Search"/> finds it by name or tag.</summary>
	public static void Register( string name, VdmField field, string[] tags = null, string thumbnailPath = null, string folder = null )
	{
		var key = (name ?? "").Trim().ToLowerInvariant();
		if ( key.Length == 0 ) throw new ArgumentException( "A registered stamp needs a name.", nameof( name ) );
		if ( key.StartsWith( "vdm:" ) )
			key = key[4..];
		if ( field is null ) throw new ArgumentNullException( nameof( field ) );
		_registered[key] = field;
		_registeredInfo[key] = new VdmStampInfo
		{
			Name = key,
			Tags = tags ?? Array.Empty<string>(),
			Source = folder is null ? "registered" : "shared",
			ThumbnailPath = thumbnailPath,
			Folder = folder,
		};
	}

	/// <summary>Add a shared folder of .json/.png fields: every loadable file becomes a
	/// stamp named after the file, tagged "shared". Files whose stem matches a stamp
	/// that already exists are skipped, never shadowed, and unreadable files are
	/// skipped rather than failing the whole folder. Returns the number added.</summary>
	public static int AddSharedFolder( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "A shared folder needs a path.", nameof( path ) );
		if ( !Directory.Exists( path ) ) throw new DirectoryNotFoundException( $"No shared VDM folder at '{path}'." );
		var added = 0;
		foreach ( var file in Directory.EnumerateFiles( path, "*.*", SearchOption.TopDirectoryOnly ) )
		{
			var ext = Path.GetExtension( file ).ToLowerInvariant();
			if ( ext != ".json" && ext != ".png" ) continue;
			var stem = Path.GetFileNameWithoutExtension( file ).ToLowerInvariant();
			if ( IsKnown( stem ) ) continue;
			try
			{
				Register( stem, VdmField.Load( file ), new[] { "shared" }, null, path );
				added++;
			}
			catch { }
		}
		if ( !_sharedFolders.Contains( path ) )
			_sharedFolders.Add( path );
		return added;
	}

	static VdmField Build( Func<float, float, Vec3> at )
	{
		const int size = 64;
		var vectors = new Vec3[size * size];
		for ( var y = 0; y < size; y++ )
		for ( var x = 0; x < size; x++ )
		{
			var cx = x / (float)(size - 1) * 2f - 1f;
			var cy = y / (float)(size - 1) * 2f - 1f;
			var r = MathF.Sqrt( cx * cx + cy * cy );
			var fade = r >= 1f ? 0f : 0.5f + 0.5f * MathF.Cos( MathF.PI * r );
			vectors[y * size + x] = at( cx, cy ) * fade;
		}
		return new VdmField { Width = size, Height = size, Vectors = vectors };
	}

	static float Smooth( float a, float b, float x )
	{
		var t = Math.Clamp( (x - a) / (b - a), 0f, 1f );
		return t * t * (3f - 2f * t);
	}

	/// <summary>A hook: direction rotates from +normal at the base (cy=-1) to past horizontal at the tip (cy=+1), so the tip curls sideways and dips back down over its own base. The sideways component is what a heightfield cannot do.</summary>
	static Vec3 HornHook( float cx, float cy )
	{
		var t = (cy + 1f) * 0.5f;
		var angle = t * 1.95f;
		var dirX = MathF.Sin( angle );
		var dirZ = MathF.Cos( angle );
		var width = 0.35f + 0.25f * t;
		var mag = MathF.Exp( -(cx * cx) / (width * width) ) * (0.55f + 0.45f * MathF.Sin( MathF.PI * t ));
		return new Vec3( dirX * mag, 0f, dirZ * mag );
	}

	/// <summary>A scale plate: attached at cx=-1, jutting toward +x, its free edge (cx≈0.35) dipping down so the plate overlaps what lies under it.</summary>
	static Vec3 ScaleOverhang( float cx, float cy )
	{
		const float lip = 0.35f;
		var attach = Smooth( -1f, -0.2f, cx );
		var cyProfile = MathF.Exp( -(cy * cy) / 0.5625f );
		// The free edge: a narrow band of strong sideways push right at the lip, so lip
		// vertices overshoot the row beyond them and the triangles between the rows flip —
		// that flip is the underside of the overhang, and a smooth ramp cannot produce it
		// (a gentle tangential gradient only steepens the surface, it never folds it).
		float lipTx;
		if ( cx < lip - 0.15f )
			lipTx = 0.25f * Smooth( -0.2f, lip - 0.15f, cx );
		else if ( cx <= lip + 0.05f )
			lipTx = 0.25f + 0.9f * Smooth( lip - 0.15f, lip + 0.05f, cx );
		else if ( cx < lip + 0.25f )
			lipTx = 1.15f * (1f - Smooth( lip + 0.05f, lip + 0.25f, cx ));
		else
			lipTx = 0f;
		float plateau;
		if ( cx <= lip )
			plateau = 1f;
		else if ( cx < lip + 0.2f )
			plateau = 1f - 1.5f * (cx - lip) / 0.2f;
		else if ( cx < lip + 0.45f )
			plateau = -0.5f + 0.5f * (cx - lip - 0.2f) / 0.25f;
		else
			plateau = 0f;
		var tx = attach * cyProfile * lipTx;
		return new Vec3( tx, 0f, 0.85f * attach * cyProfile * plateau );
	}

	/// <summary>A cupped ear flap: a central bump whose sides pull toward the centreline and whose rim curls forward.</summary>
	static Vec3 Ear( float cx, float cy )
	{
		var r = MathF.Sqrt( cx * cx + cy * cy );
		var bump = MathF.Exp( -(r * r) / 0.49f );
		var rim = Smooth( 0.55f, 0.9f, r );
		return new Vec3( -cx * (0.5f * bump + 0.3f * rim), 0f, bump + 0.3f * rim );
	}

	/// <summary>A nose: a central protrusion with two nostril dips left and right of centre.</summary>
	static Vec3 Nose( float cx, float cy )
	{
		var r = MathF.Sqrt( cx * cx + cy * cy );
		var bump = MathF.Exp( -(r * r) / 0.4225f );
		var dipL = MathF.Exp( -(((cx + 0.42f) * (cx + 0.42f) + (cy + 0.1f) * (cy + 0.1f)) / 0.0484f) );
		var dipR = MathF.Exp( -(((cx - 0.42f) * (cx - 0.42f) + (cy + 0.1f) * (cy + 0.1f)) / 0.0484f) );
		return new Vec3( -cx * 0.15f * bump, 0f, 0.9f * bump - 0.9f * (dipL + dipR) );
	}

	/// <summary>A wound: a depressed centre with a raised rim ring that curls outward.</summary>
	static Vec3 WoundLip( float cx, float cy )
	{
		var r = MathF.Sqrt( cx * cx + cy * cy );
		var dip = MathF.Exp( -(r * r) / 0.1024f );
		var rim = MathF.Exp( -((r - 0.62f) * (r - 0.62f)) / 0.0289f );
		var radial = r > 1e-4f ? new Vec3( cx / r, cy / r, 0f ) : Vec3.Zero;
		return new Vec3( radial.x * 0.45f * rim, radial.y * 0.45f * rim, -0.7f * dip + 0.95f * rim );
	}
}
