using System;
using Effigy;
using Marionette.Desktop;

/// <summary>
/// Geppetto 2D animation suite, lane 6 tests: Camera2D pan / zoom / rotate
/// evaluation and multiplane parallax ratios. No rendering, no fixtures:
/// every check is arithmetic on the camera model itself.
/// Registration line for the coordinator sweep (NOT added here):
/// Camera2DTests.Register( registry, settings );
/// </summary>
internal static class Camera2DTests
{
    public static void Register( TestRegistry registry, TestSettings settings )
    {
        void Check( string name, Action test ) => registry.Add( "camera2d", name, test );
        void Require( bool condition, string message = "assertion failed" )
        {
            if ( !condition ) throw new Exception( message );
        }
        void Near( float actual, float expected, string message, float tolerance = 1e-4f )
        {
            Require( MathF.Abs( actual - expected ) <= tolerance, $"{message}: got {actual}, want {expected}" );
        }
        void Throws( Action action, string message = "expected an error, got silent success" )
        {
            try { action(); }
            catch ( Exception ) { return; }
            throw new Exception( message );
        }

        Camera2D PanCamera()
        {
            var camera = new Camera2D();
            camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Y = 0, Zoom = 1, RotationDegrees = 0, Ease = RigEase.Linear } );
            camera.AddKey( new Camera2DKey { Frame = 30, X = 300, Y = 60, Zoom = 3, RotationDegrees = 90, Ease = RigEase.Linear } );
            return camera;
        }

        Check( "empty camera is the identity view", () =>
        {
            var state = new Camera2D().Evaluate( 12 );
            Near( state.X, 0, "empty x" ); Near( state.Y, 0, "empty y" );
            Near( state.Zoom, 1, "empty zoom" ); Near( state.RotationDegrees, 0, "empty rotation" );
        } );

        Check( "view holds before the first key and after the last", () =>
        {
            var state = PanCamera();
            Near( state.Evaluate( -5 ).X, 0, "before first" );
            Near( state.Evaluate( 99 ).X, 300, "after last" );
            Near( state.Evaluate( 99 ).Zoom, 3, "after last zoom" );
        } );

        Check( "linear pan midpoint is the exact midpoint", () =>
        {
            var state = PanCamera().Evaluate( 15 );
            Near( state.X, 150, "linear pan x" ); Near( state.Y, 30, "linear pan y" );
        } );

        Check( "linear zoom and rotation midpoints are exact", () =>
        {
            var state = PanCamera().Evaluate( 15 );
            Near( state.Zoom, 2, "linear zoom" );
            Near( state.RotationDegrees, 45, "linear rotation" );
        } );

        Check( "smooth ease at quarter span is 0.125, not linear 0.25", () =>
        {
            var camera = new Camera2D();
            camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Ease = RigEase.Smooth } );
            camera.AddKey( new Camera2DKey { Frame = 30, X = 300, Ease = RigEase.Smooth } );
            Near( camera.Evaluate( 7.5f ).X, 37.5f, "smooth quarter" );
        } );

        Check( "ease-in at quarter span is t squared", () =>
        {
            var camera = new Camera2D();
            camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Ease = RigEase.EaseIn } );
            camera.AddKey( new Camera2DKey { Frame = 30, X = 300, Ease = RigEase.EaseIn } );
            Near( camera.Evaluate( 7.5f ).X, 18.75f, "ease-in quarter" );
        } );

        Check( "ease-out at quarter span is 1 minus (1 minus t) squared", () =>
        {
            var camera = new Camera2D();
            camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Ease = RigEase.EaseOut } );
            camera.AddKey( new Camera2DKey { Frame = 30, X = 300, Ease = RigEase.EaseOut } );
            Near( camera.Evaluate( 7.5f ).X, 131.25f, "ease-out quarter" );
        } );

        Check( "stepped holds the first key until the next key lands", () =>
        {
            var camera = new Camera2D();
            camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Ease = RigEase.Stepped } );
            camera.AddKey( new Camera2DKey { Frame = 30, X = 300, Ease = RigEase.Stepped } );
            Near( camera.Evaluate( 29 ).X, 0, "stepped holds" );
            Near( camera.Evaluate( 30 ).X, 300, "stepped lands" );
        } );

        Check( "segment ease comes from the outgoing key", () =>
        {
            var camera = new Camera2D();
            camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Ease = RigEase.Linear } );
            camera.AddKey( new Camera2DKey { Frame = 10, X = 100, Ease = RigEase.Stepped } );
            camera.AddKey( new Camera2DKey { Frame = 20, X = 200, Ease = RigEase.Linear } );
            Near( camera.Evaluate( 5 ).X, 50, "first segment uses first key ease" );
            Near( camera.Evaluate( 15 ).X, 100, "second segment uses second key ease" );
        } );

        Check( "keys added out of order still evaluate in frame order", () =>
        {
            var camera = new Camera2D();
            camera.AddKey( new Camera2DKey { Frame = 30, X = 300, Ease = RigEase.Linear } );
            camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Ease = RigEase.Linear } );
            Near( camera.Evaluate( 15 ).X, 150, "sorted evaluation" );
        } );

        Check( "re-keying a frame replaces it instead of double-keying", () =>
        {
            var camera = new Camera2D();
            camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Ease = RigEase.Linear } );
            camera.AddKey( new Camera2DKey { Frame = 0, X = 50, Ease = RigEase.Linear } );
            camera.AddKey( new Camera2DKey { Frame = 30, X = 300, Ease = RigEase.Linear } );
            Near( camera.Evaluate( 0 ).X, 50, "replacement wins" );
            Require( camera.Keys.Count == 2, "duplicate frame left two keys" );
        } );

        Check( "cubic and monotone keys evaluate with exact endpoints", () =>
        {
            foreach ( var ease in new[] { RigEase.Cubic, RigEase.Monotone } )
            {
                var camera = new Camera2D();
                camera.AddKey( new Camera2DKey { Frame = 0, X = 0, Ease = ease } );
                camera.AddKey( new Camera2DKey { Frame = 30, X = 300, Ease = ease } );
                Near( camera.Evaluate( 0 ).X, 0, ease + " start" );
                Near( camera.Evaluate( 30 ).X, 300, ease + " end" );
                var mid = camera.Evaluate( 15 ).X;
                Require( mid >= 0 && mid <= 300, ease + " midpoint out of range: " + mid );
            }
        } );

        Check( "invalid keys are rejected, not rendered", () =>
        {
            Throws( () => new Camera2D().AddKey( new Camera2DKey { Frame = 0, Zoom = 0 } ), "zero zoom accepted" );
            Throws( () => new Camera2D().AddKey( new Camera2DKey { Frame = 0, Zoom = -2 } ), "negative zoom accepted" );
            Throws( () => new Camera2D().AddKey( new Camera2DKey { Frame = 0, X = float.NaN } ), "NaN pan accepted" );
            Throws( () => new Camera2D().AddKey( null! ), "null key accepted" );
        } );

        Check( "pan and zoom move a point as expected", () =>
        {
            var state = new Camera2DState( 10, 20, 2, 0 );
            var (x, y) = state.Transform( 15, 20 );
            Near( x, 10, "pan/zoom x" ); Near( y, 0, "pan/zoom y" );
        } );

        Check( "rotation turns a point and inverts back exactly", () =>
        {
            var state = new Camera2DState( 0, 0, 1, 90 );
            var (x, y) = state.Transform( 1, 0 );
            Near( x, 0, "rotated x" ); Near( y, -1, "rotated y" );
            var (wx, wy) = state.InverseTransform( x, y );
            Near( wx, 1, "inverse x" ); Near( wy, 0, "inverse y" );
        } );

        Check( "transform round-trips for a panned zoomed rotated view", () =>
        {
            var state = PanCamera().Evaluate( 15 );
            var (sx, sy) = state.Transform( 40, -30, 400, 300 );
            var (wx, wy) = state.InverseTransform( sx, sy, 400, 300 );
            Near( wx, 40, "round-trip x", 1e-3f ); Near( wy, -30, "round-trip y", 1e-3f );
        } );

        Check( "multiplane parallax factors are 1 minus depth", () =>
        {
            var camera = PanCamera();
            camera.AddLayer( new Camera2DLayer { Name = "fg", Depth = 0 } );
            camera.AddLayer( new Camera2DLayer { Name = "mid", Depth = 0.5f } );
            camera.AddLayer( new Camera2DLayer { Name = "bg", Depth = 1 } );
            var layers = camera.EvaluateLayers( 30 );
            Near( layers[0].ParallaxFactor, 1, "fg factor" );
            Near( layers[1].ParallaxFactor, 0.5f, "mid factor" );
            Near( layers[2].ParallaxFactor, 0, "bg factor" );
        } );

        Check( "multiplane pan zoom rotation all scale by the same factor", () =>
        {
            var camera = PanCamera();
            camera.AddLayer( new Camera2DLayer { Name = "fg", Depth = 0 } );
            camera.AddLayer( new Camera2DLayer { Name = "mid", Depth = 0.5f } );
            camera.AddLayer( new Camera2DLayer { Name = "bg", Depth = 1 } );
            var layers = camera.EvaluateLayers( 30 );
            Near( layers[0].Camera.X, 300, "fg pan" );
            Near( layers[1].Camera.X, 150, "mid pan is half" );
            Near( layers[2].Camera.X, 0, "bg pan is zero" );
            Near( layers[0].Camera.Zoom, 3, "fg zoom" );
            Near( layers[1].Camera.Zoom, 2, "mid zoom is half-way to 1" );
            Near( layers[2].Camera.Zoom, 1, "bg zoom is 1" );
            Near( layers[0].Camera.RotationDegrees, 90, "fg rotation" );
            Near( layers[1].Camera.RotationDegrees, 45, "mid rotation is half" );
            Near( layers[2].Camera.RotationDegrees, 0, "bg rotation is zero" );
        } );

        Check( "background layer points ignore camera pan entirely", () =>
        {
            var camera = PanCamera();
            camera.AddLayer( new Camera2DLayer { Name = "bg", Depth = 1 } );
            camera.AddLayer( new Camera2DLayer { Name = "fg", Depth = 0 } );
            var bg = camera.EvaluateLayer( 30, "bg" );
            var fg = camera.EvaluateLayer( 30, "fg" );
            var (bx, by) = bg.Transform( 50, 50 );
            var (fx, fy) = fg.Transform( 50, 50 );
            Near( bx, 50, "bg point unmoved by pan" );
            Near( by, 50, "bg point unmoved by pan y" );
            // fg at frame 30: centre (300,60), zoom 3, rotation 90, so (50,50)
            // -> dx=-250, dy=-10 -> rotated by -90 -> (-10,250) -> zoomed (-30,750).
            Near( fx, -30, "fg point panned, rotated and zoomed x" );
            Near( fy, 750, "fg point panned, rotated and zoomed y" );
        } );

        Check( "layer rules are enforced", () =>
        {
            var camera = new Camera2D();
            Throws( () => camera.AddLayer( new Camera2DLayer { Name = "", Depth = 0 } ), "unnamed layer accepted" );
            camera.AddLayer( new Camera2DLayer { Name = "bg", Depth = 1 } );
            Throws( () => camera.AddLayer( new Camera2DLayer { Name = "bg", Depth = 0 } ), "duplicate layer accepted" );
            Throws( () => camera.AddLayer( new Camera2DLayer { Name = "x", Depth = float.NaN } ), "NaN depth accepted" );
            Throws( () => camera.EvaluateLayer( 0, "missing" ), "unknown layer evaluated" );
        } );

        Check( "depth outside range saturates instead of inverting", () =>
        {
            var camera = new Camera2D();
            camera.AddLayer( new Camera2DLayer { Name = "near", Depth = -1 } );
            camera.AddLayer( new Camera2DLayer { Name = "far", Depth = 2 } );
            Near( camera.Layers[0].ParallaxFactor, 1, "below-zero depth saturates to 1" );
            Near( camera.Layers[1].ParallaxFactor, 0, "above-one depth saturates to 0" );
        } );

        Check( "sample range covers a frame sequence end to end", () =>
        {
            var frames = PanCamera().SampleRange( 0, 30 );
            Require( frames.Length == 31, "sample length" );
            Near( frames[0].X, 0, "first sampled frame" );
            Near( frames[15].X, 150, "middle sampled frame" );
            Near( frames[30].X, 300, "last sampled frame" );
            for ( var i = 1; i < frames.Length; i++ )
                Require( frames[i].X >= frames[i - 1].X, "linear pan sequence must not go backwards" );
            Throws( () => PanCamera().SampleRange( 30, 0 ), "reversed sample range accepted" );
        } );
    }
}
