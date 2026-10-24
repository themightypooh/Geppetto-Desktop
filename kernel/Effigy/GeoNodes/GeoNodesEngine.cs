using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Effigy;

// Geometry nodes (ticket 18): the value model. Engine-free; only kernel mesh types.
//
// IMMUTABILITY CONTRACT. Every GeometrySet, GeoMesh, GeoCurves, GeoInstances and GeoArray that
// leaves a node is treated as frozen: the evaluator memoizes node outputs and hands the same
// objects to every downstream consumer, so an operation that changes geometry builds a new one
// (sharing untouched arrays is fine, writing into them is not).

/// <summary>Socket value types. Int and Bool are stored as float in <see cref="GeoArray"/>.</summary>
public enum GeoSocketType { Geometry, Float, Int, Vector, Bool }

/// <summary>Attribute domains. Edge is readable (fields, selections) but not storable.</summary>
public enum GeoDomain { Point, Edge, Face, Instance }

public enum GeoErrorCode
{
	UnknownNodeType,
	DuplicateNodeId,
	UnknownSocket,
	UnknownProperty,
	InvalidPropertyValue,
	TypeMismatch,
	FieldToSingleValue,
	MultipleLinksToInput,
	MissingInput,
	Cycle,
	NoOutput,
	MultipleOutputs,
	InvalidValue,
	InvalidKeyBinding,
	EvaluationFailed,
	UnsupportedVersion,
	MalformedJson,
}

/// <summary>One structured graph problem. NodeId/Socket are null when not applicable.</summary>
public sealed record GeoNodeError( GeoErrorCode Code, string NodeId, string Socket, string Message )
{
	public override string ToString() => $"{Code} [{NodeId ?? "-"}{(Socket is null ? "" : "." + Socket)}]: {Message}";
}

public sealed class GeoGraphException : Exception
{
	public IReadOnlyList<GeoNodeError> Errors { get; }

	public GeoGraphException( IReadOnlyList<GeoNodeError> errors )
		: base( string.Join( "; ", errors.Select( e => e.ToString() ) ) ) => Errors = errors;

	public GeoGraphException( GeoNodeError error ) : this( new[] { error } ) { }
}

/// <summary>Thrown by node implementations; the evaluator turns it into EvaluationFailed for the node.</summary>
public sealed class GeoNodeFailure : Exception
{
	public GeoNodeFailure( string message ) : base( message ) { }
}

/// <summary>A typed column of values: per element of a domain, or a single element for a constant.</summary>
public sealed class GeoArray
{
	public readonly GeoSocketType Type;
	public readonly float[] F;
	public readonly Vec3[] V;

	public GeoArray( GeoSocketType type, float[] values )
	{
		if ( type is GeoSocketType.Vector or GeoSocketType.Geometry ) throw new ArgumentException( "scalar array needs a scalar type" );
		Type = type;
		F = values ?? throw new ArgumentNullException( nameof( values ) );
	}

	public GeoArray( Vec3[] values )
	{
		Type = GeoSocketType.Vector;
		V = values ?? throw new ArgumentNullException( nameof( values ) );
	}

	public int Count => F?.Length ?? V.Length;

	public float Float( int i ) => F is not null ? F[i] : (V[i].x + V[i].y + V[i].z) / 3f;
	public Vec3 Vector( int i ) => V is not null ? V[i] : new Vec3( F[i], F[i], F[i] );
	public bool Bool( int i ) => F is not null ? F[i] != 0f : V[i].LengthSquared != 0f;

	public static GeoArray Filled( GeoSocketType type, int count, object value )
	{
		if ( type == GeoSocketType.Vector )
		{
			var v = GeoValues.ToVector( value );
			var a = new Vec3[count];
			Array.Fill( a, v );
			return new GeoArray( a );
		}

		var f = new float[count];
		Array.Fill( f, GeoValues.ToFloat( value ) );
		return new GeoArray( type, f );
	}

	public static GeoArray Zero( GeoSocketType type, int count ) =>
		type == GeoSocketType.Vector ? new GeoArray( new Vec3[count] ) : new GeoArray( type, new float[count] );

	/// <summary>Convert to another value type: float→vector broadcasts, vector→float averages, bool is non-zero.</summary>
	public GeoArray As( GeoSocketType type )
	{
		if ( type == Type ) return this;
		var n = Count;

		if ( type == GeoSocketType.Vector )
		{
			var v = new Vec3[n];
			for ( var i = 0; i < n; i++ ) v[i] = Vector( i );
			return new GeoArray( v );
		}

		var f = new float[n];
		for ( var i = 0; i < n; i++ )
		{
			f[i] = type switch
			{
				GeoSocketType.Bool => Bool( i ) ? 1f : 0f,
				GeoSocketType.Int => MathF.Round( Float( i ) ),
				_ => Float( i ),
			};
		}

		return new GeoArray( type, f );
	}

	/// <summary>Gather elements by source index.</summary>
	public GeoArray Gather( IReadOnlyList<int> source )
	{
		if ( V is not null )
		{
			var v = new Vec3[source.Count];
			for ( var i = 0; i < v.Length; i++ ) v[i] = V[source[i]];
			return new GeoArray( v );
		}

		var f = new float[source.Count];
		for ( var i = 0; i < f.Length; i++ ) f[i] = F[source[i]];
		return new GeoArray( Type, f );
	}
}

/// <summary>Helpers for single values flowing on non-geometry sockets: float, int, bool, Vec3 or a GeoField.</summary>
public static class GeoValues
{
	public static float ToFloat( object v ) => v switch
	{
		float f => f,
		int i => i,
		bool b => b ? 1f : 0f,
		double d => (float)d,
		Vec3 x => (x.x + x.y + x.z) / 3f,
		GeoField => throw new GeoNodeFailure( "a field was given where a single value is required" ),
		_ => throw new GeoNodeFailure( $"not a number: {v ?? "null"}" ),
	};

	public static Vec3 ToVector( object v ) => v switch
	{
		Vec3 x => x,
		GeoField => throw new GeoNodeFailure( "a field was given where a single value is required" ),
		_ => new Vec3( ToFloat( v ), ToFloat( v ), ToFloat( v ) ),
	};

	/// <summary>Convert a single value to a socket type's canonical CLR representation.</summary>
	public static object Convert( object v, GeoSocketType type ) => v switch
	{
		GeoField f => f.As( type ),
		_ => type switch
		{
			GeoSocketType.Float => ToFloat( v ),
			GeoSocketType.Int => (int)MathF.Round( ToFloat( v ) ),
			GeoSocketType.Bool => v is Vec3 x ? x.LengthSquared != 0f : ToFloat( v ) != 0f,
			GeoSocketType.Vector => ToVector( v ),
			_ => v,
		},
	};

	public static bool IsFinite( object v ) => v switch
	{
		float f => float.IsFinite( f ),
		double d => double.IsFinite( d ),
		Vec3 x => float.IsFinite( x.x ) && float.IsFinite( x.y ) && float.IsFinite( x.z ),
		_ => true,
	};

	/// <summary>Stable, culture-invariant text for hashing and JSON.</summary>
	public static string Canonical( object v ) => v switch
	{
		null => "null",
		float f => "f" + f.ToString( "R", CultureInfo.InvariantCulture ),
		int i => "i" + i.ToString( CultureInfo.InvariantCulture ),
		bool b => b ? "true" : "false",
		Vec3 x => "v" + x.x.ToString( "R", CultureInfo.InvariantCulture ) + "," + x.y.ToString( "R", CultureInfo.InvariantCulture ) + "," + x.z.ToString( "R", CultureInfo.InvariantCulture ),
		string s => "s" + s,
		_ => throw new ArgumentException( $"cannot canonicalise {v.GetType().Name}" ),
	};
}

/// <summary>
/// A lazily evaluated per-element function, Blender's field. Evaluated against a
/// <see cref="GeoFieldContext"/> (a component plus a domain) by the node that consumes it.
/// </summary>
public abstract class GeoField
{
	public abstract GeoSocketType Type { get; }
	public abstract GeoArray Evaluate( GeoFieldContext ctx );

	public GeoField As( GeoSocketType type ) => type == Type ? this : new GeoFuncField( type, ctx => Evaluate( ctx ).As( type ) );

	public static GeoField Constant( object value, GeoSocketType type ) => new GeoConstantField( GeoValues.Convert( value, type ), type );

	/// <summary>Wrap a single value as a field, or return the field.</summary>
	public static GeoField From( object value, GeoSocketType type ) => value is GeoField f ? f.As( type ) : Constant( value, type );
}

public sealed class GeoConstantField : GeoField
{
	readonly GeoSocketType _type;
	public readonly object Value;
	public GeoConstantField( object value, GeoSocketType type ) { Value = value; _type = type; }
	public override GeoSocketType Type => _type;
	public override GeoArray Evaluate( GeoFieldContext ctx ) => GeoArray.Filled( _type, ctx.Count, Value );
}

public sealed class GeoFuncField : GeoField
{
	readonly GeoSocketType _type;
	readonly Func<GeoFieldContext, GeoArray> _eval;
	public GeoFuncField( GeoSocketType type, Func<GeoFieldContext, GeoArray> eval ) { _type = type; _eval = eval; }
	public override GeoSocketType Type => _type;

	public override GeoArray Evaluate( GeoFieldContext ctx )
	{
		var a = _eval( ctx );
		if ( a.Count != ctx.Count ) throw new GeoNodeFailure( $"field produced {a.Count} values for {ctx.Count} elements" );
		return a.As( _type );
	}
}

/// <summary>A mesh plus named point and face attributes. Wraps a kernel PolyMesh (faces carry per-corner UVs and material).</summary>
public sealed class GeoMesh
{
	public readonly PolyMesh Poly;
	public readonly SortedDictionary<string, GeoArray> PointAttributes;
	public readonly SortedDictionary<string, GeoArray> FaceAttributes;

	public const string ColorAttribute = "color";
	public const string NormalAttribute = "normal";

	public GeoMesh( PolyMesh poly, SortedDictionary<string, GeoArray> points = null, SortedDictionary<string, GeoArray> faces = null )
	{
		Poly = poly ?? throw new ArgumentNullException( nameof( poly ) );
		PointAttributes = points ?? new SortedDictionary<string, GeoArray>( StringComparer.Ordinal );
		FaceAttributes = faces ?? new SortedDictionary<string, GeoArray>( StringComparer.Ordinal );

		foreach ( var (name, a) in PointAttributes )
			if ( a.Count != poly.Positions.Count ) throw new ArgumentException( $"point attribute '{name}' has {a.Count} values for {poly.Positions.Count} points" );
		foreach ( var (name, a) in FaceAttributes )
			if ( a.Count != poly.Faces.Count ) throw new ArgumentException( $"face attribute '{name}' has {a.Count} values for {poly.Faces.Count} faces" );
	}

	public int PointCount => Poly.Positions.Count;
	public int FaceCount => Poly.Faces.Count;
	public int EdgeCount => Edges.Length;

	public static SortedDictionary<string, GeoArray> NewAttributes() => new( StringComparer.Ordinal );
	public static SortedDictionary<string, GeoArray> Copy( SortedDictionary<string, GeoArray> a ) => new( a, StringComparer.Ordinal );

	EdgeKey[] _edges;
	Vec3[] _faceNormals, _pointNormals;
	int[] _faceIslands, _pointIslands;

	/// <summary>Every undirected edge, sorted by (A, B) - the edge domain's element order.</summary>
	public EdgeKey[] Edges
	{
		get
		{
			if ( _edges is not null ) return _edges;
			var set = new HashSet<EdgeKey>();
			foreach ( var f in Poly.Faces )
				for ( var i = 0; i < f.Count; i++ ) set.Add( new EdgeKey( f.Indices[i], f.Indices[(i + 1) % f.Count] ) );
			var list = set.ToArray();
			Array.Sort( list, ( a, b ) => a.A != b.A ? a.A.CompareTo( b.A ) : a.B.CompareTo( b.B ) );
			return _edges = list;
		}
	}

	public Vec3[] FaceNormals => _faceNormals ??= Poly.Faces.Select( f => Poly.FaceNormal( f ) ).ToArray();

	/// <summary>Custom normals (the "normal" point attribute) when set, otherwise computed vertex normals.</summary>
	public Vec3[] PointNormals
	{
		get
		{
			if ( _pointNormals is not null ) return _pointNormals;
			if ( PointAttributes.TryGetValue( NormalAttribute, out var custom ) && custom.V is not null ) return _pointNormals = custom.V;
			return _pointNormals = Poly.ComputeVertexNormals();
		}
	}

	/// <summary>Connected-component index per face (faces sharing a vertex are connected), numbered in first-face order.</summary>
	public int[] FaceIslands { get { ComputeIslands(); return _faceIslands; } }
	public int[] PointIslands { get { ComputeIslands(); return _pointIslands; } }
	public int IslandCount { get { ComputeIslands(); return _islandCount; } }
	int _islandCount;

	void ComputeIslands()
	{
		if ( _faceIslands is not null ) return;
		var parent = new int[PointCount];
		for ( var i = 0; i < parent.Length; i++ ) parent[i] = i;
		int Find( int x ) { while ( parent[x] != x ) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
		foreach ( var f in Poly.Faces )
			for ( var i = 1; i < f.Count; i++ )
			{
				var a = Find( f.Indices[0] ); var b = Find( f.Indices[i] );
				if ( a != b ) parent[Math.Max( a, b )] = Math.Min( a, b );
			}

		var number = new Dictionary<int, int>();
		var faceIslands = new int[FaceCount];
		for ( var fi = 0; fi < FaceCount; fi++ )
		{
			var root = Find( Poly.Faces[fi].Indices[0] );
			if ( !number.TryGetValue( root, out var id ) ) number[root] = id = number.Count;
			faceIslands[fi] = id;
		}

		var pointIslands = new int[PointCount];
		for ( var v = 0; v < PointCount; v++ )
		{
			var root = Find( v );
			if ( !number.TryGetValue( root, out var id ) ) number[root] = id = number.Count; // loose vertex
			pointIslands[v] = id;
		}

		_islandCount = number.Count;
		_faceIslands = faceIslands;
		_pointIslands = pointIslands;
	}

	/// <summary>
	/// The renderable/exportable kernel mesh: a clone of <see cref="Poly"/> with the "color" point
	/// attribute baked into <see cref="PolyMesh.VertexColors"/> (alpha 1). Other attributes, custom
	/// normals included, have no PolyMesh representation and are not carried.
	/// </summary>
	public PolyMesh ToPolyMesh()
	{
		var m = Poly.Clone();
		if ( PointAttributes.TryGetValue( ColorAttribute, out var c ) )
		{
			var colors = new Vec4[m.Positions.Count];
			for ( var i = 0; i < colors.Length; i++ ) { var v = c.Vector( i ); colors[i] = new Vec4( v.x, v.y, v.z, 1f ); }
			m.VertexColors = colors;
		}
		return m;
	}
}

/// <summary>One spline. Poly when <see cref="HandleLeft"/> is null, otherwise Bezier (handles per control point).</summary>
public sealed class GeoSpline
{
	public Vec3[] Points;
	public Vec3[] HandleLeft, HandleRight;
	public bool Cyclic;
	/// <summary>Evaluated samples per Bezier segment.</summary>
	public int Resolution = 12;

	public bool IsBezier => HandleLeft is not null;

	public GeoSpline Clone() => new()
	{
		Points = (Vec3[])Points.Clone(),
		HandleLeft = (Vec3[])HandleLeft?.Clone(),
		HandleRight = (Vec3[])HandleRight?.Clone(),
		Cyclic = Cyclic,
		Resolution = Resolution,
	};

	/// <summary>The polyline the spline describes: control points for poly, sampled segments for Bezier.</summary>
	public Vec3[] Evaluated()
	{
		if ( !IsBezier || Points.Length < 2 ) return Points;
		var res = Math.Max( 1, Resolution );
		var segments = Cyclic ? Points.Length : Points.Length - 1;
		var output = new List<Vec3>( segments * res + 1 );
		for ( var s = 0; s < segments; s++ )
		{
			var e = (s + 1) % Points.Length;
			Vec3 p0 = Points[s], p1 = HandleRight[s], p2 = HandleLeft[e], p3 = Points[e];
			for ( var j = 0; j < res; j++ )
			{
				var t = j / (float)res; var u = 1f - t;
				output.Add( p0 * (u * u * u) + p1 * (3f * u * u * t) + p2 * (3f * u * t * t) + p3 * (t * t * t) );
			}
		}
		if ( !Cyclic ) output.Add( Points[^1] );
		return output.ToArray();
	}
}

/// <summary>Curves with point-domain attributes over the control points of all splines, in order.</summary>
public sealed class GeoCurves
{
	public readonly IReadOnlyList<GeoSpline> Splines;
	public readonly SortedDictionary<string, GeoArray> PointAttributes;

	public GeoCurves( IReadOnlyList<GeoSpline> splines, SortedDictionary<string, GeoArray> attributes = null )
	{
		Splines = splines;
		PointAttributes = attributes ?? GeoMesh.NewAttributes();
		foreach ( var (name, a) in PointAttributes )
			if ( a.Count != PointCount ) throw new ArgumentException( $"curve attribute '{name}' has {a.Count} values for {PointCount} points" );
	}

	public int PointCount => Splines.Sum( s => s.Points.Length );

	public Vec3[] Positions() => Splines.SelectMany( s => s.Points ).ToArray();
}

/// <summary>Instances: references into a prototype list, each with a transform, plus instance-domain attributes.</summary>
public sealed class GeoInstances
{
	public readonly GeometrySet[] Prototypes;
	public readonly int[] Reference;
	public readonly Xform[] Transforms;
	public readonly SortedDictionary<string, GeoArray> Attributes;

	public GeoInstances( GeometrySet[] prototypes, int[] reference, Xform[] transforms, SortedDictionary<string, GeoArray> attributes = null )
	{
		if ( reference.Length != transforms.Length ) throw new ArgumentException( "reference/transform count mismatch" );
		foreach ( var r in reference ) if ( (uint)r >= (uint)prototypes.Length ) throw new ArgumentException( "instance references a missing prototype" );
		Prototypes = prototypes; Reference = reference; Transforms = transforms;
		Attributes = attributes ?? GeoMesh.NewAttributes();
		foreach ( var (name, a) in Attributes )
			if ( a.Count != Count ) throw new ArgumentException( $"instance attribute '{name}' has {a.Count} values for {Count} instances" );
	}

	public int Count => Reference.Length;
}

/// <summary>What flows on a Geometry socket: any of a mesh, curves and instances.</summary>
public sealed class GeometrySet
{
	public readonly GeoMesh Mesh;
	public readonly GeoCurves Curves;
	public readonly GeoInstances Instances;

	public static readonly GeometrySet Empty = new( null, null, null );

	public GeometrySet( GeoMesh mesh, GeoCurves curves = null, GeoInstances instances = null )
	{
		Mesh = mesh is { PointCount: 0, FaceCount: 0 } && mesh.PointAttributes.Count == 0 ? null : mesh;
		Curves = curves is { Splines.Count: 0 } ? null : curves;
		Instances = instances is { Count: 0 } ? null : instances;
	}

	public static GeometrySet FromMesh( PolyMesh poly ) => new( new GeoMesh( poly ) );

	public bool IsEmpty => Mesh is null && Curves is null && Instances is null;
	public GeometrySet WithMesh( GeoMesh m ) => new( m, Curves, Instances );
	public GeometrySet WithCurves( GeoCurves c ) => new( Mesh, c, Instances );
	public GeometrySet WithInstances( GeoInstances i ) => new( Mesh, Curves, i );
}

/// <summary>A component and domain a field is evaluated over.</summary>
public sealed class GeoFieldContext
{
	public readonly GeoDomain Domain;
	public readonly int Count;
	public readonly GeoMesh Mesh;
	public readonly GeoCurves Curves;
	public readonly GeoInstances Instances;

	GeoFieldContext( GeoDomain domain, int count, GeoMesh mesh, GeoCurves curves, GeoInstances instances )
	{
		Domain = domain; Count = count; Mesh = mesh; Curves = curves; Instances = instances;
	}

	/// <summary>A one-element context with no geometry: how a node with only constant inputs evaluates.</summary>
	public static readonly GeoFieldContext Single = new( GeoDomain.Point, 1, null, null, null );

	public static GeoFieldContext ForMesh( GeoMesh mesh, GeoDomain domain ) => domain switch
	{
		GeoDomain.Point => new( domain, mesh.PointCount, mesh, null, null ),
		GeoDomain.Face => new( domain, mesh.FaceCount, mesh, null, null ),
		GeoDomain.Edge => new( domain, mesh.EdgeCount, mesh, null, null ),
		_ => throw new GeoNodeFailure( $"a mesh has no {domain} domain" ),
	};

	public static GeoFieldContext ForCurves( GeoCurves curves ) => new( GeoDomain.Point, curves.PointCount, null, curves, null );
	public static GeoFieldContext ForInstances( GeoInstances instances ) => new( GeoDomain.Instance, instances.Count, null, null, instances );

	string Where => Mesh is not null ? $"mesh {Domain}" : Curves is not null ? "curve point" : Instances is not null ? "instance" : "single value";

	Vec3[] _positions;

	public Vec3[] Positions()
	{
		if ( _positions is not null ) return _positions;
		if ( Mesh is not null )
		{
			var p = Mesh.Poly.Positions;
			return _positions = Domain switch
			{
				GeoDomain.Point => p.ToArray(),
				GeoDomain.Face => Mesh.Poly.Faces.Select( f => Mesh.Poly.FaceCentroid( f ) ).ToArray(),
				_ => Mesh.Edges.Select( e => (p[e.A] + p[e.B]) * 0.5f ).ToArray(),
			};
		}
		if ( Curves is not null ) return _positions = Curves.Positions();
		if ( Instances is not null ) return _positions = Instances.Transforms.Select( t => t.Origin ).ToArray();
		throw new GeoNodeFailure( "Position needs geometry; it was evaluated as a single value" );
	}

	public Vec3[] Normals()
	{
		if ( Mesh is null ) throw new GeoNodeFailure( $"Normal is only defined on meshes, not on {Where}" );
		return Domain switch
		{
			GeoDomain.Point => Mesh.PointNormals,
			GeoDomain.Face => Mesh.FaceNormals,
			_ => Mesh.Edges.Select( e => (Mesh.PointNormals[e.A] + Mesh.PointNormals[e.B]).Normal ).ToArray(),
		};
	}

	public float[] Index()
	{
		if ( this == Single ) throw new GeoNodeFailure( "Index needs geometry; it was evaluated as a single value" );
		var f = new float[Count];
		for ( var i = 0; i < f.Length; i++ ) f[i] = i;
		return f;
	}

	public float[] MaterialIndex()
	{
		if ( Mesh is null || Domain != GeoDomain.Face ) throw new GeoNodeFailure( $"Material Index is a mesh face value, not available on {Where}" );
		return Mesh.Poly.Faces.Select( f => (float)f.Material ).ToArray();
	}

	public float[] Island()
	{
		if ( Mesh is null ) throw new GeoNodeFailure( $"Mesh Island is only defined on meshes, not on {Where}" );
		return Domain switch
		{
			GeoDomain.Point => Mesh.PointIslands.Select( i => (float)i ).ToArray(),
			GeoDomain.Face => Mesh.FaceIslands.Select( i => (float)i ).ToArray(),
			_ => Mesh.Edges.Select( e => (float)Mesh.PointIslands[e.A] ).ToArray(),
		};
	}

	/// <summary>A named attribute adapted to this domain (point↔face averaged, point/face→edge averaged), or null when absent.</summary>
	public GeoArray Attribute( string name )
	{
		if ( Curves is not null ) return Curves.PointAttributes.TryGetValue( name, out var c ) ? c : null;
		if ( Instances is not null ) return Instances.Attributes.TryGetValue( name, out var i ) ? i : null;
		if ( Mesh is null ) return null;

		if ( Mesh.PointAttributes.TryGetValue( name, out var pa ) )
			return Domain switch
			{
				GeoDomain.Point => pa,
				GeoDomain.Face => Average( pa, Mesh.Poly.Faces.Select( f => (IReadOnlyList<int>)f.Indices ).ToList() ),
				_ => Average( pa, Mesh.Edges.Select( e => (IReadOnlyList<int>)new[] { e.A, e.B } ).ToList() ),
			};

		if ( Mesh.FaceAttributes.TryGetValue( name, out var fa ) )
		{
			if ( Domain == GeoDomain.Face ) return fa;
			var vf = Mesh.Poly.BuildVertexFaces();
			if ( Domain == GeoDomain.Point ) return Average( fa, vf.Select( l => (IReadOnlyList<int>)l ).ToList() );
			var ef = Mesh.Poly.BuildEdgeFaces();
			return Average( fa, Mesh.Edges.Select( e => (IReadOnlyList<int>)ef[e] ).ToList() );
		}

		return null;
	}

	static GeoArray Average( GeoArray source, IReadOnlyList<IReadOnlyList<int>> groups )
	{
		if ( source.V is not null )
		{
			var v = new Vec3[groups.Count];
			for ( var i = 0; i < v.Length; i++ )
			{
				var g = groups[i]; var s = Vec3.Zero;
				foreach ( var j in g ) s += source.V[j];
				v[i] = g.Count > 0 ? s / g.Count : Vec3.Zero;
			}
			return new GeoArray( v );
		}

		var f = new float[groups.Count];
		for ( var i = 0; i < f.Length; i++ )
		{
			var g = groups[i]; var s = 0f;
			foreach ( var j in g ) s += source.F[j];
			f[i] = g.Count > 0 ? s / g.Count : 0f;
		}
		// An averaged Int/Bool is no longer integral; it is read back as Float.
		return new GeoArray( source.Type == GeoSocketType.Bool || source.Type == GeoSocketType.Int ? GeoSocketType.Float : source.Type, f );
	}
}

/// <summary>Deterministic hashing: FNV-1a 64 over canonical text, and a content hash of geometry.</summary>
public static class GeoHash
{
	public static ulong Fnv( string text )
	{
		var h = 14695981039346656037UL;
		foreach ( var c in text )
		{
			h ^= (byte)c; h *= 1099511628211UL;
			h ^= (byte)(c >> 8); h *= 1099511628211UL;
		}
		return h;
	}

	/// <summary>Order-sensitive hash of everything a GeometrySet carries: positions (bit-exact), faces, UVs, materials, attributes, curves, instances.</summary>
	public static ulong Content( GeometrySet g )
	{
		var h = 14695981039346656037UL;
		void Add( uint x ) { for ( var i = 0; i < 4; i++ ) { h ^= (byte)(x >> (8 * i)); h *= 1099511628211UL; } }
		void AddF( float f ) => Add( BitConverter.SingleToUInt32Bits( f ) );
		void AddV( Vec3 v ) { AddF( v.x ); AddF( v.y ); AddF( v.z ); }
		void AddS( string s ) { Add( (uint)s.Length ); foreach ( var c in s ) Add( c ); }
		void AddAttrs( SortedDictionary<string, GeoArray> attrs )
		{
			Add( (uint)attrs.Count );
			foreach ( var (name, a) in attrs )
			{
				AddS( name ); Add( (uint)a.Type );
				if ( a.V is not null ) foreach ( var v in a.V ) AddV( v ); else foreach ( var f in a.F ) AddF( f );
			}
		}
		void AddSet( GeometrySet s )
		{
			if ( s.Mesh is { } m )
			{
				Add( 1 ); Add( (uint)m.PointCount );
				foreach ( var p in m.Poly.Positions ) AddV( p );
				Add( (uint)m.FaceCount );
				foreach ( var f in m.Poly.Faces )
				{
					Add( (uint)f.Count ); Add( (uint)f.Material );
					foreach ( var i in f.Indices ) Add( (uint)i );
					foreach ( var uv in f.UVs ) { AddF( uv.x ); AddF( uv.y ); }
				}
				AddAttrs( m.PointAttributes ); AddAttrs( m.FaceAttributes );
			}
			if ( s.Curves is { } c )
			{
				Add( 2 );
				foreach ( var sp in c.Splines )
				{
					Add( (uint)sp.Points.Length ); Add( sp.Cyclic ? 1u : 0u ); Add( (uint)sp.Resolution );
					foreach ( var p in sp.Points ) AddV( p );
					if ( sp.IsBezier ) { foreach ( var p in sp.HandleLeft ) AddV( p ); foreach ( var p in sp.HandleRight ) AddV( p ); }
				}
				AddAttrs( c.PointAttributes );
			}
			if ( s.Instances is { } inst )
			{
				Add( 3 ); Add( (uint)inst.Count );
				foreach ( var t in inst.Transforms ) { AddV( t.X ); AddV( t.Y ); AddV( t.Z ); AddV( t.Origin ); }
				foreach ( var r in inst.Reference ) Add( (uint)r );
				AddAttrs( inst.Attributes );
				foreach ( var p in inst.Prototypes ) AddSet( p );
			}
		}
		AddSet( g );
		return h;
	}

	/// <summary>Seeded integer hash (PCG-style) → [0,1). Deterministic across runs and runtimes.</summary>
	public static float Unit( uint seed, uint index, uint stream = 0 )
	{
		var x = index * 747796405u + 2891336453u + seed * 0x9E3779B9u + stream * 0x85EBCA6Bu;
		x = ((x >> (int)((x >> 28) + 4u)) ^ x) * 277803737u;
		x = (x >> 22) ^ x;
		x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
		return (x >> 8) * (1f / 16777216f);
	}
}
