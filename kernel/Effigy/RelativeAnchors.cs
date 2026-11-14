using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// S&amp;box-native Lane 3a core: relative socket anchors.
///
/// A parent body registers named landmark frames — <c>socket_neck</c>,
/// <c>shoulder_L</c>, <c>socket_wrist</c>, and so on — each with an origin
/// <c>P</c> and an orientation <c>Q</c>. A child is then placed by the
/// spec's composition, never by a raw world-space float:
///
///     T_child = T_parent_socket * T_offset
///
/// Scaling, rotating, or posing the parent therefore moves the child
/// identically: the socket transform is recomputed from the parent, and
/// the child follows through the same multiplication. A limb parented to
/// <c>shoulder_L</c> cannot drift away from the torso it belongs to.
///
/// What this file is NOT: it does not parse BuildScript origin strings
/// (<c>origin=parent.socket_name + [dx, dy, dz]</c>) and it does not wire
/// anything into BuildScript. That resolution helper and wiring belong to
/// a separate lane agent and are owed, not applied here. Callers pass a
/// resolved socket name plus a <see cref="Vec3"/> or <see cref="Xform"/>
/// offset; this file does the frame math.
///
/// A <see cref="SocketFrame"/> carries position + orientation only.
/// Scale lives in the parent <see cref="Xform"/> (see
/// <see cref="SocketFrame.ApplyParent"/> and
/// <see cref="RelativeAnchorSet.ComposeWithParent"/>), so a scaled parent
/// is never flattened into a unit-quaternion frame and silently lost.
/// Shear and mirror have no quaternion: <see cref="Quat.FromXform"/>
/// normalises them away, so orientation recovered from a sheared or
/// mirrored parent transform is the rotation part only.
/// </summary>
public readonly struct SocketFrame : IEquatable<SocketFrame>
{
	public readonly string Name;
	public readonly Vec3 Position;
	public readonly Quat Orientation;

	public SocketFrame( string name, Vec3 position, Quat orientation )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "Socket name must not be empty.", nameof( name ) );
		Name = name;
		Position = position;
		Orientation = orientation;
	}

	public SocketFrame( string name, Vec3 position )
		: this( name, position, Quat.Identity )
	{
	}

	/// <summary>This frame as a transform: rotation <c>Q</c> at origin <c>P</c>.</summary>
	public Xform ToXform() => Orientation.ToXform( Position );

	/// <summary>Same frame, with the basis scaled per axis before rotation.</summary>
	public Xform ToXform( Vec3 scale ) => Orientation.ToXform( Position, scale );

	/// <summary>A point given in this socket's local space, in the space this frame lives in.</summary>
	public Vec3 TransformPoint( Vec3 local ) => ToXform().TransformPoint( local );

	/// <summary>A direction given in this socket's local space (translation ignored).</summary>
	public Vec3 TransformDirection( Vec3 local ) => ToXform().TransformDirection( local );

	/// <summary>Child transform for a translation offset in this socket's local space.</summary>
	public Xform Compose( Vec3 offset ) => ToXform() * Xform.Translate( offset );

	/// <summary>Child transform for a full local offset transform: T_socket * T_offset.</summary>
	public Xform Compose( Xform offset ) => ToXform() * offset;

	/// <summary>
	/// A parent-local frame placed in the parent's space. Scale-preserving:
	/// the result is an <see cref="Xform"/>, not a <see cref="SocketFrame"/>,
	/// because a frame has no scale slot to hold the parent's scale in.
	/// </summary>
	public static Xform ApplyParent( Xform parent, SocketFrame local ) =>
		parent * local.ToXform();

	/// <summary>
	/// The rotation + position of a combined transform, with scale divided
	/// out (see <see cref="Quat.FromXform"/>). Loses shear/mirror by design.
	/// </summary>
	public static SocketFrame FromXform( string name, Xform combined ) =>
		new( name, combined.Origin, Quat.FromXform( combined ) );

	/// <summary>Recover a local point from a world point through this frame's inverse.</summary>
	public Vec3 ToLocalPoint( Vec3 world ) => ToXform().Inverse.TransformPoint( world );

	public bool Equals( SocketFrame o ) =>
		Name == o.Name && Position.Equals( o.Position ) &&
		Orientation.x == o.Orientation.x && Orientation.y == o.Orientation.y &&
		Orientation.z == o.Orientation.z && Orientation.w == o.Orientation.w;

	public override bool Equals( object obj ) => obj is SocketFrame f && Equals( f );
	public override int GetHashCode() => HashCode.Combine( Name, Position, Orientation );
	public override string ToString() => $"{Name} at {Position} rot {Orientation}";
}

/// <summary>
/// A named registry of socket frames for one body (or one document).
///
/// Names are matched exactly and case-sensitively: <c>shoulder_L</c> is
/// not <c>shoulder_l</c>. A BuildScript origin string must resolve to the
/// exact registered name, or lookup fails loudly instead of silently
/// parenting to the wrong side. Re-registering a name replaces it, so a
/// re-posed body updates its sockets in place rather than accumulating
/// stale frames.
/// </summary>
public sealed class RelativeAnchorSet
{
	readonly Dictionary<string, SocketFrame> frames = new( StringComparer.Ordinal );
	readonly List<string> order = new();

	public int Count => frames.Count;

	public IReadOnlyList<string> Names => order.AsReadOnly();

	public void Register( SocketFrame frame )
	{
		if ( !frames.ContainsKey( frame.Name ) )
			order.Add( frame.Name );
		frames[frame.Name] = frame;
	}

	public void Register( string name, Vec3 position ) =>
		Register( new SocketFrame( name, position ) );

	public void Register( string name, Vec3 position, Quat orientation ) =>
		Register( new SocketFrame( name, position, orientation ) );

	public bool Contains( string name ) => name is not null && frames.ContainsKey( name );

	public bool TryGet( string name, out SocketFrame frame )
	{
		if ( name is null )
		{
			frame = default;
			return false;
		}
		return frames.TryGetValue( name, out frame );
	}

	public bool Remove( string name )
	{
		if ( name is null || !frames.Remove( name ) )
			return false;
		order.Remove( name );
		return true;
	}

	/// <summary>Child transform for a translation offset: T_socket * T_offset.</summary>
	public bool TryResolve( string name, Vec3 offset, out Xform child )
	{
		if ( !TryGet( name, out var frame ) )
		{
			child = default;
			return false;
		}
		child = frame.Compose( offset );
		return true;
	}

	/// <summary>Child transform for a full local offset transform: T_socket * T_offset.</summary>
	public bool TryResolve( string name, Xform offset, out Xform child )
	{
		if ( !TryGet( name, out var frame ) )
		{
			child = default;
			return false;
		}
		child = frame.Compose( offset );
		return true;
	}

	public Xform Resolve( string name, Vec3 offset )
	{
		if ( !TryResolve( name, offset, out var child ) )
			throw new InvalidOperationException( $"No socket named '{name}' is registered." );
		return child;
	}

	public Xform Resolve( string name, Xform offset )
	{
		if ( !TryResolve( name, offset, out var child ) )
			throw new InvalidOperationException( $"No socket named '{name}' is registered." );
		return child;
	}

	/// <summary>A local point through a named socket, in the space the frame lives in.</summary>
	public bool TryResolvePoint( string name, Vec3 local, out Vec3 world )
	{
		if ( !TryGet( name, out var frame ) )
		{
			world = default;
			return false;
		}
		world = frame.TransformPoint( local );
		return true;
	}

	public Vec3 ResolvePoint( string name, Vec3 local )
	{
		if ( !TryResolvePoint( name, local, out var world ) )
			throw new InvalidOperationException( $"No socket named '{name}' is registered." );
		return world;
	}

	/// <summary>
	/// The torso-scale path: a parent-local socket placed by the parent's
	/// full transform (translation + rotation + scale), then the child
	/// offset composed on top. Result = parent * T_socket * T_offset, so
	/// scaling the parent scales the attachment point by the same factor.
	/// </summary>
	public Xform ComposeWithParent( Xform parent, string name, Vec3 offset )
	{
		if ( !TryGet( name, out var frame ) )
			throw new InvalidOperationException( $"No socket named '{name}' is registered." );
		return parent * frame.ToXform() * Xform.Translate( offset );
	}

	/// <summary>Parent-space socket transform, scale-preserving (see <see cref="SocketFrame.ApplyParent"/>).</summary>
	public Xform WorldSocket( Xform parent, string name )
	{
		if ( !TryGet( name, out var frame ) )
			throw new InvalidOperationException( $"No socket named '{name}' is registered." );
		return SocketFrame.ApplyParent( parent, frame );
	}
}
