#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Service coordinating skeletal pose manipulation, coordinate frame transitions,
/// and preview drag session lifecycle adhering to E2 history and transport policies.
/// </summary>
public static class PoseEditService
{
	/// <summary>
	/// Begins a new tentative drag session for posing the specified bone.
	/// </summary>
	public static DragSession BeginPoseDrag( DocumentSession session, string boneName, MarionettePlayer? player = null ) =>
		DragSession.BeginPoseDrag( session, boneName, player );

	/// <summary>
	/// Safely attempts to begin a pose drag session, returning false if the bone or skeleton cannot be resolved.
	/// </summary>
	public static bool TryBeginPoseDrag(
		DocumentSession session,
		string boneName,
		[NotNullWhen( true )] out DragSession? dragSession,
		MarionettePlayer? player = null )
	{
		dragSession = null;
		try
		{
			dragSession = DragSession.BeginPoseDrag( session, boneName, player );
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Converts a desired world transform to local transform under the given parent's world transform.
	/// Formula: inverse(parentWorld) * desiredWorld.
	/// </summary>
	public static Xform WorldToLocal( Xform desiredWorld, Xform parentWorld )
	{
		ValidateTransform( desiredWorld );
		ValidateTransform( parentWorld );
		return parentWorld.Inverse * desiredWorld;
	}

	/// <summary>
	/// Converts a local transform to world transform under the given parent's world transform.
	/// Formula: parentWorld * localTransform.
	/// </summary>
	public static Xform LocalToWorld( Xform desiredLocal, Xform parentWorld )
	{
		ValidateTransform( desiredLocal );
		ValidateTransform( parentWorld );
		return parentWorld * desiredLocal;
	}

	/// <summary>
	/// Rotates a bone's world transform about its head (origin in world space).
	/// </summary>
	public static Xform RotateAboutHead( Xform startWorld, Vec3 axisDirection, float radians )
	{
		ValidateTransform( startWorld );
		if ( !float.IsFinite( radians ) ) throw new ArgumentException( "Rotation radians must be finite.", nameof( radians ) );
		if ( !float.IsFinite( axisDirection.x ) || !float.IsFinite( axisDirection.y ) || !float.IsFinite( axisDirection.z ) )
			throw new ArgumentException( "Axis direction must be finite.", nameof( axisDirection ) );
		if ( axisDirection.LengthSquared < 1e-7f ) throw new ArgumentException( "Axis direction cannot be zero.", nameof( axisDirection ) );

		var rot = Xform.RotateAbout( startWorld.Origin, axisDirection, radians );
		return rot * startWorld;
	}

	/// <summary>
	/// Rotates a bone's world transform about its head (origin in world space) using Vector3 axis.
	/// </summary>
	public static Xform RotateAboutHead( Xform startWorld, Vector3 axisDirection, float radians ) =>
		RotateAboutHead( startWorld, new Vec3( axisDirection.X, axisDirection.Y, axisDirection.Z ), radians );

	/// <summary>
	/// Translates a bone's world transform by adding a delta to its head position.
	/// </summary>
	public static Xform TranslateHead( Xform startWorld, Vec3 deltaWorld )
	{
		ValidateTransform( startWorld );
		if ( !float.IsFinite( deltaWorld.x ) || !float.IsFinite( deltaWorld.y ) || !float.IsFinite( deltaWorld.z ) )
			throw new ArgumentException( "Delta translation must be finite.", nameof( deltaWorld ) );

		return new Xform( startWorld.X, startWorld.Y, startWorld.Z, startWorld.Origin + deltaWorld );
	}

	/// <summary>
	/// Translates a bone's world transform by adding a delta to its head position using Vector3.
	/// </summary>
	public static Xform TranslateHead( Xform startWorld, Vector3 deltaWorld ) =>
		TranslateHead( startWorld, new Vec3( deltaWorld.X, deltaWorld.Y, deltaWorld.Z ) );

	/// <summary>
	/// Evaluates forward kinematics for a rig given an array of local poses, writing results to worldPoses.
	/// </summary>
	public static void ForwardKinematics( Skeleton rig, ReadOnlySpan<Xform> localPoses, Span<Xform> worldPoses )
	{
		ArgumentNullException.ThrowIfNull( rig );
		int count = Math.Min( rig.Count, Math.Min( localPoses.Length, worldPoses.Length ) );
		for ( int b = 0; b < count; b++ )
		{
			int parent = rig.Bones[b].Parent;
			worldPoses[b] = parent >= 0 ? worldPoses[parent] * localPoses[b] : localPoses[b];
		}
	}

	/// <summary>
	/// Checks whether a transform has finite coordinates and a non-singular basis.
	/// </summary>
	public static bool IsValidTransform( Xform xf )
	{
		return float.IsFinite( xf.Origin.x ) && float.IsFinite( xf.Origin.y ) && float.IsFinite( xf.Origin.z ) &&
			   float.IsFinite( xf.X.x ) && float.IsFinite( xf.X.y ) && float.IsFinite( xf.X.z ) &&
			   float.IsFinite( xf.Y.x ) && float.IsFinite( xf.Y.y ) && float.IsFinite( xf.Y.z ) &&
			   float.IsFinite( xf.Z.x ) && float.IsFinite( xf.Z.y ) && float.IsFinite( xf.Z.z ) &&
			   MathF.Abs( xf.Determinant ) >= 1e-12f;
	}

	/// <summary>
	/// Checks whether two transforms are approximately equal within a specified Euclidean tolerance.
	/// </summary>
	public static bool AreApproximatelyEqual( Xform a, Xform b, float epsilon = 1e-5f )
	{
		float epSq = epsilon * epsilon;
		return (a.Origin - b.Origin).LengthSquared < epSq
			&& (a.X - b.X).LengthSquared < epSq
			&& (a.Y - b.Y).LengthSquared < epSq
			&& (a.Z - b.Z).LengthSquared < epSq;
	}

	/// <summary>
	/// Resolves the relevant skeleton from player or document session.
	/// </summary>
	public static Skeleton? ResolveSkeleton( DocumentSession session, MarionettePlayer? player = null ) =>
		player?.Rig
		?? session.Studio?.Rig
		?? session.FbxSource?.Skeleton
		?? session.CurrentSnapshot?.Mesh.Rig;

	/// <summary>
	/// Returns the current local pose buffer of the player.
	/// </summary>
	public static ReadOnlySpan<Xform> GetLocalPoses( MarionettePlayer player )
	{
		ArgumentNullException.ThrowIfNull( player );
		return MarionettePlayerInternals.GetLocalPose( player );
	}

	/// <summary>
	/// Returns the current world pose buffer of the player.
	/// </summary>
	public static ReadOnlySpan<Xform> GetWorldPoses( MarionettePlayer player )
	{
		ArgumentNullException.ThrowIfNull( player );
		return MarionettePlayerInternals.GetWorldPose( player );
	}

	/// <summary>
	/// Returns the inverse bind world transform buffer of the player.
	/// </summary>
	public static ReadOnlySpan<Xform> GetInvBindWorld( MarionettePlayer player )
	{
		ArgumentNullException.ThrowIfNull( player );
		return MarionettePlayerInternals.GetInvBindWorld( player );
	}

	static void ValidateTransform( Xform xf )
	{
		if ( !IsValidTransform( xf ) )
			throw new ArgumentException( "Transform coordinates must be finite and basis non-singular." );
	}
}
