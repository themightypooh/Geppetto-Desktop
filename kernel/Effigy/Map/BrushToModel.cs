using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Effigy.Map;

// BrushToModel — Propper++: brush-built detail becomes a model.
// Board: hammer-propper-brush-to-model (Geppetto Desktop, Hammer P2).
//
// WHAT IT DOES. Given a brush selection (PolyMesh solids + a material-name
// table for their Face.Material slots), produce a native model asset — one
// merged mesh with a UV-island atlas, a baked ambient-occlusion map, and a
// physics/collision mesh — registered in a ModelRegistry, plus the
// prop_static entity that references it. Non-uniform scale is supported
// (applied about the selection's AABB centre, so the prop stays in place).
// The source brushes are replaced by the prop by default, or kept, per the
// caller's KeepSourceBrushes flag.
//
// PIPELINE (each stage is the existing kernel service, not a reimplementation):
//   bake     — MapBrush overload: MapBakeExport.Bake (the real bake
//              path), which tessellates the brushes, drops nodraw faces and
//              preserves material names. PolyMesh overload: the same
//              contract by direct concatenation, for MapCsg-style callers
//              whose solids are already PolyMeshes. Overlapping brushes are
//              concatenated, NOT boolean-unioned: for a true union pre-merge
//              convex pairs with MapCsg.TryMerge. Face-sharing/disjoint
//              selections have exact volumes; partially overlapping
//              selections overcount, and the result says so in its notes,
//              not by silently fixing it.
//   scale    — non-uniform scale about the AABB centre.
//   atlas    — UVUnwrap.Unwrap over the merged mesh (one atlas, one scale,
//              so texel density is uniform across the prop).
//   AO       — BakeJob, AmbientOcclusion only, low == high (self-bake: the
//              prop occludes itself). Small default size/rays: a prop bake
//              is a detail bake, not a hero bake; callers raise AoSize/AoRays.
//   physics  — one exact convex hull per source brush, via
//              ConvexDecomp with a single cluster (Target/Min/MaxHulls
//              all 1), so hull volumes match brush volumes. The merged
//              point-cloud decomposition is NOT used: clustering the merged
//              vertices under/over-covers boxy brush geometry (measured
//              2026-11-13: a 2x2x2 box decomposed to one 2.667 tetrahedron
//              instead of 8; an L-trim to a single 6.5 hull instead of 5).
//              A single non-convex brush gets its convex hull (over-cover);
//              split non-convex selections into convex brushes first.
//   NO WELD, NO UNION. The merged mesh keeps the internal faces where
//              two brushes share one (the bar's top under the riser's
//              bottom): removing them would be a boolean union, and a
//              non-convex selection like the trim cannot be one brush
//              anyway (MapCsg.TryMerge refuses it as merge-not-convex).
//              So the merged mesh is NOT a MeshValidator-closed solid
//              when brushes share faces — the bake path's per-face-corner
//              vertices make it a soup, and welding the corners instead
//              makes the junction edges shared by three faces. Volume,
//              atlas and AO are exact regardless, and collision comes
//              from the per-brush hulls, not from the merged mesh. A
//              selection of disjoint brushes IS closed (each box keeps its
//              own closed shell).
//   register — ModelRegistry.Register; a taken name is a named refusal, never
//              a silent overwrite.
//   prop     — PropEntity (prop_static) with model name, origin = selection
//              centre, and the applied scale.
//
// RESULTS ARE DATA, NOT EXCEPTIONS. A refused conversion returns a
// BrushToModelResult with Ok false and a machine-readable Reason slug plus a
// human Message. Invalid input is a named reason, never a silent fix.
//   brush-to-model-input-null       the brush list (or one brush) was null
//   brush-to-model-input-empty      the list was empty, or a brush has no faces
//   brush-to-model-input-open       a brush is not a closed solid
//   brush-to-model-input-invalid    a MapBrush has a geometry defect
//   brush-to-model-bake-failed      the map bake threw / returned no mesh
//   brush-to-model-bad-name         ModelName is blank
//   brush-to-model-bad-scale        a scale component is <= 0 or not finite
//   brush-to-model-bad-density      DensityKgPerCubicInch is negative / NaN
//   brush-to-model-bad-ao-size      AoSize is not a power of two in 16..8192
//   brush-to-model-bad-ao-rays      AoRays is < 1
//   brush-to-model-bad-surface-prop SurfaceProp is not flesh/metal/wood
//   brush-to-model-bad-unwrap-angle UnwrapAngleDegrees is outside (0, 180]
//   brush-to-model-bad-material-slot a face's material slot has no name
//   brush-to-model-all-nodraw       every face was nodraw: nothing to model
//   brush-to-model-no-volume        the merged mesh encloses no volume
//   brush-to-model-atlas-failed     UVUnwrap threw or produced no charts
//   brush-to-model-atlas-out-of-bounds an atlas UV landed outside 0..1
//   brush-to-model-ao-failed        the AO bake failed or was cancelled
//   brush-to-model-ao-empty         the AO bake filled no texel
//   brush-to-model-physics-failed   ConvexDecomp produced no hull
//   brush-to-model-name-taken       the registry already holds that name
//
// DEPENDENCY STATUS (2026-11-13; MapBakeExport landed 16:07 and MapBrush
// 16:08, both AFTER this ticket started — the notes below were updated
// when they landed, and the MapBrush overload was added to use them).
//   * MapBrush — LANDED (kernel/Effigy/Map/MapBrush.cs, namespace Effigy):
//     MapBrushFace (unit outward normal + distance + FaceMaterial),
//     FromPlanes / FromVertices / Box / Validate() / ToMesh(). The
//     MapBrush overload below is the primary entry point: it bakes through
//     MapBakeExport.Bake, the real bake path.
//   * MapBakeExport — LANDED (kernel/Effigy/Map/MapBakeExport.cs,
//     namespace Effigy): Bake(IReadOnlyList<MapBrush>, displacements,
//     options) -> MapBakeMesh { Mesh, Materials, ExcludedNoDrawFaces,
//     Issues }. The MapBrush overload consumes it directly.
//   * MapDocument — still NOT landed (no MapDocument.cs on disk). The map
//     document that will own the source-brush replacement does not exist
//     yet; the result reports RemovedBrushCount / KeptBrushCount for it.
//   * ModelRegistry — no canonical registry exists anywhere in the tree
//     (grep: zero hits); it belongs to hammer-model-particle-sound-
//     registries, still todo with no files claimed. The ModelRegistry below
//     is the interim home this ticket's acceptance ("a registered model")
//     needs: same contract (register by name, refuse a taken name, lookup
//     by name), in Effigy.Map so a later Effigy-level registry cannot
//     collide with it. The integrator re-points registration at the
//     canonical registry when it lands.
//   * FaceMaterial — LANDED: nodraw is decided through
//     ToolTexture.IsNoDraw on the material name, which is
//     FaceMaterial.ContributesToBakedMesh's rule.
//   * Shared-tree breakage (not mine to fix): MapClip.cs and
//     MapBrushPrimitives.cs reference a MapFace type MapBrush does NOT
//     define (it defines MapBrushFace) — the shared tree currently fails
//     to compile (9x CS0246); the owners/integrator reconcile the name.
//     BrushToModel never mentions MapFace.
//
// DOCUMENT OWNERSHIP. BrushToModel is document-agnostic: it cannot remove
// brushes from a MapDocument that does not exist yet. It reports
// RemovedBrushCount / KeptBrushCount; the map document applies the removal
// (or keeps the brushes when KeepSourceBrushes is set). The input brushes
// are never mutated: the merged mesh is built from copies.
//
// NO DESKTOP UI. Backend / data-model / API / headless only, per Pooh
// 2026-11-13. No Carve operation exists here, ever.

/// <summary>Options for one brush-to-model conversion.</summary>
public sealed class BrushToModelOptions
{
	/// <summary>Registry key and prop model name. Must be non-blank.</summary>
	public string ModelName = "prop";

	/// <summary>Non-uniform scale, applied about the selection's AABB
	/// centre. Every component must be positive and finite.</summary>
	public Vec3 Scale = new Vec3( 1f, 1f, 1f );

	/// <summary>Mass density in kg per cubic inch (kernel units are
	/// inches). MassKg = Volume * this. 0.01 is light wood/plastic order.</summary>
	public float DensityKgPerCubicInch = 0.01f;

	/// <summary>False skips the AO bake; the asset's AoBake is null and
	/// its AO summary values are NaN.</summary>
	public bool BakeAo = true;

	/// <summary>AO map size, square. Power of two, 16..8192. 64 is the
	/// prop default: a detail bake, not a hero bake.</summary>
	public int AoSize = 64;

	/// <summary>Hemisphere rays per AO texel. 8 is the prop default.</summary>
	public int AoRays = 8;

	/// <summary>False skips the physics mesh; Physics is null.</summary>
	public bool BuildPhysics = true;

	/// <summary>Surface property for every physics hull: flesh, metal or
	/// wood. Default flesh (ConvexDecomp's default).</summary>
	public string SurfaceProp = "flesh";

	/// <summary>Unwrap charting angle in degrees (see UVUnwrap.Unwrap).
	/// 66 keeps a cylinder wall whole and still splits a box corner.</summary>
	public float UnwrapAngleDegrees = 66f;

	/// <summary>Gutter between atlas islands, as a fraction of the square.</summary>
	public float AtlasMargin = 0.01f;

	/// <summary>True keeps the source brushes alongside the prop (the
	/// caller then removes nothing); false (default) replaces them, and
	/// the result reports them as removed.</summary>
	public bool KeepSourceBrushes = false;
}

/// <summary>One converted prop: the model asset itself.</summary>
public sealed class BrushModelAsset
{
	/// <summary>Registry key; also the prop's model name.</summary>
	public string ModelName;

	/// <summary>Merged, scaled mesh with the atlas in Face.UVs.</summary>
	public PolyMesh Mesh;

	/// <summary>Material names, indexed by Face.Material slot.</summary>
	public IReadOnlyList<string> Materials;

	/// <summary>What the atlas produced: chart count, faces, stretch.</summary>
	public UnwrapReport Atlas;

	/// <summary>The AO bake set, or null when BakeAo was false.</summary>
	public BakeSet AoBake;

	/// <summary>AO summary over filled texels (R channel, 0..1). NaN when
	/// BakeAo was false.</summary>
	public float AoMean = float.NaN;

	/// <summary>Darkest filled AO texel. NaN when BakeAo was false.</summary>
	public float AoMin = float.NaN;

	/// <summary>Brightest filled AO texel. NaN when BakeAo was false.</summary>
	public float AoMax = float.NaN;

	/// <summary>Fraction of AO texels a ray landed on. 0 when BakeAo was false.</summary>
	public float AoFilledFraction;

	/// <summary>Physics hulls, or null when BuildPhysics was false.</summary>
	public ConvexDecompResult Physics;

	/// <summary>Sum of the physics hull volumes. 0 when BuildPhysics was false.</summary>
	public float PhysicsVolume;

	/// <summary>Enclosed volume of the (scaled) model mesh, cubic inches.</summary>
	public float Volume;

	/// <summary>Volume * DensityKgPerCubicInch, kilograms.</summary>
	public float MassKg;

	/// <summary>Selection AABB centre; also the prop entity origin.</summary>
	public Vec3 Centre;

	/// <summary>Selection AABB corners, after scaling.</summary>
	public Vec3 Min, Max;

	/// <summary>The scale that was applied.</summary>
	public Vec3 Scale;

	/// <summary>How many brushes went in.</summary>
	public int SourceBrushCount;

	/// <summary>Faces that survived into the model mesh.</summary>
	public int FacesKept;

	/// <summary>Faces dropped for being nodraw.</summary>
	public int FacesDroppedNoDraw;
}

/// <summary>The prop_static entity a conversion emits: a plain data record
/// with a JSON round-trip, so a map document can store it and read it back
/// without depending on the model asset being loaded.</summary>
public sealed class PropEntity : IEquatable<PropEntity>
{
	/// <summary>The only class a BrushToModel conversion emits.</summary>
	public const string ClassName = "prop_static";

	/// <summary>Registry key of the model this prop references.</summary>
	public string ModelName;

	/// <summary>Prop origin in map space: the selection's AABB centre.</summary>
	public Vec3 Origin;

	/// <summary>The non-uniform scale applied to the model.</summary>
	public Vec3 Scale = new Vec3( 1f, 1f, 1f );

	/// <summary>Optional targetname; empty means none.</summary>
	public string TargetName = "";

	static string Vec( Vec3 v ) =>
		$"{v.x.ToString( "R", CultureInfo.InvariantCulture )} " +
		$"{v.y.ToString( "R", CultureInfo.InvariantCulture )} " +
		$"{v.z.ToString( "R", CultureInfo.InvariantCulture )}";

	static Vec3 ParseVec( string text, string field )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			throw new ArgumentException( $"Prop entity is missing '{field}'." );
		var parts = text.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		if ( parts.Length != 3 )
			throw new ArgumentException( $"Prop entity '{field}' must be three numbers; got '{text}'." );
		return new Vec3(
			float.Parse( parts[0], CultureInfo.InvariantCulture ),
			float.Parse( parts[1], CultureInfo.InvariantCulture ),
			float.Parse( parts[2], CultureInfo.InvariantCulture ) );
	}

	/// <summary>JSON form: classname / model / origin / scale / targetname.</summary>
	public JsonObject ToJson()
	{
		var o = new JsonObject();
		o["classname"] = ClassName;
		o["model"] = ModelName ?? "";
		o["origin"] = Vec( Origin );
		o["scale"] = Vec( Scale );
		o["targetname"] = TargetName ?? "";
		return o;
	}

	/// <summary>Compact JSON string of <see cref="ToJson"/>.</summary>
	public string ToJsonString() => ToJson().ToJsonString();

	/// <summary>Read back what <see cref="ToJson"/> wrote. Throws
	/// ArgumentException on a missing or malformed field, never a null.</summary>
	public static PropEntity FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var classname = json["classname"]?.GetValue<string>();
		if ( classname != ClassName )
			throw new ArgumentException( $"Prop entity classname must be '{ClassName}'; got '{classname}'." );
		return new PropEntity
		{
			ModelName = json["model"]?.GetValue<string>() ?? "",
			Origin = ParseVec( json["origin"]?.GetValue<string>(), "origin" ),
			Scale = ParseVec( json["scale"]?.GetValue<string>(), "scale" ),
			TargetName = json["targetname"]?.GetValue<string>() ?? "",
		};
	}

	/// <inheritdoc cref="FromJson(JsonObject)"/>
	public static PropEntity FromJsonString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "Prop entity JSON is empty." );
		if ( JsonNode.Parse( json ) is not JsonObject o )
			throw new ArgumentException( "Prop entity JSON is not an object." );
		return FromJson( o );
	}

	public bool Equals( PropEntity other )
	{
		if ( other is null ) return false;
		return ModelName == other.ModelName
			&& Origin.Equals( other.Origin )
			&& Scale.Equals( other.Scale )
			&& ( TargetName ?? "" ) == ( other.TargetName ?? "" );
	}

	public override bool Equals( object obj ) => obj is PropEntity other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( ModelName, Origin, Scale, TargetName );
	public override string ToString() => $"{ClassName} '{ModelName}' at {Vec( Origin )} scale {Vec( Scale )}";
}

/// <summary>Interim model registry: models by name, for BrushToModel
/// conversions. A name registers once; a second registration under the same
/// name is refused (Register returns false), never a silent overwrite.
/// See the mismatch note at the top of this file: the canonical registry
/// belongs to hammer-model-particle-sound-registries (still todo); this is
/// the same contract in Effigy.Map until the integrator re-points it.</summary>
public sealed class ModelRegistry
{
	readonly Dictionary<string, BrushModelAsset> _assets = new( StringComparer.Ordinal );

	/// <summary>How many models are registered.</summary>
	public int Count => _assets.Count;

	/// <summary>Registered names, in registration order.</summary>
	public IReadOnlyCollection<string> Names => _assets.Keys;

	/// <summary>Register an asset. False when the asset or its name is
	/// missing, or the name is already taken — the existing asset is kept.</summary>
	public bool Register( BrushModelAsset asset )
	{
		if ( asset is null || string.IsNullOrWhiteSpace( asset.ModelName ) ) return false;
		if ( _assets.ContainsKey( asset.ModelName ) ) return false;
		_assets[asset.ModelName] = asset;
		return true;
	}

	/// <summary>Look a model up by name. False (asset null) for a missing
	/// or blank name.</summary>
	public bool TryGet( string name, out BrushModelAsset asset )
	{
		asset = null;
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		return _assets.TryGetValue( name, out asset );
	}

	/// <summary>True when a model of this name is registered.</summary>
	public bool Contains( string name ) =>
		!string.IsNullOrWhiteSpace( name ) && _assets.ContainsKey( name );

	/// <summary>Remove a model. False when it was not registered.</summary>
	public bool Remove( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) ) return false;
		return _assets.Remove( name );
	}

	/// <summary>Drop every registration.</summary>
	public void Clear() => _assets.Clear();
}

/// <summary>The outcome of one brush-to-model conversion. Ok false always
/// carries a machine-readable Reason slug and a human Message, and Asset /
/// Prop null — a refusal is never a half-built model.</summary>
public sealed class BrushToModelResult
{
	public BrushToModelResult( bool ok, BrushModelAsset asset, ModelRegistry registry,
		PropEntity prop, string reason, string message, int removedBrushes, int keptBrushes,
		IReadOnlyList<string> notes = null )
	{
		Ok = ok;
		Asset = asset;
		Registry = registry;
		Prop = prop;
		Reason = reason;
		Message = message;
		RemovedBrushCount = removedBrushes;
		KeptBrushCount = keptBrushes;
		Notes = notes ?? Array.Empty<string>();
	}

	public bool Ok { get; }
	public BrushModelAsset Asset { get; }
	public ModelRegistry Registry { get; }
	public PropEntity Prop { get; }
	public string Reason { get; }
	public string Message { get; }
	public int RemovedBrushCount { get; }
	public int KeptBrushCount { get; }

	/// <summary>Bake issues that did not block the conversion (UV defects,
	/// dropped degenerate faces). Empty on the PolyMesh path and on failure.</summary>
	public IReadOnlyList<string> Notes { get; }

	public static BrushToModelResult Succeeded( BrushModelAsset asset, ModelRegistry registry,
		PropEntity prop, int removedBrushes, int keptBrushes, IReadOnlyList<string> notes = null ) =>
		new( true, asset, registry, prop, null, null, removedBrushes, keptBrushes, notes );

	public static BrushToModelResult Failed( string reason, string message,
		ModelRegistry registry, int sourceBrushCount ) =>
		new( false, null, registry, null, reason, message, 0, sourceBrushCount );
}

/// <summary>Brush-to-model conversion (Propper++). Pure: the input brushes
/// are never mutated, and the result reports how many source brushes the
/// caller should remove — the map document owns the actual removal.</summary>
public static class BrushToModel
{
	/// <summary>Convert a brush selection into a registered model asset
	/// plus its prop_static entity.
	/// <paramref name="brushes"/> are the selected solids (closed
	/// PolyMeshes). <paramref name="materialNames"/> maps each face's
	/// Material slot to a material name; null means every face is an
	/// unnamed ordinary material. <paramref name="registry"/> is the
	/// registry the model is registered in; null makes a fresh one, which
	/// the result carries. Never throws for bad input: refusals come back
	/// as a BrushToModelResult with a named Reason.</summary>
	public static BrushToModelResult Convert( IReadOnlyList<PolyMesh> brushes,
		IReadOnlyList<string> materialNames, BrushToModelOptions options = null,
		ModelRegistry registry = null )
	{
		options ??= new BrushToModelOptions();
		registry ??= new ModelRegistry();
		int sourceCount = brushes is null ? 0 : brushes.Count;

		BrushToModelResult Fail( string reason, string message ) =>
			BrushToModelResult.Failed( reason, message, registry, sourceCount );

		if ( brushes is null )
			return Fail( "brush-to-model-input-null", "No brush selection was given." );
		if ( brushes.Count == 0 )
			return Fail( "brush-to-model-input-empty", "The brush selection is empty." );
		var optionProblem = ValidateOptions( options, registry, sourceCount );
		if ( optionProblem != null ) return optionProblem;

		foreach ( var b in brushes )
		{
			if ( b is null )
				return Fail( "brush-to-model-input-null", "One brush in the selection was null." );
			if ( b.FaceCount == 0 )
				return Fail( "brush-to-model-input-empty", "One brush in the selection has no faces." );
			var check = MeshValidator.Validate( b );
			if ( !check.IsClosed )
				return Fail( "brush-to-model-input-open",
					$"One brush is not a closed solid ({check.BoundaryEdges} boundary edge(s)), so it has no inside to model." );
		}

		// Merge: concatenate, dropping nodraw faces. Only vertices a kept
		// face actually uses are copied, so a dropped brush leaves no
		// orphan vertices behind for the physics hulls to trip over.
		var merged = new PolyMesh();
		int kept = 0, dropped = 0;
		var contributes = new bool[brushes.Count];
		for ( int bi = 0; bi < brushes.Count; bi++ )
		{
			var b = brushes[bi];
			var keptFaces = new List<Face>();
			foreach ( var f in b.Faces )
			{
				if ( f.Material < 0 || ( materialNames is not null && f.Material >= materialNames.Count ) )
					return Fail( "brush-to-model-bad-material-slot",
						$"A face uses material slot {f.Material}, but only {( materialNames is null ? 0 : materialNames.Count )} material name(s) were given." );
				string name = materialNames is null ? "" : materialNames[f.Material];
				if ( ToolTexture.IsNoDraw( name ) ) { dropped++; continue; }
				keptFaces.Add( f );
			}
			var remap = new Dictionary<int, int>();
			foreach ( var f in keptFaces )
			foreach ( var vi in f.Indices )
			{
				if ( remap.ContainsKey( vi ) ) continue;
				remap[vi] = merged.Positions.Count;
				merged.AddVertex( b.Positions[vi] );
			}
			foreach ( var f in keptFaces )
			{
				var idx = new int[f.Indices.Length];
				for ( int c = 0; c < idx.Length; c++ ) idx[c] = remap[f.Indices[c]];
				merged.AddFace( idx, (Vec2[])f.UVs.Clone(), f.Material );
				kept++;
			}
			contributes[bi] = keptFaces.Count > 0;
		}
		if ( kept == 0 )
			return Fail( "brush-to-model-all-nodraw",
				"Every face in the selection is nodraw, so there is nothing to model." );
		if ( MathF.Abs( merged.SignedVolume() ) <= 1e-6f )
			return Fail( "brush-to-model-no-volume",
				"The merged brushes enclose no volume." );

		var (physics, physicsVolume, physicsProblem) = BuildPhysics( brushes, contributes, options, registry, sourceCount );
		if ( physicsProblem != null ) return physicsProblem;
		return Finish( merged, sourceCount, kept, dropped, materialNames, options, registry, physics, physicsVolume );
	}

	static (ConvexDecompResult Physics, float Volume, BrushToModelResult Problem) BuildPhysics(
		IReadOnlyList<PolyMesh> brushes, IReadOnlyList<bool> contributes,
		BrushToModelOptions options, ModelRegistry registry, int sourceCount )
	{
		if ( !options.BuildPhysics ) return (null, 0f, null);
		var result = new ConvexDecompResult();
		float volume = 0f;
		for ( int i = 0; i < brushes.Count; i++ )
		{
			if ( !contributes[i] ) continue;
			if ( !TryHullForBrush( brushes[i].Positions, options.SurfaceProp, out var hull, out var error ) )
				return (null, 0f, BrushToModelResult.Failed( "brush-to-model-physics-failed",
					$"Brush {i} produced no physics hull: {error}", registry, sourceCount ));
			result.Hulls.Add( hull );
			volume += hull.Volume;
		}
		if ( result.Hulls.Count == 0 )
			return (null, 0f, BrushToModelResult.Failed( "brush-to-model-physics-failed",
				"No brush contributed a physics hull.", registry, sourceCount ));
		return (result, volume, null);
	}

	static (ConvexDecompResult Physics, float Volume, BrushToModelResult Problem) BuildPhysics(
		IReadOnlyList<MapBrush> brushes, IReadOnlyList<bool> contributes,
		BrushToModelOptions options, ModelRegistry registry, int sourceCount )
	{
		if ( !options.BuildPhysics ) return (null, 0f, null);
		var result = new ConvexDecompResult();
		float volume = 0f;
		for ( int i = 0; i < brushes.Count; i++ )
		{
			if ( !contributes[i] ) continue;
			if ( !TryHullForBrush( brushes[i].Vertices, options.SurfaceProp, out var hull, out var error ) )
				return (null, 0f, BrushToModelResult.Failed( "brush-to-model-physics-failed",
					$"Brush {i} produced no physics hull: {error}", registry, sourceCount ));
			result.Hulls.Add( hull );
			volume += hull.Volume;
		}
		if ( result.Hulls.Count == 0 )
			return (null, 0f, BrushToModelResult.Failed( "brush-to-model-physics-failed",
				"No brush contributed a physics hull.", registry, sourceCount ));
		return (result, volume, null);
	}

	/// <summary>One exact hull for one convex brush: ConvexDecomp with a
	/// single cluster, so the hull is the convex hull of the brush's own
	/// vertices and its volume is the brush's volume.</summary>
	static bool TryHullForBrush( IReadOnlyList<Vec3> points, string surfaceProp,
		out ConvexDecompHull hull, out string error )
	{
		hull = null;
		error = null;
		var r = ConvexDecomp.Decompose( points,
			new ConvexDecomp.Options { TargetHulls = 1, MinHulls = 1, MaxHulls = 1, SurfaceProp = surfaceProp } );
		if ( r.Hulls.Count == 0 )
		{
			error = r.Reason ?? "no hull";
			return false;
		}
		hull = r.Hulls[0];
		return true;
	}

	static BrushToModelResult ValidateOptions( BrushToModelOptions options, ModelRegistry registry, int sourceCount )
	{
		if ( string.IsNullOrWhiteSpace( options.ModelName ) )
			return BrushToModelResult.Failed( "brush-to-model-bad-name", "The model name is blank.", registry, sourceCount );
		if ( !FinitePositive( options.Scale ) )
			return BrushToModelResult.Failed( "brush-to-model-bad-scale",
				$"Scale must have positive finite components; got ({options.Scale.x}, {options.Scale.y}, {options.Scale.z}).", registry, sourceCount );
		if ( !float.IsFinite( options.DensityKgPerCubicInch ) || options.DensityKgPerCubicInch < 0f )
			return BrushToModelResult.Failed( "brush-to-model-bad-density",
				$"Density must be finite and >= 0; got {options.DensityKgPerCubicInch}.", registry, sourceCount );
		if ( options.BakeAo && !IsPow2InRange( options.AoSize, 16, 8192 ) )
			return BrushToModelResult.Failed( "brush-to-model-bad-ao-size",
				$"AO size must be a power of two from 16 to 8192; got {options.AoSize}.", registry, sourceCount );
		if ( options.BakeAo && options.AoRays < 1 )
			return BrushToModelResult.Failed( "brush-to-model-bad-ao-rays",
				$"AO rays per texel must be >= 1; got {options.AoRays}.", registry, sourceCount );
		if ( options.BuildPhysics && !ConvexDecomp.KnownSurfaceProps.Contains( options.SurfaceProp ) )
			return BrushToModelResult.Failed( "brush-to-model-bad-surface-prop",
				$"Surface property must be one of {string.Join( ", ", ConvexDecomp.KnownSurfaceProps )}; got '{options.SurfaceProp}'.", registry, sourceCount );
		if ( options.UnwrapAngleDegrees <= 0f || options.UnwrapAngleDegrees > 180f )
			return BrushToModelResult.Failed( "brush-to-model-bad-unwrap-angle",
				$"Unwrap angle must be in (0, 180]; got {options.UnwrapAngleDegrees}.", registry, sourceCount );
		return null;
	}

	/// <summary>MapBrush defect codes that say something about the SOLID
	/// (and so refuse a conversion). The remaining codes (UV / lightmap /
	/// smoothing-group defects) do not block the conversion: the bake
	/// reports them and they come back on the result's Notes.</summary>
	static readonly HashSet<string> GeometryDefects = new()
	{
		MapBrushDefect.TooFewFaces, MapBrushDefect.DegeneratePlane, MapBrushDefect.Unbounded,
		MapBrushDefect.DegenerateFace, MapBrushDefect.ZeroVolume, MapBrushDefect.NonPlanarFace,
		MapBrushDefect.ConcaveInput, MapBrushDefect.DuplicateFace, MapBrushDefect.CoplanarSplitFace,
	};

	/// <summary>Convert a MapBrush selection into a registered model asset
	/// plus its prop_static entity. This is the primary entry point: the
	/// mesh comes from the bake path (<see cref="MapBakeExport.Bake"/>),
	/// which tessellates the brushes, drops nodraw faces and preserves
	/// material names. A brush with a geometry defect (see
	/// <see cref="MapBrushDefect"/>) is refused with
	/// "brush-to-model-input-invalid" and its defect codes. Never throws
	/// for bad input: refusals come back as a BrushToModelResult with a
	/// named Reason.</summary>
	public static BrushToModelResult Convert( IReadOnlyList<MapBrush> brushes,
		BrushToModelOptions options = null, ModelRegistry registry = null )
	{
		options ??= new BrushToModelOptions();
		registry ??= new ModelRegistry();
		int sourceCount = brushes is null ? 0 : brushes.Count;

		BrushToModelResult Fail( string reason, string message ) =>
			BrushToModelResult.Failed( reason, message, registry, sourceCount );

		if ( brushes is null )
			return Fail( "brush-to-model-input-null", "No brush selection was given." );
		if ( brushes.Count == 0 )
			return Fail( "brush-to-model-input-empty", "The brush selection is empty." );
		var optionProblem = ValidateOptions( options, registry, sourceCount );
		if ( optionProblem != null ) return optionProblem;

		for ( int i = 0; i < brushes.Count; i++ )
		{
			var b = brushes[i];
			if ( b is null )
				return Fail( "brush-to-model-input-null", $"Brush {i} in the selection was null." );
			var bad = new List<string>();
			foreach ( var defect in b.Validate() )
				if ( GeometryDefects.Contains( defect.Code ) )
					bad.Add( defect.ToString() );
			if ( bad.Count > 0 )
				return Fail( "brush-to-model-input-invalid",
					$"Brush {i} is not a valid solid: {string.Join( "; ", bad )}" );
			if ( b.FaceCount < 4 )
				return Fail( "brush-to-model-input-invalid",
					$"Brush {i} has only {b.FaceCount} face(s); a solid needs at least 4." );
		}

		MapBakeMesh baked;
		try
		{
			baked = MapBakeExport.Bake( brushes, null, new MapBakeOptions() );
		}
		catch ( Exception e )
		{
			return Fail( "brush-to-model-bake-failed",
				$"The map bake threw instead of returning a mesh: {e.Message}" );
		}
		if ( baked is null || baked.Mesh is null )
			return Fail( "brush-to-model-bake-failed", "The map bake returned no mesh." );
		if ( baked.Mesh.FaceCount == 0 )
		{
			if ( baked.ExcludedNoDrawFaces > 0 )
				return Fail( "brush-to-model-all-nodraw",
					"Every face in the selection is nodraw, so there is nothing to model." );
			return Fail( "brush-to-model-no-volume",
				"The baked brushes enclose no volume." );
		}
		var contributes = new bool[brushes.Count];
		for ( int i = 0; i < brushes.Count; i++ )
			contributes[i] = brushes[i].Faces.Any( f => f.Material.ContributesToBakedMesh );
		var (physics, physicsVolume, physicsProblem) = BuildPhysics( brushes, contributes, options, registry, sourceCount );
		if ( physicsProblem != null ) return physicsProblem;
		return Finish( baked.Mesh, sourceCount, baked.FaceCount, baked.ExcludedNoDrawFaces,
			baked.Materials, options, registry, physics, physicsVolume, baked.Issues );
	}

	/// <summary>Shared tail of both Convert overloads: scale, atlas, AO,
	/// physics, registration, prop. <paramref name="merged"/> is the
	/// already-merged mesh (bake path or direct concatenation).</summary>
	static BrushToModelResult Finish( PolyMesh merged, int sourceCount, int facesKept, int facesDroppedNoDraw,
		IReadOnlyList<string> materials, BrushToModelOptions options, ModelRegistry registry,
		ConvexDecompResult physics, float physicsVolume, IReadOnlyList<string> notes = null )
	{
		BrushToModelResult Fail( string reason, string message ) =>
			BrushToModelResult.Failed( reason, message, registry, sourceCount );

		if ( MathF.Abs( merged.SignedVolume() ) <= 1e-6f )
			return Fail( "brush-to-model-no-volume",
				"The merged brushes enclose no volume." );

		// Scale about the AABB centre, so the prop stays where the brushes were.
		var (min, max, centre) = Bounds( merged.Positions );
		for ( int i = 0; i < merged.Positions.Count; i++ )
		{
			var p = merged.Positions[i];
			merged.Positions[i] = new Vec3(
				centre.x + ( p.x - centre.x ) * options.Scale.x,
				centre.y + ( p.y - centre.y ) * options.Scale.y,
				centre.z + ( p.z - centre.z ) * options.Scale.z );
		}
		merged.InvalidateVertexCache();
		(min, max, centre) = Bounds( merged.Positions );
		float volume = MathF.Abs( merged.SignedVolume() );

		// Atlas: one unwrap over the merged mesh, at one scale.
		UnwrapReport atlas;
		try
		{
			atlas = UVUnwrap.Unwrap( merged, options.UnwrapAngleDegrees, options.AtlasMargin );
		}
		catch ( Exception e )
		{
			return Fail( "brush-to-model-atlas-failed",
				$"The UV unwrap threw instead of returning an atlas: {e.Message}" );
		}
		if ( atlas.Charts <= 0 )
			return Fail( "brush-to-model-atlas-failed",
				"The UV unwrap produced no charts." );
		foreach ( var f in merged.Faces )
		foreach ( var uv in f.UVs )
		{
			if ( uv.x < -1e-3f || uv.x > 1.001f || uv.y < -1e-3f || uv.y > 1.001f )
				return Fail( "brush-to-model-atlas-out-of-bounds",
					$"An atlas UV landed outside the 0..1 square: ({uv.x}, {uv.y})." );
		}

		// AO: self-bake (low == high), AmbientOcclusion only.
		BakeSet aoBake = null;
		float aoMean = float.NaN, aoMin = float.NaN, aoMax = float.NaN, aoFilled = 0f;
		if ( options.BakeAo )
		{
			BakeJobResult baked;
			try
			{
				var job = new BakeJob(
					new[] { new BakeGroup { Name = options.ModelName, Low = merged, High = merged } },
					BakeMaps.AmbientOcclusion, options.AoSize, options.AoSize,
					new BakeOptions { AoRays = options.AoRays, Supersample = 1 } );
				baked = job.Run();
			}
			catch ( Exception e )
			{
				return Fail( "brush-to-model-ao-failed",
					$"The AO bake threw instead of returning a result: {e.Message}" );
			}
			if ( !baked.Succeeded )
				return Fail( "brush-to-model-ao-failed",
					baked.Cancelled ? "The AO bake was cancelled." : $"The AO bake failed: {baked.Error}" );
			aoBake = baked.Set;
			var map = aoBake.AmbientOcclusion;
			if ( map is null )
				return Fail( "brush-to-model-ao-failed", "The AO bake produced no AO map." );
			int filled = 0;
			double sum = 0;
			float mn = 1f, mx = 0f;
			for ( int i = 0; i < map.Filled.Length; i++ )
			{
				if ( !map.Filled[i] ) continue;
				filled++;
				float v = map.Rgb[i * 3] / 255f;
				sum += v;
				if ( v < mn ) mn = v;
				if ( v > mx ) mx = v;
			}
			if ( filled == 0 )
				return Fail( "brush-to-model-ao-empty",
					"The AO bake filled no texel, so there is no AO to keep." );
			aoMean = (float)( sum / filled );
			aoMin = mn;
			aoMax = mx;
			aoFilled = (float)filled / map.Filled.Length;
		}

		var asset = new BrushModelAsset
		{
			ModelName = options.ModelName,
			Mesh = merged,
			Materials = materials is null ? new List<string>() : materials.ToList(),
			Atlas = atlas,
			AoBake = aoBake,
			AoMean = aoMean,
			AoMin = aoMin,
			AoMax = aoMax,
			AoFilledFraction = aoFilled,
			Physics = physics,
			PhysicsVolume = physicsVolume,
			Volume = volume,
			MassKg = volume * options.DensityKgPerCubicInch,
			Centre = centre,
			Min = min,
			Max = max,
			Scale = options.Scale,
			SourceBrushCount = sourceCount,
			FacesKept = facesKept,
			FacesDroppedNoDraw = facesDroppedNoDraw,
		};

		if ( !registry.Register( asset ) )
			return Fail( "brush-to-model-name-taken",
				$"A model named '{options.ModelName}' is already registered. Pick a unique model name." );

		var prop = new PropEntity
		{
			ModelName = options.ModelName,
			Origin = centre,
			Scale = options.Scale,
		};
		int removed = options.KeepSourceBrushes ? 0 : sourceCount;
		int keptBrushes = options.KeepSourceBrushes ? sourceCount : 0;
		return BrushToModelResult.Succeeded( asset, registry, prop, removed, keptBrushes, notes );
	}

	static bool FinitePositive( Vec3 v ) =>
		float.IsFinite( v.x ) && float.IsFinite( v.y ) && float.IsFinite( v.z )
		&& v.x > 0f && v.y > 0f && v.z > 0f;

	static bool IsPow2InRange( int v, int lo, int hi ) =>
		v >= lo && v <= hi && ( v & ( v - 1 ) ) == 0;

	static (Vec3 Min, Vec3 Max, Vec3 Centre) Bounds( IReadOnlyList<Vec3> positions )
	{
		var min = positions[0];
		var max = positions[0];
		foreach ( var p in positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		return (min, max, ( min + max ) * 0.5f);
	}
}
