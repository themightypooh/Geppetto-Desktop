using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Effigy;

/// <summary>STEP AP214 import/export for analytic B-rep solids (godlike-occt-brep-core-step-export).</summary>
public static class StepExchange
{
	/// <summary>Result of reading a STEP file: the solid, plus everything that was skipped or went wrong. Never silent.</summary>
	public sealed class StepReadResult
	{
		public BrepSolid Solid;
		public List<string> Warnings = new();
		public List<string> Errors = new();
		public bool Ok => Errors.Count == 0 && Solid is not null;
	}

	/// <summary>Write <paramref name="solid"/> as STEP AP214 (ISO 10303-21, SI millimetres).
	/// Throws <see cref="InvalidOperationException"/> naming the problems when the solid is not valid — an invalid solid is never written as if it were one.</summary>
	public static string Write( BrepSolid solid, string name = "part" )
	{
		ArgumentNullException.ThrowIfNull( solid );
		var problems = solid.Validate();
		if ( problems.Count > 0 )
			throw new InvalidOperationException( "Cannot write an invalid B-rep solid to STEP: " + string.Join( "; ", problems ) );
		return new Writer( solid, string.IsNullOrWhiteSpace( name ) ? "part" : name ).Run();
	}

	/// <summary>Read a STEP AP214 file produced by <see cref="Write"/> (or any STEP file whose analytic
	/// surfaces match the subset: planes, cylinders, cones, spheres, tori). Unknown entities are
	/// reported in <see cref="StepReadResult.Warnings"/>, never silently dropped.</summary>
	public static StepReadResult Read( string stepText )
	{
		ArgumentNullException.ThrowIfNull( stepText );
		return new Reader( stepText ).Run();
	}

	static string F( double v ) => v.ToString( "R", CultureInfo.InvariantCulture );
	static string F( float v ) => v.ToString( "R", CultureInfo.InvariantCulture );

	// ============================ writer ============================

	sealed class Writer
	{
		readonly BrepSolid _solid;
		readonly string _name;
		readonly List<string> _lines = new();
		int _nextId = 1;
		readonly Dictionary<string, int> _points = new();
		readonly Dictionary<string, int> _dirs = new();
		// EDGE_CURVE id per BrepEdge index.
		readonly Dictionary<int, int> _edgeCurves = new();
		int _context, _shapeRep;

		public Writer( BrepSolid solid, string name ) { _solid = solid; _name = name; }

		int Id() => _nextId++;

		int Point( Vec3 p )
		{
			var key = $"{F( p.x )},{F( p.y )},{F( p.z )}";
			if ( _points.TryGetValue( key, out var id ) ) return id;
			id = Id();
			_lines.Add( $"#{id}=CARTESIAN_POINT('',({F( p.x )},{F( p.y )},{F( p.z )}));" );
			_points[key] = id;
			return id;
		}

		int Direction( Vec3 d )
		{
			var n = d.Normal;
			var key = $"{F( n.x )},{F( n.y )},{F( n.z )}";
			if ( _dirs.TryGetValue( key, out var id ) ) return id;
			id = Id();
			_lines.Add( $"#{id}=DIRECTION('',({F( n.x )},{F( n.y )},{F( n.z )}));" );
			_dirs[key] = id;
			return id;
		}

		int Placement( Vec3 location, Vec3 axis )
		{
			BrepSolid.Basis( axis, out var e1, out _ );
			var id = Id();
			_lines.Add( $"#{id}=AXIS2_PLACEMENT_3D('',#{Point( location )},#{Direction( axis )},#{Direction( e1 )});" );
			return id;
		}

		int Surface( BrepFace f )
		{
			var id = Id();
			switch ( f.Kind )
			{
				case BrepSurfaceKind.Plane:
					_lines.Add( $"#{id}=PLANE('',#{Placement( f.Origin, f.Axis )});" );
					break;
				case BrepSurfaceKind.Cylinder:
					_lines.Add( $"#{id}=CYLINDRICAL_SURFACE('',#{Placement( f.Origin, f.Axis )},{F( f.Radius )});" );
					break;
				case BrepSurfaceKind.Cone:
				{
					// Placement origin at the base centre, axis from base toward the apex, radius = base radius.
					// Apex = origin + axis * (radius / tan(semi-angle)); the reader inverts exactly this.
					var baseCentre = f.Origin + f.Axis * (float)f.V1;
					var up = -f.Axis;
					_lines.Add( $"#{id}=CONICAL_SURFACE('',#{Placement( baseCentre, up )},{F( f.Radius )},{F( f.Radius2 )});" );
					break;
				}
				case BrepSurfaceKind.Sphere:
					_lines.Add( $"#{id}=SPHERICAL_SURFACE('',#{Placement( f.Origin, f.Axis )},{F( f.Radius )});" );
					break;
				default: // Torus
					_lines.Add( $"#{id}=TOROIDAL_SURFACE('',#{Placement( f.Origin, f.Axis )},{F( f.Radius )},{F( f.Radius2 )});" );
					break;
			}
			return id;
		}

		int Vertex( Vec3 p )
		{
			var id = Id();
			_lines.Add( $"#{id}=VERTEX_POINT('',#{Point( p )});" );
			return id;
		}

		int EdgeCurve( BrepEdge e )
		{
			var id = Id();
			if ( e.Kind == BrepEdgeKind.Line )
			{
				_lines.Add( $"#{id}=EDGE_CURVE('',#{Vertex( e.Start )},#{Vertex( e.End )},#{LineCurve( e )},.T.);" );
			}
			else
			{
				_lines.Add( $"#{id}=EDGE_CURVE('',#{Vertex( e.Start )},#{Vertex( e.End )},#{CircleCurve( e )},.T.);" );
			}
			return id;
		}

		int LineCurve( BrepEdge e )
		{
			var id = Id();
			_lines.Add( $"#{id}=LINE('',#{Point( e.Start )},#{Direction( e.End - e.Start )});" );
			return id;
		}

		int CircleCurve( BrepEdge e )
		{
			var id = Id();
			_lines.Add( $"#{id}=CIRCLE('',#{Placement( e.Center, e.Axis )},{F( e.Radius )});" );
			return id;
		}

		int OrientedEdge( int edgeCurveId, bool forward )
		{
			var id = Id();
			_lines.Add( $"#{id}=ORIENTED_EDGE('',*,*,#{edgeCurveId},{(forward ? ".T." : ".F.")});" );
			return id;
		}

		int EdgeLoop( IEnumerable<int> orientedEdges )
		{
			var id = Id();
			_lines.Add( $"#{id}=EDGE_LOOP('',({string.Join( ",", orientedEdges.Select( e => "#" + e ) )}));" );
			return id;
		}

		/// <summary>Ordered (edge, forward) traversal of one boundary of <paramref name="faceIndex"/>.
		/// Planar faces follow their stored loop order; lateral faces use the rim/seam construction.</summary>
		List<(BrepEdge Edge, bool Forward)> FaceLoop( int faceIndex, bool inner )
		{
			var face = _solid.Faces[faceIndex];
			var loop = new List<(BrepEdge, bool)>();
			if ( face.Kind == BrepSurfaceKind.Cylinder )
			{
				var circles = _solid.Edges.Where( e => e.Kind == BrepEdgeKind.Circle && (e.FaceA == faceIndex || e.FaceB == faceIndex) ).ToList();
				var seam = _solid.Edges.FirstOrDefault( e => e.Seam && e.FaceA == faceIndex );
				var bottom = circles.OrderBy( e => Vec3.Dot( e.Center - face.Origin, face.Axis ) ).FirstOrDefault();
				var top = circles.OrderByDescending( e => Vec3.Dot( e.Center - face.Origin, face.Axis ) ).FirstOrDefault();
				if ( bottom is null || top is null || seam is null ) return loop;
				// Bottom rim CCW from above, seam up, top rim CW from above, seam down: the standard
				// lateral-face wire (the face stays on the left of the traversal, viewed from outside).
				loop.Add( (bottom, true) );
				loop.Add( (seam, true) );
				loop.Add( (top, false) );
				loop.Add( (seam, false) );
				return loop;
			}
			if ( face.Kind == BrepSurfaceKind.Cone )
			{
				var baseCircle = _solid.Edges.FirstOrDefault( e => e.Kind == BrepEdgeKind.Circle && (e.FaceA == faceIndex || e.FaceB == faceIndex) );
				var seam = _solid.Edges.FirstOrDefault( e => e.Seam && e.FaceA == faceIndex );
				if ( baseCircle is null || seam is null ) return loop;
				loop.Add( (baseCircle, true) );
				loop.Add( (seam, true) );
				loop.Add( (seam, false) );
				return loop;
			}
			if ( face.Kind == BrepSurfaceKind.Sphere )
			{
				var equator = _solid.Edges.FirstOrDefault( e => e.Kind == BrepEdgeKind.Circle && (e.FaceA == faceIndex || e.FaceB == faceIndex) );
				if ( equator is null ) return loop;
				// North hemisphere (V0 == 0): the equator CCW from +axis. South: reversed.
				loop.Add( (equator, face.V0 == 0) );
				return loop;
			}
			if ( face.Kind == BrepSurfaceKind.Torus )
			{
				var circles = _solid.Edges.Where( e => e.Kind == BrepEdgeKind.Circle && (e.FaceA == faceIndex || e.FaceB == faceIndex) ).ToList();
				if ( circles.Count == 0 ) return loop;
				// Two boundary circles: the one at U0 is the outer bound, the one at U1 the second bound.
				// (Bound orientation on torus halves is the one place OCCT's own reader is the arbiter;
				// see packaging/OCCT.md. Spans round-trip exactly via the GEPPETTO comment below.)
				BrepSolid.Basis( face.Axis, out var e1, out var e2 );
				double AngleOf( BrepEdge e )
				{
					var rel = e.Center - face.Origin;
					return Math.Atan2( Vec3.Dot( rel, e2 ), Vec3.Dot( rel, e1 ) );
				}
				circles.Sort( (a, b) => AngleOf( a ).CompareTo( AngleOf( b ) ) );
				var pick = inner ? circles[^1] : circles[0];
				loop.Add( (pick, true) );
				return loop;
			}
			// Plane: follow the stored loop order. Circle rims: one edge. Rectangles: chain the line edges.
			var planeEdges = _solid.Edges.Where( e => e.FaceA == faceIndex || e.FaceB == faceIndex ).ToList();
			var circle = planeEdges.FirstOrDefault( e => e.Kind == BrepEdgeKind.Circle );
			if ( circle is not null )
			{
				// Disc or annulus: pick the rim by radius. Outer loop = larger radius, inner = smaller.
				var circles = planeEdges.Where( e => e.Kind == BrepEdgeKind.Circle ).OrderByDescending( e => e.Radius ).ToList();
				var rim = inner ? circles[^1] : circles[0];
				// The rim is CCW from its axis; the face normal decides the flag.
				loop.Add( (rim, Vec3.Dot( rim.Axis, face.Axis ) > 0) );
				return loop;
			}
			var pts = inner && face.InnerLoop is not null ? face.InnerLoop : face.Loop;
			for ( int i = 0; i < pts.Count; i++ )
			{
				var a = pts[i]; var b = pts[(i + 1) % pts.Count];
				var edge = planeEdges.FirstOrDefault( e => e.Kind == BrepEdgeKind.Line
					&& ((e.Start.AlmostEquals( a, 1e-3f ) && e.End.AlmostEquals( b, 1e-3f ))
						|| (e.Start.AlmostEquals( b, 1e-3f ) && e.End.AlmostEquals( a, 1e-3f ))) );
				if ( edge is null ) continue;
				loop.Add( (edge, edge.Start.AlmostEquals( a, 1e-3f )) );
			}
			return loop;
		}

		public string Run()
		{
			var sb = new StringBuilder();
			sb.AppendLine( "ISO-10303-21;" );
			sb.AppendLine( "HEADER;" );
			sb.AppendLine( "FILE_DESCRIPTION(('STEP AP214'),'2;1');" );
			sb.AppendLine( $"FILE_NAME('{_name}.stp','{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}','Geppetto Desktop','Geppetto Desktop','OCCT-BREP-CORE','','');" );
			sb.AppendLine( "FILE_SCHEMA(('AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }'));" );
			sb.AppendLine( "ENDSEC;" );
			sb.AppendLine( "DATA;" );

			// Representation context + units (SI millimetres).
			_context = Id();
			_lines.Add( $"#{_context}=GEOMETRIC_REPRESENTATION_CONTEXT(3,'3D');" );
			var mm = Id(); _lines.Add( $"#{mm}=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));" );
			var deg = Id(); _lines.Add( $"#{deg}=(CONVERSION_BASED_UNIT('DEGREE','DEG',.PLANE_ANGLE.,0.017453292519943295));" );
			var sr = Id(); _lines.Add( $"#{sr}=(SI_UNIT(.STERADIAN.,.SOLID_ANGLE.));" );
			var units = Id(); _lines.Add( $"#{units}=GLOBAL_UNIT_ASSIGNED_CONTEXT((#{mm},#{deg},#{sr}));" );
			var unc = Id(); _lines.Add( $"#{unc}=GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#{mm}),0.000001,'');" );

			// Shared EDGE_CURVEs first (faces reference them; dedup by BrepEdge index).
			for ( int i = 0; i < _solid.Edges.Count; i++ )
				_edgeCurves[i] = EdgeCurve( _solid.Edges[i] );
			int EdgeCurveId( BrepEdge e ) => _edgeCurves[_solid.Edges.IndexOf( e )];

			var faceIds = new List<int>();
			for ( int fi = 0; fi < _solid.Faces.Count; fi++ )
			{
				var f = _solid.Faces[fi];
				// Geppetto annotation: exact spans/area, so the reader restores them instead of guessing.
				_lines.Add( $"/* GEPPETTO_BREPFACE {fi} {f.Kind} U0={F( f.U0 )} U1={F( f.U1 )} V0={F( f.V0 )} V1={F( f.V1 )} Area={F( f.AreaMm2 )} Reversed={(f.Reversed ? 1 : 0)} */" );
				var bounds = new List<int>();
				var outer = FaceLoop( fi, inner: false );
				if ( outer.Count > 0 )
				{
					var loopId = EdgeLoop( outer.Select( t => OrientedEdge( EdgeCurveId( t.Edge ), t.Forward ) ) );
					var boundId = Id();
					_lines.Add( $"#{boundId}=FACE_OUTER_BOUND('',#{loopId},.T.);" );
					bounds.Add( boundId );
				}
				if ( f.InnerLoop is { Count: > 0 } )
				{
					var innerLoop = FaceLoop( fi, inner: true );
					if ( innerLoop.Count > 0 )
					{
						var loopId = EdgeLoop( innerLoop.Select( t => OrientedEdge( EdgeCurveId( t.Edge ), t.Forward ) ) );
						var boundId = Id();
						_lines.Add( $"#{boundId}=FACE_BOUND('',#{loopId},.F.);" );
						bounds.Add( boundId );
					}
				}
				else if ( f.Kind == BrepSurfaceKind.Torus )
				{
					var second = FaceLoop( fi, inner: true );
					if ( second.Count > 0 && second[0].Edge != (outer.Count > 0 ? outer[0].Edge : null) )
					{
						var loopId = EdgeLoop( second.Select( t => OrientedEdge( EdgeCurveId( t.Edge ), t.Forward ) ) );
						var boundId = Id();
						_lines.Add( $"#{boundId}=FACE_BOUND('',#{loopId},.F.);" );
						bounds.Add( boundId );
					}
				}
				var faceId = Id();
				_lines.Add( $"#{faceId}=ADVANCED_FACE('',({string.Join( ",", bounds.Select( b => "#" + b ) )}),#{Surface( f )},.T.);" );
				faceIds.Add( faceId );
			}

			var shellId = Id();
			_lines.Add( $"#{shellId}=CLOSED_SHELL('',({string.Join( ",", faceIds.Select( f => "#" + f ) )}));" );
			var brepId = Id();
			_lines.Add( $"#{brepId}=MANIFOLD_SOLID_BREP('{_name}',#{shellId});" );
			_shapeRep = Id();
			_lines.Add( $"#{_shapeRep}=ADVANCED_BREP_SHAPE_REPRESENTATION('{_name}',(#{brepId}),#{_context});" );
			_ = units; _ = unc;

			foreach ( var line in _lines ) sb.AppendLine( line );
			sb.AppendLine( "ENDSEC;" );
			sb.AppendLine( "END-ISO-10303-21;" );
			return sb.ToString();
		}
	}

	// ============================ reader ============================

	sealed class Reader
	{
		readonly string _text;
		readonly StepReadResult _result = new();
		readonly Dictionary<int, string> _types = new();
		readonly Dictionary<int, List<string>> _args = new();
		readonly Dictionary<int, (int Face, string Kind, double U0, double U1, double V0, double V1, double Area, bool Reversed)> _annotations = new();

		public Reader( string text ) { _text = text; }

		public StepReadResult Run()
		{
			try
			{
				Parse();
				Rebuild();
			}
			catch ( Exception ex )
			{
				_result.Errors.Add( $"step-read-failed: {ex.Message}" );
			}
			return _result;
		}

		static List<string> SplitTopLevel( string s )
		{
			var parts = new List<string>();
			int depth = 0; bool inStr = false; var cur = new StringBuilder();
			for ( int i = 0; i < s.Length; i++ )
			{
				var c = s[i];
				if ( inStr )
				{
					cur.Append( c );
					if ( c == '\'' )
					{
						if ( i + 1 < s.Length && s[i + 1] == '\'' ) { cur.Append( '\'' ); i++; }
						else inStr = false;
					}
					continue;
				}
				if ( c == '\'' ) { inStr = true; cur.Append( c ); continue; }
				if ( c == '(' ) depth++;
				if ( c == ')' ) depth--;
				if ( c == ',' && depth == 0 ) { parts.Add( cur.ToString().Trim() ); cur.Clear(); continue; }
				cur.Append( c );
			}
			if ( cur.Length > 0 ) parts.Add( cur.ToString().Trim() );
			return parts;
		}

		void Parse()
		{
			// Join physical lines into entities (one entity ends at ';'); capture Geppetto comments.
			var entities = new List<string>();
			var buf = new StringBuilder();
			string pendingComment = null;
			bool inData = false;
			foreach ( var raw in _text.Split( '\n' ) )
			{
				var line = raw.Trim();
				var comment = Regex.Match( line, @"/\*\s*GEPPETTO_BREPFACE\s+(.*?)\s*\*/" );
				if ( comment.Success ) { pendingComment = comment.Groups[1].Value; continue; }
				if ( line.StartsWith( "/*" ) ) continue;
				if ( line == "DATA;" ) { inData = true; continue; }
				if ( !inData ) continue; // HEADER section (FILE_DESCRIPTION/FILE_NAME/FILE_SCHEMA) is not entities
				if ( line.Length == 0 || line == "ENDSEC;" || line.StartsWith( "END-" ) ) continue;
				buf.Append( line );
				if ( line.EndsWith( ";" ) )
				{
					var entity = buf.ToString();
					buf.Clear();
					// Complex entity instance, e.g. the SI units tuple
					// (#2=(LENGTH_UNIT()NAMED_UNIT(*)SI_UNIT(.MILLI.,.METRE.));) — a known
					// unit declaration, not a skipped line.
					if ( Regex.IsMatch( entity, @"#\d+\s*=\s*\(" ) ) continue;
					var m = Regex.Match( entity, @"#(\d+)\s*=\s*([A-Z0-9_]+)\s*\((.*)\)\s*;", RegexOptions.Singleline );
					if ( !m.Success ) { _result.Warnings.Add( $"step-skipped-line: could not parse '{entity[..Math.Min( 80, entity.Length )]}'" ); continue; }
					var id = int.Parse( m.Groups[1].Value, CultureInfo.InvariantCulture );
					_types[id] = m.Groups[2].Value;
					_args[id] = SplitTopLevel( m.Groups[3].Value );
					if ( pendingComment is not null && m.Groups[2].Value == "ADVANCED_FACE" )
					{
						ParseAnnotation( id, pendingComment );
						pendingComment = null;
					}
				}
			}
			_ = entities;
		}

		void ParseAnnotation( int faceId, string comment )
		{
			// Format: "<index> <Kind> U0=.. U1=.. V0=.. V1=.. Area=.. Reversed=0/1"
			var parts = comment.Split( new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries );
			if ( parts.Length < 2 ) return;
			double Get( string key )
			{
				var p = parts.FirstOrDefault( x => x.StartsWith( key + "=", StringComparison.Ordinal ) );
				return p is null ? double.NaN : double.Parse( p[(key.Length + 1)..], CultureInfo.InvariantCulture );
			}
			var rev = parts.FirstOrDefault( x => x.StartsWith( "Reversed=", StringComparison.Ordinal ) );
			_annotations[faceId] = (int.Parse( parts[0], CultureInfo.InvariantCulture ), parts[1],
				Get( "U0" ), Get( "U1" ), Get( "V0" ), Get( "V1" ), Get( "Area" ), rev is not null && rev.EndsWith( "1" ));
		}

		static int Ref( string token )
		{
			token = token.Trim();
			if ( token.StartsWith( "#" ) ) return int.Parse( token[1..], CultureInfo.InvariantCulture );
			throw new FormatException( $"Expected an entity reference, got '{token}'" );
		}

		static double Num( string token ) => double.Parse( token.Trim(), CultureInfo.InvariantCulture );

		static bool Is( Dictionary<int, string> types, int id, string type ) => types.TryGetValue( id, out var t ) && t == type;

		Vec3 ReadPoint( int id )
		{
			// CARTESIAN_POINT('',(x,y,z)): args[0] is the '' name, args[1] the coordinates.
			var coords = SplitTopLevel( _args[id][1].Trim().Trim( '(', ')' ) );
			return new Vec3( (float)Num( coords[0] ), (float)Num( coords[1] ), (float)Num( coords[2] ) );
		}

		Vec3 ReadDirection( int id )
		{
			// DIRECTION('',(x,y,z)): args[0] is the '' name, args[1] the components.
			var coords = SplitTopLevel( _args[id][1].Trim().Trim( '(', ')' ) );
			return new Vec3( (float)Num( coords[0] ), (float)Num( coords[1] ), (float)Num( coords[2] ) ).Normal;
		}

		(int Location, int Axis, int Ref) ReadPlacement( int id )
		{
			// AXIS2_PLACEMENT_3D('',#location,#axis,#ref): args[0] is the '' name.
			var a = _args[id];
			return (Ref( a[1] ), Ref( a[2] ), a.Count > 3 && a[3] != "$" ? Ref( a[3] ) : -1);
		}

		void Rebuild()
		{
			var solidId = _types.FirstOrDefault( kv => kv.Value == "MANIFOLD_SOLID_BREP" ).Key;
			if ( solidId == 0 )
			{
				_result.Errors.Add( "step-no-solid: no MANIFOLD_SOLID_BREP found" );
				return;
			}
			var name = _args[solidId][0].Trim( '\'' );
			var shellId = Ref( _args[solidId][1] );
			var faceIds = SplitTopLevel( _args[shellId][1].Trim().Trim( '(', ')' ) ).Select( Ref ).ToList();

			var solid = new BrepSolid { Name = string.IsNullOrWhiteSpace( name ) ? "part" : name };
			var edgeCurveFaces = new Dictionary<int, List<int>>(); // edge-curve id -> face indices using it
			var edgeCurveTwiceSameFace = new HashSet<int>();

			for ( int fi = 0; fi < faceIds.Count; fi++ )
			{
				var face = ReadFace( faceIds[fi], fi, edgeCurveFaces, edgeCurveTwiceSameFace );
				if ( face is null ) continue;
				solid.Faces.Add( face );
			}

			// Edges from the EDGE_CURVEs, linked to the faces that bound them.
			var edgeCurveIds = _types.Where( kv => kv.Value == "EDGE_CURVE" ).Select( kv => kv.Key ).ToList();
			foreach ( var ecId in edgeCurveIds )
			{
				var edge = ReadEdge( ecId, edgeCurveFaces, edgeCurveTwiceSameFace );
				if ( edge is not null ) solid.Edges.Add( edge );
			}

			solid.VolumeMm3 = RecognizeVolume( solid );
			if ( double.IsNaN( solid.VolumeMm3 ) )
				_result.Warnings.Add( "step-volume-unrecognized: face combination does not match a known primitive; volume left unknown" );
			_result.Solid = solid;

			// Unknown entity types, reported once each.
			var known = new HashSet<string> { "GEOMETRIC_REPRESENTATION_CONTEXT", "GLOBAL_UNIT_ASSIGNED_CONTEXT", "GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT", "LENGTH_UNIT", "NAMED_UNIT", "SI_UNIT", "CONVERSION_BASED_UNIT", "CARTESIAN_POINT", "DIRECTION", "AXIS2_PLACEMENT_3D", "PLANE", "CYLINDRICAL_SURFACE", "CONICAL_SURFACE", "SPHERICAL_SURFACE", "TOROIDAL_SURFACE", "VERTEX_POINT", "LINE", "CIRCLE", "EDGE_CURVE", "ORIENTED_EDGE", "EDGE_LOOP", "FACE_OUTER_BOUND", "FACE_BOUND", "ADVANCED_FACE", "CLOSED_SHELL", "MANIFOLD_SOLID_BREP", "ADVANCED_BREP_SHAPE_REPRESENTATION" };
			foreach ( var t in _types.Values.Distinct().Where( t => !known.Contains( t ) ) )
				_result.Warnings.Add( $"step-unknown-entity: {t} present but not interpreted" );
			var problems = solid.Validate();
			foreach ( var p in problems ) _result.Warnings.Add( "step-validation: " + p );
		}

		BrepFace ReadFace( int faceId, int faceIndex, Dictionary<int, List<int>> edgeCurveFaces, HashSet<int> twiceSameFace )
		{
			var a = _args[faceId];
			var bounds = SplitTopLevel( a[1].Trim().Trim( '(', ')' ) ).Select( Ref ).ToList();
			var surfaceId = Ref( a[2] );
			var surfaceType = _types[surfaceId];
			var face = new BrepFace();
			var loopPts = new List<Vec3>();
			var innerPts = new List<Vec3>();
			bool firstBound = true;
			foreach ( var boundId in bounds )
			{
				var boundType = _types[boundId];
				var loopId = Ref( _args[boundId][1] );
				var oriented = SplitTopLevel( _args[loopId][1].Trim().Trim( '(', ')' ) ).Select( Ref ).ToList();
				var pts = new List<Vec3>();
				foreach ( var oeId in oriented )
				{
					var ecId = Ref( _args[oeId][3] );
					if ( !edgeCurveFaces.TryGetValue( ecId, out var list ) ) { list = new List<int>(); edgeCurveFaces[ecId] = list; }
					list.Add( faceIndex );
					pts.AddRange( EdgeCurvePoints( ecId ) );
					if ( oriented.Count( x => Ref( _args[x][3] ) == ecId ) > 1 ) twiceSameFace.Add( ecId );
				}
				if ( firstBound ) loopPts = pts; else innerPts = pts;
				firstBound = false;
				_ = boundType;
			}
			face.Loop = loopPts;
			if ( innerPts.Count > 0 ) face.InnerLoop = innerPts;

			switch ( surfaceType )
			{
				case "PLANE":
				{
					var (loc, ax, _) = ReadPlacement( Ref( _args[surfaceId][1] ) );
					face.Kind = BrepSurfaceKind.Plane;
					face.Origin = ReadPoint( loc );
					face.Axis = ReadDirection( ax );
					face.AreaMm2 = PlaneArea( face );
					break;
				}
				case "CYLINDRICAL_SURFACE":
				{
					var (loc, ax, _) = ReadPlacement( Ref( _args[surfaceId][1] ) );
					face.Kind = BrepSurfaceKind.Cylinder;
					face.Origin = ReadPoint( loc );
					face.Axis = ReadDirection( ax );
					face.Radius = Num( _args[surfaceId][2] );
					face.U0 = 0; face.U1 = 2 * Math.PI;
					DeriveCylinderSpan( face, bounds );
					face.AreaMm2 = face.Radius * (face.U1 - face.U0) * (face.V1 - face.V0);
					break;
				}
				case "CONICAL_SURFACE":
				{
					var (loc, ax, _) = ReadPlacement( Ref( _args[surfaceId][1] ) );
					face.Kind = BrepSurfaceKind.Cone;
					var baseCentre = ReadPoint( loc );
					var up = ReadDirection( ax );
					var r = Num( _args[surfaceId][2] );
					var semi = Num( _args[surfaceId][3] );
					var height = r / Math.Tan( semi );
					face.Origin = baseCentre + up * (float)height; // apex
					face.Axis = -up; // from apex toward the base, as the factory builds it
					face.Radius = r;
					face.Radius2 = semi;
					face.U0 = 0; face.U1 = 2 * Math.PI; face.V0 = 0; face.V1 = height;
					face.AreaMm2 = Math.Tan( semi ) / Math.Cos( semi ) * (face.U1 - face.U0) * (height * height) / 2.0;
					break;
				}
				case "SPHERICAL_SURFACE":
				{
					var (loc, ax, _) = ReadPlacement( Ref( _args[surfaceId][1] ) );
					face.Kind = BrepSurfaceKind.Sphere;
					face.Origin = ReadPoint( loc );
					face.Axis = ReadDirection( ax );
					face.Radius = Num( _args[surfaceId][2] );
					face.U0 = 0; face.U1 = 2 * Math.PI;
					DeriveSphereSpan( face );
					face.AreaMm2 = face.Radius * face.Radius * (face.U1 - face.U0) * (Math.Cos( face.V0 ) - Math.Cos( face.V1 ));
					break;
				}
				case "TOROIDAL_SURFACE":
				{
					var (loc, ax, _) = ReadPlacement( Ref( _args[surfaceId][1] ) );
					face.Kind = BrepSurfaceKind.Torus;
					face.Origin = ReadPoint( loc );
					face.Axis = ReadDirection( ax );
					face.Radius = Num( _args[surfaceId][2] );
					face.Radius2 = Num( _args[surfaceId][3] );
					face.U0 = 0; face.U1 = 2 * Math.PI; face.V0 = 0; face.V1 = 2 * Math.PI;
					face.AreaMm2 = (face.U1 - face.U0) * face.Radius2 * (face.Radius * (face.V1 - face.V0) + face.Radius2 * (Math.Sin( face.V1 ) - Math.Sin( face.V0 )));
					break;
				}
				default:
					_result.Warnings.Add( $"step-unknown-surface: {surfaceType} on face #{faceId} is not an analytic surface this reader builds" );
					return null;
			}
			if ( _annotations.TryGetValue( faceId, out var ann ) )
			{
				if ( double.IsFinite( ann.U0 ) ) face.U0 = ann.U0;
				if ( double.IsFinite( ann.U1 ) ) face.U1 = ann.U1;
				if ( double.IsFinite( ann.V0 ) ) face.V0 = ann.V0;
				if ( double.IsFinite( ann.V1 ) ) face.V1 = ann.V1;
				if ( double.IsFinite( ann.Area ) && ann.Area > 0 ) face.AreaMm2 = ann.Area;
				face.Reversed = ann.Reversed;
			}
			return face;
		}

		List<Vec3> EdgeCurvePoints( int ecId )
		{
			// EDGE_CURVE('',#v1,#v2,#curve,.T.): args[0] is the '' name.
			var a = _args[ecId];
			var pts = new List<Vec3>();
			var v1 = ReadPoint( Ref( _args[Ref( a[1] )][1] ) );
			var v2 = ReadPoint( Ref( _args[Ref( a[2] )][1] ) );
			pts.Add( v1 );
			if ( !v1.AlmostEquals( v2, 1e-3f ) ) pts.Add( v2 );
			return pts;
		}

		void DeriveCylinderSpan( BrepFace face, List<int> bounds )
		{
			// V span from the rim circles' centres projected on the axis.
			var axial = new List<double>();
			foreach ( var boundId in bounds )
			{
				var loopId = Ref( _args[boundId][1] );
				foreach ( var oeId in SplitTopLevel( _args[loopId][1].Trim().Trim( '(', ')' ) ).Select( Ref ) )
				{
					var ecId = Ref( _args[oeId][3] );
					if ( !Is( _types, ecId, "EDGE_CURVE" ) ) continue;
					var curveId = Ref( _args[ecId][3] ); // EDGE_CURVE curve is args[3], not args[2]
					if ( !Is( _types, curveId, "CIRCLE" ) ) continue;
					var (loc, _, _) = ReadPlacement( Ref( _args[curveId][1] ) );
					axial.Add( Vec3.Dot( ReadPoint( loc ) - face.Origin, face.Axis ) );
				}
			}
			if ( axial.Count >= 2 ) { face.V0 = axial.Min(); face.V1 = axial.Max(); }
			else { face.V0 = 0; face.V1 = 0; _result.Warnings.Add( "step-cylinder-span: rim circles missing, V span defaulted to 0" ); }
		}

		void DeriveSphereSpan( BrepFace face )
		{
			// Hemisphere from the loop's Newell normal against the axis: north when it aligns.
			if ( face.Loop.Count < 3 ) { face.V0 = 0; face.V1 = Math.PI; return; }
			double nx = 0, ny = 0, nz = 0;
			for ( int i = 0; i < face.Loop.Count; i++ )
			{
				var a = face.Loop[i]; var b = face.Loop[(i + 1) % face.Loop.Count];
				nx += (a.y - b.y) * (a.z + b.z);
				ny += (a.z - b.z) * (a.x + b.x);
				nz += (a.x - b.x) * (a.y + b.y);
			}
			var dot = nx * face.Axis.x + ny * face.Axis.y + nz * face.Axis.z;
			if ( dot >= 0 ) { face.V0 = 0; face.V1 = Math.PI / 2; }
			else { face.V0 = Math.PI / 2; face.V1 = Math.PI; }
		}

		double PlaneArea( BrepFace face )
		{
			if ( face.InnerLoop is { Count: > 0 } )
			{
				// Annulus: outer minus inner, from the rim radii (loop points' distance from the centre).
				double Outer( List<Vec3> pts ) => pts.Max( p => (p - face.Origin).Length );
				var ro = Outer( face.Loop );
				var ri = Outer( face.InnerLoop );
				return Math.PI * (ro * ro - ri * ri);
			}
			if ( face.Loop.Count == 0 ) return 0;
			// Polygon area via Newell (rectangles and sampled discs alike; discs use their rim radius).
			double nx = 0, ny = 0, nz = 0;
			for ( int i = 0; i < face.Loop.Count; i++ )
			{
				var a = face.Loop[i]; var b = face.Loop[(i + 1) % face.Loop.Count];
				nx += (a.y - b.y) * (a.z + b.z);
				ny += (a.z - b.z) * (a.x + b.x);
				nz += (a.x - b.x) * (a.y + b.y);
			}
			return 0.5 * Math.Sqrt( nx * nx + ny * ny + nz * nz );
		}

		BrepEdge ReadEdge( int ecId, Dictionary<int, List<int>> edgeCurveFaces, HashSet<int> twiceSameFace )
		{
			// EDGE_CURVE('',#v1,#v2,#curve,.T.): args[0] is the '' name, the curve is args[3].
			var a = _args[ecId];
			var curveId = Ref( a[3] );
			var curveType = _types[curveId];
			var v1 = ReadPoint( Ref( _args[Ref( a[1] )][1] ) );
			var v2 = ReadPoint( Ref( _args[Ref( a[2] )][1] ) );
			var edge = new BrepEdge { Start = v1, End = v2 };
			if ( curveType == "LINE" )
			{
				edge.Kind = BrepEdgeKind.Line;
			}
			else if ( curveType == "CIRCLE" )
			{
				edge.Kind = BrepEdgeKind.Circle;
				var (loc, ax, _) = ReadPlacement( Ref( _args[curveId][1] ) );
				edge.Center = ReadPoint( loc );
				edge.Axis = ReadDirection( ax );
				edge.Radius = Num( _args[curveId][2] );
				edge.Angle0 = 0; edge.Angle1 = 2 * Math.PI;
			}
			else
			{
				_result.Warnings.Add( $"step-unknown-curve: {curveType} on edge #{ecId} is not a line or circle" );
				return null;
			}
			if ( edgeCurveFaces.TryGetValue( ecId, out var faces ) )
			{
				var distinct = faces.Distinct().ToList();
				if ( distinct.Count >= 2 ) { edge.FaceA = distinct[0]; edge.FaceB = distinct[1]; }
				else if ( distinct.Count == 1 )
				{
					edge.FaceA = distinct[0];
					edge.FaceB = twiceSameFace.Contains( ecId ) ? distinct[0] : -1;
					edge.Seam = twiceSameFace.Contains( ecId );
				}
			}
			return edge;
		}

		double RecognizeVolume( BrepSolid solid )
		{
			var kinds = solid.Faces.Select( f => f.Kind ).ToList();
			int Count( BrepSurfaceKind k ) => kinds.Count( x => x == k );
			if ( kinds.Count == 6 && Count( BrepSurfaceKind.Plane ) == 6 )
			{
				var xs = solid.Faces.SelectMany( f => f.Loop ).Select( p => (double)p.x );
				var ys = solid.Faces.SelectMany( f => f.Loop ).Select( p => (double)p.y );
				var zs = solid.Faces.SelectMany( f => f.Loop ).Select( p => (double)p.z );
				return (xs.Max() - xs.Min()) * (ys.Max() - ys.Min()) * (zs.Max() - zs.Min());
			}
			if ( kinds.Count == 3 && Count( BrepSurfaceKind.Cylinder ) == 1 && Count( BrepSurfaceKind.Plane ) == 2 )
			{
				var cyl = solid.Faces.First( f => f.Kind == BrepSurfaceKind.Cylinder );
				return Math.PI * cyl.Radius * cyl.Radius * (cyl.V1 - cyl.V0);
			}
			if ( kinds.Count == 4 && Count( BrepSurfaceKind.Cylinder ) == 2 && Count( BrepSurfaceKind.Plane ) == 2 )
			{
				var cyls = solid.Faces.Where( f => f.Kind == BrepSurfaceKind.Cylinder ).OrderByDescending( f => f.Radius ).ToList();
				return Math.PI * (cyls[0].Radius * cyls[0].Radius - cyls[1].Radius * cyls[1].Radius) * (cyls[0].V1 - cyls[0].V0);
			}
			if ( kinds.Count == 2 && Count( BrepSurfaceKind.Cone ) == 1 && Count( BrepSurfaceKind.Plane ) == 1 )
			{
				var cone = solid.Faces.First( f => f.Kind == BrepSurfaceKind.Cone );
				return Math.PI * cone.Radius * cone.Radius * (cone.V1 - cone.V0) / 3.0;
			}
			if ( kinds.Count == 2 && Count( BrepSurfaceKind.Sphere ) == 2 )
			{
				var r = solid.Faces[0].Radius;
				return 4.0 / 3.0 * Math.PI * r * r * r;
			}
			if ( kinds.Count == 2 && Count( BrepSurfaceKind.Torus ) == 2 )
			{
				var t = solid.Faces[0];
				return 2 * Math.PI * Math.PI * t.Radius * t.Radius2 * t.Radius2;
			}
			return double.NaN;
		}
	}
}
