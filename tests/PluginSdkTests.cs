using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Marionette.Desktop;

// Godlike Wave 3 P2 — Plugin SDK v1 + text-scene collaboration.
// Covers: unsigned/tampered/mis-signed add-ons rejected, API-version and
// capability gates, install/uninstall/registry, a plug-in running in the
// sandbox (project-root-scoped assets, capability-checked registration),
// store search ranking, text-scene canonical round-trips and diffs,
// two-artist file locking, and the turntable viewer. No build, app launch
// or screenshot is claimed here; the full build and visual acceptance
// belong to the final integrator.
internal static class PluginSdkTests
{
	static readonly byte[] PublisherKey = Encoding.UTF8.GetBytes( "test-publisher-key-123" );

	/// <summary>A test plug-in: registers one brush, reads one asset, writes
	/// one stamped asset, logs. It only ever sees the IPluginApi.</summary>
	sealed class FakeBrushPlugin : ISdkPlugin
	{
		public string Name => "Fake Rivet Brush";
		public string Version => "1.0";
		public void OnLoad( IPluginApi api )
		{
			api.Log( "fake brush loaded" );
			api.RegisterBrush( "Rivet", "A rivet brush" );
			var text = api.ReadAssetText( "notes.txt" );
			api.WriteAssetText( "out.txt", text + "-stamped" );
		}
		public void OnUnload() { }
	}

	public static void Register( TestRegistry suite, TestSettings settings )
	{
		void Check( string name, Action test ) => suite.Add( "plugin-sdk", name, test );
		void Require( bool condition, string message = "assertion failed" )
		{
			if ( !condition ) throw new Exception( message );
		}

		static string TempDir()
		{
			var dir = Path.Combine( Path.GetTempPath(), "plugin-sdk-tests-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( dir );
			return dir;
		}

		static void Cleanup( string dir )
		{
			try { if ( Directory.Exists( dir ) ) Directory.Delete( dir, recursive: true ); } catch { }
		}

		static Dictionary<string, byte[]> Trusted() => new()
		{
			["test-publisher"] = PublisherKey,
		};

		/// <summary>A signed package directory: addon.json + brush.dll +
		/// readme.txt, with correct per-file hashes.</summary>
		static (string PackageDir, AddonManifest Manifest) MakePackage(
			string root, Action<AddonManifest>? tweak = null, byte[]? signKey = null )
		{
			var packageDir = Path.Combine( root, "package" );
			Directory.CreateDirectory( packageDir );
			File.WriteAllText( Path.Combine( packageDir, "brush.dll" ), "fake-dll-bytes" );
			File.WriteAllText( Path.Combine( packageDir, "readme.txt" ), "A rivet brush." );
			var manifest = new AddonManifest
			{
				Id = "rivet-brush",
				Name = "Rivet Brush",
				Version = "1.0",
				Author = "Test Author",
				Summary = "Places rivets along a curve.",
				Kind = AddonKind.Brush,
				Tags = new List<string> { "rivet", "detail" },
				PublisherId = "test-publisher",
				RequiredApiMajor = 1,
				RequiredApiMinor = 0,
				EntryPoint = "Fake.Plugin, Fake",
				Capabilities = new List<string> { "brush.register", "assets.read", "assets.write", "log" },
				Files = new List<AddonFile>
				{
					new() { Path = "brush.dll", Sha256 = AddonSignature.Sha256HexFile( Path.Combine( packageDir, "brush.dll" ) ) },
					new() { Path = "readme.txt", Sha256 = AddonSignature.Sha256HexFile( Path.Combine( packageDir, "readme.txt" ) ) },
				},
			};
			tweak?.Invoke( manifest );
			manifest.Signature = AddonSignature.Sign( manifest, signKey ?? PublisherKey );
			var json = JsonSerializer.Serialize( manifest, new JsonSerializerOptions
			{
				PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
				WriteIndented = true,
			} );
			File.WriteAllText( Path.Combine( packageDir, "addon.json" ), json );
			return ( packageDir, manifest );
		}

		Check( "an unsigned add-on is rejected", () =>
		{
			var root = TempDir();
			try
			{
				var (packageDir, manifest) = MakePackage( root );
				// Strip the signature after signing: the package is unsigned.
				var json = File.ReadAllText( Path.Combine( packageDir, "addon.json" ) );
				var unsigned = JsonSerializer.Deserialize<AddonManifest>( json, new JsonSerializerOptions
				{
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
					PropertyNameCaseInsensitive = true,
				} )!;
				unsigned.Signature = null;
				File.WriteAllText( Path.Combine( packageDir, "addon.json" ),
					JsonSerializer.Serialize( unsigned, new JsonSerializerOptions
					{
						PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
						WriteIndented = true,
					} ) );
				_ = manifest;
				var validation = AddonPackage.Validate( packageDir, Trusted(), PluginSdk.CurrentApi );
				Require( !validation.Ok, "unsigned package must fail validation" );
				Require( validation.Errors.Any( e => e.Contains( "Unsigned", StringComparison.Ordinal ) ),
					"rejection must name the missing signature" );
			}
			finally { Cleanup( root ); }
		} );

		Check( "a tampered file is rejected", () =>
		{
			var root = TempDir();
			try
			{
				var (packageDir, _) = MakePackage( root );
				File.WriteAllText( Path.Combine( packageDir, "readme.txt" ), "Tampered." );
				var validation = AddonPackage.Validate( packageDir, Trusted(), PluginSdk.CurrentApi );
				Require( !validation.Ok, "tampered package must fail validation" );
				Require( validation.Errors.Any( e => e.Contains( "hash mismatch", StringComparison.Ordinal ) ),
					"rejection must name the hash mismatch" );
			}
			finally { Cleanup( root ); }
		} );

		Check( "a bad signature and an unknown publisher are rejected", () =>
		{
			var root = TempDir();
			try
			{
				var (packageDir, _) = MakePackage( root, signKey: Encoding.UTF8.GetBytes( "someone-elses-key" ) );
				var validation = AddonPackage.Validate( packageDir, Trusted(), PluginSdk.CurrentApi );
				Require( !validation.Ok, "package signed with another key must fail" );
				Require( validation.Errors.Any( e => e.Contains( "Signature does not match", StringComparison.Ordinal ) ),
					"rejection must name the signature" );

				var (packageDir2, _) = MakePackage( Path.Combine( root, "second" ),
					tweak: m => m.PublisherId = "nobody" );
				var validation2 = AddonPackage.Validate( packageDir2, Trusted(), PluginSdk.CurrentApi );
				Require( !validation2.Ok, "unknown publisher must fail" );
				Require( validation2.Errors.Any( e => e.Contains( "Unknown publisher", StringComparison.Ordinal ) ),
					"rejection must name the unknown publisher" );
			}
			finally { Cleanup( root ); }
		} );

		Check( "an incompatible API version and an ungrantable capability are rejected", () =>
		{
			var root = TempDir();
			try
			{
				var (packageDir, _) = MakePackage( Path.Combine( root, "a" ),
					tweak: m => m.RequiredApiMajor = 2 );
				var validation = AddonPackage.Validate( packageDir, Trusted(), PluginSdk.CurrentApi );
				Require( !validation.Ok, "API major 2 must fail against host 1.0" );
				Require( validation.Errors.Any( e => e.Contains( "needs API 2.0", StringComparison.Ordinal ) ),
					"rejection must name the API version" );

				var (packageDir2, _) = MakePackage( Path.Combine( root, "b" ),
					tweak: m => m.Capabilities.Add( "kernel" ) );
				var validation2 = AddonPackage.Validate( packageDir2, Trusted(), PluginSdk.CurrentApi );
				Require( !validation2.Ok, "a 'kernel' capability must fail" );
				Require( validation2.Errors.Any( e => e.Contains( "never granted", StringComparison.Ordinal ) ),
					"rejection must name the ungrantable capability" );
			}
			finally { Cleanup( root ); }
		} );

		Check( "API version compatibility follows same-major, host-minor-grows", () =>
		{
			Require( new PluginApiVersion( 1, 0 ).SatisfiedBy( PluginSdk.CurrentApi ),
				"1.0 must be satisfied by the 1.0 host" );
			Require( !new PluginApiVersion( 1, 1 ).SatisfiedBy( PluginSdk.CurrentApi ),
				"1.1 must not be satisfied by the 1.0 host" );
			Require( !new PluginApiVersion( 2, 0 ).SatisfiedBy( PluginSdk.CurrentApi ),
				"2.0 must not be satisfied by the 1.0 host" );
			Require( new PluginApiVersion( 1, 0 ).SatisfiedBy( new PluginApiVersion( 1, 3 ) ),
				"1.0 must be satisfied by a 1.3 host" );
		} );

		Check( "a valid add-on installs, is listed, refuses a reinstall, and uninstalls", () =>
		{
			var root = TempDir();
			try
			{
				var (packageDir, _) = MakePackage( root );
				var addonsRoot = Path.Combine( root, "addons" );
				var install = AddonInstaller.Install( packageDir, addonsRoot, Trusted() );
				Require( install.Ok, "valid package must install: " + install.Error );
				Require( install.Installed != null && install.Installed.Id == "rivet-brush",
					"install record must name the add-on" );
				Require( File.Exists( Path.Combine( install.Installed!.Path, "brush.dll" ) ),
					"installed files must be copied" );
				Require( AddonInstaller.Installed( addonsRoot ).Count == 1,
					"registry must list one installed add-on" );

				var again = AddonInstaller.Install( packageDir, addonsRoot, Trusted() );
				Require( !again.Ok && again.Error!.Contains( "already installed", StringComparison.Ordinal ),
					"reinstalling the same version must be refused" );

				Require( AddonInstaller.Uninstall( addonsRoot, "rivet-brush", "1.0" ),
					"uninstall must succeed" );
				Require( AddonInstaller.Installed( addonsRoot ).Count == 0,
					"registry must be empty after uninstall" );
				Require( !Directory.Exists( install.Installed.Path ),
					"uninstall must remove the add-on directory" );
			}
			finally { Cleanup( root ); }
		} );

		Check( "a plug-in runs in the sandbox: only the versioned API, project-scoped assets", () =>
		{
			var root = TempDir();
			try
			{
				var projectRoot = Path.Combine( root, "project" );
				Directory.CreateDirectory( projectRoot );
				File.WriteAllText( Path.Combine( projectRoot, "notes.txt" ), "hello" );
				var logs = new List<string>();
				var host = new SdkPluginHost();
				var api = host.CreateApi( projectRoot,
					new[] { "brush.register", "assets.read", "assets.write", "log" },
					message => logs.Add( message ) );
				var plugin = host.Load( new FakeBrushPlugin(), api );
				Require( plugin.Name == "Fake Rivet Brush", "host must load the plug-in" );
				Require( logs.Contains( "fake brush loaded" ), "plug-in must reach the log" );
				Require( api.Registrations.Any( r => r.Kind == "brush" && r.Name == "Rivet" ),
					"plug-in must register its brush" );
				Require( File.ReadAllText( Path.Combine( projectRoot, "out.txt" ) ) == "hello-stamped",
					"plug-in must read and write project assets" );

				var threw = false;
				try { api.WriteAssetText( "../escape.txt", "x" ); }
				catch ( InvalidOperationException ) { threw = true; }
				Require( threw, "an asset path escaping the project root must be refused" );

				var restricted = new PluginApi( projectRoot, new[] { "brush.register", "log" } );
				threw = false;
				try { restricted.RegisterMaterial( "Skin", "x" ); }
				catch ( InvalidOperationException ) { threw = true; }
				Require( threw, "a capability the manifest did not grant must be refused" );
				Require( host.Loaded.Count == 1, "host must track the loaded plug-in" );
				host.Unload( "Fake Rivet Brush" );
				Require( host.Loaded.Count == 0, "unload must drop the plug-in" );
			}
			finally { Cleanup( root ); }
		} );

		Check( "store search ranks name over tags over summary", () =>
		{
			var catalog = new AddonStoreCatalog
			{
				Entries = new List<StoreEntry>
				{
					new() { Id = "stitch", Name = "Stitch Set", Summary = "A rivet-free stitch kit.", Kind = AddonKind.Brush },
					new() { Id = "bolt", Name = "Bolt Kit", Summary = "Bolts.", Tags = new List<string> { "rivet" }, Kind = AddonKind.Brush },
					new() { Id = "rivet", Name = "Rivet Brush", Summary = "Places rivets.", Tags = new List<string> { "detail" }, Kind = AddonKind.Brush },
				},
			};
			var results = catalog.Search( "rivet" );
			Require( results.Count == 3, "all three entries mention rivet somewhere" );
			Require( results[0].Id == "rivet" && results[1].Id == "bolt" && results[2].Id == "stitch",
				"search must rank name, then tags, then summary" );
			Require( catalog.Search( "" ).Select( e => e.Id ).SequenceEqual( new[] { "bolt", "rivet", "stitch" } ),
				"a blank query must return everything in name order" );
			Require( catalog.Search( "zzz" ).Count == 0, "no match must return nothing" );
		} );

		Check( "a text scene round-trips byte-identical and a one-node change diffs to one line", () =>
		{
			var doc = new TextSceneDocument
			{
				Layers = new List<TextSceneLayer>
				{
					new() { Name = "model", Artist = "A", Department = "modeling" },
					new() { Name = "texture", Artist = "B", Department = "lookdev" },
				},
				Assets = new List<TextSceneAsset>
				{
					new() { Id = "hero", Kind = "effigy", Path = "assets/hero.effigy" },
					new() { Id = "skin", Kind = "texture", Path = "assets/skin.png" },
				},
				Nodes = new List<TextSceneNode>
				{
					new() { Id = "n1", Name = "Hero", Layer = "model" },
					new() { Id = "n2", ParentId = "n1", Name = "Head", Layer = "model" },
					new() { Id = "n3", ParentId = "n1", Name = "Skin", Layer = "texture" },
				},
				Transforms = new List<TextSceneTransform>
				{
					new() { NodeId = "n1", Position = new[] { 0f, 0f, 0f } },
					new() { NodeId = "n2", Position = new[] { 0f, 1.5f, 0f }, RotationDegrees = new[] { 0f, 90f, 0f } },
					new() { NodeId = "n3", Scale = new[] { 2f, 2f, 2f } },
				},
				UnknownLines = new List<string> { "futurefield\tfoo" },
			};
			var first = doc.ToText();
			var second = doc.ToText();
			Require( first == second, "the same document must serialize to the same bytes" );

			var parsed = TextSceneDocument.Parse( first );
			Require( parsed.Layers.Count == 2 && parsed.Assets.Count == 2
				&& parsed.Nodes.Count == 3 && parsed.Transforms.Count == 3,
				"parse must recover every record" );
			Require( parsed.Nodes.First( n => n.Id == "n2" ).ParentId == "n1",
				"parse must recover the parent link" );
			Require( parsed.UnknownLines.Count == 1 && parsed.UnknownLines[0] == "futurefield\tfoo",
				"unknown lines must survive a round-trip" );
			Require( parsed.ToText() == first, "a parse+serialize round-trip must be byte-identical" );

			doc.Nodes.First( n => n.Id == "n2" ).Name = "Head v2";
			var changed = doc.ToText();
			var changedLines = first.Split( '\n' ).Zip( changed.Split( '\n' ),
				( a, b ) => a == b ).Count( same => !same );
			Require( changedLines == 1, $"a one-node change must diff to one line, got {changedLines}" );

			var threw = false;
			try { _ = TextSceneDocument.Parse( first + "node\tn1\t-\tDupe\t-\n" ); }
			catch ( FormatException ) { threw = true; }
			Require( threw, "a duplicate node id must be rejected" );
			threw = false;
			try { _ = TextSceneDocument.Parse( "gtscn 1\nnode\tn9\tn-missing\tOrphan\t-\n" ); }
			catch ( FormatException ) { threw = true; }
			Require( threw, "a dangling parent must be rejected" );
			threw = false;
			try { _ = TextSceneDocument.Parse( "not-a-scene\n" ); }
			catch ( FormatException ) { threw = true; }
			Require( threw, "a bad header must be rejected" );
			Require( TextSceneDocument.LfsAttributes().Contains( "*.glb filter=lfs", StringComparison.Ordinal ),
				"LFS attributes must send GLBs to LFS" );
		} );

		Check( "two-artist locking prevents overwrite and a stale lock can be taken over", () =>
		{
			var root = TempDir();
			try
			{
				var asset = Path.Combine( root, "hero.effigy" );
				File.WriteAllText( asset, "binary" );
				var now = new DateTimeOffset( 2026, 11, 13, 10, 0, 0, TimeSpan.Zero );
				Func<DateTimeOffset> clock = () => now;

				var a = AssetLock.TryAcquire( asset, "A", clock: clock );
				Require( a.Acquired, "A must acquire the free lock" );
				var b = AssetLock.TryAcquire( asset, "B", clock: clock );
				Require( !b.Acquired && b.Error!.Contains( "locked by A", StringComparison.Ordinal ),
					"B must be refused while A holds a live lock" );

				now = now.AddMinutes( 5 );
				Require( AssetLock.Heartbeat( asset, "A", clock ),
					"A's heartbeat at +5 minutes must keep the lock live" );
				Require( !AssetLock.TryAcquire( asset, "B", clock: clock ).Acquired,
					"B must still be refused after A's heartbeat" );

				now = now.AddMinutes( 15 );
				var takeover = AssetLock.TryAcquire( asset, "B", clock: clock );
				Require( takeover.Acquired && takeover.PreviousOwner == "A",
					"B must take over the stale lock and name the previous owner" );
				Require( !AssetLock.Release( asset, "A" ),
					"A must not be able to release B's lock" );
				Require( AssetLock.Release( asset, "B" ), "B must be able to release" );
				Require( AssetLock.Current( asset ) is null, "no lock must remain after release" );
				Require( AssetLock.TryAcquire( asset, "C", clock: clock ).Acquired,
					"C must acquire after the release" );
			}
			finally { Cleanup( root ); }
		} );

		Check( "the turntable viewer generates a turntable page next to the GLB", () =>
		{
			var root = TempDir();
			try
			{
				File.WriteAllBytes( Path.Combine( root, "hero.glb" ), new byte[] { 0x67, 0x6c, 0x54, 0x46 } );
				var viewer = TurntableViewer.WriteViewer( root, "hero.glb", "<Rivet & Co>" );
				Require( File.Exists( viewer ), "viewer.html must be written" );
				var html = File.ReadAllText( viewer );
				Require( html.Contains( "hero.glb", StringComparison.Ordinal ),
					"the page must load the GLB" );
				Require( html.Contains( "autoRotate", StringComparison.Ordinal ),
					"the page must turntable auto-rotate" );
				Require( html.Contains( "three@0.160.0", StringComparison.Ordinal ),
					"the page must pin the three.js version" );
				Require( html.Contains( "&lt;Rivet &amp; Co&gt;", StringComparison.Ordinal ),
					"the title must be HTML-encoded" );
				Require( !html.Contains( root, StringComparison.Ordinal ),
					"the page must not leak a local filesystem path" );

				var threw = false;
				try { _ = TurntableViewer.GenerateHtml( "../secret.glb", "x" ); }
				catch ( ArgumentException ) { threw = true; }
				Require( threw, "a GLB path escaping the folder must be refused" );
				threw = false;
				try { _ = TurntableViewer.WriteViewer( root, "missing.glb", "x" ); }
				catch ( FileNotFoundException ) { threw = true; }
				Require( threw, "a missing GLB must be refused" );
			}
			finally { Cleanup( root ); }
		} );
	}
}
