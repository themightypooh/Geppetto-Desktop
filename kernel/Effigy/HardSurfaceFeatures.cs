using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Hard-surface detailing layer (wave 3), from agent-research-hardsurface.
/// All features here are NEW feature classes, so they register by reflection and
/// no existing feature file had to be edited for them. The only edited file is
/// EdgeBlend.cs, which gained profile/asymmetric/variable-radius support that
/// BevelProfile and VariableFillet call into.
///
/// Shared conventions, stated once so each feature does not re-litigate them:
/// - A script line is `add Groove path=0,0,1;4,0,1 width=0.2 depth=0.15 ...` — point
///   lists are semicolon-separated `x,y,z` triples, exactly like Fin's root/tip.
/// - Z is up in this kernel (describe reports "z up"), so groove normals and the
///   wear direction default to (0,0,1) for a top face.
/// - Warnings are the report channel: a feature that ran has no other way to say
///   what it did, so Cleanup and EdgeWear warn with their counts even on success.
/// </summary>
public static class HardSurfacePaths
{
	public static List<Vec3> ParsePoints( string text )
	{
		var points = new List<Vec3>();
		if ( string.IsNullOrWhiteSpace( text ) ) return points;
		foreach ( var part in text.Split( ';' ) )
		{
			var p = part.Trim();
			if ( p.Length == 0 ) continue;
			var nums = p.Split( ',' );
			if ( nums.Length != 3
				|| !float.TryParse( nums[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x )
				|| !float.TryParse( nums[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y )
				|| !float.TryParse( nums[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z ) )
				throw new ArgumentException( $"Point '{p}' is not an x,y,z triple." );
			points.Add( new Vec3( x, y, z ) );
		}
		return points;
	}

	public static float FalloffWeight( float t, string curve ) => curve switch
	{
		"Sphere" => MathF.Sqrt( Math.Max( 0f, 1f - t * t ) ),
		"Root" => MathF.Sqrt( Math.Max( 0f, 1f - t ) ),
		"InverseSquare" => (1f - t) * (1f - t) / (1f + 3f * t * t),
		"Sharp" => (1f - t) * (1f - t) * (1f - t),
		"Linear" => 1f - t,
		_ => (1f + MathF.Cos( MathF.PI * t )) * 0.5f, // Smooth: cosine
	};

	public static float MeanEdgeLength( PolyMesh mesh )
	{
		var sum = 0.0; var count = 0;
		foreach ( var f in mesh.Faces )
			for ( var i = 0; i < f.Count; i++ )
			{
				sum += (mesh.Positions[f.Indices[(i + 1) % f.Count]] - mesh.Positions[f.Indices[i]]).Length;
				count++;
			}
		return count == 0 ? 0f : (float)(sum / count);
	}

	/// <summary>Open edges have exactly one adjacent face; non-manifold edges have 3+.</summary>
	public static (int Open, int NonManifold) EdgeCounts( PolyMesh mesh )
	{
		var open = 0; var non = 0;
		foreach ( var (_, faces) in mesh.BuildEdgeFaces() )
		{
			if ( faces.Count == 1 ) open++;
			else if ( faces.Count > 2 ) non++;
		}
		return (open, non);
	}
}

/// <summary>Groove / panel line (research rank 1). A sweep prism along a path on a
/// face, subtracted with the exact boolean, with the rim edges filleted at a
/// fraction of the width — the same seam-only treatment Boolean already does.
///
/// The cutter overshoots the path ends by half the width along the path, and
/// sticks out above the surface by a full width, so its end faces and its top
/// face are never coplanar with the part — the Hole through-hole trick, applied
/// to a line instead of a point. That means a groove reads slightly longer than
/// its path (half a width at each end); a panel line that must stop exactly
/// should end its path half a width short of the stop.
/// Profile: Square is a rectangular channel, V is a triangular channel, U is a
/// flat-bottomed channel with rounded lower corners. Draft tapers Square and U
/// walls inward by that angle; V already tapers and ignores Draft.
/// </summary>
public sealed class GrooveFeature : Feature
{
	public override string TypeName => "Groove";
	public override GeometryKind Accepts => GeometryKind.Body;

	/// <summary>Clone welded exactly the way the topology verb welds
	/// (TopologyReport.Measure): quantize by diagonal*1e-6 with rounding,
	/// collapse consecutive duplicates in each face ring, drop faces that
	/// collapse below 3 distinct vertices. Edge counts on this clone then
	/// match the welded topology counts. Counting only — the clone is
	/// discarded. (Cleanup's Weld is NOT used here: it drops collapsed
	/// faces from the stored mesh, which itself opens edges.)</summary>
	private static PolyMesh WeldedCloneForCount( PolyMesh mesh )
	{
		var m = mesh.Clone();
		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in m.Positions )
		{
			lo = new Vec3( MathF.Min( lo.x, p.x ), MathF.Min( lo.y, p.y ), MathF.Min( lo.z, p.z ) );
			hi = new Vec3( MathF.Max( hi.x, p.x ), MathF.Max( hi.y, p.y ), MathF.Max( hi.z, p.z ) );
		}
		var weld = MathF.Max( (hi - lo).Length * 1e-6f, 1e-7f );
		var cell = new Dictionary<(long, long, long), int>();
		var kept = new List<Vec3>();
		var remap = new int[m.Positions.Count];
		for ( var i = 0; i < m.Positions.Count; i++ )
		{
			var p = m.Positions[i];
			var key = ((long)MathF.Round( p.x / weld ), (long)MathF.Round( p.y / weld ), (long)MathF.Round( p.z / weld ));
			if ( cell.TryGetValue( key, out var k ) ) remap[i] = k;
			else { remap[i] = kept.Count; cell[key] = kept.Count; kept.Add( p ); }
		}
		var faces = new List<Face>();
		foreach ( var f in m.Faces )
		{
			var ring = new List<int>();
			foreach ( var index in f.Indices )
			{
				var w = remap[index];
				if ( ring.Count == 0 || ring[^1] != w ) ring.Add( w );
			}
			if ( ring.Count > 1 && ring[^1] == ring[0] ) ring.RemoveAt( ring.Count - 1 );
			if ( ring.Count < 3 || ring.Distinct().Count() != ring.Count ) continue;
			faces.Add( new Face( ring.ToArray(), null, f.Material ) );
		}
		m.Positions = kept;
		m.Faces = faces;
		return m;
	}

	private static PolyMesh TriangulateNgons( PolyMesh mesh )
	{
		// The exact boolean leaves n-gon fragments, and a second boolean on
		// that fragmented mesh comes back open (proven: two grooves on one
		// plate gave 6 open edges, each groove alone is closed). Splitting
		// every n-gon into triangles keeps boundary edges intact, so closure
		// is preserved and the next boolean gets an all-triangle input.
		var hasFaceUv2 = mesh.FaceUV2 is not null && mesh.FaceUV2.Length == mesh.Faces.Count;
		var faces = new List<Face>( mesh.Faces.Count );
		var faceUv2 = hasFaceUv2 ? new List<int[]>() : null;
		for ( var fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[fi];
			// Faces with a repeated corner (degenerate slivers from a blend)
			// must pass through untouched: splitting them drops edges their
			// neighbours rely on for closure, and the next boolean then
			// refuses the mesh as not closed. Only clean polygons split.
			var repeats = false;
			for ( var a = 1; a < f.Indices.Length && !repeats; a++ )
				for ( var b = 0; b < a; b++ )
					if ( f.Indices[a] == f.Indices[b] ) { repeats = true; break; }
			if ( f.Indices.Length <= 3 || repeats ) { faces.Add( f ); if ( hasFaceUv2 ) faceUv2!.Add( mesh.FaceUV2![fi] ); continue; }
			var corners = new Vec3[f.Indices.Length];
			for ( var j = 0; j < f.Indices.Length; j++ ) corners[j] = mesh.Positions[f.Indices[j]];
			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				faces.Add( new Face( new[] { f.Indices[ia], f.Indices[ib], f.Indices[ic] },
					f.UVs.Length == f.Count ? new[] { f.UVs[ia], f.UVs[ib], f.UVs[ic] } : null, f.Material ) );
				if ( hasFaceUv2 ) faceUv2!.Add( new[] { mesh.FaceUV2![fi][ia], mesh.FaceUV2[fi][ib], mesh.FaceUV2[fi][ic] } );
			}
		}
		mesh.Faces = faces;
		if ( hasFaceUv2 ) mesh.FaceUV2 = faceUv2!.ToArray();
		return mesh;
	}

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly StringParam Path = new( "Path" );
	public readonly Vec3Param Normal = new( "Normal", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam Width = new( "Width", 0.2f, 0.0001f, unit: "u" );
	public readonly FloatParam Depth = new( "Depth", 0.2f, 0.0001f, unit: "u" );
	public readonly ChoiceParam Profile = new( "Profile", new[] { "Square", "V", "U" } );
	public readonly FloatParam Draft = new( "Draft", 0f, 0f, 45f, unit: "deg" );
	public readonly BoolParam Rim = new( "Chamfer rim", true );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Path, Normal, Width, Depth, Profile, Draft, Rim };

	protected override void Execute( FeatureContext ctx )
	{
		List<Vec3> points;
		try { points = HardSurfacePaths.ParsePoints( Path.Value ); }
		catch ( ArgumentException e ) { Fail( "Groove path cannot be read", e.Message, "Write points as x,y,z;x,y,z" ); return; }

		if ( points.Count < 2 )
		{
			Fail( "A groove needs a path of at least two points",
				$"Path gave {points.Count} point(s).", "Write points as x,y,z;x,y,z" );
			return;
		}

		var n = Normal.Value;
		if ( n.LengthSquared < 1e-12f )
		{
			Fail( "Groove normal is zero", "The normal says which way is out of the face.",
				"Set Normal to the face normal, e.g. 0,0,1 for a top face" );
			return;
		}
		n = n.Normal;
		var width = Width.Clamped;
		var depth = Depth.Clamped;

		// Extended path: half a width past each end, so the cutter exits the solid.
		var dir0 = (points[1] - points[0]).Normal;
		var dirN = (points[^1] - points[^2]).Normal;
		var stations = new List<Vec3> { points[0] - dir0 * width * 0.5f };
		stations.AddRange( points );
		stations.Add( points[^1] + dirN * width * 0.5f );

		// Frames: tangent per station, side = in-plane perpendicular to tangent.
		var frames = new List<(Vec3 Tangent, Vec3 Side)>();
		for ( var i = 0; i < stations.Count; i++ )
		{
			Vec3 t;
			if ( i == 0 ) t = (stations[1] - stations[0]).Normal;
			else if ( i == stations.Count - 1 ) t = (stations[i] - stations[i - 1]).Normal;
			else
			{
				t = (stations[i + 1] - stations[i - 1]);
				t = t.LengthSquared < 1e-12f ? (stations[i + 1] - stations[i]).Normal : t.Normal;
			}
			var side = Vec3.Cross( t, n );
			if ( side.LengthSquared < 1e-10f )
			{
				Fail( "Groove path runs along its own normal",
					"A groove path must lie in the face, not poke through it.",
					"Give a path on the face and a Normal pointing out of it" );
				return;
			}
			frames.Add( (t, side.Normal) );
		}

		// Section polygon in (side, normal) coordinates. Top sits a full width above
		// the path plane so the cutter always pokes out of the surface.
		var top = width;
		var half = width * 0.5f;
		var draftRad = Draft.Clamped * MathF.PI / 180f;
		var bottomHalf = Math.Max( width * 0.05f, half - depth * MathF.Tan( draftRad ) );
		var section = Profile.Value switch
		{
			"V" => new List<(float S, float N)> { (-half, top), (half, top), (0f, -depth) },
			"U" => new List<(float S, float N)>
			{
				(-half, top), (half, top), (bottomHalf, -depth + Math.Min( width * 0.25f, depth * 0.5f )),
				(0f, -depth), (-bottomHalf, -depth + Math.Min( width * 0.25f, depth * 0.5f )),
			},
			_ => new List<(float S, float N)> { (-half, top), (half, top), (bottomHalf, -depth), (-bottomHalf, -depth) },
		};

		var cutter = new PolyMesh();
		var ring = new List<int[]>();
		for ( var i = 0; i < stations.Count; i++ )
		{
			var row = new int[section.Count];
			for ( var j = 0; j < section.Count; j++ )
				row[j] = cutter.AddVertex( stations[i] + frames[i].Side * section[j].S + n * section[j].N );
			ring.Add( row );
		}
		for ( var i = 0; i < stations.Count - 1; i++ )
			for ( var j = 0; j < section.Count; j++ )
			{
				var j2 = (j + 1) % section.Count;
				cutter.AddFace( new[] { ring[i][j], ring[i][j2], ring[i + 1][j2], ring[i + 1][j] } );
			}
		// Caps as fans from the section centroid vertex.
		for ( var end = 0; end < 2; end++ )
		{
			var row = end == 0 ? ring[0] : ring[^1];
			var centre = new Vec3( 0, 0, 0 );
			foreach ( var v in row ) centre += cutter.Positions[v];
			centre = centre * (1f / row.Length);
			var ci = cutter.AddVertex( centre );
			for ( var j = 0; j < section.Count; j++ )
			{
				var j2 = (j + 1) % section.Count;
				if ( end == 0 ) cutter.AddFace( new[] { ci, row[j2], row[j] } );
				else cutter.AddFace( new[] { ci, row[j], row[j2] } );
			}
		}
		if ( cutter.SignedVolume() < 0f )
			foreach ( var f in cutter.Faces ) Array.Reverse( f.Indices );

		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var meanEdge = HardSurfacePaths.MeanEdgeLength( mesh );
			var lo = float.MaxValue; var hi = float.MinValue;
			foreach ( var p in mesh.Positions )
			{
				var d = Vec3.Dot( p, n );
				if ( d < lo ) lo = d;
				if ( d > hi ) hi = d;
			}
			var extent = hi - lo;
			if ( depth >= extent )
			{
				Fail( "Groove is deeper than the part is thick",
					$"Depth is {depth:0.###} and the part spans {extent:0.###} along the groove normal, so the groove would cut clean through.",
					$"Reduce Depth below {extent:0.###}", "Or groove a thicker part" );
				return;
			}
			if ( meanEdge > 0f && width < 2f * meanEdge )
				Warn( $"Groove width {width:0.###} is finer than this mesh can hold",
					$"Mean edge length is {meanEdge:0.###}, so groove walls narrower than twice that alias into lumps.",
					"Subdivide the part first", "Or raise Width above twice the edge length" );

			var original = mesh;
			PolyMesh cut;
			try { cut = MeshBoolean.Apply( BooleanOp.Subtract, mesh, cutter ); cut = TriangulateNgons( cut ); }
			catch ( Exception e )
			{
				var cause = e is FeatureException fe && !string.IsNullOrEmpty( fe.Diagnostic?.Cause )
					? fe.Diagnostic.Cause : e.Message;
				Fail( "Groove cut failed", cause,
					"Simplify the path (no self-intersections, no hairpin turns)",
					"Raise Width, or lower Depth" );
				return;
			}
			body.Mesh = cut;

			if ( Rim.Value )
			{
				var seams = ExactMeshBoolean.SeamEdges( cut, original, cutter );
				// Rim only: the seam also runs along the groove floor and the end
				// walls, and filleting those on a coarse mesh opens the mesh
				// (proven: cut alone is closed, full-seam fillet is not). The rim
				// is the seam at the surface, i.e. within a quarter of the depth
				// of the path plane; the floor sits a full depth below it.
				var plane0 = points[0];
				var rimSeams = new HashSet<EdgeKey>();
				foreach ( var e in seams )
				{
					var a = cut.Positions[e.A]; var b = cut.Positions[e.B];
					var mid = (a + b) * 0.5f;
					if ( MathF.Abs( Vec3.Dot( mid - plane0, n ) ) < depth * 0.25f )
						rimSeams.Add( e );
				}
				if ( rimSeams.Count > 0 )
				{
					var rim = EdgeBlend.FilletReport( cut, Math.Min( width * 0.25f, depth * 0.5f ), 2, rimSeams );
					if ( rim.Failure is null )
					{
						// A rim fillet on a mesh that already carries a groove
						// (blend strips from the first groove) can come back
						// open — the exact boolean's next cut then inherits
						// the damage. Never let the rim make closure worse:
						// compare the FINAL mesh (after triangulation) with
						// the cut, and if it is worse, keep the cut and stay
						// sharp.
						// Compare WELDED CLONES, not the meshes themselves:
						// the boolean and the blend both leave coincident
						// duplicate vertices, so raw index counts hide
						// boundary edges that welding reveals — comparing
						// unwelded counts let a 14-open rim through. The
						// clone weld drops nothing (see WeldedCloneForCount):
						// Cleanup's Weld drops collapsed faces, which itself
						// opens edges and made clean rims look open.
						var candidate = TriangulateNgons( rim.Mesh );
						var before = HardSurfacePaths.EdgeCounts( WeldedCloneForCount( cut ) );
						var after = HardSurfacePaths.EdgeCounts( WeldedCloneForCount( candidate ) );
						if ( after.Open <= before.Open && after.NonManifold <= before.NonManifold )
							body.Mesh = candidate;
						else
							Warn( $"Groove rim skipped: rim {after.Open} open / {after.NonManifold} non-manifold vs cut {before.Open} open / {before.NonManifold} non-manifold",
								$"Filleting the rim left {after.Open} open and {after.NonManifold} non-manifold edges (cut alone: {before.Open} open, {before.NonManifold} non-manifold), so the groove edge stays sharp.",
								"Cut this groove before any other groove on the same face", "Or turn Chamfer rim off" );
					}
					else Warn( "Groove rim could not be chamfered, so the groove edge stays sharp",
						rim.Failure.Problem ?? "The rim edges could not be filleted.",
						"Lower Width", "Or turn Chamfer rim off" );
				}
			}
			var added = SeparatePieces( ctx, body );
			if ( added > 0 )
				Warn( $"Groove separated {body.Name} into {added + 1} part(s)",
					"The groove cut all the way through a thin wall.",
					"Reduce Depth if separating was not the intent" );
		}
	}
}

/// <summary>Bevel profiles (research rank 2). Flat is Chamfer, Round is Fillet,
/// TwoStep is two flats meeting at a ridge (break = how far the ridge sits from
/// the flat-chamfer line toward the original corner, 0 = flat, 0.9 = nearly
/// sharp), Bullnose is a round-over at the largest radius that still fits,
/// Asymmetric is a chamfer with two setbacks: the face whose normal is closest
/// to Asymmetric side gets Size, the other face gets Second setback.
/// Asymmetric is a chamfer profile only — a rounded asymmetric blend would need
/// two different arc centres and is refused with that explanation.
/// </summary>
public sealed class BevelProfileFeature : Feature
{
	public override string TypeName => "BevelProfile";
	public override GeometryKind Accepts => GeometryKind.Edge | GeometryKind.Face | GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Size = new( "Size", 0.1f, 0.0001f, unit: "u" );
	public readonly ChoiceParam Profile = new( "Profile", new[] { "Flat", "Round", "TwoStep", "Bullnose", "Asymmetric" } );
	public readonly FloatParam Break = new( "Break", 0.4f, 0f, 0.9f );
	public readonly FloatParam SecondSetback = new( "Second setback", 0.05f, 0.0001f, unit: "u" );
	public readonly Vec3Param Side = new( "Asymmetric side", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam AngleThreshold = new( "Angle threshold", 15f, 0f, 180f, unit: "deg" );
	public readonly IntParam Segments = new( "Segments", 4, 1, 16 );
	public List<EdgeRef> Edges = new();

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Size, Profile, Break, SecondSetback, Side, AngleThreshold, Segments };

	protected override void Execute( FeatureContext ctx )
	{
		if ( Profile.Value == "Asymmetric" && SecondSetback.Clamped <= 0f )
		{
			Fail( "Asymmetric bevel needs a second setback",
				"Second setback is zero, so there is no second face distance to cut to.",
				"Set Second setback above zero" );
			return;
		}
		BlendReport Report( PolyMesh mesh, HashSet<EdgeKey> keys )
		{
			var size = Size.Clamped;
			return Profile.Value switch
			{
				"Flat" => keys is null
					? EdgeBlend.ChamferReport( mesh, size, AngleThreshold.Clamped )
					: EdgeBlend.ChamferReport( mesh, size, keys ),
				"Asymmetric" => keys is null
					? EdgeBlend.ChamferAsymmetricReport( mesh, size, SecondSetback.Clamped, Side.Value, AngleThreshold.Clamped )
					: EdgeBlend.ChamferAsymmetricReport( mesh, size, SecondSetback.Clamped, Side.Value, keys ),
				"TwoStep" => keys is null
					? EdgeBlend.FilletProfileReport( mesh, size, AngleThreshold.Clamped, 2, EdgeBlend.BlendProfile.TwoStep, Break.Clamped )
					: EdgeBlend.FilletProfileReport( mesh, size, 2, keys, EdgeBlend.BlendProfile.TwoStep, Break.Clamped ),
				"Bullnose" => keys is null
					? EdgeBlend.FilletProfileReport( mesh, size, AngleThreshold.Clamped, Segments.Clamped, EdgeBlend.BlendProfile.Bullnose )
					: EdgeBlend.FilletProfileReport( mesh, size, Segments.Clamped, keys, EdgeBlend.BlendProfile.Bullnose ),
				_ => keys is null
					? EdgeBlend.FilletReport( mesh, size, AngleThreshold.Clamped, Segments.Clamped )
					: EdgeBlend.FilletReport( mesh, size, Segments.Clamped, keys ),
			};
		}
		CommitBlend( BlendBodies( ctx, Edges, Report ), "Size" );
	}
}

/// <summary>Variable-radius fillet (research rank 3, step 1). Every sharp edge is
/// rounded at a radius that fades with distance from Falloff centre, using the
/// same falloff curves the CAD proportional-editing work added to EditOp.
/// Falloff radius 0 means no falloff: every sharp edge gets Radius, which is
/// just Fillet — the feature refuses that degenerate case and says so, rather
/// than silently duplicating Fillet.
/// </summary>
public sealed class VariableFilletFeature : Feature
{
	public override string TypeName => "VariableFillet";
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Radius = new( "Radius", 0.2f, 0.0001f, unit: "u" );
	public readonly Vec3Param At = new( "Falloff centre", new Vec3( 0, 0, 0 ) );
	public readonly FloatParam FalloffRadius = new( "Falloff radius", 0f, 0f, unit: "u" );
	public readonly ChoiceParam FalloffCurve = new( "Falloff curve", new[] { "Smooth", "Sphere", "Root", "InverseSquare", "Sharp", "Linear" } );
	public readonly FloatParam AngleThreshold = new( "Angle threshold", 15f, 0f, 180f, unit: "deg" );
	public readonly IntParam Segments = new( "Segments", 4, 1, 16 );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Radius, At, FalloffRadius, FalloffCurve, AngleThreshold, Segments };

	protected override void Execute( FeatureContext ctx )
	{
		if ( FalloffRadius.Clamped <= 0f )
		{
			Fail( "Variable fillet needs a falloff radius",
				"Falloff radius is 0, so every edge would get the same radius — that is just Fillet.",
				"Set Falloff radius above zero, or use Fillet" );
			return;
		}
		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var edgeFaces = mesh.BuildEdgeFaces();
			var normals = mesh.Faces.Select( mesh.FaceNormal ).ToArray();
			var sizes = new Dictionary<EdgeKey, float>();
			foreach ( var (key, faces) in edgeFaces )
			{
				if ( faces.Count != 2 ) continue;
				var deg = MathF.Acos( Math.Clamp( Vec3.Dot( normals[faces[0]], normals[faces[1]] ), -1f, 1f ) ) * 180f / MathF.PI;
				if ( deg < AngleThreshold.Clamped ) continue;
				var mid = (mesh.Positions[key.A] + mesh.Positions[key.B]) * 0.5f;
				var t = Math.Clamp( (mid - At.Value).Length / FalloffRadius.Clamped, 0f, 1f );
				var r = Radius.Clamped * HardSurfacePaths.FalloffWeight( t, FalloffCurve.Value );
				if ( r > Radius.Clamped * 0.01f ) sizes[key] = r;
			}
			if ( sizes.Count == 0 )
			{
				Fail( "No edge is in range of the falloff",
					$"No edge sharper than {AngleThreshold.Clamped:0.#}° sits close enough to the falloff centre to keep a radius above 1% of {Radius.Clamped:0.###}.",
					"Move Falloff centre onto the part", "Raise Falloff radius", "Lower Angle threshold" );
				return;
			}
			var report = EdgeBlend.FilletVariableReport( mesh, sizes, Segments.Clamped );
			if ( report.Failure is not null )
				throw new FeatureException( report.Failure );
			body.Mesh = report.Mesh;
			if ( report.Warnings.Count > 0 )
				Warn( report.Warnings[0].Problem, report.Warnings[0].Cause ?? "",
					report.Warnings[0].Remedies.ToArray() );
		}
	}
}

/// <summary>Crease subdivision (research rank 3, step 2, in the small form that
/// is actually buildable: CatmullClark already takes a crease map, Profile
/// already uses it, but no script feature exposed it). Edges sharper than the
/// threshold keep their position through subdivision at the given weight
/// (1 = fully sharp), so a bevelled part can be subdivided without melting.
/// This is the support-loop equivalent without adding geometry.
/// </summary>
public sealed class CreaseSubdivideFeature : Feature
{
	public override string TypeName => "CreaseSubdivide";
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly IntParam Levels = new( "Levels", 1, 0, 6 );
	public readonly FloatParam AngleThreshold = new( "Angle threshold", 15f, 0f, 180f, unit: "deg" );
	public readonly FloatParam Weight = new( "Crease weight", 1f, 0f, 1f );

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Levels, AngleThreshold, Weight };

	protected override void Execute( FeatureContext ctx )
	{
		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var edgeFaces = mesh.BuildEdgeFaces();
			var normals = mesh.Faces.Select( mesh.FaceNormal ).ToArray();
			var creases = new Dictionary<EdgeKey, float>();
			foreach ( var (key, faces) in edgeFaces )
			{
				if ( faces.Count != 2 ) continue;
				var deg = MathF.Acos( Math.Clamp( Vec3.Dot( normals[faces[0]], normals[faces[1]] ), -1f, 1f ) ) * 180f / MathF.PI;
				if ( deg >= AngleThreshold.Clamped ) creases[key] = Weight.Clamped;
			}
			if ( creases.Count == 0 )
				Warn( "No edge is sharper than the threshold, so nothing is creased",
					"The subdivision still runs, but no edge is held sharp.",
					"Lower Angle threshold" );
			body.Mesh = CatmullClark.Subdivide( mesh, Levels.Clamped, creases, out _ );
		}
	}
}

/// <summary>Fastener instancer (research rank 4). One fastener is built from
/// existing primitives — hex or socket head, washer, shaft — then instanced at
/// each point, oriented to Normal, and KEPT AS SEPARATE BODIES, never boolean
/// merged: merging re-opens the boolean failure modes and destroys the
/// material-slot split (dark steel fastener on a painted body is half the read).
/// Recess Cross is two thin boxes subtracted from the head; Hex is a hex
/// cylinder subtracted from the head. The shaft points INTO the body (against
/// Normal), so only the head and washer show — that is what a fitted bolt
/// looks like; a bolt floating above the surface would be the error.
/// </summary>
public sealed class FastenerFeature : Feature
{
	public override string TypeName => "Fastener";
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly StringParam Points = new( "Points" );
	public readonly Vec3Param Normal = new( "Normal", new Vec3( 0, 0, 1 ) );
	public readonly ChoiceParam Type = new( "Type", new[] { "HexBolt", "SocketHead", "Rivet", "VentSlot" } );
	public readonly FloatParam Diameter = new( "Diameter", 0.5f, 0.0001f, unit: "u" );
	public readonly FloatParam HeadHeight = new( "Head height", 0.2f, 0.0001f, unit: "u" );
	public readonly ChoiceParam Recess = new( "Recess", new[] { "None", "Cross", "Hex" } );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Points, Normal, Type, Diameter, HeadHeight, Recess, Material };

	protected override void Execute( FeatureContext ctx )
	{
		List<Vec3> points;
		try { points = HardSurfacePaths.ParsePoints( Points.Value ); }
		catch ( ArgumentException e ) { Fail( "Fastener points cannot be read", e.Message, "Write points as x,y,z;x,y,z" ); return; }
		if ( points.Count == 0 )
		{
			Fail( "Fasteners need at least one point", "No placement point was given.",
				"Write points as x,y,z;x,y,z" );
			return;
		}
		var n = Normal.Value;
		if ( n.LengthSquared < 1e-12f )
		{
			Fail( "Fastener normal is zero", "The normal says which way the bolt head faces.",
				"Set Normal to the face normal, e.g. 0,0,1 for a top face" );
			return;
		}
		n = n.Normal;
		var d = Diameter.Clamped;
		var hh = HeadHeight.Clamped;

		// Rotation from local +Z to the face normal.
		var zAxis = new Vec3( 0, 0, 1 );
		var dot = Math.Clamp( Vec3.Dot( zAxis, n ), -1f, 1f );
		var axis = Vec3.Cross( zAxis, n );
		Xform orient;
		if ( axis.LengthSquared < 1e-12f )
			orient = dot < 0f ? Xform.Rotate( new Vec3( 1, 0, 0 ), MathF.PI ) : Xform.Rotate( zAxis, 0f );
		else
			orient = Xform.Rotate( axis.Normal, MathF.Acos( dot ) );

		var baseName = string.IsNullOrWhiteSpace( Name ) ? "Fastener" : Name;
		for ( var i = 0; i < points.Count; i++ )
		{
			var mesh = BuildFastener( d, hh );
			MeshTransform.Apply( mesh, orient );
			MeshTransform.Apply( mesh, Xform.Translate( points[i] ) );
			foreach ( var f in mesh.Faces ) f.Material = Material.Clamped;
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"{baseName} {i + 1}", mesh ) );
		}
	}

	PolyMesh BuildFastener( float d, float hh )
	{
		PolyMesh mesh;
		switch ( Type.Value )
		{
			case "SocketHead":
			{
				var head = Primitives.Cylinder( d * 0.5f, hh, 24, 0 );
				MeshTransform.Apply( head, Xform.Translate( new Vec3( 0, 0, hh * 0.5f ) ) );
				if ( Recess.Value == "Hex" )
				{
					var tool = Primitives.Cylinder( d * 0.25f, hh, 6, 0 );
					MeshTransform.Apply( tool, Xform.Translate( new Vec3( 0, 0, hh * 0.75f ) ) );
					head = MeshBoolean.Apply( BooleanOp.Subtract, head, tool );
				}
				var shaft = Primitives.Cylinder( d * 0.3f, d * 2f, 16, 0 );
				MeshTransform.Apply( shaft, Xform.Translate( new Vec3( 0, 0, -d ) ) );
				mesh = head;
				MeshTransform.Append( mesh, shaft );
				break;
			}
			case "Rivet":
			{
				var dome = Primitives.QuadSphere( d * 0.5f, 4, 0 );
				var shaft = Primitives.Cylinder( d * 0.3f, d * 1.5f, 16, 0 );
				MeshTransform.Apply( shaft, Xform.Translate( new Vec3( 0, 0, -d * 0.75f ) ) );
				mesh = dome;
				MeshTransform.Append( mesh, shaft );
				break;
			}
			case "VentSlot":
			{
				// A capsule lying flat along X, half-sunk so it reads as a slot bar.
				mesh = Primitives.Capsule( d * 0.25f, d, 16, 4, 0 );
				MeshTransform.Apply( mesh, Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI * 0.5f ) );
				MeshTransform.Apply( mesh, Xform.Translate( new Vec3( 0, 0, d * 0.12f ) ) );
				break;
			}
			default: // HexBolt
			{
				var head = Primitives.Cylinder( d * 0.55f, hh, 6, 0 );
				MeshTransform.Apply( head, Xform.Translate( new Vec3( 0, 0, hh * 0.5f ) ) );
				if ( Recess.Value == "Cross" )
				{
					// Sequential subtracts, but with STAGGERED depths: the
					// two slots' bottoms must not be coplanar, or the second
					// cut duplicates the first slot's bottom face over the
					// crossing square (that was the 32 non-manifold edges;
					// a boolean Union of the boxes gave 20 open edges, and
					// one appended multi-solid tool gave 3 open / 18
					// non-manifold — staggered sequential is the cleanest).
					var toolX = Primitives.Box( d * 0.8f, d * 0.18f, hh, 0 );
					var toolY = Primitives.Box( d * 0.18f, d * 0.8f, hh * 0.8f, 0 );
					MeshTransform.Apply( toolX, Xform.Translate( new Vec3( 0, 0, hh * 0.75f ) ) );
					MeshTransform.Apply( toolY, Xform.Translate( new Vec3( 0, 0, hh * 0.75f ) ) );
					head = MeshBoolean.Apply( BooleanOp.Subtract, head, toolX );
					head = MeshBoolean.Apply( BooleanOp.Subtract, head, toolY );
				}
				else if ( Recess.Value == "Hex" )
				{
					var tool = Primitives.Cylinder( d * 0.28f, hh, 6, 0 );
					MeshTransform.Apply( tool, Xform.Translate( new Vec3( 0, 0, hh * 0.75f ) ) );
					head = MeshBoolean.Apply( BooleanOp.Subtract, head, tool );
				}
				var washer = Primitives.Torus( d * 0.6f, d * 0.08f, 24, 8, 0 );
				MeshTransform.Apply( washer, Xform.Translate( new Vec3( 0, 0, d * 0.08f ) ) );
				var shaft = Primitives.Cylinder( d * 0.32f, d * 2f, 16, 0 );
				MeshTransform.Apply( shaft, Xform.Translate( new Vec3( 0, 0, -d ) ) );
				mesh = head;
				MeshTransform.Append( mesh, washer );
				MeshTransform.Append( mesh, shaft );
				break;
			}
		}
		return mesh;
	}
}

/// <summary>Boolean cleanup + pre-boolean check (research rank 5, steps 1 and 2).
/// Cleanup welds vertices within Epsilon, drops degenerate faces and unused
/// vertices, and reports open/non-manifold counts before and after (the counts
/// the topology verb already reports, wired into the feature's report).
/// Check does NOT alter anything: it warns about open/non-manifold edges,
/// near-miss vertex pairs, and coplanar face pairs between bodies in the
/// selection — the "nudge the cutter by 0.01 or expect slivers" warning, made
/// automatic. Step 3, the segments=64 hang, is NOT fixed here: the fragment
/// cap does not trigger there, and a hang that fails cleanly would still be a
/// failure — it needs its own investigation, and this feature does not pretend
/// otherwise.
/// </summary>
public sealed class CleanupFeature : Feature
{
	public override string TypeName => "Cleanup";
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Mode = new( "Mode", new[] { "Cleanup", "Check" } );
	public readonly FloatParam Epsilon = new( "Epsilon", 0.001f, 0f, unit: "u" );

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Mode, Epsilon };

	protected override void Execute( FeatureContext ctx )
	{
		var bodies = RequireBodies( ctx, Bodies );
		if ( Epsilon.Clamped <= 0f )
		{
			Fail( "Cleanup epsilon is zero", "Welding with a zero distance welds nothing.",
				"Set Epsilon above zero, e.g. 0.001" );
			return;
		}
		foreach ( var body in bodies )
		{
			var diag = body.Mesh.BoundsDiagonal;
			if ( Epsilon.Clamped > diag * 0.1f )
			{
				Fail( "Cleanup epsilon would collapse the part",
					$"Epsilon is {Epsilon.Clamped:0.###} and the part's diagonal is {diag:0.###}, so welding would merge real features.",
					$"Set Epsilon below {diag * 0.1f:0.###}" );
				return;
			}
		}
		if ( Mode.Value == "Check" )
		{
			var lines = new List<string>();
			foreach ( var body in bodies )
			{
				var (open, non) = HardSurfacePaths.EdgeCounts( body.Mesh );
				var degen = body.Mesh.Faces.Count( f => f.Count < 3 || body.Mesh.FaceArea( f ) < 1e-12f );
				var near = CountNearMiss( body.Mesh, Epsilon.Clamped );
				lines.Add( $"{body.Name}: {open} open, {non} non-manifold, {degen} degenerate faces, {near} near-miss vertex pairs" );
			}
			// Coplanar face pairs between distinct bodies in the selection.
			var coplanar = 0;
			for ( var a = 0; a < bodies.Count; a++ )
				for ( var b = a + 1; b < bodies.Count; b++ )
					coplanar += CountCoplanarPairs( bodies[a].Mesh, bodies[b].Mesh, Epsilon.Clamped );
			if ( coplanar > 0 )
				lines.Add( $"{coplanar} coplanar face pair(s) between bodies — nudge a cutter by 0.01 or expect slivers" );
			Warn( "Boolean check: " + string.Join( "; ", lines ),
				"Check alters nothing; it only names what a boolean would trip over.",
				"Weld near-miss vertices with Cleanup", "Nudge cutters off exact alignment" );
			return;
		}
		foreach ( var body in bodies )
		{
			var mesh = body.Mesh;
			var (openBefore, nonBefore) = HardSurfacePaths.EdgeCounts( mesh );
			var (vertsBefore, facesBefore) = (mesh.VertexCount, mesh.FaceCount);
			var welded = Weld( mesh, Epsilon.Clamped );
			body.Mesh = welded;
			var (openAfter, nonAfter) = HardSurfacePaths.EdgeCounts( welded );
			Warn( $"Cleanup on {body.Name}: {vertsBefore} verts / {facesBefore} faces / {openBefore} open / {nonBefore} non-manifold"
				+ $" -> {welded.VertexCount} verts / {welded.FaceCount} faces / {openAfter} open / {nonAfter} non-manifold",
				"Vertices within epsilon were merged and degenerate faces dropped; nothing else was changed.",
				"If open edges remain, the mesh was open before cleanup, not broken by it" );
		}
	}

	static int CountNearMiss( PolyMesh mesh, float eps )
	{
		var grid = new Dictionary<(int, int, int), List<int>>();
		var count = 0;
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var key = ((int)MathF.Floor( p.x / eps ), (int)MathF.Floor( p.y / eps ), (int)MathF.Floor( p.z / eps ));
			for ( var dx = -1; dx <= 1; dx++ )
				for ( var dy = -1; dy <= 1; dy++ )
					for ( var dz = -1; dz <= 1; dz++ )
						if ( grid.TryGetValue( (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list ) )
							foreach ( var j in list )
								if ( (mesh.Positions[j] - p).Length > 1e-9f && (mesh.Positions[j] - p).Length <= eps )
									count++;
			if ( !grid.TryGetValue( key, out var own ) ) grid[key] = own = new List<int>();
			own.Add( i );
		}
		return count;
	}

	static int CountCoplanarPairs( PolyMesh a, PolyMesh b, float eps )
	{
		var count = 0;
		foreach ( var fa in a.Faces )
		{
			var na = a.FaceNormal( fa );
			if ( na.LengthSquared < 1e-12f ) continue;
			na = na.Normal;
			var ca = a.FaceCentroid( fa );
			foreach ( var fb in b.Faces )
			{
				var nb = b.FaceNormal( fb );
				if ( nb.LengthSquared < 1e-12f ) continue;
				nb = nb.Normal;
				if ( Math.Abs( Math.Abs( Vec3.Dot( na, nb ) ) - 1f ) > 0.01f ) continue;
				var cb = b.FaceCentroid( fb );
				if ( Math.Abs( Vec3.Dot( cb - ca, na ) ) > eps ) continue;
				// Centroids close enough that the faces plausibly overlap, not just share a plane.
				var ra = MathF.Sqrt( a.FaceArea( fa ) ) + MathF.Sqrt( b.FaceArea( fb ) );
				if ( (cb - ca).Length <= ra ) count++;
			}
		}
		return count;
	}

	public static PolyMesh Weld( PolyMesh mesh, float eps )
	{
		var grid = new Dictionary<(int, int, int), List<int>>();
		var remap = new int[mesh.Positions.Count];
		var kept = new List<Vec3>();
		var keptSkin = mesh.IsRigged ? new List<BoneWeight[]>() : null;
		var keptUv2 = mesh.UV2 is not null && mesh.UV2.Count == mesh.Positions.Count ? new List<Vec2>() : null;
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var key = ((int)MathF.Floor( p.x / eps ), (int)MathF.Floor( p.y / eps ), (int)MathF.Floor( p.z / eps ));
			var found = -1;
			for ( var dx = -1; dx <= 1 && found < 0; dx++ )
				for ( var dy = -1; dy <= 1 && found < 0; dy++ )
					for ( var dz = -1; dz <= 1 && found < 0; dz++ )
						if ( grid.TryGetValue( (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var list ) )
							foreach ( var j in list )
								if ( (mesh.Positions[j] - p).Length <= eps ) { found = j; break; }
			if ( found >= 0 ) { remap[i] = remap[found]; continue; }
			remap[i] = kept.Count;
			kept.Add( p );
			if ( keptSkin is not null ) keptSkin.Add( mesh.Skin[i] );
			if ( keptUv2 is not null ) keptUv2.Add( mesh.UV2[i] );
			if ( !grid.TryGetValue( key, out var own ) ) grid[key] = own = new List<int>();
			own.Add( i );
		}
		var result = new PolyMesh { Positions = kept };
		if ( keptSkin is not null ) result.Skin = new SkinWeights { Vertices = keptSkin };
		if ( keptUv2 is not null ) result.UV2 = keptUv2;
		foreach ( var f in mesh.Faces )
		{
			var idx = f.Indices.Select( v => remap[v] ).Distinct().ToArray();
			if ( idx.Length < 3 ) continue;
			var nf = new Face( idx, idx.Length == f.Count ? f.UVs : null, f.Material );
			if ( result.FaceArea( nf ) < 1e-12f ) continue;
			result.Faces.Add( nf );
		}
		// Compact out vertices no kept face references (booleans leave
		// loose vertices behind; without this pass Cleanup reports the
		// same vertex count it started with).
		var used = new HashSet<int>();
		foreach ( var f in result.Faces ) foreach ( var v in f.Indices ) used.Add( v );
		var remap2 = new int[kept.Count];
		var finalPos = new List<Vec3>();
		var finalSkin = keptSkin is null ? null : new List<BoneWeight[]>();
		var finalUv2 = keptUv2 is null ? null : new List<Vec2>();
		for ( var i = 0; i < kept.Count; i++ )
		{
			if ( !used.Contains( i ) ) { remap2[i] = -1; continue; }
			remap2[i] = finalPos.Count;
			finalPos.Add( kept[i] );
			if ( finalSkin is not null ) finalSkin.Add( keptSkin![i] );
			if ( finalUv2 is not null ) finalUv2.Add( keptUv2![i] );
		}
		foreach ( var f in result.Faces )
			for ( var j = 0; j < f.Indices.Length; j++ ) f.Indices[j] = remap2[f.Indices[j]];
		result.Positions = finalPos;
		if ( finalSkin is not null ) result.Skin = new SkinWeights { Vertices = finalSkin };
		if ( finalUv2 is not null ) result.UV2 = finalUv2;
		// FaceUV2, when present, maps corners to UV2 indices: remap through
		// both vertex maps (original -> kept -> compacted).
		if ( mesh.FaceUV2 is not null && mesh.FaceUV2.Length == mesh.Faces.Count )
		{
			var keptFaces = new List<int[]>();
			var fi = 0;
			foreach ( var f in mesh.Faces )
			{
				var idx = f.Indices.Select( v => remap[v] ).Distinct().ToArray();
				if ( idx.Length >= 3 && mesh.FaceUV2[fi] is not null )
					keptFaces.Add( mesh.FaceUV2[fi].Select( u => remap2[remap[u]] ).ToArray() );
				fi++;
			}
			if ( keptFaces.Count == result.Faces.Count ) result.FaceUV2 = keptFaces.ToArray();
		}
		return result;
	}
}

/// <summary>Geometric edge wear (research rank 6). A seeded-by-score subset of
/// convex edges — sharpest first, biased toward edges facing Wear direction
/// (up/outward: the edges hands and holsters actually touch) — is filleted at
/// Wear radius, and the new blend faces are re-slotted to Wear material.
/// Blend faces are identified geometrically, not by index: a result face whose
/// normal matches no original face normal and whose centroid sits near a worn
/// edge is a blend face. Wear must be subtle: Amount defaults to 0.25, and
/// uniform wear on every edge would read as a rendering bug, not age.
/// </summary>
public sealed class EdgeWearFeature : Feature
{
	public override string TypeName => "EdgeWear";
	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Radius = new( "Wear radius", 0.05f, 0.0001f, unit: "u" );
	public readonly FloatParam Amount = new( "Amount", 0.25f, 0f, 1f );
	public readonly Vec3Param Direction = new( "Wear direction", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam AngleThreshold = new( "Angle threshold", 15f, 0f, 180f, unit: "deg" );
	public readonly IntParam WearSlot = new( "Wear material slot", 1, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Bodies, Radius, Amount, Direction, AngleThreshold, WearSlot };

	protected override void Execute( FeatureContext ctx )
	{
		var dir = Direction.Value.LengthSquared > 1e-12f ? Direction.Value.Normal : new Vec3( 0, 0, 1 );
		foreach ( var body in RequireBodies( ctx, Bodies ) )
		{
			var mesh = body.Mesh;
			var edgeFaces = mesh.BuildEdgeFaces();
			var normals = mesh.Faces.Select( mesh.FaceNormal ).ToArray();
			var scored = new List<(EdgeKey Key, float Score)>();
			foreach ( var (key, faces) in edgeFaces )
			{
				if ( faces.Count != 2 ) continue;
				var nA = normals[faces[0]]; var nB = normals[faces[1]];
				if ( nA.LengthSquared < 1e-12f || nB.LengthSquared < 1e-12f ) continue;
				nA = nA.Normal; nB = nB.Normal;
				var along = (mesh.Positions[key.B] - mesh.Positions[key.A]).Normal;
				if ( Vec3.Dot( Vec3.Cross( nA, nB ), along ) <= 0f ) continue; // concave: not wear
				var deg = MathF.Acos( Math.Clamp( Vec3.Dot( nA, nB ), -1f, 1f ) ) * 180f / MathF.PI;
				if ( deg < AngleThreshold.Clamped ) continue;
				var outward = (nA + nB).Normal;
				var bias = 0.5f + 0.5f * Math.Max( 0f, Vec3.Dot( outward, dir ) );
				scored.Add( (key, deg * bias) );
			}
			if ( scored.Count == 0 )
			{
				Fail( "No convex edge is sharp enough to wear",
					$"No convex edge opens at {AngleThreshold.Clamped:0.#}° or more.",
					"Lower Angle threshold", "Wear a sharper part" );
				return;
			}
			scored.Sort( (a, b) => b.Score.CompareTo( a.Score ) );
			var take = Math.Max( 1, (int)Math.Ceiling( Amount.Clamped * scored.Count ) );
			var keys = scored.Take( take ).Select( s => s.Key ).ToList();
			var originalNormals = normals.Where( n => n.LengthSquared > 1e-12f ).Select( n => n.Normal ).ToList();
			var wornSegments = keys.Select( k => (mesh.Positions[k.A], mesh.Positions[k.B]) ).ToList();
			var report = EdgeBlend.FilletReport( mesh, Radius.Clamped, 3, keys );
			if ( report.Failure is not null )
				throw new FeatureException( report.Failure );
			var worn = report.Mesh;
			var reslotted = 0;
			foreach ( var f in worn.Faces )
			{
				var fn = worn.FaceNormal( f );
				if ( fn.LengthSquared < 1e-12f ) continue;
				fn = fn.Normal;
				if ( originalNormals.Any( on => Vec3.Dot( fn, on ) > 0.996f ) ) continue; // original face, not blend
				var c = worn.FaceCentroid( f );
				var near = false;
				foreach ( var (a, b) in wornSegments )
				{
					var ab = b - a;
					var t = ab.LengthSquared < 1e-12f ? 0f : Math.Clamp( Vec3.Dot( c - a, ab ) / ab.LengthSquared, 0f, 1f );
					if ( (a + ab * t - c).Length <= Radius.Clamped * 2f ) { near = true; break; }
				}
				if ( near ) { f.Material = WearSlot.Clamped; reslotted++; }
			}
			body.Mesh = worn;
			Warn( $"Edge wear on {body.Name}: {keys.Count} of {scored.Count} convex edges worn at radius {Radius.Clamped:0.###}, {reslotted} blend faces re-slotted to material {WearSlot.Clamped}",
				"Wear is sharpest-first and biased toward edges facing the wear direction, not uniform.",
				"Raise Amount for more wear, lower it if the part reads as damaged rather than used" );
		}
	}
}
