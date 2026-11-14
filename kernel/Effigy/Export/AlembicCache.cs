using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Effigy;

namespace Effigy.Export;

/// <summary>Thrown when an Alembic subset cache file is malformed; the message names the defect.</summary>
public sealed class AlembicFormatException : Exception
{
	public AlembicFormatException( string message ) : base( message ) { }
}

/// <summary>
/// Time mapping for baked geometry (godlike-usd-stage-alembic-materialx).
/// The FPS is explicit on the cache and is the only source of truth —
/// Blender converts via scene FPS, never a hardcoded 24, and so do we.
/// A cache built at 30 fps and read as 24 would silently retime every
/// sample, so the FPS travels in the file header and a mismatch is an
/// error, not a guess.
/// </summary>
public sealed class AlembicTimeMap
{
	public double Fps { get; }

	public AlembicTimeMap( double fps )
	{
		if ( double.IsNaN( fps ) || double.IsInfinity( fps ) || fps <= 0 )
			throw new ArgumentException( "Alembic FPS must be a positive finite number (got " + fps + "); it comes from the scene, never a hardcoded 24" );
		Fps = fps;
	}

	/// <summary>Seconds for an integer frame at this cache's FPS.</summary>
	public double FrameToSeconds( int frame ) => frame / Fps;

	/// <summary>Nearest integer frame for a time in seconds (halves round away from zero).</summary>
	public int SecondsToFrame( double seconds ) => (int)Math.Round( seconds * Fps, MidpointRounding.AwayFromZero );
}

/// <summary>One baked frame: an integer frame plus the vertex positions at that frame.</summary>
public sealed class AlembicSample
{
	public int Frame { get; }
	public Vec3[] Positions { get; }

	public AlembicSample( int frame, Vec3[] positions )
	{
		Frame = frame;
		Positions = positions ?? throw new ArgumentNullException( nameof( positions ) );
	}
}

/// <summary>
/// Sampled-geometry cache in the Alembic spirit (godlike-usd-stage-alembic-materialx).
///
/// HONEST BOUNDARY: this is the Effigy subset container (magic
/// "EFFIGY-ABC1"), NOT a native Ogawa/HDF5 .abc file. Native Alembic
/// read/write needs the Alembic C++ library, which is the packaging task's
/// job (package-universal-desktop-cli-native-dependencies-and-headless-renderer).
/// What IS real here: the data model (per-frame baked positions), the
/// FPS-correct time mapping, the binary container with validation, and the
/// refusal rules. A file this writer produces is labelled by its magic so
/// no tool can mistake it for a native .abc.
///
/// Refusals (named, never silent): empty cache, inconsistent vertex counts
/// across frames (a topology change), non-positive FPS, bad magic/version.
/// Not stored in this slice: per-frame UVs, normals, vertex colours,
/// topology changes, camera/light samples.
/// </summary>
public sealed class AlembicCache
{
	public const string Magic = "EFFIGY-ABC1";
	public const int Version = 1;

	public string ObjectPath { get; }
	public double Fps { get; }
	public AlembicTimeMap TimeMap { get; }
	public List<AlembicSample> Samples { get; } = new List<AlembicSample>();

	public static IReadOnlyList<string> KnownLimitations => new List<string>
	{
		"Native Ogawa/HDF5 .abc containers are not read or written; this is the Effigy subset container (magic EFFIGY-ABC1).",
		"Per-frame topology change is refused (vertex count must be constant across samples).",
		"UVs, normals and vertex colours are not stored per frame in this slice.",
		"Samples are nearest-frame lookups; no interpolation between frames in this slice.",
		"Camera, light and transform samples are not stored; geometry only.",
	};

	public AlembicCache( string objectPath, double fps )
	{
		if ( string.IsNullOrEmpty( objectPath ) ) throw new ArgumentException( "Alembic object path is empty" );
		ObjectPath = objectPath;
		TimeMap = new AlembicTimeMap( fps );
		Fps = fps;
	}

	/// <summary>Add a baked frame. Samples stay sorted by frame. A vertex count that differs from the first sample is a topology change and is refused, naming the frame.</summary>
	public void AddSample( int frame, Vec3[] positions )
	{
		if ( positions == null || positions.Length == 0 ) throw new ArgumentException( "Alembic sample at frame " + frame + " has no positions" );
		if ( Samples.Count > 0 && positions.Length != Samples[0].Positions.Length )
			throw new InvalidOperationException( "Alembic cache '" + ObjectPath + "': topology change at frame " + frame + " (expected " + Samples[0].Positions.Length + " vertices, got " + positions.Length + "); per-frame topology change is not supported" );
		var sample = new AlembicSample( frame, (Vec3[])positions.Clone() );
		int at = Samples.Count;
		while ( at > 0 && Samples[at - 1].Frame > frame ) at--;
		Samples.Insert( at, sample );
	}

	/// <summary>Nearest sample to a time in seconds (ties go to the earlier frame).</summary>
	public AlembicSample SampleAtTime( double seconds )
	{
		if ( Samples.Count == 0 ) throw new InvalidOperationException( "Alembic cache '" + ObjectPath + "' has no samples" );
		int frame = TimeMap.SecondsToFrame( seconds );
		AlembicSample best = Samples[0];
		int bestDistance = Math.Abs( best.Frame - frame );
		for ( int i = 1; i < Samples.Count; i++ )
		{
			int distance = Math.Abs( Samples[i].Frame - frame );
			if ( distance < bestDistance ) { best = Samples[i]; bestDistance = distance; }
		}
		return best;
	}

	/// <summary>Positions of the nearest sample to a time in seconds.</summary>
	public Vec3[] PositionsAtTime( double seconds ) => SampleAtTime( seconds ).Positions;

	public void Write( string filePath )
	{
		if ( string.IsNullOrEmpty( filePath ) ) throw new ArgumentException( "Alembic output path is empty" );
		if ( Samples.Count == 0 ) throw new InvalidOperationException( "Alembic cache '" + ObjectPath + "' refuses to write an empty cache: an empty export is not an export" );
		var dir = Path.GetDirectoryName( filePath );
		if ( !string.IsNullOrEmpty( dir ) ) Directory.CreateDirectory( dir );
		using ( var fs = File.Create( filePath ) )
		using ( var writer = new BinaryWriter( fs ) )
		{
			writer.Write( Encoding.ASCII.GetBytes( Magic ) );
			writer.Write( Version );
			writer.Write( Fps );
			byte[] pathBytes = Encoding.UTF8.GetBytes( ObjectPath );
			writer.Write( pathBytes.Length );
			writer.Write( pathBytes );
			writer.Write( Samples.Count );
			foreach ( var sample in Samples )
			{
				writer.Write( sample.Frame );
				writer.Write( sample.Positions.Length );
				foreach ( var p in sample.Positions )
				{
					writer.Write( p.x );
					writer.Write( p.y );
					writer.Write( p.z );
				}
			}
		}
	}

	public static AlembicCache Read( string filePath )
	{
		if ( string.IsNullOrEmpty( filePath ) ) throw new ArgumentException( "Alembic input path is empty" );
		if ( !File.Exists( filePath ) ) throw new FileNotFoundException( "Alembic cache not found: " + filePath );
		using ( var fs = File.OpenRead( filePath ) )
		using ( var reader = new BinaryReader( fs ) )
		{
			byte[] magic = reader.ReadBytes( Magic.Length );
			string magicText = Encoding.ASCII.GetString( magic );
			if ( magicText != Magic )
				throw new AlembicFormatException( "not an Effigy Alembic subset cache (magic '" + magicText + "', expected '" + Magic + "'); native Ogawa .abc is not supported in this slice" );
			int version = reader.ReadInt32();
			if ( version != Version )
				throw new AlembicFormatException( "unsupported Effigy Alembic cache version " + version + " (expected " + Version + ")" );
			double fps = reader.ReadDouble();
			if ( double.IsNaN( fps ) || double.IsInfinity( fps ) || fps <= 0 )
				throw new AlembicFormatException( "Alembic cache has a non-positive FPS (" + fps + ")" );
			int pathLength = reader.ReadInt32();
			if ( pathLength < 0 || pathLength > 1_000_000 )
				throw new AlembicFormatException( "Alembic cache has an implausible object path length " + pathLength );
			string objectPath = Encoding.UTF8.GetString( reader.ReadBytes( pathLength ) );
			int sampleCount = reader.ReadInt32();
			if ( sampleCount < 0 || sampleCount > 1_000_000 )
				throw new AlembicFormatException( "Alembic cache has an implausible sample count " + sampleCount );
			var cache = new AlembicCache( objectPath, fps );
			int vertexCount = -1;
			for ( int s = 0; s < sampleCount; s++ )
			{
				int frame = reader.ReadInt32();
				int count = reader.ReadInt32();
				if ( count <= 0 || count > 10_000_000 )
					throw new AlembicFormatException( "Alembic cache sample at frame " + frame + " has an implausible vertex count " + count );
				if ( vertexCount < 0 ) vertexCount = count;
				if ( count != vertexCount )
					throw new AlembicFormatException( "Alembic cache '" + objectPath + "': topology change at frame " + frame + " (expected " + vertexCount + " vertices, got " + count + ")" );
				var positions = new Vec3[count];
				for ( int i = 0; i < count; i++ ) positions[i] = new Vec3( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
				cache.Samples.Add( new AlembicSample( frame, positions ) );
			}
			cache.Samples.Sort( ( a, b ) => a.Frame.CompareTo( b.Frame ) );
			return cache;
		}
	}
}
