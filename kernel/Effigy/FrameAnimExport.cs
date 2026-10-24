using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>What one export produced, so a caller can check it instead of
/// trusting it: one PNG per frame, in order, plus the manifest path.</summary>
public sealed class FrameAnimExportResult
{
	public List<string> FrameFiles = new();
	public string ManifestPath = "";
	public int FrameCount;
}

/// <summary>Exports a frame animation as a PNG sequence plus a timing /
/// exposure manifest. Deliberately NOT an animated-container encoder:
/// GIF, APNG and WebM are a follow-up lane that consumes this sequence.
/// Compositing is source-over, track order is draw order, and the
/// rotoscope reference is never composited — it is listed in the manifest
/// with bakedIntoDrawings=false so no downstream tool mistakes it for
/// content. Frames resolve drawings by id at export time, so a redraw
/// between exports changes the next sequence without touching the clip.</summary>
public static class FrameAnimExporter
{
	public static FrameAnimExportResult Export( FrameAnimClip clip, FrameDrawingLibrary library, string outputDir )
	{
		if ( clip == null ) throw new ArgumentNullException( nameof( clip ) );
		if ( library == null ) throw new ArgumentNullException( nameof( library ) );
		var errors = clip.Validate( library );
		if ( errors.Count > 0 ) throw new InvalidOperationException( "Cannot export an invalid clip: " + string.Join( " ", errors ) );

		Directory.CreateDirectory( outputDir );
		var result = new FrameAnimExportResult { FrameCount = clip.FrameCount };
		var canvas = new byte[clip.Width * clip.Height * 4];

		for ( int frame = 0; frame < clip.FrameCount; frame++ )
		{
			Array.Clear( canvas, 0, canvas.Length );
			foreach ( var pair in clip.CompositionAt( frame ) )
				CompositeOver( canvas, library.Get( pair.Value ).Rgba );
			var path = Path.Combine( outputDir, $"frame_{frame:D4}.png" );
			PngWriter.WriteFileRgba( path, canvas, clip.Width, clip.Height );
			result.FrameFiles.Add( path );
		}

		result.ManifestPath = Path.Combine( outputDir, "manifest.json" );
		WriteManifest( clip, result.ManifestPath );
		return result;
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

	static void WriteManifest( FrameAnimClip clip, string path )
	{
		using var stream = File.Create( path );
		using var writer = new Utf8JsonWriter( stream, new JsonWriterOptions { Indented = true } );
		writer.WriteStartObject();
		writer.WriteString( "format", "frame-anim/1" );
		writer.WriteString( "name", clip.Name );
		writer.WriteNumber( "fps", clip.Fps );
		writer.WriteBoolean( "loop", clip.Loop );
		writer.WriteNumber( "width", clip.Width );
		writer.WriteNumber( "height", clip.Height );
		writer.WriteNumber( "frameCount", clip.FrameCount );
		writer.WriteNumber( "durationSeconds", clip.DurationSeconds );
		writer.WriteString( "framesNote", "One PNG per frame, frame_0000.png onward. No animated container is produced by this lane." );

		writer.WritePropertyName( "tracks" );
		writer.WriteStartArray();
		foreach ( var track in clip.Tracks )
		{
			writer.WriteStartObject();
			writer.WriteString( "name", track.Name );
			writer.WritePropertyName( "exposures" );
			writer.WriteStartArray();
			foreach ( var exposure in track.Exposures )
			{
				writer.WriteStartObject();
				writer.WriteString( "drawingId", exposure.DrawingId );
				writer.WriteNumber( "startFrame", exposure.StartFrame );
				writer.WriteNumber( "hold", exposure.Hold );
				writer.WriteNumber( "endFrame", exposure.EndFrame );
				writer.WriteString( "role", exposure.Role.ToString() );
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		writer.WriteEndArray();

		writer.WritePropertyName( "frames" );
		writer.WriteStartArray();
		for ( int frame = 0; frame < clip.FrameCount; frame++ )
		{
			writer.WriteStartObject();
			writer.WriteNumber( "frame", frame );
			writer.WriteNumber( "timeSeconds", clip.TimeAt( frame ) );
			writer.WritePropertyName( "drawings" );
			writer.WriteStartArray();
			foreach ( var pair in clip.CompositionAt( frame ) )
			{
				writer.WriteStartObject();
				writer.WriteString( "track", pair.Key );
				writer.WriteString( "drawingId", pair.Value );
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		writer.WriteEndArray();

		if ( clip.Reference != null )
		{
			writer.WritePropertyName( "reference" );
			writer.WriteStartObject();
			writer.WriteString( "sourcePath", clip.Reference.SourcePath );
			writer.WriteString( "kind", clip.Reference.Kind );
			writer.WriteNumber( "frameCount", clip.Reference.FrameCount );
			writer.WriteNumber( "startFrame", clip.Reference.StartFrame );
			writer.WriteNumber( "opacity", clip.Reference.Opacity );
			writer.WriteBoolean( "bakedIntoDrawings", false );
			writer.WriteEndObject();
		}
		writer.WriteEndObject();
	}

	/// <summary>Rebuilds a clip's TIMING from a manifest: fps, loop, size,
	/// tracks and exposures. Drawings are ids only — pixels live in the
	/// library, never in the manifest — so a caller pairs the result with
	/// its own library before rendering or re-exporting.</summary>
	public static FrameAnimClip FromManifest( string manifestPath )
	{
		using var doc = JsonDocument.Parse( File.ReadAllText( manifestPath ) );
		var root = doc.RootElement;
		var clip = new FrameAnimClip(
			root.GetProperty( "name" ).GetString(),
			root.GetProperty( "width" ).GetInt32(),
			root.GetProperty( "height" ).GetInt32() )
		{
			Fps = root.GetProperty( "fps" ).GetInt32(),
			Loop = root.GetProperty( "loop" ).GetBoolean()
		};
		foreach ( var trackJson in root.GetProperty( "tracks" ).EnumerateArray() )
		{
			var track = clip.AddTrack( trackJson.GetProperty( "name" ).GetString() );
			foreach ( var exposureJson in trackJson.GetProperty( "exposures" ).EnumerateArray() )
				track.Expose(
					exposureJson.GetProperty( "drawingId" ).GetString(),
					exposureJson.GetProperty( "startFrame" ).GetInt32(),
					exposureJson.GetProperty( "hold" ).GetInt32(),
					Enum.Parse<FrameRole>( exposureJson.GetProperty( "role" ).GetString() ) );
		}
		if ( root.TryGetProperty( "reference", out var referenceJson ) )
			clip.Reference = new RotoscopeReference(
				referenceJson.GetProperty( "sourcePath" ).GetString(),
				referenceJson.GetProperty( "kind" ).GetString(),
				referenceJson.GetProperty( "frameCount" ).GetInt32(),
				referenceJson.GetProperty( "startFrame" ).GetInt32(),
				(float)referenceJson.GetProperty( "opacity" ).GetDouble() );
		return clip;
	}
}
