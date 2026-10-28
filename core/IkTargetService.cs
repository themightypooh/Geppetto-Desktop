#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Resolved IK constraint information and declared goal-space anchoring metadata.
/// </summary>
public sealed record IkConstraintInfo
{
	public RigIk Constraint { get; init; } = null!;
	public RigGoalSpace Space { get; init; } = RigGoalSpace.Model;
	public string? AnchorObjectName { get; init; }
	public Xform AnchorTransform { get; init; } = Xform.Identity;
}

/// <summary>
/// Result of attempting to resolve an IK constraint on a rig/clip.
/// </summary>
public sealed record IkResolveResult
{
	[MemberNotNullWhen( true, nameof( Info ) )]
	public bool Success { get; init; }
	public IkConstraintInfo? Info { get; init; }
	public string? Diagnostic { get; init; }

	public static IkResolveResult Found(
		RigIk constraint,
		RigGoalSpace space = RigGoalSpace.Model,
		string? anchorObject = null,
		Xform? anchorTransform = null ) =>
		new()
		{
			Success = true,
			Info = new IkConstraintInfo
			{
				Constraint = constraint ?? throw new ArgumentNullException( nameof( constraint ) ),
				Space = space,
				AnchorObjectName = anchorObject,
				AnchorTransform = anchorTransform ?? Xform.Identity
			}
		};

	public static IkResolveResult Missing( string diagnostic ) =>
		new()
		{
			Success = false,
			Diagnostic = diagnostic
		};
}

/// <summary>
/// Authoritative outcome of an IK solve pass executed through kernel solvers.
/// </summary>
public sealed record IkSolveResult
{
	[MemberNotNullWhen( true, nameof( SolvedWorldPoses ) )]
	[MemberNotNullWhen( true, nameof( SolvedLocalPoses ) )]
	public bool Success { get; init; }
	public Xform[]? SolvedWorldPoses { get; init; }
	public Xform[]? SolvedLocalPoses { get; init; }
	public Vec3 SolvedEndPosition { get; init; }
	public Vec3 TargetPosition { get; init; }
	public float MissDistance { get; init; }
	public bool Reached { get; init; }
	public string? Diagnostic { get; init; }

	public static IkSolveResult Failed( string diagnostic ) =>
		new()
		{
			Success = false,
			Diagnostic = diagnostic
		};
}

/// <summary>
/// Service managing IK target manipulation, coordinate frame transitions (World / Model / Object),
/// kernel solver dispatch, and tentative preview drag sessions adhering to E2 history.
/// </summary>
public static class IkTargetService
{
	/// <summary>
	/// Resolves the live constraint and goal-space anchor for the given bone in the document session.
	/// </summary>
	public static IkResolveResult ResolveConstraint( DocumentSession session, string boneName, float frame = 0f )
	{
		if ( session == null ) return IkResolveResult.Missing( "DocumentSession is null." );
		if ( string.IsNullOrWhiteSpace( boneName ) )
		{
			boneName = session.Selection?.BoneName ?? "";
			if ( string.IsNullOrWhiteSpace( boneName ) )
				return IkResolveResult.Missing( "No bone name was provided and no bone is currently selected." );
		}

		var skeleton = PoseEditService.ResolveSkeleton( session );
		if ( skeleton == null )
			return IkResolveResult.Missing( "DocumentSession has no skeleton/rig available." );

		int boneIndex = skeleton.IndexOf( boneName );
		if ( boneIndex < 0 )
			return IkResolveResult.Missing( $"Bone '{boneName}' does not exist in the skeleton." );

		return ResolveConstraint( session.Clip, boneName, frame, skeleton );
	}

	/// <summary>
	/// Resolves the live constraint and goal-space anchor for the given bone from a clip and skeleton.
	/// </summary>
	public static IkResolveResult ResolveConstraint( RigAnimFile? clip, string boneName, float frame = 0f, Skeleton? skeleton = null )
	{
		if ( string.IsNullOrWhiteSpace( boneName ) )
			return IkResolveResult.Missing( "Bone name cannot be empty." );

		if ( skeleton != null && skeleton.IndexOf( boneName ) < 0 )
			return IkResolveResult.Missing( $"Bone '{boneName}' does not exist in the skeleton." );

		RigIk? constraint = null;
		if ( clip?.Ik != null )
		{
			constraint = clip.Ik.FirstOrDefault( c => string.Equals( c.EndBone, boneName, StringComparison.OrdinalIgnoreCase ) );
		}

		if ( constraint == null )
		{
			return IkResolveResult.Missing( $"No IK constraint found targeting end bone '{boneName}'." );
		}

		if ( skeleton != null )
		{
			int current = skeleton.IndexOf( boneName );
			int depth = 0;
			while ( current >= 0 && depth < constraint.ChainLength )
			{
				current = skeleton.Bones[current].Parent;
				if ( current >= 0 ) depth++;
			}
			if ( depth < constraint.ChainLength )
			{
				return IkResolveResult.Missing( $"End bone '{boneName}' has only {depth} ancestors, but constraint requires {constraint.ChainLength}." );
			}
		}

		RigGoalSpace space = RigGoalSpace.Model;
		string? anchorObject = null;
		Xform anchorXf = Xform.Identity;

		if ( clip != null && clip.GoalSpaces.TryGetValue( boneName, out var anchor ) )
		{
			space = anchor.Space;
			anchorObject = anchor.Object;
			if ( space == RigGoalSpace.Object && !string.IsNullOrEmpty( anchorObject ) )
			{
				var placed = clip.PlacementAt( anchorObject, frame );
				if ( placed.HasValue )
					anchorXf = placed.Value.ToXform();
			}
		}

		return IkResolveResult.Found( constraint, space, anchorObject, anchorXf );
	}

	/// <summary>
	/// Translates a point in model space into the goal's declared coordinate space.
	/// </summary>
	public static Vec3 ModelToGoalSpace( Vec3 targetPosInModel, RigGoalSpace space, Xform? anchorTransformInModel = null, Xform? modelInWorld = null )
	{
		ValidateVec3( targetPosInModel, nameof( targetPosInModel ) );
		return space switch
		{
			RigGoalSpace.Model => targetPosInModel,
			RigGoalSpace.World => (modelInWorld ?? Xform.Identity).TransformPoint( targetPosInModel ),
			RigGoalSpace.Object => (anchorTransformInModel ?? Xform.Identity).Inverse.TransformPoint( targetPosInModel ),
			_ => targetPosInModel
		};
	}

	/// <summary>
	/// Translates a point in the goal's declared coordinate space into model space.
	/// </summary>
	public static Vec3 GoalSpaceToModel( Vec3 targetPosInGoalSpace, RigGoalSpace space, Xform? anchorTransformInModel = null, Xform? modelInWorld = null )
	{
		ValidateVec3( targetPosInGoalSpace, nameof( targetPosInGoalSpace ) );
		return space switch
		{
			RigGoalSpace.Model => targetPosInGoalSpace,
			RigGoalSpace.World => (modelInWorld ?? Xform.Identity).Inverse.TransformPoint( targetPosInGoalSpace ),
			RigGoalSpace.Object => (anchorTransformInModel ?? Xform.Identity).TransformPoint( targetPosInGoalSpace ),
			_ => targetPosInGoalSpace
		};
	}

	/// <summary>
	/// Translates a transform in model space into the goal's declared coordinate space.
	/// </summary>
	public static Xform ModelToGoalSpace( Xform targetInModel, RigGoalSpace space, Xform? anchorTransformInModel = null, Xform? modelInWorld = null )
	{
		ValidateTransform( targetInModel );
		return space switch
		{
			RigGoalSpace.Model => targetInModel,
			RigGoalSpace.World => (modelInWorld ?? Xform.Identity) * targetInModel,
			RigGoalSpace.Object => (anchorTransformInModel ?? Xform.Identity).Inverse * targetInModel,
			_ => targetInModel
		};
	}

	/// <summary>
	/// Translates a transform in the goal's declared coordinate space into model space.
	/// </summary>
	public static Xform GoalSpaceToModel( Xform targetInGoalSpace, RigGoalSpace space, Xform? anchorTransformInModel = null, Xform? modelInWorld = null )
	{
		ValidateTransform( targetInGoalSpace );
		return space switch
		{
			RigGoalSpace.Model => targetInGoalSpace,
			RigGoalSpace.World => (modelInWorld ?? Xform.Identity).Inverse * targetInGoalSpace,
			RigGoalSpace.Object => (anchorTransformInModel ?? Xform.Identity) * targetInGoalSpace,
			_ => targetInGoalSpace
		};
	}

	/// <summary>
	/// Translates a point from world space to model space.
	/// </summary>
	public static Vec3 WorldToModel( Vec3 targetInWorld, Xform modelInWorld )
	{
		ValidateVec3( targetInWorld, nameof( targetInWorld ) );
		ValidateTransform( modelInWorld );
		return modelInWorld.Inverse.TransformPoint( targetInWorld );
	}

	/// <summary>
	/// Translates a point from model space to world space.
	/// </summary>
	public static Vec3 ModelToWorld( Vec3 targetInModel, Xform modelInWorld )
	{
		ValidateVec3( targetInModel, nameof( targetInModel ) );
		ValidateTransform( modelInWorld );
		return modelInWorld.TransformPoint( targetInModel );
	}

	/// <summary>
	/// Vector3 overload for translating model point to declared goal space.
	/// </summary>
	public static Vector3 ModelToGoalSpace( Vector3 targetPosInModel, RigGoalSpace space, Xform? anchorTransformInModel = null, Xform? modelInWorld = null )
	{
		var v = ModelToGoalSpace( new Vec3( targetPosInModel.X, targetPosInModel.Y, targetPosInModel.Z ), space, anchorTransformInModel, modelInWorld );
		return new Vector3( v.x, v.y, v.z );
	}

	/// <summary>
	/// Vector3 overload for translating declared goal space point to model space.
	/// </summary>
	public static Vector3 GoalSpaceToModel( Vector3 targetPosInGoalSpace, RigGoalSpace space, Xform? anchorTransformInModel = null, Xform? modelInWorld = null )
	{
		var v = GoalSpaceToModel( new Vec3( targetPosInGoalSpace.X, targetPosInGoalSpace.Y, targetPosInGoalSpace.Z ), space, anchorTransformInModel, modelInWorld );
		return new Vector3( v.x, v.y, v.z );
	}

	/// <summary>
	/// Solves the IK chain using the kernel ChainIk solver and optional RigLimits.
	/// Out-of-reach targets produce clamped results with measurable miss distance without NaNs or crashes.
	/// Does not mutate the skeleton bind pose or stored constraint.
	/// </summary>
	public static IkSolveResult Solve(
		Skeleton skeleton,
		IReadOnlyList<Xform> worldPoses,
		RigIk constraint,
		RigPose goalInModel,
		IReadOnlyList<RigLimit>? limits = null,
		float tolerance = 0.05f )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		ArgumentNullException.ThrowIfNull( worldPoses );
		ArgumentNullException.ThrowIfNull( constraint );

		if ( !float.IsFinite( goalInModel.Position.x ) || !float.IsFinite( goalInModel.Position.y ) || !float.IsFinite( goalInModel.Position.z ) )
			return IkSolveResult.Failed( "Goal position coordinates must be finite numbers." );

		if ( !float.IsFinite( goalInModel.Rotation.x ) || !float.IsFinite( goalInModel.Rotation.y ) ||
			 !float.IsFinite( goalInModel.Rotation.z ) || !float.IsFinite( goalInModel.Rotation.w ) )
			return IkSolveResult.Failed( "Goal rotation quaternion coordinates must be finite numbers." );

		int end = skeleton.IndexOf( constraint.EndBone );
		if ( end < 0 )
			return IkSolveResult.Failed( $"End bone '{constraint.EndBone}' not found in skeleton." );

		if ( worldPoses.Count < skeleton.Count )
			return IkSolveResult.Failed( $"World poses buffer size ({worldPoses.Count}) is smaller than skeleton bone count ({skeleton.Count})." );

		// Execute solver on a copy or directly (ChainIk.Apply does not mutate input worldPoses or constraint).
		var solvedWorld = ChainIk.Apply( skeleton, worldPoses, constraint, goalInModel, limits );
		if ( solvedWorld == null )
		{
			return IkSolveResult.Failed( "Kernel ChainIk solver returned no solution (chain degenerate, disabled, or target coincides with root)." );
		}

		// Ensure no NaNs were produced
		for ( int b = 0; b < skeleton.Count; b++ )
		{
			if ( !PoseEditService.IsValidTransform( solvedWorld[b] ) )
				return IkSolveResult.Failed( $"Solver produced invalid or non-finite transform for bone '{skeleton.Bones[b].Name}'." );
		}

		var solvedLocal = RigAnimFile.LocalSpace( skeleton, solvedWorld );
		var solvedEndPos = solvedWorld[end].Origin;
		float miss = (solvedEndPos - goalInModel.Position).Length;
		bool reached = miss <= tolerance;

		return new IkSolveResult
		{
			Success = true,
			SolvedWorldPoses = solvedWorld,
			SolvedLocalPoses = solvedLocal,
			SolvedEndPosition = solvedEndPos,
			TargetPosition = goalInModel.Position,
			MissDistance = miss,
			Reached = reached
		};
	}

	/// <summary>
	/// Solves closed-form two-bone IK using the kernel TwoBoneIk solver.
	/// </summary>
	public static IkSolveResult SolveTwoBone(
		Skeleton skeleton,
		IReadOnlyList<Xform> worldPoses,
		int endBoneIndex,
		Vec3 targetInModel,
		Vec3 poleDirection,
		float softness = 0f,
		float tolerance = 0.05f )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		ArgumentNullException.ThrowIfNull( worldPoses );

		if ( endBoneIndex < 0 || endBoneIndex >= skeleton.Count )
			return IkSolveResult.Failed( $"Invalid end bone index {endBoneIndex}." );

		if ( !float.IsFinite( targetInModel.x ) || !float.IsFinite( targetInModel.y ) || !float.IsFinite( targetInModel.z ) )
			return IkSolveResult.Failed( "Target coordinates must be finite numbers." );

		if ( !float.IsFinite( poleDirection.x ) || !float.IsFinite( poleDirection.y ) || !float.IsFinite( poleDirection.z ) )
			return IkSolveResult.Failed( "Pole direction coordinates must be finite numbers." );

		var solvedWorld = TwoBoneIk.Apply( skeleton, worldPoses, endBoneIndex, targetInModel, poleDirection, softness );
		if ( solvedWorld == null )
		{
			return IkSolveResult.Failed( "TwoBoneIk solver returned no solution (chain degenerate or target on root)." );
		}

		for ( int b = 0; b < skeleton.Count; b++ )
		{
			if ( !PoseEditService.IsValidTransform( solvedWorld[b] ) )
				return IkSolveResult.Failed( $"Solver produced invalid or non-finite transform for bone '{skeleton.Bones[b].Name}'." );
		}

		var solvedLocal = RigAnimFile.LocalSpace( skeleton, solvedWorld );
		var solvedEndPos = solvedWorld[endBoneIndex].Origin;
		float miss = (solvedEndPos - targetInModel).Length;
		bool reached = miss <= tolerance;

		return new IkSolveResult
		{
			Success = true,
			SolvedWorldPoses = solvedWorld,
			SolvedLocalPoses = solvedLocal,
			SolvedEndPosition = solvedEndPos,
			TargetPosition = targetInModel,
			MissDistance = miss,
			Reached = reached
		};
	}

	/// <summary>
	/// Begins an IK preview drag session with E2 history integration, pausing transport and deriving
	/// all tentative solutions from initial pose.
	/// </summary>
	public static IkDragSession BeginIkDrag(
		DocumentSession session,
		string boneName,
		MarionettePlayer? player = null,
		RigIk? explicitConstraint = null,
		RigGoalSpace? explicitSpace = null,
		Xform? anchorTransform = null,
		Xform? modelInWorld = null,
		IReadOnlyList<RigLimit>? explicitLimits = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( string.IsNullOrWhiteSpace( boneName ) )
		{
			boneName = session.Selection?.BoneName
				?? throw new ArgumentException( "No bone name provided and no bone is currently selected.", nameof( boneName ) );
		}

		var skeleton = PoseEditService.ResolveSkeleton( session, player )
			?? throw new InvalidOperationException( "DocumentSession has no skeleton/rig available to pose." );

		int boneIndex = skeleton.IndexOf( boneName );
		if ( boneIndex < 0 )
			throw new ArgumentException( $"Bone '{boneName}' was not found in the skeleton hierarchy.", nameof( boneName ) );

		RigIk constraint;
		RigGoalSpace space = explicitSpace ?? RigGoalSpace.Model;
		string? anchorObj = null;
		Xform anchorXf = anchorTransform ?? Xform.Identity;

		if ( explicitConstraint != null )
		{
			constraint = explicitConstraint;
		}
		else
		{
			var resolveRes = ResolveConstraint( session, boneName );
			if ( !resolveRes.Success )
				throw new InvalidOperationException( resolveRes.Diagnostic ?? "Failed to resolve IK constraint." );

			constraint = resolveRes.Info!.Constraint;
			if ( !explicitSpace.HasValue ) space = resolveRes.Info.Space;
			if ( !anchorTransform.HasValue ) anchorXf = resolveRes.Info.AnchorTransform;
			anchorObj = resolveRes.Info.AnchorObjectName;
		}

		var limits = explicitLimits ?? session.Clip?.Limits;
		return new IkDragSession(
			session,
			player,
			skeleton,
			boneName,
			boneIndex,
			constraint,
			space,
			anchorObj,
			anchorXf,
			modelInWorld ?? Xform.Identity,
			limits );
	}

	/// <summary>
	/// Safely attempts to begin an IK drag session, producing an explicit diagnostic on failure.
	/// </summary>
	public static bool TryBeginIkDrag(
		DocumentSession session,
		string boneName,
		[NotNullWhen( true )] out IkDragSession? dragSession,
		out string? diagnostic,
		MarionettePlayer? player = null,
		RigIk? explicitConstraint = null,
		RigGoalSpace? explicitSpace = null,
		Xform? anchorTransform = null,
		Xform? modelInWorld = null,
		IReadOnlyList<RigLimit>? explicitLimits = null )
	{
		dragSession = null;
		diagnostic = null;

		if ( session == null )
		{
			diagnostic = "DocumentSession is null.";
			return false;
		}

		if ( string.IsNullOrWhiteSpace( boneName ) )
		{
			boneName = session.Selection?.BoneName ?? "";
			if ( string.IsNullOrWhiteSpace( boneName ) )
			{
				diagnostic = "No bone name provided and no bone selected.";
				return false;
			}
		}

		var skeleton = PoseEditService.ResolveSkeleton( session, player );
		if ( skeleton == null )
		{
			diagnostic = "No skeleton/rig available in session or player.";
			return false;
		}

		int boneIndex = skeleton.IndexOf( boneName );
		if ( boneIndex < 0 )
		{
			diagnostic = $"Bone '{boneName}' not found in skeleton.";
			return false;
		}

		RigIk? constraint = explicitConstraint;
		RigGoalSpace space = explicitSpace ?? RigGoalSpace.Model;
		string? anchorObj = null;
		Xform anchorXf = anchorTransform ?? Xform.Identity;

		if ( constraint == null )
		{
			var resolveRes = ResolveConstraint( session, boneName );
			if ( !resolveRes.Success )
			{
				diagnostic = resolveRes.Diagnostic ?? "Missing IK constraint.";
				return false;
			}
			constraint = resolveRes.Info!.Constraint;
			if ( !explicitSpace.HasValue ) space = resolveRes.Info.Space;
			if ( !anchorTransform.HasValue ) anchorXf = resolveRes.Info.AnchorTransform;
			anchorObj = resolveRes.Info.AnchorObjectName;
		}

		try
		{
			var limits = explicitLimits ?? session.Clip?.Limits;
			dragSession = new IkDragSession(
				session,
				player,
				skeleton,
				boneName,
				boneIndex,
				constraint,
				space,
				anchorObj,
				anchorXf,
				modelInWorld ?? Xform.Identity,
				limits );
			return true;
		}
		catch ( Exception ex )
		{
			diagnostic = ex.Message;
			return false;
		}
	}

	static void ValidateVec3( Vec3 v, string paramName )
	{
		if ( !float.IsFinite( v.x ) || !float.IsFinite( v.y ) || !float.IsFinite( v.z ) )
			throw new ArgumentException( "Vector coordinates must be finite numbers.", paramName );
	}

	static void ValidateTransform( Xform xf )
	{
		if ( !PoseEditService.IsValidTransform( xf ) )
			throw new ArgumentException( "Transform coordinates must be finite and basis non-singular." );
	}
}

/// <summary>
/// Active IK target preview drag session. Derive all tentative updates from start pose,
/// solves using kernel solvers, updates player buffers / GPU palette for interactive picking,
/// and commits 1 undo entry or cleanly cancels.
/// </summary>
public sealed class IkDragSession : IDisposable
{
	readonly DocumentSession _session;
	readonly MarionettePlayer? _player;
	readonly Skeleton _skeleton;
	readonly string _boneName;
	readonly int _endBoneIndex;
	readonly RigIk _constraint;
	readonly RigGoalSpace _goalSpace;
	readonly string? _anchorObjectName;
	Xform _anchorTransform;
	Xform _modelInWorld;
	readonly IReadOnlyList<RigLimit>? _limits;

	readonly Xform[] _startLocalPoses;
	readonly Xform[] _startWorldPoses;
	readonly Xform[] _currentLocalPoses;
	readonly Xform[] _currentWorldPoses;

	readonly EditTransaction _transaction;
	readonly SessionSelection _startSelection;
	readonly bool _wasPlaying;
	readonly bool _wasSessionDirty;
	bool _finished;

	public DocumentSession Session => _session;
	public MarionettePlayer? Player => _player;
	public Skeleton Skeleton => _skeleton;
	public string BoneName => _boneName;
	public int EndBoneIndex => _endBoneIndex;
	public RigIk Constraint => _constraint;
	public RigGoalSpace GoalSpace => _goalSpace;
	public string? AnchorObjectName => _anchorObjectName;
	public Xform AnchorTransform => _anchorTransform;
	public Xform ModelInWorld => _modelInWorld;
	public bool IsFinished => _finished;

	public Vec3 StartTargetInModel { get; }
	public Vec3 StartTargetInGoalSpace { get; }
	public Vec3 CurrentTargetInModel { get; private set; }
	public Vec3 CurrentTargetInGoalSpace { get; private set; }
	public Quat? CurrentTargetRotationInModel { get; private set; }
	public float CurrentMissDistance { get; private set; }
	public bool CurrentReached { get; private set; }

	public ReadOnlySpan<Xform> LocalPoses => _currentLocalPoses.AsSpan( 0, _skeleton.Count );
	public ReadOnlySpan<Xform> WorldPoses => _currentWorldPoses.AsSpan( 0, _skeleton.Count );
	public Xform CurrentEndBoneWorldPose => _currentWorldPoses[_endBoneIndex];

	internal IkDragSession(
		DocumentSession session,
		MarionettePlayer? player,
		Skeleton skeleton,
		string boneName,
		int endBoneIndex,
		RigIk constraint,
		RigGoalSpace goalSpace,
		string? anchorObjectName,
		Xform anchorTransform,
		Xform modelInWorld,
		IReadOnlyList<RigLimit>? limits )
	{
		_session = session ?? throw new ArgumentNullException( nameof( session ) );
		_player = player;
		_skeleton = skeleton ?? throw new ArgumentNullException( nameof( skeleton ) );
		_boneName = boneName;
		_endBoneIndex = endBoneIndex;
		_constraint = constraint ?? throw new ArgumentNullException( nameof( constraint ) );
		_goalSpace = goalSpace;
		_anchorObjectName = anchorObjectName;
		_anchorTransform = anchorTransform;
		_modelInWorld = modelInWorld;
		_limits = limits;

		int count = skeleton.Count;
		_startLocalPoses = new Xform[count];
		_startWorldPoses = new Xform[count];
		_currentLocalPoses = new Xform[count];
		_currentWorldPoses = new Xform[count];

		// Transport handling
		_wasPlaying = (player?.Playing ?? false) || session.IsPlaying;
		if ( player != null ) player.Playing = false;
		session.IsPlaying = false;

		_wasSessionDirty = session.IsDirty;
		_startSelection = session.Selection;

		if ( player != null )
		{
			var playerLocal = MarionettePlayerInternals.GetLocalPose( player );
			var playerWorld = MarionettePlayerInternals.GetWorldPose( player );
			Array.Copy( playerLocal, _startLocalPoses, count );
			Array.Copy( playerWorld, _startWorldPoses, count );
		}
		else
		{
			for ( int b = 0; b < count; b++ )
			{
				_startLocalPoses[b] = skeleton.Bones[b].Local;
				int parent = skeleton.Bones[b].Parent;
				_startWorldPoses[b] = parent >= 0 ? _startWorldPoses[parent] * _startLocalPoses[b] : _startLocalPoses[b];
			}
		}

		Array.Copy( _startLocalPoses, _currentLocalPoses, count );
		Array.Copy( _startWorldPoses, _currentWorldPoses, count );

		StartTargetInModel = _startWorldPoses[endBoneIndex].Origin;
		StartTargetInGoalSpace = IkTargetService.ModelToGoalSpace( StartTargetInModel, _goalSpace, _anchorTransform, _modelInWorld );
		CurrentTargetInModel = StartTargetInModel;
		CurrentTargetInGoalSpace = StartTargetInGoalSpace;
		CurrentTargetRotationInModel = Quat.FromXform( _startWorldPoses[endBoneIndex] );
		CurrentMissDistance = 0f;
		CurrentReached = true;

		_transaction = session.History.BeginTransaction(
			$"IK drag {boneName}",
			restoreBefore: () => RestoreToPose( _startLocalPoses, _startWorldPoses )
		);
	}

	/// <summary>Attach a goal/weight key command so the IK solve and the keys it implies commit
	/// as one undo entry (G4 auto-key). Left unjoined, the drag stays an unkeyed override.</summary>
	public void JoinCommand( IEditCommand command )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		_transaction.JoinCommand( command );
	}

	/// <summary>
	/// Updates the preview by moving the IK target to a specific position (and optional rotation) in model space.
	/// Derives the tentative solution from the drag start pose.
	/// </summary>
	public bool UpdatePreview( Vec3 targetInModel, Quat? targetRotationInModel = null )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		if ( !float.IsFinite( targetInModel.x ) || !float.IsFinite( targetInModel.y ) || !float.IsFinite( targetInModel.z ) )
			throw new ArgumentException( "Target coordinates must be finite.", nameof( targetInModel ) );

		CurrentTargetInModel = targetInModel;
		CurrentTargetInGoalSpace = IkTargetService.ModelToGoalSpace( targetInModel, _goalSpace, _anchorTransform, _modelInWorld );
		CurrentTargetRotationInModel = targetRotationInModel ?? Quat.FromXform( _startWorldPoses[_endBoneIndex] );

		var goal = new RigPose( targetInModel, CurrentTargetRotationInModel.Value, Vec3.One );
		var res = IkTargetService.Solve( _skeleton, _startWorldPoses, _constraint, goal, _limits );
		if ( !res.Success )
			return false;

		Array.Copy( res.SolvedLocalPoses!, _currentLocalPoses, _skeleton.Count );
		Array.Copy( res.SolvedWorldPoses!, _currentWorldPoses, _skeleton.Count );
		CurrentMissDistance = res.MissDistance;
		CurrentReached = res.Reached;

		_transaction.UpdatePreview( () => ApplyPosesToPlayer( _currentLocalPoses, _currentWorldPoses ) );
		return true;
	}

	/// <summary>
	/// Vector3 overload for updating preview in model space.
	/// </summary>
	public bool UpdatePreview( Vector3 targetInModel ) =>
		UpdatePreview( new Vec3( targetInModel.X, targetInModel.Y, targetInModel.Z ) );

	/// <summary>
	/// Updates the preview using a target defined in the goal's declared coordinate space.
	/// </summary>
	public bool UpdatePreviewGoalInGoalSpace( Vec3 targetInGoalSpace, Quat? targetRotationInGoalSpace = null )
	{
		var targetInModel = IkTargetService.GoalSpaceToModel( targetInGoalSpace, _goalSpace, _anchorTransform, _modelInWorld );
		Quat? targetRotInModel = targetRotationInGoalSpace.HasValue
			? Quat.FromXform( IkTargetService.GoalSpaceToModel( targetRotationInGoalSpace.Value.ToXform( Vec3.Zero, Vec3.One ), _goalSpace, _anchorTransform, _modelInWorld ) )
			: null;
		return UpdatePreview( targetInModel, targetRotInModel );
	}

	/// <summary>
	/// Vector3 overload for updating preview in goal's declared space.
	/// </summary>
	public bool UpdatePreviewGoalInGoalSpace( Vector3 targetInGoalSpace ) =>
		UpdatePreviewGoalInGoalSpace( new Vec3( targetInGoalSpace.X, targetInGoalSpace.Y, targetInGoalSpace.Z ) );

	/// <summary>
	/// Updates preview from world space coordinates.
	/// </summary>
	public bool UpdatePreviewWorldTarget( Vec3 targetInWorld, Quat? targetRotationInWorld = null )
	{
		var targetInModel = IkTargetService.WorldToModel( targetInWorld, _modelInWorld );
		Quat? targetRotInModel = targetRotationInWorld.HasValue
			? Quat.FromXform( _modelInWorld.Inverse * targetRotationInWorld.Value.ToXform( Vec3.Zero, Vec3.One ) )
			: null;
		return UpdatePreview( targetInModel, targetRotInModel );
	}

	/// <summary>
	/// Vector3 overload for updating preview from world space.
	/// </summary>
	public bool UpdatePreviewWorldTarget( Vector3 targetInWorld ) =>
		UpdatePreviewWorldTarget( new Vec3( targetInWorld.X, targetInWorld.Y, targetInWorld.Z ) );

	/// <summary>
	/// Translates the target in model space by adding a delta offset to the starting target position.
	/// </summary>
	public bool UpdatePreviewTranslationDelta( Vec3 deltaInModel ) =>
		UpdatePreview( StartTargetInModel + deltaInModel );

	/// <summary>
	/// Vector3 overload for delta translation preview.
	/// </summary>
	public bool UpdatePreviewTranslationDelta( Vector3 deltaInModel ) =>
		UpdatePreviewTranslationDelta( new Vec3( deltaInModel.X, deltaInModel.Y, deltaInModel.Z ) );

	/// <summary>
	/// Updates the anchor object's transform in model space (e.g. when an object moves under an anchored target).
	/// Re-evaluates the preview at the new anchored position.
	/// </summary>
	public bool UpdateAnchorTransform( Xform newAnchorTransform )
	{
		PoseEditService.IsValidTransform( newAnchorTransform );
		_anchorTransform = newAnchorTransform;
		if ( _goalSpace == RigGoalSpace.Object )
		{
			var newTargetInModel = _anchorTransform.TransformPoint( CurrentTargetInGoalSpace );
			return UpdatePreview( newTargetInModel, CurrentTargetRotationInModel );
		}
		return true;
	}

	/// <summary>
	/// Commits the drag session into EditHistory as one undoable command.
	/// </summary>
	public void Commit()
	{
		if ( _finished ) return;
		_finished = true;

		var finalLocal = (Xform[])_currentLocalPoses.Clone();
		var finalWorld = (Xform[])_currentWorldPoses.Clone();
		bool isNoOp = AreAllApproximatelyEqual( finalLocal, _startLocalPoses );

		_transaction.Commit(
			applyFinal: () =>
			{
				Array.Copy( finalLocal, _currentLocalPoses, _skeleton.Count );
				Array.Copy( finalWorld, _currentWorldPoses, _skeleton.Count );
				ApplyPosesToPlayer( finalLocal, finalWorld );
				if ( _player != null ) _player.Playing = false;
				_session.IsPlaying = false;
			},
			isNoOp: () => isNoOp
		);

		if ( isNoOp && !_wasSessionDirty )
		{
			_session.SavedRevision = _session.Revision;
		}
	}

	/// <summary>
	/// Cancels the drag session and restores initial poses without creating an undo entry.
	/// </summary>
	public void Cancel()
	{
		if ( _finished ) return;
		_finished = true;

		_transaction.Cancel();

		if ( !_wasSessionDirty )
		{
			_session.SavedRevision = _session.Revision;
		}
	}

	public void Dispose()
	{
		if ( !_finished )
		{
			Cancel();
		}
	}

	void ApplyPosesToPlayer( Xform[] localPoses, Xform[] worldPoses )
	{
		if ( _player == null ) return;
		var playerLocal = MarionettePlayerInternals.GetLocalPose( _player );
		var playerWorld = MarionettePlayerInternals.GetWorldPose( _player );
		var playerGpu = MarionettePlayerInternals.GetGpuBones( _player );
		var playerInvBind = MarionettePlayerInternals.GetInvBindWorld( _player );

		int count = Math.Min( _skeleton.Count, playerLocal.Length );
		Array.Copy( localPoses, playerLocal, count );
		Array.Copy( worldPoses, playerWorld, count );

		for ( int i = 0; i < count && i < playerGpu.Length; i++ )
		{
			playerGpu[i] = MarionettePlayer.ToGlMatrix( playerWorld[i] * playerInvBind[i] );
		}
	}

	void RestoreToPose( Xform[] startLocalPoses, Xform[] startWorldPoses )
	{
		Array.Copy( startLocalPoses, _currentLocalPoses, _skeleton.Count );
		Array.Copy( startWorldPoses, _currentWorldPoses, _skeleton.Count );
		ApplyPosesToPlayer( startLocalPoses, startWorldPoses );
		if ( _player != null ) _player.Playing = _wasPlaying;
		_session.IsPlaying = _wasPlaying;
		_session.Selection = _startSelection;
	}

	static bool AreAllApproximatelyEqual( ReadOnlySpan<Xform> a, ReadOnlySpan<Xform> b, float epsilon = 1e-4f )
	{
		if ( a.Length != b.Length ) return false;
		for ( int i = 0; i < a.Length; i++ )
		{
			if ( !PoseEditService.AreApproximatelyEqual( a[i], b[i], epsilon ) )
				return false;
		}
		return true;
	}
}
