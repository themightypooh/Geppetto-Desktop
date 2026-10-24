using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Tissue stretched across a gap that tore open faster than it could let go: sinew between the
/// lips of a mouth wrenched open, strands across a wound. A tendon is a bundle of two or three
/// fibres twisting half a turn about each other along the span, each flared to about 2.2× where it
/// roots into the surface and thinned to half in the middle; the bundle sags by an eighth of its
/// span. A snapped one hangs from its root and tapers to a point.
/// </summary>
public static class Tendons
{
	/// <summary>
	/// Appends a tendon from <paramref name="a"/> to <paramref name="b"/> (z is down for the sag)
	/// to a vertex and face list. <paramref name="reach"/> under 1 makes it snapped: it runs that
	/// fraction of the way, drops under its weight and ends in a point.
	/// </summary>
	public static void Append( List<Vec3> positions, List<Face> faces, Vec3 a, Vec3 b, float radius, float reach, Random rng, int sides = 7, int steps = 16 )
	{
		AppendSpan( positions, faces, a, b, radius, reach, 0.125f, rng, sides, steps );
	}

	/// <summary>Like <see cref="Append"/>, with the sag as a fraction of the span instead of the
	/// fixed one-eighth: a tendon feature's <c>sag=</c> parameter lands here.</summary>
	public static void AppendSpan( List<Vec3> positions, List<Face> faces, Vec3 a, Vec3 b, float radius, float reach, float sagFraction, Random rng, int sides = 7, int steps = 16 )
	{
		var span = (b - a).Length;
		if ( span < 1e-5f || radius <= 0f ) return;
		var axis = (b - a) / span;
		Basis( axis, out var ux, out var uy );
		var fibres = rng.Next( 2, 4 );
		var phase = (float)rng.NextDouble() * MathF.PI * 2f;
		for ( var k = 0; k < fibres; k++ )
		{
			var path = new List<Vec3>();
			var radii = new List<float>();
			var ang0 = phase + MathF.PI * 2f * k / fibres;
			var fr = radius * (fibres == 2 ? 0.75f : 0.65f) * (0.85f + 0.3f * (float)rng.NextDouble());
			for ( var i = 0; i <= steps; i++ )
			{
				var t = reach * i / steps;
				var sag = -span * sagFraction * 4f * t * (1f - t);
				var hang = reach < 1f ? -span * 0.35f * (t / reach) * (t / reach) : 0f;
				var ang = ang0 + MathF.PI * t;
				// Fibres stay bundled (a round cord, not a flat ribbon): at most ~0.45 radii apart.
				var spread = radius * (0.25f * MathF.Sin( MathF.PI * Math.Clamp( t, 0f, 1f ) ) + 0.18f);
				path.Add( a + (b - a) * t + new Vec3( 0, 0, sag + hang ) + (ux * MathF.Cos( ang ) + uy * MathF.Sin( ang )) * spread );
				var root = 1f + 1.2f * (Step( 0.18f, 0f, t ) + (reach >= 1f ? Step( 0.82f, 1f, t ) : 0f));
				var waist = 1f - 0.5f * MathF.Sin( MathF.PI * Math.Clamp( t, 0f, 1f ) );
				var tip = reach < 1f ? Step( reach, reach * 0.6f, t ) * 0.9f + 0.1f : 1f;
				radii.Add( fr * root * waist * tip );
			}
			Tube( positions, faces, path, radii, sides );
		}
	}

	/// <summary>An open tube with a radius per path point, appended to a vertex and face list.</summary>
	public static void Tube( List<Vec3> positions, List<Face> faces, IList<Vec3> path, IList<float> radii, int sides = 7 )
	{
		var start = positions.Count;
		var n = path.Count;
		var prevX = Vec3.Zero;
		for ( var i = 0; i < n; i++ )
		{
			var t = path[Math.Min( i + 1, n - 1 )] - path[Math.Max( i - 1, 0 )];
			t = t.LengthSquared > 1e-12f ? t.Normal : new Vec3( 1, 0, 0 );
			Basis( t, out var x, out var y );
			if ( i > 0 && Vec3.Dot( x, prevX ) < 0f ) { x = -x; y = -y; }
			prevX = x;
			for ( var k = 0; k < sides; k++ )
			{
				var ang = MathF.PI * 2f * k / sides;
				positions.Add( path[i] + (x * MathF.Cos( ang ) + y * MathF.Sin( ang )) * radii[i] );
			}
		}
		for ( var i = 0; i + 1 < n; i++ )
			for ( var k = 0; k < sides; k++ )
			{
				var k2 = (k + 1) % sides;
				faces.Add( new Face( new[] { start + i * sides + k, start + i * sides + k2, start + (i + 1) * sides + k2, start + (i + 1) * sides + k } ) );
			}
	}

	static void Basis( Vec3 d, out Vec3 x, out Vec3 y )
	{
		var helper = MathF.Abs( d.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		x = Vec3.Cross( d, helper ).Normal;
		y = Vec3.Cross( d, x ).Normal;
	}

	static float Step( float a, float b, float x )
	{
		var t = Math.Clamp( (x - a) / (b - a), 0f, 1f );
		return t * t * (3f - 2f * t);
	}
}

/// <summary>Script: <c>add Tendons name=JawSinew fromBody=UpperJaw toBody=LowerJaw count=6
/// radius=0.08 sag=0.15</c>. Twisted sinew/saliva strands spanning the facing surfaces of two
/// bodies — an open jaw, a wound, a creature mouth — as real geometry, not paint. Each strand
/// starts at a deterministic area-weighted sample on the from-body and ends at the nearest
/// surface point on the to-body (BVH), so strands only span where the two bodies actually face
/// each other; a strand with no facing surface is skipped, and if none land the feature fails
/// honestly instead of building an empty body.</summary>
public sealed class TendonsFeature : Feature
{
	public override string TypeName => "Tendons";

	public readonly StringParam FromBody = new( "From body", "" );
	public readonly StringParam ToBody = new( "To body", "" );
	public readonly IntParam Count = new( "Count", 6, 1, 64 );
	public readonly FloatParam Radius = new( "Radius", 0.08f, 0.001f, 10f, unit: "u" );
	public readonly FloatParam Sag = new( "Sag", 0.15f, 0f, 1f );
	public readonly IntParam Sides = new( "Sides", 7, 3, 16 );
	public readonly IntParam Seed = new( "Seed", 42, 0, int.MaxValue ) { Slider = false };
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { FromBody, ToBody, Count, Radius, Sag, Sides, Seed, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var from = Find( ctx, FromBody.Value, "from" );
		var to = Find( ctx, ToBody.Value, "to" );

		if ( from.Mesh is null || from.Mesh.FaceCount == 0 )
			Fail( $"'{from.Name}' has no faces to root tendons on.", "Tendons start on a surface.", "Point fromBody at a body with real surface" );

		if ( to.Mesh is null || to.Mesh.FaceCount == 0 )
			Fail( $"'{to.Name}' has no faces for tendons to land on.", "Tendons end on a surface.", "Point toBody at a body with real surface" );

		var rng = new Random( Seed.Value );
		var tree = MeshBVH.Build( to.Mesh );
		var positions = new List<Vec3>();
		var faces = new List<Face>();
		var built = 0;

		for ( var i = 0; i < Count.Clamped; i++ )
		{
			var (point, normal) = SampleSurface( from.Mesh, rng );
			var start = point + normal * 0.01f;
			var hit = tree.NearestSurface( to.Mesh, start, float.MaxValue );

			if ( hit is null )
				continue;

			var firstFace = faces.Count;
			Tendons.AppendSpan( positions, faces, start, hit.Value.Point, Radius.Clamped, 1f, Sag.Clamped, rng, Sides.Clamped, 16 );

			for ( var fi = firstFace; fi < faces.Count; fi++ )
				faces[fi].Material = Material.Clamped;

			built++;
		}

		if ( built == 0 )
			Fail( $"No tendon landed between '{from.Name}' and '{to.Name}'.", "Every sampled start missed the to-body's surface.", "Move the bodies so they face each other, or swap fromBody/toBody" );

		var mesh = new PolyMesh { Positions = positions, Faces = faces };
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );
	}

	static Body Find( FeatureContext ctx, string name, string role )
	{
		var body = ctx.Bodies.FirstOrDefault( b => string.Equals( b.Name, name, StringComparison.OrdinalIgnoreCase ) || b.Id == name );

		if ( body is null )
			Fail( $"No body called '{name}' for the {role} side.", "Tendons span between two bodies already in the studio.", "Add the bodies first, then add Tendons" );

		return body;
	}

	static (Vec3 Point, Vec3 Normal) SampleSurface( PolyMesh mesh, Random rng )
	{
		var total = 0f;

		foreach ( var f in mesh.Faces )
			total += mesh.FaceArea( f );

		var pick = (float)rng.NextDouble() * total;

		foreach ( var f in mesh.Faces )
		{
			pick -= mesh.FaceArea( f );

			if ( pick <= 0f )
			{
				var normal = mesh.FaceNormal( f );
				return (mesh.FaceCentroid( f ), normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 ));
			}
		}

		var last = mesh.Faces[^1];
		var lastNormal = mesh.FaceNormal( last );
		return (mesh.FaceCentroid( last ), lastNormal.LengthSquared > 1e-12f ? lastNormal.Normal : new Vec3( 0, 0, 1 ));
	}
}

/// <summary>Teeth along a jaw arc, as real geometry: one tapered cone per tooth, standing on
/// a circular arc in the XY plane. Upper-jaw teeth point down (<c>flip: true</c>), lower-jaw
/// teeth point up. A helper, like <see cref="EyeAsset"/> — it builds a mesh the caller places,
/// it is not itself a script verb.</summary>
public static class DentalArc
{
	/// <param name="arcRadius">Radius of the jaw curve the teeth stand on.</param>
	/// <param name="arcDegrees">How much of the circle the teeth span, centred on +X.</param>
	/// <param name="count">Teeth on the arc; 1 puts a single tooth at +X.</param>
	/// <param name="toothHeight">Tip-to-base height of each tooth.</param>
	/// <param name="toothBaseRadius">Radius of each tooth's base circle.</param>
	/// <param name="sides">Sides per tooth cone, 3 or more.</param>
	/// <param name="flip">True points the teeth down (-Z) for an upper jaw.</param>
	public static PolyMesh Build( float arcRadius, float arcDegrees, int count, float toothHeight, float toothBaseRadius, int sides = 6, bool flip = false )
	{
		if ( arcRadius <= 0f )
			throw new ArgumentException( "A dental arc needs a positive radius.", nameof( arcRadius ) );

		if ( count < 1 )
			throw new ArgumentException( "A dental arc needs at least one tooth.", nameof( count ) );

		if ( toothHeight <= 0f || toothBaseRadius <= 0f )
			throw new ArgumentException( "Teeth need a positive height and base radius.", nameof( toothHeight ) );

		if ( sides < 3 )
			throw new ArgumentException( "A tooth cone needs at least 3 sides.", nameof( sides ) );

		var mesh = new PolyMesh();
		var sweep = arcDegrees * MathF.PI / 180f;
		var dir = flip ? -1f : 1f;

		for ( var i = 0; i < count; i++ )
		{
			var angle = count == 1 ? 0f : -sweep / 2f + sweep * i / (count - 1);
			var centre = new Vec3( arcRadius * MathF.Cos( angle ), arcRadius * MathF.Sin( angle ), 0f );
			var ring = new int[sides];

			for ( var k = 0; k < sides; k++ )
			{
				var theta = MathF.PI * 2f * k / sides;
				ring[k] = mesh.AddVertex( centre + new Vec3( toothBaseRadius * MathF.Cos( theta ), toothBaseRadius * MathF.Sin( theta ), 0f ) );
			}

			var apex = mesh.AddVertex( centre + new Vec3( 0f, 0f, dir * toothHeight ) );

			for ( var k = 0; k < sides; k++ )
			{
				var k2 = (k + 1) % sides;
				mesh.AddFace( flip
					? new[] { ring[k], apex, ring[k2] }
					: new[] { ring[k], ring[k2], apex } );
			}

			mesh.AddFace( (int[])ring.Clone() );
		}

		return mesh;
	}
}
