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
		if ( f is not GarmentFeature g || g.Bodies.BodyIds.Count > 0 ) return;
		var ids = WearerBodyIds( studio );
		if ( ids.Count == 0 ) ids = TorsoBodyIds( studio );
		g.Bodies.BodyIds.AddRange( ids );
	}

	static readonly System.Text.RegularExpressions.Regex Extremity =
		new( "head|hand|foot|wrist|toe|finger", System.Text.RegularExpressions.RegexOptions.IgnoreCase );

	/// <summary>With no Wearer loaded, the character's own bodies a garment can go on: every non-garment
	/// body bound to a bone that is not a head, hand or foot. Cutting from every body made the fit fail
	/// ("covers nothing") because the hat, gloves and boots sit inside the torso's shell.</summary>
	public static List<string> TorsoBodyIds( PartStudio studio ) =>
		studio.Bodies.Where( b => !b.IsGarment && studio.BodyBoneMap.TryGetValue( b.Id, out var bone ) && !Extremity.IsMatch( bone ) )
			.Select( b => b.Id ).ToList();

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
				GeppettoToolContext.Warn( "Garment needs a rig to find the torso and arms. Load a Wearer first (Clothing > Wearer), or add bones in Rig." );
				return true;
			case GarmentFeature when WearerBodyIds( studio ).Count == 0 && TorsoBodyIds( studio ).Count == 0:
				GeppettoToolContext.Warn( "Garment: no body is bound to a torso bone to dress. Load a Wearer first (Clothing > Wearer), or Assign Body in Rig." );
				return true;
			case GarmentFeature when WearerBodyIds( studio ).Count == 0:
				GeppettoToast.Info( "Garment: no Wearer loaded, so it is cut from this character's torso, arms and legs." );
				break;
			case FurFeature or TrimFeature or FabricFeature or GarmentDetailFeature when !hasGarment:
				GeppettoToolContext.Warn( $"{f.TypeName} goes on a garment - add a Garment first." );
				return true;
			case FurFeature:
				GeppettoToolContext.Say( "Fur: no faces picked, so it covers every garment. Set Coverage to 'Trim the openings' for collar/cuff fur." );
				break;
		}

		Prepare( studio, f );
		if ( f is GarmentDetailFeature detail && ReachableSpot( studio, detail ) is { } moved )
			GeppettoToast.Info( moved );
		GeppettoFeatureDialog.ShowNew( doc, f, closed: ok =>
		{
			if ( ok && f.Error != null ) GeppettoToolContext.Warn( $"{f.TypeName}: {f.Error}" );
		} );
		if ( f.Error != null ) GeppettoToolContext.Warn( $"{f.TypeName}: {f.Error} Change the settings or Cancel." );
		return true;
	}

	/// <summary>A new Pocket / Hardware / Wear / Glow starts on its default place (the chest). A garment cut
	/// from a character made of separate parts (Stringbean) is bands with gaps between them, and the chest
	/// can fall in a gap, so the box opened on an error. Moves the new detail to the first place the garment
	/// does cover (and a new Wear's patch up to the garment's face size). Returns what was changed, for a
	/// toast; null when the default already builds, or nothing does and the box says why.</summary>
	public static string? ReachableSpot( PartStudio studio, GarmentDetailFeature f )
	{
		if ( studio.Rig is not { Count: > 0 } rig || f.Facing.Index != 0 ) return null;
		var surfaces = studio.Bodies.Where( b => b.IsGarment && !b.IsGarmentDetail && !b.IsReference && b.Mesh.FaceCount > 0 )
			.Select( b => new DetailSurface( DetailEdges.Sheet( b.Mesh ) ) ).ToList();
		if ( surfaces.Count == 0 ) return null;
		var map = new BodyRegions.Map( rig );
		var sides = DetailSpots.Sides( f.Side.Index );
		bool Reaches( int i ) => surfaces.Any( s => sides.Any( side => DetailSpots.Find( s, map, DetailSpots.Names[i], side ) != null ) );
		int start = f.Spot.Index;
		// Reaching the spot is not always enough: Wear marks whole faces whose middle falls inside its
		// patch, and a coarse garment has none inside 3 x 2 in. So the candidates are tried with a real
		// build, the default first, and Wear's patch grows (x1..x4) before giving a place up.
		var candidates = Enumerable.Range( 0, DetailSpots.Names.Length )
			.OrderBy( i => i == start ? -1 : i ).Where( Reaches ).Take( 6 ).ToList();
		if ( candidates.Count == 0 ) return null;
		var wear = f as WearFeature;
		float w = wear?.Width.Value ?? 0f, h = wear?.Height.Value ?? 0f;
		var scales = wear != null ? new[] { 1f, 2f, 3f, 4f } : new[] { 1f };
		studio.Features.Add( f );
		try
		{
			foreach ( var i in candidates )
				foreach ( var k in scales )
				{
					f.Spot.Index = i;
					if ( wear != null ) { wear.Width.Value = MathF.Min( w * k, 12f ); wear.Height.Value = MathF.Min( h * k, 12f ); }
					studio.MarkDirty( f ); studio.Rebuild();
					if ( f.Error != null ) continue;
					if ( i == start && k == 1f ) return null;
					string from = DetailSpots.Names[start].ToLowerInvariant(), to = DetailSpots.Names[i].ToLowerInvariant();
					string size = wear != null ? $"{wear.Width.Value:0.#} x {wear.Height.Value:0.#} in" : "";
					string why = k == 1f ? $"the garment does not cover the {from}, so it starts on the {to}"
						: i == start ? $"the garment's faces are bigger than {w:0.#} x {h:0.#} in, so it starts at {size}"
						: $"the {from} is not covered and the faces are bigger than {w:0.#} x {h:0.#} in, so it starts on the {to} at {size}";
					return $"{f.TypeName}: {why}. Change it in the box.";
				}
			// Last, a Wear that finds no face at any place goes along the openings (hem, collar,
			// cuffs), which every garment has, whatever its faces are like.
			if ( wear != null && wear.Area.Index == 0 )
			{
				f.Spot.Index = start;
				wear.Width.Value = w; wear.Height.Value = h;
				wear.Area.Index = 1;
				studio.MarkDirty( f ); studio.Rebuild();
				if ( f.Error == null )
					return $"{f.TypeName}: no face of the garment fits a {w:0.#} x {h:0.#} in patch on the body, so it starts along the openings. Change Area in the box.";
				wear.Area.Index = 0;
			}
		}
		finally { studio.Features.Remove( f ); studio.MarkDirty( studio.Features.Count ); }
		f.Spot.Index = start;
		if ( wear != null ) { wear.Width.Value = w; wear.Height.Value = h; }
		return null;
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
			GeppettoToolContext.Warn( "Nothing to shape yet - add a Garment first." );
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
		GeppettoToast.Info( $"Shape garment: Mesh edit '{edit.Name}' added. Shape it with the Model mesh tools; edits survive garment changes." );
	}
}
#endif
