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

	public FocusLayer()
	{
		Material = new LiveMaterial( Teal, Roughness: .9f, Emission: new Vector3( .12f, .55f, .42f ), Alpha: .38f, Blend: true );
		Visible = false;
	}

	public override bool Dynamic => false;
	public IReadOnlyList<int> Bodies => _bodies;

	/// <summary>Light these bodies of <paramref name="picker"/> (none clears it). Bind pose: live previews
	/// carry no player.</summary>
	public void Set( MeshPicker picker, IEnumerable<int> bodies )
	{
		_bodies.Clear();
		_tris.Clear();
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
