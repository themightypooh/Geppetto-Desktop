using System;
using System.Collections.Generic;
using System.Linq;

namespace Marionette.Desktop;

public enum RenderPassKind { Shadow, DepthPrePass, Lit, PostChain }

/// <summary>The single place that decides the order <see cref="SceneRenderer"/> runs its passes in.
/// New passes are added here and to <see cref="SceneRenderer"/>'s dispatch, nowhere else.</summary>
public static class RenderPassOrder
{
	public static readonly IReadOnlyList<RenderPassKind> Default =
		Array.AsReadOnly( new[] { RenderPassKind.Shadow, RenderPassKind.DepthPrePass, RenderPassKind.Lit, RenderPassKind.PostChain } );

	/// <summary>Null when <paramref name="order"/> is valid, otherwise the reason it is not.
	/// The lit pass samples the shadow map and the pre-pass depth (SSAO/SSR); the post chain
	/// consumes the lit HDR colour, so it must run last.</summary>
	public static string Validate( IReadOnlyList<RenderPassKind> order )
	{
		if ( order is null || order.Count != Enum.GetValues<RenderPassKind>().Length ) return "Every declared pass must run exactly once";
		foreach ( var kind in Enum.GetValues<RenderPassKind>() )
			if ( order.Count( k => k == kind ) != 1 ) return $"{kind} must run exactly once";
		int At( RenderPassKind k ) => order.ToList().IndexOf( k );
		if ( At( RenderPassKind.Shadow ) > At( RenderPassKind.Lit ) ) return "Shadow must run before Lit";
		if ( At( RenderPassKind.DepthPrePass ) > At( RenderPassKind.Lit ) ) return "DepthPrePass must run before Lit";
		if ( order[^1] != RenderPassKind.PostChain ) return "PostChain must run last";
		return null;
	}
}
