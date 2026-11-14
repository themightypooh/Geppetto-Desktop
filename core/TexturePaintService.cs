using System;
using System.Collections.Generic;

namespace Marionette.Desktop;

/// <summary>
/// Paints directly onto textures for .effigy documents, in texture (texel) space.
///
/// TWO APIS, KEPT SEPARATE ON PURPOSE. The legacy <see cref="Image"/>/<see cref="Brush"/>/
/// <see cref="Paint"/> API paints a standalone RGBA image and is unchanged from the stub this
/// file started as (old TexturePaintTests depend on its exact blend). The <see cref="TextureBrush"/>
/// / <see cref="Session"/> API paints a kernel <see cref="Effigy.PaintCanvas"/> — the same canvas
/// type <see cref="DocumentSession"/> reads when it swaps live paint textures into a snapshot —
/// on any surface channel (colour, roughness, metalness, height, emissive, opacity, normal).
///
/// HOW THIS FITS THE LIVE-PAINT SWAP (read, do not re-implement). DocumentSession.BuildSnapshot
/// maps each visible body's <c>body.Mesh.Paint</c> canvas by material slot and turns it into the
/// same images the .fbx export writes: opaque colour, roughness, metalness, and a height-derived
/// normal map, with descriptor factors set to 1/white because the maps already hold absolute
/// values. <see cref="UploadSet"/> builds exactly that set — same
/// <c>PaintLayers.Opaque</c> / <c>PaintTextureSet.HeightToNormal</c> calls, same null rules
/// (no normal without a height map, no rough/metal without their maps) — so a viewport uploader
/// can push a stroke's dirty rect live using the same images the snapshot path would produce,
/// instead of inventing a second texture conversion.
///
/// THE DURABILITY TRAP, STATED PLAINLY -- AND THE DURABLE PATH OUT OF IT. A
/// PaintFeature canvas is a DERIVED artifact: it is replayed from the feature stroke
/// log on every rebuild, and a direct texel scrub is undone by the next rebuild (the
/// kernel says this itself, on PaintStroke.Erase). So painting onto body.Mesh.Paint
/// through this service is PREVIEW-ONLY: it shows in the viewport, but it is not saved
/// with the document and it does not survive the next PaintFeature rebuild. Two further
/// consequences callers must know:
///   1. DocumentSession paint fingerprint (PaintFeature.Revision + canvas identity) does
///      NOT move when texels are scrubbed directly, so TryPromoteSnapshot will happily
///      keep serving the pre-stroke snapshot. After a direct-canvas stroke the caller must
///      build a fresh snapshot explicitly (or push the session dirty rect through the GL
///      uploader), not wait for staleness to notice.
///   2. The durable path for a TEXEL stroke is the kernel texel log, not a mesh stroke:
///      Session.ToTexelStroke turns a session stroke into an Effigy.TexelStroke, and
///      PaintFeature.AddTexelStroke records it (which bumps Revision, rebuilds the canvas
///      with the stroke replayed on top of the mesh strokes, and moves the fingerprint, so
///      the normal snapshot path refreshes). The ServeMode texpaint command and the
///      texpaint script verb are the callers that do this. This service deliberately does
///      NOT fabricate mesh PaintStrokes from texels: a mesh stroke path is 3D surface points
///      with normals, and inventing those from texels would paint the wrong faces.
/// WHY SERVEMODE DOES NOT CALL ToTexelStroke (wave-5 papercut decision: documented,
/// not routed). ServeMode texpaint and the script texpaint verb build their TexelStroke
/// directly from parsed command args (path points + channel/color/radius/strength/
/// falloff/spacing/erase) -- no TexturePaintService.Session exists on those paths, and
/// constructing one would need a throwaway PaintCanvas plus dab-stream semantics that
/// do not match a parsed path, for zero benefit: the durable commit already happens
/// through PaintFeature.AddTexelStroke on both paths. ToTexelStroke is the bridge for
/// LIVE-DABBED Session strokes (a caller that dabbed onto a canvas and now wants the
/// stroke in the layer log); it is exercised by TexturePaintTests today and stays
/// available for future live-paint callers. Do not 'fix' its zero production callers
/// by inventing a Session on the command path.
/// A caller that wants strokes to persist commits them through AddTexelStroke; a caller that
/// wants a live texture preview (or owns a standalone canvas outright) uses this service.
/// Undo restores write bytes straight into the canvas buffer, which does not touch the
/// canvas own dirty flags: the session therefore reports its own dirty rect
/// (Session.DirtyRect) for the uploader to use after an undo/redo.
/// </summary>
public static class TexturePaintService
{
	// ------------------------------------------------------------------
	// Legacy standalone-image API. Behaviour unchanged from the stub.
	// ------------------------------------------------------------------

	public sealed class Image { public int W, H; public byte[] Rgba; public Image( int w, int h ){ W=w; H=h; Rgba=new byte[w*h*4]; for(int i=0;i<Rgba.Length;i+=4){Rgba[i]=255;Rgba[i+1]=255;Rgba[i+2]=255;Rgba[i+3]=255;} } }

	public sealed class Brush { public float X, Y, Radius=10f, Strength=1f; public byte R=255, G=0, B=0, A=255; }

	public static void Paint( Image img, Brush b )
	{
		if ( img is null ) throw new ArgumentNullException( nameof( img ) );
		if ( b is null ) throw new ArgumentNullException( nameof( b ) );
		if ( b.Radius <= 0 ) throw new ArgumentException( "radius must be positive" );
		int r = ( int ) MathF.Ceiling( b.Radius );
		for ( int y = -r; y <= r; y++ )
		for ( int x = -r; x <= r; x++ )
		{
			int px = ( int ) b.X + x;
			int py = ( int ) b.Y + y;
			if ( px < 0 || py < 0 || px >= img.W || py >= img.H ) continue;
			float d = MathF.Sqrt( x * x + y * y );
			if ( d > b.Radius ) continue;
			float t = 1f - d / b.Radius;
			float w = t * b.Strength;
			if ( w > 1f ) w = 1f;
			int idx = ( py * img.W + px ) * 4;
			img.Rgba[ idx ] = ( byte )( img.Rgba[ idx ] * ( 1 - w ) + b.R * w );
			img.Rgba[ idx + 1 ] = ( byte )( img.Rgba[ idx + 1 ] * ( 1 - w ) + b.G * w );
			img.Rgba[ idx + 2 ] = ( byte )( img.Rgba[ idx + 2 ] * ( 1 - w ) + b.B * w );
			img.Rgba[ idx + 3 ] = ( byte )( img.Rgba[ idx + 3 ] * ( 1 - w ) + b.A * w );
		}
	}

	// ------------------------------------------------------------------
	// Texture-space brush for a PaintCanvas.
	// ------------------------------------------------------------------

	/// <summary>
	/// A brush dab in texel space. For scalar channels (roughness, metalness, height) only
	/// <see cref="R"/> is read, as the value — the same convention the kernel's stroke log uses.
	/// </summary>
	public sealed class TextureBrush
	{
		public float X, Y;
		public float Radius = 10f;
		public float Strength = 1f;
		public byte R = 255, G = 0, B = 0;
		public bool Erase;
		public Effigy.BrushFalloff Falloff = Effigy.BrushFalloff.Smooth;
		/// <summary>Distance between dabs along a stroke, as a fraction of <see cref="Radius"/>.</summary>
		public float Spacing = 0.5f;
		public Effigy.PaintChannel Channel = Effigy.PaintChannel.Color;
	}

	/// <summary>
	/// The channel canvas a brush writes: the colour canvas itself, or a companion map.
	/// A companion map is created on demand (empty, so it bakes over its base value until
	/// painted). Mask is refused: it is a layer mask, not a surface channel, and never leaves
	/// the layer — painting it as a texture would be a category error, not a texture.
	/// </summary>
	public static Effigy.PaintCanvas ResolveChannel( Effigy.PaintCanvas canvas, Effigy.PaintChannel channel )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		switch ( channel )
		{
			case Effigy.PaintChannel.Color: return canvas;
			case Effigy.PaintChannel.Roughness:
				return canvas.RoughnessMap ??= new Effigy.PaintCanvas( canvas.Width, canvas.Height );
			case Effigy.PaintChannel.Metalness:
				return canvas.MetalnessMap ??= new Effigy.PaintCanvas( canvas.Width, canvas.Height );
			case Effigy.PaintChannel.Height:
				return canvas.HeightMap ??= new Effigy.PaintCanvas( canvas.Width, canvas.Height );
			case Effigy.PaintChannel.Emissive:
				return canvas.EmissiveMap ??= new Effigy.PaintCanvas( canvas.Width, canvas.Height );
			case Effigy.PaintChannel.Opacity:
				return canvas.OpacityMap ??= new Effigy.PaintCanvas( canvas.Width, canvas.Height );
			case Effigy.PaintChannel.Normal:
				return canvas.NormalMap ??= new Effigy.PaintCanvas( canvas.Width, canvas.Height );
			default:
				throw new ArgumentException( $"Channel {channel} is not a paintable surface channel.", nameof( channel ) );
		}
	}

	/// <summary>
	/// One dab at the brush's position. Returns the number of texels touched. Out-of-bounds
	/// dabs are clipped, not thrown (a stroke near an island edge hands back plenty of texels
	/// outside the canvas, exactly like the kernel's Blend guards assume).
	/// </summary>
	public static int DabOnCanvas( Effigy.PaintCanvas canvas, TextureBrush brush )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( brush.Radius <= 0 ) throw new ArgumentException( "radius must be positive" );
		var target = ResolveChannel( canvas, brush.Channel );
		float strength = Math.Clamp( brush.Strength, 0f, 1f );
		if ( strength <= 0f ) return 0;
		byte r = brush.Channel == Effigy.PaintChannel.Color ? brush.R : brush.R;
		byte g = brush.Channel == Effigy.PaintChannel.Color ? brush.G : brush.R;
		byte b = brush.Channel == Effigy.PaintChannel.Color ? brush.B : brush.R;
		int rad = ( int ) MathF.Ceiling( brush.Radius );
		int touched = 0;
		for ( int y = -rad; y <= rad; y++ )
		for ( int x = -rad; x <= rad; x++ )
		{
			int px = ( int ) MathF.Round( brush.X ) + x;
			int py = ( int ) MathF.Round( brush.Y ) + y;
			if ( px < 0 || py < 0 || px >= target.Width || py >= target.Height ) continue;
			float d = MathF.Sqrt( x * x + y * y );
			if ( d > brush.Radius ) continue;
			float w = Effigy.Brush.Falloff( d / brush.Radius, brush.Falloff ) * strength;
			if ( w <= 0f ) continue;
			if ( brush.Erase ) target.Erase( px, py, w );
			else target.Blend( px, py, r, g, b, w );
			touched++;
		}
		return touched;
	}

	/// <summary>
	/// A stroke from (x0, y0) to (x1, y1), dabbed at <see cref="TextureBrush.Spacing"/> intervals
	/// so a fast drag is a continuous line, not a dotted trail. Returns the number of dabs.
	/// </summary>
	public static int StrokeOnCanvas( Effigy.PaintCanvas canvas, TextureBrush brush, float x0, float y0, float x1, float y1 )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		float dx = x1 - x0, dy = y1 - y0;
		float dist = MathF.Sqrt( dx * dx + dy * dy );
		float step = Math.Max( 1f, brush.Radius * Math.Max( 0.05f, brush.Spacing ) );
		int steps = Math.Max( 1, ( int ) MathF.Ceiling( dist / step ) );
		int dabs = 0;
		for ( int i = 0; i <= steps; i++ )
		{
			float t = steps == 0 ? 0f : ( float ) i / steps;
			var dab = new TextureBrush
			{
				X = x0 + dx * t, Y = y0 + dy * t, Radius = brush.Radius, Strength = brush.Strength,
				R = brush.R, G = brush.G, B = brush.B, Erase = brush.Erase, Falloff = brush.Falloff,
				Spacing = brush.Spacing, Channel = brush.Channel,
			};
			DabOnCanvas( canvas, dab );
			dabs++;
		}
		return dabs;
	}


	/// <summary>
	/// One dab of a PBR material: the same gesture stamps EVERY channel the material
	/// uses (colour, roughness, metalness, height) from one brush position, one value
	/// per channel — the service-level twin of PaintSession's material stroke. A channel
	/// the material does not use is left exactly as it is, so "Rust" over gold keeps the
	/// gold's height when rust has no height of its own. Returns the texels touched
	/// across all channels. Erase clears every channel the material uses.
	/// </summary>
	public static int DabMaterialOnCanvas( Effigy.PaintCanvas canvas, TextureBrush brush, Effigy.PaintPbrMaterial material )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		if ( brush.Radius <= 0 ) throw new ArgumentException( "radius must be positive" );

		var touched = 0;
		foreach ( var channel in material.Channels() )
		{
			var (r, g, b) = material.ValueFor( channel );
			var dab = new TextureBrush
			{
				X = brush.X, Y = brush.Y, Radius = brush.Radius, Strength = brush.Strength,
				R = (byte)Math.Round( Math.Clamp( r, 0f, 1f ) * 255f ),
				G = (byte)Math.Round( Math.Clamp( g, 0f, 1f ) * 255f ),
				B = (byte)Math.Round( Math.Clamp( b, 0f, 1f ) * 255f ),
				Erase = brush.Erase, Falloff = brush.Falloff, Spacing = brush.Spacing, Channel = channel,
			};
			touched += DabOnCanvas( canvas, dab );
		}
		return touched;
	}

	/// <summary>
	/// A material stroke from (x0, y0) to (x1, y1): a DabMaterialOnCanvas at
	/// Spacing intervals so a fast drag is a continuous line. Returns the number of dabs.
	/// </summary>
	public static int StrokeMaterialOnCanvas( Effigy.PaintCanvas canvas, TextureBrush brush, Effigy.PaintPbrMaterial material, float x0, float y0, float x1, float y1 )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( material is null ) throw new ArgumentNullException( nameof( material ) );
		float dx = x1 - x0, dy = y1 - y0;
		float dist = MathF.Sqrt( dx * dx + dy * dy );
		float step = Math.Max( 1f, brush.Radius * Math.Max( 0.05f, brush.Spacing ) );
		int steps = Math.Max( 1, ( int ) MathF.Ceiling( dist / step ) );
		int dabs = 0;
		for ( int i = 0; i <= steps; i++ )
		{
			float t = steps == 0 ? 0f : ( float ) i / steps;
			var dab = new TextureBrush
			{
				X = x0 + dx * t, Y = y0 + dy * t, Radius = brush.Radius, Strength = brush.Strength,
				Erase = brush.Erase, Falloff = brush.Falloff, Spacing = brush.Spacing,
			};
			DabMaterialOnCanvas( canvas, dab, material );
			dabs++;
		}
		return dabs;
	}

	/// <summary>Fill a whole channel at <paramref name="strength"/> (1 = full replace).</summary>
	public static void FillOnCanvas( Effigy.PaintCanvas canvas, Effigy.PaintChannel channel, byte r, byte g, byte b, float strength = 1f )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		var target = ResolveChannel( canvas, channel );
		float s = Math.Clamp( strength, 0f, 1f );
		if ( s <= 0f ) return;
		byte vr = channel == Effigy.PaintChannel.Color ? r : r;
		byte vg = channel == Effigy.PaintChannel.Color ? g : r;
		byte vb = channel == Effigy.PaintChannel.Color ? b : r;
		for ( int y = 0; y < target.Height; y++ )
		for ( int x = 0; x < target.Width; x++ )
			target.Blend( x, y, vr, vg, vb, s );
	}

	// ------------------------------------------------------------------
	// Upload set: the same images DocumentSession's live-paint swap builds.
	// ------------------------------------------------------------------

	/// <summary>
	/// The texture images a canvas uploads as, in exactly the form DocumentSession's
	/// ApplyPaintTextures builds them: colour baked opaque over its base, roughness/metalness
	/// baked opaque over theirs, height converted to a normal map. Null where the map was never
	/// painted — except Colour, which is the canvas itself and always present. A caller binding
	/// these must use factor 1/white, as DocumentSession does: the maps already hold absolute
	/// values, and a palette grey factor would darken them a second time.
	/// </summary>
	public sealed class TextureSet
	{
		public int Width, Height;
		public byte[] Color, Normal, Roughness, Metalness;
	}

	public static TextureSet UploadSet( Effigy.PaintCanvas canvas, float bumpStrength = Effigy.PaintTextureSet.DefaultBumpStrength )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		return new TextureSet
		{
			Width = canvas.Width,
			Height = canvas.Height,
			Color = Effigy.PaintLayers.Opaque( canvas, Effigy.PaintChannel.Color ),
			Normal = canvas.HeightMap is null ? null : Effigy.PaintTextureSet.HeightToNormal( canvas.HeightMap, bumpStrength ),
			Roughness = canvas.RoughnessMap is null ? null : Effigy.PaintLayers.Opaque( canvas, Effigy.PaintChannel.Roughness ),
			Metalness = canvas.MetalnessMap is null ? null : Effigy.PaintLayers.Opaque( canvas, Effigy.PaintChannel.Metalness ),
		};
	}

	// ------------------------------------------------------------------
	// Undoable session over one canvas channel.
	// ------------------------------------------------------------------

	/// <summary>
	/// An undoable texture-paint session on one channel of one canvas. The channel map is NOT
	/// created until the first dab, so opening a session never changes what a snapshot uploads.
	/// Undo/redo restore exact bytes; because that bypasses the canvas's own dirty flags, the
	/// session reports its own dirty rect for the uploader after any stroke, undo, or redo.
	/// </summary>
	public sealed class Session
	{
		const int MaxUndo = 32;

		sealed class StrokeRecord
		{
			public int X, Y, W, H;
			public byte[] Before, After;
		}

		readonly Effigy.PaintCanvas _canvas;
		readonly Effigy.PaintChannel _channel;
		readonly List<StrokeRecord> _undo = new();
		readonly List<StrokeRecord> _redo = new();

		TextureBrush _brush;
		bool _active;
		float _lastX, _lastY;
		bool _hasLast;

		// The stroke's waypoints, in the order they were dabbed: the durable record of
		// this session stroke. A dab's own (x, y) is recorded, not the interpolated
		// points between LineTo endpoints -- the kernel's texel replay interpolates at
		// Spacing itself, with the same step rule, so replaying these waypoints
		// reproduces the live stroke's dab stream (exactly for a LineTo-driven stroke,
		// where consecutive waypoints already sit one step apart).
		readonly List<Effigy.Vec2> _waypoints = new();

		/// <summary>The waypoints of the stroke in progress, or of the last finished stroke
		/// until the next one begins. Empty before any dab.</summary>
		public IReadOnlyList<Effigy.Vec2> Waypoints => _waypoints;

		/// <summary>
		/// This session's stroke as a committable kernel texel stroke, or null when no
		/// stroke has been dabbed yet. The caller commits it with
		/// <c>PaintFeature.AddTexelStroke</c> -- that is what makes the stroke survive a
		/// rebuild and a save, where the session's own canvas scrub does not. Colour
		/// bytes become 0..1 floats; a scalar channel's value rides in R, the same
		/// convention the kernel replay uses.
		/// </summary>
		public Effigy.TexelStroke ToTexelStroke()
		{
			if ( _brush is null || _waypoints.Count == 0 )
				return null;

			var stroke = new Effigy.TexelStroke
			{
				Channel = _channel,
				R = _brush.R / 255f, G = _brush.G / 255f, B = _brush.B / 255f, A = 1f,
				Radius = _brush.Radius, Strength = _brush.Strength, Falloff = _brush.Falloff,
				Spacing = _brush.Spacing, Erase = _brush.Erase,
			};

			foreach ( var w in _waypoints )
				stroke.Path.Add( new Effigy.TexelStrokePoint( w.x, w.y ) );

			return stroke;
		}

		// Snapshot being built for the stroke in progress: rect + pre-stroke bytes.
		int _snapX, _snapY, _snapW, _snapH;
		byte[] _snapBytes;
		bool _hasSnap;

		public Session( Effigy.PaintCanvas canvas, Effigy.PaintChannel channel )
		{
			_canvas = canvas ?? throw new ArgumentNullException( nameof( canvas ) );
			if ( channel == Effigy.PaintChannel.Mask )
				throw new ArgumentException( "Mask is not a surface channel.", nameof( channel ) );
			_channel = channel;
		}

		public Effigy.PaintCanvas Canvas => _canvas;
		public Effigy.PaintChannel Channel => _channel;
		/// <summary>The channel canvas, or null before the first dab created it.</summary>
		public Effigy.PaintCanvas Target => _canvas.Channel( _channel );
		public bool CanUndo => _undo.Count > 0;
		public bool CanRedo => _redo.Count > 0;
		public bool HasDirty { get; private set; }
		public int DirtyX0 { get; private set; }
		public int DirtyY0 { get; private set; }
		public int DirtyX1 { get; private set; }
		public int DirtyY1 { get; private set; }

		public void ClearDirtyFlag() => HasDirty = false;

		public void BeginStroke( TextureBrush brush )
		{
			if ( _active ) throw new InvalidOperationException( "A stroke is already in progress." );
			_brush = brush ?? throw new ArgumentNullException( nameof( brush ) );
			_waypoints.Clear();
			if ( _brush.Channel != _channel )
				throw new ArgumentException( $"Session channel is {_channel}, brush channel is {_brush.Channel}." );
			_active = true;
			_hasSnap = false;
			_snapBytes = null;
			_hasLast = false;
		}

		/// <summary>One dab at (x, y) within the open stroke.</summary>
		public void Dab( float x, float y )
		{
			if ( !_active ) throw new InvalidOperationException( "No stroke in progress — call BeginStroke first." );
			// Snapshot expansion happens BEFORE the dab: the fringe of a grown rect is still
			// pre-stroke (every texel already dabbed lies inside the old rect), so copying it
			// from the live canvas now is exactly the pre-stroke state.
			EnsureSnapshot( x, y );
			var dab = new TextureBrush
			{
				X = x, Y = y, Radius = _brush.Radius, Strength = _brush.Strength,
				R = _brush.R, G = _brush.G, B = _brush.B, Erase = _brush.Erase, Falloff = _brush.Falloff,
				Spacing = _brush.Spacing, Channel = _channel,
			};
			DabOnCanvas( _canvas, dab );
			_waypoints.Add( new Effigy.Vec2( x, y ) );
			MarkDirty( x, y );
			_lastX = x; _lastY = y; _hasLast = true;
		}

		/// <summary>Continue the open stroke to (x, y), dabbing along the segment.</summary>
		public void LineTo( float x, float y )
		{
			if ( !_active ) throw new InvalidOperationException( "No stroke in progress — call BeginStroke first." );
			if ( !_hasLast ) { Dab( x, y ); return; }
			float dx = x - _lastX, dy = y - _lastY;
			float dist = MathF.Sqrt( dx * dx + dy * dy );
			float step = Math.Max( 1f, _brush.Radius * Math.Max( 0.05f, _brush.Spacing ) );
			int steps = Math.Max( 1, ( int ) MathF.Ceiling( dist / step ) );
			for ( int i = 1; i <= steps; i++ )
			{
				float t = ( float ) i / steps;
				Dab( _lastX + dx * t, _lastY + dy * t );
			}
		}

		public void EndStroke()
		{
			if ( !_active ) throw new InvalidOperationException( "No stroke in progress." );
			_active = false;
			if ( !_hasSnap ) return; // A stroke with no dabs changes nothing and is not undoable.
			var target = Target;
			var after = CopyRect( target, _snapX, _snapY, _snapW, _snapH );
			_undo.Add( new StrokeRecord { X = _snapX, Y = _snapY, W = _snapW, H = _snapH, Before = _snapBytes, After = after } );
			if ( _undo.Count > MaxUndo ) _undo.RemoveAt( 0 );
			_redo.Clear();
			_hasSnap = false;
			_snapBytes = null;
		}

		public bool Undo()
		{
			if ( _undo.Count == 0 ) return false;
			var rec = _undo[_undo.Count - 1];
			_undo.RemoveAt( _undo.Count - 1 );
			RestoreRect( rec, rec.Before );
			_redo.Add( rec );
			SetDirtyRect( rec );
			return true;
		}

		public bool Redo()
		{
			if ( _redo.Count == 0 ) return false;
			var rec = _redo[_redo.Count - 1];
			_redo.RemoveAt( _redo.Count - 1 );
			RestoreRect( rec, rec.After );
			_undo.Add( rec );
			SetDirtyRect( rec );
			return true;
		}

		void EnsureSnapshot( float x, float y )
		{
			var target = ResolveChannel( _canvas, _channel );
			int rad = ( int ) MathF.Ceiling( _brush.Radius );
			int cx = ( int ) MathF.Round( x ), cy = ( int ) MathF.Round( y );
			int x0 = Math.Max( 0, cx - rad ), y0 = Math.Max( 0, cy - rad );
			int x1 = Math.Min( target.Width - 1, cx + rad ), y1 = Math.Min( target.Height - 1, cy + rad );
			if ( x1 < x0 || y1 < y0 ) return; // Dab entirely off-canvas: nothing to snapshot.
			if ( !_hasSnap )
			{
				_snapX = x0; _snapY = y0; _snapW = x1 - x0 + 1; _snapH = y1 - y0 + 1;
				_snapBytes = CopyRect( target, _snapX, _snapY, _snapW, _snapH );
				_hasSnap = true;
				return;
			}
			int nx0 = Math.Min( _snapX, x0 ), ny0 = Math.Min( _snapY, y0 );
			int nx1 = Math.Max( _snapX + _snapW - 1, x1 ), ny1 = Math.Max( _snapY + _snapH - 1, y1 );
			if ( nx0 == _snapX && ny0 == _snapY && nx1 == _snapX + _snapW - 1 && ny1 == _snapY + _snapH - 1 ) return;
			var grown = new byte[( nx1 - nx0 + 1 ) * ( ny1 - ny0 + 1 ) * 4];
			// Old rect's bytes carry over at their offset; the fringe is copied from the live
			// canvas, which is still pre-stroke there (see Dab's comment).
			for ( int yy = 0; yy < _snapH; yy++ )
				Array.Copy( _snapBytes, yy * _snapW * 4, grown, ( ( _snapY - ny0 + yy ) * ( nx1 - nx0 + 1 ) + ( _snapX - nx0 ) ) * 4, _snapW * 4 );
			for ( int yy = ny0; yy <= ny1; yy++ )
			for ( int xx = nx0; xx <= nx1; xx++ )
			{
				if ( xx >= _snapX && xx < _snapX + _snapW && yy >= _snapY && yy < _snapY + _snapH ) continue;
				int src = ( yy * target.Width + xx ) * 4;
				int dst = ( ( yy - ny0 ) * ( nx1 - nx0 + 1 ) + ( xx - nx0 ) ) * 4;
				Array.Copy( target.Rgba, src, grown, dst, 4 );
			}
			_snapX = nx0; _snapY = ny0; _snapW = nx1 - nx0 + 1; _snapH = ny1 - ny0 + 1;
			_snapBytes = grown;
		}

		static byte[] CopyRect( Effigy.PaintCanvas target, int x, int y, int w, int h )
		{
			var bytes = new byte[w * h * 4];
			for ( int yy = 0; yy < h; yy++ )
				Array.Copy( target.Rgba, ( ( y + yy ) * target.Width + x ) * 4, bytes, yy * w * 4, w * 4 );
			return bytes;
		}

		void RestoreRect( StrokeRecord rec, byte[] bytes )
		{
			var target = ResolveChannel( _canvas, _channel );
			for ( int yy = 0; yy < rec.H; yy++ )
				Array.Copy( bytes, yy * rec.W * 4, target.Rgba, ( ( rec.Y + yy ) * target.Width + rec.X ) * 4, rec.W * 4 );
		}

		void MarkDirty( float x, float y )
		{
			int rad = ( int ) MathF.Ceiling( _brush.Radius );
			int cx = ( int ) MathF.Round( x ), cy = ( int ) MathF.Round( y );
			int x0 = Math.Max( 0, cx - rad ), y0 = Math.Max( 0, cy - rad );
			int x1 = Math.Min( _canvas.Width - 1, cx + rad ), y1 = Math.Min( _canvas.Height - 1, cy + rad );
			if ( !HasDirty ) { DirtyX0 = x0; DirtyY0 = y0; DirtyX1 = x1; DirtyY1 = y1; HasDirty = true; }
			else { DirtyX0 = Math.Min( DirtyX0, x0 ); DirtyY0 = Math.Min( DirtyY0, y0 ); DirtyX1 = Math.Max( DirtyX1, x1 ); DirtyY1 = Math.Max( DirtyY1, y1 ); }
		}

		void SetDirtyRect( StrokeRecord rec )
		{
			DirtyX0 = rec.X; DirtyY0 = rec.Y; DirtyX1 = rec.X + rec.W - 1; DirtyY1 = rec.Y + rec.H - 1;
			HasDirty = true;
		}
	}
}
