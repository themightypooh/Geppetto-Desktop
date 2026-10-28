using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>R6: per-batch values for the lobes beyond metal/rough, copied out of a
/// <see cref="MaterialDescriptor"/>. Plain data, no GL handles.</summary>
public sealed class LobeUniforms
{
	readonly int _transmission, _thickness, _ior, _attenuationColor, _attenuationDistance;
	readonly int _subsurface, _subsurfaceRadius, _subsurfaceScale;
	readonly int _sheen, _sheenTint, _sheenRoughness, _clearcoat, _clearcoatRoughness, _anisotropy, _anisotropyRotation;

	public LobeUniforms( GlShader s )
	{
		_transmission = s.Uniform( "uTransmission" ); _thickness = s.Uniform( "uThickness" ); _ior = s.Uniform( "uIor" );
		_attenuationColor = s.Uniform( "uAttenuationColor" ); _attenuationDistance = s.Uniform( "uAttenuationDistance" );
		_subsurface = s.Uniform( "uSubsurface" ); _subsurfaceRadius = s.Uniform( "uSubsurfaceRadius" ); _subsurfaceScale = s.Uniform( "uSubsurfaceScale" );
		_sheen = s.Uniform( "uSheen" ); _sheenTint = s.Uniform( "uSheenTint" ); _sheenRoughness = s.Uniform( "uSheenRoughness" );
		_clearcoat = s.Uniform( "uClearcoat" ); _clearcoatRoughness = s.Uniform( "uClearcoatRoughness" );
		_anisotropy = s.Uniform( "uAnisotropy" ); _anisotropyRotation = s.Uniform( "uAnisotropyRotation" );
	}

	public void Apply( GlShader s, in MaterialLobes m )
	{
		s.SetFloat( _transmission, m.Transmission ); s.SetFloat( _thickness, m.Thickness ); s.SetFloat( _ior, m.Ior );
		s.SetVec3( _attenuationColor, m.AttenuationColor.X, m.AttenuationColor.Y, m.AttenuationColor.Z );
		s.SetFloat( _attenuationDistance, m.AttenuationDistance );
		s.SetFloat( _subsurface, m.Subsurface ); s.SetVec3( _subsurfaceRadius, m.SubsurfaceRadius.X, m.SubsurfaceRadius.Y, m.SubsurfaceRadius.Z );
		s.SetFloat( _subsurfaceScale, m.SubsurfaceScale );
		s.SetFloat( _sheen, m.Sheen ); s.SetVec3( _sheenTint, m.SheenTint.X, m.SheenTint.Y, m.SheenTint.Z ); s.SetFloat( _sheenRoughness, m.SheenRoughness );
		s.SetFloat( _clearcoat, m.Clearcoat ); s.SetFloat( _clearcoatRoughness, m.ClearcoatRoughness );
		s.SetFloat( _anisotropy, m.Anisotropy ); s.SetFloat( _anisotropyRotation, m.AnisotropyRotation );
	}
}
