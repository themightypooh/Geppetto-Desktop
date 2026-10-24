using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Local face-slot wear, or real cuts through both walls with thread geometry.</summary>
public sealed class WearFeature : GarmentDetailFeature
{
	public override string TypeName => "Wear";
	public readonly ChoiceParam Style = new( "Style", new[] { "Abrasion", "Dirt", "Rip", "Hole" } );
	public readonly ChoiceParam Area = new( "Area", new[] { "Body part", "All openings", "Hem (lowest)", "Collar (highest)", "Cuffs (the rest)" } );
	public readonly FloatParam Width = new( "Width", 3f, 0.5f, 12f, "in" );
	public readonly FloatParam Height = new( "Height", 2f, 0.2f, 12f, "in" );
	public readonly FloatParam Amount = new( "Amount", 0.8f, 0f, 1f );
	public readonly IntParam Seed = new( "Seed", 17, 0, 65535 );
	public readonly BoolParam Fray = new( "Loose threads", true );
	/// <summary>Hard-surface rust, §4 crane/buoy: fraction of faces re-slotted to rust. 0 = off, garment path only.</summary>
	public readonly FloatParam Rust = new( "Rust", 0f, 0f, 1f );

	/// <summary>Hard-surface dents: fraction of the body's size pitted inward. 0 = off.</summary>
	public readonly FloatParam Dents = new( "Dents", 0f, 0f, 1f );

	/// <summary>Waterline height for the algae band, in model units. Only read when Algae band &gt; 0.</summary>
	public readonly FloatParam Waterline = new( "Waterline", 0f, -100000f, 100000f, "u" );

	/// <summary>Algae coverage inside the waterline band, 0..1. 0 = off.</summary>
	public readonly FloatParam Algae = new( "Algae", 0f, 0f, 1f );

	/// <summary>Half-width of the algae band around Waterline, in model units. 0 = off.</summary>
	public readonly FloatParam AlgaeBand = new( "Algae band", 0f, 0f, 1000f, "u" );
	public override IReadOnlyList<IParam> Parameters => Placement.Concat( new IParam[] { Style, Area, Width, Height, Amount, Fray, Seed, Rust, Dents, Waterline, Algae, AlgaeBand } ).ToArray();
	public int ChangedFaces { get; private set; }
	public int RustFaces { get; private set; }
	public int AlgaeFaces { get; private set; }
	public int DentedVertices { get; private set; }
	protected override void Execute( FeatureContext ctx )
	{
		// Hard-surface rust/wear (§4): when any rust dial is up, the garment path
		// below must not run — it needs a rig and a garment, and a buoy has neither.
		// With every rust dial at its 0 default this branch never fires, so existing
		// garment behaviour is unchanged.
		if ( Rust.Clamped > 0f || Dents.Clamped > 0f || AlgaeBand.Clamped > 0f )
		{
			RustPath( ctx );
			return;
		}

		var targets = Targets( ctx );
		ChangedFaces = 0;
		var map = new BodyRegions.Map( ctx.Rig );
		var rng = new Random( Seed.Clamped );
		var variants = new Dictionary<int, int>();
		int Variant( int source )
		{
			if ( variants.TryGetValue( source, out var existing ) ) return existing;
			var slot = NextSlot( ctx );
			ctx.ShellSlots.Add( slot );
			Materials.Add( new DetailMaterial { Kind = Style.Index == 1 ? "dirt" : "worn", Slot = slot,
				SourceSlot = source, Key = Id + "_wear_" + source } );
			return variants[source] = slot;
		}
		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			var sheet = MeshSolidify.OuterSheet( mesh, out var offset );
			var thick = sheet is not null;
			sheet ??= mesh;
			var surface = new DetailSurface( sheet );
			var frames = DetailSpots.Sides( Side.Index ).Select( s => DetailSpots.Find( surface, map, Spot.Value, s, Across.Clamped, Up.Clamped ) )
				.Where( f => f.HasValue ).Select( f => f.Value.Turned( Angle.Clamped ) ).ToList();
			var edges = Area.Index > 0 ? DetailEdges.Openings( mesh, Area.Index - 1 ).SelectMany( e => e.Points ).ToList() : new List<Vec3>();
			var picked = new HashSet<int>();
			for ( var fi = 0; fi < sheet.FaceCount; fi++ )
			{
				var face = sheet.Faces[fi];
				if ( ctx.ShellSlots.Contains( face.Material ) ) continue;
				var p = sheet.FaceCentroid( face );
				var strength = 0f;
				if ( Area.Index > 0 && edges.Count > 0 )
					strength = 1f - edges.Min( e => (e - p).Length ) / Width.Clamped;
				else if ( Area.Index == 0 )
					foreach ( var f in frames )
					{
						var d = p - f.Point;
						if ( MathF.Abs( Vec3.Dot( d, f.Normal ) ) > 2f || Vec3.Dot( sheet.FaceNormal( face ), f.Normal ) < 0f ) continue;
						var x = Vec3.Dot( d, f.Right ) / (Width.Clamped * 0.5f);
						var y = Vec3.Dot( d, f.Up ) / ((Style.Index == 2 ? MathF.Min( Height.Clamped, 0.6f ) : Height.Clamped) * 0.5f);
						strength = MathF.Max( strength, 1f - MathF.Sqrt( x * x + y * y ) );
					}
				if ( strength <= 0f || Amount.Clamped == 0f ) continue;
				if ( Style.Index < 2 && rng.NextDouble() > Amount.Clamped * MathF.Min( 1f, strength * 3f ) ) continue;
				if ( Style.Index >= 2 && rng.NextDouble() > Amount.Clamped ) continue;
				picked.Add( fi );
			}
			if ( picked.Count == 0 ) continue;
			ChangedFaces += picked.Count;
			if ( Style.Index < 2 )
			{
				foreach ( var fi in picked )
				{
					var slot = Variant( sheet.Faces[fi].Material );
					mesh.Faces[thick ? fi * 2 : fi].Material = slot;
				}
				continue;
			}
			// Never discard shells a later feature already grew. Cutting should precede Fur.
			if ( thick && mesh.VertexCount != offset * 2 )
				Fail( "Cut the cloth before growing fur", "This garment already has extra shell geometry.", "Move Wear above Fur in the feature tree" );
			if ( picked.Count == sheet.FaceCount )
				Fail( "The cut would remove the whole garment", "The selected area covers every face.", "Reduce Width or Height" );
			var threads = new PolyMesh();
			if ( Fray.Value )
			{
				var edgeFaces = sheet.BuildEdgeFaces();
				foreach ( var fi in picked )
				{
					var face = sheet.Faces[fi];
					for ( var k = 0; k < face.Count; k++ )
					{
						var a = face.Indices[k]; var b = face.Indices[(k + 1) % face.Count];
						if ( !edgeFaces[new EdgeKey( a, b )].Any( other => !picked.Contains( other ) ) ) continue;
						var slot = Variant( face.Material );
						for ( var t = 0; t < 3; t++ )
						{
							var p = Vec3.Lerp( sheet.Positions[a], sheet.Positions[b], (t + 0.5f) / 3f );
							var end = p + (sheet.FaceCentroid( face ) - p).Normal * (0.12f + (float)rng.NextDouble() * 0.35f);
							DetailShapes.Tube( threads, new[] { p, end + new Vec3( 0, 0, -0.1f ) }, 0.012f, 3, false, slot );
						}
					}
				}
			}
			// Rebuild the cut sheet and its rim, compacting orphaned vertices so OuterSheet remains
			// readable by the next Trim/Fur/Clothing check. Reuse the original inner wall positions.
			var cut = new PolyMesh();
			var remap = new Dictionary<int, int>();
			foreach ( var fi in Enumerable.Range( 0, sheet.FaceCount ).Where( i => !picked.Contains( i ) ) )
			{
				var f = sheet.Faces[fi];
				cut.AddFace( f.Indices.Select( v => remap.TryGetValue( v, out var id ) ? id : remap[v] = cut.AddVertex( thick ? mesh.Positions[v] : sheet.Positions[v] ) ).ToArray(),
					(Vec2[])f.UVs.Clone(), f.Material );
			}
			if ( sheet.IsRigged )
			{
				cut.Skin = new SkinWeights();
				foreach ( var v in remap.OrderBy( kv => kv.Value ).Select( kv => kv.Key ) ) cut.Skin.Vertices.Add( (BoneWeight[])sheet.Skin[v].Clone() );
			}
			var thickness = thick ? Enumerable.Range( 0, offset ).Average( v => (mesh.Positions[v + offset] - mesh.Positions[v]).Length ) : 0f;
			body.Mesh = thick ? MeshSolidify.Solidify( cut, thickness, smooth: true ) : cut;
			if ( threads.FaceCount > 0 )
			{
				if ( mesh.IsRigged ) SkinBinder.TransferFrom( threads, mesh );
				ctx.Bodies.Add( new Body( ctx.NewBodyId(), $"Fray on {body.Name}", threads ) { IsGarment = true, IsGarmentDetail = true } );
			}
		}
		if ( ChangedFaces == 0 && Amount.Clamped > 0f )
			Fail( "No cloth faces in that area", "The garment may not reach that place, or its faces are larger than the cut.", "Choose a covered place, enlarge the area, or subdivide the garment before Wear" );
	}

	/// <summary>
	/// Hard-surface rust, dents and waterline algae on any body — no rig, no garment
	/// needed. Rust and algae are face-slot reassignments (the same mechanism the
	/// garment path uses for worn/dirt slots); dents are real inward displacement
	/// along per-vertex normals, so they read in the viewport even where the viewer
	/// renders material slots flat. A face slot is only a number until a material
	/// definition exists for it, so the rust/algae slots are also registered in
	/// <see cref="GarmentDetailFeature.Materials"/> like every other detail slot.
	/// </summary>
	void RustPath( FeatureContext ctx )
	{
		ChangedFaces = 0;
		RustFaces = 0;
		AlgaeFaces = 0;
		DentedVertices = 0;
		Materials.Clear();

		var targets = RequireBodies( ctx, Bodies );

		if ( targets.Count == 0 )
			Fail( "No body to rust.", "The selection names no body in this studio.", "Pick a body first" );

		var maxSlot = ctx.Bodies.SelectMany( b => b.Mesh.Faces ).Select( f => f.Material )
			.Concat( ctx.ShellSlots ).DefaultIfEmpty( 0 ).Max();
		var rustSlot = maxSlot + 1;
		var algaeSlot = maxSlot + 2;
		var rustUsed = false;
		var algaeUsed = false;

		foreach ( var body in targets )
		{
			var mesh = body.Mesh;

			if ( mesh is null || mesh.VertexCount == 0 )
				continue;

			// Per-vertex normals for the dents: face normals averaged over the
			// faces that touch the vertex, so a dent sinks into the surface
			// instead of sliding sideways along it.
			var normals = new Vec3[mesh.VertexCount];

			foreach ( var face in mesh.Faces )
			{
				var n = mesh.FaceNormal( face );

				foreach ( var vi in face.Indices )
					normals[vi] += n;
			}

			for ( var vi = 0; vi < normals.Length; vi++ )
				normals[vi] = normals[vi].LengthSquared < 1e-12f ? new Vec3( 0, 0, 1 ) : normals[vi].Normal;

			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );

			foreach ( var p in mesh.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}

			var diagonal = (max - min).Length;
			var dentDepth = Dents.Clamped * diagonal * 0.03f;

			if ( Dents.Clamped > 0f && dentDepth > 0f )
			{
				for ( var vi = 0; vi < mesh.VertexCount; vi++ )
				{
					var n = Hash01( mesh.Positions[vi] * 0.9f + new Vec3( Seed.Clamped * 0.13f, 0f, 0f ) );

					if ( n <= 0.45f )
						continue;

					mesh.Positions[vi] -= normals[vi] * ((n - 0.45f) / 0.55f) * dentDepth;
					DentedVertices++;
				}
			}

			for ( var fi = 0; fi < mesh.FaceCount; fi++ )
			{
				var face = mesh.Faces[fi];
				var c = mesh.FaceCentroid( face );
				var inBand = AlgaeBand.Clamped > 0f && MathF.Abs( c.z - Waterline.Clamped ) <= AlgaeBand.Clamped;

				if ( inBand && Hash01( c * 1.7f + new Vec3( Seed.Clamped * 0.29f, 0f, 0f ) ) < Algae.Clamped )
				{
					face.Material = algaeSlot;
					AlgaeFaces++;
					algaeUsed = true;
					continue;
				}

				if ( Rust.Clamped > 0f
					&& Hash01( c * 1.7f + new Vec3( Seed.Clamped * 0.13f, 0f, 0f ) )
						< Rust.Clamped * (0.35f + 0.65f * Hash01( c + new Vec3( Seed.Clamped * 0.07f, 0f, 0f ) )) )
				{
					face.Material = rustSlot;
					RustFaces++;
					rustUsed = true;
				}
			}
		}

		if ( rustUsed )
		{
			Materials.Add( new DetailMaterial { Kind = "rust", Slot = rustSlot, SourceSlot = 0, Key = Id + "_rust" } );
			ctx.ShellSlots.Add( rustSlot );
		}

		if ( algaeUsed )
		{
			Materials.Add( new DetailMaterial { Kind = "algae", Slot = algaeSlot, SourceSlot = 0, Key = Id + "_algae" } );
			ctx.ShellSlots.Add( algaeSlot );
		}

		ChangedFaces = RustFaces + AlgaeFaces;

		if ( RustFaces == 0 && AlgaeFaces == 0 && DentedVertices == 0 && (Rust.Clamped > 0f || AlgaeBand.Clamped > 0f) )
			Fail( "Rust changed nothing.", "No face rolled under the rust or algae thresholds.",
				"Raise Rust or Algae", "Check the Waterline band covers the body" );
	}

	/// <summary>Deterministic 0..1 noise from a point, so a rebuild rusts the same faces.</summary>
	static float Hash01( Vec3 p )
	{
		var h = MathF.Sin( p.x * 12.9898f + p.y * 78.233f + p.z * 37.719f ) * 43758.5453f;
		return h - MathF.Floor( h );
	}
}
