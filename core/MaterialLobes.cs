using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>R6: per-batch values for the lobes beyond metal/rough, copied out of a
/// <see cref="MaterialDescriptor"/>. Plain data, no GL handles.</summary>
public readonly record struct MaterialLobes(
	float Transmission, float Thickness, float Ior, Vector3 AttenuationColor, float AttenuationDistance,
	float Subsurface, Vector3 SubsurfaceRadius, float SubsurfaceScale,
	float Sheen, Vector3 SheenTint, float SheenRoughness,
	float Clearcoat, float ClearcoatRoughness,
	float Anisotropy, float AnisotropyRotation )
{
	public static readonly MaterialLobes None = new( 0, 0, 1.5f, Vector3.One, 0, 0, new Vector3( 1, .2f, .1f ), .05f, 0, Vector3.One, .5f, 0, .03f, 0, 0 );

	public static MaterialLobes From( MaterialDescriptor d )
	{
		ArgumentNullException.ThrowIfNull( d );
		// Reject nonfinite payloads before publishing GL uniforms. Clamps alone do
		// not sanitize NaN, and negative/amplifying absorption is not a volume.
		float Finite( float value ) => float.IsFinite( value ) ? value
			: throw new ArgumentException( "Material lobe factors must be finite.", nameof( d ) );
		Vector3 Vector( Effigy.Vec3 value ) => new( Finite( value.x ), Finite( value.y ), Finite( value.z ) );
		return new(
			Math.Clamp( Finite( d.Transmission ), 0, 1 ), MathF.Max( 0, Finite( d.Thickness ) ), MathF.Max( 1, Finite( d.Ior ) ),
			Vector3.Clamp( Vector( d.AttenuationColor ), Vector3.Zero, Vector3.One ), MathF.Max( 0, Finite( d.AttenuationDistance ) ),
			Math.Clamp( Finite( d.Subsurface ), 0, 1 ), Vector3.Max( Vector( d.SubsurfaceRadius ), Vector3.Zero ), MathF.Max( 0, Finite( d.SubsurfaceScale ) ),
			Math.Clamp( Finite( d.Sheen ), 0, 1 ), Vector3.Clamp( Vector( d.SheenTint ), Vector3.Zero, Vector3.One ), Math.Clamp( Finite( d.SheenRoughness ), .07f, 1 ),
			Math.Clamp( Finite( d.Clearcoat ), 0, 1 ), Math.Clamp( Finite( d.ClearcoatRoughness ), .03f, 1 ),
			Math.Clamp( Finite( d.Anisotropy ), -.95f, .95f ), Finite( d.AnisotropyRotation ) );
	}

}

