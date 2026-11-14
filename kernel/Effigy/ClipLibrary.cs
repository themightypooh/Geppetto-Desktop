using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>Game-clip pipeline, part 1: the named clip library
/// (<c>godlike-game-clip-pipeline</c>, Wave 2 P1).
///
/// A library entry names one clip and says how it plays: trim a frame window out of
/// the source, scale its speed, repeat it. <see cref="Resolve"/> builds the played
/// clip as a new <see cref="RigAnimFile"/> — the source is never rewritten, so one
/// source feeds a walk, a walk looped twice, and a half-speed walk without copies.
///
/// Load is a cheap scan: <see cref="ClipLibrary.LoadFolder"/> reads only the JSON
/// header of each <c>.riganim</c> file (frame rate + frame count) and defers the
/// full <see cref="RigAnimFile.ReadFile"/> until the entry is first used. A folder
/// of 50 clips loads as a directory scan plus 50 small header reads, not 50 decodes;
/// the wall-clock for that load is reported, not claimed — the final build measures it.
///
/// What Resolve carries: bone/part/camera/IK-goal/IK-weight/morph tracks (keys and
/// channel curves), morph events and marks. What it does NOT carry: the clip's IK
/// setup, constraints, limits, goal spaces, objects, props, drivers and collision
/// (those belong to the source rig, not to one played clip) — a resolved clip plays
/// on the same skeleton the source was authored for. Root-motion settings ride
/// through unchanged; for a repeated clip the caller should re-extract root motion
/// afterwards, because the carried settings describe one cycle, not the repeat.</summary>
public sealed class ClipLibraryEntry
{
	/// <summary>The name the library and the export use. Unique inside one library.</summary>
	public string Name;

	/// <summary>The file the entry was scanned from, or "" for an in-memory clip.</summary>
	public string SourcePath = "";

	/// <summary>First frame of the played window, inclusive; -1 means the clip's first frame.</summary>
	public int TrimIn = -1;

	/// <summary>Last frame of the played window, inclusive; -1 means the clip's last frame.</summary>
	public int TrimOut = -1;

	/// <summary>Playback speed: 2 plays twice as fast (half the frames), 0.5 twice as slow.
/// Keys are re-timed to whole frames, so a scale that lands keys between frames rounds them.</summary>
	public float Scale = 1f;

	/// <summary>How many times the trimmed window repeats, back to back. 1 is once.</summary>
	public int Repeat = 1;

	/// <summary>Frame rate read from the file header at scan time (0 when unknown).</summary>
	public int HeaderFrameRate;

	/// <summary>Frame count read from the file header at scan time (0 when unknown).</summary>
	public int HeaderFrameCount;

	readonly Func<RigAnimFile> _loader;
	RigAnimFile _clip;

	public ClipLibraryEntry( string name, RigAnimFile clip )
	{
		Name = name ?? throw new ArgumentNullException( nameof( name ) );
		_clip = clip ?? throw new ArgumentNullException( nameof( clip ) );
		_loader = () => _clip;
		HeaderFrameRate = clip.FrameRate;
		HeaderFrameCount = clip.FrameCount;
	}

	public ClipLibraryEntry( string name, string sourcePath )
	{
		Name = name ?? throw new ArgumentNullException( nameof( name ) );
		SourcePath = sourcePath ?? throw new ArgumentNullException( nameof( sourcePath ) );
		_loader = () => RigAnimFile.ReadFile( SourcePath );
	}

	/// <summary>True once the source clip has been decoded (in-memory entries start loaded).</summary>
	public bool IsLoaded => _clip is not null;

	/// <summary>The source clip, decoded on first use and cached after that.</summary>
	public RigAnimFile Source => _clip ??= _loader();

	/// <summary>The clip as it plays: trimmed, re-timed and repeated, as a new file.
/// Throws when the scale is not positive, the repeat is below 1, or the trim window is empty.</summary>
	public RigAnimFile Resolve()
	{
		var source = Source;

		if ( Scale <= 0f )
			throw new ArgumentException( $"clip '{Name}' has scale {Scale}: scale must be positive", nameof( Scale ) );
		if ( Repeat < 1 )
			throw new ArgumentException( $"clip '{Name}' has repeat {Repeat}: repeat must be at least 1", nameof( Repeat ) );

		var last = source.LastFrame;
		var inFrame = TrimIn < 0 ? 0 : Math.Min( TrimIn, last );
		var outFrame = TrimOut < 0 ? last : Math.Min( TrimOut, last );

		if ( inFrame > outFrame )
			throw new InvalidOperationException( $"clip '{Name}' trim {TrimIn}..{TrimOut} is empty on a clip ending at frame {last}" );

		var span = (int)MathF.Round( ( outFrame - inFrame ) / Scale );
		var played = new RigAnimFile
		{
			SourceModel = source.SourceModel,
			RigAsset = source.RigAsset,
			FrameRate = source.FrameRate,
			FrameCount = ( span + 1 ) * Repeat,
			RootMotion = source.RootMotion,
			PlayInPlace = source.PlayInPlace,
		};

		int Map( int frame, int rep ) => rep * ( span + 1 ) + (int)MathF.Round( ( frame - inFrame ) / Scale );

		foreach ( var track in source.Tracks )
			played.Tracks.Add( ClipTrackCopy.Remap( track, f => Map( f, 0 ), inFrame, outFrame, Repeat, span ) );

		// Events and marks follow the same window: an event outside it is dropped,
		// one straddling it is clamped to it, a mark outside it is dropped.
		foreach ( var e in source.MorphEvents )
		{
			if ( e.End < inFrame || e.Start > outFrame )
				continue;
			var start = Math.Max( e.Start, inFrame );
			var end = Math.Min( e.End, outFrame );

			for ( var rep = 0; rep < Repeat; rep++ )
				played.MorphEvents.Add( new RigMorphEvent
				{
					Morph = e.Morph,
					Start = Map( start, rep ),
					End = Map( end, rep ),
					FadeIn = e.FadeIn,
					FadeOut = e.FadeOut,
					Value = e.Value,
				} );
		}

		if ( source.Marks is not null )
		{
			played.Marks = new List<RigAnimMark>();
			foreach ( var m in source.Marks )
			{
				if ( m.Frame < inFrame || m.Frame > outFrame )
					continue;
				for ( var rep = 0; rep < Repeat; rep++ )
					played.Marks.Add( new RigAnimMark { Name = m.Name, Frame = Map( m.Frame, rep ), Note = m.Note } );
			}
		}

		return played;
	}

	public override string ToString() =>
		$"'{Name}' trim {TrimIn}..{TrimOut} scale {Scale} repeat {Repeat}" +
		( SourcePath.Length > 0 ? $" from {SourcePath}" : "" );
}

/// <summary>What a folder scan found: counts, skips with reasons, and the wall-clock
/// the scan itself took. The scan never fails the whole load for one bad file.</summary>
public sealed class ClipLibraryLoadReport
{
	public int FilesFound;
	public int Added;
	public int Skipped;
	public List<string> SkippedReasons = new();
	public long ElapsedMilliseconds;
	public string Error;
	public bool Ok => Error is null;

	public override string ToString() =>
		Error ?? $"clip library: {Added} clip(s) from {FilesFound} file(s), {Skipped} skipped, {ElapsedMilliseconds} ms";
}

/// <summary>Named clips: one source clip per name, each with its own trim/scale/repeat.
/// Names are matched case-insensitively; adding a second entry with the same name is an error.</summary>
public sealed class ClipLibrary
{
	public List<ClipLibraryEntry> Entries = new();

	public int Count => Entries.Count;

	public void Add( ClipLibraryEntry entry )
	{
		if ( entry is null )
			throw new ArgumentNullException( nameof( entry ) );
		if ( Find( entry.Name ) is not null )
			throw new ArgumentException( $"the library already has a clip named '{entry.Name}'", nameof( entry ) );
		Entries.Add( entry );
	}

	public ClipLibraryEntry Find( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return null;
		return Entries.FirstOrDefault( e => string.Equals( e.Name, name, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>Scan a folder for <c>.riganim</c> files and add one entry per file, in
	/// file-name order. Each entry reads only the file header here (frame rate and frame
	/// count); the full clip decodes lazily on first use. Files that do not parse, and
	/// names already in the library, are skipped with a reason in the report — the scan
	/// itself never throws for one bad file.</summary>
	public ClipLibraryLoadReport LoadFolder( string folder )
	{
		var report = new ClipLibraryLoadReport();
		var clock = Stopwatch.StartNew();

		if ( string.IsNullOrWhiteSpace( folder ) || !Directory.Exists( folder ) )
		{
			report.Error = $"no clip folder '{folder}'";
			return report;
		}

		var files = Directory.GetFiles( folder, "*.riganim" ).OrderBy( f => f, StringComparer.OrdinalIgnoreCase ).ToArray();
		report.FilesFound = files.Length;

		foreach ( var file in files )
		{
			var name = Path.GetFileNameWithoutExtension( file );

			if ( Find( name ) is not null )
			{
				report.Skipped++;
				report.SkippedReasons.Add( $"'{name}': already in the library" );
				continue;
			}

			try
			{
				using var doc = JsonDocument.Parse( File.ReadAllText( file ) );
				var root = doc.RootElement;
				var rate = root.TryGetProperty( "AnimationSpeed", out var speed ) && speed.ValueKind == JsonValueKind.Number
					? (int)MathF.Round( speed.GetSingle() ) : 30;
				var count = root.TryGetProperty( "FrameCount", out var frames ) && frames.ValueKind == JsonValueKind.Number
					? frames.GetInt32() : 0;
				Entries.Add( new ClipLibraryEntry( name, file ) { HeaderFrameRate = rate, HeaderFrameCount = count } );
				report.Added++;
			}
			catch ( Exception ex )
			{
				report.Skipped++;
				report.SkippedReasons.Add( $"'{name}': {ex.Message}" );
			}
		}

		report.ElapsedMilliseconds = clock.ElapsedMilliseconds;
		return report;
	}
}

/// <summary>Deep copies of <see cref="RigTrack"/>s: keys, channel curves and the
/// modifier list. Shared by <see cref="ClipLibraryEntry.Resolve"/> (re-timed copies)
/// and <see cref="ControlRigBaker"/> (straight copies).</summary>
internal static class ClipTrackCopy
{
	public static RigTrack DeepCopy( RigTrack source )
	{
		var copy = new RigTrack { Name = source.Name, Kind = source.Kind };

		foreach ( var key in source.Keys )
			copy.Keys.Add( new RigKey
			{
				Frame = key.Frame,
				Local = key.Local,
				Ease = key.Ease,
				TangentIn = key.TangentIn?.ToArray(),
				TangentOut = key.TangentOut?.ToArray(),
			} );

		foreach ( var (channel, keys) in source.ChannelCurves )
			copy.ChannelCurves[channel] = keys.Select( k => new ScalarKey( k.Frame, k.Value ) ).ToList();

		copy.Modifiers.AddRange( source.Modifiers );
		return copy;
	}

	/// <summary>Copy a track through a frame map, keeping only keys inside
	/// <paramref name="minFrame"/>..<paramref name="maxFrame"/> and repeating the
	/// mapped window <paramref name="repeat"/> times. Two source keys that land on
	/// one frame collapse to the later one — a track must not key twice on a frame.</summary>
	public static RigTrack Remap( RigTrack source, Func<int, int> mapFrame, int minFrame, int maxFrame, int repeat, int span )
	{
		var copy = new RigTrack { Name = source.Name, Kind = source.Kind };

		for ( var rep = 0; rep < repeat; rep++ )
		{
			var keys = new SortedDictionary<int, RigKey>();

			foreach ( var key in source.Keys )
			{
				if ( key.Frame < minFrame || key.Frame > maxFrame )
					continue;
				var frame = mapFrame( key.Frame ) + rep * ( span + 1 );
				keys[frame] = new RigKey
				{
					Frame = frame,
					Local = key.Local,
					Ease = key.Ease,
					TangentIn = key.TangentIn?.ToArray(),
					TangentOut = key.TangentOut?.ToArray(),
				};
			}

			copy.Keys.AddRange( keys.Values );
		}

		for ( var rep = 0; rep < repeat; rep++ )
		{
			foreach ( var (channel, keys) in source.ChannelCurves )
			{
				var remapped = new SortedDictionary<int, ScalarKey>();

				foreach ( var key in keys )
				{
					if ( key.Frame < minFrame || key.Frame > maxFrame )
						continue;
					var frame = mapFrame( key.Frame ) + rep * ( span + 1 );
					remapped[frame] = new ScalarKey( frame, key.Value );
				}

				if ( !copy.ChannelCurves.TryGetValue( channel, out var list ) )
					copy.ChannelCurves[channel] = list = new List<ScalarKey>();
				list.AddRange( remapped.Values );
			}
		}

		copy.Modifiers.AddRange( source.Modifiers );
		return copy;
	}
}
