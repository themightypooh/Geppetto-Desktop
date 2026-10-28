using System;
using System.Collections.Generic;
using Godot;
using Marionette.Desktop;
using Effigy;

namespace Geppetto;

/// <summary>
/// G6: Material bridge converting engine-free <see cref="MaterialDescriptor"/> to Godot <see cref="StandardMaterial3D"/>.
/// Maps base color, roughness, metallic, emission, normal and ambient occlusion channels,
/// handles alpha modes and culling, and maps extended lobes (clearcoat, subsurface, sheen/rim, refraction, anisotropy).
/// Emits named warnings for non-native Survey ps1_lit and stylized features rather than silent drops.
/// </summary>
public static class EffigyMaterialBridge
{
	private static readonly Dictionary<string, ImageTexture> TextureCache = new( StringComparer.Ordinal );

	/// <summary>
	/// Converts a <see cref="MaterialDescriptor"/> into a Godot <see cref="StandardMaterial3D"/>.
	/// </summary>
	public static StandardMaterial3D ToGodotMaterial( MaterialDescriptor descriptor, MaterialResolver resolver = null )
	{
		ArgumentNullException.ThrowIfNull( descriptor );

		var mat = new StandardMaterial3D
		{
			ResourceName = descriptor.Name ?? descriptor.Id
		};

		// 1. Base color factor & texture
		mat.AlbedoColor = new Color(
			descriptor.BaseColorFactor.x,
			descriptor.BaseColorFactor.y,
			descriptor.BaseColorFactor.z,
			descriptor.BaseColorFactor.w
		);

		// 2. Roughness & Metallic
		mat.Roughness = Math.Clamp( descriptor.RoughnessFactor, 0f, 1f );
		mat.Metallic = Math.Clamp( descriptor.MetallicFactor, 0f, 1f );

		// 3. Emission
		var em = descriptor.EmissionFactor;
		if ( em.x > 0.001f || em.y > 0.001f || em.z > 0.001f || descriptor.EmissionMap != null )
		{
			mat.EmissionEnabled = true;
			mat.Emission = new Color( em.x, em.y, em.z );
		}

		// 4. Culling & DoubleSided
		mat.CullMode = descriptor.DoubleSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back;

		// 5. Alpha mode & scissor cutoff
		switch ( descriptor.AlphaMode )
		{
			case AlphaMode.Opaque:
				mat.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
				break;
			case AlphaMode.Mask:
				mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
				mat.AlphaScissorThreshold = Math.Clamp( descriptor.AlphaCutoff, 0f, 1f );
				break;
			case AlphaMode.Blend:
				mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
				break;
			case AlphaMode.Hashed:
				mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaHash;
				break;
		}

		// 6. Extended R6 lobes
		if ( descriptor.Clearcoat > 0.001f )
		{
			mat.ClearcoatEnabled = true;
			mat.Clearcoat = Math.Clamp( descriptor.Clearcoat, 0f, 1f );
			mat.ClearcoatRoughness = Math.Clamp( descriptor.ClearcoatRoughness, 0f, 1f );
		}

		if ( descriptor.Subsurface > 0.001f )
		{
			mat.SubsurfScatterEnabled = true;
			mat.SubsurfScatterStrength = Math.Clamp( descriptor.Subsurface, 0f, 1f );
			mat.SubsurfScatterTransmittanceEnabled = true;
			var rad = descriptor.SubsurfaceRadius;
			mat.SubsurfScatterTransmittanceColor = new Color( rad.x, rad.y, rad.z );
		}

		if ( descriptor.Transmission > 0.001f )
		{
			mat.RefractionEnabled = true;
			mat.RefractionScale = Math.Clamp( descriptor.Transmission, -1f, 1f );
		}

		if ( descriptor.Sheen > 0.001f )
		{
			mat.RimEnabled = true;
			mat.Rim = Math.Clamp( descriptor.Sheen, 0f, 1f );
			mat.RimTint = Math.Clamp( descriptor.SheenRoughness, 0f, 1f );
		}

		if ( MathF.Abs( descriptor.Anisotropy ) > 0.001f )
		{
			mat.AnisotropyEnabled = true;
			mat.Anisotropy = Math.Clamp( descriptor.Anisotropy, -1f, 1f );
		}

		// 7. Normal map
		if ( descriptor.NormalMap != null )
		{
			mat.NormalEnabled = true;
			mat.NormalScale = descriptor.NormalStrength;
		}

		// 8. Ambient Occlusion
		if ( descriptor.AmbientOcclusionMap != null )
		{
			mat.AOEnabled = true;
			mat.AOLightAffect = 1.0f;
		}

		// 9. Texture channel loading via resolver
		if ( resolver != null )
		{
			BindTextureChannel( resolver, descriptor, MaterialChannel.BaseColor, tex => mat.AlbedoTexture = tex );
			BindTextureChannel( resolver, descriptor, MaterialChannel.Normal, tex => mat.NormalTexture = tex );
			BindTextureChannel( resolver, descriptor, MaterialChannel.Roughness, tex => mat.RoughnessTexture = tex );
			BindTextureChannel( resolver, descriptor, MaterialChannel.Metallic, tex => mat.MetallicTexture = tex );
			BindTextureChannel( resolver, descriptor, MaterialChannel.Emission, tex => mat.EmissionTexture = tex );
			BindTextureChannel( resolver, descriptor, MaterialChannel.AmbientOcclusion, tex => mat.AOTexture = tex );
		}

		// 10. Survey ps1_lit named warnings
		if ( descriptor.Ps1Wear > 0f || descriptor.Ps1Grime > 0f || descriptor.Ps1Glow > 0f || descriptor.Ps1BootDirt > 0f )
		{
			GD.PushWarning( $"[EffigyMaterialBridge] Material '{descriptor.Id}': Survey ps1_lit properties (wear={descriptor.Ps1Wear}, grime={descriptor.Ps1Grime}, glow={descriptor.Ps1Glow}, bootDirt={descriptor.Ps1BootDirt}) are not native StandardMaterial3D lobes." );
		}

		if ( descriptor.LookToon > 0f || descriptor.LookStylized > 0f )
		{
			GD.PushWarning( $"[EffigyMaterialBridge] Material '{descriptor.Id}': Toon/stylized look properties (toon={descriptor.LookToon}, stylized={descriptor.LookStylized}) require a custom shader." );
		}

		return mat;
	}

	/// <summary>
	/// Resolves or falls back to a <see cref="StandardMaterial3D"/> for a given <see cref="CpuMesh.DrawBatch"/>.
	/// </summary>
	public static StandardMaterial3D ToGodotMaterial( in CpuMesh.DrawBatch batch, MaterialResolver resolver = null )
	{
		if ( resolver != null )
		{
			var resolved = resolver.ResolveSlot( batch.MaterialSlot, batch.MaterialName ?? $"slot_{batch.MaterialSlot}" );
			if ( resolved.Descriptor != null )
				return ToGodotMaterial( resolved.Descriptor, resolver );
		}

		string name = batch.MaterialName ?? $"slot_{batch.MaterialSlot}";
		var paletteEntry = MaterialPalette.ForName( name );
		var fallbackDesc = new MaterialDescriptor( name )
		{
			BaseColorFactor = new Vec4( paletteEntry.R, paletteEntry.G, paletteEntry.B, 1f ),
			RoughnessFactor = paletteEntry.Roughness,
			MetallicFactor = paletteEntry.Metallic,
			EmissionFactor = new Vec3( paletteEntry.Er, paletteEntry.Eg, paletteEntry.Eb )
		};

		return ToGodotMaterial( fallbackDesc, resolver );
	}

	private static void BindTextureChannel( MaterialResolver resolver, MaterialDescriptor descriptor, MaterialChannel channel, Action<ImageTexture> apply )
	{
		var binding = descriptor.GetChannel( channel );
		if ( binding == null ) return;

		string cacheKey = $"{binding.Reference.Path}::{binding.ColorSpace}";
		if ( TextureCache.TryGetValue( cacheKey, out var cachedTex ) )
		{
			apply( cachedTex );
			return;
		}

		var loadResult = resolver.LoadTexture( binding, descriptor.Id );
		if ( !loadResult.Success || loadResult.Image == null )
		{
			GD.PushWarning( $"[EffigyMaterialBridge] Failed loading {channel} texture '{binding.Reference.Path}' for material '{descriptor.Id}': {loadResult.Diagnostic?.Message}" );
			return;
		}

		var imgData = loadResult.Image;
		var godotImg = Image.CreateFromData( imgData.Width, imgData.Height, false, Image.Format.Rgba8, imgData.Rgba );
		var texture = ImageTexture.CreateFromImage( godotImg );
		TextureCache[cacheKey] = texture;
		apply( texture );
	}
}
