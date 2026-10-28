using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Blender-style SurfaceDeform / MeshDeform: bind accessory bodies to a cage body, deform the
/// cage, and carry the accessories with it — a pauldron riding a bending arm, a belt on a twisting
/// torso, a badge on a stretching chest.
///
/// WHY ONE FEATURE DEFORMS THE CAGE TOO: a history rebuild runs features in order, so a binding
/// recorded here cannot see a cage deformed by a LATER feature — by the time this feature runs,
/// the cage is still in its bind pose. The feature therefore does the whole move in one place:
/// bind to the cage as it stands, deform the cage by the cage-deform numbers, then carry each
/// accessory by its binding. The cage body in the studio IS deformed by this feature, and the
/// accessories follow it — that is the visible story, in one script line.
///
/// TWO MODES, DIFFERENT MATH (see CageBinding):
/// SurfaceDeform binds each accessory vertex to the nearest cage triangle (barycentric weights
/// plus a signed offset along the triangle normal) — right for accessories riding a surface.
/// MeshDeform binds each accessory vertex to its nearest cage vertices with inverse-distance
/// weights and carries it by their weighted displacement — right for a simplified proxy cage,
/// where the accessory may sit inside the cage volume far from any surface. It is NOT Blender's
/// harmonic-coordinate MeshDeform (no volumetric solve); that limit is stated, not hidden.
/// </summary>
public sealed class CageDeformFeature : Feature
{
	public override string TypeName => "CageDeform";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly BodySelectionParam Cage = new( "Cage" );

	/// <summary>How much of the follow to apply (1 = all the way).</summary>
	public readonly FloatParam Strength = new( "Strength", 1f, 0f, 1f );

	public readonly ChoiceParam Mode = new( "Mode", new[] { "SurfaceDeform", "MeshDeform" } );

	/// <summary>The deform applied to the cage itself (and therefore, through the binding, to the accessories).</summary>
	public readonly ChoiceParam CageMode = new( "Cage mode", new[] { "Twist", "Bend", "Taper", "Stretch" }, 1 );

	public readonly ChoiceParam CageAxis = new( "Cage axis", new[] { "X", "Y", "Z" }, 2 );

	/// <summary>Twist and Bend: the total turn over the band, in degrees.</summary>
	public readonly FloatParam CageAngle = new( "Cage angle", 45f, -3600f, 3600f, "deg" );

	/// <summary>Taper: how much the cross-section grows over the band. Stretch: how much longer the band gets.</summary>
	public readonly FloatParam CageFactor = new( "Cage factor", 0.5f, -10f, 10f );

	/// <summary>The band the cage deform acts over, as fractions of the cage's length along the axis.</summary>
	public readonly FloatParam CageLower = new( "Cage lower", 0f, 0f, 1f );

	public readonly FloatParam CageUpper = new( "Cage upper", 1f, 0f, 1f );

	/// <summary>SurfaceDeform only: vertices farther off the cage than this are left alone.</summary>
	public readonly FloatParam Reach = new( "Reach", 24f, 0.01f, 1000f, "u" );

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Cage, Strength, Mode, CageMode, CageAxis, CageAngle, CageFactor, CageLower, CageUpper, Reach };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { CageLower, CageUpper, Reach };

	protected override void Execute( FeatureContext ctx )
	{
		var cages = ctx.Bodies.Where( b => Cage.Matches( b ) && b.Mesh is { FaceCount: > 0 } ).ToList();

		if ( cages.Count != 1 )
		{
			FailOn( Cage.Label,
				"Pick exactly one cage body",
				$"The cage selection matches {cages.Count} bodies with faces; a binding needs exactly one.",
				"Pick the body the accessories should follow, e.g. the limb under the armour" );
		}

		var cage = cages[0];
		var moving = RequireBodies( ctx, Bodies ).Where( b => b != cage ).ToList();

		if ( moving.Count == 0 )
		{
			FailOn( Bodies.Label,
				"Nothing to carry",
				"Every selected body is also the cage.",
				"Pick the accessories apart from the cage" );
		}

		if ( CageUpper.Clamped <= CageLower.Clamped )
		{
			FailOn( "Cage upper",
				"The band is empty",
				$"The upper limit ({CageUpper.Clamped:0.###}) has to be above the lower one ({CageLower.Clamped:0.###}).",
				"Raise Cage upper, or lower Cage lower" );
		}

		// Bind FIRST, while the cage is still in its bind pose.
		var bindings = new List<(Body Body, CageBinding Binding)>();
		foreach ( var body in moving )
		{
			var binding = Mode.Index == 0
				? CageBinding.BindSurface( cage.Mesh, body.Mesh, Reach.Clamped )
				: CageBinding.BindCage( cage.Mesh, body.Mesh );
			bindings.Add( (body, binding) );
		}

		if ( bindings.All( b => b.Binding.BoundCount == 0 ) )
		{
			FailOn( "Reach",
				"Nothing bound to the cage",
				$"No accessory vertex is within {Reach.Clamped:0.##} of the cage.",
				"Raise Reach, or move the accessory onto the cage" );
		}

		// Then deform the cage, then carry the accessories by their bindings.
		DeformCage( cage.Mesh );

		foreach ( var (body, binding) in bindings )
		{
			binding.ApplyTo( body.Mesh, cage.Mesh, Strength.Clamped );

			if ( binding.UnboundCount > 0 )
			{
				Warn(
					"Some vertices stayed put",
					$"{binding.UnboundCount} of {body.Mesh.VertexCount} vertices on '{body.Name}' were beyond Reach and did not follow.",
					"Raise Reach, or move the accessory closer to the cage" );
			}
		}
	}

	void DeformCage( PolyMesh mesh )
	{
		var axis = CageAxis.Index switch
		{
			0 => new Vec3( 1f, 0f, 0f ),
			1 => new Vec3( 0f, 1f, 0f ),
			_ => new Vec3( 0f, 0f, 1f ),
		};

		var lo = new Vec3( float.MaxValue, float.MaxValue, float.MaxValue );
		var hi = new Vec3( float.MinValue, float.MinValue, float.MinValue );
		foreach ( var p in mesh.Positions )
		{
			lo = new Vec3( MathF.Min( lo.x, p.x ), MathF.Min( lo.y, p.y ), MathF.Min( lo.z, p.z ) );
			hi = new Vec3( MathF.Max( hi.x, p.x ), MathF.Max( hi.y, p.y ), MathF.Max( hi.z, p.z ) );
		}

		var centre = (lo + hi) * 0.5f;
		var pivotAlong = Vec3.Dot( centre, axis );
		var minAlong = Vec3.Dot( lo, axis );
		var maxAlong = Vec3.Dot( hi, axis );
		// NOTE: Dot with a negative axis component is not used here — CageAxis is always a
		// positive unit axis, so min/max along it come from the box corners directly.
		// The box-centre pivot keeps the middle of the cage still while the ends move.
		var span = maxAlong - minAlong;

		var settings = new SimpleDeformSettings
		{
			Mode = (SimpleDeformMode)Math.Clamp( CageMode.Index, 0, 3 ),
			Axis = axis,
			Toward = axis.x != 0f ? new Vec3( 0f, 1f, 0f ) : new Vec3( 1f, 0f, 0f ),
			Pivot = centre,
			Angle = CageAngle.Value * MathF.PI / 180f,
			Factor = CageFactor.Value,
			Lower = minAlong + CageLower.Clamped * span - pivotAlong,
			Upper = minAlong + CageUpper.Clamped * span - pivotAlong,
		};

		for ( var i = 0; i < mesh.Positions.Count; i++ )
			mesh.Positions[i] = SimpleDeform.Deform( mesh.Positions[i], settings );
	}
}
