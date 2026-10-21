using System;
using System.Collections.Generic;
using Effigy;
using Marionette.Desktop;

namespace Marionette.Desktop;

public static class MaterialLibrary
{
	public static readonly Dictionary<string, MaterialDescriptor> Presets = new( StringComparer.OrdinalIgnoreCase )
	{
		["matte"] = new MaterialDescriptor( "matte" ) { BaseColorFactor = new Vec4( 0.8f, 0.8f, 0.8f, 1f ), RoughnessFactor = 0.9f, MetallicFactor = 0f },
		["glossy"] = new MaterialDescriptor( "glossy" ) { BaseColorFactor = new Vec4( 0.9f, 0.9f, 0.9f, 1f ), RoughnessFactor = 0.2f, MetallicFactor = 0f },
		["metal"] = new MaterialDescriptor( "metal" ) { BaseColorFactor = new Vec4( 0.9f, 0.9f, 0.9f, 1f ), RoughnessFactor = 0.3f, MetallicFactor = 1f },
		["skin"] = new MaterialDescriptor( "skin" ) { BaseColorFactor = new Vec4( 0.95f, 0.75f, 0.65f, 1f ), RoughnessFactor = 0.6f, MetallicFactor = 0f },
		["fabric"] = new MaterialDescriptor( "fabric" ) { BaseColorFactor = new Vec4( 0.7f, 0.3f, 0.3f, 1f ), RoughnessFactor = 0.85f, MetallicFactor = 0f },
		["plastic"] = new MaterialDescriptor( "plastic" ) { BaseColorFactor = new Vec4( 0.2f, 0.5f, 0.9f, 1f ), RoughnessFactor = 0.4f, MetallicFactor = 0f },
		["emissive"] = new MaterialDescriptor( "emissive" ) { BaseColorFactor = new Vec4( 1f, 1f, 1f, 1f ), EmissionFactor = new Vec3( 1f, 1f, 1f ), RoughnessFactor = 0.5f, MetallicFactor = 0f },
		// metals
		["metal-chrome"] = new MaterialDescriptor( "metal-chrome" ) { BaseColorFactor = new Vec4( 0.95f, 0.95f, 0.95f, 1f ), RoughnessFactor = 0.1f, MetallicFactor = 1f },
		["metal-brass"] = new MaterialDescriptor( "metal-brass" ) { BaseColorFactor = new Vec4( 0.9f, 0.75f, 0.3f, 1f ), RoughnessFactor = 0.35f, MetallicFactor = 1f },
		["metal-rust"] = new MaterialDescriptor( "metal-rust" ) { BaseColorFactor = new Vec4( 0.5f, 0.3f, 0.2f, 1f ), RoughnessFactor = 0.9f, MetallicFactor = 0.6f },
		// glass / alpha
		["glass-clear"] = new MaterialDescriptor( "glass-clear" ) { BaseColorFactor = new Vec4( 1f, 1f, 1f, 0.1f ), RoughnessFactor = 0.05f, MetallicFactor = 0f },
		["glass-tinted"] = new MaterialDescriptor( "glass-tinted" ) { BaseColorFactor = new Vec4( 0.3f, 0.6f, 0.9f, 0.25f ), RoughnessFactor = 0.1f, MetallicFactor = 0f },
		// fabrics
		["fabric-cotton"] = new MaterialDescriptor( "fabric-cotton" ) { BaseColorFactor = new Vec4( 0.9f, 0.9f, 0.85f, 1f ), RoughnessFactor = 0.95f, MetallicFactor = 0f },
		["fabric-silk"] = new MaterialDescriptor( "fabric-silk" ) { BaseColorFactor = new Vec4( 0.85f, 0.2f, 0.3f, 1f ), RoughnessFactor = 0.3f, MetallicFactor = 0f },
		["fabric-denim"] = new MaterialDescriptor( "fabric-denim" ) { BaseColorFactor = new Vec4( 0.15f, 0.25f, 0.5f, 1f ), RoughnessFactor = 0.8f, MetallicFactor = 0f },
		// skin tones
		["skin-light"] = new MaterialDescriptor( "skin-light" ) { BaseColorFactor = new Vec4( 0.95f, 0.8f, 0.7f, 1f ), RoughnessFactor = 0.55f, MetallicFactor = 0f },
		["skin-dark"] = new MaterialDescriptor( "skin-dark" ) { BaseColorFactor = new Vec4( 0.45f, 0.3f, 0.22f, 1f ), RoughnessFactor = 0.6f, MetallicFactor = 0f },
		// terrain
		["terrain-grass"] = new MaterialDescriptor( "terrain-grass" ) { BaseColorFactor = new Vec4( 0.3f, 0.6f, 0.2f, 1f ), RoughnessFactor = 0.9f, MetallicFactor = 0f },
		["terrain-rock"] = new MaterialDescriptor( "terrain-rock" ) { BaseColorFactor = new Vec4( 0.4f, 0.4f, 0.4f, 1f ), RoughnessFactor = 0.85f, MetallicFactor = 0f },
		["terrain-sand"] = new MaterialDescriptor( "terrain-sand" ) { BaseColorFactor = new Vec4( 0.85f, 0.75f, 0.5f, 1f ), RoughnessFactor = 0.7f, MetallicFactor = 0f },
		// plastics
		["plastic-glossy"] = new MaterialDescriptor( "plastic-glossy" ) { BaseColorFactor = new Vec4( 0.9f, 0.1f, 0.1f, 1f ), RoughnessFactor = 0.2f, MetallicFactor = 0f },
		["plastic-matte"] = new MaterialDescriptor( "plastic-matte" ) { BaseColorFactor = new Vec4( 0.2f, 0.2f, 0.2f, 1f ), RoughnessFactor = 0.9f, MetallicFactor = 0f },
	};

	public static MaterialDescriptor Get( string name )
	{
		if ( name is null ) throw new ArgumentNullException( nameof( name ) );
		if ( Presets.TryGetValue( name, out var d ) ) return d;
		throw new KeyNotFoundException( $"Unknown material preset: {name}" );
	}

	public static IEnumerable<string> Names() => Presets.Keys;
}
