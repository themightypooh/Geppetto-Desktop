using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>
/// The Layers panel as a DATA MODEL only: selection, group expansion, the active variant and the
/// edits the panel offers — toggle, opacity, intensity, variant overrides — all delegating to a
/// <see cref="UnifiedLayerStack"/>.
///
/// There is no rendering here and no UI toolkit type: the kernel stays engine-free (see RULES.md
/// rule 8), and the viewer binds this model to whatever it draws, the way
/// <see cref="LineworkUiModel"/> describes UI without drawing it. The panel never owns layer
/// content — the stack does — so a panel and a script editing the same stack cannot disagree
/// about what the stack holds; they only disagree about what is selected, which is panel state.
/// </summary>
public sealed class LayersPanelModel
{
	public UnifiedLayerStack Stack { get; }

	/// <summary>The selected entry's name, or null when nothing is selected.</summary>
	public string SelectedName { get; private set; }

	/// <summary>Group names currently expanded in the panel. "" (ungrouped) is always shown.</summary>
	public HashSet<string> ExpandedGroups { get; } = new();

	public LayersPanelModel( UnifiedLayerStack stack )
	{
		Stack = stack ?? throw new ArgumentNullException( nameof( stack ) );
	}

	/// <summary>The selected entry, or null when nothing is selected.</summary>
	public UnifiedLayerEntry Selected => Stack.Find( SelectedName );

	/// <summary>Select an entry by name; an unknown name clears the selection rather than
	/// pointing at an entry that is not there.</summary>
	public void Select( string name )
	{
		SelectedName = Stack.Find( name )?.Name;
	}

	/// <summary>Expand a collapsed group or collapse an expanded one.</summary>
	public void ToggleGroup( string group )
	{
		if ( string.IsNullOrEmpty( group ) )
			return;

		if ( !ExpandedGroups.Add( group ) )
			ExpandedGroups.Remove( group );
	}

	public bool IsGroupExpanded( string group ) =>
		string.IsNullOrEmpty( group ) || ExpandedGroups.Contains( group );

	/// <summary>Switch the active variant. A name with no overrides yet simply means "base
	/// values until somebody overrides them in that variant".</summary>
	public void SetActiveVariant( string variant ) => Stack.ActiveVariant = variant;

	/// <summary>Set a paint entry's opacity. A sculpt entry has no opacity to set; this is a
	/// no-op for one rather than an error, because the panel offers the same slider for both.</summary>
	public void SetOpacity( string name, float opacity )
	{
		var entry = Stack.Find( name );

		if ( entry?.Paint is not null )
			entry.Paint.Opacity = Math.Clamp( opacity, 0f, 1f );
	}

	/// <summary>Set a sculpt entry's intensity (-1..1). A paint entry has no intensity; no-op.</summary>
	public void SetIntensity( string name, float intensity )
	{
		var entry = Stack.Find( name );

		if ( entry is not null && entry.Kind == UnifiedLayerKind.Sculpt )
			entry.Intensity = Math.Clamp( intensity, -1f, 1f );
	}

	/// <summary>Flip an entry's enabled flag. Unknown names do nothing.</summary>
	public void ToggleEnabled( string name )
	{
		var entry = Stack.Find( name );

		if ( entry is not null )
			entry.Enabled = !entry.Enabled;
	}

	/// <summary>Set one entry's opacity override inside one variant, from the variant editor.</summary>
	public void SetVariantOpacity( string variant, string name, float opacity ) =>
		Stack.SetVariantOverride( variant, name, opacity: opacity );

	/// <summary>Set one entry's intensity override inside one variant, from the variant editor.</summary>
	public void SetVariantIntensity( string variant, string name, float intensity ) =>
		Stack.SetVariantOverride( variant, name, intensity: intensity );

	/// <summary>Panel state as JSON: selection, expanded groups, active variant. The stack itself
	/// is NOT in here — it serializes separately, so a panel can be restored onto a stack that
	/// was edited elsewhere without dragging stale layer content with it.</summary>
	public string ToJson()
	{
		var root = new JsonObject
		{
			["selected"] = SelectedName,
			["activeVariant"] = Stack.ActiveVariant,
		};

		var groups = new JsonArray();
		foreach ( var group in ExpandedGroups )
			groups.Add( group );
		root["expandedGroups"] = groups;

		return root.ToJsonString();
	}

	public static LayersPanelModel FromJson( UnifiedLayerStack stack, string json )
	{
		if ( stack is null )
			throw new ArgumentNullException( nameof( stack ) );

		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "a panel needs JSON to read", nameof( json ) );

		var root = JsonNode.Parse( json )?.AsObject()
			?? throw new JsonException( "the panel JSON has no object at its root" );

		var panel = new LayersPanelModel( stack );

		foreach ( var node in root["expandedGroups"]?.AsArray() ?? new JsonArray() )
		{
			var group = node?.GetValue<string>();
			if ( !string.IsNullOrEmpty( group ) )
				panel.ExpandedGroups.Add( group );
		}

		panel.Stack.ActiveVariant = root["activeVariant"]?.GetValue<string>();
		panel.Select( root["selected"]?.GetValue<string>() );

		return panel;
	}
}
