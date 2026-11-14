using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Fit Citizen's skeleton INTO a mesh, instead of snapping the mesh onto Citizen.
///
/// WHY A WRAPPER AND NOT ANOTHER RETARGET. <see cref="SkeletonRetarget.Fit"/> already fits a
/// source skeleton to a target, and <see cref="Playermodel.Build"/> already calls it with
/// <see cref="CitizenSkeleton.Build()"/> as the target. What that path does not give a caller
/// is a Citizen-only fit that can be reasoned about on its own: given Citizen's table and a
/// cloud of mesh vertices, where do Citizen's joints sit in THIS mesh, with Citizen's roll
/// provably untouched? This is that step, as a pure function over a skeleton and points, so
/// it can be tested headless without a studio, a mesh object, or the editor.
///
/// MEDIAL-AXIS FITTING, NOT SNAPPING. Snapping a bone's head to the nearest vertex, or to a
/// source joint, is what walks a model into a knot: the head lands on the skin, or on a joint
/// from a differently-proportioned rig, and the bone's frame follows it. Here each bone keeps
/// Citizen's frame exactly — the fitted world basis is a copy of Citizen's world basis, column
/// for column, and only the origin moves. What moves it is the mesh's own medial axis: the
/// vertices nearest the bone's segment are binned along that segment, each bin's centroid is
/// a point on the axis the flesh actually wraps around, and the fitted head is Citizen's head
/// projected onto the line through those centroids. A tube shifted sideways moves the joint
/// sideways by the same amount and not at all along the bone; a tube centred on Citizen's
/// axis leaves the joint where Citizen put it, to float noise.
///
/// WHAT COUNTS AS A BODY BONE. Everything hanging off `pelvis`. Auxiliary bones — twists,
/// helpers, IK targets, holds, face widgets, clothing bones, per
/// <see cref="Skeleton.IsAuxiliaryBone"/> — are not fitted: they ride their parent's move
/// exactly, so their Local is bit-preserved (up to float round-trip) and they stay glued to
/// the limb they belong to. The `root_IK` subtree has no body parent, so it is copied
/// unchanged: IK targets are driven, not fitted, and a fit that dragged them would hand the
/// graph targets that no longer match the body. A bone with too few vertices in any bin, or
/// whose fitted head would cross its parent (the direction from parent to child flipping
/// sign), is NOT refitted: it keeps its Citizen offset from its (possibly fitted) parent —
/// riding the parent's move instead of tearing the chain — and is reported in
/// <see cref="FitResult.Unchanged"/> / <see cref="FitResult.Rejected"/> rather than guessed at.
///
/// COMMAND SHAPE (modelled here, NOT wired here). The script verb this is written for is
/// <c>rig citizen fit=Body</c> — see <see cref="ParseCommand"/> and
/// <see cref="CommandShape"/>. Wiring that verb into BuildScript, and registering this
/// test file, is coordinator work: this file edits nothing and registers nothing. The
/// caller that does wire it should follow <see cref="Playermodel.Build"/>'s order — fit,
/// then <see cref="TwistWeights.Spread"/> over the fitted skeleton (twist distribution is
/// deliberately NOT duplicated here), then depth-first ordering — passing
/// <c>mesh.Positions</c> as <paramref name="Fit"/> 's points.
/// </summary>
public static class CitizenFit
{
	/// <summary>The one fit target the command names today. Other targets, if they ever
	/// exist, extend this enum and <see cref="ParseCommand"/> together.</summary>
	public enum CitizenFitTarget { Body }

	/// <summary>The script shape this wrapper models: <c>rig citizen fit=Body</c>.</summary>
	public static string CommandShape => "rig citizen fit=Body";

	/// <summary>Parse <c>rig citizen fit=Body</c>. Case-insensitive on the words; anything
	/// else — a missing target, an unknown target, extra tokens — throws, so a mistyped
	/// verb fails loudly instead of silently fitting the wrong thing.</summary>
	public static CitizenFitTarget ParseCommand( string command )
	{
		if ( string.IsNullOrWhiteSpace( command ) )
			throw new ArgumentException( $"Citizen fit needs a command like '{CommandShape}'.", nameof( command ) );

		var tokens = command.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries );

		if ( tokens.Length != 3
			|| !tokens[0].Equals( "rig", StringComparison.OrdinalIgnoreCase )
			|| !tokens[1].Equals( "citizen", StringComparison.OrdinalIgnoreCase )
			|| !tokens[2].StartsWith( "fit=", StringComparison.OrdinalIgnoreCase ) )
			throw new ArgumentException( $"Not a citizen fit command — expected '{CommandShape}', got '{command}'.", nameof( command ) );

		var target = tokens[2]["fit=".Length..];

		if ( target.Equals( "Body", StringComparison.OrdinalIgnoreCase ) )
			return CitizenFitTarget.Body;

		throw new ArgumentException( $"Unknown citizen fit target '{target}' — expected 'Body'.", nameof( command ) );
	}

	/// <summary>Tuning for <see cref="Fit"/>. The defaults are the ones the tests pin.</summary>
	public sealed class FitOptions
	{
		/// <summary>How far down a segment the first (joint) bin reaches, as a fraction of
		/// segment length. 0.25 keeps the joint bin near the joint instead of averaging
		/// half the limb into it.</summary>
		public float ProximalFraction = 0.25f;

		/// <summary>Vertices a bin needs before it counts. Fewer than this and the bin is
		/// ignored rather than trusted — a single stray vertex is not a medial axis.</summary>
		public int MinBinVertices = 3;

		/// <summary>How far a head may move, as a multiple of its own segment length. A
		/// fit further than that is not a fit, it is a different joint, and is rejected.</summary>
		public float MaxMoveMultiple = 1f;
	}

	public sealed class FitResult
	{
		/// <summary>Citizen's skeleton with fitted heads, same names, parents and order,
		/// same lengths, Citizen's bases copied column for column.</summary>
		public Skeleton Skeleton;

		public int FittedBones => Fitted.Count;
		public int UnchangedBones => Unchanged.Count;

		/// <summary>Bones whose head was refitted from the mesh.</summary>
		public List<string> Fitted = new();

		/// <summary>Bones not directly refitted: no coverage, too few bin vertices, or
		/// rejected by a guard. "Unchanged" means the head was not refitted from the
		/// mesh — a child of a fitted bone still rides its parent's move through the
		/// hierarchy, keeping its Citizen offset from that parent.</summary>
		public List<string> Unchanged = new();

		/// <summary>Bones whose mesh fit was computed and then refused — a move further
		/// than <see cref="FitOptions.MaxMoveMultiple"/> segment lengths, or a move that
		/// would flip the parent-to-child direction. These are also in
		/// <see cref="Unchanged"/>.</summary>
		public List<string> Rejected = new();
	}

	/// <summary>Fit <paramref name="citizen"/> into <paramref name="points"/> (mesh
	/// vertices in the same space as the skeleton's world bind pose). The input skeleton
	/// is never mutated: the result is a fresh skeleton built from fitted world frames.
	/// An empty point list fits nothing and throws nothing.</summary>
	public static FitResult Fit( Skeleton citizen, IReadOnlyList<Vec3> points, FitOptions options = null )
	{
		ArgumentNullException.ThrowIfNull( citizen );
		ArgumentNullException.ThrowIfNull( points );

		options ??= new FitOptions();

		if ( options.ProximalFraction <= 0f || options.ProximalFraction > 0.5f )
			throw new ArgumentException( "ProximalFraction must be in (0, 0.5].", nameof( options ) );
		if ( options.MinBinVertices < 1 )
			throw new ArgumentException( "MinBinVertices must be at least 1.", nameof( options ) );
		if ( options.MaxMoveMultiple <= 0f )
			throw new ArgumentException( "MaxMoveMultiple must be positive.", nameof( options ) );

		var pelvis = citizen.IndexOf( "pelvis" );

		if ( pelvis < 0 )
			throw new ArgumentException( "CitizenFit needs a skeleton with a 'pelvis' root.", nameof( citizen ) );

		// Original world frames first, all of them, so no fitted bone is ever measured
		// against a half-rebuilt hierarchy.
		var origWorld = new Xform[citizen.Count];
		var origHead = new Vec3[citizen.Count];

		for ( var i = 0; i < citizen.Count; i++ )
		{
			origWorld[i] = citizen.WorldBind( i );
			origHead[i] = origWorld[i].Origin;
		}

		var isBody = new bool[citizen.Count];

		for ( var i = 0; i < citizen.Count; i++ )
		{
			for ( var p = i; p >= 0; p = citizen.Bones[p].Parent )
			{
				if ( p == pelvis )
				{
					isBody[i] = true;
					break;
				}
			}
		}

		// Segments for the bones that can be fitted, from Citizen's own geometry — the
		// geometric head-to-child span, NOT the frame's +Y, because Citizen's bones run
		// +X down the bone and +Y would measure every twist and swing along the wrong
		// axis. TryBodySpan already knows this; a fan joint (pelvis, hand) keeps its
		// authored tail, and a bone with no usable span simply is not a candidate.
		var segment = new (Vec3 Head, Vec3 Tail, Vec3 Dir, float Length)?[citizen.Count];

		for ( var i = 0; i < citizen.Count; i++ )
		{
			if ( !isBody[i] || Skeleton.IsAuxiliaryBone( citizen.Bones[i].Name ) )
				continue;

			if ( !citizen.TryBodySpan( i, out var head, out var tail ) )
				continue;

			var span = tail - head;
			var len = span.Length;

			if ( len < 1e-3f )
				continue;

			segment[i] = (head, tail, span / len, len);
		}

		// Nearest-segment assignment, once, against Citizen's segments. A vertex belongs
		// to the bone whose flesh it is actually inside, not to the bone whose name a
		// painter guessed.
		var assigned = new List<Vec3>[citizen.Count];

		for ( var i = 0; i < citizen.Count; i++ )
			assigned[i] = new List<Vec3>();

		foreach ( var p in points )
		{
			var best = -1;
			var bestDist = float.MaxValue;

			for ( var i = 0; i < citizen.Count; i++ )
			{
				if ( segment[i] is not (var head, var tail, _, _) )
					continue;

				var d = PointSegmentDistanceSquared( p, head, tail );

				if ( d < bestDist )
				{
					bestDist = d;
					best = i;
				}
			}

			if ( best >= 0 )
				assigned[best].Add( p );
		}

		// Bin edges along the segment, as fractions of its length. The first bin is the
		// joint bin; the later bins are what the medial axis line is fitted through.
		// The first bin starts slightly before the head because geometry rides above a
		// joint (a shoulder cap sits above the shoulder bone's head).
		var edges = new[] { -0.10f, options.ProximalFraction, 0.60f, 1.10f };

		var result = new FitResult();
		var fittedWorld = new Xform[citizen.Count];
		var fittedHead = new Vec3[citizen.Count];

		for ( var i = 0; i < citizen.Count; i++ )
		{
			var name = citizen.Bones[i].Name;
			var parent = citizen.Bones[i].Parent;

			// A bone that is not itself refitted still rides its parent's move, so a
			// fitted parent never tears the chain apart from its child. Parents
			// precede children, so the parent's fitted head is already known here.
			Vec3 RideParent() => parent >= 0
				? origHead[i] + (fittedHead[parent] - origHead[parent])
				: origHead[i];

			Vec3? head;

			if ( isBody[i] && !Skeleton.IsAuxiliaryBone( name ) && segment[i] is (var segHead, _, var dir, var segLen) )
			{
				head = FitHead( citizen, i, segHead, dir, segLen, assigned[i], edges, options.MinBinVertices );

				if ( head.HasValue )
				{
					var candidate = head.Value;
					var rejected = false;

					if ( (candidate - origHead[i]).Length > options.MaxMoveMultiple * segLen )
						rejected = true;

					// The knot guard. A fitted child must still sit on the same side of
					// its fitted parent that Citizen put it on; a move that flips that
					// direction is a bone walking into a knot, and is refused even
					// though the mesh points asked for it.
					if ( !rejected && parent >= 0 && isBody[parent] )
					{
						var origVec = origHead[i] - origHead[parent];
						var newVec = candidate - fittedHead[parent];

						if ( origVec.LengthSquared > 1e-6f && Vec3.Dot( newVec, origVec ) <= 0f )
							rejected = true;
					}

					if ( rejected )
					{
						result.Rejected.Add( name );
						head = RideParent();
						result.Unchanged.Add( name );
					}
					else
					{
						result.Fitted.Add( name );
					}
				}
				else
				{
					head = RideParent();
					result.Unchanged.Add( name );
				}
			}
			else if ( Skeleton.IsAuxiliaryBone( name ) && parent >= 0 )
			{
				// Ride the parent's move exactly. This preserves the auxiliary bone's
				// Local (up to float round-trip).
				head = RideParent();
				result.Unchanged.Add( name );
			}
			else
			{
				head = origHead[i];
				result.Unchanged.Add( name );
			}

			fittedHead[i] = head.Value;

			// Rotation copied, origin replaced. This is the whole roll guarantee: no
			// frame in the result was ever recomputed from geometry.
			var world = new Xform( origWorld[i].X, origWorld[i].Y, origWorld[i].Z, fittedHead[i] );
			fittedWorld[i] = world;

			if ( MathF.Sign( world.Determinant ) != MathF.Sign( origWorld[i].Determinant ) )
				throw new InvalidOperationException( $"CitizenFit flipped the handedness of '{name}' — this cannot happen with a copied basis." );
		}

		var skeleton = new Skeleton();

		for ( var i = 0; i < citizen.Count; i++ )
		{
			var bone = citizen.Bones[i];
			var local = bone.Parent < 0 ? fittedWorld[i] : fittedWorld[bone.Parent].Inverse * fittedWorld[i];
			var idx = skeleton.AddBone( bone.Name, bone.Parent, local, bone.Length );
			skeleton.Bones[idx].Soft = bone.Soft?.Clone();
		}

		result.Skeleton = skeleton;
		return result;
	}

	/// <summary>The fitted head for one bone, or null when the mesh says nothing usable
	/// about it. Bin centroids along the segment define the medial axis; with two or
	/// more bins the axis is the line through the first and last centroids, with one
	/// bin it is the segment's own direction through that centroid. Either way the
	/// fitted head is Citizen's head projected onto that line — lateral correction
	/// only, no drift along the bone.</summary>
	static Vec3? FitHead( Skeleton citizen, int index, Vec3 segHead, Vec3 dir, float segLen,
		List<Vec3> points, float[] edges, int minBinVertices )
	{
		var centroids = new List<Vec3>();

		for ( var b = 0; b < edges.Length - 1; b++ )
		{
			var lo = edges[b] * segLen;
			var hi = edges[b + 1] * segLen;
			var sum = Vec3.Zero;
			var count = 0;

			foreach ( var p in points )
			{
				var along = Vec3.Dot( p - segHead, dir );

				if ( along < lo || along > hi )
					continue;

				sum += p;
				count++;
			}

			if ( count >= minBinVertices )
				centroids.Add( sum / count );
		}

		if ( centroids.Count == 0 )
			return null;

		var origHead = citizen.WorldBind( index ).Origin;

		if ( centroids.Count == 1 )
		{
			var c = centroids[0];
			return c + dir * Vec3.Dot( origHead - c, dir );
		}

		var first = centroids[0];
		var last = centroids[^1];
		var axis = last - first;

		if ( axis.LengthSquared < 1e-8f )
			return first + dir * Vec3.Dot( origHead - first, dir );

		axis /= axis.Length;

		if ( Vec3.Dot( axis, dir ) < 0f )
			axis = -axis;

		return first + axis * Vec3.Dot( origHead - first, axis );
	}

	static float PointSegmentDistanceSquared( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var lenSq = ab.LengthSquared;

		if ( lenSq < 1e-12f )
			return (p - a).LengthSquared;

		var t = Vec3.Dot( p - a, ab ) / lenSq;
		t = t < 0f ? 0f : t > 1f ? 1f : t;

		return (p - (a + ab * t)).LengthSquared;
	}
}
