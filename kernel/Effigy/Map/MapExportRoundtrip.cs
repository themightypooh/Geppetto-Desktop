// Hammer Desktop: export round-trip validation (board task
// hammer-interop-export-roundtrip-validation, 2026-11-13).
//
// MapDocument -> OBJ/GLB (baked mesh + materials), -> VMF/.map (plane
// fidelity), -> native compile output (BSP + PVS + lightmaps). Each export
// is re-imported / re-read and compared with the source (brush counts,
// volumes, materials, entity counts, I/O); a failing round-trip is a named
// RoundtripIssue, never a silent export. Wired into the IssueEngine as
// ExportRoundtripRule (an export check over the IssueDocument model).
//
// NATIVE ONLY: no Hammer app, no shelling out to Hammer/Hammer++/VBSP/VVIS/
// VRAD. Backend / data-model / API only, no Desktop UI.
//
// Canonical input is VmfDocument (the VMF/.map transfer model,
// hammer-vmf-map-import-export, DONE). RECONCILED 2026-11-13: MapDocument
// HAS since landed (kernel/Effigy/Map/MapDocument.cs, namespace Effigy)
// and CompileInput.FromDocument(MapDocument) exists — but the shared tree
// currently does not compile MapDocument (EntityOutput shape mismatch, see
// handoff), so the adapters here (ToMapBrushes / ToCompileInput /
// ToIssueDocument) stay the seams the final integrator replaces with
// MapDocument mappings once that conflict is resolved. The
// dependency specs are honoured exactly as filed:
//   - VmfIo / Map220Io: plane-based sides, exact doubles, "R" formatting.
//   - MapBakeExport (hammer-obj-glb-map-export, DONE worker 17):
//     Bake + ExportObj are real; ExportGlb is a MINIMAL stub (header +
//     asset JSON, no mesh/material payload — see MapBakeExport.GlbNote).
//     CheckGlb validates the container and records the missing payload
//     as a BLOCKED path, it does not claim a GLB round-trip.
//     The real mesh round-trip (core GlbExport.Write -> GlbImport.Read)
//     is exercised in tests/MapExportRoundtripTests.cs, which may
//     reference the core project; this kernel file must stay engine-free
//     and core-free (Effigy.Kernel has no core reference).
//   - CompileRunner (hammer-compile-native-pipeline, DONE worker 22):
//     CompileInput.FromDocument(MapDocument) EXISTS now; the
//     adapter ToCompileInput here is that seam, same shape.
//   - IssueEngine: IIssueRule is honoured as filed; the rule runs the
//     VMF + .map text round-trips in memory (no file I/O, no compile).
//
// EXACTNESS (worker 16's bug class: a .map writer emitting the VMF scale
// form "[u] scale [v] scale" instead of the Valve 220 form
// "[u] [v] rotation xscale yscale"): plane points, UV axes, rotation,
// scales and materials are compared EXACTLY (double ==) on the VMF and
// .map paths, and the emitted .map text is checked structurally (no
// number may sit between the two UV brackets; exactly three numbers
// follow them). The sealed-room fixture carries fractional, non-dyadic
// coordinates (crate top 64.1) so a truncating writer is caught, not
// forgiven. The OBJ path is float text (6 decimals) and compares at
// 1e-4; the compile path is float planes and compares at 1e-3.
//
// KNOWN LOSSES, reported never silently dropped:
//   - Valve 220 .map has no displacements, entity I/O, visgroups,
//     cameras, cordon, versioninfo, lightmap scale or smoothing groups:
//     Map220Io.Write lists them in its warnings and CompareDocuments
//     skips those fields on the map220 path only.
//   - Displacements are preserved by the VMF path and are NOT carried
//     into the OBJ/GLB bake or the compile input by this harness
//     (MapBakeDisplacement.FromDisplacement takes Effigy.Map.Displacement,
//     not VmfDisplacement — the conversion is a later ticket).
//   - Brush-entity detail flags are not carried into the compile input
//     (no IsDetail on the transfer model yet).
//   - Valve 220 .map carries no brush/entity/face ids: CompareDocuments
//     skips id comparison on the map220 path (CheckMap220 notes it).
//   - ObjReader only recovers material_N slots, so a named-usemtl OBJ
//     re-imports every face on slot 0: CompareObjMesh keys topology on
//     vertex indices + the per-face usemtl NAME parsed from the OBJ text
//     (ScanFaceMaterials), never on the reader slot number.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

namespace Effigy.Map;

/// <summary>One named round-trip failure (or blockage). A failing export is
/// this, never a silent success.</summary>
public sealed class RoundtripIssue
{
	public string Path = "";
	public string Code = "";
	public string Message = "";
	public string FixHint = "";
	public IssueSeverity Severity = IssueSeverity.Error;
	public int BrushId = -1;
	public int FaceId = -1;
	public string Material = "";

	public override string ToString() => $"[{Path} {Code}] {Message}";
}

/// <summary>What one export path did: the comparison counts, the named
/// issues, and whether the path could even run at full fidelity
/// (<see cref="Blocked"/> — the GLB payload stub — is not a pass).</summary>
public sealed class RoundtripPathResult
{
	public string Path = "";
	public IReadOnlyList<RoundtripIssue> Issues = Array.Empty<RoundtripIssue>();
	public bool Blocked;
	public string BlockedReason = "";
	public int SourceBrushCount;
	public int ResultBrushCount;
	public int SourceFaceCount;
	public int ResultFaceCount;
	public int SourceMaterialCount;
	public int ResultMaterialCount;
	public int SourceEntityCount;
	public int ResultEntityCount;
	public IReadOnlyList<string> Notes = Array.Empty<string>();

	public bool Passed => !Blocked && Issues.Count == 0;
}

/// <summary>All five paths for one document: vmf, map220, obj, glb,
/// compile. <see cref="Passed"/> is false while any path fails or is
/// blocked (today: the GLB mesh payload, worker 17's wiring).</summary>
public sealed class MapExportRoundtripResult
{
	public IReadOnlyList<RoundtripPathResult> Paths = Array.Empty<RoundtripPathResult>();
	public bool Passed => Paths.Count > 0 && Paths.All( p => p.Passed );
	public IReadOnlyList<RoundtripIssue> AllIssues => Paths.SelectMany( p => p.Issues ).ToList();

	/// <summary>Round-trip failures as IssueEngine issues, so an export
	/// check can run wherever the engine runs. Material losses map to
	/// <see cref="IssueKind.MissingMaterial"/>; everything else to
	/// <see cref="IssueKind.InvalidBrush"/>.</summary>
	public IReadOnlyList<Issue> ToIssues() => AllIssues.Select( MapExportRoundtrip.ToIssue ).ToList();
}

/// <summary>Export -&gt; re-import -&gt; compare harness for all three map
/// export paths, plus the IssueEngine export check.</summary>
public static class MapExportRoundtrip
{
	// ----- the sealed-room fixture -------------------------------------

	/// <summary>Interior 256x256x128, 16-unit walls, plus a crate with
	/// fractional, non-dyadic coordinates (top 64.1) for the
	/// exact-precision checks.</summary>
	public const string SealedRoomName = "sealed-room";

	public const string FloorMaterial = "concrete/concretefloor001";
	public const string CeilingMaterial = "concrete/concreteceiling001";
	public const string WallMaterial = "brick/brickwall001";
	public const string CrateMaterial = "wood/woodbox001";

	/// <summary>6 room brushes + 1 crate.</summary>
	public const int SealedRoomBrushCount = 7;
	/// <summary>7 brushes x 6 faces.</summary>
	public const int SealedRoomFaceCount = 42;

	/// <summary>Exact vertex text a truncating writer would corrupt:
	/// the crate's non-dyadic top (64.1) and fractional corners.</summary>
	public const string ExactVertexProbe = "( -48.125 16.375 64.1 )";

	/// <summary>The canonical sealed room: floor, ceiling, four walls,
	/// one crate, info_player_start + light. Brush ids 1..7, face ids
	/// 1..6 per brush, faces in -X, +X, -Y, +Y, -Z, +Z order (the
	/// MapBakeBrush.Box order), plane points wound so each face normal
	/// points out of its solid.</summary>
	public static VmfDocument SealedRoom()
	{
		var doc = new VmfDocument();
		doc.WorldspawnSet( "skyname", "sky_day01_01" );
		doc.WorldBrushes.Add( BoxBrush( 1, new VmfVec( -144, -144, -16 ), new VmfVec( 144, 144, 0 ), FloorMaterial ) );
		doc.WorldBrushes.Add( BoxBrush( 2, new VmfVec( -144, -144, 128 ), new VmfVec( 144, 144, 144 ), CeilingMaterial ) );
		doc.WorldBrushes.Add( BoxBrush( 3, new VmfVec( -144, -144, 0 ), new VmfVec( -128, 144, 128 ), WallMaterial ) );
		doc.WorldBrushes.Add( BoxBrush( 4, new VmfVec( 128, -144, 0 ), new VmfVec( 144, 144, 128 ), WallMaterial ) );
		doc.WorldBrushes.Add( BoxBrush( 5, new VmfVec( -128, -144, 0 ), new VmfVec( 128, -128, 128 ), WallMaterial ) );
		doc.WorldBrushes.Add( BoxBrush( 6, new VmfVec( -128, 128, 0 ), new VmfVec( 128, 144, 128 ), WallMaterial ) );
		doc.WorldBrushes.Add( BoxBrush( 7, new VmfVec( -48.125, 16.375, 0 ), new VmfVec( -16.0625, 48.5, 64.1 ), CrateMaterial ) );

		var start = new VmfEntity { Id = 1, ClassName = "info_player_start" };
		start.Set( "origin", "0 0 32" );
		start.Set( "angles", "0 0 0" );
		doc.Entities.Add( start );

		var light = new VmfEntity { Id = 2, ClassName = "light" };
		light.Set( "origin", "0 0 96" );
		light.Set( "_light", "255 240 200 400" );
		doc.Entities.Add( light );
		return doc;
	}

	/// <summary>One axis-aligned box brush. Faces -X, +X, -Y, +Y, -Z, +Z;
	/// the three plane points of each face are wound so the face normal
	/// (right-hand rule) points out of the solid.</summary>
	public static VmfBrush BoxBrush( int id, VmfVec min, VmfVec max, string material )
	{
		var x0 = min.X; var y0 = min.Y; var z0 = min.Z;
		var x1 = max.X; var y1 = max.Y; var z1 = max.Z;
		var brush = new VmfBrush { Id = id };
		var faces = new[]
		{
			new[] { new VmfVec( x0, y1, z0 ), new VmfVec( x0, y0, z0 ), new VmfVec( x0, y0, z1 ) }, // -X
			new[] { new VmfVec( x1, y0, z0 ), new VmfVec( x1, y1, z0 ), new VmfVec( x1, y1, z1 ) }, // +X
			new[] { new VmfVec( x0, y0, z0 ), new VmfVec( x1, y0, z0 ), new VmfVec( x1, y0, z1 ) }, // -Y
			new[] { new VmfVec( x1, y1, z0 ), new VmfVec( x0, y1, z0 ), new VmfVec( x0, y1, z1 ) }, // +Y
			new[] { new VmfVec( x0, y1, z0 ), new VmfVec( x1, y1, z0 ), new VmfVec( x1, y0, z0 ) }, // -Z
			new[] { new VmfVec( x0, y0, z1 ), new VmfVec( x1, y0, z1 ), new VmfVec( x1, y1, z1 ) }, // +Z
		};
		for ( var i = 0; i < faces.Length; i++ )
			brush.Faces.Add( new VmfFace { Id = i + 1, PlanePoints = faces[i], Material = material } );
		return brush;
	}

	// ----- adapters (seams: MapDocument has not landed) ----------------

	/// <summary>VmfDocument -&gt; plane-based MapBrushes for the bake path.
	/// Double plane (normal, distance) narrows to float; the exactness
	/// bar lives on the VMF/.map paths, not here.</summary>
	public static List<MapBrush> ToMapBrushes( VmfDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		var brushes = new List<MapBrush>();
		foreach ( var b in doc.AllBrushes() )
			brushes.Add( MapBrush.FromPlanes( b.Faces.Select( f =>
				new global::Effigy.MapBrushFace( ToVec3( f.Normal ), (float)f.Distance, f.Material ) ) ) );
		return brushes;
	}

	/// <summary>VmfDocument -&gt; compile brushes. The CompileInput
	/// seam: when CompileInput.FromDocument lands, this body moves
	/// there unchanged.</summary>
	public static List<CompileBrush> ToCompileBrushes( VmfDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		var brushes = new List<CompileBrush>();
		foreach ( var b in doc.AllBrushes() )
		{
			var faces = b.Faces.Select( f =>
			{
				var cf = new CompileFace( ToVec3( f.Normal ), (float)f.Distance, f.Material );
				cf.LightmapScale = f.LightmapScale / 16f;
				return cf;
			} );
			brushes.Add( new CompileBrush( "brush" + b.Id, b.Id, faces ) );
		}
		return brushes;
	}

	/// <summary>VmfDocument -&gt; CompileInput: brushes, player start from
	/// info_player_start, lights from light entities ("_light" key,
	/// "R G B intensity"; a missing light falls back to one default
	/// light at the room centre so the bake still has a source).</summary>
	public static CompileInput ToCompileInput( VmfDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		var input = new CompileInput( SealedRoomName );
		input.Brushes.AddRange( ToCompileBrushes( doc ) );
		foreach ( var e in doc.Entities )
		{
			if ( e.ClassName == "info_player_start" && input.PlayerStart == null )
			{
				var origin = ParseOrigin( e.Get( "origin" ) );
				if ( origin != null ) input.PlayerStart = origin.Value;
			}
			else if ( e.ClassName == "light" )
			{
				var origin = ParseOrigin( e.Get( "origin" ) );
				if ( origin != null ) input.Lights.Add( ParseLight( origin.Value, e.Get( "_light" ) ?? e.Get( "light" ) ) );
			}
		}
		if ( input.Lights.Count == 0 )
			input.Lights.Add( new CompileLight( new Vec3( 0, 0, 64 ), 1f ) );
		return input;
	}

	/// <summary>VmfDocument -&gt; IssueDocument for the engine rule.
	/// Face polygons come from the no-drift record: the clone's stored
	/// vertices, solved from the planes.</summary>
	public static IssueDocument ToIssueDocument( VmfDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		var copy = doc.Clone();
		copy.AttachStoredVertices();
		var issue = new IssueDocument { Name = SealedRoomName };
		foreach ( var b in copy.AllBrushes() )
		{
			var faces = b.Faces.Select( f => new IssueFace(
				ToVec3( f.Normal ), f.Distance, f.Material,
				f.StoredVertices.Select( v => ToVec3( v ) ).ToList() ) );
			issue.Brushes.Add( new IssueBrush( b.Id, faces ) );
		}
		foreach ( var e in copy.Entities )
		{
			var origin = ParseOrigin( e.Get( "origin" ) ) ?? Vec3.Zero;
			var entity = new IssueEntity( e.Id, e.ClassName, origin );
			foreach ( var kv in e.KeyValues )
				if ( kv.Key != "origin" ) entity.SetKeyValue( kv.Key, kv.Value );
			issue.Entities.Add( entity );
		}
		return issue;
	}

	/// <summary>IssueDocument -&gt; VmfDocument, for the engine rule: plane
	/// points are rebuilt from each face's (normal, distance) — three
	/// points on the plane, so the plane survives even though the
	/// original points are gone. Non-finite normals rebuild to NaN
	/// points, which the writer silently zeroes (MapIoText.Num) — the
	/// comparison then flags the loss instead of hiding it.</summary>
	public static VmfDocument FromIssueDocument( IssueDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		var vmf = new VmfDocument();
		foreach ( var b in doc.Brushes )
		{
			if ( b == null ) continue;
			var brush = new VmfBrush { Id = b.Id };
			for ( var i = 0; i < b.Faces.Count; i++ )
			{
				var f = b.Faces[i];
				var n = ToVmfVec( f.Normal );
				var points = PlanePointsFromNormalDistance( n, f.Distance );
				brush.Faces.Add( new VmfFace { Id = i + 1, PlanePoints = points, Material = f.Material ?? "" } );
			}
			vmf.WorldBrushes.Add( brush );
		}
		foreach ( var e in doc.Entities )
		{
			if ( e == null ) continue;
			var entity = new VmfEntity { Id = e.Id, ClassName = e.ClassName ?? "" };
			entity.Set( "origin", $"{e.Origin.x} {e.Origin.y} {e.Origin.z}" );
			foreach ( var kv in e.KeyValues )
				if ( kv.Key != "origin" ) entity.Set( kv.Key, kv.Value );
			vmf.Entities.Add( entity );
		}
		return vmf;
	}

	static VmfVec[] PlanePointsFromNormalDistance( VmfVec n, double d )
	{
		var p0 = n * d;
		var axis = Math.Abs( n.X ) < 0.9 ? new VmfVec( 1, 0, 0 ) : new VmfVec( 0, 1, 0 );
		var u = VmfVec.Cross( n, axis ).Normalized;
		var v = VmfVec.Cross( n, u );
		return new[] { p0, p0 + u, p0 + v };
	}

	// ----- document comparison (the exactness core) --------------------

	/// <summary>Compare two documents field by field. VMF path compares
	/// everything; the map220 path skips what Valve 220 .map cannot
	/// carry (versioninfo, visgroups, cameras, cordon, displacements,
	/// entity I/O, lightmap scale, smoothing groups, stored vertices) —
	/// those losses are the writer's warnings, not silent drops.
	/// <paramref name="exactPlanePoints"/> true means plane points,
	/// UV axes, rotation and scales compare with double == (the VMF /
	/// .map text bar); false compares plane normal + distance at 1e-6
	/// (the IssueEngine rule, whose input is float planes).</summary>
	public static IReadOnlyList<RoundtripIssue> CompareDocuments( VmfDocument expected, VmfDocument actual, string path, bool exactPlanePoints = true )
	{
		if ( expected == null ) throw new ArgumentNullException( nameof( expected ) );
		if ( actual == null ) throw new ArgumentNullException( nameof( actual ) );
		var issues = new List<RoundtripIssue>();
		var code = "EXPORT_ROUNDTRIP_" + path.ToUpperInvariant();
		var isMap = path == "map220";
		var tol = exactPlanePoints ? 0.0 : 1e-6;

		void Add( string suffix, string message, string fix, int brushId = -1, int faceId = -1, string material = "" ) =>
			issues.Add( new RoundtripIssue { Path = path, Code = code + "_" + suffix, Message = message, FixHint = fix, BrushId = brushId, FaceId = faceId, Material = material } );

		if ( !isMap )
		{
			if ( expected.EditorVersion != actual.EditorVersion || expected.EditorBuild != actual.EditorBuild
				|| expected.MapVersion != actual.MapVersion || expected.FormatVersion != actual.FormatVersion || expected.Prefab != actual.Prefab )
				Add( "VERSION", $"versioninfo changed: editor {expected.EditorVersion}/{expected.EditorBuild} map {expected.MapVersion} format {expected.FormatVersion} prefab {expected.Prefab} -> {actual.EditorVersion}/{actual.EditorBuild} map {actual.MapVersion} format {actual.FormatVersion} prefab {actual.Prefab}",
					"Keep versioninfo identical through a VMF round-trip; a writer that rewrites it is lossy." );
			if ( expected.VisGroups.Count != actual.VisGroups.Count )
				Add( "VISGROUP_COUNT", $"visgroup count {expected.VisGroups.Count} != {actual.VisGroups.Count}", "Preserve visgroups through a VMF round-trip." );
			else for ( var i = 0; i < expected.VisGroups.Count; i++ )
			{
				var e = expected.VisGroups[i]; var a = actual.VisGroups[i];
				if ( e.Id != a.Id || e.Name != a.Name || e.Color != a.Color )
					Add( "VISGROUP", $"visgroup {i} '{e.Name}'#{e.Id} '{e.Color}' -> '{a.Name}'#{a.Id} '{a.Color}'", "Preserve visgroups through a VMF round-trip." );
			}
			if ( expected.Cameras.Count != actual.Cameras.Count )
				Add( "CAMERA_COUNT", $"camera count {expected.Cameras.Count} != {actual.Cameras.Count}", "Preserve cameras through a VMF round-trip." );
			if ( expected.HasCordon != actual.HasCordon || expected.CordonActive != actual.CordonActive
				|| !expected.CordonMins.Equals( actual.CordonMins ) || !expected.CordonMaxs.Equals( actual.CordonMaxs ) )
				Add( "CORDON", "cordon block changed through a VMF round-trip", "Preserve the cordon block through a VMF round-trip." );
			if ( expected.OtherBlocks.Count != actual.OtherBlocks.Count )
				Add( "OTHER_BLOCKS", $"unrecognised top-level block count {expected.OtherBlocks.Count} != {actual.OtherBlocks.Count}", "Preserve unrecognised VMF blocks verbatim through a VMF round-trip." );
		}

		if ( !KeyValuesEqual( expected.WorldspawnKeyValues, actual.WorldspawnKeyValues ) )
			Add( "WORLDSPAWN", "worldspawn keyvalues changed", "Preserve worldspawn keyvalues in order through the round-trip." );

		// KNOWN LOSS (Valve 220 has no id fields at all — no brush, face or
		// entity ids survive a .map round-trip): ids compare on the VMF path
		// only. Planes, UVs, materials and keyvalues still compare exactly.
		var eBrushes = expected.AllBrushes().ToList();
		var aBrushes = actual.AllBrushes().ToList();
		if ( expected.WorldBrushes.Count != actual.WorldBrushes.Count )
			Add( "BRUSH_COUNT", $"world brush count {expected.WorldBrushes.Count} != {actual.WorldBrushes.Count}", "A brush was lost or gained; count world brushes before and after the export." );
		if ( eBrushes.Count != aBrushes.Count )
			Add( "BRUSH_COUNT_ALL", $"total brush count {eBrushes.Count} != {aBrushes.Count}", "A brush was lost or gained; count all brushes before and after the export." );
		for ( var i = 0; i < Math.Min( eBrushes.Count, aBrushes.Count ); i++ )
		{
			var e = eBrushes[i]; var a = aBrushes[i];
			if ( !isMap && e.Id != a.Id )
				Add( "BRUSH_ID", $"brush {i} id {e.Id} != {a.Id}", "Preserve brush ids through the round-trip.", e.Id );
			if ( e.Faces.Count != a.Faces.Count )
			{
				Add( "FACE_COUNT", $"brush #{e.Id} face count {e.Faces.Count} != {a.Faces.Count}", "A face was lost or gained; count faces per brush before and after the export.", e.Id );
				continue;
			}
			for ( var fi = 0; fi < e.Faces.Count; fi++ ) CompareFace( e.Faces[fi], a.Faces[fi], e.Id, issues, code, path, exactPlanePoints, tol, isMap );
		}

		if ( expected.Entities.Count != actual.Entities.Count )
			Add( "ENTITY_COUNT", $"entity count {expected.Entities.Count} != {actual.Entities.Count}", "An entity was lost or gained; count entities before and after the export." );
		for ( var i = 0; i < Math.Min( expected.Entities.Count, actual.Entities.Count ); i++ )
		{
			var e = expected.Entities[i]; var a = actual.Entities[i];
			if ( !isMap && e.Id != a.Id )
				Add( "ENTITY_ID", $"entity {i} id {e.Id} != {a.Id}", "Preserve entity ids through the round-trip.", -1, -1, "" );
			if ( e.ClassName != a.ClassName )
				Add( "ENTITY_CLASS", $"entity #{e.Id} classname '{e.ClassName}' != '{a.ClassName}'", "Preserve entity classnames through the round-trip." );
			if ( !KeyValuesEqual( e.KeyValues, a.KeyValues ) )
				Add( "ENTITY_KEYVALUES", $"entity #{e.Id} '{e.ClassName}' keyvalues changed", "Preserve entity keyvalues in order through the round-trip." );
			if ( e.Brushes.Count != a.Brushes.Count )
				Add( "ENTITY_BRUSH_COUNT", $"entity #{e.Id} '{e.ClassName}' brush count {e.Brushes.Count} != {a.Brushes.Count}", "A brush-entity brush was lost or gained." );
			if ( !isMap )
			{
				if ( e.Outputs.Count != a.Outputs.Count )
					Add( "ENTITY_IO_COUNT", $"entity #{e.Id} '{e.ClassName}' I/O output count {e.Outputs.Count} != {a.Outputs.Count}", "Preserve entity I/O through a VMF round-trip." );
				else for ( var oi = 0; oi < e.Outputs.Count; oi++ )
				{
					var eo = e.Outputs[oi]; var ao = a.Outputs[oi];
					if ( eo.Event != ao.Event || eo.Target != ao.Target || eo.Input != ao.Input || eo.Parameter != ao.Parameter
						|| eo.Delay != ao.Delay || eo.FireCount != ao.FireCount )
						Add( "ENTITY_IO", $"entity #{e.Id} output {oi} '{eo.Event}' -> '{eo.Target}' changed", "Preserve entity I/O rows exactly through a VMF round-trip." );
				}
			}
		}
		return issues;
	}

	static void CompareFace( VmfFace e, VmfFace a, int brushId, List<RoundtripIssue> issues, string code, string path, bool exact, double tol, bool isMap )
	{
		void Add( string suffix, string message, string fix ) =>
			issues.Add( new RoundtripIssue { Path = path, Code = code + "_" + suffix, Message = message, FixHint = fix, BrushId = brushId, FaceId = e.Id, Material = e.Material } );

		if ( !isMap && e.Id != a.Id )
			Add( "FACE_ID", $"brush #{brushId} face id {e.Id} != {a.Id}", "Preserve face ids through the round-trip." );
		if ( exact )
		{
			for ( var i = 0; i < 3; i++ )
				if ( e.PlanePoints[i].X != a.PlanePoints[i].X || e.PlanePoints[i].Y != a.PlanePoints[i].Y || e.PlanePoints[i].Z != a.PlanePoints[i].Z )
				{
					Add( "PLANE_DRIFT", $"brush #{brushId} face #{e.Id} plane point {i} {e.PlanePoints[i].ToVmfString()} -> {a.PlanePoints[i].ToVmfString()}: the writer did not serialise the vertex exactly (double == required)",
						"Serialise plane points with full round-trip precision (\"R\", invariant culture); never truncate, round or rebuild them from triangulated vertices." );
					break;
				}
		}
		else if ( !e.Normal.ApproxEquals( a.Normal, tol ) || Math.Abs( e.Distance - a.Distance ) > tol )
			Add( "PLANE", $"brush #{brushId} face #{e.Id} plane normal {e.Normal.ToVmfString()} d={e.Distance:G17} -> {a.Normal.ToVmfString()} d={a.Distance:G17}",
				"Preserve face planes through the round-trip; a drifting plane moves the solid." );
		if ( e.Material != a.Material )
			Add( "MATERIAL", $"brush #{brushId} face #{e.Id} material '{e.Material}' -> '{a.Material}': a material was dropped or renamed",
				"Preserve every face material verbatim; a dropped material is a failed export, not a default." );
		if ( !AxisEqual( e.UAxis, a.UAxis, tol ) || !AxisEqual( e.VAxis, a.VAxis, tol ) )
			Add( "UV", $"brush #{brushId} face #{e.Id} UV axes changed (U {e.UAxis.ToVmfString()} / V {e.VAxis.ToVmfString()} -> U {a.UAxis.ToVmfString()} / V {a.VAxis.ToVmfString()})",
				"Preserve explicit UV axes through the round-trip; a shifted axis re-textures the face." );
		if ( exact ? e.Rotation != a.Rotation : Math.Abs( e.Rotation - a.Rotation ) > tol )
			Add( "ROTATION", $"brush #{brushId} face #{e.Id} rotation {e.Rotation:G17} -> {a.Rotation:G17}", "Preserve UV rotation through the round-trip." );
		if ( e.UvFromStandardMap != a.UvFromStandardMap )
			Add( "UV_FORM", $"brush #{brushId} face #{e.Id} UV form changed (legacy .map form: {e.UvFromStandardMap} -> {a.UvFromStandardMap})",
				"A Valve 220 face must stay Valve 220; a legacy-form face must stay legacy." );
		if ( !isMap )
		{
			if ( e.LightmapScale != a.LightmapScale )
				Add( "LIGHTMAP_SCALE", $"brush #{brushId} face #{e.Id} lightmap scale {e.LightmapScale} -> {a.LightmapScale}", "Preserve lightmap scale through a VMF round-trip." );
			if ( e.SmoothingGroups != a.SmoothingGroups )
				Add( "SMOOTHING", $"brush #{brushId} face #{e.Id} smoothing groups {e.SmoothingGroups} -> {a.SmoothingGroups}", "Preserve smoothing groups through a VMF round-trip." );
			if ( ( e.Displacement == null ) != ( a.Displacement == null ) )
				Add( "DISPLACEMENT", $"brush #{brushId} face #{e.Id} displacement presence changed", "Preserve displacements through a VMF round-trip." );
			else if ( e.Displacement != null && ( e.Displacement.Power != a.Displacement.Power
				|| !e.Displacement.StartPosition.Equals( a.Displacement.StartPosition )
				|| e.Displacement.Elevation != a.Displacement.Elevation ) )
				Add( "DISPLACEMENT", $"brush #{brushId} face #{e.Id} displacement header changed (power {e.Displacement.Power} -> {a.Displacement.Power})",
					"Preserve displacement power, start position and elevation through a VMF round-trip." );
			if ( e.StoredVertices.Count != a.StoredVertices.Count )
				Add( "STORED_VERTEX_COUNT", $"brush #{brushId} face #{e.Id} stored vertex count {e.StoredVertices.Count} != {a.StoredVertices.Count}",
					"Preserve the stored-vertex no-drift record through a VMF round-trip." );
			else for ( var i = 0; i < e.StoredVertices.Count; i++ )
				if ( !e.StoredVertices[i].Equals( a.StoredVertices[i] ) )
				{
					Add( "STORED_VERTEX", $"brush #{brushId} face #{e.Id} stored vertex {i} {e.StoredVertices[i].ToVmfString()} -> {a.StoredVertices[i].ToVmfString()}: the no-drift record drifted",
						"Write stored vertices verbatim with full round-trip precision." );
					break;
				}
		}
	}

	static bool AxisEqual( VmfUvAxis e, VmfUvAxis a, double tol ) =>
		tol == 0.0
			? e.Axis.X == a.Axis.X && e.Axis.Y == a.Axis.Y && e.Axis.Z == a.Axis.Z && e.Offset == a.Offset && e.Scale == a.Scale
			: e.Axis.ApproxEquals( a.Axis, tol ) && Math.Abs( e.Offset - a.Offset ) <= tol && Math.Abs( e.Scale - a.Scale ) <= tol;

	static bool KeyValuesEqual( IReadOnlyList<KeyValuePair<string, string>> e, IReadOnlyList<KeyValuePair<string, string>> a )
	{
		if ( e.Count != a.Count ) return false;
		for ( var i = 0; i < e.Count; i++ )
			if ( e[i].Key != a[i].Key || e[i].Value != a[i].Value ) return false;
		return true;
	}

	// ----- path 1: VMF -------------------------------------------------

	/// <summary>VMF text round-trip: write, re-parse, compare exactly
	/// (plane points, UV, materials, entities, I/O), plus the stored-
	/// vertex no-drift record (AttachStoredVertices first, then verify
	/// the re-parsed vertices sit on their planes and match exactly)
	/// and a text-level probe for the fractional crate vertex.</summary>
	public static RoundtripPathResult CheckVmf( VmfDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		var issues = new List<RoundtripIssue>();
		var work = doc.Clone();
		work.AttachStoredVertices();
		var text = VmfIo.Write( work );
		if ( !text.Contains( ExactVertexProbe, StringComparison.Ordinal ) )
			issues.Add( new RoundtripIssue { Path = "vmf", Code = "EXPORT_ROUNDTRIP_VMF_VERTEX_TEXT",
				Message = $"VMF text does not contain the exact fractional vertex {ExactVertexProbe}: the writer truncated or reformatted a coordinate",
				FixHint = "Serialise plane points with \"R\" (invariant culture); never truncate coordinates." } );
		var reparsed = VmfIo.Parse( text );
		issues.AddRange( CompareDocuments( work, reparsed, "vmf", exactPlanePoints: true ) );
		foreach ( var v in reparsed.VerifyStoredVertices() )
			issues.Add( new RoundtripIssue { Path = "vmf", Code = "EXPORT_ROUNDTRIP_VMF_STORED_VERTEX_OFF_PLANE",
				Message = v, FixHint = "Stored vertices must sit on their face plane; solve them from the planes, never invent them." } );
		return new RoundtripPathResult
		{
			Path = "vmf",
			Issues = issues,
			SourceBrushCount = work.AllBrushes().Count(),
			ResultBrushCount = reparsed.AllBrushes().Count(),
			SourceFaceCount = work.AllBrushes().Sum( b => b.Faces.Count ),
			ResultFaceCount = reparsed.AllBrushes().Sum( b => b.Faces.Count ),
			SourceMaterialCount = DistinctMaterials( work ).Count,
			ResultMaterialCount = DistinctMaterials( reparsed ).Count,
			SourceEntityCount = work.Entities.Count,
			ResultEntityCount = reparsed.Entities.Count,
		};
	}

	// ----- path 2: Valve 220 .map --------------------------------------

	/// <summary>Valve 220 .map text round-trip: write (warnings are
	/// named issues, never silent), re-parse, compare exactly, plus the
	/// structural scale-form check (worker 16's bug class: a scale after
	/// a single bracket is the VMF form and must not appear) and the
	/// exact fractional-vertex text probe.</summary>
	public static RoundtripPathResult CheckMap220( VmfDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		var issues = new List<RoundtripIssue>();
		var text = Map220Io.Write( doc, out var warnings );
		foreach ( var w in warnings )
			issues.Add( new RoundtripIssue { Path = "map220", Code = "EXPORT_ROUNDTRIP_MAP220_KNOWN_LOSS", Message = w,
				Severity = IssueSeverity.Warning,
				FixHint = "Valve 220 .map has no field for this; keep the VMF as the master copy and treat the .map as lossy for this feature." } );
		if ( !text.Contains( ExactVertexProbe, StringComparison.Ordinal ) )
			issues.Add( new RoundtripIssue { Path = "map220", Code = "EXPORT_ROUNDTRIP_MAP220_VERTEX_TEXT",
				Message = $".map text does not contain the exact fractional vertex {ExactVertexProbe}: the writer truncated or reformatted a coordinate",
				FixHint = "Serialise plane points with \"R\" (invariant culture); never truncate coordinates." } );
		foreach ( var line in text.Split( '\n' ) )
			CheckMap220FaceLineForm( line.Trim(), issues );
		var reparsed = Map220Io.Parse( text );
		issues.AddRange( CompareDocuments( doc, reparsed, "map220", exactPlanePoints: true ) );
		var mapNotes = new List<string> { "Valve 220 .map carries no brush/entity/face ids; ids are not preserved on this path (VMF is the master copy)." };
		return new RoundtripPathResult
		{
			Path = "map220",
			Issues = issues,
			SourceBrushCount = doc.AllBrushes().Count(),
			ResultBrushCount = reparsed.AllBrushes().Count(),
			SourceFaceCount = doc.AllBrushes().Sum( b => b.Faces.Count ),
			ResultFaceCount = reparsed.AllBrushes().Sum( b => b.Faces.Count ),
			SourceMaterialCount = DistinctMaterials( doc ).Count,
			ResultMaterialCount = DistinctMaterials( reparsed ).Count,
			SourceEntityCount = doc.Entities.Count,
			ResultEntityCount = reparsed.Entities.Count,
			Notes = mapNotes,
		};
	}

	/// <summary>Structural guard for the worker-16 bug class: in Valve
	/// 220 form the two UV brackets are followed by "rotation xscale
	/// yscale" and nothing sits between the brackets. A scale after the
	/// first bracket is the VMF form, which the .map parser rejects —
	/// and which this check catches in the emitted text first.</summary>
	static void CheckMap220FaceLineForm( string line, List<RoundtripIssue> issues )
	{
		if ( !line.StartsWith( "(", StringComparison.Ordinal ) ) return;
		void Add( string message ) => issues.Add( new RoundtripIssue { Path = "map220", Code = "EXPORT_ROUNDTRIP_MAP220_SCALE_FORM",
			Message = message, FixHint = "Write Valve 220 faces as \"[u] [v] rotation xscale yscale\" — the scale follows BOTH brackets, never one." } );
		var firstClose = line.IndexOf( ']' );
		if ( firstClose < 0 )
		{
			// Legacy (non-220) form: MATERIAL xoff yoff rotation xscale yscale.
			var tokens = line.Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( tokens.Length < 16 || !tokens.Skip( tokens.Length - 5 ).All( IsNumber ) )
				Add( $"legacy .map face line has an unexpected shape: '{line}'" );
			return;
		}
		var secondOpen = line.IndexOf( '[', firstClose );
		if ( secondOpen < 0 ) { Add( $"Valve 220 face line has only one UV bracket: '{line}'" ); return; }
		var between = line.Substring( firstClose + 1, secondOpen - firstClose - 1 );
		foreach ( var ch in between )
			if ( char.IsDigit( ch ) )
			{
				Add( $"Valve 220 face line carries a scale after the first UV bracket (VMF form): '{line}'" );
				return;
			}
		var secondClose = line.IndexOf( ']', secondOpen );
		if ( secondClose < 0 ) { Add( $"Valve 220 face line has an unclosed second UV bracket: '{line}'" ); return; }
		var tail = line.Substring( secondClose + 1 ).Trim().Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( tail.Length != 3 || !tail.All( IsNumber ) )
			Add( $"Valve 220 face line must end with 'rotation xscale yscale' after both brackets: '{line}'" );
	}

	static bool IsNumber( string token ) =>
		double.TryParse( token, NumberStyles.Float, CultureInfo.InvariantCulture, out _ );

	// ----- path 3a: OBJ (baked mesh) -----------------------------------

	/// <summary>Bake to a static mesh, export OBJ (+ .mtl) through
	/// MapBakeExport, re-import with ObjReader, compare face counts,
	/// vertex positions (1e-4 — OBJ is 6-decimal float text), face
	/// topology + per-face material slots (as multisets: the writer
	/// groups faces by material), corner UVs, and the material name
	/// lists from the .mtl and the usemtl runs.</summary>
	public static RoundtripPathResult CheckObj( VmfDocument doc, string workDir, MapBakeOptions options = null )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		if ( string.IsNullOrWhiteSpace( workDir ) ) throw new ArgumentException( "OBJ export needs a work directory.", nameof( workDir ) );
		Directory.CreateDirectory( workDir );
		var issues = new List<RoundtripIssue>();
		var baked = MapBakeExport.Bake( ToMapBrushes( doc ), new List<MapBakeDisplacement>(), options );
		foreach ( var b in baked.Issues )
			issues.Add( new RoundtripIssue { Path = "obj", Code = "EXPORT_ROUNDTRIP_OBJ_BAKE", Message = b,
				FixHint = "Fix the bake defect named here; the OBJ export cannot round-trip a mesh the bake rejected." } );
		var objPath = Path.Combine( workDir, SealedRoomName + ".obj" );
		var exported = MapBakeExport.ExportObj( baked, objPath, options );
		if ( !exported.ObjWritten || !File.Exists( objPath ) )
			issues.Add( new RoundtripIssue { Path = "obj", Code = "EXPORT_ROUNDTRIP_OBJ_NOT_WRITTEN",
				Message = $"OBJ export reported written={exported.ObjWritten} but '{objPath}' is missing",
				FixHint = "ExportObj must write the file it claims to write." } );
		var notes = new List<string>();
		if ( !string.IsNullOrWhiteSpace( exported.Note ) ) notes.Add( exported.Note );
		PolyMesh reimported = null;
		List<string> usemtlNames = null;
		List<string> mtlNames = null;
		if ( File.Exists( objPath ) )
		{
			reimported = ObjReader.ReadFile( objPath );
			var objText = File.ReadAllText( objPath );
			usemtlNames = ScanNames( objText, "usemtl" );
			if ( File.Exists( exported.MtlPath ) )
				mtlNames = ScanNames( File.ReadAllText( exported.MtlPath ), "newmtl" );
			else
				issues.Add( new RoundtripIssue { Path = "obj", Code = "EXPORT_ROUNDTRIP_OBJ_MTL_MISSING",
					Message = $"OBJ export wrote no .mtl next to '{objPath}'",
					FixHint = "ExportObj must write the .mtl the OBJ references." } );
			if ( mtlNames != null )
			{
				var expected = baked.Materials.Select( MapBakeExport.SanitizeMaterialName ).ToList();
				if ( !expected.SequenceEqual( mtlNames ) )
					issues.Add( new RoundtripIssue { Path = "obj", Code = "EXPORT_ROUNDTRIP_OBJ_MATERIAL",
						Message = $".mtl materials [{string.Join( ", ", mtlNames )}] != baked [{string.Join( ", ", expected )}]",
						FixHint = "Preserve every baked material name in the .mtl; a dropped material is a failed export." } );
			}
			issues.AddRange( CompareObjMesh( baked, reimported, usemtlNames, ScanFaceMaterials( objText ) ) );
		}
		return new RoundtripPathResult
		{
			Path = "obj",
			Issues = issues,
			Notes = notes,
			SourceBrushCount = doc.AllBrushes().Count(),
			ResultBrushCount = doc.AllBrushes().Count(),
			SourceFaceCount = baked.FaceCount,
			ResultFaceCount = reimported == null ? 0 : reimported.FaceCount,
			SourceMaterialCount = baked.Materials.Count,
			ResultMaterialCount = usemtlNames == null ? 0 : usemtlNames.Count,
			SourceEntityCount = doc.Entities.Count,
			ResultEntityCount = doc.Entities.Count,
		};
	}

	/// <summary>Baked mesh vs re-imported OBJ. Public so a test can feed
	/// a TAMPERED re-import (e.g. a usemtl swapped to another material)
	/// and prove the comparison catches it.</summary>
	public static IReadOnlyList<RoundtripIssue> CompareObjMesh( MapBakeMesh baked, PolyMesh reimported, IReadOnlyList<string> reimportedMaterialNames )
		=> CompareObjMesh( baked, reimported, reimportedMaterialNames, null );

	/// <summary>As <see cref="CompareObjMesh(MapBakeMesh, PolyMesh, IReadOnlyList{string})"/>,
	/// plus per-face material names in file order (one per re-imported face, from
		/// <see cref="ScanFaceMaterials"/>): topology then compares indices AND the
		/// material name on each face, so a face re-assigned to another material is
		/// caught even when the distinct-name set is unchanged.</summary>
	public static IReadOnlyList<RoundtripIssue> CompareObjMesh( MapBakeMesh baked, PolyMesh reimported, IReadOnlyList<string> reimportedMaterialNames, IReadOnlyList<string> reimportedFaceMaterialNames )
	{
		if ( baked == null ) throw new ArgumentNullException( nameof( baked ) );
		if ( reimported == null ) throw new ArgumentNullException( nameof( reimported ) );
		var issues = new List<RoundtripIssue>();
		void Add( string suffix, string message, string fix ) =>
			issues.Add( new RoundtripIssue { Path = "obj", Code = "EXPORT_ROUNDTRIP_OBJ_" + suffix, Message = message, FixHint = fix } );

		if ( baked.FaceCount != reimported.FaceCount )
			Add( "FACE_COUNT", $"OBJ face count {reimported.FaceCount} != baked {baked.FaceCount}", "Every baked face must survive the OBJ round-trip." );
		if ( baked.VertexCount != reimported.VertexCount )
			Add( "VERTEX_COUNT", $"OBJ vertex count {reimported.VertexCount} != baked {baked.VertexCount}", "Every baked vertex must survive the OBJ round-trip." );
		else
		{
			var drifted = 0;
			for ( var i = 0; i < baked.VertexCount; i++ )
			{
				var a = baked.Mesh.Positions[i]; var b = reimported.Positions[i];
				if ( Math.Abs( a.x - b.x ) > 1e-4f || Math.Abs( a.y - b.y ) > 1e-4f || Math.Abs( a.z - b.z ) > 1e-4f )
				{
					if ( drifted < 3 )
						Add( "VERTEX_DRIFT", $"OBJ vertex {i} {a} -> {b}: drift over 1e-4 (OBJ is 6-decimal float text)", "Write OBJ vertices with enough decimals; a vertex that moves is a broken export." );
					drifted++;
				}
			}
			if ( drifted > 3 )
				Add( "VERTEX_DRIFT", $"{drifted} OBJ vertices drifted over 1e-4 in total", "Write OBJ vertices with enough decimals; vertices that move are a broken export." );
		}
		// Face topology + per-face material, as multisets: the OBJ writer
		// groups faces by material, so file order is not bake order. Keys
		// use the MATERIAL NAME, never the reader slot: ObjReader only
		// recovers material_N slots (see ObjWriter.SlotFromMaterialName),
		// so a named-usemtl OBJ re-imports every face on slot 0 and slot
		// numbers cannot be compared. The 4-arg overload (used by
		// CheckObj, which parses per-face names from the OBJ text) keys on
		// the name; this 3-arg overload keys on indices only and relies on
		// the MATERIAL name-set check below for material coverage.
		var bakedKeys = baked.Mesh.Faces.Select( f => string.Join( ",", f.Indices ) ).OrderBy( k => k, StringComparer.Ordinal ).ToList();
		var reKeys = reimported.Faces.Select( f => string.Join( ",", f.Indices ) ).OrderBy( k => k, StringComparer.Ordinal ).ToList();
		if ( reimportedFaceMaterialNames != null )
		{
			if ( reimportedFaceMaterialNames.Count == reimported.FaceCount )
			{
				bakedKeys = baked.Mesh.Faces.Select( f => MapBakeExport.SanitizeMaterialName( baked.Materials[f.Material] ) + ":" + string.Join( ",", f.Indices ) ).OrderBy( k => k, StringComparer.Ordinal ).ToList();
				reKeys = reimported.Faces.Select( ( f, i ) => ( reimportedFaceMaterialNames[i] ?? "" ) + ":" + string.Join( ",", f.Indices ) ).OrderBy( k => k, StringComparer.Ordinal ).ToList();
			}
			else
				Add( "TOPOLOGY", $"per-face material names ({reimportedFaceMaterialNames.Count}) do not line up with re-imported faces ({reimported.FaceCount}); topology compared by indices only",
					"One usemtl name per 'f' line, in file order; a face without a preceding usemtl has no material to compare." );
		}
		if ( !bakedKeys.SequenceEqual( reKeys, StringComparer.Ordinal ) )
			Add( "TOPOLOGY", $"OBJ face topology/material slots differ from the baked mesh ({bakedKeys.Count} baked faces vs {reKeys.Count} re-imported; first difference at sorted position {FirstDifference( bakedKeys, reKeys )})",
				"Preserve every face's corners and material slot through the OBJ round-trip; a swapped slot is a dropped material." );
		// Corner UVs as multisets (V is flipped out and back; 1e-4 grid).
		var bakedUvs = baked.Mesh.Faces.SelectMany( f => f.UVs ?? Array.Empty<Vec2>() ).Select( u => ( (long)Math.Round( u.x * 1e4 ), (long)Math.Round( u.y * 1e4 ) ) ).OrderBy( u => u.Item1 ).ThenBy( u => u.Item2 ).ToList();
		var reUvs = reimported.Faces.SelectMany( f => f.UVs ?? Array.Empty<Vec2>() ).Select( u => ( (long)Math.Round( u.x * 1e4 ), (long)Math.Round( u.y * 1e4 ) ) ).OrderBy( u => u.Item1 ).ThenBy( u => u.Item2 ).ToList();
		if ( !bakedUvs.SequenceEqual( reUvs ) )
			Add( "UV", $"OBJ corner UVs differ from the baked mesh ({bakedUvs.Count} baked vs {reUvs.Count} re-imported)", "Preserve corner UVs through the OBJ round-trip (V flips out and back exactly once)." );
		var expectedNames = baked.Materials.Select( MapBakeExport.SanitizeMaterialName ).ToList();
		var actualNames = ( reimportedMaterialNames ?? new List<string>() ).ToList();
		if ( !expectedNames.SequenceEqual( actualNames, StringComparer.Ordinal ) )
			Add( "MATERIAL", $"OBJ usemtl materials [{string.Join( ", ", actualNames )}] != baked [{string.Join( ", ", expectedNames )}]: a material was dropped, renamed or reordered",
				"Preserve every baked material name, in first-appearance order, as a usemtl run; a dropped material is a failed export." );
		return issues;
	}

	static int FirstDifference( IReadOnlyList<string> a, IReadOnlyList<string> b )
	{
		for ( var i = 0; i < Math.Min( a.Count, b.Count ); i++ )
			if ( a[i] != b[i] ) return i;
		return Math.Min( a.Count, b.Count );
	}

	/// <summary>One material name per 'f' line in an OBJ, in file order: the
	/// usemtl in force when the face was written ("" when a face precedes
	/// any usemtl). ObjReader collapses named materials to one slot, so
	/// this text-level read is the only per-face material record.</summary>
	public static List<string> ScanFaceMaterials( string text )
	{
		var names = new List<string>();
		if ( string.IsNullOrEmpty( text ) ) return names;
		var current = "";
		foreach ( var raw in text.Split( '\n' ) )
		{
			var line = raw.Trim();
			if ( line.StartsWith( "usemtl ", StringComparison.Ordinal ) ) current = line.Substring( "usemtl ".Length ).Trim();
			else if ( line.StartsWith( "f ", StringComparison.Ordinal ) || line == "f" ) names.Add( current );
		}
		return names;
	}

	/// <summary>First-appearance-ordered distinct names after a keyword
	/// ("usemtl" in an OBJ, "newmtl" in an .mtl).</summary>
	public static List<string> ScanNames( string text, string keyword )
	{
		var names = new List<string>();
		if ( string.IsNullOrEmpty( text ) ) return names;
		foreach ( var raw in text.Split( '\n' ) )
		{
			var line = raw.Trim();
			if ( !line.StartsWith( keyword + " ", StringComparison.Ordinal ) ) continue;
			var name = line.Substring( keyword.Length ).Trim();
			if ( name.Length > 0 && !names.Contains( name ) ) names.Add( name );
		}
		return names;
	}

	// ----- path 3b: GLB (container only — payload is worker 17) --------

	/// <summary>GLB through MapBakeExport.ExportGlb: validate the
	/// container (magic, version 2, total length, JSON chunk parses,
	/// asset version 2.0) and record the honest limit — GlTfExporter
	/// writes a header + asset JSON only, so the mesh/material payload
	/// is BLOCKED (see <see cref="MapBakeExport.GlbNote"/>), not passed.
	/// The real payload round-trip (core GlbExport.Write -&gt;
	/// GlbImport.Read) is the seam test in MapExportRoundtripTests.</summary>
	public static RoundtripPathResult CheckGlb( VmfDocument doc, string workDir, MapBakeOptions options = null )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		if ( string.IsNullOrWhiteSpace( workDir ) ) throw new ArgumentException( "GLB export needs a work directory.", nameof( workDir ) );
		Directory.CreateDirectory( workDir );
		var issues = new List<RoundtripIssue>();
		var baked = MapBakeExport.Bake( ToMapBrushes( doc ), new List<MapBakeDisplacement>(), options );
		var glbPath = Path.Combine( workDir, SealedRoomName + ".glb" );
		var exported = MapBakeExport.ExportGlb( baked, glbPath, options );
		var notes = new List<string> { MapBakeExport.GlbNote };
		bool blocked = false;
		string blockedReason = "";
		if ( !exported.GlbWritten || !File.Exists( glbPath ) )
		{
			issues.Add( new RoundtripIssue { Path = "glb", Code = "EXPORT_ROUNDTRIP_GLB_NOT_WRITTEN",
				Message = $"GLB export reported written={exported.GlbWritten} but '{glbPath}' is missing",
				FixHint = "ExportGlb must write the file it claims to write." } );
		}
		else
		{
			var bytes = File.ReadAllBytes( glbPath );
			issues.AddRange( CheckGlbContainer( bytes ) );
			if ( issues.Count == 0 )
			{
				blocked = true;
				blockedReason = MapBakeExport.GlbNote;
				issues.Add( new RoundtripIssue { Path = "glb", Code = "EXPORT_ROUNDTRIP_GLB_PAYLOAD_MISSING",
					Severity = IssueSeverity.Warning,
					Message = "GLB carries a valid container (header + asset JSON) but no mesh/material payload: GlTfExporter ignores the baked mesh (see MapBakeExport.GlbNote). The mesh round-trip is blocked until worker 17 wires MapBakeMesh -> GlbPart through core GlbExport.Write.",
					FixHint = "Wire MapBakeMesh -> GlbPart (name, mesh, material) through core/GlbExport.Write in MapBakeExport.ExportGlb; do not claim a GLB round-trip until then." } );
			}
		}
		return new RoundtripPathResult
		{
			Path = "glb",
			Issues = issues,
			Blocked = blocked,
			BlockedReason = blockedReason,
			Notes = notes,
			SourceBrushCount = doc.AllBrushes().Count(),
			ResultBrushCount = doc.AllBrushes().Count(),
			SourceFaceCount = baked.FaceCount,
			ResultFaceCount = 0,
			SourceMaterialCount = baked.Materials.Count,
			ResultMaterialCount = 0,
			SourceEntityCount = doc.Entities.Count,
			ResultEntityCount = doc.Entities.Count,
		};
	}

	/// <summary>GLB container validation, no core dependency: magic,
	/// version 2, declared length == file length, JSON chunk parses,
	/// asset version 2.0. A present non-empty "meshes" array would mean
	/// the payload stub is gone (the caller then un-blocks the path).</summary>
	public static IReadOnlyList<RoundtripIssue> CheckGlbContainer( byte[] bytes )
	{
		var issues = new List<RoundtripIssue>();
		void Add( string message ) => issues.Add( new RoundtripIssue { Path = "glb", Code = "EXPORT_ROUNDTRIP_GLB_CONTAINER",
			Message = message, FixHint = "Write a standard GLB: 12-byte header (magic, version 2, total length), JSON chunk, BIN chunk." } );
		if ( bytes == null || bytes.Length < 12 ) { Add( $"GLB is {( bytes == null ? 0 : bytes.Length )} bytes, shorter than the 12-byte header." ); return issues; }
		if ( BitConverter.ToUInt32( bytes, 0 ) != 0x46546C67 ) { Add( "GLB magic is not 'glTF'." ); return issues; }
		if ( BitConverter.ToUInt32( bytes, 4 ) != 2 ) { Add( $"GLB version is {BitConverter.ToUInt32( bytes, 4 )}, not 2." ); return issues; }
		if ( BitConverter.ToUInt32( bytes, 8 ) != bytes.Length ) { Add( $"GLB declares {BitConverter.ToUInt32( bytes, 8 )} bytes but the file is {bytes.Length}." ); return issues; }
		if ( bytes.Length < 20 ) { Add( "GLB has no JSON chunk header." ); return issues; }
		var jsonLength = (int)BitConverter.ToUInt32( bytes, 12 );
		if ( BitConverter.ToUInt32( bytes, 16 ) != 0x4E4F534A ) { Add( "GLB first chunk is not JSON." ); return issues; }
		if ( jsonLength < 0 || 20 + jsonLength > bytes.Length ) { Add( $"GLB JSON chunk length {jsonLength} overruns the file." ); return issues; }
		JsonDocument json;
		try { json = JsonDocument.Parse( new ReadOnlyMemory<byte>( bytes, 20, jsonLength ) ); }
		catch ( JsonException ex ) { Add( $"GLB JSON chunk does not parse: {ex.Message}" ); return issues; }
		using ( json )
		{
			var root = json.RootElement;
			if ( !root.TryGetProperty( "asset", out var asset ) || !asset.TryGetProperty( "version", out var version )
				|| version.GetString() != "2.0" )
				Add( "GLB asset version is not 2.0." );
			else if ( root.TryGetProperty( "meshes", out var meshes ) && meshes.ValueKind == JsonValueKind.Array && meshes.GetArrayLength() > 0 )
			{
				// The payload stub is gone: a meshes array exists. This
				// check does not claim the payload round-trips — the
				// caller (CheckGlb) still blocks until the seam test
				// proves it — but the container now carries geometry.
			}
		}
		return issues;
	}

	// ----- path 3c: native compile -------------------------------------

	/// <summary>Native compile round-trip (Preview preset by default:
	/// BSP + fast visibility + low-res lightmap): run CompileRunner,
	/// require success + sealed (no leak), the player start inside an
	/// empty leaf, every interior leaf visible from itself, one
	/// lightmap face per input face with light on it, then re-read the
	/// .bsp / .pvs / .lightmap files from disk and compare headers and
	/// counts with the in-memory results. A failed run reports its
	/// named CompileIssues and skips the file re-reads (a failed run
	/// claims no partial product, Baker's rule); a leak also writes
	/// the .lin point file, which the caller can check at
	/// &lt;workDir&gt;/compile/sealed-room.lin.</summary>
	public static RoundtripPathResult CheckCompile( VmfDocument doc, string workDir, CompilePreset preset = CompilePreset.Preview )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		if ( string.IsNullOrWhiteSpace( workDir ) ) throw new ArgumentException( "Compile needs a work directory.", nameof( workDir ) );
		var outDir = Path.Combine( workDir, "compile" );
		Directory.CreateDirectory( outDir );
		var issues = new List<RoundtripIssue>();
		var notes = new List<string>();
		var input = ToCompileInput( doc );
		var totalFaces = input.Brushes.Sum( b => b.Faces.Count );
		var options = new CompileOptions { OutputFolder = outDir };
		var result = CompileRunner.Run( input, preset, options );
		notes.Add( $"preset {preset}, {result.CompletedStages.Count} stage(s) completed in {result.Duration.TotalMilliseconds:F0} ms" );
		if ( result.Bsp != null )
			notes.Add( $"bsp leaves {result.Bsp.LeafCount} (empty {result.Bsp.EmptyLeafCount}, solid {result.Bsp.SolidLeafCount}, void {result.Bsp.VoidLeafCount}), portals {result.Bsp.PortalCount}" );
		foreach ( var ci in result.Issues )
			issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_" + ci.Code.ToUpperInvariant().Replace( "-", "_" ),
				Message = ci.Message, FixHint = ci.FixHint } );
		if ( !result.Succeeded )
		{
			issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_FAILED",
				Message = $"compile failed at stage {result.FailedStage}: {result.Error}",
				FixHint = "Fix the named compile issue above; a failed compile has no BSP/PVS/lightmap product to validate." } );
			return BuildCompilePathResult( doc, issues, notes, totalFaces, 0, result );
		}
		if ( input.PlayerStart != null )
		{
			if ( result.Leak == null )
				issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_NO_LEAK_CHECK",
					Message = "player start is set but no leak check ran",
					FixHint = "Run the leak check whenever a player start exists; a sealed map must prove it." } );
			else if ( result.Leak.IsLeaked )
				issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_LEAK",
					Message = $"map leaks: void reachable from the player start through a gap at {result.Leak.GapLocation} (leaf {result.Leak.GapLeafId}, portal {result.Leak.GapPortalId})",
					FixHint = "Seal the gap the .lin point file names; a leaking map has no valid BSP product." } );
		}
		if ( result.Bsp == null )
			issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_NO_BSP",
				Message = "compile succeeded but produced no BSP result",
				FixHint = "A successful compile must produce leaves and portals." } );
		else
		{
			if ( result.Bsp.LeafCount == 0 )
				issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_NO_LEAVES",
					Message = "BSP has no leaves", FixHint = "The BSP build must partition the map into leaves." } );
			if ( input.PlayerStart != null )
			{
				var leaf = result.Bsp.LeafContaining( input.PlayerStart.Value );
				if ( leaf == null || leaf.IsSolid || leaf.IsVoid )
					issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_NO_INTERIOR_LEAF",
						Message = $"player start {input.PlayerStart.Value} is not inside an empty leaf (leaf {( leaf == null ? -1 : leaf.Id )})",
						FixHint = "The player start must sit in playable empty space, not inside a solid or the void." } );
			}
		}
		if ( result.Pvs == null )
		{
			if ( preset != CompilePreset.QuickPlay )
				issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_NO_PVS",
					Message = $"preset {preset} produced no PVS result",
					FixHint = "A visibility preset must produce one visible set per interior leaf." } );
		}
		else if ( result.Bsp != null )
		{
			foreach ( var leaf in result.Bsp.Leaves )
			{
				if ( leaf.IsSolid || leaf.IsVoid ) continue;
				var visible = result.Pvs.VisibleFrom( leaf.Id );
				if ( !visible.Contains( leaf.Id ) )
					issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_PVS_SELF",
						Message = $"interior leaf {leaf.Id} cannot see itself in the PVS",
						FixHint = "Every interior leaf sees itself; a PVS that drops it is corrupt." } );
				foreach ( var v in visible )
				{
					var target = result.Bsp.Leaves.FirstOrDefault( l => l.Id == v );
					if ( target == null || target.IsSolid || target.IsVoid )
					{
						issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_PVS_TARGET",
							Message = $"PVS for leaf {leaf.Id} lists leaf {v}, which is not an interior leaf",
							FixHint = "PVS sets list interior leaves only; void and solid leaves are never visible." } );
						break;
					}
				}
			}
		}
		var lightmapFaces = 0;
		if ( result.Lightmaps == null )
		{
			if ( preset != CompilePreset.QuickPlay )
				issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_NO_LIGHTMAP",
					Message = $"preset {preset} produced no lightmap result",
					FixHint = "A lightmap preset must bake one face per input face." } );
		}
		else
		{
			lightmapFaces = result.Lightmaps.Faces.Count;
			if ( lightmapFaces != totalFaces )
				issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_LIGHTMAP_FACE_COUNT",
					Message = $"lightmap faces {lightmapFaces} != input faces {totalFaces}: a face lost its lightmap",
					FixHint = "Every brush face gets a lightmap, detail included; a missing face is a failed bake." } );
			foreach ( var f in result.Lightmaps.Faces )
			{
				var brush = input.Brushes.FirstOrDefault( b => b.Id == f.BrushId );
				if ( brush == null || f.FaceIndex < 0 || f.FaceIndex >= brush.Faces.Count )
				{
					issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_LIGHTMAP_FACE_REF",
						Message = $"lightmap face names brush {f.BrushId} face {f.FaceIndex}, which does not exist",
						FixHint = "Lightmap faces must reference real input faces." } );
					break;
				}
			}
			if ( result.Lightmaps.Faces.Count > 0 && !( result.Lightmaps.Mean > 0f ) )
				issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_LIGHTMAP_DARK",
					Message = "lightmap mean is 0: no light reached any face",
					FixHint = "Place a light inside the map; a fully dark bake means the lights never reached the faces." } );
		}
		// Re-read the product files from disk and compare with memory.
		issues.AddRange( CheckCompileFiles( outDir, input.Name, result ) );
		return BuildCompilePathResult( doc, issues, notes, totalFaces, lightmapFaces, result );
	}

	static RoundtripPathResult BuildCompilePathResult( VmfDocument doc, List<RoundtripIssue> issues, List<string> notes, int totalFaces, int lightmapFaces, CompileResult result ) =>
		new RoundtripPathResult
		{
			Path = "compile",
			Issues = issues,
			Notes = notes,
			SourceBrushCount = doc.AllBrushes().Count(),
			ResultBrushCount = result.Bsp == null ? 0 : result.Bsp.LeafCount,
			SourceFaceCount = totalFaces,
			ResultFaceCount = lightmapFaces,
			SourceMaterialCount = DistinctMaterials( doc ).Count,
			ResultMaterialCount = DistinctMaterials( doc ).Count,
			SourceEntityCount = doc.Entities.Count,
			ResultEntityCount = doc.Entities.Count,
		};

	/// <summary>Re-read .bsp / .pvs / .lightmap from disk: headers,
	/// counts and (for PVS and lightmaps) the per-leaf / per-face
	/// values must match the in-memory compile results.</summary>
	static IReadOnlyList<RoundtripIssue> CheckCompileFiles( string outDir, string outputName, CompileResult result )
	{
		var issues = new List<RoundtripIssue>();
		void Add( string suffix, string message, string fix ) =>
			issues.Add( new RoundtripIssue { Path = "compile", Code = "EXPORT_ROUNDTRIP_COMPILE_" + suffix, Message = message, FixHint = fix } );

		var name = outputName;
		var bspPath = Path.Combine( outDir, name + ".bsp" );
		var pvsPath = Path.Combine( outDir, name + ".pvs" );
		var lightmapPath = Path.Combine( outDir, name + ".lightmap" );
		if ( result.Bsp != null )
		{
			if ( !File.Exists( bspPath ) ) Add( "BSP_FILE_MISSING", $".bsp product '{bspPath}' is missing", "A successful compile writes its .bsp product." );
			else
			{
				var lines = File.ReadAllLines( bspPath );
				if ( lines.Length < 3 || lines[0] != "GEPPETTO-BSP 1" || lines[1] != $"leaves {result.Bsp.LeafCount}" || lines[2] != $"portals {result.Bsp.PortalCount}" )
					Add( "BSP_FILE_HEADER", $".bsp header/counts do not match the in-memory BSP ({lines.Length} lines, first: '{( lines.Length > 0 ? lines[0] : "" )}')",
						"The .bsp file must carry the same leaf/portal counts the compile built." );
				else if ( lines.Count( l => l.StartsWith( "leaf ", StringComparison.Ordinal ) ) != result.Bsp.LeafCount
					|| lines.Count( l => l.StartsWith( "portal ", StringComparison.Ordinal ) ) != result.Bsp.PortalCount )
					Add( "BSP_FILE_COUNT", ".bsp leaf/portal line counts do not match the in-memory BSP",
						"The .bsp file must list every leaf and portal the compile built." );
			}
		}
		if ( result.Pvs != null )
		{
			if ( !File.Exists( pvsPath ) ) Add( "PVS_FILE_MISSING", $".pvs product '{pvsPath}' is missing", "A successful compile writes its .pvs product." );
			else
			{
				var lines = File.ReadAllLines( pvsPath );
				if ( lines.Length < 2 || lines[0] != "GEPPETTO-PVS 1" || lines[1] != $"quality {result.Pvs.Quality}" )
					Add( "PVS_FILE_HEADER", ".pvs header/quality do not match the in-memory PVS", "The .pvs file must carry the same visibility quality the compile used." );
				else
				{
					var parsed = new Dictionary<int, List<int>>();
					foreach ( var line in lines.Skip( 2 ) )
					{
						if ( !line.StartsWith( "leaf ", StringComparison.Ordinal ) ) continue;
						var colon = line.IndexOf( ':' );
						if ( colon < 0 || !int.TryParse( line.Substring( 5, colon - 5 ).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id ) ) continue;
						var rest = line.Substring( colon + 1 ).Trim();
						parsed[id] = rest.Length == 0 ? new List<int>() : rest.Split( ',' ).Select( s => int.Parse( s.Trim(), CultureInfo.InvariantCulture ) ).ToList();
					}
					foreach ( var pair in result.Pvs.Sets )
					{
						if ( !parsed.TryGetValue( pair.Key, out var set ) || !set.SequenceEqual( pair.Value ) )
						{
							Add( "PVS_FILE_SET", $".pvs set for leaf {pair.Key} does not match the in-memory PVS",
								"The .pvs file must list the same visible leaves the compile computed." );
							break;
						}
					}
				}
			}
		}
		if ( result.Lightmaps != null )
		{
			if ( !File.Exists( lightmapPath ) ) Add( "LIGHTMAP_FILE_MISSING", $".lightmap product '{lightmapPath}' is missing", "A successful compile writes its .lightmap product." );
			else
			{
				var lines = File.ReadAllLines( lightmapPath );
				if ( lines.Length < 2 || lines[0] != "GEPPETTO-LIGHTMAP 1" || lines[1] != $"resolution {result.Lightmaps.Resolution}" )
					Add( "LIGHTMAP_FILE_HEADER", ".lightmap header/resolution do not match the in-memory bake", "The .lightmap file must carry the same resolution the bake used." );
				else
				{
					var faceLines = lines.Where( l => l.StartsWith( "face ", StringComparison.Ordinal ) ).ToList();
					if ( faceLines.Count != result.Lightmaps.Faces.Count )
						Add( "LIGHTMAP_FILE_COUNT", $".lightmap face lines {faceLines.Count} != in-memory {result.Lightmaps.Faces.Count}",
							"The .lightmap file must carry one face block per baked face." );
					else for ( var i = 0; i < faceLines.Count; i++ )
					{
						var tokens = faceLines[i].Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
						// face <brushId> <faceIndex> <material> mean <mean> sampled <s>/<t>
						if ( tokens.Length < 8 || tokens[0] != "face" || tokens[4] != "mean"
							|| !double.TryParse( tokens[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var mean )
							|| Math.Abs( mean - result.Lightmaps.Faces[i].Mean ) > 1e-3 )
						{
							Add( "LIGHTMAP_FILE_FACE", $".lightmap face line {i} does not match the in-memory bake: '{faceLines[i]}'",
								"The .lightmap file must carry the same per-face means the bake computed." );
							break;
						}
					}
				}
			}
		}
		return issues;
	}

	// ----- all paths ---------------------------------------------------

	/// <summary>All five paths for one document. Source structural
	/// defects are reported first on the pseudo-path "source"; each
	/// path is isolated, so one failing path never hides the others.</summary>
	public static MapExportRoundtripResult ValidateAll( VmfDocument doc, string workDir, MapBakeOptions options = null, CompilePreset preset = CompilePreset.Preview )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		if ( string.IsNullOrWhiteSpace( workDir ) ) throw new ArgumentException( "Validation needs a work directory.", nameof( workDir ) );
		var paths = new List<RoundtripPathResult>();
		var sourceIssues = doc.Validate().Select( d => new RoundtripIssue { Path = "source", Code = "EXPORT_ROUNDTRIP_SOURCE_INVALID",
			Message = d, FixHint = "Fix the source document first; an invalid map cannot round-trip cleanly." } ).ToList();
		paths.Add( new RoundtripPathResult
		{
			Path = "source",
			Issues = sourceIssues,
			SourceBrushCount = doc.AllBrushes().Count(),
			ResultBrushCount = doc.AllBrushes().Count(),
			SourceFaceCount = doc.AllBrushes().Sum( b => b.Faces.Count ),
			ResultFaceCount = doc.AllBrushes().Sum( b => b.Faces.Count ),
			SourceMaterialCount = DistinctMaterials( doc ).Count,
			ResultMaterialCount = DistinctMaterials( doc ).Count,
			SourceEntityCount = doc.Entities.Count,
			ResultEntityCount = doc.Entities.Count,
		} );
		paths.Add( RunPath( "vmf", () => CheckVmf( doc ) ) );
		paths.Add( RunPath( "map220", () => CheckMap220( doc ) ) );
		paths.Add( RunPath( "obj", () => CheckObj( doc, Path.Combine( workDir, "obj" ), options ) ) );
		paths.Add( RunPath( "glb", () => CheckGlb( doc, Path.Combine( workDir, "glb" ), options ) ) );
		paths.Add( RunPath( "compile", () => CheckCompile( doc, Path.Combine( workDir, "compile" ), preset ) ) );
		return new MapExportRoundtripResult { Paths = paths };
	}

	static RoundtripPathResult RunPath( string path, Func<RoundtripPathResult> run )
	{
		try { return run(); }
		catch ( Exception ex )
		{
			return new RoundtripPathResult
			{
				Path = path,
				Issues = new[] { new RoundtripIssue { Path = path, Code = "EXPORT_ROUNDTRIP_" + path.ToUpperInvariant() + "_EXCEPTION",
					Message = $"{path} check threw {ex.GetType().Name}: {ex.Message}",
					FixHint = "Fix the export or the check; a throwing check is a failed round-trip, not a pass." } },
			};
		}
	}

	// ----- IssueEngine wiring ------------------------------------------

	/// <summary>A round-trip failure as an IssueEngine issue. Material
	/// losses are MissingMaterial; everything else is InvalidBrush.</summary>
	public static Issue ToIssue( RoundtripIssue issue )
	{
		if ( issue == null ) throw new ArgumentNullException( nameof( issue ) );
		return new Issue
		{
			Kind = issue.Code.Contains( "MATERIAL", StringComparison.Ordinal ) ? IssueKind.MissingMaterial : IssueKind.InvalidBrush,
			Severity = issue.Severity,
			Code = issue.Code,
			Message = $"[{issue.Path}] {issue.Message}",
			FixHint = issue.FixHint,
			BrushId = issue.BrushId,
			EntityId = -1,
		};
	}

	// ----- helpers -----------------------------------------------------

	static List<string> DistinctMaterials( VmfDocument doc ) =>
		doc.AllBrushes().SelectMany( b => b.Faces ).Select( f => f.Material ).Distinct( StringComparer.Ordinal ).OrderBy( m => m, StringComparer.Ordinal ).ToList();

	static Vec3 ToVec3( VmfVec v ) => new Vec3( (float)v.X, (float)v.Y, (float)v.Z );
	static VmfVec ToVmfVec( Vec3 v ) => new VmfVec( v.x, v.y, v.z );

	static Vec3? ParseOrigin( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return null;
		var parts = text.Trim().Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 ) return null;
		if ( !double.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
			|| !double.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
			|| !double.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) ) return null;
		return new Vec3( (float)x, (float)y, (float)z );
	}

	static CompileLight ParseLight( Vec3 position, string lightKv )
	{
		var color = new Vec3( 1, 1, 1 );
		var intensity = 1f;
		if ( !string.IsNullOrWhiteSpace( lightKv ) )
		{
			var parts = lightKv.Trim().Split( new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length >= 3
				&& double.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r )
				&& double.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g )
				&& double.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b ) )
				color = new Vec3( (float)( r / 255.0 ), (float)( g / 255.0 ), (float)( b / 255.0 ) );
			if ( parts.Length >= 4 && double.TryParse( parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var i ) )
				intensity = (float)i;
		}
		return new CompileLight( position, intensity, color );
	}
}

/// <summary>Export round-trip as an IssueEngine rule (Id
/// "export-roundtrip", Global scope): rebuilds the VMF document from
/// the IssueDocument's planes, runs the VMF and Valve 220 .map text
/// round-trips in memory, and reports any drift as a named issue. It
/// compares planes (normal + distance, 1e-6) rather than exact plane
/// points — the IssueDocument carries float planes, not the source's
/// double points — and it never throws: a broken check reports one
/// EXPORT_ROUNDTRIP_RULE_ERROR issue instead of failing the run.
/// Register it with engine.Register(new ExportRoundtripRule()); the
/// eight default rules stay registered alongside it.</summary>
public sealed class ExportRoundtripRule : IIssueRule
{
	public const string RuleId = "export-roundtrip";
	public string Id => RuleId;
	public string Name => "Export round-trip";
	public IssueScope Scope => IssueScope.Global;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.InvalidBrush, IssueKind.MissingMaterial };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		try
		{
			if ( document == null ) return Array.Empty<Issue>();
			var vmf = MapExportRoundtrip.FromIssueDocument( document );
			var issues = new List<Issue>();
			var vmfText = VmfIo.Write( vmf );
			var vmfBack = VmfIo.Parse( vmfText );
			foreach ( var ri in MapExportRoundtrip.CompareDocuments( vmf, vmfBack, "vmf", exactPlanePoints: false ) )
				issues.Add( MapExportRoundtrip.ToIssue( ri ) );
			var mapText = Map220Io.Write( vmf, out var warnings );
			foreach ( var w in warnings )
				issues.Add( new Issue
				{
					Kind = IssueKind.InvalidBrush,
					Severity = IssueSeverity.Warning,
					Code = "EXPORT_ROUNDTRIP_MAP220_KNOWN_LOSS",
					Message = "[map220] " + w,
					FixHint = "Valve 220 .map has no field for this; keep the VMF as the master copy.",
				} );
			var mapBack = Map220Io.Parse( mapText );
			foreach ( var ri in MapExportRoundtrip.CompareDocuments( vmf, mapBack, "map220", exactPlanePoints: false ) )
				issues.Add( MapExportRoundtrip.ToIssue( ri ) );
			return issues;
		}
		catch ( Exception ex )
		{
			return new[] { new Issue
			{
				Kind = IssueKind.InvalidBrush,
				Severity = IssueSeverity.Error,
				Code = "EXPORT_ROUNDTRIP_RULE_ERROR",
				Message = $"Export round-trip check failed: {ex.GetType().Name}: {ex.Message}",
				FixHint = "Fix the export check; the map itself was not changed.",
			} };
		}
	}
}
