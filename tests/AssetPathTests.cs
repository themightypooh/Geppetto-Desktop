using Marionette.Desktop;

internal static class AssetPathTests
{
    public static void Register( TestRegistry suite, TestSettings settings )
    {
        (string Root, string Outside, AssetPathResolver Resolver) Fixture()
        {
            string root = Path.Combine( settings.WorkDirectory, "asset-paths", Guid.NewGuid().ToString( "N" ), "project" );
            string outside = Path.Combine( Path.GetDirectoryName( root )!, "outside" );
            Directory.CreateDirectory( root ); Directory.CreateDirectory( outside );
            Directory.CreateDirectory( Path.Combine( root, "textures" ) );
            File.WriteAllText( Path.Combine( root, "textures", "skin.png" ), "source sentinel" );
            File.WriteAllText( Path.Combine( outside, "skin.png" ), "external sentinel" );
            return (root, outside, new AssetPathResolver( root ));
        }
        void Check( string name, Action test ) => suite.Add( "asset-paths", name, test );
        void LinuxCheck( string name, Action test ) => suite.Add( "asset-paths", name, test,
            () => OperatingSystem.IsLinux() ? null : "Linux-specific case/symlink fixture; not exercised on this platform" );

        Check( "relative roots and implicit external references are rejected", () =>
        {
            Throws<ArgumentException>( () => new AssetPathResolver( "relative-root" ) );
            var f = Fixture();
            Throws<ArgumentException>( () => f.Resolver.ResolveRead( new( "skin", Path.Combine( f.Outside, "skin.png" ) ) ) );
            Throws<ArgumentException>( () => f.Resolver.ResolveRead( new( "skin", "skin.png", AssetReferenceKind.ExternalReadOnly ) ) );
            Throws<ArgumentException>( () => f.Resolver.ResolveRead( new( "", "textures/skin.png" ) ) );
        } );
        Check( "relative assets normalize without changing cwd or source", () =>
        {
            var f = Fixture();
            string before = Environment.CurrentDirectory;
            var a = f.Resolver.ResolveRead( new( "skin", "textures/./../textures/skin.png" ) );
            var b = f.Resolver.ResolveRead( new( "same logical asset", "textures\\skin.png" ) );
            Require( a.CanonicalPath == b.CanonicalPath && a.ReferencePath == "textures/skin.png" );
            Require( a.Id == "skin" && b.Id != a.Id && Environment.CurrentDirectory == before );
            Require( File.ReadAllText( a.CanonicalPath ) == "source sentinel" );
        } );
        Check( "changing cwd does not change explicit-root resolution", () =>
        {
            var f = Fixture();
            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = f.Outside;
                Require( f.Resolver.ResolveRead( new( "skin", "textures/skin.png" ) ).CanonicalPath == Path.Combine( f.Root, "textures", "skin.png" ) );
            }
            finally { Environment.CurrentDirectory = previous; }
        } );
        Check( "two same basenames retain separate canonical identities", () =>
        {
            var f = Fixture(); Directory.CreateDirectory( Path.Combine( f.Root, "other" ) );
            File.WriteAllText( Path.Combine( f.Root, "other", "skin.png" ), "different" );
            Require( f.Resolver.ResolveRead( new( "a", "textures/skin.png" ) ).CanonicalPath != f.Resolver.ResolveRead( new( "b", "other/skin.png" ) ).CanonicalPath );
        } );
        Check( "traversal and sibling-prefix escape are rejected", () =>
        {
            var f = Fixture();
            Throws<UnauthorizedAccessException>( () => f.Resolver.ResolveRead( new( "escape", "../outside/skin.png" ) ) );
            Throws<UnauthorizedAccessException>( () => f.Resolver.ResolveWrite( new( "escape", "../project-sibling/new.png" ) ) );
        } );
        Check( "external reads are opt-in and always read-only", () =>
        {
            var f = Fixture(); var external = new AssetReference( "external", Path.Combine( f.Outside, "skin.png" ), AssetReferenceKind.ExternalReadOnly );
            var result = f.Resolver.ResolveRead( external );
            Require( result.Exists && result.Kind == AssetReferenceKind.ExternalReadOnly );
            Throws<UnauthorizedAccessException>( () => f.Resolver.ResolveWrite( external ) );
        } );
        Check( "missing reads and new outputs never create directories", () =>
        {
            var f = Fixture(); var reference = new AssetReference( "new", "new/subdir/model.effigy" );
            Throws<FileNotFoundException>( () => f.Resolver.ResolveRead( reference ) );
            Require( !f.Resolver.ResolveRead( reference, requireExists: false ).Exists );
            Require( !f.Resolver.ResolveWrite( reference ).Exists && !Directory.Exists( Path.Combine( f.Root, "new" ) ) );
        } );
        Check( "directories and file ancestors are not valid asset paths", () =>
        {
            var f = Fixture();
            Throws<IOException>( () => f.Resolver.ResolveRead( new( "dir", "textures" ) ) );
            Throws<IOException>( () => f.Resolver.ResolveWrite( new( "file-parent", "textures/skin.png/new" ) ) );
        } );
        Check( "Unicode and spaces survive resolution and a project move", () =>
        {
            var f = Fixture(); var reference = new AssetReference( "stable-id", "texture café 海.png" );
            File.WriteAllText( Path.Combine( f.Root, reference.Path ), "owned fixture" );
            var old = f.Resolver.ResolveRead( reference );
            string moved = f.Root + " moved"; Directory.Move( f.Root, moved );
            var result = new AssetPathResolver( moved ).ResolveRead( reference );
            Require( result.Id == old.Id && result.ReferencePath == old.ReferencePath && result.CanonicalPath != old.CanonicalPath );
        } );
        Check( "Save As relocation preserves logical ID and source without copying", () =>
        {
            var f = Fixture(); var reference = new AssetReference( "stable-id", "textures/skin.png" );
            var textureRoot = new AssetPathResolver( Path.Combine( f.Root, "textures" ) );
            var inside = f.Resolver.RelocateReference( reference, textureRoot );
            Require( inside.Id == reference.Id && inside.Path == "skin.png" && inside.Kind == AssetReferenceKind.ProjectRelative );
            var outside = new AssetPathResolver( f.Outside );
            Throws<UnauthorizedAccessException>( () => f.Resolver.RelocateReference( reference, outside ) );
            var external = f.Resolver.RelocateReference( reference, outside, allowExternal: true );
            Require( external.Kind == AssetReferenceKind.ExternalReadOnly && external.Id == reference.Id );
            Require( outside.ResolveRead( external ).CanonicalPath == f.Resolver.ResolveRead( reference ).CanonicalPath );
            Require( File.ReadAllText( Path.Combine( f.Outside, "skin.png" ) ) == "external sentinel" );
        } );
        Check( "relocation cannot silently turn an external source into a writable project asset", () =>
        {
            var f = Fixture();
            var external = new AssetReference( "external", Path.Combine( f.Root, "textures", "skin.png" ), AssetReferenceKind.ExternalReadOnly );
            Throws<UnauthorizedAccessException>( () => f.Resolver.RelocateReference( external, f.Resolver ) );
            var relocated = f.Resolver.RelocateReference( external, f.Resolver, allowExternal: true );
            Require( relocated.Kind == AssetReferenceKind.ExternalReadOnly );
            Throws<UnauthorizedAccessException>( () => f.Resolver.ResolveWrite( relocated ) );
        } );
            LinuxCheck( "case mismatch is diagnosed on Linux", () =>
            {
                var f = Fixture();
                try { f.Resolver.ResolveRead( new( "case", "textures/SKIN.png" ) ); }
                catch ( FileNotFoundException ex ) { Require( ex.Message.Contains( "case" ) ); return; }
                throw new Exception( "Case-mismatched asset unexpectedly resolved" );
            } );
            LinuxCheck( "symlink escapes and nonexistent output ancestors are rejected", () =>
            {
                var f = Fixture(); Directory.CreateSymbolicLink( Path.Combine( f.Root, "escape" ), f.Outside );
                Throws<UnauthorizedAccessException>( () => f.Resolver.ResolveRead( new( "escape", "escape/skin.png" ) ) );
                Throws<UnauthorizedAccessException>( () => f.Resolver.ResolveWrite( new( "escape", "escape/new/subdir/output.png" ) ) );
                File.CreateSymbolicLink( Path.Combine( f.Root, "dangling" ), Path.Combine( f.Outside, "absent.png" ) );
                Throws<UnauthorizedAccessException>( () => f.Resolver.ResolveWrite( new( "escape", "dangling" ) ) );
            } );
            LinuxCheck( "in-root symlink aliases share canonical identity", () =>
            {
                var f = Fixture(); Directory.CreateSymbolicLink( Path.Combine( f.Root, "alias" ), "textures" );
                Require( f.Resolver.ResolveRead( new( "alias", "alias/skin.png" ) ).CanonicalPath == f.Resolver.ResolveRead( new( "source", "textures/skin.png" ) ).CanonicalPath );
            } );
            LinuxCheck( "symlink cycles fail and link-dotdot follows filesystem semantics", () =>
            {
                var f = Fixture();
                File.CreateSymbolicLink( Path.Combine( f.Root, "cycle" ), "cycle" );
                Throws<IOException>( () => f.Resolver.ResolveRead( new( "cycle", "cycle" ) ) );
                Directory.CreateSymbolicLink( Path.Combine( f.Root, "escape" ), f.Outside );
                // Lexically looks inside root, but after following escape, '..' is its parent.
                Throws<UnauthorizedAccessException>( () => f.Resolver.ResolveWrite( new( "escape", "escape/../new.png" ) ) );
            } );
    }

    static void Require( bool condition ) { if ( !condition ) throw new Exception( "Asset path invariant failed." ); }
    static void Throws<T>( Action body ) where T : Exception
    {
        try { body(); }
        catch ( T ) { return; }
        throw new Exception( $"Expected {typeof(T).Name}." );
    }
}
