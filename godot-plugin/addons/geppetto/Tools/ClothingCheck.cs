#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>
/// Check / Push out / Clear marks, ported from s&box's CheckGarments and PushOutClipping. Check runs the
/// kernel's GarmentCheck on every garment against the wearer, logs the report and marks each clipping
/// vertex in the viewport (yellow shallow, red deep). Push out raises the clipping garments' Clearance by
/// the depth found, as one undo step, and checks again.
/// </summary>
public static class ClothingCheck
{
	/// <summary>Deepest clip per garment feature from the last Check.</summary>
	static readonly Dictionary<string, float> LastClipDepth = new();

	/// <summary>The clipping points of the last Check, in Effigy space, with 0..1 depth (1 = deepest).</summary>
	public static readonly List<(Vec3 Point, float Depth)> Marks = new();

	static Node3D? _markRoot;

	public static IReadOnlyDictionary<string, float> ClipDepths => LastClipDepth;

	/// <summary>Runs the check. Returns the reports (empty when there is no garment) and the message s&box showed.</summary>
	public static List<GarmentReport> Check( GeppettoDocument doc, out string message )
	{
		var studio = doc.Studio;
		var reports = new List<GarmentReport>();
		var garments = studio.Bodies.Where( b => b.IsGarment ).ToList();
		Marks.Clear();
		if ( garments.Count == 0 )
		{
			message = "Nothing to check yet - add a Garment first.";
			return reports;
		}

		var worn = studio.Bodies.Where( b => !b.IsGarment ).Select( b => b.Mesh ).ToList();
		var furSlots = studio.Features.OfType<FurFeature>().Where( f => !f.Suppressed && f.ResolvedSlot >= 0 ).Select( f => f.ResolvedSlot ).ToHashSet();
		LastClipDepth.Clear();

		float deepest = 0f;
		int clipping = 0, faults = 0;
		var points = new List<(Vec3, float)>();
		foreach ( var g in garments )
		{
			var report = GarmentCheck.Run( WithoutFur( g.Mesh, furSlots ), worn );
			report.Name = g.Name ?? g.Id;
			reports.Add( report );
			deepest = MathF.Max( deepest, report.DeepestClip );
			clipping += report.Clipping;
			if ( !report.Clean ) faults++;
			if ( report.Clipping > 0 && g.FeatureId != null )
				LastClipDepth[g.FeatureId] = MathF.Max( LastClipDepth.GetValueOrDefault( g.FeatureId ), report.DeepestClip );
			foreach ( var line in report.Lines() ) GD.Print( $"[Geppetto] {line}" );
			for ( int i = 0; i < report.ClipPoints.Count; i++ )
				points.Add( ( report.ClipPoints[i], i < report.ClipDepths.Count ? report.ClipDepths[i] : 0f ) );
		}

		foreach ( var (p, d) in points )
			Marks.Add( ( p, deepest > 0f ? d / deepest : 1f ) );

		message = clipping > 0
			? $"{clipping} vertices clip, deepest {deepest:0.##} in - marked yellow to red. Push out fixes it, or choose a looser Fit; the rest is in the console."
			: faults == 0
				? $"Checked {garments.Count} garment(s): nothing wrong. Details in the console."
				: $"{faults} of {garments.Count} garments have something to fix - see the console.";
		return reports;
	}

	/// <summary>Raises Clearance on every garment that clipped (a Trim's clip goes to the garments it is on),
	/// as one undo step, then checks again. Returns the message.</summary>
	public static string PushOut( GeppettoDocument doc )
	{
		if ( LastClipDepth.Count == 0 )
			return "Nothing to push out - run Check first, or the last one found no clipping.";

		var studio = doc.Studio;
		var targets = new Dictionary<GarmentFeature, float>();
		foreach ( var (id, depth) in LastClipDepth )
		{
			var feature = studio.Features.FirstOrDefault( f => f.Id == id );
			IEnumerable<GarmentFeature> under = feature switch
			{
				GarmentFeature g => new[] { g },
				TrimFeature t => GarmentsUnder( studio, t ),
				_ => Array.Empty<GarmentFeature>(),
			};
			foreach ( var g in under )
				targets[g] = MathF.Max( targets.GetValueOrDefault( g ), depth );
		}
		LastClipDepth.Clear();
		if ( targets.Count == 0 )
			return "The garments that clipped are no longer in the document.";

		bool hitCap = false;
		var changes = new List<(GarmentFeature G, float OldV, string? OldE, float NewV)>();
		foreach ( var (g, depth) in targets )
		{
			float next = g.Clearance.Clamped + depth + 0.05f;
			if ( next > g.Clearance.Max ) hitCap = true;
			changes.Add( ( g, g.Clearance.Value, g.Clearance.Expr, MathF.Round( MathF.Min( next, g.Clearance.Max ), 2 ) ) );
		}

		doc.Execute( new DelegateEditCommand( "Push out clipping",
			execute: () => { foreach ( var c in changes ) { c.G.Clearance.Value = c.NewV; c.G.Clearance.Expr = null!; studio.MarkDirty( c.G ); } },
			undo: () => { foreach ( var c in changes ) { c.G.Clearance.Value = c.OldV; c.G.Clearance.Expr = c.OldE!; studio.MarkDirty( c.G ); } } ) );

		Check( doc, out var after );
		return hitCap
			? $"Pushed out as far as Clearance goes ({changes.Max( c => c.G.Clearance.Max ):0.##} in). A deeper clip needs a looser Fit. {after}"
			: $"Pushed out {changes.Count} garment(s). {after}";
	}

	static IEnumerable<GarmentFeature> GarmentsUnder( PartStudio studio, TrimFeature trim )
	{
		var all = studio.Features.OfType<GarmentFeature>().Where( g => !g.Suppressed ).ToList();
		if ( trim.Bodies.BodyIds.Count == 0 ) return all;
		var ids = studio.Bodies.Where( b => trim.Bodies.Matches( b ) ).Select( b => b.FeatureId ).ToHashSet();
		var matched = all.Where( g => ids.Contains( g.Id ) ).ToList();
		return matched.Count > 0 ? matched : all;
	}

	/// <summary>The garment without its fur shells (kept vertex order and weights), as s&box's WithoutFur.</summary>
	public static PolyMesh WithoutFur( PolyMesh source, HashSet<int> furSlots )
	{
		if ( source is null || furSlots.Count == 0 || !source.Faces.Any( f => furSlots.Contains( f.Material ) ) )
			return source!;

		var map = new int[source.VertexCount];
		Array.Fill( map, -1 );
		foreach ( var face in source.Faces )
			if ( !furSlots.Contains( face.Material ) )
				foreach ( var vi in face.Indices )
					if ( (uint)vi < (uint)map.Length ) map[vi] = 0;

		var cloth = new PolyMesh();
		var skin = source.IsRigged ? new SkinWeights() : null;
		for ( var vi = 0; vi < map.Length; vi++ )
		{
			if ( map[vi] < 0 ) continue;
			map[vi] = cloth.VertexCount;
			cloth.AddVertex( source.Positions[vi] );
			skin?.Vertices.Add( source.Skin[vi] );
		}
		foreach ( var face in source.Faces )
		{
			if ( furSlots.Contains( face.Material ) || face.Indices.Any( vi => (uint)vi >= (uint)map.Length ) ) continue;
			cloth.AddFace( face.Indices.Select( vi => map[vi] ).ToArray(), face.UVs, face.Material );
		}
		cloth.Skin = skin;
		return cloth;
	}

	// ---- dial tools --------------------------------------------------------------------------------

	public static bool CheckTool( GeppettoToolContext ctx )
	{
		var doc = ctx.Doc;
		if ( doc == null ) return ctx.Need();
		Check( doc, out var message );
		Report( ctx, message );
		return true;
	}

	public static bool PushOutTool( GeppettoToolContext ctx )
	{
		var doc = ctx.Doc;
		if ( doc == null ) return ctx.Need();
		Report( ctx, PushOut( doc ) );
		return true;
	}

	public static bool ClearMarksTool( GeppettoToolContext ctx )
	{
		if ( Marks.Count == 0 && GeppettoTools.Active is not MarksTool )
		{
			GeppettoToolContext.Say( "No clipping marks to clear." );
			return true;
		}
		Marks.Clear();
		if ( GeppettoTools.Active is MarksTool ) GeppettoTools.Activate( null );
		GeppettoTools.Redraw();
		GeppettoToolContext.Say( "Clipping marks cleared." );
		return true;
	}

	static void Report( GeppettoToolContext ctx, string message )
	{
		if ( Marks.Count > 0 )
		{
			_markRoot = ctx.Character?.Root as Node3D;
			GeppettoTools.Activate( new MarksTool() );
			GeppettoTools.Redraw();
			GD.PushWarning( $"[Geppetto] {message}" );
		}
		else
		{
			if ( GeppettoTools.Active is MarksTool ) GeppettoTools.Activate( null );
			GeppettoToolContext.Say( message );
		}
	}

	/// <summary>Draws the clipping marks over the viewport. It takes no input, so the camera still works;
	/// Esc or Clear marks removes it.</summary>
	sealed class MarksTool : IGeppettoViewportTool
	{
		public string Name => "Clipping marks";

		public bool Input( Camera3D camera, InputEvent e ) => false;

		public void Draw( Control overlay )
		{
			var camera = EditorInterface.Singleton.GetEditorViewport3D( 0 )?.GetCamera3D();
			if ( camera == null || Marks.Count == 0 ) return;
			var xf = _markRoot != null && GodotObject.IsInstanceValid( _markRoot ) && _markRoot.IsInsideTree() ? _markRoot.GlobalTransform : Transform3D.Identity;
			foreach ( var (p, d) in Marks )
			{
				var world = xf * EffigyMeshBridge.TransformPosition( p.x, p.y, p.z );
				if ( camera.IsPositionBehind( world ) ) continue;
				var screen = camera.UnprojectPosition( world );
				var colour = new Color( 1f, 1f - Math.Clamp( d, 0f, 1f ), 0f );
				overlay.DrawCircle( screen, 3f + 2f * d, colour );
			}
		}

		public void Exit() { }
	}
}
#endif
