using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// Alembic baked-geometry cache (godlike-usd-stage-alembic-materialx, Platform P0).
///
/// The cache model + FPS-correct time mapping are implemented here in pure C#.
/// The .abc binary itself is an HDF5 container: reading/writing it needs the
/// native Alembic backend, which belongs to
/// package-universal-desktop-cli-native-dependencies-and-headless-renderer.
/// Until that lands, AlembicAbcCodec.Unavailable refuses .abc files honestly
/// (LastRoute names the missing backend) — no .abc file is ever written or
/// silently dropped. The headless round-trip test runs against the cache model
/// via its Geppetto interchange JSON (explicitly NOT .abc).
///
/// TIME MAPPING (the Blender rule): Alembic time comes from the scene frame rate,
/// never a hardcoded 24 fps. SecondsAtFrame(frame) = frame / fps; a cache baked
/// at 30 fps played at 24 fps would drift 25% — the fps rides with the cache.
/// </summary>
public sealed class AlembicTimeSampling
{
	/// <summary>Scene frames per second. Required at construction; there is no default 24.</summary>
	public double FramesPerSecond { get; }

	public AlembicTimeSampling( double framesPerSecond )
	{
		if ( !(framesPerSecond > 0.0) || double.IsNaN( framesPerSecond ) || double.IsInfinity( framesPerSecond ) )
			throw new ArgumentOutOfRangeException( nameof( framesPerSecond ), "Alembic time sampling needs a real scene frame rate (frames per second > 0)." );
		FramesPerSecond = framesPerSecond;
	}

	public static AlembicTimeSampling FromSceneFps( double framesPerSecond ) => new( framesPerSecond );

	/// <summary>Time in seconds of a frame number. Frame 0 is time 0.</summary>
	public double SecondsAtFrame( long frame ) => frame / FramesPerSecond;

	/// <summary>Nearest frame number for a time in seconds.</summary>
	public long FrameAtSeconds( double seconds ) => (long)Math.Round( seconds * FramesPerSecond );

	/// <summary>Sample times for a run of frames starting at startFrame.</summary>
	public double[] SampleTimes( long startFrame, int count )
	{
		var times = new double[count];
		for ( var i = 0; i < count; i++ ) times[i] = SecondsAtFrame( startFrame + i );
		return times;
	}
}

/// <summary>One baked frame: a frame number, its time in seconds, and a snapshot of the mesh.</summary>
public sealed class AlembicFrame
{
	public long Frame;
	public double TimeSeconds;
	public PolyMesh Mesh;

	public AlembicFrame Clone() => new() { Frame = Frame, TimeSeconds = TimeSeconds, Mesh = Mesh?.Clone() };
}

/// <summary>
/// A baked-geometry cache: per-frame mesh snapshots under one time sampling.
/// Baked geometry keeps its topology across frames (that is what "baked" means);
/// a frame whose vertex/face count differs is a Validate issue, never silently merged.
/// </summary>
public sealed class AlembicCache
{
	public const string InterchangeFormat = "geppetto-alembic-cache/1";

	public AlembicTimeSampling TimeSampling { get; }
	public List<AlembicFrame> Frames = new();

	public AlembicCache( AlembicTimeSampling timeSampling )
	{
		TimeSampling = timeSampling ?? throw new ArgumentNullException( nameof( timeSampling ) );
	}

	/// <summary>Add a frame; the mesh is cloned, so later edits to the caller's mesh cannot rewrite history.</summary>
	public void AddFrame( long frame, PolyMesh mesh )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		if ( Frames.Any( f => f.Frame == frame ) )
			throw new ArgumentException( $"Alembic cache already has frame {frame}; baked caches hold one sample per frame." );
		var sample = new AlembicFrame { Frame = frame, TimeSeconds = TimeSampling.SecondsAtFrame( frame ), Mesh = mesh.Clone() };
		Frames.Add( sample );
		Frames.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
	}

	/// <summary>Nearest frame to a time in seconds (baked caches are per-frame; nothing is interpolated).</summary>
	public AlembicFrame SampleAtTime( double seconds )
	{
		if ( Frames.Count == 0 ) return null;
		var target = TimeSampling.FrameAtSeconds( seconds );
		AlembicFrame best = null;
		var bestDistance = long.MaxValue;
		foreach ( var frame in Frames )
		{
			var distance = Math.Abs( frame.Frame - target );
			if ( distance < bestDistance ) { bestDistance = distance; best = frame; }
		}
		return best;
	}

	/// <summary>Issues with the cache: empty, non-monotonic times, or topology drift across frames.</summary>
	public List<string> Validate()
	{
		var issues = new List<string>();
		if ( Frames.Count == 0 ) { issues.Add( "cache is empty" ); return issues; }
		for ( var i = 1; i < Frames.Count; i++ )
			if ( Frames[i].TimeSeconds <= Frames[i - 1].TimeSeconds )
				issues.Add( $"frame {Frames[i].Frame} time {Frames[i].TimeSeconds} is not after frame {Frames[i - 1].Frame}" );
		var first = Frames[0].Mesh;
		for ( var i = 1; i < Frames.Count; i++ )
		{
			var mesh = Frames[i].Mesh;
			if ( mesh.VertexCount != first.VertexCount || mesh.FaceCount != first.FaceCount )
				issues.Add( $"frame {Frames[i].Frame} topology drift: {mesh.VertexCount} verts / {mesh.FaceCount} faces vs frame {Frames[0].Frame} {first.VertexCount} / {first.FaceCount}" );
		}
		return issues;
	}

	/// <summary>Geppetto cache interchange JSON (NOT .abc): fps + per-frame meshes. Round-trips FromJson.</summary>
	public string ToJson()
	{
		var frames = new List<object>();
		foreach ( var frame in Frames )
		{
			var positions = new List<float>();
			foreach ( var p in frame.Mesh.Positions ) { positions.Add( p.x ); positions.Add( p.y ); positions.Add( p.z ); }
			var faces = new List<object>();
			foreach ( var f in frame.Mesh.Faces )
			{
				var face = new Dictionary<string, object> { ["indices"] = f.Indices.ToList() };
				if ( f.UVs is not null && f.UVs.Length == f.Count )
				{
					var uvs = new List<float>();
					foreach ( var uv in f.UVs ) { uvs.Add( uv.x ); uvs.Add( uv.y ); }
					face["uvs"] = uvs;
				}
				faces.Add( face );
			}
			frames.Add( new Dictionary<string, object>
			{
				["frame"] = frame.Frame,
				["positions"] = positions,
				["faces"] = faces,
			} );
		}
		var doc = new Dictionary<string, object>
		{
			["format"] = InterchangeFormat,
			["fps"] = TimeSampling.FramesPerSecond,
			["frames"] = frames,
		};
		return JsonSerializer.Serialize( doc );
	}

	public static AlembicCache FromJson( string json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		using var parsed = JsonDocument.Parse( json );
		var root = parsed.RootElement;
		if ( !root.TryGetProperty( "format", out var format ) || format.GetString() != InterchangeFormat )
			throw new FormatException( $"Not a {InterchangeFormat} cache document." );
		var fps = root.GetProperty( "fps" ).GetDouble();
		var cache = new AlembicCache( new AlembicTimeSampling( fps ) );
		foreach ( var frameElement in root.GetProperty( "frames" ).EnumerateArray() )
		{
			var frameNumber = frameElement.GetProperty( "frame" ).GetInt64();
			var mesh = new PolyMesh();
			var positions = frameElement.GetProperty( "positions" ).EnumerateArray().Select( e => (float)e.GetDouble() ).ToArray();
			for ( var i = 0; i + 2 < positions.Length; i += 3 )
				mesh.AddVertex( new Vec3( positions[i], positions[i + 1], positions[i + 2] ) );
			foreach ( var faceElement in frameElement.GetProperty( "faces" ).EnumerateArray() )
			{
				var indices = faceElement.GetProperty( "indices" ).EnumerateArray().Select( e => e.GetInt32() ).ToArray();
				Vec2[] uvs = null;
				if ( faceElement.TryGetProperty( "uvs", out var uvsElement ) )
				{
					var flat = uvsElement.EnumerateArray().Select( e => (float)e.GetDouble() ).ToArray();
					uvs = new Vec2[flat.Length / 2];
					for ( var i = 0; i < uvs.Length; i++ ) uvs[i] = new Vec2( flat[i * 2], flat[i * 2 + 1] );
				}
				mesh.Faces.Add( new Face( indices, uvs ) );
			}
			cache.Frames.Add( new AlembicFrame { Frame = frameNumber, TimeSeconds = cache.TimeSampling.SecondsAtFrame( frameNumber ), Mesh = mesh } );
		}
		cache.Frames.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
		return cache;
	}
}

/// <summary>Binary .abc read/write. Only the unavailable implementation exists until the native backend lands.</summary>
public interface IAlembicAbcCodec
{
	string LastRoute { get; }
	AlembicCache ReadAbc( byte[] abc );
	byte[] WriteAbc( AlembicCache cache );
}

/// <summary>
/// The .abc boundary: honest refusal, ManifoldBoolean-style (LastRoute + a named
/// reason). Callers get a NotSupportedException naming the packaging task that
/// owns the native backend — never an empty cache, never a fake .abc file.
/// </summary>
public static class AlembicAbcCodec
{
	public static IAlembicAbcCodec Unavailable { get; } = new UnavailableCodec();

	sealed class UnavailableCodec : IAlembicAbcCodec
	{
		public string LastRoute => "unavailable: native Alembic backend not bundled (see package-universal-desktop-cli-native-dependencies-and-headless-renderer)";

		public AlembicCache ReadAbc( byte[] abc ) => throw new NotSupportedException(
			"Alembic .abc binary (HDF5) read needs the native Alembic backend, bundled by " +
			"package-universal-desktop-cli-native-dependencies-and-headless-renderer. " +
			"The cache model and FPS time mapping are implemented in AlembicCache; no .abc file is read or silently dropped." );

		public byte[] WriteAbc( AlembicCache cache ) => throw new NotSupportedException(
			"Alembic .abc binary (HDF5) write needs the native Alembic backend, bundled by " +
			"package-universal-desktop-cli-native-dependencies-and-headless-renderer. " +
			"The cache model and FPS time mapping are implemented in AlembicCache; no .abc file is written or silently dropped." );
	}
}
