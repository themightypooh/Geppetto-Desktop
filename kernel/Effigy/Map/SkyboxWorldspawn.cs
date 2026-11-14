using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy.Map;

// Hammer Desktop P2: 2D/3D skybox + sky_camera + worldspawn map properties (native).
// Board: hammer-skybox-2d-3d-worldspawn (Geppetto Desktop, Hammer P2).
//
// WHAT THIS FILE OWNS (pass 1: the data). Worldspawn map properties (2D skybox
// name, chapter/title, detail props, fog) + the sky_camera entity + the 3D
// skybox section (scaled geometry + its own origin) as native map data, with
// a VMF round-trip and a bake contribution. Backend / data-model / API only:
// no Desktop UI, no Hammer/VBSP shell-out, no viewport code. Units are the
// kernel's inches, Z-up (handoffs/CONTRACTS.md).
//
// WHAT IT REUSES (no type in this file is a second copy of a sibling type —
// a redefinition would be a duplicate-type compile break; reconcile, do not
// fork):
//   - WorldspawnProperties / SkyboxSettings / SkyboxSection3D / StarterMap
//     (StarterTemplates.cs): the template/authoring form. This file reuses
//     WorldspawnProperties as its worldspawn record and converts
//     StarterMap -> SkyboxWorldspawn (see FromStarterMap). There is NO
//     reverse conversion here: a VmfBrush face is a plane (three points), and
//     rebuilding the IssueBrush polygons from planes needs the plane-
//     intersection solver (MapCsg) — that direction belongs to the
//     MapDocument adapter, not to this file.
//   - VmfDocument / VmfIo (VmfIo.cs): the transfer model. Skybox data
//     round-trips through it; MapDocument does not exist yet.
//   - SkyboxWarning + the warning-code constants (SkyboxPreview.cs): the
//     named warnings this file and the preview share. New codes this file
//     introduces are declared below; the shared ones are referenced, never
//     re-declared with a second literal.
//   - FaceMaterial / MapBakeBrush / MapBakeDocument (FaceMaterial.cs,
//     MapBakeExport.cs, namespace Effigy): the bake side. ToMapBakeBrushes
//     hands world-scale section brushes to MapBakeExport.Bake; this file
//     does not redefine any bake type.
//
// DEPENDENCY STATUS (2026-11-13, verified against the board + the tree):
//   - hammer-entity-registry-fgd-json is DONE, but sky_camera and
//     light_environment are NOT in the landed registry
//     (kernel/Effigy/Map/registry/core-source1.json: 13 classes, neither of
//     them). This file therefore validates sky_camera by classname and
//     reports registry membership with IsSkyCameraInRegistry (expected
//     FALSE until the integrator adds the class); it does NOT edit the
//     registry or claim registry validation. Same for worldspawn: it is not
//     a registry class either.
//   - hammer-map-document-model is IN PROGRESS; MapDocument.cs does not
//     exist on disk. This file binds to a map by string id only
//     (SkyboxWorldspawn.MapId, the PanelLayout.LayoutDocument.MapId pattern)
//     and the integrator adds the one thin adapter (MapDocument ->
//     SkyboxWorldspawn) when that type lands. No MapDocument member is
//     invented here.
//   - SkyboxPreview.cs (worker 24, in progress) is the CONSUMER of this
//     data: it reads worldspawn "skyname", sky_camera and the section from a
//     VmfDocument. This file's writer produces exactly the shapes it reads
//     (see the section-identification rule below), and its fog reading is
//     the worldspawn fallback this file writes to. One known mismatch,
//     documented not papered over: SkyboxPreview.ResolveFog reads the
//     worldspawn fogstart/fogend/fogcolor/fogmaxdensity keys but ignores a
//     worldspawn "fogenable" "0" (it only honours fogenable on
//     env_fog_controller). This file still writes fogenable, because the
//     DATA must be able to say "fog disabled"; the one-line preview fix is
//     a follow-up for the preview/lighting ticket.
//
// WORLDSPAWN KEY MAPPING (this ticket defines it; StarterTemplates.cs
// deferred both fields to this ticket):
//   SkyboxName     -> "skyname"          (standard Source worldspawn key)
//   Chapter        -> "chaptertitle"     (standard Source worldspawn key:
//                                          the chapter card on map start)
//   Title          -> "geppetto_title"   (NO standard Source worldspawn key
//                                          for a map title exists in this
//                                          repo; Geppetto-native, following
//                                          VmfIo's "geppetto_vertices"
//                                          precedent. Survives Geppetto
//                                          round-trips; Hammer may drop
//                                          unknown worldspawn keys on re-save)
//   DetailProps    -> "detailvbsp"       (standard Source worldspawn key:
//   DetailVbsp        detail prop file, default "detail.vbsp") +
//                    "detailmaterial"   (standard Source worldspawn key:
//   DetailMaterial    detail sprite material, default
//                                          "detail/detailsprites"). Both keys
//                                          are written only when DetailProps
//                                          is true.
//   Fog (preset)   -> "fog"              (Geppetto-side preset name, not a
//                                          Source key; the lighting ticket
//                                          resolves presets to parameters.
//                                          Independent of FogParams below.)
//   FogParams      -> "fogstart" / "fogend" / "fogcolor" ("r g b" 0-255) /
//                     "fogmaxdensity" / "fogenable" ("1"/"0"). The same keys
//                     SkyboxPreview reads as its worldspawn fog fallback;
//                     env_fog_controller still wins at preview time.
//
// APPLYTO OWNERSHIP. ApplyToVmfDocument owns the skybox slice of the
// document: it sets the worldspawn keys above (removing one when the
// aggregate has no value for it), upserts the aggregate's sky_camera
// entities by id (removing sky_camera entities the aggregate does not
// contain), and upserts the aggregate's section brushes by id into the
// "3D Skybox" visgroup (removing visgroup members the aggregate does not
// contain). Anything outside the skybox slice is left untouched. From->
// edit -> ApplyTo is therefore lossless; applying a fresh aggregate to a
// document intentionally replaces that document's skybox data.
//
// SECTION IDENTIFICATION (same rule as SkyboxPreview, so writer and reader
// agree): (1) visgroup rule (primary, exact): brushes whose editor
// visgroup is named "3D Skybox" are the section. (2) Spatial rule
// (fallback, only when the visgroup yields no buildable brush and a
// sky_camera exists): a brush belongs to the section when its centroid
// (mean of its face plane points — a cheap stand-in for the preview's
// solved-mesh centroid; the visgroup rule is the exact one) lies within
// SkyboxPreview.SectionRadius of the sky_camera origin. A brush with
// fewer than 4 faces or a degenerate plane is never a section member
// (named warning skybox-invalid-section-brush).
//
// BAKE DIRECTION. The preview holds the section as a 1/16 miniature about
// the sky_camera origin (SkyboxPreview's filed rule). The ENGINE scales the
// authored section UP by the sky_camera scale at render time, and the BAKE
// does the same: p' = anchor + (p - anchor) * scale, where the anchor is
// the sky_camera origin when a camera exists, else the section's own
// origin. SkyboxBakeData carries the world-scale brushes plus the anchor
// it used, so a caller can see which source won; ToMapBakeBrushes turns
// them into MapBakeBrushes for MapBakeExport.Bake. The bake never invents
// a camera: without one the section anchor is used and Validate warns.
//
// WARNINGS ARE DATA, NOT EXCEPTIONS (repo convention). A 3D section
// without a sky_camera is the named warning "skybox-missing-sky-camera"
// in the result, never a crash; every other defect below is likewise a
// named warning code with a human message. New codes in this file:
//   skybox-section-camera-mismatch  section origin/scale disagree with the
//                                    sky_camera (the engine follows the camera)
//   skybox-empty-sky-name           3D section present but no 2D sky name
//   skybox-invalid-section-scale    section scale missing/invalid/<=0
// Shared codes (SkyboxPreview): skybox-missing-sky-camera,
// skybox-multiple-sky-cameras, skybox-invalid-sky-camera-origin,
// skybox-invalid-sky-camera-scale, skybox-invalid-section-brush,
// skybox-invalid-fog.

/// <summary>
/// The sky_camera entity: the origin and scale of the 3D skybox section.
/// Source class "sky_camera"; NOT in the landed entity registry (see the
/// file header) — validated by classname, not by registry lookup.
/// </summary>
public sealed class SkyCamera
{
	/// <summary>Source classname.</summary>
	public const string ClassName = "sky_camera";

	/// <summary>Default scale: the Source 1/16 3D-skybox factor.</summary>
	public const double DefaultScale = 16.0;

	/// <summary>Entity id in the VMF document (0 = unassigned).</summary>
	public int Id;

	/// <summary>Targetname, "" when unnamed.</summary>
	public string TargetName = "";

	/// <summary>sky_camera origin: the anchor of the 3D skybox section.</summary>
	public VmfVec Origin;

	/// <summary>World units per skybox unit. Source standard is 16.</summary>
	public double Scale = DefaultScale;

	/// <summary>True when the scale is a finite positive number.</summary>
	public bool HasValidScale => double.IsFinite( Scale ) && Scale > 0;

	/// <summary>
	/// The entity as a VmfEntity: keys "origin" "(x y z)", "scale", and
	/// "targetname" when named — the exact keys SkyboxPreview reads.
	/// </summary>
	public VmfEntity ToVmfEntity()
	{
		var e = new VmfEntity { Id = Id, ClassName = ClassName };
		e.Set( "origin", Origin.ToVmfString() );
		e.Set( "scale", SkyboxWorldspawn.FormatDouble( Scale ) );
		if ( !string.IsNullOrEmpty( TargetName ) ) e.Set( "targetname", TargetName );
		return e;
	}

	/// <summary>
	/// Parse a sky_camera VmfEntity. A bad origin warns
	/// (skybox-invalid-sky-camera-origin) and reads as (0, 0, 0); a bad
	/// scale warns (skybox-invalid-sky-camera-scale) and reads as 16 — the
	/// same fallbacks SkyboxPreview applies. A missing scale key is the
	/// default 16 with NO warning (a camera without a scale key is normal
	/// Hammer output). <paramref name="warnings"/> is required, never null.
	/// </summary>
	public static SkyCamera FromVmfEntity( VmfEntity entity, List<SkyboxWarning> warnings )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( warnings is null ) throw new ArgumentNullException( nameof( warnings ) );
		var camera = new SkyCamera { Id = entity.Id, TargetName = entity.TargetName ?? "" };
		if ( !SkyboxWorldspawn.TryParseVec3( entity.Get( "origin" ), out var origin ) )
		{
			warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidSkyCameraOrigin,
				$"sky_camera origin '{entity.Get( "origin" ) ?? ""}' is not 'x y z'; (0, 0, 0) is used." ) );
			origin = VmfVec.Zero;
		}
		camera.Origin = origin;
		var scaleText = entity.Get( "scale" );
		if ( scaleText is null ) camera.Scale = DefaultScale;
		else if ( !SkyboxWorldspawn.TryParseDouble( scaleText, out var scale ) || !double.IsFinite( scale ) || scale <= 0 )
		{
			warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidSkyCameraScale,
				$"sky_camera scale '{scaleText}' is not a positive number; the default {DefaultScale} is used." ) );
			camera.Scale = DefaultScale;
		}
		else camera.Scale = scale;
		return camera;
	}

	/// <summary>Scale defects as warnings; empty means the camera is valid.</summary>
	public List<SkyboxWarning> Validate()
	{
		var warnings = new List<SkyboxWarning>();
		if ( !HasValidScale )
			warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidSkyCameraScale,
				$"sky_camera scale '{SkyboxWorldspawn.FormatDouble( Scale )}' is not a positive number; the default {DefaultScale} is used." ) );
		return warnings;
	}

	public SkyCamera Clone() => new SkyCamera { Id = Id, TargetName = TargetName, Origin = Origin, Scale = Scale };
}

/// <summary>
/// Structured worldspawn fog (the fogstart/fogend/fogcolor/fogmaxdensity/
/// fogenable keys). Distinct from SkyboxPreview.SkyboxFog, which is the
/// RESOLVED preview fog (env_fog_controller wins over worldspawn); this is
/// the STORED worldspawn data, and ToSkyboxFog projects it onto the
/// preview type with Source "worldspawn". Colour is 0..1 RGB (a is 1),
/// matching SkyboxFog; the VMF form is "r g b" 0-255.
/// </summary>
public sealed class WorldspawnFog
{
	/// <summary>True when any fog key was present on read.</summary>
	public bool Present;

	/// <summary>False only when fogenable was explicitly "0".</summary>
	public bool Enabled = true;

	/// <summary>Near distance where fog starts; 0 when the key was absent.</summary>
	public double Start;

	/// <summary>Far distance where fog is opaque; 0 when the key was absent.</summary>
	public double End;

	/// <summary>Fog colour, 0..1 RGB; white when the key was absent.</summary>
	public Vec4 Color = new Vec4( 1, 1, 1, 1 );

	/// <summary>Maximum fog density, 0..1; 1 when the key was absent.</summary>
	public double MaxDensity = 1.0;

	/// <summary>
	/// Read the worldspawn fog keys. Each unparseable key warns
	/// (skybox-invalid-fog) and keeps its default; a missing key is not a
	/// warning. <paramref name="warnings"/> is required, never null.
	/// </summary>
	public static WorldspawnFog FromWorldspawn( VmfDocument document, List<SkyboxWarning> warnings )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( warnings is null ) throw new ArgumentNullException( nameof( warnings ) );
		var fog = new WorldspawnFog();
		var start = document.WorldspawnGet( SkyboxWorldspawn.FogStartKey );
		var end = document.WorldspawnGet( SkyboxWorldspawn.FogEndKey );
		var color = document.WorldspawnGet( SkyboxWorldspawn.FogColorKey );
		var density = document.WorldspawnGet( SkyboxWorldspawn.FogMaxDensityKey );
		var enable = document.WorldspawnGet( SkyboxWorldspawn.FogEnableKey );
		fog.Present = start != null || end != null || color != null || density != null || enable != null;
		if ( !fog.Present ) return fog;
		if ( enable != null ) fog.Enabled = enable != "0";
		if ( start != null )
		{
			if ( SkyboxWorldspawn.TryParseDouble( start, out var s ) && double.IsFinite( s ) ) fog.Start = s;
			else warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidFog, $"Fog start '{start}' is not a number; 0 is used." ) );
		}
		if ( end != null )
		{
			if ( SkyboxWorldspawn.TryParseDouble( end, out var e ) && double.IsFinite( e ) ) fog.End = e;
			else warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidFog, $"Fog end '{end}' is not a number; 0 is used." ) );
		}
		if ( color != null )
		{
			if ( !SkyboxWorldspawn.TryParseColour255( color, out var c ) )
				warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidFog, $"Fog colour '{color}' is not 'r g b' 0-255; white is used." ) );
			else fog.Color = c;
		}
		if ( density != null )
		{
			if ( SkyboxWorldspawn.TryParseDouble( density, out var d ) && double.IsFinite( d ) ) fog.MaxDensity = d;
			else warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidFog, $"Fog max density '{density}' is not a number; 1 is used." ) );
		}
		return fog;
	}

	/// <summary>
	/// Write the fog keys onto a document's worldspawn. When Present is
	/// false every fog key is REMOVED (ApplyTo owns the keys — see the file
	/// header); when present all five keys are written, including fogenable
	/// "1"/"0" (see the SkyboxPreview fogenable note in the file header).
	/// </summary>
	public void ApplyToWorldspawn( VmfDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( !Present )
		{
			SkyboxWorldspawn.RemoveWorldspawnKey( document, SkyboxWorldspawn.FogStartKey );
			SkyboxWorldspawn.RemoveWorldspawnKey( document, SkyboxWorldspawn.FogEndKey );
			SkyboxWorldspawn.RemoveWorldspawnKey( document, SkyboxWorldspawn.FogColorKey );
			SkyboxWorldspawn.RemoveWorldspawnKey( document, SkyboxWorldspawn.FogMaxDensityKey );
			SkyboxWorldspawn.RemoveWorldspawnKey( document, SkyboxWorldspawn.FogEnableKey );
			return;
		}
		document.WorldspawnSet( SkyboxWorldspawn.FogStartKey, SkyboxWorldspawn.FormatDouble( Start ) );
		document.WorldspawnSet( SkyboxWorldspawn.FogEndKey, SkyboxWorldspawn.FormatDouble( End ) );
		document.WorldspawnSet( SkyboxWorldspawn.FogColorKey, SkyboxWorldspawn.FormatColour255( Color ) );
		document.WorldspawnSet( SkyboxWorldspawn.FogMaxDensityKey, SkyboxWorldspawn.FormatDouble( MaxDensity ) );
		document.WorldspawnSet( SkyboxWorldspawn.FogEnableKey, Enabled ? "1" : "0" );
	}

	/// <summary>Fog-range defects as warnings; empty means the fog is valid (or absent).</summary>
	public List<SkyboxWarning> Validate()
	{
		var warnings = new List<SkyboxWarning>();
		if ( Present && Enabled && Start > 0 && End > 0 && End <= Start )
			warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidFog,
				$"Fog end ({SkyboxWorldspawn.FormatDouble( End )}) must be greater than fog start ({SkyboxWorldspawn.FormatDouble( Start )}); the fog has no visible range." ) );
		return warnings;
	}

	/// <summary>Project onto the preview fog type (Source "worldspawn" when present).</summary>
	public SkyboxFog ToSkyboxFog() => new SkyboxFog
	{
		Enabled = Present && Enabled,
		Start = (float)Start,
		End = (float)End,
		Color = Color,
		MaxDensity = (float)MaxDensity,
		Source = Present ? "worldspawn" : "none",
	};

	public WorldspawnFog Clone() => new WorldspawnFog
	{
		Present = Present, Enabled = Enabled, Start = Start, End = End, Color = Color, MaxDensity = MaxDensity,
	};
}

/// <summary>
/// The 3D skybox section's contribution to the baked scene: the section
/// brushes scaled UP to world size about the bake anchor (the engine
/// direction — the inverse of the preview's 1/16 miniature), plus the 2D
/// sky name, the camera the anchor came from, and the fog. The bake
/// caller merges ToMapBakeBrushes into its MapBakeDocument brush list;
/// MapBakeDocument itself has no skybox slot (that is the bake ticket's /
/// integrator's seam, not a field this file can invent).
/// </summary>
public sealed class SkyboxBakeData
{
	/// <summary>2D sky name from worldspawn "skyname"; "" when the map names no sky.</summary>
	public string SkyboxName = "";

	/// <summary>True when a sky_camera entity exists in the source data.</summary>
	public bool HasSkyCamera;

	/// <summary>The anchor the world-scale transform used (camera origin when a camera exists, else the section origin).</summary>
	public VmfVec AnchorOrigin;

	/// <summary>The scale the world-scale transform used.</summary>
	public double AnchorScale = SkyCamera.DefaultScale;

	/// <summary>Where the anchor came from: "sky_camera" or "section".</summary>
	public string AnchorSource = "section";

	/// <summary>Structured worldspawn fog carried into the bake.</summary>
	public WorldspawnFog Fog = new WorldspawnFog();

	/// <summary>Section brushes at WORLD scale (p' = anchor + (p - anchor) * scale).</summary>
	public List<VmfBrush> WorldBrushes = new List<VmfBrush>();

	/// <summary>Section brushes in the source data (before scaling).</summary>
	public int SectionBrushCount;

	/// <summary>Source section brushes that could not be baked (fewer than 4 faces, or a degenerate plane).</summary>
	public int SkippedBrushes;

	/// <summary>Axis-aligned bounds of the world-scale brushes; Zero when WorldBrushes is empty.</summary>
	public VmfVec BoundsMin;

	/// <summary>Axis-aligned bounds of the world-scale brushes; Zero when WorldBrushes is empty.</summary>
	public VmfVec BoundsMax;

	/// <summary>True when WorldBrushes is non-empty (BoundsMin/Max are meaningful).</summary>
	public bool HasBounds => WorldBrushes.Count > 0;

	/// <summary>Warnings raised while building this bake data (bad scale, skipped brushes); empty is a clean build.</summary>
	public List<SkyboxWarning> Warnings = new List<SkyboxWarning>();

	/// <summary>Total faces across the world-scale brushes.</summary>
	public int SectionFaceCount
	{
		get
		{
			var n = 0;
			foreach ( var b in WorldBrushes ) n += b.Faces.Count;
			return n;
		}
	}

	/// <summary>
	/// The world-scale brushes as MapBakeBrushes, ready to append to a
	/// MapBakeDocument brush list for MapBakeExport.Bake. Faces with an
	/// empty material become nodraw (FaceMaterial.NoDraw — the unseen-face
	/// rule), so they stay in the brush but contribute no baked faces.
	/// Brushes with fewer than 4 convertible faces are skipped and counted
	/// in <paramref name="skipped"/>.
	/// </summary>
	public List<MapBakeBrush> ToMapBakeBrushes( out int skipped )
	{
		skipped = 0;
		var outBrushes = new List<MapBakeBrush>();
		foreach ( var brush in WorldBrushes )
		{
			if ( brush is null ) { skipped++; continue; }
			var normals = new List<Vec3>();
			var distances = new List<float>();
			var materials = new List<FaceMaterial>();
			foreach ( var face in brush.Faces )
			{
				if ( face is null ) continue;
				var (n, d) = face.ToPlane();
				if ( n.Equals( VmfVec.Zero ) ) continue;
				normals.Add( new Vec3( (float)n.X, (float)n.Y, (float)n.Z ) );
				distances.Add( (float)d );
				materials.Add( string.IsNullOrWhiteSpace( face.Material ) ? FaceMaterial.NoDraw : FaceMaterial.ForMaterial( face.Material ) );
			}
			if ( normals.Count < 4 ) { skipped++; continue; }
			outBrushes.Add( MapBakeBrush.FromPlanes( $"skybox-{brush.Id}", normals, distances, materials ) );
		}
		return outBrushes;
	}
}

/// <summary>
/// The skybox/worldspawn data for one map: worldspawn properties (2D sky,
/// chapter/title, detail props, fog preset), structured worldspawn fog,
/// sky_camera entities, and the 3D skybox section (brushes in SKYBOX space
/// + their own origin and scale). The transfer form is VmfDocument; the
/// MapDocument adapter is the integrator's seam (see the file header).
/// </summary>
public sealed class SkyboxWorldspawn
{
	// ---- Worldspawn key mapping (this ticket defines it) ----
	public const string SkyNameKey = "skyname";
	public const string ChapterKey = "chaptertitle";
	public const string TitleKey = "geppetto_title";
	public const string DetailVbspKey = "detailvbsp";
	public const string DetailMaterialKey = "detailmaterial";
	public const string FogPresetKey = "fog";
	public const string FogStartKey = "fogstart";
	public const string FogEndKey = "fogend";
	public const string FogColorKey = "fogcolor";
	public const string FogMaxDensityKey = "fogmaxdensity";
	public const string FogEnableKey = "fogenable";

	/// <summary>Default detail prop file (Source worldspawn default).</summary>
	public const string DefaultDetailVbsp = "detail.vbsp";

	/// <summary>Default detail sprite material (Source worldspawn default).</summary>
	public const string DefaultDetailMaterial = "detail/detailsprites";

	/// <summary>Default 3D-skybox scale (the Source 1/16 factor).</summary>
	public const double DefaultSectionScale = 16.0;

	// ---- New warning codes (shared ones live on SkyboxPreview) ----
	/// <summary>Section origin/scale disagree with the sky_camera.</summary>
	public const string SectionCameraMismatch = "skybox-section-camera-mismatch";

	/// <summary>3D section present but the map names no 2D sky.</summary>
	public const string EmptySkyName = "skybox-empty-sky-name";

	/// <summary>Section scale missing/invalid/&lt;=0.</summary>
	public const string InvalidSectionScale = "skybox-invalid-section-scale";

	/// <summary>
	/// String id of the map this data belongs to (the PanelLayout
	/// LayoutDocument.MapId pattern); "" = unbound. MapDocument does not
	/// exist yet — the integrator binds this to MapDocument.Id.
	/// </summary>
	public string MapId = "";

	/// <summary>
	/// Worldspawn properties (StarterTemplates-owned type): 2D skybox name,
	/// chapter, title, detail-props flag, fog preset name. Never null on a
	/// constructed aggregate; Validate tolerates a null defensively.
	/// </summary>
	public WorldspawnProperties Worldspawn = new WorldspawnProperties();

	/// <summary>Detail prop file, written as "detailvbsp" when DetailProps is true.</summary>
	public string DetailVbsp = DefaultDetailVbsp;

	/// <summary>Detail sprite material, written as "detailmaterial" when DetailProps is true.</summary>
	public string DetailMaterial = DefaultDetailMaterial;

	/// <summary>Structured worldspawn fog (the fog* keys).</summary>
	public WorldspawnFog FogParams = new WorldspawnFog();

	/// <summary>All sky_camera entities (first is primary); empty = no camera.</summary>
	public List<SkyCamera> Cameras = new List<SkyCamera>();

	/// <summary>First sky_camera, or null when the map has none.</summary>
	public SkyCamera Camera => Cameras.Count > 0 ? Cameras[0] : null;

	/// <summary>True when at least one sky_camera exists.</summary>
	public bool HasSkyCamera => Cameras.Count > 0;

	/// <summary>
	/// Anchor of the 3D section in skybox space; the sky_camera sits here
	/// when one exists. Independent of the camera so a section can be
	/// authored (and baked) before its camera is placed; Validate warns
	/// when it disagrees with the camera.
	/// </summary>
	public VmfVec SectionOrigin;

	/// <summary>World units per skybox unit for the section. Source standard is 16.</summary>
	public double SectionScale = DefaultSectionScale;

	/// <summary>
	/// 3D skybox section brushes in SKYBOX space (distant mountains/city).
	/// They are NOT world brushes and never take part in leak/seal checks.
	/// </summary>
	public List<VmfBrush> SectionBrushes = new List<VmfBrush>();

	/// <summary>True when the map has a 3D skybox section.</summary>
	public bool Has3DSection => SectionBrushes.Count > 0;

	/// <summary>True when the section is 3D (same as Has3DSection).</summary>
	public bool Is3D => Has3DSection;

	/// <summary>True when worldspawn names a 2D sky.</summary>
	public bool Has2DSky => !string.IsNullOrEmpty( Worldspawn?.SkyboxName );

	/// <summary>
	/// True when "sky_camera" is a known class in the given registry.
	/// Expected FALSE against the landed core-source1.json until the
	/// integrator adds the class (see the file header) — a false here is a
	/// registry gap, not a data defect, and never fails Validate.
	/// </summary>
	public static bool IsSkyCameraInRegistry( EntityRegistry registry ) =>
		registry != null && registry.Contains( SkyCamera.ClassName );

	// ---- VMF round-trip ----

	/// <summary>
	/// Read skybox/worldspawn data from a VmfDocument. Returns the
	/// aggregate plus the warnings raised while parsing (bad camera
	/// origin/scale, bad fog values, unbuildable section brushes).
	/// The document is never mutated; section brushes are cloned.
	/// Section identification follows the visgroup-then-spatial rule in
	/// the file header.
	/// </summary>
	public static (SkyboxWorldspawn Skybox, List<SkyboxWarning> Warnings) FromVmfDocument( VmfDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var warnings = new List<SkyboxWarning>();
		var skybox = new SkyboxWorldspawn();

		skybox.Worldspawn.SkyboxName = document.WorldspawnGet( SkyNameKey ) ?? "";
		skybox.Worldspawn.Chapter = document.WorldspawnGet( ChapterKey ) ?? "";
		skybox.Worldspawn.Title = document.WorldspawnGet( TitleKey ) ?? "";
		skybox.Worldspawn.Fog = document.WorldspawnGet( FogPresetKey ) ?? "";
		var detailVbsp = document.WorldspawnGet( DetailVbspKey );
		skybox.Worldspawn.DetailProps = !string.IsNullOrEmpty( detailVbsp );
		if ( skybox.Worldspawn.DetailProps ) skybox.DetailVbsp = detailVbsp;
		var detailMaterial = document.WorldspawnGet( DetailMaterialKey );
		if ( !string.IsNullOrEmpty( detailMaterial ) ) skybox.DetailMaterial = detailMaterial;
		skybox.FogParams = WorldspawnFog.FromWorldspawn( document, warnings );

		foreach ( var entity in document.Entities )
		{
			if ( entity is null ) continue;
			if ( string.Equals( entity.ClassName, SkyCamera.ClassName, StringComparison.OrdinalIgnoreCase ) )
				skybox.Cameras.Add( SkyCamera.FromVmfEntity( entity, warnings ) );
		}

		skybox.SectionOrigin = skybox.HasSkyCamera ? skybox.Camera.Origin : VmfVec.Zero;
		skybox.SectionScale = skybox.HasSkyCamera ? skybox.Camera.Scale : DefaultSectionScale;
		foreach ( var brush in FindSectionBrushes( document, skybox.Camera, warnings ) )
			skybox.SectionBrushes.Add( brush.Clone() );
		return (skybox, warnings);
	}

	/// <summary>
	/// Write this aggregate onto a VmfDocument (see the ownership rule in
	/// the file header): worldspawn keys are set or removed, sky_camera
	/// entities are upserted by id (others removed), and section brushes
	/// are upserted by id into the "3D Skybox" visgroup (other visgroup
	/// members removed). Everything outside the skybox slice is untouched.
	/// </summary>
	public void ApplyToVmfDocument( VmfDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );

		// Worldspawn keys: set when the aggregate has a value, remove when not.
		if ( !string.IsNullOrEmpty( Worldspawn?.SkyboxName ) ) document.WorldspawnSet( SkyNameKey, Worldspawn.SkyboxName );
		else RemoveWorldspawnKey( document, SkyNameKey );
		if ( !string.IsNullOrEmpty( Worldspawn?.Chapter ) ) document.WorldspawnSet( ChapterKey, Worldspawn.Chapter );
		else RemoveWorldspawnKey( document, ChapterKey );
		if ( !string.IsNullOrEmpty( Worldspawn?.Title ) ) document.WorldspawnSet( TitleKey, Worldspawn.Title );
		else RemoveWorldspawnKey( document, TitleKey );
		if ( Worldspawn != null && Worldspawn.DetailProps && !string.IsNullOrEmpty( DetailVbsp ) )
			document.WorldspawnSet( DetailVbspKey, DetailVbsp );
		else RemoveWorldspawnKey( document, DetailVbspKey );
		if ( Worldspawn != null && Worldspawn.DetailProps && !string.IsNullOrEmpty( DetailMaterial ) )
			document.WorldspawnSet( DetailMaterialKey, DetailMaterial );
		else RemoveWorldspawnKey( document, DetailMaterialKey );
		if ( !string.IsNullOrEmpty( Worldspawn?.Fog ) ) document.WorldspawnSet( FogPresetKey, Worldspawn.Fog );
		else RemoveWorldspawnKey( document, FogPresetKey );
		( FogParams ?? new WorldspawnFog() ).ApplyToWorldspawn( document );

		// sky_camera entities: upsert by id, remove the ones this aggregate does not contain.
		var wantedCameraIds = new HashSet<int>();
		foreach ( var camera in Cameras )
		{
			if ( camera is null ) continue;
			var entity = camera.ToVmfEntity();
			if ( entity.Id == 0 || document.Entities.Any( e => e.Id == entity.Id && !string.Equals( e.ClassName, SkyCamera.ClassName, StringComparison.OrdinalIgnoreCase ) ) )
				entity.Id = NextEntityId( document );
			wantedCameraIds.Add( entity.Id );
			var at = document.Entities.FindIndex( e => e.Id == entity.Id );
			if ( at >= 0 ) document.Entities[at] = entity;
			else document.Entities.Add( entity );
		}
		document.Entities.RemoveAll( e => string.Equals( e.ClassName, SkyCamera.ClassName, StringComparison.OrdinalIgnoreCase ) && !wantedCameraIds.Contains( e.Id ) );

		// Section brushes: upsert by id into the "3D Skybox" visgroup, remove other members.
		// A brush keeps its id when that id is free or belongs to an existing
		// section member (the re-apply case); it is minted a fresh id only
		// when the id is 0, already claimed by an earlier brush in this
		// apply, or belongs to a non-section brush (never clobber the map).
		var existingSectionVg = FindSectionVisGroupId( document );
		var sectionVisGroupId = SectionBrushes.Count > 0 ? EnsureSectionVisGroup( document ) : existingSectionVg;
		var existingSectionIds = new HashSet<int>();
		if ( existingSectionVg >= 0 )
			foreach ( var b in document.AllBrushes() )
				if ( b.VisGroupId == existingSectionVg ) existingSectionIds.Add( b.Id );
		var wantedBrushIds = new HashSet<int>();
		var nextBrushId = NextBrushId( document );
		foreach ( var brush in SectionBrushes )
		{
			if ( brush is null ) continue;
			var copy = brush.Clone();
			if ( copy.Id == 0 || wantedBrushIds.Contains( copy.Id )
				|| document.AllBrushes().Any( b => b.Id == copy.Id && !existingSectionIds.Contains( b.Id ) ) )
				copy.Id = nextBrushId++;
			wantedBrushIds.Add( copy.Id );
			RemoveBrushById( document, copy.Id );
			copy.VisGroupId = sectionVisGroupId;
			document.WorldBrushes.Add( copy );
		}
		if ( existingSectionVg >= 0 )
		{
			document.WorldBrushes.RemoveAll( b => b.VisGroupId == existingSectionVg && !wantedBrushIds.Contains( b.Id ) );
			foreach ( var entity in document.Entities )
				entity.Brushes.RemoveAll( b => b.VisGroupId == existingSectionVg && !wantedBrushIds.Contains( b.Id ) );
		}
	}

	/// <summary>
	/// All defects as named warnings; empty means the data is clean.
	/// Never throws for data issues (a null document member reads as its
	/// default); a null aggregate itself is a programmer error and throws.
	/// </summary>
	public List<SkyboxWarning> Validate()
	{
		var warnings = new List<SkyboxWarning>();
		if ( Cameras.Count > 1 )
			warnings.Add( new SkyboxWarning( SkyboxPreview.MultipleSkyCameras,
				$"The map has {Cameras.Count} sky_camera entities; the first (targetname '{Cameras[0]?.TargetName ?? ""}') is used." ) );
		foreach ( var camera in Cameras )
		{
			if ( camera is null ) continue;
			warnings.AddRange( camera.Validate() );
		}
		if ( Has3DSection && !HasSkyCamera )
			warnings.Add( new SkyboxWarning( SkyboxPreview.MissingSkyCamera,
				$"The map has a 3D skybox section ({SectionBrushes.Count} brush(es)) but no sky_camera entity, so the section has no origin or scale. Add a sky_camera to use the section." ) );
		if ( !double.IsFinite( SectionScale ) || SectionScale <= 0 )
			warnings.Add( new SkyboxWarning( InvalidSectionScale,
				$"3D skybox section scale '{FormatDouble( SectionScale )}' is not a positive number; the default {DefaultSectionScale} is used." ) );
		if ( Has3DSection && HasSkyCamera )
		{
			var camera = Camera;
			if ( !SectionOrigin.ApproxEquals( camera.Origin, 1e-6 ) || Math.Abs( SectionScale - camera.Scale ) > 1e-9 )
				warnings.Add( new SkyboxWarning( SectionCameraMismatch,
					$"The 3D skybox section anchor ({SectionOrigin.ToVmfString()} at scale {FormatDouble( SectionScale )}) disagrees with the sky_camera ({camera.Origin.ToVmfString()} at scale {FormatDouble( camera.Scale )}); the engine follows the sky_camera." ) );
		}
		if ( Has3DSection && !Has2DSky )
			warnings.Add( new SkyboxWarning( EmptySkyName,
				"The map has a 3D skybox section but worldspawn names no 2D skybox (skyname is empty); the section has no sky backdrop." ) );
		foreach ( var brush in SectionBrushes )
		{
			if ( brush is null ) continue;
			if ( brush.Faces.Count < 4 || brush.Faces.Any( f => f is null || f.IsDegeneratePlane ) )
				warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidSectionBrush,
					$"3D skybox brush #{brush.Id} is not a closed convex solid, so it is left out of the preview and the bake." ) );
		}
		if ( FogParams != null ) warnings.AddRange( FogParams.Validate() );
		return warnings;
	}

	/// <summary>
	/// The bake contribution: section brushes scaled UP to world size about
	/// the bake anchor (sky_camera origin when a camera exists, else the
	/// section origin), plus the 2D sky name and fog. A bad anchor scale
	/// falls back to the default 16 with a warning on the result; brushes
	/// that cannot bake are counted in SkippedBrushes with a warning.
	/// </summary>
	public SkyboxBakeData ToBakeData()
	{
		var bake = new SkyboxBakeData
		{
			SkyboxName = Worldspawn?.SkyboxName ?? "",
			HasSkyCamera = HasSkyCamera,
			Fog = ( FogParams ?? new WorldspawnFog() ).Clone(),
			SectionBrushCount = SectionBrushes.Count,
		};
		var useCamera = HasSkyCamera;
		var anchor = useCamera ? Camera.Origin : SectionOrigin;
		var scale = useCamera ? Camera.Scale : SectionScale;
		if ( !double.IsFinite( scale ) || scale <= 0 )
		{
			bake.Warnings.Add( new SkyboxWarning( InvalidSectionScale,
				$"Bake anchor scale '{FormatDouble( scale )}' is not a positive number; the default {DefaultSectionScale} is used." ) );
			scale = DefaultSectionScale;
		}
		bake.AnchorOrigin = anchor;
		bake.AnchorScale = scale;
		bake.AnchorSource = useCamera ? "sky_camera" : "section";
		var min = new VmfVec( double.MaxValue, double.MaxValue, double.MaxValue );
		var max = new VmfVec( double.MinValue, double.MinValue, double.MinValue );
		foreach ( var brush in SectionBrushes )
		{
			if ( brush is null || brush.Faces.Count < 4 || brush.Faces.Any( f => f is null || f.IsDegeneratePlane ) )
			{
				bake.SkippedBrushes++;
				if ( brush != null )
					bake.Warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidSectionBrush,
						$"3D skybox brush #{brush.Id} is not a closed convex solid, so it is left out of the bake." ) );
				continue;
			}
			var world = ScaleBrush( brush, anchor, scale );
			foreach ( var face in world.Faces )
				foreach ( var p in face.PlanePoints )
				{
					min = new VmfVec( Math.Min( min.X, p.X ), Math.Min( min.Y, p.Y ), Math.Min( min.Z, p.Z ) );
					max = new VmfVec( Math.Max( max.X, p.X ), Math.Max( max.Y, p.Y ), Math.Max( max.Z, p.Z ) );
				}
			bake.WorldBrushes.Add( world );
		}
		if ( bake.WorldBrushes.Count > 0 ) { bake.BoundsMin = min; bake.BoundsMax = max; }
		return bake;
	}

	/// <summary>
	/// Adopt a starter template's skybox data: worldspawn properties,
	/// sky_camera entities (scale defaults to 16 when the template sets
	/// no scale key — OutdoorDisplacement sets none), and the 3D section
	/// (origin/scale/brushes; brush faces take their first three polygon
	/// vertices as plane points, the StarterTemplates.ToVmf convention).
	/// MapId is bound to the template document's name. A template with no
	/// section yields an aggregate with no section (2D sky only).
	/// </summary>
	public static SkyboxWorldspawn FromStarterMap( StarterMap map )
	{
		if ( map is null ) throw new ArgumentNullException( nameof( map ) );
		var skybox = new SkyboxWorldspawn { MapId = map.Document?.Name ?? "" };
		if ( map.Worldspawn != null )
		{
			skybox.Worldspawn = new WorldspawnProperties
			{
				SkyboxName = map.Worldspawn.SkyboxName ?? "",
				Chapter = map.Worldspawn.Chapter ?? "",
				Title = map.Worldspawn.Title ?? "",
				DetailProps = map.Worldspawn.DetailProps,
				Fog = map.Worldspawn.Fog ?? "",
			};
		}
		if ( map.Document != null )
		{
			foreach ( var entity in map.Document.Entities )
			{
				if ( entity is null ) continue;
				if ( !string.Equals( entity.ClassName, SkyCamera.ClassName, StringComparison.OrdinalIgnoreCase ) ) continue;
				var scale = SkyCamera.DefaultScale;
				var scaleText = entity.GetKeyValue( "scale" );
				if ( !string.IsNullOrEmpty( scaleText ) && TryParseDouble( scaleText, out var parsed ) && double.IsFinite( parsed ) && parsed > 0 )
					scale = parsed;
				skybox.Cameras.Add( new SkyCamera
				{
					Id = entity.Id,
					TargetName = entity.TargetName ?? "",
					Origin = new VmfVec( entity.Origin.x, entity.Origin.y, entity.Origin.z ),
					Scale = scale,
				} );
			}
		}
		var section = map.Skybox?.Section3D;
		if ( section != null )
		{
			skybox.SectionOrigin = new VmfVec( section.Origin.x, section.Origin.y, section.Origin.z );
			skybox.SectionScale = double.IsFinite( section.Scale ) && section.Scale > 0 ? section.Scale : DefaultSectionScale;
			var faceId = 0;
			foreach ( var brush in section.Brushes )
			{
				if ( brush is null ) continue;
				var vmfBrush = new VmfBrush { Id = brush.Id };
				foreach ( var face in brush.Faces )
				{
					if ( face is null || face.Vertices.Count < 3 )
						throw new InvalidOperationException(
							$"Cannot adopt starter section brush {brush.Id}: a face has fewer than 3 polygon vertices." );
					vmfBrush.Faces.Add( new VmfFace
					{
						Id = ++faceId,
						Material = face.Material ?? "",
						PlanePoints = new[]
						{
							new VmfVec( face.Vertices[0].x, face.Vertices[0].y, face.Vertices[0].z ),
							new VmfVec( face.Vertices[1].x, face.Vertices[1].y, face.Vertices[1].z ),
							new VmfVec( face.Vertices[2].x, face.Vertices[2].y, face.Vertices[2].z ),
						},
					} );
				}
				skybox.SectionBrushes.Add( vmfBrush );
			}
		}
		return skybox;
	}

	public SkyboxWorldspawn Clone()
	{
		var copy = new SkyboxWorldspawn
		{
			MapId = MapId,
			Worldspawn = Worldspawn is null ? new WorldspawnProperties() : new WorldspawnProperties
			{
				SkyboxName = Worldspawn.SkyboxName ?? "",
				Chapter = Worldspawn.Chapter ?? "",
				Title = Worldspawn.Title ?? "",
				DetailProps = Worldspawn.DetailProps,
				Fog = Worldspawn.Fog ?? "",
			},
			DetailVbsp = DetailVbsp,
			DetailMaterial = DetailMaterial,
			FogParams = ( FogParams ?? new WorldspawnFog() ).Clone(),
			SectionOrigin = SectionOrigin,
			SectionScale = SectionScale,
		};
		foreach ( var camera in Cameras ) if ( camera != null ) copy.Cameras.Add( camera.Clone() );
		foreach ( var brush in SectionBrushes ) if ( brush != null ) copy.SectionBrushes.Add( brush.Clone() );
		return copy;
	}

	// ---- Section identification (visgroup rule, then spatial fallback) ----

	static List<VmfBrush> FindSectionBrushes( VmfDocument document, SkyCamera camera, List<SkyboxWarning> warnings )
	{
		var found = new List<VmfBrush>();
		var sectionIds = new HashSet<int>();
		foreach ( var vg in document.VisGroups )
			if ( string.Equals( vg.Name, SkyboxPreview.SectionVisGroupName, StringComparison.OrdinalIgnoreCase ) )
				sectionIds.Add( vg.Id );

		if ( sectionIds.Count > 0 )
		{
			foreach ( var brush in document.AllBrushes() )
			{
				if ( brush is null || !sectionIds.Contains( brush.VisGroupId ) ) continue;
				if ( !IsBuildableSectionBrush( brush ) )
				{
					warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidSectionBrush,
						$"3D skybox brush #{brush.Id} is not a closed convex solid, so it is left out of the section." ) );
					continue;
				}
				found.Add( brush );
			}
			if ( found.Count > 0 ) return found;
			// A "3D Skybox" visgroup exists but holds no buildable brush:
			// fall through to the spatial rule rather than reporting an
			// empty section while section geometry sits by the camera
			// (the same fall-through SkyboxPreview applies).
		}

		if ( camera is null ) return found;
		foreach ( var brush in document.AllBrushes() )
		{
			if ( brush is null || !IsBuildableSectionBrush( brush ) ) continue;
			var centroid = BrushCentroid( brush );
			if ( ( centroid - camera.Origin ).Length <= SkyboxPreview.SectionRadius )
				found.Add( brush );
		}
		return found;
	}

	static bool IsBuildableSectionBrush( VmfBrush brush ) =>
		brush.Faces.Count >= 4 && brush.Faces.All( f => f != null && !f.IsDegeneratePlane );

	/// <summary>Mean of the face plane points (location only, not a volume centroid).</summary>
	static VmfVec BrushCentroid( VmfBrush brush )
	{
		var sum = VmfVec.Zero;
		var count = 0;
		foreach ( var face in brush.Faces )
		{
			if ( face is null ) continue;
			foreach ( var p in face.PlanePoints ) { sum = sum + p; count++; }
		}
		return count == 0 ? VmfVec.Zero : sum * ( 1.0 / count );
	}

	// ---- ApplyTo helpers ----

	static int NextEntityId( VmfDocument document )
	{
		var max = 0;
		foreach ( var e in document.Entities ) if ( e.Id > max ) max = e.Id;
		return max + 1;
	}

	static int NextBrushId( VmfDocument document )
	{
		var max = 0;
		foreach ( var b in document.AllBrushes() ) if ( b.Id > max ) max = b.Id;
		return max + 1;
	}

	static int FindSectionVisGroupId( VmfDocument document )
	{
		foreach ( var vg in document.VisGroups )
			if ( string.Equals( vg.Name, SkyboxPreview.SectionVisGroupName, StringComparison.OrdinalIgnoreCase ) )
				return vg.Id;
		return -1;
	}

	static int EnsureSectionVisGroup( VmfDocument document )
	{
		var existing = FindSectionVisGroupId( document );
		if ( existing >= 0 ) return existing;
		var id = 1;
		foreach ( var vg in document.VisGroups ) if ( vg.Id >= id ) id = vg.Id + 1;
		document.VisGroups.Add( new VmfVisGroup { Id = id, Name = SkyboxPreview.SectionVisGroupName, Color = "0 128 255" } );
		return id;
	}

	static void RemoveBrushById( VmfDocument document, int id )
	{
		document.WorldBrushes.RemoveAll( b => b.Id == id );
		foreach ( var entity in document.Entities ) entity.Brushes.RemoveAll( b => b.Id == id );
	}

	internal static void RemoveWorldspawnKey( VmfDocument document, string key ) =>
		document.WorldspawnKeyValues.RemoveAll( kv => kv.Key == key );

	/// <summary>Scale one brush about an anchor: p' = anchor + (p - anchor) * scale. Plane points (and stored vertices) transform; the face planes follow because VmfFace derives them from the points.</summary>
	static VmfBrush ScaleBrush( VmfBrush brush, VmfVec anchor, double scale )
	{
		var copy = brush.Clone();
		foreach ( var face in copy.Faces )
		{
			for ( var i = 0; i < face.PlanePoints.Length; i++ )
				face.PlanePoints[i] = ScalePoint( face.PlanePoints[i], anchor, scale );
			for ( var i = 0; i < face.StoredVertices.Count; i++ )
				face.StoredVertices[i] = ScalePoint( face.StoredVertices[i], anchor, scale );
		}
		return copy;
	}

	static VmfVec ScalePoint( VmfVec p, VmfVec anchor, double scale ) =>
		anchor + ( p - anchor ) * scale;

	// ---- Number / colour parsing (invariant culture; mirrors SkyboxPreview) ----

	/// <summary>Invariant-culture double; false on any unparseable text.</summary>
	internal static bool TryParseDouble( string text, out double value )
	{
		value = 0;
		return !string.IsNullOrWhiteSpace( text )
			&& double.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out value );
	}

	/// <summary>Parse "x y z" (parens/brackets tolerated); false on any unparseable component.</summary>
	internal static bool TryParseVec3( string text, out VmfVec v )
	{
		v = VmfVec.Zero;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var t = text.Trim().TrimStart( '(', '[' ).TrimEnd( ')', ']' ).Trim();
		var parts = t.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		if ( !TryParseDouble( parts[0], out var x ) || !TryParseDouble( parts[1], out var y ) || !TryParseDouble( parts[2], out var z ) ) return false;
		v = new VmfVec( x, y, z );
		return true;
	}

	/// <summary>Parse "r g b" with components in 0-255 into a 0..1 RGB colour; false on bad input.</summary>
	internal static bool TryParseColour255( string text, out Vec4 color )
	{
		color = new Vec4( 1, 1, 1, 1 );
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		if ( !TryParseDouble( parts[0], out var r ) || !TryParseDouble( parts[1], out var g ) || !TryParseDouble( parts[2], out var b ) ) return false;
		color = new Vec4( Clamp01( r / 255.0 ), Clamp01( g / 255.0 ), Clamp01( b / 255.0 ), 1f );
		return true;
	}

	static float Clamp01( double v ) => v < 0 ? 0f : v > 1 ? 1f : (float)v;

	/// <summary>Round-trip ("R") invariant formatting; NaN/Infinity become "0" (the VmfIo convention).</summary>
	internal static string FormatDouble( double value )
	{
		if ( double.IsNaN( value ) || double.IsInfinity( value ) ) return "0";
		return value.ToString( "R", CultureInfo.InvariantCulture );
	}

	/// <summary>Format a 0..1 RGB colour as "r g b" 0-255 integers.</summary>
	internal static string FormatColour255( Vec4 color )
	{
		int C( float c ) => (int)Math.Round( Math.Max( 0f, Math.Min( 1f, c ) ) * 255f );
		return $"{C( color.x )} {C( color.y )} {C( color.z )}";
	}
}
