using System;

namespace Marionette.Desktop;

/// <summary>
/// Flat viewport colours for named materials until real maps land.
/// Camhead colours come from the average albedo of its authored textures;
/// other documents retain the provisional name-based palette.
/// </summary>
public static class MaterialPalette
{
	public readonly struct Entry
	{
		public readonly string Name;
		public readonly float R, G, B;
		public readonly float Roughness;
		public readonly float Metallic;
		public readonly float Er, Eg, Eb;

		public Entry( string name, float r, float g, float b, float roughness = 0.5f, float metallic = 0f, float er = 0f, float eg = 0f, float eb = 0f )
		{
			Name = name;
			R = r; G = g; B = b;
			Roughness = roughness;
			Metallic = metallic;
			Er = er; Eg = eg; Eb = eb;
		}
	}

	public static Entry ForName( string name )
	{
		var key = (name ?? "").ToLowerInvariant();
		if ( key.StartsWith( "materials/camhead/", StringComparison.Ordinal ) )
		{
			var material = System.IO.Path.GetFileNameWithoutExtension( key );
			return material switch
			{
				"shell"      => new Entry( name, 0.263f, 0.298f, 0.342f, roughness: 0.36f, metallic: 0.15f ),
				"frame"      => new Entry( name, 0.157f, 0.167f, 0.186f, roughness: 0.44f, metallic: 0.65f ),
				"joint"      => new Entry( name, 0.254f, 0.246f, 0.205f, roughness: 0.32f, metallic: 0.78f ),
				"panel"      => new Entry( name, 0.783f, 0.747f, 0.676f, roughness: 0.40f, metallic: 0.04f ),
				"cable"      => new Entry( name, 0.757f, 0.324f, 0.063f, roughness: 0.55f, metallic: 0.00f ),
				"lens_black" => new Entry( name, 0.166f, 0.176f, 0.188f, roughness: 0.48f, metallic: 0.45f ),
				"lens_metal" => new Entry( name, 0.676f, 0.696f, 0.716f, roughness: 0.20f, metallic: 0.90f ),
				"glass"      => new Entry( name, 0.131f, 0.206f, 0.244f, roughness: 0.06f, metallic: 0.15f ),
				"sensor"     => new Entry( name, 0.085f, 0.442f, 0.510f, roughness: 0.08f, metallic: 0.82f, er: 0.04f, eg: 0.22f, eb: 0.26f ),
				"chrome"     => new Entry( name, 0.859f, 0.869f, 0.888f, roughness: 0.12f, metallic: 0.96f ),
				"tally"      => new Entry( name, 0.832f, 0.057f, 0.057f, roughness: 0.18f, metallic: 0.00f, er: 1.35f, eg: 0.06f, eb: 0.05f ),
				"eyeball"    => new Entry( name, 0.085f, 0.094f, 0.123f, roughness: 0.08f, metallic: 0.05f ),
				"blade"      => new Entry( name, 0.188f, 0.207f, 0.257f, roughness: 0.30f, metallic: 0.72f ),
				"lid"        => new Entry( name, 0.120f, 0.131f, 0.140f, roughness: 0.42f, metallic: 0.50f ),
				"rubber"     => new Entry( name, 0.067f, 0.067f, 0.078f, roughness: 0.82f, metallic: 0.00f ),
				_            => new Entry( name, 0.620f, 0.600f, 0.570f, roughness: 0.50f, metallic: 0.00f ),
			};
		}
		// Exact Survey names: don't let a generic substring like "ring" recolour unrelated models.
		// Head/collar base colours are linear-space means of the authored albedo PNGs.
		Entry? survey = key switch
		{
			"face_hair" => new Entry( name, 0.01138f, 0.01033f, 0.00984f, roughness: 0.75f ),
			"face_skin" => new Entry( name, 0.18290f, 0.09392f, 0.06513f, roughness: 0.60f ),
			"face_eyes" => new Entry( name, 0.16230f, 0.09886f, 0.08251f, roughness: 0.20f ),
			"collar_trim" => new Entry( name, 0.26707f, 0.14376f, 0.02944f, roughness: 0.25f, metallic: 0.85f ),
			"collar_dark" => new Entry( name, 0.02597f, 0.03183f, 0.04212f, roughness: 0.50f, metallic: 0.65f ),
			"collar_liner" => new Entry( name, 0.01069f, 0.00650f, 0.00438f, roughness: 0.85f ),
			"collar_plate" => new Entry( name, 0.08140f, 0.08026f, 0.07209f, roughness: 0.60f ),
			"hab_glow_green" => new Entry( name, 0.04426f, 0.85043f, 0.11436f, roughness: 0.35f, er: 0.08f, eg: 1.25f, eb: 0.18f ),
			"suit_body" => new Entry( name, 0.18f, 0.22f, 0.28f, roughness: 0.55f, metallic: 0.05f ),
			"hab_leather" => new Entry( name, 0.16f, 0.12f, 0.10f, roughness: 0.70f ),
			"hab_gunmetal" => new Entry( name, 0.22f, 0.23f, 0.25f, roughness: 0.40f, metallic: 0.75f ),
			"hab_steel" => new Entry( name, 0.55f, 0.56f, 0.58f, roughness: 0.28f, metallic: 0.85f ),
			"armour_glove_l" or "armour_glove_r" or "armour_sleeve_l" or "armour_sleeve_r" => new Entry( name, 0.12f, 0.14f, 0.16f, roughness: 0.45f, metallic: 0.70f ),
			"arm_ring" => new Entry( name, 0.70f, 0.62f, 0.25f, roughness: 0.20f, metallic: 0.90f ),
			"arm_web" => new Entry( name, 0.08f, 0.10f, 0.12f, roughness: 0.70f ),
			"hab_readout" => new Entry( name, 0.05f, 0.35f, 0.45f, roughness: 0.30f, er: 0.05f, eg: 0.30f, eb: 0.40f ),
			"hab_mustard" => new Entry( name, 0.22f, 0.18f, 0.10f, roughness: 0.80f ),
			_ => null
		};
		if ( survey is { } materialEntry ) return materialEntry;
		if ( key.Contains( "chrome" ) ) return new Entry( name, 0.78f, 0.80f, 0.84f, roughness: 0.14f, metallic: 0.95f );
		if ( key.Contains( "brass" ) )  return new Entry( name, 0.72f, 0.55f, 0.22f, roughness: 0.24f, metallic: 0.90f );
		if ( key.Contains( "paint2" ) ) return new Entry( name, 0.18f, 0.28f, 0.55f, roughness: 0.26f, metallic: 0.10f );
		if ( key.Contains( "paint" ) )  return new Entry( name, 0.55f, 0.12f, 0.10f, roughness: 0.26f, metallic: 0.10f );
		if ( key.Contains( "metal" ) )  return new Entry( name, 0.35f, 0.36f, 0.38f, roughness: 0.34f, metallic: 0.85f );
		if ( key.Contains( "soot" ) )   return new Entry( name, 0.08f, 0.08f, 0.09f, roughness: 0.88f, metallic: 0.05f );
		if ( key.Contains( "rubber" ) ) return new Entry( name, 0.12f, 0.11f, 0.11f, roughness: 0.80f, metallic: 0.00f );
		if ( key.Contains( "ceramic" ) )return new Entry( name, 0.82f, 0.80f, 0.74f, roughness: 0.22f, metallic: 0.00f );
		if ( key.Contains( "hot" ) || key.Contains( "glow" ) ) return new Entry( name, 0.95f, 0.35f, 0.08f, roughness: 0.35f, metallic: 0.0f, er: 1.25f, eg: 0.35f, eb: 0.05f );
		if ( key.Contains( "cable" ) || key.Contains( "rein" ) ) return new Entry( name, 0.16f, 0.14f, 0.12f, roughness: 0.72f, metallic: 0.05f );
		if ( key.Contains( "hull" ) )   return new Entry( name, 0.42f, 0.43f, 0.45f, roughness: 0.38f, metallic: 0.65f );
		return new Entry( name ?? "material_0", 0.62f, 0.60f, 0.57f, roughness: 0.50f, metallic: 0.00f );
	}
}
