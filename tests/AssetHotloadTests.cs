using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Effigy.Map;

internal static class AssetHotloadTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( string name, Action test ) => registry.Add( "assethotload", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}
		void Throws<T>( Action action, string what ) where T : Exception
		{
			try { action(); }
			catch ( T ) { return; }
			catch ( Exception ex ) { throw new Exception( $"{what}: wrong exception {ex.GetType().Name}" ); }
			throw new Exception( $"{what}: expected {typeof( T ).Name}, got silent success" );
		}

		string FixtureDir()
		{
			foreach ( var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() } )
			{
				var dir = new DirectoryInfo( start );
				while ( dir is not null )
				{
					var candidate = Path.Combine( dir.FullName, "tests", "fixtures", "hammer", "hammer-assets-hotload" );
					if ( File.Exists( Path.Combine( candidate, "crate.obj" ) ) ) return candidate;
					dir = dir.Parent;
				}
			}
			throw new Exception( "assethotload fixture dir not found (crate.obj missing above cwd / BaseDirectory)" );
		}

		// Fresh project root per test: fixture files are copied in, the
		// tests edit the COPIES, never the fixtures.
		string NewProject( string name )
		{
			var root = Path.Combine( settings.WorkDirectory, "assethotload", name + "-" + Guid.NewGuid().ToString( "N" ).Substring( 0, 8 ) );
			var fixture = FixtureDir();
			Directory.CreateDirectory( Path.Combine( root, "models", "props" ) );
			Directory.CreateDirectory( Path.Combine( root, "materials" ) );
			Directory.CreateDirectory( Path.Combine( root, "particles" ) );
			Directory.CreateDirectory( Path.Combine( root, "sounds" ) );
			File.Copy( Path.Combine( fixture, "crate.obj" ), Path.Combine( root, "models", "props", "crate.obj" ) );
			File.Copy( Path.Combine( fixture, "metal.vmt" ), Path.Combine( root, "materials", "metal.vmt" ) );
			File.Copy( Path.Combine( fixture, "spark.json" ), Path.Combine( root, "particles", "spark.json" ) );
			File.Copy( Path.Combine( fixture, "beep.wav" ), Path.Combine( root, "sounds", "beep.wav" ) );
			return root;
		}

		string ProjectPath( string root, string relative ) =>
			Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) );

		void OverwriteFromFixture( string root, string fixtureName, string relativeTarget )
		{
			var target = ProjectPath( root, relativeTarget );
			File.Copy( Path.Combine( FixtureDir(), fixtureName ), target, overwrite: true );
			File.SetLastWriteTimeUtc( target, DateTime.UtcNow.AddSeconds( 2 ) );
		}

		void BumpMtime( string root, string relativeTarget ) =>
			File.SetLastWriteTimeUtc( ProjectPath( root, relativeTarget ), DateTime.UtcNow.AddSeconds( 2 ) );

		HotloadDocument DefaultDocument()
		{
			var doc = new HotloadDocument();
			doc.AddModelReference( "models/props/crate.obj", 7, "prop_static crate" );
			doc.AddMaterialReference( "materials/metal.vmt", 3, 0, "crate face" );
			doc.AddSoundReference( "sounds/beep.wav", 9, "crate creak" );
			doc.AddParticleReference( "particles/spark.json", 11, "impact sparks" );
			return doc;
		}

		AssetHotload WatchAll( string root, HotloadDocument? doc = null )
		{
			var hotload = new AssetHotload( root, doc );
			hotload.Watch( AssetKind.Model, "models/props/crate.obj" );
			hotload.Watch( AssetKind.Material, "materials/metal.vmt" );
			hotload.Watch( AssetKind.Sound, "sounds/beep.wav" );
			hotload.Watch( AssetKind.Particle, "particles/spark.json" );
			return hotload;
		}

		Check( "unchanged files report nothing on repeated polls", () =>
		{
			var hotload = WatchAll( NewProject( "unchanged" ), DefaultDocument() );
			Require( hotload.PollForChanges().Count == 0, "first poll reported a change for untouched files" );
			Require( hotload.PollForChanges().Count == 0, "second poll reported a change for untouched files" );
		} );

		Check( "touching a model file reports one Modified change for it", () =>
		{
			var root = NewProject( "touch-model" );
			var hotload = WatchAll( root, DefaultDocument() );
			OverwriteFromFixture( root, "crate-v2.obj", "models/props/crate.obj" );
			var changes = hotload.PollForChanges();
			Require( changes.Count == 1, $"expected 1 change, got {changes.Count}" );
			Require( changes[0].Kind == HotloadChangeKind.Modified, $"change kind was {changes[0].Kind}, expected Modified" );
			Require( changes[0].Asset.Kind == AssetKind.Model, "changed asset was not the model" );
			Require( changes[0].Asset.Path == "models/props/crate.obj", $"changed path was {changes[0].Asset.Path}" );
		} );

		Check( "reload after a model edit returns the new definition", () =>
		{
			var root = NewProject( "reload-model" );
			var hotload = WatchAll( root, DefaultDocument() );
			var model = hotload.TryGetAsset( AssetKind.Model, "models/props/crate.obj" )!;
			var first = hotload.Reload( model );
			Require( first.Success, "initial model reload failed: " + first.Error );
			Require( first.DefinitionChanged, "initial load did not count as a definition change" );
			Require( model.Revision == 1, $"revision after first load was {model.Revision}, expected 1" );
			Require( first.Definition!.VertexCount == 8 && first.Definition.FaceCount == 6,
				$"initial definition was {first.Definition.VertexCount} verts / {first.Definition.FaceCount} faces, expected 8 / 6" );
			var hashBefore = first.Definition.ContentHash;
			OverwriteFromFixture( root, "crate-v2.obj", "models/props/crate.obj" );
			Require( hotload.PollForChanges().Count == 1, "edit was not reported" );
			var second = hotload.Reload( model );
			Require( second.Success, "reload after edit failed: " + second.Error );
			Require( second.DefinitionChanged, "reload after edit reported no definition change" );
			Require( model.Revision == 2, $"revision after reload was {model.Revision}, expected 2" );
			Require( second.Definition!.VertexCount == 12 && second.Definition.FaceCount == 8,
				$"reloaded definition was {second.Definition.VertexCount} verts / {second.Definition.FaceCount} faces, expected 12 / 8" );
			Require( second.Definition.ContentHash != hashBefore, "content hash did not move after the edit" );
			Require( second.Definition.Fields["object"] == "crate", "obj object name was not parsed" );
		} );

		Check( "mtime-only touch reports Modified but reload leaves the definition unchanged", () =>
		{
			var root = NewProject( "touch-only" );
			var hotload = WatchAll( root, DefaultDocument() );
			var model = hotload.TryGetAsset( AssetKind.Model, "models/props/crate.obj" )!;
			var first = hotload.Reload( model );
			BumpMtime( root, "models/props/crate.obj" );
			var changes = hotload.PollForChanges();
			Require( changes.Count == 1 && changes[0].Kind == HotloadChangeKind.Modified, "mtime-only touch was not reported as Modified" );
			var second = hotload.Reload( model );
			Require( second.Success, "reload after touch failed: " + second.Error );
			Require( !second.DefinitionChanged, "mtime-only touch wrongly counted as a definition change" );
			Require( model.Revision == 1, $"revision moved to {model.Revision} on an unchanged reload" );
			Require( second.Definition!.ContentHash == first.Definition!.ContentHash, "content hash moved on an unchanged reload" );
		} );

		Check( "second poll after a reload reports nothing", () =>
		{
			var root = NewProject( "poll-quiet" );
			var hotload = WatchAll( root, DefaultDocument() );
			var model = hotload.TryGetAsset( AssetKind.Model, "models/props/crate.obj" )!;
			hotload.Reload( model );
			OverwriteFromFixture( root, "crate-v2.obj", "models/props/crate.obj" );
			Require( hotload.PollForChanges().Count == 1, "edit was not reported" );
			hotload.Reload( model );
			Require( hotload.PollForChanges().Count == 0, "poll after reload still reported a change" );
		} );

		Check( "deleting a referenced model reports Deleted and revalidates to a named MISSING_MODEL issue", () =>
		{
			var root = NewProject( "delete-model" );
			var hotload = WatchAll( root, DefaultDocument() );
			var model = hotload.TryGetAsset( AssetKind.Model, "models/props/crate.obj" )!;
			hotload.Reload( model );
			File.Delete( ProjectPath( root, "models/props/crate.obj" ) );
			var changes = hotload.PollForChanges();
			Require( changes.Count == 1 && changes[0].Kind == HotloadChangeKind.Deleted, "deletion was not reported as Deleted" );
			Require( hotload.PollForChanges().Count == 0, "deletion was reported twice" );
			var reload = hotload.Reload( model );
			Require( !reload.Success, "reload of a deleted model wrongly succeeded" );
			Require( reload.Error.Contains( "not found", StringComparison.OrdinalIgnoreCase ), $"reload error did not name the missing file: {reload.Error}" );
			Require( reload.Definition is not null, "failed reload cleared the last good definition" );
			var issues = hotload.RevalidateReferences( model );
			Require( issues.Count == 1, $"expected 1 issue, got {issues.Count}" );
			Require( issues[0].Code == "MISSING_MODEL", $"issue code was {issues[0].Code}, expected MISSING_MODEL" );
			Require( issues[0].EntityId == 7, $"issue entity was {issues[0].EntityId}, expected 7" );
			Require( issues[0].AssetPath == "models/props/crate.obj", "issue did not name the model path" );
			Require( issues[0].FixHint.Length > 0, "issue has no fix hint" );
			Require( hotload.RevalidateAll().Count == 1, "RevalidateAll did not return the same single issue" );
		} );

		Check( "restoring a deleted model reports Added and revalidates clean", () =>
		{
			var root = NewProject( "restore-model" );
			var hotload = WatchAll( root, DefaultDocument() );
			var model = hotload.TryGetAsset( AssetKind.Model, "models/props/crate.obj" )!;
			hotload.Reload( model );
			File.Delete( ProjectPath( root, "models/props/crate.obj" ) );
			Require( hotload.PollForChanges().Count == 1, "deletion was not reported" );
			OverwriteFromFixture( root, "crate.obj", "models/props/crate.obj" );
			var changes = hotload.PollForChanges();
			Require( changes.Count == 1 && changes[0].Kind == HotloadChangeKind.Added, "restore was not reported as Added" );
			var reload = hotload.Reload( model );
			Require( reload.Success, "reload after restore failed: " + reload.Error );
			Require( !reload.DefinitionChanged, "restoring identical bytes wrongly counted as a definition change" );
			Require( model.Revision == 1, $"revision moved to {model.Revision} on an identical restore" );
			Require( hotload.RevalidateReferences( model ).Count == 0, "revalidate still flagged the restored model" );
		} );

		Check( "material edit reloads the new VMT fields", () =>
		{
			var root = NewProject( "edit-material" );
			var hotload = WatchAll( root, DefaultDocument() );
			var first = hotload.Reload( AssetKind.Material, "materials/metal.vmt" );
			Require( first.Success, "initial material reload failed: " + first.Error );
			Require( first.Definition!.Fields["$basetexture"] == "dev/metal", "initial $basetexture was not parsed" );
			OverwriteFromFixture( root, "metal-v2.vmt", "materials/metal.vmt" );
			Require( hotload.PollForChanges().Count == 1, "material edit was not reported" );
			var second = hotload.Reload( AssetKind.Material, "materials/metal.vmt" );
			Require( second.Success && second.DefinitionChanged, "material reload after edit failed or reported no change" );
			Require( second.Definition!.Fields["$basetexture"] == "dev/metal2", $"reloaded $basetexture was {second.Definition.Fields["$basetexture"]}" );
			Require( second.Definition.Fields["$color"] == "[1 0 0]", "reloaded $color was not parsed" );
			Require( hotload.RevalidateReferences( hotload.TryGetAsset( AssetKind.Material, "materials/metal.vmt" )! ).Count == 0,
				"revalidate flagged the edited material" );
		} );

		Check( "sound file change is detected and reloads by hash at equal length", () =>
		{
			var root = NewProject( "edit-sound" );
			var hotload = WatchAll( root, DefaultDocument() );
			var sound = hotload.TryGetAsset( AssetKind.Sound, "sounds/beep.wav" )!;
			var first = hotload.Reload( sound );
			Require( first.Success, "initial sound reload failed: " + first.Error );
			Require( first.Definition!.Fields.Count == 0, "binary sound wrongly gained parsed fields" );
			Require( first.Definition.Length == 844, $"sound length was {first.Definition.Length}, expected 844" );
			Require( first.Definition.ContentHash.Length == 64, "sound hash is not a SHA-256 hex string" );
			OverwriteFromFixture( root, "beep-v2.wav", "sounds/beep.wav" );
			Require( hotload.PollForChanges().Count == 1, "sound edit was not reported" );
			var second = hotload.Reload( sound );
			Require( second.Success && second.DefinitionChanged, "sound reload after edit failed or reported no change" );
			Require( second.Definition!.Length == 844, "sound length moved between the two fixtures" );
			Require( second.Definition.ContentHash != first.Definition.ContentHash, "sound hash did not move at equal length" );
		} );

		Check( "particle JSON edit reloads the new params", () =>
		{
			var root = NewProject( "edit-particle" );
			var hotload = WatchAll( root, DefaultDocument() );
			var particle = hotload.TryGetAsset( AssetKind.Particle, "particles/spark.json" )!;
			var first = hotload.Reload( particle );
			Require( first.Success, "initial particle reload failed: " + first.Error );
			Require( first.Definition!.Fields["count"] == "64", "initial particle count was not parsed" );
			Require( first.Definition.Fields["system"] == "spark_burst", "initial particle system was not parsed" );
			OverwriteFromFixture( root, "spark-v2.json", "particles/spark.json" );
			Require( hotload.PollForChanges().Count == 1, "particle edit was not reported" );
			var second = hotload.Reload( particle );
			Require( second.Success && second.DefinitionChanged, "particle reload after edit failed or reported no change" );
			Require( second.Definition!.Fields["count"] == "128", $"reloaded count was {second.Definition.Fields["count"]}, expected 128" );
			Require( second.Definition.Fields["lifetime"] == "2.0", "reloaded lifetime was not parsed" );
		} );

		Check( "revalidate with everything present returns no issues", () =>
		{
			var root = NewProject( "revalidate-clean" );
			var hotload = WatchAll( root, DefaultDocument() );
			foreach ( var asset in hotload.WatchedAssets )
			{
				var reload = hotload.Reload( asset );
				Require( reload.Success, $"initial reload failed for {asset.Path}: {reload.Error}" );
			}
			Require( hotload.RevalidateAll().Count == 0, "clean map revalidated with issues" );
		} );

		Check( "revalidate with no document bound throws", () =>
		{
			var root = NewProject( "no-document" );
			var hotload = WatchAll( root );
			var model = hotload.TryGetAsset( AssetKind.Model, "models/props/crate.obj" )!;
			Throws<InvalidOperationException>( () => hotload.RevalidateReferences( model ), "RevalidateReferences without a document" );
			Throws<InvalidOperationException>( () => hotload.RevalidateAll(), "RevalidateAll without a document" );
		} );

		Check( "watching the same path twice returns one asset and Unwatch stops reporting", () =>
		{
			var root = NewProject( "watch-twice" );
			var hotload = WatchAll( root, DefaultDocument() );
			var first = hotload.TryGetAsset( AssetKind.Model, "models/props/crate.obj" )!;
			var second = hotload.Watch( AssetKind.Model, "models\\props\\crate.obj" );
			Require( ReferenceEquals( first, second ), "second Watch returned a different asset" );
			Require( hotload.WatchedAssets.Count == 4, $"watched count was {hotload.WatchedAssets.Count}, expected 4" );
			Require( hotload.Unwatch( AssetKind.Model, "models/props/crate.obj" ), "Unwatch reported the model was not watched" );
			Require( !hotload.Unwatch( AssetKind.Model, "models/props/crate.obj" ), "second Unwatch wrongly succeeded" );
			Require( hotload.WatchedAssets.Count == 3, "unwatched model still listed" );
			OverwriteFromFixture( root, "crate-v2.obj", "models/props/crate.obj" );
			var changes = hotload.PollForChanges();
			Require( changes.Count == 0, "unwatched model edit was still reported" );
		} );

		Check( "absolute and escaping paths are rejected and unwatched reloads throw", () =>
		{
			var root = NewProject( "bad-paths" );
			var hotload = WatchAll( root, DefaultDocument() );
			Throws<ArgumentException>( () => hotload.Watch( AssetKind.Model, "../outside.obj" ), "escaping path" );
			Throws<ArgumentException>( () => hotload.Watch( AssetKind.Model, "/abs/path.obj" ), "absolute path" );
			Throws<ArgumentException>( () => hotload.Watch( AssetKind.Model, "C:\\x.obj" ), "drive path" );
			Throws<ArgumentException>( () => hotload.Watch( AssetKind.Model, "  " ), "blank path" );
			Require( AssetHotload.NormalizePath( "models/../models/x.obj" ) == "models/x.obj", "harmless .. was not collapsed" );
			Throws<ArgumentException>( () => hotload.Reload( AssetKind.Model, "models/props/not-watched.obj" ), "reload by kind+path of an unwatched asset" );
			Throws<ArgumentException>( () => hotload.Reload( new HotloadAsset { Kind = AssetKind.Model, Path = "models/props/crate.obj" } ), "reload of a foreign asset instance" );
			Throws<ArgumentException>( () => new AssetHotload( "" ), "empty root" );
			Throws<DirectoryNotFoundException>( () => new AssetHotload( Path.Combine( root, "no-such-root" ) ), "missing root" );
		} );

		Check( "PollAndReload reloads two edits and reports zero issues", () =>
		{
			var root = NewProject( "poll-and-reload" );
			var hotload = WatchAll( root, DefaultDocument() );
			foreach ( var asset in hotload.WatchedAssets ) hotload.Reload( asset );
			OverwriteFromFixture( root, "crate-v2.obj", "models/props/crate.obj" );
			OverwriteFromFixture( root, "metal-v2.vmt", "materials/metal.vmt" );
			var report = hotload.PollAndReload();
			Require( report.Changes.Count == 2, $"report had {report.Changes.Count} changes, expected 2" );
			Require( report.Reloads.Count == 2 && report.Reloads.All( r => r.Success ), "report reloads were not all successful" );
			Require( report.Reloads.All( r => r.DefinitionChanged ), "report reloads did not all change" );
			Require( report.Issues.Count == 0, $"report had {report.Issues.Count} issues, expected 0" );
			Require( !report.HasIssues, "report wrongly flagged issues" );
			Require( report.ToString().Contains( "2 changed", StringComparison.Ordinal ), $"report text was: {report}" );
		} );

		Check( "PollAndReload on a deleted model reports the failed reload and the named issue", () =>
		{
			var root = NewProject( "poll-and-reload-deleted" );
			var hotload = WatchAll( root, DefaultDocument() );
			foreach ( var asset in hotload.WatchedAssets ) hotload.Reload( asset );
			File.Delete( ProjectPath( root, "models/props/crate.obj" ) );
			var report = hotload.PollAndReload();
			Require( report.Changes.Count == 1 && report.Changes[0].Kind == HotloadChangeKind.Deleted, "report did not carry the deletion" );
			Require( report.Reloads.Count == 1 && !report.Reloads[0].Success, "report did not carry the failed reload" );
			Require( report.Issues.Count == 1 && report.Issues[0].Code == "MISSING_MODEL", "report did not carry the MISSING_MODEL issue" );
			Require( report.HasIssues, "report wrongly claimed no issues" );
		} );

		Check( "deleting an unreferenced asset reports Deleted but revalidates with no issues", () =>
		{
			var root = NewProject( "delete-unreferenced" );
			var hotload = new AssetHotload( root, new HotloadDocument() );
			var spare = hotload.Watch( AssetKind.Model, "models/props/crate.obj" );
			hotload.Reload( spare );
			File.Delete( ProjectPath( root, "models/props/crate.obj" ) );
			Require( hotload.PollForChanges().Count == 1, "deletion was not reported" );
			Require( hotload.RevalidateReferences( spare ).Count == 0, "unreferenced deletion wrongly produced an issue" );
			Require( hotload.RevalidateAll().Count == 0, "unreferenced deletion wrongly produced an issue via RevalidateAll" );
		} );
	}
}
