using System;
using System.Collections.Generic;
using System.Linq;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// Multi-part photorealistic eye, tearline and eyelash assembly (spec
/// docs/CHARACTER-HAIR-FUR-MASTER-SPEC.md section 4.2; board task
/// multipart-eye-assembly, verb `add Eyes`).
///
/// What this adds over the existing <see cref="EyeFeature"/> (FaceFeatures.cs),
/// which it deliberately does NOT duplicate: EyeFeature promotes
/// <see cref="EyeAsset"/> to a feature — a sclera with the front cap cut out,
/// the asset's concave iris bowl and same-radius cornea cap, a pupil disc and
/// a catchlight bead. EyeMaker builds the full assembly the spec asks for:
///
///  1. A physical corneal bulge: the anterior cap is a SMALLER-radius dome
///     (R_cornea = 0.65 * R, spec 0.31 in on a 0.48 in eye) whose pole sits
///     ~0.11 * R proud of the sclera sphere, instead of EyeAsset's cap of the
///     same sphere. The dome base circle matches the sclera opening
///     (x = 0.86 * R, radius 0.51 * R) so the two meet at the limbus.
///  2. A procedural iris texture baker (<see cref="BakeIrisMap"/>) with a
///     limbal ring, 256 radial stroma fibres, Fuchs crypts, a collarette ring
///     and central heterochromia. EyeAsset's IrisTexture has fibres and a
///     limbal darkening only; the crypts, collarette and heterochromia are new.
///  3. pupil_dilate / pupil_constrict shape keys on the iris (and pupil disc)
///     that radially scale the inner rings while the outer limbus stays
///     locked (delta 0 at the rim ring).
///  4. Lacrimal caruncle + a 0.03 in tearline meniscus ribbon at the lower lid.
///  5. Curved upper (28) / lower (16) eyelash card strips carrying their own
///     blink_L / blink_R morphs, so the lashes move 1:1 with the blink and
///     ride OVER the corneal bulge instead of clipping through it
///     (<see cref="RideOverCornea"/>).
///
/// Eye-local space is the FaceFeatures convention for the whole file:
/// forward is +X, up is +Z, lateral is Y, left is +Y. A finished eye is
/// rotated so +X faces <see cref="EyeOptions.Forward"/> and moved to its
/// centre. Geometry is in inches, Z-up in document space, like the rest of
/// the kernel.
///
/// The concave iris dish (front at 0.78 * R, dish 0.12 * R) and the pupil
/// boundary shapes mirror <see cref="EyeAsset"/> so the two builders stay
/// consistent; the ring counts differ (5 iris rings here, 3 there) because
/// the pupil morphs need inner rings to scale.
///
/// Material notes (honest limits): SlotLook has Ior, Clearcoat,
/// ClearcoatRoughness, Subsurface and SubsurfaceColor fields, which the
/// EyesFeature wrapper fills from <see cref="CorneaMaterial"/> and
/// <see cref="ScleraMaterial"/>; it has NO scalar Transmission field (only a
/// TransmissionMap), so the spec's Transmission = 0.92 lives on
/// <see cref="CorneaMaterial.Transmission"/> for exporters/renderers to read.
/// There is no `eye_sclera` / `eye_iris` preset in SlotLook yet; the wrapper
/// carries the values in a fallback look, and the report for this task gives
/// the preset lines for the coordinator to add.
/// </summary>
public static class EyeMaker
{
	/// <summary>Spec 4.2: corneal dome radius on a 0.48 in eye is 0.31 in.</summary>
	public const float CorneaRadiusFraction = 0.65f;

	/// <summary>The sclera opening: EyeFeature cuts faces whose centre is further forward than this.</summary>
	public const float ScleraCutX = 0.86f;

	/// <summary>Iris disc plane and dish depth, as fractions of R (EyeAsset's constants).</summary>
	public const float IrisFrontX = 0.78f, IrisDish = 0.12f;

	/// <summary>Upper / lower eyelash clump counts (spec 4.2 item 4).</summary>
	public const int UpperLashClumps = 28, LowerLashClumps = 16;

	/// <summary>Iris annulus rings (pupil boundary .. rim). 5, not EyeAsset's 3, so the pupil morphs have inner rings to scale.</summary>
	public const int IrisRings = 5, IrisSegments = 32;

	/// <summary>Cornea dome rings (pole .. base circle).</summary>
	public const int DomeRings = 8, DomeSegments = 32;

	/// <summary>Blink morph names the lash meshes carry (one per side).</summary>
	public const string BlinkLeftMorph = "blink_L", BlinkRightMorph = "blink_R";

	/// <summary>
	/// The cornea's physical material, spec 4.2 item 1. Transmission has no
	/// SlotLook scalar (only TransmissionMap), so it lives here for export.
	/// </summary>
	public sealed class CorneaMaterial
	{
		public float Transmission = 0.92f;
		public float Ior = 1.376f;
		public float Clearcoat = 1.0f;
		public float Roughness = 0.01f;
	}

	public static readonly CorneaMaterial Cornea = new();

	/// <summary>
	/// The sclera's material, spec 4.2 item 1: strong red subsurface scatter
	/// with faint episcleral capillaries (the capillary tint rides the iris
	/// baker's limbal falloff; the AO lid-shadow gradient is a renderer job).
	/// </summary>
	public sealed class ScleraMaterial
	{
		public float Subsurface = 0.75f;
		public Vec3 SubsurfaceColor = new( 0.909804f, 0.290196f, 0.235294f ); // #e84a3c
		public Vec3 Color = new( 0.949020f, 0.917647f, 0.850980f ); // #f2ead9
		public float Roughness = 0.55f;
	}

	public static readonly ScleraMaterial Sclera = new();

	/// <summary>Everything `add Eyes` takes. Positions are eye centres in document space.</summary>
	public sealed class EyeOptions
	{
		public string Name = "Eyes";

		/// <summary>
		/// Spec 4.2's example numbers (1.28 lateral, 3.1 up) in the desktop frame
		/// (forward +X, up +Z, lateral Y, left +Y); the example's 64.2 is a
		/// Mobile-frame height, not a desktop forward distance, so forward is 0
		/// here. Callers normally pass landmark positions instead.
		/// </summary>
		public Vec3 Left = new( 0f, 1.28f, 3.1f );
		public Vec3 Right = new( 0f, -1.28f, 3.1f );

		public Vec3 Forward = new( 1f, 0f, 0f );

		/// <summary>Eyeball radius in inches (spec 0.48).</summary>
		public float Radius = 0.48f;

		/// <summary>Iris size as a fraction of Radius; presets mirror EyeAsset (Human 0.50, Cat 0.58, Dragon 0.60, Goat 0.52, Keyhole 0.55).</summary>
		public string Preset = "Human";
		public float IrisFraction => (Preset ?? "").Trim().ToLowerInvariant() switch
		{
			"cat" => 0.58f,
			"dragon" => 0.60f,
			"goat" => 0.52f,
			"keyhole" => 0.55f,
			_ => 0.50f,
		};

		/// <summary>Iris colour, 0..1 RGB. The verb passes a hex string; the feature parses it.</summary>
		public Vec3 IrisColor = new( 0.231373f, 0.431373f, 0.549020f ); // #3b6e8c

		/// <summary>Pupil radius as a fraction of Radius (spec example 0.32). EyeAsset's fixed pupil is 0.22.</summary>
		public float Pupil = 0.22f;

		public PupilShape PupilShape = PupilShape.Round;
		public bool Catchlight = true;

		public int ScleraSlot = 0, IrisSlot = 1, CorneaSlot = 2, PupilSlot = 3, TearlineSlot = 4, LashSlot = 5, CaruncleSlot = 6, CatchlightSlot = 7;

		/// <summary>Iris radius in inches, from Radius and the preset.</summary>
		public float IrisRadius => Radius * IrisFraction;

		/// <summary>Pupil radius in inches, from Radius and Pupil.</summary>
		public float PupilRadius => Radius * Pupil;

		public EyeOptions Clone() => (EyeOptions)MemberwiseClone();
	}

	/// <summary>One finished eye: every part mesh, the baked iris texture, and the radii the morphs were built against.</summary>
	public sealed class EyeAssembly
	{
		public string Name = "";
		public bool IsLeft;
		public PolyMesh Sclera, Iris, Cornea, Pupil, Caruncle, Tearline, LashesUpper, LashesLower, Catchlight;
		public byte[] IrisTexture;
		public int IrisTextureSize = 256;
		public float Radius, IrisRadius, PupilRadius;
		public string BlinkMorph => IsLeft ? BlinkLeftMorph : BlinkRightMorph;

		/// <summary>Every part mesh, in build order, for a caller that just wants the bodies.</summary>
		public IEnumerable<PolyMesh> Parts()
		{
			yield return Sclera;
			yield return Iris;
			yield return Cornea;
			yield return Pupil;
			yield return Caruncle;
			yield return Tearline;
			yield return LashesUpper;
			yield return LashesLower;
			if ( Catchlight is not null ) yield return Catchlight;
		}
	}

	public static void Validate( EyeOptions o )
	{
		if ( o is null ) throw new ArgumentNullException( nameof( o ) );
		if ( !(o.Radius > 0f) ) throw new ArgumentException( "An eye needs a positive radius.", nameof( o ) );
		if ( !(o.IrisRadius > 0f) || o.IrisRadius >= o.Radius ) throw new ArgumentException( $"Iris radius ({o.IrisRadius:0.###}) must be positive and smaller than radius ({o.Radius:0.###}).", nameof( o ) );
		if ( o.Pupil < 0.05f || o.Pupil > 0.45f ) throw new ArgumentOutOfRangeException( nameof( o ), $"Pupil ({o.Pupil:0.###}) is a fraction of radius and must be 0.05..0.45." );
		if ( o.PupilRadius >= o.IrisRadius ) throw new ArgumentException( $"Pupil radius ({o.PupilRadius:0.###}) must be smaller than iris radius ({o.IrisRadius:0.###}).", nameof( o ) );
		if ( o.Forward.LengthSquared < 1e-8f ) throw new ArgumentException( "Forward needs a direction.", nameof( o ) );
	}

	/// <summary>Both eyes of a pair, left (+Y) and right (-Y).</summary>
	public static (EyeAssembly Left, EyeAssembly Right) BuildPair( EyeOptions options )
	{
		Validate( options );
		return (BuildSingle( options, true ), BuildSingle( options, false ));
	}

	/// <summary>One eye, built at the origin in eye-local space, then rotated to face Forward and moved to its centre.</summary>
	public static EyeAssembly BuildSingle( EyeOptions options, bool left )
	{
		Validate( options );
		var o = options;
		var r = o.Radius;
		var centre = left ? o.Left : o.Right;

		var assembly = new EyeAssembly
		{
			Name = (string.IsNullOrWhiteSpace( o.Name ) ? "Eyes" : o.Name) + (left ? "_L" : "_R"),
			IsLeft = left,
			Radius = r,
			IrisRadius = o.IrisRadius,
			PupilRadius = o.PupilRadius,
		};

		assembly.Sclera = BuildSclera( o );
		assembly.Iris = BuildIris( o );
		assembly.Cornea = BuildCorneaDome( o );
		assembly.Pupil = BuildPupilDisc( o );
		AddPupilMorphs( assembly, o );
		assembly.Caruncle = BuildCaruncle( o, left );
		assembly.Tearline = BuildTearline( o );
		assembly.LashesUpper = BuildLashes( o, upper: true );
		assembly.LashesLower = BuildLashes( o, upper: false );
		var blinkMorph = assembly.BlinkMorph;
		AddBlinkMorph( assembly.LashesUpper, o, upper: true, blinkMorph );
		AddBlinkMorph( assembly.LashesLower, o, upper: false, blinkMorph );
		if ( o.Catchlight )
		{
			var bead = Primitives.QuadSphere( 0.08f * r, 4, o.CatchlightSlot );
			assembly.Catchlight = FaceMesh.Moved( bead, new Vec3( 0.95f * r, 0.2f * r, 0.35f * r ) );
		}
		assembly.IrisTexture = BakeIrisMap( o.IrisColor, 256, o.PupilRadius / o.IrisRadius );
		assembly.IrisTextureSize = 256;

		// Place: rotate eye-local +X onto Forward, then move to the eye centre.
		foreach ( var part in assembly.Parts() )
		{
			FaceMesh.Rotated( part, new Vec3( 1, 0, 0 ), o.Forward.Normal );
			FaceMesh.Moved( part, centre );
		}

		return assembly;
	}

	/// <summary>Sclera: a UV-sphere with the anterior cap removed, the same cut EyeFeature makes (faces whose centre is further forward than 0.86 * R go, leaving a ~0.51 * R hole the iris bowl sits in).</summary>
	static PolyMesh BuildSclera( EyeOptions o )
	{
		var r = o.Radius;
		var sphere = Primitives.QuadSphere( r, 8, o.ScleraSlot );
		var kept = new PolyMesh();
		foreach ( var face in sphere.Faces )
		{
			var cx = 0f;
			foreach ( var vi in face.Indices ) cx += sphere.Positions[vi].x;
			cx /= face.Indices.Length;
			if ( cx > ScleraCutX * r ) continue;
			var idx = face.Indices.Select( vi => kept.AddVertex( sphere.Positions[vi] ) ).ToArray();
			kept.AddFace( idx, face.UVs, o.ScleraSlot );
		}
		return kept;
	}

	/// <summary>Concave iris: a 5-ring dished annulus from the pupil boundary out to the rim. The rim sits forward (frontX) and the pupil edge sits back by the dish, so light through the cornea casts the bright caustic on the opposite side. Ring t is stored in the vertex order (ring * IrisSegments + k) for the pupil morphs.</summary>
	static PolyMesh BuildIris( EyeOptions o )
	{
		var r = o.Radius;
		var irisR = o.IrisRadius;
		var pupilR = o.PupilRadius;
		var frontX = IrisFrontX * r;
		var dish = IrisDish * r;
		var mesh = new PolyMesh();
		var rings = new int[IrisRings][];

		for ( var ri = 0; ri < IrisRings; ri++ )
		{
			rings[ri] = new int[IrisSegments];
			var t = ri / (float)(IrisRings - 1);
			for ( var k = 0; k < IrisSegments; k++ )
			{
				var theta = MathF.PI * 2f * k / IrisSegments;
				var boundary = PupilRadiusAt( theta, o.PupilShape, pupilR );
				var rad = boundary + (irisR - boundary) * t;
				var x = frontX - dish * (1f - t);
				rings[ri][k] = mesh.AddVertex( new Vec3( x, rad * MathF.Cos( theta ), rad * MathF.Sin( theta ) ) );
			}
		}

		for ( var ri = 0; ri + 1 < IrisRings; ri++ )
			for ( var k = 0; k < IrisSegments; k++ )
			{
				var k2 = (k + 1) % IrisSegments;
				var a = rings[ri][k]; var b = rings[ri][k2]; var c = rings[ri + 1][k2]; var d = rings[ri + 1][k];
				mesh.AddFace( new[] { a, b, c, d },
					new[] { Uv( mesh.Positions[a], irisR ), Uv( mesh.Positions[b], irisR ), Uv( mesh.Positions[c], irisR ), Uv( mesh.Positions[d], irisR ) },
					o.IrisSlot );
			}

		return mesh;
	}

	/// <summary>
	/// The physical corneal bulge: a spherical cap of a SMALLER sphere (Rc = 0.65 * R) whose
	/// base circle is the sclera opening (radius a at x = 0.86 * R) and whose pole bulges
	/// ~0.11 * R proud of the sclera sphere. The cap's sphere centre sits on the eye axis at
	/// x0 = 0.86 * R - sqrt(Rc^2 - a^2); the pole is at x0 + Rc. EyeAsset's cornea is a cap of
	/// the eyeball sphere itself (pole at x = R, no bulge); this replaces it.
	/// </summary>
	static PolyMesh BuildCorneaDome( EyeOptions o )
	{
		var r = o.Radius;
		var rc = CorneaRadiusFraction * r;
		var a = r * MathF.Sqrt( 1f - ScleraCutX * ScleraCutX ); // opening radius at x = 0.86R
		var x0 = ScleraCutX * r - MathF.Sqrt( rc * rc - a * a );
		var phiMax = MathF.Acos( Math.Clamp( MathF.Sqrt( rc * rc - a * a ) / rc, -1f, 1f ) );
		var mesh = new PolyMesh();
		var pole = mesh.AddVertex( new Vec3( x0 + rc, 0, 0 ) );
		var dome = new int[DomeRings][];

		for ( var ri = 0; ri < DomeRings; ri++ )
		{
			dome[ri] = new int[DomeSegments];
			var phi = phiMax * (ri + 1) / DomeRings;
			for ( var k = 0; k < DomeSegments; k++ )
			{
				var theta = MathF.PI * 2f * k / DomeSegments;
				dome[ri][k] = mesh.AddVertex( new Vec3(
					x0 + rc * MathF.Cos( phi ),
					rc * MathF.Sin( phi ) * MathF.Cos( theta ),
					rc * MathF.Sin( phi ) * MathF.Sin( theta ) ) );
			}
		}

		for ( var k = 0; k < DomeSegments; k++ )
		{
			var k2 = (k + 1) % DomeSegments;
			mesh.AddFace( new[] { pole, dome[0][k2], dome[0][k] },
				new[] { new Vec2( 0.5f, 0.5f ), Uv( mesh.Positions[dome[0][k2]], o.IrisRadius ), Uv( mesh.Positions[dome[0][k]], o.IrisRadius ) },
				o.CorneaSlot );
		}

		for ( var ri = 0; ri + 1 < DomeRings; ri++ )
			for ( var k = 0; k < DomeSegments; k++ )
			{
				var k2 = (k + 1) % DomeSegments;
				var a0 = dome[ri][k]; var b0 = dome[ri][k2]; var c0 = dome[ri + 1][k2]; var d0 = dome[ri + 1][k];
				mesh.AddFace( new[] { a0, b0, c0, d0 },
					new[] { Uv( mesh.Positions[a0], o.IrisRadius ), Uv( mesh.Positions[b0], o.IrisRadius ), Uv( mesh.Positions[c0], o.IrisRadius ), Uv( mesh.Positions[d0], o.IrisRadius ) },
					o.CorneaSlot );
			}

		return mesh;
	}

	/// <summary>Pupil disc: a 32-gon fan at the pupil boundary, on the iris's inner-ring plane, so the pupil reads as a hole even where the iris texture is dark.</summary>
	static PolyMesh BuildPupilDisc( EyeOptions o )
	{
		var r = o.Radius;
		var pupilR = o.PupilRadius;
		var frontX = IrisFrontX * r;
		var dish = IrisDish * r;
		var mesh = new PolyMesh();
		var centre = mesh.AddVertex( new Vec3( frontX - dish, 0, 0 ) );
		var ring = new int[IrisSegments];
		for ( var k = 0; k < IrisSegments; k++ )
		{
			var theta = MathF.PI * 2f * k / IrisSegments;
			var boundary = PupilRadiusAt( theta, o.PupilShape, pupilR );
			ring[k] = mesh.AddVertex( new Vec3( frontX - dish, boundary * MathF.Cos( theta ), boundary * MathF.Sin( theta ) ) );
		}
		for ( var k = 0; k < IrisSegments; k++ )
		{
			var k2 = (k + 1) % IrisSegments;
			mesh.AddFace( new[] { centre, ring[k], ring[k2] },
				new[] { new Vec2( 0.5f, 0.5f ), Uv( mesh.Positions[ring[k]], o.IrisRadius ), Uv( mesh.Positions[ring[k2]], o.IrisRadius ) },
				o.PupilSlot );
		}
		return mesh;
	}

	/// <summary>
	/// pupil_dilate / pupil_constrict on the iris and the pupil disc. Each iris
	/// vertex scales radially about the eye axis by a factor that lerps from the
	/// pupil factor at the inner ring (t = 0) to 1 at the rim (t = 1), so the
	/// outer limbus is locked (delta exactly 0 on the rim ring) while the
	/// pupil aperture visibly widens / narrows. The disc's ring scales by the
	/// full factor, its centre does not move.
	/// </summary>
	static void AddPupilMorphs( EyeAssembly assembly, EyeOptions o )
	{
		const float dilateFactor = 1.6f, constrictFactor = 0.55f;
		// Never let a dilated pupil cross the iris rim.
		var dilate = MathF.Min( dilateFactor, 0.95f * o.IrisRadius / o.PupilRadius );

		Vec3[] ScaledIris( float factor )
		{
			var target = assembly.Iris.Positions.ToArray();
			for ( var vi = 0; vi < target.Length; vi++ )
			{
				var t = (vi / IrisSegments) / (float)(IrisRings - 1);
				var s = MathF.Max( 0.05f, factor + (1f - factor) * t );
				var p = target[vi];
				target[vi] = new Vec3( p.x, p.y * s, p.z * s );
			}
			return target;
		}

		Vec3[] ScaledDisc( float factor )
		{
			var target = assembly.Pupil.Positions.ToArray();
			for ( var vi = 1; vi < target.Length; vi++ )
			{
				var p = target[vi];
				target[vi] = new Vec3( p.x, p.y * factor, p.z * factor );
			}
			return target;
		}

		assembly.Iris.AddMorph( "pupil_dilate", ScaledIris( dilate ) );
		assembly.Iris.AddMorph( "pupil_constrict", ScaledIris( constrictFactor ) );
		assembly.Pupil.AddMorph( "pupil_dilate", ScaledDisc( dilate ) );
		assembly.Pupil.AddMorph( "pupil_constrict", ScaledDisc( constrictFactor ) );
	}

	/// <summary>Lacrimal caruncle: a small pink mound at the inner canthus (medial side: -Y for a left eye, +Y for a right eye), sitting on the eyeball surface just inside the lid line.</summary>
	static PolyMesh BuildCaruncle( EyeOptions o, bool left )
	{
		var r = o.Radius;
		var medial = left ? -1f : 1f;
		var mound = Primitives.QuadSphere( 1f, 4, o.CaruncleSlot );
		MeshTransform.Apply( mound, Xform.Scale( new Vec3( 0.10f * r, 0.14f * r, 0.16f * r ) ) );
		return FaceMesh.Moved( mound, new Vec3( 0.80f * r, medial * 0.52f * r, -0.10f * r ) );
	}

	/// <summary>
	/// Tearline meniscus: a thin 0.03 in ribbon hugging the lower eyelid-to-cornea
	/// intersection, following the lower lid arc across the front of the eye. The
	/// upper edge rides the eyeball surface; the lower edge is 0.03 in further
	/// down the lid, pushed out along the surface normal so it hugs rather than
	/// sinks. Both edges are ridden over the corneal bulge (see
	/// <see cref="RideOverCornea"/>), which is what makes the wet glisten sit
	/// on the cornea instead of behind it.
	/// </summary>
	static PolyMesh BuildTearline( EyeOptions o )
	{
		var r = o.Radius;
		const int n = 17;
		var upper = new List<Vec3>( n );
		var lower = new List<Vec3>( n );
		for ( var i = 0; i < n; i++ )
		{
			var theta = (-70f + i * (140f / (n - 1))) * MathF.PI / 180f;
			var y = 0.50f * r * MathF.Sin( theta );
			var z = -0.38f * r * MathF.Cos( theta );
			var x = MathF.Sqrt( MathF.Max( 0f, r * r - y * y - z * z ) );
			var p = new Vec3( x, y, z );
			upper.Add( RideOverCornea( p, o ) );
			var q = p + p.Normal * 0.005f * r - new Vec3( 0, 0, 0.03f );
			lower.Add( RideOverCornea( q, o ) );
		}
		var mesh = new PolyMesh();
		FaceMesh.Strip( mesh, upper, lower, o.TearlineSlot, closed: false );
		return mesh;
	}

	/// <summary>
	/// Eyelash card strips: tapered curved cards along the lid margin, one clump
	/// per step of the lid arc. The card base sits on the eyeball surface, the
	/// mid and tip curl outward (up for the upper lid, down for the lower) and
	/// every vertex is ridden over the corneal bulge, so the strip hugs the eye
	/// instead of floating off it or sinking into the cornea.
	/// </summary>
	static PolyMesh BuildLashes( EyeOptions o, bool upper )
	{
		var r = o.Radius;
		var clumps = upper ? UpperLashClumps : LowerLashClumps;
		var mesh = new PolyMesh();
		var lidZ = upper ? 0.42f * r : -0.38f * r;
		var len = upper ? 0.20f * r : 0.11f * r;
		var w0 = upper ? 0.09f * r : 0.07f * r;
		var curl = upper ? 0.30f * len : -0.30f * len;
		var outTilt = upper ? 0.55f : -0.55f;

		for ( var i = 0; i < clumps; i++ )
		{
			var theta = (-70f + (i + 0.5f) * (140f / clumps)) * MathF.PI / 180f;
			var y = 0.50f * r * MathF.Sin( theta );
			var z = lidZ * MathF.Cos( theta );
			var x = MathF.Sqrt( MathF.Max( 0f, r * r - y * y - z * z ) );
			var baseC = new Vec3( x, y, z );
			// Lid tangent, by a finite difference along the arc.
			var eps = 0.02f;
			var pA = LidPoint( o, upper, theta - eps );
			var pB = LidPoint( o, upper, theta + eps );
			var tangent = (pB - pA).Normal;
			var normal = baseC.Normal;
			var dir = (normal + new Vec3( 0, 0, outTilt )).Normal;

			var bL = RideOverCornea( baseC - tangent * (w0 / 2f), o );
			var bR = RideOverCornea( baseC + tangent * (w0 / 2f), o );
			var midC = baseC + dir * (len * 0.55f) + new Vec3( 0, 0, curl * 0.55f );
			var mL = RideOverCornea( midC - tangent * (w0 * 0.25f), o );
			var mR = RideOverCornea( midC + tangent * (w0 * 0.25f), o );
			var tipC = baseC + dir * len + new Vec3( 0, 0, curl );
			var tL = RideOverCornea( tipC - tangent * (w0 * 0.06f), o );
			var tR = RideOverCornea( tipC + tangent * (w0 * 0.06f), o );

			FaceMesh.Quad( mesh, bL, bR, mR, mL, o.LashSlot );
			FaceMesh.Quad( mesh, mL, mR, tR, tL, o.LashSlot );
		}

		return mesh;
	}

	static Vec3 LidPoint( EyeOptions o, bool upper, float theta )
	{
		var r = o.Radius;
		var y = 0.50f * r * MathF.Sin( theta );
		var z = (upper ? 0.42f : -0.38f) * r * MathF.Cos( theta );
		var x = MathF.Sqrt( MathF.Max( 0f, r * r - y * y - z * z ) );
		return new Vec3( x, y, z );
	}

	/// <summary>
	/// The blink morph for one lash mesh, named blink_L or blink_R for the side
	/// so the lashes move 1:1 with the eyelid morph of the same name. The target
	/// rotates every lash vertex about the eye centre (the lateral Y axis):
	/// the upper lid swings down (+0.6 rad about +Y) and the lower lid rises
	/// (-0.6 rad), sliding along the eyeball surface; then each vertex is
	/// ridden over the corneal bulge, so a closing lid never clips the cornea.
	/// A full-weight blink is the closed lid; the card is rigid in the morph
	/// (one rotation for the whole clump), which is what "parented to the
	/// blink morph" means for a card strip.
	/// </summary>
	static void AddBlinkMorph( PolyMesh lashes, EyeOptions o, bool upper, string morphName )
	{
		// The morph name is per side, not per lid: both lids of one eye share
		// blink_L / blink_R, and each mesh's target closes its own lid.
		var angle = upper ? 0.6f : -0.6f;
		var rot = Xform.Rotate( new Vec3( 0, 1, 0 ), angle );
		var target = lashes.Positions.Select( p => RideOverCornea( rot.TransformPoint( p ), o ) ).ToArray();
		lashes.AddMorph( morphName, target );
	}

	/// <summary>
	/// Push a point out along its ray from the eye centre when the corneal dome
	/// shell sits outside it on that ray, so lashes and the tearline ride OVER
	/// the bulge instead of clipping through it. Points whose ray misses the
	/// dome cap, or that already sit outside it, are returned unchanged.
	/// </summary>
	static Vec3 RideOverCornea( Vec3 p, EyeOptions o )
	{
		var r = o.Radius;
		var rc = CorneaRadiusFraction * r;
		var a = r * MathF.Sqrt( 1f - ScleraCutX * ScleraCutX );
		var x0 = ScleraCutX * r - MathF.Sqrt( rc * rc - a * a );
		var centre = new Vec3( x0, 0, 0 );
		var dist = p.Length;
		if ( dist < 1e-6f ) return p;
		var d = p / dist;
		// Ray-sphere: |s*d - centre| = rc. Near hit s = -b - sqrt(disc).
		var oc = -centre;
		var b = Vec3.Dot( oc, d );
		var c = Vec3.Dot( oc, oc ) - rc * rc;
		var disc = b * b - c;
		if ( disc <= 0f ) return p;
		var s = -b - MathF.Sqrt( disc );
		if ( s <= 0f || s <= dist ) return p;
		var hit = d * s;
		if ( hit.x < ScleraCutX * r - 1e-4f ) return p;
		if ( MathF.Sqrt( hit.y * hit.y + hit.z * hit.z ) > a + 1e-4f ) return p;
		return d * (s + 0.012f * r);
	}

	/// <summary>Pupil boundary radius at angle theta (0 = +Y, PI/2 = +Z). Mirrors EyeAsset.PupilRadiusAt; the geometry's pupil shape is authoritative, the texture just must not paint colour over the hole.</summary>
	static float PupilRadiusAt( float theta, PupilShape shape, float p )
	{
		var cos = MathF.Cos( theta );
		var sin = MathF.Sin( theta );
		switch ( shape )
		{
			case PupilShape.VerticalSlit:
				return Ellipse( 0.22f * p, p, cos, sin );
			case PupilShape.HorizontalGoat:
				return Ellipse( p, 0.35f * p, cos, sin );
			case PupilShape.Keyhole:
			{
				var toBottom = MathF.Acos( Math.Clamp( -sin, -1f, 1f ) );
				var notch = toBottom < 0.6f ? 1f + 0.6f * (1f - toBottom / 0.6f) : 1f;
				return p * notch;
			}
			default:
				return p;
		}
	}

	static float Ellipse( float a, float b, float cos, float sin )
	{
		var den = MathF.Sqrt( (b * cos) * (b * cos) + (a * sin) * (a * sin) );
		return den < 1e-9f ? a : a * b / den;
	}

	static Vec2 Uv( Vec3 p, float irisR ) => new( 0.5f + p.y / (2f * irisR), 0.5f + p.z / (2f * irisR ) );

	/// <summary>
	/// Procedural iris texture baker: 256x256 RGBA (row-major, row 0 at the
	/// top like every other texture here), mapping the iris disc to the unit
	/// circle. Beyond EyeAsset's IrisTexture (radial fibres + limbal
	/// darkening + black pupil) this adds the spec's collarette ring at
	/// r = 0.42, Fuchs crypts (dark pits in a theta/r cell grid), and central
	/// heterochromia (amber around the pupil blending out to
	/// <paramref name="irisColor"/>). The texture's pupil is a round
	/// approximation — the geometry's pupil shape is authoritative; the
	/// texture just must not paint colour over the hole. Deterministic for a
	/// given seed (same eye renders the same way headless and in s&amp;box).
	/// </summary>
	/// <param name="pupilFraction">Pupil radius as a fraction of iris radius (pupilR / irisR); texels inside it are black.</param>
	public static byte[] BakeIrisMap( Vec3 irisColor, int size = 256, float pupilFraction = 0.44f, int seed = 7 )
	{
		if ( size < 16 ) throw new ArgumentOutOfRangeException( nameof( size ), "An iris map needs at least 16x16 texels." );
		if ( pupilFraction < 0.05f || pupilFraction >= 1f ) throw new ArgumentOutOfRangeException( nameof( pupilFraction ), "Pupil fraction must be 0.05..1." );

		var rgba = new byte[size * size * 4];
		var amber = new Vec3( 0.784314f, 0.564706f, 0.227451f ); // central heterochromia #c8903a

		for ( var y = 0; y < size; y++ )
		{
			for ( var x = 0; x < size; x++ )
			{
				var nx = (x - (size - 1) / 2f) / ((size - 1) / 2f);
				var ny = (y - (size - 1) / 2f) / ((size - 1) / 2f);
				var r = MathF.Sqrt( nx * nx + ny * ny );
				var o = (y * size + x) * 4;

				if ( r > 1f )
				{
					rgba[o + 3] = 0;
					continue;
				}

				if ( r < pupilFraction )
				{
					rgba[o] = rgba[o + 1] = rgba[o + 2] = 0;
					rgba[o + 3] = 255;
					continue;
				}

				var theta = MathF.Atan2( ny, nx );
				var normTheta = (theta + MathF.PI) / (MathF.PI * 2f);

				// Central heterochromia: amber around the pupil, blending out to the iris colour by r = 0.45.
				var colour = irisColor;
				if ( r < 0.45f )
				{
					var t = Math.Clamp( (r - pupilFraction) / MathF.Max( 1e-4f, 0.45f - pupilFraction ), 0f, 1f );
					t = t * t * (3f - 2f * t);
					colour = Vec3.Lerp( amber, irisColor, t );
				}

				// 256 radial stroma fibres: one brightness streak per fibre, banded along r so a fibre reads as a streak, not a wedge.
				var fibre = (int)MathF.Floor( normTheta * 256f );
				var band = (int)MathF.Floor( r * 24f );
				var brightness = 0.72f + 0.45f * Hash( fibre, band, seed );

				// Fuchs crypts: dark pits where a coarse theta/r cell hashes high.
				var sector = (int)MathF.Floor( normTheta * 64f );
				var ring = (int)MathF.Floor( r * 12f );
				var crypt = Hash( sector, ring, seed ^ 0xC7 );
				if ( crypt > 0.93f ) brightness *= 0.4f;
				else if ( crypt > 0.86f ) brightness *= 0.55f;

				// Collarette ring at r = 0.42: a raised bright ring with a darker edge.
				var dColl = MathF.Abs( r - 0.42f );
				if ( dColl < 0.025f ) brightness *= 1f + 0.18f * (1f - dColl / 0.025f);

				// Limbal ring: darkened outer annulus, r in [0.85, 1.0].
				if ( r > 0.85f ) brightness *= 1f - 0.65f * (r - 0.85f) / 0.15f;

				rgba[o] = Byte( colour.x * brightness );
				rgba[o + 1] = Byte( colour.y * brightness );
				rgba[o + 2] = Byte( colour.z * brightness );
				rgba[o + 3] = 255;
			}
		}

		return rgba;
	}

	static byte Byte( float v ) => (byte)Math.Clamp( (int)MathF.Round( v * 255f ), 0, 255 );

	/// <summary>Same deterministic integer hash EyeAsset uses for its iris fibres.</summary>
	static float Hash( int x, int y, int seed )
	{
		unchecked
		{
			var h = (uint)(x * 374761393 + y * 668265263 + seed * 144665);
			h = (h ^ (h >> 13)) * 1274126177u;
			h ^= h >> 16;
			return (h & 0xFFFFFF) / (float)0xFFFFFF;
		}
	}
}

/// <summary>
/// Eyes: the `add Eyes` verb (spec 4.2). A pair of full EyeMaker assemblies
/// (sclera + corneal bulge + concave iris + pupil disc + caruncle +
/// tearline + lash cards + catchlight), one feature in the history so the
/// pair rebuilds together. The feature's name prefixes every body
/// (Eyes_L_sclera, Eyes_R_iris, …).
///
/// `add Eyes name=Eyes left=1.28,3.1,64.2 right=-1.28,3.1,64.2 radius=0.48 iris_color=#3b6e8c pupil=0.32`
/// is the spec's example; positions are eye centres in document space
/// (forward +X, up +Z, lateral Y, left +Y) — the example's numbers are a
/// Mobile-frame illustration, so the defaults here carry its lateral/up
/// values (1.28, 3.1) with forward 0, and callers normally pass landmark
/// positions. `pupil` is the pupil radius as a fraction of the eyeball
/// radius (0.22 = EyeAsset's fixed pupil).
/// </summary>
public sealed class EyesFeature : Feature
{
	public override string TypeName => "Eyes";
	public readonly Vec3Param Left = new( "Left", new Vec3( 0f, 1.28f, 3.1f ) );
	public readonly Vec3Param Right = new( "Right", new Vec3( 0f, -1.28f, 3.1f ) );
	public readonly Vec3Param Forward = new( "Forward", new Vec3( 1f, 0f, 0f ) );
	public readonly FloatParam Radius = new( "Radius", 0.48f, 0.01f, 100f, "u" );
	public readonly ChoiceParam Preset = new( "Preset", new[] { "Human", "Cat", "Goat", "Dragon", "Keyhole" } );
	public readonly ChoiceParam PupilShape = new( "Pupil shape", new[] { "Round", "VerticalSlit", "HorizontalGoat", "Keyhole" } );
	public readonly StringParam IrisColor = new( "Iris color", "#3b6e8c" );
	public readonly FloatParam Pupil = new( "Pupil", 0.22f, 0.05f, 0.45f );
	public readonly BoolParam Catchlight = new( "Catchlight", true );
	public readonly IntParam ScleraSlot = new( "Sclera slot", 0, 0, 63 ) { Slider = false };
	public readonly IntParam IrisSlot = new( "Iris slot", 1, 0, 63 ) { Slider = false };
	public readonly IntParam CorneaSlot = new( "Cornea slot", 2, 0, 63 ) { Slider = false };
	public readonly IntParam PupilSlot = new( "Pupil slot", 3, 0, 63 ) { Slider = false };
	public readonly IntParam TearlineSlot = new( "Tearline slot", 4, 0, 63 ) { Slider = false };
	public readonly IntParam LashSlot = new( "Lash slot", 5, 0, 63 ) { Slider = false };
	public readonly IntParam CaruncleSlot = new( "Caruncle slot", 6, 0, 63 ) { Slider = false };
	public readonly IntParam CatchlightSlot = new( "Catchlight slot", 7, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Left, Right, Forward, Radius, Preset, PupilShape, IrisColor, Pupil, Catchlight, ScleraSlot, IrisSlot, CorneaSlot, PupilSlot, TearlineSlot, LashSlot, CaruncleSlot, CatchlightSlot };

	protected override void Execute( FeatureContext ctx )
	{
		var r = Radius.Clamped;
		if ( !(r > 0f) )
			FailOn( "Radius", "An eye needs a positive radius", $"Radius is {r:0.###}.", "Increase Radius" );

		var pupilShape = PupilShape.Value switch
		{
			"VerticalSlit" => Effigy.Render.PupilShape.VerticalSlit,
			"HorizontalGoat" => Effigy.Render.PupilShape.HorizontalGoat,
			"Keyhole" => Effigy.Render.PupilShape.Keyhole,
			_ => Effigy.Render.PupilShape.Round,
		};

		var options = new EyeMaker.EyeOptions
		{
			Name = string.IsNullOrWhiteSpace( Name ) ? "Eyes" : Name,
			Left = Left.Value,
			Right = Right.Value,
			Forward = Forward.Value,
			Radius = r,
			Preset = Preset.Value,
			IrisColor = FurMaterial.ParseHex( IrisColor.Value, new Vec3( 0.231373f, 0.431373f, 0.549020f ) ),
			Pupil = Pupil.Clamped,
			PupilShape = pupilShape,
			Catchlight = Catchlight.Value,
			ScleraSlot = ScleraSlot.Clamped,
			IrisSlot = IrisSlot.Clamped,
			CorneaSlot = CorneaSlot.Clamped,
			PupilSlot = PupilSlot.Clamped,
			TearlineSlot = TearlineSlot.Clamped,
			LashSlot = LashSlot.Clamped,
			CaruncleSlot = CaruncleSlot.Clamped,
			CatchlightSlot = CatchlightSlot.Clamped,
		};

		EyeMaker.EyeAssembly left = null!, right = null!; // Fail throws; never observed, only satisfies definite assignment.
		try
		{
			(left, right) = EyeMaker.BuildPair( options );
		}
		catch ( ArgumentException e )
		{
			// Fail throws (DoesNotReturn); the feature reports the bad parameters.
			Fail( e.Message, "The eye parameters do not fit together.", "Check Radius, Preset and Pupil" );
		}

		foreach ( var eye in new[] { left, right } )
		{
			var p = options.Name + (eye.IsLeft ? "_L" : "_R");
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_sclera", eye.Sclera ) );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_iris", eye.Iris ) );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_cornea", eye.Cornea ) );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_pupil", eye.Pupil ) );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_caruncle", eye.Caruncle ) );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_tearline", eye.Tearline ) );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_lashes_upper", eye.LashesUpper ) );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_lashes_lower", eye.LashesLower ) );
			if ( eye.Catchlight is not null )
				ctx.Bodies.Add( new Body( ctx.NewBodyId(), p + "_catchlight", eye.Catchlight ) );
		}

		// Slot looks (PartStudio merges these into MaterialLooks; a `material`
		// line for the slot still wins). eye_cornea and wet_mucus are shipped
		// presets; eye_sclera / eye_iris do not exist in SlotLook yet, so those
		// looks carry the values directly (see the task report for the preset
		// lines to add).
		if ( !SlotLook.TryPreset( "eye_cornea", out var cornea ) ) cornea = new SlotLook();
		cornea.Rough = EyeMaker.Cornea.Roughness;
		cornea.Ior = EyeMaker.Cornea.Ior;
		cornea.Clearcoat = EyeMaker.Cornea.Clearcoat;
		cornea.ClearcoatRoughness = EyeMaker.Cornea.Roughness;
		ctx.SlotLooks[options.CorneaSlot] = cornea;
		ctx.SlotLooks[options.TearlineSlot] = cornea.Clone();

		ctx.SlotLooks[options.ScleraSlot] = new SlotLook
		{
			Color = EyeMaker.Sclera.Color,
			Rough = EyeMaker.Sclera.Roughness,
			Subsurface = EyeMaker.Sclera.Subsurface,
			SubsurfaceColor = EyeMaker.Sclera.SubsurfaceColor,
		};
		ctx.SlotLooks[options.IrisSlot] = new SlotLook { Color = options.IrisColor, Rough = 0.35f };
		ctx.SlotLooks[options.PupilSlot] = new SlotLook { Color = new Vec3( 0.04f, 0.04f, 0.05f ), Rough = 0.25f };
		ctx.SlotLooks[options.LashSlot] = new SlotLook { Color = new Vec3( 0.10f, 0.07f, 0.06f ), Rough = 0.6f };
		if ( !SlotLook.TryPreset( "wet_mucus", out var mucus ) ) mucus = new SlotLook();
		mucus.Color = FurMaterial.ParseHex( "#c96a72", new Vec3( 0.79f, 0.42f, 0.45f ) );
		ctx.SlotLooks[options.CaruncleSlot] = mucus;
	}
}
