using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Scale tile patterns. The outline and dome are per-pattern;
/// the lattice, overlap and bake are the same for all six.</summary>
public enum ScalePattern
{
	CtenoidFish,
	DiamondReptile,
	HexSnake,
	DragonPlate,
	PangolinArmor,
	ArmadilloBand,
}

/// <summary>One scale tile as real geometry: a domed plate clipped to the
/// pattern's outline. Local space: root edge at y = 0, tip at y = Length,
/// width centred on x = 0, dome rising in +Z. The dome is low at the root
/// (that end tucks under the row above) and proud at the free tip, which is
/// what makes a row of tiles read as overlapping scales instead of flat
/// shingles. UVs are 0..1 across the tile's bounding box.</summary>
public static class ScaleTileBuilder
{
	/// <summary>Tile outline in normalized coords (x -0.5..0.5, y 0..1),
	/// root edge first, counter-clockwise.</summary>
	public static IReadOnlyList<Vec2> Outline( ScalePattern pattern ) => pattern switch
	{
		// Fan with a scalloped tip: five ctenii (comb teeth) on the free edge.
		ScalePattern.CtenoidFish => new[]
		{
			new Vec2( -0.42f, 0f ), new Vec2( 0.42f, 0f ), new Vec2( 0.5f, 0.4f ),
			new Vec2( 0.46f, 0.7f ), new Vec2( 0.36f, 0.88f ), new Vec2( 0.28f, 1f ),
			new Vec2( 0.18f, 0.88f ), new Vec2( 0.09f, 1f ), new Vec2( 0f, 0.88f ),
			new Vec2( -0.09f, 1f ), new Vec2( -0.18f, 0.88f ), new Vec2( -0.28f, 1f ),
			new Vec2( -0.36f, 0.88f ), new Vec2( -0.46f, 0.7f ), new Vec2( -0.5f, 0.4f ),
		},
		ScalePattern.DiamondReptile => new[]
		{
			new Vec2( 0f, 0f ), new Vec2( 0.5f, 0.45f ), new Vec2( 0f, 1f ), new Vec2( -0.5f, 0.45f ),
		},
		ScalePattern.HexSnake => new[]
		{
			new Vec2( -0.4f, 0f ), new Vec2( 0.4f, 0f ), new Vec2( 0.5f, 0.5f ),
			new Vec2( 0.25f, 1f ), new Vec2( -0.25f, 1f ), new Vec2( -0.5f, 0.5f ),
		},
		// Pointed kite with a central keel: the tip is the point.
		ScalePattern.DragonPlate => new[]
		{
			new Vec2( 0f, 0f ), new Vec2( 0.42f, 0.35f ), new Vec2( 0.18f, 0.8f ),
			new Vec2( 0f, 1f ), new Vec2( -0.18f, 0.8f ), new Vec2( -0.42f, 0.35f ),
		},
		// Rounded petal: an octagon around (0, 0.55), slightly lifted off y = 0.
		ScalePattern.PangolinArmor => new[]
		{
			new Vec2( 0f, 0.1f ), new Vec2( 0.35f, 0.23f ), new Vec2( 0.5f, 0.55f ),
			new Vec2( 0.35f, 0.87f ), new Vec2( 0f, 1f ), new Vec2( -0.35f, 0.87f ),
			new Vec2( -0.5f, 0.55f ), new Vec2( -0.35f, 0.23f ),
		},
		// Wide band segment: flat root, arched tip.
		ScalePattern.ArmadilloBand => new[]
		{
			new Vec2( -0.5f, 0f ), new Vec2( 0.5f, 0f ), new Vec2( 0.5f, 0.7f ),
			new Vec2( 0.35f, 1f ), new Vec2( -0.35f, 1f ), new Vec2( -0.5f, 0.7f ),
		},
		_ => throw new ArgumentException( $"Unknown scale pattern {pattern}.", nameof( pattern ) ),
	};

	/// <summary>Dome profile at normalized tile coords (uN 0..1 across,
	/// vN 0..1 root-to-tip): 0 at the side edges, low at the tucked root,
	/// proud at the free tip. 1 at the tip centre.</summary>
	public static float DomeProfile( float uN, float vN ) =>
		MathF.Sin( MathF.PI * Math.Clamp( uN, 0f, 1f ) ) * (0.25f + 0.75f * Math.Clamp( vN, 0f, 1f ));

	/// <summary>True when a normalized tile point (x -0.5..0.5, y 0..1) lies
	/// inside the pattern's outline (ray casting).</summary>
	public static bool InsideOutline( ScalePattern pattern, float x, float y )
	{
		var outline = Outline( pattern );
		var inside = false;

		for ( var i = 0; i < outline.Count; i++ )
		{
			var a = outline[i];
			var b = outline[(i + 1) % outline.Count];

			if ( (a.y > y) == (b.y > y) )
				continue;

			var cross = a.x + (y - a.y) / (b.y - a.y) * (b.x - a.x);

			if ( x < cross )
				inside = !inside;
		}

		return inside;
	}

	/// <summary>One tile. Vertex colours: R = root-to-tip, G = pattern hash,
	/// B = ambient occlusion (dark at the tucked root).</summary>
	public static PolyMesh BuildTile( ScalePattern pattern, float length, float width, float dome, int material = 0 )
	{
		if ( length <= 0f )
			throw new ArgumentException( $"A scale tile needs a positive length, not {length}.", nameof( length ) );

		if ( width <= 0f )
			throw new ArgumentException( $"A scale tile needs a positive width, not {width}.", nameof( width ) );

		if ( dome < 0f )
			throw new ArgumentException( $"A scale tile's dome cannot be negative, not {dome}.", nameof( dome ) );

		const int cols = 7;
		const int rows = 9;
		var mesh = new PolyMesh();
		var colors = new List<Vec4>();
		var hash = ((int)pattern * 2654435761u % 256) / 255f;
		var idx = new int[rows, cols];

		for ( var j = 0; j < rows; j++ )
		{
			var vN = j / (float)(rows - 1);

			for ( var i = 0; i < cols; i++ )
			{
				var uN = i / (float)(cols - 1);
				var x = (uN - 0.5f) * width;
				var y = vN * length;
				var z = dome * DomeProfile( uN, vN );
				idx[j, i] = mesh.AddVertex( new Vec3( x, y, z ) );
				colors.Add( new Vec4( vN, hash, 0.35f + 0.65f * vN, 1f ) );
			}
		}

		for ( var j = 0; j < rows - 1; j++ )
		{
			for ( var i = 0; i < cols - 1; i++ )
			{
				// Keep the quad only when its centre is inside the outline, so the
				// tile's silhouette is the pattern's, not its bounding box.
				var cu = (i + 0.5f) / (cols - 1) - 0.5f;
				var cv = (j + 0.5f) / (rows - 1);

				if ( !InsideOutline( pattern, cu, cv ) )
					continue;

				// +Z winding, as the feather vane.
				var a = idx[j, i];
				var b = idx[j, i + 1];
				var c = idx[j + 1, i + 1];
				var d = idx[j + 1, i];
				var u0 = i / (float)(cols - 1);
				var u1 = (i + 1) / (float)(cols - 1);
				var v0 = j / (float)(rows - 1);
				var v1 = (j + 1) / (float)(rows - 1);

				mesh.AddFace( new[] { a, b, c, d },
					new[] { new Vec2( u0, v0 ), new Vec2( u1, v0 ), new Vec2( u1, v1 ), new Vec2( u0, v1 ) }, material );
			}
		}

		mesh.VertexColors = colors.ToArray();
		return mesh;
	}
}

/// <summary>Lattice options for a scale coat. The lattice lives in
/// normalized UV space (0..1 across the body's UV bounds): TileU/TileV are
/// fractions of the UV range, so the mesh mode and the bake mode evaluate
/// the exact same lattice — the same body in bake mode shows the same
/// scales the mesh mode placed.</summary>
public sealed class ScaleLatticeOptions
{
	public ScalePattern Pattern = ScalePattern.CtenoidFish;
	/// <summary>Tile width as a fraction of the UV width.</summary>
	public float TileU = 0.08f;
	/// <summary>Tile length as a fraction of the UV height.</summary>
	public float TileV = 0.12f;
	/// <summary>Fraction of each tile's length the next row overlaps (0..0.9).</summary>
	public float Overlap = 0.5f;
	/// <summary>Tile dome height, model units.</summary>
	public float Dome = 0.06f;
}

/// <summary>The overlapping scale heightfield. Rows run in +v (head to tail);
/// odd rows are staggered half a tile, and each row's tip overlaps the next
/// row's root like roof shingles, so where two tiles cover one point the
/// head-ward tile (lowest row index) is the visible one.</summary>
public static class ScaleLattice
{
	/// <summary>Row pitch in normalized v: tile length minus the overlap.</summary>
	public static float RowPitch( ScaleLatticeOptions o ) =>
		o.TileV * (1f - Math.Clamp( o.Overlap, 0f, 0.9f ));

	/// <summary>Heightfield at a normalized UV point. Returns false when no
	/// tile covers the point (a groove between scales). TileId is stable
	/// within one lattice (row * 100000 + column).</summary>
	public static bool TryEvaluate( float u, float v, ScaleLatticeOptions o,
		out float height, out int tileId, out float localU, out float localV )
	{
		height = 0f;
		tileId = -1;
		localU = 0f;
		localV = 0f;

		if ( o is null )
			throw new ArgumentNullException( nameof( o ) );

		// The lattice covers the unit square and nothing outside it: a
		// point past u = 1 would otherwise wrap onto a phantom column,
		// and a point below v = 0 onto a phantom row.
		if ( u < 0f || u > 1f || v < 0f || v > 1f )
			return false;

		var pitch = RowPitch( o );

		if ( pitch < 1e-6f || o.TileU < 1e-6f || o.TileV < 1e-6f )
			return false;

		var row0 = (int)Math.Floor( v / pitch );

		// How many rows can cover one point: the tile is TileV long and rows
		// are pitch apart, so at high overlap several rows stack over a point.
		var rowsCovering = Math.Max( 1, (int)Math.Ceiling( 1f / (1f - Math.Clamp( o.Overlap, 0f, 0.9f )) ) );

		// Lowest row index first: the head-ward tile's tip lies on top of the
		// row below it, so it is the visible one where rows overlap. Rows
		// start at 0: without the clamp a point near v = 0 is claimed by
		// phantom row -1, whose tile spans v -pitch..TileV-pitch, and the
		// overlap point then reports a different tile than row 0 alone.
		for ( var r = Math.Max( 0, row0 - rowsCovering + 1 ); r <= row0; r++ )
		{
			var lv = (v - r * pitch) / o.TileV;

			if ( lv < 0f || lv > 1f )
				continue;

			var offset = r % 2 != 0 ? o.TileU * 0.5f : 0f;
			var col = (int)Math.Floor( (u - offset) / o.TileU );
			var lu = (u - offset - col * o.TileU) / o.TileU - 0.5f;

			if ( lu < -0.5f || lu > 0.5f )
				continue;

			if ( !ScaleTileBuilder.InsideOutline( o.Pattern, lu, lv ) )
				continue;

			height = o.Dome * ScaleTileBuilder.DomeProfile( lu + 0.5f, lv );
			tileId = r * 100000 + col;
			localU = lu;
			localV = lv;
			return true;
		}

		return false;
	}

	/// <summary>Tangent-space normal of the heightfield at a normalized UV
	/// point, from finite differences. <paramref name="uvToWorld"/> is model
	/// units per UV unit (the feature derives it from the body's bounds), so
	/// the slopes are true surface slopes, not UV slopes. Encoded in the
	/// <see cref="NormalBake"/> default convention (FlipGreen off): stored
	/// green runs along -dP/dv, i.e. +dh/dv.</summary>
	public static Vec3 NormalAt( float u, float v, ScaleLatticeOptions o, float uvToWorld )
	{
		var epsU = o.TileU * 0.02f;
		var epsV = o.TileV * 0.02f;

		float H( float uu, float vv ) => TryEvaluate( uu, vv, o, out var h, out _, out _, out _ ) ? h : 0f;

		var dhdu = (H( u + epsU, v ) - H( u - epsU, v )) / (2f * epsU);
		var dhdv = (H( u, v + epsV ) - H( u, v - epsV )) / (2f * epsV);
		var slopeU = dhdu / Math.Max( 1e-6f, uvToWorld );
		var slopeV = dhdv / Math.Max( 1e-6f, uvToWorld );
		var l = MathF.Sqrt( 1f + slopeU * slopeU + slopeV * slopeV );

		return new Vec3( -slopeU / l, slopeV / l, 1f / l );
	}

	/// <summary>Cavity grey, 0..1: deep grooves between tiles read dark,
	/// flats and ridges read bright — the <see cref="BakeSet"/> cavity
	/// convention (white on flats and ridges).</summary>
	public static float CavityAt( float u, float v, ScaleLatticeOptions o )
	{
		if ( !TryEvaluate( u, v, o, out var height, out _, out _, out _ ) )
			return 0.12f;

		return 0.35f + 0.65f * Math.Clamp( height / Math.Max( 1e-6f, o.Dome ), 0f, 1f );
	}

	/// <summary>Thin-film iridescence mask, 0..1: per-tile hue phase plus a
	/// bright edge at each tile's free tip. Zero where no tile covers.</summary>
	public static float IridescenceAt( float u, float v, ScaleLatticeOptions o, float iridescence )
	{
		if ( !TryEvaluate( u, v, o, out _, out var tileId, out _, out var localV ) )
			return 0f;

		var phase = ((tileId * 2654435761u) % 100) / 100f;
		var bands = 0.55f + 0.45f * MathF.Sin( 2f * MathF.PI * (3f * localV + phase) );
		var tip = localV > 0.8f ? 0.2f : 0f;

		return Math.Clamp( iridescence, 0f, 1f ) * Math.Clamp( bands + tip, 0f, 1f );
	}

	/// <summary>Hue phase for the iridescence map's green channel, 0..1.</summary>
	public static float IridescencePhase( float u, float v, ScaleLatticeOptions o )
	{
		if ( !TryEvaluate( u, v, o, out _, out var tileId, out _, out var localV ) )
			return 0f;

		var phase = ((tileId * 2654435761u) % 100) / 100f;
		return (phase + localV) % 1f;
	}
}

/// <summary>
/// Script: <c>add ScaleCoat name=Scales bodies=Body pattern=ctenoid tilt=8 mode=bake</c>
/// (pattern accepts the full name or its start, e.g. <c>pattern=ctenoid</c>).
///
/// Two modes over the same lattice:
/// <list type="bullet">
/// <item><description><c>mode=mesh</c>: overlapping instanced scale tiles in a
/// staggered brick/hex lattice along the body's UVs, each tile tilted outward
/// by <c>tilt</c> degrees (6..15) so its tip stands proud and never clips the
/// surface or the row below.</description></item>
/// <item><description><c>mode=bake</c>: the exact overlapping heightfield
/// evaluated into a normal map, a cavity map, a height map and a thin-film
/// iridescence mask — as a <see cref="BakeSet"/> on the feature and as the
/// target body's <c>Paint.HeightMap</c>.</description></item>
/// </list>
/// Both modes need UVs on the target body; a body with no usable UVs fails
/// honestly (add a UVProject first) rather than scattering scales at random.
/// "Usable" means laid out by a UVProject in this document (mesh.UvAuthored):
/// a primitive's per-face unit squares — and any import's stacked tiles — all
/// cover the same square, so the lattice cannot tell the body's faces apart
/// and a coat built on them would pile every tile onto one face. An imported
/// body with a real unwrap still needs a UVProject run over it first.
/// </summary>
public sealed class ScaleCoatFeature : Feature
{
	public override string TypeName => "ScaleCoat";

	public static readonly string[] Patterns =
		{ "CtenoidFish", "DiamondReptile", "HexSnake", "DragonPlate", "PangolinArmor", "ArmadilloBand" };
	public static readonly string[] Modes = { "Mesh", "Bake" };

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Pattern = new( "Pattern", Patterns );
	public readonly ChoiceParam Mode = new( "Mode", Modes );
	public readonly FloatParam Tilt = new( "Tilt", 8f, 6f, 15f, unit: "deg" );
	public readonly FloatParam TileU = new( "Tile U", 0.08f, 0.01f, 1f );
	public readonly FloatParam TileV = new( "Tile V", 0.12f, 0.01f, 1f );
	public readonly FloatParam Overlap = new( "Overlap", 0.5f, 0f, 0.9f );
	public readonly FloatParam Dome = new( "Dome", 0.06f, 0f, 10f, unit: "u" );
	public readonly IntParam Size = new( "Bake size", 1024, 64, 2048 ) { Slider = false };
	public readonly FloatParam Iridescence = new( "Iridescence", 0.5f, 0f, 1f );
	public readonly IntParam Seed = new( "Seed", 42, 0, int.MaxValue ) { Slider = false };
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Pattern, Mode, Tilt, TileU, TileV, Overlap, Dome, Size, Iridescence, Seed, Material };

	/// <summary>Bake result from the last Execute in bake mode, null otherwise.
	/// Normal, Cavity and Height are set; HeightMin/HeightMax/HeightRange are
	/// the dome in model units; CavityRange is 1.</summary>
	public BakeSet LastBake => _bake;

	/// <summary>Thin-film iridescence mask from the last bake (R = mask,
	/// G = hue phase, B = free-tip edge), null unless bake mode ran.</summary>
	public BakedMap LastIridescence => _iridescence;

	/// <summary>Tiles placed by the last Execute in mesh mode.</summary>
	public int LastTileCount => _tileCount;

	/// <summary>Fraction of the lattice's unit UV square a tile covered in the
	/// last Execute: bake texels in bake mode, a 64x64 sample grid in mesh
	/// mode, so the two modes report the same quantity.</summary>
	public float LastCoveredFraction => _coveredFraction;

	BakeSet _bake;
	BakedMap _iridescence;
	int _tileCount;
	float _coveredFraction;

	static ScalePattern ParsePattern( string value ) => value switch
	{
		"CtenoidFish" => ScalePattern.CtenoidFish,
		"DiamondReptile" => ScalePattern.DiamondReptile,
		"HexSnake" => ScalePattern.HexSnake,
		"DragonPlate" => ScalePattern.DragonPlate,
		"PangolinArmor" => ScalePattern.PangolinArmor,
		"ArmadilloBand" => ScalePattern.ArmadilloBand,
		_ => throw new ArgumentException( $"Unknown scale pattern '{value}'.", nameof( value ) ),
	};

	protected override void Execute( FeatureContext ctx )
	{
		var bodies = RequireBodies( ctx, Bodies )
			.Where( b => b.Mesh is not null && b.Mesh.VertexCount > 0 && b.Mesh.FaceCount > 0 ).ToList();

		if ( bodies.Count == 0 )
			Fail( "ScaleCoat needs a body to coat.", "The selected bodies have no mesh with faces.", "Add a body first, or pick bodies=Body" );

		var lattice = new ScaleLatticeOptions
		{
			Pattern = ParsePattern( Pattern.Value ),
			TileU = TileU.Clamped,
			TileV = TileV.Clamped,
			Overlap = Overlap.Clamped,
			Dome = Dome.Clamped,
		};

		_bake = null;
		_iridescence = null;
		_tileCount = 0;
		_coveredFraction = 0f;

		if ( Mode.Value == "Bake" )
			Bake( bodies, lattice );
		else
			BuildMeshCoat( ctx, bodies, lattice );
	}

	void BuildMeshCoat( FeatureContext ctx, List<Body> bodies, ScaleLatticeOptions lattice )
	{
		var coat = new PolyMesh();
		var tilt = Tilt.Clamped * MathF.PI / 180f;

		foreach ( var body in bodies )
		{
			var mesh = body.Mesh;
			var sampler = new MeshUvSampler( mesh );

			if ( !sampler.HasUv )
				Fail( $"ScaleCoat needs UVs on '{body.Name ?? body.Id}' to place its lattice.",
					"The target body has no usable UV triangles.",
					"Add a UVProject on the body first" );

			// A primitive's per-face unit squares all cover the same square,
			// so the sampler would land every tile on the first face: the
			// lattice needs a layout a UVProject authored, not just any UVs.
			if ( !mesh.UvAuthored )
				Fail( $"ScaleCoat needs a UVProject layout on '{body.Name ?? body.Id}'.",
					"The target body's UVs were never laid out by a UVProject in this document.",
					"Add a UVProject on the body first" );

			sampler.UvBounds( out var uMin, out var uMax, out var vMin, out var vMax );
			var uvWidth = Math.Max( 1e-6f, uMax - uMin );
			var uvHeight = Math.Max( 1e-6f, vMax - vMin );
			var uvDiag = MathF.Sqrt( uvWidth * uvWidth + uvHeight * uvHeight );
			var bbox = BoundsOf( mesh );
			var modelDiag = (bbox.Max - bbox.Min).Length;
			var uvToWorld = modelDiag / uvDiag; // model units per UV unit

			var pitch = ScaleLattice.RowPitch( lattice );
			var rows = (int)Math.Ceiling( 1f / pitch ) + 1;
			var cols = (int)Math.Ceiling( 1f / lattice.TileU ) + 1;

			for ( var r = 0; r < rows; r++ )
			{
				var vn = r * pitch;
				if ( vn > 1f )
					break;

				var offset = r % 2 != 0 ? lattice.TileU * 0.5f : 0f;

				for ( var c = -1; c < cols; c++ )
				{
					var un = c * lattice.TileU + offset;
					if ( un < -lattice.TileU || un > 1f )
						continue;

					var u = uMin + Math.Clamp( un, 0f, 1f ) * uvWidth;
					var v = vMin + Math.Clamp( vn, 0f, 1f ) * uvHeight;

					if ( !sampler.TrySample( u, v, out var point, out var normal, out var tangent ) )
						continue;

					// Tile frame: length along the UV tangent, tilted outward so the
					// tip lifts off the surface instead of clipping it.
					var n = normal.Normal;
					var t = tangent - n * Vec3.Dot( tangent, n );

					if ( t.LengthSquared < 1e-8f )
						t = Math.Abs( n.x ) < 0.9f ? Vec3.Cross( n, new Vec3( 1, 0, 0 ) ) : Vec3.Cross( n, new Vec3( 0, 1, 0 ) );

					t = t.Normal;
					var across = Vec3.Cross( n, t ).Normal;
					var dir = t * MathF.Cos( tilt ) + n * MathF.Sin( tilt );
					var up = n * MathF.Cos( tilt ) - t * MathF.Sin( tilt );

					var tileWorldLength = lattice.TileV * uvHeight * uvToWorld;
					var tileWorldWidth = lattice.TileU * uvWidth * uvToWorld;
					var tile = ScaleTileBuilder.BuildTile( lattice.Pattern, tileWorldLength, tileWorldWidth, lattice.Dome, Material.Clamped );
					var root = point + n * 0.005f;

					for ( var i = 0; i < tile.Positions.Count; i++ )
					{
						var local = tile.Positions[i];
						tile.Positions[i] = root + across * local.x + dir * local.y + up * local.z;
					}

					MeshMerge.Append( coat, tile );
					_tileCount++;
				}
			}
		}

		// Mesh mode covers the same lattice the bake measures, so report the
		// same quantity: the fraction of the unit UV square a tile covers,
		// sampled on a fixed grid. Mesh mode never set this before, so it
		// always reported 0 even with a full coat of tiles.
		var covered = 0;
		const int coverageGrid = 64;
		for ( var gy = 0; gy < coverageGrid; gy++ )
			for ( var gx = 0; gx < coverageGrid; gx++ )
				if ( ScaleLattice.TryEvaluate( (gx + 0.5f) / coverageGrid, (gy + 0.5f) / coverageGrid, lattice, out _, out _, out _, out _ ) )
					covered++;
		_coveredFraction = covered / (float)(coverageGrid * coverageGrid);

		if ( _tileCount == 0 )
			Fail( "ScaleCoat placed no tiles.",
				"No lattice point landed on a UV'd face of the target bodies.",
				"Check the bodies have UVs covering their faces, or raise tile U/V" );

		if ( coat.VertexCount == 0 )
			Fail( "ScaleCoat built no coat mesh.", "Every tile missed the surface.", "Check the target bodies have faces" );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, coat ) );
	}

	void Bake( List<Body> bodies, ScaleLatticeOptions lattice )
	{
		var size = Size.Clamped;

		// UV bounds are per body in mesh mode, but a bake is one atlas per body:
		// each target gets its own maps over its own UV bounds, and the last
		// target's set is the one the feature publishes (targets normally share
		// one body; multi-body bakes keep each body's own Paint.HeightMap).
		BakeSet last = null;
		BakedMap lastIrid = null;
		var coveredTotal = 0;
		var texelTotal = 0;

		foreach ( var body in bodies )
		{
			var mesh = body.Mesh;
			var sampler = new MeshUvSampler( mesh );

			if ( !sampler.HasUv )
				Fail( $"ScaleCoat bake needs UVs on '{body.Name ?? body.Id}'.",
					"The target body has no usable UV triangles.",
					"Add a UVProject on the body first" );

			if ( !mesh.UvAuthored )
				Fail( $"ScaleCoat bake needs a UVProject layout on '{body.Name ?? body.Id}'.",
					"The target body's UVs were never laid out by a UVProject in this document.",
					"Add a UVProject on the body first" );

			sampler.UvBounds( out var uMin, out var uMax, out var vMin, out var vMax );
			var uvWidth = Math.Max( 1e-6f, uMax - uMin );
			var uvHeight = Math.Max( 1e-6f, vMax - vMin );
			var uvDiag = MathF.Sqrt( uvWidth * uvWidth + uvHeight * uvHeight );
			var bbox = BoundsOf( mesh );
			var uvToWorld = (bbox.Max - bbox.Min).Length / uvDiag;

			var normal = new BakedMap( size, size );
			var cavity = new BakedMap( size, size );
			var height = new BakedMap( size, size );
			var irid = new BakedMap( size, size );
			var covered = 0;

			for ( var y = 0; y < size; y++ )
			{
				// BakedMap row 0 is v = 0; the PaintCanvas below flips to top-row-0.
				var vn = (y + 0.5f) / size;

				for ( var x = 0; x < size; x++ )
				{
					var un = (x + 0.5f) / size;
					var i = y * size + x;
					var isCovered = ScaleLattice.TryEvaluate( un, vn, lattice, out var h, out _, out _, out _ );
					var n = ScaleLattice.NormalAt( un, vn, lattice, uvToWorld );
					var cav = ScaleLattice.CavityAt( un, vn, lattice );
					var ir = ScaleLattice.IridescenceAt( un, vn, lattice, Iridescence.Clamped );
					var phase = ScaleLattice.IridescencePhase( un, vn, lattice );

					normal.Rgb[i * 3] = (byte)(Math.Clamp( n.x * 0.5f + 0.5f, 0f, 1f ) * 255f);
					normal.Rgb[i * 3 + 1] = (byte)(Math.Clamp( n.y * 0.5f + 0.5f, 0f, 1f ) * 255f);
					normal.Rgb[i * 3 + 2] = (byte)(Math.Clamp( n.z * 0.5f + 0.5f, 0f, 1f ) * 255f);
					normal.Filled[i] = isCovered;

					cavity.Rgb[i * 3] = cavity.Rgb[i * 3 + 1] = cavity.Rgb[i * 3 + 2] =
						(byte)(Math.Clamp( cav, 0f, 1f ) * 255f);
					cavity.Filled[i] = isCovered;

					// Height: mid grey is the bare surface, white is one dome out.
					var hg = 0.5f + 0.5f * Math.Clamp( h / Math.Max( 1e-6f, lattice.Dome ), 0f, 1f );
					height.Rgb[i * 3] = height.Rgb[i * 3 + 1] = height.Rgb[i * 3 + 2] =
						(byte)(Math.Clamp( hg, 0f, 1f ) * 255f);
					height.Filled[i] = isCovered;

					irid.Rgb[i * 3] = (byte)(Math.Clamp( ir, 0f, 1f ) * 255f);
					irid.Rgb[i * 3 + 1] = (byte)(Math.Clamp( phase, 0f, 1f ) * 255f);
					irid.Rgb[i * 3 + 2] = (byte)(Math.Clamp( ir * (isCovered ? 1f : 0f), 0f, 1f ) * 255f);
					irid.Filled[i] = isCovered;

					if ( isCovered )
						covered++;
				}
			}

			// The body's PaintCanvas is the live paint target: the heightfield
			// lands in its HeightMap (grey = 0 bare surface .. 1 one dome out),
			// flipped so canvas row 0 (top of the image) is v = 1.
			var canvas = mesh.Paint ?? new PaintCanvas( size, size );
			var heightMap = new PaintCanvas( size, size );

			for ( var y = 0; y < size; y++ )
			{
				var vn = (y + 0.5f) / size;

				for ( var x = 0; x < size; x++ )
				{
					var un = (x + 0.5f) / size;
					ScaleLattice.TryEvaluate( un, vn, lattice, out var h, out _, out _, out _ );
					var grey = (byte)(Math.Clamp( h / Math.Max( 1e-6f, lattice.Dome ), 0f, 1f ) * 255f);
					var ci = ((size - 1 - y) * size + x) * 4;
					heightMap.Rgba[ci] = grey;
					heightMap.Rgba[ci + 1] = grey;
					heightMap.Rgba[ci + 2] = grey;
					heightMap.Rgba[ci + 3] = 255;
				}
			}

			canvas.HeightMap = heightMap;
			mesh.Paint = canvas;

			last = new BakeSet
			{
				Normal = normal,
				Cavity = cavity,
				Height = height,
				HeightMin = 0f,
				HeightMax = lattice.Dome,
				HeightRange = lattice.Dome,
				CavityRange = 1f,
				Samples = size * size,
				Hits = covered,
			};
			lastIrid = irid;
			coveredTotal += covered;
			texelTotal += size * size;
		}

		_bake = last;
		_iridescence = lastIrid;
		_coveredFraction = texelTotal == 0 ? 0f : coveredTotal / (float)texelTotal;

		if ( _coveredFraction < 0.01f )
			Fail( "ScaleCoat bake covered almost nothing.",
				"Under 1% of bake texels landed on a tile.",
				"Raise tile U/V, or check the body's UV bounds cover its faces" );
	}

	static (Vec3 Min, Vec3 Max) BoundsOf( PolyMesh mesh )
	{
		var min = mesh.Positions[0];
		var max = mesh.Positions[0];

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( Math.Min( min.x, p.x ), Math.Min( min.y, p.y ), Math.Min( min.z, p.z ) );
			max = new Vec3( Math.Max( max.x, p.x ), Math.Max( max.y, p.y ), Math.Max( max.z, p.z ) );
		}

		return (min, max);
	}
}
