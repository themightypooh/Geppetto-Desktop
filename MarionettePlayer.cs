using System;
using System.IO;
using System.Numerics;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// Rig &amp; clip evaluator for GPU vertex skinning.
///
/// Pre-allocates all bone pose buffers and pre-indexes <see cref="RigAnimFile"/> tracks by
/// bone index at load time so bone-key <see cref="Step"/> allocates 0 bytes per frame.
/// Supported live IK uses reusable buffers; unsupported solver configurations retain
/// the authoritative kernel sampler.
/// Also provides a procedural idle/turntable animation mode when no .riganim clip is loaded.
/// </summary>
public sealed class MarionettePlayer
{
	public const int MaxGpuBones = 220;

	public Skeleton Rig { get; }
	public RigAnimFile Clip { get; private set; }
	public bool Playing { get; set; }
	float _time;
	bool _poseDirty;
	int _lastFrame;
	RigLimit[][] _limits;
	LiveIkSolverBuffers _liveIkBuffers;
	public bool Loop { get; set; } = true;
	public bool UsesKernelSolver { get; private set; }
	public float Time
	{
		get => _time;
		set
		{
			if ( !float.IsFinite( value ) ) throw new ArgumentOutOfRangeException( nameof( value ) );
			_time = value;
			_poseDirty = true;
		}
	}

	readonly int _boneCount;
	readonly Xform[] _bindLocal;
	readonly Xform[] _bindWorld;
	readonly Xform[] _invBindWorld;
	readonly Xform[] _localPose;
	readonly Xform[] _worldPose;
	readonly Matrix4x4[] _gpuBones;

	// Pre-indexed clip tracks per bone (null when unkeyed) to avoid RigAnimFile.Find closures.
	readonly RigTrack[] _boneTracks;

	// Cached bone indices for procedural mechanical/humanoid life when no clip is provided.
	readonly int _bPelvis, _bSpine0, _bSpine1, _bSpine2, _bNeck0, _bHead, _bNeckTilt;
	readonly int _bLensFocus, _bLensHood, _bAntenna, _bLidUp, _bLidLo;
	readonly int _bArmUpperL, _bArmLowerL, _bHandL;
	readonly int _bArmUpperR, _bArmLowerR, _bHandR;
	readonly int[] _bIris = new int[7];
	readonly int[] _bNeckChain = new int[6];

	public ReadOnlySpan<Matrix4x4> GpuBones => _gpuBones.AsSpan( 0, Math.Min( _boneCount, MaxGpuBones ) );
	public ReadOnlySpan<Xform> WorldPose => _worldPose.AsSpan( 0, _boneCount );

	public MarionettePlayer( Skeleton rig, string clipPath = null, bool startPlaying = false )
	{
		Rig = rig ?? throw new ArgumentNullException( nameof( rig ) );
		_boneCount = rig.Count;
		if ( _boneCount > MaxGpuBones )
			throw new NotSupportedException( $"This viewport supports at most {MaxGpuBones} bones; this rig has {_boneCount}." );
		_bindLocal = new Xform[_boneCount];
		_bindWorld = new Xform[_boneCount];
		_invBindWorld = new Xform[_boneCount];
		_localPose = new Xform[_boneCount];
		_worldPose = new Xform[_boneCount];
		_gpuBones = new Matrix4x4[Math.Max( 1, Math.Min( _boneCount, MaxGpuBones ) )];
		_boneTracks = new RigTrack[_boneCount];

		for ( int b = 0; b < _boneCount; b++ )
		{
			_bindLocal[b] = rig.Bones[b].Local;
			_localPose[b] = _bindLocal[b];
			int parent = rig.Bones[b].Parent;
			if ( parent < -1 || parent >= b )
				throw new InvalidDataException( $"Bone '{rig.Bones[b].Name}' has an invalid parent index {parent}." );
			var bindW = parent >= 0 ? _bindWorld[parent] * _bindLocal[b] : _bindLocal[b];
			_bindWorld[b] = bindW;
			_worldPose[b] = bindW;
			try { _invBindWorld[b] = bindW.Inverse; }
			catch ( InvalidOperationException ex )
			{
				throw new InvalidDataException( $"Bone '{rig.Bones[b].Name}' has a singular bind transform.", ex );
			}
			if ( b < _gpuBones.Length )
				_gpuBones[b] = Matrix4x4.Identity;
		}

		_bPelvis    = rig.IndexOf( "pelvis" );
		_bSpine0    = rig.IndexOf( "spine_0" );
		_bSpine1    = rig.IndexOf( "spine_1" );
		_bSpine2    = rig.IndexOf( "spine_2" );
		_bNeck0     = rig.IndexOf( "neck_0" );
		_bHead      = rig.IndexOf( "head" );
		_bNeckTilt  = rig.IndexOf( "neck_tilt" );
		_bLensFocus = rig.IndexOf( "lens_focus" );
		_bLensHood  = rig.IndexOf( "lens_hood" );
		_bAntenna   = rig.IndexOf( "antenna" );
		_bLidUp     = rig.IndexOf( "lid_up" );
		_bLidLo     = rig.IndexOf( "lid_lo" );
		_bArmUpperL = rig.IndexOf( "arm_upper_L" );
		_bArmLowerL = rig.IndexOf( "arm_lower_L" );
		_bHandL     = rig.IndexOf( "hand_L" );
		_bArmUpperR = rig.IndexOf( "arm_upper_R" );
		_bArmLowerR = rig.IndexOf( "arm_lower_R" );
		_bHandR     = rig.IndexOf( "hand_R" );

		for ( int i = 0; i < _bIris.Length; i++ )
			_bIris[i] = rig.IndexOf( $"iris_{i}" );
		for ( int i = 0; i < _bNeckChain.Length; i++ )
			_bNeckChain[i] = rig.IndexOf( $"neckchain_{i}" );

		if ( !string.IsNullOrWhiteSpace( clipPath ) )
		{
			LoadClip( clipPath );
			Playing = true;
		}
		else
		{
			Playing = startPlaying;
		}
	}

	public void LoadClip( string clipPath )
	{
		var clip = RigAnimFile.ReadFile( clipPath );
		if ( !string.IsNullOrWhiteSpace( clip.RigAsset ) )
		{
			var rigPath = Path.GetFullPath( clip.RigAsset, Path.GetDirectoryName( Path.GetFullPath( clipPath ) ) );
			clip.UseRig( File.ReadAllText( rigPath ) );
		}
		LoadClip( clip );
	}

	public void LoadClip( RigAnimFile clip )
	{
		ArgumentNullException.ThrowIfNull( clip );
		foreach ( var track in clip.Tracks )
			if ( track.Kind is not (RigTrackKind.Bone or RigTrackKind.IkGoal or RigTrackKind.IkWeight) )
				throw new NotSupportedException( $"Desktop playback does not yet support {track.Kind} tracks ('{track.Name}')." );
		Clip = clip;
		InvalidateClipCache( preservePlayhead: false );
	}

	/// <summary>
	/// Invalidate track, limit, and end-frame caches when clip keys or limits change.
	/// Optionally preserves the user's playhead and playing state during re-evaluation.
	/// </summary>
	public void InvalidateClipCache( bool preservePlayhead = true )
	{
		if ( Clip is null ) return;
		_lastFrame = Clip.LastFrame;
		UsesKernelSolver = Clip.PlayInPlace && Clip.RootMotion is { Enabled: true };
		Array.Clear( _boneTracks );
		_limits = new RigLimit[_boneCount][];

		foreach ( var track in Clip.Tracks )
		{
			if ( track.Kind == RigTrackKind.IkGoal && track.Keys.Count > 0 ) UsesKernelSolver = true;
			if ( track.Kind != RigTrackKind.Bone ) continue;
			int bi = Rig.IndexOf( track.Name );
			if ( bi >= 0 && bi < _boneCount )
				_boneTracks[bi] = track;
		}
		for ( int b = 0; b < _boneCount; b++ )
		{
			var limits = new System.Collections.Generic.List<RigLimit>();
			if ( Clip.Limits is not null )
				foreach ( var limit in Clip.Limits )
					if ( limit.Enabled && limit.Bone == Rig.Bones[b].Name ) limits.Add( limit );
			_limits[b] = limits.ToArray();
		}
		// Clip edits call InvalidateClipCache; rebuild the pre-indexed solver plan there,
		// never inside the playback frame. The sampler owns its pose/chain scratch arrays.
		_liveIkBuffers = UsesKernelSolver ? new LiveIkSolverBuffers( Rig, Clip ) : null;
		if ( !preservePlayhead ) Time = 0;
		_poseDirty = true;
		Step( 0 );
	}

	public void ResetBindPose()
	{
		Playing = false;
		_time = 0f;
		_poseDirty = false;
		for ( int b = 0; b < _boneCount; b++ )
		{
			_localPose[b] = _bindLocal[b];
			_worldPose[b] = _bindWorld[b];
			if ( b < _gpuBones.Length )
				_gpuBones[b] = Matrix4x4.Identity;
		}
	}

	/// <summary>
	/// Evaluate one animation frame and update the GPU skinning matrix palette.
	/// Bone-key and supported live-IK playback allocate nothing after clip load.
	/// UsesKernelSolver identifies clips requiring an IK/root-motion evaluator, not necessarily
	/// a kernel fallback; unsupported configurations still use the authoritative sampler.
	/// </summary>
	public void Step( float dt )
	{
		if ( !float.IsFinite( dt ) || dt < 0 ) throw new ArgumentOutOfRangeException( nameof( dt ) );
		if ( _boneCount == 0 || (!Playing && !_poseDirty) ) return;
		if ( Playing ) _time += dt;
		_poseDirty = false;

		if ( Clip is not null )
			SampleClipZeroAlloc( Time );
		else
			SampleProceduralZeroAlloc( Time );

		// Forward kinematics (parent index < child index invariant in Effigy Skeleton).
		for ( int b = 0; b < _boneCount; b++ )
		{
			int parent = Rig.Bones[b].Parent;
			_worldPose[b] = parent >= 0 ? _worldPose[parent] * _localPose[b] : _localPose[b];
			if ( b < _gpuBones.Length )
				_gpuBones[b] = ToGlMatrix( _worldPose[b] * _invBindWorld[b] );
		}
	}

	void SampleClipZeroAlloc( float seconds )
	{
		int fps = Math.Max( 1, Clip.FrameRate );
		float rawFrame = seconds * fps;
		float frame = _lastFrame <= 0 ? 0 : Loop && Playing
			? ((rawFrame % _lastFrame) + _lastFrame) % _lastFrame
			: Math.Clamp( rawFrame, 0, _lastFrame );
		if ( !Loop && rawFrame >= _lastFrame ) Playing = false;
		if ( UsesKernelSolver )
		{
			_liveIkBuffers.Sample( frame );
			_liveIkBuffers.Local.CopyTo( _localPose );
			return;
		}

		for ( int b = 0; b < _boneCount; b++ )
		{
			var track = _boneTracks[b];
			var pose = track is { Keys.Count: > 0 } ? track.Evaluate( frame ).ToXform() : _bindLocal[b];
			foreach ( var limit in _limits[b] )
			{
				var rotation = Quat.FromXform( pose );
				var (pitch, yaw, roll) = RigLimit.ToAngles( rotation );
				var clamped = Quat.FromAngles( Math.Clamp( pitch, limit.Min.x, limit.Max.x ),
					Math.Clamp( yaw, limit.Min.y, limit.Max.y ), Math.Clamp( roll, limit.Min.z, limit.Max.z ) );
				var q = Quaternion.Lerp( new Quaternion( rotation.x, rotation.y, rotation.z, rotation.w ),
					new Quaternion( clamped.x, clamped.y, clamped.z, clamped.w ), Math.Clamp( limit.Weight, 0, 1 ) );
				pose = new Quat( q.X, q.Y, q.Z, q.W ).ToXform( pose.Origin, Quat.ScaleOf( pose ) );
			}
			_localPose[b] = pose;
		}
	}

	void SampleProceduralZeroAlloc( float t )
	{
		for ( int b = 0; b < _boneCount; b++ )
			_localPose[b] = _bindLocal[b];

		// Subtle breathing & weight shift through spine
		float breath = MathF.Sin( t * 2.1f );
		float scan   = MathF.Sin( t * 1.15f );
		float nod    = MathF.Cos( t * 1.75f );

		RotateLocal( _bPelvis, 0f, 0f, MathF.Sin( t * 1.15f ) * 2.2f );
		RotateLocal( _bSpine0, 0f, breath * 1.8f, scan * 2.5f );
		RotateLocal( _bSpine1, 0f, breath * 2.2f, scan * 3.0f );
		RotateLocal( _bSpine2, 0f, breath * 2.5f, scan * 3.5f );

		// Camera head scanning + mechanical tilt + focus ring + antenna whip
		RotateLocal( _bNeck0,     0f, nod * 4.5f, scan * 8.0f );
		RotateLocal( _bHead,      scan * 4.0f, nod * 7.5f, scan * 16.0f );
		RotateLocal( _bNeckTilt,  0f, nod * 5.0f, 0f );
		RotateLocal( _bLensFocus, 0f, MathF.Sin( t * 2.8f ) * 32.0f, 0f );
		RotateLocal( _bAntenna,   MathF.Sin( t * 5.2f - 0.6f ) * 12.0f, 0f, MathF.Cos( t * 4.1f ) * 9.0f );

		// Periodic shutter blink
		float blinkWave = MathF.Pow( MathF.Max( 0f, MathF.Sin( t * 1.6f ) ), 18f );
		RotateLocal( _bLidUp,  blinkWave * 18.0f, 0f, 0f );
		RotateLocal( _bLidLo, -blinkWave * 18.0f, 0f, 0f );

		// Arms & hands counter-sway
		RotateLocal( _bArmUpperL, MathF.Sin( t * 2.1f + 0.4f ) * 5.5f, 0f,  breath * 3.5f );
		RotateLocal( _bArmLowerL, 0f, 0f, (MathF.Sin( t * 2.1f ) * 0.5f + 0.5f) * 9.0f );
		RotateLocal( _bHandL,     MathF.Cos( t * 2.1f ) * 6.0f, 0f, 0f );

		RotateLocal( _bArmUpperR, -MathF.Sin( t * 2.1f + 0.4f ) * 5.5f, 0f, -breath * 3.5f );
		RotateLocal( _bArmLowerR, 0f, 0f, (MathF.Cos( t * 2.1f ) * 0.5f + 0.5f) * 9.0f );
		RotateLocal( _bHandR,    -MathF.Cos( t * 2.1f ) * 6.0f, 0f, 0f );

		// Neck chain secondary wave
		for ( int i = 0; i < _bNeckChain.Length; i++ )
			RotateLocal( _bNeckChain[i], MathF.Sin( t * 3.5f - i * 0.45f ) * 4.0f, 0f, 0f );
	}

	void RotateLocal( int bone, float rxDeg, float ryDeg, float rzDeg )
	{
		if ( bone < 0 || bone >= _boneCount ) return;
		var pose = _localPose[bone];
		const float d2r = MathF.PI / 180f;
		var rx = Xform.Rotate( new Vec3( 1, 0, 0 ), rxDeg * d2r );
		var ry = Xform.Rotate( new Vec3( 0, 1, 0 ), ryDeg * d2r );
		var rz = Xform.Rotate( new Vec3( 0, 0, 1 ), rzDeg * d2r );
		var rot = rz * ry * rx;
		_localPose[bone] = new Xform(
			pose.TransformDirection( rot.X ),
			pose.TransformDirection( rot.Y ),
			pose.TransformDirection( rot.Z ),
			pose.Origin );
	}

	/// <summary>
	/// Pack an Effigy <see cref="Xform"/> into a <see cref="Matrix4x4"/> whose memory layout
	/// matches GLSL column-major <c>mat4</c> when uploaded with <c>transpose = false</c>.
	/// </summary>
	public static Matrix4x4 ToGlMatrix( Xform xf ) => new(
		xf.X.x,      xf.X.y,      xf.X.z,      0f,
		xf.Y.x,      xf.Y.y,      xf.Y.z,      0f,
		xf.Z.x,      xf.Z.y,      xf.Z.z,      0f,
		xf.Origin.x, xf.Origin.y, xf.Origin.z, 1f );
}
