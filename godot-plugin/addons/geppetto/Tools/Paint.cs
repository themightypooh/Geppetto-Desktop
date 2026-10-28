#if TOOLS
#nullable enable
using Effigy;

namespace Geppetto;

/// <summary>
/// The Paint workspace's dial (Channel, Layers, Brush) and the Rig workspace's Paint Weights, wired to the
/// brushes in <see cref="PaintBrushTool"/> and <see cref="PaintWeightsTool"/>.
/// </summary>
public static partial class GeppettoTools
{
	static partial void RegisterPaint()
	{
		void Channel( string tool, PaintChannel ch ) => Register( "Paint", tool, ctx =>
		{
			if ( ctx.Character == null || ctx.Doc == null ) return ctx.Need();
			var brush = Brush( ctx );
			brush.SetChannel( ch );
			if ( brush.Session == null )
				GeppettoToolContext.Say( $"Paint brush on {PaintChannels.Label( ch )}: click a part of the model to open its paint layer." );
			else
				GeppettoToolContext.Say( $"Painting {PaintChannels.Label( ch )}." );
			return true;
		} );
		Channel( "Colour", PaintChannel.Color );
		Channel( "Roughness", PaintChannel.Roughness );
		Channel( "Metal", PaintChannel.Metalness );
		Channel( "Normal", PaintChannel.Height );

		void Layer( string tool, bool fill ) => Register( "Paint", tool, ctx =>
		{
			if ( ctx.Character == null || ctx.Doc == null ) return ctx.Need();
			var brush = Brush( ctx );
			if ( brush.BodyId is { } id )
			{
				// s&box's AddPaintLayer: a new layer right over the open one, on the same part.
				brush.PendingNewLayer = fill;
				brush.OpenBody( id );
			}
			else
			{
				brush.PendingNewLayer = fill;
				GeppettoToolContext.Say( fill
					? "Fill Layer: click the part to cover with a new fill layer."
					: "New Layer: click the part to paint on a new layer." );
			}
			return true;
		} );
		Layer( "New Layer", false );
		Layer( "Fill Layer", true );

		void Field( string tool, string field ) => Register( "Paint", tool, ctx =>
		{
			if ( ctx.Character == null || ctx.Doc == null ) return ctx.Need();
			var brush = Brush( ctx );
			brush.FocusBar( field );
			GeppettoToolContext.Say( field switch
			{
				"radius" => "Brush radius: type it in the paint bar, or use [ and ] over the model.",
				"strength" => "Paint strength: type it in the paint bar (0.01 to 1).",
				_ => "Channel value: set it in the paint bar (the swatch on Base Color, the Value box on the others).",
			} );
			return true;
		} );
		Field( "Brush radius", "radius" );
		Field( "Paint strength", "strength" );
		Field( "Channel value", "value" );

		Register( "Rig", "Paint Weights", ctx =>
		{
			if ( ctx.Character == null || ctx.Doc == null ) return ctx.Need();
			var tool = new PaintWeightsTool( ctx.Doc, ctx.Plugin, GeppettoBrush.BodyNode( ctx.Character ) );
			if ( !tool.Open() ) { tool.Exit(); return true; }
			Activate( tool );
			return true;
		} );
	}

	/// <summary>The active paint brush for this character's document, or a new one made active.</summary>
	static PaintBrushTool Brush( GeppettoToolContext ctx )
	{
		if ( Active is PaintBrushTool b && ReferenceEquals( b.Doc, ctx.Doc ) ) return b;
		var brush = new PaintBrushTool( ctx.Doc!, ctx.Plugin, GeppettoBrush.BodyNode( ctx.Character ) );
		Activate( brush );
		return brush;
	}
}
#endif
