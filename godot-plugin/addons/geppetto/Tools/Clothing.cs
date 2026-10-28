#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;

namespace Geppetto;

/// <summary>
/// The Clothing workspace, ported from s&box's EffigyWindow.Clothing.cs / .GarmentDetails.cs:
/// load a wearer, cut garments off it, trim, fur, fabric, sewn-on details, check for clipping and push
/// it out, then publish a .clothing item or a live-cloth file. Every garment piece is a real kernel
/// feature in the .effigy, so it saves, undoes and rebuilds like any other feature.
/// </summary>
public static partial class GeppettoTools
{
	const string ClothingWs = "Clothing";

	static partial void RegisterClothing()
	{
		ClothingMaterials.Install();

		Register( ClothingWs, "Wearer", ClothingWearer.Run );

		foreach ( var tool in new[] { "Garment", "Trim", "Fur", "Fabric", "Pocket", "Hardware", "Wear", "Glow" } )
		{
			string name = tool;
			Register( ClothingWs, name, ctx => ClothingTools.NewFeatureTool( ctx, name ) );
		}

		Register( ClothingWs, "Shape garment", ClothingTools.ShapeGarmentTool );
		Register( ClothingWs, "Check", ClothingCheck.CheckTool );
		Register( ClothingWs, "Push out", ClothingCheck.PushOutTool );
		Register( ClothingWs, "Clear marks", ClothingCheck.ClearMarksTool );
		Register( ClothingWs, "Make clothing", ClothingPublish.MakeClothingTool );
		Register( ClothingWs, "Make live cloth", ClothingPublish.MakeLiveClothTool );
	}
}

/// <summary>The feature tools and Shape garment.</summary>
public static class ClothingTools
{
	/// <summary>The kernel feature a Clothing dial tool adds (the same defaults s&box's NewFeature used).</summary>
	public static Feature? NewFeature( string tool ) => tool switch
	{
		"Garment" => new GarmentFeature(),
		"Trim" => new TrimFeature(),
		"Fur" => new FurFeature(),
		"Fabric" => new FabricFeature(),
		"Pocket" => new PocketFeature(),
		"Hardware" => new HardwareFeature(),
		"Wear" => new WearFeature(),
		"Glow" => new GlowFeature(),
		_ => null,
	};

	/// <summary>The bodies the newest live Wearer brought in, so a new Garment is cut from the wearer
	/// rather than from every body in the document (s&box asked you to select the body first).</summary>
	public static List<string> WearerBodyIds( PartStudio studio )
	{
		var wearer = studio.Features.OfType<WearerFeature>().LastOrDefault( w => !w.Suppressed && w.Error == null );
		if ( wearer == null ) return new List<string>();
		return studio.Bodies.Where( b => b.FeatureId == wearer.Id ).Select( b => b.Id ).ToList();
	}

	/// <summary>Fills in what s&box got from the selection: a garment goes on the wearer.</summary>
	public static void Prepare( PartStudio studio, Feature f )
	{
		if ( f is GarmentFeature g && g.Bodies.BodyIds.Count == 0 )
			g.Bodies.BodyIds.AddRange( WearerBodyIds( studio ) );
	}

	public static bool NewFeatureTool( GeppettoToolContext ctx, string tool )
	{
		var doc = ctx.Doc;
		if ( doc == null ) return ctx.Need();
		var f = NewFeature( tool );
		if ( f == null ) return false;
		var studio = doc.Studio;
		bool hasGarment = studio.Bodies.Any( b => b.IsGarment );

		switch ( f )
		{
			case GarmentFeature when studio.Rig == null || studio.Rig.Count == 0:
				GD.PushWarning( "[Geppetto] Garment: a garment needs a rig to find the torso and arms - load a Wearer first (Clothing > Wearer). It will show an error until then." );
				break;
			case GarmentFeature when WearerBodyIds( studio ).Count == 0:
				GD.PushWarning( "[Geppetto] Garment: no Wearer loaded, so it is cut from every body in the document. Load a Wearer first to dress a body." );
				break;
			case FurFeature when !hasGarment:
				GD.PushWarning( "[Geppetto] Fur grows on garments - add a Garment first, or it has nothing to grow on." );
				break;
			case FurFeature:
				GeppettoToolContext.Say( "Fur: no faces picked, so it covers every garment. Set Coverage to 'Trim the openings' for collar/cuff fur." );
				break;
			case TrimFeature or FabricFeature or GarmentDetailFeature when !hasGarment:
				GD.PushWarning( $"[Geppetto] {f.TypeName} goes on a garment - add a Garment first." );
				break;
		}

		Prepare( studio, f );
		GeppettoFeatureDialog.ShowNew( doc, f );
		return true;
	}

	// ---- Shape garment ---------------------------------------------------------------------------

	/// <summary>Garment bodies made by a GarmentFeature (not trims or details): what Shape garment can edit.</summary>
	public static List<Body> ShapeableGarments( PartStudio studio )
	{
		var garmentIds = studio.Features.OfType<GarmentFeature>().Select( g => g.Id ).ToHashSet();
		return studio.Bodies.Where( b => b.IsGarment && !b.IsGarmentDetail && b.FeatureId != null && garmentIds.Contains( b.FeatureId ) ).ToList();
	}

	/// <summary>Puts a Mesh edit on one garment body, as one undo step (s&box's ShapeGarment). The edit
	/// keeps itself if the garment above it changes, so reshaping the hem by hand survives a Length tweak.</summary>
	public static MeshEditFeature ShapeGarment( GeppettoDocument doc, Body target )
	{
		var edit = new MeshEditFeature { Name = target.Name ?? "Garment" };
		edit.Bodies.BodyIds.Add( target.Id );
		edit.KeepIfChanged.Value = true;
		doc.AddFeature( edit );
		return edit;
	}

	public static bool ShapeGarmentTool( GeppettoToolContext ctx )
	{
		var doc = ctx.Doc;
		if ( doc == null ) return ctx.Need();
		var garments = ShapeableGarments( doc.Studio );
		if ( garments.Count == 0 )
		{
			GD.PushWarning( "[Geppetto] Nothing to shape yet - add a Garment first." );
			return true;
		}

		if ( garments.Count == 1 ) { Shape( doc, garments[0] ); return true; }

		// Several garments: s&box used the selection; Poohdot gets a small picker listing them.
		var menu = new PopupMenu { Title = "Shape which garment?" };
		for ( int i = 0; i < garments.Count; i++ )
		{
			string label = garments[i].Name ?? garments[i].Id;
			menu.AddItem( label, i );
			menu.SetItemTooltip( i, label );
		}
		menu.IdPressed += id => Shape( doc, garments[(int)id] );
		menu.PopupHide += () => menu.QueueFree();
		EditorInterface.Singleton.GetBaseControl().AddChild( menu );
		menu.PopupCentered();
		return true;
	}

	static void Shape( GeppettoDocument doc, Body target )
	{
		var edit = ShapeGarment( doc, target );
		GeppettoToolContext.Say( $"Shape garment: a Mesh edit '{edit.Name}' is on the garment. Use the Model workspace's mesh tools on it; the edits are kept if the garment's settings change." );
	}
}
#endif
