using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy.Export;

namespace Effigy;

/// <summary>
/// hammer-obj-glb-map-export: bake a map (brushes + displacements) to a static
/// mesh with per-face materials and export it OBJ + GLB through the EXISTING
/// exporters. No new exporter — the bake hands a <see cref="PolyMesh"/> to
/// <see cref="ObjWriter"/> and <see cref="GlTfExporter"/>, preserving material
/// names. Backend/data-model/API only; no UI, no Hammer app, no shell-out.
/// Units are the kernel's inches, Z-up (CONTRACTS.md).
///
/// DEPENDENCY STATUS (2026-11-13, muse-3-desktop-hammer-17):
///   * FaceMaterial — LANDED (kernel/Effigy/Map/FaceMaterial.cs). This file
///     consumes it directly: one <see cref="FaceMaterial"/> per brush face
///     (material name + FaceUv), <c>IsNoDraw</c> / <c>ContributesToBakedMesh</c>
///     for the nodraw rule, <c>FaceUv.ComputeUvs</c> for planar UVs.
///   * MapBrush (hammer-brush-core-plane-based-brushes) — LANDED during this
///     ticket (kernel/Effigy/Map/MapBrush.cs): MapBrushFace (unit outward
///     normal + distance + FaceMaterial, never null) and MapBrush.FromPlanes /
///     FromVertices / Box / Validate() / ToMesh() match the spec exactly as
///     filed, and the plane convention is identical to this file's. The
///     adapter is <see cref="MapBakeBrush.FromMapBrush"/> (1:1 face map) and
///     the MapBrush Bake overload, which also surfaces MapBrush.Validate()
///     defects as bake issues. This file still must NOT redefine MapBrush.
///     MISMATCH (not mine to fix): MapClip.cs / MapBrushPrimitives.cs
///     reference a type named MapFace, which MapBrush does NOT define (it
///     defines MapBrushFace) — that is why the shared tree currently fails
///     to compile (9x CS0246); the owners/integrator reconcile the name.
///   * MapDocument (hammer-map-document-model) — NOT landed: no
///     kernel/Effigy/Map/MapDocument.cs on disk. The bake entry point takes
///     <see cref="MapBakeDocument"/> (name + brushes + displacements), the
///     bake-side form of a MapDocument; the integrator maps
///     MapDocument.Brushes/Displacements onto it. Nothing here invents
///     MapDocument members.
///   * Displacement (hammer-displacement-data-model) — LANDED during this
///     ticket (kernel/Effigy/Map/Displacement.cs, namespace Effigy.Map):
///     Create(face, power), row-major Offsets/Alphas, Sew/Subdivide/ToMesh/
///     Validate/Snapshot match the spec. The adapter is
///     <see cref="MapBakeDisplacement.FromDisplacement"/> (corners/power/
///     offsets/alphas 1:1; corner UVs preserved exactly via the explicit
///     <see cref="MapBakeDisplacement.Uvs"/> array). Two seams the
///     Displacement type leaves open, documented not papered over: its face
///     carries only an int MaterialId (no material NAME — the caller supplies
///     MaterialA/B FaceMaterials), and its ToMesh() keeps quads with the
///     blend in VertexColors while this bake splits the blend per triangle
///     by centroid alpha. Sew is the Displacement ticket's job, not the
///     bake's: each displacement tessellates independently.
///     MISMATCH (not mine to fix): Displacement.cs defines its OWN
///     Effigy.Map.MapBrushFace (corners + int MaterialId + UV scalars),
///     distinct from Effigy.MapBrushFace (plane + FaceMaterial) — the
///     integrator must pick one face type; this file references the
///     Displacement face only through the fully-qualified
///     Effigy.Map.Displacement type and never aliases MapBrushFace.
///   * GlTfExporter — READ-ONLY, called, never edited. It currently writes a
///     minimal GLB header + asset JSON and ignores the mesh argument, so the
///     mesh/material payload does NOT yet reach .glb bytes; see
///     <see cref="MapBakeExport.GlbNote"/>. The full GLB writer is
///     core/GlbExport.Write(GlbPart), which the kernel cannot reference — a
///     core-level follow-up wires MapBakeMesh -&gt; GlbPart through it.
///   * ObjWriter — READ-ONLY, called, never edited. It emits one
///     <c>usemtl</c> run per material slot (grouped, first-appearance order),
///     flips V on write, and keeps quads unless triangulated.
///
/// BAKE RULES.
///   * Plane convention: a brush is the intersection of half-spaces; a point
///     is inside when Dot(Normal, p) &lt;= Distance for every face, Normal
///     outward. Each face polygon is a large quad on its plane clipped by
///     every other plane (Sutherland-Hodgman) — the plane-intersection
///     vertex solve the MapBrush spec names.
///   * Nodraw faces are real solid faces: they take part in clipping (the
///     brush stays solid) but emit no mesh faces, and their material is
///     never registered. They are counted in
///     <see cref="MapBakeMesh.ExcludedNoDrawFaces"/> and stay in the brush.
///   * The baked mesh is FACE-SPLIT: every face owns its vertices (no
///     welding across faces), so brush edges stay hard and UV seams stay
///     exact. Quads stay quads; displacement cells become two triangles.
///     Smoothing groups and lightmap scales do not live on PolyMesh faces;
///     they are carried per mesh face in <see cref="MapBakeFaceInfo"/>.
///   * Invalid geometry is a NAMED ISSUE, never a silent fix: zero-volume
///     brushes, planes that miss the solid, degenerate polygons/triangles,
///     invalid UV transforms and all-nodraw brushes are recorded in
///     <see cref="MapBakeMesh.Issues"/> with the brush/displacement name.
///     Structurally impossible INPUT (nulls, &lt; 4 planes, bad grid sizes)
///     throws <see cref="ArgumentException"/> naming the problem, matching
///     FaceMaterial's constructor style.
/// </summary>
public static class MapBakeExport
{
	/// <summary>Default object name written into OBJ files.</summary>
	public const string DefaultObjectName = "map";

	/// <summary>
	/// What <see cref="ExportGlb"/> honestly reports: the kernel GLB exporter
	/// is a minimal stub (see the file header). The mesh/material counts on a
	/// <see cref="MapBakeExportResult"/> come from the baked mesh, not from
	/// parsing the .glb back — the .glb bytes currently carry a header + asset
	/// JSON only. Do not quote a GLB round-trip as verification until the
	/// core-level GlbExport wiring lands.
	/// </summary>
	public const string GlbNote = "GlTfExporter (kernel/Effigy/Export/GlTfExporter.cs) currently writes a minimal GLB header + asset JSON and does not embed mesh or material payloads; the mesh/material counts on this result come from the baked mesh. The full GLB writer is core/GlbExport.Write(GlbPart) — a core-level follow-up wires MapBakeMesh -> GlbPart (name, mesh, material) through it.";

	/// <summary>Bake a whole map document to a static mesh with per-face materials.</summary>
	public static MapBakeMesh Bake( MapBakeDocument document, MapBakeOptions options = null )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		return Bake( document.Brushes, document.Displacements, options );
	}

	/// <summary>
	/// Bake real <see cref="MapBrush"/>es (hammer-brush-core-plane-based-brushes,
	/// landed) + displacements. Each brush is adapted with
	/// <see cref="MapBakeBrush.FromMapBrush"/> and its <c>Validate()</c> defects
	/// are prepended to the bake issues, so an invalid brush is reported by its
	/// defect codes (e.g. ZeroVolume) and still bakes what is bakeable, never
	/// silently. Brushes with fewer than 4 faces cannot convert and are skipped
	/// with a named issue.
	/// </summary>
	public static MapBakeMesh Bake( IReadOnlyList<MapBrush> brushes, IReadOnlyList<MapBakeDisplacement> displacements, MapBakeOptions options = null )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		var bakeBrushes = new List<MapBakeBrush>();
		var issues = new List<string>();
		for ( var i = 0; i < brushes.Count; i++ )
		{
			var brush = brushes[i];
			if ( brush is null ) { issues.Add( $"brush {i}: null brush skipped." ); continue; }
			foreach ( var defect in brush.Validate() )
				issues.Add( $"brush {i}: {defect.Code}: {defect.Message}" );
			if ( brush.FaceCount < 4 )
			{
				issues.Add( $"brush {i}: too few faces to bake ({brush.FaceCount})." );
				continue;
			}
			bakeBrushes.Add( MapBakeBrush.FromMapBrush( brush ) );
		}
		var baked = Bake( bakeBrushes, displacements, options );
		if ( issues.Count == 0 ) return baked;
		var merged = new List<string>( issues );
		merged.AddRange( baked.Issues );
		return new MapBakeMesh( baked.Mesh, baked.Materials, baked.FaceInfo, merged,
			baked.ExcludedNoDrawFaces, baked.ExcludedDegenerateFaces, baked.BrushFaceCount, baked.DisplacementFaceCount );
	}

	/// <summary>Bake brushes + displacements to a static mesh with per-face materials.</summary>
	public static MapBakeMesh Bake( IReadOnlyList<MapBakeBrush> brushes, IReadOnlyList<MapBakeDisplacement> displacements, MapBakeOptions options = null )
	{
		if ( brushes is null ) throw new ArgumentNullException( nameof( brushes ) );
		options = options ?? new MapBakeOptions();
		var mesh = new PolyMesh();
		var materials = new List<string>();
		var slots = new Dictionary<string, int>( StringComparer.Ordinal );
		var faceInfo = new List<MapBakeFaceInfo>();
		var issues = new List<string>();
		var excludedNoDraw = 0;
		var excludedDegenerate = 0;
		var brushFaceCount = 0;
		var displacementFaceCount = 0;

		int SlotFor( string name )
		{
			if ( !slots.TryGetValue( name, out var slot ) )
			{
				slot = materials.Count;
				slots.Add( name, slot );
				materials.Add( name );
			}
			return slot;
		}

		for ( var bi = 0; bi < brushes.Count; bi++ )
		{
			var brush = brushes[bi];
			if ( brush is null ) { issues.Add( $"brush {bi}: null brush skipped." ); continue; }
			var before = mesh.FaceCount;
			BakeBrush( brush, bi, mesh, faceInfo, issues, options, SlotFor, ref excludedNoDraw, ref excludedDegenerate );
			brushFaceCount += mesh.FaceCount - before;
		}
		for ( var di = 0; di < ( displacements is null ? 0 : displacements.Count ); di++ )
		{
			var disp = displacements[di];
			if ( disp is null ) { issues.Add( $"displacement {di}: null displacement skipped." ); continue; }
			var before = mesh.FaceCount;
			BakeDisplacement( disp, di, mesh, faceInfo, issues, options, SlotFor, ref excludedNoDraw, ref excludedDegenerate );
			displacementFaceCount += mesh.FaceCount - before;
		}

		return new MapBakeMesh( mesh, materials, faceInfo, issues, excludedNoDraw, excludedDegenerate, brushFaceCount, displacementFaceCount );
	}

	/// <summary>
	/// Export the baked mesh as OBJ (+ .mtl) through <see cref="ObjWriter"/>.
	/// Material names are preserved verbatim as <c>usemtl</c> / <c>newmtl</c>
	/// names (whitespace/control characters are replaced with '_' and the
	/// change is noted on the result, because OBJ material names are single
	/// tokens). Quads are kept unless <c>options.TriangulateObj</c> is set.
	/// </summary>
	public static MapBakeExportResult ExportObj( MapBakeMesh baked, string objPath, MapBakeOptions options = null )
	{
		if ( baked is null ) throw new ArgumentNullException( nameof( baked ) );
		if ( string.IsNullOrWhiteSpace( objPath ) ) throw new ArgumentException( "OBJ export needs a destination path." );
		options = options ?? new MapBakeOptions();
		var mtlPath = Path.ChangeExtension( objPath, ".mtl" );
		var entries = new List<(string Name, Vec3 Colour)>();
		var sanitized = new List<string>();
		foreach ( var name in baked.Materials )
		{
			var clean = SanitizeMaterialName( name );
			if ( clean != name ) sanitized.Add( $"'{name}' -> '{clean}'" );
			var colour = new Vec3( 1f, 1f, 1f );
			if ( options.MaterialColours is not null && options.MaterialColours.TryGetValue( name, out var c ) ) colour = c;
			entries.Add( (clean, colour) );
		}
		ObjWriter.WriteMtl( mtlPath, entries );
		ObjWriter.WriteFile( baked.Mesh, objPath,
			objectName: string.IsNullOrWhiteSpace( options.ObjectName ) ? DefaultObjectName : options.ObjectName,
			smoothingAngleDegrees: ObjWriter.DefaultSmoothingAngleDegrees,
			materialName: slot => SanitizeMaterialName( baked.Materials[slot] ),
			triangulate: options.TriangulateObj,
			materialLibrary: Path.GetFileName( mtlPath ) );
		var result = new MapBakeExportResult( baked ) { ObjPath = objPath, MtlPath = mtlPath, ObjWritten = true };
		if ( sanitized.Count > 0 )
			result.Note = "OBJ material names sanitized (whitespace/control -> '_'): " + string.Join( ", ", sanitized ) + ".";
		return result;
	}

	/// <summary>
	/// Export the baked mesh as GLB through <see cref="GlTfExporter"/>. The
	/// baked <see cref="PolyMesh"/> is passed as the mesh argument, so a
	/// future GlTfExporter that reads it receives the real mesh. See
	/// <see cref="GlbNote"/>: the current exporter writes a minimal GLB, so
	/// the counts on the result come from the baked mesh.
	/// </summary>
	public static MapBakeExportResult ExportGlb( MapBakeMesh baked, string glbPath, MapBakeOptions options = null )
	{
		if ( baked is null ) throw new ArgumentNullException( nameof( baked ) );
		if ( string.IsNullOrWhiteSpace( glbPath ) ) throw new ArgumentException( "GLB export needs a destination path." );
		options = options ?? new MapBakeOptions();
		var written = GlTfExporter.ExportGlb( baked.Mesh, glbPath, options.GltfOptions ?? ExportOptions.Default );
		return new MapBakeExportResult( baked ) { GlbPath = glbPath, GlbWritten = written, Note = GlbNote };
	}

	/// <summary>Export both OBJ (+ .mtl) and GLB for the same baked mesh.</summary>
	public static MapBakeExportResult ExportAll( MapBakeMesh baked, string objPath, string glbPath, MapBakeOptions options = null )
	{
		if ( baked is null ) throw new ArgumentNullException( nameof( baked ) );
		var obj = ExportObj( baked, objPath, options );
		var glb = ExportGlb( baked, glbPath, options );
		var result = new MapBakeExportResult( baked )
		{
			ObjPath = obj.ObjPath, MtlPath = obj.MtlPath, ObjWritten = obj.ObjWritten,
			GlbPath = glb.GlbPath, GlbWritten = glb.GlbWritten,
		};
		var notes = new List<string>();
		if ( !string.IsNullOrWhiteSpace( obj.Note ) ) notes.Add( obj.Note );
		if ( !string.IsNullOrWhiteSpace( glb.Note ) ) notes.Add( glb.Note );
		result.Note = string.Join( " ", notes );
		return result;
	}

	/// <summary>OBJ material names are single tokens: whitespace/control characters become '_'.</summary>
	public static string SanitizeMaterialName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return "material";
		var chars = name.Trim().ToCharArray();
		for ( var i = 0; i < chars.Length; i++ )
			if ( char.IsWhiteSpace( chars[i] ) || char.IsControl( chars[i] ) ) chars[i] = '_';
		return new string( chars );
	}

	static void BakeBrush( MapBakeBrush brush, int brushIndex, PolyMesh mesh, List<MapBakeFaceInfo> faceInfo,
		List<string> issues, MapBakeOptions options, Func<string, int> slotFor, ref int excludedNoDraw, ref int excludedDegenerate )
	{
		var allNoDraw = true;
		for ( var fi = 0; fi < brush.Faces.Count; fi++ )
		{
			var face = brush.Faces[fi];
			if ( face.Material.ContributesToBakedMesh ) allNoDraw = false;
			foreach ( var defect in face.Material.Uv.Validate() )
				issues.Add( $"brush '{brush.Name}' face {fi} ({face.Material.MaterialName}): {defect}" );
		}
		// Tessellate every face first — nodraw faces are real solid faces, so
		// the brush volume is computed from ALL polygons, not just the kept
		// (emitted) subset, which is never closed on its own.
		var polygons = new List<(int FaceIndex, List<Vec3> Polygon, float Area)>();
		for ( var fi = 0; fi < brush.Faces.Count; fi++ )
		{
			var face = brush.Faces[fi];
			var polygon = FacePolygon( brush, fi, options );
			if ( polygon is null || polygon.Count < 3 )
			{
				issues.Add( $"brush '{brush.Name}' face {fi} ({face.Material.MaterialName}): plane does not intersect the solid (no polygon), skipped." );
				continue;
			}
			var area = NewellNormal( polygon ).Length / 2f;
			if ( area < options.MinFaceArea )
			{
				issues.Add( $"brush '{brush.Name}' face {fi} ({face.Material.MaterialName}): polygon area {area} is below {options.MinFaceArea}, dropped." );
				excludedDegenerate++;
				continue;
			}
			polygons.Add( (fi, polygon, area) );
		}
		if ( polygons.Count == 0 )
		{
			issues.Add( $"brush '{brush.Name}': zero volume — no face produced a polygon." );
			return;
		}
		var volume = 0f;
		foreach ( var (fi, polygon, area) in polygons )
		{
			var centroid = Vec3.Zero;
			foreach ( var p in polygon ) centroid += p;
			centroid /= polygon.Count;
			volume += Vec3.Dot( centroid, NewellNormal( polygon ) ) / 6f;
		}
		if ( MathF.Abs( volume ) < 1e-3f )
			issues.Add( $"brush '{brush.Name}': zero volume (signed volume {volume})." );
		var emitted = 0;
		foreach ( var (fi, polygon, area) in polygons )
		{
			var face = brush.Faces[fi];
			if ( !face.Material.ContributesToBakedMesh )
			{
				excludedNoDraw++;
				continue;
			}
			var uvs = face.Material.Uv.ComputeUvs( polygon, face.Normal );
			var baseIndex = mesh.Positions.Count;
			foreach ( var p in polygon ) mesh.Positions.Add( p );
			var indices = new int[polygon.Count];
			for ( var i = 0; i < indices.Length; i++ ) indices[i] = baseIndex + i;
			var slot = slotFor( face.Material.MaterialName );
			mesh.AddFace( indices, uvs, slot );
			faceInfo.Add( new MapBakeFaceInfo
			{
				BrushIndex = brushIndex, DisplacementIndex = -1, FaceIndex = fi,
				BrushName = brush.Name, Material = face.Material.MaterialName,
				SmoothingGroup = face.Material.Uv.SmoothingGroup,
				LightmapScale = face.Material.Uv.LightmapScale,
				IsDisplacement = false, MeshFaceIndex = mesh.FaceCount - 1,
			} );
			emitted++;
		}
		if ( emitted == 0 && allNoDraw )
			issues.Add( $"brush '{brush.Name}': all {brush.Faces.Count} faces are nodraw; nothing baked from this brush." );
	}

	static void BakeDisplacement( MapBakeDisplacement disp, int dispIndex, PolyMesh mesh, List<MapBakeFaceInfo> faceInfo,
		List<string> issues, MapBakeOptions options, Func<string, int> slotFor, ref int excludedNoDraw, ref int excludedDegenerate )
	{
		var rawNormal = disp.FaceNormal;
		if ( rawNormal.LengthSquared <= 1e-12f )
		{
			issues.Add( $"displacement '{disp.Name}': base corners are degenerate (zero face normal), skipped." );
			return;
		}
		var unit = rawNormal.Normal;
		var grid = disp.GridSize;
		var verts = new Vec3[grid * grid];
		for ( var j = 0; j < grid; j++ )
		{
			var t = (float)j / ( grid - 1 );
			for ( var i = 0; i < grid; i++ )
			{
				var s = (float)i / ( grid - 1 );
				verts[j * grid + i] = Bilinear( disp.BaseCorners, s, t ) + unit * disp.Offsets[j * grid + i];
			}
		}
		var kept = 0;
		var skippedNoDraw = 0;
		var skippedDegenerate = 0;
		void Tri( int ia, int ib, int ic )
		{
			var triNormal = Vec3.Cross( verts[ib] - verts[ia], verts[ic] - verts[ia] );
			var area = triNormal.Length / 2f;
			if ( area < options.MinFaceArea )
			{
				issues.Add( $"displacement '{disp.Name}': degenerate triangle (area {area}), dropped." );
				skippedDegenerate++;
				return;
			}
			if ( Vec3.Dot( triNormal, unit ) < 0f ) { var swap = ib; ib = ic; ic = swap; }
			var alpha = ( disp.Alphas[ia] + disp.Alphas[ib] + disp.Alphas[ic] ) / 3f;
			var mat = alpha >= 0.5f ? disp.MaterialB : disp.MaterialA;
			if ( !mat.ContributesToBakedMesh )
			{
				skippedNoDraw++;
				return;
			}
			var baseIndex = mesh.Positions.Count;
			mesh.Positions.Add( verts[ia] );
			mesh.Positions.Add( verts[ib] );
			mesh.Positions.Add( verts[ic] );
			var uvA = VertexUv( disp, mat, ia, grid );
			var uvB = VertexUv( disp, mat, ib, grid );
			var uvC = VertexUv( disp, mat, ic, grid );
			mesh.AddFace( new[] { baseIndex, baseIndex + 1, baseIndex + 2 }, new[] { uvA, uvB, uvC }, slotFor( mat.MaterialName ) );
			faceInfo.Add( new MapBakeFaceInfo
			{
				BrushIndex = -1, DisplacementIndex = dispIndex, FaceIndex = -1,
				BrushName = disp.Name, Material = mat.MaterialName,
				SmoothingGroup = mat.Uv.SmoothingGroup,
				LightmapScale = mat.Uv.LightmapScale,
				IsDisplacement = true, MeshFaceIndex = mesh.FaceCount - 1,
			} );
			kept++;
		}
		for ( var j = 0; j < grid - 1; j++ )
			for ( var i = 0; i < grid - 1; i++ )
			{
				var a = j * grid + i;
				var b = j * grid + i + 1;
				var c = ( j + 1 ) * grid + i + 1;
				var d = ( j + 1 ) * grid + i;
				Tri( a, b, c );
				Tri( a, c, d );
			}
		excludedNoDraw += skippedNoDraw;
		excludedDegenerate += skippedDegenerate;
		if ( kept == 0 )
			issues.Add( $"displacement '{disp.Name}': no triangles baked (all nodraw or degenerate)." );
	}

	/// <summary>
	/// One face's polygon by plane intersection: a large quad on the face plane,
	/// clipped by every other half-space. Winding is corrected to the outward
	/// normal; consecutive duplicate vertices are welded.
	/// </summary>
	static List<Vec3> FacePolygon( MapBakeBrush brush, int faceIndex, MapBakeOptions options )
	{
		var face = brush.Faces[faceIndex];
		if ( face.Normal.LengthSquared <= 1e-12f ) return null;
		var n = face.Normal.Normal;
		var p0 = face.Normal * ( face.Distance / face.Normal.LengthSquared );
		var (uAxis, vAxis) = FaceUv.PlaneAxes( n );
		var e = options.ClipExtent;
		var poly = new List<Vec3>
		{
			p0 - uAxis * e - vAxis * e,
			p0 + uAxis * e - vAxis * e,
			p0 + uAxis * e + vAxis * e,
			p0 - uAxis * e + vAxis * e,
		};
		for ( var j = 0; j < brush.Faces.Count; j++ )
		{
			if ( j == faceIndex ) continue;
			poly = ClipHalfSpace( poly, brush.Faces[j].Normal, brush.Faces[j].Distance );
			if ( poly.Count < 3 ) return poly;
		}
		if ( Vec3.Dot( NewellNormal( poly ), n ) < 0f ) poly.Reverse();
		return Weld( poly, options.WeldTolerance );
	}

	/// <summary>Sutherland-Hodgman clip against one half-space: keep Dot(n, p) &lt;= d.</summary>
	static List<Vec3> ClipHalfSpace( List<Vec3> poly, Vec3 normal, float distance )
	{
		const float eps = 1e-3f;
		var output = new List<Vec3>();
		for ( var i = 0; i < poly.Count; i++ )
		{
			var a = poly[i];
			var b = poly[( i + 1 ) % poly.Count];
			var da = Vec3.Dot( normal, a ) - distance;
			var db = Vec3.Dot( normal, b ) - distance;
			var aIn = da <= eps;
			var bIn = db <= eps;
			if ( aIn ) output.Add( a );
			if ( aIn != bIn )
			{
				var t = da / ( da - db );
				output.Add( a + ( b - a ) * t );
			}
		}
		return output;
	}

	/// <summary>Newell normal: direction is the polygon normal, length is twice the area.</summary>
	static Vec3 NewellNormal( IReadOnlyList<Vec3> poly )
	{
		var n = Vec3.Zero;
		for ( var i = 0; i < poly.Count; i++ )
		{
			var a = poly[i];
			var b = poly[( i + 1 ) % poly.Count];
			n += new Vec3(
				( a.y - b.y ) * ( a.z + b.z ),
				( a.z - b.z ) * ( a.x + b.x ),
				( a.x - b.x ) * ( a.y + b.y ) );
		}
		return n;
	}

	static List<Vec3> Weld( List<Vec3> poly, float tolerance )
	{
		var output = new List<Vec3>();
		foreach ( var p in poly )
		{
			if ( output.Count == 0 || ( p - output[output.Count - 1] ).Length > tolerance )
				output.Add( p );
		}
		while ( output.Count > 1 && ( output[0] - output[output.Count - 1] ).Length <= tolerance )
			output.RemoveAt( output.Count - 1 );
		return output;
	}

	/// <summary>One grid vertex's UV: the displacement's explicit UVs when it has them (a landed Displacement's UvAt values), else the material's FaceUv transform of the 0..1 grid position.</summary>
	static Vec2 VertexUv( MapBakeDisplacement disp, FaceMaterial mat, int index, int grid )
	{
		if ( disp.Uvs is not null ) return disp.Uvs[index];
		return mat.Uv.Transform( new Vec2( (float)( index % grid ) / ( grid - 1 ), (float)( index / grid ) / ( grid - 1 ) ) );
	}

	static Vec3 Bilinear( Vec3[] corners, float s, float t )
	{
		var c0 = corners[0];
		var c1 = corners[1];
		var c2 = corners[2];
		var c3 = corners[3];
		return c0 * ( ( 1f - s ) * ( 1f - t ) ) + c1 * ( s * ( 1f - t ) ) + c2 * ( s * t ) + c3 * ( ( 1f - s ) * t );
	}
}

/// <summary>
/// One face of a map brush, as the bake consumes it: the MapBrush spec's
/// plane (outward <see cref="Normal"/>, interior Dot(Normal, p) &lt;=
/// <see cref="Distance"/>) plus the <see cref="FaceMaterial"/> the
/// FaceMaterial ticket landed (material name + FaceUv). Immutable.
/// </summary>
public readonly struct MapBakePlaneFace
{
	public readonly Vec3 Normal;
	public readonly float Distance;
	public readonly FaceMaterial Material;

	public MapBakePlaneFace( Vec3 normal, float distance, FaceMaterial material )
	{
		if ( normal.LengthSquared <= 1e-12f )
			throw new ArgumentException( "A brush face plane needs a non-zero normal." );
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		Normal = normal;
		Distance = distance;
		Material = material;
	}
}

/// <summary>
/// The bake-side form of a MapBrush (hammer-brush-core-plane-based-brushes):
/// faces as planes + per-face <see cref="FaceMaterial"/>. This is NOT a second
/// brush type — it carries the plane set the bake clips against and nothing
/// else. Adapt a landed MapBrush with <see cref="FromMapBrush"/> (a MapBrush
/// built FromVertices feeds in the same way once its planes are solved).
/// </summary>
public sealed class MapBakeBrush
{
	public string Name { get; }
	public IReadOnlyList<MapBakePlaneFace> Faces { get; }
	public int FaceCount => Faces.Count;

	public MapBakeBrush( string name, IReadOnlyList<MapBakePlaneFace> faces )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		if ( faces.Count < 4 )
			throw new ArgumentException( $"A brush needs at least 4 planes to enclose a solid; got {faces.Count}." );
		if ( faces.Any( f => f.Material is null ) )
			throw new ArgumentException( "Every brush face needs a material." );
		Name = string.IsNullOrWhiteSpace( name ) ? "brush" : name.Trim();
		Faces = faces.ToArray();
	}

	/// <summary>Mirror of the MapBrush spec's FromPlanes: parallel plane + material lists.</summary>
	public static MapBakeBrush FromPlanes( string name, IReadOnlyList<Vec3> normals, IReadOnlyList<float> distances, IReadOnlyList<FaceMaterial> materials )
	{
		if ( normals is null ) throw new ArgumentNullException( nameof( normals ) );
		if ( distances is null ) throw new ArgumentNullException( nameof( distances ) );
		if ( materials is null ) throw new ArgumentNullException( nameof( materials ) );
		if ( normals.Count != distances.Count || normals.Count != materials.Count )
			throw new ArgumentException( $"FromPlanes needs parallel lists; got {normals.Count} normals, {distances.Count} distances, {materials.Count} materials." );
		var faces = new MapBakePlaneFace[normals.Count];
		for ( var i = 0; i < normals.Count; i++ )
			faces[i] = new MapBakePlaneFace( normals[i], distances[i], materials[i] );
		return new MapBakeBrush( name, faces );
	}

	/// <summary>
	/// Adapt a landed <see cref="MapBrush"/>: its faces map 1:1 (plane +
	/// FaceMaterial; MapBrushFace.Material is never null). The plane convention
	/// is the same (unit outward normal, Normal.p &lt;= Distance inside), so
	/// nothing is re-solved or re-derived here.
	/// </summary>
	public static MapBakeBrush FromMapBrush( MapBrush brush, string name = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		var faces = new MapBakePlaneFace[brush.FaceCount];
		for ( var i = 0; i < brush.FaceCount; i++ )
		{
			var f = brush.Faces[i];
			faces[i] = new MapBakePlaneFace( f.Normal, f.Distance, f.Material );
		}
		return new MapBakeBrush( string.IsNullOrWhiteSpace( name ) ? "brush" : name.Trim(), faces );
	}

	/// <summary>
	/// Axis-aligned box. Face order is -X, +X, -Y, +Y, -Z (floor), +Z
	/// (ceiling). The box is a dumb constructor: min &gt;= max on an axis is
	/// NOT rejected here — the bake reports it as a zero-volume issue.
	/// </summary>
	public static MapBakeBrush Box( Vec3 min, Vec3 max, IReadOnlyList<FaceMaterial> faceMaterials )
	{
		if ( faceMaterials is null ) throw new ArgumentNullException( nameof( faceMaterials ) );
		if ( faceMaterials.Count != 6 )
			throw new ArgumentException( $"A box needs exactly 6 face materials (-X,+X,-Y,+Y,-Z,+Z); got {faceMaterials.Count}." );
		return FromPlanes( "box",
			new[] { new Vec3( -1f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), new Vec3( 0f, -1f, 0f ), new Vec3( 0f, 1f, 0f ), new Vec3( 0f, 0f, -1f ), new Vec3( 0f, 0f, 1f ) },
			new[] { -min.x, max.x, -min.y, max.y, -min.z, max.z },
			faceMaterials );
	}

	/// <summary>Box with one material on all six faces.</summary>
	public static MapBakeBrush Box( Vec3 min, Vec3 max, FaceMaterial material )
	{
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		return Box( min, max, new[] { material, material, material, material, material, material } );
	}

	/// <summary>Box with one material name on all six faces.</summary>
	public static MapBakeBrush Box( Vec3 min, Vec3 max, string materialName ) =>
		Box( min, max, FaceMaterial.ForMaterial( materialName ) );
}

/// <summary>
/// The bake-side form of a Displacement (hammer-displacement-data-model):
/// a subdivided quad bound to a brush face. Corners are ordered CCW seen from
/// outside, so Cross(c1-c0, c3-c0) is the outward face normal; offsets push
/// grid vertices along that normal; alpha blends MaterialA -&gt; MaterialB
/// (per-triangle, by centroid alpha &gt;= 0.5). Power 2/3/4 gives a
/// (2^power + 1)^2 grid. Sew is NOT here — the Displacement ticket owns it.
/// Adapt a landed Displacement with <see cref="FromDisplacement"/>.
/// </summary>
public sealed class MapBakeDisplacement
{
	public string Name { get; }
	public Vec3[] BaseCorners { get; }
	public int Power { get; }
	public int GridSize => ( 1 << Power ) + 1;
	public float[] Offsets { get; }
	public float[] Alphas { get; }
	public FaceMaterial MaterialA { get; }
	public FaceMaterial MaterialB { get; }

	/// <summary>Explicit per-vertex UVs (row-major, GridSize^2), used verbatim when present; null means derive from the materials' FaceUv.</summary>
	public Vec2[] Uvs { get; }

	/// <summary>Outward face normal from the base corners (unnormalized).</summary>
	public Vec3 FaceNormal => Vec3.Cross( BaseCorners[1] - BaseCorners[0], BaseCorners[3] - BaseCorners[0] );

	public MapBakeDisplacement( string name, Vec3[] baseCorners, int power, float[] offsets, float[] alphas, FaceMaterial materialA, FaceMaterial materialB, Vec2[] uvs = null )
	{
		var defects = Check( baseCorners, power, offsets, alphas, materialA, materialB, uvs );
		if ( defects.Count > 0 )
			throw new ArgumentException( "Invalid displacement: " + string.Join( "; ", defects ) );
		Name = string.IsNullOrWhiteSpace( name ) ? "displacement" : name.Trim();
		BaseCorners = baseCorners.ToArray();
		Power = power;
		Offsets = offsets.ToArray();
		Alphas = alphas.ToArray();
		MaterialA = materialA;
		MaterialB = materialB;
		Uvs = uvs is null ? null : uvs.ToArray();
	}

	/// <summary>Flat displacement: zero offsets, zero alphas (all MaterialA).</summary>
	public static MapBakeDisplacement Flat( string name, Vec3[] baseCorners, int power, FaceMaterial materialA, FaceMaterial materialB = null )
	{
		var grid = ( 1 << power ) + 1;
		var count = grid * grid;
		return new MapBakeDisplacement( name, baseCorners, power, new float[count], new float[count], materialA, materialB ?? materialA );
	}

	/// <summary>
	/// Adapt a landed <see cref="Effigy.Map.Displacement"/>: corners, power,
	/// offsets and alphas map 1:1 (same row-major grid layout). Two things the
	/// Displacement type does NOT carry, so the caller supplies them:
	/// <paramref name="materialA"/> / <paramref name="materialB"/> — the
	/// Displacement face stores only an int MaterialId, and the second
	/// material's identity is a map-level concern the Displacement ticket
	/// explicitly leaves out. The face's corner UVs are preserved EXACTLY via
	/// the explicit <see cref="Uvs"/> array (Displacement.UvAt, bilinear from
	/// the corner UVs), not re-derived from a FaceUv transform.
	/// </summary>
	public static MapBakeDisplacement FromDisplacement( Effigy.Map.Displacement disp, FaceMaterial materialA, FaceMaterial materialB, string name = null )
	{
		if ( disp is null ) throw new ArgumentNullException( nameof( disp ) );
		if ( materialA is null ) throw new ArgumentNullException( nameof( materialA ) );
		if ( materialB is null ) throw new ArgumentNullException( nameof( materialB ) );
		var grid = disp.GridSize;
		var uvs = new Vec2[grid * grid];
		for ( var r = 0; r < grid; r++ )
			for ( var c = 0; c < grid; c++ )
				uvs[r * grid + c] = disp.UvAt( r, c );
		return new MapBakeDisplacement( string.IsNullOrWhiteSpace( name ) ? "displacement" : name.Trim(),
			disp.Face.Corners, disp.Power, disp.Offsets, disp.Alphas, materialA, materialB, uvs );
	}

	/// <summary>Named defects, never a silent fix; empty means the displacement is usable.</summary>
	public IReadOnlyList<string> Validate() => Check( BaseCorners, Power, Offsets, Alphas, MaterialA, MaterialB, Uvs );

	static List<string> Check( Vec3[] baseCorners, int power, float[] offsets, float[] alphas, FaceMaterial materialA, FaceMaterial materialB, Vec2[] uvs )
	{
		var defects = new List<string>();
		if ( baseCorners is null || baseCorners.Length != 4 )
			defects.Add( $"A displacement needs exactly 4 base corners; got {( baseCorners is null ? 0 : baseCorners.Length )}." );
		if ( power < 2 || power > 4 )
			defects.Add( $"Displacement power is {power}; it must be 2, 3 or 4." );
		var expected = ( power >= 2 && power <= 4 ) ? ( ( 1 << power ) + 1 ) * ( ( 1 << power ) + 1 ) : -1;
		if ( offsets is null || offsets.Length != expected )
			defects.Add( $"Displacement offsets has {( offsets is null ? 0 : offsets.Length )} entries; the power-{power} grid needs {expected}." );
		if ( alphas is null || alphas.Length != expected )
			defects.Add( $"Displacement alphas has {( alphas is null ? 0 : alphas.Length )} entries; the power-{power} grid needs {expected}." );
		if ( uvs is not null && uvs.Length != expected )
			defects.Add( $"Displacement UVs has {uvs.Length} entries; the power-{power} grid needs {expected}." );
		if ( alphas is not null && alphas.Length == expected )
			for ( var i = 0; i < alphas.Length; i++ )
				if ( alphas[i] < 0f || alphas[i] > 1f ) { defects.Add( $"Displacement alpha {i} is {alphas[i]}; alphas must be 0..1." ); break; }
		if ( materialA is null ) defects.Add( "Displacement needs MaterialA." );
		if ( materialB is null ) defects.Add( "Displacement needs MaterialB." );
		if ( baseCorners is not null && baseCorners.Length == 4 &&
			Vec3.Cross( baseCorners[1] - baseCorners[0], baseCorners[3] - baseCorners[0] ).LengthSquared <= 1e-12f )
			defects.Add( "Displacement base corners are degenerate (zero face normal)." );
		return defects;
	}
}

/// <summary>
/// The bake-side form of a MapDocument (hammer-map-document-model): the map
/// as the bake consumes it — brushes + displacements. Entities, layers /
/// visgroups, compile settings and undo history are the MapDocument's own
/// concern and do not reach the bake; when MapDocument lands, the integrator
/// maps its Brushes/Displacements onto this type.
/// </summary>
public sealed class MapBakeDocument
{
	public string Name { get; }
	public IReadOnlyList<MapBakeBrush> Brushes { get; }
	public IReadOnlyList<MapBakeDisplacement> Displacements { get; }

	public MapBakeDocument( string name, IReadOnlyList<MapBakeBrush> brushes, IReadOnlyList<MapBakeDisplacement> displacements )
	{
		Name = string.IsNullOrWhiteSpace( name ) ? "map" : name.Trim();
		Brushes = brushes ?? Array.Empty<MapBakeBrush>();
		Displacements = displacements ?? Array.Empty<MapBakeDisplacement>();
	}
}

/// <summary>Bake knobs. Defaults suit Hammer-scale maps (inches, Z-up).</summary>
public sealed class MapBakeOptions
{
	/// <summary>Half-size of the starting polygon when a face plane is clipped (inches).</summary>
	public float ClipExtent { get; set; } = 8192f;
	/// <summary>Consecutive polygon vertices closer than this are welded (inches).</summary>
	public float WeldTolerance { get; set; } = 1e-3f;
	/// <summary>Faces/triangles smaller than this area are dropped + reported (sq inches).</summary>
	public float MinFaceArea { get; set; } = 1e-6f;
	/// <summary>OBJ keeps quads by default (the kernel's OBJ convention); true triangulates on write.</summary>
	public bool TriangulateObj { get; set; } = false;
	/// <summary>Object name written into OBJ files.</summary>
	public string ObjectName { get; set; } = "map";
	/// <summary>Options forwarded to GlTfExporter; null means ExportOptions.Default.</summary>
	public ExportOptions GltfOptions { get; set; }
	/// <summary>Diffuse colours for the .mtl, by material name; missing names get white.</summary>
	public IReadOnlyDictionary<string, Vec3> MaterialColours { get; set; }
}

/// <summary>
/// Provenance of one baked mesh face: which brush/displacement it came from,
/// its material name, and the FaceUv smoothing group / lightmap scale the
/// PolyMesh face itself cannot carry.
/// </summary>
public sealed class MapBakeFaceInfo
{
	public int BrushIndex;
	public int DisplacementIndex;
	public int FaceIndex;
	public string BrushName;
	public string Material;
	public int SmoothingGroup;
	public float LightmapScale;
	public bool IsDisplacement;
	public int MeshFaceIndex;
}

/// <summary>
/// A baked map: the static <see cref="PolyMesh"/> (face-split, quads kept,
/// per-face material slots), the material table (slot i == Materials[i],
/// first-appearance order), per-face provenance, and the bake report:
/// nodraw/degenerate exclusions and named <see cref="Issues"/>.
/// </summary>
public sealed class MapBakeMesh
{
	public PolyMesh Mesh { get; }
	public IReadOnlyList<string> Materials { get; }
	public IReadOnlyList<MapBakeFaceInfo> FaceInfo { get; }
	public IReadOnlyList<string> Issues { get; }
	public int ExcludedNoDrawFaces { get; }
	public int ExcludedDegenerateFaces { get; }
	public int BrushFaceCount { get; }
	public int DisplacementFaceCount { get; }
	public int FaceCount => Mesh.FaceCount;
	public int VertexCount => Mesh.VertexCount;

	public MapBakeMesh( PolyMesh mesh, IReadOnlyList<string> materials, IReadOnlyList<MapBakeFaceInfo> faceInfo,
		IReadOnlyList<string> issues, int excludedNoDrawFaces, int excludedDegenerateFaces, int brushFaceCount, int displacementFaceCount )
	{
		Mesh = mesh;
		Materials = materials;
		FaceInfo = faceInfo;
		Issues = issues;
		ExcludedNoDrawFaces = excludedNoDrawFaces;
		ExcludedDegenerateFaces = excludedDegenerateFaces;
		BrushFaceCount = brushFaceCount;
		DisplacementFaceCount = displacementFaceCount;
	}
}

/// <summary>
/// The outcome of an OBJ/GLB export of a <see cref="MapBakeMesh"/>: paths,
/// whether each file was written, and the mesh/material counts the export was
/// made from. Counts and names come from the baked mesh — see
/// <see cref="MapBakeExport.GlbNote"/> for why the .glb bytes themselves do
/// not yet carry them.
/// </summary>
public sealed class MapBakeExportResult
{
	public MapBakeMesh Baked { get; }
	public string ObjPath { get; set; }
	public string MtlPath { get; set; }
	public bool ObjWritten { get; set; }
	public string GlbPath { get; set; }
	public bool GlbWritten { get; set; }
	public string Note { get; set; } = "";
	public int MeshFaceCount => Baked.FaceCount;
	public int MeshVertexCount => Baked.VertexCount;
	public int MaterialCount => Baked.Materials.Count;
	public IReadOnlyList<string> MaterialNames => Baked.Materials;

	public MapBakeExportResult( MapBakeMesh baked )
	{
		Baked = baked ?? throw new ArgumentNullException( nameof( baked ) );
	}
}
