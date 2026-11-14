#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

internal static class HardSurfaceKitTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		// Fixture for godlike-hard-surface-sculpt-kit-stroke-engine.
		// The kit works on PolyMesh directly and reuses the shared stroke core
		// (StrokePath) and the boolean provider (MeshBoolean); it does not touch
		// SculptSession.cs (owned by the sculpt-layers ticket).
		//
		// Not yet registered in tests/TestRegistry.cs: that file is
		// coordinator-owned and the final integrator adds the one
		// HardSurfaceKitTests.Register(registry, settings); line.
		registry.Add( "hardsurface", "dam standard cuts a sharp crease", () =>
		{
			var mesh = Primitives.Plane( 2f, 2f, 20, 20 );
			var path = new List<Vec3> { new( -1f, 0f, 0f ), new( 1f, 0f, 0f ) };
			HardSurfaceKit.DamStandard( mesh, path, 0.3f, 0.2f, 0.8f );
			var centre = Nearest( mesh, new Vec3( 0f, 0f, 0f ) );
			Assert( MathF.Abs( mesh.Positions[centre].z - (-0.2f) ) < 1e-4f,
				$"centre of the dam must drop by the full depth, z={mesh.Positions[centre].z}" );
			var edge = Nearest( mesh, new Vec3( 0f, 0.2f, 0f ) );
			Assert( mesh.Positions[edge].z < 0f && mesh.Positions[edge].z > -0.05f,
				$"falloff must soften the crease away from the path, z={mesh.Positions[edge].z}" );
			Assert( mesh.Positions[edge].y < 0.2f,
				$"pinch must pull the crease wall toward the path, y={mesh.Positions[edge].y}" );
			var far = Nearest( mesh, new Vec3( 0f, 0.8f, 0f ) );
			Assert( mesh.Positions[far].z == 0f && mesh.Positions[far].y == 0.8f,
				"vertices outside the radius must be untouched" );
		} );

		registry.Add( "hardsurface", "trim dynamic flattens bumps onto the plane", () =>
		{
			var mesh = Primitives.Plane( 2f, 2f, 20, 20 );
			for ( var i = 0; i < mesh.Positions.Count; i++ )
				if ( MathF.Abs( mesh.Positions[i].y ) <= 0.4f )
					mesh.Positions[i] = new Vec3( mesh.Positions[i].x, mesh.Positions[i].y, 0.25f );
			var path = new List<Vec3> { new( -1f, 0f, 0f ), new( 1f, 0f, 0f ) };
			HardSurfaceKit.TrimDynamic( mesh, path, new Vec3( 0f, 0f, 1f ), 0.5f, 1f );
			var centre = Nearest( mesh, new Vec3( 0f, 0f, 0f ) );
			Assert( MathF.Abs( mesh.Positions[centre].z ) < 0.01f,
				$"ridge on the stroke must flatten onto the plane, z={mesh.Positions[centre].z}" );
			var shoulder = Nearest( mesh, new Vec3( 0f, 0.4f, 0f ) );
			Assert( mesh.Positions[shoulder].z > 0.2f,
				$"falloff must leave the ridge shoulder standing, z={mesh.Positions[shoulder].z}" );
		} );

		registry.Add( "hardsurface", "trim boolean cuts a clean closed half", () =>
		{
			var box = Primitives.Box( 2f, 2f, 2f, 0 );
			Assert( MathF.Abs( box.SignedVolume() - 8f ) < 1e-4f, "box fixture must be 8 cu in" );
			var cut = HardSurfaceKit.TrimBoolean( box, new Vec3( 0f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), keepAbove: true );
			Assert( cut.Error is null, $"clean cut must not report an error: {cut.Error}" );
			Assert( cut.Mesh is not null, "clean cut must return a mesh" );
			Assert( cut.Closed, "trim boolean result must be a closed solid" );
			Assert( MathF.Abs( cut.Mesh!.SignedVolume() - 4f ) < 0.2f,
				$"keeping x>0 of a 2x2x2 box must leave ~4 cu in, got {cut.Mesh.SignedVolume()}" );
			Assert( !string.IsNullOrEmpty( cut.Route ), "trim boolean must report which engine ran" );
		} );

		registry.Add( "hardsurface", "trim boolean leaves a missed plane alone", () =>
		{
			var box = Primitives.Box( 2f, 2f, 2f, 0 );
			var cut = HardSurfaceKit.TrimBoolean( box, new Vec3( 5f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), keepAbove: true );
			Assert( cut.Error is null, $"a missed plane is not an error: {cut.Error}" );
			Assert( cut.Mesh is not null && MathF.Abs( cut.Mesh.SignedVolume() - 8f ) < 1e-4f,
				"a plane that misses the mesh must leave it unchanged" );
			Assert( cut.Route.Contains( "misses", StringComparison.Ordinal ),
				$"the route must say the plane missed, got '{cut.Route}'" );
		} );

		registry.Add( "hardsurface", "trim boolean refuses a whole-mesh cut", () =>
		{
			var box = Primitives.Box( 2f, 2f, 2f, 0 );
			var cut = HardSurfaceKit.TrimBoolean( box, new Vec3( 5f, 0f, 0f ), new Vec3( 1f, 0f, 0f ), keepAbove: false );
			Assert( cut.Mesh is null, "a cut that removes everything must return no mesh" );
			Assert( cut.Error is not null && cut.Error.Contains( "whole mesh", StringComparison.Ordinal ),
				$"refusal must say why, got '{cut.Error}'" );
		} );

		registry.Add( "hardsurface", "pressure curves shape the multiplier", () =>
		{
			Assert( MathF.Abs( HardSurfaceKit.PressureCurve.Linear.Evaluate( 0.5f ) - 0.5f ) < 1e-6f, "linear must pass through" );
			Assert( MathF.Abs( HardSurfaceKit.PressureCurve.Soft.Evaluate( 0.5f ) - 0.35f ) < 1e-6f, "soft must sit below linear mid-stroke" );
			Assert( MathF.Abs( HardSurfaceKit.PressureCurve.Hard.Evaluate( 0.5f ) - 0.65f ) < 1e-6f, "hard must sit above linear mid-stroke" );
			Assert( HardSurfaceKit.PressureCurve.Linear.Evaluate( -1f ) == 0f, "pressure below 0 must clamp to the first value" );
			Assert( HardSurfaceKit.PressureCurve.Linear.Evaluate( 2f ) == 1f, "pressure above 1 must clamp to the last value" );
			Throws( () => new HardSurfaceKit.PressureCurve( (0.5f, 0.5f) ) );
			Throws( () => new HardSurfaceKit.PressureCurve( (0f, 0f), (0.5f, 0.5f), (0.5f, 0.6f) ) );
		} );

		registry.Add( "hardsurface", "alpha library search returns named alphas", () =>
		{
			var library = new HardSurfaceKit.AlphaLibrary();
			Assert( library.Entries.Count == BrushAlpha.ProceduralNames.Length,
				$"library must hold every procedural alpha ({BrushAlpha.ProceduralNames.Length}), got {library.Entries.Count}" );
			var rivets = library.Search( "rivet" );
			Assert( rivets.Any( e => e.Name == "rivets" ), "searching 'rivet' must find the rivets alpha" );
			var hard = library.Search( "hard-surface" );
			Assert( hard.Count >= 3, $"hard-surface tag must match rivets/cracks/stitches, got {hard.Count}" );
			var cracks = library.Get( "cracks" );
			Assert( cracks is not null && cracks.Alpha.Width == 256 && cracks.Alpha.Height == 256,
				"Get('cracks') must return the 256x256 procedural stamp" );
			var sample = cracks!.Alpha.Sample( 0.5f, 0.5f );
			Assert( sample >= 0f && sample <= 1f, $"stamp samples must be 0..1, got {sample}" );
			Assert( library.Get( "nope" ) is null, "unknown alpha name must return null" );
			Assert( library.Search( "" ).Count == library.Entries.Count, "empty query must return everything" );
		} );

		registry.Add( "hardsurface", "lazy mouse smooths a jittered path", () =>
		{
			var points = new List<Vec3>();
			for ( var i = 0; i <= 20; i++ )
				points.Add( new Vec3( i * 0.1f, (i % 2 == 0 ? 0.2f : -0.2f), 0f ) );
			var lazy = StrokePath.Expand( StrokeMode.Lazy, points, 0.1f, lazyRadius: 0.5f );
			var raw = StrokePath.Expand( StrokeMode.Space, points, 0.1f );
			Assert( lazy.Count > 0, "lazy expansion must produce samples" );
			var lazyJitter = lazy.Max( s => MathF.Abs( s.Point.y ) );
			var rawJitter = raw.Max( s => MathF.Abs( s.Point.y ) );
			Assert( lazyJitter < 0.1f, $"lazy mouse must damp the jitter below 0.1, got {lazyJitter}" );
			Assert( rawJitter > 0.15f, $"the raw path must still carry its jitter, got {rawJitter}" );
		} );

		registry.Add( "hardsurface", "stroke record replays identically", () =>
		{
			var a = Primitives.Plane( 2f, 2f, 20, 20 );
			var b = Primitives.Plane( 2f, 2f, 20, 20 );
			var record = new HardSurfaceKit.StrokeRecord
			{
				Kind = HardSurfaceKit.KitStrokeKind.DamStandard,
				Path = new List<Vec3> { new( -1f, 0f, 0f ), new( 1f, 0f, 0f ) },
				Radius = 0.3f,
				Depth = 0.2f,
				Pinch = 0.8f,
			};
			HardSurfaceKit.DamStandard( a, record.Path, record.Radius, record.Depth, record.Pinch );
			record.Replay( b );
			Assert( a.Positions.Count == b.Positions.Count, "replay must keep the vertex count" );
			for ( var i = 0; i < a.Positions.Count; i++ )
				Assert( (a.Positions[i] - b.Positions[i]).Length < 1e-6f,
					$"replay must reproduce the stroke exactly (vertex {i} differs)" );
		} );

		registry.Add( "hardsurface", "lod display picks a level inside the face budget", () =>
		{
			var sculpt = new MultiresSculpt( Primitives.Plane( 2f, 2f, 4, 4 ) );
			sculpt.AddLevel();
			sculpt.AddLevel();
			Assert( sculpt.TopLevel == 2, $"fixture must have 3 levels, top={sculpt.TopLevel}" );
			var cost0 = sculpt.Cost( 0 ).Faces;
			var cost1 = sculpt.Cost( 1 ).Faces;
			var cost2 = sculpt.Cost( 2 ).Faces;
			Assert( cost0 < cost1 && cost1 < cost2, $"levels must cost more as they rise ({cost0}/{cost1}/{cost2})" );
			Assert( HardSurfaceKit.PickDisplayLevel( sculpt, cost0 ) == 0, "budget for the cage only must pick level 0" );
			Assert( HardSurfaceKit.PickDisplayLevel( sculpt, cost1 ) == 1, "budget for level 1 must pick level 1" );
			Assert( HardSurfaceKit.PickDisplayLevel( sculpt, cost2 ) == 2, "budget for the top must pick the top" );
			Assert( HardSurfaceKit.PickDisplayLevel( sculpt, cost1 - 1 ) == 0, "one face under level 1 must drop to level 0" );
			var shown = HardSurfaceKit.ApplyDisplayLevel( sculpt, cost1 );
			Assert( shown == 1 && sculpt.ViewLevel == 1, "applying a level must move ViewLevel" );
			Assert( sculpt.Display().FaceCount <= cost1, "the displayed mesh must fit the budget" );
			sculpt.ViewLevel = sculpt.TopLevel;
		} );
	}

	static int Nearest( PolyMesh mesh, Vec3 target )
	{
		var best = 0;
		var bestDist = float.MaxValue;
		for ( var i = 0; i < mesh.Positions.Count; i++ )
		{
			var d = (mesh.Positions[i] - target).Length;
			if ( d < bestDist )
			{
				bestDist = d;
				best = i;
			}
		}
		return best;
	}

	static void Assert( bool condition, string message )
	{
		if ( !condition ) throw new Exception( message );
	}

	static void Throws( Action action )
	{
		try { action(); }
		catch ( Exception ) { return; }
		throw new Exception( "expected an error, got silent success" );
	}
}
