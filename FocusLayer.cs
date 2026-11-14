using System;
using System.Collections.Generic;
using System.Numerics;

namespace Marionette.Desktop;

/// <summary>The chest's "part it's on": the body a live run's step just touched, washed in a soft
/// emissive teal over its whole surface so it reads at a glance without hiding the shape. Nudged
/// toward the eye like the selection fill, so it wins against the surface it lies on.</summary>
public sealed class FocusLayer : LiveLayer
{
	public static readonly Vector3 Teal = new( .25f, .95f, .75f );

	readonly List<int> _bodies = new();
	readonly List<Vector3> _tris = new();

	public FocusLayer() : this( Teal, new Vector3( .12f, .55f, .42f ), .38f ) { }

	readonly Vector3 _color, _glow;
	/// <summary>A wash in any colour (the step flash: green for a new part, amber for an edited one).</summary>
	public FocusLayer( Vector3 color, Vector3 glow, float alpha )
	{
		_color = color; _glow = glow;
		Fade( alpha );
		Visible = false;
	}

	/// <summary>Set the wash's strength (0..1 of its own alpha); the step flash fades with this.</summary>
	public void Fade( float alpha ) =>
		Material = new LiveMaterial( _color, Roughness: .9f, Emission: _glow * Math.Clamp( alpha * 2.2f, 0f, 1.5f ), Alpha: Math.Clamp( alpha, 0f, 1f ), Blend: true );

	public override bool Dynamic => false;
	public IReadOnlyList<int> Bodies => _bodies;

	/// <summary>Edges of the lit bodies with the normals of the faces either side (a lone face gives the same one twice),
	/// for the outline: a rim, a crease, or a silhouette from where the eye is.</summary>
	public readonly record struct Edge( Vector3 A, Vector3 B, Vector3 N1, Vector3 N2, bool Open );
	Edge[] _edges;
	const int OutlineTriBudget = 250_000;
	public Edge[] Edges
	{
		get
		{
			if ( _edges is not null ) return _edges;
			if ( _tris.Count / 3 > OutlineTriBudget ) return _edges = Array.Empty<Edge>();
			static (long, long, long) Q( Vector3 v ) => ((long)MathF.Round( v.X * 2000 ), (long)MathF.Round( v.Y * 2000 ), (long)MathF.Round( v.Z * 2000 ));
			var seen = new Dictionary<((long, long, long), (long, long, long)), (Vector3 a, Vector3 b, Vector3 n, int count, Vector3 n2)>();
			for ( int i = 0; i + 2 < _tris.Count; i += 3 )
			{
				var n = LiveBuilder.SafeNormalize( Vector3.Cross( _tris[i + 1] - _tris[i], _tris[i + 2] - _tris[i] ), Vector3.UnitZ );
				for ( int e = 0; e < 3; e++ )
				{
					Vector3 a = _tris[i + e], b = _tris[i + (e + 1) % 3];
					var qa = Q( a ); var qb = Q( b );
					var key = qa.CompareTo( qb ) <= 0 ? (qa, qb) : (qb, qa);
					if ( seen.TryGetValue( key, out var old ) ) seen[key] = (old.a, old.b, old.n, old.count + 1, n);
					else seen[key] = (a, b, n, 1, n);
				}
			}
			var list = new List<Edge>();
			foreach ( var v in seen.Values )
			{
				// an edge between two near-coplanar triangles (the diagonal of a flat face) is not an outline
				if ( v.count == 2 && Vector3.Dot( v.n, v.n2 ) > .999f ) continue;
				list.Add( new Edge( v.a, v.b, v.n, v.n2, v.count == 1 ) );
			}
			return _edges = list.ToArray();
		}
	}

	/// <summary>Light these bodies of <paramref name="picker"/> (none clears it). Bind pose: live previews
	/// carry no player.</summary>
	public void Set( MeshPicker picker, IEnumerable<int> bodies )
	{
		_bodies.Clear();
		_tris.Clear();
		_edges = null;
		if ( picker is not null )
			foreach ( int body in bodies )
			{
				if ( body < 0 || body >= picker.Count || _bodies.Contains( body ) ) continue;
				_bodies.Add( body );
				picker.Triangles( body, ReadOnlySpan<Matrix4x4>.Empty, ( a, b, c ) => { _tris.Add( a ); _tris.Add( b ); _tris.Add( c ); } );
			}
		Visible = _tris.Count > 0;
	}

	/// <summary>Which bodies a step's part name means: the body of that name (or id), else every body whose
	/// name starts with it (an Import's pieces), ignoring case.</summary>
	public static List<int> Match( CpuMesh.BodyPickEntry[] picks, string name )
	{
		var found = new List<int>();
		if ( string.IsNullOrWhiteSpace( name ) || picks is null ) return found;
		if ( name.Contains( ',' ) )
		{
			// "Suit / boot_L,Suit / boot_R": the bodies a step works on, all of them
			foreach ( var one in name.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
				foreach ( int i in Match( picks, one ) )
					if ( !found.Contains( i ) ) found.Add( i );
			return found;
		}
		name = name.Trim();
		for ( int i = 0; i < picks.Length; i++ )
			if ( string.Equals( picks[i].Name, name, StringComparison.OrdinalIgnoreCase ) || string.Equals( picks[i].Id, name, StringComparison.OrdinalIgnoreCase ) )
				found.Add( i );
		if ( found.Count == 0 )
			for ( int i = 0; i < picks.Length; i++ )
				if ( (picks[i].Name ?? "").StartsWith( name, StringComparison.OrdinalIgnoreCase ) ) found.Add( i );
		return found;
	}

	public override void Emit( LiveBuilder b, in LiveView view )
	{
		for ( int i = 0; i + 2 < _tris.Count; i += 3 )
		{
			Vector3 a = _tris[i], c1 = _tris[i + 1], c2 = _tris[i + 2];
			var n = LiveBuilder.SafeNormalize( Vector3.Cross( c1 - a, c2 - a ), Vector3.UnitZ );
			// a hair toward the eye, in proportion to distance (what SelectionLayer's Nudge does)
			var eye = view.Eye;
			b.Triangle( a + (eye - a) * .002f, c1 + (eye - c1) * .002f, c2 + (eye - c2) * .002f, n, n, n );
		}
	}
}


/// <summary>The outline of a <see cref="FocusLayer"/>'s bodies, in one colour: the same stroked line the selection
/// draws. Shows rims and creases always, and the silhouette from where the eye is.</summary>
public sealed class FocusOutline : LiveLayer
{
	readonly FocusLayer _from;
	readonly Vector3 _color;
	public float Width { get; set; } = .012f;

	public FocusOutline( FocusLayer from, Vector3 color )
	{
		_from = from; _color = color; Visible = false;
		Fade( 1f );
	}

	public void Fade( float alpha ) =>
		Material = new LiveMaterial( _color, Roughness: .8f, Emission: _color * 2.5f * Math.Clamp( alpha, 0f, 1f ), Alpha: Math.Clamp( alpha, 0f, 1f ), Blend: true );

	/// <summary>Faces the eye from different sides, so it moves as the camera does.</summary>
	public override bool Dynamic => true;

	public override void Emit( LiveBuilder b, in LiveView view )
	{
		if ( !_from.Visible ) return;
		foreach ( var e in _from.Edges )
		{
			bool draw = e.Open;
			if ( !draw )
			{
				var toEye = view.Eye - (e.A + e.B) * .5f;
				draw = Vector3.Dot( e.N1, toEye ) > 0 != Vector3.Dot( e.N2, toEye ) > 0 || Vector3.Dot( e.N1, e.N2 ) < .5f;
			}
			if ( draw ) SelectionLayer.Stroke( b, view, e.A, e.B, Width );
		}
	}
}
