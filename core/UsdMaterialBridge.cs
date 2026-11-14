#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Effigy;
using Effigy.Export;

// Parallel implementation note: kernel/Effigy/UsdStage.cs defines Effigy.UsdPreviewSurface
// with the same simple name. This alias pins this file to the Effigy.Export slice so both
// implementations can coexist in the tree until the integrator picks one.

using UsdPreviewSurface = Effigy.Export.UsdPreviewSurface;

namespace Marionette.Desktop;

/// <summary>
/// Bridge between the kernel-side <see cref="UsdPreviewSurface"/> / MaterialX
/// material model and core <see cref="MaterialDescriptor"/>
/// (godlike-usd-stage-alembic-materialx).
///
/// This is the same PBR factor convention the GLB exporter uses
/// (BaseColorFactor / RoughnessFactor / MetallicFactor / EmissionFactor,
/// sRGB colour, linear data), so a material that round-trips through USD or
/// MaterialX shades the same as one exported to GLB. Anything the other side
/// cannot carry is a named loss on the result, never a silent drop:
/// opacity becomes an alpha mode (volumetric transmission is not
/// represented), and the Survey ps1 overlays, subsurface, sheen and
/// anisotropy have no UsdPreviewSurface equivalent.
/// </summary>
public static class UsdMaterialBridge
{
	/// <summary>Preview surface to a core descriptor. <paramref name="losses"/> names everything that did not survive.</summary>
	public static MaterialDescriptor ToMaterialDescriptor( UsdPreviewSurface surface, out List<string> losses )
	{
		if ( surface == null ) throw new ArgumentNullException( nameof( surface ) );
		losses = new List<string>();
		var desc = new MaterialDescriptor( surface.Name, surface.Name )
		{
			BaseColorFactor = new Vec4( surface.DiffuseColor.x, surface.DiffuseColor.y, surface.DiffuseColor.z, surface.Opacity ),
			RoughnessFactor = surface.Roughness,
			MetallicFactor = surface.Metallic,
			EmissionFactor = surface.EmissiveColor,
			Clearcoat = surface.Clearcoat,
			ClearcoatRoughness = surface.ClearcoatRoughness,
			Ior = surface.Ior,
			AlphaCutoff = surface.OpacityThreshold,
			AlphaMode = surface.OpacityThreshold > 0f ? AlphaMode.Mask : surface.Opacity < 1f ? AlphaMode.Blend : AlphaMode.Opaque,
		};
		if ( !string.IsNullOrEmpty( surface.DiffuseMap ) ) desc = desc.WithChannel( MaterialChannel.BaseColor, TextureBinding.ForBaseColor( Ref( surface.DiffuseMap ) ) );
		if ( !string.IsNullOrEmpty( surface.NormalMap ) ) desc = desc.WithChannel( MaterialChannel.Normal, TextureBinding.ForNormal( Ref( surface.NormalMap ) ) );
		if ( !string.IsNullOrEmpty( surface.RoughnessMap ) ) desc = desc.WithChannel( MaterialChannel.Roughness, TextureBinding.ForData( Ref( surface.RoughnessMap ) ) );
		if ( !string.IsNullOrEmpty( surface.MetallicMap ) ) desc = desc.WithChannel( MaterialChannel.Metallic, TextureBinding.ForData( Ref( surface.MetallicMap ) ) );
		if ( !string.IsNullOrEmpty( surface.EmissiveMap ) ) desc = desc.WithChannel( MaterialChannel.Emission, TextureBinding.ForEmission( Ref( surface.EmissiveMap ) ) );
		if ( surface.Opacity < 1f ) losses.Add( "material '" + surface.Name + "': opacity " + surface.Opacity + " maps to AlphaMode " + desc.AlphaMode + "; volumetric transmission is not represented" );
		return desc;
	}

	/// <summary>Core descriptor to a preview surface. <paramref name="losses"/> names everything that did not survive.</summary>
	public static UsdPreviewSurface FromMaterialDescriptor( MaterialDescriptor desc, out List<string> losses )
	{
		if ( desc == null ) throw new ArgumentNullException( nameof( desc ) );
		losses = new List<string>();
		var surface = new UsdPreviewSurface
		{
			Name = desc.Id,
			DiffuseColor = new Vec3( desc.BaseColorFactor.x, desc.BaseColorFactor.y, desc.BaseColorFactor.z ),
			Opacity = desc.BaseColorFactor.w,
			Roughness = desc.RoughnessFactor,
			Metallic = desc.MetallicFactor,
			EmissiveColor = desc.EmissionFactor,
			Clearcoat = desc.Clearcoat,
			ClearcoatRoughness = desc.ClearcoatRoughness,
			Ior = desc.Ior,
			OpacityThreshold = desc.AlphaMode == AlphaMode.Mask ? desc.AlphaCutoff : 0f,
			DiffuseMap = desc.BaseColorMap?.Reference.Path,
			NormalMap = desc.NormalMap?.Reference.Path,
			RoughnessMap = desc.RoughnessMap?.Reference.Path,
			MetallicMap = desc.MetallicMap?.Reference.Path,
			EmissiveMap = desc.EmissionMap?.Reference.Path,
		};
		if ( desc.Transmission > 0f ) losses.Add( "material '" + desc.Id + "': transmission " + desc.Transmission + " has no UsdPreviewSurface equivalent and was dropped (opacity kept the fade)" );
		if ( desc.Subsurface > 0f ) losses.Add( "material '" + desc.Id + "': subsurface " + desc.Subsurface + " has no UsdPreviewSurface equivalent and was dropped" );
		if ( desc.Sheen > 0f ) losses.Add( "material '" + desc.Id + "': sheen " + desc.Sheen + " has no UsdPreviewSurface equivalent and was dropped" );
		if ( desc.Anisotropy != 0f ) losses.Add( "material '" + desc.Id + "': anisotropy " + desc.Anisotropy + " has no UsdPreviewSurface equivalent and was dropped" );
		if ( desc.Ps1Occlusion != 1f || desc.Ps1Wear != 0f || desc.Ps1Grime != 0f || desc.Ps1Glow != 0f || desc.Ps1BootDirt != 0f )
			losses.Add( "material '" + desc.Id + "': Survey ps1 overlays (occlusion/wear/grime/glow/boot dirt) have no UsdPreviewSurface equivalent and were dropped" );
		if ( desc.AlphaMode == AlphaMode.Hashed ) losses.Add( "material '" + desc.Id + "': hashed alpha has no UsdPreviewSurface equivalent; written as opaque with the opacity factor" );
		return surface;
	}

	static AssetReference Ref( string path ) =>
		new AssetReference( Path.GetFileNameWithoutExtension( path ), path,
			Path.IsPathRooted( path ) ? AssetReferenceKind.ExternalReadOnly : AssetReferenceKind.ProjectRelative );
}
