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
	/// The material for one <see cref="CpuMesh.DrawBatch"/>, chosen the way Geppetto's desktop viewer
	/// chooses it (DocumentSession): the slot's own look (`materiallook SLOT ...`, from
	/// <paramref name="looks"/>) wins, then whatever the resolver finds (.vmat overrides, a .vmat slot
	/// name), then the name-based palette.
	/// </summary>
	/// <param name="looks">The document's slot looks (<c>studio.MaterialLooks</c>); null for none.</param>
	/// <param name="baseDir">Directory that relative look map paths resolve against (the document's folder).</param>
	public static StandardMaterial3D ToGodotMaterial( in CpuMesh.DrawBatch batch, MaterialResolver resolver = null,
		IReadOnlyDictionary<int, SlotLook> looks = null, string baseDir = null )
	{
		string name = batch.MaterialName ?? $"slot_{batch.MaterialSlot}";

		if ( looks != null && looks.TryGetValue( batch.MaterialSlot, out var look ) && look != null )
			return ToGodotMaterial( name, look, baseDir );

		if ( resolver != null )
		{
			var resolved = resolver.ResolveSlot( batch.MaterialSlot, name );
			if ( resolved.Descriptor != null )
				return ToGodotMaterial( resolved.Descriptor, resolver );
		}

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

	/// <summary>
	/// Converts an Effigy <see cref="SlotLook"/> into a <see cref="StandardMaterial3D"/>, following the
	/// kernel's SlotLook.Shade rules. Look colours are sRGB as typed, which is what StandardMaterial3D's
	/// albedo/emission colours expect, so they go in unconverted. Fields with no StandardMaterial3D
	/// equivalent push a named warning instead of being dropped silently.
	/// </summary>
	public static StandardMaterial3D ToGodotMaterial( string name, SlotLook look, string baseDir = null )
	{
		ArgumentNullException.ThrowIfNull( look );
		string id = name ?? look.Preset ?? "look";
		var unsupported = new List<string>();
		var mat = new StandardMaterial3D { ResourceName = id };

		// Colour, opacity. Opacity < 1 is the kernel's thin-membrane alpha (fins, wings, glass).
		mat.AlbedoColor = new Color( look.Color.x, look.Color.y, look.Color.z, Math.Clamp( look.Opacity, 0f, 1f ) );
		if ( look.Opacity < 1f )
			mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;

		mat.Roughness = Math.Clamp( look.Rough, 0f, 1f );
		mat.Metallic = Math.Clamp( look.Metal, 0f, 1f );

		// Dielectric F0: the kernel uses Specular directly, or ((ior-1)/(ior+1))^2 when ior is set.
		// Godot's metallic_specular s gives F0 = 0.16 * s^2 (0.5 -> 0.04).
		float f0 = look.Ior > 0f ? (look.Ior - 1f) * (look.Ior - 1f) / ((look.Ior + 1f) * (look.Ior + 1f)) : look.Specular;
		mat.MetallicSpecular = Math.Clamp( MathF.Sqrt( Math.Max( f0, 0f ) / 0.16f ), 0f, 1f );
		if ( look.SpecularTint > 0f ) unsupported.Add( $"speculartint={look.SpecularTint}" );

		// Diffuse model: the kernel is Lambert unless burleydiffuse is on; Godot defaults to Burley.
		mat.DiffuseMode = look.BurleyDiffuse ? BaseMaterial3D.DiffuseModeEnum.Burley : BaseMaterial3D.DiffuseModeEnum.Lambert;

		// Glow: the kernel shades Linear(Emit) * 2 as light.
		if ( look.Glows || !string.IsNullOrWhiteSpace( look.EmitMap ) )
		{
			mat.EmissionEnabled = true;
			mat.Emission = new Color( look.Emit.x, look.Emit.y, look.Emit.z );
			mat.EmissionEnergyMultiplier = 2f;
		}

		// Clearcoat, with the kernel's implicit 0.4 coat on smooth dielectrics.
		float coat = look.ImplicitCoat ? MathF.Max( look.Clearcoat, look.Rough < 0.25f && look.Metal < 0.5f ? 0.4f : 0f ) : look.Clearcoat;
		if ( coat > 0.001f )
		{
			mat.ClearcoatEnabled = true;
			mat.Clearcoat = Math.Clamp( coat, 0f, 1f );
			mat.ClearcoatRoughness = look.Clearcoat > 0f ? Math.Clamp( look.ClearcoatRoughness, 0.01f, 1f ) : 0.04f;
		}

		// Subsurface: strength, scatter colour and depth (model inches -> metres).
		if ( look.Subsurface > 0.001f )
		{
			mat.SubsurfScatterEnabled = true;
			mat.SubsurfScatterStrength = Math.Clamp( look.Subsurface, 0f, 1f );
			mat.SubsurfScatterTransmittanceEnabled = true;
			mat.SubsurfScatterTransmittanceColor = new Color( look.SubsurfaceColor.x, look.SubsurfaceColor.y, look.SubsurfaceColor.z );
			if ( look.ScatterDistSet )
			{
				mat.SubsurfScatterTransmittanceDepth = look.ScatterDist.x * EffigyMeshBridge.ScaleToMetres;
				if ( MathF.Abs( look.ScatterDist.x - look.ScatterDist.y ) > 1e-4f || MathF.Abs( look.ScatterDist.x - look.ScatterDist.z ) > 1e-4f )
					unsupported.Add( "per-channel scatterdist (red distance used)" );
			}
		}

		// Anisotropy. Godot's direction comes from tangents/flowmap; a rotation angle has no equivalent.
		if ( MathF.Abs( look.Aniso ) > 0.001f )
		{
			mat.AnisotropyEnabled = true;
			mat.Anisotropy = Math.Clamp( look.Aniso, -1f, 1f );
			if ( MathF.Abs( look.AnisoRotation ) > 0.001f ) unsupported.Add( $"anisorotation={look.AnisoRotation}" );
		}
		if ( string.Equals( look.Preset, "hair_aniso", StringComparison.OrdinalIgnoreCase ) )
			unsupported.Add( "hair_aniso fibre shading" );

		// Sheen is approximated by Godot's rim lobe; a custom sheen tint needs a shader.
		if ( look.Sheen > 0.001f )
		{
			mat.RimEnabled = true;
			mat.Rim = Math.Clamp( look.Sheen, 0f, 1f );
			mat.RimTint = 0f;
			unsupported.Add( look.SheenTint.HasValue ? "sheen tint (sheen approximated by rim)" : "sheen (approximated by rim)" );
		}

		// Texture maps: paths resolve against the document folder first, as Shade() does.
		mat.Uv1Scale = new Vector3( look.MapScale.x, look.MapScale.y, 1f );
		mat.Uv1Offset = new Vector3( look.MapOffset.x, look.MapOffset.y, 0f );
		if ( look.ClampMaps ) mat.TextureRepeat = false;

		if ( LoadLookMap( id, "albedo", look.AlbedoMap, baseDir, unsupported ) is { } albedo )
		{
			mat.AlbedoTexture = albedo;
			// The kernel shows the map's own colours (tint white) when a map is set.
			mat.AlbedoColor = new Color( 1f, 1f, 1f, mat.AlbedoColor.A );
		}
		if ( LoadLookMap( id, "roughness", look.RoughnessMap, baseDir, unsupported ) is { } rough )
		{
			// Kernel: the map's red channel replaces the scalar. Godot multiplies, so scalar = 1.
			mat.RoughnessTexture = rough;
			mat.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Red;
			mat.Roughness = 1f;
		}
		if ( LoadLookMap( id, "metalness", look.MetalnessMap, baseDir, unsupported ) is { } metal )
		{
			mat.MetallicTexture = metal;
			mat.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Red;
			mat.Metallic = 1f;
		}
		if ( LoadLookMap( id, "normal", look.NormalMap, baseDir, unsupported ) is { } normal )
		{
			mat.NormalEnabled = true;
			mat.NormalTexture = normal;
			mat.NormalScale = look.NormalStrength;
		}
		if ( LoadLookMap( id, "ao", look.AoMap, baseDir, unsupported ) is { } ao )
		{
			mat.AOEnabled = true;
			mat.AOTexture = ao;
			mat.AOTextureChannel = BaseMaterial3D.TextureChannel.Red;
		}
		if ( LoadLookMap( id, "emission", look.EmitMap, baseDir, unsupported ) is { } emit )
		{
			// Kernel: glow = scalar emit + map * emitstrength. Godot adds colour and map, then scales both.
			mat.EmissionTexture = emit;
			mat.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Add;
			mat.EmissionEnergyMultiplier = look.EmitStrength;
			if ( look.Glows && MathF.Abs( look.EmitStrength - 2f ) > 1e-3f )
				unsupported.Add( "emit colour and emitmap scaled separately (one energy used for both)" );
		}
		if ( LoadLookMap( id, "height", look.HeightMap, baseDir, unsupported ) is { } height )
		{
			mat.HeightmapEnabled = true;
			mat.HeightmapTexture = height;
			mat.HeightmapScale = look.HeightStrength;
		}
		if ( LoadLookMap( id, "detailnormal", look.DetailNormalMap, baseDir, unsupported ) is { } detail )
		{
			mat.DetailEnabled = true;
			mat.DetailNormal = detail;
			unsupported.Add( $"detailscale={look.DetailScale} (detail normal uses the base UV scale)" );
		}
		if ( !string.IsNullOrWhiteSpace( look.AnisoMap ) ) unsupported.Add( $"anisomap={look.AnisoMap}" );
		if ( !string.IsNullOrWhiteSpace( look.SheenMap ) ) unsupported.Add( $"sheenmap={look.SheenMap}" );
		if ( !string.IsNullOrWhiteSpace( look.TransmissionMap ) ) unsupported.Add( $"transmissionmap={look.TransmissionMap}" );

		// Renderer-side look spectrum: no StandardMaterial3D lobe for these.
		if ( look.Toon > 0f || look.Stylized > 0f || look.ToonRim > 0f )
			GD.PushWarning( $"[EffigyMaterialBridge] Material '{id}': Toon/stylized look properties (toon={look.Toon}, toonbands={look.ToonBands}, toonrim={look.ToonRim}, stylized={look.Stylized}) require a custom shader." );
		if ( look.OutlineWidth > 0f ) unsupported.Add( $"outline={look.OutlineWidth}" );
		if ( look.ShadowTint.x != 1f || look.ShadowTint.y != 1f || look.ShadowTint.z != 1f ) unsupported.Add( "shadowtint" );
		if ( look.Iridescence > 0f ) unsupported.Add( $"iridescence={look.Iridescence}" );
		if ( look.LayerColor.HasValue ) unsupported.Add( "layer (layercolor/layerrough/layermetal/layermask)" );

		if ( unsupported.Count > 0 )
			GD.PushWarning( $"[EffigyMaterialBridge] Material '{id}': slot look properties not mapped to StandardMaterial3D (need a custom shader): {string.Join( ", ", unsupported )}." );

		return mat;
	}

	/// <summary>Loads a look's map like SlotLook.Shade: rooted paths as-is, else relative to
	/// <paramref name="baseDir"/> when that file exists, else relative to the working directory.
	/// A missing map warns and the scalar stays, never failing the import.</summary>
	private static ImageTexture LoadLookMap( string id, string kind, string path, string baseDir, List<string> unsupported )
	{
		if ( string.IsNullOrWhiteSpace( path ) ) return null;
		if ( path.StartsWith( "noise:", StringComparison.OrdinalIgnoreCase ) )
		{
			unsupported.Add( $"{kind}map={path} (procedural noise map)" );
			return null;
		}

		string full = System.IO.Path.IsPathRooted( path ) ? path
			: !string.IsNullOrWhiteSpace( baseDir ) && System.IO.File.Exists( System.IO.Path.Combine( baseDir, path ) ) ? System.IO.Path.Combine( baseDir, path )
			: System.IO.Path.GetFullPath( path );

		string cacheKey = $"look::{full}";
		if ( TextureCache.TryGetValue( cacheKey, out var cached ) ) return cached;

		var img = new Image();
		if ( !System.IO.File.Exists( full ) || img.Load( full ) != Error.Ok )
		{
			GD.PushWarning( $"[EffigyMaterialBridge] Material '{id}': slot map {kind}={path} could not be loaded; using the scalar instead." );
			return null;
		}
		img.GenerateMipmaps();
		var tex = ImageTexture.CreateFromImage( img );
		TextureCache[cacheKey] = tex;
		return tex;
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
