using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Effigy;

/// <summary>Geometry Nodes evaluation on top of the promoted core in <see cref="GeoNodesEngine"/>.
/// This file is the node library: generators, curve/instancing nodes, mesh operations and the
/// spatial fields the roadmap's Lane D (v4) lists. A node is a pure function of its inputs —
/// it never mutates the geometry it is given, matching the core's immutability contract.
///
/// Two graph spellings are supported, both evaluated by <see cref="GeoGraph"/>:
///   * JSON: {"nodes":[{"id":"g","type":"Grid","size_x":4}, {"id":"p","type":"DistributePointsOnFaces","geometry":"g","density":2}]}
///     Branching: a node's "geometry" property names an earlier node's id; "inputs" maps a
///     named input (prototype, profile, target, geometries) to a node id.
///   * Pipe DSL (linear chains only): "Grid size_x=4 | DistributePointsOnFaces density=2 | InstanceOnPoints prototype=..."
///     Each stage's geometry input is the previous stage's output. Field values in the DSL are
///     written field:Name,arg=value,arg=value (see <see cref="GeoFields"/>).
/// A node property that is a JSON object with a "field" key, or a DSL field: value, becomes a
/// <see cref="GeoField"/>; plain numbers and "x,y,z" strings become constant fields.</summary>
public sealed class GeoNodeCall
{
	public string Type;
	public Dictionary<string, object> Props = new( StringComparer.OrdinalIgnoreCase );
	public string GeometryId;
	public Dictionary<string, string> Inputs = new( StringComparer.OrdinalIgnoreCase );

	public bool Has( string key ) => Props.ContainsKey( key );

	public object Raw( string key, object fallback = null ) => Props.TryGetValue( key, out var v ) ? v : fallback;

	public float F( string key, float fallback )
	{
		if ( !Props.TryGetValue( key, out var v ) || v is null ) return fallback;
		if ( v is GeoField ) throw new GeoNodeFailure( $"property '{key}' is a field, not a number" );
		return GeoValues.ToFloat( v );
	}

	public int I( string key, int fallback ) => (int)MathF.Round( F( key, fallback ) );

	public bool B( string key, bool fallback )
	{
		if ( !Props.TryGetValue( key, out var v ) || v is null ) return fallback;
		return v is bool b ? b : GeoValues.ToFloat( v ) != 0f;
	}

	public string S( string key, string fallback ) => Props.TryGetValue( key, out var v ) && v is not null ? v.ToString() : fallback;

	public Vec3 V( string key, Vec3 fallback )
	{
		if ( !Props.TryGetValue( key, out var v ) || v is null ) return fallback;
		if ( v is Vec3 x ) return x;
		if ( v is string s ) return ParseVec3( s, fallback );
		return new Vec3( GeoValues.ToFloat( v ), GeoValues.ToFloat( v ), GeoValues.ToFloat( v ) );
	}

	public static Vec3 ParseVec3( string s, Vec3 fallback )
	{
		var parts = (s ?? "").Split( ',' ).Select( p => p.Trim() ).ToArray();
		if ( parts.Length == 3
			&& float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
			&& float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
			&& float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
			return new Vec3( x, y, z );
		return fallback;
	}

	public static float ParseFloat( string s, float fallback ) =>
		float.TryParse( (s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v ) ? v : fallback;
}

/// <summary>What a node runs against: the named geometry sets already evaluated, plus the
/// geometry flowing into the node.</summary>
public sealed class GeoEvalContext
{
	public IReadOnlyDictionary<string, GeometrySet> Named;
	public GeometrySet Input;

	public GeometrySet Geometry( GeoNodeCall call )
	{
		if ( call.GeometryId is not null )
		{
			if ( Named.TryGetValue( call.GeometryId, out var g ) ) return g;
			throw new GeoGraphException( new GeoNodeError( GeoErrorCode.MissingInput, call.Type, "geometry", $"unknown geometry '{call.GeometryId}'" ) );
		}
		return Input ?? GeometrySet.Empty;
	}

	public GeometrySet NamedInput( GeoNodeCall call, string name )
	{
		if ( call.Inputs.TryGetValue( name, out var id ) && Named.TryGetValue( id, out var g ) ) return g;
		return null;
	}
}

/// <summary>Field construction from node properties. A field value is one of: a <see cref="GeoField"/>
/// already, a JSON object {"field":"NoiseField","scale":2}, or a DSL string "field:NoiseField,scale=2".
/// Anything else becomes a constant field of the requested type.</summary>
public static class GeoFields
{
	public static GeoField From( object value, GeoSocketType type, GeoEvalContext ctx )
	{
		if ( value is GeoField f ) return f.As( type );
		if ( value is Dictionary<string, object> dict && dict.TryGetValue( "field", out var name ) )
			return Build( name.ToString(), dict, ctx ).As( type );
		if ( value is string s && s.StartsWith( "field:", StringComparison.OrdinalIgnoreCase ) )
			return BuildDsl( s[6..], ctx ).As( type );
		if ( value is string text )
		{
			// A plain "x,y,z" string is a vector constant, a plain number is a scalar one.
			// Without this, a constant vector offset would reach ToFloat and throw.
			if ( type == GeoSocketType.Vector )
			{
				var parts = text.Split( ',' ).Select( p => p.Trim() ).ToArray();
				if ( parts.Length == 3
					&& float.TryParse( parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var vx )
					&& float.TryParse( parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var vy )
					&& float.TryParse( parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vz ) )
					return GeoField.Constant( new Vec3( vx, vy, vz ), type );
			}
			if ( float.TryParse( text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fv ) )
				return GeoField.Constant( fv, type );
		}
		return GeoField.Constant( value ?? 0f, type );
	}

	public static GeoField FromProp( GeoNodeCall call, string key, GeoSocketType type, GeoEvalContext ctx, object fallback )
		=> From( call.Raw( key, fallback ), type, ctx );

	static GeoField BuildDsl( string spec, GeoEvalContext ctx )
	{
		var parts = spec.Split( ',' ).Select( p => p.Trim() ).Where( p => p.Length > 0 ).ToArray();
		if ( parts.Length == 0 ) throw new GeoNodeFailure( "empty field specification" );
		var dict = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
		foreach ( var p in parts.Skip( 1 ) )
		{
			var kv = p.Split( '=', 2 );
			dict[kv[0].Trim()] = kv.Length > 1 ? (object)kv[1].Trim() : true;
		}
		return Build( parts[0], dict, ctx );
	}

	static float Arg( IReadOnlyDictionary<string, object> d, string key, float fallback )
	{
		if ( !d.TryGetValue( key, out var v ) || v is null ) return fallback;
		return v is string s ? GeoNodeCall.ParseFloat( s, fallback ) : GeoValues.ToFloat( v );
	}

	static string ArgS( IReadOnlyDictionary<string, object> d, string key, string fallback )
		=> d.TryGetValue( key, out var v ) && v is not null ? v.ToString() : fallback;

	static GeoField ArgField( IReadOnlyDictionary<string, object> d, string key, GeoEvalContext ctx, GeoSocketType type, float fallback )
		=> d.TryGetValue( key, out var v ) ? From( v, type, ctx ) : GeoField.Constant( fallback, type );

	static GeoField Build( string name, IReadOnlyDictionary<string, object> d, GeoEvalContext ctx )
	{
		switch ( name )
		{
			case "PositionField":
				return new GeoFuncField( GeoSocketType.Vector, c => new GeoArray( c.Positions() ) );
			case "NormalField":
				return new GeoFuncField( GeoSocketType.Vector, c => new GeoArray( c.Normals() ) );
			case "IndexField":
				return new GeoFuncField( GeoSocketType.Float, c => new GeoArray( GeoSocketType.Float, c.Index() ) );
			case "SplineParameterField":
				return new GeoFuncField( GeoSocketType.Float, c =>
				{
					var f = new float[c.Count];
					if ( c.Curves is not null )
					{
						var i = 0;
						foreach ( var sp in c.Curves.Splines )
						{
							var n = sp.Points.Length;
							for ( var k = 0; k < n; k++ ) f[i++] = n > 1 ? k / (float)(n - 1) : 0f;
						}
					}
					else for ( var i = 0; i < f.Length; i++ ) f[i] = c.Count > 1 ? i / (float)(c.Count - 1) : 0f;
					return new GeoArray( GeoSocketType.Float, f );
				} );
			case "NoiseField":
				{
					var scale = Arg( d, "scale", 1f );
					var detail = Math.Max( 1, (int)MathF.Round( Arg( d, "detail", 3f ) ) );
					var seed = (uint)Math.Max( 0, MathF.Round( Arg( d, "seed", 0f ) ) );
					return new GeoFuncField( GeoSocketType.Float, c =>
					{
						var pos = c.Positions();
						var f = new float[pos.Length];
						for ( var i = 0; i < f.Length; i++ ) f[i] = Noise.Fbm( pos[i] * scale, detail, seed );
						return new GeoArray( GeoSocketType.Float, f );
					} );
				}
			case "VoronoiField":
				{
					var scale = Arg( d, "scale", 1f );
					var mode = ArgS( d, "mode", "f1" );
					if ( mode == "color" )
						return new GeoFuncField( GeoSocketType.Vector, c =>
						{
							var pos = c.Positions();
							var v = new Vec3[pos.Length];
							for ( var i = 0; i < v.Length; i++ ) v[i] = Noise.VoronoiColor( pos[i] * scale );
							return new GeoArray( v );
						} );
					return new GeoFuncField( GeoSocketType.Float, c =>
					{
						var pos = c.Positions();
						var f = new float[pos.Length];
						for ( var i = 0; i < f.Length; i++ ) f[i] = Noise.VoronoiF1( pos[i] * scale );
						return new GeoArray( GeoSocketType.Float, f );
					} );
				}
			case "ProximityField":
				{
					// Target arrives through the node's "target" named input: GeoNodes sets
					// CurrentTarget before running each node, so a field built during that
					// node's execution sees it. Built outside a node run this throws, honestly.
					var t = GeoNodes.CurrentTarget;
					if ( t?.Mesh is null ) throw new GeoNodeFailure( "ProximityField needs a target geometry" );
					var pts = t.Mesh.Poly.Positions;
					return new GeoFuncField( GeoSocketType.Float, c =>
					{
						var pos = c.Positions();
						var f = new float[pos.Length];
						for ( var i = 0; i < f.Length; i++ )
						{
							var best = float.MaxValue;
							foreach ( var p in pts ) { var dd = (p - pos[i]).LengthSquared; if ( dd < best ) best = dd; }
							f[i] = MathF.Sqrt( best );
						}
						return new GeoArray( GeoSocketType.Float, f );
					} );
				}
			case "RaycastField":
				{
					var t = GeoNodes.CurrentTarget;
					if ( t?.Mesh is null ) throw new GeoNodeFailure( "RaycastField needs a target geometry" );
					var dir = d.TryGetValue( "direction", out var dv ) ? GeoNodeCall.ParseVec3( dv.ToString(), new Vec3( 0, 0, -1 ) ) : new Vec3( 0, 0, -1 );
					var tris = t.Mesh.Poly.Faces.SelectMany( f => TriangulateFace( t.Mesh.Poly, f ) ).ToArray();
					return new GeoFuncField( GeoSocketType.Float, c =>
					{
						var pos = c.Positions();
						var f = new float[pos.Length];
						for ( var i = 0; i < f.Length; i++ ) f[i] = Noise.Raycast( pos[i], dir, tris );
						return new GeoArray( GeoSocketType.Float, f );
					} );
				}
			case "MathField":
				{
					var op = ArgS( d, "op", "add" );
					var a = ArgField( d, "a", ctx, GeoSocketType.Float, 0f );
					var b = ArgField( d, "b", ctx, GeoSocketType.Float, 0f );
					return new GeoFuncField( GeoSocketType.Float, c =>
					{
						var av = a.Evaluate( c );
						var bv = b.Evaluate( c );
						var f = new float[c.Count];
						for ( var i = 0; i < f.Length; i++ )
						{
							var x = av.Float( i ); var y = bv.Float( i );
							f[i] = op switch
							{
								"add" => x + y,
								"subtract" => x - y,
								"multiply" => x * y,
								"divide" => y != 0f ? x / y : 0f,
								"power" => MathF.Pow( x, y ),
								"min" => MathF.Min( x, y ),
								"maximum" => MathF.Max( x, y ),
								"sin" => MathF.Sin( x ),
								"cos" => MathF.Cos( x ),
								"abs" => MathF.Abs( x ),
								"sqrt" => MathF.Sqrt( MathF.Max( 0f, x ) ),
								_ => throw new GeoNodeFailure( $"unknown MathField op '{op}'" ),
							};
						}
						return new GeoArray( GeoSocketType.Float, f );
					} );
				}
			case "ColorRampField":
				{
					// Source field comes from the "input" key, not "field" (which names this
					// field's own type in the JSON object form). Default is a constant 0.
					var src = ArgField( d, "input", ctx, GeoSocketType.Float, 0f );
					var stops = ParseStops( ArgS( d, "stops", "0:0,0,0;1:1,1,1" ) );
					return new GeoFuncField( GeoSocketType.Vector, c =>
					{
						var sv = src.Evaluate( c );
						var v = new Vec3[c.Count];
						for ( var i = 0; i < v.Length; i++ ) v[i] = Ramp( stops, sv.Float( i ) );
						return new GeoArray( v );
					} );
				}
			default:
				throw new GeoNodeFailure( $"unknown field '{name}'" );
		}
	}

	static (float T, Vec3 C)[] ParseStops( string s )
	{
		var list = new List<(float, Vec3)>();
		foreach ( var part in s.Split( ';' ) )
		{
			var kv = part.Split( ':', 2 );
			if ( kv.Length != 2 ) continue;
			list.Add( (GeoNodeCall.ParseFloat( kv[0], 0f ), GeoNodeCall.ParseVec3( kv[1], Vec3.Zero )) );
		}
		list.Sort( ( a, b ) => a.Item1.CompareTo( b.Item1 ) );
		return list.Count > 0 ? list.ToArray() : new[] { (0f, Vec3.Zero), (1f, Vec3.One) };
	}

	static Vec3 Ramp( (float T, Vec3 C)[] stops, float t )
	{
		if ( t <= stops[0].T ) return stops[0].C;
		if ( t >= stops[^1].T ) return stops[^1].C;
		for ( var i = 1; i < stops.Length; i++ )
			if ( t <= stops[i].T )
			{
				var u = (t - stops[i - 1].T) / (stops[i].T - stops[i - 1].T);
				return Vec3.Lerp( stops[i - 1].C, stops[i].C, u );
			}
		return stops[^1].C;
	}

	internal static (Vec3 A, Vec3 B, Vec3 C)[] TriangulateFace( PolyMesh poly, Face f )
	{
		var list = new List<(Vec3, Vec3, Vec3)>();
		for ( var i = 1; i + 1 < f.Indices.Length; i++ )
			list.Add( (poly.Positions[f.Indices[0]], poly.Positions[f.Indices[i]], poly.Positions[f.Indices[i + 1]]) );
		return list.ToArray();
	}
}

/// <summary>Deterministic spatial noise. Value noise over a hashed lattice, fractal-summed;
/// Voronoi F1 over hashed feature points; a Möller–Trumbore raycast. All of it seeded, so the
/// same graph always builds the same geometry — a procedural node that re-rolls on every
/// rebuild would be unusable in a parametric tree.</summary>
public static class Noise
{
	static uint Hash3( int x, int y, int z, uint seed )
	{
		uint h = seed ^ (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791);
		h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
		return h;
	}

	static float Unit3( int x, int y, int z, uint seed ) => (Hash3( x, y, z, seed ) >> 8) * (1f / 16777216f);

	static float Smooth( float t ) => t * t * (3f - 2f * t);

	public static float Value( Vec3 p, uint seed )
	{
		var x0 = (int)MathF.Floor( p.x ); var y0 = (int)MathF.Floor( p.y ); var z0 = (int)MathF.Floor( p.z );
		var fx = p.x - x0; var fy = p.y - y0; var fz = p.z - z0;
		float L( int dx, int dy, int dz ) => Unit3( x0 + dx, y0 + dy, z0 + dz, seed );
		var c000 = L( 0, 0, 0 ); var c100 = L( 1, 0, 0 ); var c010 = L( 0, 1, 0 ); var c110 = L( 1, 1, 0 );
		var c001 = L( 0, 0, 1 ); var c101 = L( 1, 0, 1 ); var c011 = L( 0, 1, 1 ); var c111 = L( 1, 1, 1 );
		var u = Smooth( fx ); var v = Smooth( fy ); var w = Smooth( fz );
		float Mix( float a, float b, float t ) => a + (b - a) * t;
		return Mix( Mix( Mix( c000, c100, u ), Mix( c010, c110, u ), v ),
			Mix( Mix( c001, c101, u ), Mix( c011, c111, u ), v ), w ) * 2f - 1f;
	}

	public static float Fbm( Vec3 p, int octaves, uint seed )
	{
		var sum = 0f; var amp = 1f; var total = 0f; var q = p;
		for ( var o = 0; o < octaves; o++ )
		{
			sum += Value( q, seed + (uint)o * 101u ) * amp;
			total += amp; amp *= 0.5f; q *= 2.03f;
		}
		return total > 0f ? sum / total : 0f;
	}

	static Vec3 CellPoint( int x, int y, int z, uint seed ) =>
		new( x + Unit3( x, y, z, seed ), y + Unit3( x, y, z, seed + 7919u ), z + Unit3( x, y, z, seed + 104729u ) );

	public static float VoronoiF1( Vec3 p )
	{
		var cx = (int)MathF.Floor( p.x ); var cy = (int)MathF.Floor( p.y ); var cz = (int)MathF.Floor( p.z );
		var best = float.MaxValue;
		for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
				for ( var dz = -1; dz <= 1; dz++ )
				{
					var cp = CellPoint( cx + dx, cy + dy, cz + dz, 0u );
					var d = (cp - p).Length;
					if ( d < best ) best = d;
				}
		return best;
	}

	public static Vec3 VoronoiColor( Vec3 p )
	{
		var cx = (int)MathF.Floor( p.x ); var cy = (int)MathF.Floor( p.y ); var cz = (int)MathF.Floor( p.z );
		var best = float.MaxValue; var bx = cx; var by = cy; var bz = cz;
		for ( var dx = -1; dx <= 1; dx++ )
			for ( var dy = -1; dy <= 1; dy++ )
				for ( var dz = -1; dz <= 1; dz++ )
				{
					var cp = CellPoint( cx + dx, cy + dy, cz + dz, 0u );
					var d = (cp - p).Length;
					if ( d < best ) { best = d; bx = cx + dx; by = cy + dy; bz = cz + dz; }
				}
		return new Vec3( Unit3( bx, by, bz, 0u ), Unit3( bx, by, bz, 12345u ), Unit3( bx, by, bz, 67890u ) );
	}

	/// <summary>Closest hit distance along dir, or -1 on a miss.</summary>
	public static float Raycast( Vec3 origin, Vec3 dir, (Vec3 A, Vec3 B, Vec3 C)[] tris )
	{
		var d = dir.LengthSquared > 1e-12f ? dir.Normal : new Vec3( 0, 0, -1 );
		var best = float.MaxValue;
		foreach ( var (a, b, c) in tris )
		{
			var e1 = b - a; var e2 = c - a;
			var pv = Vec3.Cross( d, e2 );
			var det = Vec3.Dot( e1, pv );
			if ( MathF.Abs( det ) < 1e-8f ) continue;
			var inv = 1f / det;
			var tv = origin - a;
			var u = Vec3.Dot( tv, pv ) * inv;
			if ( u < 0f || u > 1f ) continue;
			var qv = Vec3.Cross( tv, e1 );
			var v = Vec3.Dot( d, qv ) * inv;
			if ( v < 0f || u + v > 1f ) continue;
			var t = Vec3.Dot( e2, qv ) * inv;
			if ( t > 1e-6f && t < best ) best = t;
		}
		return best == float.MaxValue ? -1f : best;
	}
}

/// <summary>Mesh construction helpers shared by the node implementations. Every builder
/// returns a fresh <see cref="PolyMesh"/>; nothing here mutates an input mesh.</summary>
public static class GeoMeshBuild
{
	public static PolyMesh Quad( Vec3 a, Vec3 b, Vec3 c, Vec3 d, int material = 0 ) =>
		QuadUv( a, b, c, d, new Vec2( 0, 0 ), new Vec2( 1, 0 ), new Vec2( 1, 1 ), new Vec2( 0, 1 ), material );

	public static PolyMesh QuadUv( Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec2 ua, Vec2 ub, Vec2 uc, Vec2 ud, int material = 0 )
	{
		var m = new PolyMesh();
		var ia = m.AddVertex( a ); var ib = m.AddVertex( b ); var ic = m.AddVertex( c ); var id = m.AddVertex( d );
		m.AddFace( new[] { ia, ib, ic, id }, new[] { ua, ub, uc, ud }, material );
		return m;
	}

	public static void Append( PolyMesh dst, PolyMesh src )
	{
		var map = new int[src.Positions.Count];
		for ( var i = 0; i < map.Length; i++ ) map[i] = dst.AddVertex( src.Positions[i] );
		foreach ( var f in src.Faces )
			dst.AddFace( f.Indices.Select( i => map[i] ).ToArray(), f.UVs, f.Material );
	}

	public static GeoMesh FromPoly( PolyMesh poly ) => new( poly );

	public static PolyMesh CloneWithPositions( PolyMesh src, Vec3[] positions )
	{
		var m = new PolyMesh();
		foreach ( var p in positions ) m.AddVertex( p );
		foreach ( var f in src.Faces ) m.AddFace( (int[])f.Indices.Clone(), f.UVs, f.Material );
		return m;
	}

	public static void QuadStrip( PolyMesh m, IReadOnlyList<int> ringA, IReadOnlyList<int> ringB, bool closed, int material = 0 )
	{
		var n = ringA.Count;
		var steps = closed ? n : n - 1;
		for ( var i = 0; i < steps; i++ )
		{
			var j = (i + 1) % n;
			m.AddFace( new[] { ringA[i], ringB[i], ringB[j], ringA[j] }, null, material );
		}
	}
}

/// <summary>The node implementations: generators, curves/instancing, mesh operations.
/// Each method takes the node's call and its evaluation context and returns a new
/// <see cref="GeometrySet"/>; generators ignore the flowing input, the rest transform it.</summary>
public static class GeoNodeImpl
{
	static GeometrySet Mesh( PolyMesh poly ) => GeometrySet.FromMesh( poly );

	static GeoMesh NeedMesh( GeoEvalContext ctx, GeoNodeCall call )
	{
		var g = ctx.Geometry( call );
		if ( g.Mesh is null ) throw new GeoNodeFailure( $"node '{call.Type}' needs a mesh input" );
		return g.Mesh;
	}

	static GeoCurves NeedCurves( GeoEvalContext ctx, GeoNodeCall call )
	{
		var g = ctx.Geometry( call );
		if ( g.Curves is null ) throw new GeoNodeFailure( $"node '{call.Type}' needs a curves input" );
		return g.Curves;
	}

	// --- generators ---------------------------------------------------------------------------

	public static GeometrySet Grid( GeoNodeCall c, GeoEvalContext ctx )
	{
		var sx = c.F( "size_x", 2f ); var sy = c.F( "size_y", 2f );
		var nx = Math.Max( 2, c.I( "vertices_x", 3 ) ); var ny = Math.Max( 2, c.I( "vertices_y", 3 ) );
		var m = new PolyMesh();
		var idx = new int[nx, ny];
		for ( var j = 0; j < ny; j++ )
			for ( var i = 0; i < nx; i++ )
			{
				var u = nx > 1 ? i / (float)(nx - 1) : 0f;
				var v = ny > 1 ? j / (float)(ny - 1) : 0f;
				idx[i, j] = m.AddVertex( new Vec3( (u - 0.5f) * sx, (v - 0.5f) * sy, 0f ) );
			}
		for ( var j = 0; j + 1 < ny; j++ )
			for ( var i = 0; i + 1 < nx; i++ )
				m.AddFace( new[] { idx[i, j], idx[i + 1, j], idx[i + 1, j + 1], idx[i, j + 1] },
					new[] { new Vec2( i / (float)(nx - 1), j / (float)(ny - 1) ), new Vec2( (i + 1) / (float)(nx - 1), j / (float)(ny - 1) ), new Vec2( (i + 1) / (float)(nx - 1), (j + 1) / (float)(ny - 1) ), new Vec2( i / (float)(nx - 1), (j + 1) / (float)(ny - 1) ) } );
		return Mesh( m );
	}

	public static GeometrySet UVSphere( GeoNodeCall c, GeoEvalContext ctx )
	{
		var r = c.F( "radius", 1f ); var seg = Math.Max( 3, c.I( "segments", 16 ) ); var rings = Math.Max( 2, c.I( "rings", 8 ) );
		var m = new PolyMesh();
		var top = m.AddVertex( new Vec3( 0, 0, r ) );
		var bottom = m.AddVertex( new Vec3( 0, 0, -r ) );
		var ring = new int[rings, seg];
		for ( var j = 0; j < rings; j++ )
		{
			var phi = MathF.PI * (j + 1) / (rings + 1);
			for ( var i = 0; i < seg; i++ )
			{
				var th = 2f * MathF.PI * i / seg;
				ring[j, i] = m.AddVertex( new Vec3( r * MathF.Sin( phi ) * MathF.Cos( th ), r * MathF.Sin( phi ) * MathF.Sin( th ), r * MathF.Cos( phi ) ) );
			}
		}
		for ( var i = 0; i < seg; i++ ) m.AddFace( new[] { top, ring[0, (i + 1) % seg], ring[0, i] } );
		for ( var j = 0; j + 1 < rings; j++ )
			for ( var i = 0; i < seg; i++ )
				m.AddFace( new[] { ring[j, i], ring[j, (i + 1) % seg], ring[j + 1, (i + 1) % seg], ring[j + 1, i] } );
		for ( var i = 0; i < seg; i++ ) m.AddFace( new[] { bottom, ring[rings - 1, i], ring[rings - 1, (i + 1) % seg] } );
		return Mesh( m );
	}

	public static GeometrySet IcoSphere( GeoNodeCall c, GeoEvalContext ctx )
	{
		var r = c.F( "radius", 1f ); var levels = Math.Max( 0, c.I( "subdivisions", 1 ) );
		var t = (1f + MathF.Sqrt( 5f )) / 2f;
		var verts = new List<Vec3>
		{
			new(-1,t,0), new(1,t,0), new(-1,-t,0), new(1,-t,0),
			new(0,-1,t), new(0,1,t), new(0,-1,-t), new(0,1,-t),
			new(t,0,-1), new(t,0,1), new(-t,0,-1), new(-t,0,1),
		};
		for ( var i = 0; i < verts.Count; i++ ) verts[i] = verts[i].Normal * r;
		var faces = new List<int[]>
		{
			new[]{0,11,5}, new[]{0,5,1}, new[]{0,1,7}, new[]{0,7,10}, new[]{0,10,11},
			new[]{1,5,9}, new[]{5,11,4}, new[]{11,10,2}, new[]{10,7,6}, new[]{7,1,8},
			new[]{3,9,4}, new[]{3,4,2}, new[]{3,2,6}, new[]{3,6,8}, new[]{3,8,9},
			new[]{4,9,5}, new[]{2,4,11}, new[]{6,2,10}, new[]{8,6,7}, new[]{9,8,1},
		};
		for ( var level = 0; level < levels; level++ )
		{
			var mid = new Dictionary<(int, int), int>();
			var next = new List<int[]>();
			foreach ( var f in faces )
			{
				int M( int a, int b )
				{
					var k = a < b ? (a, b) : (b, a);
					if ( mid.TryGetValue( k, out var v ) ) return v;
					var idx = verts.Count;
					verts.Add( ((verts[a] + verts[b]) * 0.5f).Normal * r );
					mid[k] = idx;
					return idx;
				}
				var ab = M( f[0], f[1] ); var bc = M( f[1], f[2] ); var ca = M( f[2], f[0] );
				next.Add( new[] { f[0], ab, ca } ); next.Add( new[] { f[1], bc, ab } );
				next.Add( new[] { f[2], ca, bc } ); next.Add( new[] { ab, bc, ca } );
			}
			faces = next;
		}
		var m = new PolyMesh();
		foreach ( var v in verts ) m.AddVertex( v );
		foreach ( var f in faces ) m.AddFace( f );
		return Mesh( m );
	}

	public static GeometrySet Cylinder( GeoNodeCall c, GeoEvalContext ctx )
	{
		var r = c.F( "radius", 1f ); var depth = c.F( "depth", 2f ); var n = Math.Max( 3, c.I( "vertices", 16 ) );
		var fill = c.B( "fill_caps", true );
		var m = new PolyMesh();
		var bot = new int[n]; var top = new int[n];
		for ( var i = 0; i < n; i++ )
		{
			var th = 2f * MathF.PI * i / n;
			bot[i] = m.AddVertex( new Vec3( r * MathF.Cos( th ), r * MathF.Sin( th ), -depth / 2f ) );
			top[i] = m.AddVertex( new Vec3( r * MathF.Cos( th ), r * MathF.Sin( th ), depth / 2f ) );
		}
		for ( var i = 0; i < n; i++ )
		{
			var j = (i + 1) % n;
			m.AddFace( new[] { bot[i], bot[j], top[j], top[i] } );
		}
		if ( fill )
		{
			var bc = m.AddVertex( new Vec3( 0, 0, -depth / 2f ) );
			var tc = m.AddVertex( new Vec3( 0, 0, depth / 2f ) );
			for ( var i = 0; i < n; i++ )
			{
				var j = (i + 1) % n;
				m.AddFace( new[] { bc, bot[j], bot[i] } );
				m.AddFace( new[] { tc, top[i], top[j] } );
			}
		}
		return Mesh( m );
	}

	static GeometrySet CurvesOf( params GeoSpline[] splines ) => new( null, new GeoCurves( splines ) );

	public static GeometrySet CurveLine( GeoNodeCall c, GeoEvalContext ctx )
	{
		var a = c.V( "start", Vec3.Zero ); var b = c.V( "end", new Vec3( 0, 0, 1 ) );
		var count = Math.Max( 2, c.I( "points", 2 ) );
		var pts = new Vec3[count];
		for ( var i = 0; i < count; i++ ) pts[i] = Vec3.Lerp( a, b, count > 1 ? i / (float)(count - 1) : 0f );
		return CurvesOf( new GeoSpline { Points = pts } );
	}

	public static GeometrySet CurveCircle( GeoNodeCall c, GeoEvalContext ctx )
	{
		var r = c.F( "radius", 1f ); var n = Math.Max( 3, c.I( "segments", 16 ) );
		var pts = new Vec3[n];
		for ( var i = 0; i < n; i++ )
		{
			var th = 2f * MathF.PI * i / n;
			pts[i] = new Vec3( r * MathF.Cos( th ), r * MathF.Sin( th ), 0f );
		}
		return CurvesOf( new GeoSpline { Points = pts, Cyclic = true } );
	}

	public static GeometrySet CurveSpiral( GeoNodeCall c, GeoEvalContext ctx )
	{
		var r0 = c.F( "radius_start", 1f ); var r1 = c.F( "radius_end", 1f );
		var h = c.F( "height", 2f ); var turns = c.F( "turns", 2f ); var n = Math.Max( 2, c.I( "points", 32 ) );
		var pts = new Vec3[n];
		for ( var i = 0; i < n; i++ )
		{
			var t = n > 1 ? i / (float)(n - 1) : 0f;
			var th = 2f * MathF.PI * turns * t;
			var r = r0 + (r1 - r0) * t;
			pts[i] = new Vec3( r * MathF.Cos( th ), r * MathF.Sin( th ), h * t );
		}
		return CurvesOf( new GeoSpline { Points = pts } );
	}

	// --- curves & instancing --------------------------------------------------------------------

	static Vec3[] SamplePolyline( Vec3[] pts, bool cyclic, int count )
	{
		if ( count <= 0 ) return Array.Empty<Vec3>();
		if ( pts.Length == 0 ) return Array.Empty<Vec3>();
		if ( count == 1 ) return new[] { pts[0] };
		var segs = cyclic ? pts.Length : pts.Length - 1;
		var lens = new float[segs]; var total = 0f;
		for ( var s = 0; s < segs; s++ ) { lens[s] = (pts[(s + 1) % pts.Length] - pts[s]).Length; total += lens[s]; }
		var outPts = new Vec3[count];
		for ( var i = 0; i < count; i++ )
		{
			var d = total * i / (count - 1);
			var s = 0;
			while ( s < segs - 1 && d > lens[s] ) { d -= lens[s]; s++; }
			var a = pts[s]; var b = pts[(s + 1) % pts.Length];
			var u = lens[s] > 1e-9f ? d / lens[s] : 0f;
			outPts[i] = Vec3.Lerp( a, b, u );
		}
		return outPts;
	}

	public static GeometrySet ResampleCurve( GeoNodeCall c, GeoEvalContext ctx )
	{
		var curves = NeedCurves( ctx, c );
		var mode = c.S( "mode", "count" );
		var splines = new List<GeoSpline>();
		foreach ( var sp in curves.Splines )
		{
			var eval = sp.Evaluated();
			int count;
			if ( mode == "length" )
			{
				var spacing = Math.Max( 1e-4f, c.F( "length", 1f ) );
				var total = 0f;
				for ( var i = 0; i + 1 < eval.Length; i++ ) total += (eval[i + 1] - eval[i]).Length;
				count = Math.Max( 2, (int)MathF.Round( total / spacing ) + 1 );
			}
			else count = Math.Max( 2, c.I( "count", 16 ) );
			splines.Add( new GeoSpline { Points = SamplePolyline( eval, sp.Cyclic, count ), Cyclic = false } );
		}
		return new GeometrySet( null, new GeoCurves( splines ) );
	}

	public static GeometrySet TrimCurve( GeoNodeCall c, GeoEvalContext ctx )
	{
		var curves = NeedCurves( ctx, c );
		var start = Math.Clamp( c.F( "start", 0f ), 0f, 1f );
		var end = Math.Clamp( c.F( "end", 1f ), 0f, 1f );
		if ( end < start ) (start, end) = (end, start);
		var splines = new List<GeoSpline>();
		foreach ( var sp in curves.Splines )
		{
			var eval = sp.Evaluated();
			var dense = SamplePolyline( eval, sp.Cyclic, Math.Max( 2, eval.Length * 4 ) );
			var a = (int)MathF.Round( start * (dense.Length - 1) );
			var b = (int)MathF.Round( end * (dense.Length - 1) );
			var pts = new Vec3[Math.Max( 1, b - a + 1 )];
			Array.Copy( dense, a, pts, 0, pts.Length );
			splines.Add( new GeoSpline { Points = pts } );
		}
		return new GeometrySet( null, new GeoCurves( splines ) );
	}

	public static GeometrySet CurveToMesh( GeoNodeCall c, GeoEvalContext ctx )
	{
		var curves = NeedCurves( ctx, c );
		var profile = ctx.NamedInput( c, "profile" );
		var radiusField = GeoFields.FromProp( c, "radius", GeoSocketType.Float, ctx, 0.1f );
		var segs = Math.Max( 3, c.I( "profile_segments", 8 ) );
		// Cross-section in the XY plane: a circle by default, or the profile curve's own points.
		Vec2[] section;
		bool closedSection;
		if ( profile?.Curves is { Splines.Count: > 0 } )
		{
			var ev = profile.Curves.Splines[0].Evaluated();
			section = ev.Select( p => new Vec2( p.x, p.y ) ).ToArray();
			closedSection = profile.Curves.Splines[0].Cyclic;
		}
		else
		{
			section = new Vec2[segs];
			for ( var i = 0; i < segs; i++ )
			{
				var th = 2f * MathF.PI * i / segs;
				section[i] = new Vec2( MathF.Cos( th ), MathF.Sin( th ) );
			}
			closedSection = true;
		}
		var m = new PolyMesh();
		var curveCtx = GeoFieldContext.ForCurves( curves );
		var radius = radiusField.Evaluate( curveCtx );
		var controlIndex = 0;
		foreach ( var sp in curves.Splines )
		{
			var eval = sp.Evaluated();
			if ( eval.Length < 2 ) { controlIndex += sp.Points.Length; continue; }
			// Radius per evaluated sample: interpolate the control-point radius values.
			var ctrlR = new float[sp.Points.Length];
			for ( var i = 0; i < ctrlR.Length; i++ ) ctrlR[i] = radius.Float( controlIndex + i );
			controlIndex += sp.Points.Length;
			var rings = new List<int[]>();
			for ( var s = 0; s < eval.Length; s++ )
			{
				var tangent = s == 0 ? (eval[1] - eval[0]).Normal
					: s == eval.Length - 1 ? (eval[^1] - eval[^2]).Normal
					: (eval[s + 1] - eval[s - 1]).Normal;
				if ( tangent.LengthSquared < 1e-12f ) tangent = new Vec3( 0, 0, 1 );
				var helper = MathF.Abs( tangent.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 );
				var bx = Vec3.Cross( helper, tangent ).Normal;
				var by = Vec3.Cross( tangent, bx ).Normal;
				var tParam = eval.Length > 1 ? s / (float)(eval.Length - 1) : 0f;
				var ci = sp.Points.Length > 1 ? tParam * (sp.Points.Length - 1) : 0f;
				var i0 = (int)MathF.Floor( ci ); var frac = ci - i0;
				var rr = i0 + 1 < ctrlR.Length ? ctrlR[i0] * (1f - frac) + ctrlR[i0 + 1] * frac : ctrlR[^1];
				var ring = new int[section.Length];
				for ( var k = 0; k < section.Length; k++ )
					ring[k] = m.AddVertex( eval[s] + bx * (section[k].x * rr) + by * (section[k].y * rr) );
				rings.Add( ring );
			}
			for ( var s = 0; s + 1 < rings.Count; s++ )
				GeoMeshBuild.QuadStrip( m, rings[s], rings[s + 1], closedSection );
		}
		return Mesh( m );
	}

	static Xform BasisFromNormal( Vec3 normal )
	{
		var n = normal.LengthSquared > 1e-12f ? normal.Normal : new Vec3( 0, 0, 1 );
		var helper = MathF.Abs( n.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 );
		var x = Vec3.Cross( helper, n ).Normal;
		var y = Vec3.Cross( n, x ).Normal;
		return new Xform( x, y, n, Vec3.Zero );
	}

	public static GeometrySet DistributePointsOnFaces( GeoNodeCall c, GeoEvalContext ctx )
	{
		var mesh = NeedMesh( ctx, c );
		var mode = c.S( "mode", "random" );
		var seed = (uint)Math.Max( 0, c.I( "seed", 0 ) );
		var minDist = c.F( "min_distance", 0.5f );
		var faces = mesh.Poly.Faces;
		var areas = faces.Select( f => mesh.Poly.FaceArea( f ) ).ToArray();
		var totalArea = areas.Sum();
		int target;
		if ( c.Has( "count" ) ) target = Math.Max( 0, c.I( "count", 0 ) );
		else target = (int)MathF.Round( c.F( "density", 1f ) * totalArea );
		var points = new List<Vec3>(); var normals = new List<Vec3>();
		var rnd = new Random( (int)seed );
		if ( mode == "poisson" )
		{
			// Dart throwing per face, capped attempts: min-distance holds on the flat face,
			// which is the honest reading for a surface scatter (true geodesic Poisson is a
			// different, much larger algorithm and is not claimed here).
			var attempts = 0; var cap = Math.Max( 100, target * 60 );
			while ( points.Count < target && attempts < cap )
			{
				attempts++;
				var fi = PickFace( areas, totalArea, rnd );
				if ( fi < 0 ) break;
				var f = faces[fi];
				var (p, n) = RandomPointOnFace( mesh.Poly, f, rnd );
				if ( points.Any( q => (q - p).Length < minDist ) ) continue;
				points.Add( p ); normals.Add( n );
			}
		}
		else
		{
			for ( var i = 0; i < target; i++ )
			{
				var fi = PickFace( areas, totalArea, rnd );
				if ( fi < 0 ) break;
				var (p, n) = RandomPointOnFace( mesh.Poly, faces[fi], rnd );
				points.Add( p ); normals.Add( n );
			}
		}
		var poly = new PolyMesh();
		foreach ( var p in points ) poly.AddVertex( p );
		var attrs = GeoMesh.NewAttributes();
		if ( normals.Count > 0 ) attrs["normal"] = new GeoArray( normals.ToArray() );
		return new GeometrySet( new GeoMesh( poly, attrs ) );
	}

	static int PickFace( float[] areas, float total, Random rnd )
	{
		if ( total <= 0f || areas.Length == 0 ) return -1;
		var r = (float)rnd.NextDouble() * total;
		for ( var i = 0; i < areas.Length; i++ ) { r -= areas[i]; if ( r <= 0f ) return i; }
		return areas.Length - 1;
	}

	static (Vec3 P, Vec3 N) RandomPointOnFace( PolyMesh poly, Face f, Random rnd )
	{
		// Fan-triangulate the face, pick a triangle by area, then a barycentric point.
		var tris = new List<(int, int, int)>();
		for ( var i = 1; i + 1 < f.Indices.Length; i++ ) tris.Add( (f.Indices[0], f.Indices[i], f.Indices[i + 1]) );
		var areas = tris.Select( t => Vec3.Cross( poly.Positions[t.Item2] - poly.Positions[t.Item1], poly.Positions[t.Item3] - poly.Positions[t.Item1] ).Length * 0.5f ).ToArray();
		var total = areas.Sum();
		var r = total > 0f ? (float)rnd.NextDouble() * total : 0f;
		var ti = 0;
		for ( ; ti < areas.Length; ti++ ) { r -= areas[ti]; if ( r <= 0f ) break; }
		ti = Math.Min( ti, tris.Count - 1 );
		var (a, b, cc) = tris[ti];
		var u = (float)rnd.NextDouble(); var v = (float)rnd.NextDouble();
		if ( u + v > 1f ) { u = 1f - u; v = 1f - v; }
		var p = poly.Positions[a] + (poly.Positions[b] - poly.Positions[a]) * u + (poly.Positions[cc] - poly.Positions[a]) * v;
		return (p, poly.FaceNormal( f ));
	}

	public static GeometrySet InstanceOnPoints( GeoNodeCall c, GeoEvalContext ctx )
	{
		var points = ctx.Geometry( c );
		var prototypes = new List<GeometrySet>();
		if ( c.Inputs.TryGetValue( "prototypes", out var plist ) )
		{
			foreach ( var id in plist.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
				if ( ctx.Named.TryGetValue( id, out var g ) ) prototypes.Add( g );
		}
		var single = ctx.NamedInput( c, "prototype" );
		if ( single is not null ) prototypes.Add( single );
		if ( prototypes.Count == 0 ) throw new GeoNodeFailure( "InstanceOnPoints needs a prototype geometry (inputs.prototype)" );
		Vec3[] positions; Vec3[] pointNormals;
		if ( points.Instances is not null )
		{
			positions = points.Instances.Transforms.Select( t => t.Origin ).ToArray();
			pointNormals = positions.Select( _ => new Vec3( 0, 0, 1 ) ).ToArray();
		}
		else if ( points.Mesh is not null )
		{
			positions = points.Mesh.Poly.Positions.ToArray();
			pointNormals = points.Mesh.PointAttributes.TryGetValue( "normal", out var na ) && na.V is not null
				? na.V : points.Mesh.PointNormals;
		}
		else throw new GeoNodeFailure( "InstanceOnPoints needs points (a mesh of points or instances)" );
		var n = positions.Length;
		var rotField = GeoFields.FromProp( c, "rotation", GeoSocketType.Vector, ctx, Vec3.Zero );
		var scaleField = GeoFields.FromProp( c, "scale", GeoSocketType.Vector, ctx, Vec3.One );
		var pickField = GeoFields.FromProp( c, "pick", GeoSocketType.Float, ctx, 0f );
		var instCtx = points.Instances is not null ? GeoFieldContext.ForInstances( points.Instances )
			: points.Mesh is not null ? GeoFieldContext.ForMesh( points.Mesh, GeoDomain.Point ) : GeoFieldContext.Single;
		var rots = rotField.Evaluate( instCtx ); var scales = scaleField.Evaluate( instCtx ); var picks = pickField.Evaluate( instCtx );
		var refs = new int[n]; var xforms = new Xform[n];
		for ( var i = 0; i < n; i++ )
		{
			refs[i] = prototypes.Count == 1 ? 0 : Math.Clamp( (int)MathF.Round( picks.Float( i ) ), 0, prototypes.Count - 1 );
			var basis = BasisFromNormal( pointNormals[i] );
			var e = rots.Vector( i );
			var euler = Xform.FromEulerXyz( e );
			var s = scales.Vector( i );
			var combined = new Xform( basis.X * s.x, basis.Y * s.y, basis.Z * s.z, positions[i] );
			// Apply the euler rotation in the point's local frame, then the basis+scale.
			xforms[i] = new Xform(
				combined.TransformDirection( euler.X ), combined.TransformDirection( euler.Y ), combined.TransformDirection( euler.Z ),
				combined.TransformPoint( Vec3.Zero ) );
			// combined.TransformPoint(Zero) is positions[i]; euler has no translation, so this
			// equals basis*scale*euler with origin at the point. (Kept explicit, not optimised away.)
		}
		var inst = new GeoInstances( prototypes.ToArray(), refs, xforms );
		return new GeometrySet( points.Mesh, points.Curves, inst );
	}

	public static GeometrySet RealizeInstances( GeoNodeCall c, GeoEvalContext ctx )
	{
		var g = ctx.Geometry( c );
		var outMesh = new PolyMesh();
		if ( g.Mesh is not null ) GeoMeshBuild.Append( outMesh, g.Mesh.Poly );
		if ( g.Instances is not null )
			for ( var i = 0; i < g.Instances.Count; i++ )
			{
				var proto = g.Instances.Prototypes[g.Instances.Reference[i]];
				if ( proto.Mesh is null ) continue;
				var t = g.Instances.Transforms[i];
				var moved = new PolyMesh();
				foreach ( var p in proto.Mesh.Poly.Positions ) moved.AddVertex( t.TransformPoint( p ) );
				foreach ( var f in proto.Mesh.Poly.Faces ) moved.AddFace( (int[])f.Indices.Clone(), f.UVs, f.Material );
				GeoMeshBuild.Append( outMesh, moved );
			}
		return Mesh( outMesh );
	}
}

/// <summary>Mesh-operation nodes (continued from <see cref="GeoNodeImpl"/>).</summary>
public static class GeoMeshOps
{
	static GeoMesh NeedMesh( GeoEvalContext ctx, GeoNodeCall call )
	{
		var g = ctx.Geometry( call );
		if ( g.Mesh is null ) throw new GeoNodeFailure( $"node '{call.Type}' needs a mesh input" );
		return g.Mesh;
	}

	static GeometrySet Mesh( PolyMesh poly ) => GeometrySet.FromMesh( poly );

	/// <summary>Offset a field into a per-point vector: a Vector field is used as-is; a scalar
	/// field offsets along the point normal. Selection (a Bool field) defaults to all points.</summary>
	static Vec3[] Offsets( GeoNodeCall call, GeoEvalContext ctx, GeoMesh mesh, string key, string selKey, GeoFieldContext fieldCtx, Vec3[] normals )
	{
		var field = GeoFields.FromProp( call, key, GeoSocketType.Vector, ctx, Vec3.Zero );
		// FromProp with Vector type converts a scalar constant into a broadcast vector, which
		// would offset diagonally instead of along the normal — so detect the scalar case first.
		// A "x,y,z" string is a vector, not a scalar: without that exclusion it reaches
		// ToFloat and throws "not a number" (caught by GeoNodesTests, fixed here).
		var raw = call.Raw( key, null );
		var isVectorString = raw is string vs
			&& vs.Split( ',' ).Select( p => p.Trim() ).ToArray() is { Length: 3 } vp
			&& float.TryParse( vp[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _ )
			&& float.TryParse( vp[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _ )
			&& float.TryParse( vp[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _ );
		var isScalar = !isVectorString && raw is not null && raw is not GeoField && raw is not Dictionary<string, object>
			&& !(raw is string s && s.StartsWith( "field:", StringComparison.OrdinalIgnoreCase ));
		Vec3[] off;
		if ( isScalar )
		{
			var sf = GeoFields.From( raw, GeoSocketType.Float, ctx );
			var sv = sf.Evaluate( fieldCtx );
			off = new Vec3[fieldCtx.Count];
			for ( var i = 0; i < off.Length; i++ ) off[i] = normals[i] * sv.Float( i );
		}
		else
		{
			var v = field.Evaluate( fieldCtx );
			off = new Vec3[fieldCtx.Count];
			for ( var i = 0; i < off.Length; i++ ) off[i] = v.Vector( i );
		}
		if ( call.Has( selKey ) )
		{
			var sel = GeoFields.FromProp( call, selKey, GeoSocketType.Bool, ctx, true );
			var sv = sel.Evaluate( fieldCtx );
			for ( var i = 0; i < off.Length; i++ ) if ( !sv.Bool( i ) ) off[i] = Vec3.Zero;
		}
		return off;
	}

	public static GeometrySet SetPosition( GeoNodeCall c, GeoEvalContext ctx )
	{
		var mesh = NeedMesh( ctx, c );
		var fieldCtx = GeoFieldContext.ForMesh( mesh, GeoDomain.Point );
		var normals = mesh.PointNormals;
		var off = Offsets( c, ctx, mesh, "offset", "selection", fieldCtx, normals );
		var pos = mesh.Poly.Positions.ToArray();
		for ( var i = 0; i < pos.Length; i++ ) pos[i] += off[i];
		var poly = GeoMeshBuild.CloneWithPositions( mesh.Poly, pos );
		return new GeometrySet( new GeoMesh( poly, GeoMesh.Copy( mesh.PointAttributes ), GeoMesh.Copy( mesh.FaceAttributes ) ) );
	}

	public static GeometrySet ExtrudeMesh( GeoNodeCall c, GeoEvalContext ctx )
	{
		var mesh = NeedMesh( ctx, c );
		var mode = c.S( "mode", "face" );
		var poly = mesh.Poly;
		var m = new PolyMesh();
		foreach ( var p in poly.Positions ) m.AddVertex( p );
		var pointCtx = GeoFieldContext.ForMesh( mesh, GeoDomain.Point );
		var normals = mesh.PointNormals;
		var off = Offsets( c, ctx, mesh, "offset", "selection", pointCtx, normals );
		// OffsetScale multiplies the offset per element (field on the element's domain).
		float ScaleAt( GeoFieldContext fc, int i, GeoField f ) => f.Evaluate( fc ).Float( i );
		if ( mode == "face" )
		{
			var faceCtx = GeoFieldContext.ForMesh( mesh, GeoDomain.Face );
			var scaleField = GeoFields.FromProp( c, "offset_scale", GeoSocketType.Float, ctx, 1f );
			var faceSel = c.Has( "selection" ) ? GeoFields.FromProp( c, "selection", GeoSocketType.Bool, ctx, true ).Evaluate( faceCtx ) : null;
			for ( var fi = 0; fi < poly.Faces.Count; fi++ )
			{
				var f = poly.Faces[fi];
				if ( faceSel is not null && !faceSel.Bool( fi ) ) { m.AddFace( (int[])f.Indices.Clone(), f.UVs, f.Material ); continue; }
				var sc = ScaleAt( faceCtx, fi, scaleField );
				var top = new int[f.Count];
				for ( var k = 0; k < f.Count; k++ ) top[k] = m.AddVertex( poly.Positions[f.Indices[k]] + off[f.Indices[k]] * sc );
				for ( var k = 0; k < f.Count; k++ )
				{
					var k2 = (k + 1) % f.Count;
					m.AddFace( new[] { f.Indices[k], f.Indices[k2], top[k2], top[k] }, null, f.Material );
				}
				m.AddFace( top, f.UVs, f.Material );
			}
		}
		else if ( mode == "edge" )
		{
			foreach ( var f in poly.Faces ) m.AddFace( (int[])f.Indices.Clone(), f.UVs, f.Material );
			var edgeCtx = GeoFieldContext.ForMesh( mesh, GeoDomain.Edge );
			var scaleField = GeoFields.FromProp( c, "offset_scale", GeoSocketType.Float, ctx, 1f );
			var edges = mesh.Edges;
			for ( var ei = 0; ei < edges.Length; ei++ )
			{
				var e = edges[ei];
				var sc = ScaleAt( edgeCtx, ei, scaleField );
				var a2 = m.AddVertex( poly.Positions[e.A] + off[e.A] * sc );
				var b2 = m.AddVertex( poly.Positions[e.B] + off[e.B] * sc );
				m.AddFace( new[] { e.A, e.B, b2, a2 } );
			}
		}
		else if ( mode == "point" )
		{
			// A point extrude has no face to grow: it adds the offset vertex and an edge
			// (a 2-corner face) from the original point to it, so the spike exists in the
			// mesh instead of being silently dropped. Downstream face code should treat
			// 2-corner faces as edges, not as surface.
			foreach ( var f in poly.Faces ) m.AddFace( (int[])f.Indices.Clone(), f.UVs, f.Material );
			for ( var i = 0; i < poly.Positions.Count; i++ )
			{
				if ( off[i].LengthSquared < 1e-12f ) continue;
				var j = m.AddVertex( poly.Positions[i] + off[i] );
				m.AddFace( new[] { i, j } );
			}
		}
		else throw new GeoNodeFailure( $"ExtrudeMesh mode '{mode}' is not face, edge or point" );
		return Mesh( m );
	}

	public static GeometrySet ScaleElements( GeoNodeCall c, GeoEvalContext ctx )
	{
		var mesh = NeedMesh( ctx, c );
		var domain = c.S( "domain", "face" );
		var poly = mesh.Poly;
		if ( domain == "point" )
		{
			var fieldCtx = GeoFieldContext.ForMesh( mesh, GeoDomain.Point );
			var scale = GeoFields.FromProp( c, "scale", GeoSocketType.Float, ctx, 1f ).Evaluate( fieldCtx );
			var centre = Vec3.Zero;
			foreach ( var p in poly.Positions ) centre += p;
			centre /= Math.Max( 1, poly.Positions.Count );
			var pos = poly.Positions.ToArray();
			for ( var i = 0; i < pos.Length; i++ ) pos[i] = centre + (pos[i] - centre) * scale.Float( i );
			return new GeometrySet( new GeoMesh( GeoMeshBuild.CloneWithPositions( poly, pos ), GeoMesh.Copy( mesh.PointAttributes ), GeoMesh.Copy( mesh.FaceAttributes ) ) );
		}
		// Face (and edge, via its two vertices) scaling duplicates vertices per element, so
		// elements scale about their own centroid without dragging their neighbours.
		var m = new PolyMesh();
		var faceCtx = GeoFieldContext.ForMesh( mesh, GeoDomain.Face );
		var fscale = GeoFields.FromProp( c, "scale", GeoSocketType.Float, ctx, 1f ).Evaluate( faceCtx );
		for ( var fi = 0; fi < poly.Faces.Count; fi++ )
		{
			var f = poly.Faces[fi];
			var centre = poly.FaceCentroid( f );
			var sc = fscale.Float( fi );
			var idx = new int[f.Count];
			for ( var k = 0; k < f.Count; k++ ) idx[k] = m.AddVertex( centre + (poly.Positions[f.Indices[k]] - centre) * sc );
			m.AddFace( idx, f.UVs, f.Material );
		}
		return Mesh( m );
	}

	public static GeometrySet SubdivideMesh( GeoNodeCall c, GeoEvalContext ctx )
	{
		var mesh = NeedMesh( ctx, c );
		var levels = Math.Max( 0, c.I( "levels", 1 ) );
		var poly = mesh.Poly;
		for ( var level = 0; level < levels; level++ ) poly = SubdivideOnce( poly );
		return Mesh( poly );
	}

	static PolyMesh SubdivideOnce( PolyMesh src )
	{
		// Midpoint subdivision: every n-gon becomes n quads (centroid + edge midpoints).
		// Original vertices keep their positions, so this refines without smoothing —
		// it is not Catmull-Clark, and does not claim to be.
		var m = new PolyMesh();
		var vertMap = new int[src.Positions.Count];
		for ( var i = 0; i < vertMap.Length; i++ ) vertMap[i] = m.AddVertex( src.Positions[i] );
		var edgeMid = new Dictionary<EdgeKey, int>();
		int Mid( int a, int b )
		{
			var k = new EdgeKey( a, b );
			if ( edgeMid.TryGetValue( k, out var v ) ) return v;
			var idx = m.AddVertex( (src.Positions[a] + src.Positions[b]) * 0.5f );
			edgeMid[k] = idx;
			return idx;
		}
		foreach ( var f in src.Faces )
		{
			var centre = m.AddVertex( src.FaceCentroid( f ) );
			var n = f.Count;
			var mids = new int[n];
			for ( var k = 0; k < n; k++ ) mids[k] = Mid( f.Indices[k], f.Indices[(k + 1) % n] );
			for ( var k = 0; k < n; k++ )
			{
				var prev = (k + n - 1) % n;
				m.AddFace( new[] { vertMap[f.Indices[k]], mids[k], centre, mids[prev] }, null, f.Material );
			}
		}
		return m;
	}

	public static GeometrySet Triangulate( GeoNodeCall c, GeoEvalContext ctx )
	{
		var mesh = NeedMesh( ctx, c );
		var m = new PolyMesh();
		foreach ( var p in mesh.Poly.Positions ) m.AddVertex( p );
		foreach ( var f in mesh.Poly.Faces )
		{
			if ( f.Count < 3 ) { m.AddFace( (int[])f.Indices.Clone(), f.UVs, f.Material ); continue; }
			for ( var i = 1; i + 1 < f.Count; i++ )
				m.AddFace( new[] { f.Indices[0], f.Indices[i], f.Indices[i + 1] }, null, f.Material );
		}
		return Mesh( m );
	}

	public static GeometrySet MergeByDistance( GeoNodeCall c, GeoEvalContext ctx )
	{
		var mesh = NeedMesh( ctx, c );
		var dist = Math.Max( 0f, c.F( "distance", 0.001f ) );
		var poly = mesh.Poly;
		var parent = new int[poly.Positions.Count];
		for ( var i = 0; i < parent.Length; i++ ) parent[i] = i;
		int Find( int x ) { while ( parent[x] != x ) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
		for ( var i = 0; i < poly.Positions.Count; i++ )
			for ( var j = i + 1; j < poly.Positions.Count; j++ )
				if ( (poly.Positions[i] - poly.Positions[j]).Length <= dist )
				{
					var a = Find( i ); var b = Find( j );
					if ( a != b ) parent[Math.Max( a, b )] = Math.Min( a, b );
				}
		var rep = new Dictionary<int, int>(); var pos = new List<Vec3>();
		var map = new int[poly.Positions.Count];
		for ( var i = 0; i < map.Length; i++ )
		{
			var r = Find( i );
			if ( !rep.TryGetValue( r, out var idx ) ) { idx = pos.Count; rep[r] = idx; pos.Add( poly.Positions[i] ); }
			map[i] = idx;
		}
		var m = new PolyMesh();
		foreach ( var p in pos ) m.AddVertex( p );
		foreach ( var f in poly.Faces )
		{
			var idx = f.Indices.Select( i => map[i] ).Distinct().ToArray();
			if ( idx.Length < 2 ) continue;
			if ( idx.Length == 2 ) { m.AddFace( idx, null, f.Material ); continue; }
			m.AddFace( idx, null, f.Material );
		}
		return Mesh( m );
	}

	public static GeometrySet JoinGeometry( GeoNodeCall c, GeoEvalContext ctx )
	{
		var sets = new List<GeometrySet>();
		if ( c.Inputs.TryGetValue( "geometries", out var list ) )
			foreach ( var id in list.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
				if ( ctx.Named.TryGetValue( id, out var g ) ) sets.Add( g );
		var single = ctx.NamedInput( c, "geometry" );
		if ( single is not null ) sets.Add( single );
		if ( sets.Count == 0 ) sets.Add( ctx.Geometry( c ) );
		var outMesh = new PolyMesh();
		var outCurves = new List<GeoSpline>();
		var outProtos = new List<GeometrySet>(); var outRefs = new List<int>(); var outXf = new List<Xform>();
		foreach ( var g in sets )
		{
			if ( g.Mesh is not null ) GeoMeshBuild.Append( outMesh, g.Mesh.Poly );
			if ( g.Curves is not null ) outCurves.AddRange( g.Curves.Splines );
			if ( g.Instances is not null )
			{
				var off = outProtos.Count;
				outProtos.AddRange( g.Instances.Prototypes );
				outRefs.AddRange( g.Instances.Reference.Select( r => r + off ) );
				outXf.AddRange( g.Instances.Transforms );
			}
		}
		var result = new GeometrySet(
			outMesh.Positions.Count > 0 || outMesh.Faces.Count > 0 ? new GeoMesh( outMesh ) : null,
			outCurves.Count > 0 ? new GeoCurves( outCurves ) : null,
			outXf.Count > 0 ? new GeoInstances( outProtos.ToArray(), outRefs.ToArray(), outXf.ToArray() ) : null );
		return result;
	}
}

/// <summary>Graph evaluation: the registry, topological ordering, JSON and pipe-DSL parsing.
/// CurrentTarget is set by the evaluator before each node runs so Proximity/Raycast fields
/// built during that node see the node's target input. It is deliberately a plain static:
/// graph evaluation is single-threaded and re-entrant use is not a supported pattern.</summary>
public static class GeoNodes
{
	public static GeometrySet CurrentTarget;

	public delegate GeometrySet NodeFn( GeoNodeCall Call, GeoEvalContext Ctx );

	public static readonly Dictionary<string, NodeFn> Registry = new( StringComparer.OrdinalIgnoreCase )
	{
		["Grid"] = GeoNodeImpl.Grid,
		["UVSphere"] = GeoNodeImpl.UVSphere,
		["IcoSphere"] = GeoNodeImpl.IcoSphere,
		["Cylinder"] = GeoNodeImpl.Cylinder,
		["CurveLine"] = GeoNodeImpl.CurveLine,
		["CurveCircle"] = GeoNodeImpl.CurveCircle,
		["CurveSpiral"] = GeoNodeImpl.CurveSpiral,
		["CurveToMesh"] = GeoNodeImpl.CurveToMesh,
		["ResampleCurve"] = GeoNodeImpl.ResampleCurve,
		["TrimCurve"] = GeoNodeImpl.TrimCurve,
		["DistributePointsOnFaces"] = GeoNodeImpl.DistributePointsOnFaces,
		["InstanceOnPoints"] = GeoNodeImpl.InstanceOnPoints,
		["RealizeInstances"] = GeoNodeImpl.RealizeInstances,
		["SetPosition"] = GeoMeshOps.SetPosition,
		["ExtrudeMesh"] = GeoMeshOps.ExtrudeMesh,
		["ScaleElements"] = GeoMeshOps.ScaleElements,
		["SubdivideMesh"] = GeoMeshOps.SubdivideMesh,
		["Triangulate"] = GeoMeshOps.Triangulate,
		["MergeByDistance"] = GeoMeshOps.MergeByDistance,
		["JoinGeometry"] = GeoMeshOps.JoinGeometry,
	};

	/// <summary>Node types the registry knows, plus the field types <see cref="GeoFields"/> builds.
	/// The roadmap asks for 20 high-leverage nodes; this library ships 20 nodes and 10 fields.</summary>
	public static IReadOnlyList<string> NodeTypes => Registry.Keys.OrderBy( k => k ).ToArray();

	public static GeometrySet EvaluateWithIds( IReadOnlyList<(string Id, GeoNodeCall Call)> nodes )
	{
		var named = new Dictionary<string, GeometrySet>( StringComparer.OrdinalIgnoreCase );
		var ids = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var (id, _) in nodes )
			if ( !ids.Add( id ) )
				throw new GeoGraphException( new GeoNodeError( GeoErrorCode.DuplicateNodeId, id, null, $"duplicate node id '{id}'" ) );
		var order = TopologicalIds( nodes );
		GeometrySet last = GeometrySet.Empty;
		foreach ( var (id, node) in order )
		{
			if ( !Registry.TryGetValue( node.Type, out var fn ) )
				throw new GeoGraphException( new GeoNodeError( GeoErrorCode.UnknownNodeType, id, null, $"unknown node type '{node.Type}'" ) );
			var ctx = new GeoEvalContext { Named = named, Input = last };
			CurrentTarget = ctx.NamedInput( node, "target" );
			try { last = fn( node, ctx ); }
			catch ( GeoNodeFailure f )
			{
				throw new GeoGraphException( new GeoNodeError( GeoErrorCode.EvaluationFailed, id, null, f.Message ) );
			}
			finally { CurrentTarget = null; }
			named[id] = last;
		}
		return last;
	}

	static List<(string Id, GeoNodeCall Call)> TopologicalIds( IReadOnlyList<(string Id, GeoNodeCall Call)> nodes )
	{
		var byId = nodes.ToDictionary( n => n.Id, StringComparer.OrdinalIgnoreCase );
		var state = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		var order = new List<(string, GeoNodeCall)>();
		void Visit( string id )
		{
			if ( state.TryGetValue( id, out var s ) )
			{
				if ( s == 1 ) throw new GeoGraphException( new GeoNodeError( GeoErrorCode.Cycle, id, null, $"graph cycle through '{id}'" ) );
				return;
			}
			state[id] = 1;
			var node = byId[id].Call;
			var deps = new List<string>();
			if ( node.GeometryId is not null ) deps.Add( node.GeometryId );
			foreach ( var inputList in node.Inputs.Values )
				foreach ( var part in inputList.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
					if ( byId.ContainsKey( part ) && !deps.Contains( part ) ) deps.Add( part );
			foreach ( var d in deps )
				if ( byId.ContainsKey( d ) ) Visit( d );
			state[id] = 2;
			order.Add( (id, node) );
		}
		foreach ( var (id, _) in nodes ) Visit( id );
		return order;
	}

	/// <summary>Evaluate a graph string: JSON when it starts with '{', otherwise the pipe DSL.</summary>
	public static GeometrySet EvaluateGraph( string graph )
	{
		if ( string.IsNullOrWhiteSpace( graph ) )
			throw new GeoNodeFailure( "a GeoNodes graph is required (JSON or pipe DSL)" );
		var text = graph.Trim();
		return text.StartsWith( "{" ) ? EvaluateJson( text ) : EvaluatePipe( text );
	}

	public static GeometrySet EvaluateJson( string json )
	{
		JsonDocument doc;
		try { doc = JsonDocument.Parse( json ); }
		catch ( JsonException e ) { throw new GeoGraphException( new GeoNodeError( GeoErrorCode.MalformedJson, null, null, e.Message ) ); }
		using ( doc )
		{
			if ( !doc.RootElement.TryGetProperty( "nodes", out var arr ) || arr.ValueKind != JsonValueKind.Array )
				throw new GeoGraphException( new GeoNodeError( GeoErrorCode.MalformedJson, null, null, "graph JSON needs a \"nodes\" array" ) );
			var nodes = new List<(string, GeoNodeCall)>();
			var i = 0;
			foreach ( var el in arr.EnumerateArray() )
			{
				var call = new GeoNodeCall();
				call.Type = el.TryGetProperty( "type", out var t ) ? t.GetString() : throw new GeoGraphException( new GeoNodeError( GeoErrorCode.MalformedJson, null, null, "a node needs a \"type\"" ) );
				if ( el.TryGetProperty( "geometry", out var g ) && g.ValueKind == JsonValueKind.String ) call.GeometryId = g.GetString();
				if ( el.TryGetProperty( "inputs", out var ins ) && ins.ValueKind == JsonValueKind.Object )
					foreach ( var p in ins.EnumerateObject() )
						call.Inputs[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : string.Join( ",", p.Value.EnumerateArray().Select( x => x.GetString() ) );
				if ( el.TryGetProperty( "props", out var props ) && props.ValueKind == JsonValueKind.Object )
					foreach ( var p in props.EnumerateObject() )
						call.Props[p.Name] = JsonValue( p.Value );
				else
					foreach ( var p in el.EnumerateObject() )
						if ( p.Name is not ("id" or "type" or "geometry" or "inputs" or "props") )
							call.Props[p.Name] = JsonValue( p.Value );
				var id = el.TryGetProperty( "id", out var idEl ) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : $"n{i}";
				nodes.Add( (id, call) );
				i++;
			}
			return EvaluateWithIds( nodes );
		}
	}

	static object JsonValue( JsonElement el ) => el.ValueKind switch
	{
		JsonValueKind.Number => el.TryGetInt32( out var i ) ? (object)(float)i : (object)(float)el.GetDouble(),
		JsonValueKind.String => el.GetString(),
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		JsonValueKind.Object => el.EnumerateObject().ToDictionary( p => p.Name, p => JsonValue( p.Value ), StringComparer.OrdinalIgnoreCase ),
		JsonValueKind.Array => string.Join( ",", el.EnumerateArray().Select( x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString() ) ),
		_ => null,
	};

	public static GeometrySet EvaluatePipe( string dsl )
	{
		var stages = SplitPipe( dsl );
		var nodes = new List<(string, GeoNodeCall)>();
		for ( var i = 0; i < stages.Count; i++ )
		{
			var tokens = Tokenise( stages[i] );
			if ( tokens.Count == 0 ) continue;
			var call = new GeoNodeCall { Type = tokens[0] };
			foreach ( var tok in tokens.Skip( 1 ) )
			{
				var kv = tok.Split( '=', 2 );
				if ( kv.Length != 2 ) throw new GeoNodeFailure( $"pipe stage '{stages[i]}': token '{tok}' is not key=value" );
				var key = kv[0].Trim(); var value = kv[1].Trim();
				if ( key == "geometry" ) call.GeometryId = value;
				else if ( key is "prototype" or "profile" or "target" or "prototypes" or "geometries" ) call.Inputs[key] = value;
				else if ( float.TryParse( value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f ) ) call.Props[key] = f;
				else if ( bool.TryParse( value, out var b ) ) call.Props[key] = b;
				else call.Props[key] = value;
			}
			nodes.Add( ($"s{i}", call) );
		}
		if ( nodes.Count == 0 ) throw new GeoNodeFailure( "empty pipe graph" );
		// Pipe stages are linear: each stage's geometry is the previous stage's output, so
		// clear any accidental geometry links and let EvaluateWithIds' Input chaining carry it.
		foreach ( var (_, call) in nodes ) call.GeometryId = null;
		return EvaluateWithIds( nodes );
	}

	static List<string> SplitPipe( string dsl )
	{
		var parts = new List<string>(); var sb = new StringBuilder(); var inQuote = false;
		foreach ( var ch in dsl )
		{
			if ( ch == '"' ) inQuote = !inQuote;
			if ( ch == '|' && !inQuote ) { parts.Add( sb.ToString() ); sb.Clear(); }
			else sb.Append( ch );
		}
		parts.Add( sb.ToString() );
		return parts.Where( p => !string.IsNullOrWhiteSpace( p ) ).Select( p => p.Trim() ).ToList();
	}

	static List<string> Tokenise( string stage )
	{
		var tokens = new List<string>(); var sb = new StringBuilder(); var inQuote = false;
		foreach ( var ch in stage )
		{
			if ( ch == '"' ) { inQuote = !inQuote; continue; }
			if ( char.IsWhiteSpace( ch ) && !inQuote )
			{
				if ( sb.Length > 0 ) { tokens.Add( sb.ToString() ); sb.Clear(); }
			}
			else sb.Append( ch );
		}
		if ( sb.Length > 0 ) tokens.Add( sb.ToString() );
		return tokens;
	}
}

/// <summary>
/// What one bot-facing GeoNodes evaluation did. <see cref="Ok"/> is false with
/// <see cref="Error"/> set when the graph was refused — a refused graph never
/// produces a mesh, so a bot cannot mistake an empty result for a built one.
/// <see cref="InstanceOrigins"/> are the scatter points' positions (empty when the
/// graph produced no instances). Realized counts are -1 unless
/// <see cref="GeoNodesCommands.Evaluate(string, bool)"/> was asked to realize.
/// </summary>
public sealed class GeoNodesCommandResult
{
	public bool Ok;
	public string Error = "";
	public bool HasMesh;
	public bool HasCurves;
	public bool HasInstances;
	public int PointCount;
	public int FaceCount;
	public int InstanceCount;
	public Vec3[] InstanceOrigins = Array.Empty<Vec3>();
	public int RealizedPointCount = -1;
	public int RealizedFaceCount = -1;
	public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
	public bool HasBounds;

	public static GeoNodesCommandResult Fail( string error ) => new() { Ok = false, Error = error };
}

/// <summary>
/// Bot-facing GeoNodes evaluation: graph in, evaluated mesh out, without creating
/// a feature or a body. This is the evaluate-only probe — the feature path already
/// exists (<see cref="GeoNodesFeature"/> via <c>add GeoNodes</c> / add_feature),
/// but a bot that wants to check a graph before committing it to the feature tree
/// needs an answer that is not a rebuilt document. Evaluation runs through the
/// same <see cref="GeoNodes.EvaluateGraph"/> the feature uses, so the probe and
/// the build cannot disagree. The hair/fur scatter seam is covered here too: a
/// DistributePointsOnFaces + InstanceOnPoints graph reports the scattered
/// instance count and origins, and realizing it bakes the prototypes into a mesh.
/// </summary>
public static class GeoNodesCommands
{
	/// <summary>Node types the registry knows, for a bot to check a spelling before sending a graph.</summary>
	public static IReadOnlyList<string> NodeTypes => GeoNodes.NodeTypes;

	/// <summary>
	/// Evaluate a graph string (JSON when it starts with '{', otherwise the pipe
	/// DSL). With <paramref name="realizeInstances"/>, instances are baked into a
	/// mesh and the realized counts are reported; a mesh result realizes to itself,
	/// and a curves-only graph is refused honestly instead of baking to junk.
	/// </summary>
	public static GeoNodesCommandResult Evaluate( string graph, bool realizeInstances = false )
	{
		if ( string.IsNullOrWhiteSpace( graph ) )
			return GeoNodesCommandResult.Fail( "a GeoNodes graph is required (JSON or pipe DSL)" );
		GeometrySet result;
		try { result = GeoNodes.EvaluateGraph( graph ); }
		catch ( GeoGraphException e )
		{
			return GeoNodesCommandResult.Fail( "graph failed: " + string.Join( "; ", e.Errors ) );
		}
		catch ( GeoNodeFailure e )
		{
			return GeoNodesCommandResult.Fail( "graph failed: " + e.Message );
		}
		var r = new GeoNodesCommandResult { Ok = true };
		r.HasMesh = result.Mesh is not null;
		r.HasCurves = result.Curves is not null;
		r.HasInstances = result.Instances is not null;
		if ( result.Mesh is not null )
		{
			r.PointCount = result.Mesh.PointCount;
			r.FaceCount = result.Mesh.FaceCount;
			FillBounds( result.Mesh.Poly, r );
		}
		if ( result.Instances is not null )
		{
			r.InstanceCount = result.Instances.Count;
			r.InstanceOrigins = result.Instances.Transforms.Select( t => t.Origin ).ToArray();
		}
		if ( !realizeInstances ) return r;
		// Instances win: RealizeInstances appends the input mesh AND the baked
		// prototypes, so a points mesh with instances realizes to the full scatter,
		// not just the bare points. A mesh with no instances realizes to itself.
		PolyMesh realised;
		if ( result.Instances is not null )
		{
			GeometrySet done;
			try
			{
				done = GeoNodeImpl.RealizeInstances(
					new GeoNodeCall { Type = "RealizeInstances" },
					new GeoEvalContext { Named = new Dictionary<string, GeometrySet>(), Input = result } );
			}
			catch ( GeoNodeFailure e )
			{
				return GeoNodesCommandResult.Fail( "realize failed: " + e.Message );
			}
			if ( done.Mesh is null )
				return GeoNodesCommandResult.Fail( "the graph produced instances but no mesh to realise" );
			realised = done.Mesh.Poly;
		}
		else if ( result.Mesh is not null )
			realised = result.Mesh.Poly;
		else
			return GeoNodesCommandResult.Fail( "the graph produced curves but no mesh, and a realised mesh is a mesh" );
		r.RealizedPointCount = realised.Positions.Count;
		r.RealizedFaceCount = realised.Faces.Count;
		return r;
	}

	static void FillBounds( PolyMesh poly, GeoNodesCommandResult r )
	{
		if ( poly.Positions.Count == 0 ) return;
		var minX = float.MaxValue; var minY = float.MaxValue; var minZ = float.MaxValue;
		var maxX = float.MinValue; var maxY = float.MinValue; var maxZ = float.MinValue;
		foreach ( var p in poly.Positions )
		{
			if ( p.x < minX ) minX = p.x;
			if ( p.y < minY ) minY = p.y;
			if ( p.z < minZ ) minZ = p.z;
			if ( p.x > maxX ) maxX = p.x;
			if ( p.y > maxY ) maxY = p.y;
			if ( p.z > maxZ ) maxZ = p.z;
		}
		r.MinX = minX; r.MinY = minY; r.MinZ = minZ;
		r.MaxX = maxX; r.MaxY = maxY; r.MaxZ = maxZ;
		r.HasBounds = true;
	}
}
