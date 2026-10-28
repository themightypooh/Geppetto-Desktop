#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Rotation order options available in FBX Model nodes.
/// In standard FBX files, property "RotationOrder" defines the Euler convention.
/// </summary>
public enum FbxRotationOrder
{
	EulerXYZ = 0,
	EulerXZY = 1,
	EulerYZX = 2,
	EulerYXZ = 3,
	EulerZXY = 4,
	EulerZYX = 5,
	SphericXYZ = 6
}

/// <summary>
/// Interpolation mode for FBX animation curve keys.
/// </summary>
public enum FbxInterpolationMode
{
	Constant,
	Linear,
	Cubic
}

/// <summary>
/// Severity level of a feasibility audit issue.
/// </summary>
public enum FeasibilityIssueSeverity
{
	Warning,
	Error
}

/// <summary>
/// Structured description of an animation feature or compatibility issue discovered during audit.
/// </summary>
public sealed record FeasibilityIssue(
	FeasibilityIssueSeverity Severity,
	string Feature,
	string NodeOrTakeName,
	string Message
);

/// <summary>
/// Comprehensive result of auditing an FBX file or animation data structure against the
/// approved first-subset animation contract.
/// </summary>
public sealed class FbxAnimationAuditResult
{
	public bool IsFeasible => Issues.All( i => i.Severity != FeasibilityIssueSeverity.Error );
	public string TakeName { get; set; } = "";
	public int LayerCount { get; set; } = 1;
	public int FrameRate { get; set; } = 30;
	public int FrameCount { get; set; } = 0;
	public double DurationSeconds { get; set; } = 0.0;
	public List<FeasibilityIssue> Issues { get; } = new();
	public List<string> SupportedBones { get; } = new();
	public List<string> UnsupportedBones { get; } = new();
	public List<string> MissingTargetBones { get; } = new();

	public bool HasErrors => Issues.Any( i => i.Severity == FeasibilityIssueSeverity.Error );

	public void AddError( string feature, string nodeOrTake, string message ) =>
		Issues.Add( new FeasibilityIssue( FeasibilityIssueSeverity.Error, feature, nodeOrTake, message ) );

	public void AddWarning( string feature, string nodeOrTake, string message ) =>
		Issues.Add( new FeasibilityIssue( FeasibilityIssueSeverity.Warning, feature, nodeOrTake, message ) );
}

/// <summary>
/// Specification and contract constants for the approved first subset of FBX animation import.
///
/// FEASIBILITY ANALYSIS & ARCHITECTURAL CONTRACT:
/// ----------------------------------------------
/// Existing FbxReader.cs and FbxImport.cs inspect FBX scenes for static/skinned geometry:
/// - FbxReader reads Bind matrices (TransformLink / Transform) from Deformer/Cluster clusters,
///   per-corner normals/UVs, and control points.
/// - FbxImport converts positions and bone binds into Effigy space (+Z up, -Y front, +X right, inches)
///   using AxisMap and UnitScale.
///
/// What FbxReader does NOT currently have:
/// 1. AnimationStacks (Takes): No parsing of AnimationStack objects or take start/stop time codes.
/// 2. AnimationLayers: No evaluation of layered animation, blend modes, weights, or layer hierarchies.
/// 3. AnimationCurveNodes & Curves: No extraction of property-channel connections (d|X, d|Y, d|Z)
///    or keyframe arrays (KeyTime, KeyValueFloat, KeyAttrFlags, KeyAttrDataFloat tangents).
/// 4. Complex FBX Transform Evaluation: Full FBX node transform evaluation formula:
///      World = ParentWorld * Translation * RotationOffset * RotationPivot * PreRotation *
///              Rotation * PostRotation^-1 * RotationPivot^-1 * ScalingOffset * ScalingPivot *
///              Scaling * ScalingPivot^-1
///    Evaluating this dynamically for unbaked pivots or non-zero pre/post rotations would require
///    resampling or complex inverse compensation.
/// 5. Rotation Orders: FBX supports 6 Euler orders + Spheric XYZ. Non-XYZ orders require reordering
///    axis multiplications.
///
/// APPROVED FIRST SUBSET CONTRACT (Ticket I3 / I4):
/// 1. Take selection: Exactly one take (AnimationStack); ambiguous takes require explicit selection.
/// 2. Single Layer: Exactly one AnimationLayer. Multi-layer animation is explicitly REJECTED with a
///    structured error.
/// 3. Rotation Order: eEulerXYZ only. Custom/non-XYZ rotation orders are REJECTED with a structured error.
/// 4. Baked Pivots & Pre/Post: RotationPivot, ScalingPivot, PreRotation, PostRotation must be identity (0,0,0).
///    Unbaked pivots are REJECTED with structured errors.
/// 5. Supported Interpolations: Constant/Stepped (RigEase.Stepped), Linear (RigEase.Linear), Cubic (RigEase.Cubic).
/// 6. Transform Derivation: Local bone pose is derived consistently via:
///      Local = Inverse(ParentWorld) * ChildWorld
/// 7. Units & Coordinates: Converted once via FbxImport.AxisMap and UnitScale factor (cm -> inches).
/// </summary>
public static class FbxAnimationContract
{
	public const string FeatureLayeredAnimation = "LayeredAnimation";
	public const string FeatureCustomRotationOrder = "CustomRotationOrder";
	public const string FeatureUnbakedPivot = "UnbakedPivot";
	public const string FeaturePrePostRotation = "PrePostRotation";
	public const string FeatureMissingTargetBone = "MissingTargetBone";
	public const string FeatureUnsupportedInterpolation = "UnsupportedInterpolation";
	public const string FeatureAmbiguousTake = "AmbiguousTake";
	public const string FeatureNoAnimation = "NoAnimation";

	public const FbxRotationOrder ApprovedRotationOrder = FbxRotationOrder.EulerXYZ;
	public const int MaxSupportedLayers = 1;
}

/// <summary>
/// Conversion utilities between FBX KTime (ticks) and kernel seconds / integer frames.
/// FBX tick definition: 46,186,158,000 ticks per second (1 KTime unit).
/// </summary>
public static class FbxAnimationTime
{
	/// <summary>
	/// Number of FBX KTime ticks in one second.
	/// </summary>
	public const long FbxTicksPerSecond = 46_186_158_000L;

	public static double TicksToSeconds( long ticks ) => (double)ticks / FbxTicksPerSecond;

	public static long SecondsToTicks( double seconds ) => (long)Math.Round( seconds * FbxTicksPerSecond );

	public static int TicksToKernelFrame( long ticks, int frameRate )
	{
		if ( frameRate <= 0 ) throw new ArgumentOutOfRangeException( nameof( frameRate ), "Frame rate must be positive." );
		return (int)Math.Round( (double)ticks * frameRate / FbxTicksPerSecond );
	}

	public static long KernelFrameToTicks( int frame, int frameRate )
	{
		if ( frameRate <= 0 ) throw new ArgumentOutOfRangeException( nameof( frameRate ), "Frame rate must be positive." );
		return (long)Math.Round( (double)frame / frameRate * FbxTicksPerSecond );
	}

	public static float KernelFrameToSeconds( int frame, int frameRate )
	{
		if ( frameRate <= 0 ) throw new ArgumentOutOfRangeException( nameof( frameRate ), "Frame rate must be positive." );
		return (float)frame / frameRate;
	}

	public static int SecondsToKernelFrame( float seconds, int frameRate )
	{
		if ( frameRate <= 0 ) throw new ArgumentOutOfRangeException( nameof( frameRate ), "Frame rate must be positive." );
		return (int)Math.Round( seconds * frameRate );
	}
}

/// <summary>
/// Mathematical routines for Euler angle conversion, transform composition, and local derivation.
/// </summary>
public static class FbxAnimationMath
{
	/// <summary>
	/// Converts Euler angles in degrees to a unit quaternion under the specified FBX rotation order.
	/// In System.Numerics / Effigy Quat, Hamilton quaternion multiplication applies rotations right-to-left
	/// when transforming vectors (q_z * q_y * q_x applies X first, then Y, then Z).
	/// </summary>
	public static Quat EulerToQuat( float degX, float degY, float degZ, FbxRotationOrder order = FbxRotationOrder.EulerXYZ )
	{
		const float degToRad = MathF.PI / 180f;
		var qx = Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), degX * degToRad );
		var qy = Quat.FromAxisAngle( new Vec3( 0, 1, 0 ), degY * degToRad );
		var qz = Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), degZ * degToRad );

		// Under Hamilton product:
		// Vector3.Transform(v, q2 * q1) transforms by q1 first, then q2.
		// Therefore to apply in order (first -> second -> third):
		// composite = third * second * first.
		return order switch
		{
			FbxRotationOrder.EulerXYZ => (qz * qy * qx).Normalized,
			FbxRotationOrder.EulerXZY => (qy * qz * qx).Normalized,
			FbxRotationOrder.EulerYZX => (qx * qz * qy).Normalized,
			FbxRotationOrder.EulerYXZ => (qz * qx * qy).Normalized,
			FbxRotationOrder.EulerZXY => (qy * qx * qz).Normalized,
			FbxRotationOrder.EulerZYX => (qx * qy * qz).Normalized,
			_ => (qz * qy * qx).Normalized
		};
	}

	/// <summary>
	/// Derives child's local transform from parent's world transform and child's world transform:
	/// Local = Inverse(ParentWorld) * ChildWorld
	/// </summary>
	public static Xform DeriveLocal( Xform parentWorld, Xform childWorld )
	{
		return parentWorld.Inverse * childWorld;
	}

	/// <summary>
	/// Composes child's world transform from parent's world transform and child's local transform:
	/// ChildWorld = ParentWorld * ChildLocal
	/// </summary>
	public static Xform ComposeWorld( Xform parentWorld, Xform childLocal )
	{
		return parentWorld * childLocal;
	}

	/// <summary>
	/// Derives child's local pose (RigPose) from parent's world transform and child's world transform.
	/// </summary>
	public static RigPose DeriveLocalPose( Xform parentWorld, Xform childWorld )
	{
		var localXform = DeriveLocal( parentWorld, childWorld );
		return RigPose.FromXform( localXform );
	}
}

/// <summary>
/// Synthetic keyframe descriptor for feasibility testing and verification.
/// </summary>
public sealed record SyntheticKeyframe(
	long TimeTicks,
	Vec3 Translation,
	Vec3 RotationEuler,
	Vec3 Scale,
	FbxInterpolationMode Interpolation = FbxInterpolationMode.Linear
);

/// <summary>
/// Synthetic bone node descriptor for feasibility testing and verification.
/// </summary>
public sealed class SyntheticBoneNode
{
	public string Name { get; set; } = "";
	public string? ParentName { get; set; }
	public FbxRotationOrder RotationOrder { get; set; } = FbxRotationOrder.EulerXYZ;
	public Vec3 RotationPivot { get; set; } = Vec3.Zero;
	public Vec3 ScalingPivot { get; set; } = Vec3.Zero;
	public Vec3 PreRotation { get; set; } = Vec3.Zero;
	public Vec3 PostRotation { get; set; } = Vec3.Zero;
	public List<SyntheticKeyframe> Keyframes { get; } = new();
}

/// <summary>
/// Synthetic animation take descriptor.
/// </summary>
public sealed class SyntheticAnimationTake
{
	public string Name { get; set; } = "Take 001";
	public int LayerCount { get; set; } = 1;
	public long StartTicks { get; set; } = 0;
	public long StopTicks { get; set; } = FbxAnimationTime.FbxTicksPerSecond;
	public List<SyntheticBoneNode> Nodes { get; } = new();
}

/// <summary>
/// Complete synthetic FBX animation container for feasibility auditing and conversion tests.
/// </summary>
public sealed class SyntheticFbxAnimationData
{
	public List<SyntheticAnimationTake> Takes { get; } = new();
	public double UnitScale { get; set; } = 2.54; // default to kernel inches (2.54 cm / inch)
	public (int Axis, int Sign) UpAxis { get; set; } = (1, 1); // FBX default: Y-up
	public (int Axis, int Sign) FrontAxis { get; set; } = (2, 1); // FBX default: +Z front
	public (int Axis, int Sign) CoordAxis { get; set; } = (0, 1); // FBX default: +X right
}

/// <summary>
/// Owned synthetic reference fixture and independent analytical golden oracle.
///
/// INDEPENDENT ORACLE SPECIFICATION:
/// ---------------------------------
/// Hierarchy:
///   - "root": no parent (parentIndex = -1)
///   - "child": parent is "root"
///
/// FrameRate: 30 fps
/// Duration: 1.0 second (31 frames, 0 to 30)
///
/// Keyframe 0 (t = 0.0s, frame 0):
///   root:
///     World Translation = (0, 0, 0)
///     World Rotation EulerXYZ = (0°, 0°, 0°) -> Quat.Identity (0, 0, 0, 1)
///     World Scale = (1, 1, 1)
///   child:
///     Local Translation = (0, 10, 0)
///     Local Rotation EulerXYZ = (0°, 0°, 0°) -> Quat.Identity (0, 0, 0, 1)
///     Local Scale = (1, 1, 1)
///     Golden World of child = (0, 10, 0), Quat.Identity, Scale=(1, 1, 1)
///
/// Keyframe 15 (t = 0.5s, frame 15):
///   root:
///     Translation = (5, 10, 0)
///     Rotation EulerXYZ = (0°, 0°, 90°) -> 90° about Z:
///       q_root = (0, 0, sqrt(2)/2, sqrt(2)/2) ≈ (0, 0, 0.70710678, 0.70710678)
///     Scale = (2, 2, 2)
///   child (local relative to root):
///     Local Translation = (0, 4, 0)
///     Local Rotation EulerXYZ = (90°, 0°, 0°) -> 90° about X:
///       q_child = (sqrt(2)/2, 0, 0, sqrt(2)/2) ≈ (0.70710678, 0, 0, 0.70710678)
///     Local Scale = (1, 1, 1)
///
///   Independent Mathematical Composition:
///     Root basis vectors:
///       X_root = Rotate_Z(90°) * (1, 0, 0) * 2 = (0, 2, 0)
///       Y_root = Rotate_Z(90°) * (0, 1, 0) * 2 = (-2, 0, 0)
///       Z_root = Rotate_Z(90°) * (0, 0, 1) * 2 = (0, 0, 2)
///       Origin_root = (5, 10, 0)
///     Child world origin:
///       Origin_root + Y_root * 4 = (5, 10, 0) + (-2, 0, 0) * 4 = (-3, 10, 0)
///     Child world rotation:
///       q_world = q_root * q_child = (0.5, 0.5, 0.5, 0.5)
///     Child world scale:
///       (2, 2, 2)
///
/// Keyframe 30 (t = 1.0s, frame 30):
///   root:
///     Translation = (10, 20, 0)
///     Rotation EulerXYZ = (0°, 0°, 180°) -> 180° about Z:
///       q_root = (0, 0, 1, 0)
///     Scale = (1, 1, 1)
///   child:
///     Local Translation = (0, 5, 0)
///     Local Rotation EulerXYZ = (0°, 0°, 0°) -> Quat.Identity (0, 0, 0, 1)
///     Local Scale = (1, 1, 1)
///   Child world origin:
///     (10, 20, 0) + Rotate_Z(180°) * (0, 5, 0) = (10, 20 - 5, 0) = (10, 15, 0)
///     Rotation = (0, 0, 1, 0)
///     Scale = (1, 1, 1)
/// </summary>
public static class FbxAnimationReferenceFixture
{
	public const string RootBoneName = "root";
	public const string ChildBoneName = "child";
	public const int FrameRate = 30;
	public const int FrameCount = 31;
	public const double DurationSeconds = 1.0;

	// Frame 0 Golden Values
	public static readonly Vec3 GoldenRootPos0 = new( 0, 0, 0 );
	public static readonly Quat GoldenRootRot0 = Quat.Identity;
	public static readonly Vec3 GoldenRootScale0 = new( 1, 1, 1 );

	public static readonly Vec3 GoldenChildLocalPos0 = new( 0, 10, 0 );
	public static readonly Quat GoldenChildLocalRot0 = Quat.Identity;
	public static readonly Vec3 GoldenChildLocalScale0 = new( 1, 1, 1 );

	public static readonly Vec3 GoldenChildWorldPos0 = new( 0, 10, 0 );
	public static readonly Quat GoldenChildWorldRot0 = Quat.Identity;
	public static readonly Vec3 GoldenChildWorldScale0 = new( 1, 1, 1 );

	// Frame 15 Golden Values (t = 0.5s)
	public static readonly Vec3 GoldenRootPos15 = new( 5, 10, 0 );
	public static readonly Quat GoldenRootRot15 = FbxAnimationMath.EulerToQuat( 0, 0, 90f );
	public static readonly Vec3 GoldenRootScale15 = new( 2, 2, 2 );

	public static readonly Vec3 GoldenChildLocalPos15 = new( 0, 4, 0 );
	public static readonly Quat GoldenChildLocalRot15 = FbxAnimationMath.EulerToQuat( 90f, 0, 0 );
	public static readonly Vec3 GoldenChildLocalScale15 = new( 1, 1, 1 );

	// Analytical Golden Child World at Frame 15:
	// Pos = (-3, 10, 0), Rot = (0.5, 0.5, 0.5, 0.5), Scale = (2, 2, 2)
	public static readonly Vec3 GoldenChildWorldPos15 = new( -3, 10, 0 );
	public static readonly Quat GoldenChildWorldRot15 = new( 0.5f, 0.5f, 0.5f, 0.5f );
	public static readonly Vec3 GoldenChildWorldScale15 = new( 2, 2, 2 );

	// Frame 30 Golden Values (t = 1.0s)
	public static readonly Vec3 GoldenRootPos30 = new( 10, 20, 0 );
	public static readonly Quat GoldenRootRot30 = FbxAnimationMath.EulerToQuat( 0, 0, 180f );
	public static readonly Vec3 GoldenRootScale30 = new( 1, 1, 1 );

	public static readonly Vec3 GoldenChildLocalPos30 = new( 0, 5, 0 );
	public static readonly Quat GoldenChildLocalRot30 = Quat.Identity;
	public static readonly Vec3 GoldenChildLocalScale30 = new( 1, 1, 1 );

	public static readonly Vec3 GoldenChildWorldPos30 = new( 10, 15, 0 );
	public static readonly Quat GoldenChildWorldRot30 = new( 0, 0, 1, 0 );
	public static readonly Vec3 GoldenChildWorldScale30 = new( 1, 1, 1 );

	/// <summary>
	/// Builds a matching Skeleton structure for the reference fixture.
	/// </summary>
	public static Skeleton CreateReferenceSkeleton()
	{
		var skeleton = new Skeleton();
		skeleton.AddBone( RootBoneName, -1, Xform.Identity, 10f );
		skeleton.AddBone( ChildBoneName, 0, Xform.Translate( GoldenChildLocalPos0 ), 5f );
		return skeleton;
	}

	/// <summary>
	/// Creates the owned synthetic animation data representing this reference fixture.
	/// </summary>
	public static SyntheticFbxAnimationData CreateReferenceAnimationData( double unitScale = 2.54 )
	{
		var data = new SyntheticFbxAnimationData { UnitScale = unitScale };
		var take = new SyntheticAnimationTake
		{
			Name = "ReferenceTake",
			LayerCount = 1,
			StartTicks = 0,
			StopTicks = FbxAnimationTime.FbxTicksPerSecond
		};

		var rootNode = new SyntheticBoneNode
		{
			Name = RootBoneName,
			ParentName = null,
			RotationOrder = FbxRotationOrder.EulerXYZ
		};
		rootNode.Keyframes.Add( new SyntheticKeyframe( 0, GoldenRootPos0, new Vec3( 0, 0, 0 ), GoldenRootScale0, FbxInterpolationMode.Linear ) );
		rootNode.Keyframes.Add( new SyntheticKeyframe( FbxAnimationTime.KernelFrameToTicks( 15, FrameRate ), GoldenRootPos15, new Vec3( 0, 0, 90f ), GoldenRootScale15, FbxInterpolationMode.Linear ) );
		rootNode.Keyframes.Add( new SyntheticKeyframe( FbxAnimationTime.KernelFrameToTicks( 30, FrameRate ), GoldenRootPos30, new Vec3( 0, 0, 180f ), GoldenRootScale30, FbxInterpolationMode.Linear ) );

		var childNode = new SyntheticBoneNode
		{
			Name = ChildBoneName,
			ParentName = RootBoneName,
			RotationOrder = FbxRotationOrder.EulerXYZ
		};
		childNode.Keyframes.Add( new SyntheticKeyframe( 0, GoldenChildLocalPos0, new Vec3( 0, 0, 0 ), GoldenChildLocalScale0, FbxInterpolationMode.Linear ) );
		childNode.Keyframes.Add( new SyntheticKeyframe( FbxAnimationTime.KernelFrameToTicks( 15, FrameRate ), GoldenChildLocalPos15, new Vec3( 90f, 0, 0 ), GoldenChildLocalScale15, FbxInterpolationMode.Linear ) );
		childNode.Keyframes.Add( new SyntheticKeyframe( FbxAnimationTime.KernelFrameToTicks( 30, FrameRate ), GoldenChildLocalPos30, new Vec3( 0, 0, 0 ), GoldenChildLocalScale30, FbxInterpolationMode.Linear ) );

		take.Nodes.Add( rootNode );
		take.Nodes.Add( childNode );
		data.Takes.Add( take );

		return data;
	}

	/// <summary>
	/// Constructs a raw FbxNode tree hierarchy representing this reference fixture, suitable for
	/// auditing and testing raw FBX object model parsing.
	/// </summary>
	public static FbxNode CreateReferenceFbxNodeTree(
		int layerCount = 1,
		FbxRotationOrder rootRotationOrder = FbxRotationOrder.EulerXYZ,
		Vec3? rootRotationPivot = null,
		Vec3? rootPreRotation = null )
	{
		var root = new FbxNode { Name = "" };

		// GlobalSettings
		var settings = new FbxNode { Name = "GlobalSettings" };
		var props70 = new FbxNode { Name = "Properties70" };
		props70.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "UnitScaleFactor", "double", "Number", "", 2.54 } } );
		props70.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "UpAxis", "int", "Integer", "", 1 } } );
		props70.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "UpAxisSign", "int", "Integer", "", 1 } } );
		props70.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "FrontAxis", "int", "Integer", "", 2 } } );
		props70.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "FrontAxisSign", "int", "Integer", "", 1 } } );
		props70.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "CoordAxis", "int", "Integer", "", 0 } } );
		props70.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "CoordAxisSign", "int", "Integer", "", 1 } } );
		settings.Children.Add( props70 );
		root.Children.Add( settings );

		var objects = new FbxNode { Name = "Objects" };
		var connections = new FbxNode { Name = "Connections" };

		long stackId = 1000L;
		var stackNode = new FbxNode
		{
			Name = "AnimationStack",
			Props = new List<object> { stackId, "ReferenceTake\0\x01AnimStack", "AnimStack" }
		};
		objects.Children.Add( stackNode );

		for ( int l = 0; l < layerCount; l++ )
		{
			long layerId = 2000L + l;
			var layerNode = new FbxNode
			{
				Name = "AnimationLayer",
				Props = new List<object> { layerId, $"Layer_{l}\0\x01AnimLayer", "AnimLayer" }
			};
			objects.Children.Add( layerNode );
			connections.Children.Add( new FbxNode
			{
				Name = "C",
				Props = new List<object> { "OO", layerId, stackId }
			} );
		}

		long rootModelId = 3001L;
		var rootModel = new FbxNode
		{
			Name = "Model",
			Props = new List<object> { rootModelId, $"{RootBoneName}\0\x01Model", "LimbNode" }
		};
		var rootProps = new FbxNode { Name = "Properties70" };
		rootProps.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "RotationOrder", "enum", "", "", (int)rootRotationOrder } } );
		var rotPiv = rootRotationPivot ?? Vec3.Zero;
		rootProps.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "RotationPivot", "Vector3D", "Vector", "", (double)rotPiv.x, (double)rotPiv.y, (double)rotPiv.z } } );
		var preRot = rootPreRotation ?? Vec3.Zero;
		rootProps.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "PreRotation", "Vector3D", "Vector", "", (double)preRot.x, (double)preRot.y, (double)preRot.z } } );
		rootModel.Children.Add( rootProps );
		objects.Children.Add( rootModel );

		long childModelId = 3002L;
		var childModel = new FbxNode
		{
			Name = "Model",
			Props = new List<object> { childModelId, $"{ChildBoneName}\0\x01Model", "LimbNode" }
		};
		var childProps = new FbxNode { Name = "Properties70" };
		childProps.Children.Add( new FbxNode { Name = "P", Props = new List<object> { "RotationOrder", "enum", "", "", 0 } } );
		childModel.Children.Add( childProps );
		objects.Children.Add( childModel );

		// Child parented to root
		connections.Children.Add( new FbxNode
		{
			Name = "C",
			Props = new List<object> { "OO", childModelId, rootModelId }
		} );

		root.Children.Add( objects );
		root.Children.Add( connections );

		return root;
	}
}

/// <summary>
/// Auditor that validates FBX animation structures against the approved first subset contract.
/// </summary>
public static class FbxAnimationAuditor
{
	/// <summary>
	/// Audits a synthetic animation data container.
	/// </summary>
	public static FbxAnimationAuditResult Audit( SyntheticFbxAnimationData data, Skeleton? targetSkeleton = null )
	{
		ArgumentNullException.ThrowIfNull( data );
		var result = new FbxAnimationAuditResult();

		if ( data.Takes.Count == 0 )
		{
			result.AddError( FbxAnimationContract.FeatureNoAnimation, "", "No animation takes found in data." );
			return result;
		}

		if ( data.Takes.Count > 1 )
		{
			result.AddWarning( FbxAnimationContract.FeatureAmbiguousTake, data.Takes[0].Name,
				$"Data contains {data.Takes.Count} takes. The approved first subset supports only single-take import; taking '{data.Takes[0].Name}'." );
		}

		var take = data.Takes[0];
		result.TakeName = take.Name;
		result.LayerCount = take.LayerCount;
		result.DurationSeconds = FbxAnimationTime.TicksToSeconds( take.StopTicks - take.StartTicks );
		result.FrameCount = FbxAnimationTime.TicksToKernelFrame( take.StopTicks - take.StartTicks, result.FrameRate ) + 1;

		if ( take.LayerCount > FbxAnimationContract.MaxSupportedLayers )
		{
			result.AddError( FbxAnimationContract.FeatureLayeredAnimation, take.Name,
				$"Take '{take.Name}' has {take.LayerCount} animation layers. Multi-layer animation is unsupported in the first subset." );
		}

		HashSet<string>? targetBoneNames = targetSkeleton != null
			? new HashSet<string>( targetSkeleton.Bones.Select( b => b.Name ), StringComparer.Ordinal )
			: null;

		foreach ( var node in take.Nodes )
		{
			bool nodeSupported = true;

			if ( node.RotationOrder != FbxAnimationContract.ApprovedRotationOrder )
			{
				result.AddError( FbxAnimationContract.FeatureCustomRotationOrder, node.Name,
					$"Node '{node.Name}' uses unsupported rotation order '{node.RotationOrder}'. Only EulerXYZ is supported." );
				nodeSupported = false;
			}

			if ( node.RotationPivot.LengthSquared > 1e-8f )
			{
				result.AddError( FbxAnimationContract.FeatureUnbakedPivot, node.Name,
					$"Node '{node.Name}' has unbaked RotationPivot ({node.RotationPivot.x}, {node.RotationPivot.y}, {node.RotationPivot.z}). Pivots must be baked." );
				nodeSupported = false;
			}

			if ( node.ScalingPivot.LengthSquared > 1e-8f )
			{
				result.AddError( FbxAnimationContract.FeatureUnbakedPivot, node.Name,
					$"Node '{node.Name}' has unbaked ScalingPivot ({node.ScalingPivot.x}, {node.ScalingPivot.y}, {node.ScalingPivot.z}). Pivots must be baked." );
				nodeSupported = false;
			}

			if ( node.PreRotation.LengthSquared > 1e-8f )
			{
				result.AddError( FbxAnimationContract.FeaturePrePostRotation, node.Name,
					$"Node '{node.Name}' has non-zero PreRotation ({node.PreRotation.x}, {node.PreRotation.y}, {node.PreRotation.z}). Pre/post rotations must be baked." );
				nodeSupported = false;
			}

			if ( node.PostRotation.LengthSquared > 1e-8f )
			{
				result.AddError( FbxAnimationContract.FeaturePrePostRotation, node.Name,
					$"Node '{node.Name}' has non-zero PostRotation ({node.PostRotation.x}, {node.PostRotation.y}, {node.PostRotation.z}). Pre/post rotations must be baked." );
				nodeSupported = false;
			}

			if ( targetBoneNames != null && !targetBoneNames.Contains( node.Name ) )
			{
				result.AddWarning( FbxAnimationContract.FeatureMissingTargetBone, node.Name,
					$"Animation targets bone '{node.Name}' which does not exist in the target skeleton." );
				result.MissingTargetBones.Add( node.Name );
			}

			if ( nodeSupported )
				result.SupportedBones.Add( node.Name );
			else
				result.UnsupportedBones.Add( node.Name );
		}

		return result;
	}

	/// <summary>
	/// Audits a raw FbxNode tree.
	/// </summary>
	public static FbxAnimationAuditResult Audit( FbxNode root, Skeleton? targetSkeleton = null, string? requestedTake = null )
	{
		ArgumentNullException.ThrowIfNull( root );
		var result = new FbxAnimationAuditResult();

		var objects = root.Child( "Objects" );
		if ( objects == null )
		{
			result.AddError( FbxAnimationContract.FeatureNoAnimation, "", "FBX file has no Objects section." );
			return result;
		}

		var stacks = objects.Children.Where( n => n.Name == "AnimationStack" ).ToList();
		if ( stacks.Count == 0 )
		{
			result.AddError( FbxAnimationContract.FeatureNoAnimation, "", "No AnimationStack (take) objects found in FBX file." );
			return result;
		}

		FbxNode? chosenStack = null;
		if ( requestedTake != null )
			chosenStack = stacks.FirstOrDefault( s => s.ObjectName == requestedTake );
		chosenStack ??= stacks[0];

		result.TakeName = chosenStack.ObjectName;

		if ( stacks.Count > 1 )
		{
			result.AddWarning( FbxAnimationContract.FeatureAmbiguousTake, chosenStack.ObjectName,
				$"FBX contains {stacks.Count} takes. Single-take subset selected take '{chosenStack.ObjectName}'." );
		}

		long stackId = chosenStack.Props.Count > 0 && chosenStack.Props[0] is long id ? id : 0L;

		// Inspect connections for animation layers connected to this stack
		var connections = root.Child( "Connections" )?.Children ?? new List<FbxNode>();
		var connectedLayerIds = new List<long>();

		// Build fast object lookup
		var byId = new Dictionary<long, FbxNode>();
		foreach ( var o in objects.Children )
		{
			if ( o.Props.Count > 0 && o.Props[0] is long oid )
				byId[oid] = o;
		}

		foreach ( var c in connections )
		{
			if ( c.Name == "C" && c.Props.Count >= 3 && c.Props[1] is long child && c.Props[2] is long parent && parent == stackId )
			{
				if ( byId.TryGetValue( child, out var childNode ) && childNode.Name == "AnimationLayer" )
					connectedLayerIds.Add( child );
			}
		}

		result.LayerCount = Math.Max( 1, connectedLayerIds.Count );
		if ( connectedLayerIds.Count > FbxAnimationContract.MaxSupportedLayers )
		{
			result.AddError( FbxAnimationContract.FeatureLayeredAnimation, chosenStack.ObjectName,
				$"Take '{chosenStack.ObjectName}' has {connectedLayerIds.Count} animation layers. Multi-layer animation is unsupported in the first subset." );
		}

		HashSet<string>? targetBoneNames = targetSkeleton != null
			? new HashSet<string>( targetSkeleton.Bones.Select( b => b.Name ), StringComparer.Ordinal )
			: null;

		// Inspect Model nodes for rotation order, pivots, and pre/post rotations
		foreach ( var model in objects.Children.Where( n => n.Name == "Model" ) )
		{
			string name = model.ObjectName;
			bool nodeSupported = true;

			var propsNode = model.Child( "Properties70" );
			if ( propsNode != null )
			{
				foreach ( var p in propsNode.Children.Where( c => c.Name == "P" && c.Props.Count > 0 ) )
				{
					string propName = p.Props[0] as string ?? "";

					if ( propName == "RotationOrder" && p.Props.Count >= 5 )
					{
						int orderVal = Convert.ToInt32( p.Props[4] );
						if ( orderVal != (int)FbxAnimationContract.ApprovedRotationOrder )
						{
							result.AddError( FbxAnimationContract.FeatureCustomRotationOrder, name,
								$"Model '{name}' has rotation order '{orderVal}' ({(FbxRotationOrder)orderVal}). Only EulerXYZ (0) is supported." );
							nodeSupported = false;
						}
					}
					else if ( propName == "RotationPivot" && p.Props.Count >= 7 )
					{
						double x = Convert.ToDouble( p.Props[4] );
						double y = Convert.ToDouble( p.Props[5] );
						double z = Convert.ToDouble( p.Props[6] );
						if ( x * x + y * y + z * z > 1e-8 )
						{
							result.AddError( FbxAnimationContract.FeatureUnbakedPivot, name,
								$"Model '{name}' has unbaked RotationPivot ({x}, {y}, {z}). Only baked zero pivots are supported." );
							nodeSupported = false;
						}
					}
					else if ( propName == "ScalingPivot" && p.Props.Count >= 7 )
					{
						double x = Convert.ToDouble( p.Props[4] );
						double y = Convert.ToDouble( p.Props[5] );
						double z = Convert.ToDouble( p.Props[6] );
						if ( x * x + y * y + z * z > 1e-8 )
						{
							result.AddError( FbxAnimationContract.FeatureUnbakedPivot, name,
								$"Model '{name}' has unbaked ScalingPivot ({x}, {y}, {z}). Only baked zero pivots are supported." );
							nodeSupported = false;
						}
					}
					else if ( (propName == "PreRotation" || propName == "PostRotation") && p.Props.Count >= 7 )
					{
						double x = Convert.ToDouble( p.Props[4] );
						double y = Convert.ToDouble( p.Props[5] );
						double z = Convert.ToDouble( p.Props[6] );
						if ( x * x + y * y + z * z > 1e-8 )
						{
							result.AddError( FbxAnimationContract.FeaturePrePostRotation, name,
								$"Model '{name}' has non-zero {propName} ({x}, {y}, {z}). Pre/post rotations must be baked." );
							nodeSupported = false;
						}
					}
				}
			}

			if ( targetBoneNames != null && !targetBoneNames.Contains( name ) )
			{
				result.AddWarning( FbxAnimationContract.FeatureMissingTargetBone, name,
					$"Animation targets bone '{name}' which is not in target skeleton." );
				result.MissingTargetBones.Add( name );
			}

			if ( nodeSupported )
				result.SupportedBones.Add( name );
			else
				result.UnsupportedBones.Add( name );
		}

		return result;
	}

	/// <summary>
	/// Audits raw FBX binary or ASCII bytes.
	/// </summary>
	public static FbxAnimationAuditResult Audit( byte[] fbxBytes, Skeleton? targetSkeleton = null, string? requestedTake = null )
	{
		ArgumentNullException.ThrowIfNull( fbxBytes );
		var root = FbxNode.ParseAny( fbxBytes );
		return Audit( root, targetSkeleton, requestedTake );
	}
}

/// <summary>
/// Converts audited animation data into kernel RigAnimFile types.
/// </summary>
public static class FbxAnimationConverter
{
	public static RigEase MapInterpolationToEase( FbxInterpolationMode mode ) => mode switch
	{
		FbxInterpolationMode.Constant => RigEase.Stepped,
		FbxInterpolationMode.Linear => RigEase.Linear,
		FbxInterpolationMode.Cubic => RigEase.Cubic,
		_ => RigEase.Linear
	};

	/// <summary>
	/// Converts synthetic animation data into a native RigAnimFile.
	/// Translates FBX tick timestamps into integer frames, scales positions according to UnitScale,
	/// converts EulerXYZ to Quat, and derives local bone transforms.
	/// </summary>
	public static RigAnimFile ConvertToRigAnimFile(
		SyntheticFbxAnimationData data,
		Skeleton? targetSkeleton = null,
		int frameRate = 30 )
	{
		ArgumentNullException.ThrowIfNull( data );
		var audit = FbxAnimationAuditor.Audit( data, targetSkeleton );
		if ( audit.HasErrors )
		{
			var firstError = audit.Issues.First( i => i.Severity == FeasibilityIssueSeverity.Error );
			throw new InvalidOperationException( $"Cannot convert FBX animation: {firstError.Feature} on '{firstError.NodeOrTakeName}': {firstError.Message}" );
		}

		var take = data.Takes[0];
		int maxFrame = 0;

		// Applied unit scale factor (converting to Effigy inches: 1 inch = 2.54 cm)
		float scaleFactor = (float)(data.UnitScale / FbxExportAdapter.InchesToCentimetres);

		var clip = new RigAnimFile
		{
			FrameRate = frameRate,
			SourceModel = take.Name
		};

		HashSet<string>? validBoneNames = targetSkeleton != null
			? new HashSet<string>( targetSkeleton.Bones.Select( b => b.Name ), StringComparer.Ordinal )
			: null;

		foreach ( var node in take.Nodes )
		{
			// Clean diagnostic for missing bone target
			if ( validBoneNames != null && !validBoneNames.Contains( node.Name ) )
				continue;

			var track = new RigTrack
			{
				Name = node.Name,
				Kind = RigTrackKind.Bone
			};

			foreach ( var kf in node.Keyframes )
			{
				int frame = FbxAnimationTime.TicksToKernelFrame( kf.TimeTicks, frameRate );
				if ( frame > maxFrame ) maxFrame = frame;

				var pos = kf.Translation * scaleFactor;
				var rot = FbxAnimationMath.EulerToQuat( kf.RotationEuler.x, kf.RotationEuler.y, kf.RotationEuler.z, node.RotationOrder );
				var scl = kf.Scale;

				track.Keys.Add( new RigKey
				{
					Frame = frame,
					Local = new RigPose( pos, rot, scl ),
					Ease = MapInterpolationToEase( kf.Interpolation )
				} );
			}

			// Sort keys chronologically
			track.Keys.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
			clip.Tracks.Add( track );
		}

		clip.FrameCount = maxFrame + 1;
		return clip;
	}
}
