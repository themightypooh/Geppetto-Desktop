#nullable enable
using System;
using System.Collections.Generic;

namespace Marionette.Desktop;

public interface IEditCommand
{
	string Description { get; }
	void Execute();
	void Undo();
	bool IsNoOp => false;
}

public sealed class DelegateEditCommand : IEditCommand
{
	public string Description { get; }
	readonly Action _execute;
	readonly Action _undo;
	readonly Func<bool>? _isNoOp;

	public DelegateEditCommand( string description, Action execute, Action undo, Func<bool>? isNoOp = null )
	{
		Description = description ?? throw new ArgumentNullException( nameof( description ) );
		_execute = execute ?? throw new ArgumentNullException( nameof( execute ) );
		_undo = undo ?? throw new ArgumentNullException( nameof( undo ) );
		_isNoOp = isNoOp;
	}

	public void Execute() => _execute();
	public void Undo() => _undo();
	public bool IsNoOp => _isNoOp?.Invoke() ?? false;
}

public sealed class ValueChangeCommand<T> : IEditCommand
{
	public string Description { get; }
	readonly Action<T> _setter;
	readonly T _before;
	readonly T _after;
	readonly IEqualityComparer<T> _comparer;

	public ValueChangeCommand( string description, Action<T> setter, T before, T after, IEqualityComparer<T>? comparer = null )
	{
		Description = description ?? throw new ArgumentNullException( nameof( description ) );
		_setter = setter ?? throw new ArgumentNullException( nameof( setter ) );
		_before = before;
		_after = after;
		_comparer = comparer ?? EqualityComparer<T>.Default;
	}

	public void Execute() => _setter( _after );
	public void Undo() => _setter( _before );
	public bool IsNoOp => _comparer.Equals( _before, _after );
}

public sealed class EditTransaction : IDisposable
{
	readonly EditHistory _history;
	readonly string _description;
	readonly Action _restoreBefore;
	readonly List<IEditCommand> _joined = new();
	bool _finished;

	internal EditTransaction( EditHistory history, string description, Action restoreBefore )
	{
		_history = history ?? throw new ArgumentNullException( nameof( history ) );
		_description = description ?? throw new ArgumentNullException( nameof( description ) );
		_restoreBefore = restoreBefore ?? throw new ArgumentNullException( nameof( restoreBefore ) );
	}

	public void UpdatePreview( Action applyPreview )
	{
		if ( _finished ) throw new InvalidOperationException( "Transaction is already finished." );
		ArgumentNullException.ThrowIfNull( applyPreview );
		applyPreview();
	}

	/// <summary>Attach a command that executes and undoes with this transaction so both halves of
	/// one user action (a pose override plus the clip key it implies) share a single undo entry.
	/// Joined commands run after the final apply and, on undo, before the restore.</summary>
	public void JoinCommand( IEditCommand command )
	{
		if ( _finished ) throw new InvalidOperationException( "Transaction is already finished." );
		ArgumentNullException.ThrowIfNull( command );
		_joined.Add( command );
	}

	public void Commit( Action applyFinal, Func<bool>? isNoOp = null )
	{
		if ( _finished ) throw new InvalidOperationException( "Transaction is already finished." );
		ArgumentNullException.ThrowIfNull( applyFinal );
		_finished = true;

		if ( _joined.Count == 0 )
		{
			_history.CommitTransaction( _description, _restoreBefore, applyFinal, isNoOp );
			return;
		}

		var joined = _joined.ToArray();
		// A joined key is itself an edit, so the pair must not collapse to a no-op just because
		// the bone ended up where it started: the whole action is a no-op only if the pose did
		// not move *and* every joined command reports no change.
		bool Combined()
		{
			if ( isNoOp?.Invoke() != true ) return false;
			for ( int i = 0; i < joined.Length; i++ )
				if ( !joined[i].IsNoOp ) return false;
			return true;
		}

		_history.CommitTransaction( _description, _restoreBefore, applyFinal, Combined, joined );
	}

	public void Cancel()
	{
		if ( _finished ) return;
		_finished = true;
		_restoreBefore();
		_history.CancelTransaction();
	}

	public void Dispose()
	{
		if ( !_finished ) Cancel();
	}
}

public sealed class EditHistory
{
	sealed record HistoryEntry( IEditCommand Command, Guid StateIdBefore, Guid StateIdAfter );

	readonly List<HistoryEntry> _undoStack = new();
	readonly Stack<HistoryEntry> _redoStack = new();
	readonly int _capacity;

	public int Capacity => _capacity;
	public int UndoCount => _undoStack.Count;
	public int RedoCount => _redoStack.Count;
	public bool CanUndo => _undoStack.Count > 0 && CurrentTransaction == null;
	public bool CanRedo => _redoStack.Count > 0 && CurrentTransaction == null;

	public Guid CurrentStateId { get; private set; } = Guid.NewGuid();
	public Guid SavedStateId { get; private set; }

	public bool IsDirty => CurrentStateId != SavedStateId;

	public bool SavedStateEvicted =>
		CurrentStateId != SavedStateId
		&& _undoStack.TrueForAll( e => e.StateIdBefore != SavedStateId && e.StateIdAfter != SavedStateId )
		&& !_redoStackContains( SavedStateId );

	public EditTransaction? CurrentTransaction { get; private set; }

	public event Action? StateChanged;

	public EditHistory( int capacity = 100 )
	{
		if ( capacity < 1 ) throw new ArgumentOutOfRangeException( nameof( capacity ), "Capacity must be at least 1." );
		_capacity = capacity;
		SavedStateId = CurrentStateId;
	}

	public void MarkSaved()
	{
		SavedStateId = CurrentStateId;
	}

	public void MarkSaved( Guid stateId )
	{
		SavedStateId = stateId;
	}

	public void Execute( IEditCommand command )
	{
		ArgumentNullException.ThrowIfNull( command );
		if ( CurrentTransaction != null ) throw new InvalidOperationException( "Cannot execute a command while a transaction is active." );

		if ( command.IsNoOp ) return;

		command.Execute();

		var entry = new HistoryEntry( command, CurrentStateId, Guid.NewGuid() );
		_redoStack.Clear();
		_undoStack.Add( entry );
		CurrentStateId = entry.StateIdAfter;

		EnforceCapacity();
		StateChanged?.Invoke();
	}

	public void Undo()
	{
		if ( !CanUndo ) throw new InvalidOperationException( "Nothing to undo." );

		int lastIndex = _undoStack.Count - 1;
		var entry = _undoStack[lastIndex];
		_undoStack.RemoveAt( lastIndex );

		entry.Command.Undo();

		_redoStack.Push( entry );
		CurrentStateId = entry.StateIdBefore;

		StateChanged?.Invoke();
	}

	public void Redo()
	{
		if ( !CanRedo ) throw new InvalidOperationException( "Nothing to redo." );

		var entry = _redoStack.Pop();
		entry.Command.Execute();

		_undoStack.Add( entry );
		CurrentStateId = entry.StateIdAfter;

		EnforceCapacity();
		StateChanged?.Invoke();
	}

	public void Clear()
	{
		if ( CurrentTransaction != null ) throw new InvalidOperationException( "Cannot clear history while a transaction is active." );
		_undoStack.Clear();
		_redoStack.Clear();
		CurrentStateId = Guid.NewGuid();
		SavedStateId = CurrentStateId;
		StateChanged?.Invoke();
	}

	public EditTransaction BeginTransaction( string description, Action restoreBefore )
	{
		if ( CurrentTransaction != null ) throw new InvalidOperationException( "A transaction is already active." );
		var tx = new EditTransaction( this, description, restoreBefore );
		CurrentTransaction = tx;
		return tx;
	}

	internal void CommitTransaction( string description, Action restoreBefore, Action applyFinal, Func<bool>? isNoOp,
		IReadOnlyList<IEditCommand>? joined = null )
	{
		CurrentTransaction = null;
		if ( isNoOp?.Invoke() == true )
		{
			restoreBefore();
			return;
		}

		applyFinal();

		Action apply = applyFinal;
		Action restore = restoreBefore;
		if ( joined is { Count: > 0 } )
		{
			apply = () =>
			{
				applyFinal();
				for ( int i = 0; i < joined.Count; i++ ) joined[i].Execute();
			};
			restore = () =>
			{
				for ( int i = joined.Count - 1; i >= 0; i-- ) joined[i].Undo();
				restoreBefore();
			};
			apply();
		}

		var cmd = new DelegateEditCommand( description, apply, restore, isNoOp );
		var entry = new HistoryEntry( cmd, CurrentStateId, Guid.NewGuid() );

		_redoStack.Clear();
		_undoStack.Add( entry );
		CurrentStateId = entry.StateIdAfter;

		EnforceCapacity();
		StateChanged?.Invoke();
	}

	internal void CancelTransaction()
	{
		CurrentTransaction = null;
		StateChanged?.Invoke();
	}

	void EnforceCapacity()
	{
		while ( _undoStack.Count > _capacity )
		{
			_undoStack.RemoveAt( 0 );
		}
	}

	bool _redoStackContains( Guid stateId )
	{
		foreach ( var entry in _redoStack )
		{
			if ( entry.StateIdBefore == stateId || entry.StateIdAfter == stateId ) return true;
		}
		return false;
	}
}
