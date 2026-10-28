using System;
using System.Collections.Generic;
using System.IO;

namespace Marionette.Desktop;

public enum AssetReferenceKind { ProjectRelative, ExternalReadOnly }

/// <summary>Persist the logical ID and reference, never derived GPU indices or an implicit cwd.</summary>
public sealed record AssetReference( string Id, string Path, AssetReferenceKind Kind = AssetReferenceKind.ProjectRelative );

public sealed record ResolvedAsset( string Id, string CanonicalPath, string ReferencePath,
	AssetReferenceKind Kind, bool Exists );

/// <summary>
/// CPU-only project-root path policy. No files/directories are written while resolving.
/// External source reads require an explicitly typed reference; external writes are refused.
/// This validates current filesystem state, not a race-proof sandbox: save/open services must
/// revalidate immediately before IO and use safe atomic-file operations.
/// </summary>
public sealed class AssetPathResolver
{
	readonly StringComparison _comparison = OperatingSystem.IsWindows()
		? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
	public string ProjectRoot { get; }
	public string CanonicalRoot { get; }

	public AssetPathResolver( string projectRoot )
	{
		if ( string.IsNullOrWhiteSpace( projectRoot ) || !System.IO.Path.IsPathFullyQualified( projectRoot ) )
			throw new ArgumentException( "Project root must be an explicit absolute directory.", nameof( projectRoot ) );
		ProjectRoot = System.IO.Path.TrimEndingDirectorySeparator( System.IO.Path.GetFullPath( projectRoot ) );
		CanonicalRoot = PhysicalPath( ProjectRoot );
		if ( !Directory.Exists( CanonicalRoot ) ) throw new DirectoryNotFoundException( $"Project root not found: {projectRoot}" );
	}

	/// <summary>Resolve a project-relative or explicitly external source. Missing files fail by default.</summary>
	public ResolvedAsset ResolveRead( AssetReference reference, bool requireExists = true ) =>
		Resolve( reference, write: false, requireExists );

	/// <summary>Validate an output path without creating it. External source references cannot be outputs.</summary>
	public ResolvedAsset ResolveWrite( AssetReference reference ) => Resolve( reference, write: true, requireExists: false );

	ResolvedAsset Resolve( AssetReference reference, bool write, bool requireExists )
	{
		ArgumentNullException.ThrowIfNull( reference );
		if ( string.IsNullOrWhiteSpace( reference.Id ) || string.IsNullOrWhiteSpace( reference.Path ) || reference.Path.IndexOf( '\0' ) >= 0 )
			throw new ArgumentException( "An asset requires a nonempty logical ID and a valid path." );
		if ( reference.Kind is not (AssetReferenceKind.ProjectRelative or AssetReferenceKind.ExternalReadOnly) )
			throw new ArgumentException( "Unknown asset reference kind." );
		// References use '/' as the portable separator; accept '\\' in project-relative input.
		string path = reference.Kind == AssetReferenceKind.ProjectRelative ? reference.Path.Replace( '\\', '/' ) : reference.Path;
		string absolute;
		if ( reference.Kind == AssetReferenceKind.ExternalReadOnly )
		{
			if ( write ) throw new UnauthorizedAccessException( $"External asset '{reference.Id}' is read-only." );
			if ( !System.IO.Path.IsPathFullyQualified( path ) ) throw new ArgumentException( "External assets require an explicit absolute path." );
			absolute = path;
		}
		else
		{
			if ( System.IO.Path.IsPathRooted( path ) || (path.Length > 1 && path[1] == ':') )
				throw new ArgumentException( "Project assets must use relative paths. Declare external sources explicitly." );
			absolute = System.IO.Path.Combine( ProjectRoot, path );
			if ( !Inside( System.IO.Path.GetFullPath( absolute ), ProjectRoot ) )
				throw new UnauthorizedAccessException( $"Asset '{reference.Id}' escapes the project root." );
		}

		// Walk symlinks before collapsing '..': link/../file must follow filesystem semantics,
		// not string-only normalization. Also handles existing ancestors of a new output path.
		string canonical = PhysicalPath( absolute );
		if ( reference.Kind == AssetReferenceKind.ProjectRelative && !Inside( canonical, CanonicalRoot ) )
			throw new UnauthorizedAccessException( $"Asset '{reference.Id}' resolves outside the project (possibly through a symlink)." );
		if ( Directory.Exists( canonical ) ) throw new IOException( $"Asset '{reference.Id}' names a directory, not a file: {canonical}" );
		bool exists = File.Exists( canonical );
		if ( requireExists && !exists )
			throw new FileNotFoundException( $"Asset '{reference.Id}' not found (check path spelling and case): {canonical}", canonical );
		string display = reference.Kind == AssetReferenceKind.ProjectRelative
			? System.IO.Path.GetRelativePath( CanonicalRoot, canonical ).Replace( '\\', '/' ) : canonical;
		return new ResolvedAsset( reference.Id, canonical, display, reference.Kind, exists );
	}

	/// <summary>
	/// Keep the same source when a manifest is saved under a different root. Assets outside
	/// the new root become explicit read-only externals only with opt-in; no copying is implied.
	/// Moving an entire project tree instead keeps the original relative references unchanged.
	/// </summary>
	public AssetReference RelocateReference( AssetReference reference, AssetPathResolver destination, bool allowExternal = false )
	{
		ArgumentNullException.ThrowIfNull( destination );
		var source = ResolveRead( reference );
		if ( source.Kind == AssetReferenceKind.ExternalReadOnly )
		{
			if ( !allowExternal ) throw new UnauthorizedAccessException( $"Relocating external asset '{reference.Id}' requires explicit external opt-in." );
			return new AssetReference( reference.Id, source.CanonicalPath, AssetReferenceKind.ExternalReadOnly );
		}
		if ( destination.Inside( source.CanonicalPath, destination.CanonicalRoot ) )
			return new AssetReference( reference.Id,
				System.IO.Path.GetRelativePath( destination.CanonicalRoot, source.CanonicalPath ).Replace( '\\', '/' ) );
		if ( !allowExternal ) throw new UnauthorizedAccessException( $"Save As would reference '{reference.Id}' outside the destination project; explicit external opt-in or a separately authorized copy is required." );
		return new AssetReference( reference.Id, source.CanonicalPath, AssetReferenceKind.ExternalReadOnly );
	}

	bool Inside( string path, string root ) => string.Equals( path, root, _comparison ) ||
		path.StartsWith( System.IO.Path.EndsInDirectorySeparator( root ) ? root : root + System.IO.Path.DirectorySeparatorChar, _comparison );

	static string PhysicalPath( string absolute )
	{
		string root = System.IO.Path.GetPathRoot( absolute ) ?? throw new ArgumentException( "Absolute path required." );
		var pending = new LinkedList<string>( Segments( absolute[root.Length..] ) );
		string current = root;
		int links = 0;
		while ( pending.First is { } first )
		{
			string segment = first.Value; pending.RemoveFirst();
			if ( segment == "." ) continue;
			if ( segment == ".." ) { current = System.IO.Path.GetDirectoryName( System.IO.Path.TrimEndingDirectorySeparator( current ) ) ?? root; continue; }
			if ( File.Exists( current ) ) throw new IOException( $"Path traverses a file instead of a directory: {current}" );
			string candidate = System.IO.Path.Combine( current, segment );
			string target = new FileInfo( candidate ).LinkTarget;
			if ( target is null ) { current = candidate; continue; }
			if ( ++links > 40 ) throw new IOException( "Too many symlinks (or a symlink cycle) in asset path." );
			if ( System.IO.Path.IsPathRooted( target ) )
			{
				root = System.IO.Path.GetPathRoot( target ); current = root;
				target = target[root.Length..];
			}
			var parts = Segments( target );
			for ( int i = parts.Length - 1; i >= 0; i-- ) pending.AddFirst( parts[i] );
		}
		return System.IO.Path.TrimEndingDirectorySeparator( current );
	}

	static string[] Segments( string value ) => value.Split(
		new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries );
}
