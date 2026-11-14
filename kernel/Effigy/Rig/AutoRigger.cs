using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Heuristic auto-rigging from mesh bounds. Produces a Skeleton and weight maps.
/// The bot command is `rig auto` (BuildScriptRig.RigVerb): mesh in, Skeleton +
/// SkinWeights out, installed on the studio; see <see cref="TryRigStudio"/>.
/// The AiSuiteTools route is the existing `rig_command` tool, which already
/// allows the `rig` verb — no new tool is needed for the route itself.
///
/// TWO PATHS, chosen from the mesh's own proportions — never from its name:
///
///   humanoid — a tall, slender body whose longest span is a vertical axis
///     (Y or Z: the kernel is Z-up, the original 2-bone path used Y) and whose
///     height is at least 3x its depth. It gets a Citizen-named biped skeleton
///     (pelvis, spine_0..2, neck_0, head, clavicles, arm_upper/lower, hands,
///     leg_upper/lower, ankles, balls, both sides) placed from measured bounds,
///     with distance-based weights per limb segment. Bone names come through
///     <see cref="CitizenBoneMap.UnrealStyle"/> — the template is spelled in
///     Unreal-style source names and resolved to citizen names, so a rename in
///     the map renames the rig instead of silently stranding it.
///
///   bounds — everything else keeps the original 2-bone Root + Tip rig from the
///     mesh bounds. A quadruped's longest span is its body length (horizontal),
///     so it falls here instead of being force-fit into a biped.
///
/// HONEST LIMITS. The humanoid joints are placed from height fractions and one
/// measured torso width, not from landmarks: on a mesh with no real torso band
/// the shoulders sit at a guessed width. The facing direction is not measured
/// either — feet point along +facing, so a mesh facing the other way gets
/// mirrored feet. Weights are inverse-distance to the nearest bone segments,
/// pruned to the strongest 4 per vertex like export; they are NOT a heat
/// diffusion (see BindFeature, method Heat) and they are a runtime binding:
/// assigning them to a body's mesh does not survive a rebuild — follow
/// `rig auto` with `bind all` for weights that rebuild with the model.
/// </summary>
public static class AutoRigger
{
	public sealed class RigResult
	{
		public Skeleton Skeleton { get; set; } = new Skeleton();
		public SkinWeights Weights { get; set; } = new SkinWeights();

		/// <summary>"humanoid" or "bounds": which path built this rig.</summary>
		public string Template { get; set; } = "bounds";

		public bool Humanoid => Template == "humanoid";

		public string Summary =>
			$"{Template}: {Skeleton.Count} bones ({string.Join( ", ", Skeleton.Bones.Select( b => b.Name ) )}); " +
			$"{Weights.Count} vertices weighted";
	}

	/// <summary>
	/// Rig a mesh: the humanoid template when its proportions fit, else the
	/// 2-bone bounds rig. Throws <see cref="ArgumentException"/> on an empty or
	/// degenerate mesh instead of returning a silent empty rig — use
	/// <see cref="TryRig"/> when the caller needs the reason as data.
	/// </summary>
	public static RigResult Rig( PolyMesh mesh )
	{
		if ( !TryRig( mesh, out var result, out var why ) )
			throw new ArgumentException( why, nameof( mesh ) );

		return result;
	}

	/// <summary>As <see cref="Rig"/>, with the failure reason as data instead of an exception.</summary>
	public static bool TryRig( PolyMesh mesh, out RigResult result, out string why )
	{
		result = new RigResult();

		if ( !CheckMesh( mesh, out why ) )
			return false;

		result = RigHumanoid( mesh ) ?? RigBounds( mesh );
		why = null;
		return true;
	}

	/// <summary>
	/// The humanoid template path on its own: a Citizen-named biped placed from
	/// the mesh bounds, or null when the proportions do not fit a biped (a
	/// quadruped, a prop, a lying-down body). Throws on an empty or degenerate
	/// mesh — null means "not a biped", not "no mesh".
	/// </summary>
	public static RigResult? RigHumanoid( PolyMesh mesh )
	{
		if ( !CheckMesh( mesh, out var why ) )
			throw new ArgumentException( why, nameof( mesh ) );

		if ( !TryMeasure( mesh, out var min, out var max, out var center, out var vertical, out var lateral, out var facing ) )
			return null;

		var span = Span( min, max );
		var height = Comp( span, vertical );
		var depth = MathF.Min( Comp( span, lateral ), Comp( span, facing ) );

		// Slender in depth, not in arm span: a T-posed biped is as wide as it is
		// tall, so width cannot be the test — depth is what separates a body
		// standing up from one lying down or on four legs.
		if ( vertical == 0 || height < 3f * depth )
			return null;

		var torsoHalf = TorsoHalfWidth( mesh, min, center, vertical, lateral, height );
		var up = AxisVec( vertical );

		// Laterality: the feet point along +facing, so the character faces
		// +facing with up +up — and their left hand is on the up x facing
		// side of the lateral axis (Z-up, facing +Y: left is -X). The
		// template used to put L at +lateral, which is the character's
		// right: a left-hand vertex was weighted to hand_R. Derive the
		// sign from the axes so it holds on any axis assignment, not
		// just Z-up. (up, facing and lateral are three distinct axes,
		// so up x facing is exactly +/- the lateral axis.)
		var leftSign = Vec3.Dot( Vec3.Cross( up, AxisVec( facing ) ), AxisVec( lateral ) ) < 0f ? -1f : 1f;

		// Joint heights as fractions of the measured height from the bottom.
		// (pelvis, spine, neck, head) follow the usual biped stack; limbs hang
		// from the shoulder/hip lines the torso band measured.
		var at = P( min, center, vertical, lateral, facing, height );
		var sh = 0.80f * torsoHalf; // shoulder lateral offset
		var hip = 0.32f * torsoHalf; // hip lateral offset
		var head = new Dictionary<string, (Vec3 Head, Vec3 Tail)>( StringComparer.Ordinal )
		{
			["pelvis"] = (at( 0, 0, 0.50f ), at( 0, 0, 0.58f )),
			["spine_01"] = (at( 0, 0, 0.58f ), at( 0, 0, 0.66f )),
			["spine_02"] = (at( 0, 0, 0.66f ), at( 0, 0, 0.74f )),
			["spine_03"] = (at( 0, 0, 0.74f ), at( 0, 0, 0.82f )),
			["neck"] = (at( 0, 0, 0.82f ), at( 0, 0, 0.88f )),
			["head"] = (at( 0, 0, 0.88f ), at( 0, 0, 1.00f )),
		};

		foreach ( var side in new[] { "L", "R" } )
		{
			// L is the up x facing side (see leftSign above); R is the other one.
			var s = side == "L" ? leftSign : -leftSign;
			head[$"clavicle_{side}"] = (at( 0, 0, 0.80f ), at( s * sh, 0, 0.80f ));
			head[$"upperarm_{side}"] = (at( s * sh, 0, 0.80f ), at( s * 0.95f * torsoHalf, 0, 0.62f ));
			head[$"forearm_{side}"] = (at( s * 0.95f * torsoHalf, 0, 0.62f ), at( s * 1.05f * torsoHalf, 0, 0.44f ));
			head[$"hand_{side}"] = (at( s * 1.05f * torsoHalf, 0, 0.44f ), at( s * 1.05f * torsoHalf, 0, 0.38f ));
			head[$"thigh_{side}"] = (at( s * hip, 0, 0.50f ), at( s * hip, 0, 0.26f ));
			head[$"calf_{side}"] = (at( s * hip, 0, 0.26f ), at( s * hip, 0, 0.04f ));
			head[$"foot_{side}"] = (at( s * hip, 0, 0.04f ), at( s * hip, 0.05f * height, 0.01f ));
			head[$"ball_{side}"] = (at( s * hip, 0.05f * height, 0.01f ), at( s * hip, 0.11f * height, 0.005f ));
		}

		var map = CitizenBoneMap.UnrealStyle();
		string Citizen( string source ) => map.TryGetValue( source, out var citizen )
			? citizen
			: throw new InvalidOperationException( $"CitizenBoneMap has no citizen name for template bone '{source}'." );

		var result = new RigResult { Template = "humanoid" };
		var rig = result.Skeleton;
		var indexBySource = new Dictionary<string, int>( StringComparer.Ordinal );

		foreach ( var (bone, parent) in CitizenBoneMap.HumanoidTemplateBones() )
		{
			var (h, t) = head[bone];
			var parentIndex = parent.Length == 0 ? -1 : indexBySource[parent];
			indexBySource[bone] = rig.AddBoneFromPoints( Citizen( bone ), parentIndex, h, t, up );
		}

		rig.RootBone = "pelvis";
		rig.HipsBone = "pelvis";

		result.Weights = WeightsBySegment( mesh, rig, height );
		return result;
	}

	/// <summary>
	/// Whether this mesh reads as a biped by proportions. False for null, empty
	/// and degenerate meshes as well as for non-bipeds.
	/// </summary>
	public static bool LooksHumanoid( PolyMesh mesh )
	{
		if ( !CheckMesh( mesh, out _ ) )
			return false;

		if ( !TryMeasure( mesh, out var min, out var max, out _, out var vertical, out var lateral, out var facing ) )
			return false;

		var span = Span( min, max );
		return vertical != 0 && Comp( span, vertical ) >= 3f * MathF.Min( Comp( span, lateral ), Comp( span, facing ) );
	}

	/// <summary>
	/// Bot-command entry point for `rig auto`: rig one body of a studio and
	/// install the result — the skeleton into <c>studio.Rig</c> (persisted by
	/// StudioDocument), the weights onto the body's mesh (a runtime binding;
	/// follow with `bind all` for weights that survive a rebuild).
	/// <paramref name="bodyName"/> names the body, or null / "all" for the
	/// studio's only body. Refuses honestly when the studio has no (or more
	/// than one unnamed) body, when the mesh is empty/degenerate, and when the
	/// studio already has bones unless <paramref name="replace"/> is set.
	/// Weights are indexed by the installed skeleton's bone order, which is
	/// the result skeleton's order appended onto an empty (or replaced) rig.
	/// </summary>
	public static bool TryRigStudio( PartStudio studio, string bodyName, bool replace, out RigResult result, out string why )
	{
		result = new RigResult();

		if ( studio is null )
		{
			why = "Auto-rig needs a studio.";
			return false;
		}

		if ( studio.Rig.Count > 0 && !replace )
		{
			why = $"The model already has {studio.Rig.Count} bones. Add replace=1 to start over, or run `rig clear` first.";
			return false;
		}

		Body body;
		if ( string.IsNullOrWhiteSpace( bodyName ) || bodyName.Equals( "all", StringComparison.OrdinalIgnoreCase ) )
		{
			var withMesh = studio.Bodies.Where( b => b?.Mesh is { VertexCount: > 0 } ).ToList();

			if ( withMesh.Count == 0 )
			{
				why = "Auto-rig needs a body with a mesh. The studio has no bodies yet.";
				return false;
			}

			if ( withMesh.Count > 1 )
			{
				why = $"Auto-rig needs one body named: the studio has {withMesh.Count} bodies ({string.Join( ", ", withMesh.Select( b => b.Name ?? b.Id ) )}).";
				return false;
			}

			body = withMesh[0];
		}
		else
		{
			// A named lookup on an empty studio must say the studio is empty, not
			// that the name is wrong: "No body called 'Body'. Bodies: ." names a
			// body that never existed and lists nothing, which reads as a typo
			// instead of the real refusal. Same wording as the unnamed path.
			if ( studio.Bodies.Count == 0 )
			{
				why = "Auto-rig needs a body with a mesh. The studio has no bodies yet.";
				return false;
			}

			body = ModelMeasure.FindBody( studio, bodyName );

			if ( body is null )
			{
				why = $"No body called '{bodyName}'. Bodies: {string.Join( ", ", studio.Bodies.Select( b => b.Name ?? b.Id ) )}.";
				return false;
			}
		}

		if ( !TryRig( body.Mesh, out result, out why ) )
		{
			why = $"'{body.Name ?? body.Id}': {why}";
			return false;
		}

		if ( replace )
		{
			studio.Rig.Bones.Clear();

			foreach ( var key in studio.BodyBoneMap.Where( kv => studio.Rig.IndexOf( kv.Value ) < 0 ).Select( kv => kv.Key ).ToList() )
				studio.BodyBoneMap.Remove( key );
		}

		try
		{
			RigBuild.Append( studio.Rig, result.Skeleton );
		}
		catch ( ArgumentException e )
		{
			why = e.Message;
			return false;
		}

		if ( result.Skeleton.RootBone.Length > 0 )
			studio.Rig.RootBone = result.Skeleton.RootBone;

		if ( result.Skeleton.HipsBone.Length > 0 )
			studio.Rig.HipsBone = result.Skeleton.HipsBone;

		body.Mesh.Skin = result.Weights;
		why = null;
		return true;
	}

	// --- validation ---------------------------------------------------------------------------

	static bool CheckMesh( PolyMesh mesh, out string why )
	{
		why = null;

		if ( mesh is null )
		{
			why = "Auto-rig needs a mesh.";
			return false;
		}

		if ( mesh.Positions.Count == 0 )
		{
			why = "Auto-rig needs a mesh with vertices: the mesh is empty.";
			return false;
		}

		if ( mesh.FaceCount == 0 )
		{
			why = "Auto-rig needs a mesh with faces: vertices alone have no surface to weight.";
			return false;
		}

		var min = mesh.Positions[0];
		var max = mesh.Positions[0];

		foreach ( var p in mesh.Positions )
		{
			if ( !float.IsFinite( p.x ) || !float.IsFinite( p.y ) || !float.IsFinite( p.z ) )
			{
				why = "Auto-rig needs finite vertex positions: the mesh has a NaN or infinite coordinate.";
				return false;
			}

			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
		}

		if ( (max - min).LengthSquared < 1e-12f )
		{
			why = "Auto-rig needs a mesh with some size: every vertex is at the same point.";
			return false;
		}

		return true;
	}

	// --- measuring ----------------------------------------------------------------------------

	static bool TryMeasure( PolyMesh mesh, out Vec3 min, out Vec3 max, out Vec3 center, out int vertical, out int lateral, out int facing )
	{
		min = mesh.Positions[0];
		max = mesh.Positions[0];
		center = Vec3.Zero;

		foreach ( var p in mesh.Positions )
		{
			min = new Vec3( MathF.Min( min.x, p.x ), MathF.Min( min.y, p.y ), MathF.Min( min.z, p.z ) );
			max = new Vec3( MathF.Max( max.x, p.x ), MathF.Max( max.y, p.y ), MathF.Max( max.z, p.z ) );
			center += p;
		}

		center /= mesh.Positions.Count;

		var span = Span( min, max );
		vertical = LongAxis( span );

		// Lateral is the wider of the two horizontal axes (arm-span direction);
		// facing is the other one.
		var h0 = vertical == 0 ? 1 : 0;
		var h1 = vertical == 2 ? 1 : 2;
		lateral = Comp( span, h0 ) >= Comp( span, h1 ) ? h0 : h1;
		facing = lateral == h0 ? h1 : h0;
		return true;
	}

	static int LongAxis( Vec3 span )
	{
		if ( span.x >= span.y && span.x >= span.z ) return 0;
		if ( span.y >= span.z ) return 1;
		return 2;
	}

	static Vec3 Span( Vec3 min, Vec3 max ) => max - min;

	static float Comp( Vec3 v, int axis ) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;

	static Vec3 AxisVec( int axis ) => axis == 0 ? new Vec3( 1, 0, 0 ) : axis == 1 ? new Vec3( 0, 1, 0 ) : new Vec3( 0, 0, 1 );

	static Vec3 SetAxis( Vec3 p, int axis, float value ) =>
		axis == 0 ? new Vec3( value, p.y, p.z ) : axis == 1 ? new Vec3( p.x, value, p.z ) : new Vec3( p.x, p.y, value );

	/// <summary>
	/// Half the torso's lateral width, measured from the vertices in the torso
	/// band (45–65% of the height) so hanging arms do not set it. Falls back to
	/// a quarter of the height when the band is empty.
	/// </summary>
	static float TorsoHalfWidth( PolyMesh mesh, Vec3 min, Vec3 center, int vertical, int lateral, float height )
	{
		var lo = Comp( min, vertical ) + 0.45f * height;
		var hi = Comp( min, vertical ) + 0.65f * height;
		var c = Comp( center, lateral );
		var w = 0f;

		foreach ( var p in mesh.Positions )
		{
			var v = Comp( p, vertical );

			if ( v >= lo && v <= hi )
				w = MathF.Max( w, MathF.Abs( Comp( p, lateral ) - c ) );
		}

		return w > 1e-6f ? w : 0.25f * height;
	}

	/// <summary>Point builder: lateral/facing offsets in world units from the body centre, height as a fraction of the measured height.</summary>
	static Func<float, float, float, Vec3> P( Vec3 min, Vec3 center, int vertical, int lateral, int facing, float height ) =>
		( lat, face, frac ) =>
		{
			var v = Comp( min, vertical ) + frac * height;
			return SetAxis( SetAxis( SetAxis( Vec3.Zero, lateral, Comp( center, lateral ) + lat ), facing, Comp( center, facing ) + face ), vertical, v );
		};

	// --- the 2-bone bounds rig (the original Rig, kept as the fallback) -----------------------

	static RigResult RigBounds( PolyMesh mesh )
	{
		var result = new RigResult { Template = "bounds" };

		TryMeasure( mesh, out var min, out var max, out var center, out var vertical, out _, out _ );
		var span = Span( min, max );
		var height = Comp( span, vertical );

		var rootPoint = SetAxis( center, vertical, Comp( min, vertical ) );
		var tipPoint = SetAxis( center, vertical, Comp( max, vertical ) );

		var root = new Bone( "Root", -1, Xform.Identity, height * 0.5f );
		root.Local = new Xform( new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), rootPoint );
		var tip = new Bone( "Tip", 0, Xform.Identity, height * 0.5f );
		tip.Local = new Xform( new Vec3( 1, 0, 0 ), new Vec3( 0, 1, 0 ), new Vec3( 0, 0, 1 ), AxisVec( vertical ) * height );
		result.Skeleton.Bones.Add( root );
		result.Skeleton.Bones.Add( tip );
		result.Skeleton.RootBone = "Root";

		var w = new SkinWeights( mesh.Positions.Count );

		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var d0 = (mesh.Positions[i] - rootPoint).Length;
			var d1 = (mesh.Positions[i] - tipPoint).Length;
			w[i] = new[] { new BoneWeight( d0 <= d1 ? 0 : 1, 1f ) };
		}

		result.Weights = w;
		return result;
	}

	// --- weights --------------------------------------------------------------------------------

	/// <summary>
	/// Inverse-distance weights to the nearest bone segments, normalised to a
	/// partition of unity and pruned to the strongest 4 per vertex, like export.
	/// </summary>
	static SkinWeights WeightsBySegment( PolyMesh mesh, Skeleton rig, float height )
	{
		var falloff = 0.12f * height;
		var heads = new Vec3[rig.Count];
		var tails = new Vec3[rig.Count];

		for ( var b = 0; b < rig.Count; b++ )
		{
			heads[b] = rig.HeadWorld( b );
			tails[b] = rig.TailWorld( b );
		}

		var weights = new SkinWeights( mesh.Positions.Count );

		for ( var v = 0; v < mesh.Positions.Count; v++ )
		{
			var acc = new Dictionary<int, float>();

			for ( var b = 0; b < rig.Count; b++ )
			{
				var d = DistanceToSegment( mesh.Positions[v], heads[b], tails[b] );
				var q = d / falloff;
				acc[b] = 1f / (1f + q * q);
			}

			weights[v] = SkinWeights.Prune( SkinWeights.Finish( acc ), 4 );
		}

		return weights;
	}

	static float DistanceToSegment( Vec3 p, Vec3 a, Vec3 b )
	{
		var ab = b - a;
		var len2 = ab.LengthSquared;

		if ( len2 < 1e-12f )
			return (p - a).Length;

		var t = Math.Clamp( Vec3.Dot( p - a, ab ) / len2, 0f, 1f );
		return (p - (a + ab * t)).Length;
	}
}
