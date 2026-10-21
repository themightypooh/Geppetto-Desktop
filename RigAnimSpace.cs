#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Reconciles a clip's authoring space with the rig it is played on.
///
/// s&box authors .riganim bone translations in centimetres. The desktop rig is in inches,
/// because the FBX import scales the mesh and the rig together (an Effigy unit is an inch).
/// Applying a centimetre clip to an inch rig puts every translation 2.54x too far out: the
/// arms leave the body and the skeleton fans apart.
///
/// The factor is measured from the rig itself rather than guessed from file metadata, so
/// both authoring conventions work. Bones whose clip translation length is constant across
/// their track keep their bind length (a pose rotates a bone, it does not stretch it), which
/// makes the ratio clip-length / bind-length a direct read of the unit difference. Bones
/// with animated translations are skipped: they can change length on purpose.
///
/// A BONE THAT DISAGREES IS NOT STRETCHED. Some clips were authored against a rig whose
/// arm chain is not the one in the mesh file. Applying such a translation to this rig
/// stretches the mesh, which looks like a broken model rather than a wrong animation. So a
/// bone whose translation does not match its bind length keeps its bind translation and
/// takes only the clip's rotation, and the mismatch is reported.
///
/// Only translations are scaled. Rotations and scales are unit free.
///
/// A CLIP CAN USE A SHALLOWER HIERARCHY THAN THE MESH FILE. s&box's model compiler drops
/// bones that carry no skin weight (a root, clavicles, an unweighted arm_lower, twist
/// controllers), and a .riganim stores each kept bone relative to its nearest kept
/// ancestor, or to model space when none is left. The elbow bend of a culled arm_lower is
/// then baked into the hand's translation. Played against the mesh file's own parents those
/// locals crumple the arm. <see cref="Detect"/> recognises this from the bind pose (keyed
/// bones whose clip offset matches their bind offset from the nearest keyed ancestor) and
/// returns the clip-side parent of each bone, plus the yaw between s&box model space and
/// this rig's model space, which the compiler's axis convention can turn by 90 degrees.
/// </summary>
public static class RigAnimSpace
{
	public const float CentimetresPerInch = 2.54f;
	/// <summary>A ratio this close to 1 means the clip and the rig share a unit.</summary>
	const float SameUnitTolerance = 0.05f;
	/// <summary>A ratio this close to 2.54 means the clip is in centimetres.</summary>
	const float CentimetreTolerance = 0.15f;
	/// <summary>How far a bone's ratio may sit from the measured factor and still agree.</summary>
	const float AgreementTolerance = 0.1f;
	const int MinimumSamples = 3;

	/// <summary>Scale for the clip's bone translations, plus what it was based on.</summary>
	public sealed record Result( float PositionScale, string Note, int Samples, float MedianRatio, bool[] TranslationAgrees, int DisagreeingBones )
	{
		public bool Resolved => Samples >= MinimumSamples;
		/// <summary>Per bone, the bone the clip's local is relative to (-1 = clip model
		/// space). Null when the clip uses this rig's own parents.</summary>
		public int[]? ClipParent { get; init; }
		/// <summary>Maps the clip's model space into this rig's model space.</summary>
		public Xform RigFromClipModel { get; init; } = Xform.Identity;
	}

	/// <summary>
	/// Measures the unit ratio between <paramref name="clip"/> and
	/// <paramref name="rig"/>, and which bones agree with the rig. Returns 1 when the two
	/// agree, and 1/2.54 when the clip is in centimetres and the rig in inches.
	/// </summary>
	public static Result Detect( Skeleton rig, RigAnimFile clip )
	{
		if ( rig is null ) throw new ArgumentNullException( nameof( rig ) );
		if ( clip is null ) throw new ArgumentNullException( nameof( clip ) );

		var agrees = new bool[rig.Count];
		Array.Fill( agrees, true ); // Untracked and root translations remain authored.
		var ratios = new List<double>( 32 );
		foreach ( var track in clip.Tracks )
		{
			if ( track.Kind != RigTrackKind.Bone || track.Keys.Count == 0 ) continue;
			int bone = rig.IndexOf( track.Name );
			if ( bone < 0 || rig.Bones[bone].Parent < 0 ) continue;
			double bind = Length( rig.Bones[bone].Local.Origin );
			if ( bind < 1e-3 ) continue;
			var first = track.Evaluate( track.Keys[0].Frame ).Position;
			double firstLength = Length( first );
			if ( firstLength < 1e-3 ) continue;
			// A track that animates its own translation can change length on purpose.
			if ( !ConstantLength( track, firstLength ) ) continue;
			ratios.Add( firstLength / bind );
		}
		if ( ratios.Count < MinimumSamples )
		{
			var parents = DetectCulledHierarchy( rig, clip, 1f, out var fromClip, out int witnesses );
			var unresolved = "no comparable bone translations; clip left in its own units";
			if ( parents is not null )
				unresolved += $"; clip skeleton omits unweighted bones ({witnesses} bind offsets confirm), keyed bones follow the clip's parents";
			return new Result( 1f, unresolved, ratios.Count, 0, agrees, 0 ) { ClipParent = parents, RigFromClipModel = fromClip };
		}

		// The unit factor is the ratio most bones agree on. A clip authored against a
		// different rig can have outliers; cluster the consistent offsets first.
		double factor = MostCommonRatio( ratios );
		float scale;
		string note;
		if ( Math.Abs( factor - 1 ) <= SameUnitTolerance )
		{
			scale = 1f;
			note = "clip and rig share a unit";
		}
		else if ( Math.Abs( factor - CentimetresPerInch ) <= CentimetreTolerance )
		{
			scale = 1f / CentimetresPerInch;
			note = "clip translations are centimetres and the rig is inches; scaled by 1/2.54";
		}
		else
		{
			scale = 1f;
			note = $"clip unit ratio {factor:F3} is neither 1 nor 2.54; clip left in its own units";
		}

		var hierarchy = DetectCulledHierarchy( rig, clip, scale, out var rigFromClip, out int culledMatches );
		foreach ( var track in clip.Tracks )
		{
			if ( track.Kind != RigTrackKind.Bone || track.Keys.Count == 0 ) continue;
			int bone = rig.IndexOf( track.Name );
			if ( bone < 0 || rig.Bones[bone].Parent < 0 || scale == 1f ) continue;
			// A bone re-parented past a culled joint carries that joint's rotation in its
			// translation, so its length legitimately differs from the bind offset.
			if ( hierarchy is not null && hierarchy[bone] != rig.Bones[bone].Parent ) continue;
			double bind = Length( rig.Bones[bone].Local.Origin );
			double firstLength = Length( track.Evaluate( track.Keys[0].Frame ).Position );
			// A root/zero initial offset can deliberately move. A foreign chain that
			// already disagrees at its first key must not stretch, even if it animates.
			agrees[bone] = firstLength < 1e-3 || (bind >= 1e-3
				&& Math.Abs( firstLength * scale / bind - 1 ) <= AgreementTolerance);
		}
		int disagreeing = 0;
		foreach ( var track in clip.Tracks )
		{
			if ( track.Kind != RigTrackKind.Bone || track.Keys.Count == 0 ) continue;
			int bone = rig.IndexOf( track.Name );
			if ( bone < 0 || bone >= agrees.Length ) continue;
			if ( !agrees[bone] ) disagreeing++;
		}
		if ( disagreeing > 0 )
			note += $"; {disagreeing} tracked bone(s) do not match this rig and keep their bind translation";
		if ( hierarchy is not null )
			note += $"; clip skeleton omits unweighted bones ({culledMatches} bind offsets confirm), keyed bones follow the clip's parents";
		return new Result( scale, note, ratios.Count, (float)factor, agrees, disagreeing ) { ClipParent = hierarchy, RigFromClipModel = rigFromClip };
	}

	/// <summary>
	/// Finds the clip-side parent of each keyed bone when the clip was authored on the
	/// compiled (weight-culled) skeleton. Every keyed bone whose rig parent is not keyed is a
	/// witness: its first-key translation is compared with its bind offset from the rig
	/// parent and with its bind offset from the nearest keyed ancestor (or from model space,
	/// under each of the four yaws). The culled reading wins only when some witness confirms
	/// it and none confirms the rig's own parents. Witnesses whose two readings coincide, and
	/// animated ones matching neither, are not evidence either way.
	/// </summary>
	static int[]? DetectCulledHierarchy( Skeleton rig, RigAnimFile clip, float scale, out Xform rigFromClip, out int matches )
	{
		rigFromClip = Xform.Identity;
		matches = 0;
		int n = rig.Count;
		var keyed = new bool[n];
		var first = new Vec3[n];
		foreach ( var track in clip.Tracks )
		{
			if ( track.Kind != RigTrackKind.Bone || track.Keys.Count == 0 ) continue;
			int bone = rig.IndexOf( track.Name );
			if ( bone < 0 ) continue;
			keyed[bone] = true;
			var p = track.Evaluate( track.Keys[0].Frame ).Position;
			first[bone] = new Vec3( p.x * scale, p.y * scale, p.z * scale );
		}
		var parent = new int[n];
		var world = new Xform[n];
		bool differs = false;
		for ( int b = 0; b < n; b++ )
		{
			int p = rig.Bones[b].Parent;
			world[b] = p >= 0 ? world[p] * rig.Bones[b].Local : rig.Bones[b].Local;
			int a = p;
			while ( a >= 0 && !keyed[a] ) a = rig.Bones[a].Parent;
			parent[b] = keyed[b] ? a : p;
			if ( keyed[b] && a != p ) differs = true;
		}
		if ( !differs ) return null;

		int bestCulled = 0, bestRig = 0, bestYaw = 0;
		for ( int yaw = 0; yaw < 4; yaw++ )
		{
			var clipFromRig = Yaw( yaw );
			int culled = 0, own = 0;
			for ( int b = 0; b < n; b++ )
			{
				int p = rig.Bones[b].Parent;
				if ( !keyed[b] || parent[b] == p ) continue;
				var ownOffset = rig.Bones[b].Local.Origin;
				var culledOffset = parent[b] >= 0
					? (world[parent[b]].Inverse * world[b]).Origin
					: clipFromRig.TransformDirection( world[b].Origin );
				if ( Near( ownOffset, culledOffset ) ) continue;
				if ( Near( first[b], culledOffset ) ) culled++;
				else if ( Near( first[b], ownOffset ) ) own++;
			}
			if ( culled > bestCulled ) { bestCulled = culled; bestRig = own; bestYaw = yaw; }
		}
		if ( bestCulled == 0 || bestRig > 0 ) return null;
		matches = bestCulled;
		rigFromClip = Yaw( (4 - bestYaw) % 4 );
		return parent;
	}

	/// <summary>An exact quarter-turn yaw about +Z (s&box and the rig are both Z-up).</summary>
	static Xform Yaw( int quarterTurns )
	{
		var (c, s) = (quarterTurns & 3) switch { 0 => (1f, 0f), 1 => (0f, 1f), 2 => (-1f, 0f), _ => (0f, -1f) };
		return new Xform( new Vec3( c, s, 0 ), new Vec3( -s, c, 0 ), new Vec3( 0, 0, 1 ), new Vec3( 0, 0, 0 ) );
	}

	static bool Near( Vec3 a, Vec3 b )
	{
		double d = Length( new Vec3( a.x - b.x, a.y - b.y, a.z - b.z ) );
		return d <= 0.01 + 0.01 * Length( b );
	}

	/// <summary>The ratio the most bones share, found by clustering and then taking that
	/// cluster's mean. One bone's oddity cannot move the unit factor.</summary>
	static double MostCommonRatio( List<double> ratios )
	{
		var sorted = new List<double>( ratios );
		sorted.Sort();
		double bestStart = sorted[0], bestCount = 0, bestSum = 0;
		for ( int i = 0; i < sorted.Count; i++ )
		{
			int count = 0;
			double sum = 0;
			for ( int j = i; j < sorted.Count; j++ )
			{
				if ( sorted[j] - sorted[i] > 0.02 * sorted[i] ) break;
				count++;
				sum += sorted[j];
			}
			if ( count > bestCount ) { bestCount = count; bestStart = sorted[i]; bestSum = sum; }
		}
		return bestCount > 0 ? bestSum / bestCount : bestStart;
	}

	static bool ConstantLength( RigTrack track, double firstLength )
	{
		// Checking only the endpoints mistakes an out-and-back translation for a
		// rigid bone offset. Every authored key must support a length inference.
		foreach ( var key in track.Keys )
			if ( Math.Abs( Length( key.Local.Position ) - firstLength ) > 1e-4 ) return false;
		return true;
	}

	static double Length( Vec3 v ) => Math.Sqrt( v.x * v.x + v.y * v.y + v.z * v.z );
}
