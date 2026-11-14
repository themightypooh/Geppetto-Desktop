using System;
using System.Collections.Generic;
using System.Globalization;

namespace Effigy.Map;

// Hammer Desktop P2: 3D/2D skybox preview scene data (sky_camera evaluation).
// Board: hammer-preview-skybox-scene (Geppetto Desktop, Hammer P2).
//
// WHAT IT DOES. Given a map document, build the preview scene a future
// viewport / AI caller can render: the 3D skybox section's geometry scaled
// to 1/16 about the sky_camera origin, the 2D sky name from worldspawn, the
// fog parameters, and the sun lighting resolved from the lighting entities.
// Backend / data-model / API only: no viewport code, no Desktop UI, no
// Hammer/VBSP shell-out. Units are the kernel's inches, Z-up.
//
// DEPENDENCY STATUS (2026-11-13, verified against the board + the tree):
//   - hammer-skybox-2d-3d-worldspawn (pass 1: stores skybox/worldspawn
//     data) is todo / unclaimed; hammer-lighting-environment-sun-fog is
//     todo / unclaimed; hammer-map-document-model (MapDocument) is
//     in-progress but kernel/Effigy/Map/MapDocument.cs does not exist on
//     disk yet, and neither does any sky_camera / worldspawn skybox type.
//   Per the ticket ("implement against the spec exactly as filed and report
//   any mismatch"), this file therefore builds on the transfer model that
//   DOES exist — VmfDocument (VmfIo.cs): worldspawn keyvalues, entities
//   with classname / origin / keyvalues, and brushes as face planes. The
//   filed entry point is BuildPreview(document) -> PreviewScene; the
//   document here is the VmfDocument. When MapDocument lands, the
//   integrator adds ONE thin adapter (MapDocument -> VmfDocument
//   projection, or a BuildPreview(MapDocument) overload that projects onto
//   this same evaluation) without changing this file's rules. No type in
//   this file may be redefined by a sibling (that would be a duplicate-type
//   compile break — reconcile, do not fork). This follows the same pattern
//   as MapCsg (builds on PolyMesh while MapBrush is missing) and
//   IssueEngine (own document snapshot while MapDocument is missing).
//
// FILED-SPEC MAPPING (adapter seams, no invented API):
//   worldspawn "skyname"                       <-> pass-1 worldspawn skybox name
//   sky_camera entity (origin + scale)         <-> pass-1 sky_camera entity
//   3D skybox section brushes                  <-> pass-1 3D skybox section
//     ("scaled geometry + its own origin")
//   env_fog_controller / worldspawn fog keys   <-> pass-1 worldspawn fog +
//     lighting-ticket env_fog_controller
//   light_environment + env_sun                <-> lighting-ticket sun pair
//     (sun without env_sun warns, per that ticket)
//
// THE 1/16 TRANSFORM (the filed acceptance, read literally). The preview
// scene holds the section geometry at 1/16 scale about the sky_camera
// origin:  p' = origin + (p - origin) / scale, with scale from the
// sky_camera "scale" key (default 16). A section authored at full scale
// therefore previews as a 1/16 miniature sitting at the sky_camera origin,
// and PreviewScene carries SkyCameraOrigin + Scale so a caller can place
// it. NOTE FOR THE INTEGRATOR: Source's engine scales the authored
// 3D-skybox section UP by the sky_camera scale at render time (the
// authored section is already small). If the pass-1 author meant the
// preview to hold world-composited (x16) geometry instead of the 1/16
// miniature, the single line in TransformBrush is the flip —
// p' = origin + (p - origin) * scale — and the tests' expected bounds
// flip with it. The filed test sentence ("geometry is 1/16 scale about
// the sky_camera origin") is what this file implements.
//
// HOW THE 3D SECTION IS IDENTIFIED (pass 1 stores it; no marker type
// exists yet, so the rule is stated here, not guessed silently):
//   1. Visgroup rule (primary): brushes whose editor visgroup is named
//      "3D Skybox" are the section. This is the Hammer editing convention
//      for grouping the section, and it is exact — a main-map brush is
//      never misread as skybox.
//   2. Spatial rule (fallback, only when no "3D Skybox" visgroup exists
//      in the document): a brush belongs to the section when its solid
//      centroid lies within SectionRadius (4096 units) of the sky_camera
//      origin. This mirrors the engine behaviour — the section is built
//      around the sky_camera, far from the playable map — and 4096 covers
//      a full 32768-unit map at 1/16 (2048 half-extent) with room to spare.
//      When no visgroup exists AND no sky_camera exists, no brush can be
//      located, so there is no section and no warning.
//   Brush-entity brushes (func_brush etc.) are included on the same
//   rules; world brushes and brush-entity brushes are one set here.
//   A section brush that will not build as a closed convex solid
//   (MeshFromPlanes returns null) is skipped with the named warning
//   skybox-invalid-section-brush; it never crashes the preview.
//
// WARNINGS ARE DATA, NOT EXCEPTIONS. A 3D section without a sky_camera
// is the named warning "skybox-missing-sky-camera" in the result, never
// a crash; every other defect below is likewise a named warning code with
// a human message, and the preview still builds from whatever parsed.
//   skybox-missing-sky-camera         3D section present, no sky_camera
//   skybox-multiple-sky-cameras       more than one sky_camera (first wins)
//   skybox-invalid-sky-camera-origin  sky_camera origin unparseable (Zero used)
//   skybox-invalid-sky-camera-scale   sky_camera scale missing/invalid/<=0 (16 used)
//   skybox-invalid-section-brush      a section brush is not a closed solid
//   skybox-multiple-fog-controllers   more than one env_fog_controller (first wins)
//   skybox-invalid-fog                an env_fog_controller / worldspawn fog
//                                     keyvalue is unparseable (default kept)
//   skybox-multiple-light-environments more than one light_environment (first wins)
//   skybox-sun-without-env-sun        light_environment with no env_sun
//   skybox-invalid-sun-angles         light_environment angles unparseable (+Z used)
//   skybox-invalid-sun-light          light_environment _light/_ambient
//                                     unparseable (white / brightness 1 kept)
//
// SUN DIRECTION CONVENTION. Source entity angles are pitch/yaw/roll with
// pitch positive-DOWN, so elevation above the horizon is -pitch. The sun
// direction stored here is the unit vector pointing FROM the scene TOWARD
// the sun: (cos(el)*cos(yaw), cos(el)*sin(yaw), sin(el)). angles "0 90 0"
// therefore gives (0, 1, 0). A renderer that needs the light travel
// direction negates it.
//
// FOG SOURCES. env_fog_controller wins when present (fogstart, fogend,
// fogcolor "r g b" 0-255, fogmaxdensity, and fogenable "0" disabling it
// when the key is present). Otherwise the worldspawn fog keys of the
// same names are read, per the pass-1 worldspawn-properties spec. Absent
// keys stay 0 (unset); End <= Start means the fog has no visible range.
// When neither source exists, Fog.Enabled is false.
//
// TOGGLE. BuildPreview(document, enabled: false) returns an empty,
// disabled scene (no geometry, no warnings, no sun/fog). The flag is the
// API toggle the ticket asks for; a future viewport calls it with the
// user's skybox-preview switch.
//
// GEOMETRY. Face polygons come from MapCsg.MeshFromPlanes (the shared
// plane-intersection vertex solve — no second solver here), so each
// preview face is one brush plane with its material name, source brush id
// and source face id. Displacement faces preview from their base planes;
// the displacement grid itself is not tessellated here.

/// <summary>One named warning carried by a <see cref="SkyboxPreviewScene"/>; never thrown.</summary>
public sealed class SkyboxWarning
{
	public string Code = "";
	public string Message = "";

	public SkyboxWarning() { }
	public SkyboxWarning( string code, string message ) { Code = code; Message = message; }
}

/// <summary>Fog parameters resolved for the preview, from env_fog_controller or worldspawn keys.</summary>
public sealed class SkyboxFog
{
	/// <summary>True when a fog source was found (and not disabled via fogenable "0").</summary>
	public bool Enabled;
	/// <summary>Near distance where fog starts; 0 when the key was absent (unset).</summary>
	public float Start;
	/// <summary>Far distance where fog is opaque; 0 when the key was absent (unset).</summary>
	public float End;
	/// <summary>Fog colour, 0..1 RGB (a is always 1).</summary>
	public Vec4 Color = new Vec4( 1, 1, 1, 1 );
	/// <summary>Maximum fog density, 0..1; 1 when the key was absent.</summary>
	public float MaxDensity = 1f;
	/// <summary>Where the values came from: "env_fog_controller", "worldspawn", or "none".</summary>
	public string Source = "none";
}

/// <summary>Sun lighting resolved from light_environment (+ env_sun). Null on the scene when the map has no light_environment.</summary>
public sealed class SkyboxSun
{
	/// <summary>Unit vector pointing FROM the scene TOWARD the sun (see the file header for the angles convention).</summary>
	public Vec3 Direction = new Vec3( 0, 0, 1 );
	/// <summary>Sun colour, 0..1 RGB (a is always 1).</summary>
	public Vec4 Color = new Vec4( 1, 1, 1, 1 );
	/// <summary>Sun brightness from the 4th component of _light; 1 when absent.</summary>
	public float Brightness = 1f;
	/// <summary>Ambient colour from _ambient, 0..1 RGB (a is always 1); black when absent.</summary>
	public Vec4 Ambient = new Vec4( 0, 0, 0, 1 );
	/// <summary>True when an env_sun entity exists to pair with the light_environment.</summary>
	public bool HasEnvSun;
}

/// <summary>One transformed face of the 3D skybox section, in preview coordinates.</summary>
public sealed class SkyboxPreviewFace
{
	/// <summary>Polygon vertices after the 1/16 transform about the sky_camera origin, outward-wound.</summary>
	public Vec3[] Vertices = Array.Empty<Vec3>();
	/// <summary>Material name as stored on the source face.</summary>
	public string Material = "";
	/// <summary>Id of the source brush this face came from.</summary>
	public int SourceBrushId;
	/// <summary>Id of the source face this polygon came from, or -1 when it could not be matched.</summary>
	public int SourceFaceId = -1;
}

/// <summary>
/// The skybox preview scene: scaled 3D-section geometry at 1/16 about the
/// sky_camera origin + 2D sky parameters + fog + sun, as a data structure a
/// future viewport / AI caller can render. No viewport code lives here.
/// </summary>
public sealed class SkyboxPreviewScene
{
	/// <summary>False when built with enabled: false — everything else is empty by construction.</summary>
	public bool Enabled;
	/// <summary>2D sky name from worldspawn "skyname"; "" when the map names no sky.</summary>
	public string SkyName = "";
	/// <summary>True when SkyName is non-empty.</summary>
	public bool Has2DSky => !string.IsNullOrEmpty( SkyName );
	/// <summary>True when a sky_camera entity was found.</summary>
	public bool HasSkyCamera;
	/// <summary>sky_camera origin (the preview scene's own origin); Zero when there is no sky_camera.</summary>
	public Vec3 SkyCameraOrigin = Vec3.Zero;
	/// <summary>sky_camera scale (default 16); the divisor applied to section geometry.</summary>
	public float Scale = SkyboxPreview.DefaultScale;
	/// <summary>True when at least one brush was identified as 3D-skybox section (even if it failed to build — see Warnings).</summary>
	public bool Has3DSection;
	/// <summary>Transformed section faces; empty when there is no section, no sky_camera, or the preview is disabled.</summary>
	public List<SkyboxPreviewFace> Faces = new List<SkyboxPreviewFace>();
	/// <summary>Axis-aligned bounds of the transformed faces; SkyCameraOrigin (or Zero) when Faces is empty.</summary>
	public Vec3 SectionBoundsMin = Vec3.Zero;
	/// <summary>Axis-aligned bounds of the transformed faces; SkyCameraOrigin (or Zero) when Faces is empty.</summary>
	public Vec3 SectionBoundsMax = Vec3.Zero;
	/// <summary>Fog parameters; Enabled is false when the map defines no fog.</summary>
	public SkyboxFog Fog = new SkyboxFog();
	/// <summary>Sun lighting; null when the map has no light_environment.</summary>
	public SkyboxSun Sun;
	/// <summary>Named warnings; empty is a clean build. A missing sky_camera is a warning here, never a crash.</summary>
	public List<SkyboxWarning> Warnings = new List<SkyboxWarning>();

	/// <summary>True when Warnings contains the given code.</summary>
	public bool HasWarning( string code )
	{
		foreach ( var w in Warnings ) if ( w.Code == code ) return true;
		return false;
	}
}

/// <summary>
/// Builds a <see cref="SkyboxPreviewScene"/> from a map document. Static
/// and allocation-light by design: the caller owns the document, the
/// result is a fresh snapshot, and nothing here touches the UI or the
/// filesystem.
/// </summary>
public static class SkyboxPreview
{
	/// <summary>Default sky_camera scale: the Source 1/16 3D-skybox factor.</summary>
	public const float DefaultScale = 16f;

	/// <summary>Half-extent, in inches, of the spatial fallback region around the sky_camera origin (see the file header).</summary>
	public const float SectionRadius = 4096f;

	/// <summary>Editor visgroup name that marks the 3D skybox section.</summary>
	public const string SectionVisGroupName = "3D Skybox";

	/// <summary>3D section present but no sky_camera entity: the named warning the ticket requires.</summary>
	public const string MissingSkyCamera = "skybox-missing-sky-camera";
	public const string MultipleSkyCameras = "skybox-multiple-sky-cameras";
	public const string InvalidSkyCameraOrigin = "skybox-invalid-sky-camera-origin";
	public const string InvalidSkyCameraScale = "skybox-invalid-sky-camera-scale";
	public const string InvalidSectionBrush = "skybox-invalid-section-brush";
	public const string MultipleFogControllers = "skybox-multiple-fog-controllers";
	public const string InvalidFog = "skybox-invalid-fog";
	public const string MultipleLightEnvironments = "skybox-multiple-light-environments";
	public const string SunWithoutEnvSun = "skybox-sun-without-env-sun";
	public const string InvalidSunAngles = "skybox-invalid-sun-angles";
	public const string InvalidSunLight = "skybox-invalid-sun-light";

	/// <summary>
	/// Build the preview scene for a map document. A missing sky_camera is
	/// the named warning <see cref="MissingSkyCamera"/> in the result, not
	/// an exception; a null document is a programmer error and throws.
	/// Pass enabled: false to toggle the preview off (empty scene).
	/// </summary>
	public static SkyboxPreviewScene BuildPreview( VmfDocument document, bool enabled = true )
	{
		if ( document == null ) throw new ArgumentNullException( nameof( document ) );
		var scene = new SkyboxPreviewScene { Enabled = enabled };
		if ( !enabled ) return scene;

		scene.SkyName = document.WorldspawnGet( "skyname" ) ?? "";
		scene.Fog = ResolveFog( document, scene.Warnings );
		scene.Sun = ResolveSun( document, scene.Warnings );

		var cameras = FindEntities( document, "sky_camera" );
		if ( cameras.Count > 1 )
			scene.Warnings.Add( new SkyboxWarning( MultipleSkyCameras,
				$"The map has {cameras.Count} sky_camera entities; the preview uses the first (targetname '{cameras[0].TargetName ?? ""}')." ) );
		if ( cameras.Count > 0 )
		{
			var camera = cameras[0];
			scene.HasSkyCamera = true;
			scene.SkyCameraOrigin = ParseOrigin( camera.Get( "origin" ), scene.Warnings );
			scene.Scale = ParseScale( camera.Get( "scale" ), scene.Warnings );
		}

		var members = FindSectionMembers( document, scene, out int identified );
		scene.Has3DSection = identified > 0;
		if ( identified > 0 && !scene.HasSkyCamera )
		{
			scene.Warnings.Add( new SkyboxWarning( MissingSkyCamera,
				$"The map has a 3D skybox section ({identified} brush(es)) but no sky_camera entity, so the section has no origin or scale to preview against. Add a sky_camera to preview the section." ) );
		}
		if ( scene.HasSkyCamera )
		{
			foreach ( var member in members ) TransformBrush( member.Brush, member.Mesh, member.Materials, scene );
		}

		if ( scene.Faces.Count > 0 )
		{
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var face in scene.Faces )
				foreach ( var v in face.Vertices )
				{
					min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
					max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
				}
			scene.SectionBoundsMin = min;
			scene.SectionBoundsMax = max;
		}
		else
		{
			scene.SectionBoundsMin = scene.SkyCameraOrigin;
			scene.SectionBoundsMax = scene.SkyCameraOrigin;
		}
		return scene;
	}

	/// <summary>One section member: the source brush, its solved solid, and the per-brush material table (index -> name).</summary>
	sealed class SectionMember
	{
		public VmfBrush Brush;
		public PolyMesh Mesh;
		public List<string> Materials = new List<string>();
	}

	static List<SectionMember> FindSectionMembers( VmfDocument document, SkyboxPreviewScene scene, out int identified )
	{
		identified = 0;
		var found = new List<SectionMember>();
		var sectionIds = new HashSet<int>();
		foreach ( var vg in document.VisGroups )
			if ( string.Equals( vg.Name, SectionVisGroupName, StringComparison.OrdinalIgnoreCase ) )
				sectionIds.Add( vg.Id );

		if ( sectionIds.Count > 0 )
		{
			foreach ( var brush in document.AllBrushes() )
			{
				if ( !sectionIds.Contains( brush.VisGroupId ) ) continue;
				identified++;
				var mesh = TryBuildBrushMesh( brush, out var materials );
				if ( mesh == null )
				{
					scene.Warnings.Add( new SkyboxWarning( InvalidSectionBrush,
						$"3D skybox brush #{brush.Id} is not a closed convex solid, so it is left out of the preview." ) );
					continue;
				}
				found.Add( new SectionMember { Brush = brush, Mesh = mesh, Materials = materials } );
			}
			if ( found.Count > 0 ) return found;
			// A "3D Skybox" visgroup exists but holds no buildable brush:
			// fall through to the spatial rule rather than reporting an
			// empty section while section geometry sits by the camera.
		}

		if ( !scene.HasSkyCamera ) return found;
		foreach ( var brush in document.AllBrushes() )
		{
			var mesh = TryBuildBrushMesh( brush, out var materials );
			if ( mesh == null ) continue;
			var centroid = Centroid( mesh );
			if ( ( centroid - scene.SkyCameraOrigin ).Length <= SectionRadius )
			{
				identified++;
				found.Add( new SectionMember { Brush = brush, Mesh = mesh, Materials = materials } );
			}
		}
		return found;
	}

	/// <summary>Solve a brush's solid from its face planes via the shared MapCsg solver; null when it is not a closed convex solid.</summary>
	static PolyMesh TryBuildBrushMesh( VmfBrush brush, out List<string> materials )
	{
		materials = new List<string>();
		if ( brush == null || brush.Faces.Count < 4 ) return null;
		var planes = new List<BrushPlane>();
		foreach ( var face in brush.Faces )
		{
			if ( face.IsDegeneratePlane ) continue;
			var n = face.Normal;
			string name = face.Material ?? "";
			int material = materials.IndexOf( name );
			if ( material < 0 ) { materials.Add( name ); material = materials.Count - 1; }
			planes.Add( new BrushPlane(
				new Vec3( (float)n.X, (float)n.Y, (float)n.Z ),
				(float)face.Distance, material ) );
		}
		if ( planes.Count < 4 ) return null;
		return MapCsg.MeshFromPlanes( planes );
	}

	static Vec3 Centroid( PolyMesh mesh )
	{
		var c = Vec3.Zero;
		foreach ( var p in mesh.Positions ) c += p;
		return mesh.Positions.Count == 0 ? c : c / mesh.Positions.Count;
	}

	/// <summary>The 1/16 transform: p' = origin + (p - origin) / scale. See the file header for the direction decision.</summary>
	static void TransformBrush( VmfBrush brush, PolyMesh mesh, List<string> materials, SkyboxPreviewScene scene )
	{
		var origin = scene.SkyCameraOrigin;
		float scale = scene.Scale;
		foreach ( var face in mesh.Faces )
		{
			if ( face.Indices == null || face.Indices.Length < 3 ) continue;
			var verts = new Vec3[face.Indices.Length];
			for ( int i = 0; i < face.Indices.Length; i++ )
			{
				var p = mesh.Positions[face.Indices[i]];
				verts[i] = origin + ( p - origin ) / scale;
			}
			string material = face.Material >= 0 && face.Material < materials.Count ? materials[face.Material] : "";
			scene.Faces.Add( new SkyboxPreviewFace
			{
				Vertices = verts,
				Material = material,
				SourceBrushId = brush.Id,
				SourceFaceId = MatchSourceFace( brush, mesh, face ),
			} );
		}
	}

	/// <summary>Match a solved mesh face back to its source VmfFace by plane (normal + distance); -1 when it cannot be matched.</summary>
	static int MatchSourceFace( VmfBrush brush, PolyMesh mesh, Face face )
	{
		var n = mesh.FaceNormal( face ).Normal;
		if ( n.LengthSquared < 1e-12f ) return -1;
		float d = Vec3.Dot( n, mesh.Positions[face.Indices[0]] );
		foreach ( var source in brush.Faces )
		{
			var sn = source.Normal;
			var sv = new Vec3( (float)sn.X, (float)sn.Y, (float)sn.Z );
			if ( Vec3.Dot( n, sv ) > 0.9999f && MathF.Abs( d - (float)source.Distance ) < 1e-3f )
				return source.Id;
		}
		return -1;
	}

	static SkyboxFog ResolveFog( VmfDocument document, List<SkyboxWarning> warnings )
	{
		var fog = new SkyboxFog();
		var controllers = FindEntities( document, "env_fog_controller" );
		if ( controllers.Count > 0 )
		{
			if ( controllers.Count > 1 )
				warnings.Add( new SkyboxWarning( MultipleFogControllers,
					$"The map has {controllers.Count} env_fog_controller entities; the preview uses the first." ) );
			var e = controllers[0];
			fog.Source = "env_fog_controller";
			fog.Enabled = e.Get( "fogenable" ) != "0";
			ReadFogKeys( e.Get( "fogstart" ), e.Get( "fogend" ), e.Get( "fogcolor" ), e.Get( "fogmaxdensity" ), fog, warnings );
			return fog;
		}
		string start = document.WorldspawnGet( "fogstart" );
		string end = document.WorldspawnGet( "fogend" );
		string color = document.WorldspawnGet( "fogcolor" );
		string density = document.WorldspawnGet( "fogmaxdensity" );
		if ( start != null || end != null || color != null || density != null )
		{
			fog.Source = "worldspawn";
			fog.Enabled = true;
			ReadFogKeys( start, end, color, density, fog, warnings );
		}
		return fog;
	}

	static void ReadFogKeys( string start, string end, string color, string density, SkyboxFog fog, List<SkyboxWarning> warnings )
	{
		// Each key is parsed in its own statement (not an out-var inside a
		// compound condition) so the parsed value is definitely assigned
		// where it is used; an unparseable or non-finite value warns and
		// keeps the default.
		if ( start != null )
		{
			if ( TryParseFloat( start, out float s ) && float.IsFinite( s ) ) fog.Start = s;
			else warnings.Add( new SkyboxWarning( InvalidFog, $"Fog start '{start}' is not a number; 0 is used." ) );
		}
		if ( end != null )
		{
			if ( TryParseFloat( end, out float e ) && float.IsFinite( e ) ) fog.End = e;
			else warnings.Add( new SkyboxWarning( InvalidFog, $"Fog end '{end}' is not a number; 0 is used." ) );
		}
		if ( color != null )
		{
			if ( !TryParseColour255( color, out var c ) )
				warnings.Add( new SkyboxWarning( InvalidFog, $"Fog colour '{color}' is not 'r g b' 0-255; white is used." ) );
			else fog.Color = c;
		}
		if ( density != null )
		{
			if ( TryParseFloat( density, out float d ) && float.IsFinite( d ) ) fog.MaxDensity = d;
			else warnings.Add( new SkyboxWarning( InvalidFog, $"Fog max density '{density}' is not a number; 1 is used." ) );
		}
	}

	static SkyboxSun ResolveSun( VmfDocument document, List<SkyboxWarning> warnings )
	{
		var suns = FindEntities( document, "light_environment" );
		if ( suns.Count == 0 ) return null;
		if ( suns.Count > 1 )
			warnings.Add( new SkyboxWarning( MultipleLightEnvironments,
				$"The map has {suns.Count} light_environment entities; the preview uses the first." ) );
		var e = suns[0];
		var sun = new SkyboxSun();
		sun.HasEnvSun = FindEntities( document, "env_sun" ).Count > 0;
		if ( !sun.HasEnvSun )
			warnings.Add( new SkyboxWarning( SunWithoutEnvSun,
				"The map has a light_environment but no env_sun; the sun sprite has no paired env_sun (see hammer-lighting-environment-sun-fog)." ) );
		if ( !TryParseSunDirection( e.Get( "angles" ), out var direction ) )
		{
			warnings.Add( new SkyboxWarning( InvalidSunAngles,
				$"light_environment angles '{e.Get( "angles" ) ?? ""}' are not 'pitch yaw roll'; the sun points straight up." ) );
			direction = new Vec3( 0, 0, 1 );
		}
		sun.Direction = direction;
		if ( e.Get( "_light" ) != null )
		{
			if ( !TryParseLight( e.Get( "_light" ), out var color, out var brightness ) )
				warnings.Add( new SkyboxWarning( InvalidSunLight,
					$"light_environment _light '{e.Get( "_light" )}' is not 'r g b [brightness]'; white at brightness 1 is used." ) );
			else { sun.Color = color; sun.Brightness = brightness; }
		}
		if ( e.Get( "_ambient" ) != null )
		{
			if ( !TryParseLight( e.Get( "_ambient" ), out var ambient, out _ ) )
				warnings.Add( new SkyboxWarning( InvalidSunLight,
					$"light_environment _ambient '{e.Get( "_ambient" )}' is not 'r g b [brightness]'; black ambient is used." ) );
			else sun.Ambient = ambient;
		}
		return sun;
	}

	/// <summary>Source angles "pitch yaw roll" (pitch positive-down) -> unit vector toward the sun.</summary>
	static bool TryParseSunDirection( string text, out Vec3 direction )
	{
		direction = new Vec3( 0, 0, 1 );
		if ( !TryParseVec3( text, out var angles ) ) return false;
		float elevation = -angles.x * MathF.PI / 180f;
		float yaw = angles.y * MathF.PI / 180f;
		float ce = MathF.Cos( elevation );
		direction = new Vec3( ce * MathF.Cos( yaw ), ce * MathF.Sin( yaw ), MathF.Sin( elevation ) ).Normal;
		return true;
	}

	/// <summary>Parse "r g b [brightness]" with rgb in 0-255; brightness defaults to 1.</summary>
	static bool TryParseLight( string text, out Vec4 color, out float brightness )
	{
		color = new Vec4( 1, 1, 1, 1 );
		brightness = 1f;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length < 3 || parts.Length > 4 ) return false;
		if ( !TryParseFloat( parts[0], out var r ) || !TryParseFloat( parts[1], out var g ) || !TryParseFloat( parts[2], out var b ) ) return false;
		if ( parts.Length == 4 && !TryParseFloat( parts[3], out brightness ) ) return false;
		color = new Vec4( Clamp01( r / 255f ), Clamp01( g / 255f ), Clamp01( b / 255f ), 1f );
		return true;
	}

	/// <summary>Parse "r g b" with components in 0-255.</summary>
	static bool TryParseColour255( string text, out Vec4 color )
	{
		color = new Vec4( 1, 1, 1, 1 );
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var parts = text.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		if ( !TryParseFloat( parts[0], out var r ) || !TryParseFloat( parts[1], out var g ) || !TryParseFloat( parts[2], out var b ) ) return false;
		color = new Vec4( Clamp01( r / 255f ), Clamp01( g / 255f ), Clamp01( b / 255f ), 1f );
		return true;
	}

	static float Clamp01( float v ) => v < 0f ? 0f : v > 1f ? 1f : v;

	static Vec3 ParseOrigin( string text, List<SkyboxWarning> warnings )
	{
		if ( text != null && TryParseVec3( text, out var origin ) ) return origin;
		warnings.Add( new SkyboxWarning( InvalidSkyCameraOrigin,
			$"sky_camera origin '{text ?? ""}' is not 'x y z'; (0, 0, 0) is used." ) );
		return Vec3.Zero;
	}

	static float ParseScale( string text, List<SkyboxWarning> warnings )
	{
		if ( text != null && TryParseFloat( text, out var scale ) && scale > 0f && !float.IsNaN( scale ) && !float.IsInfinity( scale ) )
			return scale;
		if ( text != null )
			warnings.Add( new SkyboxWarning( InvalidSkyCameraScale,
				$"sky_camera scale '{text}' is not a positive number; the default {DefaultScale} is used." ) );
		return DefaultScale;
	}

	/// <summary>Parse "x y z" (parens/brackets tolerated); false on any unparseable component.</summary>
	static bool TryParseVec3( string text, out Vec3 v )
	{
		v = Vec3.Zero;
		if ( string.IsNullOrWhiteSpace( text ) ) return false;
		var t = text.Trim().TrimStart( '(', '[' ).TrimEnd( ')', ']' ).Trim();
		var parts = t.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return false;
		if ( !TryParseFloat( parts[0], out var x ) || !TryParseFloat( parts[1], out var y ) || !TryParseFloat( parts[2], out var z ) ) return false;
		v = new Vec3( x, y, z );
		return true;
	}

	static bool TryParseFloat( string text, out float v ) =>
		float.TryParse( text, NumberStyles.Float, CultureInfo.InvariantCulture, out v );

	static List<VmfEntity> FindEntities( VmfDocument document, string className )
	{
		var found = new List<VmfEntity>();
		foreach ( var e in document.Entities )
			if ( string.Equals( e.ClassName, className, StringComparison.OrdinalIgnoreCase ) )
				found.Add( e );
		return found;
	}
}
