#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

/// <summary>
/// Assemblies 02: reusable part definitions and occurrence commands.
///
/// Port of Mobile app/AssemblyParts.cs (Assembly 03 data operations), adapted to the
/// Desktop ownership model: there is no CadDoc here, so the pure operations work on a
/// <see cref="PartStudio"/> (which owns the <see cref="AssemblyDocument"/> and the
/// bodies), and the <see cref="DocumentSession"/> overloads make exactly one
/// <see cref="EditHistory"/> entry per user action.
///
/// Data model (AssemblyDocument, AS01): a part definition (Id, Name, SourceKind,
/// SourceReference) is the shared source; an occurrence (Id, DefinitionId, Name,
/// Visible, Transform) is one placed copy. Placing, duplicating, renaming, hiding
/// or removing an occurrence never edits the shared source geometry: occurrences
/// carry only their own transform, in Effigy coordinates (inches, Z-up, radians
/// in core; the transform basis is dimensionless, its origin is in inches).
/// Occurrence edits change no kernel feature, so they need no kernel rebuild;
/// a source edit (rebuild) updates every occurrence of that definition at once.
///
/// Sources, first slice: internal bodies only, SourceKind "studio-body",
/// SourceReference = the body Id. Body Ids are deterministic
/// ("&lt;featureId&gt;b&lt;n&gt;"), so they survive rebuild, save and reopen.
/// External part files have no live link yet: a definition whose SourceKind is
/// anything else (for example "effigy-file" written by another tool) resolves
/// to missing. The record is kept and reported by <see cref="Issues"/>, never
/// silently deleted.
///
/// Removal and joints: removing an occurrence that joints reference is REFUSED,
/// naming the joints. Joint deletion belongs to the joints task; this service
/// never breaks a joint silently and never duplicates joint code. There is no
/// cascade delete.
///
/// Undo: a session operation snapshots the assembly JSON before the mutation
/// and restores whole snapshots on undo/redo, so one Undo reverts exactly one
/// user action and a redo restores the exact same occurrence Ids. Restore
/// replaces <c>studio.Assembly</c> with the parsed snapshot, so callers must
/// re-read <c>studio.Assembly</c> after undo/redo rather than holding the
/// object. No-op calls (rename to the current name, hide an already-hidden
/// occurrence, remove a missing one) record no history entry.
/// </summary>
public static class AssemblyPartService
{
	/// <summary>Source kind for parts taken from bodies already in this document.</summary>
	public const string StudioBodyKind = "studio-body";

	/// <summary>Why a removal did not happen.</summary>
	public enum AssemblyOccurrenceRemoveResult
	{
		Removed,
		NotFound,
		HasJoints,
	}

	/// <summary>Find an occurrence by its stable Id, or null.</summary>
	public static AssemblyOccurrence? FindOccurrence( AssemblyDocument? assembly, string? occurrenceId )
	{
		if ( assembly?.Occurrences == null || string.IsNullOrWhiteSpace( occurrenceId ) ) return null;
		return assembly.Occurrences.FirstOrDefault( o => o != null && o.Id == occurrenceId );
	}

	/// <summary>Display name for a body: the studio's BodyNames entry first, then the
	/// body's own name, then its Id. Same rule as the Bodies list, so the Parts list
	/// and the Bodies list agree.</summary>
	public static string BodyName( PartStudio? studio, Body? body )
	{
		if ( body == null ) return "";
		if ( studio != null && studio.BodyNames.TryGetValue( body.Id, out var named )
			&& !string.IsNullOrWhiteSpace( named ) )
			return named;
		return string.IsNullOrWhiteSpace( body.Name ) ? body.Id : body.Name;
	}

	/// <summary>Resolver for <see cref="AssemblyDocument.Validate"/>: can this
	/// definition's source be found in this document right now? Studio-body
	/// sources resolve against the live body list. Every other kind has no
	/// loader in this tree yet, so it reports missing — the record is kept,
	/// never deleted.</summary>
	public static bool SourceExists( PartStudio? studio, AssemblyPartDefinition? definition )
	{
		if ( studio == null || definition == null ) return false;
		if ( definition.SourceKind == StudioBodyKind )
			return studio.Bodies.Any( b => b != null && b.Id == definition.SourceReference );
		return false;
	}

	/// <summary>All Validate() issues for this document, with source existence
	/// resolved against its own bodies.</summary>
	public static IReadOnlyList<AssemblyValidationIssue> Issues( PartStudio? studio )
	{
		if ( studio?.Assembly == null ) return Array.Empty<AssemblyValidationIssue>();
		return studio.Assembly.Validate( d => SourceExists( studio, d ) );
	}

	/// <summary>The definition for a body, creating it when this is the first
	/// occurrence of that body. The caller owns the undo entry; see the
	/// <see cref="DocumentSession"/> overloads. Does not check that the body is
	/// in this studio — <see cref="AddOccurrence(PartStudio, Body)"/> does.</summary>
	public static AssemblyPartDefinition DefinitionForBody( PartStudio studio, Body body )
	{
		ArgumentNullException.ThrowIfNull( studio );
		ArgumentNullException.ThrowIfNull( body );
		var existing = studio.Assembly.Definitions.FirstOrDefault( d =>
			d != null && d.SourceKind == StudioBodyKind
			&& d.SourceReference == body.Id );
		if ( existing != null ) return existing;
		var created = new AssemblyPartDefinition
		{
			Id = AssemblyDocument.NewId(),
			Name = BodyName( studio, body ),
			SourceKind = StudioBodyKind,
			SourceReference = body.Id,
		};
		studio.Assembly.Definitions.Add( created );
		return created;
	}

	/// <summary>A name no other occurrence in this document uses yet:
	/// the base name, then "Name 2", "Name 3", ...</summary>
	public static string UniqueOccurrenceName( AssemblyDocument? assembly, string? baseName )
	{
		var name = string.IsNullOrWhiteSpace( baseName ) ? "Part" : baseName.Trim();
		var taken = new HashSet<string>(
			( assembly?.Occurrences ?? new List<AssemblyOccurrence>() ).Where( o => o != null ).Select( o => o.Name ),
			StringComparer.Ordinal );
		if ( !taken.Contains( name ) ) return name;
		for ( var n = 2; ; n++ )
		{
			var candidate = name + " " + n;
			if ( !taken.Contains( candidate ) ) return candidate;
		}
	}

	/// <summary>Add one occurrence of a body already in this document.
	/// Identity transform: the occurrence sits where its source body is.
	/// Returns the new occurrence, or null when the body is gone (not in
	/// this studio). One undo entry reverts the whole add (session overload).</summary>
	public static AssemblyOccurrence? AddOccurrence( PartStudio? studio, Body? body )
	{
		if ( studio == null || body == null ) return null;
		if ( !studio.Bodies.Any( b => b != null && b.Id == body.Id ) ) return null;
		var definition = DefinitionForBody( studio, body );
		var occurrence = new AssemblyOccurrence
		{
			Id = AssemblyDocument.NewId(),
			DefinitionId = definition.Id,
			Name = UniqueOccurrenceName( studio.Assembly, BodyName( studio, body ) ),
			Visible = true,
			Transform = new AssemblyTransform(),
		};
		studio.Assembly.Occurrences.Add( occurrence );
		return occurrence;
	}

	/// <summary>Add one occurrence, as one undo entry. Returns null (and records
	/// nothing) when the body is not in this session's studio.</summary>
	public static AssemblyOccurrence? AddOccurrence( DocumentSession session, Body? body )
	{
		ArgumentNullException.ThrowIfNull( session );
		var studio = RequireStudio( session );
		if ( body == null || !studio.Bodies.Any( b => b != null && b.Id == body.Id ) ) return null;
		return ExecuteAssemblyEdit( session, $"Add occurrence '{BodyName( studio, body )}'",
			s => AddOccurrence( s, body ) );
	}

	/// <summary>Duplicate an occurrence. The copy shares the source definition
	/// but gets a new occurrence Id and its own placement: the source transform
	/// shifted +N inches along Effigy +X, where N is how many occurrences of
	/// that definition already exist, so copies never stack invisibly on top
	/// of each other. Moving the copy afterwards never moves the original or
	/// the shared source. Returns the copy, or null when the occurrence is gone.</summary>
	public static AssemblyOccurrence? DuplicateOccurrence( PartStudio? studio, string? occurrenceId )
	{
		var source = FindOccurrence( studio?.Assembly, occurrenceId );
		if ( source == null || studio == null ) return null;
		var siblings = studio.Assembly.Occurrences.Count( o => o != null && o.DefinitionId == source.DefinitionId );
		AssemblyTransform copyTransform;
		if ( source.Transform != null && source.Transform.IsValid( out _ ) )
			copyTransform = AssemblyTransform.FromXform(
				Xform.Translate( new Vec3( siblings, 0, 0 ) ) * source.Transform.ToXform() );
		else
			copyTransform = new AssemblyTransform();
		var copy = new AssemblyOccurrence
		{
			Id = AssemblyDocument.NewId(),
			DefinitionId = source.DefinitionId,
			Name = UniqueOccurrenceName( studio.Assembly, source.Name + " copy" ),
			Visible = source.Visible,
			Transform = copyTransform,
		};
		studio.Assembly.Occurrences.Add( copy );
		return copy;
	}

	/// <summary>Duplicate an occurrence, as one undo entry. Returns null (and
	/// records nothing) when the occurrence is gone.</summary>
	public static AssemblyOccurrence? DuplicateOccurrence( DocumentSession session, string? occurrenceId )
	{
		ArgumentNullException.ThrowIfNull( session );
		var studio = RequireStudio( session );
		if ( FindOccurrence( studio.Assembly, occurrenceId ) == null ) return null;
		return ExecuteAssemblyEdit( session, "Duplicate occurrence",
			s => DuplicateOccurrence( s, occurrenceId ) );
	}

	/// <summary>Rename an occurrence. Empty or blank names are refused and the
	/// old name stands. Renaming one occurrence never renames its siblings or
	/// its definition. One undo entry reverts it (session overload).</summary>
	public static bool RenameOccurrence( PartStudio? studio, string? occurrenceId, string? newName )
	{
		var occurrence = FindOccurrence( studio?.Assembly, occurrenceId );
		if ( occurrence == null ) return false;
		var name = ( newName ?? "" ).Trim();
		if ( name.Length == 0 ) return false;
		if ( name == occurrence.Name ) return true;
		occurrence.Name = name;
		return true;
	}

	/// <summary>Rename an occurrence, as one undo entry. A blank name, an
	/// unknown occurrence, or renaming to the current name records nothing.</summary>
	public static bool RenameOccurrence( DocumentSession session, string? occurrenceId, string? newName )
	{
		ArgumentNullException.ThrowIfNull( session );
		var studio = RequireStudio( session );
		var occurrence = FindOccurrence( studio.Assembly, occurrenceId );
		if ( occurrence == null ) return false;
		var name = ( newName ?? "" ).Trim();
		if ( name.Length == 0 || name == occurrence.Name ) return name == occurrence.Name;
		return ExecuteAssemblyEdit( session, $"Rename occurrence '{occurrence.Name}' to '{name}'",
			s => RenameOccurrence( s, occurrenceId, newName ) );
	}

	/// <summary>Hide or show one occurrence. Hiding keeps the row listed,
	/// never removed from the list.</summary>
	public static bool SetOccurrenceVisible( PartStudio? studio, string? occurrenceId, bool visible )
	{
		var occurrence = FindOccurrence( studio?.Assembly, occurrenceId );
		if ( occurrence == null ) return false;
		if ( occurrence.Visible == visible ) return true;
		occurrence.Visible = visible;
		return true;
	}

	/// <summary>Hide or show one occurrence, as one undo entry. An unknown
	/// occurrence, or a value that already holds, records nothing.</summary>
	public static bool SetOccurrenceVisible( DocumentSession session, string? occurrenceId, bool visible )
	{
		ArgumentNullException.ThrowIfNull( session );
		var studio = RequireStudio( session );
		var occurrence = FindOccurrence( studio.Assembly, occurrenceId );
		if ( occurrence == null ) return false;
		if ( occurrence.Visible == visible ) return true;
		return ExecuteAssemblyEdit( session, visible ? "Show occurrence" : "Hide occurrence",
			s => SetOccurrenceVisible( s, occurrenceId, visible ) );
	}

	/// <summary>Joints that reference an occurrence, in document order.
	/// Empty when nothing references it.</summary>
	public static IReadOnlyList<AssemblyJoint> JointsReferencing( AssemblyDocument? assembly, string? occurrenceId )
	{
		if ( assembly?.Joints == null || string.IsNullOrWhiteSpace( occurrenceId ) )
			return Array.Empty<AssemblyJoint>();
		return assembly.Joints.Where( j => j != null
			&& ( j.OccurrenceA == occurrenceId || j.OccurrenceB == occurrenceId ) ).ToArray();
	}

	/// <summary>Remove an occurrence. Refused — occurrence and joints untouched —
	/// while any joint references it: joint deletion belongs to the joints task,
	/// so this never breaks a joint silently. The source definition and its
	/// other occurrences are never removed with it. One undo entry brings the
	/// occurrence back (session overload).</summary>
	public static (AssemblyOccurrenceRemoveResult Result, IReadOnlyList<AssemblyJoint> Joints)
		RemoveOccurrence( PartStudio? studio, string? occurrenceId )
	{
		var occurrence = FindOccurrence( studio?.Assembly, occurrenceId );
		if ( occurrence == null || studio == null )
			return ( AssemblyOccurrenceRemoveResult.NotFound, Array.Empty<AssemblyJoint>() );
		var joints = JointsReferencing( studio.Assembly, occurrenceId );
		if ( joints.Count > 0 ) return ( AssemblyOccurrenceRemoveResult.HasJoints, joints );
		studio.Assembly.Occurrences.Remove( occurrence );
		return ( AssemblyOccurrenceRemoveResult.Removed, Array.Empty<AssemblyJoint>() );
	}

	/// <summary>Remove an occurrence, as one undo entry. A refused removal
	/// (unknown occurrence, or one joints reference) records nothing.</summary>
	public static (AssemblyOccurrenceRemoveResult Result, IReadOnlyList<AssemblyJoint> Joints)
		RemoveOccurrence( DocumentSession session, string? occurrenceId )
	{
		ArgumentNullException.ThrowIfNull( session );
		var studio = RequireStudio( session );
		var occurrence = FindOccurrence( studio.Assembly, occurrenceId );
		if ( occurrence == null )
			return ( AssemblyOccurrenceRemoveResult.NotFound, Array.Empty<AssemblyJoint>() );
		var joints = JointsReferencing( studio.Assembly, occurrenceId );
		if ( joints.Count > 0 ) return ( AssemblyOccurrenceRemoveResult.HasJoints, joints );
		return ExecuteAssemblyEdit( session, $"Remove occurrence '{occurrence.Name}'",
			s => RemoveOccurrence( s, occurrenceId ) );
	}

	/// <summary>A stored selection (occurrence Id) is still valid only while
	/// that occurrence exists in the document. After undo/redo, save/reopen or
	/// a rebuild, call this before using the Id: it keeps the selection when
	/// the occurrence survived and clears it when it did not, so the panel
	/// never points at a vanished row.</summary>
	public static string ValidSelection( AssemblyDocument? assembly, string? occurrenceId )
	{
		if ( string.IsNullOrWhiteSpace( occurrenceId ) ) return "";
		return FindOccurrence( assembly, occurrenceId ) != null ? occurrenceId : "";
	}

	/// <summary>Row text for a failed reference: the row is kept, never
	/// auto-deleted, and the Validate() code rides along for the tooltip.</summary>
	public static string MissingLabel( string? name, string? code )
	{
		var label = "Missing: " + ( string.IsNullOrWhiteSpace( name ) ? "Unnamed part" : name );
		return string.IsNullOrWhiteSpace( code ) ? label : label + " (" + code + ")";
	}

	static PartStudio RequireStudio( DocumentSession session ) =>
		session.Studio ?? throw new InvalidOperationException( "DocumentSession has no studio document." );

	/// <summary>Run a pure assembly mutation as one history entry. The mutation
	/// runs on the first execute and its resulting assembly JSON is captured;
	/// undo restores the before snapshot and redo restores the after snapshot,
	/// so redo replays the exact same Ids instead of minting new ones.</summary>
	static T ExecuteAssemblyEdit<T>( DocumentSession session, string description, Func<PartStudio, T> mutate )
	{
		var studio = RequireStudio( session );
		var before = studio.Assembly.ToJson();
		string? after = null;
		T result = default!;
		session.History.Execute( new DelegateEditCommand( description,
			execute: () =>
			{
				if ( after is null )
				{
					result = mutate( studio );
					after = studio.Assembly.ToJson();
				}
				else
				{
					RestoreAssembly( studio, after );
				}
			},
			undo: () => RestoreAssembly( studio, before ) ) );
		return result;
	}

	static void RestoreAssembly( PartStudio studio, string json )
	{
		studio.Assembly = AssemblyDocument.FromJson( json );
	}
}
