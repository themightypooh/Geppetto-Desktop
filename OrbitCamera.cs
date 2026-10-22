using System;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>
/// Z-up orbit camera matching Effigy's coordinate system (+x forward, +y left, +z up).
/// Same yaw/elevation convention as <c>RenderView.Turn</c>.
/// </summary>
public sealed class OrbitCamera
{
	public Vector3 Target;
	public float Distance = 200f;
	public float YawDegrees = 35f;
	public float PitchDegrees = 18f;
	public float FovDegrees = 45f;
	public float Near = 0.1f;
	public float Far = 5000f;
	/// <summary>Flat projection: no perspective, so a front/side/top view lines up with a reference
	/// picture. The visible height matches what the perspective view shows at the target.</summary>
	public bool Orthographic;

	public float MinDistance = 5f;
	public float MaxDistance = 2000f;
	public float MinPitch = -89f;
	public float MaxPitch = 89f;
	public bool Flying { get; private set; }
	public Vector3 FlyPosition { get; private set; }
	public float FlySpeed { get; set; } = 28f;

	/// <summary>
	/// The start view a model opens with: FBX three-quarter, --iso, then explicit --yaw/--pitch.
	/// Shared by the window and --headless so both paths frame the same shot.
	/// </summary>
	public void ApplyStartView( string modelPath, bool iso, float? yaw, float? pitch )
	{
		if ( modelPath is not null && modelPath.EndsWith( ".fbx", StringComparison.OrdinalIgnoreCase ) ) YawDegrees = -55;
		if ( iso ) { YawDegrees = -38.66f; PitchDegrees = 23.2f; }
		if ( yaw.HasValue ) YawDegrees = yaw.Value;
		if ( pitch.HasValue ) PitchDegrees = Math.Clamp( pitch.Value, MinPitch, MaxPitch );
	}

	public void AdjustFlySpeed( float wheelSteps )
	{
		FlySpeed = Math.Clamp( FlySpeed * MathF.Pow( 1.25f, wheelSteps ), 1f, 250f );
	}

	public Vector3 Eye
	{
		get
		{
			var yaw = YawDegrees * MathF.PI / 180f;
			var pitch = PitchDegrees * MathF.PI / 180f;
			var cx = MathF.Cos( yaw ) * MathF.Cos( pitch );
			var cy = MathF.Sin( yaw ) * MathF.Cos( pitch );
			var cz = MathF.Sin( pitch );
			return Flying ? FlyPosition : Target + new Vector3( cx, cy, cz ) * Distance;
		}
	}

	public void BeginFly()
	{
		if ( Flying ) return;
		FlyPosition = Eye;
		Flying = true;
	}

	public void EndFly()
	{
		if ( !Flying ) return;
		Target = FlyPosition + Forward * Distance;
		Flying = false;
	}

	public Vector3 Forward
	{
		get
		{
			var yaw = YawDegrees * MathF.PI / 180f;
			var pitch = PitchDegrees * MathF.PI / 180f;
			return new Vector3( -MathF.Cos( yaw ) * MathF.Cos( pitch ),
				-MathF.Sin( yaw ) * MathF.Cos( pitch ), -MathF.Sin( pitch ) );
		}
	}

	public void MoveFly( float forward, float right, float up, float seconds, bool sprint )
	{
		if ( !Flying ) return;
		var side = Vector3.Normalize( Vector3.Cross( Forward, Vector3.UnitZ ) );
		var motion = Forward * forward + side * right + Vector3.UnitZ * up;
		if ( motion.LengthSquared() > 0 )
			FlyPosition += Vector3.Normalize( motion ) * FlySpeed * (sprint ? 3f : 1f) * seconds;
	}

	public void Orbit( float deltaYawDegrees, float deltaPitchDegrees )
	{
		YawDegrees += deltaYawDegrees;
		PitchDegrees = Math.Clamp( PitchDegrees + deltaPitchDegrees, MinPitch, MaxPitch );
	}

	public void Zoom( float factor )
	{
		Distance = Math.Clamp( Distance * factor, MinDistance, MaxDistance );
	}

	public void Frame( Vector3 centre, float radius )
	{
		Target = centre;
		Flying = false;
		Distance = Math.Clamp( radius * 2.4f, MinDistance, MaxDistance );
		Near = Math.Max( 0.01f, Distance * 0.001f );
		Far = Math.Max( Distance * 20f, radius * 40f );
	}

	public Matrix4x4 ViewMatrix()
	{
		var eye = Eye;
		var up = Vector3.UnitZ;
		var forward = Flying ? Forward : Vector3.Normalize( Target - eye );
		if ( MathF.Abs( Vector3.Dot( forward, up ) ) > 0.999f )
			up = Vector3.UnitX;
		return Matrix4x4.CreateLookAt( eye, eye + forward, up );
	}

	public Matrix4x4 ProjectionMatrix( float aspect )
	{
		aspect = Math.Max( 1e-3f, aspect );
		if ( Orthographic )
		{
			float half = Distance * MathF.Tan( FovDegrees * MathF.PI / 360f );
			var ortho = Matrix4x4.CreateOrthographic( 2 * half * aspect, 2 * half, Near, Far );
			// depth -1..1 like the perspective path below
			ortho.M33 = -2f / (Far - Near);
			ortho.M43 = -(Far + Near) / (Far - Near);
			return ortho;
		}
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(
			FovDegrees * MathF.PI / 180f, aspect, Near, Far );
		// System.Numerics uses depth 0..1; OpenGL's default clip volume is -1..1.
		projection.M33 = -(Far + Near) / (Far - Near);
		projection.M43 = -2f * Far * Near / (Far - Near);
		return projection;
	}

	/// <summary>
	/// Unproject a 2D window pixel coordinate into a world-space ray (Origin, Direction).
	/// </summary>
	public (Vector3 Origin, Vector3 Direction) ScreenRay( float mouseX, float mouseY, float viewportW, float viewportH )
	{
		float aspect = viewportW / MathF.Max( 1f, viewportH );
		var viewProj = ViewMatrix() * ProjectionMatrix( aspect );
		if ( !Matrix4x4.Invert( viewProj, out var invVp ) )
			return (Eye, Forward);

		float ndcX = (2f * mouseX / MathF.Max( 1f, viewportW )) - 1f;
		float ndcY = 1f - (2f * mouseY / MathF.Max( 1f, viewportH ));

		var nearH = Vector4.Transform( new Vector4( ndcX, ndcY, -1f, 1f ), invVp );
		var farH  = Vector4.Transform( new Vector4( ndcX, ndcY,  1f, 1f ), invVp );

		var nearPt = new Vector3( nearH.X, nearH.Y, nearH.Z ) / MathF.Max( 1e-6f, nearH.W );
		var farPt  = new Vector3( farH.X,  farH.Y,  farH.Z )  / MathF.Max( 1e-6f, farH.W );
		var dir = Vector3.Normalize( farPt - nearPt );
		// flat view: rays are parallel, each starting where the pixel sits on the near plane
		return (Orthographic ? nearPt : Eye, dir);
	}
}
