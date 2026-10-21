#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// What kind of placement target is evaluated.
/// </summary>
public enum PlacementTargetKind
{
	Object,
	Part,
	Prop
}

/// <summary>
/// Evaluated placement transform for an object, part, or reference prop at a specific frame.
/// </summary>
public sealed class EvaluatedPart
{
	/// <summary>The qualified name (e.g., "object", "object/part", or "prop").</summary>
	public string Key { get; init; } = "";

	/// <summary>The owner object name, or prop name.</summary>
	public string ObjectName { get; init; } = "";

	/// <summary>The part name within the object, or null for the object itself or a prop.</summary>
	public string? PartName { get; init; }

	/// <summary>Object, Part, or Prop.</summary>
	public PlacementTargetKind Kind { get; init; }

	/// <summary>Composed model/world-space transform after full hierarchy and bone-attachment evaluation.</summary>
	public Xform Transform { get; set; } = Xform.Identity;

	/// <summary>Local transform before parent composition (from keyframe curve or bind pose).</summary>
	public Xform LocalTransform { get; set; } = Xform.Identity;

	/// <summary>Model-space pose representation of <see cref="Transform"/>.</summary>
	public RigPose Pose => RigPose.FromXform( Transform );

	/// <summary>A skeleton bone this placement follows, if any.</summary>
	public string? FollowBone { get; init; }

	/// <summary>The parent part within the same object this part follows, if any.</summary>
	public string? ParentPart { get; init; }

	/// <summary>Whether a circular dependency was detected in this part's parent hierarchy.</summary>
	public bool HasCycle { get; set; }

	/// <summary>Any diagnostic warning or error generated during evaluation (e.g. cycle, missing bone).</summary>
	public string? Diagnostic { get; set; }

	/// <summary>True if evaluated without warnings or missing target diagnostics.</summary>
	public bool IsValid => Diagnostic == null;
}

/// <summary>
/// Evaluated camera shot at a specific frame.
/// Field of view rides in the scale channel of the camera track (or base camera definition).
/// </summary>
public sealed class EvaluatedCamera
{
	public string Name { get; init; } = "";
	public Vec3 Position { get; init; }
	public Quat Rotation { get; init; }
	public float FieldOfView { get; init; } = 80f;

	public Xform Transform => Rotation.ToXform( Position, Vec3.One );
	public RigPose Pose => new( Position, Rotation, new Vec3( FieldOfView, FieldOfView, FieldOfView ) );

	public Vector3 PositionV3 => new( Position.x, Position.y, Position.z );
	public Quaternion RotationQ => new( Rotation.x, Rotation.y, Rotation.z, Rotation.w );
}

/// <summary>
/// Authoring definition of a camera shot in a Marionette clip.
/// </summary>
public sealed class CameraDefinition
{
	public string Name { get; set; } = "camera";
	public bool Enabled { get; set; } = true;
	public bool Export { get; set; }
	public Vec3 Position { get; set; } = new( 96f, -96f, 72f );
	public Quat Rotation { get; set; } = Quat.Identity;
	public float FieldOfView { get; set; } = 80f;
	public float ZNear { get; set; } = 8f;
	public float ZFar { get; set; } = 4096f;

	public static CameraDefinition FromJson( JsonElement element )
	{
		var cam = new CameraDefinition();
		if ( element.TryGetProperty( "Name", out var n ) && n.ValueKind == JsonValueKind.String )
			cam.Name = n.GetString() ?? "camera";
		if ( element.TryGetProperty( "Enabled", out var en ) && (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False) )
			cam.Enabled = en.GetBoolean();
		if ( element.TryGetProperty( "Export", out var exp ) && (exp.ValueKind == JsonValueKind.True || exp.ValueKind == JsonValueKind.False) )
			cam.Export = exp.GetBoolean();

		if ( element.TryGetProperty( "Position", out var posProp ) )
		{
			if ( posProp.ValueKind == JsonValueKind.String && Floats( posProp.GetString(), 3 ) is { } p )
				cam.Position = new Vec3( p[0], p[1], p[2] );
		}
		if ( element.TryGetProperty( "Rotation", out var rotProp ) )
		{
			if ( rotProp.ValueKind == JsonValueKind.String )
			{
				var r = Floats( rotProp.GetString(), 3 );
				if ( r is { Length: >= 4 } )
					cam.Rotation = new Quat( r[0], r[1], r[2], r[3] );
				else if ( r is { Length: 3 } )
					cam.Rotation = Quat.FromAngles( r[0], r[1], r[2] );
			}
		}
		if ( element.TryGetProperty( "FieldOfView", out var fov ) && fov.ValueKind == JsonValueKind.Number )
			cam.FieldOfView = fov.GetSingle();
		if ( element.TryGetProperty( "ZNear", out var zn ) && zn.ValueKind == JsonValueKind.Number )
			cam.ZNear = zn.GetSingle();
		if ( element.TryGetProperty( "ZFar", out var zf ) && zf.ValueKind == JsonValueKind.Number )
			cam.ZFar = zf.GetSingle();

		return cam;
	}

	static float[]? Floats( string? text, int count )
	{
		if ( string.IsNullOrWhiteSpace( text ) ) return null;
		var parts = text.Split( ',' );
		if ( parts.Length < count ) return null;
		var vals = new float[parts.Length];
		for ( int i = 0; i < parts.Length; i++ )
			vals[i] = float.Parse( parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture );
		return vals;
	}
}

/// <summary>
/// Combined result of evaluating all part and camera tracks at a given frame.
/// </summary>
public sealed class PartTrackEvaluationResult
{
	public float Frame { get; init; }
	public IReadOnlyDictionary<string, EvaluatedPart> Placements { get; init; } = new Dictionary<string, EvaluatedPart>();
	public IReadOnlyDictionary<string, EvaluatedCamera> Cameras { get; init; } = new Dictionary<string, EvaluatedCamera>();
	public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

	public EvaluatedPart? GetPart( string key ) => Placements.GetValueOrDefault( key );
	public EvaluatedPart? GetPart( string objectName, string partName ) => Placements.GetValueOrDefault( PartTrackEvaluator.Qualify( objectName, partName ) );
	public EvaluatedCamera? GetCamera( string cameraName ) => Cameras.GetValueOrDefault( cameraName );
	public RigPose? GetPlacementPose( string key ) => Placements.TryGetValue( key, out var p ) ? p.Pose : null;
}

/// <summary>
/// Evaluates object, part, reference prop, and camera tracks from a RigAnimFile.
/// Handles nested composition, follow-bone attachments, cycle detection, and object-space IK goals.
/// </summary>
public static class PartTrackEvaluator
{
	public const char Separator = '/';

	/// <summary>
	/// Qualifies an object name and part name into a single key: "object/part".
	/// </summary>
	public static string Qualify( string objectName, string partName ) =>
		string.IsNullOrEmpty( objectName ) ? partName : $"{objectName}{Separator}{partName}";

	/// <summary>
	/// Splits a qualified key into object name and part name.
	/// </summary>
	public static (string ObjectName, string PartName) Split( string key )
	{
		if ( string.IsNullOrEmpty( key ) ) return ("", "");
		int slash = key.IndexOf( Separator );
		return slash < 0 ? (key, "") : (key[..slash], key[(slash + 1)..]);
	}

	/// <summary>
	/// Full evaluation of all objects, parts, reference props, and cameras at the sampled frame.
	/// </summary>
	public static PartTrackEvaluationResult Evaluate(
		RigAnimFile clip,
		float frame,
		Skeleton? skeleton = null,
		IReadOnlyList<Xform>? boneWorldPoses = null,
		IEnumerable<CameraDefinition>? cameras = null )
	{
		ArgumentNullException.ThrowIfNull( clip );

		var diagnostics = new List<string>();
		var placements = EvaluatePlacements( clip, frame, skeleton, boneWorldPoses, diagnostics );
		var evaluatedCameras = EvaluateCameras( clip, frame, cameras );

		return new PartTrackEvaluationResult
		{
			Frame = frame,
			Placements = placements,
			Cameras = evaluatedCameras,
			Diagnostics = diagnostics
		};
	}

	/// <summary>
	/// Evaluates all object, part, and prop placements from clip.Objects and clip.Props.
	/// </summary>
	public static Dictionary<string, EvaluatedPart> EvaluatePlacements(
		RigAnimFile clip,
		float frame,
		Skeleton? skeleton = null,
		IReadOnlyList<Xform>? boneWorldPoses = null,
		List<string>? diagnostics = null )
	{
		ArgumentNullException.ThrowIfNull( clip );

		if ( skeleton != null && boneWorldPoses == null )
		{
			var localBones = clip.SampleLocal( skeleton, frame );
			boneWorldPoses = RigAnimFile.ModelSpace( skeleton, localBones );
		}

		bool TryGetBone( string? boneName, out Xform boneXform )
		{
			if ( skeleton != null && boneWorldPoses != null && !string.IsNullOrWhiteSpace( boneName ) )
			{
				int idx = skeleton.IndexOf( boneName );
				if ( idx >= 0 && idx < boneWorldPoses.Count )
				{
					boneXform = boneWorldPoses[idx];
					return true;
				}
			}
			boneXform = Xform.Identity;
			return false;
		}

		Xform EvaluateLocal( string trackName, RigPose defaultPose )
		{
			var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Part && t.Name == trackName && t.Keys.Count > 0 );
			return (track != null ? track.Evaluate( frame ) : defaultPose).ToXform();
		}

		var result = new Dictionary<string, EvaluatedPart>( StringComparer.Ordinal );

		// 1. Evaluate Objects
		foreach ( var obj in clip.Objects )
		{
			if ( obj == null || string.IsNullOrWhiteSpace( obj.Name ) ) continue;

			var objLocal = EvaluateLocal( obj.Name, obj.Pose );
			Xform objWorld;
			string? objDiag = null;

			if ( !string.IsNullOrWhiteSpace( obj.FollowBone ) )
			{
				if ( TryGetBone( obj.FollowBone, out var boneXf ) )
				{
					objWorld = boneXf * objLocal;
				}
				else
				{
					objWorld = objLocal;
					objDiag = $"Object '{obj.Name}' references missing follow-bone '{obj.FollowBone}'.";
					diagnostics?.Add( objDiag );
				}
			}
			else
			{
				objWorld = objLocal;
			}

			result[obj.Name] = new EvaluatedPart
			{
				Key = obj.Name,
				ObjectName = obj.Name,
				PartName = null,
				Kind = PlacementTargetKind.Object,
				Transform = objWorld,
				LocalTransform = objLocal,
				FollowBone = obj.FollowBone,
				Diagnostic = objDiag
			};

			// 2. Evaluate Parts within this Object
			if ( obj.Parts != null )
			{
				// Memoized in-object transforms to avoid duplicate work and detect cycles
				var inObjectTransforms = new Dictionary<string, Xform>( StringComparer.Ordinal );
				var visiting = new HashSet<string>( StringComparer.Ordinal );

				foreach ( var part in obj.Parts )
				{
					if ( part == null || string.IsNullOrWhiteSpace( part.Name ) ) continue;
					string qualifiedKey = Qualify( obj.Name, part.Name );
					var partLocal = EvaluateLocal( qualifiedKey, part.Pose );

					// Hierarchy resolution:
					// Check if following a parent part
					if ( !string.IsNullOrWhiteSpace( part.ParentPart ) )
					{
						var chain = new List<string> { part.Name };
						var cur = part;
						bool cycle = false;
						string? missingParent = null;

						while ( !string.IsNullOrWhiteSpace( cur.ParentPart ) )
						{
							var parent = obj.Parts.FirstOrDefault( p => p != null && p.Name == cur.ParentPart );
							if ( parent == null )
							{
								missingParent = cur.ParentPart;
								break;
							}
							if ( chain.Contains( parent.Name ) )
							{
								cycle = true;
								chain.Add( parent.Name );
								break;
							}
							chain.Add( parent.Name );
							cur = parent;
						}

						if ( cycle )
						{
							string cycleChain = string.Join( " -> ", chain );
							string diag = $"Circular dependency detected in part parent chain for '{qualifiedKey}': {cycleChain}";
							diagnostics?.Add( diag );

							// Break cycle safely: treat part as direct child of object
							var partWorld = objWorld * partLocal;
							result[qualifiedKey] = new EvaluatedPart
							{
								Key = qualifiedKey,
								ObjectName = obj.Name,
								PartName = part.Name,
								Kind = PlacementTargetKind.Part,
								Transform = partWorld,
								LocalTransform = partLocal,
								ParentPart = part.ParentPart,
								FollowBone = part.FollowBone,
								HasCycle = true,
								Diagnostic = diag
							};
							continue;
						}

						if ( missingParent != null )
						{
							string diag = $"Part '{qualifiedKey}' references missing parent part '{missingParent}'.";
							diagnostics?.Add( diag );

							var partWorld = objWorld * partLocal;
							result[qualifiedKey] = new EvaluatedPart
							{
								Key = qualifiedKey,
								ObjectName = obj.Name,
								PartName = part.Name,
								Kind = PlacementTargetKind.Part,
								Transform = partWorld,
								LocalTransform = partLocal,
								ParentPart = part.ParentPart,
								FollowBone = part.FollowBone,
								HasCycle = false,
								Diagnostic = diag
							};
							continue;
						}

						// Valid parent chain from root down to this part
						// chain was [part, immediateParent, ..., rootLeader]
						chain.Reverse(); // Now [rootLeader, ..., immediateParent, part]
						var rootLeader = obj.Parts.First( p => p != null && p.Name == chain[0] );
						var rootLocal = EvaluateLocal( Qualify( obj.Name, rootLeader.Name ), rootLeader.Pose );

						Xform currentAccum;
						string? rootDiag = null;

						if ( !string.IsNullOrWhiteSpace( rootLeader.FollowBone ) )
						{
							if ( TryGetBone( rootLeader.FollowBone, out var boneXf ) )
							{
								currentAccum = boneXf * rootLocal;
							}
							else
							{
								currentAccum = objWorld * rootLocal;
								rootDiag = $"Part '{Qualify( obj.Name, rootLeader.Name )}' references missing follow-bone '{rootLeader.FollowBone}'.";
								diagnostics?.Add( rootDiag );
							}
						}
						else
						{
							currentAccum = objWorld * rootLocal;
						}

						for ( int i = 1; i < chain.Count; i++ )
						{
							var childPart = obj.Parts.First( p => p != null && p.Name == chain[i] );
							var childLocal = EvaluateLocal( Qualify( obj.Name, childPart.Name ), childPart.Pose );
							currentAccum = currentAccum * childLocal;
						}

						result[qualifiedKey] = new EvaluatedPart
						{
							Key = qualifiedKey,
							ObjectName = obj.Name,
							PartName = part.Name,
							Kind = PlacementTargetKind.Part,
							Transform = currentAccum,
							LocalTransform = partLocal,
							ParentPart = part.ParentPart,
							FollowBone = part.FollowBone,
							HasCycle = false,
							Diagnostic = rootDiag
						};
					}
					else if ( !string.IsNullOrWhiteSpace( part.FollowBone ) )
					{
						// Follows a bone directly
						Xform partWorld;
						string? partDiag = null;

						if ( TryGetBone( part.FollowBone, out var boneXf ) )
						{
							partWorld = boneXf * partLocal;
						}
						else
						{
							partWorld = objWorld * partLocal;
							partDiag = $"Part '{qualifiedKey}' references missing follow-bone '{part.FollowBone}'.";
							diagnostics?.Add( partDiag );
						}

						result[qualifiedKey] = new EvaluatedPart
						{
							Key = qualifiedKey,
							ObjectName = obj.Name,
							PartName = part.Name,
							Kind = PlacementTargetKind.Part,
							Transform = partWorld,
							LocalTransform = partLocal,
							ParentPart = null,
							FollowBone = part.FollowBone,
							HasCycle = false,
							Diagnostic = partDiag
						};
					}
					else
					{
						// Direct child of object
						var partWorld = objWorld * partLocal;
						result[qualifiedKey] = new EvaluatedPart
						{
							Key = qualifiedKey,
							ObjectName = obj.Name,
							PartName = part.Name,
							Kind = PlacementTargetKind.Part,
							Transform = partWorld,
							LocalTransform = partLocal,
							ParentPart = null,
							FollowBone = null,
							HasCycle = false,
							Diagnostic = null
						};
					}
				}
			}
		}

		// 3. Evaluate Reference Props
		foreach ( var prop in clip.Props )
		{
			if ( prop == null || string.IsNullOrWhiteSpace( prop.Name ) ) continue;

			var propLocal = EvaluateLocal( prop.Name, prop.Pose );
			Xform propWorld;
			string? propDiag = null;

			if ( !string.IsNullOrWhiteSpace( prop.FollowBone ) )
			{
				if ( TryGetBone( prop.FollowBone, out var boneXf ) )
				{
					propWorld = boneXf * propLocal;
				}
				else
				{
					propWorld = propLocal;
					propDiag = $"Prop '{prop.Name}' references missing follow-bone '{prop.FollowBone}'.";
					diagnostics?.Add( propDiag );
				}
			}
			else
			{
				propWorld = propLocal;
			}

			result[prop.Name] = new EvaluatedPart
			{
				Key = prop.Name,
				ObjectName = prop.Name,
				PartName = null,
				Kind = PlacementTargetKind.Prop,
				Transform = propWorld,
				LocalTransform = propLocal,
				FollowBone = prop.FollowBone,
				Diagnostic = propDiag
			};
		}

		// 4. Standalone Part tracks that are not part of an object or prop
		foreach ( var track in clip.Tracks.Where( t => t.Kind == RigTrackKind.Part && t.Keys.Count > 0 ) )
		{
			if ( !result.ContainsKey( track.Name ) )
			{
				var local = track.Evaluate( frame ).ToXform();
				var (objName, partName) = Split( track.Name );
				result[track.Name] = new EvaluatedPart
				{
					Key = track.Name,
					ObjectName = objName,
					PartName = string.IsNullOrEmpty( partName ) ? null : partName,
					Kind = string.IsNullOrEmpty( partName ) ? PlacementTargetKind.Object : PlacementTargetKind.Part,
					Transform = local,
					LocalTransform = local
				};
			}
		}

		return result;
	}

	/// <summary>
	/// Evaluates a single placement by qualified name (or object/prop name).
	/// </summary>
	public static EvaluatedPart? EvaluateSinglePlacement(
		RigAnimFile clip,
		string key,
		float frame,
		Skeleton? skeleton = null,
		IReadOnlyList<Xform>? boneWorldPoses = null,
		List<string>? diagnostics = null )
	{
		var placements = EvaluatePlacements( clip, frame, skeleton, boneWorldPoses, diagnostics );
		return placements.GetValueOrDefault( key );
	}

	/// <summary>
	/// Evaluates all cameras present in clip tracks and optional camera definitions at the given frame.
	/// FOV is extracted from the track's scale channel.
	/// </summary>
	public static Dictionary<string, EvaluatedCamera> EvaluateCameras(
		RigAnimFile clip,
		float frame,
		IEnumerable<CameraDefinition>? cameras = null )
	{
		ArgumentNullException.ThrowIfNull( clip );

		var result = new Dictionary<string, EvaluatedCamera>( StringComparer.Ordinal );
		var baseMap = cameras?.ToDictionary( c => c.Name, StringComparer.Ordinal )
			?? new Dictionary<string, CameraDefinition>( StringComparer.Ordinal );

		var cameraNames = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var t in clip.Tracks.Where( t => t.Kind == RigTrackKind.Camera && !string.IsNullOrWhiteSpace( t.Name ) ) )
			cameraNames.Add( t.Name );
		foreach ( var name in baseMap.Keys )
			cameraNames.Add( name );

		foreach ( var name in cameraNames )
		{
			baseMap.TryGetValue( name, out var baseCam );
			var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.Camera && t.Name == name && t.Keys.Count > 0 );

			if ( track != null )
			{
				var evaluated = track.Evaluate( frame );
				float fov = evaluated.Scale.x > 0f ? evaluated.Scale.x : (baseCam?.FieldOfView ?? 80f);
				result[name] = new EvaluatedCamera
				{
					Name = name,
					Position = evaluated.Position,
					Rotation = evaluated.Rotation,
					FieldOfView = fov
				};
			}
			else if ( baseCam != null )
			{
				result[name] = new EvaluatedCamera
				{
					Name = name,
					Position = baseCam.Position,
					Rotation = baseCam.Rotation,
					FieldOfView = baseCam.FieldOfView
				};
			}
			else
			{
				result[name] = new EvaluatedCamera
				{
					Name = name,
					Position = Vec3.Zero,
					Rotation = Quat.Identity,
					FieldOfView = 80f
				};
			}
		}

		return result;
	}

	/// <summary>
	/// Evaluates a single camera shot by name at a frame.
	/// </summary>
	public static EvaluatedCamera? EvaluateCamera(
		RigAnimFile clip,
		string cameraName,
		float frame,
		CameraDefinition? baseCamera = null )
	{
		var cams = EvaluateCameras( clip, frame, baseCamera != null ? new[] { baseCamera } : null );
		return cams.GetValueOrDefault( cameraName );
	}

	/// <summary>
	/// Evaluates an IK goal at a frame, taking object-space goals out of their anchor object/part
	/// and placing them accurately in model space.
	/// </summary>
	public static RigPose? EvaluateIkGoal(
		RigAnimFile clip,
		string endBone,
		float frame,
		Skeleton? skeleton = null,
		IReadOnlyList<Xform>? boneWorldPoses = null,
		IReadOnlyDictionary<string, EvaluatedPart>? precomputedPlacements = null )
	{
		ArgumentNullException.ThrowIfNull( clip );
		if ( string.IsNullOrEmpty( endBone ) ) return null;

		var track = clip.Tracks.FirstOrDefault( t => t.Kind == RigTrackKind.IkGoal && t.Name == endBone );
		if ( track == null || track.Keys.Count == 0 )
			return null;

		var key = track.Evaluate( frame );

		if ( !clip.GoalSpaces.TryGetValue( endBone, out var anchor ) || anchor.Space != RigGoalSpace.Object )
			return key;

		if ( string.IsNullOrEmpty( anchor.Object ) )
			return null;

		precomputedPlacements ??= EvaluatePlacements( clip, frame, skeleton, boneWorldPoses );

		if ( !precomputedPlacements.TryGetValue( anchor.Object, out var placedPart ) )
			return null;

		var world = placedPart.Transform * key.ToXform();
		return RigPose.FromXform( world );
	}

	/// <summary>
	/// Parses CameraDefinition objects from a .riganim JSON string's "Cameras" array if present.
	/// </summary>
	public static List<CameraDefinition> ParseCameras( string json )
	{
		var list = new List<CameraDefinition>();
		if ( string.IsNullOrWhiteSpace( json ) ) return list;

		try
		{
			using var doc = JsonDocument.Parse( json );
			if ( doc.RootElement.TryGetProperty( "Cameras", out var cameras ) && cameras.ValueKind == JsonValueKind.Array )
			{
				foreach ( var elem in cameras.EnumerateArray() )
					list.Add( CameraDefinition.FromJson( elem ) );
			}
		}
		catch
		{
			// Ignore parse errors on malformed non-JSON
		}

		return list;
	}

	/// <summary>
	/// Opt-in shot application: copies evaluated camera FOV and orientation/position to OrbitCamera.
	/// Ordinary playback never calls this and does NOT touch or mutate the user's OrbitCamera.
	/// </summary>
	public static void ApplyToOrbitCamera( EvaluatedCamera shot, OrbitCamera orbitCamera )
	{
		ArgumentNullException.ThrowIfNull( shot );
		ArgumentNullException.ThrowIfNull( orbitCamera );

		orbitCamera.FovDegrees = Math.Clamp( shot.FieldOfView, 1f, 179f );
		// When transferring shot to orbit camera in opt-in mode:
		orbitCamera.Target = shot.PositionV3;
	}
}
