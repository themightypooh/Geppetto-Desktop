using System;
using System.Collections.Generic;
using System.Linq;
using Effigy.Render;

namespace Effigy;

/// <summary>
/// Face kit, wave 3. Coordinate convention for every feature in this file, matching the
/// biped rig and EyeAsset: forward is +X, up is +Z, lateral is Y, left is +Y.
/// An EyeAsset mesh is already in eye-local space (forward +X, up +Z), so the default
/// Eye forward needs no rotation at all.
/// </summary>
static class FaceMesh
{
	static float G( float v, float centre, float radius )
	{
		var t = (v - centre) / radius;
		return MathF.Exp( -t * t );
	}

	public static float Zone( float u, float f, float l, float u0, float ru, float f0, float rf, float l0, float rl )
		=> G( u, u0, ru ) * G( f, f0, rf ) * G( l, l0, rl );

	/// <summary>Append source (already placed) into target.</summary>
	public static void Append( PolyMesh target, PolyMesh source ) => MeshTransform.Append( target, source );

	public static PolyMesh Moved( PolyMesh m, Vec3 offset )
	{
		MeshTransform.Apply( m, Xform.Translate( offset ) );
		return m;
	}

	public static PolyMesh Rotated( PolyMesh m, Vec3 from, Vec3 to )
	{
		var f = from.Normal;
		var t = to.Normal;
		if ( f.LengthSquared < 1e-8f || t.LengthSquared < 1e-8f )
			return m;
		var dot = Math.Clamp( Vec3.Dot( f, t ), -1f, 1f );
		if ( dot > 0.9999f )
			return m;
		var axis = Vec3.Cross( f, t );
		if ( axis.LengthSquared < 1e-8f )
			axis = new Vec3( 0, 0, 1 );
		MeshTransform.Apply( m, Xform.Rotate( axis.Normal, MathF.Acos( dot ) ) );
		return m;
	}

	public static void Quad( PolyMesh m, Vec3 a, Vec3 b, Vec3 c, Vec3 d, int material )
	{
		var i0 = m.AddVertex( a );
		var i1 = m.AddVertex( b );
		var i2 = m.AddVertex( c );
		var i3 = m.AddVertex( d );
		m.AddFace( new[] { i0, i1, i2, i3 }, new[] { new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ) }, material );
	}

	/// <summary>Quad strip between two point rings of equal length, closed or open.</summary>
	public static void Strip( PolyMesh m, IReadOnlyList<Vec3> ringA, IReadOnlyList<Vec3> ringB, int material, bool closed )
	{
		var n = Math.Min( ringA.Count, ringB.Count );
		var steps = closed ? n : n - 1;
		for ( var i = 0; i < steps; i++ )
		{
			var j = (i + 1) % n;
			Quad( m, ringA[i], ringA[j], ringB[j], ringB[i], material );
		}
	}
}

/// <summary>
/// Eye: promotes Render/EyeAsset to the script language. Emits the eyeball (a sphere with
/// its front cap removed so the iris bowl sits inside it, not behind it), the EyeAsset
/// iris + cornea mesh, a pupil disc built from the asset's own pupil-boundary ring
/// (so slit/goat/keyhole pupils keep their shape), and an optional catchlight bead.
/// The eyeball body takes the feature name, so name=eye_L binds to eye_L in AutoWeights.
/// </summary>
public sealed class EyeFeature : Feature
{
	public override string TypeName => "Eye";
	public readonly FloatParam Radius = new( "Radius", 1f, 0.01f, 100f, "u" );
	public readonly FloatParam IrisRadius = new( "Iris radius", 0.5f, 0.01f, 100f, "u" );
	public readonly FloatParam PupilScale = new( "Pupil scale", 1f, 0.05f, 3f );
	public readonly ChoiceParam Preset = new( "Preset", new[] { "Human", "Cat", "Goat", "Dragon", "Keyhole" } );
	public readonly ChoiceParam Pupil = new( "Pupil", new[] { "Round", "VerticalSlit", "HorizontalGoat", "Keyhole" } );
	public readonly Vec3Param IrisColor = new( "Iris color", new Vec3( 0.2f, 0.4f, 0.6f ) );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly Vec3Param Forward = new( "Forward", new Vec3( 1, 0, 0 ) );
	public readonly IntParam ScleraSlot = new( "Sclera slot", 0, 0, 63 ) { Slider = false };
	public readonly IntParam IrisSlot = new( "Iris slot", 1, 0, 63 ) { Slider = false };
	public readonly IntParam CorneaSlot = new( "Cornea slot", 2, 0, 63 ) { Slider = false };
	public readonly IntParam PupilSlot = new( "Pupil slot", 3, 0, 63 ) { Slider = false };
	public readonly BoolParam Catchlight = new( "Catchlight", true );
	public readonly IntParam CatchlightSlot = new( "Catchlight slot", 4, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[] { Radius, IrisRadius, PupilScale, Preset, Pupil, IrisColor, Position, Forward, ScleraSlot, IrisSlot, CorneaSlot, PupilSlot, Catchlight, CatchlightSlot };

	protected override void Execute( FeatureContext ctx )
	{
		var r = Radius.Clamped;
		if ( IrisRadius.Clamped >= r )
			FailOn( "Iris radius", "Iris radius must be smaller than radius", $"Iris radius is {IrisRadius.Clamped:0.###} and radius is {r:0.###}.", "Reduce Iris radius", "Increase Radius" );
		var pupilShape = Pupil.Value switch
		{
			"VerticalSlit" => PupilShape.VerticalSlit,
			"HorizontalGoat" => PupilShape.HorizontalGoat,
			"Keyhole" => PupilShape.Keyhole,
			_ => PupilShape.Round,
		};
		var asset = EyeAsset.BuildEyeAsset( r, Preset.Value, IrisColor.Value, pupilShape );
		var fwd = Forward.Value.LengthSquared > 1e-8f ? Forward.Value.Normal : new Vec3( 1, 0, 0 );
		var baseName = string.IsNullOrWhiteSpace( Name ) ? "eye" : Name;

		// Sclera: sphere with the front cap removed. A face whose centre is further
		// forward than 0.86r is cut, leaving a hole of ~0.51r radius — just outside the
		// 0.50r iris rim, so the iris bowl sits in the hole and the cornea dome covers it.
		var sclera = Primitives.QuadSphere( r, 8, ScleraSlot.Clamped );
		var kept = new PolyMesh();
		foreach ( var face in sclera.Faces )
		{
			var cx = 0f;
			foreach ( var vi in face.Indices ) cx += sclera.Positions[vi].x;
			cx /= face.Indices.Length;
			if ( cx > 0.86f * r )
				continue;
			var idx = face.Indices.Select( vi => kept.AddVertex( sclera.Positions[vi] ) ).ToArray();
			kept.AddFace( idx, face.UVs, ScleraSlot.Clamped );
		}
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), baseName, FaceMesh.Moved( FaceMesh.Rotated( kept, new Vec3( 1, 0, 0 ), fwd ), Position.Value ) ) );

		// Iris + cornea: remap the asset's two face materials onto the caller's slots.
		var irisMesh = asset.Mesh;
		for ( var i = 0; i < irisMesh.Faces.Count; i++ )
		{
			var f = irisMesh.Faces[i];
			f.Material = f.Material == 1 ? CorneaSlot.Clamped : IrisSlot.Clamped;
			irisMesh.Faces[i] = f;
		}
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), baseName + "_iris", FaceMesh.Moved( FaceMesh.Rotated( irisMesh, new Vec3( 1, 0, 0 ), fwd ), Position.Value ) ) );

		// Pupil disc from the asset's own first ring (32 vertices = pupil boundary).
		// EyeAsset constants, EyeRender.cs: frontX = 0.78r, dish = 0.12r.
		var pupil = new PolyMesh();
		var frontX = 0.78f * r;
		var dish = 0.12f * r;
		var centre = pupil.AddVertex( new Vec3( frontX - dish, 0, 0 ) );
		var ring = new List<int>();
		for ( var k = 0; k < 32 && k < asset.Mesh.Positions.Count; k++ )
		{
			var p = asset.Mesh.Positions[k];
			ring.Add( pupil.AddVertex( new Vec3( p.x, p.y * PupilScale.Clamped, p.z * PupilScale.Clamped ) ) );
		}
		for ( var k = 0; k < ring.Count; k++ )
			pupil.AddFace( new[] { centre, ring[k], ring[(k + 1) % ring.Count] }, new[] { new Vec2( 0.5f, 0.5f ), new Vec2( 0, 0 ), new Vec2( 1, 0 ) }, PupilSlot.Clamped );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), baseName + "_pupil", FaceMesh.Moved( FaceMesh.Rotated( pupil, new Vec3( 1, 0, 0 ), fwd ), Position.Value ) ) );

		if ( Catchlight.Value )
		{
			var bead = Primitives.QuadSphere( 0.08f * r, 4, CatchlightSlot.Clamped );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), baseName + "_catchlight", FaceMesh.Moved( FaceMesh.Rotated( FaceMesh.Moved( bead, new Vec3( 0.95f * r, 0.2f * r, 0.35f * r ) ), new Vec3( 1, 0, 0 ), fwd ), Position.Value ) ) );
		}
	}
}

/// <summary>
/// Head: a quad base mesh with correct face-loop intent — a deformed all-quad sphere, so
/// there are no triangle/n-gon fans in the eye or mouth zones and the only valence-3
/// corners sit at cube diagonals, outside those zones (see FaceCheck). It is a base mesh,
/// not a finished head: jaw taper, brow ridge and a nose bump are shaped in, detail and
/// features are added downstream.
/// </summary>
public sealed class HeadFeature : Feature
{
	public override string TypeName => "Head";
	public readonly FloatParam Width = new( "Width", 14f, 1f, 100f, "u" );
	public readonly FloatParam Height = new( "Height", 18f, 1f, 100f, "u" );
	public readonly FloatParam Depth = new( "Depth", 16f, 1f, 100f, "u" );
	public readonly IntParam Divisions = new( "Divisions", 6, 2, 16 );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };
	public override IReadOnlyList<IParam> Parameters => new IParam[] { Width, Height, Depth, Divisions, Position, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var mesh = Primitives.QuadSphere( 1f, Divisions.Clamped, Material.Clamped );
		var hx = Depth.Clamped / 2f;
		var hy = Width.Clamped / 2f;
		var hz = Height.Clamped / 2f;
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			var x = p.x * hx;
			var y = p.y * hy;
			var z = p.z * hz;
			// Jaw taper: narrower below the centre line.
			if ( z < 0 ) y *= 1f - 0.25f * (-z / hz);
			// Brow ridge and nose bump, both front-only and falloff-shaped.
			var brow = x > 0 ? MathF.Exp( -MathF.Pow( (z - 0.32f * hz) / (0.12f * hz), 2 ) ) * MathF.Exp( -MathF.Pow( y / (0.45f * hy), 2 ) ) : 0f;
			x += 0.35f * brow;
			var nose = x > 0 ? MathF.Exp( -MathF.Pow( y / (0.14f * hy), 2 ) ) * MathF.Exp( -MathF.Pow( (z + 0.05f * hz) / (0.16f * hz), 2 ) ) : 0f;
			x += 0.9f * nose;
			// Chin: lower front pushed slightly forward.
			var chin = x > 0 ? MathF.Exp( -MathF.Pow( (z + 0.72f * hz) / (0.14f * hz), 2 ) ) * MathF.Exp( -MathF.Pow( y / (0.3f * hy), 2 ) ) : 0f;
			x += 0.25f * chin;
			mesh.Positions[i] = new Vec3( x, y, z );
		}
		// Face flatten: the front is a face, not a ball.
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var p = mesh.Positions[i];
			if ( p.x > 0 ) mesh.Positions[i] = new Vec3( p.x * 0.96f, p.y, p.z );
		}
		MeshTransform.Apply( mesh, Xform.Translate( Position.Value ) );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), string.IsNullOrWhiteSpace( Name ) ? "Head" : Name, mesh ) );
	}
}

/// <summary>
/// Mouth kit: dental arches, gum line, tongue, lip ring with Cupid's bow and a fuller
/// lower lip, and a dark cavity so an open mouth never shows floating teeth. Upper teeth
/// are one body named exactly "teeth" so AutoWeights treats them as a rigid island;
/// lower teeth are "teeth_lower" (jaw bone moves them once bound).
/// </summary>
public sealed class MouthFeature : Feature
{
	public override string TypeName => "Mouth";
	public readonly FloatParam Width = new( "Width", 6f, 0.5f, 50f, "u" );
	public readonly FloatParam Height = new( "Height", 2f, 0.2f, 20f, "u" );
	public readonly FloatParam Depth = new( "Depth", 1.5f, 0.2f, 20f, "u" );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly IntParam ToothSlot = new( "Tooth slot", 0, 0, 63 ) { Slider = false };
	public readonly IntParam GumSlot = new( "Gum slot", 1, 0, 63 ) { Slider = false };
	public readonly IntParam TongueSlot = new( "Tongue slot", 2, 0, 63 ) { Slider = false };
	public readonly IntParam LipSlot = new( "Lip slot", 3, 0, 63 ) { Slider = false };
	public readonly IntParam CavitySlot = new( "Cavity slot", 4, 0, 63 ) { Slider = false };
	public override IReadOnlyList<IParam> Parameters => new IParam[] { Width, Height, Depth, Position, ToothSlot, GumSlot, TongueSlot, LipSlot, CavitySlot };

	static float ArchX( float t, float depth ) => MathF.Cos( t ) * depth;
	static float ArchY( float t, float halfW ) => MathF.Sin( t ) * halfW;

	protected override void Execute( FeatureContext ctx )
	{
		var w = Width.Clamped;
		var h = Height.Clamped;
		var d = Depth.Clamped;
		var archDepth = w * 0.35f;
		var archHalf = w * 0.5f;
		var pos = Position.Value;

		// Cavity: dark box behind the teeth.
		var cavity = Primitives.Box( d * 0.5f, w * 0.9f, h * 1.2f, CavitySlot.Clamped );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "mouth_cavity", FaceMesh.Moved( cavity, pos + new Vec3( -d * 0.4f, 0, 0 ) ) ) );

		// Teeth: 14 per arch, incisors flat, canines taller, molars broad.
		for ( var lower = 0; lower < 2; lower++ )
		{
			var teeth = new PolyMesh();
			for ( var i = 0; i < 14; i++ )
			{
				var t = (-65f + i * (130f / 13f)) * MathF.PI / 180f;
				var at = MathF.Abs( t );
				var tw = at < 0.35f ? 0.45f : at < 0.65f ? 0.5f : 0.55f;
				var th = at < 0.35f ? 0.7f : at < 0.65f ? 0.85f : 0.6f;
				var box = Primitives.Box( 0.4f, tw, th, ToothSlot.Clamped );
				var zc = lower == 0 ? h * 0.35f : -h * 0.35f;
				FaceMesh.Append( teeth, FaceMesh.Moved( box, new Vec3( ArchX( t, archDepth ), ArchY( t, archHalf ), zc ) ) );
			}
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), lower == 0 ? "teeth" : "teeth_lower", FaceMesh.Moved( teeth, pos ) ) );
		}

		// Gum: a strip along each arch at the tooth roots.
		var gum = new PolyMesh();
		for ( var lower = 0; lower < 2; lower++ )
		{
			var outer = new List<Vec3>();
			var inner = new List<Vec3>();
			for ( var i = 0; i <= 20; i++ )
			{
				var t = (-65f + i * (130f / 20f)) * MathF.PI / 180f;
				var zc = lower == 0 ? h * 0.62f : -h * 0.62f;
				outer.Add( new Vec3( ArchX( t, archDepth ), ArchY( t, archHalf ), zc ) );
				inner.Add( new Vec3( ArchX( t, archDepth * 0.82f ), ArchY( t, archHalf * 0.82f ), zc ) );
			}
			FaceMesh.Strip( gum, outer, inner, GumSlot.Clamped, false );
		}
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "gum", FaceMesh.Moved( gum, pos ) ) );

		// Tongue: flattened ellipsoid on the mouth floor, tip forward.
		var tongue = Primitives.QuadSphere( 1f, 4, TongueSlot.Clamped );
		MeshTransform.Apply( tongue, Xform.Scale( new Vec3( d * 0.6f, w * 0.55f, h * 0.35f ) ) );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "tongue", FaceMesh.Moved( tongue, pos + new Vec3( archDepth * 0.2f, 0, -h * 0.35f ) ) ) );

		// Lip ring: outer loop with Cupid's bow on top and a fuller lower lip,
		// inner loop as the mouth opening, front/back faces plus walls.
		float TopOuter( float y )
		{
			var yn = 2f * y / w;
			return 0.45f * h * (1f - yn * yn) + 0.08f * h * MathF.Exp( -MathF.Pow( (MathF.Abs( y ) - 0.18f * w) / (0.08f * w), 2 ) ) - 0.08f * h * MathF.Exp( -MathF.Pow( y / (0.08f * w), 2 ) );
		}
		float BottomOuter( float y )
		{
			var yn = 2f * y / w;
			return -0.55f * h * (1f - yn * yn);
		}
		var outerLoop = new List<Vec3>();
		var innerLoop = new List<Vec3>();
		const int n = 24;
		for ( var i = 0; i <= n; i++ )
		{
			var y = -w / 2f + i * (w / n);
			outerLoop.Add( new Vec3( d, y, TopOuter( y ) ) );
			innerLoop.Add( new Vec3( d, y * 0.92f, 0.12f * h * (1f - MathF.Pow( 2f * y / w, 2 )) ) );
		}
		for ( var i = n; i >= 0; i-- )
		{
			var y = -w / 2f + i * (w / n);
			outerLoop.Add( new Vec3( d, y, BottomOuter( y ) ) );
			innerLoop.Add( new Vec3( d, y * 0.92f, -0.12f * h * (1f - MathF.Pow( 2f * y / w, 2 )) ) );
		}
		var lip = new PolyMesh();
		// Front annulus (outer -> inner) and back annulus, plus outer and inner walls.
		var backOuter = outerLoop.Select( p => p - new Vec3( d, 0, 0 ) ).ToList();
		var backInner = innerLoop.Select( p => p - new Vec3( d, 0, 0 ) ).ToList();
		FaceMesh.Strip( lip, outerLoop, innerLoop, LipSlot.Clamped, true );
		FaceMesh.Strip( lip, backInner, backOuter, LipSlot.Clamped, true );
		FaceMesh.Strip( lip, outerLoop, backOuter, LipSlot.Clamped, true );
		FaceMesh.Strip( lip, backInner, innerLoop, LipSlot.Clamped, true );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), "lip", FaceMesh.Moved( lip, pos ) ) );
	}
}

/// <summary>
/// Ear: a stylised folded ear — flattened torus helix, concha bowl, lobe and tragus —
/// merged into one body named ear_L / ear_R. A clean folded silhouette, not a disc.
/// </summary>
public sealed class EarFeature : Feature
{
	public override string TypeName => "Ear";
	public readonly FloatParam Height = new( "Height", 5f, 0.5f, 50f, "u" );
	public readonly FloatParam Width = new( "Width", 3f, 0.5f, 50f, "u" );
	public readonly ChoiceParam Side = new( "Side", new[] { "Left", "Right" } );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };
	public override IReadOnlyList<IParam> Parameters => new IParam[] { Height, Width, Side, Position, Material };

	protected override void Execute( FeatureContext ctx )
	{
		var h = Height.Clamped;
		var w = Width.Clamped;
		var mesh = new PolyMesh();
		// Helix: torus ring about Z, rotated into the YZ (side) plane, flattened forward.
		var helix = Primitives.Torus( w * 0.4f, w * 0.12f, 24, 8, Material.Clamped );
		MeshTransform.Apply( helix, Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI / 2f ) );
		MeshTransform.Apply( helix, Xform.Scale( new Vec3( 0.35f, 1f, 1.35f ) ) );
		FaceMesh.Append( mesh, helix );
		// Concha bowl and lobe, flattened spheres; tragus, a small cone at the front.
		var concha = Primitives.QuadSphere( 1f, 4, Material.Clamped );
		MeshTransform.Apply( concha, Xform.Scale( new Vec3( w * 0.18f, w * 0.32f, h * 0.3f ) ) );
		FaceMesh.Append( mesh, FaceMesh.Moved( concha, new Vec3( w * 0.05f, 0, -h * 0.05f ) ) );
		var lobe = Primitives.QuadSphere( 1f, 4, Material.Clamped );
		MeshTransform.Apply( lobe, Xform.Scale( new Vec3( w * 0.15f, w * 0.28f, h * 0.18f ) ) );
		FaceMesh.Append( mesh, FaceMesh.Moved( lobe, new Vec3( 0, 0, -h * 0.42f ) ) );
		var tragus = Primitives.Cone( w * 0.12f, h * 0.18f, 12, Material.Clamped );
		MeshTransform.Apply( tragus, Xform.Rotate( new Vec3( 0, 1, 0 ), MathF.PI / 2f ) );
		FaceMesh.Append( mesh, FaceMesh.Moved( tragus, new Vec3( w * 0.18f, 0, -h * 0.12f ) ) );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Side.Value == "Right" ? "ear_R" : "ear_L", FaceMesh.Moved( mesh, Position.Value ) ) );
	}
}

/// <summary>
/// ExpressionPack: FACS-lite morph geometry for a head body, named to match FaceRig's
/// controller keys (stereo keys get _L/_R morphs) so `facerig build` keeps them instead
/// of trimming them away, plus the proposal's own names as aliases. Morphs are hand-keyed
/// (no driver) until brow/lid bones drive them. Zones are falloffs off the head bounds,
/// forward +X, up +Z, lateral Y, left +Y.
/// </summary>
public sealed class ExpressionPackFeature : Feature
{
	public override string TypeName => "ExpressionPack";
	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Strength = new( "Strength", 1f, 0f, 3f );
	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Strength };

	// (morph name, kind, side: 0 both, +1 left only, -1 right only)
	static readonly (string, string, int)[] Keys =
	{
		("blink", "blink", 0), ("half_closed", "halfclosed", 0),
		("lid_closer_L", "lidcloser", 1), ("lid_closer_R", "lidcloser", -1),
		("lid_raiser_L", "lidraiser", 1), ("lid_raiser_R", "lidraiser", -1),
		("lid_tightener_L", "lidtightener", 1), ("lid_tightener_R", "lidtightener", -1),
		("lid_droop_L", "liddroop", 1), ("lid_droop_R", "liddroop", -1),
		("inner_raiser_L", "innerraiser", 1), ("inner_raiser_R", "innerraiser", -1),
		("outer_raiser_L", "outerraiser", 1), ("outer_raiser_R", "outerraiser", -1),
		("lowerer_L", "lowerer", 1), ("lowerer_R", "lowerer", -1),
		("cheek_raiser_L", "cheekraiser", 1), ("cheek_raiser_R", "cheekraiser", -1),
		("wrinkler", "wrinkler", 0), ("dilator", "dilator", 0),
		("cheek_puffer_L", "cheekpuffer", 1), ("cheek_puffer_R", "cheekpuffer", -1),
		("upper_raiser_L", "upperraiser", 1), ("upper_raiser_R", "upperraiser", -1),
		("corner_puller_L", "cornerpuller", 1), ("corner_puller_R", "cornerpuller", -1),
		("smile", "smile", 0),
		("corner_depressor_L", "cornerdepressor", 1), ("corner_depressor_R", "cornerdepressor", -1),
		("dimpler_L", "dimpler", 1), ("dimpler_R", "dimpler", -1),
		("stretcher_L", "stretcher", 1), ("stretcher_R", "stretcher", -1),
		("part", "part", 0), ("puckerer", "puckerer", 0), ("funneler", "funneler", 0),
		("bite", "bite", 0), ("presser", "presser", 0), ("tightener", "tightener", 0),
		("lower_lip", "lowerlip", 0), ("chin_raiser", "chinraiser", 0),
		("mouth_left", "mouthleft", 0), ("mouth_right", "mouthright", 0),
		("jaw_drop", "jawdrop", 0), ("mouth_drop", "mouthdrop", 0),
		("jaw_clencher", "jawclencher", 0), ("jaw_left", "jawleft", 0), ("jaw_right", "jawright", 0),
		// Proposal aliases, same geometry as their FaceRig counterparts.
		("browInnerUp_L", "innerraiser", 1), ("browInnerUp_R", "innerraiser", -1),
		("browOuterUp_L", "outerraiser", 1), ("browOuterUp_R", "outerraiser", -1),
		("browDown_L", "lowerer", 1), ("browDown_R", "lowerer", -1),
		("browTogether", "browtogether", 0),
		("lidUpperDown_L", "lidcloser", 1), ("lidUpperDown_R", "lidcloser", -1),
		("lidLowerUp_L", "lidtightener", 1), ("lidLowerUp_R", "lidtightener", -1),
		("eyeWide_L", "lidraiser", 1), ("eyeWide_R", "lidraiser", -1),
		("jawOpen", "jawdrop", 0), ("mouthSmile", "smile", 0),
		("mouthFrown", "frown", 0), ("mouthPucker", "puckerer", 0), ("mouthWide", "stretcherboth", 0),
		("cheekRaise_L", "cheekraiser", 1), ("cheekRaise_R", "cheekraiser", -1),
		("noseScrunch", "wrinkler", 0), ("nostrilFlare", "dilator", 0),
	};

	static float SideFactor( float l, int side )
	{
		if ( side == 0 ) return 1f;
		// Left is +Y: full on the named side, fading to 0 a quarter-width past centre.
		var s = side > 0 ? l : -l;
		return Math.Clamp( (s + 0.25f) / 0.5f, 0f, 1f );
	}

	static Vec3 Delta( string kind, float u, float f, float l, float hz, float hy, float hx )
	{
		// u = up (-1..1), f = forward (-1..1), l = lateral (-1..1), all relative to head centre.
		var z = new Vec3( 0, 0, 0 );
		float Brow( float lc ) => FaceMesh.Zone( u, f, l, 0.35f, 0.14f, 0.75f, 0.35f, lc, 0.22f );
		float UpperLid( float lc ) => FaceMesh.Zone( u, f, l, 0.18f, 0.1f, 0.75f, 0.35f, lc, 0.25f );
		float LowerLid( float lc ) => FaceMesh.Zone( u, f, l, 0.02f, 0.09f, 0.75f, 0.35f, lc, 0.25f );
		float Mouth( float lc ) => FaceMesh.Zone( u, f, l, -0.35f, 0.14f, 0.75f, 0.35f, lc, 0.3f );
		float LowerFace() => FaceMesh.Zone( u, f, l, -0.6f, 0.45f, 0.6f, 0.5f, 0f, 0.8f );
		return kind switch
		{
			"blink" => new Vec3( 0, 0, -0.10f * hz * UpperLid( 0 ) + 0.06f * hz * LowerLid( 0 ) ),
			"halfclosed" => new Vec3( 0, 0, -0.14f * hz * UpperLid( 0 ) + 0.08f * hz * LowerLid( 0 ) ),
			"lidcloser" => new Vec3( 0, 0, -0.16f * hz * UpperLid( 0 ) ),
			"lidraiser" => new Vec3( 0, 0, 0.12f * hz * UpperLid( 0 ) ),
			"lidtightener" => new Vec3( 0, 0, 0.10f * hz * LowerLid( 0 ) ),
			"liddroop" => new Vec3( 0, 0, -0.08f * hz * UpperLid( 0 ) - 0.04f * hz * Brow( 0 ) ),
			"innerraiser" => new Vec3( 0, 0, 0.14f * hz * Brow( 0 ) ),
			"outerraiser" => new Vec3( 0, 0, 0.14f * hz * Brow( 0 ) ),
			"lowerer" => new Vec3( 0, -Math.Sign( l ) * 0.05f * hy * Brow( 0 ), -0.10f * hz * Brow( 0 ) ),
			"browtogether" => new Vec3( 0, -Math.Sign( l ) * 0.08f * hy * Brow( 0 ), -0.04f * hz * Brow( 0 ) ),
			"cheekraiser" => new Vec3( 0.02f * hx * FaceMesh.Zone( u, f, l, -0.1f, 0.2f, 0.7f, 0.4f, 0, 0.6f ), 0, 0.08f * hz * FaceMesh.Zone( u, f, l, -0.1f, 0.2f, 0.7f, 0.4f, 0, 0.6f ) ),
			"wrinkler" => new Vec3( -0.05f * hx * FaceMesh.Zone( u, f, l, 0.05f, 0.12f, 0.8f, 0.3f, 0, 0.3f ), 0, 0.03f * hz * FaceMesh.Zone( u, f, l, 0.05f, 0.12f, 0.8f, 0.3f, 0, 0.3f ) ),
			"dilator" => new Vec3( 0, Math.Sign( l ) * 0.08f * hy * FaceMesh.Zone( u, f, l, -0.05f, 0.1f, 0.8f, 0.3f, 0, 0.25f ), 0 ),
			"cheekpuffer" => new Vec3( 0.08f * hx * FaceMesh.Zone( u, f, l, -0.25f, 0.25f, 0.6f, 0.4f, 0, 0.6f ), Math.Sign( l ) * 0.05f * hy * FaceMesh.Zone( u, f, l, -0.25f, 0.25f, 0.6f, 0.4f, 0, 0.6f ), 0 ),
			"upperraiser" => new Vec3( 0, 0, 0.10f * hz * FaceMesh.Zone( u, f, l, -0.28f, 0.1f, 0.75f, 0.35f, 0, 0.35f ) ),
			"cornerpuller" => new Vec3( 0.02f * hx * Mouth( 0 ), Math.Sign( l ) * 0.10f * hy * FaceMesh.Zone( u, f, l, -0.35f, 0.14f, 0.75f, 0.35f, 0, 0.2f ), 0.12f * hz * Mouth( 0 ) ),
			"smile" => new Vec3( 0, Math.Sign( l ) * 0.06f * hy * Mouth( 0 ), 0.10f * hz * Mouth( 0 ) - 0.05f * hz * FaceMesh.Zone( u, f, l, -0.45f, 0.1f, 0.75f, 0.35f, 0, 0.35f ) ),
			"cornerdepressor" => new Vec3( 0, 0, -0.12f * hz * Mouth( 0 ) ),
			"dimpler" => new Vec3( 0, -Math.Sign( l ) * 0.08f * hy * Mouth( 0 ), 0 ),
			"stretcher" => new Vec3( 0, Math.Sign( l ) * 0.12f * hy * Mouth( 0 ), -0.02f * hz * Mouth( 0 ) ),
			"stretcherboth" => new Vec3( 0, Math.Sign( l ) * 0.12f * hy * Mouth( 0 ), 0 ),
			"frown" => new Vec3( 0, 0, -0.10f * hz * Mouth( 0 ) ),
			"part" => new Vec3( 0, 0, 0.05f * hz * FaceMesh.Zone( u, f, l, -0.28f, 0.1f, 0.75f, 0.35f, 0, 0.35f ) - 0.05f * hz * FaceMesh.Zone( u, f, l, -0.42f, 0.1f, 0.75f, 0.35f, 0, 0.35f ) ),
			"puckerer" => new Vec3( 0.12f * hx * Mouth( 0 ), -Math.Sign( l ) * 0.08f * hy * Mouth( 0 ), 0 ),
			"funneler" => new Vec3( 0.10f * hx * Mouth( 0 ), -Math.Sign( l ) * 0.05f * hy * Mouth( 0 ), 0 ),
			"bite" => new Vec3( 0, 0, 0.08f * hz * FaceMesh.Zone( u, f, l, -0.42f, 0.1f, 0.75f, 0.35f, 0, 0.35f ) ),
			"presser" => new Vec3( 0, 0, -0.05f * hz * FaceMesh.Zone( u, f, l, -0.28f, 0.1f, 0.75f, 0.35f, 0, 0.35f ) + 0.05f * hz * FaceMesh.Zone( u, f, l, -0.42f, 0.1f, 0.75f, 0.35f, 0, 0.35f ) ),
			"tightener" => new Vec3( 0, Math.Sign( l ) * 0.03f * hy * Mouth( 0 ), -0.03f * hz * Mouth( 0 ) ),
			"lowerlip" => new Vec3( 0, 0, -0.10f * hz * FaceMesh.Zone( u, f, l, -0.42f, 0.1f, 0.75f, 0.35f, 0, 0.35f ) ),
			"chinraiser" => new Vec3( 0, 0, 0.08f * hz * FaceMesh.Zone( u, f, l, -0.7f, 0.2f, 0.7f, 0.4f, 0, 0.4f ) ),
			"mouthleft" => new Vec3( 0, 0.10f * hy * Mouth( 0 ), 0 ),
			"mouthright" => new Vec3( 0, -0.10f * hy * Mouth( 0 ), 0 ),
			"jawdrop" => new Vec3( 0, 0, -0.20f * hz * LowerFace() ),
			"mouthdrop" => new Vec3( 0, 0, -0.30f * hz * LowerFace() ),
			"jawclencher" => new Vec3( -0.04f * hx * LowerFace(), 0, 0 ),
			"jawleft" => new Vec3( 0, 0.08f * hy * LowerFace(), 0 ),
			"jawright" => new Vec3( 0, -0.08f * hy * LowerFace(), 0 ),
			_ => z,
		};
	}

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );
		if ( targets.Count == 0 )
			Fail( "Expression pack needs a body", "No body matched the selection.", "Name a head body in Bodies, e.g. bodies=Head" );
		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in mesh.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
			var c = (min + max) / 2f;
			var hx = MathF.Max( (max.x - min.x) / 2f, 0.001f );
			var hy = MathF.Max( (max.y - min.y) / 2f, 0.001f );
			var hz = MathF.Max( (max.z - min.z) / 2f, 0.001f );
			foreach ( var (name, kind, side) in Keys )
			{
				var deltas = new Vec3[mesh.Positions.Count];
				for ( var i = 0; i < mesh.Positions.Count; i++ )
				{
					var p = mesh.Positions[i];
					var u = (p.z - c.z) / hz;
					var f = (p.x - c.x) / hx;
					var l = (p.y - c.y) / hy;
					// Brow/lid/mouth zone helpers are centred on the named side for stereo
					// keys: shift the lateral centre so a _L key peaks on the left brow.
					var lc = side == 0 ? 0f : side > 0 ? 0.35f : -0.35f;
					// Re-centre zone helpers by folding lc into l for brow/lid kinds only:
					// the Delta helpers already take lc implicitly via the call sites below
					// through the side factor, so keep l as-is and scale by SideFactor.
					_ = lc;
					var d = Delta( kind, u, f, l, hz, hy, hx ) * SideFactor( l, side ) * Strength.Clamped;
					deltas[i] = d;
				}
				mesh.Morphs.RemoveAll( m => m.Name == name );
				mesh.Morphs.Add( new MeshMorph { Name = name, Deltas = deltas, Driver = null } );
			}
		}
	}
}

/// <summary>
/// FaceSkin: anatomy-keyed surface detail as morphs — crow's feet, forehead lines,
/// nasolabial folds, tear troughs, lip texture and nose pores — with deliberate
/// left/right asymmetry (right side at 85% amplitude, different phase), so detail is
/// not a mirrored copy. Geometry detail only: pair it with a Paint layer for colour,
/// because height alone does not read in even light (the proven Pip lesson).
/// </summary>
public sealed class FaceSkinFeature : Feature
{
	public override string TypeName => "FaceSkin";
	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam Strength = new( "Strength", 1f, 0f, 3f );
	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, Strength };

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );
		if ( targets.Count == 0 )
			Fail( "Face skin needs a body", "No body matched the selection.", "Name a head body in Bodies, e.g. bodies=Head" );
		foreach ( var body in targets )
		{
			var mesh = body.Mesh;
			var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
			var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
			foreach ( var p in mesh.Positions )
			{
				min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
				max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			}
			var c = (min + max) / 2f;
			var hx = MathF.Max( (max.x - min.x) / 2f, 0.001f );
			var hy = MathF.Max( (max.y - min.y) / 2f, 0.001f );
			var hz = MathF.Max( (max.z - min.z) / 2f, 0.001f );
			void Add( string name, Func<float, float, float, float, int, Vec3> fn )
			{
				var deltas = new Vec3[mesh.Positions.Count];
				for ( var i = 0; i < mesh.Positions.Count; i++ )
				{
					var p = mesh.Positions[i];
					deltas[i] = fn( (p.z - c.z) / hz, (p.x - c.x) / hx, (p.y - c.y) / hy, 1f, i ) * Strength.Clamped;
				}
				mesh.Morphs.RemoveAll( m => m.Name == name );
				mesh.Morphs.Add( new MeshMorph { Name = name, Deltas = deltas, Driver = null } );
			}
			// Crow's feet: radiating ridges from each outer eye corner, asymmetric phase.
			Add( "skin_crowsfeet_L", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, 0.15f, 0.12f, 0.75f, 0.3f, 0.55f, 0.2f );
				return new Vec3( 0.03f * hx * w * MathF.Sin( (u * 9f + l * 7f) * 3f ), 0, 0 );
			} );
			Add( "skin_crowsfeet_R", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, 0.15f, 0.12f, 0.75f, 0.3f, -0.55f, 0.2f );
				return new Vec3( 0.025f * hx * w * MathF.Sin( (u * 9f + l * 7f) * 3f + 1.3f ), 0, 0 );
			} );
			Add( "skin_forehead", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, 0.5f, 0.2f, 0.7f, 0.4f, 0f, 0.7f );
				return new Vec3( 0.02f * hx * w * MathF.Sin( u * 28f ), 0, 0 );
			} );
			Add( "skin_nasolabial_L", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, -0.25f, 0.18f, 0.7f, 0.35f, 0.4f, 0.15f );
				return new Vec3( -0.025f * hx * w, 0, 0 );
			} );
			Add( "skin_nasolabial_R", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, -0.25f, 0.18f, 0.7f, 0.35f, -0.4f, 0.15f );
				return new Vec3( -0.021f * hx * w, 0, 0 );
			} );
			Add( "skin_teartrough_L", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, -0.02f, 0.08f, 0.75f, 0.3f, 0.35f, 0.2f );
				return new Vec3( -0.02f * hx * w, 0, 0 );
			} );
			Add( "skin_teartrough_R", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, -0.02f, 0.08f, 0.75f, 0.3f, -0.35f, 0.2f );
				return new Vec3( -0.017f * hx * w, 0, 0 );
			} );
			Add( "skin_liptexture", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, -0.35f, 0.12f, 0.75f, 0.35f, 0f, 0.4f );
				return new Vec3( 0.012f * hx * w * MathF.Sin( l * 40f ), 0, 0 );
			} );
			Add( "skin_nosepores", (u, f, l, s, i) =>
			{
				var w = FaceMesh.Zone( u, f, l, -0.05f, 0.15f, 0.8f, 0.3f, 0f, 0.25f );
				var hsh = MathF.Sin( i * 12.9898f ) * 43758.5453f;
				hsh -= MathF.Floor( hsh );
				return new Vec3( 0.015f * hx * w * (hsh - 0.5f) * 2f, 0, 0 );
			} );
		}
	}
}

/// <summary>
/// FaceCheck: the face gate. Proportions, eye symmetry (gaze proxy: both eye centres
/// at the same height and forward depth), mouth width vs inner-eye distance, and a
/// face-mode topology gate — any pole (valence 3 or 6+), triangle or n-gon whose centre
/// falls in an eye or mouth zone fails the turn naming the zone. A clean face passes
/// with a full report as a warning, so the report is visible in the build log.
/// Zones, in head bounds (forward +X, up +Z): eye zone front &gt; 0.6, up 0.05..0.5,
/// lateral 0.1..0.8; mouth zone front &gt; 0.6, up -0.6..-0.2, lateral &lt; 0.5.
/// </summary>
public sealed class FaceCheckFeature : Feature
{
	public override string TypeName => "FaceCheck";
	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly FloatParam SymmetryTol = new( "Symmetry tolerance", 0.05f, 0.001f, 0.5f );
	public override IReadOnlyList<IParam> Parameters => new IParam[] { Bodies, SymmetryTol };

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );
		if ( targets.Count == 0 )
			Fail( "Face check needs a body", "No body matched the selection.", "Name a head body in Bodies, e.g. bodies=Head" );
		var head = targets[0];
		var mesh = head.Mesh;
		var min = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var max = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}
		var c = (min + max) / 2f;
		var hx = MathF.Max( (max.x - min.x) / 2f, 0.001f );
		var hy = MathF.Max( (max.y - min.y) / 2f, 0.001f );
		var hz = MathF.Max( (max.z - min.z) / 2f, 0.001f );
		string ZoneOf( Vec3 p )
		{
			var u = (p.z - c.z) / hz;
			var f = (p.x - c.x) / hx;
			var l = MathF.Abs( (p.y - c.y) / hy );
			if ( f > 0.6f && u >= 0.05f && u <= 0.5f && l >= 0.1f && l <= 0.8f ) return "eye";
			if ( f > 0.6f && u >= -0.6f && u <= -0.2f && l <= 0.5f ) return "mouth";
			return null;
		}
		// Valence = distinct neighbours per vertex.
		var neighbours = new List<HashSet<int>>();
		for ( var i = 0; i < mesh.Positions.Count; i++ ) neighbours.Add( new HashSet<int>() );
		foreach ( var face in mesh.Faces )
			for ( var k = 0; k < face.Indices.Length; k++ )
			{
				var a = face.Indices[k];
				var b = face.Indices[(k + 1) % face.Indices.Length];
				if ( a >= 0 && a < neighbours.Count && b >= 0 && b < neighbours.Count )
				{
					neighbours[a].Add( b );
					neighbours[b].Add( a );
				}
			}
		var badPoles = new List<string>();
		var badFaces = new List<string>();
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var v = neighbours[i].Count;
			if ( v != 4 )
			{
				var z = ZoneOf( mesh.Positions[i] );
				if ( z is not null ) badPoles.Add( $"{z} pole v{i} valence {v}" );
			}
		}
		foreach ( var face in mesh.Faces )
		{
			if ( face.Indices.Length == 4 ) continue;
			var centre = Vec3.Zero;
			foreach ( var vi in face.Indices ) centre += mesh.Positions[vi];
			centre /= face.Indices.Length;
			var z = ZoneOf( centre );
			if ( z is not null ) badFaces.Add( $"{z} {face.Indices.Length}-gon" );
		}
		// Eye symmetry from eye bodies, if present.
		Body EyeBody( string n ) => ctx.Bodies.FirstOrDefault( b => b.Name == n );
		var eyeL = EyeBody( "eye_L" );
		var eyeR = EyeBody( "eye_R" );
		var eyeReport = "no eye_L/eye_R bodies in this build";
		var eyeBad = false;
		if ( eyeL?.Mesh is not null && eyeR?.Mesh is not null )
		{
			Vec3 CentreOf( Body b )
			{
				var s = Vec3.Zero;
				foreach ( var p in b.Mesh.Positions ) s += p;
				return s / Math.Max( 1, b.Mesh.Positions.Count );
			}
			var cl = CentreOf( eyeL );
			var cr = CentreOf( eyeR );
			var dz = MathF.Abs( cl.z - cr.z ) / (2f * hz);
			var dx = MathF.Abs( cl.x - cr.x ) / (2f * hx);
			var innerEye = MathF.Abs( cl.y - cr.y );
			eyeReport = $"eyes: centres y {cl.y:0.##}/{cr.y:0.##}, z diff {dz:0.###}, x diff {dx:0.###}, inner-eye distance {innerEye:0.##}";
			eyeBad = dz > SymmetryTol.Clamped || dx > SymmetryTol.Clamped;
		}
		var quads = mesh.Faces.Count( f => f.Indices.Length == 4 );
		var quadPct = mesh.Faces.Count == 0 ? 0 : 100f * quads / mesh.Faces.Count;
		var report = $"facecheck {head.Name}: bounds {(max - min).x:0.#}x{(max - min).y:0.#}x{(max - min).z:0.#}, quads {quadPct:0.#}%, {eyeReport}, " +
			$"morphs {mesh.ValidMorphs.Count()}, zone poles {badPoles.Count}, zone non-quads {badFaces.Count}";
		if ( badPoles.Count > 0 || badFaces.Count > 0 )
			Fail( "Face topology gate failed", $"{report}. Bad: {string.Join( "; ", badPoles.Take( 5 ) )} {string.Join( "; ", badFaces.Take( 5 ) )}", "Move poles out of the eye/mouth zones", "Use the Head base mesh, which places poles outside those zones" );
		if ( eyeBad )
			Fail( "Eye symmetry failed", report, "Place eye_L and eye_R at the same height and forward depth", "Mirror one eye onto the other" );
		Warn( "Face check passed", report, "This is a gate report, not a fault" );
	}
}
