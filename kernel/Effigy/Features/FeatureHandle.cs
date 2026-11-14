using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// One number a viewport arrow or ring can drag. A feature records its handles while it builds, from
/// the same direction and origin it builds with, so the arrow cannot disagree with the geometry.
/// The drag maths takes rays rather than a camera, so it runs without a viewport.
/// </summary>
public sealed class FeatureHandle
{
	public readonly string Label;
	public readonly Vec3 Origin;
	public readonly Vec3 Axis;
	/// <summary>A ring about <see cref="Axis"/> in degrees, not an arrow along it.</summary>
	public readonly bool IsAngle;
	readonly Func<float> _get;
	readonly Action<float> _set;
	readonly float _min, _max;

	public FeatureHandle( string label, Vec3 origin, Vec3 axis, Func<float> get, Action<float> set,
		bool isAngle = false, float min = float.MinValue, float max = float.MaxValue )
	{
		Label = label;
		Origin = origin;
		Axis = axis.Normal;
		IsAngle = isAngle;
		_get = get;
		_set = set;
		_min = min;
		_max = max;
	}

	/// <summary>An arrow or ring on a whole number parameter. A drag replaces a formula with the number.</summary>
	public static FeatureHandle Of( FloatParam p, Vec3 origin, Vec3 axis, bool isAngle = false ) =>
		new( p.Label, origin, axis, () => p.Value, v => { p.Value = v; p.Expr = null; }, isAngle, p.Min, p.Max );

	public float Value => _get();

	/// <summary>The arrow's point in the model.</summary>
	public Vec3 Tip => Origin + Axis * Value;

	/// <summary>Where a ray sits on this handle: inches along the arrow, or degrees round the ring.
	/// Null when the ray runs parallel to the arrow or the ring's plane and gives no answer.</summary>
	public float? Measure( Vec3 rayOrigin, Vec3 rayDirection )
	{
		var d = rayDirection.Normal;
		return IsAngle ? AngleAt( rayOrigin, d ) : AlongAxis( rayOrigin, d );
	}

	float? AlongAxis( Vec3 o, Vec3 d )
	{
		var along = Vec3.Dot( Axis, d );
		var den = 1f - along * along;

		if ( den < 1e-5f )
			return null;

		var w = o - Origin;
		return ( Vec3.Dot( w, Axis ) - along * Vec3.Dot( w, d ) ) / den;
	}

	float? AngleAt( Vec3 o, Vec3 d )
	{
		var den = Vec3.Dot( d, Axis );

		if ( MathF.Abs( den ) < 1e-5f )
			return null;

		var hit = o + d * ( Vec3.Dot( Origin - o, Axis ) / den );
		var p = FacePlane.FromPointAndNormal( Origin, Axis ).ToPlane( hit );
		return MathF.Atan2( p.y, p.x ) * 180f / MathF.PI;
	}

	/// <summary>Sets the value for a drag from <paramref name="pressed"/> to <paramref name="now"/> (both from
	/// <see cref="Measure"/>) that began at <paramref name="start"/>. Ctrl snaps to whole units (5 degrees on
	/// a ring), Shift to tenths (1 degree).</summary>
	public void Drag( float start, float pressed, float now, bool ctrl, bool shift )
	{
		var delta = now - pressed;

		// The ring's reading jumps from 180 to -180 as the mouse crosses behind it.
		if ( IsAngle )
			delta = MathF.IEEERemainder( delta, 360f );

		var value = start + delta;
		var step = ctrl ? ( IsAngle ? 5f : 1f ) : shift ? ( IsAngle ? 1f : 0.1f ) : 0f;

		if ( step > 0f )
			value = MathF.Round( value / step ) * step;

		_set( Math.Clamp( value, _min, _max ) );
	}

	/// <summary>The middle and average outward direction of picked faces, read before they move.</summary>
	internal static (Vec3 Centre, Vec3 Normal) FacesAnchor( Dictionary<Body, List<int>> byBody )
	{
		var centre = Vec3.Zero;
		var normal = Vec3.Zero;
		var count = 0;

		foreach ( var (body, faces) in byBody )
		{
			foreach ( var index in faces )
			{
				var face = body.Mesh.Faces[index];
				centre += body.Mesh.FaceCentroid( face );
				normal += body.Mesh.FaceNormal( face );
				count++;
			}
		}

		return ( centre * ( 1f / Math.Max( 1, count ) ), normal );
	}
}
