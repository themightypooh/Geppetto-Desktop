#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Map;

namespace Marionette.Desktop;

/// <summary>
/// Hammer Desktop AI: bot commands for maps + the AI Quick Play / walk /
/// fix loop (hammer-ai-bot-commands-map-playtest-loop).
///
/// The loop, in order, one scripted flow with a named result per step:
///
///   build       load the .vmf/.map + BSP-only QuickPlay bake
///               (the same calls `ops map build` makes)
///   validate    IssueEngine full check (the same call `ops map validate`
///               / `ops map issues` make)
///   play        spawn + walk validation from the bake
///   report      one aggregated report: issue codes, bake warnings,
///               walk block reason
///   fix         scripted fix for a leak (see below), or "no fix needed"
///   re-validate validate + bake again on the fixed document
///
/// DELEGATION, NOT A NEW SURFACE. This class calls the existing layers
/// and re-implements none of them:
///   - load + projections: OpsCli.LoadMap / ToIssueDocument /
///     ToQuickPlayMap (the ops-CLI map commands' own helpers)
///   - bake + walk: QuickPlay.Bake (hammer-quick-play-bsp-only-bake)
///   - validation: IssueEngine.Run over OpsCli.ToIssueDocument (the
///     same call OpsCli.MapIssues makes; re-validate uses MapIssues)
///   - persistence of a fixed map: VmfIo.WriteFile
/// Commands run through the existing bot-command surface, not a new one:
/// the AiSuiteTools `map_command` tool (map verbs, when BuildScript.Map.cs
/// lands), the BuildScript `map_*` verbs, and `ops map ...` are the
/// surfaces that call BotMapPlaytest.Run — none of them is edited here.
/// AiSuiteTools.cs is held by the capability-responses ticket and
/// OpsCli.cs by the ops-CLI map-commands ticket, so the wiring
/// (`map_playtest` tool / verb / `ops map playtest` subcommand) belongs
/// to those tickets and the final integration gate, not this one.
/// Script input (`ops map build &lt;script&gt;`) likewise needs the map
/// verbs to land first; this loop takes a map path, and its build step
/// is the `ops map build` bake over the loaded map.
///
/// LEAK FIX DESIGN. IssueAutoFix deliberately never auto-seals a leak
/// ("Go to gap — leaks are not auto-sealed"), so the loop does NOT route
/// a leak through it. The scripted fix adds ONE explicit sealing brush
/// to the VmfDocument — a real brush a human would add by hand — and the
/// re-validate step judges whether it sealed the map:
///   - primary: the bake's LeakPortal is the exact gap quad, so the seal
///     box is the portal AABB plus SealPortalMargin on every axis, with
///     the thin (portal-plane) axis widened to SealNormalHalf each side
///     of its centre so the patch straddles the full wall thickness.
///   - fallback: no portal (flood fill found no gap portal) but the
///     IssueEngine LEAK rule fired — LeakProbe's escape gives the leaking
///     direction. Its Exit point is NOT the gap: RayExitPoint expands the
///     solids bounds by 64 inches, so the exit sits 64 inches past the
///     wall, in the void; a plate centred on it would seal nothing. The
///     fallback therefore seals the whole leaking SIDE: a plate straddling
///     the document bounds' outer face on the escape axis, spanning the
///     full tangential bounds. Crude but deterministic — it seals any gap
///     on that side — and re-validate is the arbiter; a still-leaking map
///     returns Passed=false with code "leak".
/// Non-leak error issues have no scripted fix in this loop (IssueAutoFix
/// needs a MapDocument undo target and there is no VmfDocument -&gt;
/// MapDocument import yet); the report names them and the fix step
/// reports code "no-scripted-fix".
///
/// No UI, no game launch, no shelling out to Hammer / VBSP / VVIS /
/// VRAD. Native kernel APIs only. Geometry is the kernel's inches and
/// Z-up; brush faces are outward-wound (see BuildBoxBrush).
/// </summary>
public sealed class BotMapPlaytestOptions
{
	/// <summary>QuickPlay bake options (walk distance/step, player size).
	/// Null = QuickPlay defaults.</summary>
	public QuickPlayOptions? PlayOptions;
	/// <summary>Margin (inches) added on every axis of the leak-portal
	/// seal box.</summary>
	public float SealPortalMargin = 8f;
	/// <summary>Half-thickness (inches) of the seal box along the portal
	/// plane's normal: the patch extends this far either side of the
	/// portal plane so it straddles the wall it seals.</summary>
	public float SealNormalHalf = 32f;
	/// <summary>Material for the sealing brush.</summary>
	public string SealMaterial = "BRICK/BRICKWALL001A";
	/// <summary>When set and a fix was applied, the fixed map is written
	/// here with VmfIo.WriteFile. Null = do not write anything. Never
	/// defaults to the source path: the loop never overwrites its input.</summary>
	public string? FixedMapPath;
}

/// <summary>One named step of the playtest loop. <see cref="Ran"/> is
/// false only when an earlier step failed hard (build exception) and the
/// loop stopped; <see cref="Ok"/> is the step's own verdict.
/// <see cref="Code"/> is "" when Ok, else the named code a bot switches
/// on ("leak", "no-player-start", "no-scripted-fix", ...).</summary>
public sealed class BotMapStepResult
{
	public string Name = "";
	public bool Ran;
	public bool Ok;
	public string Code = "";
	public string Detail = "";
	public List<string> IssueCodes = new List<string>();
}

/// <summary>Result of <see cref="BotMapPlaytest.Run"/>: the six steps in
/// order, plus the loop-level verdict. <see cref="Passed"/> is true only
/// when every step ran, the fix step succeeded, and the re-validate step
/// is clean — a map that still leaks after the scripted fix returns
/// Passed=false. (Build / validate are diagnostic: they report the leak
/// on a leaking map, so they are not required Ok.)</summary>
public sealed class BotMapPlaytestResult
{
	public string MapPath = "";
	public List<BotMapStepResult> Steps = new List<BotMapStepResult>();
	public bool Passed;
	public bool NeedsFix;
	public bool FixApplied;
	public string FixName = "";
	/// <summary>Box of the sealing brush that was added, or null when no
	/// brush was added.</summary>
	public SealBox? SealBox;
	/// <summary>Path the fixed map was written to, or "" when nothing
	/// was written.</summary>
	public string FixedMapPath = "";

	public BotMapStepResult? Step( string name )
	{
		foreach ( var step in Steps )
			if ( string.Equals( step.Name, name, StringComparison.Ordinal ) )
				return step;
		return null;
	}
}

/// <summary>Axis-aligned seal box: the patch brush's bounds, inches,
/// Z-up.</summary>
public sealed class SealBox
{
	public Vec3 Min;
	public Vec3 Max;

	public SealBox( Vec3 min, Vec3 max )
	{
		Min = min;
		Max = max;
	}

	public override string ToString() =>
		"(" + Min.x + " " + Min.y + " " + Min.z + ")..(" +
		Max.x + " " + Max.y + " " + Max.z + ")";
}

/// <summary>The AI playtest loop for Hammer maps, headless. Pure over the
/// loaded document except for the optional fixed-map write.</summary>
public static class BotMapPlaytest
{
	public const string StepBuild = "build";
	public const string StepValidate = "validate";
	public const string StepPlay = "play";
	public const string StepReport = "report";
	public const string StepFix = "fix";
	public const string StepReValidate = "re-validate";

	public static readonly string[] StepNames =
		{ StepBuild, StepValidate, StepPlay, StepReport, StepFix, StepReValidate };

	/// <summary>Run the full loop on a .vmf / Valve 220 .map file. The
	/// load throws FileNotFoundException / ArgumentException for a
	/// missing or non-map path (same as OpsCli.LoadMap); a bake or
	/// projection failure is reported on the build step with its named
	/// code and the later steps are returned un-run.</summary>
	public static BotMapPlaytestResult Run( string mapPath, BotMapPlaytestOptions? options = null )
	{
		options ??= new BotMapPlaytestOptions();
		var result = new BotMapPlaytestResult { MapPath = mapPath ?? "" };
		foreach ( var name in StepNames )
			result.Steps.Add( new BotMapStepResult { Name = name } );
		BotMapStepResult At( string name ) => result.Step( name )!;

		VmfDocument doc;
		QuickPlayBakeResult bake;
		IReadOnlyList<Issue> issues;
		try
		{
			doc = OpsCli.LoadMap( mapPath );
			var quick = OpsCli.ToQuickPlayMap( doc );
			bake = QuickPlay.Bake( quick, options.PlayOptions );
		}
		catch ( QuickPlayException ex )
		{
			var step = At( StepBuild );
			step.Ran = true;
			step.Code = ex.Code ?? "quickplay-error";
			step.Detail = ex.Message;
			return result;
		}
		catch ( OpsCli.MapCommandException ex )
		{
			var step = At( StepBuild );
			step.Ran = true;
			step.Code = ex.Code ?? "map-command-error";
			step.Detail = ex.Message;
			return result;
		}

		var issueDoc = OpsCli.ToIssueDocument( doc );
		issues = new IssueEngine().Run( issueDoc );

		var build = At( StepBuild );
		build.Ran = true;
		build.Ok = bake.Sealed;
		build.Code = bake.Sealed ? "" : "leak";
		build.Detail = bake.Sealed
			? "bake sealed; spawn ok=" + bake.Spawn.Ok + "; walk blocked=" + bake.Walk.Blocked
			: "bake unsealed; leak portal " + ( bake.LeakPortal != null ? "present" : "absent" );
		if ( !bake.Sealed )
			build.IssueCodes.Add( "LEAK" );

		var validate = At( StepValidate );
		validate.Ran = true;
		validate.IssueCodes = DistinctCodes( issues );
		validate.Ok = !issues.Any( i => i != null && i.Severity == IssueSeverity.Error );
		validate.Code = validate.Ok ? "" : FirstErrorCode( issues );
		validate.Detail = issues.Count == 0
			? "no issues"
			: issues.Count + " issue(s): " + string.Join( ", ", validate.IssueCodes );

		var play = At( StepPlay );
		play.Ran = true;
		play.Ok = bake.Spawn.Ok && !bake.Walk.Blocked;
		play.Code = play.Ok ? "" : ( bake.Walk.Blocked
			? ( bake.Walk.BlockReason ?? "walk-blocked" )
			: ( bake.Spawn.Reason ?? "spawn" ) );
		play.Detail = "spawn ok=" + bake.Spawn.Ok +
			( bake.Spawn.Ok ? "" : " (" + bake.Spawn.Reason + ")" ) +
			"; walk blocked=" + bake.Walk.Blocked +
			( bake.Walk.Blocked
				? " (" + bake.Walk.BlockReason + ( bake.Walk.BlockedBy != null ? " by " + bake.Walk.BlockedBy : "" ) + ")"
				: " walked " + bake.Walk.DistanceWalked + " in " + bake.Walk.StepsTaken + " steps" );

		var report = At( StepReport );
		report.Ran = true;
		report.Ok = true;
		report.IssueCodes = DistinctCodes( issues );
		if ( !bake.Sealed && !report.IssueCodes.Contains( "LEAK" ) )
			report.IssueCodes.Add( "LEAK" );
		report.Detail = "sealed=" + bake.Sealed +
			"; issues=" + ( report.IssueCodes.Count == 0 ? "none" : string.Join( ",", report.IssueCodes ) ) +
			"; warnings=" + ( bake.Warnings.Count == 0 ? "none" : string.Join( ";", bake.Warnings ) ) +
			"; walk=" + ( bake.Walk.Blocked ? bake.Walk.BlockReason : "ok" );

		result.NeedsFix = !bake.Sealed ||
			issues.Any( i => i != null && i.Kind == IssueKind.Leak ) ||
			issues.Any( i => i != null && i.Severity == IssueSeverity.Error );

		var fix = At( StepFix );
		fix.Ran = true;
		if ( !result.NeedsFix )
		{
			fix.Ok = true;
			fix.Detail = "no fix needed: map sealed, no error issues";
		}
		else if ( issues.Any( i => i != null && i.Kind == IssueKind.Leak ) || !bake.Sealed )
		{
			var seal = SealBoxForBake( bake, issueDoc, options );
			if ( seal is null )
			{
				fix.Ok = false;
				fix.Code = "leak-no-location";
				fix.Detail = "leak reported but neither a bake portal nor a probe escape located it; no brush added";
			}
			else
			{
				var (brushId, faceId) = NextIds( doc );
				doc.WorldBrushes.Add( BuildBoxBrush( brushId, faceId, seal.Min, seal.Max, options.SealMaterial ) );
				result.FixApplied = true;
				result.SealBox = seal;
				result.FixName = bake.LeakPortal != null ? "seal-leak-portal" : "seal-leak-escape";
				fix.Ok = true;
				fix.Detail = result.FixName + ": added brush #" + brushId + " box " + seal;
				if ( options.FixedMapPath != null )
				{
					VmfIo.WriteFile( doc, options.FixedMapPath );
					result.FixedMapPath = options.FixedMapPath;
				}
			}
		}
		else
		{
			var unfixed = DistinctCodes( issues.Where( i => i != null && i.Severity == IssueSeverity.Error ).ToList() );
			fix.Ok = false;
			fix.Code = "no-scripted-fix";
			fix.Detail = "error issues with no scripted fix in this loop: " + string.Join( ",", unfixed );
			fix.IssueCodes = unfixed;
		}

		var revalidate = At( StepReValidate );
		revalidate.Ran = true;
		var reIssues = OpsCli.MapIssues( doc );
		QuickPlayBakeResult reBake;
		try
		{
			reBake = QuickPlay.Bake( OpsCli.ToQuickPlayMap( doc ), options.PlayOptions );
		}
		catch ( QuickPlayException ex )
		{
			revalidate.Ok = false;
			revalidate.Code = ex.Code ?? "quickplay-error";
			revalidate.Detail = "re-bake failed: " + ex.Message;
			revalidate.IssueCodes = DistinctCodes( reIssues );
			return result;
		}
		catch ( OpsCli.MapCommandException ex )
		{
			revalidate.Ok = false;
			revalidate.Code = ex.Code ?? "map-command-error";
			revalidate.Detail = "re-bake failed: " + ex.Message;
			revalidate.IssueCodes = DistinctCodes( reIssues );
			return result;
		}
		revalidate.IssueCodes = DistinctCodes( reIssues );
		if ( !reBake.Sealed && !revalidate.IssueCodes.Contains( "LEAK" ) )
			revalidate.IssueCodes.Add( "LEAK" );
		revalidate.Ok = !reIssues.Any( i => i != null && i.Severity == IssueSeverity.Error )
			&& reBake.Sealed && reBake.Spawn.Ok && !reBake.Walk.Blocked;
		revalidate.Code = revalidate.Ok ? ""
			: reIssues.Any( i => i != null && i.Severity == IssueSeverity.Error ) ? FirstErrorCode( reIssues )
			: !reBake.Sealed ? "leak"
			: !reBake.Spawn.Ok ? ( reBake.Spawn.Reason ?? "spawn" )
			: ( reBake.Walk.BlockReason ?? "walk-blocked" );
		revalidate.Detail = revalidate.Ok
			? "clean: no error issues, sealed, spawn ok, walk unblocked"
			: "issues=" + ( revalidate.IssueCodes.Count == 0 ? "none" : string.Join( ",", revalidate.IssueCodes ) ) +
				"; sealed=" + reBake.Sealed +
				"; spawn ok=" + reBake.Spawn.Ok +
				"; walk blocked=" + reBake.Walk.Blocked;

		// Build / validate / play / report are diagnostic: on a leaking map
		// they correctly report the leak, so they cannot be required Ok.
		// The loop passes when every step ran, the fix step succeeded, and
		// re-validate is clean.
		result.Passed = result.Steps.All( s => s.Ran ) && At( StepFix ).Ok && At( StepReValidate ).Ok;
		return result;
	}

	static List<string> DistinctCodes( IReadOnlyList<Issue> issues )
	{
		var codes = new List<string>();
		if ( issues is null )
			return codes;
		foreach ( var issue in issues )
		{
			if ( issue is null || string.IsNullOrEmpty( issue.Code ) )
				continue;
			if ( !codes.Contains( issue.Code ) )
				codes.Add( issue.Code );
		}
		return codes;
	}

	static string FirstErrorCode( IReadOnlyList<Issue> issues )
	{
		foreach ( var issue in issues )
			if ( issue != null && issue.Severity == IssueSeverity.Error && !string.IsNullOrEmpty( issue.Code ) )
				return issue.Code;
		return "error";
	}

	/// <summary>Seal box for a leaking bake: the bake's leak portal when
	/// present, else the first probe escape from the issue document
	/// (gapped escapes first). Null when neither locates the leak.</summary>
	public static SealBox? SealBoxForBake( QuickPlayBakeResult bake, IssueDocument issueDoc, BotMapPlaytestOptions? options = null )
	{
		options ??= new BotMapPlaytestOptions();
		if ( bake != null && bake.LeakPortal != null )
			return SealBoxForPortal( bake.LeakPortal, options.SealPortalMargin, options.SealNormalHalf );
		if ( issueDoc is null )
			return null;
		IReadOnlyList<LeakEscape> escapes = LeakProbe.Probe( issueDoc );
		LeakEscape? pick = null;
		foreach ( var escape in escapes )
			if ( escape != null && escape.Gapped )
			{
				pick = escape;
				break;
			}
		if ( pick is null && escapes.Count > 0 )
			pick = escapes[0];
		if ( pick is null )
			return null;
		if ( !DocumentBounds( issueDoc, out var boundsMin, out var boundsMax ) )
			return null;
		return SealBoxForEscape( pick, boundsMin, boundsMax, options.SealNormalHalf, options.SealPortalMargin );
	}

	/// <summary>Seal box from a QuickPlay leak portal: the portal AABB
	/// plus margin on every axis, with the thin (portal-plane) axis
	/// widened to normalHalf each side of its centre.</summary>
	public static SealBox SealBoxForPortal( QuickPlayPortal portal, float margin, float normalHalf )
	{
		ArgumentNullException.ThrowIfNull( portal );
		// Vec3 is a readonly struct: build the box from locals, never mutate.
		float minX = portal.Min.x - margin, minY = portal.Min.y - margin, minZ = portal.Min.z - margin;
		float maxX = portal.Max.x + margin, maxY = portal.Max.y + margin, maxZ = portal.Max.z + margin;
		float spanX = maxX - minX, spanY = maxY - minY, spanZ = maxZ - minZ;
		if ( spanX <= spanY && spanX <= spanZ )
		{
			float centre = ( minX + maxX ) * 0.5f;
			minX = centre - normalHalf;
			maxX = centre + normalHalf;
		}
		else if ( spanY <= spanX && spanY <= spanZ )
		{
			float centre = ( minY + maxY ) * 0.5f;
			minY = centre - normalHalf;
			maxY = centre + normalHalf;
		}
		else
		{
			float centre = ( minZ + maxZ ) * 0.5f;
			minZ = centre - normalHalf;
			maxZ = centre + normalHalf;
		}
		return new SealBox( new Vec3( minX, minY, minZ ), new Vec3( maxX, maxY, maxZ ) );
	}

	/// <summary>Fallback seal box from a LeakProbe escape: a plate sealing
	/// the whole leaking side. The escape's direction picks the side; the
	/// plate straddles the document bounds' outer face on that axis
	/// (normalHalf either side) and spans the full tangential bounds
	/// (plus margin). See the file header for why the exit point itself
	/// is not the plate centre.</summary>
	public static SealBox SealBoxForEscape( LeakEscape escape, Vec3 boundsMin, Vec3 boundsMax, float normalHalf, float margin )
	{
		ArgumentNullException.ThrowIfNull( escape );
		var direction = escape.Direction;
		float ax = Math.Abs( direction.x ), ay = Math.Abs( direction.y ), az = Math.Abs( direction.z );
		if ( ax >= ay && ax >= az )
		{
			if ( direction.x > 0 )
				return new SealBox(
					new Vec3( boundsMax.x - normalHalf, boundsMin.y - margin, boundsMin.z - margin ),
					new Vec3( boundsMax.x + normalHalf, boundsMax.y + margin, boundsMax.z + margin ) );
			return new SealBox(
				new Vec3( boundsMin.x - normalHalf, boundsMin.y - margin, boundsMin.z - margin ),
				new Vec3( boundsMin.x + normalHalf, boundsMax.y + margin, boundsMax.z + margin ) );
		}
		if ( ay >= ax && ay >= az )
		{
			if ( direction.y > 0 )
				return new SealBox(
					new Vec3( boundsMin.x - margin, boundsMax.y - normalHalf, boundsMin.z - margin ),
					new Vec3( boundsMax.x + margin, boundsMax.y + normalHalf, boundsMax.z + margin ) );
			return new SealBox(
				new Vec3( boundsMin.x - margin, boundsMin.y - normalHalf, boundsMin.z - margin ),
				new Vec3( boundsMax.x + margin, boundsMin.y + normalHalf, boundsMax.z + margin ) );
		}
		if ( direction.z > 0 )
			return new SealBox(
				new Vec3( boundsMin.x - margin, boundsMin.y - margin, boundsMax.z - normalHalf ),
				new Vec3( boundsMax.x + margin, boundsMax.y + margin, boundsMax.z + normalHalf ) );
		return new SealBox(
			new Vec3( boundsMin.x - margin, boundsMin.y - margin, boundsMin.z - normalHalf ),
			new Vec3( boundsMax.x + margin, boundsMax.y + margin, boundsMin.z + normalHalf ) );
	}

	/// <summary>Axis-aligned bounds of an IssueDocument from its brushes'
	/// solved face vertices. False when no brush has any vertex.</summary>
	public static bool DocumentBounds( IssueDocument document, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		var found = false;
		if ( document?.Brushes != null )
			foreach ( var brush in document.Brushes )
			{
				if ( brush?.Faces is null )
					continue;
				foreach ( var face in brush.Faces )
				{
					if ( face?.Vertices is null )
						continue;
					foreach ( var v in face.Vertices )
					{
						min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
						max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
						found = true;
					}
				}
			}
		if ( !found )
		{
			min = Vec3.Zero;
			max = Vec3.Zero;
		}
		return found;
	}

	/// <summary>Next free brush id and face id in a VmfDocument (max
	/// existing + 1 across world and entity brushes).</summary>
	public static (int BrushId, int FaceId) NextIds( VmfDocument doc )
	{
		ArgumentNullException.ThrowIfNull( doc );
		int brushId = 0, faceId = 0;
		void Scan( VmfBrush brush )
		{
			if ( brush is null )
				return;
			if ( brush.Id > brushId )
				brushId = brush.Id;
			if ( brush.Faces != null )
				foreach ( var face in brush.Faces )
					if ( face != null && face.Id > faceId )
						faceId = face.Id;
		}
		foreach ( var brush in doc.WorldBrushes )
			Scan( brush );
		foreach ( var entity in doc.Entities )
			if ( entity != null && entity.Brushes != null )
				foreach ( var brush in entity.Brushes )
					Scan( brush );
		return (brushId + 1, faceId + 1);
	}

	/// <summary>Axis-aligned box brush from min/max, outward-wound (the
	/// same plane-point pattern as the sealed-room fixture): +X/-X faces
	/// carry uaxis [0 1 0 0] / vaxis [0 0 -1 0], +Y/-Y faces [1 0 0 0] /
	/// [0 0 -1 0], +Z/-Z faces [1 0 0 0] / [0 -1 0 0]. Plane distances
	/// are the box bounds, so OpsCli.ToQuickPlayMap projects it back to
	/// exactly this min/max.</summary>
	public static VmfBrush BuildBoxBrush( int brushId, int firstFaceId, Vec3 min, Vec3 max, string material )
	{
		if ( !( min.x < max.x && min.y < max.y && min.z < max.z ) )
			throw new ArgumentException( "seal box needs Min < Max on every axis." );
		double x0 = min.x, y0 = min.y, z0 = min.z, x1 = max.x, y1 = max.y, z1 = max.z;
		var brush = new VmfBrush { Id = brushId };
		int id = firstFaceId;
		VmfFace Face( VmfVec a, VmfVec b, VmfVec c, VmfVec u, VmfVec v ) => new VmfFace
		{
			Id = id++,
			PlanePoints = new[] { a, b, c },
			Material = material ?? "",
			UAxis = new VmfUvAxis( u, 0, 0.25 ),
			VAxis = new VmfUvAxis( v, 0, 0.25 ),
			Rotation = 0,
			LightmapScale = 16,
			SmoothingGroups = 0,
		};
		var uX = new VmfVec( 0, 1, 0 );
		var vX = new VmfVec( 0, 0, -1 );
		var uY = new VmfVec( 1, 0, 0 );
		var vY = new VmfVec( 0, 0, -1 );
		var uZ = new VmfVec( 1, 0, 0 );
		var vZ = new VmfVec( 0, -1, 0 );
		// +X
		brush.Faces.Add( Face( new VmfVec( x1, y0, z0 ), new VmfVec( x1, y1, z0 ), new VmfVec( x1, y1, z1 ), uX, vX ) );
		// -X
		brush.Faces.Add( Face( new VmfVec( x0, y1, z0 ), new VmfVec( x0, y0, z0 ), new VmfVec( x0, y0, z1 ), uX, vX ) );
		// +Y
		brush.Faces.Add( Face( new VmfVec( x1, y1, z0 ), new VmfVec( x0, y1, z0 ), new VmfVec( x0, y1, z1 ), uY, vY ) );
		// -Y
		brush.Faces.Add( Face( new VmfVec( x0, y0, z0 ), new VmfVec( x1, y0, z0 ), new VmfVec( x1, y0, z1 ), uY, vY ) );
		// +Z
		brush.Faces.Add( Face( new VmfVec( x0, y0, z1 ), new VmfVec( x1, y0, z1 ), new VmfVec( x1, y1, z1 ), uZ, vZ ) );
		// -Z
		brush.Faces.Add( Face( new VmfVec( x1, y0, z0 ), new VmfVec( x0, y0, z0 ), new VmfVec( x0, y1, z0 ), uZ, vZ ) );
		return brush;
	}
}
