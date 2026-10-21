#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Categories of skeletal and animation constraints found in control rigs (.ctrlrig).
/// </summary>
public enum ConstraintCategory
{
	/// <summary>Inverse kinematics (Two-bone, CCD, FABRIK, Spline).</summary>
	Ik,

	/// <summary>Secondary dynamics, soft chains, ropes, and tail springs.</summary>
	SecondarySpring,

	/// <summary>Euler rotational limits and joint clamps.</summary>
	Limit,

	/// <summary>Look-at / aim constraint pointing a bone toward a target.</summary>
	LookAt,

	/// <summary>Orientation copying from a source bone with weight scaling.</summary>
	CopyRotation,

	/// <summary>Full or partial transform copying (position, rotation, scale).</summary>
	CopyTransform,

	/// <summary>Pose-space corrective morph driver.</summary>
	MorphDriver,

	/// <summary>Root motion extraction and in-place motion constraint.</summary>
	RootMotion,

	/// <summary>Unsupported, engine-dependent, or unrecognized constraint.</summary>
	Unsupported
}

/// <summary>
/// Execution and runtime support status for constraints in the standalone desktop environment.
/// </summary>
public enum ConstraintSupportStatus
{
	/// <summary>Fully supported engine-free kernel solver (TwoBoneIk, ChainIk, ChainSpring, RigLimit, MorphDriver).</summary>
	StandaloneSupported,

	/// <summary>Supported via desktop constraint evaluation extensions (LookAt, CopyRotation, CopyTransform).</summary>
	DesktopExtended,

	/// <summary>Requires game engine runtime, physics scene, or GameObject infrastructure (e.g. rigid body ragdoll, cloth).</summary>
	EngineDependent,

	/// <summary>Unrecognized or missing constraint specification.</summary>
	Unsupported
}

/// <summary>
/// Individual constraint audit item detailing category, support status, and diagnostics.
/// </summary>
public sealed record ConstraintAuditItem
{
	public string Name { get; init; } = "";
	public string TargetBone { get; init; } = "";
	public string? SourceBone { get; init; }
	public ConstraintCategory Category { get; init; } = ConstraintCategory.Unsupported;
	public ConstraintSupportStatus SupportStatus { get; init; } = ConstraintSupportStatus.Unsupported;
	public bool IsSupported { get; init; }
	public string? Diagnostic { get; init; }
	public Dictionary<string, string> Properties { get; init; } = new( StringComparer.OrdinalIgnoreCase );
}

/// <summary>
/// Comprehensive audit result containing all categorized constraints, diagnostics, and support counts.
/// </summary>
public sealed class RigConstraintAuditResult
{
	public List<ConstraintAuditItem> Items { get; } = new();
	public List<string> Diagnostics { get; } = new();

	public int TotalCount => Items.Count;
	public int SupportedCount => Items.Count( i => i.IsSupported );
	public int UnsupportedCount => Items.Count( i => !i.IsSupported );
	public bool HasUnsupported => UnsupportedCount > 0;

	public IReadOnlyList<ConstraintAuditItem> GetByCategory( ConstraintCategory category ) =>
		Items.Where( i => i.Category == category ).ToList();

	public bool HasCategory( ConstraintCategory category ) =>
		Items.Any( i => i.Category == category );
}

/// <summary>
/// Programmatic inventory, validator, and evaluator for control rig constraints and secondary motion.
/// Provides deterministic evaluation for IK, limits, secondary springs, look-at, and copy transforms.
/// </summary>
public static class ConstraintParityInventory
{
	/// <summary>
	/// Audits a .ctrlrig JSON string, categorizing all declared constraints and reporting diagnostics.
	/// Gracefully handles malformed JSON or unknown constraint types without throwing unhandled exceptions.
	/// </summary>
	public static RigConstraintAuditResult AuditRigJson( string json )
	{
		var result = new RigConstraintAuditResult();
		if ( string.IsNullOrWhiteSpace( json ) )
		{
			result.Diagnostics.Add( "Rig definition JSON is empty or null." );
			return result;
		}

		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse( json );
		}
		catch ( Exception ex )
		{
			result.Diagnostics.Add( $"Malformed rig JSON: {ex.Message}" );
			return result;
		}

		using ( doc )
		{
			var root = doc.RootElement;
			if ( root.ValueKind != JsonValueKind.Object )
			{
				result.Diagnostics.Add( "Root element of .ctrlrig must be a JSON object." );
				return result;
			}

			// 1. IK Constraints
			if ( root.TryGetProperty( "IkConstraints", out var ikArray ) && ikArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in ikArray.EnumerateArray() )
				{
					string name = GetString( e, "Name" ) ?? "IK";
					string target = GetString( e, "TargetBone" ) ?? GetString( e, "EndBone" ) ?? "";
					int chainLength = GetInt( e, "ChainLength" ) ?? 2;
					string solver = GetString( e, "Solver" ) ?? "Ccd";
					float weight = GetFloat( e, "Weight" ) ?? 1f;
					bool enabled = GetBool( e, "Enabled" ) ?? true;

					var item = new ConstraintAuditItem
					{
						Name = name,
						TargetBone = target,
						Category = ConstraintCategory.Ik,
						SupportStatus = ConstraintSupportStatus.StandaloneSupported,
						IsSupported = true,
						Properties =
						{
							["ChainLength"] = chainLength.ToString(),
							["Solver"] = solver,
							["Weight"] = weight.ToString(),
							["Enabled"] = enabled.ToString()
						}
					};

					if ( string.IsNullOrWhiteSpace( target ) )
					{
						result.Diagnostics.Add( $"IK constraint '{name}' has empty target bone." );
					}
					result.Items.Add( item );
				}
			}

			// 2. Limit Constraints
			if ( root.TryGetProperty( "LimitConstraints", out var limitArray ) && limitArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in limitArray.EnumerateArray() )
				{
					string name = GetString( e, "Name" ) ?? "Limit";
					string target = GetString( e, "TargetBone" ) ?? GetString( e, "Bone" ) ?? "";
					string min = GetString( e, "MinAngles" ) ?? "-45,-45,-45";
					string max = GetString( e, "MaxAngles" ) ?? "45,45,45";
					float weight = GetFloat( e, "Weight" ) ?? 1f;

					result.Items.Add( new ConstraintAuditItem
					{
						Name = name,
						TargetBone = target,
						Category = ConstraintCategory.Limit,
						SupportStatus = ConstraintSupportStatus.StandaloneSupported,
						IsSupported = true,
						Properties =
						{
							["MinAngles"] = min,
							["MaxAngles"] = max,
							["Weight"] = weight.ToString()
						}
					} );
				}
			}

			// 3. Secondary Springs / Soft Chains
			if ( (root.TryGetProperty( "SoftChains", out var softArray ) || root.TryGetProperty( "SecondarySprings", out softArray ))
			     && softArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in softArray.EnumerateArray() )
				{
					string name = GetString( e, "Name" ) ?? "SoftChain";
					string bone = GetString( e, "Bone" ) ?? GetString( e, "TargetBone" ) ?? "";
					string preset = GetString( e, "Preset" ) ?? "Loose";
					float stiffness = GetFloat( e, "Stiffness" ) ?? 120f;
					float damping = GetFloat( e, "Damping" ) ?? 0.001f;
					bool rope = GetBool( e, "Rope" ) ?? false;

					result.Items.Add( new ConstraintAuditItem
					{
						Name = name,
						TargetBone = bone,
						Category = ConstraintCategory.SecondarySpring,
						SupportStatus = ConstraintSupportStatus.StandaloneSupported,
						IsSupported = true,
						Properties =
						{
							["Preset"] = preset,
							["Stiffness"] = stiffness.ToString(),
							["Damping"] = damping.ToString(),
							["Rope"] = rope.ToString()
						}
					} );
				}
			}

			// 3b. Soft Colliders
			if ( root.TryGetProperty( "SoftColliders", out var colliderArray ) && colliderArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in colliderArray.EnumerateArray() )
				{
					string bone = GetString( e, "Bone" ) ?? "";
					float radius = GetFloat( e, "Radius" ) ?? 4f;

					result.Items.Add( new ConstraintAuditItem
					{
						Name = $"SoftCollider_{bone}",
						TargetBone = bone,
						Category = ConstraintCategory.SecondarySpring,
						SupportStatus = ConstraintSupportStatus.StandaloneSupported,
						IsSupported = true,
						Properties = { ["Radius"] = radius.ToString() }
					} );
				}
			}

			// 4. Look-At Constraints
			if ( (root.TryGetProperty( "LookAtConstraints", out var lookAtArray ) || root.TryGetProperty( "AimConstraints", out lookAtArray ))
			     && lookAtArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in lookAtArray.EnumerateArray() )
				{
					string name = GetString( e, "Name" ) ?? "LookAt";
					string target = GetString( e, "TargetBone" ) ?? GetString( e, "Bone" ) ?? "";
					string source = GetString( e, "SourceBone" ) ?? GetString( e, "LookAtTarget" ) ?? "";
					float weight = GetFloat( e, "Weight" ) ?? 1f;

					result.Items.Add( new ConstraintAuditItem
					{
						Name = name,
						TargetBone = target,
						SourceBone = source,
						Category = ConstraintCategory.LookAt,
						SupportStatus = ConstraintSupportStatus.DesktopExtended,
						IsSupported = true,
						Properties = { ["Weight"] = weight.ToString() }
					} );
				}
			}

			// 5. Copy Rotation Constraints
			if ( root.TryGetProperty( "CopyRotationConstraints", out var copyRotArray ) && copyRotArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in copyRotArray.EnumerateArray() )
				{
					string name = GetString( e, "Name" ) ?? "CopyRotation";
					string target = GetString( e, "TargetBone" ) ?? "";
					string source = GetString( e, "SourceBone" ) ?? "";
					float weight = GetFloat( e, "Weight" ) ?? 1f;

					result.Items.Add( new ConstraintAuditItem
					{
						Name = name,
						TargetBone = target,
						SourceBone = source,
						Category = ConstraintCategory.CopyRotation,
						SupportStatus = ConstraintSupportStatus.DesktopExtended,
						IsSupported = true,
						Properties = { ["Weight"] = weight.ToString() }
					} );
				}
			}

			// 6. Copy Transform Constraints
			if ( root.TryGetProperty( "CopyTransformConstraints", out var copyTxArray ) && copyTxArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in copyTxArray.EnumerateArray() )
				{
					string name = GetString( e, "Name" ) ?? "CopyTransform";
					string target = GetString( e, "TargetBone" ) ?? "";
					string source = GetString( e, "SourceBone" ) ?? "";
					float weight = GetFloat( e, "Weight" ) ?? 1f;

					result.Items.Add( new ConstraintAuditItem
					{
						Name = name,
						TargetBone = target,
						SourceBone = source,
						Category = ConstraintCategory.CopyTransform,
						SupportStatus = ConstraintSupportStatus.DesktopExtended,
						IsSupported = true,
						Properties = { ["Weight"] = weight.ToString() }
					} );
				}
			}

			// 7. Morph Drivers
			if ( root.TryGetProperty( "MorphDrivers", out var driverArray ) && driverArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in driverArray.EnumerateArray() )
				{
					string morph = GetString( e, "Morph" ) ?? "";
					string bone = GetString( e, "Bone" ) ?? "";
					string channel = GetString( e, "Channel" ) ?? "Rotation";

					result.Items.Add( new ConstraintAuditItem
					{
						Name = $"Driver_{morph}",
						TargetBone = bone,
						Category = ConstraintCategory.MorphDriver,
						SupportStatus = ConstraintSupportStatus.StandaloneSupported,
						IsSupported = true,
						Properties = { ["Morph"] = morph, ["Channel"] = channel }
					} );
				}
			}

			// 8. Root Motion
			if ( root.TryGetProperty( "RootMotion", out var rmProp ) && rmProp.ValueKind == JsonValueKind.Object )
			{
				string bone = GetString( rmProp, "Bone" ) ?? "root";
				string mode = GetString( rmProp, "Mode" ) ?? "XY";
				result.Items.Add( new ConstraintAuditItem
				{
					Name = "RootMotion",
					TargetBone = bone,
					Category = ConstraintCategory.RootMotion,
					SupportStatus = ConstraintSupportStatus.StandaloneSupported,
					IsSupported = true,
					Properties = { ["Mode"] = mode }
				} );
			}

			// 9. Generic / Heterogeneous Constraints array (including unsupported or future entries)
			if ( root.TryGetProperty( "Constraints", out var genericArray ) && genericArray.ValueKind == JsonValueKind.Array )
			{
				foreach ( var e in genericArray.EnumerateArray() )
				{
					string type = GetString( e, "Type" ) ?? GetString( e, "Kind" ) ?? "Unknown";
					string name = GetString( e, "Name" ) ?? type;
					string target = GetString( e, "TargetBone" ) ?? GetString( e, "Bone" ) ?? "";
					string source = GetString( e, "SourceBone" ) ?? "";

					var category = CategorizeConstraintType( type );
					bool isSupported = category switch
					{
						ConstraintCategory.Ik or ConstraintCategory.Limit or ConstraintCategory.SecondarySpring or
						ConstraintCategory.MorphDriver or ConstraintCategory.RootMotion => true,
						ConstraintCategory.LookAt or ConstraintCategory.CopyRotation or ConstraintCategory.CopyTransform => true,
						_ => false
					};

					var status = isSupported
						? (category is ConstraintCategory.LookAt or ConstraintCategory.CopyRotation or ConstraintCategory.CopyTransform
							? ConstraintSupportStatus.DesktopExtended
							: ConstraintSupportStatus.StandaloneSupported)
						: (type.Contains( "Physics", StringComparison.OrdinalIgnoreCase ) || type.Contains( "Cloth", StringComparison.OrdinalIgnoreCase )
							? ConstraintSupportStatus.EngineDependent
							: ConstraintSupportStatus.Unsupported);

					string? diagnostic = isSupported
						? null
						: $"Constraint '{name}' of type '{type}' on bone '{target}' is not supported in standalone engine-free mode ({status}).";

					if ( diagnostic != null )
					{
						result.Diagnostics.Add( diagnostic );
					}

					result.Items.Add( new ConstraintAuditItem
					{
						Name = name,
						TargetBone = target,
						SourceBone = source,
						Category = category,
						SupportStatus = status,
						IsSupported = isSupported,
						Diagnostic = diagnostic
					} );
				}
			}

			// 10. Audit for unrecognized top-level constraint arrays or unsupported physics properties
			foreach ( var prop in root.EnumerateObject() )
			{
				if ( prop.Name.EndsWith( "Constraint", StringComparison.OrdinalIgnoreCase ) ||
				     prop.Name.EndsWith( "Constraints", StringComparison.OrdinalIgnoreCase ) )
				{
					string propName = prop.Name;
					if ( propName is not ("IkConstraints" or "LimitConstraints" or "SoftChains" or "SecondarySprings" or
					                      "LookAtConstraints" or "AimConstraints" or "CopyRotationConstraints" or
					                      "CopyTransformConstraints" or "Constraints") )
					{
						string diag = $"Control rig contains unsupported constraint block '{propName}'.";
						result.Diagnostics.Add( diag );
						result.Items.Add( new ConstraintAuditItem
						{
							Name = propName,
							Category = ConstraintCategory.Unsupported,
							SupportStatus = ConstraintSupportStatus.Unsupported,
							IsSupported = false,
							Diagnostic = diag
						} );
					}
				}
			}
		}

		return result;
	}

	/// <summary>
	/// Audits a .ctrlrig file at the specified filesystem path.
	/// </summary>
	public static RigConstraintAuditResult AuditRigFile( string path )
	{
		var result = new RigConstraintAuditResult();
		if ( string.IsNullOrWhiteSpace( path ) || !File.Exists( path ) )
		{
			result.Diagnostics.Add( $"Rig file not found: '{path}'." );
			return result;
		}

		try
		{
			string json = File.ReadAllText( path );
			return AuditRigJson( json );
		}
		catch ( Exception ex )
		{
			result.Diagnostics.Add( $"Failed to read rig file '{path}': {ex.Message}" );
			return result;
		}
	}

	static ConstraintCategory CategorizeConstraintType( string type )
	{
		if ( type.Contains( "LookAt", StringComparison.OrdinalIgnoreCase ) || type.Contains( "Aim", StringComparison.OrdinalIgnoreCase ) )
			return ConstraintCategory.LookAt;
		if ( type.Contains( "CopyRotation", StringComparison.OrdinalIgnoreCase ) )
			return ConstraintCategory.CopyRotation;
		if ( type.Contains( "CopyTransform", StringComparison.OrdinalIgnoreCase ) )
			return ConstraintCategory.CopyTransform;
		if ( type.Contains( "Ik", StringComparison.OrdinalIgnoreCase ) )
			return ConstraintCategory.Ik;
		if ( type.Contains( "Spring", StringComparison.OrdinalIgnoreCase ) || type.Contains( "Soft", StringComparison.OrdinalIgnoreCase ) )
			return ConstraintCategory.SecondarySpring;
		if ( type.Contains( "Limit", StringComparison.OrdinalIgnoreCase ) )
			return ConstraintCategory.Limit;
		if ( type.Contains( "Morph", StringComparison.OrdinalIgnoreCase ) || type.Contains( "Driver", StringComparison.OrdinalIgnoreCase ) )
			return ConstraintCategory.MorphDriver;
		if ( type.Contains( "RootMotion", StringComparison.OrdinalIgnoreCase ) )
			return ConstraintCategory.RootMotion;
		return ConstraintCategory.Unsupported;
	}

	static string? GetString( JsonElement e, string prop ) =>
		e.TryGetProperty( prop, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	static int? GetInt( JsonElement e, string prop ) =>
		e.TryGetProperty( prop, out var v ) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32( out int i ) ? i : null;

	static float? GetFloat( JsonElement e, string prop ) =>
		e.TryGetProperty( prop, out var v ) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle( out float f ) ? f : null;

	static bool? GetBool( JsonElement e, string prop ) =>
		e.TryGetProperty( prop, out var v ) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

	// --- Look-At and Copy Constraint Evaluators ---

	/// <summary>
	/// Evaluates a Look-At constraint: orients a bone's world transform so its designated aim axis
	/// points toward the target position, blended by weight.
	/// </summary>
	public static Xform EvaluateLookAt( Xform currentBoneWorld, Vec3 targetPosition, float weight = 1f, Vec3? aimAxis = null, Vec3? upAxis = null )
	{
		var aim = (aimAxis ?? new Vec3( 0, 1, 0 )).Normal;
		if ( aim.LengthSquared < 1e-4f ) aim = new Vec3( 0, 1, 0 );

		var origin = currentBoneWorld.Origin;
		var toTarget = targetPosition - origin;
		float distance = toTarget.Length;

		if ( distance < 1e-4f )
			return currentBoneWorld;

		var targetDir = toTarget / distance;
		var currentAimWorld = currentBoneWorld.TransformDirection( aim ).Normal;

		if ( currentAimWorld.LengthSquared < 1e-4f )
			currentAimWorld = targetDir;

		var deltaQuat = DeltaBetween( currentAimWorld, targetDir );

		float w = Math.Clamp( weight, 0f, 1f );
		var blendedDelta = Quat.Slerp( Quat.Identity, deltaQuat, w );

		var currentRot = Quat.FromXform( currentBoneWorld );
		var newRot = blendedDelta * currentRot;

		return newRot.ToXform( origin, Quat.ScaleOf( currentBoneWorld ) );
	}

	/// <summary>
	/// Evaluates a Copy Rotation constraint: transfers orientation from source rotation to current rotation with weight scaling.
	/// </summary>
	public static Quat EvaluateCopyRotation( Quat currentRotation, Quat sourceRotation, float weight = 1f )
	{
		float w = Math.Clamp( weight, 0f, 1f );
		return Quat.Slerp( currentRotation, sourceRotation, w );
	}

	/// <summary>
	/// Evaluates a Copy Rotation constraint on an Xform, preserving origin and scale while scaling orientation.
	/// </summary>
	public static Xform EvaluateCopyRotation( Xform currentTransform, Xform sourceTransform, float weight = 1f )
	{
		var rot = EvaluateCopyRotation( Quat.FromXform( currentTransform ), Quat.FromXform( sourceTransform ), weight );
		return rot.ToXform( currentTransform.Origin, Quat.ScaleOf( currentTransform ) );
	}

	/// <summary>
	/// Evaluates a Copy Transform constraint with weight scaling and optional channel masks.
	/// </summary>
	public static Xform EvaluateCopyTransform( Xform currentTransform, Xform sourceTransform, float weight = 1f,
		bool copyPosition = true, bool copyRotation = true, bool copyScale = true )
	{
		float w = Math.Clamp( weight, 0f, 1f );
		var pos = copyPosition ? Vec3.Lerp( currentTransform.Origin, sourceTransform.Origin, w ) : currentTransform.Origin;
		var rot = copyRotation ? Quat.Slerp( Quat.FromXform( currentTransform ), Quat.FromXform( sourceTransform ), w ) : Quat.FromXform( currentTransform );
		var scale = copyScale ? Vec3.Lerp( Quat.ScaleOf( currentTransform ), Quat.ScaleOf( sourceTransform ), w ) : Quat.ScaleOf( currentTransform );
		return rot.ToXform( pos, scale );
	}

	/// <summary>
	/// Applies Look-At constraint to a bone within a skeleton's world poses array.
	/// </summary>
	public static bool ApplyLookAt( Skeleton skeleton, Xform[] worldPoses, string boneName, Vec3 targetPosition, float weight = 1f, Vec3? aimAxis = null )
	{
		int b = skeleton.IndexOf( boneName );
		if ( b < 0 || b >= worldPoses.Length ) return false;
		worldPoses[b] = EvaluateLookAt( worldPoses[b], targetPosition, weight, aimAxis );
		return true;
	}

	/// <summary>
	/// Applies Copy Rotation constraint between bones within a skeleton's world poses array.
	/// </summary>
	public static bool ApplyCopyRotation( Skeleton skeleton, Xform[] worldPoses, string targetBone, string sourceBone, float weight = 1f )
	{
		int tb = skeleton.IndexOf( targetBone );
		int sb = skeleton.IndexOf( sourceBone );
		if ( tb < 0 || sb < 0 || tb >= worldPoses.Length || sb >= worldPoses.Length ) return false;
		worldPoses[tb] = EvaluateCopyRotation( worldPoses[tb], worldPoses[sb], weight );
		return true;
	}

	internal static Quat DeltaBetween( Vec3 from, Vec3 to )
	{
		if ( from.LengthSquared < 1e-6f || to.LengthSquared < 1e-6f )
			return Quat.Identity;

		from = from.Normal;
		to = to.Normal;

		float dot = Math.Clamp( Vec3.Dot( from, to ), -1f, 1f );
		if ( dot > 0.99999f )
			return Quat.Identity;

		if ( dot < -0.99999f )
		{
			var perp = Vec3.Cross( from, MathF.Abs( from.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 1, 0, 0 ) );
			return Quat.FromAxisAngle( perp.Normal, MathF.PI );
		}

		var axis = Vec3.Cross( from, to ).Normal;
		float angle = MathF.Acos( dot );
		return Quat.FromAxisAngle( axis, angle );
	}
}

/// <summary>
/// Stateful node representing a simulated secondary spring particle (Verlet integration).
/// </summary>
public sealed class SecondarySpringNode
{
	public string BoneName { get; }
	public float Stiffness { get; set; }
	public float Damping { get; set; }
	public float GravityWeight { get; set; }
	public float MaxAngle { get; set; }
	public Vec3 Tail { get; internal set; }
	public Vec3 Previous { get; internal set; }
	public bool Started { get; internal set; }

	public SecondarySpringNode( string boneName, Vec3 initialPosition, float stiffness = 120f, float damping = 0.001f, float gravityWeight = 0.3f, float maxAngle = 60f )
	{
		BoneName = boneName;
		Stiffness = stiffness;
		Damping = damping;
		GravityWeight = gravityWeight;
		MaxAngle = maxAngle;
		Tail = initialPosition;
		Previous = initialPosition;
		Started = true;
	}

	/// <summary>
	/// Current velocity of the particle derived from Verlet position differences.
	/// </summary>
	public Vec3 Velocity( float dt ) => dt > 1e-6f ? (Tail - Previous) / dt : Vec3.Zero;
}

/// <summary>
/// Deterministic secondary spring simulation service with fixed time-stepping and explicit reset.
/// Eliminates variable frame-rate jitter and guarantees bit-exact reproducible physics.
/// </summary>
public sealed class SecondarySpringSimulation
{
	readonly Dictionary<string, SecondarySpringNode> _nodes = new( StringComparer.Ordinal );
	float _accumulator;
	float _lastFixedStep;

	/// <summary>Fixed simulation time step (default: 120Hz = 1/120s).</summary>
	public float FixedDeltaTime { get; set; } = 1f / 120f;

	/// <summary>Maximum number of sub-steps executed per frame update to avoid spiral of death.</summary>
	public int MaxSubSteps { get; set; } = 8;

	/// <summary>Downward gravity acceleration in inches/s^2 (kernel standard: -386 in/s^2).</summary>
	public Vec3 Gravity { get; set; } = new( 0, 0, -386f );

	/// <summary>Total elapsed simulation time in seconds.</summary>
	public float TotalTime { get; private set; }

	/// <summary>Total number of fixed simulation steps executed.</summary>
	public int StepCount { get; private set; }

	public IReadOnlyCollection<SecondarySpringNode> Nodes => _nodes.Values;

	public void AddNode( string boneName, Vec3 initialPosition, float stiffness = 120f, float damping = 0.001f, float gravityWeight = 0.3f, float maxAngle = 60f )
	{
		ArgumentNullException.ThrowIfNull( boneName );
		_nodes[boneName] = new SecondarySpringNode( boneName, initialPosition, stiffness, damping, gravityWeight, maxAngle );
	}

	public bool TryGetNode( string boneName, out SecondarySpringNode? node ) =>
		_nodes.TryGetValue( boneName, out node );

	public Vec3 GetPosition( string boneName ) =>
		_nodes.TryGetValue( boneName, out var node ) ? node.Tail : Vec3.Zero;

	/// <summary>
	/// Advances the simulation using fixed time-stepping. Incoming dt is accumulated, and discrete
	/// steps of FixedDeltaTime are executed deterministically.
	/// </summary>
	public int Step( float dt, IReadOnlyDictionary<string, Vec3>? targetPositions = null )
	{
		if ( !float.IsFinite( dt ) || dt <= 0f || _nodes.Count == 0 )
			return 0;

		_accumulator += dt;
		int executed = 0;

		while ( _accumulator >= FixedDeltaTime && executed < MaxSubSteps )
		{
			StepFixed( FixedDeltaTime, targetPositions );
			_accumulator -= FixedDeltaTime;
			executed++;
		}

		if ( executed >= MaxSubSteps && _accumulator > FixedDeltaTime )
		{
			// Discard backlog excess to prevent lag accumulation
			_accumulator = 0f;
		}

		return executed;
	}

	/// <summary>
	/// Advances the simulation by exactly one fixed step of duration h.
	/// Uses kernel ChainSpring Verlet physics:
	/// carried = (tail - previous) * pow(damping, h)
	/// accel = (target - tail) * stiffness + gravity * gravityWeight
	/// next = tail + carried + accel * (h * h)
	/// </summary>
	public void StepFixed( float h, IReadOnlyDictionary<string, Vec3>? targetPositions = null )
	{
		if ( h <= 0f || _nodes.Count == 0 ) return;

		float ratio = _lastFixedStep > 1e-9f ? h / _lastFixedStep : 1f;

		foreach ( var node in _nodes.Values )
		{
			Vec3 target = targetPositions != null && targetPositions.TryGetValue( node.BoneName, out var t )
				? t
				: node.Tail;

			if ( !node.Started )
			{
				node.Tail = target;
				node.Previous = target;
				node.Started = true;
				continue;
			}

			// Teleport check (> 200 units in one frame)
			if ( (node.Tail - target).Length > 200f )
			{
				node.Tail = target;
				node.Previous = target;
				continue;
			}

			float dampingRetention = MathF.Pow( Math.Clamp( node.Damping, 0f, 1f ), h );
			var carried = (node.Tail - node.Previous) * ratio * dampingRetention;
			var accel = (target - node.Tail) * MathF.Max( node.Stiffness, 0f ) + Gravity * node.GravityWeight;
			var next = node.Tail + carried + accel * (h * h);

			node.Previous = node.Tail;
			node.Tail = next;
		}

		_lastFixedStep = h;
		TotalTime += h;
		StepCount++;
	}

	/// <summary>
	/// Resets all nodes to their initial/current target positions and clears all velocities.
	/// </summary>
	public void Reset()
	{
		foreach ( var node in _nodes.Values )
		{
			node.Previous = node.Tail;
			node.Started = false;
		}
		_accumulator = 0f;
		_lastFixedStep = 0f;
	}

	/// <summary>
	/// Resets all nodes cleanly to the specified playhead seek positions.
	/// Eliminates residual momentum and velocity artifacts when scrubbing or seeking.
	/// </summary>
	public void ResetOnSeek( IReadOnlyDictionary<string, Vec3> seekPositions )
	{
		ArgumentNullException.ThrowIfNull( seekPositions );
		foreach ( var node in _nodes.Values )
		{
			if ( seekPositions.TryGetValue( node.BoneName, out var p ) )
			{
				node.Tail = p;
				node.Previous = p;
				node.Started = true;
			}
			else
			{
				node.Previous = node.Tail;
			}
		}
		_accumulator = 0f;
		_lastFixedStep = 0f;
	}

	/// <summary>
	/// Resets all nodes cleanly when switching or reloading animation clips.
	/// </summary>
	public void ResetOnClipReload( IReadOnlyDictionary<string, Vec3>? reloadPositions = null )
	{
		if ( reloadPositions != null )
		{
			ResetOnSeek( reloadPositions );
		}
		else
		{
			Reset();
		}
	}
}
