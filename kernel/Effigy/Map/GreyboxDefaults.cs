using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P2: greybox workflow defaults (hammer-greybox-workflow-defaults).
///
/// Greybox = the 20-minute blockout: brushes first, textures later. This file is the
/// defaults + guidance DATA for that phase — nothing here is forced or auto-applied:
///   - <see cref="GreyboxDefaults.DevTextureMode"/>: a map-level flag; when on, the
///     dev material set (<see cref="DevMaterialSet"/>) is the source for visible
///     greybox faces.
///   - <see cref="GreyboxDefaults.NodrawFirst"/>: new brushes default to nodraw on
///     ALL faces; visible faces get materials later via the face API
///     (<see cref="FaceMaterial.SetMaterial"/>). The flag lives here, in
///     <see cref="BrushCreationOptions"/>, as the setting MapBrushPrimitives
///     consumes — it does NOT live in MapBrushPrimitives.cs (worker 03 owns it).
///   - <see cref="GreyboxDefaults.SuggestFuncDetail"/>: a per-brush func_detail
///     suggestion (structural vs detail candidate) from size/position. A suggestion
///     only — the func_detail ticket owns the flag and the BSP exclusion, and this
///     method never sets it.
///   - <see cref="GreyboxChecklist"/> + the Greybox*Rule classes: the greybox
///     checklist (seal, spawn, light) for the first playtest, as data and as
///     <see cref="IIssueRule"/>s an <see cref="IssueEngine"/> can register.
///     IssueEngine.cs itself is NOT edited (worker 20 owns it).
///
/// Dependency status at write time (2026-11-13, verified against the tree):
///   - hammer-face-material-uv-model (FaceMaterial) HAS landed: FaceMaterial.Default
///     is tools/toolsnodraw, IsNoDraw / ContributesToBakedMesh / BakedFaceIndices
///     are real. This file builds on them.
///   - hammer-validation-issue-engine (IssueEngine) HAS landed: IssueEngine with
///     Register/Unregister/Run/ReCheck, IIssueRule, IssueDocument/IssueBrush/
///     IssueFace/IssueEntity, LeakProbe (6 axis rays from info_player_start),
///     and 8 default rules. This file builds on them and reuses
///     <see cref="LeakProbe.PlayerStart"/> for the spawn check.
///   - hammer-brush-core-plane-based-brushes (MapBrush) has NOT landed: no
///     MapBrush type exists yet, so SuggestFuncDetail takes the landed
///     <see cref="IssueBrush"/> shape (faces as planes + polygon vertices),
///     which mirrors the MapBrush face shape from its filed spec. When MapBrush
///     lands, the integrator adds one overload taking it; the heuristic does
///     not change.
///   - hammer-brush-primitives-block-tool (MapBrushPrimitives) has NOT landed
///     (todo, unclaimed): its generators take a <see cref="BrushCreationOptions"/>
///     (defined here) and call <see cref="BrushCreationOptions.FaceMaterials"/>
///     for the new brush's faces. Nothing here references MapBrushPrimitives.
///   - hammer-optimisation-func-detail has NOT landed (todo): there is no
///     FuncDetail flag on any brush type yet, so this file suggests, never sets.
///     <see cref="IssueBrush.IsDetail"/> (the engine's existing detail flag)
///     is honoured by the heuristic: an already-detail brush stays a candidate.
///
/// DEV TEXTURE NAMES. The set uses the Source dev/ textures
/// (dev/dev_measuregeneric01 and dev/dev_measuregeneric01b — the two measure
/// textures, the ones the greybox actually paints with). They are data in
/// <see cref="DevMaterialSet"/>, one line to change;
/// the material library owns verifying them against the shipped dev textures
/// at integration. No name here is load-bearing for any test beyond the
/// canonical measure texture.
///
/// Geometry is the kernel's inches and Z-up (see handoffs/CONTRACTS.md).
/// No UI code: backend / data-model / API only.
/// </summary>
public enum DevMaterialRole
{
	Floor,
	Wall,
	Ceiling,
	Trim,
	Measure,
}

/// <summary>
/// The dev material set: which dev texture each greybox role paints with.
/// Roles exist so a caller can re-skin one role (say, the floor) without
/// changing call sites; today Floor/Ceiling/Measure share the canonical
/// measure texture and Wall/Trim share its second tone. Immutable —
/// <see cref="With"/> returns a new set.
/// </summary>
public sealed class DevMaterialSet
{
	/// <summary>The canonical greybox measure texture (orange grid, 1-unit squares).</summary>
	public const string MeasureMaterial = "dev/dev_measuregeneric01";

	/// <summary>The second-tone measure texture, for Wall/Trim contrast.</summary>
	public const string MeasureMaterialAlt = "dev/dev_measuregeneric01b";

	public string Floor { get; }
	public string Wall { get; }
	public string Ceiling { get; }
	public string Trim { get; }
	public string Measure { get; }

	public DevMaterialSet(
		string floor = MeasureMaterial,
		string wall = MeasureMaterialAlt,
		string ceiling = MeasureMaterial,
		string trim = MeasureMaterialAlt,
		string measure = MeasureMaterial )
	{
		Floor = NeedMaterial( floor, nameof( floor ) );
		Wall = NeedMaterial( wall, nameof( wall ) );
		Ceiling = NeedMaterial( ceiling, nameof( ceiling ) );
		Trim = NeedMaterial( trim, nameof( trim ) );
		Measure = NeedMaterial( measure, nameof( measure ) );
	}

	static string NeedMaterial( string name, string param )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( $"A dev material role needs a material name; '{param}' is blank.", param );
		return name.Trim();
	}

	public static DevMaterialSet Default => new DevMaterialSet();

	public string MaterialFor( DevMaterialRole role ) => role switch
	{
		DevMaterialRole.Floor => Floor,
		DevMaterialRole.Wall => Wall,
		DevMaterialRole.Ceiling => Ceiling,
		DevMaterialRole.Trim => Trim,
		DevMaterialRole.Measure => Measure,
		_ => throw new ArgumentException( $"Unknown dev material role: {role}." ),
	};

	/// <summary>Every distinct material name in the set, in role order.</summary>
	public IReadOnlyList<string> DistinctNames =>
		new[] { Floor, Wall, Ceiling, Trim, Measure }
			.Distinct( StringComparer.OrdinalIgnoreCase ).ToList();

	/// <summary>The same set with one role re-skinned; this set is unchanged.</summary>
	public DevMaterialSet With( DevMaterialRole role, string materialName )
	{
		var name = NeedMaterial( materialName, nameof( materialName ) );
		return role switch
		{
			DevMaterialRole.Floor => new DevMaterialSet( name, Wall, Ceiling, Trim, Measure ),
			DevMaterialRole.Wall => new DevMaterialSet( Floor, name, Ceiling, Trim, Measure ),
			DevMaterialRole.Ceiling => new DevMaterialSet( Floor, Wall, name, Trim, Measure ),
			DevMaterialRole.Trim => new DevMaterialSet( Floor, Wall, Ceiling, name, Measure ),
			DevMaterialRole.Measure => new DevMaterialSet( Floor, Wall, Ceiling, Trim, name ),
			_ => throw new ArgumentException( $"Unknown dev material role: {role}." ),
		};
	}

	/// <summary>JSON form: {"floor":"...","wall":"...","ceiling":"...","trim":"...","measure":"..."}.</summary>
	public JsonObject ToJson() => new()
	{
		["floor"] = Floor,
		["wall"] = Wall,
		["ceiling"] = Ceiling,
		["trim"] = Trim,
		["measure"] = Measure,
	};

	static string Name( JsonObject json, string key, string fallback )
	{
		var value = json[key]?.GetValue<string>();
		return string.IsNullOrWhiteSpace( value ) ? fallback : value.Trim();
	}

	/// <summary>Parse <see cref="ToJson"/> output. Missing roles fall back to the defaults.</summary>
	public static DevMaterialSet FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		return new DevMaterialSet(
			Name( json, "floor", MeasureMaterial ),
			Name( json, "wall", MeasureMaterialAlt ),
			Name( json, "ceiling", MeasureMaterial ),
			Name( json, "trim", MeasureMaterialAlt ),
			Name( json, "measure", MeasureMaterial ) );
	}
}

/// <summary>
/// The brush-creation settings MapBrushPrimitives consumes (defined here, not
/// in MapBrushPrimitives.cs, so the greybox ticket owns the defaults and the
/// primitives ticket only reads them): NodrawFirst + DevTextureMode + the dev
/// measure material for visible faces. A plain readonly value — pass
/// <see cref="BrushCreationOptions.Default"/> or
/// <see cref="GreyboxDefaults.CreationOptions"/>; the generators call
/// <see cref="FaceMaterials"/> for the new brush's faces and never decide
/// the defaults themselves.
/// </summary>
public readonly struct BrushCreationOptions
{
	public readonly bool NodrawFirst;
	public readonly bool DevTextureMode;
	public readonly string DevMeasureMaterial;

	public BrushCreationOptions( bool nodrawFirst = true, bool devTextureMode = true, string devMeasureMaterial = null )
	{
		NodrawFirst = nodrawFirst;
		DevTextureMode = devTextureMode;
		DevMeasureMaterial = string.IsNullOrWhiteSpace( devMeasureMaterial )
			? DevMaterialSet.MeasureMaterial
			: devMeasureMaterial.Trim();
	}

	public static BrushCreationOptions Default => new BrushCreationOptions();

	/// <summary>
	/// The new brush's per-face materials, in face order. Nodraw-first (the
	/// default): every face is <see cref="FaceMaterial.NoDraw"/> — the brush
	/// stays solid, contributes nothing to the baked mesh, and visible faces
	/// get materials later via the face API. With NodrawFirst off, every face
	/// gets the dev measure material (when DevTextureMode is on); with dev mode
	/// off there is no visible default to fall back to, so faces stay nodraw.
	/// A negative count throws; zero faces returns an empty list.
	/// </summary>
	public IReadOnlyList<FaceMaterial> FaceMaterials( int faceCount )
	{
		if ( faceCount < 0 )
			throw new ArgumentOutOfRangeException( nameof( faceCount ), faceCount, "A brush cannot have a negative face count." );
		var material = NodrawFirst || !DevTextureMode
			? FaceMaterial.NoDraw
			: new FaceMaterial( DevMeasureMaterial );
		var faces = new List<FaceMaterial>( faceCount );
		for ( var i = 0; i < faceCount; i++ )
			faces.Add( material );
		return faces;
	}
}

/// <summary>What <see cref="GreyboxDefaults.SuggestFuncDetail"/> thinks a brush is for.</summary>
public enum FuncDetailVerdict
{
	/// <summary>Keep structural (a world brush): it may seal the map.</summary>
	Structural,
	/// <summary>Trim-sized: a func_detail candidate. A suggestion only — never applied here.</summary>
	DetailCandidate,
}

/// <summary>A func_detail suggestion: the verdict plus the reason, so a caller
/// (and the mapper reading it) can see WHY, not just what.</summary>
public sealed class FuncDetailSuggestion
{
	public FuncDetailVerdict Verdict { get; }
	public string Reason { get; }

	public FuncDetailSuggestion( FuncDetailVerdict verdict, string reason )
	{
		Verdict = verdict;
		Reason = reason ?? "";
	}

	public bool IsDetailCandidate => Verdict == FuncDetailVerdict.DetailCandidate;

	public override string ToString() => $"[{Verdict}] {Reason}";
}

/// <summary>
/// Greybox workflow defaults: dev-texture mode + dev material set, nodraw-first
/// brush creation, and the func_detail suggestion heuristic. One instance per
/// map document (a map-level flag, persisted with the document via
/// <see cref="ToJson"/> / <see cref="FromJson"/>); <see cref="Default"/> is
/// the greybox-phase starting point (dev mode on, nodraw first).
/// </summary>
public sealed class GreyboxDefaults
{
	/// <summary>When true, the dev material set is the source for visible greybox faces.</summary>
	public bool DevTextureMode { get; set; } = true;

	/// <summary>When true (the default), new brushes start nodraw on all faces.</summary>
	public bool NodrawFirst { get; set; } = true;

	/// <summary>The dev material set these defaults draw from.</summary>
	public DevMaterialSet DevMaterials { get; set; } = DevMaterialSet.Default;

	public static GreyboxDefaults Default => new GreyboxDefaults();

	/// <summary>The creation settings for MapBrushPrimitives, from these flags.</summary>
	public BrushCreationOptions CreationOptions =>
		new BrushCreationOptions( NodrawFirst, DevTextureMode, DevMaterials.Measure );

	/// <summary>The new brush's per-face materials — see <see cref="BrushCreationOptions.FaceMaterials"/>.</summary>
	public IReadOnlyList<FaceMaterial> NewBrushFaceMaterials( int faceCount ) =>
		CreationOptions.FaceMaterials( faceCount );

	/// <summary>
	/// Nodraw-first, applied to existing faces: every face becomes nodraw,
	/// each keeping its own UV. Returns a new list; the input faces are
	/// unchanged. A null list throws; a null face inside it throws naming
	/// the index — a missing face is a defect, not a face to silently nodraw.
	/// </summary>
	public static IReadOnlyList<FaceMaterial> ApplyNodrawFirst( IReadOnlyList<FaceMaterial> faces )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		var result = new List<FaceMaterial>( faces.Count );
		for ( var i = 0; i < faces.Count; i++ )
		{
			if ( faces[i] is null )
				throw new ArgumentException( $"Cannot nodraw-first face {i}: it is null." );
			result.Add( FaceMaterial.SetMaterial( faces[i], ToolTexture.NoDrawName ) );
		}
		return result;
	}

	/// <summary>
	/// The dev material for a role, when DevTextureMode is on. Returns false
	/// (and an empty name) when dev mode is off — there is no dev material to
	/// apply then, and the caller falls back to its own material, not to a
	/// silently substituted one.
	/// </summary>
	public bool TryMaterialForRole( DevMaterialRole role, out string materialName )
	{
		if ( !DevTextureMode )
		{
			materialName = "";
			return false;
		}
		materialName = DevMaterials.MaterialFor( role );
		return true;
	}

	/// <summary>
	/// A brush is a func_detail candidate when it is trim-sized: every axis
	/// fits in a <see cref="DetailMaxExtent"/>-inch cube. Larger than that,
	/// it is structural unless it is thin in two axes (a trim strip / sill /
	/// rod) and no longer than <see cref="DetailMaxStripLength"/> inches.
	/// </summary>
	public const float DetailMaxExtent = 64f;

	/// <summary>An axis at or under this thick counts as "thin" for the strip rule.</summary>
	public const float DetailThinExtent = 16f;

	/// <summary>A thin-in-two-axes brush longer than this may be a wall, not trim.</summary>
	public const float DetailMaxStripLength = 512f;

	/// <summary>
	/// SuggestFuncDetail(brush): structural vs detail candidate, from the
	/// brush's size — never auto-applied, and conservative by design: anything
	/// that could seal the map (large, or degenerate / unmeasurable) is
	/// structural, because a sealing brush must never be func_detail (the
	/// func_detail ticket's validation rule, not this method, enforces that).
	/// A brush already flagged detail (<see cref="IssueBrush.IsDetail"/>) stays
	/// a candidate. Null or geometry-less brushes are structural with a reason.
	/// </summary>
	public static FuncDetailSuggestion SuggestFuncDetail( IssueBrush brush )
	{
		if ( brush is null )
			return new FuncDetailSuggestion( FuncDetailVerdict.Structural,
				"No brush to judge: structural is the safe default — a brush that might seal must never be suggested as detail." );
		if ( brush.IsDetail )
			return new FuncDetailSuggestion( FuncDetailVerdict.DetailCandidate,
				"Already flagged func_detail." );
		var bounds = BrushBounds( brush );
		if ( bounds is null )
			return new FuncDetailSuggestion( FuncDetailVerdict.Structural,
				"Brush has no vertices to measure: structural is the safe default." );
		var (min, max) = bounds.Value;
		var ex = max.x - min.x;
		var ey = max.y - min.y;
		var ez = max.z - min.z;
		if ( Math.Abs( brush.Volume() ) <= 1e-6 )
			return new FuncDetailSuggestion( FuncDetailVerdict.Structural,
				$"Brush has zero volume ({ex:0.#}x{ey:0.#}x{ez:0.#}): degenerate, structural is the safe default." );
		var longest = Math.Max( ex, Math.Max( ey, ez ) );
		if ( ex <= DetailMaxExtent && ey <= DetailMaxExtent && ez <= DetailMaxExtent )
			return new FuncDetailSuggestion( FuncDetailVerdict.DetailCandidate,
				$"Fits in a {DetailMaxExtent:0.#}in cube ({ex:0.#}x{ey:0.#}x{ez:0.#}): trim-sized, a detail candidate." );
		var thinAxes = 0;
		if ( ex <= DetailThinExtent ) thinAxes++;
		if ( ey <= DetailThinExtent ) thinAxes++;
		if ( ez <= DetailThinExtent ) thinAxes++;
		if ( thinAxes >= 2 && longest <= DetailMaxStripLength )
			return new FuncDetailSuggestion( FuncDetailVerdict.DetailCandidate,
				$"Thin in two axes ({ex:0.#}x{ey:0.#}x{ez:0.#}): a trim strip / sill, a detail candidate." );
		return new FuncDetailSuggestion( FuncDetailVerdict.Structural,
			$"Largest extent {longest:0.#}in ({ex:0.#}x{ey:0.#}x{ez:0.#}): sealing-sized, keep structural." );
	}

	static (Vec3 min, Vec3 max)? BrushBounds( IssueBrush brush )
	{
		if ( brush.Faces is null )
			return null;
		var found = false;
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var face in brush.Faces )
		{
			if ( face is null || face.Vertices is null )
				continue;
			foreach ( var v in face.Vertices )
			{
				min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
				max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
				found = true;
			}
		}
		return found ? (min, max) : null;
	}

	/// <summary>JSON form: {"devTextureMode":true,"nodrawFirst":true,"devMaterials":{...}}.</summary>
	public JsonObject ToJson() => new()
	{
		["devTextureMode"] = DevTextureMode,
		["nodrawFirst"] = NodrawFirst,
		["devMaterials"] = DevMaterials.ToJson(),
	};

	public string ToJsonString() => ToJson().ToJsonString();

	/// <summary>Parse <see cref="ToJson"/> output. Missing fields fall back to the defaults.</summary>
	public static GreyboxDefaults FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var result = new GreyboxDefaults();
		if ( json["devTextureMode"] is not null )
			result.DevTextureMode = json["devTextureMode"]!.GetValue<bool>();
		if ( json["nodrawFirst"] is not null )
			result.NodrawFirst = json["nodrawFirst"]!.GetValue<bool>();
		if ( json["devMaterials"] is JsonObject devJson )
			result.DevMaterials = DevMaterialSet.FromJson( devJson );
		return result;
	}

	public static GreyboxDefaults FromJsonString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "GreyboxDefaults JSON text is blank." );
		if ( JsonNode.Parse( json ) is not JsonObject obj )
			throw new ArgumentException( "GreyboxDefaults JSON text is not an object." );
		return FromJson( obj );
	}
}

/// <summary>
/// One greybox checklist item: the seal / spawn / light checks for the first
/// playtest, as data. <see cref="Passed"/> is false exactly when the map is
/// not ready on this check; <see cref="Severity"/> is Warning for a failed
/// check (checklist guidance, not a compile gate — the engine's own Leak /
/// UnsealedMap / InvalidBrush rules are the errors) and Info when it passes.
/// </summary>
public sealed class GreyboxChecklistItem
{
	public string CheckId { get; }
	public string Name { get; }
	public bool Passed { get; }
	public IssueSeverity Severity { get; }
	public string Message { get; }
	public string FixHint { get; }

	public GreyboxChecklistItem( string checkId, string name, bool passed, string message, string fixHint )
	{
		CheckId = checkId;
		Name = name;
		Passed = passed;
		Severity = passed ? IssueSeverity.Info : IssueSeverity.Warning;
		Message = message ?? "";
		FixHint = fixHint ?? "";
	}

	public override string ToString() =>
		$"[{CheckId}] {( Passed ? "pass" : "FAIL" )}: {Message}";
}

/// <summary>
/// The greybox checklist (seal check, spawn check, light check) for the first
/// playtest, evaluated over an <see cref="IssueDocument"/> — the same snapshot
/// the IssueEngine checks, so no MapDocument adapter is needed here either.
/// <see cref="Check"/> returns all three items, in seal / spawn / light order,
/// whether they pass or fail: a checklist that only reports failures cannot
/// show a mapper what is already done.
/// <see cref="Rules"/> is the same three checks as <see cref="IIssueRule"/>s,
/// for an <see cref="IssueEngine"/> to register (no edit to IssueEngine.cs).
///
/// SEAL HONESTY. The seal item is a PRE-check, not a leak proof: it verifies
/// sealing geometry exists, a player start exists to probe from, and the
/// spawn is not buried in solid — then it asks <see cref="LeakProbe"/> whether
/// the void is reachable. The flood-fill leak proof belongs to LeakFinder
/// (hammer-19); until it lands, LeakProbe's 6 axis rays are what the engine's
/// Leak / UnsealedMap rules run on, and this checklist says so instead of
/// pretending to be a proof. The seal RULE stays silent whenever the engine's
/// own Leak / UnsealedMap rules already report the same opening, so one
/// engine Run never lists the same gap twice; the checklist ITEM still fails,
/// because the seal is what failed.
/// </summary>
public static class GreyboxChecklist
{
	/// <summary>The player-start classname the spawn check looks for.</summary>
	public const string SpawnClassName = "info_player_start";

	/// <summary>Classnames that count as a light for the light check.</summary>
	public static readonly string[] LightClassNames = { "light", "light_spot", "light_environment", "light_dynamic" };

	/// <summary>The three checklist checks as engine rules, in seal / spawn / light order.</summary>
	public static IReadOnlyList<IIssueRule> Rules => new IIssueRule[]
	{
		new GreyboxSealRule(),
		new GreyboxSpawnRule(),
		new GreyboxLightRule(),
	};

	/// <summary>All three checklist items, in seal / spawn / light order. Null document throws.</summary>
	public static IReadOnlyList<GreyboxChecklistItem> Check( IssueDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		return new[]
		{
			SealItem( document ),
			SpawnItem( document ),
			LightItem( document ),
		};
	}

	static GreyboxChecklistItem SealItem( IssueDocument document )
	{
		var failure = SealPreCheckFailure( document );
		if ( failure is not null )
			return new GreyboxChecklistItem( "greybox-seal", "Seal", false, failure.Message, failure.FixHint );
		var escapes = LeakProbe.Probe( document );
		if ( escapes.Count > 0 )
		{
			var directions = string.Join( ", ", escapes.Select( e => e.Direction.ToString() ) );
			return new GreyboxChecklistItem( "greybox-seal", "Seal", false,
				$"Seal fails: the void is reachable {directions} from the player start (nearest opening near {escapes[0].Exit}).",
				"Close the opening on the reported side(s); a leak through a gap is reported separately as a leak." );
		}
		return new GreyboxChecklistItem( "greybox-seal", "Seal", true,
			"Seal pre-check passed: sealing geometry exists, the spawn is in open space, and no axis ray escapes. The flood-fill leak proof is the Leak / Unsealed-map rules.",
			"" );
	}

	static GreyboxChecklistItem SpawnItem( IssueDocument document )
	{
		var start = LeakProbe.PlayerStart( document );
		if ( start is null )
			return new GreyboxChecklistItem( "greybox-spawn", "Spawn", false,
				"No info_player_start: the map cannot be playtested, and the seal cannot be probed without one.",
				"Place an info_player_start in open space inside the sealed room." );
		return new GreyboxChecklistItem( "greybox-spawn", "Spawn", true,
			$"Player start present at {start.Value}.",
			"" );
	}

	static GreyboxChecklistItem LightItem( IssueDocument document )
	{
		if ( document.Entities is not null )
		{
			foreach ( var entity in document.Entities )
			{
				if ( entity is null )
					continue;
				foreach ( var lightClass in LightClassNames )
				{
					if ( string.Equals( entity.ClassName, lightClass, StringComparison.OrdinalIgnoreCase ) )
						return new GreyboxChecklistItem( "greybox-light", "Light", true,
							$"Light present: {entity.ClassName} at {entity.Origin}.",
							"" );
				}
			}
			foreach ( var entity in document.Entities )
			{
				if ( entity is not null && string.Equals( entity.ClassName, "env_sun", StringComparison.OrdinalIgnoreCase ) )
					return new GreyboxChecklistItem( "greybox-light", "Light", false,
						"Only env_sun is present: it needs a light_environment to light the map.",
						"Add a light_environment paired with the env_sun, or a light." );
			}
		}
		return new GreyboxChecklistItem( "greybox-light", "Light", false,
			"No light: the first playtest would be pitch black.",
			"Add a light or a light_environment before the first playtest." );
	}

	/// <summary>
	/// The seal pre-check failure, or null when the pre-check passes: no
	/// brushes, no structural (non-detail) brush, no player start to probe
	/// from, or the spawn buried in solid. Shared by the checklist item and
	/// the seal rule so they can never disagree about the pre-check.
	/// </summary>
	internal static GreyboxSealFailure SealPreCheckFailure( IssueDocument document )
	{
		var brushes = new List<IssueBrush>();
		if ( document.Brushes is not null )
			foreach ( var brush in document.Brushes )
				if ( brush is not null )
					brushes.Add( brush );
		if ( brushes.Count == 0 )
			return new GreyboxSealFailure(
				"No brushes in the map: nothing seals it.",
				"Block out the room with structural brushes first; detail brushes do not seal." );
		var structural = false;
		foreach ( var brush in brushes )
		{
			if ( brush.IsDetail )
				continue;
			if ( brush.Faces is not null && brush.Faces.Count > 0 )
			{
				structural = true;
				break;
			}
		}
		if ( !structural )
			return new GreyboxSealFailure(
				"Every brush is func_detail: detail brushes do not seal the map.",
				"Keep at least the walls / floor / ceiling as structural world brushes." );
		var start = LeakProbe.PlayerStart( document );
		if ( start is null )
			return new GreyboxSealFailure(
				"Seal not verified: no info_player_start to probe from.",
				"Place an info_player_start, then the leak check can probe the seal." );
		foreach ( var brush in brushes )
		{
			if ( brush.IsDetail )
				continue;
			if ( IsInsideSolid( start.Value, brush ) )
				return new GreyboxSealFailure(
					$"Player start is inside solid brush #{brush.Id}: the spawn is buried.",
					"Move the info_player_start out of the brush into open space.",
					brush.Id, start.Value );
		}
		return null;
	}

	/// <summary>Point-in-brush via the face half-spaces (solid = Dot(N, p) &lt;= Distance).</summary>
	internal static bool IsInsideSolid( Vec3 point, IssueBrush brush )
	{
		if ( brush.Faces is null || brush.Faces.Count == 0 )
			return false;
		const float epsilon = 1e-3f;
		foreach ( var face in brush.Faces )
		{
			if ( face is null )
				return false;
			if ( Vec3.Dot( face.Normal.Normal, point ) - face.Distance > epsilon )
				return false;
		}
		return true;
	}
}

/// <summary>A seal pre-check failure: what failed, how to fix it, and (for a
/// buried spawn) which brush and where.</summary>
internal sealed class GreyboxSealFailure
{
	public string Message { get; }
	public string FixHint { get; }
	public int BrushId { get; }
	public Vec3 Location { get; }
	public bool HasLocation { get; }

	public GreyboxSealFailure( string message, string fixHint, int brushId = -1, Vec3 location = default, bool hasLocation = false )
	{
		Message = message ?? "";
		FixHint = fixHint ?? "";
		BrushId = brushId;
		Location = location;
		HasLocation = hasLocation || brushId >= 0;
	}
}

/// <summary>
/// Greybox seal rule: the seal PRE-check as an engine rule (no brushes, no
/// structural brush, spawn buried in solid). It stays silent when the
/// pre-check passes — including when the probe escapes, because the engine's
/// own Leak / UnsealedMap rules already report that opening, and one Run must
/// never list the same gap twice. Warning, not Error: a greybox in progress
/// is not sealed yet, and the checklist is guidance, not a compile gate.
/// </summary>
public sealed class GreyboxSealRule : IIssueRule
{
	public const string RuleId = "greybox-seal";
	public string Id => RuleId;
	public string Name => "Greybox seal pre-check";
	public IssueScope Scope => IssueScope.Global;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.UnsealedMap };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null )
			return issues;
		var failure = GreyboxChecklist.SealPreCheckFailure( document );
		if ( failure is null )
			return issues;
		issues.Add( new Issue
		{
			Kind = IssueKind.UnsealedMap,
			Severity = IssueSeverity.Warning,
			Code = "GREYBOX_UNSEALED",
			BrushId = failure.BrushId,
			Location = failure.Location,
			HasLocation = failure.HasLocation,
			Message = failure.Message,
			FixHint = failure.FixHint,
		} );
		return issues;
	}
}

/// <summary>
/// Greybox spawn rule: no info_player_start. No IssueKind covers "a required
/// entity is missing" (the landed enum has MissingTarget for a dangling
/// reference, EntityNoClass for an entity with no classname, nothing for an
/// entity that is not there at all), so this rule reports under
/// <see cref="IssueKind.MissingTarget"/> — the least-wrong bucket, and the one
/// whose ReCheck scope (Entity, via MissingTargetRule) makes the issue drop
/// and re-emit correctly on every re-check instead of going stale. The stable
/// identity is the Code (GREYBOX_NO_SPAWN): switch on Code, never on Kind,
/// for greybox issues. A dedicated MissingEntity kind is a one-line enum
/// addition for the IssueEngine owner; until then this bucket is documented,
/// not silent. Scope is Entity because the check reads the entity list.
/// </summary>
public sealed class GreyboxSpawnRule : IIssueRule
{
	public const string RuleId = "greybox-spawn";
	public string Id => RuleId;
	public string Name => "Greybox spawn check";
	public IssueScope Scope => IssueScope.Entity;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.MissingTarget };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null )
			return issues;
		if ( LeakProbe.PlayerStart( document ) is not null )
			return issues;
		issues.Add( new Issue
		{
			Kind = IssueKind.MissingTarget,
			Severity = IssueSeverity.Warning,
			Code = "GREYBOX_NO_SPAWN",
			HasLocation = false,
			Message = "No info_player_start: the map cannot be playtested, and the seal cannot be probed without one.",
			FixHint = "Place an info_player_start in open space inside the sealed room.",
		} );
		return issues;
	}
}

/// <summary>
/// Greybox light rule: no light entity. Same MissingTarget-bucket note as
/// <see cref="GreyboxSpawnRule"/>: no landed IssueKind covers a missing
/// required entity, the Code (GREYBOX_NO_LIGHT) is the stable identity, and
/// Entity scope keeps ReCheck refresh correct. An env_sun alone does not
/// count — it needs a light_environment to light the map.
/// </summary>
public sealed class GreyboxLightRule : IIssueRule
{
	public const string RuleId = "greybox-light";
	public string Id => RuleId;
	public string Name => "Greybox light check";
	public IssueScope Scope => IssueScope.Entity;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.MissingTarget };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null )
			return issues;
		if ( document.Entities is not null )
		{
			foreach ( var entity in document.Entities )
			{
				if ( entity is null )
					continue;
				foreach ( var lightClass in GreyboxChecklist.LightClassNames )
				{
					if ( string.Equals( entity.ClassName, lightClass, StringComparison.OrdinalIgnoreCase ) )
						return issues;
				}
			}
		}
		var onlySun = false;
		if ( document.Entities is not null )
		{
			foreach ( var entity in document.Entities )
			{
				if ( entity is not null && string.Equals( entity.ClassName, "env_sun", StringComparison.OrdinalIgnoreCase ) )
				{
					onlySun = true;
					break;
				}
			}
		}
		issues.Add( new Issue
		{
			Kind = IssueKind.MissingTarget,
			Severity = IssueSeverity.Warning,
			Code = "GREYBOX_NO_LIGHT",
			HasLocation = false,
			Message = onlySun
				? "Only env_sun is present: it needs a light_environment to light the map."
				: "No light: the first playtest would be pitch black.",
			FixHint = onlySun
				? "Add a light_environment paired with the env_sun, or a light."
				: "Add a light or a light_environment before the first playtest.",
		} );
		return issues;
	}
}
