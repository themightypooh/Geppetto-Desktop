#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Policy governing how partial bone target matches are handled during clip validation.
/// </summary>
public enum TargetMatchingPolicy
{
	/// <summary>
	/// Partial matches produce warnings and identify unmapped bones; clip remains playable.
	/// </summary>
	AllowPartialWithWarnings,

	/// <summary>
	/// All clip targets must match skeleton bones; partial match is treated as a hard error.
	/// </summary>
	RequireAllTargets
}

/// <summary>
/// Configuration options for animation clip capability validation.
/// </summary>
public sealed class ClipValidationOptions
{
	/// <summary>
	/// Target matching policy (allow partial matches with warnings by default).
	/// </summary>
	public TargetMatchingPolicy TargetPolicy { get; init; } = TargetMatchingPolicy.AllowPartialWithWarnings;

	/// <summary>
	/// Optional AssetPathResolver used to resolve RigAsset references.
	/// </summary>
	public AssetPathResolver? PathResolver { get; init; }

	/// <summary>
	/// Base directory for resolving relative rig asset paths when no AssetPathResolver is provided.
	/// </summary>
	public string? ClipDirectory { get; init; }

	/// <summary>
	/// Whether to test clip playback inside a temporary evaluator before approving.
	/// </summary>
	public bool ValidateEvaluation { get; init; } = true;
}

/// <summary>
/// Structured audit result detailing supported and unsupported animation features,
/// hard errors, warnings, matched targets, and unmapped bones.
/// </summary>
public sealed class ClipAuditResult
{
	/// <summary>
	/// True if the clip is playable with current desktop engine capabilities and contains no hard errors.
	/// </summary>
	public bool IsPlayable => Errors.Count == 0 && UnsupportedFeatures.Count == 0;

	/// <summary>
	/// True if the clip has no hard errors.
	/// </summary>
	public bool IsCompatible => Errors.Count == 0;

	public bool HasErrors => Errors.Count > 0;
	public bool HasWarnings => Warnings.Count > 0;

	public List<string> Errors { get; } = new();
	public IReadOnlyList<string> HardErrors => Errors;

	public List<string> Warnings { get; } = new();

	public List<string> UnsupportedFeatures { get; } = new();
	public List<string> SupportedFeatures { get; } = new();

	public List<string> MatchedTargets { get; } = new();
	public List<string> MissingTargets { get; } = new();
	public IReadOnlyList<string> UnknownTargets => MissingTargets;

	public List<string> UnmappedSkeletonBones { get; } = new();
	public List<string> MissingMorphTracks { get; } = new();

	public string? ResolvedRigPath { get; set; }
	public bool RigResolved { get; set; }

	public void AddError( string message ) => Errors.Add( message );
	public void AddWarning( string message ) => Warnings.Add( message );
	public void AddUnsupported( string feature ) => UnsupportedFeatures.Add( feature );
	public void AddSupported( string feature ) => SupportedFeatures.Add( feature );

	public override string ToString()
	{
		var sb = new StringBuilder();
		sb.AppendLine( IsPlayable ? "Audit: PASS (Playable)" : "Audit: FAIL (Rejected)" );
		if ( Errors.Count > 0 ) sb.AppendLine( $"  Errors ({Errors.Count}): {string.Join( "; ", Errors )}" );
		if ( UnsupportedFeatures.Count > 0 ) sb.AppendLine( $"  Unsupported ({UnsupportedFeatures.Count}): {string.Join( ", ", UnsupportedFeatures )}" );
		if ( Warnings.Count > 0 ) sb.AppendLine( $"  Warnings ({Warnings.Count}): {string.Join( "; ", Warnings )}" );
		if ( MissingTargets.Count > 0 ) sb.AppendLine( $"  Missing Targets ({MissingTargets.Count}): {string.Join( ", ", MissingTargets )}" );
		if ( MatchedTargets.Count > 0 ) sb.AppendLine( $"  Matched Targets ({MatchedTargets.Count}): {string.Join( ", ", MatchedTargets )}" );
		return sb.ToString().TrimEnd();
	}
}

/// <summary>
/// Capability validator for animation clips prior to playback.
/// Inspects tracks and clip-level fields (keyed targets, morph events, drivers, root motion,
/// control rig assets), reports structured audit results, and performs safe evaluation
/// into a temporary evaluator to ensure failed clips never mutate session or player state.
/// </summary>
public static class ClipCapabilityValidator
{
	/// <summary>
	/// Validate an animation clip against a target skeleton and options.
	/// </summary>
	public static ClipAuditResult Validate( RigAnimFile? clip, Skeleton? rig = null, ClipValidationOptions? options = null )
	{
		var audit = new ClipAuditResult();
		options ??= new ClipValidationOptions();

		if ( clip is null )
		{
			audit.AddError( "Animation clip is null." );
			return audit;
		}

		// 1. Clip-level metadata validation
		if ( clip.FrameRate <= 0 )
			audit.AddError( $"FrameRate must be positive, was {clip.FrameRate}." );
		if ( clip.FrameCount < 0 )
			audit.AddError( $"FrameCount cannot be negative, was {clip.FrameCount}." );
		if ( clip.Tracks is null )
		{
			audit.AddError( "Clip tracks list is null." );
			return audit;
		}

		// 2. Track kind, keyframe, and corruption validation
		foreach ( var track in clip.Tracks )
		{
			if ( track is null )
			{
				audit.AddError( "Clip contains a null track entry." );
				continue;
			}

			if ( string.IsNullOrWhiteSpace( track.Name ) )
				audit.AddError( "Track target name cannot be empty." );

			if ( !Enum.IsDefined( typeof( RigTrackKind ), track.Kind ) )
			{
				audit.AddUnsupported( $"TrackKind:{(int)track.Kind}" );
				audit.AddError( $"Track '{track.Name}' has unknown track kind {(int)track.Kind}." );
				continue;
			}

			// Desktop playback currently supports Bone, IkGoal, IkWeight
			if ( track.Kind is not (RigTrackKind.Bone or RigTrackKind.IkGoal or RigTrackKind.IkWeight) )
			{
				audit.AddUnsupported( track.Kind.ToString() );
				audit.AddError( $"Desktop playback does not yet support {track.Kind} tracks ('{track.Name}')." );
			}

			// Validate track keys and check for corruption
			if ( track.Keys is null )
			{
				audit.AddError( $"Track '{track.Name}' has null keyframes list." );
				continue;
			}

			int prevFrame = -1;
			for ( int i = 0; i < track.Keys.Count; i++ )
			{
				var key = track.Keys[i];
				if ( key is null )
				{
					audit.AddError( $"Track '{track.Name}' has null key at index {i}." );
					continue;
				}

				if ( key.Frame < 0 )
					audit.AddError( $"Track '{track.Name}' key at index {i} has negative frame {key.Frame}." );

				if ( !Enum.IsDefined( typeof( RigEase ), key.Ease ) )
					audit.AddError( $"Track '{track.Name}' key at frame {key.Frame} has unknown ease mode {(int)key.Ease}." );

				var pos = key.Local.Position;
				if ( !float.IsFinite( pos.x ) || !float.IsFinite( pos.y ) || !float.IsFinite( pos.z ) )
					audit.AddError( $"Track '{track.Name}' key at frame {key.Frame} has non-finite position ({pos.x}, {pos.y}, {pos.z})." );

				var rot = key.Local.Rotation;
				if ( !float.IsFinite( rot.x ) || !float.IsFinite( rot.y ) || !float.IsFinite( rot.z ) || !float.IsFinite( rot.w ) )
					audit.AddError( $"Track '{track.Name}' key at frame {key.Frame} has non-finite rotation ({rot.x}, {rot.y}, {rot.z}, {rot.w})." );

				var scale = key.Local.Scale;
				if ( !float.IsFinite( scale.x ) || !float.IsFinite( scale.y ) || !float.IsFinite( scale.z ) )
					audit.AddError( $"Track '{track.Name}' key at frame {key.Frame} has non-finite scale ({scale.x}, {scale.y}, {scale.z})." );

				if ( key.TangentIn is not null && key.TangentIn.Any( f => !float.IsFinite( f ) ) )
					audit.AddError( $"Track '{track.Name}' key at frame {key.Frame} has non-finite TangentIn values." );

				if ( key.TangentOut is not null && key.TangentOut.Any( f => !float.IsFinite( f ) ) )
					audit.AddError( $"Track '{track.Name}' key at frame {key.Frame} has non-finite TangentOut values." );

				if ( key.Frame < prevFrame )
					audit.AddError( $"Track '{track.Name}' keyframes are not in ascending order ({key.Frame} after {prevFrame})." );

				prevFrame = key.Frame;
			}
		}

		// 3. Target matching policy against Skeleton
		if ( rig is not null )
		{
			var boneTracks = clip.Tracks.Where( t => t.Kind == RigTrackKind.Bone ).ToList();
			var distinctBoneNames = boneTracks.Select( t => t.Name ).Distinct().ToList();

			foreach ( var name in distinctBoneNames )
			{
				if ( rig.IndexOf( name ) >= 0 )
					audit.MatchedTargets.Add( name );
				else
					audit.MissingTargets.Add( name );
			}

			for ( int i = 0; i < rig.Count; i++ )
			{
				var boneName = rig.Bones[i].Name;
				if ( !distinctBoneNames.Contains( boneName ) )
					audit.UnmappedSkeletonBones.Add( boneName );
			}

			if ( distinctBoneNames.Count == 0 )
			{
				audit.AddError( "Clip contains no bone tracks matching skeleton." );
			}
			else if ( audit.MatchedTargets.Count == 0 )
			{
				audit.AddError( $"Completely incompatible clip: zero target bones match the skeleton ({distinctBoneNames.Count} unmapped: {string.Join( ", ", audit.MissingTargets )})." );
			}
			else if ( audit.MissingTargets.Count > 0 )
			{
				if ( options.TargetPolicy == TargetMatchingPolicy.RequireAllTargets )
				{
					audit.AddError( $"Clip contains {audit.MissingTargets.Count} targets missing from skeleton: {string.Join( ", ", audit.MissingTargets )}." );
				}
				else
				{
					audit.AddWarning( $"Clip contains {audit.MissingTargets.Count} unmapped target bones: {string.Join( ", ", audit.MissingTargets )}." );
				}
			}

			// Check IK goal and weight bones against skeleton
			foreach ( var ikTrack in clip.Tracks.Where( t => t.Kind is RigTrackKind.IkGoal or RigTrackKind.IkWeight ) )
			{
				if ( rig.IndexOf( ikTrack.Name ) < 0 )
					audit.AddWarning( $"IK track references bone '{ikTrack.Name}' which was not found in skeleton." );
			}

			if ( audit.MatchedTargets.Count > 0 )
				audit.AddSupported( "BoneTracks" );
		}

		// 4. Morph events & morph tracks validation
		if ( clip.MorphEvents is not null )
		{
			foreach ( var me in clip.MorphEvents )
			{
				if ( me is null )
				{
					audit.AddError( "Clip contains a null MorphEvent." );
					continue;
				}

				if ( string.IsNullOrWhiteSpace( me.Morph ) )
					audit.AddError( "Morph event has an empty target morph name." );

				if ( me.Start < 0 || me.End < 0 || me.Start > me.End )
					audit.AddError( $"Morph event '{me.Morph}' has invalid frame range [{me.Start}, {me.End}]." );

				if ( !float.IsFinite( me.Value ) )
					audit.AddError( $"Morph event '{me.Morph}' has non-finite value {me.Value}." );

				if ( me.FadeIn < 0 || me.FadeOut < 0 )
					audit.AddError( $"Morph event '{me.Morph}' has negative fade duration (FadeIn={me.FadeIn}, FadeOut={me.FadeOut})." );

				// Diagnose morph event without morph tracks
				bool hasMorphTrack = clip.Tracks.Any( t => t.Kind == RigTrackKind.Morph && t.Name == me.Morph );
				if ( !hasMorphTrack && !string.IsNullOrWhiteSpace( me.Morph ) )
				{
					if ( !audit.MissingMorphTracks.Contains( me.Morph ) )
						audit.MissingMorphTracks.Add( me.Morph );
					audit.AddWarning( $"Morph event '{me.Morph}' has no corresponding morph track in the clip." );
				}
			}

			if ( clip.MorphEvents.Count > 0 && audit.Errors.Count == 0 )
				audit.AddSupported( "MorphEvents" );
		}

		// 5. Root motion configuration validation
		if ( clip.RootMotion is not null )
		{
			if ( !Enum.IsDefined( typeof( RootMotionMode ), clip.RootMotion.Mode ) )
				audit.AddError( $"Root motion has invalid mode: {(int)clip.RootMotion.Mode}." );

			if ( string.IsNullOrWhiteSpace( clip.RootMotion.Bone ) )
			{
				audit.AddError( "Root motion bone name cannot be empty." );
			}
			else if ( rig is not null && rig.IndexOf( clip.RootMotion.Bone ) < 0 )
			{
				audit.AddError( $"Root motion bone '{clip.RootMotion.Bone}' was not found in skeleton." );
			}

			if ( !string.IsNullOrWhiteSpace( clip.RootMotion.Source ) && rig is not null && rig.IndexOf( clip.RootMotion.Source ) < 0 )
			{
				audit.AddError( $"Root motion source bone '{clip.RootMotion.Source}' was not found in skeleton." );
			}

			if ( clip.RootMotion.Enabled && audit.Errors.Count == 0 )
				audit.AddSupported( "RootMotion" );
		}

		// 6. Morph drivers validation
		if ( clip.Drivers is not null )
		{
			foreach ( var driver in clip.Drivers )
			{
				if ( driver is null ) continue;

				if ( string.IsNullOrWhiteSpace( driver.Morph ) )
					audit.AddWarning( "Morph driver has empty morph name." );

				if ( rig is not null && !string.IsNullOrWhiteSpace( driver.Bone ) && rig.IndexOf( driver.Bone ) < 0 )
					audit.AddWarning( $"Morph driver for '{driver.Morph}' references missing bone '{driver.Bone}'." );

				if ( driver.Channel == DriverChannel.Distance && rig is not null && !string.IsNullOrWhiteSpace( driver.OtherBone ) && rig.IndexOf( driver.OtherBone ) < 0 )
					audit.AddWarning( $"Morph driver for '{driver.Morph}' references missing distance secondary bone '{driver.OtherBone}'." );

				if ( !float.IsFinite( driver.Rest ) || !float.IsFinite( driver.Target ) || !float.IsFinite( driver.Value ) )
					audit.AddError( $"Morph driver for '{driver.Morph}' has non-finite numeric parameters." );

				if ( !Enum.IsDefined( typeof( DriverChannel ), driver.Channel ) )
					audit.AddError( $"Morph driver for '{driver.Morph}' has invalid channel {(int)driver.Channel}." );

				if ( !Enum.IsDefined( typeof( DriverAxis ), driver.Axis ) )
					audit.AddError( $"Morph driver for '{driver.Morph}' has invalid axis {(int)driver.Axis}." );

				if ( !Enum.IsDefined( typeof( RigEase ), driver.Ease ) )
					audit.AddError( $"Morph driver for '{driver.Morph}' has invalid ease {(int)driver.Ease}." );
			}

			if ( clip.Drivers.Count > 0 && audit.Errors.Count == 0 )
				audit.AddSupported( "MorphDrivers" );
		}

		// 7. Rig asset resolution and validation
		if ( !string.IsNullOrWhiteSpace( clip.RigAsset ) )
		{
			ResolveAndValidateRigAsset( clip, audit, options );
		}

		// 8. Safe temporary evaluation
		if ( rig is not null && options.ValidateEvaluation && audit.IsPlayable )
		{
			try
			{
				var tempPlayer = new MarionettePlayer( rig );
				tempPlayer.LoadClip( clip );
				tempPlayer.Step( 0 );
				if ( clip.FrameCount > 1 )
					tempPlayer.Step( 1f / Math.Max( 1, clip.FrameRate ) );
			}
			catch ( Exception ex )
			{
				audit.AddError( $"Temporary playback evaluation failed: {ex.Message}" );
			}
		}

		return audit;
	}

	/// <summary>
	/// Validate an animation clip file at the specified path.
	/// </summary>
	public static ClipAuditResult Validate( string clipPath, Skeleton? rig = null, ClipValidationOptions? options = null )
	{
		var audit = new ClipAuditResult();
		if ( string.IsNullOrWhiteSpace( clipPath ) || !File.Exists( clipPath ) )
		{
			audit.AddError( $"Clip file not found: '{clipPath}'." );
			return audit;
		}

		try
		{
			var clip = RigAnimFile.ReadFile( clipPath );
			options ??= new ClipValidationOptions();
			var effectiveOptions = new ClipValidationOptions
			{
				TargetPolicy = options.TargetPolicy,
				PathResolver = options.PathResolver,
				ClipDirectory = options.ClipDirectory ?? Path.GetDirectoryName( Path.GetFullPath( clipPath ) ),
				ValidateEvaluation = options.ValidateEvaluation
			};
			return Validate( clip, rig, effectiveOptions );
		}
		catch ( Exception ex )
		{
			audit.AddError( $"Failed to read clip file '{clipPath}': {ex.Message}" );
			return audit;
		}
	}

	static void ResolveAndValidateRigAsset( RigAnimFile clip, ClipAuditResult audit, ClipValidationOptions options )
	{
		string rigReference = clip.RigAsset;
		string? canonicalRigPath = null;

		if ( options.PathResolver is not null )
		{
			try
			{
				var assetRef = Path.IsPathFullyQualified( rigReference )
					? new AssetReference( "rig", rigReference, AssetReferenceKind.ExternalReadOnly )
					: new AssetReference( "rig", rigReference, AssetReferenceKind.ProjectRelative );

				var resolved = options.PathResolver.ResolveRead( assetRef, requireExists: true );
				canonicalRigPath = resolved.CanonicalPath;
			}
			catch ( Exception ex )
			{
				audit.AddError( $"Referenced control rig '{rigReference}' could not be resolved: {ex.Message}" );
				return;
			}
		}
		else
		{
			string candidate = !string.IsNullOrEmpty( options.ClipDirectory )
				? Path.Combine( options.ClipDirectory, rigReference )
				: (Path.IsPathFullyQualified( rigReference ) ? rigReference : Path.GetFullPath( rigReference ));

			if ( !File.Exists( candidate ) )
			{
				audit.AddError( $"Referenced control rig '{rigReference}' not found at '{candidate}'." );
				return;
			}

			canonicalRigPath = candidate;
		}

		if ( string.IsNullOrEmpty( canonicalRigPath ) || !File.Exists( canonicalRigPath ) )
		{
			audit.AddError( $"Referenced control rig '{rigReference}' not found." );
			return;
		}

		try
		{
			string rigJson = File.ReadAllText( canonicalRigPath );
			using var doc = JsonDocument.Parse( rigJson );
			clip.UseRig( rigJson );
			audit.RigResolved = true;
			audit.ResolvedRigPath = canonicalRigPath;
			audit.AddSupported( "ControlRig" );
		}
		catch ( JsonException ex )
		{
			audit.AddError( $"Referenced control rig '{rigReference}' contains malformed JSON: {ex.Message}" );
		}
		catch ( Exception ex )
		{
			audit.AddError( $"Referenced control rig '{rigReference}' failed to load: {ex.Message}" );
		}
	}

	/// <summary>
	/// Try applying a clip to a MarionettePlayer after capability audit and temporary evaluation.
	/// If rejected, the player's existing clip, time, world pose, and playing state remain unchanged.
	/// </summary>
	public static bool TryApply( MarionettePlayer player, RigAnimFile clip, out ClipAuditResult audit, ClipValidationOptions? options = null )
	{
		ArgumentNullException.ThrowIfNull( player );
		options ??= new ClipValidationOptions();

		audit = Validate( clip, player.Rig, options );
		if ( !audit.IsPlayable )
		{
			return false;
		}

		player.LoadClip( clip );
		return true;
	}

	/// <summary>
	/// Try applying a clip from a file path to a MarionettePlayer.
	/// </summary>
	public static bool TryApply( MarionettePlayer player, string clipPath, out ClipAuditResult audit, ClipValidationOptions? options = null )
	{
		ArgumentNullException.ThrowIfNull( player );
		if ( string.IsNullOrWhiteSpace( clipPath ) || !File.Exists( clipPath ) )
		{
			audit = new ClipAuditResult();
			audit.AddError( $"Clip file not found: '{clipPath}'." );
			return false;
		}

		try
		{
			var clip = RigAnimFile.ReadFile( clipPath );
			var effectiveOptions = new ClipValidationOptions
			{
				TargetPolicy = options?.TargetPolicy ?? TargetMatchingPolicy.AllowPartialWithWarnings,
				PathResolver = options?.PathResolver,
				ClipDirectory = options?.ClipDirectory ?? Path.GetDirectoryName( Path.GetFullPath( clipPath ) ),
				ValidateEvaluation = options?.ValidateEvaluation ?? true
			};
			return TryApply( player, clip, out audit, effectiveOptions );
		}
		catch ( Exception ex )
		{
			audit = new ClipAuditResult();
			audit.AddError( $"Failed to read clip file '{clipPath}': {ex.Message}" );
			return false;
		}
	}

	/// <summary>
	/// Try applying a clip to a DocumentSession after capability audit and temporary evaluation.
	/// If rejected, the session's existing clip, revision, playhead, and playing state remain unchanged.
	/// </summary>
	public static bool TryApply( DocumentSession session, RigAnimFile clip, out ClipAuditResult audit,
		string? clipPath = null, string? clipJson = null, ClipValidationOptions? options = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		options ??= new ClipValidationOptions();

		var skeleton = GetSkeleton( session );
		if ( skeleton is null || skeleton.Count == 0 )
		{
			audit = new ClipAuditResult();
			audit.AddError( "Cannot play a skeletal clip on a model without bones." );
			return false;
		}

		audit = Validate( clip, skeleton, options );
		if ( !audit.IsPlayable )
		{
			return false;
		}

		session.SetClip( clipPath, clipJson, clip );
		return true;
	}

	/// <summary>
	/// Try applying a clip from a file path to a DocumentSession.
	/// </summary>
	public static bool TryApply( DocumentSession session, string clipPath, out ClipAuditResult audit, ClipValidationOptions? options = null )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( string.IsNullOrWhiteSpace( clipPath ) || !File.Exists( clipPath ) )
		{
			audit = new ClipAuditResult();
			audit.AddError( $"Clip file not found: '{clipPath}'." );
			return false;
		}

		try
		{
			string json = File.ReadAllText( clipPath );
			var clip = RigAnimFile.Read( json );
			var effectiveOptions = new ClipValidationOptions
			{
				TargetPolicy = options?.TargetPolicy ?? TargetMatchingPolicy.AllowPartialWithWarnings,
				PathResolver = options?.PathResolver,
				ClipDirectory = options?.ClipDirectory ?? Path.GetDirectoryName( Path.GetFullPath( clipPath ) ),
				ValidateEvaluation = options?.ValidateEvaluation ?? true
			};
			return TryApply( session, clip, out audit, clipPath, json, effectiveOptions );
		}
		catch ( Exception ex )
		{
			audit = new ClipAuditResult();
			audit.AddError( $"Failed to read clip file '{clipPath}': {ex.Message}" );
			return false;
		}
	}

	/// <summary>
	/// Apply a clip to a MarionettePlayer, throwing InvalidOperationException on validation failure.
	/// If rejected, the player remains completely unmodified.
	/// </summary>
	public static void ApplySafe( MarionettePlayer player, RigAnimFile clip, ClipValidationOptions? options = null )
	{
		if ( !TryApply( player, clip, out var audit, options ) )
			throw new InvalidOperationException( $"Cannot apply rejected animation clip:\n{audit}" );
	}

	/// <summary>
	/// Apply a clip to a DocumentSession, throwing InvalidOperationException on validation failure.
	/// If rejected, the session remains completely unmodified.
	/// </summary>
	public static void ApplySafe( DocumentSession session, RigAnimFile clip, string? clipPath = null, string? clipJson = null, ClipValidationOptions? options = null )
	{
		if ( !TryApply( session, clip, out var audit, clipPath, clipJson, options ) )
			throw new InvalidOperationException( $"Cannot apply rejected animation clip:\n{audit}" );
	}

	/// <summary>
	/// Retrieve the active skeleton from a DocumentSession if present.
	/// </summary>
	public static Skeleton? GetSkeleton( DocumentSession session )
	{
		ArgumentNullException.ThrowIfNull( session );
		if ( session.SourceKind == DocumentSourceKind.Fbx )
			return session.FbxSource?.Skeleton;
		return session.Studio?.Rig;
	}
}
