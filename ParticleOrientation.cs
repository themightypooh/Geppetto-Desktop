#nullable enable
using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// 3D orientation modes for particles (wave-6 lane P30).
///
/// Self-contained evaluators only: given a particle's position, velocity and
/// rotation plus the camera position/direction, they return a 3x3 orientation
/// basis for rendering. They do NOT draw anything and are NOT wired into any
/// render layer — the coordinator sweep owns that hookup (see the lane report
/// for the exact integration lines).
///
/// Units: kernel inches, Z-up, seconds — the same conventions as
/// <see cref="ParticleSystem"/>. Speeds are inches/second, so
/// <see cref="ParticleOrientationOptions.StretchFactor"/> is in seconds/inch
/// (a dimensionless length multiplier comes out of 1 + factor * speed).
///
/// Basis convention (matches <see cref="ParticleLayer"/> in LiveGeometry.cs,
/// which builds quads as position +/- view.Right*h +/- view.Up*h with normal
/// -view.Forward): a basis is right-handed, i.e. Right x Up == Normal, all
/// three vectors unit length. The Up axis is the "length" axis: it follows the
/// velocity in <see cref="ParticleOrientationMode.VelocityAligned"/> and in
/// <see cref="ParticleOrientationMode.StretchedBillboard"/>, and it is the
/// fixed axis in <see cref="ParticleOrientationMode.AxialBillboard"/>.
/// Quad corners from <see cref="ParticleOrientationResult.QuadCorners"/>
/// are in <see cref="ParticleLayer"/> order: (-r-u, +r-u, +r+u, -r+u).
///
/// Fallback rule, stated once and used everywhere: whenever a mode's defining
/// direction is undefined (speed ~ 0, velocity parallel to the view normal,
/// camera looking straight down the axial axis), the evaluator falls back to
/// the camera billboard and sets
/// <see cref="ParticleOrientationResult.FellBackToBillboard"/> — it never
/// returns a NaN, zero, or non-orthonormal basis. A NaN/zero input, by
/// contrast, is a caller bug and is rejected loudly by
/// <see cref="ParticleOrientationOptions.Validate"/> instead of being
/// silently papered over.
/// </summary>
public enum ParticleOrientationMode
{
	/// <summary>Billboard: the quad plane always faces the camera.</summary>
	CameraBillboard,
	/// <summary>Up axis follows the velocity vector (streaks, sparks, mesh
	/// instances flown nose-first). Falls back to billboard at ~0 speed.</summary>
	VelocityAligned,
	/// <summary>Up axis is one fixed world axis (default Z: vertical streaks
	/// such as rain); the other two axes rotate to face the camera as far as
	/// the fixed axis allows.</summary>
	AxialBillboard,
	/// <summary>Camera-facing billboard stretched along the velocity
	/// direction projected onto the billboard plane: length grows with
	/// speed, width stays at particle size.</summary>
	StretchedBillboard,
}

/// <summary>Options for <see cref="ParticleOrientation.Evaluate"/>.</summary>
public struct ParticleOrientationOptions
{
	/// <summary>Which mode to evaluate. Default: camera billboard.</summary>
	public ParticleOrientationMode Mode;
	/// <summary>Fixed axis for <see cref="ParticleOrientationMode.AxialBillboard"/>.
	/// Must be non-zero; it is normalized by the evaluator. Default: Z.</summary>
	public Vector3 AxialAxis;
	/// <summary>Reference "up" used to break ties when a primary axis is
	/// parallel to it. Must be non-zero; normalized by the evaluator.
	/// Default: Z (the world up in this Z-up tree).</summary>
	public Vector3 WorldUp;
	/// <summary>Stretch per unit speed for
	/// <see cref="ParticleOrientationMode.StretchedBillboard"/>:
	/// LengthScale = 1 + StretchFactor * speed. Must be >= 0.
	/// Default 1 (at 10 in/s the quad is 11x as long as it is wide).</summary>
	public float StretchFactor;
	/// <summary>Speed at or below which a particle counts as stationary
	/// (inches/second). Default 1e-4 — far below any visible motion, far
	/// above float noise for the speeds this sim produces.</summary>
	public float SpeedEpsilon;

	public static ParticleOrientationOptions Default => new()
	{
		Mode = ParticleOrientationMode.CameraBillboard,
		AxialAxis = Vector3.UnitZ,
		WorldUp = Vector3.UnitZ,
		StretchFactor = 1f,
		SpeedEpsilon = 1e-4f,
	};

	/// <summary>Loud validation. Throws <see cref="ArgumentException"/> on:
	/// a zero or non-finite axis / world-up, a negative or non-finite stretch
	/// factor, a negative or non-finite speed epsilon, and — when a camera
	/// forward is supplied — a zero, non-finite, or non-unit camera forward.
	/// Camera forward must arrive normalized because it is used directly as
	/// the billboard normal (-forward); axes, by contrast, are normalized
	/// by the evaluator, so only zero/non-finite axes are rejected.</summary>
	public void Validate( Vector3? cameraForward = null )
	{
		if ( !IsFinite( AxialAxis ) || AxialAxis.LengthSquared() <= 1e-12f )
			throw new ArgumentException( $"ParticleOrientation: AxialAxis must be a non-zero finite vector, got {AxialAxis}." );
		if ( !IsFinite( WorldUp ) || WorldUp.LengthSquared() <= 1e-12f )
			throw new ArgumentException( $"ParticleOrientation: WorldUp must be a non-zero finite vector, got {WorldUp}." );
		if ( !float.IsFinite( StretchFactor ) || StretchFactor < 0 )
			throw new ArgumentException( $"ParticleOrientation: StretchFactor must be >= 0 and finite, got {StretchFactor}." );
		if ( !float.IsFinite( SpeedEpsilon ) || SpeedEpsilon < 0 )
			throw new ArgumentException( $"ParticleOrientation: SpeedEpsilon must be >= 0 and finite, got {SpeedEpsilon}." );
		if ( cameraForward is { } f )
		{
			if ( !IsFinite( f ) || f.LengthSquared() <= 1e-12f )
				throw new ArgumentException( $"ParticleOrientation: camera forward must be a non-zero finite vector, got {f}." );
			if ( MathF.Abs( f.Length() - 1f ) > 1e-3f )
				throw new ArgumentException( $"ParticleOrientation: camera forward must be normalized (unit length), got length {f.Length()} from {f}. Normalize it before calling Evaluate." );
		}
	}

	internal static bool IsFinite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );

	internal static Vector3 RequireFinite( Vector3 v, string name )
	{
		if ( !IsFinite( v ) ) throw new ArgumentException( $"ParticleOrientation: {name} must be finite, got {v}." );
		return v;
	}
}

/// <summary>Right-handed orthonormal basis: Right x Up == Normal, all unit.</summary>
public readonly struct ParticleOrientationBasis
{
	public Vector3 Right { get; }
	public Vector3 Up { get; }
	public Vector3 Normal { get; }

	public ParticleOrientationBasis( Vector3 right, Vector3 up, Vector3 normal )
	{
		Right = right; Up = up; Normal = normal;
	}

	/// <summary>True when all three axes are unit length, mutually
	/// perpendicular, and Right x Up == Normal, within <paramref name="tol"/>.</summary>
	public bool IsOrthonormal( float tol = 1e-4f )
	{
		if ( MathF.Abs( Right.Length() - 1f ) > tol ) return false;
		if ( MathF.Abs( Up.Length() - 1f ) > tol ) return false;
		if ( MathF.Abs( Normal.Length() - 1f ) > tol ) return false;
		if ( MathF.Abs( Vector3.Dot( Right, Up ) ) > tol ) return false;
		if ( MathF.Abs( Vector3.Dot( Right, Normal ) ) > tol ) return false;
		if ( MathF.Abs( Vector3.Dot( Up, Normal ) ) > tol ) return false;
		return ( Vector3.Cross( Right, Up ) - Normal ).Length() <= tol;
	}

	/// <summary>Quaternion rotating local X/Y/Z onto Right/Up/Normal, so
	/// Vector3.Transform(localAxis, Matrix4x4.CreateFromQuaternion(q))
	/// reproduces this basis. Built from the basis columns directly, never
	/// via a transposed matrix guess.</summary>
	public Quaternion ToQuaternion()
	{
		// Rotation matrix columns are the basis vectors.
		float m00 = Right.X, m01 = Up.X, m02 = Normal.X;
		float m10 = Right.Y, m11 = Up.Y, m12 = Normal.Y;
		float m20 = Right.Z, m21 = Up.Z, m22 = Normal.Z;
		float trace = m00 + m11 + m22;
		if ( trace > 0 )
		{
			float s = MathF.Sqrt( trace + 1f ) * 2f;
			return new Quaternion( ( m21 - m12 ) / s, ( m02 - m20 ) / s, ( m10 - m01 ) / s, 0.25f * s );
		}
		if ( m00 > m11 && m00 > m22 )
		{
			float s = MathF.Sqrt( 1f + m00 - m11 - m22 ) * 2f;
			return new Quaternion( 0.25f * s, ( m01 + m10 ) / s, ( m02 + m20 ) / s, ( m21 - m12 ) / s );
		}
		if ( m11 > m22 )
		{
			float s = MathF.Sqrt( 1f + m11 - m00 - m22 ) * 2f;
			return new Quaternion( ( m01 + m10 ) / s, 0.25f * s, ( m12 + m21 ) / s, ( m02 - m20 ) / s );
		}
		float s2 = MathF.Sqrt( 1f + m22 - m00 - m11 ) * 2f;
		return new Quaternion( ( m02 + m20 ) / s2, ( m12 + m21 ) / s2, 0.25f * s2, ( m10 - m01 ) / s2 );
	}
}

/// <summary>Result of <see cref="ParticleOrientation.Evaluate"/>.</summary>
public readonly struct ParticleOrientationResult
{
	public ParticleOrientationBasis Basis { get; }
	/// <summary>Multiplier on particle size along the Up (length) axis.
	/// 1 for every mode except stretched, where it is 1 + factor * speed.</summary>
	public float LengthScale { get; }
	/// <summary>Multiplier on particle size along the Right (width) axis.
	/// Always 1: stretching never changes the width.</summary>
	public float WidthScale { get; }
	/// <summary>True when the requested mode could not define a direction
	/// and a fallback was used instead: the camera billboard for velocity /
	/// stretched modes, a deterministic perpendicular width direction for
	/// axial mode when the camera looks straight down the axis. Never a
	/// NaN basis.</summary>
	public bool FellBackToBillboard { get; }
	public ParticleOrientationMode Mode { get; }

	public ParticleOrientationResult( ParticleOrientationBasis basis, float lengthScale, float widthScale, bool fellBack, ParticleOrientationMode mode )
	{
		Basis = basis; LengthScale = lengthScale; WidthScale = widthScale; FellBackToBillboard = fellBack; Mode = mode;
	}

	/// <summary>Quad corners in <see cref="ParticleLayer"/> order
	/// (-r-u, +r-u, +r+u, -r+u), with half-width = size*WidthScale/2 and
	/// half-length = size*LengthScale/2. Callers applying the layer's
	/// fade-out shrink pass size*fade, not size.</summary>
	public (Vector3, Vector3, Vector3, Vector3) QuadCorners( Vector3 position, float size )
	{
		var r = Basis.Right * ( size * WidthScale * 0.5f );
		var u = Basis.Up * ( size * LengthScale * 0.5f );
		return ( position - r - u, position + r - u, position + r + u, position - r + u );
	}

	/// <summary>Model matrix in the same scale·rotation·translation packing
	/// as <see cref="ParticleSystem.InstanceMatrix"/>: local X is scaled by
	/// WidthScale*size, local Y by LengthScale*size, local Z by size.</summary>
	public Matrix4x4 ToMatrix( Vector3 position, float size ) =>
		Matrix4x4.CreateScale( WidthScale * size, LengthScale * size, size )
		* Matrix4x4.CreateFromQuaternion( Basis.ToQuaternion() )
		* Matrix4x4.CreateTranslation( position );
}

/// <summary>Orientation-mode evaluators. Pure functions of their inputs;
/// no simulation state, no rendering, no allocation beyond the returned structs.</summary>
public static class ParticleOrientation
{
	/// <summary>Evaluate <paramref name="options"/>.Mode for one particle.
	/// <paramref name="cameraForward"/> is the direction the camera looks
	/// (NOT the direction to the camera) and must be normalized — see
	/// <see cref="ParticleOrientationOptions.Validate"/>. Start from
	/// <see cref="ParticleOrientationOptions.Default"/> and change the
	/// fields you need; a partially-initialized options struct (e.g. only
	/// Mode set, axes zero) is rejected loudly rather than silently
	/// completed, so a missing axis can never hide as a default.</summary>
	public static ParticleOrientationResult Evaluate(
		Vector3 position, Vector3 velocity, Vector3 cameraPosition, Vector3 cameraForward,
		ParticleOrientationOptions options )
	{
		options.Validate( cameraForward );
		ParticleOrientationOptions.RequireFinite( position, nameof( position ) );
		ParticleOrientationOptions.RequireFinite( velocity, nameof( velocity ) );
		ParticleOrientationOptions.RequireFinite( cameraPosition, nameof( cameraPosition ) );

		var worldUp = Vector3.Normalize( options.WorldUp );
		// Look-at normal: from the particle toward the camera. If the camera
		// sits exactly on the particle, fall back to -forward (the existing
		// layer's normal), which is unit by validation.
		var toCamera = cameraPosition - position;
		var normal = toCamera.LengthSquared() > 1e-12f ? Vector3.Normalize( toCamera ) : -cameraForward;

		ParticleOrientationBasis billboard = BillboardBasis( normal, worldUp );
		switch ( options.Mode )
		{
			case ParticleOrientationMode.CameraBillboard:
				return new ParticleOrientationResult( billboard, 1f, 1f, false, options.Mode );
			case ParticleOrientationMode.VelocityAligned:
			{
				float speed = velocity.Length();
				if ( speed <= options.SpeedEpsilon )
					return new ParticleOrientationResult( billboard, 1f, 1f, true, options.Mode );
				var up = velocity / speed;
				return new ParticleOrientationResult( BasisWithUp( up, worldUp ), 1f, 1f, false, options.Mode );
			}
			case ParticleOrientationMode.AxialBillboard:
			{
				var axis = Vector3.Normalize( options.AxialAxis );
				// Right = axis x toEye gives a normal that faces the camera
				// as far as the fixed axis allows (Normal = Right x axis).
				var right = Vector3.Cross( axis, normal );
				bool widthFallback = right.LengthSquared() <= 1e-6f;
				if ( widthFallback )
				{
					// Camera looks straight down the axis: the width direction
					// is undefined, so take any deterministic perpendicular.
					right = Vector3.Cross( worldUp, axis );
					if ( right.LengthSquared() <= 1e-6f )
						right = Vector3.Cross( MathF.Abs( axis.X ) < 0.9f ? Vector3.UnitX : Vector3.UnitY, axis );
				}
				right = Vector3.Normalize( right );
				var basis = new ParticleOrientationBasis( right, axis, Vector3.Cross( right, axis ) );
				return new ParticleOrientationResult( basis, 1f, 1f, widthFallback, options.Mode );
			}
			case ParticleOrientationMode.StretchedBillboard:
			{
				float speed = velocity.Length();
				// Project velocity onto the billboard plane; the stretch
				// direction is that projection. If it vanishes (stationary,
				// or moving straight at/away from the camera) there is no
				// stretch direction, so fall back unstretched.
				var projected = velocity - normal * Vector3.Dot( velocity, normal );
				if ( speed <= options.SpeedEpsilon || projected.LengthSquared() <= options.SpeedEpsilon * options.SpeedEpsilon )
					return new ParticleOrientationResult( billboard, 1f, 1f, true, options.Mode );
				var along = Vector3.Normalize( projected );
				var basis = new ParticleOrientationBasis( Vector3.Cross( along, normal ), along, normal );
				return new ParticleOrientationResult( basis, 1f + options.StretchFactor * speed, 1f, false, options.Mode );
			}
			default:
				throw new ArgumentException( $"ParticleOrientation: unknown mode {options.Mode}." );
		}
	}

	/// <summary>Evaluate with default options (camera billboard).</summary>
	public static ParticleOrientationResult Evaluate(
		Vector3 position, Vector3 velocity, Vector3 cameraPosition, Vector3 cameraForward ) =>
		Evaluate( position, velocity, cameraPosition, cameraForward, ParticleOrientationOptions.Default );

	/// <summary>Evaluate from a live <see cref="ParticleState"/>: position,
	/// velocity, and rotation come from the state. The four modes are
	/// camera/velocity/axis-defined and intentionally ignore the particle's
	/// spin — spin already lives in <see cref="ParticleSystem.InstanceMatrix"/>
	/// for mesh instances, and billboard quads in the existing layer carry
	/// no spin. Use <see cref="BasisFromRotation"/> when the particle's own
	/// rotation is the orientation.</summary>
	public static ParticleOrientationResult Evaluate(
		in ParticleState state, Vector3 cameraPosition, Vector3 cameraForward,
		ParticleOrientationOptions options = default ) =>
		Evaluate( state.Position, state.Velocity, cameraPosition, cameraForward,
			options.Equals( default( ParticleOrientationOptions ) ) ? ParticleOrientationOptions.Default : options );

	/// <summary>Camera billboard directly from a <see cref="LiveView"/>-style
	/// camera basis, matching <see cref="ParticleLayer"/> exactly: Right and
	/// Up as supplied (normalized), normal -forward. Rejects inconsistent
	/// views (non-perpendicular Right/Up, or cross(Right,Up) != -forward)
	/// loudly instead of returning a skewed basis.</summary>
	public static ParticleOrientationBasis CameraBillboardFromView( Vector3 right, Vector3 up, Vector3 forward )
	{
		ParticleOrientationOptions.RequireFinite( right, nameof( right ) );
		ParticleOrientationOptions.RequireFinite( up, nameof( up ) );
		new ParticleOrientationOptions { AxialAxis = Vector3.UnitZ, WorldUp = Vector3.UnitZ, StretchFactor = 0, SpeedEpsilon = 0 }.Validate( forward );
		if ( right.LengthSquared() <= 1e-12f || up.LengthSquared() <= 1e-12f )
			throw new ArgumentException( "ParticleOrientation: view Right and Up must be non-zero." );
		var r = Vector3.Normalize( right );
		var u = Vector3.Normalize( up );
		if ( MathF.Abs( Vector3.Dot( r, u ) ) > 1e-3f )
			throw new ArgumentException( $"ParticleOrientation: view Right and Up must be perpendicular, got dot {Vector3.Dot( r, u )}." );
		var normal = Vector3.Cross( r, u );
		if ( Vector3.Dot( normal, -forward ) < 1f - 1e-3f )
			throw new ArgumentException( "ParticleOrientation: view basis is inconsistent: cross(Right, Up) must equal -forward." );
		return new ParticleOrientationBasis( r, u, normal );
	}

	/// <summary>Basis from the particle's own rotation quaternion
	/// (Right = q*X, Up = q*Y, Normal = q*Z). The quaternion is normalized
	/// by this method; a zero or non-finite quaternion is rejected.</summary>
	public static ParticleOrientationBasis BasisFromRotation( Quaternion rotation )
	{
		if ( !float.IsFinite( rotation.X ) || !float.IsFinite( rotation.Y ) || !float.IsFinite( rotation.Z ) || !float.IsFinite( rotation.W ) )
			throw new ArgumentException( $"ParticleOrientation: rotation must be finite, got {rotation}." );
		float lenSq = rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z + rotation.W * rotation.W;
		if ( lenSq <= 1e-12f )
			throw new ArgumentException( "ParticleOrientation: rotation must be a non-zero quaternion." );
		var q = Quaternion.Normalize( rotation );
		return new ParticleOrientationBasis(
			Vector3.Transform( Vector3.UnitX, q ),
			Vector3.Transform( Vector3.UnitY, q ),
			Vector3.Transform( Vector3.UnitZ, q ) );
	}

	// Billboard with the given camera-facing normal: Right = worldUp x normal
	// (so for the canonical view — forward +Y, worldUp Z — Right = X, and
	// Up = normal x Right = Z, exactly the existing layer's basis).
	static ParticleOrientationBasis BillboardBasis( Vector3 normal, Vector3 worldUp )
	{
		var right = Vector3.Cross( worldUp, normal );
		if ( right.LengthSquared() <= 1e-6f )
			right = Vector3.Cross( MathF.Abs( normal.X ) < 0.9f ? Vector3.UnitX : Vector3.UnitY, normal );
		right = Vector3.Normalize( right );
		return new ParticleOrientationBasis( right, Vector3.Cross( normal, right ), normal );
	}

	// Basis whose Up axis is fixed: Right = worldUp x Up (falling back to a
	// deterministic perpendicular when Up is parallel to worldUp),
	// Normal = Right x Up, so Right x Up == Normal by construction.
	static ParticleOrientationBasis BasisWithUp( Vector3 up, Vector3 worldUp )
	{
		var right = Vector3.Cross( worldUp, up );
		if ( right.LengthSquared() <= 1e-6f )
			right = Vector3.Cross( MathF.Abs( up.X ) < 0.9f ? Vector3.UnitX : Vector3.UnitY, up );
		right = Vector3.Normalize( right );
		return new ParticleOrientationBasis( right, up, Vector3.Cross( right, up ) );
	}
}
