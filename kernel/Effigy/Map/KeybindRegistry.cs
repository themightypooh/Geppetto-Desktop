using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy.Map;

/// <summary>
/// Keybind registry for the native Geppetto Desktop map editor
/// (board hammer-keybind-registry; Round-2 report §7 #28: "the keyboard is
/// always faster" — but Hammer hides its hotkeys, so every map action gets
/// a canonical id + a default binding + remap support, as data).
///
/// Backend / data-model / API only — no editor UI, no input handling. A
/// future caller (keybind editor UI, input dispatcher, AI verbs) reads this
/// registry; this file never reads input or draws anything.
///
/// ACTION CATALOGUE. The canonical action ids come from the AI BuildScript
/// verbs ticket (hammer-ai-buildscript-verbs-map), which defines the map
/// action catalogue: brush create/edit/clip/vertex, displacement
/// create/sculpt, per-face material/UV, entity place/edit + I/O add,
/// grouping/visgroups, VMF/.map import/export, native compile
/// (Quick/Preview/Final), validation/Issue list, lightmap bake, navmesh,
/// skybox/cubemap. That ticket had NOT landed when this registry was
/// written (2026-11-13: still todo, no BuildScript.Map.cs in the tree), so
/// <see cref="DefaultCatalogue"/> is the spec-derived catalogue covering
/// exactly that scope, one action per capability, and
/// <see cref="ImportCatalogue"/> is the integration point: when the verbs
/// ticket lands it imports its verb list here (new ids are added with their
/// defaults, existing ids are replaced only with overwriteExisting: true,
/// and a user remap is never silently overwritten). If the landed verbs
/// catalogue uses different ids, the import — not this file — is where the
/// two are reconciled; see the fixture README for the current mapping.
///
/// DEFAULTS. Five defaults are the documented Hammer hotkeys named in the
/// ticket: Shift+B block, Shift+X clip, Shift+V vertex, Shift+A face edit,
/// Shift+E entity. Every other default is a Geppetto-chosen default (the
/// fixture README says which is which); none of them is claimed to be a
/// Hammer hotkey.
///
/// CONFLICTS. Two actions on one chord is a named conflict, never silent:
/// <see cref="Bind"/> / <see cref="Remap"/> refuse the change and return
/// a <see cref="KeybindResult"/> carrying the <see cref="KeybindConflict"/>
/// (chord + both action ids); the registry is unchanged.
/// </summary>
public sealed class KeybindRegistry
{
	readonly Dictionary<string, MapActionDefinition> _actions = new( StringComparer.Ordinal );
	readonly Dictionary<string, KeyChord> _bindings = new( StringComparer.Ordinal );
	readonly Dictionary<KeyChord, string> _byChord = new();

	KeybindRegistry( IEnumerable<MapActionDefinition> catalogue )
	{
		foreach ( var action in catalogue )
		{
			if ( action is null ) throw new KeybindRegistryException( KeybindErrorCode.InvalidCatalogue, "Catalogue contains a null action definition." );
			string? problem = action.Validate();
			if ( problem is not null ) throw new KeybindRegistryException( KeybindErrorCode.InvalidCatalogue, problem );
			if ( _actions.ContainsKey( action.Id ) ) throw new KeybindRegistryException( KeybindErrorCode.DuplicateAction, $"Duplicate action id '{action.Id}' in catalogue." );
			_actions.Add( action.Id, action );
			if ( action.DefaultBinding is not null )
			{
				if ( _byChord.TryGetValue( action.DefaultBinding, out string? holder ) )
					throw new KeybindRegistryException( KeybindErrorCode.Conflict,
						$"Default binding {action.DefaultBinding} is claimed by both '{holder}' and '{action.Id}'.",
						new KeybindConflict( action.DefaultBinding, holder, action.Id ) );
				_bindings.Add( action.Id, action.DefaultBinding );
				_byChord.Add( action.DefaultBinding, action.Id );
			}
		}
	}

	/// <summary>Registry preloaded with <see cref="DefaultCatalogue"/>.</summary>
	public static KeybindRegistry CreateDefault() => new( DefaultCatalogue() );

	/// <summary>
	/// The spec-derived default action catalogue: one action per capability
	/// in the BuildScript verbs scope (see the class doc), each with a
	/// default binding. Mirrored by the fixture
	/// tests/fixtures/hammer/hammer-keybind-registry/catalogue.json — a
	/// test asserts the two agree exactly.
	/// </summary>
	public static IReadOnlyList<MapActionDefinition> DefaultCatalogue() => new[]
	{
		// Tools — the five ticket-named Hammer defaults are Shift+B/X/V/A/E.
		new MapActionDefinition( "map.tool.select", "Select Tool", "Tool",
			"Select and move objects and brushes in the 2D and 3D views.", KeyChord.Parse( "Shift+S" ) ),
		new MapActionDefinition( "map.tool.block", "Block Tool", "Tool",
			"Create convex block brushes by dragging a box in a 2D view. Hammer default Shift+B.", KeyChord.Parse( "Shift+B" ) ),
		new MapActionDefinition( "map.tool.clip", "Clip Tool", "Tool",
			"Cut brushes with a 2-point clip line. Hammer default Shift+X.", KeyChord.Parse( "Shift+X" ) ),
		new MapActionDefinition( "map.tool.vertex", "Vertex Edit Tool", "Tool",
			"Move brush vertices and edges directly. Hammer default Shift+V.", KeyChord.Parse( "Shift+V" ) ),
		new MapActionDefinition( "map.tool.face", "Face Edit Tool", "Tool",
			"Edit per-face materials and UVs. Hammer default Shift+A.", KeyChord.Parse( "Shift+A" ) ),
		new MapActionDefinition( "map.tool.entity", "Entity Tool", "Tool",
			"Place point and brush entities. Hammer default Shift+E.", KeyChord.Parse( "Shift+E" ) ),
		new MapActionDefinition( "map.tool.camera", "Camera Tool", "Tool",
			"Position the 3D camera from a 2D view.", KeyChord.Parse( "Shift+C" ) ),
		new MapActionDefinition( "map.tool.displacement", "Displacement Tool", "Tool",
			"Select displacement surfaces for sculpting.", KeyChord.Parse( "Shift+D" ) ),

		// Brush create / edit / clip / vertex / CSG.
		new MapActionDefinition( "map.brush.create", "Create Brush", "Brush",
			"Create the brush drawn with the Block tool.", KeyChord.Parse( "Ctrl+Enter" ) ),
		new MapActionDefinition( "map.brush.clip", "Clip Selected Brush", "Brush",
			"Perform the clip drawn with the Clip tool.", KeyChord.Parse( "Enter" ) ),
		new MapActionDefinition( "map.brush.subtract", "CSG Subtract", "Brush",
			"Subtract the selected tool brush from the target brush.", KeyChord.Parse( "Ctrl+Alt+S" ) ),
		new MapActionDefinition( "map.brush.merge", "CSG Merge", "Brush",
			"Merge two touching convex brushes into one.", KeyChord.Parse( "Ctrl+Alt+M" ) ),
		new MapActionDefinition( "map.brush.hollow", "CSG Hollow", "Brush",
			"Hollow a solid brush into walls of a given thickness.", KeyChord.Parse( "Ctrl+Alt+H" ) ),
		new MapActionDefinition( "map.brush.transform", "Transform Selection", "Brush",
			"Move, rotate or scale the selected brushes on the grid.", KeyChord.Parse( "Ctrl+T" ) ),
		new MapActionDefinition( "map.brush.snap", "Snap Selection to Grid", "Brush",
			"Snap every vertex of the selection to the grid.", KeyChord.Parse( "Ctrl+Shift+G" ) ),
		new MapActionDefinition( "map.brush.primitive.next", "Next Primitive Shape", "Brush",
			"Cycle the Block tool primitive: box, wedge, cylinder, sphere, arch.", KeyChord.Parse( "Alt+B" ) ),

		// Displacement create / sculpt.
		new MapActionDefinition( "map.displacement.create", "Create Displacement", "Displacement",
			"Turn the selected quad faces into a displacement surface.", KeyChord.Parse( "Ctrl+Shift+D" ) ),
		new MapActionDefinition( "map.displacement.sculpt.raise", "Sculpt: Raise", "Displacement",
			"Raise displacement vertices under the sculpt brush.", KeyChord.Parse( "Alt+R" ) ),
		new MapActionDefinition( "map.displacement.sculpt.lower", "Sculpt: Lower", "Displacement",
			"Lower displacement vertices under the sculpt brush.", KeyChord.Parse( "Alt+L" ) ),
		new MapActionDefinition( "map.displacement.sculpt.smooth", "Sculpt: Smooth", "Displacement",
			"Smooth displacement vertices under the sculpt brush.", KeyChord.Parse( "Alt+M" ) ),
		new MapActionDefinition( "map.displacement.sculpt.flatten", "Sculpt: Flatten", "Displacement",
			"Flatten displacement vertices under the sculpt brush.", KeyChord.Parse( "Alt+F" ) ),
		new MapActionDefinition( "map.displacement.sew", "Sew Displacements", "Displacement",
			"Stitch the edges of neighbouring displacements together.", KeyChord.Parse( "Alt+W" ) ),

		// Per-face material / UV.
		new MapActionDefinition( "map.face.material.apply", "Apply Material to Face", "Face",
			"Apply the current material to the selected faces.", KeyChord.Parse( "Alt+A" ) ),
		new MapActionDefinition( "map.face.uv.fit", "Fit UVs to Face", "Face",
			"Fit the face texture UVs to the face bounds (0..1).", KeyChord.Parse( "Ctrl+Shift+F" ) ),
		new MapActionDefinition( "map.face.uv.align", "Align UVs", "Face",
			"Align the face UVs to the world axes.", KeyChord.Parse( "Ctrl+Alt+F" ) ),
		new MapActionDefinition( "map.face.nodraw", "Toggle Nodraw", "Face",
			"Toggle the nodraw tool material on the selected faces.", KeyChord.Parse( "Ctrl+Shift+N" ) ),

		// Entity place / edit / I-O / tie.
		new MapActionDefinition( "map.entity.place", "Place Entity", "Entity",
			"Place the selected entity class at the cursor.", KeyChord.Parse( "Ctrl+Shift+E" ) ),
		new MapActionDefinition( "map.entity.edit", "Edit Entity Properties", "Entity",
			"Edit the selected entity's keyvalues.", KeyChord.Parse( "Alt+E" ) ),
		new MapActionDefinition( "map.entity.io.add", "Add Entity I/O", "Entity",
			"Add an output (When -> Target -> Input) to the selected entity.", KeyChord.Parse( "Ctrl+Shift+I" ) ),
		new MapActionDefinition( "map.entity.tie", "Tie Brushes to Entity", "Entity",
			"Tie the selected brushes to a brush entity.", KeyChord.Parse( "Ctrl+Shift+T" ) ),
		new MapActionDefinition( "map.entity.untie", "Untie from Entity", "Entity",
			"Untie the selected brush entity back into world brushes.", KeyChord.Parse( "Ctrl+Shift+U" ) ),

		// Grouping / visgroups.
		new MapActionDefinition( "map.group.create", "Group Selection", "Group",
			"Group the selected objects.", KeyChord.Parse( "Ctrl+G" ) ),
		new MapActionDefinition( "map.group.ungroup", "Ungroup Selection", "Group",
			"Ungroup the selected groups.", KeyChord.Parse( "Ctrl+U" ) ),
		new MapActionDefinition( "map.visgroup.toggle", "Toggle Visgroup", "Group",
			"Toggle the visibility of the selected objects' visgroup.", KeyChord.Parse( "Ctrl+Shift+V" ) ),

		// VMF / .map import / export.
		new MapActionDefinition( "map.file.import.vmf", "Import VMF", "File",
			"Import a Valve VMF map file.", KeyChord.Parse( "Ctrl+Alt+O" ) ),
		new MapActionDefinition( "map.file.import.map", "Import Valve .map", "File",
			"Import a Valve 220 .map file.", KeyChord.Parse( "Alt+O" ) ),
		new MapActionDefinition( "map.file.export.vmf", "Export VMF", "File",
			"Export the map as a Valve VMF file.", KeyChord.Parse( "Ctrl+Alt+V" ) ),
		new MapActionDefinition( "map.file.export.map", "Export Valve .map", "File",
			"Export the map as a Valve 220 .map file.", KeyChord.Parse( "Ctrl+Alt+P" ) ),
		new MapActionDefinition( "map.file.export.obj", "Export OBJ/GLB", "File",
			"Export the baked map as OBJ/GLB.", KeyChord.Parse( "Ctrl+Alt+G" ) ),

		// Native compile (Quick / Preview / Final) + Quick Play.
		new MapActionDefinition( "map.compile.quick", "Quick Compile", "Compile",
			"Fast BSP-only compile for iteration.", KeyChord.Parse( "F9" ) ),
		new MapActionDefinition( "map.compile.preview", "Preview Compile", "Compile",
			"Compile with visibility and preview lighting.", KeyChord.Parse( "Shift+F9" ) ),
		new MapActionDefinition( "map.compile.final", "Final Compile", "Compile",
			"Full compile: BSP, visibility and final lighting.", KeyChord.Parse( "Ctrl+F9" ) ),
		new MapActionDefinition( "map.compile.quickplay", "Quick Play", "Compile",
			"BSP-only bake, then play from the player start.", KeyChord.Parse( "F10" ) ),

		// Validation / Issue list / leak.
		new MapActionDefinition( "map.validate.run", "Check for Problems", "Validate",
			"Run the full map validation pass (leaks, invalid brushes, missing targets).", KeyChord.Parse( "Alt+P" ) ),
		new MapActionDefinition( "map.validate.issues.list", "List Issues", "Validate",
			"List every open validation issue.", KeyChord.Parse( "Alt+Shift+P" ) ),
		new MapActionDefinition( "map.validate.leak.find", "Find Leak", "Validate",
			"Flood-fill from the outside and report the leak path.", KeyChord.Parse( "Ctrl+Shift+L" ) ),

		// Lightmap bake / navmesh / skybox / cubemap.
		new MapActionDefinition( "map.bake.lightmap", "Bake Lightmap", "Bake",
			"Bake static lightmaps for the map.", KeyChord.Parse( "Ctrl+Alt+B" ) ),
		new MapActionDefinition( "map.bake.navmesh", "Generate Navmesh", "Bake",
			"Generate the AI navigation mesh.", KeyChord.Parse( "Alt+N" ) ),
		new MapActionDefinition( "map.bake.cubemap", "Build Cubemaps", "Bake",
			"Capture and build environment cubemaps.", KeyChord.Parse( "Alt+C" ) ),
		new MapActionDefinition( "map.bake.skybox", "Apply Skybox", "Bake",
			"Apply the 2D/3D skybox to the map.", KeyChord.Parse( "Alt+K" ) ),
	};

	/// <summary>Every action definition in the registry, in catalogue order.</summary>
	public IReadOnlyList<MapActionDefinition> Catalogue => _actions.Values.ToList();

	/// <summary>Number of actions in the catalogue.</summary>
	public int Count => _actions.Count;

	/// <summary>
	/// Import an action catalogue (the BuildScript verbs ticket's verb list,
	/// when it lands). The import is atomic: if any definition is invalid,
	/// duplicates an id already in this registry (without overwrite), or
	/// claims a default chord already bound to a different action, the whole
	/// import is refused and the registry is unchanged.
	/// With overwriteExisting, an imported definition with an id already in
	/// the registry replaces that definition; a user remap is preserved
	/// (only an action still on its old default moves to the new default).
	/// </summary>
	public KeybindResult ImportCatalogue( IEnumerable<MapActionDefinition> catalogue, bool overwriteExisting = false )
	{
		if ( catalogue is null ) throw new ArgumentNullException( nameof( catalogue ) );
		var incoming = catalogue.ToList();
		foreach ( var action in incoming )
		{
			if ( action is null ) return KeybindResult.Failed( KeybindErrorCode.InvalidCatalogue, "Catalogue contains a null action definition." );
			string? problem = action.Validate();
			if ( problem is not null ) return KeybindResult.Failed( KeybindErrorCode.InvalidCatalogue, problem );
		}
		if ( incoming.Select( a => a.Id ).Distinct( StringComparer.Ordinal ).Count() != incoming.Count )
		{
			string dup = incoming.GroupBy( a => a.Id, StringComparer.Ordinal ).First( g => g.Count() > 1 ).Key;
			return KeybindResult.Failed( KeybindErrorCode.DuplicateAction, $"Catalogue contains duplicate action id '{dup}'." );
		}
		foreach ( var action in incoming )
		{
			if ( _actions.ContainsKey( action.Id ) && !overwriteExisting )
				return KeybindResult.Failed( KeybindErrorCode.DuplicateAction,
					$"Action id '{action.Id}' is already in the registry; pass overwriteExisting: true to replace it." );
		}
		// Default-chord collisions, checked against the post-import state.
		var planned = new Dictionary<KeyChord, string>( _byChord );
		foreach ( var action in incoming )
		{
			if ( !_actions.TryGetValue( action.Id, out var existing ) ) continue;
			if ( !overwriteExisting ) continue;
			// An overwritten action vacates its current binding first.
			if ( _bindings.TryGetValue( action.Id, out var current ) && current.Equals( existing.DefaultBinding ) )
				planned.Remove( current );
		}
		foreach ( var action in incoming )
		{
			if ( action.DefaultBinding is null ) continue;
			if ( _actions.ContainsKey( action.Id ) && overwriteExisting && _bindings.TryGetValue( action.Id, out var current )
				&& !current.Equals( _actions[action.Id].DefaultBinding ) )
			{
				// User remap: the new default must not steal another action's chord.
				if ( planned.TryGetValue( action.DefaultBinding, out string? holder ) && holder != action.Id )
					return KeybindResult.Failed( KeybindErrorCode.Conflict,
						$"Default binding {action.DefaultBinding} for '{action.Id}' is already bound to '{holder}'.",
						new KeybindConflict( action.DefaultBinding, holder, action.Id ) );
				continue;
			}
			if ( planned.TryGetValue( action.DefaultBinding, out string? holder2 ) && holder2 != action.Id )
				return KeybindResult.Failed( KeybindErrorCode.Conflict,
					$"Default binding {action.DefaultBinding} for '{action.Id}' is already bound to '{holder2}'.",
					new KeybindConflict( action.DefaultBinding, holder2, action.Id ) );
			planned[action.DefaultBinding] = action.Id;
		}

		foreach ( var action in incoming )
		{
			if ( _actions.TryGetValue( action.Id, out var existing ) )
			{
				// Overwrite: keep a user remap (or an explicit unbind); only
				// an action still on its old default moves to the new one.
				bool stillDefault = _bindings.TryGetValue( action.Id, out var current ) && existing.DefaultBinding is not null && current.Equals( existing.DefaultBinding );
				if ( stillDefault )
				{
					// Guarded: an earlier action in this same import may
					// already have claimed this chord as its new default.
					if ( current is not null && _byChord.TryGetValue( current, out string? holder ) && holder == action.Id )
						_byChord.Remove( current );
					_bindings.Remove( action.Id );
					if ( action.DefaultBinding is not null )
					{
						_bindings.Add( action.Id, action.DefaultBinding );
						_byChord[action.DefaultBinding] = action.Id;
					}
				}
				_actions[action.Id] = action;
				continue;
			}
			_actions.Add( action.Id, action );
			if ( action.DefaultBinding is not null )
			{
				_bindings.Add( action.Id, action.DefaultBinding );
				_byChord[action.DefaultBinding] = action.Id;
			}
		}
		return KeybindResult.Success( $"Imported {incoming.Count} action(s)." );
	}

	/// <summary>The definition for an action id, or null when unknown.</summary>
	public MapActionDefinition? FindAction( string actionId )
		=> actionId is not null && _actions.TryGetValue( actionId, out var action ) ? action : null;

	/// <summary>The chord currently bound to an action, or null when unbound / unknown.</summary>
	public KeyChord? GetBinding( string actionId )
		=> actionId is not null && _bindings.TryGetValue( actionId, out var chord ) ? chord : null;

	/// <summary>True when the action exists and has a binding.</summary>
	public bool TryGetBinding( string actionId, out KeyChord? chord )
	{
		chord = GetBinding( actionId );
		return chord is not null;
	}

	/// <summary>The action id bound to a chord, or null when the chord is free. Returns null for unparseable text.</summary>
	public string? BindingFor( KeyChord chord )
		=> chord is not null && _byChord.TryGetValue( chord, out string? actionId ) ? actionId : null;

	/// <summary>The action id bound to a chord text, or null when the chord is free or unparseable.</summary>
	public string? BindingFor( string chordText )
		=> KeyChord.TryParse( chordText, out var chord, out _ ) && chord is not null ? BindingFor( chord ) : null;

	/// <summary>True when the action's current binding is its catalogue default.</summary>
	public bool IsDefault( string actionId )
	{
		if ( !_actions.TryGetValue( actionId, out var action ) || action.DefaultBinding is null ) return false;
		return _bindings.TryGetValue( actionId, out var chord ) && chord.Equals( action.DefaultBinding );
	}

	/// <summary>
	/// Assign a chord to an action (upsert: replaces the action's current
	/// binding). Refuses — registry unchanged — when the action is unknown
	/// (<see cref="KeybindErrorCode.UnknownAction"/>) or the chord is already
	/// bound to a different action (<see cref="KeybindErrorCode.Conflict"/>,
	/// with the <see cref="KeybindConflict"/> naming both actions).
	/// Binding an action to its own current chord succeeds as a no-op.
	/// </summary>
	public KeybindResult Bind( string actionId, KeyChord chord )
	{
		if ( actionId is null ) throw new ArgumentNullException( nameof( actionId ) );
		if ( chord is null ) throw new ArgumentNullException( nameof( chord ) );
		if ( !_actions.ContainsKey( actionId ) )
			return KeybindResult.Failed( KeybindErrorCode.UnknownAction, $"Unknown map action '{actionId}'." );
		if ( _byChord.TryGetValue( chord, out string? holder ) && holder != actionId )
			return KeybindResult.Failed( KeybindErrorCode.Conflict,
				$"Chord {chord} is already bound to '{holder}'; cannot bind it to '{actionId}'.",
				new KeybindConflict( chord, holder, actionId ) );
		if ( _bindings.TryGetValue( actionId, out var old ) ) _byChord.Remove( old );
		_bindings[actionId] = chord;
		_byChord[chord] = actionId;
		return KeybindResult.Success( $"Bound '{actionId}' to {chord}." );
	}

	/// <summary><see cref="Bind(string, KeyChord)"/> with the chord parsed from text; unparseable text reports <see cref="KeybindErrorCode.InvalidChord"/>.</summary>
	public KeybindResult Bind( string actionId, string chordText )
	{
		if ( !KeyChord.TryParse( chordText, out var chord, out string? error ) )
			return KeybindResult.Failed( KeybindErrorCode.InvalidChord, error ?? $"Invalid chord '{chordText}'." );
		return Bind( actionId, chord! );
	}

	/// <summary>
	/// Change an action's existing binding to a new chord. Like
	/// <see cref="Bind"/>, but refuses with
	/// <see cref="KeybindErrorCode.NotBound"/> when the action currently has
	/// no binding to remap from.
	/// </summary>
	public KeybindResult Remap( string actionId, KeyChord chord )
	{
		if ( actionId is null ) throw new ArgumentNullException( nameof( actionId ) );
		if ( chord is null ) throw new ArgumentNullException( nameof( chord ) );
		if ( !_actions.ContainsKey( actionId ) )
			return KeybindResult.Failed( KeybindErrorCode.UnknownAction, $"Unknown map action '{actionId}'." );
		if ( !_bindings.ContainsKey( actionId ) )
			return KeybindResult.Failed( KeybindErrorCode.NotBound, $"Action '{actionId}' has no binding to remap." );
		return Bind( actionId, chord );
	}

	/// <summary><see cref="Remap(string, KeyChord)"/> with the chord parsed from text; unparseable text reports <see cref="KeybindErrorCode.InvalidChord"/>.</summary>
	public KeybindResult Remap( string actionId, string chordText )
	{
		if ( !KeyChord.TryParse( chordText, out var chord, out string? error ) )
			return KeybindResult.Failed( KeybindErrorCode.InvalidChord, error ?? $"Invalid chord '{chordText}'." );
		return Remap( actionId, chord! );
	}

	/// <summary>
	/// Remove an action's binding. Ok when a binding was removed;
	/// <see cref="KeybindErrorCode.UnknownAction"/> for an unknown action,
	/// <see cref="KeybindErrorCode.NotBound"/> when it had no binding.
	/// </summary>
	public KeybindResult Unbind( string actionId )
	{
		if ( actionId is null ) throw new ArgumentNullException( nameof( actionId ) );
		if ( !_actions.ContainsKey( actionId ) )
			return KeybindResult.Failed( KeybindErrorCode.UnknownAction, $"Unknown map action '{actionId}'." );
		if ( !_bindings.TryGetValue( actionId, out var chord ) )
			return KeybindResult.Failed( KeybindErrorCode.NotBound, $"Action '{actionId}' has no binding to remove." );
		_bindings.Remove( actionId );
		_byChord.Remove( chord );
		return KeybindResult.Success( $"Unbound '{actionId}' (was {chord})." );
	}

	/// <summary>Restore one action's catalogue default binding (or unbind it when its definition has no default).</summary>
	public KeybindResult ResetAction( string actionId )
	{
		if ( actionId is null ) throw new ArgumentNullException( nameof( actionId ) );
		if ( !_actions.TryGetValue( actionId, out var action ) )
			return KeybindResult.Failed( KeybindErrorCode.UnknownAction, $"Unknown map action '{actionId}'." );
		if ( _bindings.TryGetValue( actionId, out var old ) ) { _bindings.Remove( actionId ); _byChord.Remove( old ); }
		if ( action.DefaultBinding is null )
			return KeybindResult.Success( $"Reset '{actionId}': no default binding, left unbound." );
		// A sibling action cannot hold this chord: defaults are unique at
		// catalogue build and Bind/Remap/Import never create a duplicate.
		_bindings[actionId] = action.DefaultBinding;
		_byChord[action.DefaultBinding] = actionId;
		return KeybindResult.Success( $"Reset '{actionId}' to {action.DefaultBinding}." );
	}

	/// <summary>Restore every action's catalogue default binding.</summary>
	public void ResetToDefaults()
	{
		_bindings.Clear();
		_byChord.Clear();
		foreach ( var action in _actions.Values )
		{
			if ( action.DefaultBinding is null ) continue;
			_bindings.Add( action.Id, action.DefaultBinding );
			_byChord.Add( action.DefaultBinding, action.Id );
		}
	}

	/// <summary>
	/// The discoverable hotkey catalogue: every action with its current
	/// binding (null when unbound), its description and whether it is still
	/// on its default — sorted by category, then name.
	/// </summary>
	public IReadOnlyList<KeyBindingEntry> ListBindings() => _actions.Values
		.Select( a => new KeyBindingEntry( a.Id, a.Name, a.Category, a.Description,
			_bindings.TryGetValue( a.Id, out var chord ) ? chord : null,
			a.DefaultBinding is not null && _bindings.TryGetValue( a.Id, out var current ) && current.Equals( a.DefaultBinding ) ) )
		.OrderBy( e => e.Category, StringComparer.Ordinal )
		.ThenBy( e => e.Name, StringComparer.Ordinal )
		.ToList();

	/// <summary><see cref="ListBindings()"/> filtered to one category (case-insensitive); empty when the category has no actions.</summary>
	public IReadOnlyList<KeyBindingEntry> ListBindings( string category )
	{
		if ( category is null ) throw new ArgumentNullException( nameof( category ) );
		return ListBindings().Where( e => e.Category.Equals( category, StringComparison.OrdinalIgnoreCase ) ).ToList();
	}

	/// <summary>
	/// Discoverability query: actions whose id, name, category or
	/// description contains the text (case-insensitive). Empty text lists
	/// every action.
	/// </summary>
	public IReadOnlyList<KeyBindingEntry> Search( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return ListBindings();
		return ListBindings().Where( e =>
			e.ActionId.Contains( text, StringComparison.OrdinalIgnoreCase )
			|| e.Name.Contains( text, StringComparison.OrdinalIgnoreCase )
			|| e.Category.Contains( text, StringComparison.OrdinalIgnoreCase )
			|| e.Description.Contains( text, StringComparison.OrdinalIgnoreCase ) ).ToList();
	}

	/// <summary>
	/// Self-check: problems that should never exist (a chord bound to two
	/// actions, an action bound to a chord owned by another action, an
	/// unknown id in the binding table). Empty means the registry is healthy.
	/// </summary>
	public IReadOnlyList<string> Validate()
	{
		var problems = new List<string>();
		foreach ( var pair in _bindings )
		{
			if ( !_actions.ContainsKey( pair.Key ) ) problems.Add( $"Binding for unknown action '{pair.Key}'." );
			else if ( !_byChord.TryGetValue( pair.Value, out string? holder ) || holder != pair.Key )
				problems.Add( $"Binding {pair.Value} for '{pair.Key}' is not owned by it in the reverse index." );
		}
		foreach ( var pair in _byChord )
		{
			if ( !_bindings.TryGetValue( pair.Value, out var chord ) || !chord.Equals( pair.Key ) )
				problems.Add( $"Reverse index chord {pair.Key} does not match the binding of '{pair.Value}'." );
		}
		return problems;
	}
}

/// <summary>One map action: canonical id, display name, category, plain-words description, default binding (null = unbound by default).</summary>
public sealed record MapActionDefinition( string Id, string Name, string Category, string Description, KeyChord? DefaultBinding )
{
	/// <summary>Null when the definition is valid; otherwise the reason it is not.</summary>
	public string? Validate()
	{
		if ( string.IsNullOrWhiteSpace( Id ) ) return "Action id must not be empty.";
		if ( string.IsNullOrWhiteSpace( Name ) ) return $"Action '{Id}': name must not be empty.";
		if ( string.IsNullOrWhiteSpace( Category ) ) return $"Action '{Id}': category must not be empty.";
		if ( string.IsNullOrWhiteSpace( Description ) ) return $"Action '{Id}': description must not be empty.";
		return null;
	}
}

/// <summary>One catalogue row: an action, its current binding, and whether that binding is still its default.</summary>
public sealed record KeyBindingEntry( string ActionId, string Name, string Category, string Description, KeyChord? Chord, bool IsDefault );

/// <summary>A named keybind conflict: two actions, one chord. Never silent — the refused Bind/Remap returns this.</summary>
public sealed record KeybindConflict( KeyChord Chord, string ExistingActionId, string RequestedActionId )
{
	/// <summary>Plain-words form, e.g. "Shift+B is already bound to 'map.tool.block'; cannot bind it to 'map.tool.clip'".</summary>
	public string Message => $"{Chord} is already bound to '{ExistingActionId}'; cannot bind it to '{RequestedActionId}'.";
}

/// <summary>Named failure reasons for keybind operations. <see cref="KeybindErrorCode.None"/> means success.</summary>
public enum KeybindErrorCode
{
	None,
	UnknownAction,
	Conflict,
	InvalidChord,
	DuplicateAction,
	NotBound,
	InvalidCatalogue,
}

/// <summary>Outcome of a Bind / Remap / Unbind / Import / Reset operation. Domain failures are data, not exceptions.</summary>
public sealed record KeybindResult( bool Ok, KeybindErrorCode Error, KeybindConflict? Conflict, string Message )
{
	public static KeybindResult Success( string message ) => new( true, KeybindErrorCode.None, null, message );
	public static KeybindResult Failed( KeybindErrorCode error, string message, KeybindConflict? conflict = null )
		=> new( false, error, conflict, message );
}

/// <summary>Named keybind error. The registry API reports domain failures as <see cref="KeybindResult"/> values; this exception is for catalogue construction failures (duplicate ids / duplicate default chords) where there is no result to return.</summary>
public sealed class KeybindRegistryException : Exception
{
	public KeybindRegistryException( KeybindErrorCode code, string message, KeybindConflict? conflict = null )
		: base( message )
	{
		Code = code;
		Conflict = conflict;
	}

	public KeybindErrorCode Code { get; }
	public KeybindConflict? Conflict { get; }
}

/// <summary>
/// A keyboard chord: zero or more modifiers (Ctrl / Alt / Shift) + one key.
/// Canonical text form is modifiers in Ctrl, Alt, Shift order followed by
/// the key, e.g. "Shift+B", "Ctrl+Shift+X", "Enter", "F9".
/// </summary>
public sealed record KeyChord( KeyModifiers Modifiers, string Key )
{
	static readonly HashSet<string> NamedKeys = new( StringComparer.OrdinalIgnoreCase )
	{
		"Enter", "Escape", "Space", "Tab", "Delete", "Backspace", "Insert",
		"Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right",
	};

	static string CanonicalKey( string token )
	{
		if ( token.Length == 1 && ( ( token[0] >= 'A' && token[0] <= 'Z' ) || ( token[0] >= 'a' && token[0] <= 'z' ) || ( token[0] >= '0' && token[0] <= '9' ) ) )
			return token.ToUpperInvariant();
		if ( token.Length >= 2 && token.Length <= 3 && ( token[0] == 'F' || token[0] == 'f' )
			&& int.TryParse( token.Substring( 1 ), out int f ) && f >= 1 && f <= 12 )
			return "F" + f;
		foreach ( string named in NamedKeys )
			if ( string.Equals( named, token, StringComparison.OrdinalIgnoreCase ) ) return named;
		return token;
	}

	static bool IsKey( string token )
	{
		if ( token.Length == 1 && ( ( token[0] >= 'A' && token[0] <= 'Z' ) || ( token[0] >= 'a' && token[0] <= 'z' ) || ( token[0] >= '0' && token[0] <= '9' ) ) ) return true;
		string canonical = CanonicalKey( token );
		return NamedKeys.Contains( canonical )
			|| ( canonical.Length >= 2 && canonical.Length <= 3 && canonical[0] == 'F' && int.TryParse( canonical.Substring( 1 ), out int f ) && f >= 1 && f <= 12 );
	}

	static bool TryModifier( string token, out KeyModifiers modifier )
	{
		switch ( token.ToUpperInvariant() )
		{
			case "CTRL":
			case "CONTROL": modifier = KeyModifiers.Ctrl; return true;
			case "ALT": modifier = KeyModifiers.Alt; return true;
			case "SHIFT": modifier = KeyModifiers.Shift; return true;
			default: modifier = KeyModifiers.None; return false;
		}
	}

	/// <summary>Parse a chord; throws <see cref="KeybindRegistryException"/> (InvalidChord) on bad input.</summary>
	public static KeyChord Parse( string text )
	{
		if ( !TryParse( text, out var chord, out string? error ) )
			throw new KeybindRegistryException( KeybindErrorCode.InvalidChord, error ?? $"Invalid chord '{text}'." );
		return chord!;
	}

	/// <summary>Parse a chord without throwing; false + a plain-words error on bad input.</summary>
	public static bool TryParse( string? text, out KeyChord? chord, out string? error )
	{
		chord = null;
		error = null;
		if ( string.IsNullOrWhiteSpace( text ) ) { error = "Chord must not be empty."; return false; }
		string[] parts = text.Split( '+' );
		var modifiers = KeyModifiers.None;
		for ( int i = 0; i < parts.Length; i++ )
		{
			string token = parts[i].Trim();
			if ( token.Length == 0 ) { error = $"Invalid chord '{text}': empty segment."; return false; }
			if ( i == parts.Length - 1 )
			{
				if ( TryModifier( token, out _ ) ) { error = $"Invalid chord '{text}': a chord needs a key, not just modifiers."; return false; }
				if ( !IsKey( token ) ) { error = $"Invalid chord '{text}': unknown key '{token}'."; return false; }
				chord = new KeyChord( modifiers, CanonicalKey( token ) );
				return true;
			}
			if ( !TryModifier( token, out var modifier ) ) { error = $"Invalid chord '{text}': '{token}' is not a modifier (Ctrl/Alt/Shift) and only the last segment may be a key."; return false; }
			if ( ( modifiers & modifier ) != KeyModifiers.None ) { error = $"Invalid chord '{text}': duplicate modifier '{token}'."; return false; }
			modifiers |= modifier;
		}
		error = $"Invalid chord '{text}'.";
		return false;
	}

	/// <summary>Canonical text: "Ctrl+Shift+X", "Shift+B", "Enter", "F9".</summary>
	public override string ToString()
	{
		string mods = "";
		if ( ( Modifiers & KeyModifiers.Ctrl ) != 0 ) mods += "Ctrl+";
		if ( ( Modifiers & KeyModifiers.Alt ) != 0 ) mods += "Alt+";
		if ( ( Modifiers & KeyModifiers.Shift ) != 0 ) mods += "Shift+";
		return mods + Key;
	}
}

/// <summary>Modifier flags for a <see cref="KeyChord"/>.</summary>
[Flags]
public enum KeyModifiers
{
	None = 0,
	Ctrl = 1,
	Alt = 2,
	Shift = 4,
}
