#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Effigy;
using Godot;
using Marionette.Desktop;

namespace Geppetto;

/// <summary>What happened to one document on a save.</summary>
public enum EffigySaveOutcome
{
	/// <summary>Not dirty, so it was not looked at.</summary>
	Clean,
	/// <summary>Serialised text matched the bytes on disk, so the file was left alone (mtime unchanged).</summary>
	Unchanged,
	/// <summary>Written through <see cref="DocumentSaveService"/> (temp file, reparse check, atomic rename).</summary>
	Written,
	/// <summary>The save service refused or failed; the file on disk is untouched.</summary>
	Failed,
}

public sealed record EffigySaveEntry( string Path, string Kind, EffigySaveOutcome Outcome, string? Message = null );

/// <summary>
/// G12: saves a Godot session's Geppetto edits back to the source <c>.effigy</c> and <c>.riganim</c>
/// files. The text files stay the source of truth; Godot's imported scene is only a view of them.
///
/// ALL WRITING GOES THROUGH <see cref="DocumentSaveService"/>, so the plugin gets the same temp-file,
/// reparse-before-replace, atomic rename(2) path the desktop app uses, and the same writers:
/// <see cref="StudioDocument.Write"/> (the hand-written text format, version header chosen by what the
/// document needs, notes kept as note blocks) and <see cref="RigAnimWriter.Write"/> (tracks from the
/// kernel, every other JSON field copied from the clip as it was read).
///
/// One extra rule on top of the service: when the serialised text equals the bytes already on disk,
/// nothing is written. An editor save on an untouched document therefore never rewrites the file.
///
/// The model lives in the <see cref="EffigyLiveDocumentService"/> session (G9). Clips get a session each,
/// because a <see cref="DocumentSession"/> saves one clip only and saving it to another path would relocate
/// the model session. Edit a clip through its session's <see cref="DocumentSession.History"/>
/// (e.g. <see cref="ClipEditService"/>), which is what marks it dirty.
///
/// Every live bridge registers itself, so <see cref="SaveAllOpen"/> can be called from the editor's
/// save (<c>EditorPlugin._SaveExternalData</c>, see GeppettoPlugin.Save.cs).
/// </summary>
public sealed class EffigySaveBridge : IDisposable
{
	static readonly object RegistryLock = new();
	static readonly List<EffigySaveBridge> Registry = new();

	readonly DocumentSaveService _saver;
	readonly List<DocumentSession> _clips = new();
	bool _disposed;

	public DocumentSession Model { get; }
	public EffigyLiveDocumentService? Live { get; }
	public IReadOnlyList<DocumentSession> Clips => _clips;

	public bool IsDirty => Model.IsDirty || _clips.Any( c => c.IsDirty );

	public EffigySaveBridge( EffigyLiveDocumentService live, DocumentSaveService? saver = null, bool register = true )
		: this( live?.Session ?? throw new ArgumentNullException( nameof( live ) ), saver, register )
	{
		Live = live;
	}

	public EffigySaveBridge( DocumentSession model, DocumentSaveService? saver = null, bool register = true )
	{
		Model = model ?? throw new ArgumentNullException( nameof( model ) );
		_saver = saver ?? new DocumentSaveService();
		if ( register )
			lock ( RegistryLock ) Registry.Add( this );
	}

	/// <summary>Open a .riganim for editing next to the model. Paths may be res:// or absolute.</summary>
	public DocumentSession OpenClip( string riganimPath )
	{
		ArgumentNullException.ThrowIfNull( riganimPath );
		string disk = Globalize( riganimPath );

		var existing = _clips.FirstOrDefault( c => SamePath( c.SourcePath, disk ) );
		if ( existing != null ) return existing;

		string json = File.ReadAllText( disk );
		var clip = RigAnimFile.Read( json );
		var studio = Model.Studio ?? new PartStudio();
		// SourcePath is the clip itself, so DocumentSaveService picks the .riganim writer and never
		// relocates anything.
		var session = DocumentSession.CreateEffigy( disk, studio, disk, json, clip );
		_clips.Add( session );
		return session;
	}

	/// <summary>Save every dirty document of this bridge (or all of them with <paramref name="force"/>).</summary>
	public IReadOnlyList<EffigySaveEntry> SaveAll( bool force = false )
	{
		var report = new List<EffigySaveEntry> { SaveOne( Model, force ) };
		foreach ( var clip in _clips )
			report.Add( SaveOne( clip, force ) );
		return report;
	}

	/// <summary>Write the model to a different path (Save As). The session moves to that path.</summary>
	public EffigySaveEntry SaveModelAs( string path ) => SaveTo( Model, Globalize( path ) );

	public EffigySaveEntry SaveOne( DocumentSession session, bool force = false )
	{
		ArgumentNullException.ThrowIfNull( session );
		string kind = KindOf( session.SourcePath );
		if ( !force && !session.IsDirty )
			return new EffigySaveEntry( session.SourcePath, kind, EffigySaveOutcome.Clean );
		return SaveTo( session, session.SourcePath );
	}

	EffigySaveEntry SaveTo( DocumentSession session, string path )
	{
		string kind = KindOf( path );

		// Skip the write when the bytes would not change. Only for an in-place save: Save As always writes.
		if ( SamePath( path, session.SourcePath ) && File.Exists( path ) )
		{
			try
			{
				long revision = session.Revision;
				Guid state = session.History.CurrentStateId;
				string? text = Serialize( session, path );
				if ( text != null && File.ReadAllBytes( path ).AsSpan().SequenceEqual( new UTF8Encoding( false ).GetBytes( text ) ) )
				{
					session.MarkSaved( revision, state );
					return new EffigySaveEntry( path, kind, EffigySaveOutcome.Unchanged );
				}
			}
			catch ( Exception )
			{
				// Fall through: the service reports the real error with its own wording.
			}
		}

		// NOT _saver.Save(): that blocks on SaveAsync, whose await resumes on the captured
		// SynchronizationContext. On Godot's main thread that is GodotSynchronizationContext, which can
		// only run once this frame returns, so the editor deadlocks. Starting it on the pool leaves no
		// context to capture.
		var result = System.Threading.Tasks.Task.Run( () => _saver.SaveAsync( session, path ) ).GetAwaiter().GetResult();
		return result.IsSuccess
			? new EffigySaveEntry( result.DestinationPath ?? path, kind, EffigySaveOutcome.Written )
			: new EffigySaveEntry( path, kind, EffigySaveOutcome.Failed, $"{result.Status}: {result.ErrorMessage}" );
	}

	/// <summary>The exact text DocumentSaveService would write for <paramref name="path"/>.</summary>
	public static string? Serialize( DocumentSession session, string path )
	{
		string ext = Path.GetExtension( path ).ToLowerInvariant();
		if ( ext == ".effigy" && session.Studio != null ) return StudioDocument.Write( session.Studio );
		if ( ext == ".riganim" && session.Clip != null ) return RigAnimWriter.Write( session.Clip, session.ClipSourceJson );
		return null;
	}

	/// <summary>Save every dirty document of every open bridge. Called from the editor's save.</summary>
	public static IReadOnlyList<EffigySaveEntry> SaveAllOpen()
	{
		EffigySaveBridge[] open;
		lock ( RegistryLock ) open = Registry.ToArray();

		var all = new List<EffigySaveEntry>();
		foreach ( var bridge in open )
		{
			foreach ( var entry in bridge.SaveAll() )
			{
				all.Add( entry );
				if ( entry.Outcome == EffigySaveOutcome.Written )
					GD.Print( $"[Geppetto] Saved {entry.Path}" );
				else if ( entry.Outcome == EffigySaveOutcome.Failed )
					GD.PushError( $"[Geppetto] Could not save {entry.Path}: {entry.Message}" );
			}
		}
		return all;
	}

	/// <summary>Paths of documents with unsaved edits, across all open bridges.</summary>
	public static IReadOnlyList<string> UnsavedPaths()
	{
		EffigySaveBridge[] open;
		lock ( RegistryLock ) open = Registry.ToArray();
		return open.SelectMany( b => new[] { b.Model }.Concat( b._clips ) )
			.Where( s => s.IsDirty )
			.Select( s => s.SourcePath )
			.ToList();
	}

	public void Dispose()
	{
		if ( _disposed ) return;
		_disposed = true;
		lock ( RegistryLock ) Registry.Remove( this );
	}

	static string KindOf( string path ) => Path.GetExtension( path ).TrimStart( '.' ).ToLowerInvariant();

	static string Globalize( string path ) =>
		Path.GetFullPath( path.StartsWith( "res://" ) || path.StartsWith( "user://" ) ? ProjectSettings.GlobalizePath( path ) : path );

	static bool SamePath( string a, string b ) => string.Equals(
		Path.GetFullPath( a ), Path.GetFullPath( b ),
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal );
}
