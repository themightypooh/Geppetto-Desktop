#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Marionette.Desktop;
using Effigy;

namespace Geppetto;

/// <summary>Several edit commands that execute and undo as one history entry (undo in reverse).</summary>
public sealed class EffigyCompositeEditCommand : IEditCommand
{
	readonly IReadOnlyList<IEditCommand> _commands;

	public string Description { get; }
	public int Count => _commands.Count;

	public EffigyCompositeEditCommand( string description, IReadOnlyList<IEditCommand> commands )
	{
		Description = description ?? throw new ArgumentNullException( nameof( description ) );
		_commands = commands ?? throw new ArgumentNullException( nameof( commands ) );
	}

	public void Execute()
	{
		for ( int i = 0; i < _commands.Count; i++ )
			_commands[i].Execute();
	}

	public void Undo()
	{
		for ( int i = _commands.Count - 1; i >= 0; i-- )
			_commands[i].Undo();
	}

	public bool IsNoOp => _commands.Count == 0 || _commands.All( c => c.IsNoOp );
}

/// <summary>
/// G10 auto-key: turns a changed Skeleton3D pose (a bone moved by hand, or an IK solve from
/// <see cref="EffigyIkBridge"/>) into keyframes on the session's clip through
/// <see cref="ClipEditService.BuildInsertOrReplaceKey"/>, committed as ONE undoable step through
/// <see cref="EffigyUndoRedoAdapter"/> (so it lands in Geppetto's EditHistory and, when the adapter
/// has one, Godot's UndoRedo too).
///
/// Keys are local bone poses in kernel space (inches, Z-up), read back from the Skeleton3D with
/// <see cref="EffigyPoseBridge.ReadLocalPose"/>, so the clip stays the source of truth and samples
/// back to exactly the pose that was keyed.
/// </summary>
public sealed class EffigyAutoKey
{
	readonly DocumentSession _session;
	readonly EffigyUndoRedoAdapter _undo;
	readonly Skeleton _rig;

	/// <summary>Ease written on new keys.</summary>
	public RigEase Ease { get; set; } = RigEase.Smooth;

	/// <summary>Optional player whose clip cache is invalidated by each key.</summary>
	public MarionettePlayer? Player { get; set; }

	public EffigyAutoKey( DocumentSession session, EffigyUndoRedoAdapter undo, Skeleton rig )
	{
		_session = session ?? throw new ArgumentNullException( nameof( session ) );
		_undo = undo ?? throw new ArgumentNullException( nameof( undo ) );
		_rig = rig ?? throw new ArgumentNullException( nameof( rig ) );
	}

	/// <summary>Clip frame for a time in seconds, rounded to the nearest whole frame.</summary>
	public int FrameAtTime( double timeSeconds )
	{
		var clip = _session.Clip ?? throw new InvalidOperationException( "DocumentSession has no animation clip." );
		return Math.Max( 0, (int)Math.Round( EffigyPoseBridge.FrameAt( clip, timeSeconds ) ) );
	}

	/// <summary>Build (without executing) one command that keys the named bones at
	/// <paramref name="frame"/> from the Skeleton3D's current pose.</summary>
	public EffigyCompositeEditCommand BuildKeyBones( Skeleton3D skeleton, IEnumerable<string> boneNames, int frame, string? actionName = null )
	{
		ArgumentNullException.ThrowIfNull( skeleton );
		ArgumentNullException.ThrowIfNull( boneNames );

		var local = EffigyPoseBridge.ReadLocalPose( _rig, skeleton );
		var commands = new List<IEditCommand>();
		var names = new List<string>();

		foreach ( var name in boneNames.Distinct( StringComparer.Ordinal ) )
		{
			int b = _rig.IndexOf( name );
			if ( b < 0 )
				throw new ArgumentException( $"Bone '{name}' not found in rig." );

			commands.Add( ClipEditService.BuildInsertOrReplaceKey(
				_session, RigTrackKind.Bone, name, frame, RigPose.FromXform( local[b] ), Ease, player: Player ) );
			names.Add( name );
		}

		string label = actionName ?? (names.Count == 1 ? $"Key {names[0]} at frame {frame}" : $"Key {names.Count} bones at frame {frame}");
		return new EffigyCompositeEditCommand( label, commands );
	}

	/// <summary>Key the named bones at <paramref name="frame"/> as one undo step.
	/// Returns false when there was nothing to key.</summary>
	public bool KeyBones( Skeleton3D skeleton, IEnumerable<string> boneNames, int frame, string? actionName = null )
	{
		var cmd = BuildKeyBones( skeleton, boneNames, frame, actionName );
		if ( cmd.IsNoOp )
			return false;
		_undo.Execute( cmd, cmd.Description );
		return true;
	}

	/// <summary>
	/// Key every bone whose local pose differs from <paramref name="before"/> (a pose taken with
	/// <see cref="EffigyPoseBridge.ReadLocalPose"/> before the change) by more than the tolerances.
	/// Returns the keyed bone names (empty when nothing changed and nothing was keyed).
	/// </summary>
	public IReadOnlyList<string> KeyChangedBones( Skeleton3D skeleton, IReadOnlyList<Xform> before, int frame,
		float positionToleranceInches = 1e-3f, float rotationTolerance = 1e-4f, string? actionName = null )
	{
		ArgumentNullException.ThrowIfNull( before );
		var now = EffigyPoseBridge.ReadLocalPose( _rig, skeleton );
		var changed = new List<string>();

		for ( int b = 0; b < _rig.Count && b < before.Count; b++ )
		{
			if ( Differs( before[b], now[b], positionToleranceInches, rotationTolerance ) )
				changed.Add( _rig.Bones[b].Name );
		}

		if ( changed.Count > 0 )
			KeyBones( skeleton, changed, frame, actionName ?? $"Auto-key {changed.Count} bone(s) at frame {frame}" );
		return changed;
	}

	/// <summary>
	/// Key the bones an IK solve turned (the chain above <paramref name="endBone"/>, end included)
	/// at <paramref name="frame"/>, as one undo step. Call after a successful
	/// <see cref="EffigyIkBridge"/> solve that wrote back to the Skeleton3D.
	/// </summary>
	public bool KeyIkChain( Skeleton3D skeleton, string endBone, int chainLength, int frame, string? actionName = null )
	{
		var chain = EffigyIkBridge.ChainBones( _rig, endBone, chainLength )
			?? throw new ArgumentException( $"Chain of {chainLength} above '{endBone}' does not exist in the rig." );
		return KeyBones( skeleton, chain.Select( b => _rig.Bones[b].Name ), frame, actionName ?? $"Key IK {endBone} at frame {frame}" );
	}

	static bool Differs( in Xform a, in Xform b, float posTol, float rotTol )
	{
		if ( (a.Origin - b.Origin).Length > posTol ) return true;
		if ( (a.X - b.X).Length > rotTol ) return true;
		if ( (a.Y - b.Y).Length > rotTol ) return true;
		if ( (a.Z - b.Z).Length > rotTol ) return true;
		return false;
	}
}
