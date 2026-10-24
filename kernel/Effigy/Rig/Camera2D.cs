using System;
using System.Collections.Generic;

/// <summary>
/// Geppetto 2D animation suite, lane 6: a flat 2D camera, separate from the
/// 3D camera track and from <see cref="DmxAnimWriter"/>.
/// <para>
/// The camera is a pan / zoom / rotate rig over a 2D scene: <see cref="Camera2DState"/>
/// carries the scene point at the centre of the view, the magnification, and the
/// view rotation in degrees. Positive rotation is counter-clockwise in a y-up
/// scene; a y-down renderer flips the sign on its side, this model does not.
/// </para>
/// <para>
/// Keys are evaluated exactly the way <see cref="RigTrack"/> evaluates: held
/// before the first key and after the last, and between two keys eased by the
/// FIRST of them through <see cref="RigKey.Apply"/> — so a segment's ease is
/// the outgoing key's ease, Stepped holds the first key until the next key
/// lands, and Linear is a straight lerp. Cubic and Monotone do NOT route
/// through <see cref="RigHermite"/> here (that solver curves a 10-channel 3D
/// pose); for this camera they fall back to linear, which <see cref="RigKey.Apply"/>
/// already returns for them. Endpoints are exact for every ease mode.
/// </para>
/// <para>
/// MULTIPLANE. Each layer carries a <see cref="Camera2DLayer.Depth"/> in
/// [0, 1]: 0 is the camera plane (moves fully with the camera), 1 is
/// infinitely far (never moves with it). The parallax factor is
/// 1 - Depth, and it scales pan, zoom-about-1 and rotation alike, so a
/// background layer at depth 0.5 moves exactly half as far as the foreground
/// when the camera pans, zooms half as much, and rotates half as far.
/// Depth outside [0, 1] saturates at the ends instead of erroring, so an
/// authored layer can never invert or explode a render.
/// </para>
/// <para>
/// This file is the camera MODEL and its evaluation only. It draws nothing:
/// the rendering consumer (another lane) takes <see cref="Camera2DState"/> /
/// <see cref="Camera2DLayerState"/> and maps sprite/frame points through
/// <see cref="Camera2DState.Transform"/> / <see cref="Camera2DLayerState.Transform"/>.
/// It is deliberately NOT a <see cref="RigTrackKind.Camera"/> track — that
/// track is a 3D shot whose key scale carries field of view — and there is
/// no conversion between the two, so a 2D camera can never silently become
/// a 3D shot or vice versa.
/// </para>
/// </summary>
namespace Effigy;

/// <summary>One 2D camera key: the view at <see cref="Frame"/>.</summary>
public sealed class Camera2DKey
{
    public int Frame;
    public float X, Y;
    public float Zoom = 1f;
    public float RotationDegrees;
    /// <summary>Governs the segment LEAVING this key, as in <see cref="RigKey"/>.</summary>
    public RigEase Ease = RigEase.Smooth;
}

/// <summary>The evaluated 2D view at one frame: scene centre, magnification, rotation.</summary>
public readonly record struct Camera2DState( float X, float Y, float Zoom, float RotationDegrees )
{
    public static readonly Camera2DState Identity = new( 0f, 0f, 1f, 0f );

    /// <summary>
    /// Maps a scene point into view space about <paramref name="viewportX"/> /
    /// <paramref name="viewportY"/> (the screen centre, in the consumer's units):
    /// translate by the camera centre, rotate by -rotation, magnify by zoom.
    /// </summary>
    public (float X, float Y) Transform( float worldX, float worldY, float viewportX = 0f, float viewportY = 0f )
    {
        var dx = worldX - X;
        var dy = worldY - Y;
        var radians = -RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos( radians );
        var sin = MathF.Sin( radians );
        return (
            viewportX + (dx * cos - dy * sin) * Zoom,
            viewportY + (dx * sin + dy * cos) * Zoom );
    }

    /// <summary>Inverse of <see cref="Transform"/>: view point back to a scene point.</summary>
    public (float X, float Y) InverseTransform( float viewX, float viewY, float viewportX = 0f, float viewportY = 0f )
    {
        var dx = (viewX - viewportX) / Zoom;
        var dy = (viewY - viewportY) / Zoom;
        var radians = RotationDegrees * MathF.PI / 180f;
        var cos = MathF.Cos( radians );
        var sin = MathF.Sin( radians );
        return (
            X + dx * cos - dy * sin,
            Y + dx * sin + dy * cos );
    }
}

/// <summary>A multiplane layer: a name and a depth. Depth 0 = camera plane, 1 = infinity.</summary>
public sealed class Camera2DLayer
{
    public string Name = "";
    public float Depth;
    /// <summary>How much of the camera's motion this layer follows: 1 - Depth, clamped to [0, 1].</summary>
    public float ParallaxFactor => Math.Clamp( 1f - Depth, 0f, 1f );
}

/// <summary>The evaluated view for one multiplane layer at one frame.</summary>
public readonly record struct Camera2DLayerState( string Layer, float ParallaxFactor, Camera2DState Camera )
{
    /// <summary>Maps a scene point on this layer into view space, as <see cref="Camera2DState.Transform"/>.</summary>
    public (float X, float Y) Transform( float worldX, float worldY, float viewportX = 0f, float viewportY = 0f ) =>
        Camera.Transform( worldX, worldY, viewportX, viewportY );
}

/// <summary>
/// A keyed 2D camera: pan / zoom / rotate keys plus a multiplane layer list.
/// Keys stay sorted by frame; adding a key at an existing frame replaces it,
/// so a re-authored frame is never double-keyed. Zoom must stay positive and
/// all key values finite, or <see cref="AddKey"/> throws instead of letting a
/// later render divide by zero or smear NaNs across a frame.
/// </summary>
public sealed class Camera2D
{
    public List<Camera2DKey> Keys = new();
    public List<Camera2DLayer> Layers = new();

    public void AddKey( Camera2DKey key )
    {
        ArgumentNullException.ThrowIfNull( key );
        if ( !float.IsFinite( key.X ) || !float.IsFinite( key.Y ) ||
             !float.IsFinite( key.Zoom ) || !float.IsFinite( key.RotationDegrees ) )
            throw new ArgumentException( "Camera2D key values must be finite." );
        if ( key.Zoom <= 0f )
            throw new ArgumentException( "Camera2D zoom must be positive." );
        var at = Keys.FindIndex( k => k.Frame == key.Frame );
        if ( at >= 0 ) { Keys[at] = key; return; }
        var insert = Keys.FindIndex( k => k.Frame > key.Frame );
        if ( insert < 0 ) Keys.Add( key );
        else Keys.Insert( insert, key );
    }

    public void AddLayer( Camera2DLayer layer )
    {
        ArgumentNullException.ThrowIfNull( layer );
        if ( string.IsNullOrWhiteSpace( layer.Name ) )
            throw new ArgumentException( "Camera2D layer needs a name." );
        if ( !float.IsFinite( layer.Depth ) )
            throw new ArgumentException( "Camera2D layer depth must be finite." );
        if ( Layers.Exists( l => l.Name == layer.Name ) )
            throw new ArgumentException( $"Camera2D layer '{layer.Name}' already exists." );
        Layers.Add( layer );
    }

    /// <summary>The view at a (possibly fractional) frame. No keys means the identity view.</summary>
    public Camera2DState Evaluate( float frame )
    {
        if ( Keys.Count == 0 )
            return Camera2DState.Identity;
        if ( frame <= Keys[0].Frame )
            return StateOf( Keys[0] );
        if ( frame >= Keys[^1].Frame )
            return StateOf( Keys[^1] );
        for ( var i = 0; i < Keys.Count - 1; i++ )
        {
            var a = Keys[i];
            var b = Keys[i + 1];
            if ( frame < a.Frame || frame > b.Frame )
                continue;
            var span = b.Frame - a.Frame;
            var t = span > 0 ? (frame - a.Frame) / span : 0f;
            var eased = RigKey.Apply( a.Ease, t );
            return new Camera2DState(
                Lerp( a.X, b.X, eased ),
                Lerp( a.Y, b.Y, eased ),
                Lerp( a.Zoom, b.Zoom, eased ),
                Lerp( a.RotationDegrees, b.RotationDegrees, eased ) );
        }
        return StateOf( Keys[^1] );
    }

    /// <summary>The view for one layer at a frame: pan, zoom-about-1 and rotation all scaled by the layer's parallax factor.</summary>
    public Camera2DLayerState EvaluateLayer( float frame, string layerName )
    {
        var layer = Layers.Find( l => l.Name == layerName )
            ?? throw new ArgumentException( $"No Camera2D layer named '{layerName}'." );
        var f = layer.ParallaxFactor;
        var state = Evaluate( frame );
        return new Camera2DLayerState( layer.Name, f, new Camera2DState(
            state.X * f,
            state.Y * f,
            1f + (state.Zoom - 1f) * f,
            state.RotationDegrees * f ) );
    }

    /// <summary>The view for every layer at a frame, in layer order.</summary>
    public List<Camera2DLayerState> EvaluateLayers( float frame )
    {
        var states = new List<Camera2DLayerState>( Layers.Count );
        foreach ( var layer in Layers )
            states.Add( EvaluateLayer( frame, layer.Name ) );
        return states;
    }

    /// <summary>One evaluated view per integer frame from <paramref name="firstFrame"/> to <paramref name="lastFrame"/> inclusive.</summary>
    public Camera2DState[] SampleRange( int firstFrame, int lastFrame )
    {
        if ( lastFrame < firstFrame )
            throw new ArgumentException( "SampleRange last frame must not precede first frame." );
        var states = new Camera2DState[lastFrame - firstFrame + 1];
        for ( var f = firstFrame; f <= lastFrame; f++ )
            states[f - firstFrame] = Evaluate( f );
        return states;
    }

    static Camera2DState StateOf( Camera2DKey key ) =>
        new( key.X, key.Y, key.Zoom, key.RotationDegrees );

    static float Lerp( float a, float b, float t ) => a + (b - a) * t;
}
