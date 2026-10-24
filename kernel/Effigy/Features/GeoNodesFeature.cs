using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>A Geometry Nodes graph as a feature: <c>add GeoNodes name=Scales graph="..."</c>.
/// The graph is evaluated fresh on every rebuild, so the result is non-destructive — change
/// a node property and the whole chain rebuilds, like every other feature in the tree.
///
/// Graph spelling: JSON when the string starts with '{' (branching, named nodes, named
/// inputs for prototype/profile/target), otherwise the compact pipe DSL (linear chains,
/// one stage per node, previous output flowing into the next). See <see cref="GeoNodes"/>.
///
/// Output: the graph's mesh becomes one body. An instances-only result is realised first.
/// A curves-only result fails honestly — a Body is a mesh, and silently baking curves into
/// edge faces would export as junk; add a CurveToMesh stage to get a tube. The Material
/// slot, when set, is applied to every face of the output.</summary>
public sealed class GeoNodesFeature : Feature
{
	public override string TypeName => "GeoNodes";

	/// <summary>The graph, as JSON or as the pipe DSL. Empty is an error, not an empty body:
	/// a feature that silently produces nothing looks like a successful build of nothing.</summary>
	public readonly StringParam Graph = new( "Graph", "" );

	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Graph, Material };

	/// <summary>Evaluate a graph string into a geometry set, for tests and for callers that
	/// want the raw result (mesh, curves and instances) instead of a body.</summary>
	public static GeometrySet EvaluateGraph( string graph ) => GeoNodes.EvaluateGraph( graph );

	protected override void Execute( FeatureContext ctx )
	{
		var graph = Graph.Value ?? "";

		if ( string.IsNullOrWhiteSpace( graph ) )
		{
			FailOn( Graph.Label,
				"Graph is empty, so there is nothing to build.",
				"A GeoNodes feature is its graph; without one it cannot know what shape to make.",
				"Give a graph, e.g. graph=\"Grid size_x=4 size_y=2 | SubdivideMesh levels=1\"",
				"Or a JSON graph with named nodes, e.g. graph=\"{\\\"nodes\\\":[{\\\"id\\\":\\\"g\\\",\\\"type\\\":\\\"Grid\\\"}]}\"" );
		}

		// Initialised to null for the compiler: every failure path above throws via FailOn,
		// so a null here is unreachable at runtime, not a silent empty result.
		GeometrySet result = null;
		try { result = GeoNodes.EvaluateGraph( graph ); }
		catch ( GeoGraphException e )
		{
			FailOn( Graph.Label,
				$"The graph failed: {e.Message}",
				"Node errors name the node and the socket, so the broken stage is findable.",
				"Check the node type spelling against GeoNodes.NodeTypes",
				"Check that every geometry input names an earlier node" );
		}
		catch ( GeoNodeFailure e )
		{
			FailOn( Graph.Label, $"The graph failed: {e.Message}", "See the node documentation for that node's inputs.", "Fix the failing stage's properties" );
		}

		PolyMesh mesh = null;
		if ( result.Mesh is not null ) mesh = result.Mesh.ToPolyMesh();
		else if ( result.Instances is not null )
		{
			var realised = GeoNodeImpl.RealizeInstances(
				new GeoNodeCall { Type = "RealizeInstances" },
				new GeoEvalContext { Named = new Dictionary<string, GeometrySet>(), Input = result } );
			if ( realised.Mesh is null )
			{
				FailOn( Graph.Label,
					"The graph produced instances but no mesh to realise.",
					"RealizeInstances bakes mesh prototypes; a prototype with no mesh bakes to nothing.",
					"Give the instances a mesh prototype, or end the graph with a mesh node" );
			}
			mesh = realised.Mesh.ToPolyMesh();
		}
		else
		{
			FailOn( Graph.Label,
				"The graph produced curves but no mesh, and a body is a mesh.",
				"Curves on their own have no surface to draw, paint or export.",
				"End the graph with CurveToMesh to turn the curves into a tube",
				"Or start from a mesh generator such as Grid or UVSphere" );
		}

		var slot = Material.Clamped;
		if ( slot != 0 )
			foreach ( var f in mesh.Faces ) f.Material = slot;

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), string.IsNullOrWhiteSpace( Name ) ? "GeoNodes" : Name, mesh ) );
	}
}
