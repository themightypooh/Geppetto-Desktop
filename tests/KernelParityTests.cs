#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Effigy;

/// <summary>
/// In-suite form of the kernel parity gate (see
/// handoffs/kernel-parity-contract.md). Desktop-local: it re-hashes the
/// Desktop kernel/core trees against the committed baseline
/// (handoffs/kernel-parity-inventory.json), pins the Desktop-only file list
/// and the desktop-only API list, and asserts MeshBoolean.ApplyParallel
/// still exists with its parallel signature (it was the verified
/// Desktop-only API until the Mobile parity port synced MeshBoolean.cs
/// byte-identical; it is shared now). It never touches the Mobile tree
/// (which may not exist on a test machine).
///
/// Registered in tests/TestRegistry.cs by final-integrate 2026-11-13.
/// </summary>
internal static class KernelParityTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "kernel-parity", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		Check( "inventory baseline exists and parses", () =>
		{
			JsonElement root = LoadBaseline();
			Require( root.TryGetProperty( "trees", out _ ), "baseline has no trees" );
			Require( root.TryGetProperty( "kernel_comparison", out _ ), "baseline has no kernel_comparison" );
			Require( root.TryGetProperty( "preservation", out _ ), "baseline has no preservation" );
			Require( root.TryGetProperty( "counts", out _ ), "baseline has no counts" );
		} );

		Check( "desktop kernel hashes match baseline", () =>
		{
			JsonElement root = LoadBaseline();
			string repoRoot = FindRepoRoot();
			var expected = BaselineFiles( root, "desktop-kernel" );
			var actual = HashTree( Path.Combine( repoRoot, "kernel", "Effigy" ) );
			Require( expected.Count == actual.Count,
				$"desktop-kernel file count changed: baseline {expected.Count}, disk {actual.Count} (regenerate the baseline with tools/kernel-parity/inventory.py)" );
			foreach ( var pair in expected )
			{
				Require( actual.TryGetValue( pair.Key, out string? hash ),
					$"desktop-kernel file missing on disk: {pair.Key}" );
				Require( hash == pair.Value,
					$"desktop-kernel file changed without a baseline update: {pair.Key}" );
			}
			foreach ( string path in actual.Keys )
				Require( expected.ContainsKey( path ),
					$"desktop-kernel file not in baseline: {path} (regenerate the baseline)" );
		} );

		Check( "desktop core hashes match baseline", () =>
		{
			JsonElement root = LoadBaseline();
			string repoRoot = FindRepoRoot();
			var expected = BaselineFiles( root, "desktop-core" );
			var actual = HashTree( Path.Combine( repoRoot, "core" ) );
			Require( expected.Count == actual.Count,
				$"desktop-core file count changed: baseline {expected.Count}, disk {actual.Count} (regenerate the baseline)" );
			foreach ( var pair in expected )
			{
				Require( actual.TryGetValue( pair.Key, out string? hash ),
					$"desktop-core file missing on disk: {pair.Key}" );
				Require( hash == pair.Value,
					$"desktop-core file changed without a baseline update: {pair.Key}" );
			}
		} );

		Check( "MeshBoolean.ApplyParallel exists with its parallel signature", () =>
		{
			MethodInfo? method = typeof( MeshBoolean ).GetMethod(
				"ApplyParallel", BindingFlags.Public | BindingFlags.Static );
			Require( method is not null, "MeshBoolean.ApplyParallel is missing (parallel boolean API, shared with Mobile since the parity port; see kernel-parity-contract.md §4)" );
			Require( method!.ReturnType.Name == "PolyMesh",
				$"ApplyParallel must return PolyMesh, returns {method.ReturnType.Name}" );
			ParameterInfo[] parameters = method.GetParameters();
			Require( parameters.Length == 4,
				$"ApplyParallel must take 4 parameters, takes {parameters.Length}" );
			Require( parameters[0].ParameterType.Name == "BooleanOp",
				$"ApplyParallel parameter 0 must be BooleanOp, is {parameters[0].ParameterType.Name}" );
			Require( parameters[1].ParameterType.Name == "PolyMesh",
				$"ApplyParallel parameter 1 must be PolyMesh, is {parameters[1].ParameterType.Name}" );
			Require( parameters[2].ParameterType.Name == "PolyMesh",
				$"ApplyParallel parameter 2 must be PolyMesh, is {parameters[2].ParameterType.Name}" );
			Require( parameters[3].ParameterType.Name == "Int32",
				$"ApplyParallel parameter 3 must be int, is {parameters[3].ParameterType.Name}" );
			Require( parameters[3].HasDefaultValue && Equals( parameters[3].DefaultValue, 4 ),
				"ApplyParallel chunks parameter must default to 4" );
		} );

		Check( "baseline desktop-only list is the known 91 files", () =>
		{
			// Reconciled 2026-11-13 by final-integrate (wave 2): the Mobile
			// parity port synced the other 72 files, so the regenerated baseline
			// records only 5 Desktop-only kernel files, all export/OCCT with no
			// Mobile counterpart (Alembic/MaterialX/USD export + the OCCT native
			// shim). The check pins that list so a new Desktop-only file cannot
			// appear silently; adding one means regenerating the baseline and
			// updating this list through a kernel ticket.
			JsonElement root = LoadBaseline();
			var expected = new HashSet<string>( StringComparer.Ordinal )
			{
				"Export/AlembicCache.cs",
				"Export/MaterialX.cs",
				"Export/UsdAscii.cs",
				"Export/UsdStage.cs",
				"Map/AreaportalOccluder.cs",
				"Map/AssetHotload.cs",
				"Map/AssetRegistries.cs",
				"Map/BrushToModel.cs",
				"Map/BspBuilder.cs",
				"Map/CadToMap.cs",
				"Map/ClipPolygon.cs",
				"Map/CompileCordon.cs",
				"Map/CompileRunner.cs",
				"Map/CubemapBake.cs",
				"Map/DirectEdit3D.cs",
				"Map/Displacement.cs",
				"Map/DisplacementBacking.cs",
				"Map/DisplacementSculpt.cs",
				"Map/EntityIo.cs",
				"Map/EntityIoGraph.cs",
				"Map/EntityLinks.cs",
				"Map/EntityRegistry.cs",
				"Map/EntityReport.cs",
				"Map/FaceMaterial.cs",
				"Map/FuncDetail.cs",
				"Map/GizmoModel.cs",
				"Map/GreyboxDefaults.cs",
				"Map/HintSkip.cs",
				"Map/HotspotTexturing.cs",
				"Map/InstancesCollab.cs",
				"Map/IssueAutoFix.cs",
				"Map/IssueEngine.cs",
				"Map/KeybindRegistry.cs",
				"Map/LeakFinder.cs",
				"Map/LightEnvironment.cs",
				"Map/LightmapBake.cs",
				"Map/LightmapBakeQuality.cs",
				"Map/LinkedGroups.cs",
				"Map/LiveLighting.cs",
				"Map/Map220Io.cs",
				"Map/MapBakeExport.cs",
				"Map/MapBrush.cs",
				"Map/MapBrushPrimitives.cs",
				"Map/MapCheck.cs",
				"Map/MapClip.cs",
				"Map/MapCsg.cs",
				"Map/MapDocument.cs",
				"Map/MapDocumentIo.cs",
				"Map/MapDocumentOverlays.cs",
				"Map/MapDocumentSkybox.cs",
				"Map/MapEntities.cs",
				"Map/MapExportRoundtrip.cs",
				"Map/MapInstances.cs",
				"Map/MapLayerHierarchy.cs",
				"Map/MapLayers.cs",
				"Map/MapPacker.cs",
				"Map/MapPaste.cs",
				"Map/MapTransform.cs",
				"Map/MapUndo.cs",
				"Map/MapVertexEdit.cs",
				"Map/MaterialBridge.cs",
				"Map/MeshMode.cs",
				"Map/NavMeshGen.cs",
				"Map/OverlaysDecals.cs",
				"Map/PaintOnMap.cs",
				"Map/PanelLayout.cs",
				"Map/ParallelStageRunner.cs",
				"Map/PlaytestLoop.cs",
				"Map/PortalGraph.cs",
				"Map/PreCompileGate.cs",
				"Map/PreviewEffects.cs",
				"Map/PropFade.cs",
				"Map/PropPlacement.cs",
				"Map/QuickPlay.cs",
				"Map/RadiosityBake.cs",
				"Map/RoomHelpers.cs",
				"Map/SceneMapBridge.cs",
				"Map/SharedMaterialLibrary.cs",
				"Map/SkyboxPreview.cs",
				"Map/SkyboxWorldspawn.cs",
				"Map/Soundscapes.cs",
				"Map/StarterTemplates.cs",
				"Map/ToolTexture.cs",
				"Map/TriggersVolumes.cs",
				"Map/VisleafDebug.cs",
				"Map/VmfIo.cs",
				"Map/WaterLadders.cs",
				"OcctNative.cs",
				"Script/ApiCatalogMapPass2.cs",
				"Script/BuildScript.Map.cs",
				"Script/BuildScript.MapPass2.cs",
			};
			var actual = new HashSet<string>( StringComparer.Ordinal );
			foreach ( JsonElement item in root
				.GetProperty( "kernel_comparison" ).GetProperty( "desktop_only" ).EnumerateArray() )
				actual.Add( item.GetString()! );
			Require( expected.SetEquals( actual ),
				$"desktop-only list changed (added: {string.Join( ", ", actual.Except( expected ) )}; removed: {string.Join( ", ", expected.Except( actual ) )}). A new Desktop-only file must go through a kernel ticket and a baseline regeneration." );
		} );

		Check( "baseline mobile-only list is empty", () =>
		{
			// Reconciled 2026-11-13 by final-integrate: the six formerly
			// Mobile-only files (AssemblySolver, SheetMetalFeature, KernelLog,
			// ManualUVIslandEdit, TopologySymmetry, UVUnwrap.XAtlas) were all
			// ported to Desktop, so mobile-only is now empty. Any file
			// reappearing here means a port gap reopened.
			JsonElement root = LoadBaseline();
			var actual = new HashSet<string>( StringComparer.Ordinal );
			foreach ( JsonElement item in root
				.GetProperty( "kernel_comparison" ).GetProperty( "mobile_only" ).EnumerateArray() )
				actual.Add( item.GetString()! );
			Require( actual.Count == 0,
				$"mobile-only list is no longer empty (added: {string.Join( ", ", actual )}). A Mobile-only file means a port gap; regenerate the baseline and update this check through a kernel ticket." );
		} );

		Check( "preservation list is the known 90 desktop-only APIs", () =>
		{
			// Reconciled 2026-11-13 by final-integrate (wave 2): the Mobile
			// parity port synced MeshBoolean.cs byte-identical, so ApplyParallel
			// is shared now and no longer in the preservation list. The list
			// now pins the current audited Desktop-only APIs in differing files (a blind
			// sync either way would lose them). The check pins that exact set;
			// a change means regenerating the baseline and updating this list
			// through a kernel ticket.
			JsonElement root = LoadBaseline();
			var expected = new HashSet<string>( StringComparer.Ordinal )
			{
				"CharacterDna.cs|member float Bust",
				"CharacterDna.cs|member float GenitalSize",
				"FeatureRegistry.cs|member bool Register( Type type )",
				"Features/CharacterFeature.cs|member FloatParam Bust",
				"Features/CharacterFeature.cs|member FloatParam GenitalSize",
				"Features/RigFeatures.cs|member ClipLibrary Clips",
				"Features/RigFeatures.cs|member IReadOnlyList<PropClipBinding> All",
				"Features/RigFeatures.cs|member IReadOnlyList<PropClipBinding> ForEntity( int entityId )",
				"Features/RigFeatures.cs|member IReadOnlyList<PropClipExportRow> Manifest( MapEntities entities, PropClipBindings bindings, ClipLibrary clips, Skeleton skeleton )",
				"Features/RigFeatures.cs|member IReadOnlyList<PropClipIssue> Validate( MapEntities entities, PropClipBindings bindings, ClipLibrary clips, Skeleton skeleton )",
				"Features/RigFeatures.cs|member IReadOnlyList<PropClipIssue> ValidateBinding( PropClipBinding binding, ClipLibrary clips, Skeleton skeleton, EntityRegistry registry)",
				"Features/RigFeatures.cs|member IReadOnlyList<string> BindableInputs( string className )",
				"Features/RigFeatures.cs|member MapEntities Entities",
				"Features/RigFeatures.cs|member PropClipBinding BindDefault( MapEntity entity, ClipLibrary clips, bool loop, string rootMotion)",
				"Features/RigFeatures.cs|member PropClipBinding BindInput( MapEntity entity, string input, string clipName, ClipLibrary clips, bool loop, string rootMotion)",
				"Features/RigFeatures.cs|member PropClipBinding Find( int entityId, string input )",
				"Features/RigFeatures.cs|member PropClipBindings Bindings",
				"Features/RigFeatures.cs|member PropClipException( string code, string message )",
				"Features/RigFeatures.cs|member PropClipIssue( PropClipIssueSeverity severity, string code, string message, int entityId, string key)",
				"Features/RigFeatures.cs|member PropClipIssue()",
				"Features/RigFeatures.cs|member PropClipIssueSeverity Severity",
				"Features/RigFeatures.cs|member PropClipPlayback Fire( EntityIoInput input )",
				"Features/RigFeatures.cs|member PropClipPlayback Fire( int entityId, string inputName, string parameter)",
				"Features/RigFeatures.cs|member PropClipPlayback State( int entityId )",
				"Features/RigFeatures.cs|member PropClipPlayer( MapEntities entities, ClipLibrary clips, PropClipBindings bindings, Skeleton skeleton )",
				"Features/RigFeatures.cs|member RigAnimFile Clip",
				"Features/RigFeatures.cs|member RigAnimFile ResolveClip( ClipLibrary clips, string clipName )",
				"Features/RigFeatures.cs|member bool DoorOpen",
				"Features/RigFeatures.cs|member bool Finished",
				"Features/RigFeatures.cs|member bool HasRootMotion",
				"Features/RigFeatures.cs|member bool IsAnimatableClass( string className )",
				"Features/RigFeatures.cs|member bool Loop",
				"Features/RigFeatures.cs|member bool Playing",
				"Features/RigFeatures.cs|member bool Remove( int entityId, string input )",
				"Features/RigFeatures.cs|member bool SeamPass",
				"Features/RigFeatures.cs|member const string CodeAmbiguousTarget",
				"Features/RigFeatures.cs|member const string CodeInvalidRootMotion",
				"Features/RigFeatures.cs|member const string CodeLoopSeamPops",
				"Features/RigFeatures.cs|member const string CodeMissingModel",
				"Features/RigFeatures.cs|member const string CodeNoBinding",
				"Features/RigFeatures.cs|member const string CodeNotAnimatableProp",
				"Features/RigFeatures.cs|member const string CodeUnknownClip",
				"Features/RigFeatures.cs|member const string CodeUnknownInput",
				"Features/RigFeatures.cs|member const string CodeUnknownTarget",
				"Features/RigFeatures.cs|member const string CodeUnplayableClip",
				"Features/RigFeatures.cs|member const string CodeUnsupportedInput",
				"Features/RigFeatures.cs|member const string RootMotionAsAuthored",
				"Features/RigFeatures.cs|member const string RootMotionExtract",
				"Features/RigFeatures.cs|member const string RootMotionInPlace",
				"Features/RigFeatures.cs|member const string RootMotionStrip",
				"Features/RigFeatures.cs|member float TimeSeconds",
				"Features/RigFeatures.cs|member int Count",
				"Features/RigFeatures.cs|member int EntityId",
				"Features/RigFeatures.cs|member int Frame",
				"Features/RigFeatures.cs|member int FrameRate",
				"Features/RigFeatures.cs|member int Frames",
				"Features/RigFeatures.cs|member int LastFrame",
				"Features/RigFeatures.cs|member string ClassName",
				"Features/RigFeatures.cs|member string ClipName",
				"Features/RigFeatures.cs|member string Code",
				"Features/RigFeatures.cs|member string Input",
				"Features/RigFeatures.cs|member string Key",
				"Features/RigFeatures.cs|member string LastFiredOutput",
				"Features/RigFeatures.cs|member string Message",
				"Features/RigFeatures.cs|member string Model",
				"Features/RigFeatures.cs|member string RootBone",
				"Features/RigFeatures.cs|member string RootMotion",
				"Features/RigFeatures.cs|member string SeamSummary",
				"Features/RigFeatures.cs|member string TargetName",
				"Features/RigFeatures.cs|member string ToString()",
				"Features/RigFeatures.cs|member void Advance( int entityId, int frames )",
				"Features/RigFeatures.cs|member void Stop( int entityId )",
				"Features/RigFeatures.cs|type class PropClipBinding",
				"Features/RigFeatures.cs|type class PropClipBindings",
				"Features/RigFeatures.cs|type class PropClipException",
				"Features/RigFeatures.cs|type class PropClipExport",
				"Features/RigFeatures.cs|type class PropClipExportRow",
				"Features/RigFeatures.cs|type class PropClipIssue",
				"Features/RigFeatures.cs|type class PropClipPlayback",
				"Features/RigFeatures.cs|type class PropClipPlayer",
				"Features/RigFeatures.cs|type class PropClipValidator",
				"Features/RigFeatures.cs|type enum PropClipIssueSeverity",
				"ModifierStack.cs|member ApplyResult Apply( PolyMesh mesh, IReadOnlyList<SculptLayer> sculptLayers)",
				"ModifierStack.cs|member ApplyResult( PolyMesh mesh, IReadOnlyList<string> warnings, int droppedMorphs )",
				"ModifierStack.cs|type class ApplyResult",
				"Script/ApiCatalog.cs|member IReadOnlyDictionary<string, string> MapVerbBacking()",
				"Script/ApiCatalog.cs|member IReadOnlyList<string> MapVerbNames()",
				"Script/ApiCatalog.cs|member bool BackingTypeExists( string fullName )",
				"Sketch/ConstraintTools.cs|member ConstraintApplyResult ApplyAndSolve( Sketch sketch, ConstraintOffer offer, int pinnedPoint)",
				"Sketch/ConstraintTools.cs|type class ConstraintApplyResult",
			};
			var actual = new HashSet<string>( StringComparer.Ordinal );
			foreach ( JsonElement entry in root
				.GetProperty( "preservation" ).GetProperty( "desktop_only_api" ).EnumerateArray() )
				actual.Add( entry.GetProperty( "file" ).GetString() + "|" + entry.GetProperty( "api" ).GetString() );
			Require( expected.SetEquals( actual ),
				$"preservation list changed (added: {string.Join( ", ", actual.Except( expected ) )}; removed: {string.Join( ", ", expected.Except( actual ) )}). Regenerate the baseline and update this check through a kernel ticket." );
		} );
	}

	static string FindRepoRoot()
	{
		string? overrideRoot = Environment.GetEnvironmentVariable( "KERNEL_PARITY_REPO_ROOT" );
		if ( !string.IsNullOrEmpty( overrideRoot ) )
			return overrideRoot;
		string? dir = AppContext.BaseDirectory;
		while ( dir is not null )
		{
			if ( File.Exists( Path.Combine( dir, "handoffs", "kernel-parity-inventory.json" ) ) )
				return dir;
			dir = Path.GetDirectoryName( dir );
		}
		throw new Exception( "cannot locate repo root: handoffs/kernel-parity-inventory.json not found above " + AppContext.BaseDirectory );
	}

	static JsonElement LoadBaseline()
	{
		string? overridePath = Environment.GetEnvironmentVariable( "KERNEL_PARITY_INVENTORY" );
		string path = overridePath ?? Path.Combine( FindRepoRoot(), "handoffs", "kernel-parity-inventory.json" );
		if ( !File.Exists( path ) )
			throw new Exception( "kernel parity baseline missing: " + path );
		using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( path ) );
		return doc.RootElement.Clone();
	}

	static Dictionary<string, string> BaselineFiles( JsonElement root, string tree )
	{
		var files = new Dictionary<string, string>( StringComparer.Ordinal );
		foreach ( JsonElement item in root.GetProperty( "trees" ).GetProperty( tree ).EnumerateArray() )
			files[item.GetProperty( "path" ).GetString()!] = item.GetProperty( "sha256" ).GetString()!;
		return files;
	}

	static Dictionary<string, string> HashTree( string root )
	{
		var files = new Dictionary<string, string>( StringComparer.Ordinal );
		foreach ( string full in Directory.EnumerateFiles( root, "*.cs", SearchOption.AllDirectories ) )
		{
			string rel = Path.GetRelativePath( root, full ).Replace( Path.DirectorySeparatorChar, '/' );
			if ( rel.Contains( ".bak", StringComparison.Ordinal ) )
				continue;
			string[] segments = rel.Split( '/' );
			if ( segments.Contains( "bin" ) || segments.Contains( "obj" ) || segments.Contains( ".git" ) )
				continue;
			files[rel] = Sha256( full );
		}
		return files;
	}

	static string Sha256( string path )
	{
		using SHA256 hash = SHA256.Create();
		using FileStream stream = File.OpenRead( path );
		return Convert.ToHexString( hash.ComputeHash( stream ) ).ToLowerInvariant();
	}
}
