using System;
using System.Collections.Generic;
using Effigy;

namespace Marionette.Desktop;

/// <summary>
/// CPU-only cloth simulation foundation (step 1). Verlet/position-based style solver
/// with pinned vertices, gravity and edge-length constraints. Does not mutate input mesh.
/// </summary>
public static class ClothSimService
{
	public sealed class Options
	{
		public Vec3 Gravity { get; set; } = new Vec3( 0, -9.81f, 0 );
		public int ConstraintIterations { get; set; } = 4;
		public float Damping { get; set; } = 0.99f;
		/// <summary>Optional floor plane Y; vertices are constrained above it. NaN disables.</summary>
		public float FloorY { get; set; } = float.NaN;
		public Vec3 Wind { get; set; } = new Vec3( 0, 0, 0 );
		public float Turbulence { get; set; } = 0f;
		public Vec3 VortexCenter { get; set; } = new Vec3( 0, 0, 0 );
		public Vec3 VortexAxis { get; set; } = new Vec3( 0, 1, 0 );
		public float VortexStrength { get; set; } = 0f;
		public float WindTurbulence { get; set; } = 0f;
		public float WindGustStrength { get; set; } = 0f;
		public float WindGustFrequency { get; set; } = 1f;
		public Vec3 ObstacleCenter { get; set; } = new Vec3( 0, 0, 0 );
		public float ObstacleRadius { get; set; } = 0f; // 0 disables
		public List<(Vec3 center, float radius)> Obstacles { get; set; } = new();
		/// <summary>Triangle obstacles (a,b,c) for collision.</summary>
		public List<(Vec3 a, Vec3 b, Vec3 c)> ObstacleTriangles { get; set; } = new();
		public List<(Vec3 normal, float offset)> Planes { get; set; } = new(); // plane: normal·x >= offset
		public List<(Vec3 a, Vec3 b, float radius)> Capsules { get; set; } = new();
		/// <summary>Edge stretch factor above which the edge tears (removed). 0 disables.</summary>
		public float TearStretch { get; set; } = 0f;
		public float MaxSpeed { get; set; } = 0f; // 0 disables
		public float SleepThreshold { get; set; } = 0f; // 0 disables
		public float Stiffness { get; set; } = 1f;
		public float Bounce { get; set; } = 0f;
		public float SelfCollisionRadius { get; set; } = 0f; // 0 disables
		public float Friction { get; set; } = 0f;
		/// <summary>Uniform pressure (inflation) applied along face normals; 0 disables.</summary>
		public float Pressure { get; set; } = 0f;
		public float AirDrag { get; set; } = 0f;
		public float Lift { get; set; } = 0f;
		public float Buoyancy { get; set; } = 0f;
		public float Plasticity { get; set; } = 0f;
		public float MaxStretch { get; set; } = 0f; // 0 disables
	}

	public sealed class State
	{
		public Vec3[] Positions;
		public Vec3[] PrevPositions;
		public bool[] Pinned;
		public float[] Masses;
		public Vec3[] GpuPositions;
		public float Time;
		public List<(int a, int b, float rest)> Edges = new();
		public List<int[]> Faces = new();
		public State( int n )
		{
			Positions = new Vec3[ n ];
			PrevPositions = new Vec3[ n ];
			Pinned = new bool[ n ];
			Masses = new float[ n ];
			for ( int i = 0; i < n; i++ ) Masses[ i ] = 1f;
		}
	}

	public static State Create( PolyMesh mesh, Options options = null, IEnumerable<int> pinned = null )
	{
		if ( mesh is null ) throw new ArgumentNullException( nameof( mesh ) );
		options ??= new Options();
		var s = new State( mesh.Positions.Count );
		for ( int i = 0; i < mesh.Positions.Count; i++ )
		{
			s.Positions[ i ] = mesh.Positions[ i ];
			s.PrevPositions[ i ] = mesh.Positions[ i ];
		}
		if ( pinned != null ) foreach ( var i in pinned ) if ( i >= 0 && i < s.Pinned.Length ) s.Pinned[ i ] = true;
		// bending springs across shared edges: constrain opposite vertices
		var seenBend = new HashSet<(int,int)>();
		var edgeFaces = new Dictionary<(int,int), List<int>>();
		for ( int fi = 0; fi < mesh.Faces.Count; fi++ )
		{
			var f = mesh.Faces[ fi ];
			for ( int e = 0; e < f.Count; e++ )
			{
				int a = f.Indices[ e ];
				int b = f.Indices[ ( e + 1 ) % f.Count ];
				var key = a < b ? ( a, b ) : ( b, a );
				if ( !edgeFaces.TryGetValue( key, out var list ) ) { list = new List<int>(); edgeFaces[ key ] = list; }
				list.Add( fi );
			}
		}
		foreach ( var kv in edgeFaces )
		{
			if ( kv.Value.Count != 2 ) continue;
			var f0 = mesh.Faces[ kv.Value[ 0 ] ];
			var f1 = mesh.Faces[ kv.Value[ 1 ] ];
			// opposite vertices
			int opp0 = -1, opp1 = -1;
			foreach ( var v in f0.Indices ) if ( v != kv.Key.Item1 && v != kv.Key.Item2 ) { opp0 = v; break; }
			foreach ( var v in f1.Indices ) if ( v != kv.Key.Item1 && v != kv.Key.Item2 ) { opp1 = v; break; }
			if ( opp0 < 0 || opp1 < 0 || opp0 == opp1 ) continue;
			var key = opp0 < opp1 ? ( opp0, opp1 ) : ( opp1, opp0 );
			if ( seenBend.Contains( key ) ) continue;
			seenBend.Add( key );
			float rest = ( mesh.Positions[ opp1 ] - mesh.Positions[ opp0 ] ).Length;
			s.Edges.Add( ( opp0, opp1, rest ) );
		}

		// edges from faces
		var seen = new HashSet<(int,int)>();
		foreach ( var f in mesh.Faces )
		{
			for ( int e = 0; e < f.Count; e++ )
			{
				int a = f.Indices[ e ];
				int b = f.Indices[ ( e + 1 ) % f.Count ];
				var key = a < b ? ( a, b ) : ( b, a );
				if ( seen.Contains( key ) ) continue;
				seen.Add( key );
				float rest = ( mesh.Positions[ b ] - mesh.Positions[ a ] ).Length;
				s.Edges.Add( ( a, b, rest ) );
			}
			// bending/shear diagonal for polygon faces
			if ( f.Count >= 3 )
			{
				int a = f.Indices[ 0 ];
				int c = f.Indices[ 2 ];
				var key = a < c ? ( a, c ) : ( c, a );
				if ( !seen.Contains( key ) )
				{
					seen.Add( key );
					float rest = ( mesh.Positions[ c ] - mesh.Positions[ a ] ).Length;
					s.Edges.Add( ( a, c, rest ) );
				}
			}
		}
		foreach ( var f in mesh.Faces ) s.Faces.Add( (int[])f.Indices.Clone() );
		return s;
	}

	public static void Step( State state, float dt, Options options = null )
	{
		Integrate( state, dt, options );
		PostIntegrate( state, dt, options );
	}

	public static void Integrate( State state, float dt, Options options = null )
	{
		if ( state is null ) throw new ArgumentNullException( nameof( state ) );
		options ??= new Options();
		state.Time += dt;
		int n = state.Positions.Length;
		// integrate
		for ( int i = 0; i < n; i++ )
		{
			if ( state.Pinned[ i ] ) { state.PrevPositions[ i ] = state.Positions[ i ]; continue; }
			var vel0 = state.Positions[ i ] - state.PrevPositions[ i ];
			if ( options.SleepThreshold > 0f && vel0.Length < options.SleepThreshold * dt )
			{
				state.PrevPositions[ i ] = state.Positions[ i ];
				continue;
			}
			var vel = ( state.Positions[ i ] - state.PrevPositions[ i ] ) * options.Damping;
			if ( options.AirDrag > 0f ) vel = vel * ( 1f - options.AirDrag );
			if ( options.MaxSpeed > 0f )
			{
				float sp = vel.Length;
				if ( sp > options.MaxSpeed * dt ) vel = vel * ( options.MaxSpeed * dt / sp );
			}
			var wind = options.Wind;
			if ( options.WindGustStrength > 0f )
			{
				float g = (float)Math.Sin( state.Time * options.WindGustFrequency ) * options.WindGustStrength;
				wind = wind * ( 1f + g );
			}
			if ( options.WindTurbulence > 0f )
			{
				float t = (float)Math.Sin( ( state.Positions[i].x + state.Positions[i].y + state.Positions[i].z ) * 13.37 + i );
				wind = wind * ( 1f + options.WindTurbulence * t );
			}
			var gravity = options.Gravity + new Vec3( 0, options.Buoyancy, 0 );
			var lift = new Vec3( 0, 0, 0 );
			if ( options.Lift > 0f && wind.Length > 1e-9f )
			{
				var up = new Vec3( 0, 1, 0 );
				var ldir = Vec3.Cross( wind, up );
				lift = ldir * ( options.Lift / Math.Max( 1e-9f, ldir.Length ) );
			}
			var vforce = new Vec3( 0, 0, 0 );
			if ( options.VortexStrength != 0f )
			{
				var axis = options.VortexAxis;
				float al = axis.Length;
				if ( al > 1e-9f )
				{
					axis = axis / al;
					var r = state.Positions[ i ] - options.VortexCenter;
					vforce = Vec3.Cross( axis, r ) * options.VortexStrength;
				}
			}
			var tforce = new Vec3( 0, 0, 0 );
			if ( options.Turbulence != 0f )
			{
				var p = state.Positions[ i ];
				float nx = MathF.Sin( p.x * 12.9898f + p.y * 78.233f + p.z * 37.719f );
				float ny = MathF.Sin( p.y * 12.9898f + p.z * 78.233f + p.x * 37.719f );
				float nz = MathF.Sin( p.z * 12.9898f + p.x * 78.233f + p.y * 37.719f );
				tforce = new Vec3( nx, ny, nz ) * options.Turbulence;
			}
			var next = state.Positions[ i ] + vel + ( gravity + wind + lift + vforce + tforce ) * ( dt * dt );
			state.PrevPositions[ i ] = state.Positions[ i ];
			state.Positions[ i ] = next;
		}
	}

	public static void PostIntegrate( State state, float dt, Options options = null )
	{
		if ( state is null ) throw new ArgumentNullException( nameof( state ) );
		options ??= new Options();
		int n = state.Positions.Length;
		// sphere obstacle collision
		if ( options.ObstacleRadius > 0f )
		{
			for ( int i = 0; i < n; i++ )
			{
				var d = state.Positions[ i ] - options.ObstacleCenter;
				float dist = d.Length;
				if ( dist < options.ObstacleRadius && dist > 1e-9f )
				{
					var q = options.ObstacleCenter + d * ( options.ObstacleRadius / dist );
					state.Positions[ i ] = q;
					state.PrevPositions[ i ] = q;
				}
			}
		}
		foreach ( var (oc, or) in options.Obstacles )
		{
			if ( or <= 0f ) continue;
			for ( int i = 0; i < n; i++ )
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
		// tearing
		if ( options.TearStretch > 0f )
		{
			state.Edges.RemoveAll( e =>
			{
				float dist = ( state.Positions[ e.b ] - state.Positions[ e.a ] ).Length;
				return dist > e.rest * options.TearStretch;
			} );
		}
		// constraints
		for ( int it = 0; it < options.ConstraintIterations; it++ )
		{
			foreach ( var (a, b, rest) in state.Edges )
			{
				if ( state.Pinned[ a ] && state.Pinned[ b ] ) continue;
				var delta = state.Positions[ b ] - state.Positions[ a ];
				float dist = delta.Length;
				if ( dist < 1e-9f ) continue;
				float diff = ( dist - rest ) / dist;
				float wA = state.Pinned[ a ] ? 0f : 1f / Math.Max( 1e-6f, state.Masses[ a ] );
				float wB = state.Pinned[ b ] ? 0f : 1f / Math.Max( 1e-6f, state.Masses[ b ] );
				float wSum = wA + wB;
				if ( wSum < 1e-12f ) continue;
				var correction = delta * ( diff * options.Stiffness * wA / wSum );
				var correctionB = delta * ( diff * options.Stiffness * wB / wSum );
				if ( !state.Pinned[ a ] ) state.Positions[ a ] += correction;
				if ( !state.Pinned[ b ] ) state.Positions[ b ] -= correctionB;
			}
		}
		// max stretch clamp
		if ( options.MaxStretch > 0f )
		{
			foreach ( var (a, b, rest) in state.Edges )
			{
				var d = state.Positions[ b ] - state.Positions[ a ];
				float dist = d.Length;
				float maxd = rest * options.MaxStretch;
				if ( dist > maxd && dist > 1e-9f )
				{
					var scale = maxd / dist;
					var center = ( state.Positions[ a ] + state.Positions[ b ] ) * 0.5f;
					var half = d * ( 0.5f * scale );
					if ( !state.Pinned[ a ] ) state.Positions[ a ] = center - half;
					if ( !state.Pinned[ b ] ) state.Positions[ b ] = center + half;
				}
			}
		}
		// plasticity: adapt rest lengths
		if ( options.Plasticity > 0f )
		{
			var newEdges = new List<(int a, int b, float rest)>( state.Edges.Count );
			foreach ( var e in state.Edges )
			{
				float dist = ( state.Positions[ e.b ] - state.Positions[ e.a ] ).Length;
				float rest = e.rest + ( dist - e.rest ) * options.Plasticity;
				newEdges.Add( ( e.a, e.b, rest ) );
			}
			state.Edges = newEdges;
		}
		// plane collisions
		foreach ( var (pn, po) in options.Planes )
		{
			for ( int i = 0; i < n; i++ )
			{
				float d = Vec3.Dot( pn, state.Positions[ i ] ) - po;
				if ( d < 0f )
				{
					state.Positions[ i ] = state.Positions[ i ] - pn * d;
					state.PrevPositions[ i ] = state.Positions[ i ];
				}
			}
		}
		// capsule collisions
		foreach ( var (ca, cb, cr) in options.Capsules )
		{
			if ( cr <= 0f ) continue;
			var ab = cb - ca;
			float ab2 = Vec3.Dot( ab, ab );
			for ( int i = 0; i < n; i++ )
			{
				float t = ab2 > 1e-12f ? Vec3.Dot( state.Positions[ i ] - ca, ab ) / ab2 : 0f;
				t = Math.Clamp( t, 0f, 1f );
				var closest = ca + ab * t;
				var d = state.Positions[ i ] - closest;
				float dist = d.Length;
				if ( dist < cr && dist > 1e-9f )
				{
					var q = closest + d * ( cr / dist );
					state.Positions[ i ] = q;
					state.PrevPositions[ i ] = q;
				}
			}
		}
		// floor collision (after constraints)
		if ( !float.IsNaN( options.FloorY ) )
		{
			for ( int i = 0; i < n; i++ )
			{
				if ( state.Positions[ i ].y < options.FloorY )
				{
					var p = state.Positions[ i ];
					var q = new Vec3( p.x, options.FloorY, p.z );
					state.Positions[ i ] = q;
					float vy = state.PrevPositions[ i ].y;
					float px = state.PrevPositions[ i ].x;
					float pz = state.PrevPositions[ i ].z;
					px = px + ( state.Positions[ i ].x - px ) * options.Friction;
					pz = pz + ( state.Positions[ i ].z - pz ) * options.Friction;
					state.PrevPositions[ i ] = new Vec3( px, options.FloorY + ( options.FloorY - vy ) * options.Bounce, pz );
				}
			}
		}
		if ( options.ObstacleTriangles != null && options.ObstacleTriangles.Count > 0 ) TriangleObstaclePass( state, options.ObstacleTriangles );
		if ( options.SelfCollisionRadius > 0f ) SelfCollisionPass( state, options.SelfCollisionRadius );
		if ( options.Pressure != 0f ) PressurePass( state, options.Pressure, dt );
	}

	public static void PressurePass( State state, float pressure, float dt )
	{
		if ( state is null || pressure == 0f ) return;
		int n = state.Positions.Length;
		var force = new Vec3[ n ];
		foreach ( var f in state.Faces )
		{
			if ( f.Length < 3 ) continue;
			var a = state.Positions[ f[ 0 ] ];
			var b = state.Positions[ f[ 1 ] ];
			var c = state.Positions[ f[ 2 ] ];
			var nrm = Vec3.Cross( b - a, c - a );
			float area = nrm.Length * 0.5f;
			if ( area < 1e-12f ) continue;
			nrm = nrm * ( 1f / ( nrm.Length ) );
			var contrib = nrm * ( pressure * area / 3f );
			for ( int k = 0; k < 3 && k < f.Length; k++ )
			{
				int vi = f[ k ];
				if ( vi >= 0 && vi < n ) force[ vi ] += contrib;
			}
		}
		float scale = dt * dt;
		for ( int i = 0; i < n; i++ )
		{
			if ( state.Pinned[ i ] ) continue;
			state.Positions[ i ] += force[ i ] * scale;
		}
	}

	public static void TriangleObstaclePass( State state, List<(Vec3 a, Vec3 b, Vec3 c)> tris )
	{
		if ( state is null || tris is null ) return;
		int n = state.Positions.Length;
		for ( int i = 0; i < n; i++ )
		{
			if ( state.Pinned[ i ] ) continue;
			foreach ( var (ta, tb, tc) in tris )
			{
				// closest point on triangle
				var p = state.Positions[ i ];
				var ab = tb - ta; var ac = tc - ta; var ap = p - ta;
				float d1 = Vec3.Dot( ab, ap ); float d2 = Vec3.Dot( ac, ap );
				if ( d1 <= 0f && d2 <= 0f ) { /* vertex region A */ var diff = p - ta; float dist = diff.Length; if ( dist < 1e-9f ) continue; /* push */ if ( dist < 0.05f ) { /* ignore */ } continue; }
				// simplified: push if inside triangle's plane within radius
				var nrm = Vec3.Cross( ab, ac );
				float nl = nrm.Length;
				if ( nl < 1e-12f ) continue;
				nrm = nrm / nl;
				float distPlane = Vec3.Dot( p - ta, nrm );
				if ( MathF.Abs( distPlane ) < 0.05f )
				{
					// project point to triangle barycentric
					// simple: if within triangle, push out
					var q = p - nrm * distPlane;
					// barycentric check
					var v0 = tc - ta; var v1 = tb - ta; var v2 = q - ta;
					float dot00 = Vec3.Dot( v0, v0 );
					float dot01 = Vec3.Dot( v0, v1 );
					float dot02 = Vec3.Dot( v0, v2 );
					float dot11 = Vec3.Dot( v1, v1 );
					float dot12 = Vec3.Dot( v1, v2 );
					float invDen = 1f / ( dot00 * dot11 - dot01 * dot01 + 1e-12f );
					float u = ( dot11 * dot02 - dot01 * dot12 ) * invDen;
					float v = ( dot00 * dot12 - dot01 * dot02 ) * invDen;
					if ( u >= 0f && v >= 0f && u + v <= 1f )
					{
						state.Positions[ i ] = q + nrm * 0.05f;
						state.PrevPositions[ i ] = state.Positions[ i ];
					}
				}
			}
		}
	}

	public static void SetPinned( State state, int vertex, bool pinned )
	{
		if ( vertex < 0 || vertex >= state.Pinned.Length ) return;
		state.Pinned[ vertex ] = pinned;
	}

	public static void SelfCollisionPass( State state, float radius )
	{
		if ( state is null || radius <= 0f ) return;
		int n = state.Positions.Length;
		float cell = radius * 2f;
		if ( cell <= 0f ) return;
		var grid = new Dictionary<(int,int,int), List<int>>();
		for ( int i = 0; i < n; i++ )
		{
			var p = state.Positions[ i ];
			int cx = (int)MathF.Floor( p.x / cell );
			int cy = (int)MathF.Floor( p.y / cell );
			int cz = (int)MathF.Floor( p.z / cell );
			var key = ( cx, cy, cz );
			if ( !grid.TryGetValue( key, out var list ) ) { list = new List<int>(); grid[ key ] = list; }
			list.Add( i );
		}
		float r2 = ( 2f * radius ) * ( 2f * radius );
		for ( int i = 0; i < n; i++ )
		{
			if ( state.Pinned[ i ] ) continue;
			var pi = state.Positions[ i ];
			int cx = (int)MathF.Floor( pi.x / cell );
			int cy = (int)MathF.Floor( pi.y / cell );
			int cz = (int)MathF.Floor( pi.z / cell );
			for ( int dx = -1; dx <= 1; dx++ )
			for ( int dy = -1; dy <= 1; dy++ )
			for ( int dz = -1; dz <= 1; dz++ )
			{
				if ( !grid.TryGetValue( ( cx + dx, cy + dy, cz + dz ), out var list ) ) continue;
				foreach ( int j in list )
				{
					if ( j <= i ) continue;
					if ( state.Pinned[ j ] ) continue;
					var d = state.Positions[ j ] - pi;
					float dist2 = d.x*d.x + d.y*d.y + d.z*d.z;
					if ( dist2 < r2 && dist2 > 1e-12f )
					{
						float dist = MathF.Sqrt( dist2 );
						float push = ( 2f * radius - dist ) * 0.5f;
						var nrm = d * ( 1f / dist );
						state.Positions[ i ] -= nrm * push;
						state.Positions[ j ] += nrm * push;
					}
				}
			}
		}
	}

	public static void ApplyImpulse( State state, int vertex, Vec3 impulse )
	{
		if ( vertex < 0 || vertex >= state.Positions.Length ) return;
		if ( state.Pinned[ vertex ] ) return;
		state.PrevPositions[ vertex ] -= impulse;
	}

	public static void Reset( State state, PolyMesh source )
	{
		for ( int i = 0; i < state.Positions.Length && i < source.Positions.Count; i++ )
		{
			state.Positions[ i ] = source.Positions[ i ];
			state.PrevPositions[ i ] = source.Positions[ i ];
		}
	}

	public static PolyMesh ToMesh( State state, PolyMesh source )
	{
		var m = new PolyMesh();
		m.Positions.AddRange( state.Positions );
		if ( source.Skin != null )
		{
			var w = new SkinWeights( source.Skin.Count );
			for ( int i = 0; i < source.Skin.Count; i++ ) w[ i ] = source.Skin[ i ];
			m.Skin = w;
		}
		foreach ( var f in source.Faces ) m.Faces.Add( new Face( (int[])f.Indices.Clone(), (Vec2[])f.UVs.Clone(), f.Material ) );
		return m;
	}
}