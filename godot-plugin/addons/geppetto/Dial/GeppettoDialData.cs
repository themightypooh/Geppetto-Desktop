#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>One workspace: its stages, each with its tools. Names are copied from Geppetto's s&amp;box code
/// (EffigyWorkspaceBar.cs, EffigyWindow.cs CreateTools, EffigyWindow.Workspaces.cs / .Clothing.cs and
/// Marionette's RigControlWindow.cs for Animate), the same table the approved dial mock uses.</summary>
public sealed record GeppettoWorkspace( string Name, string Tip, int DefaultStage, (string Stage, string[] Tools)[] Stages );

/// <summary>A material the dial can hold: a kernel look preset, a colour picked off the shades/HSL rings, or
/// a saved material from the library (<see cref="GeppettoMaterialLibrary"/>: Source "user" or "project",
/// Dir its folder). Source "new" is the ring's "+" ball.</summary>
public sealed record GeppettoDialMaterial( string Name, Color Color, float Rough, float Metal, SlotLook Look, string Source = "preset", string Dir = null )
{
	public string Hex => "#" + Color.ToHtml( false ).ToUpperInvariant();
	public bool IsNew => Source == "new";
	/// <summary>Saved in a library folder, so it can be edited, renamed and deleted.</summary>
	public bool Custom => Source is "user" or "project";

	public static readonly GeppettoDialMaterial New = new( "New material…", new Color( 0.25f, 0.26f, 0.28f ), 0.5f, 0f, new SlotLook(), "new" );

	public static GeppettoDialMaterial FromPreset( string name, SlotLook look ) =>
		new( name, new Color( look.Color.x, look.Color.y, look.Color.z ), look.Rough, look.Metal, look );

	public static GeppettoDialMaterial FromColor( Color c )
	{
		var look = new SlotLook { Color = new Vec3( c.R, c.G, c.B ), Rough = 0.6f, Metal = 0f };
		return new( "#" + c.ToHtml( false ).ToUpperInvariant(), c, 0.6f, 0f, look );
	}
}

public static class GeppettoDialData
{
	public static readonly GeppettoWorkspace[] Workspaces =
	{
		new( "CAD", "Sketches, solids and the feature tree", 1, new[] {
			( "Sketch", new[] { "Sketch", "Draw", "Shapes", "Modify", "Plane", "Primitive", "Import", "Profile" } ),
			( "Solid", new[] { "Part", "Extrude", "Revolve", "Sweep", "Loft", "Spline", "Boolean" } ),
			( "Detail", new[] { "Fillet", "Chamfer", "Shell", "Move Face", "Delete Face", "Draft", "Hole" } ),
			( "Repeat", new[] { "Mirror", "Linear Pattern", "Circular Pattern", "Transform", "Bend Along Curve", "Simple Deform", "Lattice" } ),
			( "Finish", new[] { "Face Material" } ) } ),
		new( "Model", "Edit vertices, edges and faces, or sculpt", 2, new[] {
			( "Select", new[] { "Vertex", "Edge", "Face", "Grow", "Shrink", "Linked", "Invert" } ),
			( "Transform", new[] { "Move", "Rotate", "Scale", "Edge slide", "Shrink/Fatten", "Bend" } ),
			( "Add", new[] { "Primitive", "Extrude", "Inset", "Bevel", "Duplicate", "Bridge", "Spin", "Fill", "Pipe" } ),
			( "Clean up", new[] { "Merge", "Dissolve", "Delete", "Smooth", "Relax", "Flip", "Triangulate" } ),
			( "Modifiers", new[] { "Crease", "Clear crease" } ),
			( "UV", new[] { "Mark seam", "Project UVs", "Cylinder projection", "Sphere projection" } ) } ),
		new( "Paint", "Lay colour onto a body's texture", 0, new[] {
			( "Channel", new[] { "Colour", "Roughness", "Metal", "Normal" } ),
			( "Layers", new[] { "New Layer", "Fill Layer" } ),
			( "Brush", new[] { "Brush radius", "Paint strength", "Channel value" } ) } ),
		new( "Rig", "Place bones and pin bodies to them", 0, new[] {
			( "Bones", new[] { "Add Bone", "Bone from Part", "Delete" } ),
			( "Bind", new[] { "Assign Body", "Mirror bones" } ),
			( "Soft", new[] { "Make Soft", "Preview", "Rest" } ),
			( "Weights", new[] { "Paint Weights" } ),
			( "Mesh", new[] { "Subdivide", "Remesh" } ),
			( "Export", new[] { "Check model", "Wrinkle Maps" } ) } ),
		new( "Clothing", "Load a wearer, cut a garment from it, grow fur", 1, new[] {
			( "Wearer", new[] { "Wearer" } ),
			( "Garment", new[] { "Garment", "Trim", "Fur" } ),
			( "Shape", new[] { "Shape garment" } ),
			( "Material", new[] { "Fabric" } ),
			( "Details", new[] { "Pocket", "Hardware", "Wear", "Glow" } ),
			( "Check", new[] { "Check", "Push out", "Clear marks" } ),
			( "Publish", new[] { "Make clothing", "Make live cloth" } ) } ),
		new( "Animate", "Marionette: pose, keyframe, play", 0, new[] {
			( "Pose", new[] { "Move  W", "Rotate  E", "Scale  R", "Key  K", "Auto-Key" } ),
			( "Constraints", new[] { "Two-bone IK", "Joint limit" } ),
			( "Scene", new[] { "Prop", "Light", "Camera", "First Person View" } ),
			( "Events", new[] { "Spawn model", "Morph event" } ),
			( "Export", new[] { "Export Animation…" } ) } ),
	};

	/// <summary>Tiles that open a further ring (level 3) instead of running a tool: "Workspace/Tile" → its tools.
	/// The sketch tools are s&amp;box's BuildSketchStages (Draw, Shapes, Modify), squeezed to 8 a ring.
	/// "Sketch spline/fillet/mirror" keep them apart from CAD's Solid Spline, Detail Fillet and Repeat Mirror.</summary>
	public static readonly Dictionary<string, string[]> Groups = new()
	{
		["CAD/Draw"] = new[] { "Select", "Line", "Midpoint line", "Point", "Sketch spline", "Arc", "3 point arc", "Construction" },
		["CAD/Shapes"] = new[] { "Rectangle", "Centre rectangle", "Circle", "3 point circle", "Polygon", "Outer polygon", "Slot", "Ellipse" },
		["CAD/Modify"] = new[] { "Trim", "Extend", "Sketch fillet", "Offset", "Sketch mirror", "Cut", "Project", "Finish sketch" },
	};

	public static string[] Group( string ws, string tile ) => Groups.TryGetValue( ws + "/" + tile, out var g ) ? g : null;

	/// <summary>Every tool name a workspace can run: its stage tools and the tools inside its groups.</summary>
	public static bool HasTool( GeppettoWorkspace w, string tool ) =>
		w.Stages.Any( st => st.Tools.Contains( tool ) ) || Groups.Any( kv => kv.Key.StartsWith( w.Name + "/" ) && kv.Value.Contains( tool ) );

	public static GeppettoWorkspace Workspace( string name ) =>
		Workspaces.FirstOrDefault( w => w.Name == name ) ?? Workspaces[0];

	/// <summary>Kernel look presets, the dial's MATERIALS ring.</summary>
	public static List<GeppettoDialMaterial> PresetMaterials() =>
		SlotLook.Presets.Select( kv => GeppettoDialMaterial.FromPreset( kv.Key, kv.Value ) ).ToList();

	/// <summary>The whole MATERIALS ring: the "+" ball that makes a new one (first, so it is always on
	/// page 1), saved materials (this project's, then the shared library), then the kernel presets.</summary>
	public static List<GeppettoDialMaterial> RingMaterials()
	{
		var list = new List<GeppettoDialMaterial> { GeppettoDialMaterial.New };
		list.AddRange( GeppettoMaterialLibrary.Scan() );
		list.AddRange( PresetMaterials() );
		return list;
	}

	// Icons on a 24px grid, drawn as round-capped strokes (SVG path data, copied from the mock).
	// Stages first, then tools; anything missing falls back to its stage's icon.
	static readonly Dictionary<string, string> Icons = new()
	{
		["Line"] = "M5 19 19 5 M3 21h4v-4H3z M17 7h4V3h-4z", ["Rectangle"] = "M4 6h16v12H4z", ["Circle"] = "M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16 M12 12h.01",
		["Arc"] = "M4 18A10 10 0 0 1 20 18 M3 20h2M19 20h2", ["Dimension"] = "M4 8v10M20 8v10 M4 13h16 M7 10l-3 3 3 3M17 10l3 3-3 3",
		["Sketch"] = "M4 19 9 6l5 9 3-5 3 4", ["Solid"] = "M4 8l8-4 8 4v8l-8 4-8-4z M4 8l8 4 8-4M12 12v8", ["Detail"] = "M5 19V9a4 4 0 0 1 4-4h10 M5 19h14",
		["Repeat"] = "M12 3v18 M3 7h6v10H3z M15 7h6v10h-6z", ["Finish"] = "M5 20 15 10 M14 5l5 5 M12 7l5 5-3 3-5-5z",
		["Select"] = "M5 4l13 7-6 1.5L9 19z", ["Transform"] = "M12 3v18M3 12h18 M9 6l3-3 3 3M9 18l3 3 3-3M6 9l-3 3 3 3M18 9l3 3-3 3",
		["Add"] = "M12 5v14M5 12h14", ["Clean up"] = "M6 19 17 8 M14 5l5 5 M4 20h7", ["Modifiers"] = "M4 7h10M18 7h2M4 17h2M10 17h10 M14 4v6M6 14v6",
		["UV"] = "M4 4h16v16H4z M4 12h16M12 4v16", ["Channel"] = "M12 3a9 9 0 1 0 0 18 M12 3v18 M12 8h6M12 13h8", ["Layers"] = "M12 3l9 5-9 5-9-5z M3 13l9 5 9-5",
		["Brush"] = "M4 20c3 0 4-1 4-3s2-3 3.5-1.5S20 4 20 4 10 9 11.5 10.5 10 13 8 13s-4 1-4 7z", ["Bones"] = "M7 17 17 7 M5 15a2 2 0 1 0 4 4 M15 5a2 2 0 1 0 4 4",
		["Bind"] = "M9 15l-3 3a3 3 0 0 1-4-4l3-3 M15 9l3-3a3 3 0 0 0-4-4l-3 3 M8 16l8-8", ["Soft"] = "M4 14c3-6 5-6 8 0s5 6 8 0", ["Weights"] = "M6 20h12l-2-11H8z M9 9a3 3 0 0 1 6 0",
		["Mesh"] = "M4 8l8-4 8 4v8l-8 4-8-4z M4 8l16 8M20 8 4 16M12 4v16", ["Export"] = "M12 4v11M7 10l5 5 5-5M4 20h16",
		["Wearer"] = "M12 3a2.5 2.5 0 1 0 0 5 2.5 2.5 0 1 0 0-5 M7 21v-7l-2-3 3-3h8l3 3-2 3v7", ["Garment"] = "M8 3 3 6l2 4 2-1v12h10V9l2 1 2-4-5-3c-.5 1.5-2 2.5-4 2.5S8.5 4.5 8 3z",
		["Shape"] = "M5 19c0-8 3-14 7-14s7 6 7 14", ["Material"] = "M4 4h7v7H4z M13 13h7v7h-7z M13 4h7v7h-7z M4 13h7v7H4z", ["Details"] = "M12 3l2.5 6H21l-5 4 2 7-6-4-6 4 2-7-5-4h6.5z",
		["Check"] = "M4 12l5 5L20 6", ["Publish"] = "M12 20V8 M7 13l5-5 5 5 M4 4h16",
		["Plane"] = "M3 15 9 7h12l-6 8z", ["Primitive"] = "M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16 M4 12h16", ["Import"] = "M12 4v11M7 10l5 5 5-5M4 20h16", ["Profile"] = "M5 19V5h6a4 4 0 0 1 0 8H5",
		["Part"] = "M5 5h14v14H5z", ["Extrude"] = "M5 15h14v5H5z M12 13V4M8 8l4-4 4 4", ["Revolve"] = "M12 3v18 M8 6a6 6 0 1 0 8 0", ["Sweep"] = "M4 18c4 0 4-12 8-12s4 12 8 12",
		["Loft"] = "M6 4h12M4 20h16M6 4 4 20M18 4l2 16", ["Spline"] = "M3 17C7 3 13 21 21 7", ["Boolean"] = "M9 5a6 6 0 1 0 0 12 6 6 0 1 0 0-12 M15 7a6 6 0 1 0 0 12 6 6 0 1 0 0-12",
		["Fillet"] = "M5 19V11a6 6 0 0 1 6-6h8", ["Chamfer"] = "M5 19v-9l5-5h9", ["Shell"] = "M4 6h16v13H4z M7 9h10v7H7z", ["Move Face"] = "M4 14h10v6H4z M9 11V3M6 6l3-3 3 3",
		["Delete Face"] = "M4 4h16v16H4z M8 8l8 8M16 8l-8 8", ["Draft"] = "M7 20 9 4h6l2 16z", ["Hole"] = "M12 7a5 5 0 1 0 0 10 5 5 0 1 0 0-10 M4 4h16v16H4z",
		["Mirror"] = "M12 3v18 M8 7 4 12l4 5z M16 7l4 5-4 5z", ["Linear Pattern"] = "M3 9h4v6H3zM10 9h4v6h-4zM17 9h4v6h-4z", ["Circular Pattern"] = "M12 3a2 2 0 1 0 0 4 2 2 0 1 0 0-4M19 10a2 2 0 1 0 0 4M5 10a2 2 0 1 0 0 4M12 17a2 2 0 1 0 0 4",
		["Bend Along Curve"] = "M3 18c6 0 8-12 18-12 M6 15l2 3M10 11l2 3", ["Simple Deform"] = "M6 4c4 4 4 12 0 16M18 4c-4 4-4 12 0 16", ["Lattice"] = "M4 4h16v16H4zM9 4v16M15 4v16M4 9h16M4 15h16",
		["Face Material"] = "M4 4h16v16H4z M4 4l16 16", ["Vertex"] = "M12 9a3 3 0 1 0 0 6 3 3 0 1 0 0-6", ["Edge"] = "M5 19 19 5", ["Face"] = "M4 6h16v12H4z",
		["Move"] = "M12 3v18M3 12h18M9 6l3-3 3 3M9 18l3 3 3-3", ["Rotate"] = "M20 12a8 8 0 1 1-3-6.3 M20 4v5h-5", ["Scale"] = "M4 20 20 4 M14 4h6v6M4 14v6h6",
		["Inset"] = "M4 4h16v16H4z M8 8h8v8H8z", ["Bevel"] = "M5 19v-9l5-5h9", ["Bridge"] = "M3 8h5v8H3zM16 8h5v8h-5z M8 10h8M8 14h8", ["Merge"] = "M5 5l7 7 7-7 M12 12v8",
		["Smooth"] = "M3 14c3-4 6-4 9 0s6 4 9 0", ["Delete"] = "M5 7h14M10 11v6M14 11v6M7 7l1 13h8l1-13M9 7V4h6v3", ["Mark seam"] = "M4 20 20 4 M7 13l4 4M11 9l4 4",
		["Add Bone"] = "M7 17 17 7 M17 13v6M14 16h6", ["Bone from Part"] = "M4 4h8v8H4z M10 14l6-6 M16 8a2 2 0 1 0 0 .1", ["Assign Body"] = "M12 3l9 5-9 5-9-5z M12 13v8",
		["Paint Weights"] = "M4 20c3 0 4-1 4-3s2-3 3.5-1.5S20 4 20 4", ["Check model"] = "M4 12l5 5L20 6", ["Trim"] = "M4 14c2-3 4-3 6 0s4 3 6 0 4-3 4 0", ["Fur"] = "M5 20c0-6 2-12 2-12M10 20c0-7 1-14 1-14M15 20c0-6 2-12 2-12",
		["Pose"] = "M12 3a2 2 0 1 0 0 4 2 2 0 1 0 0-4 M12 7v7 M6 9l6 2 6-4 M12 14l-4 7M12 14l4 7",
		["Constraints"] = "M7 7a3 3 0 1 0 0 .1 M17 17a3 3 0 1 0 0 .1 M9 9l6 6", ["Scene"] = "M3 7h13v10H3z M16 10l5-3v10l-5-3", ["Events"] = "M13 3 5 14h6l-1 7 8-11h-6z",
		["Key  K"] = "M12 4l8 8-8 8-8-8z", ["Auto-Key"] = "M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16 M12 8v4l3 2", ["Two-bone IK"] = "M4 20 10 10l10-4 M10 10a1.5 1.5 0 1 0 0 .1",
		["Light"] = "M9 18h6M10 21h4 M12 3a6 6 0 0 0-4 10.5c.7.6 1 1.5 1 2.5h6c0-1 .3-1.9 1-2.5A6 6 0 0 0 12 3z", ["Camera"] = "M3 7h13v10H3z M16 10l5-3v10l-5-3",
		// Workspace tiles (the ring's first level).
		["ws:CAD"] = "M4 8l8-4 8 4v8l-8 4-8-4z M4 8l8 4 8-4M12 12v8", ["ws:Model"] = "M4 8l8-4 8 4v8l-8 4-8-4z M4 8l16 8M20 8 4 16M12 4v16",
		["ws:Paint"] = "M4 20c3 0 4-1 4-3s2-3 3.5-1.5S20 4 20 4 10 9 11.5 10.5 10 13 8 13s-4 1-4 7z",
		["ws:Rig"] = "M12 2.5a2.4 2.4 0 1 0 0 .1 M12 6v7.5 M12 13.5l-4.5 6.5M12 13.5l4.5 6.5 M6 9.5h12",
		["ws:Clothing"] = "M8 3 3 6l2 4 2-1v12h10V9l2 1 2-4-5-3c-.5 1.5-2 2.5-4 2.5S8.5 4.5 8 3z",
		["ws:Animate"] = "M12 2.5a2.4 2.4 0 1 0 0 .1 M12 6v7 M6 8.5l6 2 6-4 M12 13l-4 7.5M12 13l4 7.5",
		// CAD > Sketch groups and their tools ("sk:" names win over a same-named tool elsewhere).
		["Draw"] = "M4 20l3-1L19 7l-2-2L5 17z M15 7l2 2", ["Shapes"] = "M3 4h8v8H3z M17 13a4 4 0 1 0 0 8 4 4 0 1 0 0-8 M15 3l5 8h-10z",
		["Modify"] = "M6 4a2 2 0 1 0 0 4 2 2 0 1 0 0-4 M6 16a2 2 0 1 0 0 4 2 2 0 1 0 0-4 M8 7l12 9M8 17l12-9",
		["sk:Select"] = "M5 4l13 7-6 1.5L9 19z", ["sk:Line"] = "M5 19 19 5 M5 19a1.5 1.5 0 1 0 0 .1M19 5a1.5 1.5 0 1 0 0 .1",
		["sk:Midpoint line"] = "M4 20 20 4 M12 9a3 3 0 1 0 0 6 3 3 0 1 0 0-6", ["sk:Point"] = "M12 9a3 3 0 1 0 0 6 3 3 0 1 0 0-6 M12 3v3M12 18v3M3 12h3M18 12h3",
		["sk:Sketch spline"] = "M3 17C7 3 13 21 21 7 M3 17a1.5 1.5 0 1 0 0 .1 M21 7a1.5 1.5 0 1 0 0 .1", ["sk:Arc"] = "M4 18a8 8 0 0 1 16 0 M12 18h.1",
		["sk:3 point arc"] = "M4 18a8 8 0 0 1 16 0 M4 18h.1M12 10h.1M20 18h.1", ["sk:Construction"] = "M4 20l2-2M8 16l2-2M12 12l2-2M16 8l2-2M20 4h.1",
		["sk:Rectangle"] = "M4 6h16v12H4z", ["sk:Centre rectangle"] = "M4 6h16v12H4z M12 11v2M11 12h2",
		["sk:Circle"] = "M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16 M12 12h.1", ["sk:3 point circle"] = "M12 4a8 8 0 1 0 0 16 8 8 0 1 0 0-16 M12 4h.1M5 16h.1M19 16h.1",
		["sk:Polygon"] = "M12 3l8 5v8l-8 5-8-5V8z", ["sk:Outer polygon"] = "M12 4l7 4v8l-7 4-7-4V8z M12 6a6 6 0 1 0 0 12 6 6 0 1 0 0-12",
		["sk:Slot"] = "M8 7h8a5 5 0 0 1 0 10H8A5 5 0 0 1 8 7z", ["sk:Ellipse"] = "M12 6c5 0 9 2.7 9 6s-4 6-9 6-9-2.7-9-6 4-6 9-6z",
		["sk:Trim"] = "M4 12h6M14 12h6 M12 4v16 M9 9l6 6M15 9l-6 6", ["sk:Extend"] = "M4 12h10 M14 12h6 M18 9l3 3-3 3 M20 4v16",
		["sk:Sketch fillet"] = "M5 19V11a6 6 0 0 1 6-6h8", ["sk:Offset"] = "M4 18 14 8 M8 20l10-10 M14 8h4v4",
		["sk:Sketch mirror"] = "M12 3v18 M8 7 4 12l4 5z M16 7l4 5-4 5z", ["sk:Cut"] = "M6 6a2 2 0 1 0 0 .1M6 18a2 2 0 1 0 0 .1 M8 7l12 10M8 17l12-10",
		["sk:Project"] = "M3 15 9 7h12l-6 8z M12 3v8M9 8l3 3 3-3", ["sk:Finish sketch"] = "M4 12l5 5L20 6",
		["__back"] = "M10 6 4 12l6 6M4 12h16",
		["__move"] = "M12 3v18M3 12h18M9 6l3-3 3 3M9 18l3 3 3-3M6 9l-3 3 3 3M18 9l3 3-3 3",
		["__size"] = "M5 19 19 5 M13 5h6v6 M5 13v6h6",
	};


	static readonly Dictionary<string, string> Alias = new() { ["Move  W"] = "Move", ["Rotate  E"] = "Rotate", ["Scale  R"] = "Scale" };

	/// <summary>"Move  W" → "Move": the shortcut letter is only for the s&amp;box tooltip.</summary>
	public static string Clean( string tool ) =>
		tool.Length > 3 && tool[^3] == ' ' && tool[^2] == ' ' && char.IsUpper( tool[^1] ) ? tool[..^3] : tool;

	public static string IconPath( string name, string stage )
	{
		if ( Icons.TryGetValue( name, out var p ) ) return p;
		if ( Alias.TryGetValue( name, out var a ) && Icons.TryGetValue( a, out p ) ) return p;
		if ( stage != null && Icons.TryGetValue( stage, out p ) ) return p;
		return Icons["Add"];
	}

	static readonly Dictionary<string, ImageTexture> IconCache = new();

	/// <summary>Rasterises an icon path at the size it is drawn, so it stays sharp at any dial scale.</summary>
	public static ImageTexture Icon( string path, Color stroke, int px, float strokeWidth = 2.1f )
	{
		px = Math.Clamp( px, 6, 256 );
		var key = $"{path}|{stroke.ToHtml()}|{px}|{strokeWidth}";
		if ( IconCache.TryGetValue( key, out var tex ) && IsInstanceValid( tex ) ) return tex;
		var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\">" +
			$"<path d=\"{path}\" fill=\"none\" stroke=\"#{stroke.ToHtml( false )}\" stroke-opacity=\"{stroke.A:0.###}\" stroke-width=\"{strokeWidth:0.##}\" " +
			"stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";
		var img = new Image();
		img.LoadSvgFromString( svg, px / 24f );
		tex = ImageTexture.CreateFromImage( img );
		IconCache[key] = tex;
		return tex;
	}

	/// <summary>Geppetto Desktop's pixel tool icons (Dial/icons, copied from cabinet/art/tools), by dial name.
	/// Keys are "Workspace/Name" first, then "Name"; workspaces are "ws:Name". Anything missing keeps its vector icon.</summary>
	static readonly Dictionary<string, string> Desk = new()
	{
		["ws:CAD"] = "primitive",
		["ws:Model"] = "meshedit",
		["ws:Paint"] = "paint",
		["ws:Rig"] = "bind",
		["ws:Clothing"] = "garment",
		["CAD/Sketch"] = "sketch",
		["Plane"] = "plane",
		["Primitive"] = "primitive",
		["Import"] = "import",
		["Profile"] = "profile",
		["Solid"] = "extrude",
		["Part"] = "part",
		["CAD/Extrude"] = "extrude",
		["Revolve"] = "revolve",
		["Sweep"] = "sweep",
		["Loft"] = "loft",
		["Spline"] = "spline",
		["Boolean"] = "boolean",
		["Detail"] = "fillet",
		["Fillet"] = "fillet",
		["Chamfer"] = "chamfer",
		["Shell"] = "shell",
		["Move Face"] = "moveface",
		["Delete Face"] = "deleteface",
		["Draft"] = "draft",
		["Hole"] = "hole",
		["Repeat"] = "linearpattern",
		["CAD/Mirror"] = "mirror",
		["Linear Pattern"] = "linearpattern",
		["Circular Pattern"] = "circularpattern",
		["CAD/Transform"] = "transform",
		["Bend Along Curve"] = "curvedeform",
		["Simple Deform"] = "simpledeform",
		["Lattice"] = "lattice",
		["Finish"] = "facematerial",
		["Face Material"] = "facematerial",
		["Model/Transform"] = "transform",
		["Model/Add"] = "edit_extrude",
		["Model/Extrude"] = "edit_extrude",
		["Inset"] = "edit_inset",
		["Model/Bevel"] = "chamfer",
		["Bridge"] = "edit_bridge",
		["Clean up"] = "edit_dissolve",
		["Dissolve"] = "edit_dissolve",
		["Model/Delete"] = "edit_delete",
		["UV"] = "uvproject",
		["Project UVs"] = "uvproject",
		["Cylinder projection"] = "uvproject",
		["Sphere projection"] = "uvproject",
		["Paint/Brush"] = "paint",
		["Channel"] = "paintmaps",
		["Rig/Bind"] = "bind",
		["Assign Body"] = "bind",
		["Rig/Delete"] = "remove",
		["Soft"] = "flex",
		["Make Soft"] = "flex",
		["Weights"] = "skin",
		["Paint Weights"] = "skin",
		["Rig/Mesh"] = "remesh",
		["Subdivide"] = "subdivide",
		["Remesh"] = "remesh",
		["Rig/Export"] = "topology",
		["Check model"] = "topology",
		["Wrinkle Maps"] = "correctiveshapekey",
		["Clothing/Wearer"] = "wearer",
		["Clothing/Garment"] = "garment",
		["Garment"] = "garment",
		["Clothing/Trim"] = "trim",
		["Fur"] = "fur",
		["Clothing/Material"] = "fabric",
		["Fabric"] = "fabric",
		["Details"] = "pocket",
		["Pocket"] = "pocket",
		["Hardware"] = "hardware",
		["Wear"] = "wear",
		["Glow"] = "glow",
		["Clothing/Check"] = "clearance",
		["Push out"] = "clearance",
		["Publish"] = "export",
		["Make clothing"] = "bake",
		["Animate/Export"] = "export",
		["Export Animation…"] = "export",
	};

	static readonly Dictionary<string, ImageTexture> DeskCache = new();

	/// <summary>The Desktop pixel icon for a dial tile at px, or null. Scaled nearest-neighbour so the pixels stay crisp.</summary>
	public static ImageTexture DeskIcon( string ws, string name, int px )
	{
		name = Clean( name );
		if ( !Desk.TryGetValue( ws + "/" + name, out var file ) && !Desk.TryGetValue( name, out file ) ) return null;
		px = Math.Clamp( px, 6, 256 );
		var key = $"{file}|{px}";
		if ( DeskCache.TryGetValue( key, out var tex ) && IsInstanceValid( tex ) ) return tex;
		var img = Image.LoadFromFile( ProjectSettings.GlobalizePath( $"res://addons/geppetto/Dial/icons/{file}.png" ) );
		if ( img == null || img.IsEmpty() ) return null;
		// the art is drawn in 2px blocks, so half size and up stay crisp with nearest
		img.Resize( px, px, px * 2 >= img.GetWidth() && px % 30 == 0 || px >= img.GetWidth() ? Image.Interpolation.Nearest : Image.Interpolation.Cubic );
		tex = ImageTexture.CreateFromImage( img );
		DeskCache[key] = tex;
		return tex;
	}

	static bool IsInstanceValid( GodotObject o ) => GodotObject.IsInstanceValid( o );

	static readonly Dictionary<string, ImageTexture> BallCache = new();

	/// <summary>A shaded material ball: radial gradient from a light upper-left to a dark rim, plus a
	/// soft highlight, like the mock's balls. Rendered once per colour and size.</summary>
	public static ImageTexture Ball( Color c, int px ) => Ball( c, 0.5f, 0f, px );

	/// <summary>A ball for a material: rough spreads and dims the highlight, metal darkens the body and
	/// tints the highlight with the colour.</summary>
	public static ImageTexture Ball( GeppettoDialMaterial m, int px ) => Ball( m.Color, m.Rough, m.Metal, px );

	public static ImageTexture Ball( Color c, float rough, float metal, int px )
	{
		px = Math.Clamp( px, 8, 256 );
		rough = Mathf.Clamp( rough, 0f, 1f ); metal = Mathf.Clamp( metal, 0f, 1f );
		var key = $"{c.ToHtml()}|{rough:0.00}|{metal:0.00}|{px}";
		if ( BallCache.TryGetValue( key, out var tex ) && IsInstanceValid( tex ) ) return tex;
		var img = Image.CreateEmpty( px, px, false, Image.Format.Rgba8 );
		float r = px / 2f, cx = r, cy = r;
		var lightC = c.Lerp( Colors.White, 0.55f );
		var darkC = c.Lerp( Colors.Black, 0.62f );
		float gx = px * 0.38f, gy = px * 0.32f, gr = px * 0.75f;
		for ( int y = 0; y < px; y++ )
			for ( int x = 0; x < px; x++ )
			{
				float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
				float d = Mathf.Sqrt( dx * dx + dy * dy );
				float cover = Mathf.Clamp( r - d, 0f, 1f );
				if ( cover <= 0f ) { img.SetPixel( x, y, Colors.Transparent ); continue; }
				float t = Mathf.Sqrt( ( x + 0.5f - gx ) * ( x + 0.5f - gx ) + ( y + 0.5f - gy ) * ( y + 0.5f - gy ) ) / gr;
				var col = t < 0.35f ? lightC.Lerp( c, t / 0.35f ) : c.Lerp( darkC, Mathf.Clamp( ( t - 0.35f ) / 0.65f, 0f, 1f ) );
				col = col.Lerp( darkC, 0.45f * metal * Mathf.Clamp( t, 0f, 1f ) );
				// highlight ellipse at (-0.3r, -0.38r): small and bright when smooth, wide and faint when rough
				float spread = Mathf.Lerp( 0.6f, 1.6f, rough );
				float hx = ( dx + r * 0.3f ) / ( r * 0.32f * spread ), hy = ( dy + r * 0.38f ) / ( r * 0.18f * spread );
				float h = 1f - Mathf.Clamp( Mathf.Sqrt( hx * hx + hy * hy ), 0f, 1f );
				var spec = Colors.White.Lerp( c.Lerp( Colors.White, 0.4f ), metal );
				col = col.Lerp( spec, Mathf.Lerp( 0.75f, 0.2f, rough ) * Mathf.Clamp( h * 3f, 0f, 1f ) );
				col.A = cover;
				img.SetPixel( x, y, col );
			}
		tex = ImageTexture.CreateFromImage( img );
		BallCache[key] = tex;
		return tex;
	}
}
#endif
