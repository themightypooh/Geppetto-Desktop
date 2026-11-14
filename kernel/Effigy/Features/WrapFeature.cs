using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Wraps a flat sketch's closed regions round a cylinder, as a raised band (or, with Result set to
/// Remove, a groove cut into it). Onshape's Wrap, for lettering on a can, grip lines on a handle, a
/// label on a bottle.
///
/// THE SKETCH IS THE UNROLLED CYLINDER. Draw on a plane that touches the cylinder (or sits anywhere
/// parallel to its axis): distance across the plane, at right angles to the axis, becomes distance
/// ROUND the cylinder, and distance along the axis stays distance along it. So a 10 u wide letter
/// is 10 u of arc on the surface, the way a sticker would lie, not 10 u of chord.
///
/// The cylinder is either a picked part (its axis runs through the part's centre along Axis, its
/// radius is the farthest any of its points sits from that axis) or the typed Axis point / Axis /
/// Radius. Radius 0 means automatic: the picked part's radius, or the sketch plane's distance from
/// the axis, which is the radius a tangent sketch plane already says.
///
/// Each region is triangulated flat, subdivided so no edge spans more than about 4 degrees of arc,
/// and every point is bent onto the cylinder at two radii, then walled round its outline. A Result
/// of Auto or Add appends it to the picked part; Remove cuts it out through MeshBoolean.
/// </summary>
public sealed class WrapFeature : SketchConsumingFeature
{
	public override string TypeName => "Wrap";

	/// <summary>Arc segments per full turn the subdivision aims for: about 4 degrees each.</summary>
	const int SegmentsPerTurn = 96;

	/// <summary>Most splits per triangle edge, so a big region on a thin cylinder stays a few thousand
	/// triangles rather than hundreds of thousands.</summary>
	const int MaxSplits = 24;

	public readonly BodySelectionParam Cylinder = new( "Cylinder" );
	public readonly Vec3Param AxisPoint = new( "Axis point", Vec3.Zero );
	public readonly Vec3Param Axis = new( "Axis", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam Radius = new( "Radius (0 = automatic)", 0f, 0f, 100000f, "u" );
	public readonly FloatParam Depth = new( "Depth", 0.2f, 0.001f, 1000f, "u" );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Sketch, Cylinder, AxisPoint, Axis, Radius, Depth, Result };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Material };

	protected override void Execute( FeatureContext ctx )
	{
		var sketch = ResolveSketch( ctx );
		var profiles = ResolveProfiles( sketch, ctx );
		if ( profiles.Count == 0 )
			Fail( "Wrap needs a closed region in the sketch.", "The sketch has no closed shape to wrap round the cylinder.",
				"Close the outline in the sketch", "Pick a different sketch" );

		if ( Axis.Value.LengthSquared < 1e-12f )
			FailOn( "Axis", "Axis cannot be zero", "Wrap needs the direction the cylinder runs.", "Set Axis to the cylinder's length direction" );
		var a = Axis.Value.Normal;

		// The cylinder: a picked part fixes the axis position and the radius; otherwise the numbers.
		var host = Cylinder.BodyIds.Count > 0 ? ctx.Bodies.FirstOrDefault( b => Cylinder.Matches( b ) ) : null;
		if ( Cylinder.BodyIds.Count > 0 && host is null )
			Fail( "The cylinder part this wraps round is gone.", "The picked part was deleted, or sits below this feature.",
				"Pick the cylinder again", "Clear the pick and type the axis instead" );

		var c = AxisPoint.Value;
		var partRadius = 0f;
		if ( host is not null && host.Mesh is { VertexCount: > 0 } m )
		{
			var lo = m.Positions[0];
			var hi = lo;
			foreach ( var p in m.Positions )
			{
				lo = new Vec3( MathF.Min( lo.x, p.x ), MathF.Min( lo.y, p.y ), MathF.Min( lo.z, p.z ) );
				hi = new Vec3( MathF.Max( hi.x, p.x ), MathF.Max( hi.y, p.y ), MathF.Max( hi.z, p.z ) );
			}
			c = ( lo + hi ) * 0.5f;
			foreach ( var p in m.Positions )
			{
				var d = p - c;
				partRadius = MathF.Max( partRadius, ( d - a * Vec3.Dot( d, a ) ).Length );
			}
		}

		var plane = sketch.Plane;
		var n = plane.Normal;
		if ( MathF.Abs( Vec3.Dot( n, a ) ) > 0.02f )
			FailOn( "Axis", "The sketch plane is not parallel to the cylinder's axis",
				"Wrap unrolls the cylinder onto the sketch plane, so the plane has to run along the axis.",
				"Sketch on a plane that runs along the cylinder", "Set Axis to lie in the sketch plane" );

		// Where the plane comes closest to the axis: the line the sketch touches the cylinder along.
		var foot = c - n * Vec3.Dot( c - plane.Origin, n );
		var outward = foot - c;
		outward -= a * Vec3.Dot( outward, a );
		var planeDistance = outward.Length;
		var r0 = planeDistance > 1e-5f ? outward * ( 1f / planeDistance ) : n;
		var round = Vec3.Cross( a, r0 );

		var radius = Radius.Clamped > 0f ? Radius.Clamped : partRadius > 0f ? partRadius : planeDistance;
		if ( radius < 1e-4f )
			FailOn( "Radius", "The cylinder has no radius",
				"With Radius 0 the radius comes from the picked part or the sketch plane's distance from the axis, and both are zero.",
				"Type a Radius", "Pick the cylinder part", "Move the sketch plane off the axis" );

		var depth = Depth.Clamped;
		var cut = Result.Index == ResultRemove;
		if ( cut && depth >= radius )
			FailOn( "Depth", "The groove is deeper than the cylinder is thick",
				"A cut this deep would go through the middle of the part.", "Lower Depth" );

		// A little overlap into the surface, so a raised band meets a faceted cylinder (whose flats sit
		// inside its radius) and a cut breaks clean through the skin.
		var overlap = MathF.Min( depth * 0.5f, radius * 0.02f );
		var inner = cut ? radius - depth : radius - overlap;
		var outer = cut ? radius + overlap : radius + depth;
		var along0 = Vec3.Dot( foot - c, a );

		Vec3 OnCylinder( Vec2 q, float rho )
		{
			var t = q.x / radius;
			return c + a * ( along0 + q.y ) + ( r0 * MathF.Cos( t ) + round * MathF.Sin( t ) ) * rho;
		}

		Vec2 Unrolled( Vec2 s )
		{
			var d = plane.Origin + plane.XAxis * s.x + plane.YAxis * s.y - foot;
			return new Vec2( Vec3.Dot( d, round ), Vec3.Dot( d, a ) );
		}

		var mesh = new PolyMesh();
		var wide = 0f;
		var step = MathF.Tau * radius / SegmentsPerTurn;

		foreach ( var profile in profiles )
		{
			var outerLoop = profile.Outer.Select( Unrolled ).ToList();
			var holes = profile.Holes.Select( h => (IReadOnlyList<Vec2>)h.Select( Unrolled ).ToList() ).ToList();
			var points = outerLoop.Concat( holes.SelectMany( h => h ) ).ToList();
			var tris = Triangulate.WithHoles( outerLoop, holes );
			if ( tris.Count == 0 )
				Fail( "A region of the sketch could not be filled.", "One of its holes could not be joined to its outline.",
					"Check the sketch for a hole that touches the outline" );

			wide = MathF.Max( wide, points.Max( p => p.x ) - points.Min( p => p.x ) );

			// One split count per region, so two triangles sharing an edge split it at the same points
			// and the welded surface has no cracks.
			var longest = 0f;
			foreach ( var (i, j, k) in tris )
				longest = MathF.Max( longest, MathF.Max( ( points[i] - points[j] ).Length,
					MathF.Max( ( points[j] - points[k] ).Length, ( points[k] - points[i] ).Length ) ) );
			var splits = Math.Clamp( (int)MathF.Ceiling( longest / step ), 1, MaxSplits );

			AddWrappedRegion( mesh, points, tris, splits, OnCylinder, inner, outer, Material.Clamped );
		}

		if ( wide > MathF.Tau * radius )
			Warn( "The sketch is wider than the cylinder is round, so the wrap overlaps itself.",
				"Distance across the sketch becomes distance round the cylinder.",
				"Make the sketch narrower", "Raise Radius" );

		if ( SignedVolume( mesh ) < 0f )
			foreach ( var f in mesh.Faces ) Array.Reverse( f.Indices );

		Emit( ctx, mesh, host );
	}

	/// <summary>
	/// One region: its triangles subdivided <paramref name="splits"/> times per edge, welded by unrolled
	/// position, laid on the cylinder at the inner and outer radius, and walled round every edge only one
	/// triangle uses (the outline and the holes).
	/// </summary>
	static void AddWrappedRegion( PolyMesh mesh, List<Vec2> points, List<(int A, int B, int C)> tris, int splits,
		Func<Vec2, float, Vec3> onCylinder, float inner, float outer, int material )
	{
		var index = new Dictionary<(long, long), int>();
		var flat = new List<Vec2>();

		int Weld( Vec2 q )
		{
			var key = ( (long)MathF.Round( q.x * 1e4f ), (long)MathF.Round( q.y * 1e4f ) );
			if ( index.TryGetValue( key, out var at ) ) return at;
			index[key] = flat.Count;
			flat.Add( q );
			return flat.Count - 1;
		}

		var faces = new List<(int, int, int)>();
		foreach ( var (ia, ib, ic) in tris )
		{
			Vec2 p0 = points[ia], p1 = points[ib], p2 = points[ic];
			// Counter-clockwise in the unrolled plane, so every top face points away from the axis.
			if ( ( p1.x - p0.x ) * ( p2.y - p0.y ) - ( p1.y - p0.y ) * ( p2.x - p0.x ) < 0f ) ( p1, p2 ) = ( p2, p1 );

			Vec2 At( int i, int j ) => p0 + ( p1 - p0 ) * ( i / (float)splits ) + ( p2 - p0 ) * ( j / (float)splits );
			for ( var i = 0; i < splits; i++ )
			{
				for ( var j = 0; j < splits - i; j++ )
				{
					faces.Add( ( Weld( At( i, j ) ), Weld( At( i + 1, j ) ), Weld( At( i, j + 1 ) ) ) );
					if ( j < splits - i - 1 )
						faces.Add( ( Weld( At( i + 1, j ) ), Weld( At( i + 1, j + 1 ) ), Weld( At( i, j + 1 ) ) ) );
				}
			}
		}

		var top = new int[flat.Count];
		var bottom = new int[flat.Count];
		for ( var i = 0; i < flat.Count; i++ )
		{
			top[i] = mesh.AddVertex( onCylinder( flat[i], outer ) );
			bottom[i] = mesh.AddVertex( onCylinder( flat[i], inner ) );
		}

		var uses = new Dictionary<(int, int), int>();
		foreach ( var (a, b, c) in faces )
		{
			mesh.AddFace( new[] { top[a], top[b], top[c] }, null, material );
			mesh.AddFace( new[] { bottom[a], bottom[c], bottom[b] }, null, material );
			foreach ( var e in new[] { ( a, b ), ( b, c ), ( c, a ) } )
			{
				var key = e.Item1 < e.Item2 ? e : ( e.Item2, e.Item1 );
				uses[key] = uses.TryGetValue( key, out var u ) ? u + 1 : 1;
			}
		}

		// A wall on each directed edge only one triangle has: interior on its left, so the wall faces right.
		foreach ( var (a, b, c) in faces )
		{
			foreach ( var (s, e) in new[] { ( a, b ), ( b, c ), ( c, a ) } )
			{
				if ( uses[s < e ? ( s, e ) : ( e, s )] != 1 ) continue;
				mesh.AddFace( new[] { bottom[s], bottom[e], top[e], top[s] }, null, material );
			}
		}
	}

	static float SignedVolume( PolyMesh mesh )
	{
		var v = 0f;
		foreach ( var f in mesh.Faces )
		{
			var p0 = mesh.Positions[f.Indices[0]];
			for ( var i = 1; i + 1 < f.Indices.Length; i++ )
				v += Vec3.Dot( p0, Vec3.Cross( mesh.Positions[f.Indices[i]], mesh.Positions[f.Indices[i + 1]] ) );
		}
		return v / 6f;
	}
}
