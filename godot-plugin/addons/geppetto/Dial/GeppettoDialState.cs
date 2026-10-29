#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Geppetto;

/// <summary>
/// The dial's state, shared by the dial drawn in each 3D viewport: where the dial sits, which level of
/// the ring is open, the active workspace / stage / tool, the last stage and tool used in each workspace
/// (Linework's lastInGroup), the current material and the 1–8 recents. Persisted per project in the
/// editor's project metadata, so it comes back where it was left.
/// </summary>
public static class GeppettoDialState
{
	const string Section = "geppetto_dial";
	public const int MaxRecent = 8;

	// Ring levels: 0 = workspaces, 1 = the open workspace's stages, 2 = the open stage's tools.
	public static int Level;
	public static int Ws;
	public static int OpenStage;                       // stage shown at level 2
	public static readonly int[] LastStage = new int[GeppettoDialData.Workspaces.Length];
	public static readonly string[] LastTool = new string[GeppettoDialData.Workspaces.Length];

	public static Vector2 Offset;                      // screen px from the default bottom-left spot
	public static float Scale = 1f;
	public static bool MatOpen;
	public static string MatMode = "materials";        // materials | shades | hsl
	public static int MatPage;
	public static bool ForceShow;
	/// <summary>Screenshot runs: never write the dial's state into the project metadata.</summary>
	public static bool NoSave;

	public static List<GeppettoDialMaterial> Materials = new();
	public static GeppettoDialMaterial Current;
	public static readonly List<GeppettoDialMaterial> Recent = new();

	// Animation clocks (msec): the ring fanning to a new level, and the material ring popping open.
	public static ulong FanStart, RingStart;

	public static event Action Changed;
	public static event Action<string, string> ToolPicked;      // workspace, tool
	public static event Action<GeppettoDialMaterial> MaterialPicked;
	/// <summary>The "+" ball was clicked: make a new material.</summary>
	public static event Action NewMaterialAsked;
	/// <summary>A ball or recent was right-clicked: material, screen position for the menu.</summary>
	public static event Action<GeppettoDialMaterial, Vector2> MaterialMenuAsked;

	static bool _loaded;

	public static GeppettoWorkspace Workspace => GeppettoDialData.Workspaces[Ws];
	public static string CurrentTool => LastTool[Ws] ?? Workspace.Stages[LastStage[Ws]].Tools[0];

	public static void Load()
	{
		if ( _loaded ) return;
		_loaded = true;
		for ( int i = 0; i < GeppettoDialData.Workspaces.Length; i++ )
		{
			LastStage[i] = GeppettoDialData.Workspaces[i].DefaultStage;
			LastTool[i] = null;
		}
		Materials = GeppettoDialData.RingMaterials();
		Current = Materials.FirstOrDefault( m => !m.IsNew );

		var es = EditorInterface.Singleton.GetEditorSettings();
		Variant Get( string key, Variant def ) => es.GetProjectMetadata( Section, key, def );
		Offset = Get( "offset", Vector2.Zero ).AsVector2();
		Scale = Mathf.Clamp( (float)Get( "scale", 1f ).AsDouble(), 0.6f, 1.5f );
		Ws = Math.Clamp( Get( "workspace", 0 ).AsInt32(), 0, GeppettoDialData.Workspaces.Length - 1 );
		ForceShow = Get( "force_show", true ).AsBool();
		var stages = Get( "last_stage", new Godot.Collections.Dictionary() ).AsGodotDictionary();
		var tools = Get( "last_tool", new Godot.Collections.Dictionary() ).AsGodotDictionary();
		for ( int i = 0; i < GeppettoDialData.Workspaces.Length; i++ )
		{
			var w = GeppettoDialData.Workspaces[i];
			if ( stages.TryGetValue( w.Name, out var s ) ) LastStage[i] = Math.Clamp( s.AsInt32(), 0, w.Stages.Length - 1 );
			if ( tools.TryGetValue( w.Name, out var t ) && w.Stages.Any( st => st.Tools.Contains( t.AsString() ) ) ) LastTool[i] = t.AsString();
		}
		var cur = Get( "material", "" ).AsString();
		Current = FindMaterial( cur ) ?? Current;
		foreach ( var r in Get( "recent", Array.Empty<string>() ).AsStringArray() )
		{
			var m = FindMaterial( r );
			if ( m != null && Recent.Count < MaxRecent ) Recent.Add( m );
		}
		if ( Recent.Count == 0 && Current != null ) Recent.Add( Current );
	}

	static GeppettoDialMaterial FindMaterial( string key )
	{
		if ( string.IsNullOrEmpty( key ) ) return null;
		var m = Materials.FirstOrDefault( x => x.Name == key );
		if ( m != null ) return m;
		return key.StartsWith( '#' ) && Color.HtmlIsValid( key ) ? GeppettoDialMaterial.FromColor( Color.FromHtml( key ) ) : null;
	}

	public static void Save()
	{
		if ( NoSave ) return;
		var es = EditorInterface.Singleton.GetEditorSettings();
		var stages = new Godot.Collections.Dictionary();
		var tools = new Godot.Collections.Dictionary();
		for ( int i = 0; i < GeppettoDialData.Workspaces.Length; i++ )
		{
			stages[GeppettoDialData.Workspaces[i].Name] = LastStage[i];
			if ( LastTool[i] != null ) tools[GeppettoDialData.Workspaces[i].Name] = LastTool[i];
		}
		es.SetProjectMetadata( Section, "offset", Offset );
		es.SetProjectMetadata( Section, "scale", Scale );
		es.SetProjectMetadata( Section, "workspace", Ws );
		es.SetProjectMetadata( Section, "force_show", ForceShow );
		es.SetProjectMetadata( Section, "last_stage", stages );
		es.SetProjectMetadata( Section, "last_tool", tools );
		es.SetProjectMetadata( Section, "material", Current?.Name ?? "" );
		es.SetProjectMetadata( Section, "recent", Recent.Select( r => r.Name ).ToArray() );
	}

	public static void Touch() => Changed?.Invoke();

	static void Fan() => FanStart = Time.GetTicksMsec();

	// ---- ring navigation ----

	/// <summary>Level 1: open a workspace. The ring fans into its stages; its last stage is the active one.</summary>
	public static void OpenWorkspace( int ws )
	{
		Ws = ws; Level = 1; Fan(); Save(); Touch();
	}

	/// <summary>Level 2: open a stage. The ring fans into its tools.</summary>
	public static void OpenStageTools( int stage )
	{
		OpenStage = stage; LastStage[Ws] = stage; Level = 2; Fan(); Save(); Touch();
	}

	public static void Back()
	{
		if ( Level > 0 ) { Level--; Fan(); Touch(); }
	}

	public static void PickTool( string tool )
	{
		LastStage[Ws] = OpenStage;
		LastTool[Ws] = tool;
		Save(); Touch();
		ToolPicked?.Invoke( Workspace.Name, tool );
	}

	public static void ToggleMaterials()
	{
		MatOpen = !MatOpen;
		if ( MatOpen ) { RingStart = Time.GetTicksMsec(); ReloadMaterials(); }
		Touch();
	}

	public static void SetMatMode( string mode ) { MatMode = mode; Touch(); }

	public static void PageMaterials( int dir, int perPage )
	{
		int pages = Math.Max( 1, ( Materials.Count + perPage - 1 ) / perPage );
		MatPage = ( ( MatPage + dir ) % pages + pages ) % pages;
		Touch();
	}

	/// <summary>Makes a material current and puts it first in the recents (keys 1–8).</summary>
	public static void PickMaterial( GeppettoDialMaterial m, bool remember = true )
	{
		if ( m == null ) return;
		Current = m;
		if ( remember )
		{
			Recent.RemoveAll( r => r.Name == m.Name );
			Recent.Insert( 0, m );
			if ( Recent.Count > MaxRecent ) Recent.RemoveRange( MaxRecent, Recent.Count - MaxRecent );
			Save();
			MaterialPicked?.Invoke( m );
		}
		Touch();
	}

	/// <summary>Re-reads the saved materials (after a save, rename or delete, or when the ring opens).
	/// Current and recents follow their names; ones that are gone drop out.</summary>
	public static void ReloadMaterials()
	{
		Materials = GeppettoDialData.RingMaterials();
		GeppettoDialMaterial Fresh( GeppettoDialMaterial m ) => m.Source == "preset" && m.Name.StartsWith( '#' ) ? m : Materials.FirstOrDefault( x => x.Name == m.Name );
		for ( int i = Recent.Count - 1; i >= 0; i-- )
		{
			var f = Fresh( Recent[i] );
			if ( f == null ) Recent.RemoveAt( i ); else Recent[i] = f;
		}
		if ( Current != null ) Current = Fresh( Current ) ?? Materials.FirstOrDefault( m => !m.IsNew );
		int pages = Math.Max( 1, ( Materials.Count + 11 ) / 12 );
		MatPage = Math.Min( MatPage, pages - 1 );
		Save(); Touch();
	}

	public static void AskNewMaterial() => NewMaterialAsked?.Invoke();
	public static void AskMaterialMenu( GeppettoDialMaterial m, Vector2 screen ) { if ( m != null && !m.IsNew ) MaterialMenuAsked?.Invoke( m, screen ); }

	public static void PickRecent( int index )
	{
		if ( index >= 0 && index < Recent.Count ) PickMaterial( Recent[index] );
	}
}
#endif
