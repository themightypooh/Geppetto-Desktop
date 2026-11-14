using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Effigy;

/// <summary>
/// Finish: decimate + auto-UV/packing/texel-density + pre-export validation, as one
/// pipeline a mesh goes through before it is exported. This is the ticket-facing
/// finish layer (godlike-finish-decimate-auto-uv-validation, Wave 2 P1) — it does NOT
/// re-implement anything that already exists:
///
///   - Decimation itself is <see cref="Decimation"/> (godlike-decimation-projection):
///     presets, ratios, LOD chains, silhouette deviation. This file only CALLS it.
///   - Unwrapping is <see cref="UVUnwrap"/> (+ <see cref="UVAutoSeams"/> for seams),
///     packing is <see cref="UVPack"/>, density is <see cref="UVDensity"/>,
///     layout checks are <see cref="UVLayoutCheck"/>.
///   - Geometry validation and one-click fixes are <see cref="MeshDoctor"/> — the ONE
///     shared check. Nothing here duplicates a Mesh Doctor count or fix.
///   - Auto-retopo is the OpenVDB/remesh board task, not this file.
///
/// MUTATION. <see cref="FinishPipeline.Finish"/> and the <c>Decimate*</c> calls never
/// modify the source mesh: decimation returns a new mesh and the UV/validation stages
/// run on that copy. <see cref="FinishUv.AutoUv"/>, <see cref="FinishUv.EqualiseDensity"/>
/// and <see cref="FinishValidation.FixAll"/> DO modify the mesh they are handed, in
/// place — that is the unwrap/fix contract, and it is said on each method.
///
/// EXPORT HOOK (integrator wiring, not edited here). <c>core/ExportService.cs:506</c>
/// already gates every export on <see cref="MeshDoctor.PreExportCheck"/> and blocks
/// when it blocks. The UV/density gate in <see cref="FinishValidation.Validate"/> is
/// the addition to wire at that same site: after the existing gate, call
/// <c>FinishValidation.Validate</c> on the same gate meshes and block on its
/// <see cref="FinishValidationReport.Blocked"/> exactly as the geometry gate is
/// blocked on today. Do not add a second gate elsewhere.
/// </summary>
public static class FinishPipeline
{
	/// <summary>What one finish run does, in order. Decimation first (it changes the
	/// topology, so UVs made before it would be wasted), then auto-UV, then the
	/// validation gate, then the one-click fix when the gate is not clean.</summary>
	public sealed class FinishOptions
	{
		/// <summary>One of <see cref="Decimation.Presets"/> (20K / 35K / 75K / 150K /
		/// 250K triangles). Wins over <see cref="TargetTriangles"/> and
		/// <see cref="TargetRatio"/>. 0 means no preset.</summary>
		public int TargetPreset;

		/// <summary>Exact triangle target. Wins over <see cref="TargetRatio"/>.
		/// 0 means no target.</summary>
		public int TargetTriangles;

		/// <summary>Fraction of the source triangles to keep, in (0, 1]. 0 means
		/// no ratio decimation.</summary>
		public float TargetRatio;

		/// <summary>Texture size the UV layout is built and checked at, in pixels.</summary>
		public int TextureSize = 1024;

		/// <summary>Texel density to equalise every island to, in pixels per mesh
		/// unit, at <see cref="TextureSize"/>. 0 means leave the density the
		/// unwrapper produced.</summary>
		public float TargetPixelsPerUnit;

		/// <summary>Charting angle for the unwrap, in degrees.</summary>
		public float UvAngleDegrees = 66f;

		/// <summary>Gutter between islands as a fraction of the square.</summary>
		public float UvMargin = 0.01f;

		/// <summary>Generate seams with <see cref="UVAutoSeams"/> instead of using
		/// only the seams the mesh already carries.</summary>
		public bool UseAutoSeams = true;

		/// <summary>Pass strictHoles: true when the caller needs a watertight solid
		/// (print pipeline) instead of an open surface.</summary>
		public bool StrictHoles;

		/// <summary>Run the one-click fix when the validation gate is not clean.</summary>
		public bool FixBeforeExport = true;

		/// <summary>Export-size budget the finished mesh is checked against, in
		/// bytes. The ticket's acceptance is a decimated export under ~50 MB.</summary>
		public long MaxExportBytes = 50_000_000;
	}

	/// <summary>Everything one finish run produced, so the caller can report it
	/// instead of guessing.</summary>
	public sealed class FinishResult
	{
		/// <summary>The finished mesh. Never the input mesh: it is the decimated
		/// mesh when decimation ran, else a clone of the input.</summary>
		public PolyMesh Mesh;

		/// <summary>The decimation step, or null when no decimation was asked for.</summary>
		public Decimation.Result Decimation;

		/// <summary>The auto-UV step. Null only when the mesh has no faces (which
		/// Finish refuses before it gets here).</summary>
		public AutoUvResult Uv;

		/// <summary>Validation before the one-click fix.</summary>
		public FinishValidationReport Before;

		/// <summary>The one-click fix, or null when the gate was already clean and
		/// no fix ran.</summary>
		public FinishFixReport Fix;

		/// <summary>Validation after the fix (or the same as <see cref="Before"/>
		/// when no fix ran).</summary>
		public FinishValidationReport After;

		/// <summary>Rough export-size estimate for the finished mesh, in bytes
		/// (see <see cref="EstimateExportBytes"/>).</summary>
		public long ObjBytesEstimate;
		public long GlbBytesEstimate;

		/// <summary>Both estimates under <see cref="FinishOptions.MaxExportBytes"/>.</summary>
		public bool FitsExportBudget;

		public List<string> Notes = new();

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.Append( Decimation is null
				? $"{After.Doctor.Triangles} triangles, no decimation"
				: $"{Decimation.FromTriangles} -> {Decimation.ToTriangles} triangles" );
			sb.Append( $", uv: {Uv.Layout.Verdict}" );
			sb.Append( After.Blocked ? ", BLOCKED" : After.Warn ? ", warn" : ", pass" );
			sb.Append( $", export ~{ObjBytesEstimate / 1000000.0:0.0} MB OBJ / ~{GlbBytesEstimate / 1000000.0:0.0} MB GLB" );
			return sb.ToString();
		}
	}

	/// <summary>
	/// Run the whole finish on a mesh and hand back the finished mesh plus the report.
	/// The input mesh is never modified. Decimation (when asked for) runs first through
	/// <see cref="Decimation"/>; then the working copy is unwrapped, density-equalised
	/// and packed; then it is validated; then the one-click fix runs when the gate is
	/// not clean and <see cref="FinishOptions.FixBeforeExport"/> is set.
	/// </summary>
	public static FinishResult Finish( PolyMesh mesh, string name, FinishOptions options = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( mesh.FaceCount == 0 )
			throw new ArgumentException( "finish needs faces: an empty mesh has nothing to decimate, unwrap or validate", nameof( mesh ) );
		options ??= new FinishOptions();

		var result = new FinishResult();
		PolyMesh work;

		if ( options.TargetPreset != 0 )
		{
			result.Decimation = Decimation.DecimateToPreset( mesh, options.TargetPreset, name: name );
			work = result.Decimation.Mesh;
		}
		else if ( options.TargetTriangles > 0 )
		{
			result.Decimation = Decimation.DecimateTo( mesh, options.TargetTriangles, name: name );
			work = result.Decimation.Mesh;
		}
		else if ( options.TargetRatio > 0f )
		{
			result.Decimation = Decimation.DecimateToRatio( mesh, options.TargetRatio, name: name );
			work = result.Decimation.Mesh;
		}
		else
		{
			work = mesh.Clone();
		}

		result.Mesh = work;

		var uvOptions = new AutoUvOptions
		{
			AngleDegrees = options.UvAngleDegrees,
			Margin = options.UvMargin,
			TextureSize = options.TextureSize,
			TargetPixelsPerUnit = options.TargetPixelsPerUnit,
			UseAutoSeams = options.UseAutoSeams,
		};
		result.Uv = FinishUv.AutoUv( work, uvOptions );
		if ( result.Uv.Pack is not null && !result.Uv.Pack.Fits )
			result.Notes.Add( $"layout does not fit at {options.TextureSize}px — raise the texture size or lower the target density" );

		result.Before = FinishValidation.Validate( work, name, options.TextureSize, options.StrictHoles );

		if ( options.FixBeforeExport && ( result.Before.Blocked || result.Before.Warn ) )
		{
			result.Fix = FinishValidation.FixAll( work, name, options.TextureSize, uvOptions: uvOptions );
			result.After = result.Fix.After;
			if ( result.After.Blocked )
				result.Notes.Add( "one-click fix did not clear the gate — see the After report for what remains" );
		}
		else
		{
			result.After = result.Before;
		}

		( result.ObjBytesEstimate, result.GlbBytesEstimate ) = EstimateExportBytes( work );
		result.FitsExportBudget = result.ObjBytesEstimate <= options.MaxExportBytes
			&& result.GlbBytesEstimate <= options.MaxExportBytes;
		if ( !result.FitsExportBudget )
			result.Notes.Add( $"export estimate over the {options.MaxExportBytes / 1000000.0:0.#} MB budget — decimate further or raise the budget" );

		return result;
	}

	/// <summary>
	/// Rough export size for a mesh, in bytes, so the &lt;~50 MB acceptance can be
	/// checked without writing the file. ESTIMATE ONLY, ±30%: the real writer is
	/// authoritative. Assumptions, stated so they can be checked: OBJ is ASCII,
	/// ~30 bytes per vertex line ("v x y z") and ~22 bytes per face line
	/// ("f a b c d") plus a 1 KB header; GLB is binary, 32 bytes per vertex
	/// (position + normal + UV) and 12 bytes per triangle of uint32 indices,
	/// plus a 2 KB JSON chunk. Morphs, skinning and textures are not counted.
	/// </summary>
	public static (long ObjBytes, long GlbBytes) EstimateExportBytes( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var verts = mesh.VertexCount;
		var faces = mesh.FaceCount;
		var tris = Decimate.TriangleCount( mesh );
		var obj = verts * 30L + faces * 22L + 1024;
		var glb = verts * 32L + tris * 12L + 2048;
		return ( obj, glb );
	}

	/// <summary>Both export estimates under <paramref name="maxBytes"/> (default
	/// 50 MB, the ticket's decimated-export budget).</summary>
	public static bool FitsExportBudget( PolyMesh mesh, long maxBytes = 50_000_000 )
	{
		var (obj, glb) = EstimateExportBytes( mesh );
		return obj <= maxBytes && glb <= maxBytes;
	}
}

/// <summary>How the auto-UV step runs. All sizes are in pixels; densities are in
/// pixels per mesh unit (inches here) at <see cref="TextureSize"/>.</summary>
public sealed class AutoUvOptions
{
	/// <summary>Charting angle for the unwrap, in degrees.</summary>
	public float AngleDegrees = 66f;

	/// <summary>Gutter between islands as a fraction of the square.</summary>
	public float Margin = 0.01f;

	/// <summary>Texture size the layout is built and checked at, in pixels.</summary>
	public int TextureSize = 1024;

	/// <summary>Texel density to equalise every island to, in pixels per mesh
	/// unit. 0 means leave the density the unwrapper produced.</summary>
	public float TargetPixelsPerUnit;

	/// <summary>Generate seams with <see cref="UVAutoSeams"/> instead of using
	/// only the seams the mesh already carries.</summary>
	public bool UseAutoSeams = true;

	/// <summary>Unwrap mode: angle charts for hard surfaces, organic for closed
	/// sculpted forms.</summary>
	public UnwrapMode Mode = UnwrapMode.Angle;

	/// <summary>Flattening: conformal is the real one, planar the honest baseline.</summary>
	public UnwrapFlatten Flatten = UnwrapFlatten.Conformal;

	/// <summary>Gutter between islands when packing, in pixels at
	/// <see cref="TextureSize"/>.</summary>
	public float PackMarginPixels = 4f;
}

/// <summary>What the auto-UV step produced.</summary>
public sealed class AutoUvResult
{
	public UnwrapReport Unwrap;
	public UVDensity.Report DensityBefore;
	public UVDensity.Report DensityAfter;
	public UVPack.Result Pack;
	public UVLayoutReport Layout;

	/// <summary>Texture size the mesh actually needs at the target density
	/// (see <see cref="FinishUv.RequiredTextureSize"/>).</summary>
	public int RequiredTextureSize;

	public List<string> Warnings = new();
}

/// <summary>
/// Auto-UV: seam-guided unwrap, texel-density equalisation, packing — the UV half
/// of the finish pipeline. Everything here mutates the mesh in place (that is the
/// unwrap contract); callers that must keep the original pass a clone.
/// </summary>
public static class FinishUv
{
	/// <summary>
	/// Unwrap a mesh in place: seams first (<see cref="UVAutoSeams"/> when
	/// <see cref="AutoUvOptions.UseAutoSeams"/> is set and it finds any, else the
	/// seams the mesh already carries), then <see cref="UVUnwrap.Unwrap"/>, then
	/// density equalisation when a target is set, then <see cref="UVPack.Pack"/>,
	/// then the layout check. Returns the unwrap, the density before and after,
	/// the pack result and the layout verdict.
	/// </summary>
	public static AutoUvResult AutoUv( PolyMesh mesh, AutoUvOptions options = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( mesh.FaceCount == 0 )
			throw new ArgumentException( "auto-UV needs faces", nameof( mesh ) );
		options ??= new AutoUvOptions();

		HashSet<EdgeKey> seams = null;
		if ( options.UseAutoSeams )
		{
			var generated = UVAutoSeams.Generate( mesh,
				new UVAutoSeams.Options { AngleDegrees = options.AngleDegrees } );
			if ( generated.Seams.Count > 0 )
				seams = generated.Seams;
		}

		var unwrap = UVUnwrap.Unwrap( mesh, options.AngleDegrees, options.Margin,
			seams, options.Mode, options.Flatten );

		var densityBefore = UVDensity.Measure( mesh, options.TextureSize );
		if ( options.TargetPixelsPerUnit > 0f )
			UVDensity.Set( mesh, options.TargetPixelsPerUnit, options.TextureSize );

		var pack = UVPack.Pack( mesh, new UVPack.Options
		{
			TextureSize = options.TextureSize,
			MarginPixels = options.PackMarginPixels,
		} );

		var densityAfter = UVDensity.Measure( mesh, options.TextureSize );
		var layout = UVLayoutCheck.Check( mesh, options.TextureSize );

		var result = new AutoUvResult
		{
			Unwrap = unwrap,
			DensityBefore = densityBefore,
			DensityAfter = densityAfter,
			Pack = pack,
			Layout = layout,
			RequiredTextureSize = RequiredTextureSize( mesh,
				options.TargetPixelsPerUnit > 0f ? options.TargetPixelsPerUnit : densityAfter.PixelsPerUnit ),
		};

		if ( !pack.Fits )
			result.Warnings.Add( $"layout does not fit at {options.TextureSize}px" );
		if ( layout.Verdict != "PASS" )
			result.Warnings.AddRange( layout.Reasons );
		result.Warnings.AddRange( TexelDensityWarnings( mesh, options.TextureSize ) );

		return result;
	}

	/// <summary>
	/// Equalise texel density without re-unwrapping: scale every island about its
	/// own centre to <paramref name="pixelsPerUnit"/> at
	/// <paramref name="textureSize"/>, then repack (the pack scales every island
	/// by the same factor, so the equalisation survives it). Use this on a
	/// hand-made layout; <see cref="AutoUv"/> already equalises a fresh unwrap.
	/// Returns the density report after the repack.
	/// </summary>
	public static UVDensity.Report EqualiseDensity( PolyMesh mesh, int textureSize, float pixelsPerUnit )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		UVDensity.Set( mesh, pixelsPerUnit, textureSize );
		UVPack.Pack( mesh, new UVPack.Options { TextureSize = textureSize } );
		return UVDensity.Measure( mesh, textureSize );
	}

	/// <summary>
	/// Texture size a mesh needs at <paramref name="pixelsPerUnit"/>: the surface
	/// area at that density needs area × density² texels, so the side is
	/// sqrt(area) × density, rounded up to the next power of two (64 minimum,
	/// 16384 maximum). This is the enforcement behind the ticket's rule of thumb
	/// that a dense hero needs a 2K map: when this exceeds the chosen texture
	/// size, <see cref="TexelDensityWarnings"/> says so instead of silently
	/// shipping a blurry map.
	/// </summary>
	public static int RequiredTextureSize( PolyMesh mesh, float pixelsPerUnit )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( !( pixelsPerUnit > 0f ) || !float.IsFinite( pixelsPerUnit ) )
			throw new InvalidOperationException( "the texel density has to be above zero: how many pixels one unit of surface should get." );

		var area = 0.0;
		foreach ( var f in mesh.Faces )
			area += mesh.FaceArea( f );
		if ( area <= 1e-12 )
			return 64;

		var side = Math.Ceiling( Math.Sqrt( area ) * pixelsPerUnit );
		var size = 64;
		while ( size < side && size < 16384 )
			size *= 2;
		return size;
	}

	/// <summary>
	/// Texel-density warnings for a mesh at <paramref name="textureSize"/>:
	/// island spread over 1.25× (the layout is not even), and the required
	/// texture size at the mesh's own average density exceeding the chosen size
	/// (the map is too small for the detail the mesh carries). Empty when the
	/// density is even and the size is enough.
	/// </summary>
	public static List<string> TexelDensityWarnings( PolyMesh mesh, int textureSize, float spreadTolerance = 1.25f )
	{
		var warnings = new List<string>();
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var density = UVDensity.Measure( mesh, textureSize );
		if ( density.Islands.Count > 1 && density.Min > 0f && density.Max / density.Min > spreadTolerance )
			warnings.Add( $"texel density spread {density.Max / density.Min:0.00}x over the {spreadTolerance:0.00}x tolerance — equalise it" );

		if ( density.PixelsPerUnit > 0f )
		{
			var required = RequiredTextureSize( mesh, density.PixelsPerUnit );
			if ( required > textureSize )
				warnings.Add( $"needs a {required}px map at {density.PixelsPerUnit:0.#} px per unit — the {textureSize}px map is too small for this density" );
		}

		return warnings;
	}
}

/// <summary>What the stretch heatmap found. 1 is perfect: every direction across
/// every triangle gets its share of texels. The 1.5 guide comes from
/// <see cref="UnwrapReport"/>: past it, some direction gets half as many texels
/// again, or two thirds as many, as the average says it should.</summary>
public sealed class StretchReport
{
	public float Mean;
	public float Max;
	public float Threshold;

	/// <summary>Faces whose area ratio is further than <see cref="Threshold"/> from
	/// the layout mean, in either direction.</summary>
	public int OverThreshold;

	/// <summary>Worst faces first, by how far their area ratio sits from the mean.</summary>
	public List<int> WorstFaces = new();

	public override string ToString() => OverThreshold == 0
		? $"stretch {Mean:0.00} mean / {Max:0.00} max — clean at the {Threshold:0.00} guide"
		: $"stretch {Mean:0.00} mean / {Max:0.00} max — {OverThreshold} face(s) over the {Threshold:0.00} guide";
}

/// <summary>
/// Stretch heatmap: where the UV layout bends the surface, per face and per island.
/// This only MEASURES — it never moves a UV. A flagged island is fixed by relaxing
/// it (<see cref="UVIslands.Relax"/>) or re-unwrapping it, which is the caller's job.
/// </summary>
public static class FinishStretch
{
	/// <summary>Directional stretch over the whole mesh plus the worst faces by
	/// area ratio, so the heatmap has somewhere to point.</summary>
	public static StretchReport Measure( PolyMesh mesh, float threshold = 1.5f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );
		if ( !( threshold > 1f ) )
			throw new InvalidOperationException( "the stretch guide has to be above 1: 1 is a perfect isometry." );

		var (mean, max) = UVIslands.Distortion( mesh );
		var perFace = UVIslands.Stretch( mesh );

		var ranked = new List<(int face, float deviation)>();
		for ( var i = 0; i < perFace.Length; i++ )
		{
			var ratio = perFace[i];
			var deviation = ratio >= 1f ? ratio : 1f / MathF.Max( ratio, 1e-6f );
			if ( deviation > threshold )
				ranked.Add( (i, deviation) );
		}
		ranked.Sort( ( a, b ) => b.deviation.CompareTo( a.deviation ) );

		return new StretchReport
		{
			Mean = mean,
			Max = max,
			Threshold = threshold,
			OverThreshold = ranked.Count,
			WorstFaces = ranked.Select( r => r.face ).ToList(),
		};
	}

	/// <summary>Islands whose directional stretch is over the guide, worst first.
	/// Each entry is the island index in <see cref="UVIslands.Find"/> order with
	/// its mean and worst stretch.</summary>
	public static List<(int Island, float Mean, float Max)> IslandsOverThreshold( PolyMesh mesh, float threshold = 1.5f )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var layout = UVIslands.Find( mesh );
		var over = new List<(int Island, float Mean, float Max)>();
		for ( var i = 0; i < layout.Islands.Count; i++ )
		{
			var (mean, max) = UVIslands.Distortion( mesh, layout.Islands[i] );
			if ( max > threshold )
				over.Add( (i, mean, max) );
		}
		over.Sort( ( a, b ) => b.Max.CompareTo( a.Max ) );
		return over;
	}
}

/// <summary>One pre-export validation, geometry gate plus UV gate, in one report.
/// The geometry half IS the shared Mesh Doctor check — the same call the export
/// path makes — so a mesh that passes here passes there. The UV half is the part
/// the geometry gate does not cover: overlapping and outside-UV faces, flipped
/// UV winding, and texel-density spread.</summary>
public sealed class FinishValidationReport
{
	/// <summary>The shared Mesh Doctor report (counts, hard fails, warnings).</summary>
	public MeshDoctorReport Doctor;

	/// <summary>The UV layout check.</summary>
	public UVLayoutReport Layout;

	/// <summary>Texel density per island and overall.</summary>
	public UVDensity.Report Density;

	/// <summary>True when the export must not go ahead: a geometry hard fail, or
	/// the UV layout failing outright.</summary>
	public bool Blocked;

	/// <summary>True when the export may go ahead but carries warnings.</summary>
	public bool Warn;

	public List<string> HardFails = new();
	public List<string> Warnings = new();
	public string Summary = "";

	public override string ToString() => Summary;
}

/// <summary>What the one-click fix did: the validation before and after, the
/// Mesh Doctor fix steps, and whether the UVs had to be rebuilt.</summary>
public sealed class FinishFixReport
{
	public FinishValidationReport Before;
	public FinishValidationReport After;
	public MeshDoctorFixResult Fix;
	public List<string> Steps = new();

	/// <summary>True when the UV layout still failed after the geometry fix and
	/// the mesh was re-unwrapped and repacked to clear it.</summary>
	public bool UvReworked;
}

/// <summary>
/// Pre-export validation + one-click fix — the finish gate. The geometry check is
/// <see cref="MeshDoctor.PreExportCheck"/>, the exact call the export path already
/// makes (<c>core/ExportService.cs:506</c>); this class adds the UV/density gate
/// beside it and the fix that clears what it can. It never invents a second
/// geometry check: every geometry count here comes from the Mesh Doctor report.
/// </summary>
public static class FinishValidation
{
	/// <summary>
	/// Validate one mesh for export. Blocked on a Mesh Doctor hard fail, or on
	/// the UV layout failing outright (overlapping / outside-UV / flipped faces);
	/// warns on holes (unless <paramref name="strictHoles"/> makes them block),
	/// UV warnings, and uneven texel density. Exact on fixtures: every defect
	/// class the Mesh Doctor counts appears in <see cref="FinishValidationReport.HardFails"/>
	/// or <see cref="FinishValidationReport.Warnings"/> by name.
	/// </summary>
	public static FinishValidationReport Validate( PolyMesh mesh, string name = null, int textureSize = 1024, bool strictHoles = false )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var doctor = MeshDoctor.PreExportCheck( mesh, name, strictHoles );
		var layout = UVLayoutCheck.Check( mesh, textureSize );
		var density = UVDensity.Measure( mesh, textureSize );

		var uvBlocked = layout.Verdict == "FAIL";
		var report = new FinishValidationReport
		{
			Doctor = doctor.Report,
			Layout = layout,
			Density = density,
			Blocked = doctor.Block || uvBlocked,
			Warn = !doctor.Block && !uvBlocked && ( doctor.Warn || layout.Verdict == "WARN" ),
		};

		report.HardFails.AddRange( doctor.Report.HardFails );
		if ( uvBlocked )
			report.HardFails.AddRange( layout.Reasons );

		report.Warnings.AddRange( doctor.Report.Warnings );
		if ( layout.Verdict == "WARN" )
			report.Warnings.AddRange( layout.Reasons );
		if ( density.Islands.Count > 1 && density.Min > 0f && density.Max / density.Min > 2f )
			report.Warnings.Add( $"texel density spread {density.Max / density.Min:0.00}x over the 2x guide — equalise it" );

		report.Summary = $"{name ?? "mesh"}: {( report.Blocked ? "BLOCKED" : report.Warn ? "warn" : "pass" )} — {doctor.Summary} | uv: {layout}";
		return report;
	}

	/// <summary>Validate a whole export: every mesh through the same gate.
	/// Blocked when any mesh blocks; the UV layout reported is the worst one.
	/// Meshes with no faces are skipped (the capability check owns "has geometry").</summary>
	public static FinishValidationReport ValidateMany( IReadOnlyList<KeyValuePair<string, PolyMesh>> meshes, int textureSize = 1024, bool strictHoles = false )
	{
		if ( meshes is null )
			throw new ArgumentNullException( nameof( meshes ) );

		var doctor = MeshDoctor.PreExportCheck( meshes, strictHoles );
		var report = new FinishValidationReport
		{
			Doctor = doctor.Report,
			Layout = new UVLayoutReport { Verdict = "PASS" },
			Density = new UVDensity.Report { TextureSize = textureSize },
		};

		UVLayoutReport worst = null;
		foreach ( var kv in meshes )
		{
			if ( kv.Value is null || kv.Value.FaceCount == 0 )
				continue;
			var one = Validate( kv.Value, kv.Key, textureSize, strictHoles );
			if ( one.Blocked )
				report.Blocked = true;
			if ( one.Warn )
				report.Warn = true;
			report.HardFails.AddRange( one.HardFails.Select( h => $"{kv.Key}: {h}" ) );
			report.Warnings.AddRange( one.Warnings.Select( w => $"{kv.Key}: {w}" ) );
			if ( worst is null || Severity( one.Layout.Verdict ) > Severity( worst.Verdict ) )
				worst = one.Layout;
		}

		if ( worst is not null )
			report.Layout = worst;
		report.Warn = report.Warn && !report.Blocked;
		report.Summary = $"export: {( report.Blocked ? "BLOCKED" : report.Warn ? "warn" : "pass" )} — {doctor.Summary}";
		return report;

		static int Severity( string verdict ) => verdict switch
		{
			"FAIL" => 2,
			"WARN" => 1,
			_ => 0,
		};
	}

	/// <summary>
	/// The one-click fix. Mutates <paramref name="mesh"/> in place: first the
	/// Mesh Doctor fix (loose vertices, doubles, degenerate faces, holes, UV
	/// fill, normals — see <see cref="MeshDoctorFixOptions"/>), then, when the UV
	/// layout still fails outright, a full re-unwrap and repack. The After report
	/// says honestly what is left: a fix that could not clear a defect leaves it
	/// listed, it never hides it. Undo the geometry steps with
	/// <see cref="MeshDoctor.UndoAll"/> on the returned fix result; the UV rework
	/// has no undo (the old UVs are gone — re-unwrap again to change them).
	/// </summary>
	public static FinishFixReport FixAll( PolyMesh mesh, string name = null, int textureSize = 1024,
		MeshDoctorFixOptions fixOptions = null, AutoUvOptions uvOptions = null )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var before = Validate( mesh, name, textureSize );
		var fix = MeshDoctor.Fix( mesh, fixOptions );
		var steps = new List<string>( fix.Log );
		var uvReworked = false;

		var layout = UVLayoutCheck.Check( mesh, textureSize );
		if ( layout.Verdict == "FAIL" )
		{
			uvOptions ??= new AutoUvOptions { TextureSize = textureSize };
			uvOptions.TextureSize = textureSize;
			FinishUv.AutoUv( mesh, uvOptions );
			uvReworked = true;
			steps.Add( "uv: re-unwrapped and repacked to clear overlapping / outside-UV faces" );
		}

		return new FinishFixReport
		{
			Before = before,
			After = Validate( mesh, name, textureSize ),
			Fix = fix,
			Steps = steps,
			UvReworked = uvReworked,
		};
	}
}
