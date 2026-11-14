using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;

namespace Effigy;

/// <summary>
/// The .vmat for s&amp;box's skin shader — the only shipped s&amp;box shader with subsurface scattering
/// (Valve's pre-integrated skin: a curvature lookup plus a blurred diffuse normal) and per-texel
/// transmission. complex.vfx, which <see cref="FleshMaterial"/> and <see cref="BakeMaterial"/> write,
/// has neither; flesh on it reads as plastic however good the albedo is.
///
/// EVERY KEY AND NUMBER IS FACEPUNCH'S, COPIED FROM THEIR HUMAN VMATS, NOT REMEMBERED. Source:
/// github.com/Facepunch/sbox-public game/addons/citizen/Assets/models/citizen_human/ —
/// bodies/male/body_mode_dark.vmat (Body), heads/citizen_human_malehead01/citizen_human_malehead01.vmat
/// (Head) and …/mouth.vmat (Mouth). Same shader line ("shaders/skin.shader"), same sections, same
/// order. The human models only: Facepunch dropped the old Citizen.
///
/// `strings core/shaders/skin.shader_c` shows most of the names (F_USE_BENT_NORMALS, F_SSS_MASK,
/// TextureTransmissiveMask, g_flCurvatureScale…) but NOT g_vAmbientOcclusionColorBleed or
/// F_MORPH_SUPPORTED, which Facepunch's own files set — the compiled variants are compressed, so a
/// missing string proves nothing. The vmats are the authority. What still wants a look in the
/// editor (does each key show as set in the material panel?) is listed in
/// docs/progress/effigy-sbox-output.md.
///
/// See ~/.claude/skills/organic-modeling/references/techniques/realtime-flesh-shading.md for why
/// each value is what it is (curvature only bites on radii under ~13 in; transmission is for parts
/// under ~3 mm; the red AO bleed does much of the "flesh" work in creases).
/// </summary>
public static class SkinMaterial
{
	/// <summary>The shader-side numbers of one skin material. Start from a preset and change what differs.</summary>
	public sealed class Params
	{
		public string Name;

		/// <summary>Scales the curvature fed to the SSS lookup. Bodies 0.463, heads 0.279, mouth 0.569.</summary>
		public float CurvatureScale = 0.463f;

		/// <summary>How blurred the diffuse ("SSS") normal is against the detail normal.</summary>
		public float DiffuseNormalBlur = 2f;

		/// <summary>Second GGX lobe mix: 0.008 is dry skin (effectively off), 0.263 the wet mouth.</summary>
		public float SecondLobeRatio = 0.008f;

		public float SecondLobeGlossiness = 0.392f;

		/// <summary>How fast transmission fades: 0.759 head, 0.8 body, 1.858 mouth.</summary>
		public float TransmissionFalloff = 0.8f;

		/// <summary>The colour light comes through as. Human: #BF6302 orange-red.</summary>
		public Vec3 TransmissionColor = new( 0.749020f, 0.388235f, 0.007843f );

		/// <summary>AO tinted red, so creases go red rather than grey. Facepunch's on every skin.</summary>
		public Vec3 AoColorBleed = new( 0.400000f, 0.149020f, 0.129412f );

		/// <summary>Per-channel softening of the ambient normal (R fully soft).</summary>
		public Vec3 AmbientNormalSoftness = new( 1.000000f, 0.301961f, 0f );

		/// <summary>Multiplies the albedo; the mouth darkens to [0.741 0.722 0.682].</summary>
		public Vec3 ColorTint = new( 1f, 1f, 1f );

		/// <summary>One flat roughness when no roughness map is given (the mouth's 0.552). Null = the engine default map.</summary>
		public float? ConstantRoughness;

		/// <summary>Set when the model has morph targets (the human head and mouth do).</summary>
		public bool Morphs;
	}

	/// <summary>The human body (body_mode_dark): dry skin, no transmission of its own.</summary>
	public static Params Body() => new() { Name = "Body" };

	/// <summary>The human head (malehead01): tighter curvature, more blur, transmission at the ears.</summary>
	public static Params Head() => new()
	{
		Name = "Head", CurvatureScale = 0.279f, DiffuseNormalBlur = 1.834f, TransmissionFalloff = 0.759f, Morphs = true,
	};

	/// <summary>
	/// The human mouth interior: wet tissue in s&amp;box is a flat roughness of 0.552 plus a real second
	/// lobe (0.263 at gloss 0.3), not a near-mirror; no transmission.
	/// </summary>
	public static Params Mouth() => new()
	{
		Name = "Mouth", CurvatureScale = 0.569f, DiffuseNormalBlur = 0.435f,
		SecondLobeRatio = 0.263f, SecondLobeGlossiness = 0.3f,
		TransmissionFalloff = 1.858f, TransmissionColor = new Vec3( 0, 0, 0 ),
		ColorTint = new Vec3( 0.741176f, 0.721569f, 0.682353f ), ConstantRoughness = 0.552f, Morphs = true,
	};

	/// <summary>
	/// Wet creature flesh (sacs, exposed meat): the body's SSS with the mouth's wet second lobe.
	/// realtime-flesh-shading.md §5.1. Curvature is left at the body's 0.463; big masses may want
	/// 1–3, which is one of the in-engine checks still open.
	/// </summary>
	public static Params WetFlesh() => new()
	{
		Name = "Wet flesh", SecondLobeRatio = 0.263f, SecondLobeGlossiness = 0.3f,
	};

	public static readonly string[] Presets = { "Body", "Head", "Mouth", "Wet flesh" };

	public static Params Preset( string name ) => name?.Trim().ToLowerInvariant().Replace( '_', ' ' ) switch
	{
		"head" => Head(),
		"mouth" => Mouth(),
		"wet flesh" or "wet" or "flesh" => WetFlesh(),
		_ => Body(),
	};

	/// <summary>
	/// The .vmat source. Texture paths are asset-relative source images, the way every Effigy vmat
	/// names them; a null map gets the default Facepunch's files use for that slot (black for the
	/// transmission mask, i.e. none). A bent-normal map switches F_USE_BENT_NORMALS on; without one
	/// the flag stays off rather than pointing the shader at nothing.
	/// </summary>
	public static string VmatSource( Params p, string color = null, string normal = null, string roughness = null,
		string ambientOcclusion = null, string transmissiveMask = null, string bentNormal = null )
	{
		p ??= Body();
		var sb = new StringBuilder();

		sb.Append( "// THIS FILE IS AUTO-GENERATED by Effigy (SkinMaterial). Values from Facepunch's citizen_human vmats.\n" );
		sb.Append( "\n" );
		sb.Append( "Layer0\n" );
		sb.Append( "{\n" );
		sb.Append( "\tshader \"shaders/skin.shader\"\n" );

		if ( p.Morphs )
		{
			sb.Append( "\n\t//---- Animation ----\n" );
			sb.Append( "\tF_MORPH_SUPPORTED 1\n" );
		}

		if ( bentNormal is not null )
		{
			sb.Append( "\n\t//---- Skin ----\n" );
			sb.Append( "\tF_USE_BENT_NORMALS 1\n" );
		}

		sb.Append( "\n\t//---- Ambient Occlusion ----\n" );
		sb.Append( "\tg_flAmbientOcclusionDirectDiffuse \"1.000\"\n" );
		sb.Append( "\tg_flAmbientOcclusionDirectSpecular \"1.000\"\n" );
		sb.Append( $"\tg_vAmbientOcclusionColorBleed \"{Vector( p.AoColorBleed, 0f )}\"\n" );
		sb.Append( $"\tTextureAmbientOcclusion \"{ambientOcclusion ?? "materials/default/default_ao.tga"}\"\n" );

		sb.Append( "\n\t//---- Color ----\n" );
		sb.Append( "\tg_flModelTintAmount \"1.000\"\n" );
		sb.Append( $"\tg_vColorTint \"{Vector( p.ColorTint, 0f )}\"\n" );
		sb.Append( $"\tTextureColor \"{color ?? "materials/default/default_color.tga"}\"\n" );

		sb.Append( "\n\t//---- Curvature ----\n" );
		sb.Append( $"\tg_flCurvatureScale \"{F( p.CurvatureScale )}\"\n" );

		sb.Append( "\n\t//---- Fog ----\n" );
		sb.Append( "\tg_bFogEnabled \"1\"\n" );

		sb.Append( "\n\t//---- Lookup Textures ----\n" );
		sb.Append( $"\tg_vAmbientNormalSoftness \"{Vector( p.AmbientNormalSoftness, 0f )}\"\n" );
		sb.Append( "\tTextureDiffuseFalloff \"materials/default/default_skin_diffusewarp.tga\"\n" );

		sb.Append( "\n\t//---- Normal ----\n" );
		sb.Append( $"\tg_flDiffuseNormalBlur \"{F( p.DiffuseNormalBlur )}\"\n" );

		if ( bentNormal is not null )
			sb.Append( $"\tTextureBentNormal \"{bentNormal}\"\n" );

		sb.Append( $"\tTextureNormal \"{normal ?? "materials/default/default_normal.tga"}\"\n" );

		sb.Append( "\n\t//---- Roughness ----\n" );

		if ( roughness is not null )
			sb.Append( $"\tTextureRoughness \"{roughness}\"\n" );
		else if ( p.ConstantRoughness is float r )
			sb.Append( $"\tTextureRoughness \"{Vector( new Vec3( r, r, r ), 0f )}\"\n" );
		else
			sb.Append( "\tTextureRoughness \"materials/default/default_rough.tga\"\n" );

		sb.Append( "\n\t//---- Specular ----\n" );
		sb.Append( $"\tg_flSecondSpecularLobeGlossiness \"{F( p.SecondLobeGlossiness )}\"\n" );
		sb.Append( $"\tg_flSecondSpecularLobeRatio \"{F( p.SecondLobeRatio )}\"\n" );

		sb.Append( "\n\t//---- Texture Coordinates ----\n" );
		sb.Append( "\tg_vTexCoordOffset \"[0.000 0.000]\"\n" );
		sb.Append( "\tg_vTexCoordScale \"[1.000 1.000]\"\n" );

		sb.Append( "\n\t//---- Transmission ----\n" );
		sb.Append( $"\tg_flTransmissionFalloff \"{F( p.TransmissionFalloff )}\"\n" );
		sb.Append( $"\tg_vTransmissionColor \"{Vector( p.TransmissionColor, 0f )}\"\n" );
		sb.Append( $"\tTextureTransmissiveMask \"{transmissiveMask ?? "[0.000000 0.000000 0.000000 0.000000]"}\"\n" );
		sb.Append( "}\n" );

		return sb.ToString();
	}

	// --- transmission ------------------------------------------------------------------------------

	/// <summary>
	/// How much red light gets through <paramref name="mm"/> millimetres of skin: the red channel of
	/// d'Eon, Luebke &amp; Enderton's six-Gaussian skin profile with Jimenez's constants
	/// (SeparableSSS.h, SSSSTransmittance). 0.64 at 0.25 mm, 0.31 at 1 mm, 0.09 at 2 mm, 0.03 at 3 mm.
	/// </summary>
	public static float SkinTransmittanceRed( float mm )
	{
		var dd = -mm * mm;
		return 0.233f * MathF.Exp( dd / 0.0064f )
			+ 0.100f * MathF.Exp( dd / 0.0484f )
			+ 0.118f * MathF.Exp( dd / 0.187f )
			+ 0.113f * MathF.Exp( dd / 0.567f )
			+ 0.358f * MathF.Exp( dd / 1.99f )
			+ 0.078f * MathF.Exp( dd / 7.41f );
	}

	/// <summary>
	/// The transmission mask for TextureTransmissiveMask, from a thickness bake
	/// (<see cref="BakeMaps.Thickness"/>, read from <see cref="BakeSet.ThicknessUnits"/>, never from the
	/// 8-bit map): mask = skin's red transmittance at thickness × mm-per-unit ÷ <paramref name="translucency"/>.
	///
	/// <paramref name="translucency"/> is the k of realtime-flesh-shading.md §3.3: 1 is real skin,
	/// 3–10 gel-like creature flesh. Sanity check from the human head: the mask is non-zero on a few
	/// percent of texels, median about 0.1, max about 0.35. If half the creature glows, k is too big.
	///
	/// <paramref name="millimetresPerUnit"/> is 25.4 for s&amp;box (a unit is an inch). Unhit texels
	/// are black (no transmission), then padded like every other map.
	/// </summary>
	public static BakedMap TransmissionMask( BakeSet set, float translucency = 1f, float millimetresPerUnit = 25.4f, int padding = 4 )
	{
		if ( set?.Thickness is null || set.ThicknessUnits is null )
			throw new ArgumentException( "Needs a bake with BakeMaps.Thickness in it.", nameof( set ) );

		var k = MathF.Max( translucency, 1e-3f );
		var map = new BakedMap( set.Thickness.Width, set.Thickness.Height );

		for ( var i = 0; i < set.ThicknessUnits.Length; i++ )
		{
			var t = set.ThicknessUnits[i];
			var g = t < 0f ? 0f : Math.Clamp( SkinTransmittanceRed( t * millimetresPerUnit / k ), 0f, 1f );
			var b = (byte)Math.Clamp( (int)MathF.Round( g * 255f ), 0, 255 );
			map.Rgb[i * 3] = map.Rgb[i * 3 + 1] = map.Rgb[i * 3 + 2] = b;
			map.Filled[i] = t >= 0f;
		}

		NormalBake.Dilate( map, padding );
		return map;
	}

	static string F( float v ) => v.ToString( "0.000", CultureInfo.InvariantCulture );

	static string Vector( Vec3 v, float w ) => string.Format( CultureInfo.InvariantCulture,
		"[{0:0.000000} {1:0.000000} {2:0.000000} {3:0.000000}]", v.x, v.y, v.z, w );
}

/// <summary>
/// One scar / slash: a segment across the skin with a raised keloid ridge (Height +0.02 in, pale,
/// Rough 0.22) or a recessed trench, plus optional cross-stitch suture threads. Spec 4.4.
/// Parses the script form "x,y,z..x,y,z" (start..end); keloid=0 makes a trench.
/// </summary>
public sealed class SkinScar
{
	public Vec3 Start;
	public Vec3 End;
	public float Width = 0.06f;
	public bool Keloid = true;
	public bool Stitches = true;

	/// <summary>Raised pale keloid ridge (+0.02 in) or recessed wound trench (-0.01 in), spec 4.4.</summary>
	public const float KeloidHeight = 0.02f;
	public const float TrenchDepth = -0.01f;

	public float Height => Keloid ? KeloidHeight : TrenchDepth;

	/// <summary>Height at a surface point: a Gaussian ridge/trench across the scar segment.</summary>
	public float HeightAt( Vec3 p )
	{
		var d = DistanceToSegment( p, Start, End );
		var w = MathF.Max( Width, 1e-4f );
		return Height * MathF.Exp( -(d * d) / (w * w) );
	}

	/// <summary>How pale the scar reads at a surface point, 0..1 (a keloid is pale; a trench is not).</summary>
	public float PaleAt( Vec3 p )
	{
		if ( !Keloid ) return 0f;
		var d = DistanceToSegment( p, Start, End );
		var w = MathF.Max( Width, 1e-4f );
		return 0.6f * MathF.Exp( -(d * d) / (w * w) );
	}

	/// <summary>Cross-stitch suture threads: short segments crossing the scar every two widths.</summary>
	public List<(Vec3 A, Vec3 B)> StitchPoints()
	{
		var threads = new List<(Vec3, Vec3)>();
		if ( !Stitches ) return threads;
		var dir = End - Start;
		var len = dir.Length;
		if ( len < 1e-6f ) return threads;
		dir /= len;
		var perp = Vec3.Cross( dir, new Vec3( 0, 0, 1 ) );
		if ( perp.LengthSquared < 1e-6f ) perp = Vec3.Cross( dir, new Vec3( 1, 0, 0 ) );
		perp = perp.Normal * Width * 1.5f;
		var step = MathF.Max( Width * 2f, 0.05f );
		for ( var s = step; s < len; s += step )
		{
			var c = Start + dir * s;
			threads.Add( (c - perp, c + perp) );
		}
		return threads;
	}

	public static SkinScar Parse( string s )
	{
		if ( string.IsNullOrWhiteSpace( s ) ) return null;
		var parts = s.Split( "..", StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 2 ) return null;
		if ( !TryVec( parts[0], out var a ) || !TryVec( parts[1], out var b ) ) return null;
		return new SkinScar { Start = a, End = b };
	}

	static bool TryVec( string s, out Vec3 v )
	{
		v = Vec3.Zero;
		var p = s.Split( ',', StringSplitOptions.RemoveEmptyEntries );
		if ( p.Length != 3 ) return false;
		if ( !float.TryParse( p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x ) ) return false;
		if ( !float.TryParse( p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y ) ) return false;
		if ( !float.TryParse( p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z ) ) return false;
		v = new Vec3( x, y, z );
		return true;
	}

	internal static float DistanceToSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var len2 = ab.LengthSquared;
		var t = len2 < 1e-12f ? 0f : Math.Clamp( Vec3.Dot( p - a, ab ) / len2, 0f, 1f );
		return (p - (a + ab * t)).Length;
	}
}

/// <summary>
/// One sub-dermal tattoo: ink under the skin, so it desaturates and shifts toward dermal blue-teal
/// (#1d3442) with blurred edges (spec 4.4: 0.8 px blur), and it only ever touches albedo/roughness —
/// the pore normals stay on top, because a tattoo has no normal output anywhere in SkinGen.
/// </summary>
public sealed class SkinTattoo
{
	public Vec3 Centre;
	public float Radius = 1f;
	public Vec3 Ink = new( 0.1f, 0.15f, 0.2f );

	/// <summary>Edge softness as a fraction of the radius (dermis scattering above the ink).</summary>
	public float Blur = 0.15f;

	/// <summary>Dermal blue-teal shift, spec 4.4.</summary>
	public static readonly Vec3 DermalTeal = new( 0.114f, 0.204f, 0.259f );

	/// <summary>Ink mix at a surface point, 0 outside to 1 inside, softened over Blur at the edge.</summary>
	public float MixAt( Vec3 p )
	{
		var d = (p - Centre).Length;
		var edge = Math.Clamp( Blur, 0.02f, 0.9f ) * Radius;
		var inner = MathF.Max( Radius - edge, 0f );
		if ( d <= inner ) return 1f;
		if ( d >= Radius ) return 0f;
		var t = (d - inner) / MathF.Max( Radius - inner, 1e-6f );
		return 1f - t * t * (3f - 2f * t);
	}

	/// <summary>The ink colour as the skin shows it: desaturated toward dermal blue-teal.</summary>
	public Vec3 ShownInk => Vec3.Lerp( Ink, DermalTeal, 0.65f );
}

/// <summary>One freckle / sun spot: a melanin dot on the surface, from Poisson-clustered placement.</summary>
public sealed class SkinSpot
{
	public Vec3 Centre;
	public float Radius;
	public float Strength = 1f;
}

/// <summary>Per-vertex chromophore zone weights, 0..1. Gold + Crimson + Blue sum to at most 1; Lip is separate.</summary>
public sealed class SkinGenZones
{
	public float[] Gold;
	public float[] Crimson;
	public float[] Blue;
	public float[] Lip;
}

/// <summary>
/// Everything SkinGen evaluates for one mesh, per vertex: tinted colour, micro-skin roughness and
/// second-lobe ratio, freckle darkness, scar height/paleness, tattoo ink mix and colour, and the
/// vellus sheen strength. No normal output exists here on purpose: overlays never touch the pore
/// normals (spec 4.4: a tattoo sits under the pore normals).
/// </summary>
public sealed class SkinGenResult
{
	public SkinGenZones Zones;
	public Vec3[] Colour;
	public float[] Roughness;
	public float[] SecondLobe;
	/// <summary>T-zone weight (forehead + nose bridge), 0..1: tight pores + oil sheen live here.</summary>
	public float[] TZone;
	public float[] Freckle;
	public float[] ScarHeight;
	public float[] ScarPale;
	public float[] Tattoo;
	public Vec3 TattooColour;
	public float[] Sheen;
	public float SheenRoughness = SkinGenStack.VellusSheenRoughness;
	public float SheenTint = SkinGenStack.VellusSheenTint;
	public List<SkinSpot> FreckleSpots = new();
	public List<(Vec3 A, Vec3 B)> Stitches = new();
}

/// <summary>
/// SkinGen (spec 4.4): the CC4 SkinGen layer for Effigy skin. A facial 3-zone chromophore map
/// (forehead gold, cheek/nose crimson, jaw/orbit cool blue) from height + landmark masks, anatomical
/// micro-skin normal/roughness zones (T-zone tight pores + oil sheen, cheek pores along Langer lines,
/// lip vermilion grooves), and the `add SkinDetail` overlays — freckles/sun spots, scars/slashes/
/// stitches, sub-dermal tattoos, vellus peach fuzz.
///
/// This is evaluation only: it produces per-vertex values and FleshStack layers (see SkinGenBake in
/// FleshMaterial.cs). The `add SkinDetail` feature itself is a thin Feature over Evaluate, owned by
/// the coordinator — it is a new Feature file, not an edit to a shared entry point.
/// </summary>
public sealed class SkinGenStack
{
	/// <summary>Zone tints, spec 4.4: forehead gold #e8c888, cheek/nose crimson #d64545, jaw/orbit blue #5a687a.</summary>
	public static readonly Vec3 GoldTint = new( 0.910f, 0.784f, 0.533f );
	public static readonly Vec3 CrimsonTint = new( 0.839f, 0.271f, 0.271f );
	public static readonly Vec3 BlueTint = new( 0.353f, 0.408f, 0.478f );

	/// <summary>Zone strengths, spec 4.4: gold +4%, crimson +8%, blue +5%.</summary>
	public const float GoldStrength = 0.04f;
	public const float CrimsonStrength = 0.08f;
	public const float BlueStrength = 0.05f;

	/// <summary>Micro-skin roughness, spec 4.4: T-zone 0.28, cheek 0.42, lip vermilion 0.24.</summary>
	public const float TZoneRoughness = 0.28f;
	public const float CheekRoughness = 0.42f;
	public const float LipRoughness = 0.24f;

	/// <summary>T-zone sebaceous oil sheen, spec 4.4.</summary>
	public const float TZoneSecondLobe = 0.04f;
	public const float DrySecondLobe = 0.008f;

	/// <summary>Vellus peach fuzz sheen, spec 4.4.</summary>
	public const float VellusSheen = 0.22f;
	public const float VellusSheenRoughness = 0.65f;
	public const float VellusSheenTint = 0.15f;

	/// <summary>Stubble stipple strength is 0..1; the stipple itself is a pore-scale darkening.</summary>
	public bool Zones = true;
	public float Freckles;
	public float Stubble;
	public float Vellus;
	public int Seed = 1;
	public List<SkinScar> Scars = new();
	public List<SkinTattoo> Tattoos = new();

	public static SkinGenStack Default() => new() { Zones = true };

	/// <summary>
	/// The three chromophore zones plus the lip mask, per vertex, from head height and landmark
	/// distance. FaceRig frame (Y up, +Z forward): eyes sit at ~72% of head height, nose ~55%,
	/// mouth ~30%, jaw ~15% (FaceBounds.FromHeadBounds). An optional per-vertex curvature array
	/// (FleshSurface convexity, + on ridges) mildly deepens gold on convex forehead and blue in
	/// creases; null skips the term entirely.
	/// </summary>
	public SkinGenZones ZoneWeights( PolyMesh mesh, FaceBounds landmarks, float[] curvature = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var n = mesh.VertexCount;
		var zones = new SkinGenZones { Gold = new float[n], Crimson = new float[n], Blue = new float[n], Lip = new float[n] };
		if ( n == 0 ) return zones;
		var (min, max) = ModelMeasure.Bounds( mesh );
		var headH = MathF.Max( max.y - min.y, 1e-6f );
		var headW = MathF.Max( max.x - min.x, 1e-6f );
		var midEye = (landmarks.LeftEye + landmarks.RightEye) * 0.5f;
		var noseBridgeTop = midEye;
		var cheekL = new Vec3( landmarks.NoseTip.x - 0.14f * headW, landmarks.NoseTip.y + 0.06f * headH, landmarks.NoseTip.z );
		var cheekR = new Vec3( landmarks.NoseTip.x + 0.14f * headW, landmarks.NoseTip.y + 0.06f * headH, landmarks.NoseTip.z );
		for ( var i = 0; i < n; i++ )
		{
			var p = mesh.Positions[i];
			var yN = (p.y - min.y) / headH;
			var gold = Smooth( 0.60f, 0.72f, yN );
			gold = MathF.Max( gold, 1f - Smooth( 0f, 0.10f * headH, SkinScar.DistanceToSegment( p, landmarks.NoseTip, noseBridgeTop ) ) );
			var crimson = 1f - Smooth( 0f, 0.14f * headW, MathF.Min( (p - landmarks.NoseTip).Length,
				MathF.Min( (p - cheekL).Length, (p - cheekR).Length ) ) );
			var blue = Smooth( 0.24f, 0.14f, yN );
			blue = MathF.Max( blue, (1f - Smooth( 0f, 0.05f * headH, SkinScar.DistanceToSegment( p,
				new Vec3( landmarks.LeftEye.x, landmarks.LeftEye.y - 0.04f * headH, landmarks.LeftEye.z ),
				new Vec3( landmarks.RightEye.x, landmarks.RightEye.y - 0.04f * headH, landmarks.RightEye.z ) ) )) * 0.7f );
			if ( curvature is not null && i < curvature.Length )
			{
				var c = Math.Clamp( curvature[i] * headH * 0.5f, -1f, 1f );
				gold = Math.Clamp( gold * (1f + 0.15f * c), 0f, 1f );
				blue = Math.Clamp( blue * (1f - 0.15f * c), 0f, 1f );
			}
			var sum = gold + crimson + blue;
			if ( sum > 1f ) { gold /= sum; crimson /= sum; blue /= sum; }
			zones.Gold[i] = gold;
			zones.Crimson[i] = crimson;
			zones.Blue[i] = blue;
			var lipD = SkinScar.DistanceToSegment( p, landmarks.MouthLeft, landmarks.MouthRight );
			zones.Lip[i] = 1f - Smooth( 0f, 0.035f * headH, lipD );
		}
		return zones;
	}

	/// <summary>
	/// Full per-vertex SkinGen evaluation: zone tints over the base colour, micro-skin roughness and
	/// second lobe, freckles, scars, tattoos and vellus sheen. A flat base colour in gives zoned skin
	/// out; zones=false skips the chromophore tints but still applies overlays.
	/// </summary>
	public SkinGenResult Evaluate( PolyMesh mesh, FaceBounds landmarks, Vec3 baseColour, float[] curvature = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var n = mesh.VertexCount;
		var zones = Zones ? ZoneWeights( mesh, landmarks, curvature ) : new SkinGenZones
			{ Gold = new float[n], Crimson = new float[n], Blue = new float[n], Lip = new float[n] };
		var spots = Freckles > 0f ? FreckleSpots( mesh, landmarks, Freckles, Seed ) : new List<SkinSpot>();
		var result = new SkinGenResult
		{
			Zones = zones,
			Colour = new Vec3[n],
			Roughness = new float[n],
			SecondLobe = new float[n],
			TZone = new float[n],
			Freckle = new float[n],
			ScarHeight = new float[n],
			ScarPale = new float[n],
			Tattoo = new float[n],
			Sheen = new float[n],
			FreckleSpots = spots,
		};
		var (min, max) = ModelMeasure.Bounds( mesh );
		var headH = MathF.Max( max.y - min.y, 1e-6f );
		foreach ( var scar in Scars ) result.Stitches.AddRange( scar.StitchPoints() );
		for ( var i = 0; i < n; i++ )
		{
			var p = mesh.Positions[i];
			var g = zones.Gold[i];
			var c = zones.Crimson[i];
			var b = zones.Blue[i];
			var lip = zones.Lip[i];
			var tint = new Vec3(
				1f + GoldStrength * g * (GoldTint.x - 1f) + CrimsonStrength * c * (CrimsonTint.x - 1f) + BlueStrength * b * (BlueTint.x - 1f),
				1f + GoldStrength * g * (GoldTint.y - 1f) + CrimsonStrength * c * (CrimsonTint.y - 1f) + BlueStrength * b * (BlueTint.y - 1f),
				1f + GoldStrength * g * (GoldTint.z - 1f) + CrimsonStrength * c * (CrimsonTint.z - 1f) + BlueStrength * b * (BlueTint.z - 1f) );
			var col = new Vec3( baseColour.x * tint.x, baseColour.y * tint.y, baseColour.z * tint.z );
			var tzone = MathF.Max( g * Smooth( 0.60f, 0.72f, (p.y - min.y) / headH ),
				1f - Smooth( 0f, 0.10f * headH, SkinScar.DistanceToSegment( p, landmarks.NoseTip, (landmarks.LeftEye + landmarks.RightEye) * 0.5f ) ) );
			var rough = CheekRoughness + (TZoneRoughness - CheekRoughness) * tzone + (LipRoughness - CheekRoughness) * lip;
			var lobe = DrySecondLobe + (TZoneSecondLobe - DrySecondLobe) * tzone;
			var freckle = 0f;
			foreach ( var spot in spots )
			{
				var d = (p - spot.Centre).Length;
				if ( d > spot.Radius * 3f ) continue;
				freckle += spot.Strength * MathF.Exp( -(d * d) / (spot.Radius * spot.Radius) );
			}
			freckle = Math.Clamp( freckle, 0f, 1f );
			col = new Vec3( col.x * (1f - 0.35f * freckle), col.y * (1f - 0.30f * freckle), col.z * (1f - 0.25f * freckle) );
			var scarH = 0f;
			var pale = 0f;
			foreach ( var scar in Scars )
			{
				scarH += scar.HeightAt( p );
				pale = MathF.Max( pale, scar.PaleAt( p ) );
			}
			col = Vec3.Lerp( col, new Vec3( 0.92f, 0.82f, 0.78f ), pale );
			if ( scarH != 0f ) rough = Math.Clamp( rough + (0.22f - rough) * Math.Clamp( MathF.Abs( scarH ) / SkinScar.KeloidHeight, 0f, 1f ), 0f, 1f );
			var tattoo = 0f;
			var ink = Vec3.Zero;
			foreach ( var tat in Tattoos )
			{
				var m = tat.MixAt( p );
				if ( m <= 0f ) continue;
				tattoo = MathF.Max( tattoo, m );
				ink = tat.ShownInk;
			}
			if ( tattoo > 0f ) col = Vec3.Lerp( col, ink, tattoo * 0.8f );
			var sheen = Vellus * VellusSheen * (c + 0.5f * g);
			var stubbleDark = Stubble * b * 0.12f;
			col = new Vec3( col.x * (1f - stubbleDark), col.y * (1f - stubbleDark), col.z * (1f - stubbleDark) );
			result.Colour[i] = col;
			result.TZone[i] = Math.Clamp( tzone, 0f, 1f );
			result.Roughness[i] = Math.Clamp( rough, 0f, 1f );
			result.SecondLobe[i] = lobe;
			result.Freckle[i] = freckle;
			result.ScarHeight[i] = scarH;
			result.ScarPale[i] = pale;
			result.Tattoo[i] = tattoo;
			result.Sheen[i] = Math.Clamp( sheen, 0f, 1f );
		}
		result.TattooColour = Tattoos.Count > 0 ? Tattoos[0].ShownInk : Vec3.Zero;
		return result;
	}

	/// <summary>
	/// Poisson-clustered freckle / sun-spot placement: cluster centres on the nose bridge, the malar
	/// cheeks and (when the mesh runs below the jaw) the shoulders, with spots scattered around each
	/// centre. Deterministic for a seed. Density 0..1 scales the spot count; the count is capped so a
	/// dense body does not drown the bake.
	/// </summary>
	public List<SkinSpot> FreckleSpots( PolyMesh mesh, FaceBounds landmarks, float density, int seed )
	{
		var spots = new List<SkinSpot>();
		if ( mesh is null || density <= 0f ) return spots;
		var (min, max) = ModelMeasure.Bounds( mesh );
		var headH = MathF.Max( max.y - min.y, 1e-6f );
		var headW = MathF.Max( max.x - min.x, 1e-6f );
		var midEye = (landmarks.LeftEye + landmarks.RightEye) * 0.5f;
		var anchors = new List<Vec3>();
		for ( var k = 0; k < 3; k++ ) anchors.Add( Vec3.Lerp( landmarks.NoseTip, midEye, k / 2f ) );
		anchors.Add( new Vec3( landmarks.NoseTip.x - 0.14f * headW, landmarks.NoseTip.y + 0.05f * headH, landmarks.NoseTip.z ) );
		anchors.Add( new Vec3( landmarks.NoseTip.x + 0.14f * headW, landmarks.NoseTip.y + 0.05f * headH, landmarks.NoseTip.z ) );
		if ( min.y < landmarks.Jaw.y - 0.05f * headH )
		{
			anchors.Add( new Vec3( landmarks.Jaw.x - 0.30f * headW, landmarks.Jaw.y - 0.08f * headH, landmarks.Jaw.z ) );
			anchors.Add( new Vec3( landmarks.Jaw.x + 0.30f * headW, landmarks.Jaw.y - 0.08f * headH, landmarks.Jaw.z ) );
		}
		var rng = new Random( seed );
		var total = Math.Min( (int)(density * 220f), 600 );
		var perAnchor = Math.Max( 1, total / Math.Max( anchors.Count, 1 ) );
		foreach ( var anchor in anchors )
		{
			var count = perAnchor / 2 + rng.Next( perAnchor );
			for ( var s = 0; s < count && spots.Count < 600; s++ )
			{
				var jx = (float)(rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 0.10f * headW;
				var jy = (float)(rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 0.10f * headH;
				spots.Add( new SkinSpot
				{
					Centre = new Vec3( anchor.x + jx, anchor.y + jy, anchor.z ),
					Radius = (0.010f + (float)rng.NextDouble() * 0.020f) * headH,
					Strength = 0.5f + (float)rng.NextDouble() * 0.5f,
				} );
			}
		}
		return spots;
	}

	/// <summary>
	/// Displace the mesh along its vertex normals by the scar heights (keloid up, trench down), so a
	/// scar reads as a raised ridge in the geometry, not just in the albedo. Mutates the mesh once —
	/// call before baking, the way a sculpt op would. Returns how many vertices moved.
	/// </summary>
	public int DisplaceScars( PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( Scars.Count == 0 ) return 0;
		var normals = SurfaceDetail.VertexNormals( mesh );
		var moved = 0;
		for ( var i = 0; i < mesh.VertexCount; i++ )
		{
			var h = 0f;
			foreach ( var scar in Scars ) h += scar.HeightAt( mesh.Positions[i] );
			if ( MathF.Abs( h ) < 1e-6f ) continue;
			mesh.Positions[i] = mesh.Positions[i] + normals[i] * h;
			moved++;
		}
		if ( moved > 0 ) mesh.InvalidateVertexCache();
		return moved;
	}

	/// <summary>Write one vertex's micro-skin numbers into a skin.shader Params (roughness + oil sheen).</summary>
	public static void ApplyMicroSkin( SkinMaterial.Params p, float roughness, float secondLobe )
	{
		if ( p is null ) return;
		p.ConstantRoughness = Math.Clamp( roughness, 0f, 1f );
		p.SecondLobeRatio = Math.Clamp( secondLobe, 0f, 1f );
	}

	static float Smooth( float a, float b, float x )
	{
		if ( a < b ) { var t = Math.Clamp( (x - a) / MathF.Max( b - a, 1e-6f ), 0f, 1f ); return t * t * (3f - 2f * t); }
		var t2 = Math.Clamp( (x - b) / MathF.Max( a - b, 1e-6f ), 0f, 1f ); return 1f - t2 * t2 * (3f - 2f * t2);
	}
}
