#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// One-click ship checklist + finish-viewer settings (godlike File-Finish-Bake P2).
///
/// The checklist answers one question before an asset is uploaded to a store:
/// is this asset, as exported for this engine preset, shippable? It checks
/// polycount, texture resolution, map completeness, scale/units and forward/up
/// axis, plus mesh integrity (non-manifold edges, holes, degenerate and
/// zero-area faces, UV range). Mesh-integrity failures are errors for the 3D
/// print preset (a print must be watertight) and warnings for game/film
/// presets, where open meshes are legitimate.
///
/// The finish viewer modes (turntable + PBR preview with HDRI, matcap/clay/
/// normal/AO/curvature/UV-checker, wireframe/LOD comparison) are settings data
/// here. This ticket is backend-only (no UI panels): the viewer ticket consumes
/// FinishViewerSettings through the shared SceneRenderer. No rendering code
/// is duplicated for the checklist.
///
/// FromStudio derives the mesh facts from a PartStudio. It counts triangles by
/// fanning n-gon faces, matches edges by quantised vertex position (so a mesh
/// with duplicated seam vertices still reports its real boundary), and treats
/// a face as degenerate when it has fewer than three corners or a repeated
/// vertex, and zero-area when its fanned area is ~0. Overlapping-UV detection
/// is NOT claimed: the UV checks cover out-of-range and degenerate UV
/// triangles only, and say so.
/// </summary>
public enum ShipCheckSeverity
{
	Error,
	Warning
}

public sealed record ShipCheck(
	string Name,
	bool Passed,
	ShipCheckSeverity Severity,
	string Detail );

public sealed record ShipCheckResult(
	bool Passed,
	IReadOnlyList<ShipCheck> Checks )
{
	public IReadOnlyList<ShipCheck> Errors =>
		Checks.Where( c => !c.Passed && c.Severity == ShipCheckSeverity.Error ).ToList();
	public IReadOnlyList<ShipCheck> Warnings =>
		Checks.Where( c => !c.Passed && c.Severity == ShipCheckSeverity.Warning ).ToList();
}

public sealed record TextureSetInfo(
	string Name,
	int Width,
	int Height,
	IReadOnlyList<SourceChannel> PresentMaps );

public sealed record ShipChecklistInput(
	int FaceCount,
	int TriangleCount,
	int VertexCount,
	bool HasNonManifoldEdges,
	bool HasHoles,
	bool HasDegenerateFaces,
	bool HasZeroAreaFaces,
	int UvOutOfRangeCount,
	int UvDegenerateCount,
	bool IsWatertight,
	Vec3 BoundsMin,
	Vec3 BoundsMax,
	string DeclaredUpAxis,
	string DeclaredUnits,
	IReadOnlyList<TextureSetInfo> TextureSets,
	EngineExportPreset Preset,
	long MaxTriangles );

/// <summary>Finish-viewer modes. Data only; the viewer ticket wires these to SceneRenderer.</summary>
public enum FinishPreviewMode
{
	PbrHdri,
	Matcap,
	Clay,
	Normal,
	AmbientOcclusion,
	Curvature,
	UvChecker,
	Wireframe
}

public sealed record FinishViewerSettings(
	FinishPreviewMode Mode = FinishPreviewMode.PbrHdri,
	double TurntableRpm = 6,
	string? HdriPath = null,
	bool ShowWireframeOverlay = false,
	int LodIndex = 0,
	bool CompareLod = false )
{
	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();
		if ( TurntableRpm < 0 || TurntableRpm > 60 )
			errors.Add( $"Turntable speed {TurntableRpm} rpm is outside 0-60 rpm." );
		if ( LodIndex < 0 || LodIndex > 3 )
			errors.Add( $"LOD index {LodIndex} is outside LOD0-LOD3." );
		if ( Mode == FinishPreviewMode.PbrHdri && string.IsNullOrWhiteSpace( HdriPath ) )
			errors.Add( "PBR/HDRI preview needs an HDRI path; none is set." );
		return errors;
	}
}

public static class ShipChecklist
{
	/// <summary>Longest edge a shippable asset may have, in Desktop inches (10,000 in = 254 m).</summary>
	public const double MaxLongestEdgeInches = 10000;

	/// <summary>Default hero triangle budget; the finish presets are 20K/35K/75K/150K/250K.</summary>
	public const long DefaultHeroTriangleBudget = 250000;

	public const long DefaultPrintTriangleBudget = 1000000;

	/// <summary>Expected output axes per engine. Print has none: axes are irrelevant to a print.</summary>
	public static (string Up, string Forward)? ExpectedAxes( EngineTarget target ) => target switch
	{
		EngineTarget.Unreal5 => ("Z", "X"),
		EngineTarget.UnityUrp => ("Y", "Z"),
		EngineTarget.UnityHdrp => ("Y", "Z"),
		EngineTarget.Godot4 => ("Y", "-Z"),
		EngineTarget.Gltf => ("Y", "Z"),
		EngineTarget.Usd => ("Y", "Z"),
		EngineTarget.Sketchfab => ("Y", "Z"),
		EngineTarget.Print3d => null,
		_ => null
	};

	public static ShipCheckResult Run( ShipChecklistInput input )
	{
		ArgumentNullException.ThrowIfNull( input );
		ArgumentNullException.ThrowIfNull( input.Preset );
		var checks = new List<ShipCheck>();
		var preset = input.Preset;
		bool isPrint = preset.Target == EngineTarget.Print3d;
		var meshSeverity = isPrint ? ShipCheckSeverity.Error : ShipCheckSeverity.Warning;

		checks.Add( new ShipCheck( "preset exportable", preset.Supported, ShipCheckSeverity.Error,
			preset.Supported
				? $"Preset '{preset.Id}' can be exported from Desktop."
				: $"Preset '{preset.Id}' cannot be exported from Desktop: {preset.UnsupportedReason}" ) );

		checks.Add( new ShipCheck( "geometry present",
			input.TriangleCount > 0 && input.FaceCount > 0, ShipCheckSeverity.Error,
			$"{input.TriangleCount} triangles in {input.FaceCount} faces across {input.VertexCount} vertices." ) );

		long limit = input.MaxTriangles > 0
			? input.MaxTriangles
			: isPrint ? DefaultPrintTriangleBudget : DefaultHeroTriangleBudget;
		checks.Add( new ShipCheck( "triangle budget",
			input.TriangleCount > 0 && input.TriangleCount <= limit, ShipCheckSeverity.Error,
			$"{input.TriangleCount} triangles against a budget of {limit} for preset '{preset.Id}'." ) );

		double longestEdge = LongestEdge( input.BoundsMin, input.BoundsMax );
		bool boundsFinite = IsFinite( input.BoundsMin ) && IsFinite( input.BoundsMax );
		checks.Add( new ShipCheck( "bounds sane",
			boundsFinite && longestEdge > 0 && longestEdge <= MaxLongestEdgeInches, ShipCheckSeverity.Error,
			boundsFinite
				? $"Longest edge {longestEdge:0.###} in; limit {MaxLongestEdgeInches:0} in."
				: "Bounds contain a non-finite value." ) );

		checks.Add( new ShipCheck( "document units",
			string.Equals( input.DeclaredUnits, "inches", StringComparison.OrdinalIgnoreCase ), ShipCheckSeverity.Error,
			$"Document units are '{input.DeclaredUnits}'; Desktop documents are inches (Z-up)." ) );

		checks.Add( new ShipCheck( "document up axis",
			string.Equals( input.DeclaredUpAxis, "Z", StringComparison.OrdinalIgnoreCase ), ShipCheckSeverity.Error,
			$"Document up axis is '{input.DeclaredUpAxis}'; Desktop documents are Z-up." ) );

		if ( preset.Supported && string.IsNullOrWhiteSpace( preset.UnitsConversion ) )
		{
			checks.Add( new ShipCheck( "units conversion declared", false, ShipCheckSeverity.Error,
				$"Preset '{preset.Id}' declares no conversion from Desktop inches to engine units." ) );
		}
		else if ( preset.Supported )
		{
			checks.Add( new ShipCheck( "units conversion declared", true, ShipCheckSeverity.Error,
				$"Preset '{preset.Id}' converts {preset.UnitsConversion}." ) );
		}

		var expected = ExpectedAxes( preset.Target );
		if ( expected.HasValue )
		{
			bool axesOk = string.Equals( preset.OutputUpAxis, expected.Value.Up, StringComparison.OrdinalIgnoreCase ) &&
				string.Equals( preset.OutputForwardAxis, expected.Value.Forward, StringComparison.OrdinalIgnoreCase );
			checks.Add( new ShipCheck( "output axes",
				axesOk, ShipCheckSeverity.Error,
				axesOk
					? $"Preset outputs {preset.OutputUpAxis}-up / {preset.OutputForwardAxis}-forward, as {preset.Target} expects."
					: $"Preset outputs {preset.OutputUpAxis}-up / {preset.OutputForwardAxis}-forward, but {preset.Target} expects {expected.Value.Up}-up / {expected.Value.Forward}-forward." ) );
		}

		if ( !isPrint )
		{
			foreach ( var set in input.TextureSets )
			{
				bool sizeOk = set.Width > 0 && set.Height > 0 &&
					EngineExportPresets.IsPowerOfTwo( set.Width ) &&
					EngineExportPresets.IsPowerOfTwo( set.Height );
				checks.Add( new ShipCheck( $"texture resolution ({set.Name})",
					sizeOk, ShipCheckSeverity.Error,
					sizeOk
						? $"Texture set '{set.Name}' is {set.Width}x{set.Height}."
						: $"Texture set '{set.Name}' is {set.Width}x{set.Height}; sizes must be positive powers of two." ) );
				if ( preset.Supported && preset.ExportResolution > 0 )
				{
					bool withinCap = set.Width <= preset.ExportResolution && set.Height <= preset.ExportResolution;
					checks.Add( new ShipCheck( $"texture resolution cap ({set.Name})",
						withinCap, ShipCheckSeverity.Error,
						withinCap
							? $"Texture set '{set.Name}' is within the preset export cap of {preset.ExportResolution}."
							: $"Texture set '{set.Name}' is {set.Width}x{set.Height}, above the preset export cap of {preset.ExportResolution}." ) );
				}
			}

			foreach ( var required in preset.RequiredSourceMaps )
			{
				bool present = input.TextureSets.Any( s => s.PresentMaps.Contains( required ) );
				if ( required == SourceChannel.AmbientOcclusion )
				{
					checks.Add( new ShipCheck( "map completeness (ambient occlusion)",
						present, ShipCheckSeverity.Warning,
						present
							? "Ambient occlusion map is present."
							: "Ambient occlusion map is missing; packing defaults it to white (no occlusion)." ) );
				}
				else
				{
					checks.Add( new ShipCheck( $"map completeness ({required.ToString().ToLowerInvariant()})",
						present, ShipCheckSeverity.Error,
						present
							? $"Required map '{required}' is present."
							: $"Required map '{required}' is missing from every texture set." ) );
				}
			}
		}

		checks.Add( new ShipCheck( "mesh manifold",
			!input.HasNonManifoldEdges, meshSeverity,
			input.HasNonManifoldEdges
				? "Mesh has non-manifold edges (an edge shared by more than two faces)."
				: "No non-manifold edges." ) );
		checks.Add( new ShipCheck( "mesh holes",
			!input.HasHoles, meshSeverity,
			input.HasHoles
				? "Mesh has boundary edges (holes)."
				: "No boundary edges." ) );
		checks.Add( new ShipCheck( "mesh degenerate faces",
			!input.HasDegenerateFaces, meshSeverity,
			input.HasDegenerateFaces
				? "Mesh has degenerate faces (fewer than three corners, or a repeated vertex)."
				: "No degenerate faces." ) );
		checks.Add( new ShipCheck( "mesh zero-area faces",
			!input.HasZeroAreaFaces, meshSeverity,
			input.HasZeroAreaFaces
				? "Mesh has zero-area faces."
				: "No zero-area faces." ) );
		checks.Add( new ShipCheck( "uv range",
			input.UvOutOfRangeCount == 0, meshSeverity,
			input.UvOutOfRangeCount == 0
				? "All UV corners are inside [0, 1]."
				: $"{input.UvOutOfRangeCount} UV corners fall outside [0, 1]." ) );
		checks.Add( new ShipCheck( "uv degenerate triangles",
			input.UvDegenerateCount == 0, meshSeverity,
			input.UvDegenerateCount == 0
				? "No degenerate UV triangles."
				: $"{input.UvDegenerateCount} UV triangles have ~zero area." ) );

		if ( isPrint )
		{
			checks.Add( new ShipCheck( "watertight",
				input.IsWatertight, ShipCheckSeverity.Error,
				input.IsWatertight
					? "Mesh is watertight (closed, manifold)."
					: "Mesh is not watertight; a 3D print needs a closed, manifold mesh." ) );
		}

		bool passed = checks.All( c => c.Passed || c.Severity == ShipCheckSeverity.Warning );
		return new ShipCheckResult( passed, checks );
	}

	/// <summary>
	/// Derives the checklist mesh facts from a PartStudio. Document axes are
	/// Desktop native (Z-up, inches) by contract; the preset supplies the
	/// engine side. Texture sets come from the caller: this adapter cannot
	/// know which paint layers were exported.
	/// </summary>
	public static ShipChecklistInput FromStudio(
		PartStudio studio,
		EngineExportPreset preset,
		IReadOnlyList<TextureSetInfo> textureSets,
		long maxTriangles = 0 )
	{
		ArgumentNullException.ThrowIfNull( studio );
		ArgumentNullException.ThrowIfNull( preset );
		ArgumentNullException.ThrowIfNull( textureSets );

		int faceCount = 0;
		int triangleCount = 0;
		int vertexCount = 0;
		bool hasDegenerateFaces = false;
		bool hasZeroAreaFaces = false;
		int uvOutOfRangeCount = 0;
		int uvDegenerateCount = 0;
		var edgeFaces = new Dictionary<(VertexKey, VertexKey), int>();
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		bool anyPosition = false;

		foreach ( var body in studio.Bodies )
		{
			var mesh = body.Mesh;
			if ( mesh == null )
				continue;
			vertexCount += mesh.Positions.Count;
			foreach ( var position in mesh.Positions )
			{
				anyPosition = true;
				min = new Vec3( Math.Min( min.x, position.x ), Math.Min( min.y, position.y ), Math.Min( min.z, position.z ) );
				max = new Vec3( Math.Max( max.x, position.x ), Math.Max( max.y, position.y ), Math.Max( max.z, position.z ) );
			}
			foreach ( var face in mesh.Faces )
			{
				faceCount++;
				if ( face.Count < 3 || HasRepeatedIndex( face.Indices ) )
				{
					hasDegenerateFaces = true;
					continue;
				}
				triangleCount += face.Count - 2;
				double area = FanArea( mesh.Positions, face.Indices );
				if ( area < 1e-12 )
					hasZeroAreaFaces = true;

				for ( int i = 0; i < face.Count; i++ )
				{
					int a = face.Indices[i];
					int b = face.Indices[( i + 1 ) % face.Count];
					if ( a < 0 || a >= mesh.Positions.Count || b < 0 || b >= mesh.Positions.Count )
					{
						hasDegenerateFaces = true;
						continue;
					}
					var keyA = VertexKey.From( mesh.Positions[a] );
					var keyB = VertexKey.From( mesh.Positions[b] );
					var edge = keyA.CompareTo( keyB ) <= 0 ? (keyA, keyB) : (keyB, keyA);
					edgeFaces[edge] = edgeFaces.TryGetValue( edge, out int count ) ? count + 1 : 1;
				}

				if ( face.UVs != null )
				{
					for ( int i = 0; i < face.UVs.Length; i++ )
					{
						var uv = face.UVs[i];
						if ( uv.x < -1e-6f || uv.x > 1f + 1e-6f || uv.y < -1e-6f || uv.y > 1f + 1e-6f )
							uvOutOfRangeCount++;
					}
					for ( int t = 1; t + 1 < face.Count; t++ )
					{
						if ( UvTriangleArea( face.UVs[0], face.UVs[t], face.UVs[t + 1] ) < 1e-12 )
							uvDegenerateCount++;
					}
				}
			}
		}

		bool hasHoles = edgeFaces.Values.Any( count => count == 1 );
		bool hasNonManifold = edgeFaces.Values.Any( count => count > 2 );
		bool isWatertight = faceCount > 0 && !hasHoles && !hasNonManifold;
		if ( !anyPosition )
		{
			min = new Vec3( 0, 0, 0 );
			max = new Vec3( 0, 0, 0 );
		}

		return new ShipChecklistInput(
			faceCount, triangleCount, vertexCount,
			hasNonManifold, hasHoles, hasDegenerateFaces, hasZeroAreaFaces,
			uvOutOfRangeCount, uvDegenerateCount, isWatertight,
			min, max, "Z", "inches", textureSets, preset, maxTriangles );
	}

	static bool HasRepeatedIndex( int[] indices )
	{
		for ( int i = 0; i < indices.Length; i++ )
		{
			for ( int j = i + 1; j < indices.Length; j++ )
			{
				if ( indices[i] == indices[j] )
					return true;
			}
		}
		return false;
	}

	static double FanArea( List<Vec3> positions, int[] indices )
	{
		double area = 0;
		var a = positions[indices[0]];
		for ( int t = 1; t + 1 < indices.Length; t++ )
			area += TriangleArea( a, positions[indices[t]], positions[indices[t + 1]] );
		return area;
	}

	static double TriangleArea( Vec3 a, Vec3 b, Vec3 c )
	{
		double abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
		double acx = c.x - a.x, acy = c.y - a.y, acz = c.z - a.z;
		double cx = aby * acz - abz * acy;
		double cy = abz * acx - abx * acz;
		double cz = abx * acy - aby * acx;
		return 0.5 * Math.Sqrt( cx * cx + cy * cy + cz * cz );
	}

	static double UvTriangleArea( Vec2 a, Vec2 b, Vec2 c )
	{
		double abx = b.x - a.x, aby = b.y - a.y;
		double acx = c.x - a.x, acy = c.y - a.y;
		return 0.5 * Math.Abs( abx * acy - aby * acx );
	}

	static double LongestEdge( Vec3 min, Vec3 max ) =>
		Math.Max( Math.Max( max.x - min.x, max.y - min.y ), max.z - min.z );

	static bool IsFinite( Vec3 v ) =>
		float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z );

	/// <summary>Vertex position quantised to 1e-4 in, so duplicated seam vertices share an edge key.</summary>
	readonly record struct VertexKey( long X, long Y, long Z ) : IComparable<VertexKey>
	{
		public static VertexKey From( Vec3 position ) => new(
			(long)Math.Round( position.x * 10000 ),
			(long)Math.Round( position.y * 10000 ),
			(long)Math.Round( position.z * 10000 ) );

		public int CompareTo( VertexKey other )
		{
			int byX = X.CompareTo( other.X );
			if ( byX != 0 ) return byX;
			int byY = Y.CompareTo( other.Y );
			if ( byY != 0 ) return byY;
			return Z.CompareTo( other.Z );
		}
	}
}
