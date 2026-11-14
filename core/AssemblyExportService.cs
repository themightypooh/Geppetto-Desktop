using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy;
using Effigy.Assembly;

namespace Marionette.Desktop;

// AS12: assembled + per-part export with materials.
//
// Ports Mobile app/AssemblyExport.cs (Assembly 16) to Desktop. The export
// never edits the document: it reads the stored solved occurrence transforms
// from the document exactly once each, through the AS04 snapshot (which
// clones the source mesh and applies the transform once, reversing face
// winding for a mirrored occurrence), and writes files. The shared source
// mesh is never mutated and nothing is baked into the document.
//
// What resolves, and what does not:
//   studio-body : SourceReference is a body id in this document's studio.
//                 The only source kind this export can turn into geometry.
//   anything else (effigy-file, ...) : an external reference. There is no
//                 loader for it here, so the occurrence stays out with a
//                 warning, never a silent hole.
// Hidden occurrences stay out, the same way a whole-scene export leaves
// hidden bodies out. Joints never travel: every format below carries the
// stored solved pose, static, with no joint or animation metadata.
//
// Pose choice (explicit, per the ticket): Current exports the document
// exactly as stored. Rest exports the assembly's rest pose, built on a
// throwaway deep copy: every movable joint is driven to its zero value
// (hinge 0 = frames coincident, slider 0 = origins coincident) through
// the AS10 motion session, which carries the chain walk, grounding and
// limit rules. A joint that cannot move (grounded B, locked) keeps its
// stored value — its rest is where it is. A joint the session refuses for
// any other reason fails the rest build with the session's reason; the
// live document is never touched by a rest export.
//
// Units: OBJ files are written in document units (inches, Z-up) — Desktop
// has no millimetre OBJ preset; Mobile's mm preset is a UI option, not
// part of the assembly export contract. GLB files are metres Y-up, via
// GlbExport (G(v) = (v.x, v.z, -v.y) x 0.0254), one node + one mesh per
// part, no animation, no skinning.
//
// Materials: each part carries its material NAME (the snapshot's material
// name) plus the provisional palette factors for that name. Paint textures
// are NOT embedded: per-part paint embedding belongs to ExportService's
// full-document path and would duplicate its image pipeline here. The
// assembled OBJ gets a companion .mtl with the palette colours of the
// materials it uses.
//
// GLB status: the GLB path is written, but it is NOT marked green — the
// independent re-import check (tests/AssemblyExportTests.cs, GlbInfo)
// is written and must pass in the final integrator's run before anyone
// claims GLB. STL / STEP have no Desktop writers and are out of scope.
//
// A dangling joint (a joint naming an occurrence that does not exist)
// blocks every file export: Analyze reports it as an error whose message
// contains "does not exist", and the Export* entry points write nothing
// while any error is present.
//
// Not runtime verified: the final integrator runs tests/Desktop.Tests.csproj.
public enum AssemblyExportPose
{
	/// <summary>Export the document exactly as stored (the live solved pose).</summary>
	Current,
	/// <summary>Export the rest pose: movable joints driven to zero on a
	/// throwaway copy (see the file header).</summary>
	Rest
}

public sealed record AssemblyExportItem(
	AssemblyOccurrence Occurrence,
	AssemblyPartDefinition Definition,
	string SourceBodyId,
	bool Mirrored,
	string FileStem);

public sealed class AssemblyAnalysis
{
	public bool HasOccurrences;
	public List<AssemblyExportItem> Items { get; } = new List<AssemblyExportItem>();
	public List<string> Errors { get; } = new List<string>();
	public List<string> Warnings { get; } = new List<string>();
	public List<string> Notes { get; } = new List<string>();
	public int HiddenSkipped;
	public int UnresolvedSkipped;
}

public sealed class AssemblyExportResult
{
	public bool Success { get; set; }
	public IReadOnlyList<string> Files { get; set; } = Array.Empty<string>();
	public AssemblyAnalysis Analysis { get; set; }
	public string ErrorMessage { get; set; }
}

public static class AssemblyExportService
{
	/// <summary>Which occurrences can be exported, and everything the user
	/// needs to know before the files are written. No mesh is cloned here
	/// beyond what the snapshot already holds, so calling this from
	/// preflight is cheap. The snapshot must have been built from the same
	/// document (see <see cref="StudioResolver"/>); an occurrence the
	/// snapshot does not know is reported as an error, never silently
	/// dropped.</summary>
	public static AssemblyAnalysis Analyze( AssemblyDocument doc, PartStudio studio, AssemblySnapshot snapshot )
	{
		if ( studio == null ) throw new ArgumentNullException( nameof( studio ) );
		if ( snapshot == null ) throw new ArgumentNullException( nameof( snapshot ) );
		var analysis = new AssemblyAnalysis();
		if ( doc == null ) return analysis;
		analysis.HasOccurrences = doc.Occurrences is { Count: > 0 };
		// Deliberate deviation from Mobile: Mobile returns an empty
		// analysis for a document with no occurrences. Desktop adds the
		// "no part can be exported" error here too, so the file exporters
		// — which refuse while any error is present — never write an empty
		// file for an empty document.
		if ( !analysis.HasOccurrences )
		{
			analysis.Errors.Add( "No assembly part can be exported." );
			return analysis;
		}

		foreach ( var issue in doc.Validate() )
			( issue.Warning ? analysis.Warnings : analysis.Errors ).Add( issue.Message );

		// A dangling joint blocks the export instead of shipping a broken
		// assembly. Validate already reports it as missing-occurrence
		// ("... does not exist"); add the joint-naming form only when it
		// is not already reported, so the error is never duplicated.
		var occurrenceIds = new HashSet<string>( StringComparer.Ordinal );
		if ( doc.Occurrences != null )
			foreach ( var o in doc.Occurrences )
				if ( o != null && !string.IsNullOrEmpty( o.Id ) )
					occurrenceIds.Add( o.Id );
		if ( doc.Joints != null )
			foreach ( var joint in doc.Joints )
			{
				if ( joint == null ) continue;
				foreach ( var end in new[] { joint.OccurrenceA, joint.OccurrenceB } )
				{
					if ( string.IsNullOrWhiteSpace( end ) || occurrenceIds.Contains( end ) ) continue;
					if ( analysis.Errors.Any( e => e.Contains( end ) && e.Contains( "does not exist" ) ) ) continue;
					analysis.Errors.Add( $"Joint '{joint.Id}' references occurrence '{end}' which does not exist." );
				}
			}

		var definitions = new Dictionary<string, AssemblyPartDefinition>( StringComparer.Ordinal );
		if ( doc.Definitions != null )
			foreach ( var d in doc.Definitions )
				if ( d != null && !string.IsNullOrWhiteSpace( d.Id ) && !definitions.ContainsKey( d.Id ) )
					definitions[d.Id] = d;

		var bodies = new Dictionary<string, Body>( StringComparer.Ordinal );
		foreach ( var b in studio.Bodies )
			if ( b != null && !string.IsNullOrWhiteSpace( b.Id ) && !bodies.ContainsKey( b.Id ) )
				bodies[b.Id] = b;

		var snapsById = new Dictionary<string, AssemblyOccurrenceSnapshot>( StringComparer.Ordinal );
		if ( snapshot.Occurrences != null )
			foreach ( var s in snapshot.Occurrences )
				if ( s != null && !string.IsNullOrEmpty( s.OccurrenceId ) && !snapsById.ContainsKey( s.OccurrenceId ) )
					snapsById[s.OccurrenceId] = s;

		var externalWarned = new HashSet<string>( StringComparer.Ordinal );

		foreach ( var occurrence in doc.Occurrences ?? new List<AssemblyOccurrence>() )
		{
			if ( occurrence == null ) continue;
			if ( !occurrence.Visible ) { analysis.HiddenSkipped++; continue; }
			if ( string.IsNullOrWhiteSpace( occurrence.Name ) ) continue;   // Validate already errors: a part needs a name
			if ( !definitions.TryGetValue( occurrence.DefinitionId ?? "", out var definition ) ) continue;   // Validate already errors
			if ( string.IsNullOrWhiteSpace( definition.SourceKind ) || string.IsNullOrWhiteSpace( definition.SourceReference ) ) continue;   // Validate already errors: missing-source
			if ( definition.SourceKind != AssemblyPartService.StudioBodyKind )
			{
				if ( externalWarned.Add( definition.Id ) )
					analysis.Warnings.Add( $"Part '{definition.Name}' comes from '{definition.SourceKind}' ('{definition.SourceReference}'), which isn't loaded yet. It stays out of this export." );
				analysis.UnresolvedSkipped++;
				continue;
			}
			if ( !bodies.TryGetValue( definition.SourceReference, out var body ) )
			{
				analysis.Errors.Add( $"Part '{definition.Name}' points at body '{definition.SourceReference}' which is not in this document. Remove the part or restore the body." );
				continue;
			}
			if ( body.IsReference )
			{
				analysis.Errors.Add( $"Part '{definition.Name}' is a reference body, which has no shape to export." );
				continue;
			}
			if ( body.IsGarment )
			{
				analysis.Errors.Add( $"Part '{definition.Name}' is a garment, which the export leaves out." );
				continue;
			}
			if ( body.Mesh is not { FaceCount: > 0 } )
			{
				analysis.Errors.Add( $"Part '{definition.Name}' has no mesh yet. Rebuild the document and try again." );
				continue;
			}
			// Geometry comes from the snapshot, which already applied the
			// occurrence transform exactly once. A studio body the snapshot
			// cannot see is a stale-snapshot / wrong-resolver bug, reported
			// as an error rather than papered over with a second transform.
			if ( !snapsById.TryGetValue( occurrence.Id ?? "", out var snap ) || snap.MissingSource || snap.Mesh == null )
			{
				analysis.Errors.Add( $"Part '{occurrence.Name}' has no geometry in the snapshot. Rebuild the snapshot from the same document and try again." );
				continue;
			}
			if ( occurrence.Transform == null || !occurrence.Transform.IsValid( out _ ) ) continue;   // Validate already errors: invalid-transform
			bool mirrored = occurrence.Transform.ToXform().FlipsWinding;
			analysis.Items.Add( new AssemblyExportItem( occurrence, definition, body.Id, mirrored, FileStem: null ) );
		}

		// Collision-free file stems, in document order: the first keeps its
		// name, later ones get _2, _3, the same suffix the paint export uses
		// for duplicate body names.
		var usedStems = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		bool renamed = false;
		for ( int i = 0; i < analysis.Items.Count; i++ )
		{
			var item = analysis.Items[i];
			var stem = SanitizeFileStem( item.Occurrence.Name );
			var unique = stem;
			for ( int n = 2; !usedStems.Add( unique ); n++ ) unique = $"{stem}_{n}";
			if ( unique != stem ) renamed = true;
			analysis.Items[i] = item with { FileStem = unique };
		}
		if ( renamed )
			analysis.Notes.Add( "Two parts share a name, so the files get _2, _3 suffixes: " +
				string.Join( ", ", analysis.Items.Select( item => $"'{item.Occurrence.Name}' as {item.FileStem}" ) ) + "." );
		if ( analysis.HiddenSkipped > 0 )
			analysis.Notes.Add( $"{analysis.HiddenSkipped} hidden part{( analysis.HiddenSkipped == 1 ? "" : "s" )} stay{( analysis.HiddenSkipped == 1 ? "s" : "" )} out of the export." );
		var mirroredNames = analysis.Items.Where( item => item.Mirrored ).Select( item => item.Occurrence.Name ).ToList();
		if ( mirroredNames.Count > 0 )
			analysis.Notes.Add( $"Mirrored part{( mirroredNames.Count == 1 ? "" : "s" )}: {string.Join( ", ", mirroredNames )}. Faces are wound the right way out on export." );
		var painted = analysis.Items.Count( item =>
			bodies.TryGetValue( item.SourceBodyId, out var b ) && b.Mesh?.Paint != null );
		if ( painted > 1 )
			analysis.Notes.Add( "Paint maps in the assembled file come from the first part only. Export parts individually for per-part paint." );

		var joints = doc.Joints ?? new List<AssemblyJoint>();
		if ( joints.Count > 0 )
		{
			analysis.Notes.Add( "Joints don't travel with the export: this is the stored solved pose." );
			var joined = new HashSet<string>( StringComparer.Ordinal );
			foreach ( var joint in joints )
			{
				if ( !string.IsNullOrWhiteSpace( joint.OccurrenceA ) ) joined.Add( joint.OccurrenceA );
				if ( !string.IsNullOrWhiteSpace( joint.OccurrenceB ) ) joined.Add( joint.OccurrenceB );
			}
			int unjoined = analysis.Items.Count( item => !joined.Contains( item.Occurrence.Id ) );
			if ( unjoined > 0 )
				analysis.Notes.Add( $"{unjoined} part{( unjoined == 1 ? "" : "s" )} aren't joined to anything: they export exactly where they're placed." );
		}

		if ( analysis.Items.Count == 0 && analysis.Errors.Count == 0 )
			analysis.Errors.Add( "No assembly part can be exported." );
		return analysis;
	}

	/// <summary>The assembled arrangement: one merged mesh in document units
	/// (inches, Z up) with one range per occurrence, in document order, so
	/// GLB / OBJ keep one node / object per part. Each snapshot mesh is
	/// already transformed exactly once; merging only appends. Ranges carry
	/// the occurrence names; the writers de-duplicate repeats with " 2",
	/// the same convention as GLB node names. The _2 form is the file stem
	/// (FileStem) for individual part files, not the name inside an
	/// assembled file.</summary>
	public static (PolyMesh Mesh, List<BodyRange> Ranges) BuildAssembled(
		AssemblySnapshot snapshot, AssemblyAnalysis analysis, float scale = 1f )
	{
		if ( snapshot == null ) throw new ArgumentNullException( nameof( snapshot ) );
		if ( analysis == null ) throw new ArgumentNullException( nameof( analysis ) );
		var merged = new PolyMesh();
		var ranges = new List<BodyRange>( analysis.Items.Count );
		foreach ( var item in analysis.Items )
		{
			var mesh = BuildPartMesh( snapshot, item, scale );
			var start = merged.VertexCount;
			MeshTransform.Append( merged, mesh );
			ranges.Add( new BodyRange( item.Occurrence.Id, item.Occurrence.Name, start, merged.VertexCount - start, mesh.IsRigged ) );
		}
		return (merged, ranges);
	}

	/// <summary>One occurrence on its own, in document units: the snapshot's
	/// transformed clone, cloned again so the caller can scale or edit it
	/// without touching the snapshot, and scaled. Never the live document
	/// mesh, never the snapshot's own mesh.</summary>
	public static PolyMesh BuildPartMesh( AssemblySnapshot snapshot, AssemblyExportItem item, float scale = 1f )
	{
		if ( snapshot == null ) throw new ArgumentNullException( nameof( snapshot ) );
		if ( item == null ) throw new ArgumentNullException( nameof( item ) );
		var snap = snapshot.Occurrences?.FirstOrDefault( s => s != null && s.OccurrenceId == item.Occurrence.Id );
		if ( snap?.Mesh == null )
			throw new InvalidOperationException( $"Source body '{item.SourceBodyId}' for part '{item.Occurrence.Name}' is gone." );
		var mesh = snap.Mesh.Clone();
		if ( scale != 1f )
			for ( int i = 0; i < mesh.Positions.Count; i++ ) mesh.Positions[i] = mesh.Positions[i] * scale;
		return mesh;
	}

	/// <summary>The paint canvas for an occurrence's source body, so colour
	/// lookups by occurrence id find the paint that belongs to that part.</summary>
	public static PaintCanvas PaintForOccurrence( PartStudio studio, AssemblyAnalysis analysis, string occurrenceId )
	{
		var item = analysis?.Items.FirstOrDefault( i => i.Occurrence.Id == occurrenceId );
		if ( item == null ) return null;
		return studio?.Bodies.FirstOrDefault( b => b.Id == item.SourceBodyId )?.Mesh?.Paint;
	}

	/// <summary>A file stem for a part name. Same rules as the paint
	/// export's FileSafe: letters, digits, '-' and '_' survive, everything
	/// else becomes '_', at most 40 characters.</summary>
	public static string SanitizeFileStem( string name )
	{
		var stem = new string( ( name ?? "" ).Trim().Select( c => char.IsLetterOrDigit( c ) || c is '-' or '_' ? c : '_' ).ToArray() ).Trim( '_' );
		if ( stem.Length == 0 ) return "body";
		return stem.Length > 40 ? stem.Substring( 0, 40 ) : stem;
	}

	/// <summary>Display names for the parts inside one assembled file
	/// (OBJ <c>o</c> objects, GLB node/mesh names): the first keeps its
	/// name, later repeats gain " 2", " 3", the same convention GlbExport
	/// uses for duplicate clip names. Blank names become "part".</summary>
	public static IReadOnlyList<string> DedupeDisplayNames( IEnumerable<string> names )
	{
		var used = new HashSet<string>( StringComparer.Ordinal );
		var result = new List<string>();
		foreach ( var raw in names ?? Enumerable.Empty<string>() )
		{
			var name = string.IsNullOrWhiteSpace( raw ) ? "part" : raw;
			var unique = name;
			for ( int n = 2; !used.Add( unique ); n++ ) unique = $"{name} {n}";
			result.Add( unique );
		}
		return result;
	}

	/// <summary>A studio-backed resolver for the AS04 snapshot: studio-body
	/// definitions resolve to their body mesh; anything else (external
	/// kinds, missing bodies) resolves to null, which the snapshot records
	/// as missing. Revision is 0: the export builds a fresh snapshot per
	/// call, so no cross-call cache staleness can arise. The material name
	/// is the body's first used slot name, or material_0.</summary>
	public static AssemblySourceResolver StudioResolver( PartStudio studio )
	{
		if ( studio == null ) throw new ArgumentNullException( nameof( studio ) );
		return definition =>
		{
			if ( definition == null || definition.SourceKind != AssemblyPartService.StudioBodyKind )
				return null;
			var body = studio.Bodies.FirstOrDefault( b => b != null && b.Id == definition.SourceReference );
			if ( body?.Mesh == null ) return null;
			var slot = body.Mesh.Faces.Count > 0 ? body.Mesh.Faces[0].Material : 0;
			return (body.Mesh, 0, studio.NameForSlot( slot ));
		};
	}

	/// <summary>Deep copy of a document through its canonical JSON: the
	/// caller can snapshot any pose (including a motion session's rest
	/// pose) and export the copy while the live document keeps moving.</summary>
	public static AssemblyDocument CapturePose( AssemblyDocument doc )
	{
		if ( doc == null ) throw new ArgumentNullException( nameof( doc ) );
		return AssemblyDocument.FromJson( doc.ToJson() );
	}

	/// <summary>The rest pose of a document (see the file header): a deep
	/// copy with every movable joint driven to zero through the AS10 motion
	/// session. Joints that cannot move (grounded B, locked) keep their
	/// stored value. Returns null with <paramref name="error"/> set when a
	/// movable joint refuses to move for any other reason. The input
	/// document is never touched.</summary>
	public static AssemblyDocument BuildRestDocument( AssemblyDocument doc, out string error )
	{
		error = null;
		if ( doc == null ) { error = "Assembly document is missing."; return null; }
		var rest = CapturePose( doc );
		foreach ( var joint in AssemblyMotionSession.MovableJoints( rest ) )
		{
			if ( joint == null ) continue;
			if ( AssemblyGrounding.IsGrounded( rest, joint.OccurrenceB ) ) continue;
			var live = rest.Joints.FirstOrDefault( j => j != null && j.Id == joint.Id );
			if ( live == null ) continue;
			var session = new AssemblyMotionSession( rest );
			if ( !session.Begin( live ) )
			{
				error = $"Cannot move joint '{live.Id}' to its rest pose: {session.Problem}";
				return null;
			}
			session.SetValue( 0f );
			if ( !session.ApplyPreview() )
			{
				error = $"Cannot move joint '{live.Id}' to its rest pose: {session.Problem}";
				return null;
			}
		}
		return rest;
	}

	/// <summary>Assembled OBJ: one file, one <c>o</c> object per part in
	/// document order, in inches, Z-up, plus a companion .mtl with the
	/// palette colours of the materials it uses. Writes nothing while the
	/// analysis carries any error (dangling joint, missing source, ...).
	/// Derived output: an existing file of the same name is overwritten.</summary>
	public static AssemblyExportResult ExportAssembledObj(
		AssemblyDocument doc, PartStudio studio, AssemblySourceResolver resolver,
		AssemblyExportPose pose, string directory, string baseName, float scale = 1f )
	{
		if ( !Prepare( doc, studio, resolver, pose, out var poseDoc, out var snapshot, out var analysis, out var error ) )
			return new AssemblyExportResult { Success = false, Analysis = analysis, ErrorMessage = error };
		if ( analysis.Errors.Count > 0 )
			return new AssemblyExportResult { Success = false, Analysis = analysis, ErrorMessage = analysis.Errors[0] };
		if ( string.IsNullOrWhiteSpace( directory ) ) throw new ArgumentException( "An output directory is required.", nameof( directory ) );
		Directory.CreateDirectory( directory );

		var stem = SanitizeFileStem( baseName );
		var objName = $"{stem}_assembly.obj";
		var mtlName = $"{stem}_assembly.mtl";
		var names = DedupeDisplayNames( analysis.Items.Select( i => i.Occurrence.Name ) );
		var pieces = new List<ObjReader.ObjPiece>( analysis.Items.Count );
		for ( int i = 0; i < analysis.Items.Count; i++ )
			pieces.Add( new ObjReader.ObjPiece( names[i], BuildPartMesh( snapshot, analysis.Items[i], scale ) ) );

		var materialResolver = ObjMaterialResolver( studio );
		var objPath = Path.Combine( directory, objName );
		File.WriteAllText( objPath, ObjWriter.Write( pieces, ObjWriter.DefaultSmoothingAngleDegrees, materialResolver, mtlName ) );
		var mtlPath = Path.Combine( directory, mtlName );
		ObjWriter.WriteMtl( mtlPath, MtlEntries( studio, pieces ) );
		return new AssemblyExportResult
		{
			Success = true,
			Files = new[] { objPath, mtlPath },
			Analysis = analysis,
		};
	}

	/// <summary>Per-occurrence OBJs: one file per part in a
	/// "<c>&lt;base&gt;_parts</c>" folder, named by file stem (duplicates
	/// get _2, _3), each with its own .mtl, in inches, Z-up. Hidden and
	/// external parts get no file. Writes nothing while the analysis
	/// carries any error. Derived output: existing files of the same name
	/// are overwritten.</summary>
	public static AssemblyExportResult ExportPartObjs(
		AssemblyDocument doc, PartStudio studio, AssemblySourceResolver resolver,
		AssemblyExportPose pose, string directory, string baseName, float scale = 1f )
	{
		if ( !Prepare( doc, studio, resolver, pose, out var poseDoc, out var snapshot, out var analysis, out var error ) )
			return new AssemblyExportResult { Success = false, Analysis = analysis, ErrorMessage = error };
		if ( analysis.Errors.Count > 0 )
			return new AssemblyExportResult { Success = false, Analysis = analysis, ErrorMessage = analysis.Errors[0] };
		if ( string.IsNullOrWhiteSpace( directory ) ) throw new ArgumentException( "An output directory is required.", nameof( directory ) );

		var stem = SanitizeFileStem( baseName );
		var partsDir = Path.Combine( directory, $"{stem}_parts" );
		Directory.CreateDirectory( partsDir );
		var materialResolver = ObjMaterialResolver( studio );
		var files = new List<string>();
		foreach ( var item in analysis.Items )
		{
			var mesh = BuildPartMesh( snapshot, item, scale );
			var piece = new ObjReader.ObjPiece( item.Occurrence.Name, mesh );
			var objName = $"{item.FileStem}.obj";
			var mtlName = $"{item.FileStem}.mtl";
			File.WriteAllText( Path.Combine( partsDir, objName ),
				ObjWriter.Write( new[] { piece }, ObjWriter.DefaultSmoothingAngleDegrees, materialResolver, mtlName ) );
			var mtlPath = Path.Combine( partsDir, mtlName );
			ObjWriter.WriteMtl( mtlPath, MtlEntries( studio, new[] { piece } ) );
			files.Add( Path.Combine( partsDir, objName ) );
			files.Add( mtlPath );
		}
		return new AssemblyExportResult { Success = true, Files = files, Analysis = analysis };
	}

	/// <summary>Assembled GLB: one node + one mesh per part in document
	/// order, positions in metres Y-up (GlbExport), no animation, no
	/// skinning, palette materials per part. NOT marked green: the
	/// independent re-import check (AssemblyExportTests GlbInfo) must pass
	/// in the final integrator's run before anyone claims GLB. Writes
	/// nothing while the analysis carries any error. Derived output: an
	/// existing file of the same name is overwritten.</summary>
	public static AssemblyExportResult ExportAssembledGlb(
		AssemblyDocument doc, PartStudio studio, AssemblySourceResolver resolver,
		AssemblyExportPose pose, string path, float scale = 1f )
	{
		if ( !Prepare( doc, studio, resolver, pose, out var poseDoc, out var snapshot, out var analysis, out var error ) )
			return new AssemblyExportResult { Success = false, Analysis = analysis, ErrorMessage = error };
		if ( analysis.Errors.Count > 0 )
			return new AssemblyExportResult { Success = false, Analysis = analysis, ErrorMessage = analysis.Errors[0] };
		if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "An output path is required.", nameof( path ) );

		var names = DedupeDisplayNames( analysis.Items.Select( i => i.Occurrence.Name ) );
		var snapsById = new Dictionary<string, AssemblyOccurrenceSnapshot>( StringComparer.Ordinal );
		foreach ( var s in snapshot.Occurrences )
			if ( s != null && !string.IsNullOrEmpty( s.OccurrenceId ) && !snapsById.ContainsKey( s.OccurrenceId ) )
				snapsById[s.OccurrenceId] = s;
		var parts = new List<GlbPart>( analysis.Items.Count );
		for ( int i = 0; i < analysis.Items.Count; i++ )
		{
			var item = analysis.Items[i];
			var mesh = BuildPartMesh( snapshot, item, scale );
			var materialName = snapsById.TryGetValue( item.Occurrence.Id, out var snap ) && !string.IsNullOrWhiteSpace( snap.MaterialName )
				? snap.MaterialName
				: ObjWriter.DefaultMaterialName( 0 );
			var entry = MaterialPalette.ForName( materialName );
			parts.Add( new GlbPart( names[i], mesh,
				new GlbMaterial( materialName, new Vec4( entry.R, entry.G, entry.B, 1f ), entry.Metallic, entry.Roughness, Vec3.Zero, null, null, null, null ) ) );
		}
		if ( parts.Count == 0 )
			return new AssemblyExportResult { Success = false, Analysis = analysis, ErrorMessage = "No assembly part can be exported." };
		var dir = Path.GetDirectoryName( Path.GetFullPath( path ) );
		if ( !string.IsNullOrEmpty( dir ) ) Directory.CreateDirectory( dir );
		File.WriteAllBytes( path, GlbExport.Write( parts ) );
		return new AssemblyExportResult { Success = true, Files = new[] { path }, Analysis = analysis };
	}

	static bool Prepare( AssemblyDocument doc, PartStudio studio, AssemblySourceResolver resolver, AssemblyExportPose pose,
		out AssemblyDocument poseDoc, out AssemblySnapshot snapshot, out AssemblyAnalysis analysis, out string error )
	{
		poseDoc = null; snapshot = null; analysis = new AssemblyAnalysis(); error = null;
		if ( doc == null ) { error = "Assembly document is missing."; analysis.Errors.Add( error ); return false; }
		if ( studio == null ) throw new ArgumentNullException( nameof( studio ) );
		if ( resolver == null ) throw new ArgumentNullException( nameof( resolver ) );
		poseDoc = pose == AssemblyExportPose.Rest ? BuildRestDocument( doc, out error ) : doc;
		if ( poseDoc == null )
		{
			analysis.Errors.Add( error ?? "Could not build the rest pose." );
			return false;
		}
		snapshot = new AssemblySnapshotService().Build( poseDoc, resolver );
		analysis = Analyze( poseDoc, studio, snapshot );
		return true;
	}

	/// <summary>OBJ material names come from the studio's slot names
	/// (falling back to material_N), made OBJ-safe: a slot name is a file
	/// path or vmat reference as often as a bare word, and a space or slash
	/// in a <c>usemtl</c> name would break the file. The same sanitizer
	/// feeds the .mtl entries, so the names always agree.</summary>
	static Func<int, string> ObjMaterialResolver( PartStudio studio ) => slot =>
	{
		var raw = studio.NameForSlot( slot );
		if ( string.IsNullOrWhiteSpace( raw ) ) return ObjWriter.DefaultMaterialName( slot );
		var clean = new string( raw.Trim().Select( c => char.IsLetterOrDigit( c ) || c is '-' or '_' or '.' ? c : '_' ).ToArray() ).Trim( '_' );
		return clean.Length == 0 ? ObjWriter.DefaultMaterialName( slot ) : clean;
	};

	static IReadOnlyList<(string Name, Vec3 Colour)> MtlEntries( PartStudio studio, IReadOnlyList<ObjReader.ObjPiece> pieces )
	{
		var resolver = ObjMaterialResolver( studio );
		var seen = new HashSet<string>( StringComparer.Ordinal );
		var entries = new List<(string, Vec3)>();
		foreach ( var piece in pieces )
		{
			if ( piece.Mesh == null ) continue;
			foreach ( var face in piece.Mesh.Faces )
			{
				var name = resolver( face.Material );
				if ( !seen.Add( name ) ) continue;
				var entry = MaterialPalette.ForName( name );
				entries.Add( (name, new Vec3( entry.R, entry.G, entry.B )) );
			}
		}
		return entries;
	}
}
