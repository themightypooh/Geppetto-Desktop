#if TOOLS
#nullable enable
using System.IO;
using System.Threading.Tasks;
using Godot;

namespace Geppetto;

// s&box's Effigy window always has a document open, so CAD and Model tools never had "nothing to work on".
// In Poohdot the document is a Geppetto character in the scene. When the scene has none, a CAD or Model
// tool makes one first: an empty .effigy saved under res://models, imported, and added to the edited scene
// at the origin as one undo step (a real file and a real saved node, nothing generated at runtime).
public partial class GeppettoPlugin
{
	const string NewModelDir = "res://models";
	const string NewSceneFile = "res://geppetto_scene.tscn";

	bool _makingModel;

	/// <summary>The character a tool works on: the selected one, else the scene's only or first one.</summary>
	GeppettoCharacter? FindCharacter()
	{
		if ( _character != null && IsInstanceValid( _character.Root ) && _character.Root.IsInsideTree() ) return _character;
		return _character = GeppettoCharacter.Find( null );
	}

	/// <summary>With no character in the scene, a CAD or Model tool first makes a new model, then runs
	/// <paramref name="tool"/> on it. True when that was started (the tool runs once the import lands).</summary>
	bool StartWithNewModel( string ws, string tool )
	{
		if ( ws is not ("CAD" or "Model") ) return false;
		if ( _makingModel ) return true;
		_makingModel = true;
		_ = NewModelThen( ws, tool );
		return true;
	}

	async Task NewModelThen( string ws, string tool )
	{
		try
		{
			if ( await NewModel() is { } made )
			{
				_character = made;
				RefreshDial();
				RunTool( ws, tool );
			}
		}
		catch ( System.Exception e ) { GeppettoToolContext.Warn( $"Could not make a new model: {e.Message}" ); }
		finally { _makingModel = false; }
	}

	/// <summary>Writes an empty .effigy, imports it and instances it at the origin of the edited scene (a new
	/// 3D scene is made first when none is open). It is not selected: a selection puts Godot's move/rotate
	/// gizmo on it, which buries the planes Sketch asks you to click. The tools and the Features dock find it
	/// as the scene's first character instead (GeppettoCharacter.Find).</summary>
	async Task<GeppettoCharacter?> NewModel()
	{
		var root = EditorInterface.Singleton.GetEditedSceneRoot();
		if ( root == null )
		{
			string scene = ProjectSettings.GlobalizePath( NewSceneFile );
			if ( !File.Exists( scene ) )
			{
				var packed = new PackedScene();
				var node = new Node3D { Name = "Scene" };
				packed.Pack( node );
				node.Free();
				ResourceSaver.Save( packed, NewSceneFile );
			}
			EditorInterface.Singleton.OpenSceneFromPath( NewSceneFile );
			root = EditorInterface.Singleton.GetEditedSceneRoot();
			if ( root == null ) { GeppettoToolContext.Warn( "Open a 3D scene first." ); return null; }
		}
		if ( root is not Node3D ) { GeppettoToolContext.Warn( "Geppetto models go in a 3D scene. Open one first." ); return null; }

		string dir = ProjectSettings.GlobalizePath( NewModelDir );
		Directory.CreateDirectory( dir );
		int n = 1;
		string disk;
		do disk = Path.Combine( dir, $"model{n++}.effigy" ); while ( File.Exists( disk ) );
		File.WriteAllText( disk, "effigy 1\nrollback 2147483647\n" );
		string res = $"{NewModelDir}/{Path.GetFileName( disk )}";

		// A new folder or file is only importable once the editor's file scan has seen it.
		var fs = EditorInterface.Singleton.GetResourceFilesystem();
		fs.Scan();
		for ( int i = 0; i < 300 && ( fs.IsScanning() || !File.Exists( disk + ".import" ) ); i++ )
			await ToSignal( GetTree(), SceneTree.SignalName.ProcessFrame );
		if ( !File.Exists( disk + ".import" ) ) fs.ReimportFiles( new[] { res } );
		if ( ResourceLoader.Load<PackedScene>( res, cacheMode: ResourceLoader.CacheMode.Replace ) is not { } scenePacked )
		{
			GeppettoToolContext.Warn( $"Could not import the new model {res}." );
			return null;
		}

		var inst = scenePacked.Instantiate<Node3D>( PackedScene.GenEditState.Instance );
		inst.Name = Path.GetFileNameWithoutExtension( disk ).Capitalize().Replace( " ", "" );
		var ur = GetUndoRedo();
		ur.CreateAction( "Add Geppetto model", UndoRedo.MergeMode.Disable, root );
		ur.AddDoMethod( root, Node.MethodName.AddChild, inst, true );
		ur.AddDoMethod( inst, Node.MethodName.SetOwner, root );
		ur.AddDoReference( inst );
		ur.AddUndoMethod( root, Node.MethodName.RemoveChild, inst );
		ur.CommitAction();

		GeppettoFeatureTree.Refresh();
		GeppettoToolContext.Say( $"New model {res} added to the scene" );
		EditorInterface.Singleton.GetEditorToaster()?.PushToast( $"Geppetto: new model {Path.GetFileName( disk )} added. Ctrl+S saves it." );
		return GeppettoCharacter.Find( null );
	}
}
#endif
