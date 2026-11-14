using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy.Map;

// Hammer Desktop P2: 2D/3D skybox + sky_camera + worldspawn map properties (native).
// Board: hammer-skybox-2d-3d-worldspawn (Geppetto Desktop, Hammer P2).
//
// WHAT THIS FILE OWNS (pass 2: the MapDocument binding). The thin adapter
// between the native map document and the skybox aggregate
// (SkyboxWorldspawn.cs): worldspawn sky name via compile settings,
// sky_camera entities, and the 3D skybox section (brushes in the
// "3D Skybox" visgroup) as native map data, plus the bake bridge
// (MapDocument -> world-scale section brushes for MapBakeExport.Bake).
// Backend / data-model / API only: no Desktop UI, no Hammer/VBSP
// shell-out, no viewport code. Units are the kernel's inches, Z-up
// (handoffs/CONTRACTS.md).
//
// WHAT IT DOES NOT TOUCH. MapDocument.cs / MapDocumentIo.cs are owned by
// the map-document worker (daxter-hammer-map-document, lease live) — this
// file only CALLS their public API (Add/RemoveBrush, Add/RemoveEntity,
// AddVisGroup, SetCompileSettings, Find*, CompileSettings) and never edits
// them. SkyboxWorldspawn.cs, VmfIo.cs and EntityRegistry.cs are likewise
// untouched; the registry JSON change (sky_camera class) is data, not code.
//
// MAPDOCUMENT SLOTS USED (all already persisted by MapDocumentIo .gmap):
//   2D sky name  <-> MapCompileSettings.SkyName ("op set-compile" line)
//   sky_camera   <-> MapEntityData, ClassName "sky_camera", Origin field,
//                   KeyValues["scale"] (+ "targetname"); "origin" is never
//                   a keyvalue in this model (MapEntities rule).
//   3D section   <-> MapBrushData whose VisGroupIds contain the visgroup
//                   named "3D Skybox" (the same name SkyboxPreview and
//                   SkyboxWorldspawn use on the VMF leg).
//
// THE WORLDSPAWN GAP (named, not silent). MapDocument has NO slot for
// chapter / title / detail props / fog preset / structured fog: the .gmap
// "op set-compile" line carries only the sky name, and there is no
// document-level property op. So:
//   - FromMapDocument reads those fields as empty/defaults.
//   - ApplyToMapDocument returns the named warning
//     skybox-worldspawn-not-in-map-document whenever the aggregate
//     carries a value the Map leg cannot hold: a non-empty chapter,
//     title or fog preset, present structured fog, or DetailProps OFF
//     (the Map leg always reads the Source default ON —
//     WorldspawnProperties.DetailProps defaults true — so only "off"
//     is lost; "on" round-trips by coincidence of the default, not by
//     being carried). The loss is named, never silent.
//   - The VMF leg is unaffected: SkyboxWorldspawn round-trips all of
//     them through VmfDocument (see SkyboxWorldspawnTests).
//   FOLLOW-UP (smallest change, for the MapDocument owner / integrator):
//   add a worldspawn section to MapCompileSettings (Chapter, Title,
//   DetailProps, DetailVbsp, DetailMaterial, Fog preset string, and the
//   fogstart/fogend/fogcolor/fogmaxdensity/fogenable values), a new
//   "op set-worldspawn ..." history line in MapDocumentIo.Write/Read,
//   and the fold case in MapDocument.ApplyEntry; v1 files without the
//   line read defaults with a LoadNote. When that lands, this file's
//   warning goes away and From/ApplyToMapDocument carry the fields.
//
// ID RULES.
//   - MapEntityData.Id is a string; SkyCamera.Id / VmfEntity.Id are ints.
//     ToSkyCamera parses the string id (non-numeric -> 0 = unassigned);
//     ToMapEntity writes a non-zero camera id as its decimal string and
//     mints MapDocument.NewId() for 0 (same mint-on-0 shape as
//     SkyboxWorldspawn.ApplyToVmfDocument; a 0-id camera therefore churns
//     its document id across applies — use a real id for stable edits).
//   - MapBrushData.Id is a string; VmfBrush.Id is an int. A section brush
//     whose id parses as a positive int keeps it as its Vmf id; any other
//     id is minted fresh (unique within the section). So a MapDocument ->
//     VMF -> MapDocument loop renames non-numeric section brush ids,
//     while the .gmap loop (the native store) preserves every id exactly.
//     Tests cover both loops.
//   - VmfFace ids are assigned 1..N in face order on export; the
//     MapBrushFaceData form has no face-id slot, so they do not survive
//     the import leg. Face ORDER is preserved exactly both ways.
//
// UV RULES. MapBrushData faces carry FaceUv (offset/scale/rotation/
// lightmap/smoothing); VmfFace carries Valve 220 axes. The export leg
// writes the axes from FaceUv.PlaneAxes(normal) with the FaceUv offset,
// scale, rotation, lightmap scale and smoothing group, so Hammer sees
// sane UVs. The import leg recovers the FaceUv exactly when the VMF axes
// match that basis (the Geppetto-authored case, including this file's own
// output); foreign axes fall back to FaceUv.Default with ONE named
// warning per brush (skybox-section-uv-defaulted). The repo's older
// converters (MapExportRoundtrip.ToMapBrushes) drop UVs outright; this
// file does not.
//
// PLANE RULES. MapBrushData faces are planes (unit outward normal +
// distance); VmfFace planes are three points. Export builds the three
// points from (normal, distance) with the same formula as
// MapExportRoundtrip.PlanePointsFromNormalDistance, so the plane is
// exact by construction; the solved face polygon (when solvable and
// accurate to 1e-6) is stored as the face's StoredVertices no-drift
// record, exactly like VmfBrush.ComputeAndStoreVertices. Import reads
// the plane back with VmfFace.ToPlane. A face whose plane is degenerate
// converts faithfully (zero normal) and the document's Validate flags
// it; a whole section brush with fewer than 4 faces or a degenerate
// plane is left out with the shared skybox-invalid-section-brush
// warning, mirroring SkyboxWorldspawn.FromVmfDocument.
//
// SECTION IDENTIFICATION (Map leg only). The visgroup rule ONLY: a brush
// belongs to the section when its VisGroupIds contain the "3D Skybox"
// visgroup. There is NO spatial fallback here (the VMF leg's fallback
// exists for legacy files without the visgroup); in the authoring model
// the visgroup IS the marker, and ApplyToMapDocument always creates it.
//
// BAKE DIRECTION. ToBakeData / ToMapBakeBrushes run the aggregate's
// ToBakeData (section scaled UP x16 about the sky_camera origin, else
// the section origin) and hand the world-scale brushes to the caller
// for MapBakeExport.Bake. Read warnings (bad camera scale, unbuildable
// section brush) are merged into the bake data's Warnings first, so the
// bake caller sees them without a second call.
public static class MapDocumentSkybox
{
	/// <summary>Worldspawn data the aggregate carries that MapDocument cannot hold (see the file header).</summary>
	public const string WorldspawnNotInMapDocument = "skybox-worldspawn-not-in-map-document";

	/// <summary>A VmfBrush face carried displacement data, which has no MapBrushData slot here; the planes still convert.</summary>
	public const string SectionDisplacementDropped = "skybox-section-displacement-dropped";

	/// <summary>A VmfBrush face's UV axes did not match the face's PlaneAxes basis, so its UV fell back to FaceUv.Default.</summary>
	public const string SectionUvDefaulted = "skybox-section-uv-defaulted";

	/// <summary>The "3D Skybox" visgroup colour in MapVisGroup "#RRGGBB" form (the Vmf form is "0 128 255").</summary>
	public const string SectionVisGroupColour = "#0080FF";

	/// <summary>Per-component tolerance when matching VMF UV axes against FaceUv.PlaneAxes (unit vectors).</summary>
	const double UvAxisTolerance = 1e-4;

	/// <summary>A solved face polygon is stored as StoredVertices only when every vertex lies within this of the plane.</summary>
	const double StoredVertexTolerance = 1e-6;

	// ---- MapDocument -> SkyboxWorldspawn ---------------------------------------------

	/// <summary>
	/// Read the skybox/worldspawn data out of a MapDocument: 2D sky name
	/// from compile settings, sky_camera entities, and the "3D Skybox"
	/// visgroup brushes as the section. Chapter/title/detail/fog read as
	/// empty/defaults (no MapDocument slot — see the file header). The
	/// document is never mutated; section brushes are converted, not
	/// aliased. Returns the aggregate plus the warnings raised while
	/// reading (bad camera scale, unbuildable section brush).
	/// </summary>
	public static (SkyboxWorldspawn Skybox, List<SkyboxWarning> Warnings) FromMapDocument( MapDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var warnings = new List<SkyboxWarning>();
		var skybox = new SkyboxWorldspawn { MapId = doc.DocumentId };
		skybox.Worldspawn.SkyboxName = doc.CompileSettings?.SkyName ?? "";

		foreach ( var entity in doc.Entities )
		{
			if ( entity is null ) continue;
			if ( !string.Equals( entity.ClassName, SkyCamera.ClassName, StringComparison.OrdinalIgnoreCase ) ) continue;
			skybox.Cameras.Add( ToSkyCamera( entity, warnings ) );
		}

		skybox.SectionOrigin = skybox.HasSkyCamera ? skybox.Camera.Origin : VmfVec.Zero;
		skybox.SectionScale = skybox.HasSkyCamera ? skybox.Camera.Scale : SkyboxWorldspawn.DefaultSectionScale;

		var sectionGroup = FindSectionVisGroup( doc );
		if ( sectionGroup != null )
		{
			// Claimed ids, filled as brushes convert: VmfIdForSectionBrush
			// keeps a parseable positive id when it is still free, so the
			// set must start EMPTY — pre-filling it with every parseable id
			// made every keep fail and minted max+1 for all of them.
			var usedIds = new HashSet<int>();
			var nextFresh = 1;
			foreach ( var brush in doc.Brushes )
			{
				if ( brush is null || !brush.VisGroupIds.Contains( sectionGroup.Id ) ) continue;
				if ( !IsBuildableSectionBrush( brush ) )
				{
					warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidSectionBrush,
						$"3D skybox brush '{brush.Id}' is not a closed convex solid, so it is left out of the section." ) );
					continue;
				}
				var vmfId = VmfIdForSectionBrush( brush, usedIds, ref nextFresh );
				skybox.SectionBrushes.Add( ToVmfBrush( brush, vmfId ) );
			}
		}
		return (skybox, warnings);
	}

	/// <summary>
	/// Write an aggregate onto a MapDocument (see the ownership rule in
	/// the file header): the sky name goes through compile settings, the
	/// sky_camera entities are upserted by id (others removed), and the
	/// section brushes are upserted by id into the "3D Skybox" visgroup
	/// (other visgroup members removed; the visgroup itself stays).
	/// Everything outside the skybox slice is untouched, and entries are
	/// appended only for real changes, so applying the same aggregate
	/// twice appends no second history. Returns the warnings raised
	/// while applying (worldspawn data with no slot, unbuildable section
	/// brush, defaulted UVs, dropped displacement).
	/// </summary>
	public static List<SkyboxWarning> ApplyToMapDocument( SkyboxWorldspawn skybox, MapDocument doc )
	{
		if ( skybox is null ) throw new ArgumentNullException( nameof( skybox ) );
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var warnings = new List<SkyboxWarning>();

		var dropped = new List<string>();
		if ( !string.IsNullOrEmpty( skybox.Worldspawn?.Chapter ) ) dropped.Add( "chaptertitle" );
		if ( !string.IsNullOrEmpty( skybox.Worldspawn?.Title ) ) dropped.Add( "geppetto_title" );
		if ( skybox.Worldspawn != null && !skybox.Worldspawn.DetailProps ) dropped.Add( "detailvbsp/detailmaterial" );
		if ( !string.IsNullOrEmpty( skybox.Worldspawn?.Fog ) ) dropped.Add( "fog (preset)" );
		if ( skybox.FogParams != null && skybox.FogParams.Present ) dropped.Add( "fogstart/fogend/fogcolor/fogmaxdensity/fogenable" );
		if ( dropped.Count > 0 )
			warnings.Add( new SkyboxWarning( WorldspawnNotInMapDocument,
				$"MapDocument has no worldspawn slot for {string.Join( ", ", dropped )}; that data is kept in the VMF export only until the map-document format gains a worldspawn section (see MapDocumentSkybox.cs)." ) );

		var wantSky = skybox.Worldspawn?.SkyboxName ?? "";
		var current = doc.CompileSettings;
		if ( ( current?.SkyName ?? "" ) != wantSky )
		{
			var settings = ( current ?? new Effigy.MapCompileSettings() ).Clone();
			settings.SkyName = wantSky;
			doc.SetCompileSettings( settings );
		}

		var wantedCameras = new Dictionary<string, MapEntityData>( StringComparer.Ordinal );
		foreach ( var camera in skybox.Cameras )
		{
			if ( camera is null ) continue;
			var entity = ToMapEntity( camera );
			wantedCameras[entity.Id] = entity;
		}
		foreach ( var existing in doc.Entities.Where( e => e != null && string.Equals( e.ClassName, SkyCamera.ClassName, StringComparison.OrdinalIgnoreCase ) ).ToList() )
		{
			if ( wantedCameras.TryGetValue( existing.Id, out var want ) )
			{
				if ( !existing.SameAs( want ) )
				{
					doc.RemoveEntity( existing.Id );
					doc.AddEntity( want );
				}
				wantedCameras.Remove( existing.Id );
			}
			else doc.RemoveEntity( existing.Id );
		}
		foreach ( var kv in wantedCameras ) doc.AddEntity( kv.Value );

		var group = FindSectionVisGroup( doc );
		if ( skybox.SectionBrushes.Count > 0 && group is null )
		{
			group = new MapVisGroup
			{
				Id = MapDocument.NewId(),
				Name = SkyboxPreview.SectionVisGroupName,
				Colour = SectionVisGroupColour,
				Visible = true,
				CompileVisible = true,
			};
			doc.AddVisGroup( group );
		}

		var wantedBrushes = new Dictionary<string, MapBrushData>( StringComparer.Ordinal );
		foreach ( var vmfBrush in skybox.SectionBrushes )
		{
			if ( vmfBrush is null ) continue;
			if ( !IsBuildableVmfSectionBrush( vmfBrush ) )
			{
				warnings.Add( new SkyboxWarning( SkyboxPreview.InvalidSectionBrush,
					$"3D skybox brush #{vmfBrush.Id} is not a closed convex solid, so it is left out of the map document." ) );
				continue;
			}
			var data = FromVmfBrush( vmfBrush, out var brushWarnings );
			warnings.AddRange( brushWarnings );
			if ( group != null ) data.VisGroupIds = new List<string> { group.Id };
			wantedBrushes[data.Id] = data;
		}
		var sectionIds = group is null
			? new HashSet<string>( StringComparer.Ordinal )
			: new HashSet<string>( doc.Brushes.Where( b => b != null && b.VisGroupIds.Contains( group.Id ) ).Select( b => b.Id ), StringComparer.Ordinal );
		foreach ( var id in sectionIds.ToList() )
		{
			if ( wantedBrushes.TryGetValue( id, out var want ) )
			{
				var existing = doc.FindBrush( id );
				if ( existing is null || !existing.SameAs( want ) )
				{
					doc.RemoveBrush( id );
					doc.AddBrush( want );
				}
				wantedBrushes.Remove( id );
			}
			else doc.RemoveBrush( id );
		}
		foreach ( var kv in wantedBrushes ) doc.AddBrush( kv.Value );
		return warnings;
	}

	// ---- Bake bridge -----------------------------------------------------------------

	/// <summary>
	/// The bake contribution for a MapDocument: the section scaled UP to
	/// world size about the bake anchor (sky_camera origin when a camera
	/// exists, else the section origin), plus the 2D sky name and fog.
	/// Read warnings are merged into the bake data's Warnings first.
	/// </summary>
	public static SkyboxBakeData ToBakeData( MapDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var (skybox, warnings) = FromMapDocument( doc );
		var bake = skybox.ToBakeData();
		if ( warnings.Count > 0 ) bake.Warnings.InsertRange( 0, warnings );
		return bake;
	}

	/// <summary>
	/// The world-scale section brushes as MapBakeBrushes, ready to append
	/// to a MapBakeDocument brush list for MapBakeExport.Bake, with the
	/// count of brushes that could not bake and the warnings raised while
	/// reading the document.
	/// </summary>
	public static (List<MapBakeBrush> Brushes, int Skipped, List<SkyboxWarning> Warnings) ToMapBakeBrushes( MapDocument doc )
	{
		if ( doc is null ) throw new ArgumentNullException( nameof( doc ) );
		var bake = ToBakeData( doc );
		var brushes = bake.ToMapBakeBrushes( out var skipped );
		return (brushes, skipped + bake.SkippedBrushes, bake.Warnings);
	}

	// ---- Entity conversion -----------------------------------------------------------

	/// <summary>
	/// A MapEntityData sky_camera as a SkyCamera: id parsed from the
	/// string id (non-numeric reads as 0), origin from the Origin field,
	/// scale from KeyValues["scale"]. Bad origin/scale fall back exactly
	/// as SkyCamera.FromVmfEntity does (the conversion goes through a
	/// VmfEntity, so the fallback and warning behaviour cannot drift).
	/// <paramref name="warnings"/> is required, never null.
	/// </summary>
	public static SkyCamera ToSkyCamera( MapEntityData entity, List<SkyboxWarning> warnings )
	{
		if ( entity is null ) throw new ArgumentNullException( nameof( entity ) );
		if ( warnings is null ) throw new ArgumentNullException( nameof( warnings ) );
		var vmf = new VmfEntity
		{
			Id = int.TryParse( entity.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id ) ? id : 0,
			ClassName = SkyCamera.ClassName,
		};
		vmf.Set( "origin", StudioDocument.Vec( entity.Origin ) );
		if ( entity.KeyValues.TryGetValue( "scale", out var scale ) ) vmf.Set( "scale", scale );
		if ( !string.IsNullOrEmpty( entity.TargetName ) ) vmf.Set( "targetname", entity.TargetName );
		return SkyCamera.FromVmfEntity( vmf, warnings );
	}

	/// <summary>
	/// A SkyCamera as a MapEntityData: id is the camera id as a decimal
	/// string (MapDocument.NewId() when the camera id is 0), origin in
	/// the Origin field, scale and targetname in KeyValues. "origin" is
	/// never a keyvalue in this model.
	/// </summary>
	public static MapEntityData ToMapEntity( SkyCamera camera )
	{
		if ( camera is null ) throw new ArgumentNullException( nameof( camera ) );
		var entity = new MapEntityData
		{
			Id = camera.Id != 0 ? camera.Id.ToString( CultureInfo.InvariantCulture ) : MapDocument.NewId(),
			ClassName = SkyCamera.ClassName,
			Origin = new Vec3( (float)camera.Origin.X, (float)camera.Origin.Y, (float)camera.Origin.Z ),
		};
		if ( !string.IsNullOrEmpty( camera.TargetName ) ) entity.KeyValues["targetname"] = camera.TargetName;
		entity.KeyValues["scale"] = SkyboxWorldspawn.FormatDouble( camera.Scale );
		return entity;
	}

	// ---- Brush conversion ------------------------------------------------------------

	/// <summary>
	/// A MapBrushData section brush as a VmfBrush with the given Vmf id:
	/// face planes are exact by construction (three points rebuilt from
	/// each face's normal + distance), materials and UVs are carried (see
	/// the file header), and the solved face polygon is stored as the
	/// face's StoredVertices when it is accurate to 1e-6. Face ids are
	/// 1..N in face order. VisGroupId is 0; the caller assigns the
	/// section visgroup.
	/// </summary>
	public static VmfBrush ToVmfBrush( MapBrushData brush, int vmfId )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var vmfBrush = new VmfBrush { Id = vmfId };
		var faceId = 0;
		// Vmf face index -> MapBrushData face index (null faces are skipped,
		// so the two lists only line up through this map).
		var faceMap = new List<int>();
		for ( var fi = 0; fi < brush.Faces.Count; fi++ )
		{
			var face = brush.Faces[fi];
			if ( face is null ) continue;
			faceMap.Add( fi );
			var normal = new VmfVec( face.Normal.x, face.Normal.y, face.Normal.z );
			var distance = (double)face.Distance;
			var vmfFace = new VmfFace
			{
				Id = ++faceId,
				PlanePoints = PlanePointsFromNormalDistance( normal, distance ),
				Material = face.Material?.MaterialName ?? "",
			};
			var uv = face.Material?.Uv ?? FaceUv.Default;
			// A zero-length normal has no PlaneAxes (it throws) and no
			// plane: keep the face faithful with the default axes and let
			// the document's Validate flag the brush.
			if ( face.Normal.LengthSquared < 1e-12f )
			{
				vmfFace.UAxis = new VmfUvAxis( new VmfVec( 1, 0, 0 ), uv.Offset.x, uv.Scale.x );
				vmfFace.VAxis = new VmfUvAxis( new VmfVec( 0, -1, 0 ), uv.Offset.y, uv.Scale.y );
			}
			else
			{
				var (uAxis, vAxis) = FaceUv.PlaneAxes( face.Normal );
				vmfFace.UAxis = new VmfUvAxis( new VmfVec( uAxis.x, uAxis.y, uAxis.z ), uv.Offset.x, uv.Scale.x );
				vmfFace.VAxis = new VmfUvAxis( new VmfVec( vAxis.x, vAxis.y, vAxis.z ), uv.Offset.y, uv.Scale.y );
			}
			vmfFace.Rotation = uv.RotationDegrees;
			vmfFace.LightmapScale = Math.Max( 1, (int)Math.Round( uv.LightmapScale ) );
			vmfFace.SmoothingGroups = Math.Max( 0, uv.SmoothingGroup );
			vmfBrush.Faces.Add( vmfFace );
		}
		// No-drift record: the solved polygon, but only when it is actually
		// on the plane (TryComputeFaceVertices tolerates 1e-2; the VMF
		// stored-vertex check is 1e-6, so an inaccurate loop is dropped
		// rather than written as a record that fails verification).
		for ( var v = 0; v < faceMap.Count; v++ )
		{
			var i = faceMap[v];
			if ( !brush.TryComputeFaceVertices( i, out var loop ) || loop.Count < 3 ) continue;
			var face = brush.Faces[i];
			if ( face is null ) continue;
			var accurate = true;
			foreach ( var p in loop )
			{
				if ( Math.Abs( Vec3.Dot( face.Normal, p ) - face.Distance ) > StoredVertexTolerance ) { accurate = false; break; }
			}
			if ( accurate )
				vmfBrush.Faces[v].StoredVertices = loop.Select( p => new VmfVec( p.x, p.y, p.z ) ).ToList();
		}
		return vmfBrush;
	}

	/// <summary>
	/// A VmfBrush as a MapBrushData: face planes come back with
	/// VmfFace.ToPlane, face order is preserved, materials are carried,
	/// and UVs are recovered exactly when the VMF axes match the face's
	/// PlaneAxes basis (the Geppetto-authored case); foreign axes fall
	/// back to FaceUv.Default with one named warning per brush
	/// (skybox-section-uv-defaulted). Faces carrying displacement data
	/// convert as planes with one named warning per brush
	/// (skybox-section-displacement-dropped) — the displacement itself
	/// has no MapBrushData slot here. The conversion is faithful: a
	/// degenerate face keeps its zero normal and the document's Validate
	/// flags it; the caller decides whether the brush is buildable.
	/// <paramref name="warnings"/> is required, never null.
	/// </summary>
	public static MapBrushData FromVmfBrush( VmfBrush brush, out List<SkyboxWarning> warnings )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		warnings = new List<SkyboxWarning>();
		var data = new MapBrushData { Id = brush.Id.ToString( CultureInfo.InvariantCulture ) };
		var uvDefaultedFaces = 0;
		var displacedFaces = 0;
		foreach ( var face in brush.Faces )
		{
			if ( face is null ) continue;
			if ( face.Displacement != null ) displacedFaces++;
			var (normal, distance) = face.ToPlane();
			var normal3 = new Vec3( (float)normal.X, (float)normal.Y, (float)normal.Z );
			var uv = FaceUvFromVmfFace( face, normal3, out var defaulted );
			if ( defaulted ) uvDefaultedFaces++;
			var material = string.IsNullOrWhiteSpace( face.Material )
				? FaceMaterial.Default
				: new FaceMaterial( face.Material, uv );
			data.Faces.Add( new MapBrushFaceData( normal3, (float)distance, material ) );
		}
		if ( displacedFaces > 0 )
			warnings.Add( new SkyboxWarning( SectionDisplacementDropped,
				$"3D skybox brush #{brush.Id} has {displacedFaces} displaced face(s); the displacement data has no MapBrushData slot here, so the faces convert as planes." ) );
		if ( uvDefaultedFaces > 0 )
			warnings.Add( new SkyboxWarning( SectionUvDefaulted,
				$"3D skybox brush #{brush.Id} has {uvDefaultedFaces} face(s) whose UV axes do not match the face's PlaneAxes basis, so those faces use the default UV." ) );
		return data;
	}

	// ---- Helpers ---------------------------------------------------------------------

	static MapVisGroup FindSectionVisGroup( MapDocument doc )
	{
		foreach ( var vis in doc.VisGroups )
			if ( vis != null && string.Equals( vis.Name, SkyboxPreview.SectionVisGroupName, StringComparison.OrdinalIgnoreCase ) )
				return vis;
		return null;
	}

	static bool IsBuildableSectionBrush( MapBrushData brush )
	{
		if ( brush is null || brush.Faces.Count < 4 ) return false;
		foreach ( var face in brush.Faces )
		{
			if ( face is null ) return false;
			if ( !float.IsFinite( face.Normal.x ) || !float.IsFinite( face.Normal.y ) || !float.IsFinite( face.Normal.z ) ) return false;
			if ( face.Normal.LengthSquared < 1e-12f ) return false;
			if ( !float.IsFinite( face.Distance ) ) return false;
		}
		return true;
	}

	static bool IsBuildableVmfSectionBrush( VmfBrush brush )
	{
		if ( brush is null || brush.Faces.Count < 4 ) return false;
		foreach ( var face in brush.Faces )
			if ( face is null || face.IsDegeneratePlane ) return false;
		return true;
	}

	static int VmfIdForSectionBrush( MapBrushData brush, HashSet<int> usedIds, ref int nextFresh )
	{
		if ( brush != null && int.TryParse( brush.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed )
			&& parsed > 0 && usedIds.Add( parsed ) )
			return parsed;
		var fresh = nextFresh;
		while ( !usedIds.Add( fresh ) ) fresh++;
		nextFresh = fresh + 1;
		return fresh;
	}

	/// <summary>
	/// Three points on the plane (n, d): p0 on the plane plus two
	/// orthonormal in-plane steps. Same formula as
	/// MapExportRoundtrip.PlanePointsFromNormalDistance (private there);
	/// the plane is exact by construction, and VmfFace.Normal over these
	/// points is n again (u x v == n for the basis built here).
	/// </summary>
	static VmfVec[] PlanePointsFromNormalDistance( VmfVec n, double d )
	{
		var p0 = n * d;
		var axis = Math.Abs( n.X ) < 0.9 ? new VmfVec( 1, 0, 0 ) : new VmfVec( 0, 1, 0 );
		var u = VmfVec.Cross( n, axis ).Normalized;
		var v = VmfVec.Cross( n, u );
		return new[] { p0, p0 + u, p0 + v };
	}

	/// <summary>
	/// Recover a FaceUv from a VmfFace: exact when the VMF UV axes match
	/// the face's PlaneAxes basis (unit axes within 1e-4), the scales are
	/// non-zero and the lightmap scale is positive; otherwise the default
	/// UV, reported through <paramref name="defaulted"/>.
	/// </summary>
	static FaceUv FaceUvFromVmfFace( VmfFace face, Vec3 normal, out bool defaulted )
	{
		defaulted = true;
		if ( face is null || normal.LengthSquared < 1e-12f ) return FaceUv.Default;
		if ( !double.IsFinite( face.Rotation ) || !double.IsFinite( face.UAxis.Offset ) || !double.IsFinite( face.UAxis.Scale )
			|| !double.IsFinite( face.VAxis.Offset ) || !double.IsFinite( face.VAxis.Scale ) ) return FaceUv.Default;
		if ( face.UAxis.Scale == 0 || face.VAxis.Scale == 0 ) return FaceUv.Default;
		if ( face.LightmapScale <= 0 || face.SmoothingGroups < 0 ) return FaceUv.Default;
		var (uAxis, vAxis) = FaceUv.PlaneAxes( normal );
		var wantU = new VmfVec( uAxis.x, uAxis.y, uAxis.z );
		var wantV = new VmfVec( vAxis.x, vAxis.y, vAxis.z );
		if ( !face.UAxis.Axis.ApproxEquals( wantU, UvAxisTolerance ) ) return FaceUv.Default;
		if ( !face.VAxis.Axis.ApproxEquals( wantV, UvAxisTolerance ) ) return FaceUv.Default;
		defaulted = false;
		return new FaceUv(
			new Vec2( (float)face.UAxis.Offset, (float)face.VAxis.Offset ),
			new Vec2( (float)face.UAxis.Scale, (float)face.VAxis.Scale ),
			(float)face.Rotation,
			(float)face.LightmapScale,
			face.SmoothingGroups );
	}
}
