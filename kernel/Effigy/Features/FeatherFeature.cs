using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy.Render;

namespace Effigy;

/// <summary>What kind of feather a card is: an asymmetric flight feather, a
/// symmetric-ish contour feather, or a fluffy down feather with no real vane.</summary>
public enum FeatherKind
{
	Flight,
	Contour,
	Down,
}

/// <summary>Options for one feather card. Local space: root at the origin, the
/// feather runs along +Y, the vane faces +Z, the leading (narrow) edge is -X.
/// Units are model units (inches, Z-up in the studio); UVs are u across
/// (0 leading edge, 1 trailing edge) and v = 1 - t, so v = 1 at the root and
/// 0 at the tip — V runs upward, matching the baker's image rows.</summary>
public sealed class FeatherOptions
{
	public float Length = 3f;
	public float Width = 1f;

	/// <summary>Leading vane width as a fraction of the trailing vane width.
	/// 0.35 is the aerodynamic flight-feather asymmetry from the spec.</summary>
	public float LeadingRatio = 0.35f;

	/// <summary>Sideways bow of the shaft as a fraction of Length (0 = straight).</summary>
	public float Curve;
	/// <summary>How far the vane edges cup under, as a fraction of Width.</summary>
	public float Camber = 0.15f;
	public int Segments = 8;
	/// <summary>Columns per side of the rachis (leading side gets
	/// round(Across * LeadingRatio) of them).</summary>
	public int Across = 4;
	/// <summary>Rachis ridge height at the root, in model units.</summary>
	public float RachisRadius = 0.03f;
	public FeatherKind Kind = FeatherKind.Flight;
	public Vec3 RootColor = new( 0.42f, 0.35f, 0.22f );
	public Vec3 TipColor = new( 0.85f, 0.81f, 0.68f );
	public int Material;
}

/// <summary>Texture set for a feather card, baked the same way
/// <see cref="HairTextureBaker"/> bakes a hair sheet: one deterministic pass,
/// all maps agreeing pixel for pixel. Rows are root (top) to tip (bottom);
/// a pixel row y is t = (y + 0.5) / Size along the feather.</summary>
public sealed class FeatherBakeOptions
{
	public int Size = 512;
	public int Seed = 42;
	public Vec3 RootColor = new( 0.42f, 0.35f, 0.22f );
	public Vec3 TipColor = new( 0.85f, 0.81f, 0.68f );

	/// <summary>0..1 thin-film iridescence strength written into the _irid mask.</summary>
	public float Iridescence = 0.7f;
	/// <summary>Barb stripes across the (u + t) diagonal.</summary>
	public int BarbCount = 48;
	/// <summary>Root-to-tip fraction where barbs start unzipping into alpha notches.</summary>
	public float NotchStart = 0.75f;
}

/// <summary>Builds one feather as real geometry plus its barb texture set.
/// The vane is a quad grid, not a flat quad: the rachis is a raised ridge
/// down the middle (tall at the root, fading toward the tip), the vane cups
/// under at the edges, and the whole feather bows along <see cref="FeatherOptions.Curve"/>.
/// A hero feather is one call; a coat is <see cref="PlumageScatter"/>.</summary>
public static class FeatherCardBuilder
{
	/// <summary>One feather card. Vertex colours: R = root-to-tip, G = 0 on the
	/// rachis ridge / 1 on the vane, B = ambient occlusion (dark at the root).</summary>
	public static PolyMesh BuildFeather( FeatherOptions o )
	{
		if ( o is null )
			throw new ArgumentNullException( nameof( o ) );

		if ( o.Length <= 0f )
			throw new ArgumentException( $"A feather needs a positive length, not {o.Length}.", nameof( o ) );

		if ( o.Width <= 0f )
			throw new ArgumentException( $"A feather needs a positive width, not {o.Width}.", nameof( o ) );

		if ( o.Segments < 1 )
			throw new ArgumentException( $"A feather needs at least 1 segment, not {o.Segments}.", nameof( o ) );

		if ( o.Across < 1 )
			throw new ArgumentException( $"A feather needs at least 1 column a side, not {o.Across}.", nameof( o ) );

		// Down has no stiff vane: a narrow soft card with no rachis ridge.
		var leadingRatio = o.Kind == FeatherKind.Down ? 1f : Math.Clamp( o.LeadingRatio, 0.05f, 1f );
		var rachis = o.Kind == FeatherKind.Down ? 0f : Math.Max( 0f, o.RachisRadius );
		var stations = o.Segments + 1;
		const float t0 = -0.06f; // calamus: a short bare quill below the vane
		var leadingCols = Math.Max( 1, (int)Math.Round( o.Across * leadingRatio ) );
		var trailingCols = Math.Max( 1, o.Across );
		var cols = leadingCols + 1 + trailingCols;
		var mesh = new PolyMesh();
		var colors = new List<Vec4>();
		var idx = new int[stations, cols];

		// Vane half-widths at a station: (leading, trailing, span).
		(float Lead, float Trail, float Span) WidthsAt( float t )
		{
			var tc = Math.Clamp( t, 0f, 1f );

			// Vane width profile: narrow at the calamus, widest just past mid-feather,
			// a 2% nub at the tip so the last band is a rounded tip, not a degenerate fan.
			var shape = Math.Max( 0.02f, MathF.Sin( MathF.PI * (0.12f + 0.88f * tc) ) );
			var width = o.Width * shape;
			var leadW = width * leadingRatio / (1f + leadingRatio);
			var trailW = width / (1f + leadingRatio);
			return (leadW, trailW, leadW + trailW);
		}

		for ( var s = 0; s < stations; s++ )
		{
			var t = t0 + (1f - t0) * s / (stations - 1);
			var tc = Math.Clamp( t, 0f, 1f );
			var (leadW, trailW, span) = WidthsAt( t );
			var spineX = o.Curve * o.Length * tc * tc;
			var y = o.Length * t;

			for ( var j = 0; j < cols; j++ )
			{
				float x;
				if ( j <= leadingCols )
					x = -leadW + leadW * j / leadingCols;
				else
					x = trailW * (j - leadingCols) / trailingCols;

				// Rachis ridge: the centre column stands proud, its neighbours half so.
				var ridge = j == leadingCols ? rachis * (1f - 0.6f * tc)
					: Math.Abs( j - leadingCols ) == 1 ? rachis * 0.5f * (1f - 0.6f * tc) : 0f;

				// Camber: the vane cups under away from the rachis. The cup is centred
				// on the shaft (x = 0), not on the vane midpoint: for an asymmetric
				// feather the midpoint sits inside the wide trailing vane, so the old
				// parabola cupped the ridge column down by ~0.017 and a neighbour
				// column (not the ridge) was the highest point. fromRachis is -1 at
				// the leading edge, 0 on the rachis, +1 at the trailing edge.
				var fromRachis = x < 0f ? ( leadW < 1e-6f ? 0f : x / leadW ) : ( trailW < 1e-6f ? 0f : x / trailW );
				var z = ridge - o.Camber * o.Width * fromRachis * fromRachis * 0.5f;

				idx[s, j] = mesh.AddVertex( new Vec3( spineX + x, y, z ) );
				colors.Add( new Vec4( tc, j == leadingCols ? 0f : 1f, 0.35f + 0.65f * tc, 1f ) );
			}
		}

		for ( var s = 0; s < stations - 1; s++ )
		{
			for ( var j = 0; j < cols - 1; j++ )
			{
				// +Z winding: (s,j) -> (s,j+1) -> (s+1,j+1) -> (s+1,j).
				var a = idx[s, j];
				var b = idx[s, j + 1];
				var c = idx[s + 1, j + 1];
				var d = idx[s + 1, j];

				Vec2 Uv( int ss, int jj )
				{
					var tt = t0 + (1f - t0) * ss / (stations - 1);
					var (leadW, trailW, span) = WidthsAt( tt );
					var xx = jj <= leadingCols
						? -leadW + leadW * jj / leadingCols
						: trailW * (jj - leadingCols) / trailingCols;
					var uu = span < 1e-6f ? 0.5f : (xx + leadW) / span;
					return new Vec2( uu, 1f - Math.Clamp( tt, 0f, 1f ) );
				}

				mesh.AddFace( new[] { a, b, c, d },
					new[] { Uv( s, j ), Uv( s, j + 1 ), Uv( s + 1, j + 1 ), Uv( s + 1, j ) }, o.Material );
			}
		}

		mesh.VertexColors = colors.ToArray();
		return mesh;
	}

	/// <summary>Bake the feather's texture set into <paramref name="folder"/> as
	/// prefix_color.png etc. Returns every file written.
	/// <list type="bullet">
	/// <item><description>_color: root-to-tip gradient, barb striping, dark rachis.</description></item>
	/// <item><description>_alpha: vane coverage; near the tip the barbs unzip into
	/// notch splits (alpha gaps that widen root-to-tip past <see cref="FeatherBakeOptions.NotchStart"/>).</description></item>
	/// <item><description>_normal: 45-degree barb ridges in tangent space, same green
	/// convention as <see cref="NormalBake"/> with FlipGreen off (+green along -dP/dv).</description></item>
	/// <item><description>_rough: base roughness, smoother where the iridescence is strong.</description></item>
	/// <item><description>_irid: thin-film iridescence mask (luminance) in bands along the feather.</description></item>
	/// </list></summary>
	public static List<string> BakeTextures( string folder, string prefix, FeatherBakeOptions o )
	{
		if ( string.IsNullOrWhiteSpace( folder ) )
			throw new ArgumentException( "A feather sheet needs a folder.", nameof( folder ) );

		if ( o is null )
			throw new ArgumentNullException( nameof( o ) );

		var size = Math.Clamp( o.Size, 64, 4096 );
		Directory.CreateDirectory( folder );

		var color = new Raster( size, size, 0x000000 );
		var alpha = new Raster( size, size, 0x000000 );
		var normal = new Raster( size, size, Raster.Rgb( 128, 128, 255 ) );
		var rough = new Raster( size, size, 0x000000 );
		var irid = new Raster( size, size, 0x000000 );

		// Barbs run diagonally at 45 degrees from the rachis outward toward the tip:
		// the barb coordinate is the (u + t) diagonal, so the ridges are diagonal in
		// texture space and the normal tilts along that diagonal.
		var k = 2f * MathF.PI * Math.Max( 1, o.BarbCount ) / MathF.Sqrt( 2f );
		var rng = new Random( o.Seed );
		var phase = (float)rng.NextDouble() * MathF.PI * 2f;

		for ( var y = 0; y < size; y++ )
		{
			var t = (y + 0.5f) / size; // 0 root (top row) .. 1 tip (bottom row)

			for ( var x = 0; x < size; x++ )
			{
				var u = (x + 0.5f) / size; // 0 leading edge .. 1 trailing edge
				var p = k * ((u - 0.5f) + t) + phase;
				var stripe = 0.5f + 0.5f * MathF.Sin( p );
				var slope = 0.5f * MathF.Cos( p ) * k; // d(stripe)/du == d(stripe)/dt
				var i = y * size + x;

				// Alpha: unzipped notches past NotchStart, plus a soft fray at the vane edges.
				var edge = Math.Clamp( Math.Min( u, 1f - u ) / 0.03f, 0f, 1f );
				var a = 0.35f + 0.65f * edge;

				if ( t > o.NotchStart )
				{
					var w = 0.5f * (t - o.NotchStart) / Math.Max( 1e-6f, 1f - o.NotchStart);
					var frac = u * 24f - MathF.Floor( u * 24f );
					if ( frac < w * 0.8f )
						a *= 0.15f;
				}

				var col = Vec3.Lerp( o.RootColor, o.TipColor, t ) * (0.82f + 0.18f * stripe);
				var rachis = Math.Abs( u - 0.5f ) < 0.035f;
				if ( rachis )
					col *= 0.55f;

				var iridV = Math.Clamp( o.Iridescence, 0f, 1f ) * (0.5f + 0.5f * MathF.Sin( 2f * MathF.PI * (2.5f * t + 1.5f * u) ));
				var roughV = Math.Clamp( 0.6f - 0.25f * iridV + 0.1f * (stripe - 0.5f), 0.05f, 1f );

				// Tangent-space normal from the barb heightfield. Stored green is the
				// +t (along-feather) slope: texture V = 1 - t, so +green runs along -dP/dv.
				var s = 0.9f;
				var l = MathF.Sqrt( 1f + (s * slope) * (s * slope) * 2f );
				var n = new Vec3( -s * slope / l, s * slope / l, 1f / l );

				color.Pixels[i] = Rgb( col );
				alpha.Pixels[i] = Raster.Rgb( (int)(a * 255f), (int)(a * 255f), (int)(a * 255f) );
				normal.Pixels[i] = Raster.Rgb(
					(int)((n.x * 0.5f + 0.5f) * 255f),
					(int)((n.y * 0.5f + 0.5f) * 255f),
					(int)((n.z * 0.5f + 0.5f) * 255f) );
				rough.Pixels[i] = Raster.Rgb( (int)(roughV * 255f), (int)(roughV * 255f), (int)(roughV * 255f) );
				irid.Pixels[i] = Raster.Rgb( (int)(iridV * 255f), (int)(iridV * 255f), (int)(iridV * 255f) );
			}
		}

		var files = new List<string>();
		var names = new[] { ("_color", color), ("_alpha", alpha), ("_normal", normal), ("_rough", rough), ("_irid", irid) };

		foreach ( var (suffix, raster) in names )
		{
			var path = Path.Combine( folder, prefix + suffix + ".png" );
			raster.SavePng( path );
			files.Add( path );
		}

		return files;
	}

	static int Rgb( Vec3 v ) => Raster.Rgb(
		(int)(Math.Clamp( v.x, 0f, 1f ) * 255f),
		(int)(Math.Clamp( v.y, 0f, 1f ) * 255f),
		(int)(Math.Clamp( v.z, 0f, 1f ) * 255f) );
}

/// <summary>One feather's placement on a body: where its root sits, which way
/// it points, and how far its tip lifts off the surface.</summary>
public sealed class PlumagePlacement
{
	public Vec3 Root;
	public Vec3 Normal;
	public Vec3 Flow;
	/// <summary>Tip lift off the surface, degrees: 8 + 12 * curvature.</summary>
	public float TiltDeg;
	public float Length;
	public float Width;
	public FeatherKind Kind;
	public int Row;
	public int Column;
}

/// <summary>Options for scattering a plumage coat over a body.</summary>
public sealed class PlumageScatterOptions
{
	public int Rows = 4;
	public int PerRow = 8;
	public float Length = 3f;
	public float Width = 1f;
	public float LeadingRatio = 0.35f;
	public float Curve;
	public int Segments = 8;
	public int Across = 4;
	public float RachisRadius = 0.03f;
	/// <summary>Length multiplier for the covert (body-side) rows.</summary>
	public float CovertLength = 0.7f;
	/// <summary>Length multiplier for the middle (secondary) rows.</summary>
	public float SecondaryLength = 1.1f;
	/// <summary>Length multiplier for the tip-side (primary) rows.</summary>
	public float PrimaryLength = 1.5f;
	/// <summary>Base shingle tilt, degrees; curvature adds up to TiltCurvatureDeg more.</summary>
	public float TiltBaseDeg = 8f;
	public float TiltCurvatureDeg = 12f;
	public int Seed = 42;
	public int Material;
}

/// <summary>Distributes feathers across a body in shingled rows. Rows run along
/// the body's longest axis (root/body end to tip end); within a row feathers
/// are spaced across the second-longest axis and staggered half a step on odd
/// rows, so each row overlaps the next head-to-tail. The tip lift of a feather
/// is 8 degrees plus 12 degrees times the local surface curvature, so feathers
/// lie flatter on flat skin and lift over curved shoulders — and because every
/// feather is placed by area-weighted sampling inside its own row band, two
/// feathers in neighbouring rows never share a root point and never
/// interpenetrate at the root.</summary>
public static class PlumageScatter
{
	/// <summary>Placements for one body. Deterministic for a given mesh + options
	/// (one seeded RNG, used in row/column order).</summary>
	public static List<PlumagePlacement> Scatter( PolyMesh mesh, PlumageScatterOptions o )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( o is null )
			throw new ArgumentNullException( nameof( o ) );

		if ( mesh.VertexCount == 0 || mesh.FaceCount == 0 )
			throw new ArgumentException( "Plumage needs a body with faces to grow on.", nameof( mesh ) );

		var rows = Math.Max( 1, o.Rows );
		var perRow = Math.Max( 1, o.PerRow );

		var min = mesh.Positions[0];
		var max = mesh.Positions[0];

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( Math.Min( min.x, p.x ), Math.Min( min.y, p.y ), Math.Min( min.z, p.z ) );
			max = new Vec3( Math.Max( max.x, p.x ), Math.Max( max.y, p.y ), Math.Max( max.z, p.z ) );
		}

		var ext = new[] { max.x - min.x, max.y - min.y, max.z - min.z };
		var flowAxis = ext[0] >= ext[1] && ext[0] >= ext[2] ? 0 : ext[1] >= ext[2] ? 1 : 2;
		var crossAxis = flowAxis == 0 ? (ext[1] >= ext[2] ? 1 : 2) : flowAxis == 1 ? (ext[0] >= ext[2] ? 0 : 2) : (ext[0] >= ext[1] ? 0 : 1);
		var flowMin = flowAxis == 0 ? min.x : flowAxis == 1 ? min.y : min.z;
		var flowExt = ext[flowAxis];
		var crossMin = crossAxis == 0 ? min.x : crossAxis == 1 ? min.y : min.z;
		var crossExt = ext[crossAxis];

		if ( flowExt < 1e-6f || crossExt < 1e-6f )
			throw new ArgumentException( "Plumage needs a body with extent in two axes, not a flat sheet or a line.", nameof( mesh ) );

		var flowDir = flowAxis == 0 ? new Vec3( 1, 0, 0 ) : flowAxis == 1 ? new Vec3( 0, 1, 0 ) : new Vec3( 0, 0, 1 );
		var normals = mesh.ComputeVertexNormals();
		var sampler = new FaceSampler( mesh );
		var rng = new Random( o.Seed );
		var band = flowExt / rows;
		var colWidth = crossExt / perRow;
		var result = new List<PlumagePlacement>();

		for ( var r = 0; r < rows; r++ )
		{
			var f = rows == 1 ? 1f : r / (float)(rows - 1);
			// Coverts are contour feathers; secondaries and primaries are flight feathers.
			var kind = f < 0.4f ? FeatherKind.Contour : FeatherKind.Flight;
			var lenMul = f < 0.4f ? o.CovertLength : f < 0.75f ? o.SecondaryLength : o.PrimaryLength;
			var bandLo = flowMin + r * band;
			var bandHi = bandLo + band;

			for ( var c = 0; c < perRow; c++ )
			{
				// Stagger: odd rows shift half a column so the rows shingle.
				var crossTarget = crossMin + ((c + 0.5f + (r % 2 == 1 ? 0.5f : 0f)) % perRow) / perRow * crossExt;

				if ( !sampler.TrySampleInBand( rng, flowAxis, bandLo, bandHi, crossAxis, crossTarget, colWidth, normals,
					out var point, out var normal, out var faceIndex ) )
					continue;

				var curvature = sampler.CurvatureAt( faceIndex );

				result.Add( new PlumagePlacement
				{
					Root = point + normal * (0.01f * o.Length),
					Normal = normal,
					Flow = flowDir,
					TiltDeg = o.TiltBaseDeg + o.TiltCurvatureDeg * curvature,
					Length = o.Length * lenMul,
					Width = o.Width,
					Kind = kind,
					Row = r,
					Column = c,
				} );
			}
		}

		return result;
	}

	/// <summary>One merged coat mesh for <paramref name="mesh"/> from
	/// <paramref name="placements"/>: every feather built by
	/// <see cref="FeatherCardBuilder"/> and transformed into its placement frame
	/// (width along the surface, length along the flow lifted by TiltDeg).</summary>
	public static PolyMesh BuildCoat( PolyMesh mesh, IReadOnlyList<PlumagePlacement> placements, PlumageScatterOptions o )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( placements is null )
			throw new ArgumentNullException( nameof( placements ) );

		if ( o is null )
			throw new ArgumentNullException( nameof( o ) );

		var coat = new PolyMesh();

		foreach ( var p in placements )
		{
			var feather = FeatherCardBuilder.BuildFeather( new FeatherOptions
			{
				Length = p.Length,
				Width = p.Width,
				LeadingRatio = o.LeadingRatio,
				Curve = o.Curve,
				Segments = o.Segments,
				Across = o.Across,
				RachisRadius = o.RachisRadius,
				Kind = p.Kind,
				Material = o.Material,
			} );

			var n = p.Normal.Normal;
			var flow = p.Flow - n * Vec3.Dot( p.Flow, n );

			if ( flow.LengthSquared < 1e-8f )
				flow = Math.Abs( n.x ) < 0.9f ? Vec3.Cross( n, new Vec3( 1, 0, 0 ) ) : Vec3.Cross( n, new Vec3( 0, 1, 0 ) );

			flow = flow.Normal;
			var across = Vec3.Cross( n, flow ).Normal;
			var tilt = p.TiltDeg * MathF.PI / 180f;
			var dir = flow * MathF.Cos( tilt ) + n * MathF.Sin( tilt );
			var up = n * MathF.Cos( tilt ) - flow * MathF.Sin( tilt );

			for ( var i = 0; i < feather.Positions.Count; i++ )
			{
				var local = feather.Positions[i];
				feather.Positions[i] = p.Root + across * local.x + dir * local.y + up * local.z;
			}

			MeshMerge.Append( coat, feather );
		}

		return coat;
	}

	/// <summary>Area-weighted face sampler: picks a random point on the mesh
	/// inside a row band, preferring faces near a target cross position.</summary>
	sealed class FaceSampler
	{
		readonly PolyMesh _mesh;
		readonly List<(int Face, Vec3 A, Vec3 B, Vec3 C, float Area, Vec3 Centroid)> _tris = new();
		readonly Dictionary<(int, int), List<int>> _edgeFaces = new();
		readonly Vec3[] _faceNormals;

		public FaceSampler( PolyMesh mesh )
		{
			_mesh = mesh;
			_faceNormals = new Vec3[mesh.FaceCount];

			for ( var f = 0; f < mesh.FaceCount; f++ )
			{
				var face = mesh.Faces[f];
				var n = face.Indices.Length;

				if ( n < 3 )
					continue;

				// Newell normal for the face.
				var nx = 0f;
				var ny = 0f;
				var nz = 0f;

				for ( var i = 0; i < n; i++ )
				{
					var a = mesh.Positions[face.Indices[i]];
					var b = mesh.Positions[face.Indices[(i + 1) % n]];
					nx += (a.y - b.y) * (a.z + b.z);
					ny += (a.z - b.z) * (a.x + b.x);
					nz += (a.x - b.x) * (a.y + b.y);
				}

				var normal = new Vec3( nx, ny, nz );
				_faceNormals[f] = normal.LengthSquared < 1e-12f ? new Vec3( 0, 0, 1 ) : normal.Normal;

				for ( var i = 0; i < n; i++ )
				{
					var a = face.Indices[i];
					var b = face.Indices[(i + 1) % n];
					var key = a < b ? (a, b) : (b, a);

					if ( !_edgeFaces.TryGetValue( key, out var list ) )
						_edgeFaces[key] = list = new List<int>();

					if ( !list.Contains( f ) )
						list.Add( f );
				}

				// Fan triangulation for sampling.
				for ( var i = 1; i + 1 < n; i++ )
				{
					var a = mesh.Positions[face.Indices[0]];
					var b = mesh.Positions[face.Indices[i]];
					var c = mesh.Positions[face.Indices[i + 1]];
					var area = Vec3.Cross( b - a, c - a ).Length * 0.5f;

					if ( area < 1e-9f )
						continue;

					_tris.Add( (f, a, b, c, area, (a + b + c) / 3f) );
				}
			}
		}

		static float Coord( Vec3 p, int axis ) => axis == 0 ? p.x : axis == 1 ? p.y : p.z;

		public bool TrySampleInBand( Random rng, int flowAxis, float bandLo, float bandHi,
			int crossAxis, float crossTarget, float colWidth, Vec3[] normals,
			out Vec3 point, out Vec3 normal, out int faceIndex )
		{
			point = default;
			normal = default;
			faceIndex = -1;

			var inBand = _tris.Where( t => Coord( t.Centroid, flowAxis ) >= bandLo && Coord( t.Centroid, flowAxis ) < bandHi ).ToList();

			if ( inBand.Count == 0 )
				return false;

			var near = inBand.Where( t => Math.Abs( Coord( t.Centroid, crossAxis ) - crossTarget ) < colWidth ).ToList();
			var pool = near.Count > 0 ? near : inBand;
			var total = pool.Sum( t => t.Area );
			var pick = (float)rng.NextDouble() * total;

			foreach ( var tri in pool )
			{
				pick -= tri.Area;

				if ( pick > 0f )
					continue;

				// Uniform point in the triangle.
				var r1 = MathF.Sqrt( (float)rng.NextDouble() );
				var r2 = (float)rng.NextDouble();
				var w0 = 1f - r1;
				var w1 = r1 * (1f - r2);
				var w2 = r1 * r2;
				point = tri.A * w0 + tri.B * w1 + tri.C * w2;

				var face = _mesh.Faces[tri.Face];
				var n0 = normals[face.Indices[0]];
				var n1 = normals[face.Indices[1]];
				var n2 = normals[face.Indices[Math.Min( 2, face.Indices.Length - 1 )]];
				normal = (n0 * w0 + n1 * w1 + n2 * w2).Normal;
				faceIndex = tri.Face;
				return true;
			}

			return false;
		}

		/// <summary>Local surface curvature at a face, 0 flat to 1 folded: one
		/// minus the smallest normal dot with any edge-sharing face.</summary>
		public float CurvatureAt( int faceIndex )
		{
			if ( faceIndex < 0 || faceIndex >= _mesh.FaceCount )
				return 0f;

			var face = _mesh.Faces[faceIndex];
			var minDot = 1f;
			var found = false;

			for ( var i = 0; i < face.Indices.Length; i++ )
			{
				var a = face.Indices[i];
				var b = face.Indices[(i + 1) % face.Indices.Length];
				var key = a < b ? (a, b) : (b, a);

				if ( !_edgeFaces.TryGetValue( key, out var list ) )
					continue;

				foreach ( var other in list )
				{
					if ( other == faceIndex )
						continue;

					found = true;
					minDot = Math.Min( minDot, Vec3.Dot( _faceNormals[faceIndex], _faceNormals[other] ) );
				}
			}

			return found ? Math.Clamp( 1f - minDot, 0f, 1f ) : 0f;
		}
	}
}

/// <summary>Appends one mesh into another (positions, faces, UVs, vertex
/// colours), remapping face indices. Shared by the coat builders in this
/// file and <see cref="ScaleCoatFeature"/>.</summary>
internal static class MeshMerge
{
	public static void Append( PolyMesh target, PolyMesh source )
	{
		if ( target is null )
			throw new ArgumentNullException( nameof( target ) );

		if ( source is null || source.VertexCount == 0 )
			return;

		var baseIndex = target.VertexCount;
		target.Positions.AddRange( source.Positions );

		// Keep vertex colours aligned: pad whichever side is missing them.
		if ( target.VertexColors is not null || source.VertexColors is not null )
		{
			var colors = new List<Vec4>();

			if ( target.VertexColors is not null )
				colors.AddRange( target.VertexColors );
			else
				for ( var i = 0; i < baseIndex; i++ )
					colors.Add( new Vec4( 0, 0, 0, 1 ) );

			if ( source.VertexColors is not null )
				colors.AddRange( source.VertexColors );
			else
				for ( var i = 0; i < source.VertexCount; i++ )
					colors.Add( new Vec4( 0, 0, 0, 1 ) );

			target.VertexColors = colors.ToArray();
		}

		foreach ( var face in source.Faces )
		{
			var indices = new int[face.Indices.Length];

			for ( var i = 0; i < indices.Length; i++ )
				indices[i] = face.Indices[i] + baseIndex;

			target.AddFace( indices, face.UVs?.ToArray(), face.Material );
		}
	}
}

/// <summary>Maps a UV point to the surface it came from: position, smooth
/// normal and the dP/du tangent of the face whose UV triangle contains it.
/// Shared by the plumage/scatter work here and <see cref="ScaleCoatFeature"/>.
/// Faces with degenerate UV triangles are skipped; a mesh with no usable UVs
/// reports <see cref="HasUv"/> false instead of sampling garbage.</summary>
internal sealed class MeshUvSampler
{
	readonly PolyMesh _mesh;
	readonly Vec3[] _normals;
	readonly List<(int Face, Vec2 A, Vec2 B, Vec2 C, Vec3 P0, Vec3 P1, Vec3 P2, int I0, int I1, int I2, Vec3 Tangent)> _tris = new();

	public MeshUvSampler( PolyMesh mesh )
	{
		_mesh = mesh ?? throw new ArgumentNullException( nameof( mesh ) );
		_normals = mesh.ComputeVertexNormals();

		for ( var f = 0; f < mesh.FaceCount; f++ )
		{
			var face = mesh.Faces[f];

			if ( face.Indices.Length < 3 || face.UVs is null || face.UVs.Length != face.Indices.Length )
				continue;

			for ( var i = 1; i + 1 < face.Indices.Length; i++ )
			{
				var a = face.UVs[0];
				var b = face.UVs[i];
				var c = face.UVs[i + 1];
				var area2 = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

				if ( Math.Abs( area2 ) < 1e-9f )
					continue;

				var p0 = mesh.Positions[face.Indices[0]];
				var p1 = mesh.Positions[face.Indices[i]];
				var p2 = mesh.Positions[face.Indices[i + 1]];

				// dP/du from the triangle's positions and UVs.
				var e1 = p1 - p0;
				var e2 = p2 - p0;
				var d1 = new Vec2( b.x - a.x, b.y - a.y );
				var d2 = new Vec2( c.x - a.x, c.y - a.y );
				var det = d1.x * d2.y - d2.x * d1.y;
				var tangent = Math.Abs( det ) < 1e-9f
					? new Vec3( 1, 0, 0 )
					: (e1 * d2.y - e2 * d1.y) / det;

				_tris.Add( (f, a, b, c, p0, p1, p2, face.Indices[0], face.Indices[i], face.Indices[i + 1],
					tangent.LengthSquared < 1e-12f ? new Vec3( 1, 0, 0 ) : tangent.Normal) );
			}
		}
	}

	public bool HasUv => _tris.Count > 0;

	public void UvBounds( out float uMin, out float uMax, out float vMin, out float vMax )
	{
		uMin = vMin = float.MaxValue;
		uMax = vMax = float.MinValue;

		foreach ( var tri in _tris )
		{
			uMin = Math.Min( uMin, Math.Min( tri.A.x, Math.Min( tri.B.x, tri.C.x ) ) );
			uMax = Math.Max( uMax, Math.Max( tri.A.x, Math.Max( tri.B.x, tri.C.x ) ) );
			vMin = Math.Min( vMin, Math.Min( tri.A.y, Math.Min( tri.B.y, tri.C.y ) ) );
			vMax = Math.Max( vMax, Math.Max( tri.A.y, Math.Max( tri.B.y, tri.C.y ) ) );
		}

		if ( _tris.Count == 0 )
			uMin = uMax = vMin = vMax = 0f;
	}

	public bool TrySample( float u, float v, out Vec3 point, out Vec3 normal, out Vec3 tangent )
	{
		point = default;
		normal = default;
		tangent = default;

		foreach ( var tri in _tris )
		{
			var denom = (tri.B.y - tri.C.y) * (tri.A.x - tri.C.x) + (tri.C.x - tri.B.x) * (tri.A.y - tri.C.y);

			if ( Math.Abs( denom ) < 1e-12f )
				continue;

			var w0 = ((tri.B.y - tri.C.y) * (u - tri.C.x) + (tri.C.x - tri.B.x) * (v - tri.C.y)) / denom;
			var w1 = ((tri.C.y - tri.A.y) * (u - tri.C.x) + (tri.A.x - tri.C.x) * (v - tri.C.y)) / denom;
			var w2 = 1f - w0 - w1;

			if ( w0 < -1e-6f || w1 < -1e-6f || w2 < -1e-6f )
				continue;

			point = tri.P0 * w0 + tri.P1 * w1 + tri.P2 * w2;
			normal = (_normals[tri.I0] * w0 + _normals[tri.I1] * w1 + _normals[tri.I2] * w2).Normal;
			tangent = tri.Tangent;
			return true;
		}

		return false;
	}
}

/// <summary>
/// Script: <c>add Plumage name=Wings bodies=Wing_L,Wing_R rows=4 iridescence=0.7</c>
/// (also <c>add Feather ...</c>: the same feature under its file name).
///
/// A plumage coat on existing bodies, not a standalone blob: feathers are
/// scattered in shingled rows over each target body by <see cref="PlumageScatter"/>
/// (coverts at the body end, flight feathers at the tip end), each one a real
/// <see cref="FeatherCardBuilder"/> card with a rachis ridge, asymmetric vanes
/// and a baked barb texture set when <c>sheet=</c> names a folder.
/// <c>kind=Single</c> builds one hero feather at the first target's centre
/// instead of a coat.
/// </summary>
public class PlumageFeature : Feature
{
	public override string TypeName => "Plumage";

	public static readonly string[] Kinds = { "Wing", "BodyCoat", "Single" };

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Kind = new( "Kind", Kinds );
	public readonly IntParam Rows = new( "Rows", 4, 1, 64 );
	public readonly IntParam PerRow = new( "Feathers per row", 8, 1, 512 );
	public readonly FloatParam Length = new( "Length", 3f, 0.1f, 100f, unit: "u" );
	public readonly FloatParam Width = new( "Width", 1f, 0.05f, 100f, unit: "u" );
	public readonly FloatParam LeadingRatio = new( "Leading vane ratio", 0.35f, 0.05f, 1f );
	public readonly FloatParam Curve = new( "Curve", 0f, 0f, 1f );
	public readonly FloatParam Iridescence = new( "Iridescence", 0.7f, 0f, 1f );
	public readonly IntParam Segments = new( "Segments", 8, 1, 64 );
	public readonly IntParam Seed = new( "Seed", 42, 0, int.MaxValue ) { Slider = false };
	public readonly StringParam Sheet = new( "Sheet", "" );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Kind, Rows, PerRow, Length, Width, LeadingRatio, Curve, Iridescence, Segments, Seed, Sheet, Material };

	/// <summary>Placements from the last Execute, for tests. Empty until Execute runs.</summary>
	public IReadOnlyList<PlumagePlacement> LastPlacements => _placements;

	/// <summary>Feathers built by the last Execute (placements that found a surface).</summary>
	public int LastFeatherCount => _placements.Count;

	/// <summary>Texture files the last Execute baked, if a sheet folder was given.</summary>
	public IReadOnlyList<string> LastSheetFiles => _sheetFiles;

	List<PlumagePlacement> _placements = new();
	List<string> _sheetFiles = new();

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );
		var meshes = targets.Where( b => b.Mesh is not null && b.Mesh.VertexCount > 0 && b.Mesh.FaceCount > 0 )
			.Select( b => b.Mesh ).ToList();

		if ( meshes.Count == 0 )
			Fail( "Plumage needs a body to grow on.", "The selected bodies have no mesh with faces.", "Add a wing body first, or pick bodies=Wing_L" );

		var scatter = new PlumageScatterOptions
		{
			Rows = Rows.Clamped,
			PerRow = PerRow.Clamped,
			Length = Length.Clamped,
			Width = Width.Clamped,
			LeadingRatio = LeadingRatio.Clamped,
			Curve = Curve.Clamped,
			Segments = Segments.Clamped,
			Seed = Seed.Value,
			Material = Material.Clamped,
		};

		_placements = new List<PlumagePlacement>();
		var coat = new PolyMesh();

		if ( Kind.Value == "Single" )
		{
			var mesh = meshes[0];
			var min = mesh.Positions[0];
			var max = mesh.Positions[0];

			foreach ( var p in mesh.Positions )
			{
				min = new Vec3( Math.Min( min.x, p.x ), Math.Min( min.y, p.y ), Math.Min( min.z, p.z ) );
				max = new Vec3( Math.Max( max.x, p.x ), Math.Max( max.y, p.y ), Math.Max( max.z, p.z ) );
			}

			var centre = (min + max) * 0.5f;
			var placement = new PlumagePlacement
			{
				Root = centre,
				Normal = new Vec3( 0, 0, 1 ),
				Flow = new Vec3( 0, 1, 0 ),
				TiltDeg = scatter.TiltBaseDeg,
				Length = scatter.Length,
				Width = scatter.Width,
				Kind = FeatherKind.Flight,
				Row = 0,
				Column = 0,
			};
			_placements.Add( placement );
			MeshMerge.Append( coat, PlumageScatter.BuildCoat( mesh, _placements, scatter ) );
		}
		else
		{
			foreach ( var mesh in meshes )
			{
				var placements = PlumageScatter.Scatter( mesh, scatter );
				_placements.AddRange( placements );
				MeshMerge.Append( coat, PlumageScatter.BuildCoat( mesh, placements, scatter ) );
			}

			if ( _placements.Count == 0 )
				Fail( "Plumage found no surface to root feathers on.",
					"The target bodies have no faces inside any row band.",
					"Check the bodies cover the wing, or lower rows= / raise feathers-per-row=" );
		}

		if ( coat.VertexCount == 0 )
			Fail( "Plumage built no feathers.", "Every placement missed the surface.", "Check the target bodies have faces" );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, coat ) );

		if ( !string.IsNullOrWhiteSpace( Sheet.Value ) )
		{
			var safe = new string( (Name ?? "Plumage").Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() );
			_sheetFiles = FeatherCardBuilder.BakeTextures( Sheet.Value, safe, new FeatherBakeOptions
			{
				Seed = Seed.Value,
				Iridescence = Iridescence.Clamped,
			} );

			string Find( string suffix ) => _sheetFiles.FirstOrDefault( f => f.EndsWith( suffix, StringComparison.OrdinalIgnoreCase ) );

			// Wire the baked sheet into the feather slot: albedo + normal +
			// roughness from the baker, iridescence on the look so the film
			// responds to light angle. A user `material` line still wins.
			ctx.SlotLooks[Material.Clamped] = new SlotLook
			{
				AlbedoMap = Find( "_color.png" ),
				NormalMap = Find( "_normal.png" ),
				RoughnessMap = Find( "_rough.png" ),
				Iridescence = Iridescence.Clamped,
			};
		}
	}
}

/// <summary>Alias for <see cref="PlumageFeature"/> under its file name:
/// the script engine resolves <c>add</c> verbs by the bare class name,
/// so <c>add Plumage</c> finds PlumageFeature and <c>add Feather</c> finds
/// this one. Both share the parameters and the build; a saved FeatherFeature
/// rebuilds identically either way.</summary>
public sealed class FeatherFeature : PlumageFeature
{
}
