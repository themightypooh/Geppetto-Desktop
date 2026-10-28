using System.Linq;
using Godot;
using Marionette.Desktop;
using GArray = Godot.Collections.Array<Godot.Collections.Dictionary>;
using GDict = Godot.Collections.Dictionary;

namespace Geppetto;

/// <summary>
/// G11: thin Godot-facing view of the engine-free <see cref="ControlRegistry"/> (core/ControlRegistry.cs).
/// Data only: no docks, panels or widgets. A future UI panel reads features, parameters and
/// ranked search results from here as plain Godot dictionaries.
///
/// All labels, tooltips, categories and groups are DRAFTS for pooh's review; every dictionary
/// carries "is_draft" = true and "draft_source".
/// </summary>
public static class EffigyControlRegistry
{
	public static ControlRegistry Registry => ControlRegistry.Shared;

	public static int FeatureCount => Registry.Features.Count;

	/// <summary>Every catalog feature (no params), in ApiCatalog order.</summary>
	public static GArray Features()
	{
		var list = new GArray();
		foreach ( var f in Registry.Features ) list.Add( ToDict( f, includeParams: false ) );
		return list;
	}

	/// <summary>One feature with its params, or null when the name is unknown. "Material" gives the material lobes.</summary>
	public static GDict Feature( string name )
	{
		var f = Registry.Feature( name );
		return f is null ? null : ToDict( f, includeParams: true );
	}

	/// <summary>Ranked keyword + synonym search.</summary>
	public static GArray Search( string query, int limit = 20 )
	{
		var list = new GArray();
		foreach ( var hit in Registry.Search( query, limit ) )
		{
			var d = new GDict
			{
				["kind"] = hit.Kind == ControlHitKind.Feature ? "feature" : "param",
				["id"] = hit.Id,
				["score"] = hit.Score,
				["feature"] = hit.Feature?.Name ?? "",
				["matched"] = string.Join( ",", hit.Matched ),
			};
			if ( hit.Param is not null ) d["param"] = ToDict( hit.Param );
			list.Add( d );
		}
		return list;
	}

	static GDict ToDict( ControlFeature f, bool includeParams )
	{
		var d = new GDict
		{
			["name"] = f.Name,
			["class_name"] = f.ClassName,
			["kernel_type_name"] = f.KernelTypeName,
			["category"] = f.Category,
			["label"] = f.DraftLabel,
			["tooltip"] = f.DraftTooltip,
			["param_count"] = f.Params.Count,
			["is_draft"] = f.IsDraft,
			["draft_source"] = f.DraftSource,
		};
		if ( includeParams )
		{
			var ps = new GArray();
			foreach ( var p in f.Params ) ps.Add( ToDict( p ) );
			d["params"] = ps;
		}
		return d;
	}

	static GDict ToDict( ControlParam p )
	{
		var d = new GDict
		{
			["id"] = p.Id,
			["owner"] = p.Owner,
			["key"] = p.Key,
			["kernel_label"] = p.KernelLabel,
			["kind"] = p.Kind.ToString(),
			["source"] = p.Source.ToString(),
			["default"] = p.Default ?? "",
			["unit"] = p.Unit ?? "",
			["advanced"] = p.Advanced,
			["slider"] = p.Slider,
			["group"] = p.Group,
			["label"] = p.DraftLabel,
			["tooltip"] = p.DraftTooltip,
			["is_draft"] = p.IsDraft,
			["draft_source"] = p.DraftSource,
		};
		if ( p.Min.HasValue ) d["min"] = p.Min.Value;
		if ( p.Max.HasValue ) d["max"] = p.Max.Value;
		if ( p.Options is not null ) d["options"] = new Godot.Collections.Array<string>( p.Options.ToArray() );
		return d;
	}
}
