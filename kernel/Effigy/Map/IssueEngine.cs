using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P1: live Issue engine (hammer-validation-issue-engine).
///
/// Pass 1 detects issues with named fix hints; it implements NO fixes
/// (auto-fix is the later ticket hammer-issue-autofix-actions, which
/// switches on <see cref="Issue.Code"/>). Backend / data-model / API only:
/// no UI, no Hammer/VBSP shell-out, engine-free kernel code.
///
/// Rules (TrenchBroom Issue Browser, live while editing): leak, unsealed
/// map, invalid brush, missing target, missing material, entity with no
/// class, duplicate target name, compiler limits, giant skybox. Each issue carries a
/// severity, a location (entity / brush / coordinates) and a named fix hint.
///
/// Dependency status, re-verified against the tree 2026-11-13 ~16:20 EST
/// (siblings landed WHILE this file was written):
///   - MapBrush HAS landed (kernel/Effigy/Map/MapBrush.cs, namespace
///     Effigy): FromPlanes / FromVertices / FromFaceVertices / Box,
///     solved vertices / Volume / Centroid, IsValid + Validate() with
///     MapBrushDefect codes (TooFewFaces, ZeroVolume, NonPlanarFace,
///     ConcaveInput, DuplicateFace, CoplanarSplitFace, ...). Vertices
///     are solved from planes, never stored; an omitted material
///     defaults to nodraw, so a real MapBrush face never has an empty
///     material id.
///   - MapDocument has NOT landed (no MapDocument type anywhere yet).
///   - LeakFinder HAS landed (namespace Effigy): FindLeak(
///     ILeakPortalGraph, playerStart) -> LeakResult (IsLeaked,
///     GapLocation, ExitPoint, Path) + Valve .lin/.pts point files.
///     It works on a BSP portal graph, NOT on brushes, so this file's
///     ray probe cannot delegate to it directly — the graph comes
///     from BspBuilder, and the registry is the seam (unregister
///     "leak" / "unsealed-map", register a LeakFinder-backed rule).
///   - MapEntities HAS landed (namespace Effigy): MapEntity (Id,
///     ClassName, Origin, TargetName, Keyvalues) + MapEntities.
///     Validate() (missing target = error, duplicate targetname =
///     warning). Mismatches vs this file, reconciled where cheap:
///     MapEntities skips "!"-prefixed targets (special targets, not
///     entity names) — this file does the same now; it emits one
///     duplicate issue per EXTRA holder, this file emits one per
///     duplicated name; it only checks registry-Target-typed keys,
///     this file checks the literal "target" / "killtarget" keys.
///   - MapVertexEdit HAS landed (namespace Effigy): MapVertexBrush
///     (vertex/index mesh) + Validate() with per-face MapFaceDefect
///     flags (NonPlanar / NonConvex / Degenerate / ZeroArea /
///     Inverted / DuplicateFace), MoveVertex / MoveEdge / SplitFace.
///     Its planar tolerance default is 1e-3; this file's
///     InvalidBrushRule uses the same 1e-3 so the two validators
///     agree on boundary cases. Its per-face flags are the extension
///     the brush-level check lacks; the two are complementary, not
///     competing.
/// Per the ticket ("implement against the spec exactly as filed and report
/// any mismatch"), this file therefore defines its own minimal document
/// snapshot (<see cref="IssueDocument"/> / <see cref="IssueBrush"/> /
/// <see cref="IssueFace"/> / <see cref="IssueEntity"/>) in the
/// Effigy.Map namespace, shaped exactly as the sibling descs specify:
/// faces as planes (normal + distance) + per-face material + polygon
/// vertices, entities with classname / origin / keyvalues, target-name
/// validation (missing target = error, duplicate target name = warning).
/// When MapDocument lands, the integrator adds ONE thin adapter that
/// projects MapDocument onto IssueDocument; the rule logic here does not
/// change, and no type in this file may be redefined by a sibling
/// (that would be a duplicate-type compile break — reconcile, do not fork).
///
/// Leak rule honesty: the real flood-fill lives in LeakFinder
/// (hammer-19, over the BspBuilder portal graph). Until it lands, the
/// Leak / UnsealedMap rules use <see cref="LeakProbe"/>, a geometric
/// stand-in: 6 axis rays from the player start; a ray that escapes is a
/// leak if sealing faces sit near its exit (a gap between brushes) and
/// unsealed if no sealing face is anywhere near it (a missing wall).
/// A gap only a diagonal ray can reach is NOT reported — that is
/// LeakFinder's job, and this file says so instead of pretending.
/// The rule registry is the seam: register a LeakFinder-backed rule and
/// unregister <see cref="LeakRule"/> / <see cref="UnsealedMapRule"/>.
///
/// Geometry is the kernel's inches and Z-up (see handoffs/CONTRACTS.md).
/// Brush faces are outward-wound; a brush is the intersection of its
/// face half-spaces (solid = Dot(N, p) &lt;= Distance for every face).
/// </summary>
public enum IssueSeverity
{
	Error,
	Warning,
	Info,
}

public enum IssueKind
{
	Leak,
	UnsealedMap,
	InvalidBrush,
	MissingTarget,
	MissingMaterial,
	EntityNoClass,
	DuplicateTargetName,
	LimitExceeded,
	/// <summary>A 2D skybox box (sky-material brushes) vastly larger than the map bounds (hammer-compile-safety-net-gate).</summary>
	GiantSkybox,
	/// <summary>A prop entity (prop_static / prop_dynamic / prop_physics) with a zero
	/// scale component (hammer-prop-lighting-negative-scale): it cannot be lit.</summary>
	ZeroScaleProp,
}

/// <summary>Which part of a document a rule reads, so
/// <see cref="IssueEngine.ReCheck"/> knows what it may skip.
/// Brush rules are the expensive ones (geometry + GeometryLinter) and
/// are the ones ReCheck genuinely skips for unchanged brushes; Entity
/// and Global rules are O(n) and re-run in full on every ReCheck.</summary>
public enum IssueScope
{
	Brush,
	Entity,
	Global,
}

/// <summary>One detected problem. Pass 1 only: a <see cref="FixHint"/>
/// names the fix, nothing here applies it.</summary>
public sealed class Issue
{
	public IssueKind Kind;
	public IssueSeverity Severity;
	/// <summary>Stable machine code for the auto-fix ticket to switch on
	/// ("LEAK", "INVALID_BRUSH", ...).</summary>
	public string Code = "";
	public string Message = "";
	public string FixHint = "";
	/// <summary>Brush the issue is about, or -1.</summary>
	public int BrushId = -1;
	/// <summary>Entity the issue is about, or -1.</summary>
	public int EntityId = -1;
	/// <summary>World location (inches, Z-up). Meaningful only when
	/// <see cref="HasLocation"/> is true.</summary>
	public Vec3 Location;
	public bool HasLocation;

	public override string ToString() =>
		$"[{Severity} {Code}] {Message}" + ( string.IsNullOrEmpty( FixHint ) ? "" : $" (fix: {FixHint})" );
}

/// <summary>Compiler limits the engine checks. Defaults are the Source
/// engine's published limits; hammer-compile-native-pipeline owns the
/// Geppetto-native numbers when they land — override per document until
/// then. A test fixture sets small limits to make an over-limit map
/// cheap to build.</summary>
public sealed class IssueLimits
{
	public int MaxBrushes = 8192;
	public int MaxFaces = 65536;
	public int MaxEntities = 4096;
	public int MaxVertices = 65536;
}

/// <summary>One face of a brush: a plane (outward unit normal + distance,
/// solid is Dot(N, p) &lt;= Distance) plus its polygon vertices and its
/// material id. Mirrors the MapBrush face shape from
/// hammer-brush-core-plane-based-brushes.</summary>
public sealed class IssueFace
{
	public Vec3 Normal;
	public double Distance;
	public string Material = "";
	public List<Vec3> Vertices = new List<Vec3>();

	public IssueFace()
	{
	}

	public IssueFace( Vec3 normal, double distance, string material, IEnumerable<Vec3> vertices )
	{
		Normal = normal;
		Distance = distance;
		Material = material ?? "";
		if ( vertices != null )
			Vertices.AddRange( vertices );
	}

	public Vec3 Centroid()
	{
		if ( Vertices.Count == 0 )
			return Vec3.Zero;
		var sum = Vec3.Zero;
		foreach ( var v in Vertices )
			sum += v;
		return sum / Vertices.Count;
	}
}

/// <summary>One convex brush solid: the intersection of its face
/// half-spaces. Detail brushes do not seal the map (Source rule), so
/// the leak rules ignore them.</summary>
public sealed class IssueBrush
{
	public int Id;
	public List<IssueFace> Faces = new List<IssueFace>();
	public bool IsDetail;

	public IssueBrush()
	{
	}

	public IssueBrush( int id, IEnumerable<IssueFace> faces, bool isDetail = false )
	{
		Id = id;
		if ( faces != null )
			Faces.AddRange( faces );
		IsDetail = isDetail;
	}

	/// <summary>Signed volume by the divergence theorem over the
	/// fan-triangulated faces. Positive for outward-wound brushes,
	/// zero for a flat brush, negative for an inward-wound brush.</summary>
	public double Volume()
	{
		var total = 0.0;
		foreach ( var face in Faces )
		{
			if ( face is null || face.Vertices.Count < 3 )
				continue;
			var v0 = face.Vertices[0];
			for ( var i = 1; i + 1 < face.Vertices.Count; i++ )
				total += Vec3.Dot( v0, Vec3.Cross( face.Vertices[i], face.Vertices[i + 1] ) ) / 6.0;
		}
		return total;
	}

	/// <summary>Mean of all face vertices (location only, not a
	/// volume centroid).</summary>
	public Vec3 Centroid()
	{
		var sum = Vec3.Zero;
		var count = 0;
		foreach ( var face in Faces )
		{
			if ( face is null )
				continue;
			foreach ( var v in face.Vertices )
			{
				sum += v;
				count++;
			}
		}
		return count == 0 ? Vec3.Zero : sum / count;
	}
}

/// <summary>One map entity: classname, origin, keyvalues, and the
/// brushes tied to it (empty for a point entity). Mirrors the
/// MapEntities shape from hammer-entity-place-edit-validate.</summary>
public sealed class IssueEntity
{
	public int Id;
	public string ClassName = "";
	public Vec3 Origin;
	/// <summary>Per-axis prop scale, copied from MapEntity.Scale by the projection
	/// that builds this document (MapDocument adapter / integrator). (1, 1, 1)
	/// when unscaled; a zero component on a prop entity is a ZeroScaleProp issue.</summary>
	public Vec3 Scale = Vec3.One;
	public Dictionary<string, string> KeyValues = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
	public List<int> BrushIds = new List<int>();

	public IssueEntity()
	{
	}

	public IssueEntity( int id, string className, Vec3 origin )
	{
		Id = id;
		ClassName = className ?? "";
		Origin = origin;
	}

	/// <summary>This entity's own name (key "targetname"), or "".</summary>
	public string TargetName => GetKeyValue( "targetname" );

	public string GetKeyValue( string key )
	{
		if ( key is null || KeyValues is null )
			return "";
		return KeyValues.TryGetValue( key, out var value ) ? value ?? "" : "";
	}

	public void SetKeyValue( string key, string value )
	{
		if ( string.IsNullOrEmpty( key ) )
			return;
		KeyValues[key] = value ?? "";
	}
}

/// <summary>The document snapshot the engine checks. This is the
/// engine's own view of a map (see the file header: MapDocument has
/// not landed, so the integrator adapts it onto this shape with one
/// projection). Never null lists: a missing list reads as empty.</summary>
public sealed class IssueDocument
{
	public string Name = "";
	public List<IssueBrush> Brushes = new List<IssueBrush>();
	public List<IssueEntity> Entities = new List<IssueEntity>();
	public IssueLimits Limits = new IssueLimits();
	/// <summary>
	/// Reverse id maps for projections that surrogate non-integer ids
	/// (the pre-compile gate adapter: check documents carry string ids).
	/// Null when the document was built with native int ids; the gate
	/// maps engine issues back to the original string ids through these.
	/// </summary>
	public Dictionary<int, string> BrushIdByInt;
	public Dictionary<int, string> EntityIdByInt;
}

/// <summary>One check in the registry. A rule declares the issue kinds
/// it can produce so <see cref="IssueEngine.ReCheck"/> can drop and
/// refresh exactly those kinds.</summary>
public interface IIssueRule
{
	string Id { get; }
	string Name { get; }
	IssueScope Scope { get; }
	IReadOnlyList<IssueKind> Kinds { get; }
	IReadOnlyList<Issue> Check( IssueDocument document );
}

/// <summary>What the last <see cref="IssueEngine.Run"/> /
/// <see cref="IssueEngine.ReCheck"/> actually did. Tests use
/// <see cref="BrushesChecked"/> to prove ReCheck skipped unchanged
/// brushes instead of re-checking the whole map.</summary>
public sealed class IssueRunStats
{
	public bool WasIncremental;
	public int BrushesChecked;
	public int EntitiesChecked;
	public int RulesRun;
	public int IssuesFound;
	public double ElapsedMilliseconds;
}

/// <summary>Live Issue engine: a rule registry over an
/// <see cref="IssueDocument"/>.
///
/// <see cref="Run"/> checks the whole document. <see cref="ReCheck"/>
/// is the live path: give it the brush / entity ids that changed since
/// the last run plus that run's issues, and it re-checks only the
/// changed brushes with the expensive (geometry) rules, re-runs the
/// cheap entity / global rules in full, and keeps every untouched
/// issue. Call <see cref="Run"/> for the first pass; ReCheck only
/// refreshes, it never discovers issues in objects it was never told
/// about. Rules must not throw: a throwing rule fails the run, loudly,
/// instead of silently dropping its issues.</summary>
public sealed class IssueEngine
{
	readonly List<IIssueRule> _rules = new List<IIssueRule>();

	public IssueEngine()
	{
		Register( new InvalidBrushRule() );
		Register( new MissingMaterialRule() );
		Register( new MissingTargetRule() );
		Register( new DuplicateTargetNameRule() );
		Register( new EntityNoClassRule() );
		Register( new LeakRule() );
		Register( new UnsealedMapRule() );
		Register( new LimitRule() );
		Register( new GiantSkyboxRule() );
		Register( new ZeroScalePropRule() );
	}

	/// <summary>Registered rules, in registration order. The ten
	/// defaults are registered by the constructor.</summary>
	public IReadOnlyList<IIssueRule> Rules => _rules;

	/// <summary>Stats from the last <see cref="Run"/> /
	/// <see cref="ReCheck"/>.</summary>
	public IssueRunStats LastStats { get; private set; } = new IssueRunStats();

	public void Register( IIssueRule rule )
	{
		if ( rule is null )
			throw new ArgumentNullException( nameof( rule ) );
		if ( string.IsNullOrWhiteSpace( rule.Id ) )
			throw new ArgumentException( "A rule needs a non-empty Id.", nameof( rule ) );
		if ( _rules.Any( r => r.Id == rule.Id ) )
			throw new ArgumentException( $"A rule with Id '{rule.Id}' is already registered.", nameof( rule ) );
		_rules.Add( rule );
	}

	/// <summary>Removes the rule with this Id. Returns false when no
	/// such rule was registered. This is the seam LeakFinder uses:
	/// unregister "leak" / "unsealed-map" and register its flood-fill
	/// rule in their place.</summary>
	public bool Unregister( string id )
	{
		if ( string.IsNullOrEmpty( id ) )
			return false;
		var index = _rules.FindIndex( r => r.Id == id );
		if ( index < 0 )
			return false;
		_rules.RemoveAt( index );
		return true;
	}

	/// <summary>Full check: every rule over the whole document.
	/// The result is sorted deterministically (kind, brush, entity,
	/// message) so a live browser can diff runs.</summary>
	public IReadOnlyList<Issue> Run( IssueDocument document )
	{
		if ( document is null )
			throw new ArgumentNullException( nameof( document ) );
		var clock = Stopwatch.StartNew();
		var issues = new List<Issue>();
		foreach ( var rule in _rules )
		{
			var found = rule.Check( document );
			if ( found != null )
				issues.AddRange( found );
		}
		Sort( issues );
		LastStats = new IssueRunStats
		{
			WasIncremental = false,
			BrushesChecked = document.Brushes != null ? document.Brushes.Count : 0,
			EntitiesChecked = document.Entities != null ? document.Entities.Count : 0,
			RulesRun = _rules.Count,
			IssuesFound = issues.Count,
			ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
		};
		return issues;
	}

	/// <summary>Incremental re-check after an edit. Only the changed
	/// brushes are re-run through the Brush-scope rules; Entity-scope
	/// rules re-run in full (they are O(entities) and a changed target
	/// can clear or create an issue on an unchanged entity); Global
	/// rules re-run when anything changed at all. Previous issues the
	/// re-check does not refresh are kept as-is.</summary>
	public IReadOnlyList<Issue> ReCheck(
		IssueDocument document,
		IReadOnlyCollection<int> changedBrushIds,
		IReadOnlyCollection<int> changedEntityIds,
		IReadOnlyList<Issue> previous )
	{
		if ( document is null )
			throw new ArgumentNullException( nameof( document ) );
		if ( previous is null )
			throw new ArgumentNullException( nameof( previous ) );
		var clock = Stopwatch.StartNew();
		var changedBrushes = changedBrushIds != null
			? new HashSet<int>( changedBrushIds )
			: new HashSet<int>();
		var changedEntities = changedEntityIds != null
			? new HashSet<int>( changedEntityIds )
			: new HashSet<int>();
		var anythingChanged = changedBrushes.Count > 0 || changedEntities.Count > 0;
		var scopeByKind = new Dictionary<IssueKind, IssueScope>();
		foreach ( var rule in _rules )
		{
			if ( rule.Kinds is null )
				continue;
			foreach ( var kind in rule.Kinds )
				if ( !scopeByKind.ContainsKey( kind ) )
					scopeByKind[kind] = rule.Scope;
		}

		var result = new List<Issue>();
		foreach ( var issue in previous )
		{
			if ( issue is null )
				continue;
			if ( !scopeByKind.TryGetValue( issue.Kind, out var scope ) )
			{
				result.Add( issue );
				continue;
			}
			if ( scope == IssueScope.Brush && issue.BrushId >= 0 && changedBrushes.Contains( issue.BrushId ) )
				continue;
			if ( scope == IssueScope.Entity )
				continue;
			if ( scope == IssueScope.Global && anythingChanged )
				continue;
			result.Add( issue );
		}

		var rulesRun = 0;
		var brushesChecked = 0;
		if ( changedBrushes.Count > 0 )
		{
			var changed = new List<IssueBrush>();
			if ( document.Brushes != null )
				foreach ( var brush in document.Brushes )
					if ( brush != null && changedBrushes.Contains( brush.Id ) )
						changed.Add( brush );
			brushesChecked = changed.Count;
			var projection = new IssueDocument
			{
				Name = document.Name,
				Brushes = changed,
				Entities = new List<IssueEntity>(),
				Limits = document.Limits,
			};
			foreach ( var rule in _rules.Where( r => r.Scope == IssueScope.Brush ) )
			{
				rulesRun++;
				var found = rule.Check( projection );
				if ( found != null )
					result.AddRange( found );
			}
		}
		foreach ( var rule in _rules.Where( r => r.Scope == IssueScope.Entity ) )
		{
			rulesRun++;
			var found = rule.Check( document );
			if ( found != null )
				result.AddRange( found );
		}
		if ( anythingChanged )
		{
			foreach ( var rule in _rules.Where( r => r.Scope == IssueScope.Global ) )
			{
				rulesRun++;
				var found = rule.Check( document );
				if ( found != null )
					result.AddRange( found );
			}
		}
		Sort( result );
		LastStats = new IssueRunStats
		{
			WasIncremental = true,
			BrushesChecked = brushesChecked,
			EntitiesChecked = document.Entities != null ? document.Entities.Count : 0,
			RulesRun = rulesRun,
			IssuesFound = result.Count,
			ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
		};
		return result;
	}

	static void Sort( List<Issue> issues )
	{
		issues.Sort( ( a, b ) =>
		{
			var byKind = a.Kind.CompareTo( b.Kind );
			if ( byKind != 0 )
				return byKind;
			var byBrush = a.BrushId.CompareTo( b.BrushId );
			if ( byBrush != 0 )
				return byBrush;
			var byEntity = a.EntityId.CompareTo( b.EntityId );
			if ( byEntity != 0 )
				return byEntity;
			return string.CompareOrdinal( a.Message, b.Message );
		} );
	}
}

/// <summary>Invalid brush: fewer than 4 faces, a non-planar face, a
/// zero-area face, duplicate / coplanar-split faces, zero or negative
/// volume, a non-convex solid, or a mesh that is not watertight /
/// manifold per GeometryLinter. One issue per brush, listing every
/// defect found — never a silent fix, never a silent delete.</summary>
public sealed class InvalidBrushRule : IIssueRule
{
	public const string RuleId = "invalid-brush";
	public string Id => RuleId;
	public string Name => "Invalid brush";
	public IssueScope Scope => IssueScope.Brush;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.InvalidBrush };

	/// <summary>Planarity tolerance, inches. Matches
	/// MapVertexEdit.Validate's default (1e-3) so the two validators
	/// agree on boundary cases; a twisted vertex is orders of
	/// magnitude larger than this.</summary>
	public const double PlanarTolerance = 0.001;

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null || document.Brushes is null )
			return issues;
		foreach ( var brush in document.Brushes )
		{
			if ( brush is null )
				continue;
			var defects = Validate( brush );
			if ( defects.Count == 0 )
				continue;
			var shown = defects.Count <= 6
				? string.Join( "; ", defects )
				: string.Join( "; ", defects.GetRange( 0, 6 ) ) + $"; and {defects.Count - 6} more";
			var hasLocation = brush.Faces != null && brush.Faces.Count > 0;
			issues.Add( new Issue
			{
				Kind = IssueKind.InvalidBrush,
				Severity = IssueSeverity.Error,
				Code = "INVALID_BRUSH",
				BrushId = brush.Id,
				Location = hasLocation ? brush.Centroid() : Vec3.Zero,
				HasLocation = hasLocation,
				Message = $"Brush {brush.Id} is invalid: {shown}",
				FixHint = "Triangulate the brush into valid convex pieces (JACK-style); never silently delete it. Auto-fix is a later ticket.",
			} );
		}
		return issues;
	}

	/// <summary>Named defects for one brush, in check order. Shared
	/// with the vertex-edit ticket's per-face reporting: a twisted
	/// brush is flagged here, never repaired.</summary>
	public static List<string> Validate( IssueBrush brush )
	{
		var defects = new List<string>();
		if ( brush is null )
		{
			defects.Add( "brush entry is null" );
			return defects;
		}
		if ( brush.Faces is null || brush.Faces.Count == 0 )
		{
			defects.Add( "has no faces" );
			return defects;
		}
		if ( brush.Faces.Count < 4 )
			defects.Add( $"has only {brush.Faces.Count} faces (a convex solid needs at least 4)" );

		// Volume first: it is the brush-level summary verdict, and the
		// issue message only shows the first 6 defects — a degenerate
		// brush can carry a dozen per-face defects, which used to push
		// the volume verdict out of the shown window, so a zero-volume
		// brush was reported without naming the volume defect (the
		// gate + runner tests pin "zero-volume" in the message).
		var volume = brush.Volume();
		if ( double.IsNaN( volume ) || double.IsInfinity( volume ) )
			defects.Add( "volume is not a finite number" );
		else if ( volume <= 1e-6 )
			defects.Add( $"zero-volume ({volume.ToString( "0.###", CultureInfo.InvariantCulture )})" );

		for ( var i = 0; i < brush.Faces.Count; i++ )
		{
			var face = brush.Faces[i];
			if ( face is null )
			{
				defects.Add( $"face {i} is null" );
				continue;
			}
			if ( face.Vertices is null || face.Vertices.Count < 3 )
			{
				defects.Add( $"face {i} has fewer than 3 vertices" );
				continue;
			}
			var area2 = 0.0;
			var v0 = face.Vertices[0];
			for ( var k = 1; k + 1 < face.Vertices.Count; k++ )
				area2 += Vec3.Cross( face.Vertices[k] - v0, face.Vertices[k + 1] - v0 ).Length;
			if ( area2 <= 1e-9 )
				defects.Add( $"face {i} has zero area" );
			var normal = face.Normal.Normal;
			var worst = 0.0;
			foreach ( var v in face.Vertices )
				worst = Math.Max( worst, Math.Abs( Vec3.Dot( normal, v ) - face.Distance ) );
			if ( worst > PlanarTolerance )
				defects.Add( $"face {i} is not planar (max deviation {worst.ToString( "0.###", CultureInfo.InvariantCulture )})" );
			for ( var j = 0; j < i; j++ )
			{
				var other = brush.Faces[j];
				if ( other is null )
					continue;
				// Same plane, same direction: equal distances. Same
				// plane, opposite direction: NEGATED distances, because
				// the plane {Dot(N,p)=D} with N flipped is
				// {Dot(-N,p)=-D}. Comparing |D0-D1| alone is wrong: the
				// opposite faces of any origin-centred box have equal
				// |D| (e.g. floor -X D=128, +X D=128) and are NOT the
				// same plane. (Caught by the isolated harness, 2026-11-13:
				// every box in the sealed room was flagged.)
				var dot = Vec3.Dot( normal, other.Normal.Normal );
				var sameDirection = dot > 1 - 1e-6 &&
					Math.Abs( face.Distance - other.Distance ) <= PlanarTolerance;
				var oppositeSamePlane = dot < -( 1 - 1e-6 ) &&
					Math.Abs( face.Distance + other.Distance ) <= PlanarTolerance;
				if ( sameDirection || oppositeSamePlane )
					defects.Add( $"faces {j} and {i} are duplicate / coplanar-split faces" );
			}
		}

		var nonConvex = 0;
		foreach ( var face in brush.Faces )
		{
			if ( face is null || face.Vertices is null )
				continue;
			var normal = face.Normal.Normal;
			foreach ( var v in face.Vertices )
			{
				foreach ( var plane in brush.Faces )
				{
					if ( plane is null || ReferenceEquals( plane, face ) )
						continue;
					if ( Vec3.Dot( plane.Normal.Normal, v ) > plane.Distance + PlanarTolerance )
						nonConvex++;
				}
			}
			if ( nonConvex > 0 )
				break;
		}
		if ( nonConvex > 0 )
			defects.Add( "is not convex (a vertex lies outside another face's plane)" );

		var lint = LintBrushMesh( brush );
		if ( lint != null && !lint.Ok )
			foreach ( var error in lint.Errors )
				defects.Add( error );
		return defects;
	}

	/// <summary>GeometryLinter reuse: fan-triangulate the brush into
	/// one mesh and ask the linter for watertight / manifold / Euler /
	/// inverted / degenerate verdicts. Returns null when the brush has
	/// nothing triangulable (the geometric checks above already
	/// reported that).</summary>
	public static LintResult LintBrushMesh( IssueBrush brush )
	{
		if ( brush is null || brush.Faces is null )
			return null;
		var vertices = new List<Vec3>();
		var indexOf = new Dictionary<Vec3, int>();
		var triangles = new List<int[]>();
		foreach ( var face in brush.Faces )
		{
			if ( face is null || face.Vertices is null || face.Vertices.Count < 3 )
				continue;
			var ids = new List<int>();
			foreach ( var v in face.Vertices )
			{
				if ( !indexOf.TryGetValue( v, out var id ) )
				{
					id = vertices.Count;
					vertices.Add( v );
					indexOf[v] = id;
				}
				ids.Add( id );
			}
			for ( var k = 1; k + 1 < ids.Count; k++ )
				triangles.Add( new[] { ids[0], ids[k], ids[k + 1] } );
		}
		if ( triangles.Count == 0 )
			return null;
		return GeometryLinter.Lint( new LintMesh( vertices.ToArray(), triangles.ToArray() ) );
	}
}

/// <summary>Missing material: a face with no material id. One issue
/// per face. An unknown-but-present material id is NOT this issue —
/// the material bridge owns registry lookups.</summary>
public sealed class MissingMaterialRule : IIssueRule
{
	public const string RuleId = "missing-material";
	public string Id => RuleId;
	public string Name => "Missing material";
	public IssueScope Scope => IssueScope.Brush;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.MissingMaterial };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null || document.Brushes is null )
			return issues;
		foreach ( var brush in document.Brushes )
		{
			if ( brush is null || brush.Faces is null )
				continue;
			for ( var i = 0; i < brush.Faces.Count; i++ )
			{
				var face = brush.Faces[i];
				if ( face is null || !string.IsNullOrWhiteSpace( face.Material ) )
					continue;
				issues.Add( new Issue
				{
					Kind = IssueKind.MissingMaterial,
					Severity = IssueSeverity.Error,
					Code = "MISSING_MATERIAL",
					BrushId = brush.Id,
					Location = face.Centroid(),
					HasLocation = face.Vertices.Count > 0,
					Message = $"Brush {brush.Id} face {i} has no material",
					FixHint = "Assign a material; use nodraw for faces the player never sees, then flag the face for review.",
				} );
			}
		}
		return issues;
	}
}

/// <summary>Missing target: an entity whose "target" (or "killtarget")
/// key names a targetname no entity has. One issue per dangling
/// reference, naming the source entity and the missing name.</summary>
public sealed class MissingTargetRule : IIssueRule
{
	public const string RuleId = "missing-target";
	public string Id => RuleId;
	public string Name => "Missing target";
	public IssueScope Scope => IssueScope.Entity;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.MissingTarget };

	/// <summary>Keyvalue keys that reference another entity by
	/// targetname. Entity I/O (hammer-entity-io-logic-list-model)
	/// references the same names from its own lists; this rule only
	/// reads plain keyvalues.</summary>
	public static readonly string[] TargetKeys = { "target", "killtarget" };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null || document.Entities is null )
			return issues;
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entity in document.Entities )
		{
			if ( entity is null )
				continue;
			var name = entity.TargetName;
			if ( !string.IsNullOrWhiteSpace( name ) )
				names.Add( name );
		}
		foreach ( var entity in document.Entities )
		{
			if ( entity is null || entity.KeyValues is null )
				continue;
			foreach ( var key in TargetKeys )
			{
				var target = entity.GetKeyValue( key );
				// "!"-prefixed values are special targets (!self, !player,
				// ...), not entity targetnames — MapEntities.Validate()
				// skips them too.
				if ( string.IsNullOrWhiteSpace( target ) || target.StartsWith( "!", StringComparison.Ordinal ) || names.Contains( target ) )
					continue;
				issues.Add( new Issue
				{
					Kind = IssueKind.MissingTarget,
					Severity = IssueSeverity.Error,
					Code = "MISSING_TARGET",
					EntityId = entity.Id,
					Location = entity.Origin,
					HasLocation = true,
					Message = $"Entity {entity.Id} ('{entity.ClassName}') targets missing '{target}' (key '{key}')",
					FixHint = $"Create the target entity stub with targetname '{target}', or retarget to an existing entity.",
				} );
			}
		}
		return issues;
	}
}

/// <summary>Duplicate target name: two or more entities share one
/// targetname. Warning (per the MapEntities spec), one issue per
/// duplicated name, naming every holder.</summary>
public sealed class DuplicateTargetNameRule : IIssueRule
{
	public const string RuleId = "duplicate-targetname";
	public string Id => RuleId;
	public string Name => "Duplicate target name";
	public IssueScope Scope => IssueScope.Entity;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.DuplicateTargetName };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null || document.Entities is null )
			return issues;
		var byName = new Dictionary<string, List<IssueEntity>>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entity in document.Entities )
		{
			if ( entity is null )
				continue;
			var name = entity.TargetName;
			if ( string.IsNullOrWhiteSpace( name ) )
				continue;
			if ( !byName.TryGetValue( name, out var holders ) )
			{
				holders = new List<IssueEntity>();
				byName[name] = holders;
			}
			holders.Add( entity );
		}
		foreach ( var pair in byName )
		{
			if ( pair.Value.Count < 2 )
				continue;
			var ids = string.Join( ", ", pair.Value.Select( e => e.Id.ToString( CultureInfo.InvariantCulture ) ) );
			issues.Add( new Issue
			{
				Kind = IssueKind.DuplicateTargetName,
				Severity = IssueSeverity.Warning,
				Code = "DUPLICATE_TARGETNAME",
				EntityId = pair.Value[0].Id,
				Location = pair.Value[0].Origin,
				HasLocation = true,
				Message = $"targetname '{pair.Key}' is used by {pair.Value.Count} entities (ids {ids})",
				FixHint = "Rename all but one of the duplicates; each targetname must be unique.",
			} );
		}
		return issues;
	}
}

/// <summary>Entity with no class: an entity whose classname is empty.
/// One issue per entity.</summary>
public sealed class EntityNoClassRule : IIssueRule
{
	public const string RuleId = "entity-no-class";
	public string Id => RuleId;
	public string Name => "Entity with no class";
	public IssueScope Scope => IssueScope.Entity;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.EntityNoClass };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null || document.Entities is null )
			return issues;
		foreach ( var entity in document.Entities )
		{
			if ( entity is null || !string.IsNullOrWhiteSpace( entity.ClassName ) )
				continue;
			issues.Add( new Issue
			{
				Kind = IssueKind.EntityNoClass,
				Severity = IssueSeverity.Error,
				Code = "ENTITY_NO_CLASS",
				EntityId = entity.Id,
				Location = entity.Origin,
				HasLocation = true,
				Message = $"Entity {entity.Id} has no classname",
				FixHint = "Set a classname or delete the entity.",
			} );
		}
		return issues;
	}
}

/// <summary>Zero-scale prop: a prop entity (prop_static / prop_dynamic /
/// prop_physics) with a zero scale component cannot be lit or placed — its
/// orientation on that axis is undefined. One error per prop, naming the axis.
/// Mirrored (negative-determinant) scales are NOT issues: the lighting path
/// corrects them (see PropLighting in LightmapBake.cs). Non-prop entities are
/// never flagged: a zero scale on a light or a trigger is meaningless, not
/// broken. The scale comes from <see cref="IssueEntity.Scale"/> (copied from
/// MapEntity.Scale by the document projection); the default (1, 1, 1) never
/// trips this rule.</summary>
public sealed class ZeroScalePropRule : IIssueRule
{
	public const string RuleId = "zero-scale-prop";
	public string Id => RuleId;
	public string Name => "Zero-scale prop";
	public IssueScope Scope => IssueScope.Entity;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.ZeroScaleProp };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null || document.Entities is null )
			return issues;
		foreach ( var entity in document.Entities )
		{
			if ( entity is null || !IsPropClass( entity.ClassName ) )
				continue;
			var axes = ZeroScaleAxes( entity.Scale );
			if ( axes.Count == 0 )
				continue;
			issues.Add( new Issue
			{
				Kind = IssueKind.ZeroScaleProp,
				Severity = IssueSeverity.Error,
				Code = "ZERO_SCALE_PROP",
				EntityId = entity.Id,
				Location = entity.Origin,
				HasLocation = true,
				Message = $"Entity {entity.Id} ('{entity.ClassName}') has zero scale on axis {string.Join( ", ", axes )} (scale {entity.Scale.x}, {entity.Scale.y}, {entity.Scale.z}): a zero-scale prop cannot be lit",
				FixHint = "Set a non-zero scale on every axis (MapEntities.SetScale), or remove the prop.",
			} );
		}
		return issues;
	}

	static bool IsPropClass( string className ) =>
		!string.IsNullOrWhiteSpace( className ) && className.StartsWith( "prop_", StringComparison.OrdinalIgnoreCase );

	static IReadOnlyList<string> ZeroScaleAxes( Vec3 scale )
	{
		var axes = new List<string>();
		if ( scale.x == 0f ) axes.Add( "x" );
		if ( scale.y == 0f ) axes.Add( "y" );
		if ( scale.z == 0f ) axes.Add( "z" );
		return axes;
	}
}

/// <summary>Leak: a ray from the player start escapes the map through
/// a gap between sealing brushes. One issue per gap (escape points
/// within <see cref="LeakProbe.ClusterRadius"/> merge). Escapes with
/// no sealing face nearby are NOT leaks — the
/// <see cref="UnsealedMapRule"/> reports those as an unsealed map.
/// Stand-in until LeakFinder lands; see the file header.</summary>
public sealed class LeakRule : IIssueRule
{
	public const string RuleId = "leak";
	public string Id => RuleId;
	public string Name => "Leak";
	public IssueScope Scope => IssueScope.Global;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.Leak };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		foreach ( var escape in LeakProbe.Probe( document ) )
		{
			if ( !escape.Gapped )
				continue;
			issues.Add( new Issue
			{
				Kind = IssueKind.Leak,
				Severity = IssueSeverity.Error,
				Code = "LEAK",
				Location = escape.Exit,
				HasLocation = true,
				Message = $"Map leaks to the void {escape.Direction}: gap near {escape.Exit}",
				FixHint = "Close the gap at the reported location; do not seal the whole map in a skybox box.",
			} );
		}
		return issues;
	}
}

/// <summary>Unsealed map: the map has no enclosing shell at all on
/// some side — a whole wall / floor / ceiling is missing, or there are
/// no solid brushes. One issue per run, listing every open direction.
/// A map with a small gap is a leak, not unsealed: the two rules are
/// mutually exclusive per escape by construction.</summary>
public sealed class UnsealedMapRule : IIssueRule
{
	public const string RuleId = "unsealed-map";
	public string Id => RuleId;
	public string Name => "Unsealed map";
	public IssueScope Scope => IssueScope.Global;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.UnsealedMap };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var open = new List<LeakEscape>();
		foreach ( var escape in LeakProbe.Probe( document ) )
			if ( !escape.Gapped )
				open.Add( escape );
		if ( open.Count == 0 )
			return new List<Issue>();
		var directions = string.Join( ", ", open.Select( e => e.Direction.ToString() ) );
		return new List<Issue>
		{
			new Issue
			{
				Kind = IssueKind.UnsealedMap,
				Severity = IssueSeverity.Error,
				Code = "UNSEALED_MAP",
				Location = open[0].Exit,
				HasLocation = true,
				Message = $"Map is not sealed: the void is reachable {directions}; nearest open gap near {open[0].Exit}",
				FixHint = "Add the missing wall / floor / ceiling brushes on the reported side(s); a leak through a gap is reported separately as a leak.",
			},
		};
	}
}

/// <summary>Compiler limits: brush / face / entity / vertex counts
/// over the document's limits. One issue per exceeded limit, naming
/// the count, the limit, and (for faces / vertices) the biggest
/// contributing brushes.</summary>
public sealed class LimitRule : IIssueRule
{
	public const string RuleId = "limits";
	public string Id => RuleId;
	public string Name => "Compiler limits";
	public IssueScope Scope => IssueScope.Global;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.LimitExceeded };

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null )
			return issues;
		var limits = document.Limits ?? new IssueLimits();
		var brushes = document.Brushes ?? new List<IssueBrush>();
		var entities = document.Entities ?? new List<IssueEntity>();
		var faces = 0;
		var vertices = 0;
		var faceCountByBrush = new List<(int id, int faces, int vertices)>();
		foreach ( var brush in brushes )
		{
			if ( brush is null )
				continue;
			var brushFaces = brush.Faces != null ? brush.Faces.Count : 0;
			var brushVertices = 0;
			if ( brush.Faces != null )
				foreach ( var face in brush.Faces )
					if ( face != null && face.Vertices != null )
						brushVertices += face.Vertices.Count;
			faces += brushFaces;
			vertices += brushVertices;
			faceCountByBrush.Add( (brush.Id, brushFaces, brushVertices) );
		}
		if ( brushes.Count > limits.MaxBrushes )
			issues.Add( LimitIssue( $"brush count {brushes.Count} exceeds the limit {limits.MaxBrushes}",
				"Remove or merge brushes to get under the limit; detail brushes count too." ) );
		if ( faces > limits.MaxFaces )
			issues.Add( LimitIssue( $"face count {faces} exceeds the limit {limits.MaxFaces} ({Biggest( faceCountByBrush, b => b.faces )})",
				"Remove or merge brushes to get under the limit; the biggest contributors are listed." ) );
		if ( entities.Count > limits.MaxEntities )
			issues.Add( LimitIssue( $"entity count {entities.Count} exceeds the limit {limits.MaxEntities}",
				"Remove entities or merge logic into fewer entities to get under the limit." ) );
		if ( vertices > limits.MaxVertices )
			issues.Add( LimitIssue( $"vertex count {vertices} exceeds the limit {limits.MaxVertices} ({Biggest( faceCountByBrush, b => b.vertices )})",
				"Remove or merge brushes to get under the limit; the biggest contributors are listed." ) );
		return issues;
	}

	static Issue LimitIssue( string what, string fixHint ) => new Issue
	{
		Kind = IssueKind.LimitExceeded,
		Severity = IssueSeverity.Error,
		Code = "LIMIT_EXCEEDED",
		HasLocation = false,
		Message = what,
		FixHint = fixHint,
	};

	static string Biggest( List<(int id, int faces, int vertices)> byBrush, Func<(int id, int faces, int vertices), int> count )
	{
		var top = byBrush
			.OrderByDescending( count )
			.Take( 3 )
			.Where( b => count( b ) > 0 )
			.Select( b => $"brush #{b.id} ({count( b )})" )
			.ToArray();
		return top.Length == 0 ? "no contributing brush" : "biggest: " + string.Join( ", ", top );
	}
}

/// <summary>
/// Giant skybox: a 2D skybox box — brushes whose faces are ALL the sky
/// material (tools/toolsskybox) — vastly larger than the map bounds
/// (hammer-compile-safety-net-gate, beginner lore: do not seal the whole
/// map in a giant skybox box). One warning per map, naming the largest
/// skybox brush and the size ratio. A skybox box up to
/// <see cref="GiantSkyboxRatio"/>x the map's longest side is fine (a
/// snug enclosing box is legitimate); beyond that it is a warning, never
/// an error — the map still compiles.
///
/// The 3D skybox section is NOT this rule's business: section brushes
/// live in SkyboxWorldspawn (skybox space, scaled by SectionScale) and
/// never appear in the IssueDocument, so legitimate distant scenery can
/// never trip it. Detail brushes count as map brushes for the bounds
/// (they are real map geometry); a sky-material detail brush counts as
/// a skybox brush.
/// </summary>
public sealed class GiantSkyboxRule : IIssueRule
{
	public const string RuleId = "giant-skybox";
	public string Id => RuleId;
	public string Name => "Giant skybox";
	public IssueScope Scope => IssueScope.Global;
	public IReadOnlyList<IssueKind> Kinds => new[] { IssueKind.GiantSkybox };

	/// <summary>The sky material: a brush is a skybox brush when EVERY face uses it.</summary>
	public const string SkyMaterial = "tools/toolsskybox";

	/// <summary>
	/// Longest-side ratio above which a skybox box is "vastly larger"
	/// than the map: the skybox box's longest side must be strictly
	/// greater than this multiple of the map's longest side to warn
	/// (4x on a side is 64x the volume). At exactly 4x there is no issue.
	/// </summary>
	public const double GiantSkyboxRatio = 4.0;

	public IReadOnlyList<Issue> Check( IssueDocument document )
	{
		var issues = new List<Issue>();
		if ( document is null || document.Brushes is null )
			return issues;
		var skybox = new List<IssueBrush>();
		var map = new List<IssueBrush>();
		foreach ( var brush in document.Brushes )
		{
			if ( brush is null || brush.Faces is null || brush.Faces.Count == 0 )
				continue;
			if ( IsSkyboxBrush( brush ) )
				skybox.Add( brush );
			else
				map.Add( brush );
		}
		if ( skybox.Count == 0 || map.Count == 0 )
			return issues;
		if ( !TryBounds( skybox, out var skyMin, out var skyMax ) || !TryBounds( map, out var mapMin, out var mapMax ) )
			return issues;
		var skyLongest = LongestSide( skyMin, skyMax );
		var mapLongest = LongestSide( mapMin, mapMax );
		if ( !( mapLongest > 0 ) || !( skyLongest > GiantSkyboxRatio * mapLongest ) )
			return issues;
		var biggest = skybox[0];
		var biggestLongest = 0.0;
		foreach ( var brush in skybox )
		{
			if ( !TryBounds( new[] { brush }, out var bMin, out var bMax ) )
				continue;
			var side = LongestSide( bMin, bMax );
			if ( side > biggestLongest )
			{
				biggestLongest = side;
				biggest = brush;
			}
		}
		var ratio = skyLongest / mapLongest;
		var centre = ( skyMin + skyMax ) * 0.5f;
		issues.Add( new Issue
		{
			Kind = IssueKind.GiantSkybox,
			Severity = IssueSeverity.Warning,
			Code = "GIANT_SKYBOX",
			BrushId = biggest.Id,
			Location = centre,
			HasLocation = true,
			Message = string.Format( CultureInfo.InvariantCulture,
				"Skybox box (brush {0}) is {1:0.#}x the map bounds ({2:0.#} vs {3:0.#} units on the longest side) — a giant skybox box",
				biggest.Id, ratio, skyLongest, mapLongest ),
			FixHint = "Shrink the skybox box to just enclose the playable map, or use a 3D skybox section (sky_camera) for distant scenery instead of a giant 2D skybox box.",
		} );
		return issues;
	}

	static bool IsSkyboxBrush( IssueBrush brush )
	{
		foreach ( var face in brush.Faces )
		{
			if ( face is null || !string.Equals( face.Material, SkyMaterial, StringComparison.OrdinalIgnoreCase ) )
				return false;
		}
		return true;
	}

	static bool TryBounds( IReadOnlyList<IssueBrush> brushes, out Vec3 min, out Vec3 max )
	{
		min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		var any = false;
		foreach ( var brush in brushes )
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
					any = true;
				}
			}
		}
		return any;
	}

	static double LongestSide( Vec3 min, Vec3 max ) =>
		Math.Max( max.x - min.x, Math.Max( max.y - min.y, max.z - min.z ) );
}

/// <summary>One ray escape found by <see cref="LeakProbe"/>: the
/// direction the ray left in, where it left the map's bounds, and
/// whether sealing faces sit near the exit (a gap) or not (open).</summary>
public sealed class LeakEscape
{
	public Vec3 Direction;
	public Vec3 Exit;
	public bool Gapped;
}

/// <summary>Geometric leak stand-in (see the file header): 6 axis rays
/// from the player start against the solid (non-detail) brushes.
/// LeakFinder (hammer-19) replaces this with a flood-fill over the
/// BSP portal graph; until then this is what the Leak / UnsealedMap
/// rules run on, and it says so in every message it produces only
/// through the rules — the probe itself just reports escapes.</summary>
public static class LeakProbe
{
	/// <summary>Escape points closer than this (inches) are one gap.</summary>
	public const double ClusterRadius = 64.0;
	/// <summary>A sealing face within this distance of an escape point
	/// means the ray left through a gap between brushes; farther than
	/// this, the side is simply open.</summary>
	public const double GapProximity = 128.0;
	/// <summary>Rays stop here; a ray with no solid hit by then has
	/// escaped to the void.</summary>
	public const double MaxRayDistance = 32768.0;

	static readonly Vec3[] AxisDirections =
	{
		new Vec3( 1, 0, 0 ), new Vec3( -1, 0, 0 ),
		new Vec3( 0, 1, 0 ), new Vec3( 0, -1, 0 ),
		new Vec3( 0, 0, 1 ), new Vec3( 0, 0, -1 ),
	};

	/// <summary>Player start origin, or null when the map has no
	/// info_player_start. Without one there is nothing to flood from
	/// and no leak verdict — the rules report nothing.</summary>
	public static Vec3? PlayerStart( IssueDocument document )
	{
		if ( document is null || document.Entities is null )
			return null;
		foreach ( var entity in document.Entities )
			if ( entity != null && string.Equals( entity.ClassName, "info_player_start", StringComparison.OrdinalIgnoreCase ) )
				return entity.Origin;
		return null;
	}

	/// <summary>Escapes from the player start, clustered by exit
	/// point. Empty when there is no player start or nothing escapes.</summary>
	public static IReadOnlyList<LeakEscape> Probe( IssueDocument document )
	{
		var escapes = new List<LeakEscape>();
		var start = PlayerStart( document );
		if ( start is null )
			return escapes;
		var origin = start.Value;
		var solids = new List<IssueBrush>();
		if ( document.Brushes != null )
			foreach ( var brush in document.Brushes )
				if ( brush != null && !brush.IsDetail )
					solids.Add( brush );
		foreach ( var direction in AxisDirections )
		{
			if ( RayBlocked( origin, direction, solids ) )
				continue;
			var exit = RayExitPoint( origin, direction, solids );
			var gapped = IsGapped( origin, direction, exit, solids );
			var merged = false;
			foreach ( var existing in escapes )
			{
				if ( ( existing.Exit - exit ).Length <= ClusterRadius )
				{
					existing.Exit = ( existing.Exit + exit ) / 2f;
					existing.Gapped = existing.Gapped || gapped;
					merged = true;
					break;
				}
			}
			if ( !merged )
				escapes.Add( new LeakEscape { Direction = direction, Exit = exit, Gapped = gapped } );
		}
		return escapes;
	}

	static bool RayBlocked( Vec3 origin, Vec3 direction, List<IssueBrush> solids )
	{
		foreach ( var brush in solids )
			if ( RayHitsBrush( origin, direction, brush ) )
				return true;
		return false;
	}

	/// <summary>Slab test against the convex brush: the solid is the
	/// intersection of its face half-spaces.</summary>
	static bool RayHitsBrush( Vec3 origin, Vec3 direction, IssueBrush brush )
	{
		if ( brush.Faces is null || brush.Faces.Count == 0 )
			return false;
		var tEnter = 0.0;
		var tExit = MaxRayDistance;
		foreach ( var face in brush.Faces )
		{
			if ( face is null )
				return false;
			var normal = face.Normal.Normal;
			var along = Vec3.Dot( normal, direction );
			var outside = Vec3.Dot( normal, origin ) - face.Distance;
			if ( Math.Abs( along ) < 1e-9 )
			{
				if ( outside > 0 )
					return false;
				continue;
			}
			var t = -outside / along;
			if ( along < 0 )
			{
				if ( t > tEnter )
					tEnter = t;
			}
			else if ( t < tExit )
			{
				tExit = t;
			}
			if ( tEnter > tExit )
				return false;
		}
		return tExit >= 0 && tEnter <= MaxRayDistance;
	}

	static Vec3 RayExitPoint( Vec3 origin, Vec3 direction, List<IssueBrush> solids )
	{
		var hasBounds = false;
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var brush in solids )
		{
			if ( brush.Faces is null )
				continue;
			foreach ( var face in brush.Faces )
			{
				if ( face is null || face.Vertices is null )
					continue;
				foreach ( var v in face.Vertices )
				{
					min = new Vec3( MathF.Min( min.x, v.x ), MathF.Min( min.y, v.y ), MathF.Min( min.z, v.z ) );
					max = new Vec3( MathF.Max( max.x, v.x ), MathF.Max( max.y, v.y ), MathF.Max( max.z, v.z ) );
					hasBounds = true;
				}
			}
		}
		if ( !hasBounds )
			return origin + direction * 1024f;
		const float expand = 64f;
		min -= new Vec3( expand, expand, expand );
		max += new Vec3( expand, expand, expand );
		var tFar = double.PositiveInfinity;
		for ( var axis = 0; axis < 3; axis++ )
		{
			var o = axis == 0 ? origin.x : axis == 1 ? origin.y : origin.z;
			var d = axis == 0 ? direction.x : axis == 1 ? direction.y : direction.z;
			var lo = axis == 0 ? min.x : axis == 1 ? min.y : min.z;
			var hi = axis == 0 ? max.x : axis == 1 ? max.y : max.z;
			if ( Math.Abs( d ) < 1e-9 )
			{
				if ( o < lo || o > hi )
					return origin + direction * 1024f;
				continue;
			}
			var t1 = ( lo - o ) / d;
			var t2 = ( hi - o ) / d;
			tFar = Math.Min( tFar, Math.Max( t1, t2 ) );
		}
		if ( double.IsInfinity( tFar ) || tFar <= 0 )
			return origin + direction * 1024f;
		return origin + direction * (float) tFar;
	}

	/// <summary>A gap, not an open side, when a face that could have
	/// sealed this ray (normal opposed to the ray, plane ahead of the
	/// origin) sits within <see cref="GapProximity"/> of the exit.</summary>
	static bool IsGapped( Vec3 origin, Vec3 direction, Vec3 exit, List<IssueBrush> solids )
	{
		var exitDistance = ( exit - origin ).Length;
		foreach ( var brush in solids )
		{
			if ( brush.Faces is null )
				continue;
			foreach ( var face in brush.Faces )
			{
				if ( face is null )
					continue;
				var normal = face.Normal.Normal;
				if ( Vec3.Dot( normal, direction ) > -0.5f )
					continue;
				var along = Vec3.Dot( normal, direction );
				if ( Math.Abs( along ) < 1e-9 )
					continue;
				var tCross = ( face.Distance - Vec3.Dot( normal, origin ) ) / along;
				if ( tCross <= 0 || tCross >= exitDistance )
					continue;
				if ( DistanceToFacePolygon( exit, face ) <= GapProximity )
					return true;
			}
		}
		return false;
	}

	static double DistanceToFacePolygon( Vec3 point, IssueFace face )
	{
		if ( face.Vertices is null || face.Vertices.Count == 0 )
			return double.PositiveInfinity;
		if ( face.Vertices.Count < 3 )
		{
			var nearest = double.PositiveInfinity;
			foreach ( var v in face.Vertices )
				nearest = Math.Min( nearest, ( point - v ).Length );
			return nearest;
		}
		var normal = face.Normal.Normal;
		var planeDistance = Math.Abs( Vec3.Dot( normal, point ) - face.Distance );
		var foot = point - normal * (float) ( Vec3.Dot( normal, point ) - face.Distance );
		if ( PointInConvexPolygon( foot, face, normal ) )
			return planeDistance;
		var edgeNearest = double.PositiveInfinity;
		for ( var i = 0; i < face.Vertices.Count; i++ )
		{
			var a = face.Vertices[i];
			var b = face.Vertices[( i + 1 ) % face.Vertices.Count];
			edgeNearest = Math.Min( edgeNearest, PointSegmentDistance( point, a, b ) );
		}
		return edgeNearest;
	}

	static bool PointInConvexPolygon( Vec3 point, IssueFace face, Vec3 normal )
	{
		for ( var i = 0; i < face.Vertices.Count; i++ )
		{
			var a = face.Vertices[i];
			var b = face.Vertices[( i + 1 ) % face.Vertices.Count];
			if ( Vec3.Dot( Vec3.Cross( b - a, point - a ), normal ) < -1e-4f )
				return false;
		}
		return true;
	}

	static double PointSegmentDistance( Vec3 point, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var lengthSquared = ab.LengthSquared;
		if ( lengthSquared <= 1e-12f )
			return ( point - a ).Length;
		var t = Vec3.Dot( point - a, ab ) / lengthSquared;
		t = MathF.Max( 0f, MathF.Min( 1f, t ) );
		return ( point - ( a + ab * t ) ).Length;
	}
}
