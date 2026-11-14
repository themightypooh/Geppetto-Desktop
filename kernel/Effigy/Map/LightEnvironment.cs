// LightEnvironment — Hammer Desktop P2: lighting entities (hammer-lighting-environment-sun-fog).
//
// Backend / data-model / API only — no Desktop UI, no Hammer app, no shelling out to
// Hammer/Hammer++/VBSP/VVIS/VRAD. Native Geppetto: entity definitions + native bake inputs.
//
// WHAT THIS IS. The adapter between placed lighting entities (MapEntity, placed through
// MapEntities against an EntityRegistry) and the two consumers that need them:
//   * the native lightmap bake (CompileRunner, hammer-compile-native-pipeline), via
//     LightingSetup.ToCompileLights() -> CompileLight list, and
//   * the realtime-lit preview (LiveLighting, hammer-preview-live-lighting), via
//     BakeLight.ToLiveLight() / LightingSetup.ToLiveLights().
// Both consumers read the same resolved data; there is no second lighting evaluator here.
//
// THE SEVEN CLASSES (Report §1.2 #38). light + light_spot already ship in the core
// registry (kernel/Effigy/Map/registry/core-source1.json). The other six
// are defined here, as registry JSON (LightingRegistryJson, schema v1, name
// "source1-lighting") so they can be merged into any registry with no code change:
// EntityRegistry.LoadFromJson(LightingRegistryJson) / MergeInto(registry). The same
// JSON ships as the fixture tests/fixtures/hammer/hammer-lighting-environment-sun-fog/
// lighting-source1.json — the file the integrator copies into kernel/Effigy/Map/registry/.
// Key names follow the Source 1 FGD as implemented in this tree (light_environment:
// _light / _ambient / pitch; env_sun: rendercolor / size / overlaycolor / overlaysize;
// light_dynamic: _light / _distance / style; env_fog_controller: fogcolor / fogcolor2 /
// fogstart / fogend / fogmaxdensity / farz / fogenable; shadow_control: color / distance /
// disableallshadows; env_tonemap_controller: bloom_scale / autoexposure_min /
// autoexposure_max / tonemap_rate / tonemap_percent_bright_pixels / tonemap_min_avglum /
// tonemap_percent_target). Defaults are Geppetto defaults, documented on the JSON.
//
// SUN DIRECTION CONVENTION — same as SkyboxPreview (kernel/Effigy/Map/SkyboxPreview.cs),
// do not invent a second one: Source angles are pitch/yaw/roll with pitch positive-DOWN,
// so elevation above the horizon is -pitch. DirectionFromAngles() returns the unit vector
// the entity FACES: (cos(el)*cos(yaw), cos(el)*sin(yaw), sin(el)). A light_environment's
// angles point AT the sun, so its travel direction is -DirectionFromAngles(); a light_spot's
// angles point where the light SHINES, so its travel direction is +DirectionFromAngles().
// The `pitch` key, when non-zero, overrides angles.x (per TWHL: a non-zero pitch overwrites
// the one in angles). env_sun's sprite direction is DirectionFromAngles() from its own angles.
//
// _light COLOUR + BRIGHTNESS — same parse as SkyboxPreview.TryParseLight(): "r g b
// [brightness]", rgb 0-255 -> 0..1 linear, brightness is the raw 4th component (Source
// intensity in its own units; default 1 when absent). No rescaling is invented here:
// a standard Source light "255 255 255 200" resolves to colour (1,1,1), intensity 200.
// LiveLight and CompileLight are HDR, so 200 is representable; the bake clamps texels to 1.
//
// COMPILE SEAM (hammer-compile-native-pipeline, CompileRunner.cs as it exists on disk —
// read, not edited):
// CompileLight is a POINT light only (Position / Color / Intensity / Range) and
// CompileInput.Lights is List<CompileLight>. It has NO directional sun, NO ambient, NO
// cone, NO fog/shadow/tonemap input, and its DirectLight() is Lambert * 1/(1+(d/128)^2)
// with a shadow ray to the light position. ToCompileLights() therefore maps:
//   * light          -> one CompileLight, exact.
//   * light_spot     -> one CompileLight at the spot position; the cone is LOST (the
//                        conversion reports SpotConeLosses + a note; the exact cone survives
//                        on BakeLight for the preview and for a future cone-aware bake).
//   * light_environment sun -> one CompileLight on the DISTANT-LIGHT APPROXIMATION:
//                        position = mapCenter - travel * SunApproximationDistance (8192),
//                        intensity = intensity * (1 + (8192/128)^2) = intensity * 4097, so
//                        DirectLight's falloff divides it straight back to `intensity *
//                        facing` at the texel. Near-parallel rays, correct sun shadows; the
//                        only error is the direction spread over the map (~mapExtent/8192).
//                        The exact sun (direction/colour/intensity) survives on BakeLight.
//   * light_environment ambient -> NO CompileLight: the runner has no ambient input.
//                        Reported as DroppedAmbient; the integrator adds an ambient term
//                        to the bake (or a CompileLight ambient field) — see ADAPTER SEAM below.
//   * light_dynamic  -> NO CompileLight: dynamic lights are realtime, never baked.
//                        Reported as DroppedDynamic; they DO reach the preview.
//   * fog / shadow / tonemap -> carried on LightingSetup for the renderer/preview; the
//                        runner takes no such input today (shadow DisableAllShadows would
//                        need a bake flag; fog/tonemap are render-time, not bake-time).
// ADAPTER SEAM for the integrator: when CompileRunner gains a directional/ambient light
// (or a CompileLight kind field), replace ToCompileLights()'s sun approximation and the
// ambient drop with a direct mapping — BakeLight already carries the exact values, so no
// entity-side change is needed. Do not edit this file's public API to paper over the runner.
// ApplyToCompileInput (LightingSetup) is the call FromDocument should make: it
// appends the converted lights to a CompileInput and returns the conversion.
// FromDocument still only carries "light" — and it reads the key "light", not
// the registry's "_light", so a light placed through MapEntities is missed there
// too (packing worker's file, CompileRunner.cs — flagged for the integrator).
//
// PAIR VALIDATION. light_environment + env_sun are a pair: a light_environment with no
// env_sun warns SunWithoutEnvSun (the sun light has no visible sun sprite — the reading
// SkyboxPreview already uses), and an env_sun with no light_environment warns
// EnvSunWithoutLightEnvironment (a visible sun with no sun light; LiveLighting warns the
// same way for a bare sun). A direction mismatch between the two (> 5 degrees) warns
// EnvSunDirectionMismatch. All findings are warnings, never errors: FromEntities never
// throws for entity content (null input and the BakeLight constructor's own argument
// checks excepted) and always keeps a documented default, mirroring SkyboxPreview's
// warn-and-keep-default pattern. First wins for the singleton controllers
// (light_environment, env_fog_controller, shadow_control, env_tonemap_controller).
//
// IGNORED KEYS (documented, not silent): light/light_spot/light_dynamic `style` != 0 is
// a flicker pattern — the bake uses the steady value and warns LightStyleIgnored.
// `_quadratic_attenuation` is ignored: both consumers use their own fixed falloff
// (LiveLighting 1/(1+(d/64)^2), CompileRunner 1/(1+(d/128)^2)).
// `light`/`light_spot` `_cone`/`_inner_cone` are read as HALF-angles, matching LiveLight's
// contract; if Source treats _cone as the full angle this is a factor-of-2 fix at the adapter.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Effigy.Map;

/// <summary>Severity of one <see cref="LightingIssue"/> from <see cref="LightEnvironment.FromEntities"/>.</summary>
public enum LightingIssueSeverity
{
	Warning,
	Error,
}

/// <summary>
/// One named, non-fatal finding from resolving lighting entities: a missing sun pair,
/// a multiple singleton controller, an unparseable key, a dangling spot target, or an
/// out-of-range fog/tonemap value. <see cref="Code"/> is the stable machine-readable name;
/// <see cref="EntityId"/> is the placed entity's id, or 0 when the finding is about the set.
/// </summary>
public sealed class LightingIssue
{
	public LightingIssueSeverity Severity;
	public string Code = "";
	public string Message = "";
	public int EntityId;

	public LightingIssue() { }
	public LightingIssue( LightingIssueSeverity severity, string code, string message, int entityId = 0 )
	{
		Severity = severity; Code = code; Message = message; EntityId = entityId;
	}

	public override string ToString() => EntityId == 0
		? $"[{Severity} {Code}] {Message}"
		: $"[{Severity} {Code}] {Message} (entity #{EntityId})";
}

/// <summary>The Source lighting entity kinds this file resolves, per Report §1.2 #38.</summary>
public enum BakeLightKind
{
	/// <summary>Source entity "light": omnidirectional point light.</summary>
	Point,
	/// <summary>Source entity "light_spot": cone light.</summary>
	Spot,
	/// <summary>Source entity "env_sun" / the sun half of "light_environment": directional.</summary>
	Sun,
	/// <summary>Source entity "light_environment": the ambient half (no position/direction).</summary>
	Environment,
	/// <summary>Source entity "light_dynamic": realtime point light; never baked.</summary>
	Dynamic,
}

/// <summary>
/// One resolved light for the native bake / live preview: the bake light list entry.
/// Colour is linear RGB 0..1; Intensity is the Source _light brightness in its own units
/// (raw 4th component, default 1); Range 0 means no cutoff. Direction is the travel
/// direction (unit) for Spot/Sun and Zero for Point/Environment/Dynamic. Cone angles are
/// half-angles in degrees (Spot only). Immutable; construct via the factories or the
/// constructor, which applies LiveLight's argument checks so an invalid light cannot be built.
/// </summary>
public sealed class BakeLight : IEquatable<BakeLight>
{
	public int SourceEntityId { get; }
	public string ClassName { get; }
	public BakeLightKind Kind { get; }
	/// <summary>Point/Spot/Dynamic position. Ignored for Sun and Environment.</summary>
	public Vec3 Position { get; }
	/// <summary>Travel direction (unit). Sun: direction the sun shines; Spot: cone axis. Ignored for Point/Environment/Dynamic.</summary>
	public Vec3 Direction { get; }
	/// <summary>Linear RGB colour, components >= 0.</summary>
	public Vec3 Color { get; }
	/// <summary>Irradiance scale, >= 0 (Source _light brightness, raw).</summary>
	public float Intensity { get; }
	/// <summary>Point/Spot/Dynamic cutoff distance; 0 means no cutoff (infinite range).</summary>
	public float Range { get; }
	/// <summary>Spot inner cone half-angle in degrees; ignored for other kinds.</summary>
	public float InnerConeDegrees { get; }
	/// <summary>Spot outer cone half-angle in degrees; ignored for other kinds.</summary>
	public float OuterConeDegrees { get; }

	public BakeLight( BakeLightKind kind, int sourceEntityId, string className, Vec3 position, Vec3 direction,
		Vec3 color, float intensity, float range = 0f, float innerConeDegrees = 0f, float outerConeDegrees = 0f )
	{
		if ( string.IsNullOrWhiteSpace( className ) )
			throw new ArgumentException( "BakeLight needs a class name.", nameof( className ) );
		if ( intensity < 0f )
			throw new ArgumentException( $"BakeLight {sourceEntityId} ({kind}): intensity {intensity} is negative." );
		if ( range < 0f )
			throw new ArgumentException( $"BakeLight {sourceEntityId} ({kind}): range {range} is negative." );
		if ( color.x < 0f || color.y < 0f || color.z < 0f )
			throw new ArgumentException( $"BakeLight {sourceEntityId} ({kind}): colour {color} has a negative component." );
		if ( kind == BakeLightKind.Spot )
		{
			if ( innerConeDegrees < 0f || innerConeDegrees > 180f || outerConeDegrees <= 0f || outerConeDegrees > 180f )
				throw new ArgumentException( $"Spot BakeLight {sourceEntityId}: cone angles inner {innerConeDegrees} / outer {outerConeDegrees} must be within 0..180 with outer > 0." );
			if ( innerConeDegrees >= outerConeDegrees )
				throw new ArgumentException( $"Spot BakeLight {sourceEntityId}: inner cone {innerConeDegrees} must be smaller than outer cone {outerConeDegrees}." );
			if ( direction.LengthSquared <= 1e-12f )
				throw new ArgumentException( $"Spot BakeLight {sourceEntityId}: direction is zero-length." );
		}
		if ( kind == BakeLightKind.Sun && direction.LengthSquared <= 1e-12f )
			throw new ArgumentException( $"Sun BakeLight {sourceEntityId}: direction is zero-length." );
		SourceEntityId = sourceEntityId;
		ClassName = className;
		Kind = kind;
		Position = position;
		Direction = direction.LengthSquared > 1e-12f ? direction.Normal : Vec3.Zero;
		Color = color;
		Intensity = intensity;
		Range = range;
		InnerConeDegrees = innerConeDegrees;
		OuterConeDegrees = outerConeDegrees;
	}

	/// <summary>Source entity "light": omnidirectional point light at <paramref name="position"/>.</summary>
	public static BakeLight Point( int sourceEntityId, Vec3 position, Vec3 color, float intensity, float range = 0f ) =>
		new( BakeLightKind.Point, sourceEntityId, "light", position, Vec3.Zero, color, intensity, range );

	/// <summary>Source entity "light_spot": cone light; <paramref name="direction"/> is the cone axis (travel direction).</summary>
	public static BakeLight Spot( int sourceEntityId, Vec3 position, Vec3 direction, Vec3 color, float intensity,
		float innerConeDegrees, float outerConeDegrees, float range = 0f ) =>
		new( BakeLightKind.Spot, sourceEntityId, "light_spot", position, direction, color, intensity, range, innerConeDegrees, outerConeDegrees );

	/// <summary>Source entity "env_sun" / the sun half of "light_environment": directional light; <paramref name="direction"/> is the direction the sun shines.</summary>
	public static BakeLight Sun( int sourceEntityId, Vec3 direction, Vec3 color, float intensity ) =>
		new( BakeLightKind.Sun, sourceEntityId, "light_environment", Vec3.Zero, direction, color, intensity );

	/// <summary>Source entity "light_environment": the ambient half. No position or direction; contributes <c>color * intensity</c> to every face.</summary>
	public static BakeLight Environment( int sourceEntityId, Vec3 ambientColor, float intensity ) =>
		new( BakeLightKind.Environment, sourceEntityId, "light_environment", Vec3.Zero, Vec3.Zero, ambientColor, intensity );

	/// <summary>Source entity "light_dynamic": realtime point light; reaches the preview, never the bake.</summary>
	public static BakeLight Dynamic( int sourceEntityId, Vec3 position, Vec3 color, float intensity, float range = 0f ) =>
		new( BakeLightKind.Dynamic, sourceEntityId, "light_dynamic", position, Vec3.Zero, color, intensity, range );

	/// <summary>The LiveLight this bake light is for the preview evaluator (same data, same values).</summary>
	public LiveLight ToLiveLight()
	{
		switch ( Kind )
		{
			case BakeLightKind.Point: return LiveLight.Point( SourceEntityId, Position, Color, Intensity, Range );
			case BakeLightKind.Spot: return LiveLight.Spot( SourceEntityId, Position, Direction, Color, Intensity, InnerConeDegrees, OuterConeDegrees, Range );
			case BakeLightKind.Sun: return LiveLight.Sun( SourceEntityId, Direction, Color, Intensity );
			case BakeLightKind.Environment: return LiveLight.Environment( SourceEntityId, Color, Intensity );
			case BakeLightKind.Dynamic: return LiveLight.Point( SourceEntityId, Position, Color, Intensity, Range );
			default: throw new ArgumentException( $"Unknown bake light kind: {Kind}." );
		}
	}

	/// <summary>
	/// The CompileLight this bake light is for the native bake, or null when the runner
	/// cannot represent it (Environment ambient and Dynamic lights — see the file header).
	/// Point is exact; Spot drops its cone (same position/colour/intensity/range); Sun uses
	/// the distant-light approximation (see <see cref="LightEnvironment.SunApproximationDistance"/>).
	/// </summary>
	public CompileLight ToCompileLight( Vec3 mapCenter )
	{
		switch ( Kind )
		{
			case BakeLightKind.Point:
			case BakeLightKind.Spot:
				return new CompileLight( Position, Intensity, Color, Range );
			case BakeLightKind.Sun:
			{
				var distance = LightEnvironment.SunApproximationDistance;
				var compensated = Intensity * ( 1f + ( distance / 128f ) * ( distance / 128f ) );
				return new CompileLight( mapCenter - Direction * distance, compensated, Color, 0f );
			}
			default:
				return null;
		}
	}

	public bool Equals( BakeLight other ) =>
		other is not null && SourceEntityId == other.SourceEntityId && Kind == other.Kind &&
		string.Equals( ClassName, other.ClassName, StringComparison.OrdinalIgnoreCase ) &&
		Position.Equals( other.Position ) && Direction.Equals( other.Direction ) &&
		Color.Equals( other.Color ) && Intensity == other.Intensity && Range == other.Range &&
		InnerConeDegrees == other.InnerConeDegrees && OuterConeDegrees == other.OuterConeDegrees;

	public override bool Equals( object obj ) => obj is BakeLight other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( HashCode.Combine( SourceEntityId, Kind, Position, Direction, Color, Intensity ), Range, InnerConeDegrees );
	public override string ToString() => $"{Kind} #{SourceEntityId} ({ClassName}) colour {Color} intensity {Intensity:0.###}";
}

/// <summary>
/// Fog resolved from an env_fog_controller (keys per the Source FGD; the same keys
/// SkyboxPreview reads from env_fog_controller / worldspawn). Linear fog: no fog before
/// Start, full MaxDensity at/after End. Absent keys stay 0 (unset); End &lt;= Start means
/// the fog has no visible range. Enabled is false when fogenable is "0" or no controller exists.
/// </summary>
public sealed class FogSettings
{
	/// <summary>False when the controller is disabled via fogenable "0".</summary>
	public bool Enabled = true;
	/// <summary>Near distance where fog starts; 0 when the key was absent (unset).</summary>
	public float Start;
	/// <summary>Far distance where fog is opaque; 0 when the key was absent (unset).</summary>
	public float End;
	/// <summary>Fog colour, 0..1 RGB; white when the key was absent.</summary>
	public Vec3 Color = Vec3.One;
	/// <summary>Secondary fog colour, 0..1 RGB; white when the key was absent. No Geppetto consumer reads it yet.</summary>
	public Vec3 Color2 = Vec3.One;
	/// <summary>Maximum fog density, 0..1; 1 when the key was absent.</summary>
	public float MaxDensity = 1f;
	/// <summary>Far Z clip distance; -1 when the key was absent (unset). No Geppetto consumer reads it yet.</summary>
	public float FarZ = -1f;

	/// <summary>Fog factor at <paramref name="distance"/>: 0 before Start, MaxDensity at/after End, linear between. 0 when disabled or End &lt;= Start.</summary>
	public float FogFactor( float distance )
	{
		if ( !Enabled || End <= Start ) return 0f;
		var t = ( distance - Start ) / ( End - Start );
		if ( t <= 0f ) return 0f;
		if ( t >= 1f ) return MaxDensity < 0f ? 0f : MaxDensity > 1f ? 1f : MaxDensity;
		var density = MaxDensity < 0f ? 0f : MaxDensity > 1f ? 1f : MaxDensity;
		return t * density;
	}
}

/// <summary>Shadow control resolved from a shadow_control entity (Source FGD: color / distance / disableallshadows).</summary>
public sealed class ShadowSettings
{
	/// <summary>Shadow colour, 0..1 RGB; black when the key was absent.</summary>
	public Vec3 Color = Vec3.Zero;
	/// <summary>Maximum shadow distance; 0 = no limit.</summary>
	public float Distance;
	/// <summary>True when disableallshadows is non-zero: the bake must skip its shadow rays (a bake flag the runner does not take yet — see the file header).</summary>
	public bool DisableAllShadows;
}

/// <summary>
/// Tonemap control resolved from an env_tonemap_controller (Source FGD keys; inputs
/// SetBloomScale / SetAutoExposureMin / SetAutoExposureMax). Render-time only: it never
/// feeds the lightmap bake. Defaults are the Source FGD defaults.
/// </summary>
public sealed class TonemapSettings
{
	public float BloomScale = 1f;
	public float AutoExposureMin = 0.5f;
	public float AutoExposureMax = 2f;
	public float TonemapRate = 1f;
	public float PercentBrightPixels = 2f;
	public float MinAvgLum = 3f;
	public float PercentTarget = 60f;

	/// <summary>Clamp an exposure to [AutoExposureMin, AutoExposureMax]; unchanged when the range itself is invalid (min &gt; max — warned at resolve time).</summary>
	public float ClampExposure( float exposure )
	{
		if ( AutoExposureMin > AutoExposureMax ) return exposure;
		if ( exposure < AutoExposureMin ) return AutoExposureMin;
		if ( exposure > AutoExposureMax ) return AutoExposureMax;
		return exposure;
	}
}

/// <summary>
/// What <see cref="LightingSetup.ToCompileLights"/> returns: the CompileLights the native
/// bake takes, plus an honest account of what the runner could not represent (see the
/// file header for the per-kind mapping). Notes are human-readable; the counts are exact.
/// </summary>
public sealed class CompileLightConversion
{
	public IReadOnlyList<CompileLight> Lights { get; }
	public IReadOnlyList<string> Notes { get; }
	/// <summary>Suns mapped through the distant-light approximation.</summary>
	public int SunApproximations { get; }
	/// <summary>Spots mapped as points with their cone dropped.</summary>
	public int SpotConeLosses { get; }
	/// <summary>Environment ambient terms the runner has no input for.</summary>
	public int DroppedAmbient { get; }
	/// <summary>Dynamic lights excluded from the bake (realtime only).</summary>
	public int DroppedDynamic { get; }

	public CompileLightConversion( IReadOnlyList<CompileLight> lights, IReadOnlyList<string> notes,
		int sunApproximations, int spotConeLosses, int droppedAmbient, int droppedDynamic )
	{
		Lights = lights;
		Notes = notes;
		SunApproximations = sunApproximations;
		SpotConeLosses = spotConeLosses;
		DroppedAmbient = droppedAmbient;
		DroppedDynamic = droppedDynamic;
	}
}

/// <summary>
/// The lighting setup resolved from a map's entities: every resolved light (static +
/// dynamic + ambient), the first fog / shadow / tonemap controller each, the env_sun
/// sprite data, and the pair/validation warnings. Build it with
/// <see cref="LightEnvironment.FromEntities"/>; never construct it by hand.
/// </summary>
public sealed class LightingSetup
{
	public IReadOnlyList<BakeLight> Lights { get; }
	public IReadOnlyList<LightingIssue> Issues { get; }
	/// <summary>The first sun (light_environment direct light), or null when the map has none.</summary>
	public BakeLight Sun { get; }
	/// <summary>The first ambient term (light_environment _ambient), or null when none was specified.</summary>
	public BakeLight Environment { get; }
	/// <summary>True when the map has an env_sun entity.</summary>
	public bool HasEnvSun { get; }
	/// <summary>First env_sun's sprite colour, 0..1 RGB; white when no env_sun exists.</summary>
	public Vec3 EnvSunColor { get; }
	/// <summary>First env_sun's sprite size; 0 when no env_sun exists.</summary>
	public int EnvSunSize { get; }
	/// <summary>First env_sun's overlay colour, 0..1 RGB; white when no env_sun exists.</summary>
	public Vec3 EnvSunOverlayColor { get; }
	/// <summary>First env_sun's overlay size; 0 when no env_sun exists.</summary>
	public int EnvSunOverlaySize { get; }
	/// <summary>The first env_fog_controller, or null when the map has none.</summary>
	public FogSettings Fog { get; }
	/// <summary>The first shadow_control, or null when the map has none.</summary>
	public ShadowSettings Shadow { get; }
	/// <summary>The first env_tonemap_controller, or null when the map has none.</summary>
	public TonemapSettings Tonemap { get; }

	internal LightingSetup( IReadOnlyList<BakeLight> lights, IReadOnlyList<LightingIssue> issues,
		BakeLight sun, BakeLight environment, bool hasEnvSun, Vec3 envSunColor, int envSunSize,
		Vec3 envSunOverlayColor, int envSunOverlaySize, FogSettings fog, ShadowSettings shadow, TonemapSettings tonemap )
	{
		Lights = lights;
		Issues = issues;
		Sun = sun;
		Environment = environment;
		HasEnvSun = hasEnvSun;
		EnvSunColor = envSunColor;
		EnvSunSize = envSunSize;
		EnvSunOverlayColor = envSunOverlayColor;
		EnvSunOverlaySize = envSunOverlaySize;
		Fog = fog;
		Shadow = shadow;
		Tonemap = tonemap;
	}

	public bool HasIssue( string code )
	{
		foreach ( var issue in Issues )
			if ( string.Equals( issue.Code, code, StringComparison.Ordinal ) ) return true;
		return false;
	}

	public IReadOnlyList<LightingIssue> IssuesWithCode( string code )
	{
		var found = new List<LightingIssue>();
		foreach ( var issue in Issues )
			if ( string.Equals( issue.Code, code, StringComparison.Ordinal ) ) found.Add( issue );
		return found;
	}

	/// <summary>All resolved lights as LiveLights for the preview evaluator (same data, same values; dynamic lights included as points).</summary>
	public IReadOnlyList<LiveLight> ToLiveLights()
	{
		var live = new List<LiveLight>( Lights.Count );
		foreach ( var light in Lights ) live.Add( light.ToLiveLight() );
		return live;
	}

	/// <summary>
	/// The native bake light list for CompileRunner (see the file header for the per-kind
	/// mapping and what is dropped). <paramref name="mapCenter"/> anchors the sun's
	/// distant-light approximation; pass the map's centroid (e.g. the room centre).
	/// </summary>
	public CompileLightConversion ToCompileLights( Vec3 mapCenter )
	{
		var lights = new List<CompileLight>();
		var notes = new List<string>();
		int suns = 0, spots = 0, droppedAmbient = 0, droppedDynamic = 0;
		foreach ( var light in Lights )
		{
			switch ( light.Kind )
			{
				case BakeLightKind.Point:
					lights.Add( light.ToCompileLight( mapCenter ) );
					break;
				case BakeLightKind.Spot:
					lights.Add( light.ToCompileLight( mapCenter ) );
					spots++;
					notes.Add( $"Spot #{light.SourceEntityId}: cone {ConeText( light )} dropped — CompileLight is a point light; the exact cone survives on the BakeLight for the preview." );
					break;
				case BakeLightKind.Sun:
					lights.Add( light.ToCompileLight( mapCenter ) );
					suns++;
					notes.Add( $"Sun #{light.SourceEntityId}: approximated as a distant point light {LightEnvironment.SunApproximationDistance:0} units away along the sun direction with intensity compensated for the runner's falloff; replace with a true directional light when CompileRunner gains one." );
					break;
				case BakeLightKind.Environment:
					droppedAmbient++;
					notes.Add( $"Ambient #{light.SourceEntityId}: dropped — CompileRunner has no ambient input; the integrator adds an ambient term to the bake." );
					break;
				case BakeLightKind.Dynamic:
					droppedDynamic++;
					notes.Add( $"Dynamic light #{light.SourceEntityId}: excluded from the bake (realtime only); it reaches the preview." );
					break;
			}
		}
		return new CompileLightConversion( lights, notes, suns, spots, droppedAmbient, droppedDynamic );
	}

	/// <summary>
	/// Append this setup's bake lights to a <see cref="CompileInput"/> (the native bake
	/// light list) and return the conversion: point/spot/sun via <see cref="ToCompileLights"/>;
	/// ambient/dynamic are reported, not added. This is the seam CompileRunner.FromDocument
	/// should call to carry light_spot and light_environment into the bake — FromDocument
	/// itself still only carries "light" (see the file header). Does not touch the input's
	/// brushes, player start or stages.
	/// </summary>
	public CompileLightConversion ApplyToCompileInput( CompileInput input, Vec3 mapCenter )
	{
		if ( input is null ) throw new ArgumentNullException( nameof( input ) );
		var conversion = ToCompileLights( mapCenter );
		input.Lights.AddRange( conversion.Lights );
		return conversion;
	}

	static string ConeText( BakeLight light ) =>
		$"inner {light.InnerConeDegrees:0.###} / outer {light.OuterConeDegrees:0.###} degrees";
}

/// <summary>
/// Hammer Desktop P2: lighting entities — light, light_spot, light_environment +
/// env_sun, light_dynamic, env_fog_controller, shadow_control, env_tonemap_controller.
/// The registry definitions (the six classes core-source1.json does not ship) and the
/// adapter from placed entities to bake lights / preview lights / fog / shadow / tonemap.
/// See the file header for the source conventions, the CompileRunner seam and the pair rules.
/// </summary>
public static class LightEnvironment
{
	// ---- Issue codes (stable, machine-readable) ----
	public const string CodeSunWithoutEnvSun = "SunWithoutEnvSun";
	public const string CodeEnvSunWithoutLightEnvironment = "EnvSunWithoutLightEnvironment";
	public const string CodeEnvSunDirectionMismatch = "EnvSunDirectionMismatch";
	public const string CodeMultipleLightEnvironments = "MultipleLightEnvironments";
	public const string CodeMultipleFogControllers = "MultipleFogControllers";
	public const string CodeMultipleShadowControls = "MultipleShadowControls";
	public const string CodeMultipleTonemapControllers = "MultipleTonemapControllers";
	public const string CodeSpotTargetUnresolved = "SpotTargetUnresolved";
	public const string CodeSpotConeInvalid = "SpotConeInvalid";
	public const string CodeLightStyleIgnored = "LightStyleIgnored";
	public const string CodeNegativeDistance = "NegativeDistance";
	public const string CodeInvalidLightColour = "InvalidLightColour";
	public const string CodeInvalidEnvSunValue = "InvalidEnvSunValue";
	public const string CodeInvalidLightValue = "InvalidLightValue";
	public const string CodeInvalidSunAngles = "InvalidSunAngles";
	public const string CodeInvalidFogValue = "InvalidFogValue";
	public const string CodeInvalidShadowValue = "InvalidShadowValue";
	public const string CodeInvalidTonemapValue = "InvalidTonemapValue";
	public const string CodeFogEndNotAfterStart = "FogEndNotAfterStart";
	public const string CodeFogDensityOutOfRange = "FogDensityOutOfRange";
	public const string CodeTonemapExposureRangeInvalid = "TonemapExposureRangeInvalid";
	public const string CodeTonemapPercentOutOfRange = "TonemapPercentOutOfRange";
	public const string CodeTonemapNegativeValue = "TonemapNegativeValue";

	/// <summary>
	/// Distance of the sun's distant-light approximation from the map centre (see the file
	/// header). 8192 units: the direction spread over a 512-unit map is ~3.6 degrees, and
	/// the intensity compensation factor (1 + (8192/128)^2) = 4097 is exact in float.
	/// </summary>
	public const float SunApproximationDistance = 8192f;

	/// <summary>Angle between a light_environment's sun direction and its env_sun's sprite direction above which they warn as mismatched.</summary>
	public const float EnvSunMismatchDegrees = 5f;

	/// <summary>Registry name for the lighting classes.</summary>
	public const string LightingRegistryName = "source1-lighting";

	/// <summary>Number of classes <see cref="LightingRegistryJson"/> defines (the six core-source1.json does not ship).</summary>
	public const int LightingRegistryClassCount = 6;

	/// <summary>
	/// The six lighting entity classes core-source1.json does not ship, as registry JSON
	/// (schema v1). Merge it into a registry with <see cref="MergeInto"/>; the same JSON
	/// ships as tests/fixtures/hammer/hammer-lighting-environment-sun-fog/lighting-source1.json.
	/// Defaults are Geppetto defaults, documented per key.
	/// </summary>
	public const string LightingRegistryJson = """
		{
		  "schemaVersion": 1,
		  "name": "source1-lighting",
		  "description": "Source-1 lighting entity set for Geppetto Desktop (hammer-lighting-environment-sun-fog): the six lighting classes core-source1.json does not ship — light_environment, env_sun, light_dynamic, env_fog_controller, shadow_control, env_tonemap_controller. light and light_spot already ship in source1-core. Every class implicitly accepts the base keyvalues classname (string), targetname (string), parentname (target), angles (angle) and spawnflags (int); they are not repeated per class.",
		  "classes": [
		    {
		      "name": "light_environment",
		      "kind": "point",
		      "description": "Sun light: direct sunlight (_light) plus ambient (_ambient). Position does not matter; angles point at the sun (pitch positive-down, elevation is -pitch). Pair with an env_sun for the visible sun sprite. Only the first one in a map is used.",
		      "baseClasses": [ "Targetname" ],
		      "keyvalues": [
		        { "name": "_light", "type": "colour", "default": "255 255 255 200", "description": "Sun direct colour (R G B, 0-255) and brightness (4th component, default 200)." },
		        { "name": "_ambient", "type": "colour", "default": "0 0 0", "description": "Ambient colour (R G B, 0-255) and brightness (4th component). Black = no ambient term." },
		        { "name": "pitch", "type": "int", "default": "-45", "description": "Sun pitch in degrees (negative = sun above the horizon, shining down). Non-zero overrides the pitch in angles." }
		      ],
		      "spawnflags": [],
		      "inputs": [],
		      "outputs": [],
		      "helpers": [ { "kind": "origin" }, { "kind": "angles" }, { "key": "_light", "kind": "colour" } ]
		    },
		    {
		      "name": "env_sun",
		      "kind": "point",
		      "description": "Visible sun sprite in the sky. Angles point at the sun (same convention as light_environment); pair with a light_environment for the actual sun light.",
		      "baseClasses": [ "Targetname" ],
		      "keyvalues": [
		        { "name": "rendercolor", "type": "colour", "default": "255 255 255", "description": "Sun sprite colour (R G B, 0-255)." },
		        { "name": "size", "type": "int", "default": "16", "description": "Sun sprite size." },
		        { "name": "overlaycolor", "type": "colour", "default": "255 255 255", "description": "Sun glow overlay colour (R G B, 0-255)." },
		        { "name": "overlaysize", "type": "int", "default": "64", "description": "Sun glow overlay size." }
		      ],
		      "spawnflags": [],
		      "inputs": [],
		      "outputs": [],
		      "helpers": [ { "kind": "origin" }, { "kind": "angles" }, { "key": "rendercolor", "kind": "colour" } ]
		    },
		    {
		      "name": "light_dynamic",
		      "kind": "point",
		      "description": "Realtime (dynamic) point light. Never baked: it reaches the live preview only, and is excluded from the native bake light list.",
		      "baseClasses": [ "Targetname", "Parentname" ],
		      "keyvalues": [
		        { "name": "_light", "type": "colour", "default": "255 255 255 200", "description": "Light colour (R G B, 0-255) and brightness (4th component, default 200)." },
		        { "name": "_distance", "type": "float", "default": "0", "description": "Maximum distance the light reaches. 0 = infinite." },
		        { "name": "style", "type": "int", "default": "0", "description": "Light style (0 = normal, 1-63 = flicker patterns). Non-zero styles are not baked." }
		      ],
		      "spawnflags": [],
		      "inputs": [
		        { "name": "TurnOn", "argumentType": "void", "description": "Turn the light on." },
		        { "name": "TurnOff", "argumentType": "void", "description": "Turn the light off." },
		        { "name": "Toggle", "argumentType": "void", "description": "Toggle the light." }
		      ],
		      "outputs": [],
		      "helpers": [ { "kind": "origin" }, { "key": "_light", "kind": "colour" } ]
		    },
		    {
		      "name": "env_fog_controller",
		      "kind": "point",
		      "description": "Distance fog controller: linear fog between fogstart and fogend, capped at fogmaxdensity. First controller in a map wins.",
		      "baseClasses": [ "Targetname" ],
		      "keyvalues": [
		        { "name": "fogcolor", "type": "colour", "default": "255 255 255", "description": "Fog colour (R G B, 0-255)." },
		        { "name": "fogcolor2", "type": "colour", "default": "255 255 255", "description": "Secondary fog colour (R G B, 0-255). No Geppetto consumer reads it yet." },
		        { "name": "fogstart", "type": "float", "default": "0", "description": "Near distance where fog starts. 0 = unset." },
		        { "name": "fogend", "type": "float", "default": "0", "description": "Far distance where fog is opaque. 0 = unset." },
		        { "name": "fogmaxdensity", "type": "float", "default": "1", "description": "Maximum fog density, 0..1." },
		        { "name": "farz", "type": "float", "default": "-1", "description": "Far Z clip distance. -1 = unset. No Geppetto consumer reads it yet." },
		        { "name": "fogenable", "type": "int", "default": "1", "choices": [ "0", "1" ], "description": "Fog enabled (1) or disabled (0)." }
		      ],
		      "spawnflags": [],
		      "inputs": [
		        { "name": "TurnOn", "argumentType": "void", "description": "Enable the fog." },
		        { "name": "TurnOff", "argumentType": "void", "description": "Disable the fog." }
		      ],
		      "outputs": [],
		      "helpers": [ { "kind": "origin" }, { "key": "fogcolor", "kind": "colour" } ]
		    },
		    {
		      "name": "shadow_control",
		      "kind": "point",
		      "description": "Shadow controller: shadow colour and maximum shadow distance. First controller in a map wins.",
		      "baseClasses": [ "Targetname" ],
		      "keyvalues": [
		        { "name": "color", "type": "colour", "default": "0 0 0", "description": "Shadow colour (R G B, 0-255)." },
		        { "name": "distance", "type": "float", "default": "0", "description": "Maximum shadow distance. 0 = no limit." },
		        { "name": "disableallshadows", "type": "int", "default": "0", "choices": [ "0", "1" ], "description": "Disable all shadows (1) or not (0). Needs a bake flag the runner does not take yet." }
		      ],
		      "spawnflags": [],
		      "inputs": [],
		      "outputs": [],
		      "helpers": [ { "kind": "origin" }, { "key": "color", "kind": "colour" } ]
		    },
		    {
		      "name": "env_tonemap_controller",
		      "kind": "point",
		      "description": "HDR tonemap / auto-exposure controller. Render-time only: it never feeds the lightmap bake. First controller in a map wins.",
		      "baseClasses": [ "Targetname" ],
		      "keyvalues": [
		        { "name": "bloom_scale", "type": "float", "default": "1", "description": "HDR bloom scale." },
		        { "name": "autoexposure_min", "type": "float", "default": "0.5", "description": "Minimum auto-exposure." },
		        { "name": "autoexposure_max", "type": "float", "default": "2", "description": "Maximum auto-exposure." },
		        { "name": "tonemap_rate", "type": "float", "default": "1", "description": "Tonemap adaptation rate." },
		        { "name": "tonemap_percent_bright_pixels", "type": "float", "default": "2", "description": "Percent of bright pixels driving exposure, 0..100." },
		        { "name": "tonemap_min_avglum", "type": "float", "default": "3", "description": "Minimum average luminance." },
		        { "name": "tonemap_percent_target", "type": "float", "default": "60", "description": "Target percent for exposure, 0..100." }
		      ],
		      "spawnflags": [],
		      "inputs": [
		        { "name": "SetBloomScale", "argumentType": "float", "description": "Set the HDR bloom scale." },
		        { "name": "SetAutoExposureMin", "argumentType": "float", "description": "Set the minimum auto-exposure." },
		        { "name": "SetAutoExposureMax", "argumentType": "float", "description": "Set the maximum auto-exposure." }
		      ],
		      "outputs": [],
		      "helpers": [ { "kind": "origin" } ]
		    }
		  ]
		}
		""";

	/// <summary>Load the lighting registry (the six classes core-source1.json does not ship).</summary>
	public static EntityRegistry CreateLightingRegistry() =>
		EntityRegistry.LoadFromJson( LightingRegistryJson, LightingRegistryName );

	/// <summary>Merge the lighting classes into an existing registry (e.g. the core registry). Duplicate class names are rejected (DuplicateClass).</summary>
	public static void MergeInto( EntityRegistry registry )
	{
		if ( registry is null ) throw new ArgumentNullException( nameof( registry ) );
		registry.Merge( CreateLightingRegistry() );
	}

	/// <summary>All eight lighting class names this ticket defines/resolves.</summary>
	public static readonly string[] LightingClassNames =
	{
		"light", "light_spot", "light_environment", "env_sun",
		"light_dynamic", "env_fog_controller", "shadow_control", "env_tonemap_controller",
	};

	// ---- Direction helpers (Source angles convention; see the file header) ----

	/// <summary>
	/// Source angles "pitch yaw roll" (pitch positive-DOWN) -&gt; the unit vector the entity
	/// FACES: elevation is -pitch, so (cos(el)*cos(yaw), cos(el)*sin(yaw), sin(el)).
	/// angles "0 90 0" gives (0, 1, 0); pitch -90 gives (0, 0, 1). Same formula as
	/// SkyboxPreview.TryParseSunDirection.
	/// </summary>
	public static Vec3 DirectionFromAngles( float pitchDegrees, float yawDegrees )
	{
		var elevation = -pitchDegrees * MathF.PI / 180f;
		var yaw = yawDegrees * MathF.PI / 180f;
		var ce = MathF.Cos( elevation );
		return new Vec3( ce * MathF.Cos( yaw ), ce * MathF.Sin( yaw ), MathF.Sin( elevation ) ).Normal;
	}

	/// <summary>The direction a light_environment's sun SHINES (travel direction): the negative of the direction its angles point at.</summary>
	public static Vec3 SunTravelDirection( Vec3 angles, float pitchOverride = 0f )
	{
		var pitch = pitchOverride != 0f ? pitchOverride : angles.x;
		return -DirectionFromAngles( pitch, angles.y );
	}

	/// <summary>The direction a light_spot SHINES (travel direction): its angles point where the light goes.</summary>
	public static Vec3 SpotTravelDirection( Vec3 angles, float pitchOverride = 0f )
	{
		var pitch = pitchOverride != 0f ? pitchOverride : angles.x;
		return DirectionFromAngles( pitch, angles.y );
	}

	// ---- Value parsing (same shapes as SkyboxPreview; warn-and-default, never throw) ----

	/// <summary>Parse "r g b [brightness]" with rgb in 0-255; brightness defaults to 1. False on any unparseable component (colour/intensity untouched).</summary>
	public static bool TryParseLightColour( string text, out Vec3 color, out float brightness )
	{
		color = Vec3.One;
		brightness = 1f;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length < 3 || parts.Length > 4 ) return false;
		if ( !TryParseFloat( parts[0], out var r ) || !TryParseFloat( parts[1], out var g ) || !TryParseFloat( parts[2], out var b ) ) return false;
		if ( parts.Length == 4 && !TryParseFloat( parts[3], out brightness ) ) return false;
		color = new Vec3( Clamp01( r / 255f ), Clamp01( g / 255f ), Clamp01( b / 255f ) );
		return true;
	}

	/// <summary>Parse "r g b" with components in 0-255. False on any unparseable component.</summary>
	public static bool TryParseColour255( string text, out Vec3 color )
	{
		color = Vec3.One;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		if ( !TryParseFloat( parts[0], out var r ) || !TryParseFloat( parts[1], out var g ) || !TryParseFloat( parts[2], out var b ) ) return false;
		color = new Vec3( Clamp01( r / 255f ), Clamp01( g / 255f ), Clamp01( b / 255f ) );
		return true;
	}

	static bool TryParseFloat( string text, out float value ) =>
		float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out value );

	static bool TryParseInt( string text, out int value ) =>
		int.TryParse( text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value );

	static float Clamp01( float v ) => v < 0f ? 0f : v > 1f ? 1f : v;

	// ---- Entity resolution ----

	/// <summary>
	/// Resolve a map's lighting entities into a <see cref="LightingSetup"/>: bake lights
	/// (point/spot/sun/ambient/dynamic), fog, shadow and tonemap settings, the env_sun
	/// sprite data, and the pair/validation warnings. Non-lighting entities are ignored.
	/// <paramref name="resolveTarget"/> maps a light_spot targetname to its position
	/// (null = no targets resolve: the spot falls back to its angles and warns).
	/// Never throws for entity content; bad values warn and keep the documented default.
	/// </summary>
	public static LightingSetup FromEntities( IReadOnlyList<MapEntity> entities, Func<string, Vec3?> resolveTarget = null )
	{
		if ( entities is null ) throw new ArgumentNullException( nameof( entities ) );
		var lights = new List<BakeLight>();
		var issues = new List<LightingIssue>();
		BakeLight sun = null;
		BakeLight environment = null;
		var hasEnvSun = false;
		var envSunColor = Vec3.One;
		var envSunSize = 0;
		var envSunOverlayColor = Vec3.One;
		var envSunOverlaySize = 0;
		Vec3? envSunSkyDirection = null;
		Vec3? lightEnvSkyDirection = null;
		FogSettings fog = null;
		ShadowSettings shadow = null;
		TonemapSettings tonemap = null;
		var lightEnvironmentCount = 0;
		var fogCount = 0;
		var shadowCount = 0;
		var tonemapCount = 0;

		foreach ( var entity in entities )
		{
			if ( entity is null ) continue;
			switch ( entity.ClassName.ToLowerInvariant() )
			{
				case "light":
					lights.Add( ResolvePointLight( entity, "light", BakeLightKind.Point, issues ) );
					break;
				case "light_dynamic":
					lights.Add( ResolvePointLight( entity, "light_dynamic", BakeLightKind.Dynamic, issues ) );
					break;
				case "light_spot":
					lights.Add( ResolveSpotLight( entity, resolveTarget, issues ) );
					break;
				case "light_environment":
				{
					lightEnvironmentCount++;
					if ( lightEnvironmentCount > 1 )
					{
						issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeMultipleLightEnvironments,
							$"The map has {lightEnvironmentCount} light_environment entities; the first is used.", entity.Id ) );
						break;
					}
					var (sunLight, ambientLight, skyDirection) = ResolveLightEnvironment( entity, issues );
					sun = sunLight;
					environment = ambientLight;
					lightEnvSkyDirection = skyDirection;
					lights.Add( sunLight );
					if ( ambientLight is not null ) lights.Add( ambientLight );
					break;
				}
				case "env_sun":
				{
					hasEnvSun = true;
					var (color, size, overlayColor, overlaySize, skyDirection) = ResolveEnvSun( entity, issues );
					if ( envSunSkyDirection is null )
					{
						envSunColor = color;
						envSunSize = size;
						envSunOverlayColor = overlayColor;
						envSunOverlaySize = overlaySize;
						envSunSkyDirection = skyDirection;
					}
					break;
				}
				case "env_fog_controller":
				{
					fogCount++;
					if ( fogCount > 1 )
					{
						issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeMultipleFogControllers,
							$"The map has {fogCount} env_fog_controller entities; the first is used.", entity.Id ) );
						break;
					}
					fog = ResolveFog( entity, issues );
					break;
				}
				case "shadow_control":
				{
					shadowCount++;
					if ( shadowCount > 1 )
					{
						issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeMultipleShadowControls,
							$"The map has {shadowCount} shadow_control entities; the first is used.", entity.Id ) );
						break;
					}
					shadow = ResolveShadow( entity, issues );
					break;
				}
				case "env_tonemap_controller":
				{
					tonemapCount++;
					if ( tonemapCount > 1 )
					{
						issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeMultipleTonemapControllers,
							$"The map has {tonemapCount} env_tonemap_controller entities; the first is used.", entity.Id ) );
						break;
					}
					tonemap = ResolveTonemap( entity, issues );
					break;
				}
				default:
					// Not a lighting entity; MapEntities owns it. Ignored here, never an issue.
					break;
			}
		}

		if ( lightEnvironmentCount > 0 && !hasEnvSun )
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeSunWithoutEnvSun,
				"The map has a light_environment but no env_sun; the sun light has no visible sun sprite." ) );
		if ( hasEnvSun && lightEnvironmentCount == 0 )
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeEnvSunWithoutLightEnvironment,
				"The map has an env_sun but no light_environment; the visible sun has no sun light." ) );
		if ( lightEnvSkyDirection.HasValue && envSunSkyDirection.HasValue )
		{
			var dot = Vec3.Dot( lightEnvSkyDirection.Value, envSunSkyDirection.Value );
			var cosMismatch = MathF.Cos( EnvSunMismatchDegrees * MathF.PI / 180f );
			if ( dot < cosMismatch )
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeEnvSunDirectionMismatch,
					$"The env_sun points {MathF.Acos( Math.Clamp( dot, -1f, 1f ) ) * 180f / MathF.PI:0.#} degrees away from the light_environment sun direction; the sprite and the light disagree about where the sun is." ) );
		}

		return new LightingSetup( lights, issues, sun, environment, hasEnvSun,
			envSunColor, envSunSize, envSunOverlayColor, envSunOverlaySize, fog, shadow, tonemap );
	}

	// ---- Per-class resolvers (each warns and keeps a default on a bad value) ----

	static BakeLight ResolvePointLight( MapEntity entity, string className, BakeLightKind kind, List<LightingIssue> issues )
	{
		var (color, brightness) = ReadLightColour( entity, "_light", issues );
		var range = ReadRange( entity, issues );
		ReadStyle( entity, issues );
		return new BakeLight( kind, entity.Id, className, entity.Origin, Vec3.Zero, color, brightness, range );
	}

	static BakeLight ResolveSpotLight( MapEntity entity, Func<string, Vec3?> resolveTarget, List<LightingIssue> issues )
	{
		var (color, brightness) = ReadLightColour( entity, "_light", issues );
		var range = ReadRange( entity, issues );
		ReadStyle( entity, issues );
		var (inner, outer) = ReadCone( entity, issues );
		var direction = SpotTravelDirection( entity.Angles, ReadPitch( entity ) );
		if ( entity.Keyvalues.TryGetValue( "target", out var target ) && !string.IsNullOrWhiteSpace( target ) )
		{
			var resolved = resolveTarget?.Invoke( target );
			if ( resolved.HasValue )
			{
				var toTarget = resolved.Value - entity.Origin;
				if ( toTarget.LengthSquared > 1e-6f ) direction = toTarget.Normal;
				else issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeSpotTargetUnresolved,
					$"light_spot #{entity.Id} targets '{target}', which sits at the light's own origin; the angles direction is used.", entity.Id ) );
			}
			else
			{
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeSpotTargetUnresolved,
					$"light_spot #{entity.Id} targets '{target}', which no entity resolves; the angles direction is used.", entity.Id ) );
			}
		}
		return new BakeLight( BakeLightKind.Spot, entity.Id, "light_spot", entity.Origin, direction,
			color, brightness, range, inner, outer );
	}

	static (BakeLight sun, BakeLight ambient, Vec3 skyDirection) ResolveLightEnvironment( MapEntity entity, List<LightingIssue> issues )
	{
		var (color, brightness) = ReadLightColour( entity, "_light", issues );
		var pitch = ReadPitch( entity );
		var skyDirection = DirectionFromAngles( pitch != 0f ? pitch : entity.Angles.x, entity.Angles.y );
		var travel = -skyDirection;
		var sunLight = BakeLight.Sun( entity.Id, travel, color, brightness );
		BakeLight ambientLight = null;
		if ( entity.Keyvalues.TryGetValue( "_ambient", out var ambientText ) )
		{
			if ( !TryParseLightColour( ambientText, out var ambientColor, out var ambientBrightness ) )
			{
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidLightColour,
					$"light_environment #{entity.Id} _ambient '{ambientText}' is not 'r g b [brightness]'; black ambient is used.", entity.Id ) );
			}
			else if ( ambientColor.LengthSquared > 1e-12f )
			{
				ambientLight = BakeLight.Environment( entity.Id, ambientColor, ambientBrightness );
			}
		}
		return (sunLight, ambientLight, skyDirection);
	}

	static (Vec3 color, int size, Vec3 overlayColor, int overlaySize, Vec3 skyDirection) ResolveEnvSun( MapEntity entity, List<LightingIssue> issues )
	{
		var color = Vec3.One;
		var size = 16;
		var overlayColor = Vec3.One;
		var overlaySize = 64;
		if ( entity.Keyvalues.TryGetValue( "rendercolor", out var renderText ) )
		{
			if ( !TryParseColour255( renderText, out color ) )
			{
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidLightColour,
					$"env_sun #{entity.Id} rendercolor '{renderText}' is not 'r g b' 0-255; white is used.", entity.Id ) );
				color = Vec3.One;
			}
		}
		if ( entity.Keyvalues.TryGetValue( "size", out var sizeText ) && !TryParseInt( sizeText, out size ) )
		{
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidEnvSunValue,
				$"env_sun #{entity.Id} size '{sizeText}' is not an integer; 16 is used.", entity.Id ) );
			size = 16;
		}
		if ( entity.Keyvalues.TryGetValue( "overlaycolor", out var overlayText ) )
		{
			if ( !TryParseColour255( overlayText, out overlayColor ) )
			{
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidLightColour,
					$"env_sun #{entity.Id} overlaycolor '{overlayText}' is not 'r g b' 0-255; white is used.", entity.Id ) );
				overlayColor = Vec3.One;
			}
		}
		if ( entity.Keyvalues.TryGetValue( "overlaysize", out var overlaySizeText ) && !TryParseInt( overlaySizeText, out overlaySize ) )
		{
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidEnvSunValue,
				$"env_sun #{entity.Id} overlaysize '{overlaySizeText}' is not an integer; 64 is used.", entity.Id ) );
			overlaySize = 64;
		}
		var skyDirection = DirectionFromAngles( entity.Angles.x, entity.Angles.y );
		return (color, size, overlayColor, overlaySize, skyDirection);
	}

	static FogSettings ResolveFog( MapEntity entity, List<LightingIssue> issues )
	{
		var fog = new FogSettings();
		if ( entity.Keyvalues.TryGetValue( "fogenable", out var enableText ) )
		{
			if ( TryParseInt( enableText, out var enabled ) ) fog.Enabled = enabled != 0;
			else
			{
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidFogValue,
					$"env_fog_controller #{entity.Id} fogenable '{enableText}' is not an integer; fog stays enabled.", entity.Id ) );
			}
		}
		fog.Start = ReadFogFloat( entity, "fogstart", 0f, issues );
		fog.End = ReadFogFloat( entity, "fogend", 0f, issues );
		if ( entity.Keyvalues.TryGetValue( "fogcolor", out var colorText ) )
		{
			if ( !TryParseColour255( colorText, out var color ) )
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidFogValue,
					$"env_fog_controller #{entity.Id} fogcolor '{colorText}' is not 'r g b' 0-255; white is used.", entity.Id ) );
			else fog.Color = color;
		}
		if ( entity.Keyvalues.TryGetValue( "fogcolor2", out var color2Text ) )
		{
			if ( !TryParseColour255( color2Text, out var color2 ) )
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidFogValue,
					$"env_fog_controller #{entity.Id} fogcolor2 '{color2Text}' is not 'r g b' 0-255; white is used.", entity.Id ) );
			else fog.Color2 = color2;
		}
		fog.MaxDensity = ReadFogFloat( entity, "fogmaxdensity", 1f, issues );
		fog.FarZ = ReadFogFloat( entity, "farz", -1f, issues );
		if ( fog.Enabled && fog.End > 0f && fog.End <= fog.Start )
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeFogEndNotAfterStart,
				$"env_fog_controller #{entity.Id} fogend {fog.End:0.###} is not after fogstart {fog.Start:0.###}; the fog has no visible range.", entity.Id ) );
		if ( fog.MaxDensity < 0f || fog.MaxDensity > 1f )
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeFogDensityOutOfRange,
				$"env_fog_controller #{entity.Id} fogmaxdensity {fog.MaxDensity:0.###} is outside 0..1; FogFactor clamps it.", entity.Id ) );
		return fog;
	}

	static float ReadFogFloat( MapEntity entity, string key, float fallback, List<LightingIssue> issues )
	{
		if ( !entity.Keyvalues.TryGetValue( key, out var text ) ) return fallback;
		if ( TryParseFloat( text, out var value ) && float.IsFinite( value ) ) return value;
		issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidFogValue,
			$"env_fog_controller #{entity.Id} {key} '{text}' is not a finite number; {fallback:0.###} is used.", entity.Id ) );
		return fallback;
	}

	static ShadowSettings ResolveShadow( MapEntity entity, List<LightingIssue> issues )
	{
		var shadow = new ShadowSettings();
		if ( entity.Keyvalues.TryGetValue( "color", out var colorText ) )
		{
			if ( !TryParseColour255( colorText, out var color ) )
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidShadowValue,
					$"shadow_control #{entity.Id} color '{colorText}' is not 'r g b' 0-255; black is used.", entity.Id ) );
			else shadow.Color = color;
		}
		if ( entity.Keyvalues.TryGetValue( "distance", out var distanceText ) )
		{
			if ( TryParseFloat( distanceText, out var distance ) && float.IsFinite( distance ) )
			{
				if ( distance < 0f )
				{
					issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeNegativeDistance,
						$"shadow_control #{entity.Id} distance {distance:0.###} is negative; 0 (no limit) is used.", entity.Id ) );
					shadow.Distance = 0f;
				}
				else shadow.Distance = distance;
			}
			else
			{
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidShadowValue,
					$"shadow_control #{entity.Id} distance '{distanceText}' is not a finite number; 0 (no limit) is used.", entity.Id ) );
			}
		}
		if ( entity.Keyvalues.TryGetValue( "disableallshadows", out var disableText ) )
		{
			if ( TryParseInt( disableText, out var disable ) ) shadow.DisableAllShadows = disable != 0;
			else
			{
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidShadowValue,
					$"shadow_control #{entity.Id} disableallshadows '{disableText}' is not an integer; shadows stay enabled.", entity.Id ) );
			}
		}
		return shadow;
	}

	static TonemapSettings ResolveTonemap( MapEntity entity, List<LightingIssue> issues )
	{
		var tonemap = new TonemapSettings();
		tonemap.BloomScale = ReadTonemapFloat( entity, "bloom_scale", tonemap.BloomScale, issues );
		tonemap.AutoExposureMin = ReadTonemapFloat( entity, "autoexposure_min", tonemap.AutoExposureMin, issues );
		tonemap.AutoExposureMax = ReadTonemapFloat( entity, "autoexposure_max", tonemap.AutoExposureMax, issues );
		tonemap.TonemapRate = ReadTonemapFloat( entity, "tonemap_rate", tonemap.TonemapRate, issues );
		tonemap.PercentBrightPixels = ReadTonemapFloat( entity, "tonemap_percent_bright_pixels", tonemap.PercentBrightPixels, issues );
		tonemap.MinAvgLum = ReadTonemapFloat( entity, "tonemap_min_avglum", tonemap.MinAvgLum, issues );
		tonemap.PercentTarget = ReadTonemapFloat( entity, "tonemap_percent_target", tonemap.PercentTarget, issues );
		if ( tonemap.BloomScale < 0f || tonemap.TonemapRate < 0f || tonemap.MinAvgLum < 0f )
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeTonemapNegativeValue,
				$"env_tonemap_controller #{entity.Id} has a negative bloom_scale / tonemap_rate / tonemap_min_avglum; values must be >= 0.", entity.Id ) );
		if ( tonemap.AutoExposureMin > tonemap.AutoExposureMax )
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeTonemapExposureRangeInvalid,
				$"env_tonemap_controller #{entity.Id} autoexposure_min {tonemap.AutoExposureMin:0.###} exceeds autoexposure_max {tonemap.AutoExposureMax:0.###}; ClampExposure returns the exposure unchanged.", entity.Id ) );
		if ( tonemap.PercentBrightPixels < 0f || tonemap.PercentBrightPixels > 100f
			|| tonemap.PercentTarget < 0f || tonemap.PercentTarget > 100f )
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeTonemapPercentOutOfRange,
				$"env_tonemap_controller #{entity.Id} has a tonemap percent outside 0..100; percents must be 0..100.", entity.Id ) );
		return tonemap;
	}

	static float ReadTonemapFloat( MapEntity entity, string key, float fallback, List<LightingIssue> issues )
	{
		if ( !entity.Keyvalues.TryGetValue( key, out var text ) ) return fallback;
		if ( TryParseFloat( text, out var value ) && float.IsFinite( value ) ) return value;
		issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidTonemapValue,
			$"env_tonemap_controller #{entity.Id} {key} '{text}' is not a finite number; {fallback:0.###} is used.", entity.Id ) );
		return fallback;
	}

	// ---- Shared key readers ----

	/// <summary>_light "r g b [brightness]" (white / 1 when absent or unparseable, with a warning on unparseable).</summary>
	static (Vec3 color, float brightness) ReadLightColour( MapEntity entity, string key, List<LightingIssue> issues )
	{
		if ( !entity.Keyvalues.TryGetValue( key, out var text ) ) return (Vec3.One, 1f);
		if ( TryParseLightColour( text, out var color, out var brightness ) ) return (color, brightness);
		issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidLightColour,
			$"{entity.ClassName} #{entity.Id} {key} '{text}' is not 'r g b [brightness]'; white at brightness 1 is used.", entity.Id ) );
		return (Vec3.One, 1f);
	}

	/// <summary>_distance float (0 = infinite when absent; negative warns and clamps to 0).</summary>
	static float ReadRange( MapEntity entity, List<LightingIssue> issues )
	{
		if ( !entity.Keyvalues.TryGetValue( "_distance", out var text ) ) return 0f;
		if ( TryParseFloat( text, out var distance ) && float.IsFinite( distance ) )
		{
			if ( distance < 0f )
			{
				issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeNegativeDistance,
					$"{entity.ClassName} #{entity.Id} _distance {distance:0.###} is negative; 0 (infinite) is used.", entity.Id ) );
				return 0f;
			}
			return distance;
		}
		issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeInvalidLightValue,
			$"{entity.ClassName} #{entity.Id} _distance '{text}' is not a finite number; 0 (infinite) is used.", entity.Id ) );
		return 0f;
	}

	/// <summary>style int: non-zero (a flicker pattern) warns LightStyleIgnored; the bake uses the steady value.</summary>
	static void ReadStyle( MapEntity entity, List<LightingIssue> issues )
	{
		if ( entity.Keyvalues.TryGetValue( "style", out var text ) && TryParseInt( text, out var style ) && style != 0 )
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeLightStyleIgnored,
				$"{entity.ClassName} #{entity.Id} style {style} is a flicker pattern; the bake uses the steady value.", entity.Id ) );
	}

	/// <summary>Spot cone: _cone is the outer half-angle (default 45), _inner_cone the inner half-angle (default 30). Invalid pairs warn and clamp inner to half the outer.</summary>
	static (float inner, float outer) ReadCone( MapEntity entity, List<LightingIssue> issues )
	{
		var outer = 45f;
		var inner = 30f;
		if ( entity.Keyvalues.TryGetValue( "_cone", out var coneText ) && TryParseFloat( coneText, out var cone ) && float.IsFinite( cone ) ) outer = cone;
		if ( entity.Keyvalues.TryGetValue( "_inner_cone", out var innerText ) && TryParseFloat( innerText, out var innerValue ) && float.IsFinite( innerValue ) ) inner = innerValue;
		if ( outer <= 0f || outer > 180f || inner < 0f || inner >= outer )
		{
			issues.Add( new LightingIssue( LightingIssueSeverity.Warning, CodeSpotConeInvalid,
				$"light_spot #{entity.Id} cone inner {inner:0.###} / outer {outer:0.###} is invalid (need 0 <= inner < outer <= 180); inner is clamped to half the outer.", entity.Id ) );
			if ( outer <= 0f || outer > 180f ) outer = 45f;
			inner = outer * 0.5f;
		}
		return (inner, outer);
	}

	/// <summary>pitch int key (0 when absent/unparseable). Non-zero overrides angles.x — see the file header.</summary>
	static float ReadPitch( MapEntity entity )
	{
		if ( entity.Keyvalues.TryGetValue( "pitch", out var text ) && TryParseInt( text, out var pitch ) ) return pitch;
		return 0f;
	}
}
