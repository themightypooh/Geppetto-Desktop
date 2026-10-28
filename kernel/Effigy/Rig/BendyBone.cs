using System;

namespace Effigy;

/// <summary>Settings for one Blender-style Bendy Bone (B-Bone).</summary>
public sealed class BendyBoneSettings
{
    /// <summary>Segments the bone is divided into for evaluation. Must be >= 1.</summary>
    public int Segments = 8;

    /// <summary>Handle-length multiplier at the head end. 1 = Blender's default ease.</summary>
    public float EaseIn = 1f;

    /// <summary>Handle-length multiplier at the tail end. 1 = Blender's default ease.</summary>
    public float EaseOut = 1f;
}

/// <summary>One evaluated B-Bone segment: its centre position and orientation.</summary>
public readonly record struct BendySegment( Vec3 Position, Quat Rotation );

/// <summary>
/// Blender Bendy Bones (B-Bones): a bone divided into segments that follow a smooth curve
/// instead of one rigid segment, so an arc bends gradually instead of kinking at the joint.
///
/// MODEL, STATED PLAINLY. A B-Bone is a cubic Bezier curve through its two ends:
/// P0 = head, P3 = tail, P1 = head + headDir * (chord/3 * EaseIn),
/// P2 = tail - tailDir * (chord/3 * EaseOut), where headDir/tailDir are the bone's +Y
/// directions under the head/tail rotations. Segment centres sit on that curve and their
/// orientations slerp from the head rotation to the tail rotation. That is the whole model:
/// prescribed end positions AND end orientations, which is what a posed bone gives you
/// (head placed, tail placed, both ends oriented). It is NOT a length-preserving solve —
/// a curve through fixed ends is longer than its chord, so a bent B-Bone's arc length
/// exceeds the bind length, exactly as a real bent limb's centreline does.
///
/// WHAT THIS IS NOT: it does not rewrite the skeleton (no extra bones are inserted, so
/// skinning indices never shift under a running rig), and it does not aim segment frames
/// at the curve tangent — orientation follows the slerped end rotations, which is what
/// keeps roll continuous along the bone. A tangent-aimed frame would twist wherever the
/// curve's curvature vanishes.
///
/// DEGENERATE INPUT: head == tail returns every segment at the head with the head rotation
/// rather than NaNs — a zero-length bone is a user error to report upstream, not a crash here.
/// </summary>
public static class BendyBone
{
    public static BendySegment[] Evaluate( Vec3 head, Vec3 tail, Quat headRotation, Quat tailRotation, BendyBoneSettings settings )
    {
        settings ??= new BendyBoneSettings();

        if ( settings.Segments < 1 )
            throw new ArgumentOutOfRangeException( nameof( settings.Segments ), "A B-Bone needs at least 1 segment" );

        var segments = new BendySegment[settings.Segments];

        for ( var i = 0; i < settings.Segments; i++ )
        {
            var t = (i + 0.5f) / settings.Segments;
            segments[i] = new BendySegment(
                CurvePoint( head, tail, headRotation, tailRotation, settings, t ),
                Quat.Slerp( headRotation, tailRotation, t ).Normalized );
        }

        return segments;
    }

    /// <summary>Exact curve points including both ends: index 0 is the head, last is the tail.</summary>
    public static Vec3[] SampleCurve( Vec3 head, Vec3 tail, Quat headRotation, Quat tailRotation, BendyBoneSettings settings, int samples )
    {
        settings ??= new BendyBoneSettings();

        if ( samples < 2 )
            throw new ArgumentOutOfRangeException( nameof( samples ), "Sampling a curve needs at least 2 points" );

        var points = new Vec3[samples];

        for ( var i = 0; i < samples; i++ )
            points[i] = CurvePoint( head, tail, headRotation, tailRotation, settings, (float)i / (samples - 1) );

        return points;
    }

    /// <summary>Unit tangent of the curve at parameter t (0 = head, 1 = tail).</summary>
    public static Vec3 TangentAt( Vec3 head, Vec3 tail, Quat headRotation, Quat tailRotation, BendyBoneSettings settings, float t )
    {
        settings ??= new BendyBoneSettings();
        var (p0, p1, p2, p3) = ControlPoints( head, tail, headRotation, tailRotation, settings );
        t = Math.Clamp( t, 0f, 1f );

        var d = 3f * (1f - t) * (1f - t) * (p1 - p0)
            + 6f * (1f - t) * t * (p2 - p1)
            + 3f * t * t * (p3 - p2);

        return d.LengthSquared < 1e-12f ? headRotation.Rotate( new Vec3( 0, 1, 0 ) ).Normal : d.Normal;
    }

    /// <summary>
    /// A skeleton bone's bind ends in world space: head/tail from the bind pose, and the
    /// bind orientation as a quaternion (head and tail rotations are identical in the bind
    /// pose — the bone is rigid there, bending only appears under a pose).
    /// </summary>
    public static (Vec3 Head, Vec3 Tail, Quat HeadRotation, Quat TailRotation) BindEnds( Skeleton skeleton, int bone )
    {
        if ( skeleton is null )
            throw new ArgumentNullException( nameof( skeleton ) );

        if ( bone < 0 || bone >= skeleton.Count )
            throw new ArgumentOutOfRangeException( nameof( bone ) );

        var head = skeleton.HeadWorld( bone );
        var tail = skeleton.TailWorld( bone );
        var rotation = Quat.FromXform( skeleton.WorldBind( bone ) );

        return (head, tail, rotation, rotation);
    }

    static Vec3 CurvePoint( Vec3 head, Vec3 tail, Quat headRotation, Quat tailRotation, BendyBoneSettings settings, float t )
    {
        var (p0, p1, p2, p3) = ControlPoints( head, tail, headRotation, tailRotation, settings );
        t = Math.Clamp( t, 0f, 1f );

        var u = 1f - t;

        return u * u * u * p0
            + 3f * u * u * t * p1
            + 3f * u * t * t * p2
            + t * t * t * p3;
    }

    static (Vec3 P0, Vec3 P1, Vec3 P2, Vec3 P3) ControlPoints( Vec3 head, Vec3 tail, Quat headRotation, Quat tailRotation, BendyBoneSettings settings )
    {
        var chord = (tail - head).Length;

        if ( chord < 1e-6f )
            return (head, head, head, head);

        var handle = chord / 3f;
        var headDir = headRotation.Rotate( new Vec3( 0, 1, 0 ) ).Normal;
        var tailDir = tailRotation.Rotate( new Vec3( 0, 1, 0 ) ).Normal;

        return (
            head,
            head + headDir * (handle * settings.EaseIn),
            tail - tailDir * (handle * settings.EaseOut),
            tail );
    }
}
