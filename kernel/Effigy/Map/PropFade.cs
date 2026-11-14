// PropFade — prop fade distances + per-face lightmap-scale luxel density.
//
// Hammer Desktop P2: hammer-optimisation-prop-fade-lightmap-scale (worker
// muse-3-desktop-hammer-53). Backend / data-model / API only — no Desktop UI,
// no Hammer app, no shelling out to Hammer/Hammer++/VBSP/VVIS/VRAD.
//
// SOURCE (Report §1.2 #39 + §6 P2-3): prop fade (fademindist/fademaxdist)
// hides props at range; per-face lightmap scale trades bake quality for time.
// NATIVE Geppetto: metadata on props/faces consumed by the native bake.
//
// What this file owns (deps are DONE and their files are on disk — build
// against the real APIs, nothing here redefines them):
//   * MapEntities / MapEntity (hammer-entity-place-edit-validate):
//     prop_static already declares fademindist (float, default -1 = never
//     fade) and fademaxdist (float, default 0 = no max) in
//     kernel/Effigy/Map/registry/core-source1.json, so MapEntities.SetKeyvalue
//     accepts them and the registry type-checks them. PropFade.ApplyTo writes
//     through that public API; it never touches MapEntity internals.
//   * FaceUv / FaceMaterial (hammer-face-material-uv-model): the per-face
//     lightmap scale already lives on FaceUv.LightmapScale (default 16 world
//     units per luxel) and round-trips through FaceMaterial JSON. This file
//     does NOT add a second scale — it adds the bake-side reading of it:
//     LightmapLuxels turns a recorded scale into the luxel grid the native
//     bake allocates for that face.
//   * MapBakeExport (hammer-obj-glb-map-export): Bake already records each
//     baked face's scale on MapBakeFaceInfo.LightmapScale. LightmapLuxels.
//     ForBakedFace sizes the lightmap from that recorded value, so the bake
//     honours the scale instead of only carrying it.
//
// VALIDATION RULE (ticket): fade distances are non-negative and max >= min.
// The registry defaults are the UNSET state, not values: a prop with no fade
// keys, or fademindist = -1 ("never fade"), reads as PropFade.Disabled.
// A stored fademaxdist of 0 ("no max") next to a min > 0 fails the rule
// (0 < min) and is reported by Validate() as a defect — the mapper sets an
// explicit max >= min. Invalid fades never hide a prop: VisibilityAt fails
// visible (1.0) for them. A registry JSON that declares fademindist /
// fademaxdist on another class makes that class fadeable here with no code
// change, mirroring MapEntities.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Effigy;

namespace Effigy.Map;

/// <summary>Named error from the prop-fade layer. <see cref="Code"/> is the stable machine-readable name.</summary>
public sealed class PropFadeException : Exception
{
	public const string CodeNegativeFadeMin = "NegativeFadeMin";
	public const string CodeNegativeFadeMax = "NegativeFadeMax";
	public const string CodeFadeMaxBelowMin = "FadeMaxBelowMin";
	public const string CodeInvalidFadeValue = "InvalidFadeValue";
	public const string CodeFadeNotSupported = "FadeNotSupported";

	public string Code = "";

	public PropFadeException( string code, string message ) : base( message ) { Code = code; }
}

/// <summary>
/// A prop's fade distances (Source fademindist / fademaxdist, world units):
/// fully visible at or inside <see cref="FadeMinDist"/>, fully faded out at or
/// beyond <see cref="FadeMaxDist"/>, linear in between (see
/// <see cref="VisibilityAt"/>). Immutable — <see cref="ApplyTo"/> writes a
/// copy of these values onto an entity, it never mutates this value.
/// <see cref="Disabled"/> is the unset state: no fade keys, or the registry
/// default fademindist = -1 ("never fade").
/// </summary>
public readonly struct PropFade : IEquatable<PropFade>
{
	/// <summary>Registry keyvalue for the distance fading starts at.</summary>
	public const string FadeMinKey = "fademindist";
	/// <summary>Registry keyvalue for the distance the prop is fully faded out at.</summary>
	public const string FadeMaxKey = "fademaxdist";
	/// <summary>Registry default for fademindist: -1 means the prop never fades.</summary>
	public const float DisabledMinSentinel = -1f;
	/// <summary>Registry default for fademaxdist: 0 means no max.</summary>
	public const float DisabledMaxDefault = 0f;

	public readonly float FadeMinDist;
	public readonly float FadeMaxDist;
	public readonly bool IsDisabled;

	/// <summary>No fade: the prop is fully visible at any distance.</summary>
	public static readonly PropFade Disabled = new( 0f, 0f, true );

	/// <summary>
	/// A fade from <paramref name="fadeMinDist"/> to <paramref name="fadeMaxDist"/>.
	/// Throws <see cref="PropFadeException"/> naming the problem for a negative min
	/// (<see cref="PropFadeException.CodeNegativeFadeMin"/>), a negative max
	/// (<see cref="PropFadeException.CodeNegativeFadeMax"/>), or max below min
	/// (<see cref="PropFadeException.CodeFadeMaxBelowMin"/>). Equal min and max is
	/// allowed: the prop pops out at exactly that distance (a step).
	/// </summary>
	public PropFade( float fadeMinDist, float fadeMaxDist )
	{
		if ( fadeMinDist < 0f )
			throw new PropFadeException( PropFadeException.CodeNegativeFadeMin,
				$"fademindist is {fadeMinDist}: fade distances must be non-negative." );
		if ( fadeMaxDist < 0f )
			throw new PropFadeException( PropFadeException.CodeNegativeFadeMax,
				$"fademaxdist is {fadeMaxDist}: fade distances must be non-negative." );
		if ( fadeMaxDist < fadeMinDist )
			throw new PropFadeException( PropFadeException.CodeFadeMaxBelowMin,
				$"fademaxdist ({fadeMaxDist}) is below fademindist ({fadeMinDist}): max must be >= min." );
		FadeMinDist = fadeMinDist;
		FadeMaxDist = fadeMaxDist;
		IsDisabled = false;
	}

	PropFade( float fadeMinDist, float fadeMaxDist, bool disabled )
	{
		FadeMinDist = fadeMinDist;
		FadeMaxDist = fadeMaxDist;
		IsDisabled = disabled;
	}

	/// <summary>
	/// Named defects, never a silent fix: a negative min, a negative max, or a max
	/// below min (which is also what a stored fademaxdist of 0, "no max", looks
	/// like next to a min &gt; 0 — set an explicit max &gt;= min). Disabled has no
	/// defects. Empty means the fade is usable.
	/// </summary>
	public IReadOnlyList<string> Validate()
	{
		var defects = new List<string>();
		if ( IsDisabled ) return defects;
		if ( FadeMinDist < 0f ) defects.Add( $"fademindist is {FadeMinDist}: fade distances must be non-negative." );
		if ( FadeMaxDist < 0f ) defects.Add( $"fademaxdist is {FadeMaxDist}: fade distances must be non-negative." );
		if ( FadeMaxDist < FadeMinDist )
			defects.Add( $"fademaxdist ({FadeMaxDist}) is below fademindist ({FadeMinDist}): max must be >= min (a stored 0 means 'no max' — set an explicit max)." );
		return defects;
	}

	public bool IsValid => Validate().Count == 0;

	/// <summary>
	/// Visibility at <paramref name="distance"/> (world units from the viewer):
	/// 1.0 at or inside the min distance, 0.0 at or beyond the max, linear in
	/// between; a step (min == max) is 1.0 below and 0.0 at/above. Disabled and
	/// invalid fades are always 1.0 — a broken fade value must never hide a
	/// prop. NaN distance is 1.0 for the same reason; +infinity is 0.0 when the
	/// fade is valid (it is beyond any finite max).
	/// </summary>
	public float VisibilityAt( float distance )
	{
		if ( IsDisabled || !IsValid ) return 1f;
		if ( float.IsNaN( distance ) ) return 1f;
		if ( distance >= FadeMaxDist ) return 0f;
		if ( distance <= FadeMinDist ) return 1f;
		return 1f - ( distance - FadeMinDist ) / ( FadeMaxDist - FadeMinDist );
	}

	/// <summary>
	/// True when the registry declares both fade keyvalues for
	/// <paramref name="className"/> — in core-source1.json that is prop_static
	/// only. A registry JSON that adds the keys to another class makes it
	/// fadeable with no code change.
	/// </summary>
	public static bool SupportsFade( EntityRegistry registry, string className )
	{
		if ( registry is null ) throw new ArgumentNullException( nameof( registry ) );
		if ( string.IsNullOrWhiteSpace( className ) ) return false;
		var cls = registry.Find( className );
		return cls is not null && cls.FindKeyValue( FadeMinKey ) is not null && cls.FindKeyValue( FadeMaxKey ) is not null;
	}

	/// <summary>True when <paramref name="entity"/>'s class declares both fade keyvalues in its registry.</summary>
	public static bool SupportsFade( MapEntities entities, MapEntity entity )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		return SupportsFade( entities.Registry, entity.ClassName );
	}

	/// <summary>
	/// Write this fade onto <paramref name="entity"/> through
	/// <see cref="MapEntities.SetKeyvalue"/> (registry type-checked: the keys are
	/// floats). Disabled writes the registry defaults (fademindist -1,
	/// fademaxdist 0), so the disabled state is explicit in the export. Throws
	/// <see cref="PropFadeException"/> for an invalid fade (first defect's code)
	/// or a class without fade keyvalues
	/// (<see cref="PropFadeException.CodeFadeNotSupported"/>); a registry
	/// rejection propagates untouched as <see cref="EntityRegistryException"/>.
	/// </summary>
	public void ApplyTo( MapEntities entities, MapEntity entity )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( !SupportsFade( entities, entity ) )
			throw new PropFadeException( PropFadeException.CodeFadeNotSupported,
				$"Cannot set fade distances on entity #{entity.Id} ('{entity.ClassName}'): its class declares no '{FadeMinKey}' / '{FadeMaxKey}' keyvalues." );
		if ( IsDisabled )
		{
			entities.SetKeyvalue( entity, FadeMinKey, Format( DisabledMinSentinel ) );
			entities.SetKeyvalue( entity, FadeMaxKey, Format( DisabledMaxDefault ) );
			return;
		}
		var defects = Validate();
		if ( defects.Count > 0 )
		{
			var code = FadeMinDist < 0f ? PropFadeException.CodeNegativeFadeMin
				: FadeMaxDist < 0f ? PropFadeException.CodeNegativeFadeMax
				: PropFadeException.CodeFadeMaxBelowMin;
			throw new PropFadeException( code, defects[0] );
		}
		entities.SetKeyvalue( entity, FadeMinKey, Format( FadeMinDist ) );
		entities.SetKeyvalue( entity, FadeMaxKey, Format( FadeMaxDist ) );
	}

	/// <summary>
	/// Read the fade off <paramref name="entity"/> from its stored keyvalues
	/// (no registry lookup: reading never throws for a class without fade keys
	/// — it returns <see cref="Disabled"/>). No fade keys, or fademindist -1
	/// (the registry "never fade" default), reads as Disabled. A stored min
	/// with no max reads with the registry default max 0, which
	/// <see cref="Validate"/> then flags (0 &lt; min). A non-numeric stored
	/// value throws <see cref="PropFadeException"/> with code
	/// <see cref="PropFadeException.CodeInvalidFadeValue"/>.
	/// </summary>
	public static PropFade FromEntity( MapEntity entity )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		return FromKeyvalues( entity.Keyvalues );
	}

	/// <summary>Same as <see cref="FromEntity(MapEntity)"/> over a raw keyvalue dictionary (case-insensitive keys).</summary>
	public static PropFade FromKeyvalues( IReadOnlyDictionary<string, string> keyvalues )
	{
		if ( keyvalues is null ) throw new ArgumentNullException( nameof( keyvalues ) );
		if ( !keyvalues.TryGetValue( FadeMinKey, out string minText ) ) return Disabled;
		var min = Parse( FadeMinKey, minText );
		if ( min == DisabledMinSentinel ) return Disabled;
		var max = keyvalues.TryGetValue( FadeMaxKey, out string maxText )
			? Parse( FadeMaxKey, maxText )
			: DisabledMaxDefault;
		return new PropFade( min, max, false );
	}

	static float Parse( string key, string text )
	{
		if ( !float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value ) || !float.IsFinite( value ) )
			throw new PropFadeException( PropFadeException.CodeInvalidFadeValue,
				$"'{key}' value '{text}' is not a finite number." );
		return value;
	}

	/// <summary>
	/// The keyvalues an exporter writes verbatim (VMF entity block, map-document
	/// JSON): fademindist + fademaxdist, invariant-culture. Disabled writes the
	/// registry defaults (-1 / 0), so the export records the fade state
	/// explicitly instead of dropping it.
	/// </summary>
	public Dictionary<string, string> ToKeyvalues() => IsDisabled
		? new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			[FadeMinKey] = Format( DisabledMinSentinel ),
			[FadeMaxKey] = Format( DisabledMaxDefault ),
		}
		: new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase )
		{
			[FadeMinKey] = Format( FadeMinDist ),
			[FadeMaxKey] = Format( FadeMaxDist ),
		};

	/// <summary>JSON form: {"fadeMinDist":n,"fadeMaxDist":n,"disabled":bool}.</summary>
	public JsonObject ToJson() => new()
	{
		["fadeMinDist"] = IsDisabled ? (double)DisabledMinSentinel : (double)FadeMinDist,
		["fadeMaxDist"] = IsDisabled ? (double)DisabledMaxDefault : (double)FadeMaxDist,
		["disabled"] = IsDisabled,
	};

	/// <summary>
	/// Parse <see cref="ToJson"/> output. "disabled": true (or a missing pair,
	/// which is the registry defaults -1 / 0) reads as <see cref="Disabled"/>.
	/// A non-numeric value throws <see cref="ArgumentException"/> naming the key.
	/// </summary>
	public static PropFade FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		if ( json["disabled"] is not null && json["disabled"]!.GetValue<bool>() ) return Disabled;
		var min = Num( json, "fadeMinDist", DisabledMinSentinel );
		if ( min == DisabledMinSentinel ) return Disabled;
		var max = Num( json, "fadeMaxDist", DisabledMaxDefault );
		return new PropFade( min, max );
	}

	static float Num( JsonObject json, string key, float fallback )
	{
		if ( json[key] is null ) return fallback;
		try { return (float)json[key]!.GetValue<double>(); }
		catch ( Exception ex ) { throw new ArgumentException( $"PropFade JSON: '{key}' is not a number.", ex ); }
	}

	static string Format( float value ) => value.ToString( "R", CultureInfo.InvariantCulture );

	public bool Equals( PropFade o ) =>
		IsDisabled == o.IsDisabled && FadeMinDist == o.FadeMinDist && FadeMaxDist == o.FadeMaxDist;

	public override bool Equals( object obj ) => obj is PropFade other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( FadeMinDist, FadeMaxDist, IsDisabled );
	public override string ToString() => IsDisabled
		? "fade disabled"
		: $"fade {FadeMinDist:0.###}..{FadeMaxDist:0.###}";
}

/// <summary>
/// One face's lightmap grid: how many luxels the native bake allocates for a
/// face of a given size at a given lightmap scale (world units per luxel).
/// A face is at least 1x1 luxels — a tiny face still gets one sample.
/// </summary>
public readonly struct LightmapLuxelGrid : IEquatable<LightmapLuxelGrid>
{
	public readonly float FaceWidth;
	public readonly float FaceHeight;
	public readonly float LightmapScale;
	public readonly int Cols;
	public readonly int Rows;
	/// <summary>Luxels across the face: Cols * Rows.</summary>
	public readonly int Total => Cols * Rows;
	/// <summary>Luxels per world unit along an edge: 1 / scale.</summary>
	public readonly float LuxelsPerUnitLength => 1f / LightmapScale;

	public LightmapLuxelGrid( float faceWidth, float faceHeight, float lightmapScale, int cols, int rows )
	{
		FaceWidth = faceWidth;
		FaceHeight = faceHeight;
		LightmapScale = lightmapScale;
		Cols = cols;
		Rows = rows;
	}

	public bool Equals( LightmapLuxelGrid o ) =>
		FaceWidth == o.FaceWidth && FaceHeight == o.FaceHeight && LightmapScale == o.LightmapScale &&
		Cols == o.Cols && Rows == o.Rows;

	public override bool Equals( object obj ) => obj is LightmapLuxelGrid other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( FaceWidth, FaceHeight, LightmapScale, Cols, Rows );
	public override string ToString() =>
		$"{Cols}x{Rows} luxels ({Total}) at scale {LightmapScale:0.###} over {FaceWidth:0.###}x{FaceHeight:0.###}";
}

/// <summary>
/// The bake-side reading of the per-face lightmap scale
/// (<see cref="FaceUv.LightmapScale"/>): how the native lightmap bake sizes a
/// face's lightmap from its recorded scale. Luxel spacing IS the scale — one
/// luxel per <c>scale</c> world units along each edge — so doubling the scale
/// halves the linear luxel density and quarters the luxel count on the same
/// face. The ticket's acceptance case is the linear density: lightmap scale 4
/// vs 1 is 4x the luxels per unit (1/1 vs 1/4), and 16x the total count
/// (see <see cref="CountRatio"/>). Coarser luxels where the scale is large is
/// exactly how the scale trades bake quality for time: fewer samples to bake.
/// </summary>
public static class LightmapLuxels
{
	/// <summary>
	/// The luxel grid for a face <paramref name="faceWidth"/> x
	/// <paramref name="faceHeight"/> world units at
	/// <paramref name="lightmapScale"/> world units per luxel: ceil(extent /
	/// scale) per axis, at least 1. Throws <see cref="ArgumentException"/> for a
	/// non-positive scale or a negative / non-finite extent.
	/// </summary>
	public static LightmapLuxelGrid ForFace( float faceWidth, float faceHeight, float lightmapScale )
	{
		if ( !( lightmapScale > 0f ) || !float.IsFinite( lightmapScale ) )
			throw new ArgumentException( $"Lightmap scale is {lightmapScale}: it must be positive." );
		if ( !float.IsFinite( faceWidth ) || faceWidth < 0f )
			throw new ArgumentException( $"Face width is {faceWidth}: it must be finite and non-negative." );
		if ( !float.IsFinite( faceHeight ) || faceHeight < 0f )
			throw new ArgumentException( $"Face height is {faceHeight}: it must be finite and non-negative." );
		var cols = Math.Max( 1, (int)MathF.Ceiling( faceWidth / lightmapScale ) );
		var rows = Math.Max( 1, (int)MathF.Ceiling( faceHeight / lightmapScale ) );
		return new LightmapLuxelGrid( faceWidth, faceHeight, lightmapScale, cols, rows );
	}

	/// <summary>
	/// The luxel grid for one brush face: the face's projected extents
	/// (<see cref="FaceUv.ProjectedBounds"/>) at the scale recorded on its
	/// <see cref="FaceMaterial.Uv"/>. Nodraw faces still get a grid — whether a
	/// face bakes is <see cref="FaceMaterial.ContributesToBakedMesh"/>'s call,
	/// not this one's.
	/// </summary>
	public static LightmapLuxelGrid ForFace( FaceMaterial face, IReadOnlyList<Vec3> vertices, Vec3 normal )
	{
		if ( face is null ) throw new ArgumentNullException( nameof( face ) );
		var (uMin, uMax, vMin, vMax) = FaceUv.ProjectedBounds( vertices, normal );
		return ForFace( uMax - uMin, vMax - vMin, face.Uv.LightmapScale );
	}

	/// <summary>
	/// The luxel grid for a face the bake already recorded
	/// (<see cref="MapBakeFaceInfo.LightmapScale"/>): the scale the bake
	/// carried is the scale the lightmap is sized from.
	/// </summary>
	public static LightmapLuxelGrid ForBakedFace( MapBakeFaceInfo faceInfo, float faceWidth, float faceHeight )
	{
		if ( faceInfo is null ) throw new ArgumentNullException( nameof( faceInfo ) );
		return ForFace( faceWidth, faceHeight, faceInfo.LightmapScale );
	}

	/// <summary>Luxels per world unit along an edge at this scale: 1 / scale.</summary>
	public static float LinearDensity( float lightmapScale )
	{
		if ( !( lightmapScale > 0f ) || !float.IsFinite( lightmapScale ) )
			throw new ArgumentException( $"Lightmap scale is {lightmapScale}: it must be positive." );
		return 1f / lightmapScale;
	}

	/// <summary>
	/// Linear luxel-density ratio of scale A vs scale B: density(A) /
	/// density(B) = B / A. Scale 1 vs 4 is 4: the scale-1 face packs 4x the
	/// luxels per unit along each edge.
	/// </summary>
	public static float DensityRatio( float scaleA, float scaleB ) =>
		LinearDensity( scaleA ) / LinearDensity( scaleB );

	/// <summary>
	/// Total-luxel-count ratio of scale A vs scale B on the same face:
	/// (B / A)^2. Scale 1 vs 4 is 16: the scale-1 face has 16x the luxels.
	/// </summary>
	public static float CountRatio( float scaleA, float scaleB )
	{
		var ratio = DensityRatio( scaleA, scaleB );
		return ratio * ratio;
	}
}
