using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>What an exposure IS in a hand-drawn sequence, in traditional terms.
/// A key is a storytelling pose, a breakdown is the pose that sells the move
/// between two keys, an in-between is filler between breakdowns. These are
/// first-class roles in the model, not comments on a frame number: a later
/// lane (onion skin, timing chart) reads them to decide what to show.</summary>
public enum FrameRole
{
	Key,
	Breakdown,
	InBetween
}

/// <summary>One hand-drawn image, held by id. A drawing is stored ONCE and
/// referenced by exposures; it is never copied per frame. That is what makes
/// instancing and redraw possible: edit the drawing and every frame that
/// exposes it changes, because frames resolve drawings by id at render time.</summary>
public sealed class FrameDrawing
{
	public string Id;
	public string Name;
	public int Width;
	public int Height;
	public byte[] Rgba;

	public FrameDrawing( string id, string name, int width, int height, byte[] rgba )
	{
		if ( string.IsNullOrWhiteSpace( id ) ) throw new ArgumentException( "A drawing needs an id." );
		if ( width <= 0 || height <= 0 ) throw new ArgumentException( "A drawing needs a positive size." );
		if ( rgba == null || rgba.Length != width * height * 4 ) throw new ArgumentException( "RGBA must hold exactly one pixel per cell." );
		Id = id;
		Name = name;
		Width = width;
		Height = height;
		Rgba = rgba;
	}

	/// <summary>Redraw mode: replace the drawing's pixels. Exposures are
	/// untouched — they hold ids, not pixels — so every frame exposing this
	/// drawing picks the new version up on the next render/export.</summary>
	public void Redraw( byte[] rgba )
	{
		if ( rgba == null || rgba.Length != Width * Height * 4 ) throw new ArgumentException( "RGBA must hold exactly one pixel per cell." );
		Array.Copy( rgba, Rgba, rgba.Length );
	}

	/// <summary>Redraw a rectangle of the drawing and leave the rest alone.
	/// Bounds are checked; a patch that spills off the drawing is rejected
	/// rather than clipped, so a typo cannot silently paint the wrong place.</summary>
	public void PatchRegion( int x, int y, int width, int height, byte[] rgba )
	{
		if ( width <= 0 || height <= 0 ) throw new ArgumentException( "A patch needs a positive size." );
		if ( x < 0 || y < 0 || x + width > Width || y + height > Height ) throw new ArgumentException( "Patch region is outside the drawing." );
		if ( rgba == null || rgba.Length != width * height * 4 ) throw new ArgumentException( "Patch RGBA must hold exactly one pixel per cell." );
		for ( int row = 0; row < height; row++ )
			Array.Copy( rgba, row * width * 4, Rgba, ( ( y + row ) * Width + x ) * 4, width * 4 );
	}
}

/// <summary>The drawing shelf for a clip: drawings by id, nothing else.
/// The clip itself holds no pixels, only ids, so the shelf is the single
/// place a redraw lands.</summary>
public sealed class FrameDrawingLibrary
{
	readonly Dictionary<string, FrameDrawing> _drawings = new();

	public int Count => _drawings.Count;
	public IEnumerable<FrameDrawing> All => _drawings.Values;

	public void Add( FrameDrawing drawing )
	{
		if ( drawing == null ) throw new ArgumentNullException( nameof( drawing ) );
		if ( _drawings.ContainsKey( drawing.Id ) ) throw new ArgumentException( $"Drawing '{drawing.Id}' is already in the library." );
		_drawings[drawing.Id] = drawing;
	}

	public FrameDrawing Get( string id )
	{
		if ( id == null || !_drawings.TryGetValue( id, out var drawing ) ) throw new ArgumentException( $"No drawing '{id}' in the library." );
		return drawing;
	}

	public bool Contains( string id ) => id != null && _drawings.ContainsKey( id );

	public void Redraw( string id, byte[] rgba ) => Get( id ).Redraw( rgba );

	public void PatchRegion( string id, int x, int y, int width, int height, byte[] rgba ) =>
		Get( id ).PatchRegion( x, y, width, height, rgba );
}

/// <summary>One exposure: a drawing held on screen for a run of frames.
/// StartFrame is inclusive; Hold is a frame count, never a duration in
/// seconds, so the timing survives an fps change. EndFrame is inclusive.</summary>
public sealed class FrameExposure
{
	public string DrawingId;
	public int StartFrame;
	public int Hold;
	public FrameRole Role;

	public FrameExposure( string drawingId, int startFrame, int hold, FrameRole role )
	{
		if ( string.IsNullOrWhiteSpace( drawingId ) ) throw new ArgumentException( "An exposure needs a drawing id." );
		if ( startFrame < 0 ) throw new ArgumentException( "An exposure cannot start before frame 0." );
		if ( hold < 1 ) throw new ArgumentException( "An exposure must hold at least one frame." );
		DrawingId = drawingId;
		StartFrame = startFrame;
		Hold = hold;
		Role = role;
	}

	public int EndFrame => StartFrame + Hold - 1;
	public bool Covers( int frame ) => frame >= StartFrame && frame <= EndFrame;
}

/// <summary>One layer's exposure sheet: an ordered, non-overlapping run of
/// exposures. Overlap is rejected at add time, never resolved silently,
/// because two drawings on one layer at one frame is a sheet error, not a
/// blend. Gaps are legal: a gap is a blank frame, not an error.</summary>
public sealed class DrawingTrack
{
	public string Name;
	public List<FrameExposure> Exposures = new();

	public DrawingTrack( string name ) => Name = name;

	public void Expose( string drawingId, int startFrame, int hold, FrameRole role )
	{
		var exposure = new FrameExposure( drawingId, startFrame, hold, role );
		foreach ( var other in Exposures )
			if ( exposure.StartFrame <= other.EndFrame && other.StartFrame <= exposure.EndFrame )
				throw new ArgumentException( $"Exposure of '{drawingId}' at frame {startFrame} overlaps the '{other.DrawingId}' exposure on track '{Name}'." );
		Exposures.Add( exposure );
		Exposures.Sort( ( a, b ) => a.StartFrame.CompareTo( b.StartFrame ) );
	}

	/// <summary>Animating on twos: each drawing in order holds for two
	/// frames, starting at startFrame. At 24 fps that is 12 drawings a
	/// second, the traditional hand-drawn cadence.</summary>
	public void ExposeOnTwos( IEnumerable<string> drawingIds, int startFrame, FrameRole role )
	{
		int frame = startFrame;
		foreach ( var id in drawingIds )
		{
			Expose( id, frame, 2, role );
			frame += 2;
		}
	}

	public string DrawingAt( int frame )
	{
		foreach ( var exposure in Exposures )
			if ( exposure.Covers( frame ) ) return exposure.DrawingId;
		return null;
	}

	public int EndFrame => Exposures.Count == 0 ? -1 : Exposures.Max( e => e.EndFrame );
}

/// <summary>A rotoscoping reference: a video or an image sequence to draw
/// OVER. It is reference only, by construction: it is not a drawing, it
/// never appears in a frame composition, it is never composited into an
/// exported frame, and the manifest records it as not baked. A reference
/// that ended up in the drawing would be a rights and cleanliness problem,
/// so the model makes that impossible instead of merely discouraged.</summary>
public sealed class RotoscopeReference
{
	public string SourcePath;
	public string Kind;
	public int FrameCount;
	public int StartFrame;
	public float Opacity;

	public RotoscopeReference( string sourcePath, string kind, int frameCount, int startFrame, float opacity )
	{
		if ( string.IsNullOrWhiteSpace( sourcePath ) ) throw new ArgumentException( "A reference needs a source path." );
		if ( kind != "image-sequence" && kind != "video" ) throw new ArgumentException( "Reference kind is 'image-sequence' or 'video'." );
		if ( frameCount < 1 ) throw new ArgumentException( "A reference needs at least one frame." );
		if ( startFrame < 0 ) throw new ArgumentException( "A reference cannot start before frame 0." );
		if ( opacity < 0f || opacity > 1f ) throw new ArgumentException( "Reference opacity must be between 0 and 1." );
		SourcePath = sourcePath;
		Kind = kind;
		FrameCount = frameCount;
		StartFrame = startFrame;
		Opacity = opacity;
	}
}

/// <summary>A hand-drawn frame animation: per-layer drawing tracks over a
/// frame grid, at an fps, optionally looping. This is an exposure model,
/// not a keyframe list: the unit of time is a frame, the unit of content
/// is a drawing exposure, and a drawing may be exposed many times.
/// Track order is draw order: later tracks paint over earlier ones.</summary>
public sealed class FrameAnimClip
{
	public string Name;
	public int Fps = 24;
	public bool Loop = true;
	public int Width;
	public int Height;
	public List<DrawingTrack> Tracks = new();
	public RotoscopeReference Reference;

	public FrameAnimClip( string name, int width, int height )
	{
		Name = name;
		Width = width;
		Height = height;
	}

	public DrawingTrack AddTrack( string name )
	{
		var track = new DrawingTrack( name );
		Tracks.Add( track );
		return track;
	}

	public int FrameCount => Tracks.Count == 0 ? 0 : Tracks.Max( t => t.EndFrame ) + 1;
	public double DurationSeconds => Fps > 0 ? FrameCount / (double)Fps : 0;
	public double TimeAt( int frame ) => Fps > 0 ? frame / (double)Fps : 0;

	/// <summary>The drawings visible at a frame, in track (draw) order.
/// A track with no exposure covering the frame contributes nothing.</summary>
	public List<KeyValuePair<string, string>> CompositionAt( int frame )
	{
		var composition = new List<KeyValuePair<string, string>>();
		foreach ( var track in Tracks )
		{
			var drawingId = track.DrawingAt( frame );
			if ( drawingId != null ) composition.Add( new KeyValuePair<string, string>( track.Name, drawingId ) );
		}
		return composition;
	}

	public string DrawingAt( string trackName, int frame )
	{
		foreach ( var track in Tracks )
			if ( track.Name == trackName ) return track.DrawingAt( frame );
		return null;
	}

	public List<string> DistinctDrawings()
	{
		var seen = new HashSet<string>();
		foreach ( var track in Tracks )
			foreach ( var exposure in track.Exposures )
				seen.Add( exposure.DrawingId );
		var ids = seen.ToList();
		ids.Sort( StringComparer.Ordinal );
		return ids;
	}

	/// <summary>Everything that would make an export wrong, as strings so a
	/// caller can show them instead of catching. An empty list is a clean
	/// clip. Unknown drawing ids are checked against the library, because
	/// an exposure naming a drawing that does not exist is the one error
	/// the model itself cannot catch at add time.</summary>
	public List<string> Validate( FrameDrawingLibrary library )
	{
		var errors = new List<string>();
		if ( Fps <= 0 ) errors.Add( "Fps must be positive." );
		if ( Width <= 0 || Height <= 0 ) errors.Add( "Clip size must be positive." );
		if ( library == null ) { errors.Add( "A library is required to check drawing ids." ); return errors; }
		foreach ( var track in Tracks )
			foreach ( var exposure in track.Exposures )
			{
				if ( !library.Contains( exposure.DrawingId ) )
					errors.Add( $"Track '{track.Name}' exposes unknown drawing '{exposure.DrawingId}'." );
				else
				{
					var drawing = library.Get( exposure.DrawingId );
					if ( drawing.Width != Width || drawing.Height != Height )
						errors.Add( $"Drawing '{exposure.DrawingId}' is {drawing.Width}x{drawing.Height}, clip is {Width}x{Height}." );
				}
			}
		return errors;
	}
}
