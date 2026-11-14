using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Compile-time behaviour of a tool texture, as data — the %Compile* equivalents from
/// Source .vmt files. These are flags a compiler stage reads, not rendering state: a face
/// carrying one of these materials does something at compile time (or is skipped by it)
/// instead of being drawn like an ordinary surface.
/// </summary>
[Flags]
public enum ToolCompileFlags
{
	None = 0,
	/// <summary>The face has no collision: rays and players pass through it.</summary>
	NonSolid = 1 << 0,
	/// <summary>Detail brush: excluded from vis leaf splitting (%CompileDetail).</summary>
	Detail = 1 << 1,
	/// <summary>The face is not rendered and not baked (%CompileNoDraw).</summary>
	NoDraw = 1 << 2,
	/// <summary>Skybox face: renders the sky, seals the map against leaks.</summary>
	Sky = 1 << 3,
	/// <summary>Vis hint: cuts a vis leaf along this face.</summary>
	Hint = 1 << 4,
	/// <summary>Vis skip: the face is ignored by vis entirely.</summary>
	Skip = 1 << 5,
	/// <summary>Trigger volume face: the brush is a trigger, not solid geometry.</summary>
	Trigger = 1 << 6,
	/// <summary>Clip face: blocks movement (players, NPCs, or both) but is invisible.</summary>
	Clip = 1 << 7,
	/// <summary>Areaportal face: seals a doorway between vis areas.</summary>
	AreaPortal = 1 << 8,
	/// <summary>Occluder face: hides whatever is behind it from the renderer.</summary>
	Occluder = 1 << 9,
	/// <summary>Blocks light: the face stops light without being visible itself.</summary>
	BlockLight = 1 << 10,
	/// <summary>Blocks line of sight for AI without being visible itself.</summary>
	BlockLos = 1 << 11,
	/// <summary>Origin face: marks the origin of a brush entity.</summary>
	Origin = 1 << 12,
}

/// <summary>Which tool texture a material is, by what it does at compile time.</summary>
public enum ToolTextureKind
{
	NoDraw,
	Skybox,
	Hint,
	Skip,
	Trigger,
	Clip,
	AreaPortal,
	Occluder,
	BlockLight,
	BlockLos,
	Origin,
	PlayerClip,
	NpcClip,
	Ladder,
	Fog,
	/// <summary>Invisible, non-solid brush: renders nothing, blocks nothing.</summary>
	Invisible,
}

/// <summary>
/// The tool-texture registry: every Hammer tool texture this build knows, its compile
/// flags, and what it means. A tool texture is a material whose name starts with
/// "tools/" and which does something at compile time instead of being drawn — nodraw,
/// skybox, hint/skip, trigger, clip, areaportal, occluder, blocklight/blockLOS, origin
/// (the list in the ticket, plus the player/NPC clip, ladder and fog textures that sit
/// in the same tools/ family, plus the invisible / invisible-ladder textures).
///
/// SOLIDITY NOTE. Nodraw is NoDraw but NOT NonSolid: a nodraw face is dropped from the
/// baked mesh yet the brush stays solid — that is the whole point of nodraw on unseen
/// faces, and it is why <see cref="FaceMaterial.ContributesToBakedMesh"/> only consults
/// the NoDraw flag. BlockLight and BlockLos are likewise solid: they block light or
/// sight from a face of an otherwise solid brush. Everything else in the registry is
/// NonSolid, because a hint, trigger or clip volume is never a wall.
///
/// DETAIL. <see cref="ToolCompileFlags.Detail"/> (%CompileDetail) is a BRUSH-level flag,
/// not a texture: no tool texture carries it. It lives in the enum so a compiler stage
/// can express "detail brush" with the same flag set, and MapBrush (when it lands) is
/// where it belongs.
/// </summary>
public sealed class ToolTexture
{
	public string Name { get; }
	public ToolTextureKind Kind { get; }
	public ToolCompileFlags CompileFlags { get; }
	public string Description { get; }

	/// <summary>The unseen-face default material: tools/toolsnodraw.</summary>
	public const string NoDrawName = "tools/toolsnodraw";

	public ToolTexture( string name, ToolTextureKind kind, ToolCompileFlags flags, string description )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "A tool texture must have a name." );
		Name = Normalize( name );
		Kind = kind;
		CompileFlags = flags;
		Description = description ?? "";
	}

	/// <summary>True when faces with this texture are dropped from the baked mesh.</summary>
	public bool DropsFromBakedMesh => (CompileFlags & ToolCompileFlags.NoDraw) != 0;

	/// <summary>True when faces with this texture still block movement.</summary>
	public bool IsSolid => (CompileFlags & ToolCompileFlags.NonSolid) == 0;

	static ToolTexture Entry( string name, ToolTextureKind kind, ToolCompileFlags flags, string description ) =>
		new( name, kind, flags, description );

	/// <summary>Every tool texture this build knows, in canonical (lowercase) name order.</summary>
	public static IReadOnlyList<ToolTexture> All { get; } = new[]
	{
		Entry( "tools/toolsareaportal", ToolTextureKind.AreaPortal,
			ToolCompileFlags.AreaPortal | ToolCompileFlags.NonSolid,
			"Seals a doorway between vis areas; the portal opens and closes with the door." ),
		Entry( "tools/toolsblocklight", ToolTextureKind.BlockLight,
			ToolCompileFlags.BlockLight,
			"Blocks light without being visible; the brush stays solid." ),
		Entry( "tools/toolsblock_los", ToolTextureKind.BlockLos,
			ToolCompileFlags.BlockLos,
			"Blocks AI line of sight without being visible; the brush stays solid." ),
		Entry( "tools/toolsclip", ToolTextureKind.Clip,
			ToolCompileFlags.Clip | ToolCompileFlags.NonSolid,
			"Invisible clip: blocks players and NPCs, renders nothing." ),
		Entry( "tools/toolsfog", ToolTextureKind.Fog,
			ToolCompileFlags.NonSolid,
			"Fog volume boundary; not solid, not rendered." ),
		Entry( "tools/toolshint", ToolTextureKind.Hint,
			ToolCompileFlags.Hint | ToolCompileFlags.NonSolid,
			"Vis hint: cuts a vis leaf along this face." ),
		Entry( "tools/toolsinvisible", ToolTextureKind.Invisible,
			ToolCompileFlags.NonSolid,
			"Invisible and non-solid: renders nothing, blocks nothing." ),
		Entry( "tools/toolsinvisibleladder", ToolTextureKind.Ladder,
			ToolCompileFlags.NonSolid,
			"Invisible ladder volume: climbable, never solid, not shown in the editor either." ),
		Entry( "tools/toolsladder", ToolTextureKind.Ladder,
			ToolCompileFlags.NonSolid,
			"Climbable ladder volume; not solid, not rendered." ),
		Entry( "tools/toolsnodraw", ToolTextureKind.NoDraw,
			ToolCompileFlags.NoDraw,
			"Not rendered and not baked, but the brush stays solid. The unseen-face default." ),
		Entry( "tools/toolsnpcclip", ToolTextureKind.NpcClip,
			ToolCompileFlags.Clip | ToolCompileFlags.NonSolid,
			"Invisible clip for NPCs only; players pass through." ),
		Entry( "tools/toolsoccluder", ToolTextureKind.Occluder,
			ToolCompileFlags.Occluder | ToolCompileFlags.NonSolid,
			"Occluder: hides whatever is behind it from the renderer." ),
		Entry( "tools/toolsorigin", ToolTextureKind.Origin,
			ToolCompileFlags.Origin | ToolCompileFlags.NonSolid,
			"Marks the origin of a brush entity." ),
		Entry( "tools/toolsplayerclip", ToolTextureKind.PlayerClip,
			ToolCompileFlags.Clip | ToolCompileFlags.NonSolid,
			"Invisible clip for players only; NPCs pass through." ),
		Entry( "tools/toolsskip", ToolTextureKind.Skip,
			ToolCompileFlags.Skip | ToolCompileFlags.NonSolid,
			"Vis skip: the face is ignored by vis entirely." ),
		Entry( "tools/toolsskybox", ToolTextureKind.Skybox,
			ToolCompileFlags.Sky | ToolCompileFlags.NonSolid,
			"Skybox face: renders the sky and seals the map against leaks." ),
		Entry( "tools/toolstrigger", ToolTextureKind.Trigger,
			ToolCompileFlags.Trigger | ToolCompileFlags.NonSolid,
			"Trigger volume face: the brush fires triggers, it is not solid geometry." ),
	};

	static string Normalize( string materialName )
	{
		var n = materialName.Trim().Replace( '\\', '/' ).ToLowerInvariant();
		return n.StartsWith( "materials/", StringComparison.Ordinal )
			? n.Substring( "materials/".Length )
			: n;
	}

	/// <summary>
	/// The tool texture for a material name, or null when the material is ordinary.
	/// Matching is case-insensitive, tolerates backslashes, and tolerates a leading
	/// "materials/" prefix. A null or blank name is not a tool texture — it is null.
	/// </summary>
	public static ToolTexture Lookup( string materialName )
	{
		if ( string.IsNullOrWhiteSpace( materialName ) ) return null;
		var normalized = Normalize( materialName );
		return All.FirstOrDefault( t => t.Name == normalized );
	}

	/// <summary>True when the material is a known tool texture.</summary>
	public static bool IsTool( string materialName ) => Lookup( materialName ) is not null;

	/// <summary>True when the material is a NoDraw tool texture (tools/toolsnodraw).</summary>
	public static bool IsNoDraw( string materialName ) => Lookup( materialName )?.DropsFromBakedMesh == true;
}
