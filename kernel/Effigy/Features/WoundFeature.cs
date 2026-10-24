using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Wound: an anatomical gash cut into a body, not a red paint streak.
///
///   add Wound target=Torso path="0,0,10; 0,4,10; 0,8,9" width=1.2 depth=0.6 liproll=0.5 layers=Skin,Fat,Muscle,Bone tendons=3
///
/// The path is a 3D incision line across the surface. Vertices near it are displaced along
/// their own normals: the lips roll up into a raised margin (LipRoll), the wound floor sinks
/// by Depth, and the wall between them steps through anatomical terraces — outer ring Skin,
/// then Fat, then Muscle, then Bone at the centre — each with its own material slot and
/// vertex colour, so the cut reads as depth even before any texture bake. Tendons, when
/// asked for, are real sinew geometry spanned lip-to-lip (see <see cref="Tendons"/>), not
/// paint: they sag in world -z, Tendons' own convention, which is only "down" when the
/// wound's surface faces up.
///
/// HONEST LIMITS. This displaces the existing mesh; it does not cut it open, so a wound on a
/// coarse cage reads as a dent with coloured terraces, not a hole with an interior — put a
/// Subdivide above it if the gash needs real interior walls. The terrace boundaries are
/// radial fractions of the width, not measured anatomy: a real wound's fat layer is not
/// 25% of anything. And the target body is modified in place, like FleshMaterial does: the
/// wound is a step in the history, not a new part.
/// </summary>
public sealed class WoundFeature : Feature
{
	public override string TypeName => "Wound";
	public override GeometryKind Accepts => GeometryKind.Body;

	/// <summary>Body to wound, by name or id.</summary>
	public readonly StringParam Target = new( "Target", "" );

	/// <summary>Incision line: "x,y,z; x,y,z; ..." in model units.</summary>
	public readonly StringParam Path = new( "Path", "" );

	public readonly FloatParam Width = new( "Width", 1f, 0.01f, unit: "u" );
	public readonly FloatParam Depth = new( "Depth", 0.5f, 0f, unit: "u" );

	/// <summary>How rolled the lips are, 0 = flat cut edge, 1 = fully rolled margin.</summary>
	public readonly FloatParam LipRoll = new( "Lip roll", 0.5f, 0f, 1f );

	/// <summary>Terrace layers from rim to centre, comma-separated.</summary>
	public readonly StringParam Layers = new( "Layers", "Skin,Fat,Muscle,Bone" );

	/// <summary>Sinew strands spanned across the gash, 0 = none.</summary>
	public readonly IntParam TendonCount = new( "Tendons", 0, 0, 64 );

	public readonly IntParam Seed = new( "Seed", 42, 0, int.MaxValue ) { Slider = false };
	public readonly IntParam SlotSkin = new( "Skin slot", 0, 0, 63 ) { Slider = false };
	public readonly IntParam SlotFat = new( "Fat slot", 1, 0, 63 ) { Slider = false };
	public readonly IntParam SlotMuscle = new( "Muscle slot", 2, 0, 63 ) { Slider = false };
	public readonly IntParam SlotBone = new( "Bone slot", 3, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Target, Path, Width, Depth, LipRoll, Layers, TendonCount, Seed, SlotSkin, SlotFat, SlotMuscle, SlotBone };
	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { Seed, SlotSkin, SlotFat, SlotMuscle, SlotBone };

	/// <summary>Slot per terrace layer, in Layers order.</summary>
	public int SlotFor( string layer ) => layer.ToLowerInvariant() switch
	{
		"skin" => SlotSkin.Clamped,
		"fat" => SlotFat.Clamped,
		"muscle" => SlotMuscle.Clamped,
		"bone" => SlotBone.Clamped,
		_ => SlotSkin.Clamped,
	};

	/// <summary>Parse "x,y,z; ..." into points, or null with a reason.</summary>
	internal static List<Vec3> ParsePath( string text, out string why )
	{
		why = null;
		var points = new List<Vec3>();
		foreach ( var piece in (text ?? "").Split( ';' ) )
		{
			var t = piece.Trim();
			if ( t.Length == 0 ) continue;
			if ( !ModelMeasure.TryParseVec3( t, out var p ) )
			{
				why = $"'{t}' is not a point x,y,z.";
				return null;
			}
			points.Add( p );
		}
		if ( points.Count < 2 ) why = "A wound path needs at least two points.";
		return points.Count < 2 ? null : points;
	}

	/// <summary>Terrace index for a radial fraction r (0 = centre, 1 = wound edge) over n layers.</summary>
	internal static int LayerIndex( float r, int layers ) =>
		Math.Clamp( (int)((1f - Math.Clamp( r, 0f, 1f )) * layers), 0, Math.Max( 0, layers - 1 ) );

	/// <summary>Nearest point on a polyline, with the segment tangent there.</summary>
	internal static (Vec3 Point, Vec3 Tangent, float Distance) NearestOnPath( Vec3 p, IReadOnlyList<Vec3> path )
	{
		var best = (Point: path[0], Tangent: (path[1] - path[0]).Normal, Distance: float.MaxValue);
		for ( var i = 0; i + 1 < path.Count; i++ )
		{
			var a = path[i]; var b = path[i + 1];
			var ab = b - a;
			var len2 = ab.LengthSquared;
			var t = len2 < 1e-12f ? 0f : Math.Clamp( Vec3.Dot( p - a, ab ) / len2, 0f, 1f );
			var q = a + ab * t;
			var d = (p - q).Length;
			if ( d < best.Distance ) best = (q, ab.LengthSquared > 1e-12f ? ab.Normal : new Vec3( 1, 0, 0 ), d);
		}
		return best;
	}

	static void Basis( Vec3 d, out Vec3 x, out Vec3 y )
	{
		var helper = MathF.Abs( d.z ) < 0.92f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 );
		x = Vec3.Cross( d, helper ).Normal;
		y = Vec3.Cross( d, x );
	}

	protected override void Execute( FeatureContext ctx )
	{
		var body = ctx.Bodies.FirstOrDefault( b =>
			string.Equals( b.Name, Target.Value, StringComparison.OrdinalIgnoreCase ) || b.Id == Target.Value );
		if ( body is null )
			Fail( $"No body called '{Target.Value}' to wound.", "A wound is cut into a body already in the studio.", "Add the body first, then add Wound" );
		var mesh = body.Mesh;
		if ( mesh is null || mesh.FaceCount == 0 )
			Fail( $"'{body.Name}' has no faces to wound.", "A wound displaces a surface.", "Point Target at a body with real surface" );

		var path = ParsePath( Path.Value, out var why );
		if ( path is null )
			FailOn( "Path", why, "The path is a list of points separated by ';'.", "Write it like \"0,0,10; 0,4,10\"" );

		var layers = Layers.Value.Split( ',' ).Select( s => s.Trim() ).Where( s => s.Length > 0 ).ToList();
		if ( layers.Count == 0 )
			FailOn( "Layers", "No layers named.", "Layers lists the terraces from rim to centre.", "Use the default Skin,Fat,Muscle,Bone" );
		foreach ( var layer in layers )
			if ( layer.ToLowerInvariant() is not ("skin" or "fat" or "muscle" or "bone") )
				FailOn( "Layers", $"'{layer}' is not a terrace layer.", "Layers are Skin, Fat, Muscle and Bone.", "Remove it, or spell it as one of those" );

		var halfW = Width.Clamped * 0.5f;
		var lipWidth = LipRoll.Clamped * Width.Clamped;
		var outer = halfW + lipWidth;
		var normals = mesh.ComputeVertexNormals();
		var colours = mesh.HasVertexColors
			? mesh.VertexColors.ToArray()
			: Enumerable.Repeat( new Vec4( 1, 1, 1, 1 ), mesh.VertexCount ).ToArray();
		var layerOfVertex = new int[mesh.VertexCount];
		Array.Fill( layerOfVertex, -1 );
		var affected = 0;

		for ( var vi = 0; vi < mesh.VertexCount; vi++ )
		{
			var (_, _, dist) = NearestOnPath( mesh.Positions[vi], path );
			if ( dist > outer ) continue;
			var n = normals[vi];
			if ( n.LengthSquared < 1e-12f ) n = new Vec3( 0, 0, 1 );
			if ( dist <= halfW )
			{
				// Floor sinks deepest at the centre, flat at the edge: a bowl, not a cliff.
				var r = dist / halfW;
				mesh.Positions[vi] -= n * (Depth.Clamped * (1f - r * r));
				var li = LayerIndex( r, layers.Count );
				layerOfVertex[vi] = li;
				colours[vi] = FleshMaterial.WoundColour( layers[li] );
			}
			else if ( lipWidth > 0f )
			{
				// Rolled lip: highest right at the cut edge, fading to nothing at the outer rim.
				var t = (dist - halfW) / lipWidth;
				mesh.Positions[vi] += n * (LipRoll.Clamped * Depth.Clamped * 0.5f * (1f - t * t));
				layerOfVertex[vi] = 0;
				colours[vi] = FleshMaterial.WoundColour( layers[0] );
			}
			affected++;
		}

		if ( affected == 0 )
		{
			Warning = $"Path '{Path.Value}' touches no vertex of '{body.Name}' — the wound changed nothing. Move the path onto the surface, or widen Width.";
			return;
		}

		foreach ( var face in mesh.Faces )
		{
			var li = layerOfVertex[face.Indices[0]];
			if ( li >= 0 ) face.Material = SlotFor( layers[li] );
		}
		mesh.VertexColors = colours;

		var tendons = 0;
		if ( TendonCount.Clamped > 0 )
		{
			// Lip-to-lip anchors: the nearest surface point either side of the path at sampled
			// stations, so strands span the actual gash rather than a guessed offset.
			var tree = MeshBVH.Build( mesh );
			var rng = new Random( Seed.Clamped );
			var positions = new List<Vec3>();
			var faces = new List<Face>();
			// Arclength stations along the path.
			var total = 0f;
			var segLen = new float[path.Count - 1];
			for ( var i = 0; i + 1 < path.Count; i++ ) { segLen[i] = (path[i + 1] - path[i]).Length; total += segLen[i]; }
			for ( var k = 0; k < TendonCount.Clamped; k++ )
			{
				var target = total * (k + 0.5f) / TendonCount.Clamped;
				var acc = 0f; var station = path[0]; var tangent = (path[1] - path[0]).Normal;
				for ( var i = 0; i + 1 < path.Count; i++ )
				{
					if ( acc + segLen[i] >= target || i == path.Count - 2 )
					{
						var t = segLen[i] < 1e-9f ? 0f : (target - acc) / segLen[i];
						station = path[i] + (path[i + 1] - path[i]) * Math.Clamp( t, 0f, 1f );
						tangent = segLen[i] > 1e-12f ? (path[i + 1] - path[i]).Normal : tangent;
						break;
					}
					acc += segLen[i];
				}
				Basis( tangent, out var side, out _ );
				var a = tree.NearestSurface( mesh, station + side * halfW, float.MaxValue );
				var b = tree.NearestSurface( mesh, station - side * halfW, float.MaxValue );
				if ( a is null || b is null ) continue;
				var first = faces.Count;
				Tendons.AppendSpan( positions, faces, a.Value.Point, b.Value.Point, MathF.Max( 0.01f, Width.Clamped * 0.06f ), 1f, 0.125f, rng, 7, 12 );
				for ( var fi = first; fi < faces.Count; fi++ ) faces[fi].Material = SlotMuscle.Clamped;
				tendons++;
			}
			if ( tendons > 0 )
			{
				var offset = mesh.Positions.Count;
				mesh.Positions.AddRange( positions );
				foreach ( var f in faces )
				{
					var idx = f.Indices.Select( i => i + offset ).ToArray();
					mesh.Faces.Add( new Face( idx, f.UVs, f.Material ) );
				}
				var grown = colours.ToList();
				for ( var i = 0; i < positions.Count; i++ ) grown.Add( FleshMaterial.WoundColour( "Muscle" ) );
				mesh.VertexColors = grown.ToArray();
			}
		}

		Warning = tendons > 0
			? $"{affected} vertices wounded on '{body.Name}' ({layers.Count} terraces) with {tendons} tendon(s)."
			: $"{affected} vertices wounded on '{body.Name}' ({layers.Count} terraces).";
	}
}
