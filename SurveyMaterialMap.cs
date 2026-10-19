#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Explicit, read-only ModelDoc remaps for Survey's two source models. This is a CPU-side
/// phase-one adapter, not a renderer hook or an implementation of ps1_lit.shader.
/// Construct only for the corresponding Survey source identity, never by a bare slot name.
/// All paths are relative to the supplied Assets root and survive moving that tree.
/// </summary>
public sealed class SurveyMaterialMap
{
	public const string SuitModel = "models/player/player_suit.vmdl";
	public const string HeadModel = "models/player/player_head.vmdl";

	public sealed record Remap( string Slot, string VmatPath );

	private static readonly Remap[] SuitRemaps =
	{
		new( "suit_body", "materials/arms/suit_body.vmat" ),
		new( "m_PlayerModelHead", "materials/hab/hab_skin.vmat" ), // excluded placeholder head in assembled player
		new( "hab_rubber", "materials/hab/boot_sole.vmat" ),
		new( "hab_leather", "materials/hab/hab_leather.vmat" ),
		new( "hab_steel", "materials/hab/hab_steel.vmat" ),
		new( "hab_gunmetal", "materials/hab/hab_gunmetal.vmat" ),
		new( "hab_mustard", "materials/hab/boot_rubber.vmat" ),
		new( "armour_glove_R", "materials/arms/armour_glove_R.vmat" ),
		new( "armour_glove_L", "materials/arms/armour_glove_L.vmat" ),
		new( "arm_ring", "materials/hab/arm_ring.vmat" ),
		new( "hab_glow_green", "materials/hab/hab_glow_green.vmat" ),
		new( "arm_web", "materials/hab/arm_web.vmat" ),
		new( "hab_readout", "materials/hab/hab_readout.vmat" ),
		new( "hab_glow_amber", "materials/hab/hab_glow_amber.vmat" ),
		new( "armour_sleeve_R", "materials/arms/armour_sleeve_R.vmat" ),
		new( "armour_sleeve_L", "materials/arms/armour_sleeve_L.vmat" )
	};

	private static readonly Remap[] HeadRemaps =
	{
		new( "face_skin", "materials/player/face_skin.vmat" ),
		new( "face_eyes", "materials/player/face_eyes.vmat" ),
		new( "face_hair", "materials/player/face_hair.vmat" ),
		new( "collar_brass", "materials/hab/collar_brass.vmat" ),
		new( "collar_trim", "materials/hab/collar_trim.vmat" ),
		new( "collar_rubber", "materials/hab/collar_rubber.vmat" ),
		new( "collar_dark", "materials/hab/collar_dark.vmat" ),
		new( "hab_glow_green", "materials/hab/hab_glow_green.vmat" ),
		new( "hab_glow_amber", "materials/hab/hab_glow_amber.vmat" ),
		new( "collar_plate", "materials/hab/collar_plate.vmat" ),
		new( "collar_liner", "materials/hab/collar_liner.vmat" )
	};

	private readonly AssetPathResolver _paths;
	private readonly MaterialResolver _materials;
	private readonly Dictionary<string, Remap> _remaps = new( StringComparer.Ordinal );
	public string ModelReference { get; }
	public IReadOnlyList<Remap> Remaps { get; }

	/// <summary>The wrapped per-document resolver carrying this map's .vmat overrides.</summary>
	public MaterialResolver Resolver => _materials;

	public static bool IsSurveyModel( string modelReference ) =>
		modelReference is SuitModel or HeadModel;

	/// <summary>
	/// Loader seam: return a Survey remapping resolver for a source path that corresponds to
	/// an exact Survey ModelDoc, or null so the caller keeps its ordinary per-document resolver.
	/// A bare material slot name or arbitrary model must never resolve through here.
	/// </summary>
	public static SurveyMaterialMap? ForSourcePath( AssetPathResolver paths, string modelReference ) =>
		IsSurveyModel( modelReference ) ? new SurveyMaterialMap( paths, modelReference ) : null;

	public SurveyMaterialMap( AssetPathResolver paths, string modelReference )
	{
		_paths = paths ?? throw new ArgumentNullException( nameof( paths ) );
		if ( !IsSurveyModel( modelReference ) )
			throw new ArgumentException( "Survey remaps require an exact Survey ModelDoc reference, not a material slot or arbitrary model.", nameof( modelReference ) );
		ModelReference = modelReference;
		Remaps = Array.AsReadOnly( modelReference == SuitModel ? SuitRemaps : HeadRemaps );
		_materials = new MaterialResolver( paths, modelReference );
		foreach ( var remap in Remaps )
		{
			// ModelDoc lists both raw FBX names and their .vmat-suffixed aliases.
			foreach ( string slot in new[] { remap.Slot, remap.Slot + ".vmat" } )
			{
				_remaps.Add( slot, remap );
				_materials.SetOverride( slot, new AssetReference( modelReference + ":" + slot, remap.VmatPath ) );
			}
		}
	}

	/// <summary>Resolve on a load worker, not once per frame. Unknown slots retain the ordinary
	/// MaterialResolver fallback. No texture path is inferred from a filename or slot name.</summary>
	public MaterialResolutionResult ResolveSlot( int slotIndex, string slotName, bool validateTextures = true )
	{
		ArgumentNullException.ThrowIfNull( slotName );
		var result = _materials.ResolveSlot( slotIndex, slotName, validateTextures );
		if ( !_remaps.TryGetValue( slotName, out var remap ) ) return result;

		// The generic .vmat parser extracts declared PBR maps, but its shader warning does not
		// enumerate Survey's unsupported effects. Keep these visible to callers/reviewers.
		var diagnostics = result.Diagnostics.ToList();
		try
		{
			var file = _paths.ResolveRead( new AssetReference( ModelReference + ":" + slotName, remap.VmatPath ) );
			foreach ( string line in File.ReadLines( file.CanonicalPath ) )
			{
				string trimmed = line.Trim();
				int space = trimmed.IndexOfAny( new[] { ' ', '\t' } );
				if ( space < 0 ) continue;
				string key = trimmed[..space];
				if ( !key.StartsWith( "g_flPs1", StringComparison.Ordinal ) &&
					!key.StartsWith( "g_vPs1", StringComparison.Ordinal ) &&
					!key.StartsWith( "g_flSignal", StringComparison.Ordinal ) &&
					key != "F_TEXTURE_FILTERING" ) continue;
				diagnostics.Add( new MaterialDiagnostic( DiagnosticSeverity.Warning, result.Descriptor.Id,
					$"Survey ps1_lit effect '{key}' is unsupported; standard texture channels only (no palette, wear, grime, boot effects, shader glow or engine filtering).",
					remap.VmatPath ) );
			}
		}
		catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException or ArgumentException )
		{
			// MaterialResolver already reports missing/unreadable .vmat; never mask that failure.
			if ( diagnostics.Count == 0 )
				diagnostics.Add( new MaterialDiagnostic( DiagnosticSeverity.Warning, slotName,
					$"Could not inspect Survey shader effects: {ex.Message}", remap.VmatPath ) );
		}
		return result with { Diagnostics = diagnostics };
	}
}
