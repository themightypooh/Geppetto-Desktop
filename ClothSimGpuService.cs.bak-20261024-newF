using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Effigy;
using Silk.NET.OpenGL;

namespace Marionette.Desktop;

/// <summary>
/// GPU cloth simulation. Uses OpenGL 4.3+ compute shaders for integration, tearing,
/// constraint projection and collision. Falls back to the pure CPU solver whenever an
/// advanced feature is requested that the GPU path does not implement.
/// </summary>
public static class ClothSimGpuService
{
	[StructLayout( LayoutKind.Sequential, Pack = 4 )]
	struct GpuEdge { public int a; public int b; public float rest; }

	sealed class GpuBuffers
	{
		public uint IntegrateProg, TearProg, ConstraintProg, CollisionProg;
		public uint PosBuf, PrevBuf, PinBuf, EdgeBuf;
		public int N;
		public int NEdges;
		public int LocGravity, LocDamping, LocDt, LocN;
		public int LocTearStretch, LocNEdgesTear;
		public int LocStiffness, LocNEdgesConstraint;
		public int LocObstacleCenter, LocObstacleRadius, LocFloorY, LocHasFloor, LocBounce, LocFriction, LocNVertsCollision;
	}

	static readonly Dictionary<ClothSimService.State, GpuBuffers> _buffers = new();

	static bool Advanced( ClothSimService.Options o ) =>
		o.Wind.LengthSquared > 0f || o.WindGustStrength != 0f || o.WindTurbulence != 0f ||
		o.VortexStrength != 0f || o.Turbulence != 0f || o.Lift != 0f || o.Buoyancy != 0f ||
		o.AirDrag > 0f || o.MaxSpeed > 0f || o.SleepThreshold > 0f ||
		( o.Obstacles != null && o.Obstacles.Count > 1 ) ||
		( o.ObstacleTriangles != null && o.ObstacleTriangles.Count > 0 ) ||
		( o.Planes != null && o.Planes.Count > 0 ) ||
		( o.Capsules != null && o.Capsules.Count > 0 ) ||
		o.MaxStretch > 0f || o.Plasticity > 0f ||
		o.SelfCollisionRadius > 0f || o.Pressure != 0f;

	const string IntegrateSrc = @"#version 430 core
layout(local_size_x = 64) in;
layout(std430, binding = 0) buffer PosBuf { vec4 positions[]; };
layout(std430, binding = 1) buffer PrevBuf { vec4 prevPositions[]; };
layout(std430, binding = 2) buffer PinBuf { int pinned[]; };
uniform vec3 uGravity;
uniform float uDamping;
uniform float uDt;
uniform int uN;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uN)) return;
    if (pinned[i] == 1) { prevPositions[i] = positions[i]; return; }
    vec3 p = positions[i].xyz;
    vec3 q = prevPositions[i].xyz;
    vec3 vel = (p - q) * uDamping;
    vec3 next = p + vel + uGravity * (uDt * uDt);
    prevPositions[i] = vec4(p, 0.0);
    positions[i] = vec4(next, 0.0);
}
";

	const string TearSrc = @"#version 430 core
layout(local_size_x = 64) in;
layout(std430, binding = 0) buffer PosBuf { vec4 positions[]; };
struct Edge { int a; int b; float rest; };
layout(std430, binding = 3) buffer EdgeBuf { Edge edges[]; };
uniform float uTearStretch;
uniform int uNEdges;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uNEdges)) return;
    if (edges[i].rest < 0.0) return;
    int a = edges[i].a;
    int b = edges[i].b;
    vec3 pa = positions[a].xyz;
    vec3 pb = positions[b].xyz;
    float dist = length(pa - pb);
    if (dist > edges[i].rest * uTearStretch) edges[i].rest = -1.0;
}
";

	const string ConstraintSrc = @"#version 430 core
layout(local_size_x = 64) in;
layout(std430, binding = 0) buffer PosBuf { vec4 positions[]; };
layout(std430, binding = 2) buffer PinBuf { int pinned[]; };
struct Edge { int a; int b; float rest; };
layout(std430, binding = 3) buffer EdgeBuf { Edge edges[]; };
uniform float uStiffness;
uniform int uNEdges;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uNEdges)) return;
    if (edges[i].rest < 0.0) return;
    int a = edges[i].a;
    int b = edges[i].b;
    if (pinned[a] == 1 && pinned[b] == 1) return;
    vec3 pa = positions[a].xyz;
    vec3 pb = positions[b].xyz;
    vec3 delta = pb - pa;
    float dist = length(delta);
    if (dist < 1e-9) return;
    float diff = (dist - edges[i].rest) / dist;
    float wA = (pinned[a] == 1) ? 0.0 : 1.0;
    float wB = (pinned[b] == 1) ? 0.0 : 1.0;
    float wSum = wA + wB;
    if (wSum < 1e-12) return;
    if (pinned[a] == 0) positions[a].xyz += delta * (diff * uStiffness * wA / wSum);
    if (pinned[b] == 0) positions[b].xyz -= delta * (diff * uStiffness * wB / wSum);
}
";

	const string CollisionSrc = @"#version 430 core
layout(local_size_x = 64) in;
layout(std430, binding = 0) buffer PosBuf { vec4 positions[]; };
layout(std430, binding = 1) buffer PrevBuf { vec4 prevPositions[]; };
layout(std430, binding = 2) buffer PinBuf { int pinned[]; };
uniform vec3 uObstacleCenter;
uniform float uObstacleRadius;
uniform int uHasFloor;
uniform float uFloorY;
uniform float uBounce;
uniform float uFriction;
uniform int uN;
void main() {
    uint i = gl_GlobalInvocationID.x;
    if (i >= uint(uN)) return;
    if (pinned[i] == 1) return;
    vec3 p = positions[i].xyz;
    // sphere obstacle
    if (uObstacleRadius > 0.0) {
        vec3 d = p - uObstacleCenter;
        float dist = length(d);
        if (dist < uObstacleRadius && dist > 1e-9) {
            vec3 q = uObstacleCenter + d * (uObstacleRadius / dist);
            positions[i] = vec4(q, 0.0);
            prevPositions[i] = vec4(q, 0.0);
            p = q;
        }
    }
    // floor
    if (uHasFloor == 1 && p.y < uFloorY) {
        vec3 q = vec3(p.x, uFloorY, p.z);
        positions[i] = vec4(q, 0.0);
        float vy = prevPositions[i].y;
        float px = prevPositions[i].x;
        float pz = prevPositions[i].z;
        px = px + (q.x - px) * uFriction;
        pz = pz + (q.z - pz) * uFriction;
        prevPositions[i] = vec4(px, uFloorY + (uFloorY - vy) * uBounce, pz, 0.0);
    }
}
";

	static uint Compile( GL gl, string src, ShaderType type )
	{
		uint sh = gl.CreateShader( type );
		gl.ShaderSource( sh, src );
		gl.CompileShader( sh );
		gl.GetShader( sh, ShaderParameterName.CompileStatus, out int ok );
		if ( ok == 0 )
		{
			var log = gl.GetShaderInfoLog( sh );
			gl.DeleteShader( sh );
			throw new InvalidOperationException( "compute shader compile failed:\n" + log );
		}
		return sh;
	}

	static uint Link( GL gl, uint sh )
	{
		uint prog = gl.CreateProgram();
		gl.AttachShader( prog, sh );
		gl.LinkProgram( prog );
		gl.GetProgram( prog, ProgramPropertyARB.LinkStatus, out int linked );
		gl.DetachShader( prog, sh );
		gl.DeleteShader( sh );
		if ( linked == 0 )
		{
			var log = gl.GetProgramInfoLog( prog );
			gl.DeleteProgram( prog );
			throw new InvalidOperationException( "compute program link failed:\n" + log );
		}
		return prog;
	}

	static unsafe GpuBuffers Ensure( GL gl, ClothSimService.State state )
	{
		if ( _buffers.TryGetValue( state, out var b ) && b.N == state.Positions.Length && b.NEdges == state.Edges.Count ) return b;
		if ( b != null )
		{
			gl.DeleteBuffer( b.PosBuf ); gl.DeleteBuffer( b.PrevBuf ); gl.DeleteBuffer( b.PinBuf ); gl.DeleteBuffer( b.EdgeBuf );
			gl.DeleteProgram( b.IntegrateProg ); gl.DeleteProgram( b.TearProg ); gl.DeleteProgram( b.ConstraintProg ); gl.DeleteProgram( b.CollisionProg );
			_buffers.Remove( state );
		}
		b = new GpuBuffers { N = state.Positions.Length, NEdges = state.Edges.Count };
		b.IntegrateProg = Link( gl, Compile( gl, IntegrateSrc, ShaderType.ComputeShader ) );
		b.TearProg = Link( gl, Compile( gl, TearSrc, ShaderType.ComputeShader ) );
		b.ConstraintProg = Link( gl, Compile( gl, ConstraintSrc, ShaderType.ComputeShader ) );
		b.CollisionProg = Link( gl, Compile( gl, CollisionSrc, ShaderType.ComputeShader ) );

		b.LocGravity = gl.GetUniformLocation( b.IntegrateProg, "uGravity" );
		b.LocDamping = gl.GetUniformLocation( b.IntegrateProg, "uDamping" );
		b.LocDt = gl.GetUniformLocation( b.IntegrateProg, "uDt" );
		b.LocN = gl.GetUniformLocation( b.IntegrateProg, "uN" );

		b.LocTearStretch = gl.GetUniformLocation( b.TearProg, "uTearStretch" );
		b.LocNEdgesTear = gl.GetUniformLocation( b.TearProg, "uNEdges" );

		b.LocStiffness = gl.GetUniformLocation( b.ConstraintProg, "uStiffness" );
		b.LocNEdgesConstraint = gl.GetUniformLocation( b.ConstraintProg, "uNEdges" );

		b.LocObstacleCenter = gl.GetUniformLocation( b.CollisionProg, "uObstacleCenter" );
		b.LocObstacleRadius = gl.GetUniformLocation( b.CollisionProg, "uObstacleRadius" );
		b.LocHasFloor = gl.GetUniformLocation( b.CollisionProg, "uHasFloor" );
		b.LocFloorY = gl.GetUniformLocation( b.CollisionProg, "uFloorY" );
		b.LocBounce = gl.GetUniformLocation( b.CollisionProg, "uBounce" );
		b.LocFriction = gl.GetUniformLocation( b.CollisionProg, "uFriction" );
		b.LocNVertsCollision = gl.GetUniformLocation( b.CollisionProg, "uN" );

		b.PosBuf = gl.CreateBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.PosBuf );
		gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)( state.Positions.Length * 16 ), null, BufferUsageARB.DynamicDraw );

		b.PrevBuf = gl.CreateBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.PrevBuf );
		gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)( state.PrevPositions.Length * 16 ), null, BufferUsageARB.DynamicDraw );

		b.PinBuf = gl.CreateBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.PinBuf );
		gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)( state.Pinned.Length * 4 ), null, BufferUsageARB.DynamicDraw );

		b.EdgeBuf = gl.CreateBuffer();
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.EdgeBuf );
		gl.BufferData( BufferTargetARB.ShaderStorageBuffer, (nuint)( Math.Max( 1, state.Edges.Count ) * Marshal.SizeOf<GpuEdge>() ), null, BufferUsageARB.DynamicDraw );

		_buffers[ state ] = b;
		return b;
	}

	public static void Upload( ClothSimService.State state )
	{
		if ( state is null ) return;
		if ( state.GpuPositions == null || state.GpuPositions.Length != state.Positions.Length )
			state.GpuPositions = new Vec3[ state.Positions.Length ];
		Array.Copy( state.Positions, state.GpuPositions, state.Positions.Length );
	}

	public static unsafe void Upload( ClothSimService.State state, GL gl )
	{
		if ( state is null || gl is null ) return;
		Upload( state );
		var b = Ensure( gl, state );
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.PosBuf );
		var pos4 = new float[ state.Positions.Length * 4 ];
		for ( int i = 0; i < state.Positions.Length; i++ ) { pos4[i*4]=state.Positions[i].x; pos4[i*4+1]=state.Positions[i].y; pos4[i*4+2]=state.Positions[i].z; pos4[i*4+3]=0; }
		fixed ( float* p = pos4 )
			gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)( pos4.Length * 4 ), p );
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.PrevBuf );
		var prev4 = new float[ state.PrevPositions.Length * 4 ];
		for ( int i = 0; i < state.PrevPositions.Length; i++ ) { prev4[i*4]=state.PrevPositions[i].x; prev4[i*4+1]=state.PrevPositions[i].y; prev4[i*4+2]=state.PrevPositions[i].z; prev4[i*4+3]=0; }
		fixed ( float* p = prev4 )
			gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)( prev4.Length * 4 ), p );
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.PinBuf );
		var pint = new int[ state.Pinned.Length ];
		for ( int i = 0; i < pint.Length; i++ ) pint[ i ] = state.Pinned[ i ] ? 1 : 0;
		fixed ( int* p = pint )
			gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)( pint.Length * 4 ), p );
		// edges
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.EdgeBuf );
		int ec = state.Edges.Count;
		if ( ec > 0 )
		{
			var earr = new GpuEdge[ ec ];
			for ( int i = 0; i < ec; i++ ) { earr[i].a = state.Edges[i].a; earr[i].b = state.Edges[i].b; earr[i].rest = state.Edges[i].rest; }
			int sz = Marshal.SizeOf<GpuEdge>();
			fixed ( GpuEdge* p = earr )
				gl.BufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)( ec * sz ), p );
		}
	}

	public static void Download( ClothSimService.State state )
	{
		if ( state is null || state.GpuPositions == null ) return;
		Array.Copy( state.GpuPositions, state.Positions, state.Positions.Length );
	}

	public static unsafe void Download( ClothSimService.State state, GL gl )
	{
		if ( state is null || gl is null ) return;
		if ( !_buffers.TryGetValue( state, out var b ) ) { Download( state ); return; }
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.PosBuf );
		var pos4 = new float[ state.Positions.Length * 4 ];
		fixed ( float* p = pos4 )
			gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)( pos4.Length * 4 ), p );
		for ( int i = 0; i < state.Positions.Length; i++ ) state.Positions[i] = new Vec3( pos4[i*4], pos4[i*4+1], pos4[i*4+2] );
		gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.PrevBuf );
		var prev4 = new float[ state.PrevPositions.Length * 4 ];
		fixed ( float* p = prev4 )
			gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)( prev4.Length * 4 ), p );
		for ( int i = 0; i < state.PrevPositions.Length; i++ ) state.PrevPositions[i] = new Vec3( prev4[i*4], prev4[i*4+1], prev4[i*4+2] );
		// edges (tearing)
		if ( state.Edges.Count > 0 )
		{
			int ec = state.Edges.Count;
			var earr = new GpuEdge[ ec ];
			gl.BindBuffer( BufferTargetARB.ShaderStorageBuffer, b.EdgeBuf );
			int sz = Marshal.SizeOf<GpuEdge>();
			fixed ( GpuEdge* p = earr )
				gl.GetBufferSubData( BufferTargetARB.ShaderStorageBuffer, 0, (nuint)( ec * sz ), p );
			var newEdges = new List<(int a, int b, float rest)>( ec );
			for ( int i = 0; i < ec; i++ )
			{
				if ( earr[i].rest < 0f ) continue;
				newEdges.Add( ( earr[i].a, earr[i].b, earr[i].rest ) );
			}
			state.Edges = newEdges;
		}
		Array.Copy( state.Positions, state.GpuPositions, state.Positions.Length );
	}

	public static void Step( ClothSimService.State state, float dt, ClothSimService.Options options )
	{
		ClothSimService.Step( state, dt, options );
		Upload( state );
	}

	public static unsafe void Step( ClothSimService.State state, float dt, ClothSimService.Options options, GL gl )
	{
		if ( state is null ) return;
		options ??= new ClothSimService.Options();
		if ( Advanced( options ) || gl is null )
		{
			ClothSimService.Step( state, dt, options );
			Upload( state, gl );
			return;
		}
		var b = Ensure( gl, state );
		Upload( state, gl );

		// integrate
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 0, b.PosBuf );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 1, b.PrevBuf );
		gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 2, b.PinBuf );
		gl.UseProgram( b.IntegrateProg );
		gl.Uniform3( b.LocGravity, options.Gravity.x, options.Gravity.y, options.Gravity.z );
		gl.Uniform1( b.LocDamping, options.Damping );
		gl.Uniform1( b.LocDt, dt );
		gl.Uniform1( b.LocN, state.Positions.Length );
		uint groups = (uint)( ( state.Positions.Length + 63 ) / 64 );
		if ( groups == 0 ) groups = 1;
		gl.DispatchCompute( groups, 1, 1 );
		gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );

		// tear
		if ( options.TearStretch > 0f && state.Edges.Count > 0 )
		{
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 3, b.EdgeBuf );
			gl.UseProgram( b.TearProg );
			gl.Uniform1( b.LocTearStretch, options.TearStretch );
			gl.Uniform1( b.LocNEdgesTear, state.Edges.Count );
			uint egroups = (uint)( ( state.Edges.Count + 63 ) / 64 );
			if ( egroups == 0 ) egroups = 1;
			gl.DispatchCompute( egroups, 1, 1 );
			gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		}

		// constraints
		if ( state.Edges.Count > 0 )
		{
			gl.BindBufferBase( BufferTargetARB.ShaderStorageBuffer, 3, b.EdgeBuf );
			gl.UseProgram( b.ConstraintProg );
			gl.Uniform1( b.LocStiffness, options.Stiffness );
			gl.Uniform1( b.LocNEdgesConstraint, state.Edges.Count );
			uint cgroups = (uint)( ( state.Edges.Count + 63 ) / 64 );
			if ( cgroups == 0 ) cgroups = 1;
			for ( int it = 0; it < options.ConstraintIterations; it++ )
			{
				gl.DispatchCompute( cgroups, 1, 1 );
				gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
			}
		}

		// collision
		{
			gl.UseProgram( b.CollisionProg );
			gl.Uniform3( b.LocObstacleCenter, options.ObstacleCenter.x, options.ObstacleCenter.y, options.ObstacleCenter.z );
			gl.Uniform1( b.LocObstacleRadius, options.ObstacleRadius );
			int hasFloor = float.IsNaN( options.FloorY ) ? 0 : 1;
			gl.Uniform1( b.LocHasFloor, hasFloor );
			gl.Uniform1( b.LocFloorY, float.IsNaN( options.FloorY ) ? 0f : options.FloorY );
			gl.Uniform1( b.LocBounce, options.Bounce );
			gl.Uniform1( b.LocFriction, options.Friction );
			gl.Uniform1( b.LocNVertsCollision, state.Positions.Length );
			uint vgroups = (uint)( ( state.Positions.Length + 63 ) / 64 );
			if ( vgroups == 0 ) vgroups = 1;
			gl.DispatchCompute( vgroups, 1, 1 );
			gl.MemoryBarrier( MemoryBarrierMask.ShaderStorageBarrierBit );
		}

		Download( state, gl );

		state.Time += dt;
		// advanced/extra CPU post-pass for primary? already handled.
		// additional obstacles (if any) as CPU post-pass
		if ( options.Obstacles != null && options.Obstacles.Count > 1 )
		{
			// apply remaining obstacles
			var opt = options;
			// reuse PostIntegrate? It would redo all. Instead manual:
			for ( int oi = 1; oi < opt.Obstacles.Count; oi++ )
			{
				var (oc, or) = opt.Obstacles[ oi ];
				if ( or <= 0f ) continue;
				for ( int i = 0; i < state.Positions.Length; i++ )
				{
					var d = state.Positions[ i ] - oc;
					float dist = d.Length;
					if ( dist < or && dist > 1e-9f )
					{
						var q = oc + d * ( or / dist );
						state.Positions[ i ] = q;
						state.PrevPositions[ i ] = q;
					}
				}
			}
			Array.Copy( state.Positions, state.GpuPositions, state.Positions.Length );
		}
		// single primary obstacle radius handled on GPU; ObstacleRadius (legacy single) also handled.
	}

	public static void Dispatch( int workGroups ) { }

	public static void Dispatch( GL gl, ClothSimService.State state, float dt, ClothSimService.Options options = null )
		=> Step( state, dt, options ?? new ClothSimService.Options(), gl );

	public static ClothSimService.State Create( PolyMesh mesh, ClothSimService.Options options = null, System.Collections.Generic.IEnumerable<int> pinned = null )
		=> ClothSimService.Create( mesh, options, pinned );

	public static PolyMesh ToMesh( ClothSimService.State state, PolyMesh source )
		=> ClothSimService.ToMesh( state, source );
}
