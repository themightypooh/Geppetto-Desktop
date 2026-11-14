using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Effigy;

/// <summary>
/// Biovision Hierarchy (.bvh) motion-capture import: a BVH file's HIERARCHY section
/// becomes a <see cref="Skeleton"/>, its MOTION section a <see cref="RigAnimFile"/>.
///
/// CONVENTIONS, stated because every BVH parser picks them differently:
/// - A joint's OFFSET is its bind-pose local translation; its rotation channels are the
///   only motion a non-root joint carries, so its local position stays at the offset.
/// - The root's X/Y/Zposition channels ARE its local position per frame (the offset is
///   not added on top - in the files this format comes from, the channel values already
///   carry the rest height).
/// - Rotation channels compose in the order listed: for "Zrotation Xrotation Yrotation"
///   the result is qz * qx * qy (first-listed applied last to the vector), which is the
///   R = Rz * Rx * Ry matrix every BVH reader implements.
/// - Angles are degrees, Y-up, units as-written (no cm-to-unit rescale - the caller owns
///   that; <see cref="SkeletonRetarget.RetargetClip"/> rescales root motion by skeleton
///   height on retarget anyway).
/// - Joint names must be unique (a skeleton cannot hold two bones of one name); anything
///   malformed fails honestly with <see cref="Result.Error"/> instead of a half-built rig.
/// </summary>
public static class BvhImport
{
	public sealed class Result
	{
		public Skeleton Skeleton;
		public RigAnimFile Clip;
		public List<string> Notes = new();
		public string Error;
		public bool Ok => Error is null;
	}

	sealed class Node
	{
		public string Name = "";
		public Vec3 Offset;
		public List<string> Channels = new();
		public List<Node> Children = new();
		public Vec3 EndOffset;
		public bool HasEnd;
	}

	sealed class Lexer
	{
		readonly List<string> _tokens;
		int _pos;

		public Lexer( string text )
		{
			_tokens = text
				.Replace( "{", " { " )
				.Replace( "}", " } " )
				.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries )
				.ToList();
		}

		public bool AtEnd => _pos >= _tokens.Count;
		public int Remaining => _tokens.Count - _pos;
		public string Peek() => AtEnd ? "" : _tokens[_pos];
		public string Next() => AtEnd ? "" : _tokens[_pos++];

		public bool Take( string token )
		{
			if ( Peek() != token )
				return false;
			_pos++;
			return true;
		}
	}

	public static Result Parse( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return new Result { Error = "BVH: empty input." };

		try
		{
			var lx = new Lexer( text );
			if ( !lx.Take( "HIERARCHY" ) )
				return new Result { Error = "BVH: expected HIERARCHY first." };

			var root = ParseNode( lx, expectRoot: true );
			if ( root is null )
				return new Result { Error = "BVH: could not parse the root joint." };

			if ( !lx.Take( "MOTION" ) )
				return new Result { Error = "BVH: expected MOTION after the hierarchy." };

			if ( !lx.Take( "Frames:" ) || !int.TryParse( lx.Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames ) || frames < 0 )
				return new Result { Error = "BVH: expected 'Frames: N'." };

			if ( !lx.Take( "Frame" ) || !lx.Take( "Time:" ) || !float.TryParse( lx.Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dt ) || dt < 0f )
				return new Result { Error = "BVH: expected 'Frame Time: <seconds>'." };

			// Flatten the hierarchy in file order: BVH motion data follows the order the
			// joints appear in the file, which is a pre-order walk of the tree.
			var flat = new List<Node>();
			Flatten( root, flat );

			var channelCount = flat.Sum( n => n.Channels.Count );
			var values = new float[frames * channelCount];
			for ( var i = 0; i < values.Length; i++ )
			{
				if ( lx.AtEnd )
					return new Result { Error = $"BVH: motion data ends early (need {values.Length} values, found {i})." };
				if ( !float.TryParse( lx.Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) || !float.IsFinite( v ) )
					return new Result { Error = $"BVH: motion value {i} is not a finite number." };
				values[i] = v;
			}

			var result = new Result();
			if ( !lx.AtEnd )
				result.Notes.Add( $"BVH: {lx.Remaining} trailing token(s) ignored." );

			// Skeleton: bind pose is offsets with identity rotation.
			var skeleton = new Skeleton();
			var seen = new HashSet<string>( StringComparer.Ordinal );
			var order = new List<Node>();
			var queue = new Queue<(Node Node, int Parent)>();
			queue.Enqueue( (root, -1) );
			while ( queue.Count > 0 )
			{
				var (node, parent) = queue.Dequeue();
				if ( !seen.Add( node.Name ) )
					return new Result { Error = $"BVH: duplicate joint name '{node.Name}'." };
				var firstChild = node.Children.FirstOrDefault();
				var length = firstChild is not null ? firstChild.Offset.Length : node.HasEnd ? node.EndOffset.Length : 0f;
				var local = new RigPose( node.Offset, Quat.Identity, Vec3.One ).ToXform();
				var idx = skeleton.AddBone( node.Name, parent, local, length );
				order.Add( node );
				foreach ( var child in node.Children )
					queue.Enqueue( (child, idx) );
			}

			// Clip: one bone track per joint, one key per frame.
			var clip = new RigAnimFile
			{
				FrameRate = dt > 0f ? Math.Max( 1, (int)MathF.Round( 1f / dt ) ) : 30,
				FrameCount = frames,
			};
			var channelBase = new Dictionary<Node, int>();
			var running = 0;
			foreach ( var node in flat )
			{
				channelBase[node] = running;
				running += node.Channels.Count;
			}
			foreach ( var node in order )
			{
				var track = new RigTrack { Name = node.Name, Kind = RigTrackKind.Bone };
				var baseIdx = channelBase[node];
				for ( var f = 0; f < frames; f++ )
				{
					var pos = node.Offset;
					var rot = Quat.Identity;
					for ( var c = 0; c < node.Channels.Count; c++ )
					{
						var v = values[f * channelCount + baseIdx + c];
						switch ( node.Channels[c] )
						{
							case "Xposition": pos = new Vec3( v, pos.y, pos.z ); break;
							case "Yposition": pos = new Vec3( pos.x, v, pos.z ); break;
							case "Zposition": pos = new Vec3( pos.x, pos.y, v ); break;
							case "Xrotation": rot = rot * Quat.FromAxisAngle( new Vec3( 1, 0, 0 ), v * MathF.PI / 180f ); break;
							case "Yrotation": rot = rot * Quat.FromAxisAngle( new Vec3( 0, 1, 0 ), v * MathF.PI / 180f ); break;
							case "Zrotation": rot = rot * Quat.FromAxisAngle( new Vec3( 0, 0, 1 ), v * MathF.PI / 180f ); break;
							default: break;
						}
					}
					track.Keys.Add( new RigKey { Frame = f, Local = new RigPose( pos, rot, Vec3.One ) } );
				}
				clip.Tracks.Add( track );
			}

			result.Skeleton = skeleton;
			result.Clip = clip;
			result.Notes.Add( $"BVH: {order.Count} bones, {frames} frames at {clip.FrameRate} fps ({channelCount} channels/frame)." );
			if ( order.Any( n => n.Channels.Count == 0 ) )
				result.Notes.Add( "BVH: some joints carry no channels (static in the clip)." );
			return result;
		}
		catch ( Exception e )
		{
			return new Result { Error = $"BVH: {e.Message}" };
		}
	}

	static void Flatten( Node node, List<Node> flat )
	{
		flat.Add( node );
		foreach ( var child in node.Children )
			Flatten( child, flat );
	}

	static Node ParseNode( Lexer lx, bool expectRoot )
	{
		var kind = lx.Next();
		if ( ( expectRoot && kind != "ROOT" ) || ( !expectRoot && kind != "JOINT" ) )
			return null;

		var node = new Node { Name = lx.Next() };
		if ( string.IsNullOrEmpty( node.Name ) || !lx.Take( "{" ) )
			return null;

		// Peek, don't consume: JOINT children recurse back into ParseNode, which reads
		// the keyword itself.
		while ( !lx.AtEnd && lx.Peek() != "}" )
		{
			switch ( lx.Peek() )
			{
				case "OFFSET":
					lx.Next();
					if ( !TryVec3( lx, out node.Offset ) )
						return null;
					break;
				case "CHANNELS":
					lx.Next();
					if ( !int.TryParse( lx.Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count ) || count < 0 )
						return null;
					for ( var i = 0; i < count; i++ )
					{
						var channel = lx.Next();
						if ( string.IsNullOrEmpty( channel ) )
							return null;
						node.Channels.Add( channel );
					}
					break;
				case "JOINT":
					var child = ParseNode( lx, expectRoot: false );
					if ( child is null )
						return null;
					node.Children.Add( child );
					break;
				case "End":
					lx.Next();
					if ( !lx.Take( "Site" ) || !lx.Take( "{" ) )
						return null;
					if ( !lx.Take( "OFFSET" ) || !TryVec3( lx, out node.EndOffset ) )
						return null;
					node.HasEnd = true;
					if ( !lx.Take( "}" ) )
						return null;
					break;
				default:
					return null;
			}
		}

		return lx.Take( "}" ) ? node : null;
	}

	static bool TryVec3( Lexer lx, out Vec3 v )
	{
		v = Vec3.Zero;
		if ( !float.TryParse( lx.Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x ) ||
			!float.TryParse( lx.Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y ) ||
			!float.TryParse( lx.Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
			return false;
		v = new Vec3( x, y, z );
		return true;
	}
}
