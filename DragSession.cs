#nullable enable
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Effigy;

namespace Marionette.Desktop;

internal static class MarionettePlayerInternals
{
	[UnsafeAccessor( UnsafeAccessorKind.Field, Name = "_localPose" )]
	public extern static ref Xform[] GetLocalPose( MarionettePlayer player );

	[UnsafeAccessor( UnsafeAccessorKind.Field, Name = "_worldPose" )]
	public extern static ref Xform[] GetWorldPose( MarionettePlayer player );

	[UnsafeAccessor( UnsafeAccessorKind.Field, Name = "_gpuBones" )]
	public extern static ref Matrix4x4[] GetGpuBones( MarionettePlayer player );

	[UnsafeAccessor( UnsafeAccessorKind.Field, Name = "_invBindWorld" )]
	public extern static ref Xform[] GetInvBindWorld( MarionettePlayer player );

	[UnsafeAccessor( UnsafeAccessorKind.Field, Name = "_bindLocal" )]
	public extern static ref Xform[] GetBindLocal( MarionettePlayer player );

	[UnsafeAccessor( UnsafeAccessorKind.Field, Name = "_bindWorld" )]
	public extern static ref Xform[] GetBindWorld( MarionettePlayer player );
}

/// <summary>
/// Tentative pose manipulation session with history integration and cancel/commit semantics.
/// Pauses playback during active interaction, derives every preview from the initial start state,
/// and recomputes descendants and the GPU palette without modifying bind poses.
/// </summary>
public sealed class DragSession : IDisposable
{
	readonly DocumentSession _session;
	readonly MarionettePlayer? _player;
	readonly Skeleton _skeleton;
	readonly int _boneIndex;
	readonly int _parentIndex;
	readonly bool[] _isDescendant;

	readonly Xform[] _startLocalPoses;
	readonly Xform[] _startWorldPoses;
	readonly Xform[] _localPoses;
	readonly Xform[] _worldPoses;

	readonly EditTransaction _transaction;
	readonly SessionSelection _startSelection;
	readonly bool _wasPlaying;
	readonly bool _wasSessionDirty;
	bool _finished;

	public DocumentSession Session => _session;
	public MarionettePlayer? Player => _player;
	public Skeleton Skeleton => _skeleton;
	public string BoneName { get; }
	public int BoneIndex => _boneIndex;
	public int ParentIndex => _parentIndex;
	public bool IsFinished => _finished;

	public Xform StartLocalPose => _startLocalPoses[_boneIndex];
	public Xform StartWorldPose => _startWorldPoses[_boneIndex];
	public Xform CurrentLocalPose => _localPoses[_boneIndex];
	public Xform CurrentWorldPose => _worldPoses[_boneIndex];

	public ReadOnlySpan<Xform> LocalPoses => _localPoses.AsSpan( 0, _skeleton.Count );
	public ReadOnlySpan<Xform> WorldPoses => _worldPoses.AsSpan( 0, _skeleton.Count );

	DragSession( DocumentSession session, MarionettePlayer? player, Skeleton skeleton, string boneName, int boneIndex )
	{
		_session = session ?? throw new ArgumentNullException( nameof( session ) );
		_player = player;
		_skeleton = skeleton ?? throw new ArgumentNullException( nameof( skeleton ) );
		BoneName = boneName;
		_boneIndex = boneIndex;
		_parentIndex = skeleton.Bones[boneIndex].Parent;

		int count = skeleton.Count;
		_isDescendant = new bool[count];
		_isDescendant[boneIndex] = true;
		for ( int i = boneIndex + 1; i < count; i++ )
		{
			int p = skeleton.Bones[i].Parent;
			if ( p >= 0 && _isDescendant[p] )
				_isDescendant[i] = true;
		}

		_startLocalPoses = new Xform[count];
		_startWorldPoses = new Xform[count];
		_localPoses = new Xform[count];
		_worldPoses = new Xform[count];

		// Transport handling: capture playback state and pause
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

		Array.Copy( _startLocalPoses, _localPoses, count );
		Array.Copy( _startWorldPoses, _worldPoses, count );

		Xform initialLocal = _startLocalPoses[boneIndex];

		// Begin history transaction
		_transaction = session.History.BeginTransaction(
			$"Pose bone {boneName}",
			restoreBefore: () => RestoreToPose( initialLocal )
		);
	}

	public static DragSession BeginPoseDrag( DocumentSession session, string boneName, MarionettePlayer? player = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( string.IsNullOrWhiteSpace( boneName ) )
		{
			boneName = session.Selection.BoneName
				?? throw new ArgumentException( "No bone name provided and no bone is currently selected.", nameof( boneName ) );
		}

		var skeleton = player?.Rig
			?? session.Studio?.Rig
			?? session.FbxSource?.Skeleton
			?? session.CurrentSnapshot?.Mesh.Rig
			?? throw new InvalidOperationException( "DocumentSession has no skeleton/rig available to pose." );

		if ( skeleton.Count == 0 )
			throw new InvalidOperationException( "Skeleton has no bones." );

		int boneIndex = skeleton.IndexOf( boneName );
		if ( boneIndex < 0 )
			throw new ArgumentException( $"Bone '{boneName}' was not found in the skeleton hierarchy.", nameof( boneName ) );

		return new DragSession( session, player, skeleton, boneName, boneIndex );
	}

	/// <summary>Attach a clip-key command so the pose override and the key it implies commit as
	/// one undo entry (G4 auto-key). Left unjoined, the drag stays a pure unkeyed pose override.</summary>
	public void JoinCommand( IEditCommand command )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		_transaction.JoinCommand( command );
	}

	public void UpdatePreviewWorldTransform( Xform desiredWorld )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		ValidateTransform( desiredWorld );

		Xform parentWorld = _parentIndex >= 0 ? _worldPoses[_parentIndex] : Xform.Identity;
		ValidateTransform( parentWorld );

		Xform desiredLocal = parentWorld.Inverse * desiredWorld;
		ValidateTransform( desiredLocal );

		_transaction.UpdatePreview( () => ApplyLocal( desiredLocal ) );
	}

	public void UpdatePreviewLocalTransform( Xform desiredLocal )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		ValidateTransform( desiredLocal );

		_transaction.UpdatePreview( () => ApplyLocal( desiredLocal ) );
	}

	public void UpdatePreviewTranslation( Vec3 deltaWorld )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		if ( !float.IsFinite( deltaWorld.x ) || !float.IsFinite( deltaWorld.y ) || !float.IsFinite( deltaWorld.z ) )
			throw new ArgumentException( "Translation delta coordinates must be finite numbers.", nameof( deltaWorld ) );

		var startWorld = _startWorldPoses[_boneIndex];
		var desiredOrigin = startWorld.Origin + deltaWorld;
		var desiredWorld = new Xform( startWorld.X, startWorld.Y, startWorld.Z, desiredOrigin );
		UpdatePreviewWorldTransform( desiredWorld );
	}

	public void UpdatePreviewTranslation( Vector3 deltaWorld ) =>
		UpdatePreviewTranslation( new Vec3( deltaWorld.X, deltaWorld.Y, deltaWorld.Z ) );

	public void UpdatePreviewRotation( Vec3 axisDirection, float radians )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		if ( !float.IsFinite( radians ) )
			throw new ArgumentException( "Rotation angle must be finite.", nameof( radians ) );
		if ( !float.IsFinite( axisDirection.x ) || !float.IsFinite( axisDirection.y ) || !float.IsFinite( axisDirection.z ) )
			throw new ArgumentException( "Rotation axis coordinates must be finite numbers.", nameof( axisDirection ) );
		if ( axisDirection.LengthSquared < 1e-7f )
			throw new ArgumentException( "Rotation axis cannot be zero length.", nameof( axisDirection ) );

		var startWorld = _startWorldPoses[_boneIndex];
		var head = startWorld.Origin;
		var rot = Xform.RotateAbout( head, axisDirection, radians );
		var desiredWorld = rot * startWorld;
		UpdatePreviewWorldTransform( desiredWorld );
	}

	public void UpdatePreviewRotation( Vector3 axisDirection, float radians ) =>
		UpdatePreviewRotation( new Vec3( axisDirection.X, axisDirection.Y, axisDirection.Z ), radians );

	public void UpdatePreviewRotation( Quaternion worldRotationDelta )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		if ( !float.IsFinite( worldRotationDelta.X ) || !float.IsFinite( worldRotationDelta.Y ) ||
			 !float.IsFinite( worldRotationDelta.Z ) || !float.IsFinite( worldRotationDelta.W ) )
			throw new ArgumentException( "Quaternion coordinates must be finite numbers.", nameof( worldRotationDelta ) );

		float lenSq = worldRotationDelta.LengthSquared();
		if ( lenSq < 1e-7f )
			throw new ArgumentException( "Quaternion cannot be zero length.", nameof( worldRotationDelta ) );

		var startWorld = _startWorldPoses[_boneIndex];
		var rotMat = Matrix4x4.CreateFromQuaternion( worldRotationDelta );
		var head = startWorld.Origin;

		var rx = Vector3.Transform( new Vector3( startWorld.X.x, startWorld.X.y, startWorld.X.z ), rotMat );
		var ry = Vector3.Transform( new Vector3( startWorld.Y.x, startWorld.Y.y, startWorld.Y.z ), rotMat );
		var rz = Vector3.Transform( new Vector3( startWorld.Z.x, startWorld.Z.y, startWorld.Z.z ), rotMat );

		var desiredWorld = new Xform(
			new Vec3( rx.X, rx.Y, rx.Z ),
			new Vec3( ry.X, ry.Y, ry.Z ),
			new Vec3( rz.X, rz.Y, rz.Z ),
			head );

		UpdatePreviewWorldTransform( desiredWorld );
	}

	public void UpdatePreview( Vec3 deltaTranslationWorld, Vec3 rotationAxisWorld, float rotationAngleRadians )
	{
		if ( _finished ) throw new InvalidOperationException( "Drag session has ended." );
		if ( !float.IsFinite( deltaTranslationWorld.x ) || !float.IsFinite( deltaTranslationWorld.y ) || !float.IsFinite( deltaTranslationWorld.z ) )
			throw new ArgumentException( "Translation delta coordinates must be finite numbers.", nameof( deltaTranslationWorld ) );
		if ( !float.IsFinite( rotationAngleRadians ) )
			throw new ArgumentException( "Rotation angle must be finite.", nameof( rotationAngleRadians ) );
		if ( !float.IsFinite( rotationAxisWorld.x ) || !float.IsFinite( rotationAxisWorld.y ) || !float.IsFinite( rotationAxisWorld.z ) )
			throw new ArgumentException( "Rotation axis coordinates must be finite numbers.", nameof( rotationAxisWorld ) );
		if ( rotationAxisWorld.LengthSquared < 1e-7f )
			throw new ArgumentException( "Rotation axis cannot be zero length.", nameof( rotationAxisWorld ) );

		var startWorld = _startWorldPoses[_boneIndex];
		var head = startWorld.Origin;
		var rot = Xform.RotateAbout( head, rotationAxisWorld, rotationAngleRadians );
		var rotatedWorld = rot * startWorld;
		var desiredWorld = new Xform( rotatedWorld.X, rotatedWorld.Y, rotatedWorld.Z, rotatedWorld.Origin + deltaTranslationWorld );
		UpdatePreviewWorldTransform( desiredWorld );
	}

	public void UpdatePreview( Vector3 deltaTranslationWorld, Vector3 rotationAxisWorld, float rotationAngleRadians ) =>
		UpdatePreview(
			new Vec3( deltaTranslationWorld.X, deltaTranslationWorld.Y, deltaTranslationWorld.Z ),
			new Vec3( rotationAxisWorld.X, rotationAxisWorld.Y, rotationAxisWorld.Z ),
			rotationAngleRadians );

	void ApplyLocal( Xform localPose )
	{
		_localPoses[_boneIndex] = localPose;
		if ( _player != null )
		{
			var playerLocal = MarionettePlayerInternals.GetLocalPose( _player );
			playerLocal[_boneIndex] = localPose;
		}

		RecomputeDescendants();
	}

	void RecomputeDescendants()
	{
		int count = _skeleton.Count;
		var playerWorld = _player != null ? MarionettePlayerInternals.GetWorldPose( _player ) : null;
		var playerGpu = _player != null ? MarionettePlayerInternals.GetGpuBones( _player ) : null;
		var playerInvBind = _player != null ? MarionettePlayerInternals.GetInvBindWorld( _player ) : null;

		for ( int i = _boneIndex; i < count; i++ )
		{
			if ( !_isDescendant[i] ) continue;
			int p = _skeleton.Bones[i].Parent;
			_worldPoses[i] = p >= 0 ? _worldPoses[p] * _localPoses[i] : _localPoses[i];

			if ( _player != null && playerWorld != null )
			{
				playerWorld[i] = _worldPoses[i];
				if ( playerGpu != null && playerInvBind != null && i < playerGpu.Length )
				{
					playerGpu[i] = MarionettePlayer.ToGlMatrix( _worldPoses[i] * playerInvBind[i] );
				}
			}
		}
	}

	void RestoreToPose( Xform localPose )
	{
		ApplyLocal( localPose );
		if ( _player != null ) _player.Playing = _wasPlaying;
		_session.IsPlaying = _wasPlaying;
		_session.Selection = _startSelection;
	}

	public void Commit()
	{
		if ( _finished ) return;
		_finished = true;

		Xform finalLocal = _localPoses[_boneIndex];
		Xform initialLocal = _startLocalPoses[_boneIndex];
		bool isNoOp = AreApproximatelyEqual( finalLocal, initialLocal );

		_transaction.Commit(
			applyFinal: () =>
			{
				ApplyLocal( finalLocal );
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

	static void ValidateTransform( Xform xf )
	{
		if ( !float.IsFinite( xf.Origin.x ) || !float.IsFinite( xf.Origin.y ) || !float.IsFinite( xf.Origin.z ) ||
			 !float.IsFinite( xf.X.x ) || !float.IsFinite( xf.X.y ) || !float.IsFinite( xf.X.z ) ||
			 !float.IsFinite( xf.Y.x ) || !float.IsFinite( xf.Y.y ) || !float.IsFinite( xf.Y.z ) ||
			 !float.IsFinite( xf.Z.x ) || !float.IsFinite( xf.Z.y ) || !float.IsFinite( xf.Z.z ) )
		{
			throw new ArgumentException( "Transform coordinates must be finite numbers." );
		}

		if ( MathF.Abs( xf.Determinant ) < 1e-12f )
		{
			throw new ArgumentException( "Transform basis is singular and cannot be inverted." );
		}
	}

	static bool AreApproximatelyEqual( Xform a, Xform b, float epsilon = 1e-5f )
	{
		float epSq = epsilon * epsilon;
		return (a.Origin - b.Origin).LengthSquared < epSq
			&& (a.X - b.X).LengthSquared < epSq
			&& (a.Y - b.Y).LengthSquared < epSq
			&& (a.Z - b.Z).LengthSquared < epSq;
	}
}
