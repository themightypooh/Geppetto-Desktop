using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Map;

// Hint/skip brushes as a tool, not just a texture (hammer-optimisation-hint-skip-brushes).
//
// SOURCE (Report §1.3): a hint face cuts visleaves along its plane; a skip face does
// nothing and goes on the other faces of a hint brush so they don't cut. NATIVE Geppetto:
// hint/skip is brush data driving the BSP builder — a hint brush contributes ONLY its
// hint faces' planes as extra split planes; skip faces (and any ordinary faces of the
// hint brush) contribute nothing, and the hint brush itself is never solid: no leaf
// inside it becomes Solid, it only partitions the void around it.
//
// CLASSIFICATION. A face is a hint face when its material is the hint tool texture
// (tools/toolshint) and a skip face when its material is the skip tool texture
// (tools/toolsskip), via ToolTexture.Lookup — case-insensitive, tolerating backslashes
// and a leading "materials/" prefix, exactly like the rest of the tool-texture
// registry. Any other material is Ordinary and is ignored by the hint path.
//
// VALIDATION. A hint brush has at least one hint face: Validate() reports NoHintFace
// when it has none (an all-skip brush, or a brush with no tool faces at all), plus
// every defect the underlying MapBrush reports, each with its MapBrushDefect code.
// Invalid is a named reason, never a silent fix — the caller decides.
//
// BSP PATH. BspBuilder.cs is owned by the BSP ticket and is read-only here, so the
// build entry point is HintSkip.Build in this file: it mirrors BspBuilder.Build
// exactly (same checks, same root region, same portal/PVS/cluster pass over the
// same public helpers) except the split-plane set gains the hint planes, and the
// recursive splitter below is a faithful copy of BspBuilder's private Builder with
// the same constants, tie-breaks and volume guards. With no hints the result is
// identical to BspBuilder.Build — the hintskip tests prove it on two fixtures, so
// any divergence fails loudly instead of silently. The integrator's follow-up is a
// one-line merge: BspBuilder.Build gains an optional extra-split-planes parameter
// and this copy goes away; until then this file is the BSP path for hints.
//
// UNITS. Kernel inches, Z-up. No UI, no Hammer app, no shell-out: backend only.

/// <summary>One named thing wrong with a <see cref="HintBrush"/>.</summary>
public readonly struct HintBrushDefect
{
	/// <summary>The brush has no hint face: it cannot cut any leaf. An all-skip brush
	/// and a brush with no tool faces at all both report exactly this.</summary>
	public const string NoHintFace = "NoHintFace";

	public readonly string Code;
	public readonly string Message;
	public readonly int FaceIndex;

	public HintBrushDefect( string code, string message, int faceIndex = -1 )
	{
		Code = code;
		Message = message;
		FaceIndex = faceIndex;
	}

	internal HintBrushDefect( MapBrushDefect defect )
		: this( defect.Code, defect.Message, defect.FaceIndex )
	{
	}

	public override string ToString() => FaceIndex >= 0
		? $"{Code} (face {FaceIndex}): {Message}"
		: $"{Code}: {Message}";
}

/// <summary>What one face of a hint brush does at compile time.</summary>
public enum HintFaceRole
{
	/// <summary>An ordinary face: ignored by the hint path (the hint brush is a
	/// non-solid hint volume, so its ordinary faces never split space either).</summary>
	Ordinary,

	/// <summary>A hint face (tools/toolshint): its plane cuts visleaves.</summary>
	Hint,

	/// <summary>A skip face (tools/toolsskip): ignored by vis entirely.</summary>
	Skip,
}

/// <summary>
/// A hint brush: a <see cref="MapBrush"/> whose faces are classified hint / skip /
/// ordinary by material, for the BSP builder's leaf generation.
///
/// Build one from any MapBrush with the constructor (or <see cref="FromMapBrush"/>),
/// or with <see cref="Box"/> for the common case — a box whose chosen faces are
/// hint and whose remaining faces are skip. The brush is immutable after
/// construction; classification is fixed at construction from the face materials.
/// </summary>
public sealed class HintBrush
{
	/// <summary>The hint tool texture: tools/toolshint.</summary>
	public const string HintMaterialName = "tools/toolshint";

	/// <summary>The skip tool texture: tools/toolsskip.</summary>
	public const string SkipMaterialName = "tools/toolsskip";

	/// <summary>The underlying brush solid. Its planes are the hint planes' source;
	/// its volume never becomes solid in a <see cref="HintSkip"/> build.</summary>
	public MapBrush Brush { get; }

	/// <summary>Brush name, for error messages. Never null.</summary>
	public string Name { get; }

	/// <summary>One role per face, in face order.</summary>
	public IReadOnlyList<HintFaceRole> FaceRoles { get; }

	/// <summary>Indices of the hint faces, in face order.</summary>
	public IReadOnlyList<int> HintFaceIndices { get; }

	/// <summary>Indices of the skip faces, in face order.</summary>
	public IReadOnlyList<int> SkipFaceIndices { get; }

	/// <summary>Indices of the ordinary (non-tool) faces, in face order.</summary>
	public IReadOnlyList<int> OrdinaryFaceIndices { get; }

	/// <summary>Number of faces.</summary>
	public int FaceCount => Brush.FaceCount;

	/// <summary>Number of hint faces.</summary>
	public int HintFaceCount => HintFaceIndices.Count;

	/// <summary>Number of skip faces.</summary>
	public int SkipFaceCount => SkipFaceIndices.Count;

	/// <summary>True when at least one face is a hint face.</summary>
	public bool HasHintFace => HintFaceCount > 0;

	/// <summary>True when every face is a skip face (and there is at least one face):
	/// the brush cuts nothing and changes no build.</summary>
	public bool IsAllSkip => FaceCount > 0 && SkipFaceCount == FaceCount;

	/// <summary>The hint faces' planes, in face order: the cutting planes this brush
	/// contributes to a <see cref="HintSkip"/> build. Empty for an all-skip brush.</summary>
	public IReadOnlyList<BspPlane> HintPlanes { get; }

	public HintBrush( MapBrush brush, string name = "" )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		Brush = brush;
		Name = name ?? "";
		var roles = new List<HintFaceRole>( brush.FaceCount );
		var hint = new List<int>();
		var skip = new List<int>();
		var ordinary = new List<int>();
		var planes = new List<BspPlane>();
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			var role = Classify( brush.Faces[i].MaterialId );
			roles.Add( role );
			if ( role == HintFaceRole.Hint )
			{
				hint.Add( i );
				var face = brush.Faces[i];
				planes.Add( new BspPlane( face.Normal, face.Distance ) );
			}
			else if ( role == HintFaceRole.Skip ) skip.Add( i );
			else ordinary.Add( i );
		}
		FaceRoles = roles;
		HintFaceIndices = hint;
		SkipFaceIndices = skip;
		OrdinaryFaceIndices = ordinary;
		HintPlanes = planes;
	}

	/// <summary>Classify a brush from a MapBrush.</summary>
	public static HintBrush FromMapBrush( MapBrush brush, string name = "" ) => new( brush, name );

	/// <summary>
	/// Axis-aligned box hint brush from its corners. The faces at
	/// <paramref name="hintFaceIndices"/> are hint faces; every other face is a skip
	/// face. Face order is MapBrush.Box order: 0 = -X, 1 = +X, 2 = -Y, 3 = +Y,
	/// 4 = -Z, 5 = +Z. An empty index list builds an all-skip brush (valid geometry,
	/// fails <see cref="Validate"/> with <see cref="HintBrushDefect.NoHintFace"/>).
	/// An out-of-range index throws naming it.
	/// </summary>
	public static HintBrush Box( Vec3 min, Vec3 max, IReadOnlyList<int> hintFaceIndices, string name = "" )
	{
		if ( hintFaceIndices is null ) throw new ArgumentNullException( nameof( hintFaceIndices ) );
		var wanted = new HashSet<int>();
		foreach ( var i in hintFaceIndices )
		{
			if ( i < 0 || i > 5 )
				throw new ArgumentOutOfRangeException( nameof( hintFaceIndices ),
					$"Hint face index {i} is out of range: a box has faces 0-5." );
			wanted.Add( i );
		}
		var planes = new[]
		{
			(new Vec3( -1, 0, 0 ), -min.x),
			(new Vec3( 1, 0, 0 ), max.x),
			(new Vec3( 0, -1, 0 ), -min.y),
			(new Vec3( 0, 1, 0 ), max.y),
			(new Vec3( 0, 0, -1 ), -min.z),
			(new Vec3( 0, 0, 1 ), max.z),
		};
		var faces = new List<Effigy.MapBrushFace>( 6 );
		for ( var i = 0; i < 6; i++ )
			faces.Add( new Effigy.MapBrushFace( planes[i].Item1, planes[i].Item2,
				wanted.Contains( i ) ? HintMaterialName : SkipMaterialName ) );
		return new HintBrush( MapBrush.FromPlanes( faces ), name );
	}

	/// <summary>True when Validate() finds nothing wrong.</summary>
	public bool IsValid => Validate().Count == 0;

	/// <summary>
	/// All named defects, or empty when the brush is a valid hint brush. The
	/// underlying MapBrush's defects come first (same codes, same messages, same
	/// face indices); <see cref="HintBrushDefect.NoHintFace"/> is added when the
	/// brush has no hint face. Never throws for a geometrically bad brush: the
	/// defects ARE the answer.
	/// </summary>
	public IReadOnlyList<HintBrushDefect> Validate()
	{
		var defects = new List<HintBrushDefect>();
		foreach ( var d in Brush.Validate() )
			defects.Add( new HintBrushDefect( d ) );
		if ( !HasHintFace )
			defects.Add( new HintBrushDefect( HintBrushDefect.NoHintFace,
				$"Hint brush '{Name}' has no hint face: at least one face must carry {HintMaterialName}." ) );
		return defects;
	}

	/// <summary>Role of one face, by its material's tool-texture flags. A face carrying
	/// both Hint and Skip flags (no registry texture does) counts as Hint.</summary>
	public static HintFaceRole Classify( string materialName )
	{
		var tool = ToolTexture.Lookup( materialName );
		if ( tool is null ) return HintFaceRole.Ordinary;
		if ( (tool.CompileFlags & ToolCompileFlags.Hint) != 0 ) return HintFaceRole.Hint;
		if ( (tool.CompileFlags & ToolCompileFlags.Skip) != 0 ) return HintFaceRole.Skip;
		return HintFaceRole.Ordinary;
	}
}

/// <summary>
/// The BSP builder path for hint/skip brushes: hint faces insert cutting planes
/// during leaf generation, skip faces are ignored.
/// </summary>
public static class HintSkip
{
	/// <summary>
	/// The deduplicated, canonically sorted hint planes of a hint list: one entry
	/// per distinct plane, so two hint brushes on the same plane split once, and a
	/// hint plane coincident with a structural plane adds no new split. A null list
	/// contributes no planes. A null entry, or a hint brush whose underlying
	/// MapBrush is invalid, throws an ArgumentException naming the problem — except
	/// a brush with no hint face, which is valid geometry contributing zero planes
	/// (an all-skip brush changes nothing; see Validate on HintBrush for the
	/// NoHintFace report).
	/// </summary>
	public static IReadOnlyList<BspPlane> CollectHintPlanes( IEnumerable<HintBrush> hints )
	{
		var planes = new List<BspPlane>();
		if ( hints is null ) return planes;
		var seen = new HashSet<(int, int, int, int)>();
		foreach ( var hint in hints )
		{
			if ( hint is null )
				throw new ArgumentException( "Hint list contains a null hint brush.", nameof( hints ) );
			var defects = hint.Brush.Validate();
			if ( defects.Count > 0 )
				throw new ArgumentException(
					$"Hint brush '{hint.Name}' is invalid: {string.Join( "; ", defects.Select( d => d.ToString() ) )}",
					nameof( hints ) );
			foreach ( var plane in hint.HintPlanes )
				if ( seen.Add( BspPolyhedron.CanonicalKey( plane ) ) )
					planes.Add( plane );
		}
		planes.Sort();
		return planes;
	}

	/// <summary>
	/// Build a BSP tree from structural brushes plus hint brushes. Identical to
	/// <see cref="BspBuilder.Build"/> except the split-plane set also contains the
	/// hint planes from <paramref name="hints"/> (see
	/// <see cref="CollectHintPlanes"/>). Hint brushes are never solid and never
	/// count as structural: they add cutting planes only. With a null or empty hint
	/// list the result is identical to BspBuilder.Build on the same brushes.
	/// </summary>
	public static BspTree Build( IReadOnlyList<BspBrush> brushes, IReadOnlyList<HintBrush> hints,
		BspBuildOptions options = null )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		if ( brushes.Count == 0 )
			throw new ArgumentException( "A BSP build needs at least one brush; an empty map has no space to partition.", nameof( brushes ) );
		options ??= new BspBuildOptions();

		var solids = new List<BspBrush>();
		foreach ( var brush in brushes )
		{
			if ( brush is null ) throw new ArgumentException( "Brush list contains a null brush.", nameof( brushes ) );
			var defects = brush.Validate();
			if ( defects.Count > 0 )
				throw new ArgumentException( $"Brush '{brush.Name}' is invalid: {string.Join( "; ", defects )}", nameof( brushes ) );
			if ( !brush.IsDetail ) solids.Add( brush );
		}
		if ( solids.Count == 0 )
			throw new ArgumentException( "A BSP build needs at least one non-detail (structural) brush; func_detail brushes never split space.", nameof( brushes ) );

		var hintPlanes = CollectHintPlanes( hints );

		// Split planes: every structural face plane plus every hint plane,
		// canonically deduplicated and sorted, so the build does not depend on
		// brush or hint order.
		var splitPlanes = new List<BspPlane>();
		var seenPlanes = new HashSet<(int, int, int, int)>();
		foreach ( var brush in solids )
			foreach ( var plane in brush.Planes )
				if ( seenPlanes.Add( BspPolyhedron.CanonicalKey( plane ) ) )
					splitPlanes.Add( plane );
		foreach ( var plane in hintPlanes )
			if ( seenPlanes.Add( BspPolyhedron.CanonicalKey( plane ) ) )
				splitPlanes.Add( plane );
		splitPlanes.Sort();

		// Root region: the AABB of all structural brush vertices, expanded by the margin.
		var allVerts = new List<Vec3>();
		foreach ( var brush in solids ) allVerts.AddRange( brush.Vertices() );
		BspPolyhedron.Bounds( allVerts, out var min, out var max );
		var margin = options.OutsideMargin;
		min = new Vec3( min.x - margin, min.y - margin, min.z - margin );
		max = new Vec3( max.x + margin, max.y + margin, max.z + margin );
		var rootPlanes = AabbPlanes( min, max );

		var builder = new Splitter( solids, splitPlanes, options );
		var root = builder.BuildNode( rootPlanes, 0 );
		var leaves = builder.Leaves.OrderBy( l => l.Index ).ToList();

		// Portals between empty leaves, then the outside flood through them.
		var graph = PortalGraph.Build( leaves );
		var outside = graph.FloodOutside( rootPlanes );
		foreach ( var leaf in leaves )
			if ( outside[leaf.Index] )
				leaf.Kind = BspLeafKind.Outside;

		var visLeaves = new List<BspLeaf>();
		foreach ( var leaf in leaves )
			if ( leaf.Kind == BspLeafKind.Empty )
			{
				leaf.VisLeafIndex = visLeaves.Count;
				visLeaves.Add( leaf );
			}

		var pvs = PortalGraph.ComputePvs( visLeaves, graph, options );
		var clusters = PortalGraph.ComputeClusters( pvs );
		for ( int i = 0; i < visLeaves.Count; i++ ) visLeaves[i].ClusterId = clusters[i];
		var clusterCount = visLeaves.Count == 0 ? 0 : clusters.Max() + 1;

		return new BspTree( root, builder.Nodes, leaves, visLeaves, graph.Portals, pvs,
			clusterCount, visLeaves.Count > 0, graph );
	}

	static List<BspPlane> AabbPlanes( Vec3 min, Vec3 max ) => new()
	{
		new BspPlane( new Vec3( -1, 0, 0 ), -min.x ),
		new BspPlane( new Vec3( 1, 0, 0 ), max.x ),
		new BspPlane( new Vec3( 0, -1, 0 ), -min.y ),
		new BspPlane( new Vec3( 0, 1, 0 ), max.y ),
		new BspPlane( new Vec3( 0, 0, -1 ), -min.z ),
		new BspPlane( new Vec3( 0, 0, 1 ), max.z ),
	};

	/// <summary>
	/// Recursive region splitter. Faithful copy of BspBuilder's private Builder
	/// (same constants, same best-balanced-plane choice with canonical tie-break,
	/// same zero-volume child rejection): the integrator merges this into
	/// BspBuilder.Build as an extra-split-planes parameter and this copy goes away.
	/// The no-hint equivalence test guards it until then.
	/// </summary>
	sealed class Splitter
	{
		/// <summary>Minimum volume for a split child to count as a real region.</summary>
		const float MinRegionVolume = 1e-6f;

		readonly List<BspBrush> _solids;
		readonly List<BspPlane> _splitPlanes;
		readonly BspBuildOptions _options;
		readonly List<BspNode> _nodes = new();
		readonly List<BspLeaf> _leaves = new();
		int _leafCounter;

		public IReadOnlyList<BspNode> Nodes => _nodes;
		public IReadOnlyList<BspLeaf> Leaves => _leaves;

		public Splitter( List<BspBrush> solids, List<BspPlane> splitPlanes, BspBuildOptions options )
		{
			_solids = solids;
			_splitPlanes = splitPlanes;
			_options = options;
		}

		public BspNode BuildNode( List<BspPlane> region, int depth )
		{
			var node = new BspNode();
			_nodes.Add( node );
			var verts = BspPolyhedron.Vertices( region );
			if ( verts.Count == 0 )
				throw new InvalidOperationException( "BSP region collapsed to no vertices; the split planes do not enclose a volume." );

			var split = ChooseSplit( region, verts, depth );
			if ( split is null )
			{
				node.LeafIndex = _leafCounter++;
				_leaves.Add( MakeLeaf( node.LeafIndex, region, verts ) );
				return node;
			}

			node.SplitPlane = split.Value;
			var front = new List<BspPlane>( region ) { split.Value.Negated };
			var back = new List<BspPlane>( region ) { split.Value };
			node.Front = BuildNode( front, depth + 1 );
			node.Back = BuildNode( back, depth + 1 );
			return node;
		}

		BspLeaf MakeLeaf( int index, List<BspPlane> region, List<Vec3> verts )
		{
			var volume = BspPolyhedron.Volume( region, out var center );
			if ( volume <= MinRegionVolume )
			{
				// No volume (should not happen — splits are checked — but never hand
				// a caller a NaN centre): fall back to the vertex average.
				center = Vec3.Zero;
				foreach ( var v in verts ) center += v;
				center /= verts.Count;
			}
			BspPolyhedron.Bounds( verts, out var min, out var max );
			var kind = BspLeafKind.Empty;
			foreach ( var brush in _solids )
				if ( brush.Contains( center ) )
				{
					kind = BspLeafKind.Solid;
					break;
				}
			return new BspLeaf( index, kind, region, center, min, max );
		}

		/// <summary>Best split plane for the region: the plane that straddles it most
		/// evenly (fewest vertices on the lopsided side), ties broken by canonical
		/// plane order. A plane whose split would collapse a child to zero volume is
		/// skipped and the next-best plane is tried.</summary>
		BspPlane? ChooseSplit( List<BspPlane> region, List<Vec3> verts, int depth )
		{
			if ( depth >= _options.MaxDepth ) return null;
			var excluded = new HashSet<BspPlane>();
			while ( true )
			{
				BspPlane? best = null;
				var bestScore = int.MaxValue;
				foreach ( var plane in _splitPlanes )
				{
					if ( excluded.Contains( plane ) ) continue;
					int front = 0, back = 0;
					foreach ( var v in verts )
					{
						var d = plane.SignedDistance( v );
						if ( d > _options.Epsilon ) front++;
						else if ( d < -_options.Epsilon ) back++;
					}
					if ( front == 0 || back == 0 ) continue;
					var score = Math.Abs( front - back );
					if ( score >= bestScore ) continue;
					best = plane;
					bestScore = score;
					if ( bestScore == 0 ) break;
				}
				if ( best is null ) return null;
				var frontRegion = new List<BspPlane>( region ) { best.Value.Negated };
				var backRegion = new List<BspPlane>( region ) { best.Value };
				if ( HasVolume( frontRegion ) && HasVolume( backRegion ) ) return best;
				excluded.Add( best.Value );
			}
		}

		static bool HasVolume( List<BspPlane> region )
		{
			var verts = BspPolyhedron.Vertices( region );
			if ( verts.Count < 4 ) return false;
			return BspPolyhedron.Volume( region, out _ ) > MinRegionVolume;
		}
	}
}
