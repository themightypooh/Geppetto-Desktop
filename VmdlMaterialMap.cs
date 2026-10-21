#nullable enable
using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// A ModelDoc's own material bindings, turned into per-slot .vmat overrides.
///
/// This is the general form of what <see cref="SurveyMaterialMap"/> does for Survey's two
/// models: the ModelDoc says which .vmat each mesh material means, and this hands that to
/// the ordinary <see cref="MaterialResolver"/>, which already knows how to read a .vmat and
/// decode its maps. Nothing about the material itself is invented here - the .vmat file is
/// the only source of colour, roughness, normal and emission.
///
/// The file format itself belongs to the kernel, which already reads s&box's .riganim and
/// writes ModelDocs; the application never parses one.
///
/// Slots the ModelDoc does not mention keep the resolver's ordinary fallback, so a
/// partially authored ModelDoc still shows what it does author.
/// </summary>
public sealed class VmdlMaterialMap
{
	/// <summary>The ModelDoc the bindings came from.</summary>
	public VmdlReader.Model Model { get; }
	/// <summary>The resolver carrying the .vmat overrides.</summary>
	public MaterialResolver Resolver { get; }

	VmdlMaterialMap( VmdlReader.Model model, MaterialResolver resolver )
	{
		Model = model;
		Resolver = resolver;
	}

	/// <summary>
	/// Build the map for a ModelDoc, or null when it has no material bindings to offer.
	/// The .vmat paths are project-relative to the same root the resolver uses.
	/// </summary>
	public static VmdlMaterialMap? ForModel( AssetPathResolver paths, VmdlReader.Model model, string documentId )
	{
		ArgumentNullException.ThrowIfNull( paths );
		ArgumentNullException.ThrowIfNull( model );
		if ( model.Remaps.Count == 0 ) return null;
		var resolver = new MaterialResolver( paths, documentId );
		foreach ( var remap in model.Remaps )
			foreach ( string slot in VmdlReader.NameSpellings( remap.From ) )
				resolver.SetOverride( slot, new AssetReference( model.Path + ":" + slot, remap.To ) );
		return new VmdlMaterialMap( model, resolver );
	}
}
