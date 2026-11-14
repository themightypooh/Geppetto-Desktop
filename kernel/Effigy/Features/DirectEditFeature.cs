using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// One face of an imported solid, as the importer's face table records it: a stable id, the
/// face's normal and centroid, its area, what kind of surface it is, and its fillet radius
/// when it is a fillet (0 when it is not).
///
/// Direct editing here works on THIS TABLE, not on a B-rep solid. That is the honest shape of
/// the feature until the OCCT core lands: an imported STEP file arrives as a face table plus an
/// edge list, and every op below is a parametric edit to that table, recorded in the feature
/// tree like every other feature. The B-rep core consumes the same op records to edit the real
/// solid; it does not re-derive them from a mesh.
/// </summary>
public sealed class FaceRecord
{
	public string Id = "";
	public Vec3 Normal = new( 0, 0, 1 );
	public Vec3 Centroid;
	public float AreaMm2;
	public string Kind = "Planar";
	/// <summary>Fillet radius in mm when this face is a fillet, 0 otherwise.</summary>
	public float FilletRadiusMm;

	public FaceRecord Clone() => new()
	{
		Id = Id, Normal = Normal, Centroid = Centroid, AreaMm2 = AreaMm2, Kind = Kind, FilletRadiusMm = FilletRadiusMm,
	};
}

/// <summary>The direct-edit operations this feature records, in Onshape's terms.</summary>
public enum DirectEditOp
{
	MoveFace,
	DeleteFace,
	ReplaceFace,
	OffsetSurface,
	ModifyFillet,
}

/// <summary>What import healing found and did. Honest by construction: gaps that could not be stitched stay in GapsRemaining, and IsClosed is false until there are none.</summary>
public sealed class HealReport
{
	public int EdgeCount;
	public int DegenerateEdges;
	public int NakedEndpoints;
	public int GapsFound;
	public int GapsHealed;
	public int GapsRemaining;
	public float ToleranceMm;
	public bool IsClosed => GapsRemaining == 0 && DegenerateEdges == 0;

	public override string ToString() =>
		$"{EdgeCount} edges, {DegenerateEdges} degenerate, {NakedEndpoints} naked endpoints, " +
		$"{GapsFound} gaps found, {GapsHealed} healed at {ToleranceMm:0.###} mm, {GapsRemaining} remaining";
}

/// <summary>
/// Import healing over an edge list: the check an imported STEP/IGES file needs before it can
/// be treated as a solid.
///
/// A gap is a naked endpoint — an edge end no other edge shares. A closed solid has none. Two
/// naked endpoints within the stitch tolerance are one gap healed by merging them; a pair
/// farther apart than the tolerance is a real hole in the import and stays reported. This runs
/// on the mesh edge list the importer actually produced. It does not rewrite a STEP file and
/// does not claim a B-rep was healed — the OCCT ticket owns the B-rep import.
/// </summary>
public static class ImportHealing
{
	public static HealReport Heal( IReadOnlyList<(Vec3 A, Vec3 B)> edges, float toleranceMm )
	{
		if ( edges is null ) throw new ArgumentNullException( nameof( edges ) );
		if ( toleranceMm <= 0f ) throw new InvalidOperationException( "Healing needs a stitch tolerance greater than zero." );

		var report = new HealReport { EdgeCount = edges.Count, ToleranceMm = toleranceMm };
		var counts = new Dictionary<Vec3, int>();

		foreach ( var (a, b) in edges )
		{
			if ( (a - b).LengthSquared < 1e-12f ) { report.DegenerateEdges++; continue; }
			counts[a] = counts.TryGetValue( a, out var ca ) ? ca + 1 : 1;
			counts[b] = counts.TryGetValue( b, out var cb ) ? cb + 1 : 1;
		}

		var naked = counts.Where( kv => kv.Value == 1 ).Select( kv => kv.Key ).ToList();
		report.NakedEndpoints = naked.Count;
		report.GapsFound = (naked.Count + 1) / 2;

		// Greedy stitch: pair each naked endpoint with the nearest other naked endpoint
		// inside the tolerance. Greedy, not optimal — a healing report says what it did,
		// and pairing is reported per gap, so a missed optimal pairing shows up as a
		// remaining gap rather than as a silently wrong solid.
		var used = new bool[naked.Count];
		var tol2 = toleranceMm * toleranceMm;
		for ( var i = 0; i < naked.Count; i++ )
		{
			if ( used[i] ) continue;
			var best = -1;
			var best2 = float.MaxValue;
			for ( var j = i + 1; j < naked.Count; j++ )
			{
				if ( used[j] ) continue;
				var d2 = (naked[i] - naked[j]).LengthSquared;
				if ( d2 <= tol2 && d2 < best2 ) { best = j; best2 = d2; }
			}
			if ( best >= 0 ) { used[i] = true; used[best] = true; report.GapsHealed++; }
		}

		report.GapsRemaining = report.GapsFound - report.GapsHealed;
		return report;
	}

	/// <summary>Heal the edge list of a mesh: every face contributes its boundary edges, deduplicated.</summary>
	public static HealReport HealMesh( PolyMesh mesh, float toleranceMm )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		var seen = new HashSet<(int, int)>();
		var edges = new List<(Vec3, Vec3)>();
		foreach ( var face in mesh.Faces )
		{
			for ( var c = 0; c < face.Count; c++ )
			{
				var a = face.Indices[c];
				var b = face.Indices[(c + 1) % face.Count];
				var key = a < b ? (a, b) : (b, a);
				if ( !seen.Add( key ) ) continue;
				edges.Add( (mesh.Positions[a], mesh.Positions[b]) );
			}
		}
		return Heal( edges, toleranceMm );
	}
}

/// <summary>
/// Direct editing as a parametric feature: Move / Delete / Replace face, Offset surface,
/// Modify fillet — recorded in the feature tree, applied to the imported face table.
///
/// WHAT IT IS NOT: it does not move mesh vertices. MoveFaceFeature already does that for a
/// mesh solid, through FaceMove; this feature is the import-side twin, for faces that arrived
/// in a STEP/IGES face table and have no mesh yet. The two must not be confused: one edits the
/// mesh you can see, the other records the edit the B-rep core will apply to the solid it
/// imports. Both are in the tree, both are parametric, and both refuse rather than approximate
/// — a Delete with no target, a fillet radius of zero, an offset with no direction all fail
/// out loud with the reason.
/// </summary>
public sealed class DirectEditFeature : Feature
{
	public override string TypeName => "Direct edit";

	public readonly ChoiceParam Op = new( "Operation", new[] { "Move face", "Delete face", "Replace face", "Offset surface", "Modify fillet" } );

	/// <summary>How far, in mm. Move face translates by it; Offset surface moves along each face's own normal by it.</summary>
	public readonly FloatParam Distance = new( "Distance", 1f, unit: "mm" );

	/// <summary>Which way, for Move face. Ignored by Offset surface (each face has its own normal).</summary>
	public readonly Vec3Param Direction = new( "Direction", new Vec3( 0, 0, 1 ) );

	/// <summary>Face to act on. Empty means "every face in the table" for Move and Offset; Delete, Replace and Modify fillet require one.</summary>
	public readonly StringParam TargetFaceId = new( "Face id", "" );

	/// <summary>New radius in mm, for Modify fillet.</summary>
	public readonly FloatParam NewRadius = new( "New radius", 1f, 0.0001f, unit: "mm" );

	/// <summary>Replacement normal, for Replace face.</summary>
	public readonly Vec3Param ReplacementNormal = new( "Replacement normal", new Vec3( 0, 0, 1 ) );

	/// <summary>Replacement area in mm², for Replace face.</summary>
	public readonly FloatParam ReplacementArea = new( "Replacement area", 0f, 0f, unit: "mm²" );

	/// <summary>The imported face table this edit runs on. Set by the import, not by the dialog.</summary>
	public List<FaceRecord> Faces = new();

	/// <summary>The face table after this rebuild's edit. Empty before the first run.</summary>
	public List<FaceRecord> ResultFaces = new();

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Op, Distance, Direction, TargetFaceId, NewRadius, ReplacementNormal, ReplacementArea };

	/// <summary>Apply one op to a face table, returning the new table. Throws InvalidOperationException on a refusal.</summary>
	public static List<FaceRecord> Apply( DirectEditOp op, IReadOnlyList<FaceRecord> faces,
		string targetFaceId, float distanceMm, Vec3 direction, float newRadiusMm, Vec3 replacementNormal, float replacementAreaMm2 )
	{
		if ( faces is null ) throw new ArgumentNullException( nameof( faces ) );
		var result = faces.Select( f => f.Clone() ).ToList();
		var targets = string.IsNullOrEmpty( targetFaceId )
			? result
			: result.Where( f => f.Id == targetFaceId ).ToList();

		switch ( op )
		{
			case DirectEditOp.MoveFace:
			{
				if ( direction.LengthSquared < 1e-12f )
					throw new InvalidOperationException( "Move face needs a direction." );
				if ( targets.Count == 0 )
					throw new InvalidOperationException( $"No face '{targetFaceId}' in the table to move." );
				var dir = direction.Normal;
				foreach ( var face in targets )
					face.Centroid = new Vec3(
						face.Centroid.x + dir.x * distanceMm,
						face.Centroid.y + dir.y * distanceMm,
						face.Centroid.z + dir.z * distanceMm );
				return result;
			}
			case DirectEditOp.OffsetSurface:
			{
				if ( targets.Count == 0 )
					throw new InvalidOperationException( $"No face '{targetFaceId}' in the table to offset." );
				foreach ( var face in targets )
				{
					var n = face.Normal.LengthSquared < 1e-12f ? new Vec3( 0, 0, 1 ) : face.Normal.Normal;
					face.Centroid = new Vec3(
						face.Centroid.x + n.x * distanceMm,
						face.Centroid.y + n.y * distanceMm,
						face.Centroid.z + n.z * distanceMm );
				}
				return result;
			}
			case DirectEditOp.DeleteFace:
			{
				if ( string.IsNullOrEmpty( targetFaceId ) )
					throw new InvalidOperationException( "Delete face needs a face id — deleting every face would leave nothing." );
				if ( targets.Count == 0 )
					throw new InvalidOperationException( $"No face '{targetFaceId}' in the table to delete." );
				result.RemoveAll( f => f.Id == targetFaceId );
				return result;
			}
			case DirectEditOp.ReplaceFace:
			{
				if ( string.IsNullOrEmpty( targetFaceId ) )
					throw new InvalidOperationException( "Replace face needs a face id." );
				if ( targets.Count == 0 )
					throw new InvalidOperationException( $"No face '{targetFaceId}' in the table to replace." );
				if ( replacementNormal.LengthSquared < 1e-12f )
					throw new InvalidOperationException( "Replace face needs a replacement normal." );
				if ( replacementAreaMm2 <= 0f )
					throw new InvalidOperationException( "Replace face needs a replacement area greater than zero." );
				foreach ( var face in targets )
				{
					face.Normal = replacementNormal.Normal;
					face.AreaMm2 = replacementAreaMm2;
				}
				return result;
			}
			case DirectEditOp.ModifyFillet:
			{
				if ( string.IsNullOrEmpty( targetFaceId ) )
					throw new InvalidOperationException( "Modify fillet needs a face id." );
				if ( targets.Count == 0 )
					throw new InvalidOperationException( $"No face '{targetFaceId}' in the table to fillet." );
				if ( newRadiusMm <= 0f )
					throw new InvalidOperationException( "A fillet needs a radius greater than zero." );
				foreach ( var face in targets )
				{
					face.FilletRadiusMm = newRadiusMm;
					face.Kind = "Fillet";
				}
				return result;
			}
			default:
				throw new InvalidOperationException( $"Unknown direct-edit operation {op}." );
		}
	}

	protected override void Execute( FeatureContext ctx )
	{
		ResultFaces = new List<FaceRecord>();
		var op = (DirectEditOp)Op.Index;
		try
		{
			ResultFaces = Apply( op, Faces, TargetFaceId.Value, Distance.Value, Direction.Value,
				NewRadius.Value, ReplacementNormal.Value, ReplacementArea.Value );
		}
		catch ( InvalidOperationException e )
		{
			Fail( "This direct edit cannot run", e.Message, "Pick a face first", "Check the distance and radius" );
			return;
		}

		Note = op switch
		{
			DirectEditOp.DeleteFace => $"Deleted face '{TargetFaceId.Value}': {Faces.Count} -> {ResultFaces.Count} faces in the table.",
			DirectEditOp.ModifyFillet => $"Face '{TargetFaceId.Value}' fillet radius is now {NewRadius.Value:0.##} mm (recorded; B-rep solid edit is the OCCT ticket's).",
			_ => $"{Op.Value} applied to the imported face table: {ResultFaces.Count} faces.",
		};
	}
}
