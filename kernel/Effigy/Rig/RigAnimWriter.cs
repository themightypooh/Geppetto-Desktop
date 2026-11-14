using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>
/// A <see cref="RigAnimFile"/> back out as a Marionette .riganim - for headless tools that change a
/// clip's keys (the sim's secondary bake, a generator) and want the result opened in Marionette.
///
/// ONLY THE TRACKS ARE THE KERNEL'S. Everything else a clip carries - props, objects, lights,
/// cameras, events, morphs, marks - is copied from <c>baseJson</c> untouched when one is given,
/// because the kernel reads only what it needs of them and writing back its partial picture would
/// throw the rest away. With no base, the other lists are written empty.
///
/// The JSON is the shape Marionette saves (Transform as "x,y,z" strings, enums by name), so a
/// file written here opens in the editor like one it wrote itself.
/// </summary>
public static class RigAnimWriter
{
	/// <summary>True when the clip's marks are exactly what the base's Marks array reads as, so the base
	/// array (with any entries or fields the reader skips) can stay untouched.</summary>
	static bool SameMarks( System.Collections.Generic.List<RigAnimMark> marks, JsonArray baseMarks )
	{
		if ( baseMarks is null ) return false;
		var read = new System.Collections.Generic.List<(string, int, string)>();
		foreach ( var n in baseMarks )
		{
			if ( n is not JsonObject o ) continue;
			string name = o["Name"] is JsonValue nv && nv.TryGetValue<string>( out var ns ) ? ns : "mark";
			int frame = o["Frame"] is JsonValue fv && fv.TryGetValue<double>( out var fd ) ? (int)MathF.Round( (float)fd ) : 0;
			string note = o["Note"] is JsonValue tv && tv.TryGetValue<string>( out var ts ) ? ts : "";
			read.Add( (name, frame, note) );
		}
		var have = marks.Where( m => m is not null ).Select( m => (m.Name ?? "mark", m.Frame, m.Note ?? "") ).ToList();
		return have.SequenceEqual( read );
	}

	public static string Write( RigAnimFile clip, string baseJson = null )
	{
		if ( clip is null ) throw new ArgumentNullException( nameof( clip ) );

		var root = baseJson is not null
			? JsonNode.Parse( baseJson.TrimStart( '﻿' ) )?.AsObject() ?? new JsonObject()
			: new JsonObject
			{
				["ReferenceProps"] = new JsonArray(),
				["Objects"] = new JsonArray(),
				["Lights"] = new JsonArray(),
				["Cameras"] = new JsonArray(),
				["Events"] = new JsonArray(),
				["MorphEvents"] = new JsonArray(),
				["Marks"] = new JsonArray(),
			};

		root["SourceModel"] = clip.SourceModel ?? "";
		root["RigAsset"] = clip.RigAsset ?? "";
		root["AnimationSpeed"] = clip.FrameRate;
		root["FrameCount"] = Math.Max( clip.FrameCount, clip.LastKeyFrame + 1 );

		// Marks the clip read (or was given) win; a clip that never read them keeps the base's.
		if ( clip.Marks is { } marks && !SameMarks( marks, root["Marks"] as JsonArray ) )
		{
			var list = new JsonArray();
			foreach ( var m in marks )
			{
				if ( m is null ) continue;
				list.Add( new JsonObject { ["Name"] = m.Name ?? "mark", ["Frame"] = m.Frame, ["Note"] = m.Note ?? "" } );
			}
			root["Marks"] = list;
		}

		if ( clip.RootMotion is { } motion )
		{
			root["RootMotion"] = new JsonObject
			{
				["Enabled"] = motion.Enabled,
				["Bone"] = motion.Bone ?? "",
				["Source"] = motion.Source ?? "",
				["Mode"] = motion.Mode.ToString(),
				["Linear"] = motion.Linear,
			};
		}

		var tracks = new JsonArray();
		var baseTracks = new System.Collections.Generic.Dictionary<(string, string), JsonObject>();
		if ( root["BoneTracks"] is JsonArray oldTracks )
			foreach ( var node in oldTracks )
				if ( node is JsonObject t && t["BoneName"]?.GetValue<string>() is { } bone )
					baseTracks.TryAdd( (bone, t["Target"]?.GetValue<string>() ?? "Bone"), t );

		foreach ( var track in clip.Tracks )
		{
			var keys = new JsonArray();

			foreach ( var key in track.Keys.OrderBy( k => k.Frame ) )
			{
				var k = new JsonObject
				{
					["Frame"] = key.Frame,
					["Local"] = new JsonObject
					{
						["Position"] = V( key.Local.Position ),
						["Rotation"] = Q( key.Local.Rotation ),
						["Scale"] = V( key.Local.Scale ),
					},
					["Interpolation"] = key.Ease.ToString(),
				};

				if ( key.TangentIn is { Length: RigHermite.Channels } tin )
					k["TangentIn"] = new JsonArray( tin.Select( v => (JsonNode)v ).ToArray() );

				if ( key.TangentOut is { Length: RigHermite.Channels } tout )
					k["TangentOut"] = new JsonArray( tout.Select( v => (JsonNode)v ).ToArray() );

				keys.Add( k );
			}

			var written = new JsonObject
			{
				["BoneName"] = track.Name,
				["Target"] = track.Kind.ToString(),
				["Keyframes"] = keys,
			};

			// The rest of a track (its DisplayName, and whatever Marionette adds later) is the
			// base file's, like everything else outside the keys.
			if ( baseTracks.TryGetValue( (track.Name, track.Kind.ToString()), out var old ) )
				foreach ( var (name, value) in old )
					if ( !written.ContainsKey( name ) ) written[name] = value?.DeepClone();

			tracks.Add( written );
		}

		root["BoneTracks"] = tracks;

		return root.ToJsonString( new JsonSerializerOptions { WriteIndented = true } );
	}

	static string F( float v ) => v.ToString( "R", CultureInfo.InvariantCulture );
	static string V( Vec3 v ) => $"{F( v.x )},{F( v.y )},{F( v.z )}";
	static string Q( Quat q ) => $"{F( q.x )},{F( q.y )},{F( q.z )},{F( q.w )}";
}
