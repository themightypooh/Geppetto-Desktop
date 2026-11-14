using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Poly-edit operations that arrived with the modifier-stack ticket:
/// edge collapse — the one tool the session did not already have — and the
/// per-body modifier stack. Extrude, inset, loop cut, bridge, slide, knife,
/// bevel, merge by distance and loop/ring selection already live in
/// MeshAdjacency.cs; they are not duplicated here.
/// </summary>
public sealed partial class MeshEditSession
{
	/// <summary>The live, non-destructive modifiers on this body, evaluated
	/// in order. The base mesh is untouched until ApplyModifiers runs.</summary>
	public ModifierStack Modifiers { get; } = new();

	/// <summary>Collapse each selected edge to its midpoint, Blender's Edge
	/// Collapse. One undo step; the selection clears, as with Bevel.</summary>
	public void CollapseEdges()
	{
		var edges = new HashSet<EdgeKey>( SelectedEdges );

		if ( edges.Count == 0 )
			throw new InvalidOperationException( "Collapse needs edges selected. Switch to Edge mode (2) and pick the edges to pinch together." );

		Step( "Collapse Edges", () =>
		{
			Mesh = PolyEdit.CollapseEdges( Mesh, edges );
			ClearSelection();
		} );
	}

	/// <summary>The mesh as the exporters will see it: the base mesh with the
	/// live stack evaluated. The base mesh is untouched.</summary>
	public PolyMesh EvaluateModifiers() => Modifiers.Evaluate( Mesh );

	/// <summary>Apply the live stack onto the base mesh: the evaluated mesh
	/// becomes the new base and the stack clears. One undo step. Applying is
	/// destructive in one specific way: a topology-changing apply drops morph
	/// targets and invalidates sculpt layers sized to the old vertex count,
	/// so the warning is in the outcome for the caller to show. Undo restores
	/// the mesh, not the modifier list — the modifiers are consumed by the
	/// apply, as in Blender.</summary>
	public void ApplyModifiers( IReadOnlyList<SculptLayer> sculptLayers = null )
	{
		var warnings = Modifiers.TopologyWarnings( Mesh, sculptLayers );

		Step( "Apply Modifiers", () =>
		{
			var applied = Modifiers.Apply( Mesh, sculptLayers );
			Mesh = applied.Mesh;
			Modifiers.Clear();
			ClearSelection();
			Report( warnings.Count == 0
				? "Modifiers applied."
				: "Modifiers applied. " + string.Join( " ", warnings ) );
		} );
	}
}
