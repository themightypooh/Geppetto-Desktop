using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P2: water, ladders, clip / invisible geometry via tool textures
/// (hammer-water-ladders-clip-tool-textures) — NATIVE Geppetto, no Hammer app.
///
/// Tool textures are data, not Hammer materials: this file is the native flag layer
/// on top of the landed <see cref="ToolTexture"/> registry, <see cref="FaceMaterial"/>
/// and <see cref="MapBrush"/> (deps hammer-face-material-uv-model and
/// hammer-entity-place-edit-validate). It adds, in one file:
///
///  * WATER — <see cref="WaterBrush"/>: a water brush is a convex brush whose TOP
///    face (+Z) carries a water material and whose other five faces are
///    tools/toolsnodraw, so <see cref="MapBakeExport"/> bakes exactly the surface
///    (it already drops nodraw faces). <see cref="WaterVolume"/> carries the
///    surface level, depth, surface area and LOD control (<see cref="WaterSettings"/>).
///    A <c>func_water_analog</c> brush entity ties such brushes together
///    (<see cref="FuncWaterAnalog"/>); the class is NOT in the core registry
///    (kernel/Effigy/Map/registry/core-source1.json is another ticket's file), so
///    the fragment ships as <see cref="FuncWaterAnalog.RegistryFragmentJson"/> and
///    is merged into the caller's registry on demand — no core file is touched.
///  * CLIP — <see cref="ClipVolume"/>: a brush of toolsclip / toolsplayerclip /
///    toolsnpcclip. Invisible collision as a <see cref="CollisionLayer"/> flag:
///    clip blocks players and NPCs, playerclip players only, npcclip NPCs only,
///    and none of them ever blocks bullets.
///  * LADDERS — <see cref="LadderVolume"/>: a brush of toolsladder. Climbable,
///    never solid: it blocks no mover, it is a volume the player climbs through.
///    toolsinvisibleladder is the same role, invisible in the editor too;
///    toolsinvisible is a plain NonSolidTool volume (renders nothing, blocks nothing).
///  * COLLISION — <see cref="CollisionVolumes"/>: the walk-validation query. Solid
///    world brushes block every mover (players, NPCs, bullets); clip volumes block
///    their layers; water and ladders block nothing. <c>BlocksWalk</c> is the
///    segment test a playtest / validation pass calls.
///  * BLOCKLIGHT / BLOCKLOS — <see cref="VisibilityBlockers"/>: per-FACE flags.
///    A toolsblocklight face stops light through that face, a toolsblock_los face
///    stops AI line of sight; both are solid faces of an otherwise solid brush
///    (see <see cref="ToolTexture"/>), so they are collected face-by-face, not
///    brush-by-brush, and are consumed by the bake / visibility passes.
///
/// NAMESPACE NOTE. The ticket names namespace Effigy.Map. That namespace is real
/// (MapBakeExport, Displacement, VmfIo live in it); the FaceMaterial / ToolTexture /
/// MapBrush deps live in Effigy, hence the using above. Every public type here is
/// in Effigy.Map.
///
/// NAME-COLLISION NOTE. <c>Displacement.cs</c> defines a DIFFERENT
/// <c>MapBrushFace</c> in Effigy.Map (quad corners + int material id, for
/// displacement binding), while <c>MapBrush.cs</c> defines the plane-based
/// <c>Effigy.MapBrushFace</c> (normal + distance + FaceMaterial) this file
/// builds on. Unqualified <c>MapBrushFace</c> from inside Effigy.Map binds to
/// Displacement's, so every use here is written <c>Effigy.MapBrushFace</c>.
/// That collision is pre-existing and not this ticket's to fix.
///
/// BAKE DIVERGENCE, named not hidden. <see cref="MapBakeExport"/> drops ONLY
/// nodraw faces (<see cref="FaceMaterial.ContributesToBakedMesh"/>). A clip or
/// ladder brush therefore still bakes its faces today, and so does a blocklight
/// face. <see cref="WaterLadders.ShouldBakeFace"/> is the rule the bake pass
/// must consult when it is wired to this ticket: no face whose tool texture is
/// NonSolid, NoDraw, BlockLight or BlockLos bakes. This file does NOT edit the
/// bake pass (read-only here); the integrator wires it.
///
/// NO UI. Backend / data-model / API only. NO Hammer app, no shelling out to
/// Hammer / VBSP / VVIS / VRAD — everything here is native arithmetic on
/// MapBrush planes.
/// </summary>
[Flags]
public enum CollisionLayer
{
	None = 0,
	/// <summary>Blocks players (walk validation, physics).</summary>
	Player = 1 << 0,
	/// <summary>Blocks NPCs (AI movement).</summary>
	Npc = 1 << 1,
	/// <summary>Blocks bullets / traces. Clip volumes NEVER carry this.</summary>
	Bullet = 1 << 2,
	/// <summary>Players + NPCs: what a toolsclip volume blocks.</summary>
	AllMovement = Player | Npc,
	/// <summary>Every mover: what a solid world brush blocks.</summary>
	All = Player | Npc | Bullet,
}

/// <summary>Who is trying to move: a player, an NPC, or a bullet / trace.</summary>
public enum MoverKind
{
	Player,
	Npc,
	Bullet,
}

/// <summary>
/// What a brush IS to the BSP / collision / visibility passes, from its faces.
/// A brush with a water face is Water only when it passes
/// <see cref="WaterBrush.Classify"/>; anything that mixes roles is MixedTool.
/// </summary>
public enum BrushRole
{
	/// <summary>Ordinary solid brush (ordinary faces, nodraw faces, blocklight / blocklos faces).</summary>
	Solid,
	/// <summary>Water brush: one +Z water face, the rest nodraw.</summary>
	Water,
	/// <summary>All faces toolsclip: blocks players and NPCs.</summary>
	Clip,
	/// <summary>All faces toolsplayerclip: blocks players only.</summary>
	PlayerClip,
	/// <summary>All faces toolsnpcclip: blocks NPCs only.</summary>
	NpcClip,
	/// <summary>All faces toolsladder: climbable, never solid.</summary>
	Ladder,
	/// <summary>All faces NonSolid tool textures that are none of the above (trigger, fog, sky, hint, skip, origin, areaportal, occluder).</summary>
	NonSolidTool,
	/// <summary>Faces mix roles (e.g. a clip face on a brick brush): a defect, not a role.</summary>
	MixedTool,
	/// <summary>The brush failed <see cref="MapBrush.Validate"/>.</summary>
	Invalid,
}

/// <summary>Water surface detail: full shader water, or a cheap flat surface.</summary>
public enum WaterLod
{
	/// <summary>Full water surface (shader, reflections, refraction where the renderer supports them).</summary>
	Full,
	/// <summary>Cheap flat water: no reflections / refraction, same surface level and volume.</summary>
	Simple,
}

/// <summary>
/// LOD control for a water volume. <see cref="LodDistance"/> is the distance
/// beyond which the surface drops to <see cref="WaterLod.Simple"/> (0 = never:
/// the chosen LOD applies at every distance). Both values are validated on
/// construction; a negative distance has no meaning and throws.
/// </summary>
public sealed class WaterSettings
{
	public WaterLod Lod { get; }
	public float LodDistance { get; }

	public WaterSettings( WaterLod lod = WaterLod.Full, float lodDistance = 0f )
	{
		if ( lodDistance < 0f )
			throw new ArgumentException( $"Water LOD distance is {lodDistance}: it must be 0 (never) or positive." );
		Lod = lod;
		LodDistance = lodDistance;
	}

	public static WaterSettings Default => new();
}

/// <summary>
/// A classified water brush: the brush, its surface material, and the numbers
/// the BSP / collision / render passes need. The volume is the whole brush
/// (surface to bottom); the surface is the top (+Z) face only.
/// </summary>
public sealed class WaterVolume
{
	public MapBrush Brush { get; }
	/// <summary>Material name of the top face, e.g. "nature/water".</summary>
	public string SurfaceMaterial { get; }
	/// <summary>Z of the water surface (the top face's plane distance).</summary>
	public float SurfaceLevel { get; }
	/// <summary>Surface level minus the lowest vertex: how deep the pool is.</summary>
	public float Depth { get; }
	/// <summary>Area of the top face, in square inches.</summary>
	public float SurfaceArea { get; }
	/// <summary>Brush volume, in cubic inches.</summary>
	public float Volume { get; }
	public WaterSettings Settings { get; }

	internal WaterVolume( MapBrush brush, string surfaceMaterial, float surfaceLevel,
		float depth, float surfaceArea, float volume, WaterSettings settings )
	{
		Brush = brush;
		SurfaceMaterial = surfaceMaterial;
		SurfaceLevel = surfaceLevel;
		Depth = depth;
		SurfaceArea = surfaceArea;
		Volume = volume;
		Settings = settings ?? WaterSettings.Default;
	}

	/// <summary>True when the point is inside the water brush (surface to bottom).</summary>
	public bool Contains( Vec3 p ) => WaterLadders.PointInBrush( Brush, p );

	/// <summary>
	/// True when the point is inside the brush AND strictly below the surface:
	/// a point exactly on the surface is at the waterline, not under water.
	/// </summary>
	public bool IsUnderwater( Vec3 p ) => Contains( p ) && p.z < SurfaceLevel;

	public override string ToString() =>
		$"water '{SurfaceMaterial}' level {SurfaceLevel:0.###} depth {Depth:0.###} area {SurfaceArea:0.###} vol {Volume:0.###} lod {Settings.Lod}";
}

/// <summary>
/// The water brush rule (Report §1.2 #47/#48): top face water, other faces
/// nodraw. Build with <see cref="Create"/>, recognise with <see cref="Classify"/>.
/// </summary>
public static class WaterBrush
{
	/// <summary>
	/// Build a water brush from a box: the +Z face gets
	/// <paramref name="waterMaterial"/>, the other five faces get
	/// tools/toolsnodraw. The material must name water (see
	/// <see cref="WaterLadders.IsWaterMaterial"/>) and must not itself be a tool
	/// texture — a tool texture is not a water surface.
	/// </summary>
	public static MapBrush Create( Vec3 min, Vec3 max, string waterMaterial, WaterSettings settings = null )
	{
		if ( string.IsNullOrWhiteSpace( waterMaterial ) )
			throw new ArgumentException( "A water brush needs a water material name; a blank name is not a material." );
		if ( ToolTexture.Lookup( waterMaterial ) is not null )
			throw new ArgumentException( $"'{waterMaterial}' is a tool texture, not a water surface: a water face needs an ordinary water material." );
		if ( !WaterLadders.IsWaterMaterial( waterMaterial ) )
			throw new ArgumentException( $"'{waterMaterial}' does not name water: a water surface material must contain 'water' (e.g. 'nature/water')." );
		RequireBox( min, max, "water brush" );
		var water = new FaceMaterial( waterMaterial.Trim() );
		var nodraw = FaceMaterial.Default;
		return MapBrush.FromPlanes( new[]
		{
			new Effigy.MapBrushFace( new Vec3( -1, 0, 0 ), -min.x, nodraw ),
			new Effigy.MapBrushFace( new Vec3( 1, 0, 0 ), max.x, nodraw ),
			new Effigy.MapBrushFace( new Vec3( 0, -1, 0 ), -min.y, nodraw ),
			new Effigy.MapBrushFace( new Vec3( 0, 1, 0 ), max.y, nodraw ),
			new Effigy.MapBrushFace( new Vec3( 0, 0, -1 ), -min.z, nodraw ),
			new Effigy.MapBrushFace( new Vec3( 0, 0, 1 ), max.z, water ),
		} );
	}

	/// <summary>
	/// Classify a brush as a water brush. Returns the volume and an empty defect
	/// list on success; null + named defects otherwise. Defect codes:
	/// InvalidBrush, NoWaterFace, MultipleWaterFaces, WaterFaceNotOnTop,
	/// SideFaceNotNoDraw, ZeroDepth, ZeroSurfaceArea. A null brush throws.
	/// <paramref name="isWaterMaterial"/> overrides the name rule (see
	/// <see cref="WaterLadders.IsWaterMaterial"/>) for maps whose water
	/// materials do not contain "water".
	/// </summary>
	public static (WaterVolume Volume, IReadOnlyList<string> Defects) Classify(
		MapBrush brush, Func<string, bool> isWaterMaterial = null, WaterSettings settings = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var defects = new List<string>();
		foreach ( var d in brush.Validate() )
			defects.Add( $"InvalidBrush: {d.Code}: {d.Message}" );
		if ( defects.Count > 0 ) return (null, defects);
		var isWater = isWaterMaterial ?? WaterLadders.IsWaterMaterial;

		var waterFaces = new List<int>();
		for ( var i = 0; i < brush.FaceCount; i++ )
			if ( isWater( brush.Faces[i].MaterialId ) ) waterFaces.Add( i );
		if ( waterFaces.Count == 0 )
		{
			defects.Add( "NoWaterFace: no face carries a water material — a water brush needs exactly one water face, on top." );
			return (null, defects);
		}
		if ( waterFaces.Count > 1 )
		{
			defects.Add( $"MultipleWaterFaces: {waterFaces.Count} faces carry a water material ({string.Join( ", ", waterFaces )}) — a water brush has exactly one, on top." );
			return (null, defects);
		}
		var wi = waterFaces[0];
		var waterFace = brush.Faces[wi];
		if ( Vec3.Dot( waterFace.Normal, new Vec3( 0, 0, 1 ) ) < 1f - 1e-4f )
		{
			defects.Add( $"WaterFaceNotOnTop: water face {wi} has normal {waterFace.Normal}, not +Z — the water surface is the top face." );
			return (null, defects);
		}
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			if ( i == wi ) continue;
			if ( !brush.Faces[i].Material.IsNoDraw )
				defects.Add( $"SideFaceNotNoDraw: face {i} uses '{brush.Faces[i].MaterialId}' — every non-water face of a water brush must be {ToolTexture.NoDrawName}." );
		}
		if ( defects.Count > 0 ) return (null, defects);

		var surfaceLevel = waterFace.Distance;
		var minZ = float.MaxValue;
		foreach ( var v in brush.Vertices ) minZ = MathF.Min( minZ, v.z );
		var depth = surfaceLevel - minZ;
		if ( depth <= 0f )
		{
			defects.Add( $"ZeroDepth: water surface level {surfaceLevel} is not above the brush bottom {minZ}." );
			return (null, defects);
		}
		var area = WaterLadders.PolygonArea( brush.FaceVertices( wi ) );
		if ( area <= 0f )
		{
			defects.Add( "ZeroSurfaceArea: the water face has no area." );
			return (null, defects);
		}
		return (new WaterVolume( brush, waterFace.MaterialId, surfaceLevel, depth, area, brush.Volume, settings ?? WaterSettings.Default ), defects);
	}

	internal static void RequireBox( Vec3 min, Vec3 max, string what )
	{
		if ( min.x >= max.x ) throw new ArgumentException( $"{what}: min.x ({min.x}) must be below max.x ({max.x})." );
		if ( min.y >= max.y ) throw new ArgumentException( $"{what}: min.y ({min.y}) must be below max.y ({max.y})." );
		if ( min.z >= max.z ) throw new ArgumentException( $"{what}: min.z ({min.z}) must be below max.z ({max.z})." );
	}
}

/// <summary>
/// An invisible clip volume: a brush of one clip tool texture. The collision
/// layer is the whole point — clip blocks players and NPCs, playerclip players
/// only, npcclip NPCs only, and NO clip kind ever blocks bullets.
/// </summary>
public sealed class ClipVolume
{
	public MapBrush Brush { get; }
	public ToolTextureKind Kind { get; }
	public CollisionLayer Layers { get; }

	internal ClipVolume( MapBrush brush, ToolTextureKind kind, CollisionLayer layers )
	{
		Brush = brush;
		Kind = kind;
		Layers = layers;
	}

	/// <summary>The tool texture name for a clip kind.</summary>
	public static string MaterialName( ToolTextureKind kind ) => kind switch
	{
		ToolTextureKind.Clip => "tools/toolsclip",
		ToolTextureKind.PlayerClip => "tools/toolsplayerclip",
		ToolTextureKind.NpcClip => "tools/toolsnpcclip",
		_ => throw new ArgumentException( $"'{kind}' is not a clip kind: use Clip, PlayerClip or NpcClip." ),
	};

	/// <summary>Collision layers for a clip kind: Clip = players + NPCs, PlayerClip = players, NpcClip = NPCs. Bullets are never blocked.</summary>
	public static CollisionLayer LayersFor( ToolTextureKind kind ) => kind switch
	{
		ToolTextureKind.Clip => CollisionLayer.AllMovement,
		ToolTextureKind.PlayerClip => CollisionLayer.Player,
		ToolTextureKind.NpcClip => CollisionLayer.Npc,
		_ => throw new ArgumentException( $"'{kind}' is not a clip kind: use Clip, PlayerClip or NpcClip." ),
	};

	/// <summary>Build a clip box: every face the clip tool texture for <paramref name="kind"/>.</summary>
	public static ClipVolume Create( Vec3 min, Vec3 max, ToolTextureKind kind = ToolTextureKind.Clip )
	{
		WaterBrush.RequireBox( min, max, "clip volume" );
		var brush = MapBrush.Box( min, max, MaterialName( kind ) );
		var (volume, defects) = FromBrush( brush );
		if ( volume is null )
			throw new ArgumentException( $"Cannot build a clip volume: {string.Join( "; ", defects )}" );
		return volume;
	}

	/// <summary>
	/// Classify a brush as a clip volume. Null + named defects otherwise:
	/// InvalidBrush, NoClipFaces, MixedClipKinds, NonClipFace. A clip brush is
	/// ALL clip faces of ONE kind — a brick face or a second clip kind on the
	/// same brush is a defect, not a partial clip.
	/// </summary>
	public static (ClipVolume Volume, IReadOnlyList<string> Defects) FromBrush( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var defects = new List<string>();
		foreach ( var d in brush.Validate() )
			defects.Add( $"InvalidBrush: {d.Code}: {d.Message}" );
		if ( defects.Count > 0 ) return (null, defects);
		ToolTextureKind? kind = null;
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			var k = brush.Faces[i].Material.Tool?.Kind;
			if ( k is ToolTextureKind.Clip or ToolTextureKind.PlayerClip or ToolTextureKind.NpcClip )
			{
				if ( kind is null ) kind = k;
				else if ( kind != k )
					defects.Add( $"MixedClipKinds: face {i} is {k} but the brush is {kind} — one clip brush has one clip kind." );
			}
			else
			{
				defects.Add( $"NonClipFace: face {i} uses '{brush.Faces[i].MaterialId}' — every face of a clip volume must be the clip tool texture." );
			}
		}
		if ( kind is null )
		{
			defects.Add( "NoClipFaces: no face carries a clip tool texture (toolsclip / toolsplayerclip / toolsnpcclip)." );
			return (null, defects);
		}
		if ( defects.Count > 0 ) return (null, defects);
		return (new ClipVolume( brush, kind.Value, LayersFor( kind.Value ) ), defects);
	}

	/// <summary>True when this volume blocks <paramref name="mover"/> (bullets: always false).</summary>
	public bool Blocks( MoverKind mover ) => ( Layers & WaterLadders.LayerFor( mover ) ) != 0;

	/// <summary>True when the point is inside the clip volume.</summary>
	public bool Contains( Vec3 p ) => WaterLadders.PointInBrush( Brush, p );

	/// <summary>True when the point is inside the volume AND this volume blocks the mover.</summary>
	public bool BlocksPoint( Vec3 p, MoverKind mover ) => Blocks( mover ) && Contains( p );

	public override string ToString() => $"clip {Kind} layers {Layers}";
}

/// <summary>
/// A climbable ladder volume: a brush of toolsladder. It is NEVER solid — it
/// blocks no mover, it is a volume the player climbs through. Top / Bottom are
/// the brush's Z extents, the climb range.
/// </summary>
public sealed class LadderVolume
{
	public MapBrush Brush { get; }
	/// <summary>Highest Z of the ladder (where the climb ends).</summary>
	public float Top { get; }
	/// <summary>Lowest Z of the ladder (where the climb starts).</summary>
	public float Bottom { get; }
	public float Height => Top - Bottom;

	internal LadderVolume( MapBrush brush, float top, float bottom )
	{
		Brush = brush;
		Top = top;
		Bottom = bottom;
	}

	/// <summary>Build a ladder box: every face toolsladder.</summary>
	public static LadderVolume Create( Vec3 min, Vec3 max )
	{
		WaterBrush.RequireBox( min, max, "ladder volume" );
		var brush = MapBrush.Box( min, max, "tools/toolsladder" );
		var (volume, defects) = FromBrush( brush );
		if ( volume is null )
			throw new ArgumentException( $"Cannot build a ladder volume: {string.Join( "; ", defects )}" );
		return volume;
	}

	/// <summary>
	/// Classify a brush as a ladder volume. Null + named defects otherwise:
	/// InvalidBrush, NoLadderFaces, NonLadderFace. A ladder brush is ALL
	/// toolsladder faces.
	/// </summary>
	public static (LadderVolume Volume, IReadOnlyList<string> Defects) FromBrush( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var defects = new List<string>();
		foreach ( var d in brush.Validate() )
			defects.Add( $"InvalidBrush: {d.Code}: {d.Message}" );
		if ( defects.Count > 0 ) return (null, defects);
		var anyLadder = false;
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			if ( brush.Faces[i].Material.Tool?.Kind == ToolTextureKind.Ladder ) anyLadder = true;
			else defects.Add( $"NonLadderFace: face {i} uses '{brush.Faces[i].MaterialId}' — every face of a ladder volume must be toolsladder." );
		}
		if ( !anyLadder )
		{
			defects.Add( "NoLadderFaces: no face carries toolsladder." );
			return (null, defects);
		}
		if ( defects.Count > 0 ) return (null, defects);
		var top = float.MinValue; var bottom = float.MaxValue;
		foreach ( var v in brush.Vertices ) { top = MathF.Max( top, v.z ); bottom = MathF.Min( bottom, v.z ); }
		return (new LadderVolume( brush, top, bottom ), defects);
	}

	/// <summary>True when the point is inside the ladder volume.</summary>
	public bool Contains( Vec3 p ) => WaterLadders.PointInBrush( Brush, p );

	/// <summary>True when the point is inside the ladder: this is where the player can climb.</summary>
	public bool IsClimbableAt( Vec3 p ) => Contains( p );

	/// <summary>A ladder never blocks movement, for any mover.</summary>
	public bool BlocksPoint( Vec3 p, MoverKind mover ) => false;

	public override string ToString() => $"ladder z {Bottom:0.###}..{Top:0.###} height {Height:0.###}";
}

/// <summary>
/// One blocking volume in the collision build: a solid world brush (all
/// layers) or a clip volume (its clip layers). Water and ladders are never
/// blocking volumes — they are listed separately on
/// <see cref="CollisionVolumes"/>.
/// </summary>
public sealed class CollisionVolume
{
	public MapBrush Brush { get; }
	public BrushRole Role { get; }
	public CollisionLayer Layers { get; }

	internal CollisionVolume( MapBrush brush, BrushRole role, CollisionLayer layers )
	{
		Brush = brush;
		Role = role;
		Layers = layers;
	}

	public bool Blocks( MoverKind mover ) => ( Layers & WaterLadders.LayerFor( mover ) ) != 0;
	public bool Contains( Vec3 p ) => WaterLadders.PointInBrush( Brush, p );
	public bool BlocksPoint( Vec3 p, MoverKind mover ) => Blocks( mover ) && Contains( p );

	/// <summary>True when the segment enters the volume while the mover is blocked by it; <paramref name="hit"/> is the entry point.</summary>
	public bool BlocksSegment( Vec3 a, Vec3 b, MoverKind mover, out Vec3 hit )
	{
		hit = default;
		if ( !Blocks( mover ) ) return false;
		if ( !WaterLadders.SegmentIntersectsBrush( a, b, Brush, out var t ) ) return false;
		hit = a + ( b - a ) * t;
		return true;
	}

	public override string ToString() => $"{Role} layers {Layers}";
}

/// <summary>One walk-validation hit: which volume blocked the walk, and where the mover entered it.</summary>
public sealed class CollisionHit
{
	public CollisionVolume Volume { get; }
	public Vec3 Point { get; }
	/// <summary>Entry distance along the segment, 0 at the start and 1 at the end.</summary>
	public float T { get; }

	internal CollisionHit( CollisionVolume volume, Vec3 point, float t )
	{
		Volume = volume;
		Point = point;
		T = t;
	}

	public override string ToString() => $"blocked by {Volume} at {Point} (t={T:0.###})";
}

/// <summary>
/// The collision build: classify every brush once, then answer the
/// walk-validation query. Solid world brushes block every mover; clip volumes
/// block their layers; water and ladders block nothing and are listed
/// separately so a caller can ask "is this point underwater / on a ladder"
/// without treating them as walls. Brushes that are invalid or mix roles are
/// skipped and named in <see cref="Defects"/> — never silently treated as solid.
/// </summary>
public sealed class CollisionVolumes
{
	public IReadOnlyList<CollisionVolume> Blocking { get; }
	public IReadOnlyList<WaterVolume> Waters { get; }
	public IReadOnlyList<LadderVolume> Ladders { get; }
	public IReadOnlyList<string> Defects { get; }
	/// <summary>Brushes that are pure NonSolid tool volumes (trigger, fog, ...) — ignored by collision, counted so the build is auditable.</summary>
	public int IgnoredNonSolidTool { get; }

	CollisionVolumes( List<CollisionVolume> blocking, List<WaterVolume> waters,
		List<LadderVolume> ladders, List<string> defects, int ignored )
	{
		Blocking = blocking;
		Waters = waters;
		Ladders = ladders;
		Defects = defects;
		IgnoredNonSolidTool = ignored;
	}

	/// <summary>Classify every brush and build the collision set. A null list throws; a null brush is a defect, not a crash.</summary>
	public static CollisionVolumes Build( IReadOnlyList<MapBrush> brushes )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var blocking = new List<CollisionVolume>();
		var waters = new List<WaterVolume>();
		var ladders = new List<LadderVolume>();
		var defects = new List<string>();
		var ignored = 0;
		for ( var i = 0; i < brushes.Count; i++ )
		{
			var brush = brushes[i];
			if ( brush is null ) { defects.Add( $"brush {i}: NullBrush: null brush skipped." ); continue; }
			var (role, roleDefects) = WaterLadders.ClassifyBrush( brush );
			switch ( role )
			{
				case BrushRole.Solid:
					blocking.Add( new CollisionVolume( brush, role, CollisionLayer.All ) );
					break;
				case BrushRole.Water:
					waters.Add( WaterBrush.Classify( brush ).Volume );
					break;
				case BrushRole.Clip:
				case BrushRole.PlayerClip:
				case BrushRole.NpcClip:
					blocking.Add( new CollisionVolume( brush, role, ClipVolume.LayersFor(
						role == BrushRole.Clip ? ToolTextureKind.Clip :
						role == BrushRole.PlayerClip ? ToolTextureKind.PlayerClip : ToolTextureKind.NpcClip ) ) );
					break;
				case BrushRole.Ladder:
					ladders.Add( LadderVolume.FromBrush( brush ).Volume );
					break;
				case BrushRole.NonSolidTool:
					ignored++;
					break;
				default:
					foreach ( var d in roleDefects ) defects.Add( $"brush {i}: {d}" );
					break;
			}
		}
		return new CollisionVolumes( blocking, waters, ladders, defects, ignored );
	}

	/// <summary>True when the point is inside a volume that blocks this mover.</summary>
	public bool BlocksPoint( Vec3 p, MoverKind mover )
	{
		foreach ( var v in Blocking ) if ( v.BlocksPoint( p, mover ) ) return true;
		return false;
	}

	/// <summary>
	/// The walk-validation query: does the straight walk from
	/// <paramref name="from"/> to <paramref name="to"/> enter a volume that
	/// blocks <paramref name="mover"/>? Returns the NEAREST blocking volume
	/// and its entry point, or false with a null hit when the walk is clear.
	/// A walk that starts inside a blocking volume is blocked at its start.
	/// </summary>
	public bool BlocksWalk( Vec3 from, Vec3 to, MoverKind mover, out CollisionHit hit )
	{
		hit = null;
		var bestT = float.MaxValue;
		CollisionVolume best = null;
		Vec3 bestPoint = default;
		foreach ( var v in Blocking )
		{
			if ( !v.Blocks( mover ) ) continue;
			if ( !WaterLadders.SegmentIntersectsBrush( from, to, v.Brush, out var t ) ) continue;
			if ( t < bestT ) { bestT = t; best = v; bestPoint = from + ( to - from ) * t; }
		}
		if ( best is null ) return false;
		hit = new CollisionHit( best, bestPoint, bestT );
		return true;
	}

	/// <summary>True when the point is underwater in any water volume here.</summary>
	public bool IsUnderwater( Vec3 p )
	{
		foreach ( var w in Waters ) if ( w.IsUnderwater( p ) ) return true;
		return false;
	}

	/// <summary>True when the point is inside any ladder volume here.</summary>
	public bool IsOnLadder( Vec3 p )
	{
		foreach ( var l in Ladders ) if ( l.Contains( p ) ) return true;
		return false;
	}
}

/// <summary>Which visibility a blocker face stops: light (bake) or AI sight.</summary>
public enum BlockerFaceKind
{
	BlockLight,
	BlockLos,
}

/// <summary>One blocklight / blocklos face: its plane, its polygon, and what it blocks.</summary>
public sealed class BlockerFace
{
	public int BrushIndex { get; }
	public int FaceIndex { get; }
	public BlockerFaceKind Kind { get; }
	public Vec3 Normal { get; }
	public float Distance { get; }
	public IReadOnlyList<Vec3> Polygon { get; }

	internal BlockerFace( int brushIndex, int faceIndex, BlockerFaceKind kind, Vec3 normal, float distance, IReadOnlyList<Vec3> polygon )
	{
		BrushIndex = brushIndex;
		FaceIndex = faceIndex;
		Kind = kind;
		Normal = normal;
		Distance = distance;
		Polygon = polygon;
	}

	/// <summary>True when the segment crosses this face's polygon.</summary>
	public bool BlocksSegment( Vec3 a, Vec3 b ) => WaterLadders.SegmentHitsPolygon( a, b, Polygon, Normal, Distance );

	public override string ToString() => $"{Kind} brush {BrushIndex} face {FaceIndex}";
}

/// <summary>
/// Blocklight / blockLOS as native brush/face flags, consumed by the bake and
/// visibility passes. These are PER-FACE flags: a single toolsblocklight face
/// on an otherwise ordinary solid brush blocks light through exactly that
/// face. A face carrying both flags (not in the registry today, but possible)
/// is collected once per kind.
/// </summary>
public sealed class VisibilityBlockers
{
	public IReadOnlyList<BlockerFace> Faces { get; }
	public IReadOnlyList<string> Defects { get; }

	VisibilityBlockers( List<BlockerFace> faces, List<string> defects )
	{
		Faces = faces;
		Defects = defects;
	}

	/// <summary>Collect every blocklight / blocklos face from the brushes. A null list throws; a null brush is a defect.</summary>
	public static VisibilityBlockers FromBrushes( IReadOnlyList<MapBrush> brushes )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var faces = new List<BlockerFace>();
		var defects = new List<string>();
		for ( var bi = 0; bi < brushes.Count; bi++ )
		{
			var brush = brushes[bi];
			if ( brush is null ) { defects.Add( $"brush {bi}: NullBrush: null brush skipped." ); continue; }
			for ( var fi = 0; fi < brush.FaceCount; fi++ )
			{
				var face = brush.Faces[fi];
				var flags = face.Material.Tool?.CompileFlags ?? ToolCompileFlags.None;
				if ( ( flags & ( ToolCompileFlags.BlockLight | ToolCompileFlags.BlockLos ) ) == 0 ) continue;
				var polygon = brush.FaceVertices( fi );
				if ( polygon is null || polygon.Count < 3 )
				{
					defects.Add( $"brush {bi} face {fi}: NoPolygon: the blocking face has no polygon, skipped." );
					continue;
				}
				if ( ( flags & ToolCompileFlags.BlockLight ) != 0 )
					faces.Add( new BlockerFace( bi, fi, BlockerFaceKind.BlockLight, face.Normal, face.Distance, polygon ) );
				if ( ( flags & ToolCompileFlags.BlockLos ) != 0 )
					faces.Add( new BlockerFace( bi, fi, BlockerFaceKind.BlockLos, face.Normal, face.Distance, polygon ) );
			}
		}
		return new VisibilityBlockers( faces, defects );
	}

	/// <summary>True when a blocklight face stands between the two points.</summary>
	public bool BlocksLight( Vec3 from, Vec3 to )
	{
		foreach ( var f in Faces )
			if ( f.Kind == BlockerFaceKind.BlockLight && f.BlocksSegment( from, to ) ) return true;
		return false;
	}

	/// <summary>True when a blockLOS face stands between the two points (AI line of sight).</summary>
	public bool BlocksLineOfSight( Vec3 from, Vec3 to )
	{
		foreach ( var f in Faces )
			if ( f.Kind == BlockerFaceKind.BlockLos && f.BlocksSegment( from, to ) ) return true;
		return false;
	}

	public int BlockLightFaceCount => Faces.Count( f => f.Kind == BlockerFaceKind.BlockLight );
	public int BlockLosFaceCount => Faces.Count( f => f.Kind == BlockerFaceKind.BlockLos );
}

/// <summary>
/// func_water_analog: the brush entity that makes a water brush behave as
/// water in-game (swimmable volume, surface effects). Source's func_water_analog
/// has no special keyvalues beyond the implicit base ones (targetname, origin,
/// angles, spawnflags) — the water behaviour comes from the tied brushes being
/// water brushes, which <see cref="Place"/> enforces.
///
/// REGISTRY. The class is NOT in the core registry file (another ticket's
/// file, read-only here). <see cref="RegistryFragmentJson"/> is the fragment
/// for it, in exactly the EntityRegistry schema (schemaVersion 1); the same
/// JSON ships as a fixture at
/// tests/fixtures/hammer/hammer-water-ladders-clip-tool-textures/func-water-analog.json.
/// <see cref="EnsureClass"/> merges it into the caller's registry once —
/// merging twice is a DuplicateClass error in the registry, so the merge is
/// guarded by <see cref="EntityRegistry.Contains"/> and placing two water
/// entities on one registry works.
/// </summary>
public static class FuncWaterAnalog
{
	public const string ClassName = "func_water_analog";

	/// <summary>The registry fragment for func_water_analog, in EntityRegistry schemaVersion 1.</summary>
	public const string RegistryFragmentJson =
		"{\"schemaVersion\":1,\"name\":\"water-ladders-func-water-analog\",\"classes\":[" +
		"{\"name\":\"func_water_analog\",\"kind\":\"brush\"," +
		"\"description\":\"Water volume: the tied brushes must be water brushes (top face water, other faces nodraw). Makes the volume swimmable.\"," +
		"\"baseClasses\":[\"Targetname\"],\"keyvalues\":[],\"spawnflags\":[],\"inputs\":[],\"outputs\":[]," +
		"\"helpers\":[{\"kind\":\"origin\"}]}]}";

	/// <summary>Merge the func_water_analog class into the registry when it is not already there. Idempotent.</summary>
	public static void EnsureClass( EntityRegistry registry )
	{
		if ( registry is null ) throw new ArgumentNullException( nameof( registry ) );
		if ( registry.Contains( ClassName ) ) return;
		registry.Merge( EntityRegistry.LoadFromJson( RegistryFragmentJson, "func-water-analog.json" ) );
	}

	/// <summary>
	/// Tie water brushes into a func_water_analog entity. Every brush must
	/// classify as a water brush — a brick brush tied as water is an
	/// <see cref="ArgumentException"/> naming the defects, not a silent bad
	/// entity. Origin defaults to the first brush's centroid.
	/// </summary>
	public static MapEntity Place( MapEntities entities, IReadOnlyList<MapBrush> brushes,
		IReadOnlyDictionary<string, string> keyvalues = null, Vec3? origin = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( brushes is null || brushes.Count == 0 )
			throw new ArgumentException( "func_water_analog needs at least one tied brush: a water entity with no brushes is not water." );
		for ( var i = 0; i < brushes.Count; i++ )
		{
			if ( brushes[i] is null )
				throw new ArgumentException( $"func_water_analog brush {i} is null." );
			var (_, defects) = WaterBrush.Classify( brushes[i] );
			if ( defects.Count > 0 )
				throw new ArgumentException( $"func_water_analog brush {i} is not a water brush: {string.Join( "; ", defects )}" );
		}
		EnsureClass( entities.Registry );
		return entities.PlaceBrushEntity( ClassName, brushes, keyvalues, origin ?? brushes[0].Centroid );
	}

	/// <summary>
	/// Validate a placed func_water_analog entity: right class, at least one
	/// tied brush, every tied brush a MapBrush and a water brush. Named defects,
	/// empty when valid. A null entity throws.
	/// </summary>
	public static IReadOnlyList<string> Validate( MapEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		var defects = new List<string>();
		if ( !string.Equals( entity.ClassName, ClassName, StringComparison.OrdinalIgnoreCase ) )
			defects.Add( $"WrongClass: entity #{entity.Id} is '{entity.ClassName}', not '{ClassName}'." );
		if ( !entity.IsBrushEntity )
			defects.Add( $"NotBrushEntity: entity #{entity.Id} '{entity.ClassName}' is not a brush entity." );
		if ( entity.Brushes.Count == 0 )
			defects.Add( $"NoBrushes: entity #{entity.Id} '{entity.ClassName}' ties no brushes." );
		for ( var i = 0; i < entity.Brushes.Count; i++ )
		{
			if ( entity.Brushes[i] is not MapBrush brush )
			{
				defects.Add( $"NonMapBrush: tied brush {i} is a {entity.Brushes[i]?.GetType().Name ?? "null"}, not a MapBrush." );
				continue;
			}
			var (_, brushDefects) = WaterBrush.Classify( brush );
			foreach ( var d in brushDefects ) defects.Add( $"brush {i}: {d}" );
		}
		return defects;
	}

	/// <summary>The tied water brushes of a func_water_analog entity, or defects when it is not one.</summary>
	public static (IReadOnlyList<MapBrush> Brushes, IReadOnlyList<string> Defects) WaterBrushes( MapEntity entity )
	{
		var defects = Validate( entity );
		if ( defects.Count > 0 ) return (Array.Empty<MapBrush>(), defects);
		return (entity.GetBrushes<MapBrush>(), defects);
	}
}

/// <summary>
/// Shared rules and geometry for the water / ladder / clip ticket: brush
/// classification, the bake-face rule, the water-material name rule, and the
/// convex-brush point / segment tests everything else is built on.
/// </summary>
public static class WaterLadders
{
	/// <summary>Point-in-brush epsilon, in model units (inches): MapBrush.PlaneEps.</summary>
	public const float Epsilon = 1e-3f;

	/// <summary>The collision layer a mover belongs to.</summary>
	public static CollisionLayer LayerFor( MoverKind mover ) => mover switch
	{
		MoverKind.Player => CollisionLayer.Player,
		MoverKind.Npc => CollisionLayer.Npc,
		MoverKind.Bullet => CollisionLayer.Bullet,
		_ => throw new ArgumentException( $"Unknown mover kind: {mover}." ),
	};

	/// <summary>
	/// The water-material name rule: the name contains "water"
	/// (case-insensitive) and is NOT a tool texture. A name rule is honest
	/// about what it is — Source has no single canonical water material, so
	/// maps whose water materials do not contain "water" pass an explicit
	/// predicate to <see cref="WaterBrush.Classify"/> instead.
	/// </summary>
	public static bool IsWaterMaterial( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) ) return false;
		if ( ToolTexture.Lookup( materialName ) is not null ) return false;
		return materialName.Contains( "water", StringComparison.OrdinalIgnoreCase );
	}

	/// <summary>
	/// Classify a brush to its <see cref="BrushRole"/>, with named defects for
	/// Invalid and MixedTool. Blocklight / blocklos faces do not change a
	/// brush's role (they are solid faces of an otherwise solid brush — see
	/// <see cref="VisibilityBlockers"/>); nodraw faces likewise.
	/// </summary>
	public static (BrushRole Role, IReadOnlyList<string> Defects) ClassifyBrush( MapBrush brush )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var defects = new List<string>();
		foreach ( var d in brush.Validate() )
			defects.Add( $"InvalidBrush: {d.Code}: {d.Message}" );
		if ( defects.Count > 0 ) return (BrushRole.Invalid, defects);

		// Water first: any water-material face means the brush claims to be water.
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			if ( !IsWaterMaterial( brush.Faces[i].MaterialId ) ) continue;
			var (volume, waterDefects) = WaterBrush.Classify( brush );
			return volume is not null
				? (BrushRole.Water, waterDefects)
				: (BrushRole.MixedTool, waterDefects);
		}

		ToolTextureKind? clipKind = null;
		var hasLadder = false;
		var hasOtherTool = false;
		var hasOrdinary = false;
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			var tool = brush.Faces[i].Material.Tool;
			if ( tool is null ) { hasOrdinary = true; continue; }
			switch ( tool.Kind )
			{
				case ToolTextureKind.Clip:
				case ToolTextureKind.PlayerClip:
				case ToolTextureKind.NpcClip:
					if ( clipKind is null ) clipKind = tool.Kind;
					else if ( clipKind != tool.Kind )
						defects.Add( $"MixedClipKinds: face {i} is {tool.Kind} but the brush is {clipKind} — one clip brush has one clip kind." );
					break;
				case ToolTextureKind.Ladder:
					hasLadder = true;
					break;
				case ToolTextureKind.NoDraw:
				case ToolTextureKind.BlockLight:
				case ToolTextureKind.BlockLos:
					// Solid faces: no role of their own.
					break;
				default:
					hasOtherTool = true;
					break;
			}
		}
		if ( clipKind is not null )
		{
			if ( hasOrdinary || hasLadder || hasOtherTool || defects.Count > 0 )
			{
				if ( hasOrdinary ) defects.Add( "MixedToolFaces: a clip volume mixes clip faces with ordinary faces — every face must be the clip tool texture." );
				if ( hasLadder ) defects.Add( "MixedToolFaces: a clip volume mixes clip faces with ladder faces." );
				if ( hasOtherTool ) defects.Add( "MixedToolFaces: a clip volume mixes clip faces with other tool faces." );
				return (BrushRole.MixedTool, defects);
			}
			return (clipKind.Value switch
			{
				ToolTextureKind.Clip => BrushRole.Clip,
				ToolTextureKind.PlayerClip => BrushRole.PlayerClip,
				_ => BrushRole.NpcClip,
			}, defects);
		}
		if ( hasLadder )
		{
			if ( hasOrdinary || hasOtherTool )
			{
				if ( hasOrdinary ) defects.Add( "MixedToolFaces: a ladder volume mixes ladder faces with ordinary faces — every face must be toolsladder." );
				if ( hasOtherTool ) defects.Add( "MixedToolFaces: a ladder volume mixes ladder faces with other tool faces." );
				return (BrushRole.MixedTool, defects);
			}
			var (_, ladderDefects) = LadderVolume.FromBrush( brush );
			return ladderDefects.Count == 0 ? (BrushRole.Ladder, defects) : (BrushRole.MixedTool, ladderDefects);
		}
		if ( hasOtherTool && !hasOrdinary ) return (BrushRole.NonSolidTool, defects);
		if ( hasOtherTool )
		{
			defects.Add( "MixedToolFaces: NonSolid tool faces (trigger / fog / sky / hint / skip / origin / areaportal / occluder) mixed with ordinary faces." );
			return (BrushRole.MixedTool, defects);
		}
		return (BrushRole.Solid, defects);
	}

	/// <summary>
	/// The bake-face rule for this ticket: a face bakes when it is an ordinary
	/// material or a water surface. Nodraw faces never bake (already the rule
	/// in <see cref="FaceMaterial.ContributesToBakedMesh"/>); neither does any
	/// NonSolid tool face (clip, ladder, trigger, sky, ...) nor a blocklight /
	/// blocklos face — those are invisible by definition. DIVERGENCE: the
	/// landed <see cref="MapBakeExport"/> only drops nodraw today, so the
	/// integrator must consult this rule when wiring clip / ladder / blocker
	/// brushes into the bake pass; this file does not edit that pass.
	/// </summary>
	public static bool ShouldBakeFace( FaceMaterial face )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		if ( face.IsNoDraw ) return false;
		var tool = face.Tool;
		if ( tool is null ) return true;
		var flags = tool.CompileFlags;
		if ( ( flags & ToolCompileFlags.NonSolid ) != 0 ) return false;
		if ( ( flags & ( ToolCompileFlags.BlockLight | ToolCompileFlags.BlockLos ) ) != 0 ) return false;
		return true;
	}

	/// <summary>The indices of <paramref name="faces"/> that bake under <see cref="ShouldBakeFace"/> — the <see cref="FaceMaterial.BakedFaceIndices"/> rule extended to clip / ladder / blocker faces.</summary>
	public static IReadOnlyList<int> BakedFaceIndices( IReadOnlyList<FaceMaterial> faces )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		var baked = new List<int>();
		for ( var i = 0; i < faces.Count; i++ )
			if ( faces[i] is not null && ShouldBakeFace( faces[i] ) )
				baked.Add( i );
		return baked;
	}

	/// <summary>True when the point is inside the convex brush (all half-spaces, within <see cref="Epsilon"/>).</summary>
	public static bool PointInBrush( MapBrush brush, Vec3 p )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		for ( var i = 0; i < brush.FaceCount; i++ )
			if ( brush.Faces[i].SignedDistance( p ) > Epsilon ) return false;
		return true;
	}

	/// <summary>
	/// Segment vs convex brush, by half-space slab clipping: the segment is
	/// inside the brush for t in [tEnter, tExit]; it hits when that interval
	/// overlaps [0, 1]. Returns the entry t (0 when the segment starts inside).
	/// </summary>
	public static bool SegmentIntersectsBrush( Vec3 a, Vec3 b, MapBrush brush, out float tHit )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		tHit = 0f;
		var tEnter = 0f;
		var tExit = 1f;
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			var face = brush.Faces[i];
			var d0 = face.SignedDistance( a );
			var d1 = face.SignedDistance( b );
			if ( d0 > Epsilon && d1 > Epsilon ) return false;
			var denom = d1 - d0;
			if ( MathF.Abs( denom ) < 1e-9f ) continue;
			var tStar = ( Epsilon - d0 ) / denom;
			if ( denom < 0f ) tEnter = MathF.Max( tEnter, tStar );
			else tExit = MathF.Min( tExit, tStar );
			if ( tEnter > tExit ) return false;
		}
		tHit = MathF.Max( 0f, MathF.Min( 1f, tEnter ) );
		return true;
	}

	/// <summary>Polygon area by Newell's method (half the Newell normal's length). Empty / degenerate input is 0.</summary>
	public static float PolygonArea( IReadOnlyList<Vec3> polygon )
	{
		if ( polygon is null || polygon.Count < 3 ) return 0f;
		var n = Vec3.Zero;
		for ( var i = 0; i < polygon.Count; i++ )
			n += Vec3.Cross( polygon[i], polygon[( i + 1 ) % polygon.Count] );
		return n.Length / 2f;
	}

	/// <summary>
	/// Segment vs one face polygon: cross the face plane inside the segment,
	/// then check the crossing point is inside the polygon (2D ray casting in
	/// the face-plane basis; a point on an edge counts as inside). A segment
	/// parallel to the plane never blocks.
	/// </summary>
	public static bool SegmentHitsPolygon( Vec3 a, Vec3 b, IReadOnlyList<Vec3> polygon, Vec3 normal, float distance )
	{
		if ( polygon is null || polygon.Count < 3 ) return false;
		var dir = b - a;
		var denom = Vec3.Dot( normal, dir );
		if ( MathF.Abs( denom ) < 1e-9f ) return false;
		var t = ( distance - Vec3.Dot( normal, a ) ) / denom;
		if ( t < 0f || t > 1f ) return false;
		var p = a + dir * t;
		return PointInPolygon( p, polygon, normal );
	}

	static bool PointInPolygon( Vec3 p, IReadOnlyList<Vec3> polygon, Vec3 normal )
	{
		var (uAxis, vAxis) = FaceUv.PlaneAxes( normal );
		var origin = polygon[0];
		float Pu( Vec3 q ) => Vec3.Dot( q - origin, uAxis );
		float Pv( Vec3 q ) => Vec3.Dot( q - origin, vAxis );
		var px = Pu( p ); var py = Pv( p );
		// On an edge counts as inside: a light / sight ray grazing a blocker edge is blocked.
		for ( var i = 0; i < polygon.Count; i++ )
		{
			var a = polygon[i]; var b = polygon[( i + 1 ) % polygon.Count];
			var ax = Pu( a ); var ay = Pv( a ); var bx = Pu( b ); var by = Pv( b );
			var edgeLenSq = ( bx - ax ) * ( bx - ax ) + ( by - ay ) * ( by - ay );
			if ( edgeLenSq <= 1e-12f ) continue;
			var t = ( ( px - ax ) * ( bx - ax ) + ( py - ay ) * ( by - ay ) ) / edgeLenSq;
			if ( t < 0f || t > 1f ) continue;
			var cx = ax + t * ( bx - ax ); var cy = ay + t * ( by - ay );
			if ( ( px - cx ) * ( px - cx ) + ( py - cy ) * ( py - cy ) <= 1e-8f ) return true;
		}
		var inside = false;
		for ( var i = 0; i < polygon.Count; i++ )
		{
			var a = polygon[i]; var b = polygon[( i + 1 ) % polygon.Count];
			var ax = Pu( a ); var ay = Pv( a ); var bx = Pu( b ); var by = Pv( b );
			if ( ( ay > py ) != ( by > py ) )
			{
				var xAtY = ax + ( py - ay ) * ( bx - ax ) / ( by - ay );
				if ( px < xAtY ) inside = !inside;
			}
		}
		return inside;
	}
}
