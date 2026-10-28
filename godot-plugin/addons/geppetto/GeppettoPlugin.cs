#if TOOLS
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Runtime.Loader;
using Effigy;
using Godot;

namespace Geppetto;

[Tool]
public partial class GeppettoPlugin : EditorPlugin, ISerializationListener
{
	const string Marker = "m17978";
	Godot.Timer _nudge;
	static PartStudio _held;                       // a kernel object parked in a static, like a cache would be
	static CancellationTokenSource _cts;
	private EffigySceneImporter _sceneImporter;

	public void OnBeforeSerialize()
	{
		GD.Print( $"[G1] OnBeforeSerialize marker={Marker}" );
		if ( System.Environment.GetEnvironmentVariable( "G1_BG" ) == "clean" ) _cts?.Cancel();
		_held = null;
	}

	public void OnAfterDeserialize()
	{
		var kernels = AppDomain.CurrentDomain.GetAssemblies().Count( a => a.GetName().Name == "Effigy.Kernel" );
		GD.Print( $"[G1] OnAfterDeserialize marker={Marker} kernelAssembliesLoaded={kernels} ws={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB statics-survived={_held != null}" );
	}

	static void StartBackground( string doc )
	{
		_cts = new CancellationTokenSource();
		var token = _cts.Token;
		Task.Run( () =>
		{
			StudioCache.Enabled = false;
			var n = 0;
			while ( !token.IsCancellationRequested )
			{
				var st = StudioDocument.ReadFile( doc ); st.Rebuild();
				if ( ++n % 5 == 0 ) GD.Print( $"[G1] bg rebuild #{n}" );
			}
			GD.Print( "[G1] bg worker stopped" );
		} );
	}

	public override void _ExitTree()
	{
		GD.Print( $"[G1] exit marker={Marker}" );
		_nudge?.QueueFree();
		if ( _sceneImporter != null )
		{
			RemoveSceneFormatImporterPlugin( _sceneImporter );
			_sceneImporter = null;
		}
	}

	public override void _EnterTree()
	{
		_sceneImporter = new EffigySceneImporter();
		AddSceneFormatImporterPlugin( _sceneImporter );

		var kernels = AppDomain.CurrentDomain.GetAssemblies().Count( a => a.GetName().Name == "Effigy.Kernel" );
		GD.Print( $"[G1] enter marker={Marker} kernelAssembliesLoaded={kernels} ALCs={AssemblyLoadContext.All.Count()} ws={Process.GetCurrentProcess().WorkingSet64 / 1048576}MB" );
		if ( System.Environment.GetEnvironmentVariable( "G1_NUDGE" ) == "1" )
		{
			_nudge = new Godot.Timer { WaitTime = 1.0, Autostart = true };
			_nudge.Timeout += () => GetTree().Root.PropagateNotification( (int)Node.NotificationWMWindowFocusIn );
			AddChild( _nudge );
		}
		var alc = AssemblyLoadContext.GetLoadContext( typeof( PartStudio ).Assembly );
		GD.Print( $"[G1] enter. kernel={typeof( PartStudio ).Assembly.Location}" );
		GD.Print( $"[G1] alc={alc?.Name} collectible={alc?.IsCollectible} runtime={System.Environment.Version} mvid={StudioCache.KernelStamp()}" );

		// The G1 spike's load/rebuild probe only runs when asked for (G1_PROBE=1), so the
		// plugin loads quietly in normal use.
		if ( System.Environment.GetEnvironmentVariable( "G1_PROBE" ) != "1" ) return;

		var doc = System.Environment.GetEnvironmentVariable( "G1_DOC" );
		if ( string.IsNullOrEmpty( doc ) )
			doc = ProjectSettings.GlobalizePath( "res://spike/g1-spike.effigy" );

		var bg = System.Environment.GetEnvironmentVariable( "G1_BG" );
		if ( bg == "leak" || bg == "clean" ) StartBackground( doc );
		_held = StudioCache.Load( doc );
		try { Probe( doc ); }
		catch ( Exception e ) { GD.PushError( $"[G1] failed: {e}" ); }
	}

	static void Probe( string doc )
	{
		var sw = Stopwatch.StartNew();
		var ws0 = Process.GetCurrentProcess().WorkingSet64 / 1048576;
		var heap0 = GC.GetTotalMemory( true ) / 1048576;

		StudioCache.Enabled = false;                   // guaranteed cold
		sw.Restart();
		var s = StudioDocument.ReadFile( doc );
		var parseMs = sw.Elapsed.TotalMilliseconds;
		var report = s.Rebuild();
		var coldMs = sw.Elapsed.TotalMilliseconds;
		GD.Print( $"[G1] COLD  {coldMs,9:F1} ms (parse {parseMs:F1}) errors={report.Errors.Count} warnings={report.Warnings.Count} {Counts( s )}" );

		StudioCache.Enabled = true;
		StudioCache.Delete( doc );
		for ( var i = 1; i <= 3; i++ )
		{
			sw.Restart();
			var w = StudioCache.Load( doc, out var note );
			GD.Print( $"[G1] LOAD{i} {sw.Elapsed.TotalMilliseconds,9:F1} ms {Counts( w )} note=[{note}]" );
		}

		GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
		var ws1 = Process.GetCurrentProcess().WorkingSet64 / 1048576;
		var heap1 = GC.GetTotalMemory( true ) / 1048576;
		GD.Print( $"[G1] MEM workingSet {ws0}->{ws1} MB, managed {heap0}->{heap1} MB (studio still referenced: {s.Bodies.Count} bodies)" );
	}

	static string Counts( PartStudio s ) =>
		$"bodies={s.Bodies.Count} verts={s.Bodies.Sum( b => b.Mesh?.VertexCount ?? 0 )} bones={s.Rig.Count}";
}
#endif
