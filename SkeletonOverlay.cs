#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// Zero-allocation 3D line batcher and ray-cast picker for skeletons and Effigy bodies.
/// Renders X-ray bone links, joint diamonds, and selected body cages directly in OpenGL 4.6.
/// </summary>
public sealed class SkeletonOverlay : IDisposable
{
	const int MaxVertices = 8192;
	const int FloatsPerVertex = 6; // pos.xyz + col.rgb

	const string LineVertexSrc = @"#version 460 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aCol;
uniform mat4 uViewProj;
out vec3 vCol;
void main() {
	vCol = aCol;
	gl_Position = uViewProj * vec4(aPos, 1.0);
}
";

	const string LineFragmentSrc = @"#version 460 core
in vec3 vCol;
uniform float uAlpha;
out vec4 FragColor;
void main() {
	FragColor = vec4(vCol * uAlpha, 1.0);
}
";

	readonly GL _gl;
	readonly GlShader _shader;
	readonly int _uViewProj;
	readonly int _uAlpha;
	readonly uint _vao;
	readonly uint _vbo;
	readonly float[] _buffer = new float[MaxVertices * FloatsPerVertex];
	int _vertexCount;

	public bool ShowBones { get; set; }
	public bool ShowGrid { get; set; } = true;
	public int SelectedBone { get; set; } = -1;
	public int HoveredBone { get; set; } = -1;
	public int SelectedBody { get; set; } = -1;

	public unsafe SkeletonOverlay( GL gl )
	{
		_gl = gl;
		_shader = new GlShader( gl, LineVertexSrc, LineFragmentSrc );
		_shader.Use();
		_uViewProj = _shader.Uniform( "uViewProj" );
		_uAlpha = _shader.Uniform( "uAlpha" );

		_vao = _gl.GenVertexArray();
		_vbo = _gl.GenBuffer();

		_gl.BindVertexArray( _vao );
		_gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		_gl.BufferData( BufferTargetARB.ArrayBuffer,
			(nuint)(_buffer.Length * sizeof( float )), null,
			BufferUsageARB.DynamicDraw );

		const uint stride = FloatsPerVertex * sizeof( float );
		_gl.EnableVertexAttribArray( 0 );
		_gl.VertexAttribPointer( 0, 3, VertexAttribPointerType.Float, false, stride, (void*)0 );
		_gl.EnableVertexAttribArray( 1 );
		_gl.VertexAttribPointer( 1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof( float )) );
		_gl.BindVertexArray( 0 );
	}

	public unsafe void Draw(
		Matrix4x4 viewProj,
		Skeleton rig,
		ReadOnlySpan<Xform> worldPose,
		ViewportMesh.BodyPickEntry[] bodies,
		(Vector3 Min, Vector3 Max)? selectedBounds = null,
		IReadOnlyList<HandleGeometry>? handles = null )
	{
		_vertexCount = 0;

		// Ground is XY in this Z-up viewport, and must not X-ray through the model.
		if ( ShowGrid )
		{
			const int half = 10;
			const float step = 10f;
			for ( int i = -half; i <= half; i++ )
			{
				float c = (i == 0) ? 0.35f : 0.18f;
				AddLine( new Vector3( i * step, -half * step, 0f ), new Vector3( i * step, half * step, 0f ), c, c, c );
				AddLine( new Vector3( -half * step, i * step, 0f ), new Vector3( half * step, i * step, 0f ), c, c, c );
			}
			Flush( viewProj, xray: false );
			_vertexCount = 0;
		}

		if ( rig is { Count: > 0 } && (ShowBones || SelectedBone >= 0 || HoveredBone >= 0) )
		{
			int count = Math.Min( rig.Count, worldPose.Length );
			for ( int b = 0; b < count; b++ )
			{
				bool isSel = b == SelectedBone;
				bool isHov = b == HoveredBone;
				if ( !ShowBones && !isSel && !isHov ) continue;

				var xf = worldPose[b];
				var head = new Vector3( xf.Origin.x, xf.Origin.y, xf.Origin.z );
				int parent = rig.Bones[b].Parent;

				float r = isSel ? 0.98f : isHov ? 0.95f : 0.29f;
				float g = isSel ? 0.83f : isHov ? 0.55f : 0.89f;
				float bl = isSel ? 0.45f : isHov ? 0.25f : 0.84f;

				if ( parent >= 0 && parent < count )
				{
					var pxf = worldPose[parent];
					var pHead = new Vector3( pxf.Origin.x, pxf.Origin.y, pxf.Origin.z );
					AddLine( pHead, head, r, g, bl );
				}

				float jointRadius = isSel ? 0.65f : isHov ? 0.50f : 0.28f;
				AddDiamond( head, jointRadius, r, g, bl );

				if ( isSel && handles is null )
				{
					// Draw local X/Y/Z basis axes at selected bone
					var ax = new Vector3( xf.X.x, xf.X.y, xf.X.z ) * 4.0f;
					var ay = new Vector3( xf.Y.x, xf.Y.y, xf.Y.z ) * 4.0f;
					var az = new Vector3( xf.Z.x, xf.Z.y, xf.Z.z ) * 4.0f;
					AddLine( head, head + ax, 1.00f, 0.20f, 0.18f );
					AddLine( head, head + ay, 0.20f, 0.95f, 0.35f );
					AddLine( head, head + az, 0.20f, 0.55f, 1.00f );
					// Arrowheads
					AddLine( head + ax, head + ax * 0.85f + ay * 0.08f, 1.00f, 0.20f, 0.18f );
					AddLine( head + ax, head + ax * 0.85f - ay * 0.08f, 1.00f, 0.20f, 0.18f );
					AddLine( head + ay, head + ay * 0.85f + ax * 0.08f, 0.20f, 0.95f, 0.35f );
					AddLine( head + ay, head + ay * 0.85f - ax * 0.08f, 0.20f, 0.95f, 0.35f );
					AddLine( head + az, head + az * 0.85f + ax * 0.08f, 0.20f, 0.55f, 1.00f );
					AddLine( head + az, head + az * 0.85f - ax * 0.08f, 0.20f, 0.55f, 1.00f );
				}
			}
		}

		if ( bodies is not null && SelectedBody >= 0 && SelectedBody < bodies.Length )
		{
			var body = bodies[SelectedBody];
			AddAabb( selectedBounds?.Min ?? body.Min, selectedBounds?.Max ?? body.Max, 0.96f, 0.78f, 0.38f );
		}
		if ( handles is not null )
			EmitHandleLines( handles, AddHandleLine );

		Flush( viewProj, xray: true );
	}

	void AddHandleLine( GizmoHandleId id, Vector3 a, Vector3 b, Vector3 color ) =>
		AddLine( a, b, color.X, color.Y, color.Z );

	/// <summary>CPU tessellation used by both the GL batch and headless tests. Each emitted
	/// segment belongs to exactly one typed selectable shape. No arrowhead or constant-size
	/// skeleton axis is synthesized when handles are supplied to Draw.</summary>
	public static void EmitHandleLines( IReadOnlyList<HandleGeometry> handles,
		Action<GizmoHandleId, Vector3, Vector3, Vector3> emit )
	{
		ArgumentNullException.ThrowIfNull( handles );
		ArgumentNullException.ThrowIfNull( emit );
		for ( int i = 0; i < handles.Count; i++ )
		{
			var g = handles[i];
			var color = HandleColor( g );
			void Line( Vector3 a, Vector3 b ) => emit( g.Id, a, b, color );
			switch ( g.Shape )
			{
				case HandleShape.AxisSegment:
					Line( g.A, g.B );
					break;
				case HandleShape.PlaneQuad:
					Line( g.A, g.B ); Line( g.B, g.C );
					Line( g.C, g.D ); Line( g.D, g.A );
					break;
				case HandleShape.RotationRing:
					if ( !float.IsFinite( g.Radius ) || g.Radius <= 0 || g.Normal.LengthSquared() < 1e-8f )
						break;
					var n = Vector3.Normalize( g.Normal );
					var reference = MathF.Abs( n.Z ) < .9f ? Vector3.UnitZ : Vector3.UnitX;
					var u = Vector3.Normalize( Vector3.Cross( n, reference ) );
					var v = Vector3.Cross( n, u );
					const int ringSegments = 64;
					for ( int segment = 0; segment < ringSegments; segment++ )
					{
						float a = 2f * MathF.PI * segment / ringSegments;
						float b = 2f * MathF.PI * (segment + 1) / ringSegments;
						Line( g.A + g.Radius * (u * MathF.Cos(a) + v * MathF.Sin(a)),
							g.A + g.Radius * (u * MathF.Cos(b) + v * MathF.Sin(b)) );
					}
					break;
			}
		}
	}

	public static Vector3 HandleColor( HandleGeometry handle )
	{
		if ( handle.Active ) return new Vector3( 1f, .78f, .20f );
		if ( handle.Hovered ) return new Vector3( 1f, .98f, .75f );
		return handle.Id.Axis switch
		{
			GizmoAxis.X => new Vector3( 1f, .22f, .20f ),
			GizmoAxis.Y => new Vector3( .22f, .95f, .35f ),
			GizmoAxis.Z => new Vector3( .22f, .57f, 1f ),
			_ => handle.Id.Plane switch
			{
				GizmoPlane.XY => new Vector3( .75f, .67f, .28f ),
				GizmoPlane.YZ => new Vector3( .24f, .76f, .70f ),
				GizmoPlane.XZ => new Vector3( .76f, .45f, .69f ),
				_ => Vector3.One
			}
		};
	}

	unsafe void Flush( Matrix4x4 viewProj, bool xray )
	{
		if ( _vertexCount == 0 ) return;

		_gl.BindVertexArray( _vao );
		_gl.BindBuffer( BufferTargetARB.ArrayBuffer, _vbo );
		fixed ( float* ptr = _buffer )
		{
			_gl.BufferSubData( BufferTargetARB.ArrayBuffer, 0,
				(nuint)(_vertexCount * FloatsPerVertex * sizeof( float )), ptr );
		}

		_shader.Use();
		_shader.SetMatrix( _uViewProj, viewProj );

		// Pass 1: X-ray pass (through geometry, dimmed)
		_gl.DepthMask( false );
		if ( xray )
		{
			_gl.Disable( EnableCap.DepthTest );
			_shader.SetFloat( _uAlpha, 0.38f );
			_gl.DrawArrays( PrimitiveType.Lines, 0, (uint)_vertexCount );
		}

		// Pass 2: Unoccluded pass (full brightness)
		_gl.Enable( EnableCap.DepthTest );
		_shader.SetFloat( _uAlpha, 1.0f );
		_gl.DrawArrays( PrimitiveType.Lines, 0, (uint)_vertexCount );
		_gl.DepthMask( true );

		_gl.BindVertexArray( 0 );
	}

	public static int PickBone(
		Vector3 rayOrigin,
		Vector3 rayDir,
		Skeleton rig,
		ReadOnlySpan<Xform> worldPose,
		float hitRadius = 1.25f )
	{
		if ( rig is null || rig.Count == 0 ) return -1;
		int count = Math.Min( rig.Count, worldPose.Length );
		int best = -1;
		float bestT = float.MaxValue;
		float r2 = hitRadius * hitRadius;

		for ( int b = 0; b < count; b++ )
		{
			var o = worldPose[b].Origin;
			var p = new Vector3( o.x, o.y, o.z );
			var toJoint = p - rayOrigin;
			float t = Vector3.Dot( toJoint, rayDir );
			if ( t <= 0.1f || t >= bestT ) continue;

			var closest = rayOrigin + rayDir * t;
			float distSq = (p - closest).LengthSquared();
			if ( distSq <= r2 )
			{
				bestT = t;
				best = b;
			}
		}
		return best;
	}

	public static int PickBody(
		Vector3 rayOrigin,
		Vector3 rayDir,
		ViewportMesh.BodyPickEntry[] bodies )
	{
		if ( bodies is null || bodies.Length == 0 ) return -1;
		int best = -1;
		float bestScore = float.MaxValue;

		for ( int i = 0; i < bodies.Length; i++ )
		{
			var b = bodies[i];
			if ( !RayHitsAabb( rayOrigin, rayDir, b.Min, b.Max, out float tEnter ) )
				continue;

			// Prefer smaller/tighter parts when nested inside larger bounds.
			float diag = (b.Max - b.Min).Length();
			float score = tEnter + diag * 0.15f;
			if ( score < bestScore )
			{
				bestScore = score;
				best = i;
			}
		}
		return best;
	}

	internal static bool RayHitsAabb( Vector3 ro, Vector3 rd, Vector3 min, Vector3 max, out float tEnter )
	{
		tEnter = 0f;
		float tMin = 0.01f;
		float tMax = float.MaxValue;

		for ( int axis = 0; axis < 3; axis++ )
		{
			float o = axis == 0 ? ro.X : axis == 1 ? ro.Y : ro.Z;
			float d = axis == 0 ? rd.X : axis == 1 ? rd.Y : rd.Z;
			float bMin = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
			float bMax = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;

			if ( MathF.Abs( d ) < 1e-7f )
			{
				if ( o < bMin || o > bMax ) return false;
			}
			else
			{
				float invD = 1f / d;
				float t1 = (bMin - o) * invD;
				float t2 = (bMax - o) * invD;
				if ( t1 > t2 ) (t1, t2) = (t2, t1);
				if ( t1 > tMin ) tMin = t1;
				if ( t2 < tMax ) tMax = t2;
				if ( tMin > tMax ) return false;
			}
		}

		tEnter = tMin;
		return true;
	}

	void AddLine( Vector3 a, Vector3 b, float r, float g, float bl )
	{
		if ( _vertexCount + 2 > MaxVertices ) return;
		int idx = _vertexCount * FloatsPerVertex;
		_buffer[idx + 0] = a.X; _buffer[idx + 1] = a.Y; _buffer[idx + 2] = a.Z;
		_buffer[idx + 3] = r;   _buffer[idx + 4] = g;   _buffer[idx + 5] = bl;

		_buffer[idx + 6] = b.X; _buffer[idx + 7] = b.Y; _buffer[idx + 8] = b.Z;
		_buffer[idx + 9] = r;   _buffer[idx + 10] = g;  _buffer[idx + 11] = bl;
		_vertexCount += 2;
	}

	void AddDiamond( Vector3 c, float rad, float r, float g, float bl )
	{
		var px = c + new Vector3(  rad, 0, 0 );
		var nx = c + new Vector3( -rad, 0, 0 );
		var py = c + new Vector3( 0,  rad, 0 );
		var ny = c + new Vector3( 0, -rad, 0 );
		var pz = c + new Vector3( 0, 0,  rad );
		var nz = c + new Vector3( 0, 0, -rad );

		AddLine( px, py, r, g, bl ); AddLine( py, nx, r, g, bl );
		AddLine( nx, ny, r, g, bl ); AddLine( ny, px, r, g, bl );
		AddLine( pz, px, r, g, bl ); AddLine( pz, nx, r, g, bl );
		AddLine( pz, py, r, g, bl ); AddLine( pz, ny, r, g, bl );
		AddLine( nz, px, r, g, bl ); AddLine( nz, nx, r, g, bl );
		AddLine( nz, py, r, g, bl ); AddLine( nz, ny, r, g, bl );
	}

	void AddAabb( Vector3 min, Vector3 max, float r, float g, float bl )
	{
		var c000 = new Vector3( min.X, min.Y, min.Z );
		var c100 = new Vector3( max.X, min.Y, min.Z );
		var c010 = new Vector3( min.X, max.Y, min.Z );
		var c110 = new Vector3( max.X, max.Y, min.Z );
		var c001 = new Vector3( min.X, min.Y, max.Z );
		var c101 = new Vector3( max.X, min.Y, max.Z );
		var c011 = new Vector3( min.X, max.Y, max.Z );
		var c111 = new Vector3( max.X, max.Y, max.Z );

		AddLine( c000, c100, r, g, bl ); AddLine( c100, c110, r, g, bl );
		AddLine( c110, c010, r, g, bl ); AddLine( c010, c000, r, g, bl );
		AddLine( c001, c101, r, g, bl ); AddLine( c101, c111, r, g, bl );
		AddLine( c111, c011, r, g, bl ); AddLine( c011, c001, r, g, bl );
		AddLine( c000, c001, r, g, bl ); AddLine( c100, c101, r, g, bl );
		AddLine( c010, c011, r, g, bl ); AddLine( c110, c111, r, g, bl );
	}

	public void Dispose()
	{
		_gl.DeleteVertexArray( _vao );
		_gl.DeleteBuffer( _vbo );
		_shader.Dispose();
	}
}
