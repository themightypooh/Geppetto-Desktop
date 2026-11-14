using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// Hammer map undo / autosave backend (ticket hammer-undo-autosave-map-commands).
///
/// Command pattern with Do/Undo/Redo over a map document, coalescing of stroke dabs
/// into one command, command repetition, autosave next to the map, and a crash-recovery
/// record. Backend only; no UI. Mirrors the desktop EditHistory conventions
/// (core/EditHistory.cs: state ids, MarkSaved/IsDirty, capacity, no-op skipping) and
/// the kernel BrushUndo coalescing rule (Brush.cs: earliest before-state wins).
///
/// DEPENDENCY STATUS, 2026-11-13 ~16:00 EST: the MapDocument this builds on
/// (hammer-map-document-model, worker muse-3-desktop-hammer-12) had NOT landed when
/// this file was written — kernel/Effigy/Map/ did not exist at all. This file is
/// therefore written against the API in that task's desc exactly as filed: a versioned
/// map document holding brushes + displacements + entities + layers/visgroups +
/// compile settings, with Create/Open/Save, feature-ordered history with rollback,
/// and a validation hook on save. It does NOT define MapDocument (that file belongs
/// to the sibling ticket) and it does not guess at MapDocument's concrete type names.
/// Instead the contract is <see cref="IMapUndoTarget"/>: the real MapDocument (or a
/// thin adapter written by the final integrator) implements it, and
/// <see cref="InMemoryMapUndoTarget"/> is the reference implementation the tests
/// drive. If MapDocument lands with different member names, only the adapter moves —
/// the commands, the stack, the autosave format and the tests do not.
/// </summary>

/// <summary>Which kind of map edit a command performs. One command is one undo step,
/// whatever it touches: a brush move, an entity add, a displacement stroke and an I/O
/// add are each a single command (Report 1.2 #35).</summary>
public enum MapEditKind
{
	Brush,
	Entity,
	Displacement,
	Io,
	Composite
}

/// <summary>One brush in a map: an id, an axis-aligned bounds box (kernel inches, Z-up,
/// same convention as the rest of Effigy), and a material name.</summary>
public sealed class MapBrushState
{
	public string Id = "";
	public Vec3 Min;
	public Vec3 Max;
	public string Material = "";

	public MapBrushState Clone() => new MapBrushState { Id = Id, Min = Min, Max = Max, Material = Material };
}

/// <summary>One entity: a classname (info_player_start, light, ...), an origin, and
/// string key/value properties. The dictionary is copied on Clone and on set, so a
/// command's before-state can never be mutated by a later edit.</summary>
public sealed class MapEntityState
{
	public string Id = "";
	public string ClassName = "";
	public Vec3 Origin;
	public Dictionary<string, string> Properties = new Dictionary<string, string>();

	public MapEntityState Clone() => new MapEntityState
	{
		Id = Id,
		ClassName = ClassName,
		Origin = Origin,
		Properties = new Dictionary<string, string>( Properties )
	};
}

/// <summary>Displacement data for one brush face: subdivision power and one height per
/// displacement vertex, in face order. Heights are copied on Clone.</summary>
public sealed class MapDisplacementState
{
	public string BrushId = "";
	public int FaceIndex;
	public int Power;
	public float[] Heights = Array.Empty<float>();

	public MapDisplacementState Clone() => new MapDisplacementState
	{
		BrushId = BrushId,
		FaceIndex = FaceIndex,
		Power = Power,
		Heights = (float[])Heights.Clone()
	};
}

/// <summary>One entity I/O connection: when Source fires Output, Target receives Input
/// with Parameter after DelaySeconds, up to TimesToFire times (-1 is infinite, the
/// Hammer convention).</summary>
public sealed class MapIoState
{
	public string Id = "";
	public string SourceId = "";
	public string TargetId = "";
	public string Output = "";
	public string Input = "";
	public string Parameter = "";
	public float DelaySeconds;
	public int TimesToFire = -1;

	public MapIoState Clone() => new MapIoState
	{
		Id = Id,
		SourceId = SourceId,
		TargetId = TargetId,
		Output = Output,
		Input = Input,
		Parameter = Parameter,
		DelaySeconds = DelaySeconds,
		TimesToFire = TimesToFire
	};
}

/// <summary>Whole-map snapshot: the versioned, diffable text form autosave writes and
/// recovery restores. SchemaVersion 1. A reader that meets a newer version must fail
/// with a named error, never silently drop fields (same rule as MapDocument's
/// old-version load: named migration or a clear error).</summary>
public sealed class MapStateSnapshot
{
	public const int CurrentSchemaVersion = 1;

	public int SchemaVersion = CurrentSchemaVersion;
	public string DocumentId = "";
	public long Revision;
	public DateTimeOffset SavedAtUtc;
	public List<MapBrushState> Brushes = new List<MapBrushState>();
	public List<MapEntityState> Entities = new List<MapEntityState>();
	public List<MapDisplacementState> Displacements = new List<MapDisplacementState>();
	public List<MapIoState> IoConnections = new List<MapIoState>();

	public MapStateSnapshot Clone()
	{
		return new MapStateSnapshot
		{
			SchemaVersion = SchemaVersion,
			DocumentId = DocumentId,
			Revision = Revision,
			SavedAtUtc = SavedAtUtc,
			Brushes = Brushes.Select( b => b.Clone() ).ToList(),
			Entities = Entities.Select( e => e.Clone() ).ToList(),
			Displacements = Displacements.Select( d => d.Clone() ).ToList(),
			IoConnections = IoConnections.Select( i => i.Clone() ).ToList()
		};
	}

	/// <summary>Duplicate ids or a bad schema version are a corrupt snapshot, not a
	/// state to load. Throws InvalidDataException naming the problem.</summary>
	public void Validate()
	{
		if ( SchemaVersion != CurrentSchemaVersion )
			throw new InvalidDataException(
				"Unsupported map snapshot schema version " + SchemaVersion +
				" (this build reads " + CurrentSchemaVersion + "); a migration is required, refusing to load." );
		if ( string.IsNullOrWhiteSpace( DocumentId ) )
			throw new InvalidDataException( "Map snapshot has no DocumentId." );
		CheckUnique( Brushes.Select( b => b.Id ), "brush" );
		CheckUnique( Entities.Select( e => e.Id ), "entity" );
		CheckUnique( IoConnections.Select( i => i.Id ), "I/O connection" );
		var dispKeys = Displacements.Select( d => d.BrushId + "#" + d.FaceIndex );
		CheckUnique( dispKeys, "displacement" );
	}

	static void CheckUnique( IEnumerable<string> ids, string what )
	{
		var seen = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var id in ids )
		{
			if ( string.IsNullOrWhiteSpace( id ) )
				throw new InvalidDataException( "Map snapshot has a " + what + " with no id." );
			if ( !seen.Add( id ) )
				throw new InvalidDataException( "Map snapshot has two " + what + "s with id '" + id + "'." );
		}
	}
}

/// <summary>What the autosave scheduler needs from a map: identity, a dirty flag, and
/// a way to serialise / restore a whole snapshot. MapDocument implements this (or is
/// adapted to it); <see cref="IMapUndoTarget"/> extends it.</summary>
public interface IMapAutosaveSource
{
	string DocumentId { get; }
	string SourcePath { get; }
	long Revision { get; }
	bool IsDirty { get; }
	string SerializeSnapshot();
	void RestoreSnapshot( string json );
}

/// <summary>The operations MapUndo drives a map document through. Every mutating call
/// must bump Revision exactly once; lookups return null when the id is unknown.
/// MapDocument implements this interface directly or via a thin adapter — the
/// commands below only ever talk to this surface, so a MapDocument rename does not
/// ripple into undo, autosave, or tests.</summary>
public interface IMapUndoTarget : IMapAutosaveSource
{
	IReadOnlyList<MapBrushState> Brushes { get; }
	IReadOnlyList<MapEntityState> Entities { get; }
	IReadOnlyList<MapDisplacementState> Displacements { get; }
	IReadOnlyList<MapIoState> IoConnections { get; }

	MapBrushState FindBrush( string id );
	MapEntityState FindEntity( string id );
	MapDisplacementState FindDisplacement( string brushId, int faceIndex );
	MapIoState FindIo( string id );

	void AddBrush( MapBrushState state );
	void RemoveBrush( string id );
	void MoveBrush( string id, Vec3 min, Vec3 max );
	void SetBrushMaterial( string id, string material );

	void AddEntity( MapEntityState state );
	void RemoveEntity( string id );
	void MoveEntity( string id, Vec3 origin );
	void SetEntityProperty( string id, string key, string value );
	void RemoveEntityProperty( string id, string key );

	void AddDisplacement( MapDisplacementState state );
	void SetDisplacement( string brushId, int faceIndex, float[] heights, int power );
	void RemoveDisplacement( string brushId, int faceIndex );

	void AddIo( MapIoState state );
	void SetIo( MapIoState state );
	void RemoveIo( string id );

	MapStateSnapshot CaptureSnapshot();
	void ApplySnapshot( MapStateSnapshot snapshot );
	void MarkSaved();
}

/// <summary>One undoable map edit. Do applies it, Undo reverses it, and both must
/// be exact inverses: after Do then Undo the target is byte-identical to before.
/// Commands store before/after snapshots, never live references into the target.</summary>
public abstract class MapCommand
{
	public string Description { get; protected set; } = "";
	public MapEditKind Kind { get; protected set; }

	public abstract void Do( IMapUndoTarget target );
	public abstract void Undo( IMapUndoTarget target );

	/// <summary>True when Do would change nothing (moving a brush to where it already
	/// is). No-op commands are never pushed — an undo step that does nothing is a bug
	/// the user feels as a dead Ctrl+Z.</summary>
	public virtual bool IsNoOp => false;

	/// <summary>Fold a later command into this one so a run of dabs reads as a single
	/// stroke. The default is "cannot coalesce". Implementations that can must keep
	/// the EARLIEST before-state and take the LATEST after-state (BrushUndo.Absorb).</summary>
	public virtual bool TryCoalesce( MapCommand later ) => false;

	/// <summary>Build a fresh command that repeats this edit from the target's CURRENT
	/// state (TrenchBroom command repetition), or null when the edit cannot repeat
	/// (adds/removes: repeating them would duplicate or delete the wrong thing).</summary>
	public virtual MapCommand Repeat( IMapUndoTarget target ) => null;
}

/// <summary>Command from two lambdas, for edits that do not need a named type.
/// Prefer a named command when the edit is one of the four covered kinds.</summary>
public sealed class DelegateMapCommand : MapCommand
{
	readonly Action<IMapUndoTarget> _do;
	readonly Action<IMapUndoTarget> _undo;
	readonly Func<bool> _isNoOp;

	public DelegateMapCommand( string description, MapEditKind kind,
		Action<IMapUndoTarget> doAction, Action<IMapUndoTarget> undoAction, Func<bool> isNoOp = null )
	{
		Description = description;
		Kind = kind;
		_do = doAction ?? throw new ArgumentNullException( nameof( doAction ) );
		_undo = undoAction ?? throw new ArgumentNullException( nameof( undoAction ) );
		_isNoOp = isNoOp;
	}

	public override void Do( IMapUndoTarget target ) => _do( target );
	public override void Undo( IMapUndoTarget target ) => _undo( target );
	public override bool IsNoOp => _isNoOp != null && _isNoOp();
}

public sealed class AddBrushCommand : MapCommand
{
	readonly MapBrushState _state;
	public AddBrushCommand( MapBrushState state )
	{
		_state = ( state ?? throw new ArgumentNullException( nameof( state ) ) ).Clone();
		Description = "Add brush " + _state.Id;
		Kind = MapEditKind.Brush;
	}
	public override void Do( IMapUndoTarget target ) => target.AddBrush( _state.Clone() );
	public override void Undo( IMapUndoTarget target ) => target.RemoveBrush( _state.Id );
}

public sealed class RemoveBrushCommand : MapCommand
{
	readonly MapBrushState _state;
	public RemoveBrushCommand( MapBrushState state )
	{
		_state = ( state ?? throw new ArgumentNullException( nameof( state ) ) ).Clone();
		Description = "Remove brush " + _state.Id;
		Kind = MapEditKind.Brush;
	}
	public override void Do( IMapUndoTarget target ) => target.RemoveBrush( _state.Id );
	public override void Undo( IMapUndoTarget target ) => target.AddBrush( _state.Clone() );
}

public sealed class MoveBrushCommand : MapCommand
{
	readonly string _id;
	readonly Vec3 _beforeMin, _beforeMax, _afterMin, _afterMax;
	readonly Vec3 _deltaMin, _deltaMax;

	public MoveBrushCommand( string id, Vec3 beforeMin, Vec3 beforeMax, Vec3 afterMin, Vec3 afterMax )
	{
		_id = id;
		_beforeMin = beforeMin; _beforeMax = beforeMax;
		_afterMin = afterMin; _afterMax = afterMax;
		_deltaMin = afterMin - beforeMin;
		_deltaMax = afterMax - beforeMax;
		Description = "Move brush " + id;
		Kind = MapEditKind.Brush;
	}

	public override void Do( IMapUndoTarget target ) => target.MoveBrush( _id, _afterMin, _afterMax );
	public override void Undo( IMapUndoTarget target ) => target.MoveBrush( _id, _beforeMin, _beforeMax );
	public override bool IsNoOp => _beforeMin.Equals( _afterMin ) && _beforeMax.Equals( _afterMax );

	public override MapCommand Repeat( IMapUndoTarget target )
	{
		var current = target.FindBrush( _id );
		if ( current == null ) return null;
		return new MoveBrushCommand( _id, current.Min, current.Max,
			current.Min + _deltaMin, current.Max + _deltaMax );
	}
}

public sealed class SetBrushMaterialCommand : MapCommand
{
	readonly string _id, _before, _after;
	public SetBrushMaterialCommand( string id, string before, string after )
	{
		_id = id; _before = before; _after = after;
		Description = "Set brush " + id + " material to " + after;
		Kind = MapEditKind.Brush;
	}
	public override void Do( IMapUndoTarget target ) => target.SetBrushMaterial( _id, _after );
	public override void Undo( IMapUndoTarget target ) => target.SetBrushMaterial( _id, _before );
	public override bool IsNoOp => string.Equals( _before, _after, StringComparison.Ordinal );
	public override MapCommand Repeat( IMapUndoTarget target )
	{
		var current = target.FindBrush( _id );
		if ( current == null ) return null;
		return new SetBrushMaterialCommand( _id, current.Material, _after );
	}
}

public sealed class AddEntityCommand : MapCommand
{
	readonly MapEntityState _state;
	public AddEntityCommand( MapEntityState state )
	{
		_state = ( state ?? throw new ArgumentNullException( nameof( state ) ) ).Clone();
		Description = "Add entity " + _state.ClassName + " " + _state.Id;
		Kind = MapEditKind.Entity;
	}
	public override void Do( IMapUndoTarget target ) => target.AddEntity( _state.Clone() );
	public override void Undo( IMapUndoTarget target ) => target.RemoveEntity( _state.Id );
}

public sealed class RemoveEntityCommand : MapCommand
{
	readonly MapEntityState _state;
	public RemoveEntityCommand( MapEntityState state )
	{
		_state = ( state ?? throw new ArgumentNullException( nameof( state ) ) ).Clone();
		Description = "Remove entity " + _state.Id;
		Kind = MapEditKind.Entity;
	}
	public override void Do( IMapUndoTarget target ) => target.RemoveEntity( _state.Id );
	public override void Undo( IMapUndoTarget target ) => target.AddEntity( _state.Clone() );
}

public sealed class MoveEntityCommand : MapCommand
{
	readonly string _id;
	readonly Vec3 _before, _after, _delta;
	public MoveEntityCommand( string id, Vec3 before, Vec3 after )
	{
		_id = id; _before = before; _after = after; _delta = after - before;
		Description = "Move entity " + id;
		Kind = MapEditKind.Entity;
	}
	public override void Do( IMapUndoTarget target ) => target.MoveEntity( _id, _after );
	public override void Undo( IMapUndoTarget target ) => target.MoveEntity( _id, _before );
	public override bool IsNoOp => _before.Equals( _after );
	public override MapCommand Repeat( IMapUndoTarget target )
	{
		var current = target.FindEntity( _id );
		if ( current == null ) return null;
		return new MoveEntityCommand( _id, current.Origin, current.Origin + _delta );
	}
}

public sealed class SetEntityPropertyCommand : MapCommand
{
	readonly string _id, _key;
	readonly string _before, _after;
	readonly bool _hadBefore;
	public SetEntityPropertyCommand( string id, string key, string before, string after, bool hadBefore )
	{
		_id = id; _key = key; _before = before; _after = after; _hadBefore = hadBefore;
		Description = "Set entity " + id + " property " + key;
		Kind = MapEditKind.Entity;
	}
	public override void Do( IMapUndoTarget target ) => target.SetEntityProperty( _id, _key, _after );
	public override void Undo( IMapUndoTarget target )
	{
		if ( _hadBefore ) target.SetEntityProperty( _id, _key, _before );
		else target.RemoveEntityProperty( _id, _key );
	}
	public override bool IsNoOp => _hadBefore && string.Equals( _before, _after, StringComparison.Ordinal );
	public override MapCommand Repeat( IMapUndoTarget target )
	{
		var current = target.FindEntity( _id );
		if ( current == null ) return null;
		string cur;
		bool had = current.Properties.TryGetValue( _key, out cur );
		return new SetEntityPropertyCommand( _id, _key, had ? cur : "", _after, had );
	}
}

public sealed class AddDisplacementCommand : MapCommand
{
	readonly MapDisplacementState _state;
	public AddDisplacementCommand( MapDisplacementState state )
	{
		_state = ( state ?? throw new ArgumentNullException( nameof( state ) ) ).Clone();
		Description = "Add displacement on brush " + _state.BrushId + " face " + _state.FaceIndex;
		Kind = MapEditKind.Displacement;
	}
	public override void Do( IMapUndoTarget target ) => target.AddDisplacement( _state.Clone() );
	public override void Undo( IMapUndoTarget target ) => target.RemoveDisplacement( _state.BrushId, _state.FaceIndex );
}

public sealed class RemoveDisplacementCommand : MapCommand
{
	readonly MapDisplacementState _state;
	public RemoveDisplacementCommand( MapDisplacementState state )
	{
		_state = ( state ?? throw new ArgumentNullException( nameof( state ) ) ).Clone();
		Description = "Remove displacement on brush " + _state.BrushId + " face " + _state.FaceIndex;
		Kind = MapEditKind.Displacement;
	}
	public override void Do( IMapUndoTarget target ) => target.RemoveDisplacement( _state.BrushId, _state.FaceIndex );
	public override void Undo( IMapUndoTarget target ) => target.AddDisplacement( _state.Clone() );
}

/// <summary>Replace one face's displacement heights/power. Coalescible: a later dab on
/// the same brush face folds into this one, keeping the earliest before-heights and
/// taking the latest after-heights — the whole stroke undoes in one step.</summary>
public sealed class SetDisplacementCommand : MapCommand
{
	readonly string _brushId;
	readonly int _faceIndex;
	readonly float[] _beforeHeights;
	readonly int _beforePower;
	float[] _afterHeights;
	int _afterPower;

	public SetDisplacementCommand( string brushId, int faceIndex,
		float[] beforeHeights, int beforePower, float[] afterHeights, int afterPower )
	{
		_brushId = brushId;
		_faceIndex = faceIndex;
		_beforeHeights = ( beforeHeights ?? throw new ArgumentNullException( nameof( beforeHeights ) ) ).ToArray();
		_beforePower = beforePower;
		_afterHeights = ( afterHeights ?? throw new ArgumentNullException( nameof( afterHeights ) ) ).ToArray();
		_afterPower = afterPower;
		Description = "Sculpt displacement on brush " + brushId + " face " + faceIndex;
		Kind = MapEditKind.Displacement;
	}

	public string BrushId => _brushId;
	public int FaceIndex => _faceIndex;

	public override void Do( IMapUndoTarget target ) =>
		target.SetDisplacement( _brushId, _faceIndex, (float[])_afterHeights.Clone(), _afterPower );
	public override void Undo( IMapUndoTarget target ) =>
		target.SetDisplacement( _brushId, _faceIndex, (float[])_beforeHeights.Clone(), _beforePower );
	public override bool IsNoOp =>
		_beforePower == _afterPower && _beforeHeights.SequenceEqual( _afterHeights );

	public override bool TryCoalesce( MapCommand later )
	{
		var next = later as SetDisplacementCommand;
		if ( next == null || next._brushId != _brushId || next._faceIndex != _faceIndex ) return false;
		_afterHeights = (float[])next._afterHeights.Clone();
		_afterPower = next._afterPower;
		return true;
	}

	public override MapCommand Repeat( IMapUndoTarget target )
	{
		var current = target.FindDisplacement( _brushId, _faceIndex );
		if ( current == null ) return null;
		return new SetDisplacementCommand( _brushId, _faceIndex,
			(float[])current.Heights.Clone(), current.Power,
			(float[])_afterHeights.Clone(), _afterPower );
	}
}

public sealed class AddIoCommand : MapCommand
{
	readonly MapIoState _state;
	public AddIoCommand( MapIoState state )
	{
		_state = ( state ?? throw new ArgumentNullException( nameof( state ) ) ).Clone();
		Description = "Add I/O " + _state.SourceId + "." + _state.Output + " -> " + _state.TargetId + "." + _state.Input;
		Kind = MapEditKind.Io;
	}
	public override void Do( IMapUndoTarget target ) => target.AddIo( _state.Clone() );
	public override void Undo( IMapUndoTarget target ) => target.RemoveIo( _state.Id );
}

public sealed class RemoveIoCommand : MapCommand
{
	readonly MapIoState _state;
	public RemoveIoCommand( MapIoState state )
	{
		_state = ( state ?? throw new ArgumentNullException( nameof( state ) ) ).Clone();
		Description = "Remove I/O " + _state.Id;
		Kind = MapEditKind.Io;
	}
	public override void Do( IMapUndoTarget target ) => target.RemoveIo( _state.Id );
	public override void Undo( IMapUndoTarget target ) => target.AddIo( _state.Clone() );
}

public sealed class SetIoCommand : MapCommand
{
	readonly MapIoState _before, _after;
	public SetIoCommand( MapIoState before, MapIoState after )
	{
		_before = ( before ?? throw new ArgumentNullException( nameof( before ) ) ).Clone();
		_after = ( after ?? throw new ArgumentNullException( nameof( after ) ) ).Clone();
		Description = "Edit I/O " + _after.Id;
		Kind = MapEditKind.Io;
	}
	public override void Do( IMapUndoTarget target ) => target.SetIo( _after.Clone() );
	public override void Undo( IMapUndoTarget target ) => target.SetIo( _before.Clone() );
	public override bool IsNoOp =>
		_before.SourceId == _after.SourceId && _before.TargetId == _after.TargetId &&
		_before.Output == _after.Output && _before.Input == _after.Input &&
		_before.Parameter == _after.Parameter &&
		_before.DelaySeconds == _after.DelaySeconds && _before.TimesToFire == _after.TimesToFire;
}

/// <summary>Several commands executed and undone as ONE undo step (a multi-part edit,
/// or one finished stroke session). Undo runs children in reverse order.</summary>
public sealed class CompositeMapCommand : MapCommand
{
	readonly List<MapCommand> _children;
	public CompositeMapCommand( string description, IEnumerable<MapCommand> children )
	{
		if ( children == null ) throw new ArgumentNullException( nameof( children ) );
		_children = children.ToList();
		if ( _children.Count == 0 ) throw new ArgumentException( "A composite command needs at least one child.", nameof( children ) );
		Description = description;
		Kind = MapEditKind.Composite;
	}
	public IReadOnlyList<MapCommand> Children => _children;
	public override void Do( IMapUndoTarget target )
	{
		foreach ( var child in _children ) child.Do( target );
	}
	public override void Undo( IMapUndoTarget target )
	{
		for ( int i = _children.Count - 1; i >= 0; i-- ) _children[i].Undo( target );
	}
	public override bool IsNoOp => _children.All( c => c.IsNoOp );
	public override MapCommand Repeat( IMapUndoTarget target )
	{
		var repeated = new List<MapCommand>();
		foreach ( var child in _children )
		{
			var r = child.Repeat( target );
			if ( r == null ) return null;
			repeated.Add( r );
		}
		return new CompositeMapCommand( Description, repeated );
	}
}

/// <summary>The undo stack over one map target. Unlimited by default (capacity 0),
/// matching the TrenchBroom behaviour the report cites; pass a positive capacity to
/// bound memory, in which case the OLDEST entry is evicted first. A new Execute
/// clears the redo stack. Saved-state tracking mirrors EditHistory: MarkSaved
/// records the current state id, IsDirty compares against it, and undoing back to the
/// saved state clears the dirty flag again.</summary>
public sealed class MapUndo
{
	sealed class Entry
	{
		public MapCommand Command;
		public Guid Before;
		public Guid After;
	}

	readonly IMapUndoTarget _target;
	readonly List<Entry> _undoStack = new List<Entry>();
	readonly Stack<Entry> _redoStack = new Stack<Entry>();
	int _capacity; // Mutable: RemoveUndoCap() drops a constructor-supplied cap back to 0 (unlimited) without rebuilding the stack.
	readonly List<MapCommand> _strokePending = new List<MapCommand>();
	readonly Dictionary<string, MapDisplacementState> _strokeBefore = new Dictionary<string, MapDisplacementState>();

	public IMapUndoTarget Target => _target;
	public int Capacity => _capacity;
	public bool IsUnlimited => _capacity <= 0;

	/// <summary>Remove any undo cap: the stack becomes unbounded from now on
	/// (the brush-accuracy ticket's "remove any undo cap"). Entries already
	/// on the stack are kept; nothing is evicted. A stack created with the
	/// default capacity 0 is already unlimited, so this is a no-op for it.</summary>
	public void RemoveUndoCap()
	{
		_capacity = 0;
	}

	/// <summary>True when a last command exists and it can be repeated from
	/// the target's current state. (Repeat() builds a fresh command without
	/// applying it, so this peek is side-effect free.)</summary>
	public bool CanRepeatLast
	{
		get
		{
			if ( _undoStack.Count == 0 ) return false;
			return _undoStack[_undoStack.Count - 1].Command.Repeat( _target ) != null;
		}
	}
	public int UndoCount => _undoStack.Count;
	public int RedoCount => _redoStack.Count;
	public bool CanUndo => _undoStack.Count > 0 && !StrokeActive;
	public bool CanRedo => _redoStack.Count > 0 && !StrokeActive;
	public bool StrokeActive => _strokeDescription != null;
	public string LastDescription => _undoStack.Count == 0 ? "" : _undoStack[_undoStack.Count - 1].Command.Description;

	public Guid CurrentStateId { get; private set; } = Guid.NewGuid();
	public Guid SavedStateId { get; private set; }
	public bool IsDirty => CurrentStateId != SavedStateId;

	public event Action StateChanged;

	string _strokeDescription;

	public MapUndo( IMapUndoTarget target, int capacity = 0 )
	{
		_target = target ?? throw new ArgumentNullException( nameof( target ) );
		if ( capacity < 0 ) throw new ArgumentOutOfRangeException( nameof( capacity ), "Capacity must be 0 (unlimited) or positive." );
		_capacity = capacity;
		SavedStateId = CurrentStateId;
	}

	public void MarkSaved()
	{
		SavedStateId = CurrentStateId;
		_target.MarkSaved();
	}

	public void Execute( MapCommand command ) => Execute( command, true );

	/// <summary>Execute a command, applying it unless applyNow is false (used by
	/// EndStroke: the dabs were already applied live during the stroke).</summary>
	public void Execute( MapCommand command, bool applyNow )
	{
		if ( command == null ) throw new ArgumentNullException( nameof( command ) );
		if ( StrokeActive ) throw new InvalidOperationException( "Cannot execute a command while a stroke is active; use AddStrokeDab / AddStrokeCommand." );
		if ( command.IsNoOp ) return;
		if ( applyNow ) command.Do( _target );
		Push( command );
	}

	public void Undo()
	{
		if ( !CanUndo ) throw new InvalidOperationException( "Nothing to undo." );
		int last = _undoStack.Count - 1;
		var entry = _undoStack[last];
		_undoStack.RemoveAt( last );
		entry.Command.Undo( _target );
		_redoStack.Push( entry );
		CurrentStateId = entry.Before;
		if ( StateChanged != null ) StateChanged();
	}

	public void Redo()
	{
		if ( !CanRedo ) throw new InvalidOperationException( "Nothing to redo." );
		var entry = _redoStack.Pop();
		entry.Command.Do( _target );
		_undoStack.Add( entry );
		CurrentStateId = entry.After;
		EnforceCapacity();
		if ( StateChanged != null ) StateChanged();
	}

	/// <summary>Repeat the most recent command from the target's current state
	/// (TrenchBroom command repetition). Throws when the last command is not
	/// repeatable (adds/removes) or there is nothing to repeat.</summary>
	public void RepeatLast()
	{
		if ( _undoStack.Count == 0 ) throw new InvalidOperationException( "Nothing to repeat." );
		var repeated = _undoStack[_undoStack.Count - 1].Command.Repeat( _target );
		if ( repeated == null )
			throw new InvalidOperationException(
				"Command '" + _undoStack[_undoStack.Count - 1].Command.Description + "' cannot be repeated." );
		Execute( repeated );
	}

	/// <summary>Repeat the most recent command (TrenchBroom command repetition),
	/// under the exact name the brush-accuracy ticket specifies. Identical to
	/// <see cref="RepeatLast"/>: replays the last command from the target's
	/// current state and pushes the replay as a new undo entry.</summary>
	public void RepeatLastCommand() => RepeatLast();

	public void Clear()
	{
		if ( StrokeActive ) throw new InvalidOperationException( "Cannot clear history while a stroke is active." );
		_undoStack.Clear();
		_redoStack.Clear();
		CurrentStateId = Guid.NewGuid();
		SavedStateId = CurrentStateId;
		if ( StateChanged != null ) StateChanged();
	}

	// ---- Stroke coalescing ----
	// A stroke is a run of dabs (displacement sculpting, or any small command) that
	// the user experiences as ONE edit. Dabs apply live so the user sees the stroke,
	// but only ONE entry reaches the undo stack, at EndStroke. Coalescing inside the
	// session is by TryCoalesce, so repeated dabs on the same face collapse too.

	public void BeginStroke( string description )
	{
		if ( StrokeActive ) throw new InvalidOperationException( "A stroke is already active." );
		if ( string.IsNullOrWhiteSpace( description ) ) throw new ArgumentException( "A stroke needs a description.", nameof( description ) );
		_strokeDescription = description;
		_strokePending.Clear();
		_strokeBefore.Clear();
	}

	/// <summary>One displacement dab inside an active stroke. The first dab on a face
	/// records that face's pre-stroke heights; later dabs on the same face fold into
	/// the pending command (earliest before, latest after).</summary>
	public void AddDisplacementDab( string brushId, int faceIndex, float[] heights, int power )
	{
		if ( !StrokeActive ) throw new InvalidOperationException( "No stroke is active; call BeginStroke first." );
		if ( heights == null ) throw new ArgumentNullException( nameof( heights ) );
		string key = brushId + "#" + faceIndex;
		MapDisplacementState before;
		if ( !_strokeBefore.TryGetValue( key, out before ) )
		{
			var current = _target.FindDisplacement( brushId, faceIndex );
			before = current != null
				? current.Clone()
				: new MapDisplacementState { BrushId = brushId, FaceIndex = faceIndex, Power = power, Heights = (float[])heights.Clone() };
			_strokeBefore[key] = before;
		}
		var dab = new SetDisplacementCommand( brushId, faceIndex,
			(float[])before.Heights.Clone(), before.Power, (float[])heights.Clone(), power );
		AddStrokeCommand( dab );
	}

	/// <summary>Any small command as one dab of the active stroke. Applies immediately
	/// (live preview); the finished stroke is a single undo entry.</summary>
	public void AddStrokeCommand( MapCommand dab )
	{
		if ( !StrokeActive ) throw new InvalidOperationException( "No stroke is active; call BeginStroke first." );
		if ( dab == null ) throw new ArgumentNullException( nameof( dab ) );
		if ( dab.IsNoOp ) return;
		dab.Do( _target );
		if ( _strokePending.Count > 0 && _strokePending[_strokePending.Count - 1].TryCoalesce( dab ) ) return;
		_strokePending.Add( dab );
	}

	/// <summary>Finish the stroke: its dabs become one undo entry. An empty stroke
	/// (or one whose dabs all cancelled out) pushes nothing.</summary>
	public void EndStroke()
	{
		if ( !StrokeActive ) throw new InvalidOperationException( "No stroke is active." );
		string description = _strokeDescription;
		var pending = _strokePending.ToArray();
		_strokeDescription = null;
		_strokePending.Clear();
		_strokeBefore.Clear();
		if ( pending.Length == 0 ) return;
		MapCommand command = pending.Length == 1
			? pending[0]
			: new CompositeMapCommand( description, pending );
		// Already applied live; push without re-applying.
		Push( command );
	}

	/// <summary>Abandon the stroke: every dab is undone in reverse order and nothing
	/// reaches the undo stack.</summary>
	public void CancelStroke()
	{
		if ( !StrokeActive ) throw new InvalidOperationException( "No stroke is active." );
		for ( int i = _strokePending.Count - 1; i >= 0; i-- ) _strokePending[i].Undo( _target );
		_strokeDescription = null;
		_strokePending.Clear();
		_strokeBefore.Clear();
		if ( StateChanged != null ) StateChanged();
	}

	void Push( MapCommand command )
	{
		var entry = new Entry { Command = command, Before = CurrentStateId, After = Guid.NewGuid() };
		_redoStack.Clear();
		_undoStack.Add( entry );
		CurrentStateId = entry.After;
		EnforceCapacity();
		if ( StateChanged != null ) StateChanged();
	}

	void EnforceCapacity()
	{
		if ( _capacity <= 0 ) return;
		while ( _undoStack.Count > _capacity ) _undoStack.RemoveAt( 0 );
	}
}

/// <summary>Reference implementation of <see cref="IMapUndoTarget"/> and the target the
/// tests drive: plain in-memory lists, revision bumped on every mutation, snapshot
/// capture/apply by deep copy. The real MapDocument adapter must match these
/// semantics: duplicate ids and unknown ids are errors, never silent no-ops.</summary>
public sealed class InMemoryMapUndoTarget : IMapUndoTarget
{
	readonly List<MapBrushState> _brushes = new List<MapBrushState>();
	readonly List<MapEntityState> _entities = new List<MapEntityState>();
	readonly List<MapDisplacementState> _displacements = new List<MapDisplacementState>();
	readonly List<MapIoState> _ios = new List<MapIoState>();
	long _revision = 1;
	long _savedRevision = 1;

	public InMemoryMapUndoTarget( string documentId, string sourcePath )
	{
		DocumentId = documentId ?? throw new ArgumentNullException( nameof( documentId ) );
		SourcePath = sourcePath ?? "";
	}

	public string DocumentId { get; }
	public string SourcePath { get; }
	public long Revision => _revision;
	public bool IsDirty => _revision != _savedRevision;

	public IReadOnlyList<MapBrushState> Brushes => _brushes;
	public IReadOnlyList<MapEntityState> Entities => _entities;
	public IReadOnlyList<MapDisplacementState> Displacements => _displacements;
	public IReadOnlyList<MapIoState> IoConnections => _ios;

	public MapBrushState FindBrush( string id ) => _brushes.FirstOrDefault( b => b.Id == id );
	public MapEntityState FindEntity( string id ) => _entities.FirstOrDefault( e => e.Id == id );
	public MapDisplacementState FindDisplacement( string brushId, int faceIndex ) =>
		_displacements.FirstOrDefault( d => d.BrushId == brushId && d.FaceIndex == faceIndex );
	public MapIoState FindIo( string id ) => _ios.FirstOrDefault( i => i.Id == id );

	public void AddBrush( MapBrushState state )
	{
		if ( state == null ) throw new ArgumentNullException( nameof( state ) );
		if ( FindBrush( state.Id ) != null ) throw new InvalidOperationException( "Brush '" + state.Id + "' already exists." );
		_brushes.Add( state.Clone() );
		Bump();
	}

	public void RemoveBrush( string id )
	{
		var brush = RequireBrush( id );
		_brushes.Remove( brush );
		_displacements.RemoveAll( d => d.BrushId == id );
		Bump();
	}

	public void MoveBrush( string id, Vec3 min, Vec3 max )
	{
		var brush = RequireBrush( id );
		brush.Min = min;
		brush.Max = max;
		Bump();
	}

	public void SetBrushMaterial( string id, string material )
	{
		RequireBrush( id ).Material = material ?? "";
		Bump();
	}

	public void AddEntity( MapEntityState state )
	{
		if ( state == null ) throw new ArgumentNullException( nameof( state ) );
		if ( FindEntity( state.Id ) != null ) throw new InvalidOperationException( "Entity '" + state.Id + "' already exists." );
		_entities.Add( state.Clone() );
		Bump();
	}

	public void RemoveEntity( string id )
	{
		var entity = RequireEntity( id );
		_entities.Remove( entity );
		_ios.RemoveAll( i => i.SourceId == id || i.TargetId == id );
		Bump();
	}

	public void MoveEntity( string id, Vec3 origin )
	{
		RequireEntity( id ).Origin = origin;
		Bump();
	}

	public void SetEntityProperty( string id, string key, string value )
	{
		var entity = RequireEntity( id );
		if ( string.IsNullOrWhiteSpace( key ) ) throw new ArgumentException( "Property key cannot be empty.", nameof( key ) );
		if ( value == null ) throw new ArgumentNullException( nameof( value ) );
		entity.Properties[key] = value;
		Bump();
	}

	public void RemoveEntityProperty( string id, string key )
	{
		var entity = RequireEntity( id );
		entity.Properties.Remove( key );
		Bump();
	}

	public void AddDisplacement( MapDisplacementState state )
	{
		if ( state == null ) throw new ArgumentNullException( nameof( state ) );
		if ( FindDisplacement( state.BrushId, state.FaceIndex ) != null )
			throw new InvalidOperationException( "Displacement already exists on brush '" + state.BrushId + "' face " + state.FaceIndex + "." );
		RequireBrush( state.BrushId );
		_displacements.Add( state.Clone() );
		Bump();
	}

	public void SetDisplacement( string brushId, int faceIndex, float[] heights, int power )
	{
		var disp = FindDisplacement( brushId, faceIndex );
		if ( disp == null )
		{
			AddDisplacement( new MapDisplacementState
				{ BrushId = brushId, FaceIndex = faceIndex, Power = power, Heights = (float[])heights.Clone() } );
			return;
		}
		disp.Heights = (float[])heights.Clone();
		disp.Power = power;
		Bump();
	}

	public void RemoveDisplacement( string brushId, int faceIndex )
	{
		var disp = FindDisplacement( brushId, faceIndex );
		if ( disp == null ) throw new InvalidOperationException( "No displacement on brush '" + brushId + "' face " + faceIndex + "." );
		_displacements.Remove( disp );
		Bump();
	}

	public void AddIo( MapIoState state )
	{
		if ( state == null ) throw new ArgumentNullException( nameof( state ) );
		if ( FindIo( state.Id ) != null ) throw new InvalidOperationException( "I/O connection '" + state.Id + "' already exists." );
		_ios.Add( state.Clone() );
		Bump();
	}

	public void SetIo( MapIoState state )
	{
		if ( state == null ) throw new ArgumentNullException( nameof( state ) );
		var existing = FindIo( state.Id );
		if ( existing == null ) throw new InvalidOperationException( "No I/O connection '" + state.Id + "'." );
		_ios[_ios.IndexOf( existing )] = state.Clone();
		Bump();
	}

	public void RemoveIo( string id )
	{
		var io = FindIo( id );
		if ( io == null ) throw new InvalidOperationException( "No I/O connection '" + id + "'." );
		_ios.Remove( io );
		Bump();
	}

	public MapStateSnapshot CaptureSnapshot()
	{
		return new MapStateSnapshot
		{
			DocumentId = DocumentId,
			Revision = Revision,
			SavedAtUtc = DateTimeOffset.UtcNow,
			Brushes = _brushes.Select( b => b.Clone() ).ToList(),
			Entities = _entities.Select( e => e.Clone() ).ToList(),
			Displacements = _displacements.Select( d => d.Clone() ).ToList(),
			IoConnections = _ios.Select( i => i.Clone() ).ToList()
		};
	}

	public void ApplySnapshot( MapStateSnapshot snapshot )
	{
		if ( snapshot == null ) throw new ArgumentNullException( nameof( snapshot ) );
		snapshot.Validate();
		_brushes.Clear();
		_brushes.AddRange( snapshot.Brushes.Select( b => b.Clone() ) );
		_entities.Clear();
		_entities.AddRange( snapshot.Entities.Select( e => e.Clone() ) );
		_displacements.Clear();
		_displacements.AddRange( snapshot.Displacements.Select( d => d.Clone() ) );
		_ios.Clear();
		_ios.AddRange( snapshot.IoConnections.Select( i => i.Clone() ) );
		_revision = Math.Max( 1, snapshot.Revision );
		Bump();
	}

	public string SerializeSnapshot() => MapUndoJson.Write( CaptureSnapshot() );

	public void RestoreSnapshot( string json )
	{
		ApplySnapshot( MapUndoJson.Read( json ) );
	}

	public void MarkSaved()
	{
		_savedRevision = _revision;
	}

	MapBrushState RequireBrush( string id ) =>
		FindBrush( id ) ?? throw new InvalidOperationException( "No brush '" + id + "'." );
	MapEntityState RequireEntity( string id ) =>
		FindEntity( id ) ?? throw new InvalidOperationException( "No entity '" + id + "'." );

	void Bump() => _revision++;
}

/// <summary>JSON form of <see cref="MapStateSnapshot"/>: indented, diffable text.
/// Vec3 travels as [x, y, z] float arrays because System.Text.Json does not serialise
/// the kernel's readonly fields. Unknown extra JSON members are ignored on read;
/// a wrong schema version or a duplicate id fails loudly (see Validate).</summary>
public static class MapUndoJson
{
	sealed class BrushDto
	{
		public string Id { get; set; } = "";
		public float[] Min { get; set; } = Array.Empty<float>();
		public float[] Max { get; set; } = Array.Empty<float>();
		public string Material { get; set; } = "";
	}
	sealed class EntityDto
	{
		public string Id { get; set; } = "";
		public string ClassName { get; set; } = "";
		public float[] Origin { get; set; } = Array.Empty<float>();
		public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
	}
	sealed class DispDto
	{
		public string BrushId { get; set; } = "";
		public int FaceIndex { get; set; }
		public int Power { get; set; }
		public float[] Heights { get; set; } = Array.Empty<float>();
	}
	sealed class IoDto
	{
		public string Id { get; set; } = "";
		public string SourceId { get; set; } = "";
		public string TargetId { get; set; } = "";
		public string Output { get; set; } = "";
		public string Input { get; set; } = "";
		public string Parameter { get; set; } = "";
		public float DelaySeconds { get; set; }
		public int TimesToFire { get; set; }
	}
	sealed class SnapshotDto
	{
		public int SchemaVersion { get; set; }
		public string DocumentId { get; set; } = "";
		public long Revision { get; set; }
		public DateTimeOffset SavedAtUtc { get; set; }
		public List<BrushDto> Brushes { get; set; } = new List<BrushDto>();
		public List<EntityDto> Entities { get; set; } = new List<EntityDto>();
		public List<DispDto> Displacements { get; set; } = new List<DispDto>();
		public List<IoDto> IoConnections { get; set; } = new List<IoDto>();
	}

	static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions { WriteIndented = true };
	static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

	public static string Write( MapStateSnapshot snapshot )
	{
		if ( snapshot == null ) throw new ArgumentNullException( nameof( snapshot ) );
		snapshot.Validate();
		var dto = new SnapshotDto
		{
			SchemaVersion = snapshot.SchemaVersion,
			DocumentId = snapshot.DocumentId,
			Revision = snapshot.Revision,
			SavedAtUtc = snapshot.SavedAtUtc,
			Brushes = snapshot.Brushes.Select( b => new BrushDto
				{ Id = b.Id, Min = Vec( b.Min ), Max = Vec( b.Max ), Material = b.Material } ).ToList(),
			Entities = snapshot.Entities.Select( e => new EntityDto
				{ Id = e.Id, ClassName = e.ClassName, Origin = Vec( e.Origin ),
				  Properties = new Dictionary<string, string>( e.Properties ) } ).ToList(),
			Displacements = snapshot.Displacements.Select( d => new DispDto
				{ BrushId = d.BrushId, FaceIndex = d.FaceIndex, Power = d.Power,
				  Heights = (float[])d.Heights.Clone() } ).ToList(),
			IoConnections = snapshot.IoConnections.Select( i => new IoDto
				{ Id = i.Id, SourceId = i.SourceId, TargetId = i.TargetId, Output = i.Output,
				  Input = i.Input, Parameter = i.Parameter, DelaySeconds = i.DelaySeconds,
				  TimesToFire = i.TimesToFire } ).ToList()
		};
		return JsonSerializer.Serialize( dto, WriteOptions );
	}

	public static MapStateSnapshot Read( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) ) throw new InvalidDataException( "Map snapshot JSON is empty." );
		SnapshotDto dto;
		try
		{
			dto = JsonSerializer.Deserialize<SnapshotDto>( json, ReadOptions );
		}
		catch ( JsonException ex )
		{
			throw new InvalidDataException( "Map snapshot JSON is not valid: " + ex.Message, ex );
		}
		if ( dto == null ) throw new InvalidDataException( "Map snapshot JSON deserialised to nothing." );
		var snapshot = new MapStateSnapshot
		{
			SchemaVersion = dto.SchemaVersion,
			DocumentId = dto.DocumentId ?? "",
			Revision = dto.Revision,
			SavedAtUtc = dto.SavedAtUtc,
			Brushes = dto.Brushes.Select( b => new MapBrushState
				{ Id = b.Id ?? "", Min = Unvec( b.Min ), Max = Unvec( b.Max ), Material = b.Material ?? "" } ).ToList(),
			Entities = dto.Entities.Select( e => new MapEntityState
				{ Id = e.Id ?? "", ClassName = e.ClassName ?? "", Origin = Unvec( e.Origin ),
				  Properties = e.Properties ?? new Dictionary<string, string>() } ).ToList(),
			Displacements = dto.Displacements.Select( d => new MapDisplacementState
				{ BrushId = d.BrushId ?? "", FaceIndex = d.FaceIndex, Power = d.Power,
				  Heights = d.Heights ?? Array.Empty<float>() } ).ToList(),
			IoConnections = dto.IoConnections.Select( i => new MapIoState
				{ Id = i.Id ?? "", SourceId = i.SourceId ?? "", TargetId = i.TargetId ?? "",
				  Output = i.Output ?? "", Input = i.Input ?? "", Parameter = i.Parameter ?? "",
				  DelaySeconds = i.DelaySeconds, TimesToFire = i.TimesToFire } ).ToList()
		};
		snapshot.Validate();
		return snapshot;
	}

	static float[] Vec( Vec3 v ) => new[] { v.x, v.y, v.z };

	static Vec3 Unvec( float[] v )
	{
		if ( v == null || v.Length != 3 )
			throw new InvalidDataException( "Map snapshot has a vector that is not [x, y, z]." );
		return new Vec3( v[0], v[1], v[2] );
	}
}

/// <summary>One autosave on disk: the payload file next to the map plus the recovery
/// record that points at it. The record's Crashed flag is the crash marker: an
/// autosave is written assuming a crash (Crashed = true) and only a clean shutdown
/// clears it, so a record that still says crashed after a restart means the last
/// session died and the autosave must be offered first.</summary>
public sealed class MapAutosaveRecord
{
	// Properties, not fields: System.Text.Json ignores public fields by default,
	// and this record is serialised directly (unlike MapStateSnapshot, which goes
	// through MapUndoJson DTOs).
	public const int CurrentSchemaVersion = 1;
	public int SchemaVersion { get; set; } = CurrentSchemaVersion;
	public string DocumentId { get; set; } = "";
	public long Revision { get; set; }
	public string MapPath { get; set; } = "";
	public string AutosavePath { get; set; } = "";
	public DateTimeOffset TimestampUtc { get; set; }
	public bool Crashed { get; set; } = true;
}

/// <summary>Autosave + crash recovery for maps. The autosave file lives NEXT TO the
/// map (same directory, "<name>.autosave<ext>"), never in a distant recovery folder:
/// a mapper who loses the app finds the autosave beside the file they were editing.
/// Writes are atomic (temp file in the same directory + move), the same rule as the
/// desktop save contract (CONTRACTS.md: same-directory temp + flush + atomic rename,
/// never delete the destination first). Recovery never deletes anything until the
/// user accepts or discards it.</summary>
public static class MapAutosave
{
	public const string AutosaveSuffix = ".autosave";
	public const string RecoverySuffix = ".recovery.json";

	/// <summary>Autosave path for a map: "<stem>.autosave<ext>" in the map's directory.
	/// A map with no path yet (never saved) gets "<DocumentId>.autosave.effigy-map"
	/// in the supplied directory.</summary>
	public static string AutosaveFilePathFor( string mapPath, string documentId, string directoryForUnsaved )
	{
		if ( !string.IsNullOrWhiteSpace( mapPath ) )
		{
			string dir = Path.GetDirectoryName( Path.GetFullPath( mapPath ) ) ?? "";
			string stem = Path.GetFileNameWithoutExtension( mapPath );
			string ext = Path.GetExtension( mapPath );
			if ( string.IsNullOrEmpty( ext ) ) ext = ".effigy-map";
			return Path.Combine( dir, stem + AutosaveSuffix + ext );
		}
		if ( string.IsNullOrWhiteSpace( documentId ) )
			throw new ArgumentException( "An unsaved map needs a document id for its autosave file.", nameof( documentId ) );
		string fallbackDir = string.IsNullOrWhiteSpace( directoryForUnsaved )
			? Directory.GetCurrentDirectory()
			: directoryForUnsaved;
		return Path.Combine( fallbackDir, documentId + AutosaveSuffix + ".effigy-map" );
	}

	public static string RecoveryFilePathFor( string mapPath, string documentId, string directoryForUnsaved )
	{
		string autosave = AutosaveFilePathFor( mapPath, documentId, directoryForUnsaved );
		string dir = Path.GetDirectoryName( autosave ) ?? "";
		string stem = Path.GetFileNameWithoutExtension( autosave );
		return Path.Combine( dir, stem + RecoverySuffix );
	}

	/// <summary>Write the autosave payload + recovery record. The record is left
	/// marked Crashed = true: only <see cref="MarkCleanShutdown"/> clears it.</summary>
	public static MapAutosaveRecord WriteAutosave( IMapAutosaveSource source, string mapPath, string directoryForUnsaved = null )
	{
		if ( source == null ) throw new ArgumentNullException( nameof( source ) );
		string autosavePath = AutosaveFilePathFor( mapPath, source.DocumentId, directoryForUnsaved );
		string recoveryPath = RecoveryFilePathFor( mapPath, source.DocumentId, directoryForUnsaved );
		string payload = source.SerializeSnapshot();

		string dir = Path.GetDirectoryName( autosavePath ) ?? "";
		if ( !string.IsNullOrEmpty( dir ) ) Directory.CreateDirectory( dir );
		WriteAtomic( autosavePath, payload );

		var record = new MapAutosaveRecord
		{
			DocumentId = source.DocumentId,
			Revision = source.Revision,
			MapPath = mapPath ?? "",
			AutosavePath = autosavePath,
			TimestampUtc = DateTimeOffset.UtcNow,
			Crashed = true
		};
		WriteAtomic( recoveryPath, JsonSerializer.Serialize( record, new JsonSerializerOptions { WriteIndented = true } ) );
		return record;
	}

	/// <summary>The recovery record next to the map, or null when there is none, it
	/// is unreadable, or its autosave payload is gone. Corrupt records are ignored,
	/// never thrown from discovery.</summary>
	public static MapAutosaveRecord TryFindRecovery( string mapPath, string documentId = null, string directoryForUnsaved = null )
	{
		string recoveryPath;
		try
		{
			recoveryPath = RecoveryFilePathFor( mapPath, documentId, directoryForUnsaved );
		}
		catch
		{
			return null;
		}
		if ( !File.Exists( recoveryPath ) ) return null;
		try
		{
			var record = JsonSerializer.Deserialize<MapAutosaveRecord>( File.ReadAllText( recoveryPath ),
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true } );
			if ( record == null || record.SchemaVersion != MapAutosaveRecord.CurrentSchemaVersion ) return null;
			if ( string.IsNullOrWhiteSpace( record.AutosavePath ) || !File.Exists( record.AutosavePath ) ) return null;
			return record;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>True when a crashed session left an autosave behind: the host must
	/// offer the autosave FIRST on open, before the last-saved map.</summary>
	public static bool ShouldOfferRecovery( string mapPath, string documentId = null, string directoryForUnsaved = null )
	{
		var record = TryFindRecovery( mapPath, documentId, directoryForUnsaved );
		return record != null && record.Crashed;
	}

	/// <summary>Clean shutdown: the autosave is no longer a crash artefact. The files
	/// stay on disk (the user may still want them); only the marker clears.</summary>
	public static void MarkCleanShutdown( string mapPath, string documentId = null, string directoryForUnsaved = null )
	{
		var record = TryFindRecovery( mapPath, documentId, directoryForUnsaved );
		if ( record == null ) return;
		record.Crashed = false;
		string recoveryPath = RecoveryFilePathFor( mapPath, documentId, directoryForUnsaved );
		WriteAtomic( recoveryPath, JsonSerializer.Serialize( record, new JsonSerializerOptions { WriteIndented = true } ) );
	}

	/// <summary>Delete the autosave payload + recovery record after the user restores
	/// from it or declines it. Best effort; a missing file is not an error.</summary>
	public static void DiscardRecovery( string mapPath, string documentId = null, string directoryForUnsaved = null )
	{
		var record = TryFindRecovery( mapPath, documentId, directoryForUnsaved );
		if ( record == null ) return;
		TryDelete( record.AutosavePath );
		TryDelete( RecoveryFilePathFor( mapPath, documentId, directoryForUnsaved ) );
	}

	/// <summary>Restore a map target from a recovery record's autosave payload.</summary>
	public static void Restore( IMapAutosaveSource source, MapAutosaveRecord record )
	{
		if ( source == null ) throw new ArgumentNullException( nameof( source ) );
		if ( record == null ) throw new ArgumentNullException( nameof( record ) );
		if ( !File.Exists( record.AutosavePath ) )
			throw new FileNotFoundException( "Autosave payload not found: " + record.AutosavePath );
		source.RestoreSnapshot( File.ReadAllText( record.AutosavePath ) );
	}

	static void WriteAtomic( string path, string content )
	{
		string temp = path + ".tmp-" + Guid.NewGuid().ToString( "N" );
		try
		{
			using ( var stream = new FileStream( temp, FileMode.Create, FileAccess.Write, FileShare.None ) )
			using ( var writer = new StreamWriter( stream, new UTF8Encoding( false ) ) )
			{
				writer.Write( content );
				writer.Flush();
				stream.Flush();
			}
			File.Move( temp, path, true );
		}
		finally
		{
			try { if ( File.Exists( temp ) ) File.Delete( temp ); } catch { }
		}
	}

	static void TryDelete( string path )
	{
		try { if ( !string.IsNullOrWhiteSpace( path ) && File.Exists( path ) ) File.Delete( path ); } catch { }
	}
}
