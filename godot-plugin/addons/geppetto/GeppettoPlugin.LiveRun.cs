#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Effigy;
using Godot;

namespace Geppetto;

// GEPPETTO_WINDOW_WS=Live: opens the scene, selects the character, then stays open for real input from
// outside (XTest clicks into the headless display). Each file "<name>.req" dropped in the run folder
// becomes "<name>.png": the editor window with every open popup and dialog drawn over it where it sits,
// plus a "<name>.txt" with what the plugin printed since the last shot. Lets an audit see what a real
// click does instead of what a signal emitted from a test does.
public partial class GeppettoPlugin
{
	async Task LiveRun()
	{
		try
		{
			await Wait( 4 );
			string scene = System.Environment.GetEnvironmentVariable( "GEPPETTO_WINDOW_SCENE" ) ?? "res://e2e/room.tscn";
			EditorInterface.Singleton.OpenSceneFromPath( scene );
			await Wait( 2 );
			SelectCharacter();
			await Wait( 1 );
			GD.Print( $"[Live] ready, character={_character?.EffigyPath ?? "none"}" );
			File.WriteAllText( Path.Combine( _runOut, "ready" ), "" );
			while ( true )
			{
				await Wait( 0.25 );
				foreach ( var req in Directory.GetFiles( _runOut, "*.req" ) )
				{
					string name = Path.GetFileNameWithoutExtension( req );
					File.Delete( req );
					if ( _character == null ) SelectCharacter();
					await ToSignal( RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw );
					LiveShot( name );
				}
			}
		}
		catch ( Exception e ) { GD.PrintErr( "[Live] " + e ); }
	}

	void LiveShot( string name )
	{
		var main = EditorInterface.Singleton.GetBaseControl().GetWindow();
		var img = main.GetTexture().GetImage();
		img.Convert( Image.Format.Rgba8 );
		var origin = main.Position;
		var stack = new System.Collections.Generic.Stack<Node>();
		stack.Push( main );
		var lines = new System.Collections.Generic.List<string>();
		while ( stack.Count > 0 )
		{
			var n = stack.Pop();
			foreach ( var c in n.GetChildren() )
			{
				stack.Push( c );
				if ( c is not Window w || !w.Visible || w == main ) continue;
				var wi = w.GetTexture()?.GetImage();
				lines.Add( $"window {w.GetType().Name} '{w.Title}' at {w.Position - origin} size {w.Size} embedded={w.IsEmbedded()}" );
				foreach ( Node child in w.FindChildren( "*", "Control" ) )
				{
					if ( child is Control ctrl && ctrl.Visible && ( ctrl is LineEdit || ctrl is OptionButton || ctrl is EditorSpinSlider || ctrl is Button ) )
					{
						var gr = ctrl.GetGlobalRect();
						lines.Add( $"  ctrl {ctrl.GetType().Name} name={ctrl.Name} pos=({gr.Position.X - w.Position.X},{gr.Position.Y - w.Position.Y}) size={gr.Size}" );
					}
				}
				if ( wi == null || w.IsEmbedded() ) continue;
				wi.Convert( Image.Format.Rgba8 );
				img.BlendRect( wi, new Rect2I( Vector2I.Zero, wi.GetSize() ), w.Position - origin );
			}
		}
		lines.Add( $"active tool: {GeppettoTools.Active?.Name ?? "none"}" );
		lines.Add( $"character: {_character?.EffigyPath ?? "none"} features={_character?.Doc?.Studio.Features.Count}" );
		if ( _character?.Doc?.Studio is { } st )
		{
			foreach ( var f in st.Features )
			{
				string pstr = string.Join( ", ", f.Parameters.Select( p => $"{p.Label}={(p is FloatParam fp ? fp.Value.ToString() : p is ChoiceParam cp ? (cp.Index >= 0 && cp.Index < cp.Options.Length ? cp.Options[cp.Index] : cp.Index.ToString()) : p.ToString())}" ) );
				lines.Add( $"feature {f.TypeName} '{f.Name}' id={f.Id}: {pstr}" );
			}
		}
		lines.Add( $"mouse: {DisplayServer.MouseGetPosition()} selected: {string.Join( ",", EditorInterface.Singleton.GetSelection().GetSelectedNodes().Select( n => n.Name.ToString() ) )}" );
		if ( _character?.Root is Node croot )
		{
			var meshes = new System.Collections.Generic.Stack<Node>();
			meshes.Push( croot );
			while ( meshes.Count > 0 )
			{
				var n = meshes.Pop();
				foreach ( var c in n.GetChildren() ) meshes.Push( c );
				if ( n is MeshInstance3D mi )
					lines.Add( $"mesh {mi.Name} visible={mi.IsVisibleInTree()} surfaces={mi.Mesh?.GetSurfaceCount() ?? -1} aabb={mi.GetAabb()}" );
			}
		}
		if ( _character?.Doc?.LastReport is { } rep ) lines.Add( $"report errors={rep.Errors.Count} first={rep.Errors.FirstOrDefault()}" );
		img.SavePng( Path.Combine( _runOut, name + ".png" ) );
		File.WriteAllLines( Path.Combine( _runOut, name + ".txt" ), lines );
	}
}
#endif
