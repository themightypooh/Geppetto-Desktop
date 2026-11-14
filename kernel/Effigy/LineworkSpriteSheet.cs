using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>How a LineworkTimeline plays past its end. Loop and Once match the
/// panel's loop play; PingPong bounces, like SpriteAnimation's ping-pong,
/// because a walk cycle judged forward-then-back needs no extra frames.</summary>
public enum LineworkLoopMode
{
	Loop,
	PingPong,
	Once
}

/// <summary>One flat timeline frame: a single finished image, not an exposure.
/// The TimelinePanel shows frames exactly like this — Frame 1..N, one image
/// each — while FrameAnimClip underneath is a multi-track exposure sheet.
/// A timeline frame therefore holds its own pixels, so the panel, the onion
/// preview and the spritesheet packer all read the same thing without
/// resolving a library at render time.</summary>
public sealed class LineworkTimelineFrame
{
	public string Name;
	public string DrawingId;
	public int Width;
	public int Height;
	public byte[] Rgba;
	public int DurationMs;
	public FrameRole Role;

	public LineworkTimelineFrame( string name, string drawingId, int width, int height, byte[] rgba, int durationMs = 100, FrameRole role = FrameRole.InBetween )
	{
		if ( width <= 0 || height <= 0 ) throw new ArgumentException( "A timeline frame needs a positive size." );
		if ( rgba == null || rgba.Length != width * height * 4 ) throw new ArgumentException( "RGBA must hold exactly one pixel per cell." );
		if ( durationMs < 1 ) throw new ArgumentException( "A timeline frame must last at least 1 ms." );
		Name = name;
		DrawingId = drawingId;
		Width = width;
		Height = height;
		Rgba = rgba;
		DurationMs = durationMs;
		Role = role;
	}

	public static LineworkTimelineFrame Blank( string name, int width, int height, int durationMs = 100 ) =>
		new( name, "", width, height, new byte[width * height * 4], durationMs );

	/// <summary>Independent copy: editing the duplicate's pixels never touches
	/// the original, which is what the panel's Duplicate button promises.</summary>
	public LineworkTimelineFrame Copy( string newName ) =>
		new( newName, DrawingId, Width, Height, (byte[])Rgba.Clone(), DurationMs, Role );
}

/// <summary>The flat frame timeline behind LineworkTimelinePanel: ordered
/// frames, Add/Duplicate/Delete/Move, per-frame durations, and playback
/// (Loop / PingPong / Once). Deliberately NOT a second exposure model:
/// FromClip flattens a FrameAnimClip into this list, so the panel can show
/// and edit a clip frame by frame without learning exposures.</summary>
public sealed class LineworkTimeline
{
	public string Name;
	public int Fps = 12;
	public LineworkLoopMode LoopMode = LineworkLoopMode.Loop;
	public List<LineworkTimelineFrame> Frames = new();

	public LineworkTimeline( string name ) => Name = name;

	public int FrameCount => Frames.Count;
	public int Width => Frames.Count == 0 ? 0 : Frames[0].Width;
	public int Height => Frames.Count == 0 ? 0 : Frames[0].Height;
	public int TotalDurationMs => Frames.Sum( f => f.DurationMs );
	public double DurationSeconds => TotalDurationMs / 1000.0;

	public int DurationMsForFps() => Fps > 0 ? Math.Max( 1, (int)Math.Round( 1000.0 / Fps ) ) : 100;

	public void AddFrame( LineworkTimelineFrame frame )
	{
		if ( frame == null ) throw new ArgumentNullException( nameof( frame ) );
		if ( Frames.Count > 0 && (frame.Width != Width || frame.Height != Height) )
			throw new ArgumentException( $"Frame '{frame.Name}' is {frame.Width}x{frame.Height}, timeline is {Width}x{Height}." );
		Frames.Add( frame );
	}

	public LineworkTimelineFrame AddBlankFrame( int width, int height ) =>
		AddBlankFrame( width, height, $"Frame {Frames.Count + 1}" );

	public LineworkTimelineFrame AddBlankFrame( int width, int height, string name )
	{
		var frame = LineworkTimelineFrame.Blank( name, width, height, DurationMsForFps() );
		AddFrame( frame );
		return frame;
	}

	public void InsertFrame( int index, LineworkTimelineFrame frame )
	{
		if ( frame == null ) throw new ArgumentNullException( nameof( frame ) );
		if ( index < 0 || index > Frames.Count ) throw new ArgumentOutOfRangeException( nameof( index ) );
		if ( Frames.Count > 0 && (frame.Width != Width || frame.Height != Height) )
			throw new ArgumentException( $"Frame '{frame.Name}' is {frame.Width}x{frame.Height}, timeline is {Width}x{Height}." );
		Frames.Insert( index, frame );
	}

	/// <summary>Returns the new frame's index. The copy is independent.</summary>
	public int DuplicateFrame( int index )
	{
		if ( index < 0 || index >= Frames.Count ) throw new ArgumentOutOfRangeException( nameof( index ) );
		var copy = Frames[index].Copy( Frames[index].Name + " copy" );
		Frames.Insert( index + 1, copy );
		return index + 1;
	}

	public void DeleteFrame( int index )
	{
		if ( index < 0 || index >= Frames.Count ) throw new ArgumentOutOfRangeException( nameof( index ) );
		Frames.RemoveAt( index );
	}

	public void MoveFrame( int fromIndex, int toIndex )
	{
		if ( fromIndex < 0 || fromIndex >= Frames.Count ) throw new ArgumentOutOfRangeException( nameof( fromIndex ) );
		if ( toIndex < 0 || toIndex >= Frames.Count ) throw new ArgumentOutOfRangeException( nameof( toIndex ) );
		var frame = Frames[fromIndex];
		Frames.RemoveAt( fromIndex );
		Frames.Insert( toIndex, frame );
	}

	/// <summary>Frame index at an elapsed time, or -1 for an empty timeline.
	/// Loop wraps; PingPong walks forward then back without repeating the
	/// end frames; Once clamps to the last frame. Negative time is frame 0.</summary>
	public int FrameAt( long elapsedMs )
	{
		if ( Frames.Count == 0 ) return -1;
		if ( elapsedMs <= 0 ) return 0;
		var total = (long)TotalDurationMs;
		switch ( LoopMode )
		{
			case LineworkLoopMode.Once:
				if ( elapsedMs >= total ) return Frames.Count - 1;
				return FrameAtUnwrapped( elapsedMs );
			case LineworkLoopMode.PingPong:
				if ( Frames.Count == 1 ) return 0;
				var forward = total;
				var backward = total - Frames[0].DurationMs - Frames[^1].DurationMs;
				var period = forward + backward;
				if ( period <= 0 ) return 0;
				var t = elapsedMs % period;
				if ( t < forward ) return FrameAtUnwrapped( t );
				var back = t - forward;
				for ( int i = Frames.Count - 2; i >= 1; i-- )
				{
					if ( back < Frames[i].DurationMs ) return i;
					back -= Frames[i].DurationMs;
				}
				return 0;
			default:
				return FrameAtUnwrapped( elapsedMs % total );
		}
	}

	int FrameAtUnwrapped( long elapsedMs )
	{
		long t = elapsedMs;
		for ( int i = 0; i < Frames.Count; i++ )
		{
			if ( t < Frames[i].DurationMs ) return i;
			t -= Frames[i].DurationMs;
		}
		return Frames.Count - 1;
	}

	public List<string> Validate()
	{
		var errors = new List<string>();
		if ( Fps <= 0 ) errors.Add( "Fps must be positive." );
		if ( Frames.Count == 0 ) errors.Add( "Timeline has no frames." );
		foreach ( var frame in Frames )
		{
			if ( frame.Width != Width || frame.Height != Height )
				errors.Add( $"Frame '{frame.Name}' is {frame.Width}x{frame.Height}, timeline is {Width}x{Height}." );
			if ( frame.DurationMs < 1 ) errors.Add( $"Frame '{frame.Name}' must last at least 1 ms." );
			if ( frame.Rgba == null || frame.Rgba.Length != frame.Width * frame.Height * 4 )
				errors.Add( $"Frame '{frame.Name}' has the wrong pixel count." );
		}
		return errors;
	}

	/// <summary>Flattens a FrameAnimClip into flat frames: one image per frame,
	/// tracks composited source-over in track order (the exporter's rule),
	/// duration from the clip fps, role Key if any exposure at that frame
	/// is a Key, else Breakdown if any, else InBetween. A frame no track
	/// covers is a transparent blank, not an error — gaps are legal.</summary>
	public static LineworkTimeline FromClip( FrameAnimClip clip, FrameDrawingLibrary library )
	{
		if ( clip == null ) throw new ArgumentNullException( nameof( clip ) );
		if ( library == null ) throw new ArgumentNullException( nameof( library ) );
		var errors = clip.Validate( library );
		if ( errors.Count > 0 ) throw new InvalidOperationException( "Cannot flatten an invalid clip: " + string.Join( " ", errors ) );
		var timeline = new LineworkTimeline( clip.Name ) { Fps = clip.Fps, LoopMode = clip.Loop ? LineworkLoopMode.Loop : LineworkLoopMode.Once };
		var duration = timeline.DurationMsForFps();
		var canvas = new byte[clip.Width * clip.Height * 4];
		for ( int frame = 0; frame < clip.FrameCount; frame++ )
		{
			Array.Clear( canvas, 0, canvas.Length );
			var ids = new List<string>();
			var role = FrameRole.InBetween;
			foreach ( var track in clip.Tracks )
			{
				foreach ( var exposure in track.Exposures )
				{
					if ( !exposure.Covers( frame ) ) continue;
					ids.Add( exposure.DrawingId );
					if ( exposure.Role == FrameRole.Key ) role = FrameRole.Key;
					else if ( exposure.Role == FrameRole.Breakdown && role != FrameRole.Key ) role = FrameRole.Breakdown;
				}
			}
			foreach ( var pair in clip.CompositionAt( frame ) )
				CompositeOver( canvas, library.Get( pair.Value ).Rgba );
			timeline.AddFrame( new LineworkTimelineFrame(
				$"Frame {frame + 1}", string.Join( "+", ids ),
				clip.Width, clip.Height, (byte[])canvas.Clone(), duration, role ) );
		}
		return timeline;
	}

	/// <summary>Ghost plan for a playhead position, reusing OnionSkin.Compute
	/// (never reimplemented): keys default to this timeline's Key frames.</summary>
	public List<OnionFrame> GhostFrames( OnionSettings settings, int currentFrame, IReadOnlyList<int> keyFrames = null )
	{
		if ( keyFrames == null )
		{
			var keys = new List<int>();
			for ( int i = 0; i < Frames.Count; i++ )
				if ( Frames[i].Role == FrameRole.Key ) keys.Add( i );
			keyFrames = keys;
		}
		return OnionSkin.Compute( settings, currentFrame, Frames.Count, keyFrames, null, LoopMode == LineworkLoopMode.Loop );
	}

	/// <summary>Onion preview: ghosts composited farthest-first (Compute's
	/// order, so nearer ghosts win), each tinted through OnionFrame.Mix —
	/// red before, green after at TintStrength 1, the panel's convention —
	/// then the current frame painted on top at full opacity. Disabled
	/// settings, or no ghosts, return the current frame unchanged.</summary>
	public byte[] RenderOnionPreview( int currentFrame, OnionSettings settings, IReadOnlyList<int> keyFrames = null )
	{
		if ( Frames.Count == 0 ) throw new InvalidOperationException( "Cannot preview an empty timeline." );
		currentFrame = Math.Clamp( currentFrame, 0, Frames.Count - 1 );
		var canvas = (byte[])Frames[currentFrame].Rgba.Clone();
		if ( settings == null || !settings.Enabled ) return canvas;
		var ghosts = GhostFrames( settings, currentFrame, keyFrames );
		var under = new byte[canvas.Length];
		foreach ( var ghost in ghosts )
		{
			var src = Frames[ghost.Frame].Rgba;
			var tinted = new byte[src.Length];
			for ( int i = 0; i < src.Length; i += 4 )
			{
				var drawing = new Vec4( src[i] / 255f, src[i + 1] / 255f, src[i + 2] / 255f, src[i + 3] / 255f );
				var mixed = ghost.Mix( drawing );
				tinted[i] = (byte)Math.Round( Math.Clamp( mixed.x, 0f, 1f ) * 255f );
				tinted[i + 1] = (byte)Math.Round( Math.Clamp( mixed.y, 0f, 1f ) * 255f );
				tinted[i + 2] = (byte)Math.Round( Math.Clamp( mixed.z, 0f, 1f ) * 255f );
				tinted[i + 3] = (byte)Math.Round( Math.Clamp( mixed.w, 0f, 1f ) * 255f );
			}
			CompositeOver( under, tinted );
		}
		CompositeOver( under, canvas );
		return under;
	}

	static void CompositeOver( byte[] dst, byte[] src )
	{
		for ( int i = 0; i < dst.Length; i += 4 )
		{
			float sa = src[i + 3] / 255f;
			if ( sa <= 0f ) continue;
			float da = dst[i + 3] / 255f;
			float outA = sa + da * ( 1f - sa );
			if ( outA <= 0f ) continue;
			for ( int c = 0; c < 3; c++ )
				dst[i + c] = (byte)Math.Round( ( src[i + c] * sa + dst[i + c] * da * ( 1f - sa ) ) / outA );
			dst[i + 3] = (byte)Math.Round( outA * 255f );
		}
	}
}

/// <summary>One packed cell in a spritesheet: where the frame sits in the
/// atlas, plus the timing the atlas alone cannot carry.</summary>
public sealed class LineworkSpriteSheetFrame
{
	public int Index;
	public string Name;
	public string DrawingId;
	public int X;
	public int Y;
	public int Width;
	public int Height;
	public int DurationMs;
	public double TimeSeconds;
}

/// <summary>A packed spritesheet: one atlas image plus per-frame rects.
/// Packing is a plain grid (columns, optional padding), never a trim or
/// repack, so a frame's rect is always col * (w + pad), row * (h + pad)
/// and a reader can slice it without the metadata file.</summary>
public sealed class LineworkSpriteSheet
{
	public string Name;
	public int Width;
	public int Height;
	public int Columns;
	public int Rows;
	public int Padding;
	public int FrameWidth;
	public int FrameHeight;
	public int Fps;
	public LineworkLoopMode LoopMode;
	public byte[] Rgba;
	public List<LineworkSpriteSheetFrame> Frames = new();

	public static LineworkSpriteSheet Pack( LineworkTimeline timeline, int columns, int padding = 0 )
	{
		if ( timeline == null ) throw new ArgumentNullException( nameof( timeline ) );
		var errors = timeline.Validate();
		if ( errors.Count > 0 ) throw new InvalidOperationException( "Cannot pack an invalid timeline: " + string.Join( " ", errors ) );
		if ( columns < 1 ) throw new ArgumentException( "Columns must be at least 1." );
		if ( padding < 0 ) throw new ArgumentException( "Padding cannot be negative." );
		int rows = (timeline.FrameCount + columns - 1) / columns;
		int atlasW = columns * timeline.Width + (columns - 1) * padding;
		int atlasH = rows * timeline.Height + (rows - 1) * padding;
		var sheet = new LineworkSpriteSheet
		{
			Name = timeline.Name,
			Width = atlasW,
			Height = atlasH,
			Columns = columns,
			Rows = rows,
			Padding = padding,
			FrameWidth = timeline.Width,
			FrameHeight = timeline.Height,
			Fps = timeline.Fps,
			LoopMode = timeline.LoopMode,
			Rgba = new byte[atlasW * atlasH * 4]
		};
		long timeMs = 0;
		for ( int i = 0; i < timeline.FrameCount; i++ )
		{
			var frame = timeline.Frames[i];
			int col = i % columns;
			int row = i / columns;
			int x = col * (timeline.Width + padding);
			int y = row * (timeline.Height + padding);
			for ( int r = 0; r < timeline.Height; r++ )
				Array.Copy( frame.Rgba, r * timeline.Width * 4, sheet.Rgba, ( ( y + r ) * atlasW + x ) * 4, timeline.Width * 4 );
			sheet.Frames.Add( new LineworkSpriteSheetFrame
			{
				Index = i, Name = frame.Name, DrawingId = frame.DrawingId, X = x, Y = y,
				Width = frame.Width, Height = frame.Height, DurationMs = frame.DurationMs, TimeSeconds = timeMs / 1000.0
			} );
			timeMs += frame.DurationMs;
		}
		return sheet;
	}

	/// <summary>Atlas cell pixels for one packed frame, as a fresh RGBA array.</summary>
	public byte[] FramePixels( int index )
	{
		if ( index < 0 || index >= Frames.Count ) throw new ArgumentOutOfRangeException( nameof( index ) );
		var rect = Frames[index];
		var pixels = new byte[rect.Width * rect.Height * 4];
		for ( int r = 0; r < rect.Height; r++ )
			Array.Copy( Rgba, ( ( rect.Y + r ) * Width + rect.X ) * 4, pixels, r * rect.Width * 4, rect.Width * 4 );
		return pixels;
	}
}

/// <summary>What a spritesheet export produced: atlas + metadata paths.</summary>
public sealed class LineworkSpriteSheetExportResult
{
	public string SheetPath = "";
	public string MetadataPath = "";
	public LineworkSpriteSheet Sheet;
}

/// <summary>Export, metadata round-trip and frame-sequence import for
/// LineworkSpriteSheet. Export writes atlas PNG + a JSON metadata file
/// (format linework-spritesheet/1) carrying every frame rect, name,
/// drawing id, duration and start time — the atlas alone cannot carry
/// timing. Import never guesses order: PNG sequences sort by filename,
/// FrameAnim sequences follow the manifest's frame numbers.</summary>
public static class LineworkSpriteSheetIO
{
	public static LineworkSpriteSheetExportResult Export( LineworkTimeline timeline, string outputDir, int columns, int padding = 0, string sheetFileName = "spritesheet.png", string metadataFileName = "spritesheet.json" )
	{
		var sheet = LineworkSpriteSheet.Pack( timeline, columns, padding );
		Directory.CreateDirectory( outputDir );
		var sheetPath = Path.Combine( outputDir, sheetFileName );
		var metadataPath = Path.Combine( outputDir, metadataFileName );
		PngWriter.WriteFileRgba( sheetPath, sheet.Rgba, sheet.Width, sheet.Height );
		WriteMetadata( sheet, metadataPath );
		return new LineworkSpriteSheetExportResult { SheetPath = sheetPath, MetadataPath = metadataPath, Sheet = sheet };
	}

	static void WriteMetadata( LineworkSpriteSheet sheet, string path )
	{
		using var stream = File.Create( path );
		using var writer = new Utf8JsonWriter( stream, new JsonWriterOptions { Indented = true } );
		writer.WriteStartObject();
		writer.WriteString( "format", "linework-spritesheet/1" );
		writer.WriteString( "name", sheet.Name );
		writer.WriteNumber( "width", sheet.Width );
		writer.WriteNumber( "height", sheet.Height );
		writer.WriteNumber( "columns", sheet.Columns );
		writer.WriteNumber( "rows", sheet.Rows );
		writer.WriteNumber( "padding", sheet.Padding );
		writer.WriteNumber( "frameWidth", sheet.FrameWidth );
		writer.WriteNumber( "frameHeight", sheet.FrameHeight );
		writer.WriteNumber( "fps", sheet.Fps );
		writer.WriteString( "loopMode", sheet.LoopMode.ToString() );
		writer.WritePropertyName( "frames" );
		writer.WriteStartArray();
		foreach ( var frame in sheet.Frames )
		{
			writer.WriteStartObject();
			writer.WriteNumber( "index", frame.Index );
			writer.WriteString( "name", frame.Name );
			writer.WriteString( "drawingId", frame.DrawingId ?? "" );
			writer.WriteNumber( "x", frame.X );
			writer.WriteNumber( "y", frame.Y );
			writer.WriteNumber( "width", frame.Width );
			writer.WriteNumber( "height", frame.Height );
			writer.WriteNumber( "durationMs", frame.DurationMs );
			writer.WriteNumber( "timeSeconds", frame.TimeSeconds );
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	/// <summary>Reads a metadata file back into a sheet layout. Without a
	/// PNG path the pixels stay empty (layout only); with one, pixels are
	/// read and must match the metadata's atlas size, or it throws.</summary>
	public static LineworkSpriteSheet FromMetadata( string metadataPath, string pngPath = null )
	{
		using var doc = JsonDocument.Parse( File.ReadAllText( metadataPath ) );
		var root = doc.RootElement;
		if ( root.GetProperty( "format" ).GetString() != "linework-spritesheet/1" )
			throw new InvalidDataException( "Not a linework-spritesheet/1 metadata file." );
		var sheet = new LineworkSpriteSheet
		{
			Name = root.GetProperty( "name" ).GetString(),
			Width = root.GetProperty( "width" ).GetInt32(),
			Height = root.GetProperty( "height" ).GetInt32(),
			Columns = root.GetProperty( "columns" ).GetInt32(),
			Rows = root.GetProperty( "rows" ).GetInt32(),
			Padding = root.GetProperty( "padding" ).GetInt32(),
			FrameWidth = root.GetProperty( "frameWidth" ).GetInt32(),
			FrameHeight = root.GetProperty( "frameHeight" ).GetInt32(),
			Fps = root.GetProperty( "fps" ).GetInt32(),
			LoopMode = Enum.Parse<LineworkLoopMode>( root.GetProperty( "loopMode" ).GetString() ),
			Rgba = Array.Empty<byte>()
		};
		foreach ( var frameJson in root.GetProperty( "frames" ).EnumerateArray() )
			sheet.Frames.Add( new LineworkSpriteSheetFrame
			{
				Index = frameJson.GetProperty( "index" ).GetInt32(),
				Name = frameJson.GetProperty( "name" ).GetString(),
				DrawingId = frameJson.GetProperty( "drawingId" ).GetString(),
				X = frameJson.GetProperty( "x" ).GetInt32(),
				Y = frameJson.GetProperty( "y" ).GetInt32(),
				Width = frameJson.GetProperty( "width" ).GetInt32(),
				Height = frameJson.GetProperty( "height" ).GetInt32(),
				DurationMs = frameJson.GetProperty( "durationMs" ).GetInt32(),
				TimeSeconds = frameJson.GetProperty( "timeSeconds" ).GetDouble()
			} );
		if ( pngPath != null )
		{
			var (rgba, width, height) = PngReader.Read( File.ReadAllBytes( pngPath ) );
			if ( width != sheet.Width || height != sheet.Height )
				throw new InvalidDataException( $"Atlas PNG is {width}x{height}, metadata says {sheet.Width}x{sheet.Height}." );
			sheet.Rgba = rgba;
		}
		return sheet;
	}

	/// <summary>Builds a timeline from an in-memory ordered frame sequence:
	/// the caller owns the order, sizes must match, pixels are copied so
	/// later edits to the inputs never rewrite the timeline.</summary>
	public static LineworkTimeline ImportSequence( IEnumerable<LineworkTimelineFrame> frames, string name, int fps, LineworkLoopMode loopMode = LineworkLoopMode.Loop )
	{
		if ( frames == null ) throw new ArgumentNullException( nameof( frames ) );
		var timeline = new LineworkTimeline( name ) { Fps = fps, LoopMode = loopMode };
		foreach ( var frame in frames )
			timeline.AddFrame( frame.Copy( frame.Name ) );
		if ( timeline.FrameCount == 0 ) throw new ArgumentException( "A frame sequence needs at least one frame." );
		return timeline;
	}

	/// <summary>Imports a directory of PNGs as one timeline, in filename
	/// order (frame_0000.png before frame_0001.png, a.png before b.png).
	/// Every PNG must share a size; a mixed-size directory is rejected,
	/// never silently rescaled. Duration per frame comes from fps.</summary>
	public static LineworkTimeline ImportPngSequence( string directory, int fps = 12, LineworkLoopMode loopMode = LineworkLoopMode.Loop )
	{
		if ( string.IsNullOrWhiteSpace( directory ) || !Directory.Exists( directory ) )
			throw new ArgumentException( $"PNG sequence directory '{directory}' does not exist." );
		var files = Directory.GetFiles( directory )
			.Where( f => Path.GetExtension( f ).Equals( ".png", StringComparison.OrdinalIgnoreCase ) )
			.OrderBy( f => Path.GetFileName( f ), StringComparer.Ordinal )
			.ToList();
		return ImportPngFiles( files, Path.GetFileName( directory ), fps, loopMode );
	}

	public static LineworkTimeline ImportPngFiles( IReadOnlyList<string> paths, string name, int fps = 12, LineworkLoopMode loopMode = LineworkLoopMode.Loop )
	{
		if ( paths == null || paths.Count == 0 ) throw new ArgumentException( "A PNG sequence needs at least one file." );
		var frames = new List<LineworkTimelineFrame>();
		var duration = Math.Max( 1, (int)Math.Round( 1000.0 / Math.Max( 1, fps ) ) );
		foreach ( var path in paths )
		{
			var (rgba, width, height) = PngReader.Read( File.ReadAllBytes( path ) );
			frames.Add( new LineworkTimelineFrame( Path.GetFileNameWithoutExtension( path ), "", width, height, rgba, duration ) );
		}
		return ImportSequence( frames, name, fps, loopMode );
	}

	/// <summary>Imports a FrameAnimExporter sequence (frame_0000.png onward
	/// + manifest.json) back into a flat timeline, so a clip exported as a
	/// PNG sequence can be packed as a spritesheet or previewed with onion
	/// ghosts. Frame order and drawing ids come from the manifest, pixels
	/// from the PNGs; a missing frame PNG throws, it is never skipped.</summary>
	public static LineworkTimeline ImportFrameAnimSequence( string manifestPath, string framesDir )
	{
		using var doc = JsonDocument.Parse( File.ReadAllText( manifestPath ) );
		var root = doc.RootElement;
		var name = root.GetProperty( "name" ).GetString();
		var fps = root.GetProperty( "fps" ).GetInt32();
		var loop = root.GetProperty( "loop" ).GetBoolean();
		var duration = Math.Max( 1, (int)Math.Round( 1000.0 / Math.Max( 1, fps ) ) );
		var frameJsons = root.GetProperty( "frames" ).EnumerateArray()
			.OrderBy( f => f.GetProperty( "frame" ).GetInt32() ).ToList();
		if ( frameJsons.Count == 0 ) throw new ArgumentException( "FrameAnim manifest has no frames." );
		var frames = new List<LineworkTimelineFrame>();
		foreach ( var frameJson in frameJsons )
		{
			int index = frameJson.GetProperty( "frame" ).GetInt32();
			var ids = new List<string>();
			foreach ( var drawingJson in frameJson.GetProperty( "drawings" ).EnumerateArray() )
				ids.Add( drawingJson.GetProperty( "drawingId" ).GetString() );
			var path = Path.Combine( framesDir, $"frame_{index:D4}.png" );
			if ( !File.Exists( path ) ) throw new FileNotFoundException( $"FrameAnim sequence is missing {path}." );
			var (rgba, width, height) = PngReader.Read( File.ReadAllBytes( path ) );
			frames.Add( new LineworkTimelineFrame( $"Frame {index + 1}", string.Join( "+", ids ), width, height, rgba, duration ) );
		}
		return ImportSequence( frames, name, fps, loop ? LineworkLoopMode.Loop : LineworkLoopMode.Once );
	}
}
