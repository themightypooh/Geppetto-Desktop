using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Diamond / straight / diagonal knurling for machined grips (roadmap §4 Talon:
/// knurled grip, <c>SurfaceDetail.KnurlPattern</c>).
///
/// A knurl is a displacement along the surface normal, not a texture: the grip
/// keeps its knurl through remesh, export and bake. The pattern is a triangular
/// wave over the grip's circumference and length — diamond is the product of
/// two diagonal waves, which is what makes the pyramids:
///
///   diamond:  h = Depth * min(tri(u+v), tri(u-v))
///   straight: h = Depth * tri(u)          (ridges along the axis)
///   diagonal: h = Depth * tri(u+v)
///
/// where u is arc length around the grip axis divided by Pitch and v is length
/// along the axis divided by Pitch. A knurl needs mesh to displace: on a coarse
/// mesh the pattern aliases into lumps, so the feature warns when Pitch is
/// under twice the mean edge length instead of silently producing mush. The
/// script-level answer is a Subdivide before the Knurl (see the pistol sample
/// script in the test report), not a finer pitch on a coarse grip.
///
/// WHERE THIS LIVES, HONESTLY: the roadmap names this
/// <c>SurfaceDetail.KnurlPattern</c>, but <c>SurfaceDetail.cs</c> is hot — the
/// wave-2 look swarm is editing it right now — so the math lives here as
/// <see cref="KnurlPattern"/> until that file cools down, and the feature calls
/// it. Moving the method into SurfaceDetail is a cut-and-paste once the look
/// swarm lands; nothing about the feature's syntax changes when it moves.
/// </summary>
public sealed class KnurlFeature : Feature
{
	public override string TypeName => "Knurl";

	public readonly BodySelectionParam Target = new( "Target" );
	public readonly ChoiceParam Axis = new( "Axis", new[] { "Z", "X", "Y" } );
	public readonly ChoiceParam Pattern = new( "Pattern", new[] { "Diamond", "Straight", "Diagonal" } );
	public readonly FloatParam Pitch = new( "Pitch", 0.5f, 0.05f, 10f, "u" );
	public readonly FloatParam Depth = new( "Depth", 0.1f, 0f, 2f, "u" );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Target, Axis, Pattern, Pitch, Depth };

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Target );
		foreach ( var body in targets )
		{
			var report = KnurlPattern.Apply( body.Mesh,
				new KnurlOptions( Axis.Value, Pattern.Value, Pitch.Clamped, Depth.Clamped ) );
			if ( report.MeanEdgeLength > 0f && Pitch.Clamped < 2f * report.MeanEdgeLength )
				Warn( $"Knurl pitch {Pitch.Clamped} is finer than this mesh can hold",
					$"Mean edge length is {report.MeanEdgeLength:F3}, so a {Pitch.Clamped} pitch aliases into lumps instead of pyramids.",
					"Subdivide the grip first", "Or raise Pitch above twice the edge length" );
		}
	}
}

/// <summary>Knurl settings: which axis the grip runs along, which wave, how fine, how deep.</summary>
public sealed class KnurlOptions
{
	public string Axis;
	public string Pattern;
	public float Pitch;
	public float Depth;

	public KnurlOptions( string axis, string pattern, float pitch, float depth )
	{
		Axis = axis; Pattern = pattern; Pitch = pitch; Depth = depth;
	}
}

public sealed class KnurlReport
{
	public float MeanEdgeLength;
	public float MaxDisplacement;
}

/// <summary>
/// The knurl displacement itself, separate from the feature so tests and the
/// eventual SurfaceDetail home can call it without a studio. Displaces in
/// place: vertex count and face order do not change, so UVs, colours, skin
/// and morphs stay aligned.
/// </summary>
public static class KnurlPattern
{
	public static KnurlReport Apply( PolyMesh mesh, KnurlOptions options )
	{
		var report = new KnurlReport();
		if ( mesh is null || mesh.VertexCount == 0 ) return report;

		var pitch = Math.Max( 0.05f, options.Pitch );
		var depth = Math.Max( 0f, options.Depth );

		// Per-vertex normals by Newell accumulation over faces: robust for the
		// n-gon caps a cylinder carries, where a first-three-verts normal is not.
		var normals = new Vec3[mesh.VertexCount];
		var edgeSum = 0.0; var edgeCount = 0;
		foreach ( var face in mesh.Faces )
		{
			var idx = face.Indices;
			if ( idx.Length < 3 ) continue;
			var nx = 0f; var ny = 0f; var nz = 0f;
			for ( var i = 0; i < idx.Length; i++ )
			{
				var a = mesh.Positions[idx[i]];
				var b = mesh.Positions[idx[(i + 1) % idx.Length]];
				nx += (a.y - b.y) * (a.z + b.z);
				ny += (a.z - b.z) * (a.x + b.x);
				nz += (a.x - b.x) * (a.y + b.y);
				edgeSum += (b - a).Length; edgeCount++;
			}
			var n = new Vec3( nx, ny, nz );
			if ( n.Length < 1e-9f ) continue;
			n = n.Normal;
			foreach ( var v in idx ) normals[v] += n;
		}
		report.MeanEdgeLength = edgeCount == 0 ? 0f : (float)(edgeSum / edgeCount);

		for ( var v = 0; v < mesh.VertexCount; v++ )
		{
			var p = mesh.Positions[v];
			var n = normals[v];
			if ( n.Length < 1e-9f ) continue;
			n = n.Normal;

			// Circumference coordinate is arc length (angle * radius), so the
			// same pitch reads the same on a fat grip and a thin one.
			float axial, u1, u2;
			switch ( options.Axis )
			{
				case "X":
					axial = p.x;
					u1 = p.y; u2 = p.z;
					break;
				case "Y":
					axial = p.y;
					u1 = p.z; u2 = p.x;
					break;
				default:
					axial = p.z;
					u1 = p.x; u2 = p.y;
					break;
			}
			var radius = MathF.Sqrt( u1 * u1 + u2 * u2 );
			if ( radius < 1e-4f ) continue;
			var angle = MathF.Atan2( u2, u1 );
			var u = angle * radius / pitch;
			var w = axial / pitch;

			var h = options.Pattern switch
			{
				"Straight" => Tri( u ),
				"Diagonal" => Tri( u + w ),
				_ => Math.Min( Tri( u + w ), Tri( u - w ) ),
			} * depth;

			mesh.Positions[v] = p + n * h;
			if ( h > report.MaxDisplacement ) report.MaxDisplacement = h;
		}
		return report;
	}

	/// <summary>Triangle wave: 0 at integers, 1 at half-integers, linear between.</summary>
	static float Tri( float t )
	{
		var f = t - MathF.Floor( t );
		return 1f - Math.Abs( 2f * f - 1f );
	}
}
