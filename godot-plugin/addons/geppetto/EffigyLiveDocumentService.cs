#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Marionette.Desktop;
using Effigy;

namespace Geppetto;

/// <summary>
/// G9: Live document service for Godot. Manages background rebuilds of .effigy PartStudio documents,
/// emits revision events, marshals snapshots onto Godot's main thread, and adapts Geppetto's
/// <see cref="EditHistory"/> to Godot's <see cref="UndoRedo"/> / <see cref="EditorUndoRedoManager"/>.
/// </summary>
public sealed class EffigyLiveDocumentService : IDisposable
{
	private readonly DocumentSession _session;
	private readonly MaterialResolver? _materialResolver;
	private CancellationTokenSource? _inFlightCts;
	private readonly object _lock = new();
	private bool _disposed;

	public DocumentSession Session => _session;
	public MaterialResolver? MaterialResolver => _materialResolver;

	/// <summary>Current rendered snapshot, updated only on Godot's main thread.</summary>
	public RenderSnapshot? CurrentSnapshot { get; private set; }

	/// <summary>Fired on Godot's main thread whenever a background rebuild succeeds.</summary>
	public event Action<DocumentSession, RenderSnapshot>? RebuildCompleted;

	/// <summary>Fired on Godot's main thread if a rebuild fails.</summary>
	public event Action<DocumentSession, Exception>? RebuildFailed;

	/// <summary>Fired on Godot's main thread whenever the document revision changes.</summary>
	public event Action<DocumentSession, long>? RevisionChanged;

	public EffigyLiveDocumentService( DocumentSession session, MaterialResolver? materialResolver = null )
	{
		_session = session ?? throw new ArgumentNullException( nameof( session ) );
		_materialResolver = materialResolver;
	}

	public static EffigyLiveDocumentService Open( string effigyPath )
	{
		ArgumentNullException.ThrowIfNull( effigyPath );
		string diskPath = Path.GetFullPath( ProjectSettings.GlobalizePath( effigyPath ) );
		var studio = StudioDocument.ReadFile( diskPath );
		var session = DocumentSession.CreateEffigy( diskPath, studio );

		string? dir = Path.GetDirectoryName( diskPath );
		MaterialResolver? resolver = null;
		if ( !string.IsNullOrEmpty( dir ) && Directory.Exists( dir ) )
		{
			resolver = new MaterialResolver( dir, Path.GetFileNameWithoutExtension( diskPath ) );
		}

		return new EffigyLiveDocumentService( session, resolver );
	}

	/// <summary>
	/// Triggers an asynchronous background rebuild. Cancels any in-flight rebuild.
	/// Dispatches results to Godot's main thread.
	/// </summary>
	public Task<RenderSnapshot?> RequestRebuildAsync()
	{
		lock ( _lock )
		{
			if ( _disposed ) return Task.FromResult<RenderSnapshot?>( null );

			_inFlightCts?.Cancel();
			_inFlightCts?.Dispose();
			_inFlightCts = new CancellationTokenSource();
			var token = _inFlightCts.Token;

			long revision = _session.Revision;
			Callable.From( () => RevisionChanged?.Invoke( _session, revision ) ).CallDeferred();

			return Task.Run( () =>
			{
				try
				{
					token.ThrowIfCancellationRequested();

					if ( _session.Studio == null )
						throw new InvalidOperationException( "DocumentSession has no PartStudio." );

					// Autorun / Rebuild PartStudio
					_session.Studio.Rebuild();
					token.ThrowIfCancellationRequested();

					// Extract CpuMesh
					var cpuMesh = CpuMesh.FromStudio( _session.Studio, 0, _session.SourcePath );
					token.ThrowIfCancellationRequested();

					var snapshot = new RenderSnapshot(
						_session.DocumentId,
						revision,
						cpuMesh,
						player: null,
						paintFingerprint: _session.PaintFingerprint()
					);

					// Marshal to Godot main thread
					Callable.From( () =>
					{
						if ( token.IsCancellationRequested || _disposed )
						{
							snapshot.Dispose();
							return;
						}

						CurrentSnapshot?.Dispose();
						CurrentSnapshot = snapshot;
						RebuildCompleted?.Invoke( _session, snapshot );
					} ).CallDeferred();

					return (RenderSnapshot?)snapshot;
				}
				catch ( OperationCanceledException )
				{
					return null;
				}
				catch ( Exception ex )
				{
					Callable.From( () =>
					{
						if ( !token.IsCancellationRequested && !_disposed )
							RebuildFailed?.Invoke( _session, ex );
					} ).CallDeferred();
					return null;
				}
			}, token );
		}
	}

	/// <summary>
	/// Applies the current or newly built snapshot directly to a target <see cref="MeshInstance3D"/>.
	/// </summary>
	public void ApplyToMeshInstance( MeshInstance3D meshInstance, RenderSnapshot snapshot )
	{
		ArgumentNullException.ThrowIfNull( meshInstance );
		ArgumentNullException.ThrowIfNull( snapshot );

		var arrayMesh = EffigyMeshBridge.ToGodotArrayMesh( snapshot.Mesh, _materialResolver );
		meshInstance.Mesh = arrayMesh;
	}

	public void Dispose()
	{
		lock ( _lock )
		{
			if ( _disposed ) return;
			_disposed = true;
			_inFlightCts?.Cancel();
			_inFlightCts?.Dispose();
			_inFlightCts = null;
			CurrentSnapshot?.Dispose();
			CurrentSnapshot = null;
		}
	}
}

/// <summary>
/// RefCounted helper target for Godot's UndoRedo callbacks.
/// </summary>
public partial class EffigyActionTarget : RefCounted
{
	private Action? _action;

	public EffigyActionTarget() { }

	public EffigyActionTarget( Action action )
	{
		_action = action;
	}

	public void Execute() => _action?.Invoke();
}

/// <summary>
/// G9: Undo/Redo adapter bridging Geppetto's <see cref="EditHistory"/> / <see cref="IEditCommand"/>
/// to Godot's <see cref="UndoRedo"/> and <see cref="EditorUndoRedoManager"/>.
/// </summary>
public sealed class EffigyUndoRedoAdapter
{
	private readonly DocumentSession _session;
	private readonly UndoRedo? _engineUndoRedo;

	public EffigyUndoRedoAdapter( DocumentSession session, UndoRedo? engineUndoRedo = null )
	{
		_session = session ?? throw new ArgumentNullException( nameof( session ) );
		_engineUndoRedo = engineUndoRedo;
	}

	/// <summary>
	/// Executes and records a command in both Geppetto's <see cref="EditHistory"/>
	/// and Godot's undo-redo system.
	/// </summary>
	public void Execute( IEditCommand command, string? actionName = null )
	{
		ArgumentNullException.ThrowIfNull( command );
		string name = actionName ?? command.Description;

		// Execute in Geppetto EditHistory
		_session.History.Execute( command );

		// Record in Godot UndoRedo if available
		if ( _engineUndoRedo != null )
		{
			var doTarget = new EffigyActionTarget( command.Execute );
			var undoTarget = new EffigyActionTarget( command.Undo );

			_engineUndoRedo.CreateAction( name );
			_engineUndoRedo.AddDoMethod( new Callable( doTarget, EffigyActionTarget.MethodName.Execute ) );
			_engineUndoRedo.AddUndoMethod( new Callable( undoTarget, EffigyActionTarget.MethodName.Execute ) );
			_engineUndoRedo.CommitAction( execute: false ); // Already executed above
		}
	}

	/// <summary>
	/// Undoes the top action.
	/// </summary>
	public bool Undo()
	{
		if ( _engineUndoRedo != null && _engineUndoRedo.HasUndo() )
		{
			return _engineUndoRedo.Undo();
		}

		if ( _session.History.CanUndo )
		{
			_session.History.Undo();
			return true;
		}

		return false;
	}

	/// <summary>
	/// Redoes the top undone action.
	/// </summary>
	public bool Redo()
	{
		if ( _engineUndoRedo != null && _engineUndoRedo.HasRedo() )
		{
			return _engineUndoRedo.Redo();
		}

		if ( _session.History.CanRedo )
		{
			_session.History.Redo();
			return true;
		}

		return false;
	}
}
