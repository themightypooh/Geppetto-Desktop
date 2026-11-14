using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Dental, gum, tongue and creature jaw kit (spec
/// docs/CHARACTER-HAIR-FUR-MASTER-SPEC.md section 4.3; board task
/// dental-mouth-kit, verb `add MouthBag`).
///
/// What this adds over the existing <see cref="MouthFeature"/> (FaceFeatures.cs),
/// which it deliberately does NOT duplicate: MouthFeature builds 14
/// box-teeth per arch, a flat gum strip, a single-ellipsoid tongue, a lip
/// ring and a dark cavity. MouthMaker builds the full kit the spec asks for:
///
///  1. Maxillary + mandibular gum arches as parabolic U-shaped alveolar
///     ridges (outer / crest / inner rings, so the gum reads as a raised
///     ridge, not a flat strip) with recessed interdental papillae scallops
///     — one small cone in every gap between adjacent tooth roots.
///  2. Individualized dentition, 16 teeth per arch: central incisors,
///     lateral incisors, canines (with a cusp cone), first/second premolars
///     (two cusps) and first/second/third molars (four cusps), each as a
///     crown in the `tooth_enamel` look plus a separate root mesh in a
///     yellowish dentin look below the gumline. A PolyMesh has no per-vertex
///     gradient field, so the spec's "yellowish root-neck gradient" is the
///     crown-to-root material transition at the gumline — the mechanism the
///     kernel actually has — not a painted gradient.
///  3. Creature sliders: fang_length (elongates the canine cusp),
///     tusk_curve (tilts the lower canines outward), rows (revolving shark
///     tooth rows, each smaller and more reclined than the last) and
///     pharyngeal_jaw (a second, smaller toothed arch deep in the mouth).
///     Presets are additive bases: the effective slider is the preset's
///     base plus the value passed in, so `preset=vampire fang_length=0.1`
///     means vampire fangs 0.1 longer than the preset's.
///  4. An articulated 4-bone tongue (tongue_01 -&gt; tongue_02 -&gt; tongue_03
///     -&gt; tongue_tip) with filiform papillae roughness on its look and a
///     sublingual frenulum membrane under the tip. The tongue mesh carries
///     real SkinWeights bound to the chain (SkinBinder.BindSmooth), and
///     <see cref="CurlTongue"/> articulates the chain; the MouthBagFeature
///     merges the four bones into the studio rig and remaps the weights.
///
/// The arch math reuses MouthFeature's: t runs -65..65 degrees, the arch
/// point is (cos t * depth, sin t * halfWidth) in the X-Y plane, teeth point
/// along Z (upper crowns down, lower crowns up). Frame is the FaceFeatures
/// convention: forward +X, up +Z, lateral Y, left +Y. Geometry in inches.
/// </summary>
public static class MouthMaker
{
	/// <summary>Teeth per arch: 2 central + 2 lateral + 2 canine + 4 premolar + 6 molar, per side 1/1/1/2/3.</summary>
	public const int TeethPerArch = 16;

	/// <summary>Gum-arch sample points (outer / crest / inner rings).</summary>
	public const int ArchSamples = 21;

	public enum MouthPreset
	{
		Human,
		Vampire,
		Orc,
		Carnivore,
		Shark,
		Abyssal,
	}

	enum ToothKind { CentralIncisor, LateralIncisor, Canine, Premolar, Molar }

	/// <summary>Everything `add MouthBag` takes.</summary>
	public sealed class MouthOptions
	{
		public string Name = "Mouth";
		public MouthPreset Preset = MouthPreset.Human;

		/// <summary>Mouth width (lateral), height (opening) and depth (front-back), inches. MouthFeature's defaults.</summary>
		public float Width = 6f, Height = 2f, Depth = 1.5f;

		public Vec3 Position = Vec3.Zero;

		/// <summary>Added to the preset's fang base (vampire +0.35, carnivore +0.15): extra canine cusp length, inches.</summary>
		public float FangLength = 0f;

		/// <summary>Added to the preset's tusk base (orc +0.6): 0..1 outward curve of the lower canines.</summary>
		public float TuskCurve = 0f;

		/// <summary>Tooth rows; the preset raises it (shark -&gt; 3). Each extra row is smaller and more reclined.</summary>
		public int Rows = 1;

		/// <summary>Also on for the abyssal preset: a second toothed arch deep in the mouth.</summary>
		public bool PharyngealJaw = false;

		public int ToothSlot = 0, ToothRootSlot = 1, GumSlot = 2, TongueSlot = 3, CavitySlot = 4, PalateSlot = 5, FrenulumSlot = 6;

		// --- effective sliders: preset base + explicit value, so a preset never silently overrides what was typed ---

		public float EffectiveFangLength => FangLength + Preset switch
		{
			MouthPreset.Vampire => 0.35f,
			MouthPreset.Carnivore => 0.15f,
			_ => 0f,
		};

		public float EffectiveTuskCurve => TuskCurve + (Preset == MouthPreset.Orc ? 0.6f : 0f);

		public int EffectiveRows => Math.Max( Rows, Preset == MouthPreset.Shark ? 3 : 1 );

		public bool EffectivePharyngealJaw => PharyngealJaw || Preset == MouthPreset.Abyssal;

		/// <summary>Carnivore canines are 1.5x; everything else 1x.</summary>
		public float CanineScale => Preset == MouthPreset.Carnivore ? 1.5f : 1f;

		public MouthOptions Clone() => (MouthOptions)MemberwiseClone();
	}

	/// <summary>One finished mouth bag: every part mesh, the tongue rig, and the resolved sliders (so a check can read what the preset actually did).</summary>
	public sealed class MouthBag
	{
		public string Name = "";
		public PolyMesh TeethUpper, TeethLower, GumUpper, GumLower, Tongue, Palate, Cavity, Frenulum;
		public PolyMesh PharyngealJaw;
		public Skeleton TongueSkeleton;
		public int UpperTeeth, LowerTeeth;
		public int TotalTeeth => UpperTeeth + LowerTeeth;
		public float EffectiveFangLength, EffectiveTuskCurve;
		public int EffectiveRows;
		public bool HasPharyngealJaw;

		/// <summary>Every part mesh, in build order, for a caller that just wants the bodies.</summary>
		public IEnumerable<PolyMesh> Parts()
		{
			yield return TeethUpper;
			yield return TeethLower;
			yield return GumUpper;
			yield return GumLower;
			yield return Tongue;
			yield return Palate;
			yield return Cavity;
			yield return Frenulum;
			if ( PharyngealJaw is not null ) yield return PharyngealJaw;
		}
	}

	public static void Validate( MouthOptions o )
	{
		if ( o is null ) throw new ArgumentNullException( nameof( o ) );
		if ( !(o.Width > 0f) || !(o.Height > 0f) || !(o.Depth > 0f) ) throw new ArgumentException( "A mouth needs positive width, height and depth.", nameof( o ) );
		if ( o.Rows < 1 || o.Rows > 3 ) throw new ArgumentOutOfRangeException( nameof( o ), "Rows must be 1..3." );
		if ( o.FangLength < 0f || o.FangLength > 2f ) throw new ArgumentOutOfRangeException( nameof( o ), "Fang length must be 0..2 inches." );
		if ( o.TuskCurve < 0f || o.TuskCurve > 1f ) throw new ArgumentOutOfRangeException( nameof( o ), "Tusk curve must be 0..1." );
	}

	/// <summary>Build the whole mouth bag at <see cref="MouthOptions.Position"/>.</summary>
	public static MouthBag Build( MouthOptions options )
	{
		Validate( options );
		var o = options;
		var pos = o.Position;
		var w = o.Width;
		var h = o.Height;
		var d = o.Depth;
		var archDepth = w * 0.35f;
		var archHalf = w * 0.5f;

		var bag = new MouthBag
		{
			Name = string.IsNullOrWhiteSpace( o.Name ) ? "Mouth" : o.Name,
			EffectiveFangLength = o.EffectiveFangLength,
			EffectiveTuskCurve = o.EffectiveTuskCurve,
			EffectiveRows = o.EffectiveRows,
			HasPharyngealJaw = o.EffectivePharyngealJaw,
		};

		bag.TeethUpper = BuildArchTeeth( o, upper: true, archDepth, archHalf );
		bag.TeethLower = BuildArchTeeth( o, upper: false, archDepth, archHalf );
		bag.UpperTeeth = bag.LowerTeeth = TeethPerArch * o.EffectiveRows;
		bag.GumUpper = BuildGumArch( o, upper: true, archDepth, archHalf );
		bag.GumLower = BuildGumArch( o, upper: false, archDepth, archHalf );
		(bag.Tongue, bag.TongueSkeleton) = BuildTongue( o, archDepth );
		bag.Palate = BuildPalate( o, archDepth, archHalf );
		bag.Cavity = BuildCavity( o );
		bag.Frenulum = BuildFrenulum( o );
		if ( bag.HasPharyngealJaw )
			bag.PharyngealJaw = BuildPharyngealJaw( o, archDepth, archHalf );

		foreach ( var part in bag.Parts() )
			FaceMesh.Moved( part, pos );

		return bag;
	}

	/// <summary>One arch of individualized teeth, with the creature rows. Tooth i sits at arch angle t = -65 + i * (130 / 15) degrees; its kind comes from |t|: central &lt; 9, lateral &lt; 18, canine &lt; 27, premolar &lt; 45, else molar.</summary>
	static PolyMesh BuildArchTeeth( MouthOptions o, bool upper, float archDepth, float archHalf )
	{
		var teeth = new PolyMesh();
		var rows = o.EffectiveRows;

		for ( var row = 0; row < rows; row++ )
		{
			var scale = 1f - 0.14f * row;
			var rowDepth = archDepth * scale;
			var rowHalf = archHalf * scale;
			var recline = row * 0.25f; // each revolving row leans further back
			var backShift = -row * 0.12f * archDepth;

			for ( var i = 0; i < TeethPerArch; i++ )
			{
				var tDeg = -65f + i * (130f / (TeethPerArch - 1));
				var t = tDeg * MathF.PI / 180f;
				var kind = KindFor( MathF.Abs( tDeg ) );
				var at = new Vec3(
					MathF.Cos( t ) * rowDepth + backShift,
					MathF.Sin( t ) * rowHalf,
					(upper ? 0.62f : -0.62f) * o.Height );
				var tooth = BuildTooth( o, kind, upper, scale, recline, at );
				FaceMesh.Append( teeth, tooth );
			}
		}

		return teeth;
	}

	static ToothKind KindFor( float absDeg ) => absDeg switch
	{
		< 9f => ToothKind.CentralIncisor,
		< 18f => ToothKind.LateralIncisor,
		< 27f => ToothKind.Canine,
		< 45f => ToothKind.Premolar,
		_ => ToothKind.Molar,
	};

	/// <summary>
	/// One tooth at <paramref name="at"/> (its gumline point): a crown in the
	/// enamel slot with its cusps, plus a root in the dentin slot below the
	/// gumline. Built at the origin (gumline at z = 0, crown toward the mouth
	/// opening, root into the gum), tilted about the gumline point for tusk
	/// curve / row recline, then moved into place. Upper crowns point down
	/// (-Z), lower crowns point up (+Z).
	/// </summary>
	static PolyMesh BuildTooth( MouthOptions o, ToothKind kind, bool upper, float scale, float recline, Vec3 at )
	{
		var s = o.Width / 6f * scale;
		var sh = o.Height / 2f * scale;
		var tip = upper ? -1f : 1f; // crown-tip direction

		// (crown width, crown depth, crown height, root height) in inches at s = 1, sh = 1.
		var (cw, cd, ch, rh) = kind switch
		{
			ToothKind.CentralIncisor => (0.50f, 0.42f, 0.72f, 0.50f),
			ToothKind.LateralIncisor => (0.40f, 0.40f, 0.62f, 0.44f),
			ToothKind.Canine => (0.44f * o.CanineScale, 0.44f * o.CanineScale, 0.85f * o.CanineScale, 0.60f),
			ToothKind.Premolar => (0.48f, 0.46f, 0.60f, 0.42f),
			_ => (0.58f, 0.52f, 0.55f, 0.38f),
		};

		var mesh = new PolyMesh();

		// Crown: a box whose top sits 0.05 below the gumline, tip toward the opening.
		var crown = Primitives.Box( cd * s, cw * s, ch * sh, o.ToothSlot );
		MeshTransform.Apply( crown, Xform.Translate( new Vec3( 0, 0, tip * (0.05f * sh + ch * sh / 2f) ) ) );
		FaceMesh.Append( mesh, crown );

		// Cusps on the crown tip. A cone's apex is +Z; upper teeth flip it to point down.
		void Cusp( float r, float h, float dx, float dy )
		{
			var cone = Primitives.Cone( r * s, h * sh, 12, o.ToothSlot );
			if ( upper )
				MeshTransform.Apply( cone, Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI ) );
			var tipZ = tip * (0.05f * sh + ch * sh + h * sh / 2f - 0.03f * sh);
			MeshTransform.Apply( cone, Xform.Translate( new Vec3( dx * s, dy * s, tipZ ) ) );
			FaceMesh.Append( mesh, cone );
		}

		switch ( kind )
		{
			case ToothKind.Canine:
				Cusp( 0.16f, 0.30f + o.EffectiveFangLength, 0f, 0f );
				break;
			case ToothKind.Premolar:
				Cusp( 0.11f, 0.16f, 0.12f, 0f );
				Cusp( 0.11f, 0.16f, -0.12f, 0f );
				break;
			case ToothKind.Molar:
				Cusp( 0.10f, 0.12f, 0.14f, 0.13f );
				Cusp( 0.10f, 0.12f, 0.14f, -0.13f );
				Cusp( 0.10f, 0.12f, -0.14f, 0.13f );
				Cusp( 0.10f, 0.12f, -0.14f, -0.13f );
				break;
		}

		// Root: one tapered-by-scale box rising from the gumline into the gum.
		var root = Primitives.Box( cd * 0.8f * s, cw * 0.8f * s, rh * sh, o.ToothRootSlot );
		MeshTransform.Apply( root, Xform.Translate( new Vec3( 0, 0, tip * (0.05f * sh - rh * sh / 2f) ) ) );
		FaceMesh.Append( mesh, root );

		// Tilt about the gumline point, in two different planes: revolving
		// shark rows recline further back per row (about Y, with the sign
		// following the crown-tip direction so both arches lean back, not one
		// forward), and lower canines curve outward with the tusk slider
		// (about X, tip toward the cheek on each side).
		if ( MathF.Abs( recline ) > 1e-4f )
			MeshTransform.Apply( mesh, Xform.Rotate( new Vec3( 0, 1, 0 ), -tip * recline ) );
		if ( !upper && kind == ToothKind.Canine && o.EffectiveTuskCurve > 0f )
			MeshTransform.Apply( mesh, Xform.Rotate( new Vec3( 1, 0, 0 ), -Math.Sign( at.y ) * o.EffectiveTuskCurve * 0.6f ) );

		return FaceMesh.Moved( mesh, at );
	}

	/// <summary>
	/// Gum arch: a parabolic U-shaped alveolar ridge, not MouthFeature's flat
	/// strip — outer / crest / inner rings give it a raised rounded profile
	/// over the tooth roots — with a recessed interdental papilla cone in
	/// every gap between adjacent teeth, pointing toward the crown tips.
	/// </summary>
	static PolyMesh BuildGumArch( MouthOptions o, bool upper, float archDepth, float archHalf )
	{
		var gum = new PolyMesh();
		var gz = (upper ? 0.62f : -0.62f) * o.Height;
		var dip = (upper ? -0.06f : 0.06f) * o.Height; // outer/inner rings sit toward the crowns from the crest
		var outer = new List<Vec3>();
		var crest = new List<Vec3>();
		var inner = new List<Vec3>();

		for ( var i = 0; i < ArchSamples; i++ )
		{
			var t = (-65f + i * (130f / (ArchSamples - 1))) * MathF.PI / 180f;
			outer.Add( new Vec3( MathF.Cos( t ) * archDepth * 1.06f, MathF.Sin( t ) * archHalf * 1.06f, gz + dip ) );
			crest.Add( new Vec3( MathF.Cos( t ) * archDepth, MathF.Sin( t ) * archHalf, gz ) );
			inner.Add( new Vec3( MathF.Cos( t ) * archDepth * 0.82f, MathF.Sin( t ) * archHalf * 0.82f, gz + dip ) );
		}

		FaceMesh.Strip( gum, outer, crest, o.GumSlot, closed: false );
		FaceMesh.Strip( gum, crest, inner, o.GumSlot, closed: false );

		// Papillae: one small cone in each inter-tooth gap, on the crest, apex toward the crown tips.
		for ( var i = 0; i < TeethPerArch - 1; i++ )
		{
			var tGap = (-65f + (i + 0.5f) * (130f / (TeethPerArch - 1))) * MathF.PI / 180f;
			var papilla = Primitives.Cone( 0.05f * o.Width / 6f, 0.10f * o.Height / 2f, 8, o.GumSlot );
			if ( upper )
				MeshTransform.Apply( papilla, Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI ) );
			var tip = upper ? -1f : 1f;
			FaceMesh.Append( gum, FaceMesh.Moved( papilla,
				new Vec3( MathF.Cos( tGap ) * archDepth, MathF.Sin( tGap ) * archHalf, gz + tip * 0.03f * o.Height / 2f ) ) );
		}

		return gum;
	}

	/// <summary>
	/// The articulated tongue: a flattened ellipsoid on the mouth floor, bound
	/// to a 4-bone chain (tongue_01 -&gt; tongue_02 -&gt; tongue_03 -&gt;
	/// tongue_tip) running root to tip along +X. Weights come from
	/// SkinBinder.BindSmooth against the chain, so each vertex follows its
	/// nearest segment and the tongue bends instead of shearing.
	/// </summary>
	static (PolyMesh, Skeleton) BuildTongue( MouthOptions o, float archDepth )
	{
		var w = o.Width;
		var h = o.Height;
		var d = o.Depth;
		var tongue = Primitives.QuadSphere( 1f, 4, o.TongueSlot );
		MeshTransform.Apply( tongue, Xform.Scale( new Vec3( d * 0.6f, w * 0.55f, h * 0.35f ) ) );
		FaceMesh.Moved( tongue, new Vec3( archDepth * 0.2f, 0, -h * 0.35f ) );

		var skeleton = new Skeleton();
		var z = -h * 0.35f;
		// The chain spans the tongue ellipsoid: its centre is archDepth * 0.2
		// forward of the mouth origin and its half-length is 0.6 * d, so the
		// four heads sit at -0.75 / -0.25 / +0.25 / +0.75 of that half-length
		// and the tip tail reaches just past the tongue tip. Heads carry
		// Position: Build() moves the tongue mesh by Position afterwards,
		// and the bones must stay on the mesh.
		var cx = archDepth * 0.2f;
		var halfLen = 0.6f * d;
		var heads = new[]
		{
			o.Position + new Vec3( cx - 0.75f * halfLen, 0, z ),
			o.Position + new Vec3( cx - 0.25f * halfLen, 0, z ),
			o.Position + new Vec3( cx + 0.25f * halfLen, 0, z ),
			o.Position + new Vec3( cx + 0.75f * halfLen, 0, z ),
		};
		var names = new[] { "tongue_01", "tongue_02", "tongue_03", "tongue_tip" };
		for ( var i = 0; i < 4; i++ )
		{
			var tail = i < 3 ? heads[i + 1] : heads[i] + new Vec3( 0.2f * d, 0, 0.02f * h );
			skeleton.AddBoneFromPoints( names[i], i - 1, heads[i], tail );
		}

		tongue.Skin = SkinBinder.BindSmooth( tongue, skeleton );
		return (tongue, skeleton);
	}

	/// <summary>
	/// Articulate the tongue chain: curl every bone after the root by
	/// <paramref name="curlRadians"/> about the lateral axis, so the tip
	/// curls progressively (a curl of 0.3 rad per bone lifts the tip about
	/// 50 degrees over the chain). Operates on the skeleton in place, in the
	/// bones' parent space — a pose, not a rebuild.
	/// </summary>
	public static void CurlTongue( Skeleton skeleton, float curlRadians )
	{
		if ( skeleton is null ) throw new ArgumentNullException( nameof( skeleton ) );
		if ( MathF.Abs( curlRadians ) < 1e-6f ) return;
		for ( var i = 1; i < skeleton.Bones.Count; i++ )
		{
			var bone = skeleton.Bones[i];
			bone.Local = Xform.Rotate( new Vec3( 0, 1, 0 ), curlRadians ) * bone.Local;
		}
	}

	/// <summary>Hard palate: a shallow domed U-band across the roof of the mouth, following the upper arch.</summary>
	static PolyMesh BuildPalate( MouthOptions o, float archDepth, float archHalf )
	{
		var palate = new PolyMesh();
		var outer = new List<Vec3>();
		var inner = new List<Vec3>();
		for ( var i = 0; i < ArchSamples; i++ )
		{
			var t = (-65f + i * (130f / (ArchSamples - 1))) * MathF.PI / 180f;
			outer.Add( new Vec3( MathF.Cos( t ) * archDepth, MathF.Sin( t ) * archHalf, 0.55f * o.Height ) );
			inner.Add( new Vec3( MathF.Cos( t ) * archDepth * 0.8f, MathF.Sin( t ) * archHalf * 0.8f, 0.62f * o.Height ) );
		}
		FaceMesh.Strip( palate, outer, inner, o.PalateSlot, closed: false );
		return palate;
	}

	/// <summary>Dark cavity box behind the teeth, as in MouthFeature, so an open mouth never shows floating teeth.</summary>
	static PolyMesh BuildCavity( MouthOptions o )
	{
		var d = o.Depth;
		var cavity = Primitives.Box( d * 0.5f, o.Width * 0.9f, o.Height * 1.2f, o.CavitySlot );
		return FaceMesh.Moved( cavity, new Vec3( -d * 0.4f, 0, 0 ) );
	}

	/// <summary>Sublingual frenulum: a thin vertical membrane from the tongue underside down to the mouth floor, under the tip.</summary>
	static PolyMesh BuildFrenulum( MouthOptions o )
	{
		var membrane = Primitives.Box( 0.30f * o.Depth, 0.02f, 0.15f * o.Height, o.FrenulumSlot );
		return FaceMesh.Moved( membrane, new Vec3( 0.10f * o.Depth, 0, -0.55f * o.Height ) );
	}

	/// <summary>Pharyngeal jaw: a second, smaller toothed arch deep in the mouth (moray / xenomorph inner jaw), 12 small teeth on a 0.55-scale arch.</summary>
	static PolyMesh BuildPharyngealJaw( MouthOptions o, float archDepth, float archHalf )
	{
		var jaw = new PolyMesh();
		var scale = 0.55f;
		for ( var i = 0; i < 12; i++ )
		{
			var t = (-60f + i * (120f / 11)) * MathF.PI / 180f;
			var tooth = Primitives.Box( 0.30f * o.Width / 6f, 0.28f * o.Width / 6f, 0.40f * o.Height / 2f, o.ToothSlot );
			var cone = Primitives.Cone( 0.10f * o.Width / 6f, 0.16f * o.Height / 2f, 8, o.ToothSlot );
			MeshTransform.Apply( cone, Xform.Translate( new Vec3( 0, 0, 0.28f * o.Height / 2f ) ) );
			FaceMesh.Append( tooth, cone );
			FaceMesh.Append( jaw, FaceMesh.Moved( tooth,
				new Vec3( MathF.Cos( t ) * archDepth * scale - 0.45f * o.Depth, MathF.Sin( t ) * archHalf * scale, 0 ) ) );
		}
		return jaw;
	}
}

/// <summary>
/// MouthBag: the `add MouthBag` verb (spec 4.3). One feature in the history
/// so the whole oral cavity rebuilds together. The upper teeth body is named
/// exactly "teeth" and the lower "teeth_lower", as MouthFeature does, so
/// AutoWeights keeps treating the upper teeth as a rigid island and the
/// lower teeth move with the jaw bone once bound.
///
/// `add MouthBag name=Mouth preset=human fang_length=0.2` is the spec's
/// example. Sliders add to the preset's base (vampire fangs +0.35,
/// carnivore +0.15, orc tusk +0.6, shark rows -&gt; 3, abyssal pharyngeal
/// jaw on), so a preset never silently overrides a value that was typed.
/// </summary>
public sealed class MouthBagFeature : Feature
{
	public override string TypeName => "MouthBag";
	public readonly ChoiceParam Preset = new( "Preset", new[] { "Human", "Vampire", "Orc", "Carnivore", "Shark", "Abyssal" } );
	public readonly FloatParam Width = new( "Width", 6f, 0.5f, 50f, "u" );
	public readonly FloatParam Height = new( "Height", 2f, 0.2f, 20f, "u" );
	public readonly FloatParam Depth = new( "Depth", 1.5f, 0.2f, 20f, "u" );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly FloatParam FangLength = new( "Fang length", 0f, 0f, 2f, "u" );
	public readonly FloatParam TuskCurve = new( "Tusk curve", 0f, 0f, 1f );
	public readonly IntParam Rows = new( "Rows", 1, 1, 3 ) { Slider = false };
	public readonly BoolParam PharyngealJaw = new( "Pharyngeal jaw", false );
	public readonly IntParam ToothSlot = new( "Tooth slot", 0, 0, 63 ) { Slider = false };
	public readonly IntParam ToothRootSlot = new( "Tooth root slot", 1, 0, 63 ) { Slider = false };
	public readonly IntParam GumSlot = new( "Gum slot", 2, 0, 63 ) { Slider = false };
	public readonly IntParam TongueSlot = new( "Tongue slot", 3, 0, 63 ) { Slider = false };
	public readonly IntParam CavitySlot = new( "Cavity slot", 4, 0, 63 ) { Slider = false };
	public readonly IntParam PalateSlot = new( "Palate slot", 5, 0, 63 ) { Slider = false };
	public readonly IntParam FrenulumSlot = new( "Frenulum slot", 6, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Preset, Width, Height, Depth, Position, FangLength, TuskCurve, Rows, PharyngealJaw, ToothSlot, ToothRootSlot, GumSlot, TongueSlot, CavitySlot, PalateSlot, FrenulumSlot };

	protected override void Execute( FeatureContext ctx )
	{
		if ( !(Width.Clamped > 0f) || !(Height.Clamped > 0f) || !(Depth.Clamped > 0f) )
			FailOn( "Width", "A mouth needs positive width, height and depth", $"Width is {Width.Clamped:0.###}, height {Height.Clamped:0.###}, depth {Depth.Clamped:0.###}.", "Increase Width, Height and Depth" );

		var preset = Preset.Value switch
		{
			"Vampire" => MouthMaker.MouthPreset.Vampire,
			"Orc" => MouthMaker.MouthPreset.Orc,
			"Carnivore" => MouthMaker.MouthPreset.Carnivore,
			"Shark" => MouthMaker.MouthPreset.Shark,
			"Abyssal" => MouthMaker.MouthPreset.Abyssal,
			_ => MouthMaker.MouthPreset.Human,
		};

		var options = new MouthMaker.MouthOptions
		{
			Name = string.IsNullOrWhiteSpace( Name ) ? "Mouth" : Name,
			Preset = preset,
			Width = Width.Clamped,
			Height = Height.Clamped,
			Depth = Depth.Clamped,
			Position = Position.Value,
			FangLength = FangLength.Clamped,
			TuskCurve = TuskCurve.Clamped,
			Rows = Rows.Clamped,
			PharyngealJaw = PharyngealJaw.Value,
			ToothSlot = ToothSlot.Clamped,
			ToothRootSlot = ToothRootSlot.Clamped,
			GumSlot = GumSlot.Clamped,
			TongueSlot = TongueSlot.Clamped,
			CavitySlot = CavitySlot.Clamped,
			PalateSlot = PalateSlot.Clamped,
			FrenulumSlot = FrenulumSlot.Clamped,
		};

		MouthMaker.MouthBag bag = null!; // Fail throws; never observed, only satisfies definite assignment.
		try
		{
			bag = MouthMaker.Build( options );
		}
		catch ( ArgumentException e )
		{
			// Fail throws (DoesNotReturn); the feature reports the bad parameters.
			Fail( e.Message, "The mouth parameters do not fit together.", "Check Width, Height, Depth, Rows, Fang length and Tusk curve" );
		}

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "teeth", bag.TeethUpper ) );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "teeth_lower", bag.TeethLower ) );

		// One gum body for both arches, as MouthFeature has it.
		var gum = new PolyMesh();
		FaceMesh.Append( gum, bag.GumUpper );
		FaceMesh.Append( gum, bag.GumLower );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "gum", gum ) );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "tongue", bag.Tongue ) );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "palate", bag.Palate ) );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "mouth_cavity", bag.Cavity ) );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "frenulum", bag.Frenulum ) );
		if ( bag.PharyngealJaw is not null )
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), "pharyngeal_jaw", bag.PharyngealJaw ) );

		// Tongue rig: merge the four tongue bones into the studio rig
		// (FoliageFeature's precedent for a feature adding bones), reusing
		// any tongue bone that is already there, and remap the tongue mesh's
		// skin weights from the standalone chain onto the studio rig. When
		// the studio has no rig the standalone binding stays as built.
		if ( ctx.Rig is not null && bag.TongueSkeleton is not null && bag.Tongue.Skin is not null )
		{
			var map = new Dictionary<int, int>();
			for ( var i = 0; i < bag.TongueSkeleton.Bones.Count; i++ )
			{
				var bone = bag.TongueSkeleton.Bones[i];
				var existing = ctx.Rig.IndexOf( bone.Name );
				if ( existing >= 0 ) { map[i] = existing; continue; }
				var parent = bone.Parent < 0 ? -1 : map[bone.Parent];
				map[i] = ctx.Rig.AddBoneFromPoints( bone.Name, parent,
					bag.TongueSkeleton.HeadWorld( i ), bag.TongueSkeleton.TailWorld( i ) );
			}

			var remapped = new SkinWeights();
			foreach ( var weights in bag.Tongue.Skin.Vertices )
				remapped.Vertices.Add( weights
					.Select( bw => new BoneWeight( map.TryGetValue( bw.Bone, out var ni ) ? ni : bw.Bone, bw.Weight ) )
					.ToArray() );
			bag.Tongue.Skin = remapped;
		}

		// Slot looks (PartStudio merges these into MaterialLooks; a `material`
		// line for the slot still wins). tooth_enamel and wet_mucus are
		// shipped presets; the dentin root look has no preset and carries
		// its values directly.
		if ( !SlotLook.TryPreset( "tooth_enamel", out var enamel ) ) enamel = new SlotLook();
		ctx.SlotLooks[options.ToothSlot] = enamel;
		ctx.SlotLooks[options.ToothRootSlot] = new SlotLook
		{
			Color = FurMaterial.ParseHex( "#d8c890", new Vec3( 0.847059f, 0.784314f, 0.564706f ) ),
			Rough = 0.5f,
			Subsurface = 0.3f,
			SubsurfaceColor = FurMaterial.ParseHex( "#c8a860", new Vec3( 0.784314f, 0.658824f, 0.376471f ) ),
		};

		if ( !SlotLook.TryPreset( "wet_mucus", out var mucus ) ) mucus = new SlotLook();
		var gumLook = mucus.Clone();
		gumLook.Color = FurMaterial.ParseHex( "#b85a68", new Vec3( 0.721569f, 0.352941f, 0.407843f ) );
		ctx.SlotLooks[options.GumSlot] = gumLook;

		var tongueLook = mucus.Clone();
		tongueLook.Color = FurMaterial.ParseHex( "#c06a72", new Vec3( 0.752941f, 0.415686f, 0.447059f ) );
		tongueLook.Rough = 0.55f; // filiform papillae micro-roughness
		ctx.SlotLooks[options.TongueSlot] = tongueLook;

		var palateLook = mucus.Clone();
		palateLook.Color = FurMaterial.ParseHex( "#b06068", new Vec3( 0.690196f, 0.376471f, 0.407843f ) );
		ctx.SlotLooks[options.PalateSlot] = palateLook;

		var frenulumLook = mucus.Clone();
		frenulumLook.Color = FurMaterial.ParseHex( "#c07078", new Vec3( 0.752941f, 0.439216f, 0.470588f ) );
		ctx.SlotLooks[options.FrenulumSlot] = frenulumLook;

		ctx.SlotLooks[options.CavitySlot] = new SlotLook { Color = new Vec3( 0.05f, 0.03f, 0.03f ), Rough = 0.9f };
	}
}
