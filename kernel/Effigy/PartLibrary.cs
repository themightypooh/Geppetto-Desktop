using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>
/// Parametric parts for kitbashing: a cog, a bolt, a rivet, a hinge, a pipe elbow, a strap, a
/// buckle. Each is a few numbers, and each lands at a landmark. Composing named parts is a much
/// better fit for an agent than shaping a surface from nothing, and it is how a lot of hard
/// surface work is actually done by people too.
///
/// LATHED WHERE POSSIBLE. A bolt, a rivet and a knob are one profile turned about an axis, which
/// makes them a single closed manifold; the hinge and the buckle are appended pieces, so they are
/// one body with internal faces, the way a boolean Add leaves things (see Emit's comment in
/// SketchFeatures). Every part is built standing on z = 0 at the origin, pointing up, so
/// Position and Rotation on the feature mean what they say.
/// </summary>
public static class PartLibrary
{
	public static readonly string[] Names = { "Cog", "Bolt", "Hex bolt", "Rivet", "Knob", "Hinge", "Panel", "Pipe elbow", "Strap", "Buckle", "Louvre", "Grille", "Picatinny", "SpringCoil", "CableTie", "Flange", "ToggleSwitch", "HandleBar", "WeldBead", "Piston", "EyeBall", "ToothCone", "Claw", "ScalePlate", "SuckerDisc", "HornSpiral" };

	/// <summary>What each part reads from the dials, so a dialog and a script can say it.</summary>
	public static string Uses( string part ) => part switch
	{
		"Cog" => "Size = outer radius, Thickness = height, Count = teeth, Length = tooth depth",
		"Bolt" or "Hex bolt" => "Size = head radius, Length = shank length, Thickness = shank radius, Count = sides",
		"Rivet" => "Size = radius, Thickness = height, Count = sides",
		"Knob" => "Size = radius, Length = height, Count = sides",
		"Hinge" => "Size = knuckle radius, Length = hinge length, Thickness = leaf thickness, Count = knuckles, Angle = how far open",
		"Panel" => "Size = width, Length = height, Thickness = thickness",
		"Pipe elbow" => "Size = pipe radius, Length = bend radius, Angle = bend, Count = sides",
		"Strap" => "Size = width, Length = length, Thickness = thickness; along=SplineName lays it on that curve instead (Length is the curve's)",
		"Buckle" => "Size = width, Length = height, Thickness = bar thickness",
		"Louvre" => "Size = width, Length = height, Thickness = depth, Count = slats, Angle = slat tilt",
		"Grille" => "Size = width, Length = height, Thickness = bar depth, Count = vertical bars",
		"Picatinny" => "Size = rail width, Length = rail length, Thickness = rail height, Count = teeth",
		"SpringCoil" => "Size = coil radius, Length = coil height, Thickness = wire radius, Count = turns",
		"CableTie" => "Size = strap width, Length = strap length, Thickness = strap thickness",
		"Flange" => "Size = flange radius, Length = hub length, Thickness = flange thickness",
		"ToggleSwitch" => "Size = base radius, Length = lever length, Thickness = base thickness, Count = sides, Angle = lever tilt",
		"HandleBar" => "Size = tube radius, Length = bar width",
		"WeldBead" => "Size = bead radius, Length = seam length",
		"Piston" => "Size = barrel radius, Length = total length, Thickness = rod radius",
		"EyeBall" => "Size = radius, Thickness = iris radius, Count = sides",
		"ToothCone" => "Size = base radius, Length = height, Thickness = flatten, Angle = curve, Count = sides",
		"Claw" => "Size = base radius, Length = length, Thickness = flatten, Angle = curve, Count = sides",
		"ScalePlate" => "Size = half-width, Length = plate length, Thickness = dome height",
		"SuckerDisc" => "Size = radius, Thickness = cup depth",
		"HornSpiral" => "Size = base radius, Length = horn length, Angle = sweep, Count = growth rings and sides",
		_ => "",
	};

	public static PolyMesh Build( string part, float size, float length, float thickness, int count, float angle, int material = 0 )
	{
		size = MathF.Max( size, 0.01f );
		length = MathF.Max( length, 0.01f );
		thickness = MathF.Max( thickness, 0.01f );
		count = Math.Max( count, 3 );

		return part switch
		{
			"Cog" => Cog( size, thickness, count, length, material ),
			"Bolt" => Bolt( size, length, thickness, Math.Max( count, 8 ), material ),
			"Hex bolt" => Bolt( size, length, thickness, 6, material ),
			"Rivet" => Rivet( size, thickness, count, material ),
			"Knob" => Knob( size, length, count, material ),
			"Hinge" => Hinge( size, length, thickness, Math.Max( count, 2 ), angle, material ),
			"Panel" => Standing( Primitives.Box( size, thickness, length, material ), length ),
			"Pipe elbow" => PipeElbow( size, length, angle, count, material ),
			"Strap" => Standing( Primitives.Box( length, size, thickness, material ), thickness ),
			"Buckle" => Buckle( size, length, thickness, material ),
		"Louvre" => Louvre( size, length, thickness, count, angle, material ),
		"Grille" => Grille( size, length, thickness, count, material ),
		"Picatinny" => Picatinny( size, length, thickness, count, material ),
		"SpringCoil" => SpringCoil( size, length, thickness, count, material ),
		"CableTie" => CableTie( size, length, thickness, material ),
		"Flange" => Flange( size, length, thickness, material ),
		"ToggleSwitch" => ToggleSwitch( size, length, thickness, count, angle, material ),
		"HandleBar" => HandleBar( size, length, material ),
		"WeldBead" => WeldBead( size, length, material ),
		"Piston" => Piston( size, length, thickness, material ),
		"EyeBall" => EyeBall( size, thickness, count, material ),
		"ToothCone" => ToothCone( size, length, thickness, angle, count, material ),
		"Claw" => Claw( size, length, thickness, angle, count, material ),
		"ScalePlate" => ScalePlate( size, length, thickness, material ),
		"SuckerDisc" => SuckerDisc( size, thickness, material ),
		"HornSpiral" => HornSpiral( size, length, angle, count, Math.Max( count, 10 ), material ),
			_ => throw new ArgumentException( $"No part called '{part}'. Parts are {string.Join( ", ", Names )}.", nameof( part ) ),
		};
	}

	/// <summary>A centred primitive lifted to stand on z = 0.</summary>
	static PolyMesh Standing( PolyMesh mesh, float height )
	{
		MeshTransform.Apply( mesh, Xform.Translate( new Vec3( 0, 0, height * 0.5f ) ) );
		return mesh;
	}

	/// <summary>Lift or drop a mesh so its lowest vertex is on z = 0, for parts whose low point
	/// moves with their dials (an open hinge leaf, an elbow's tilted end).</summary>
	static PolyMesh Grounded( PolyMesh mesh )
	{
		if ( mesh.Positions.Count == 0 )
			return mesh;

		var minZ = mesh.Positions.Min( p => p.z );
		MeshTransform.Apply( mesh, Xform.Translate( new Vec3( 0, 0, -minZ ) ) );
		return mesh;
	}

	/// <summary>Rings of a profile turned about z: (radius, height) pairs, bottom first. A radius
	/// of zero at either end becomes a point via the cap.</summary>
	public static PolyMesh Lathe( IReadOnlyList<(float R, float Z)> profile, int sides, int material )
	{
		var rings = new List<List<Vec3>>();

		foreach ( var (r, z) in profile )
		{
			var ring = new List<Vec3>( sides );
			var radius = MathF.Max( r, 1e-4f );

			for ( var k = 0; k < sides; k++ )
			{
				var a = k * MathF.PI * 2f / sides;
				ring.Add( new Vec3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, z ) );
			}

			rings.Add( ring );
		}

		return Skinner.Skin( rings, capEnds: true, material );
	}

	/// <summary>A flat outline extruded up by <paramref name="height"/>, standing on z = 0.</summary>
	public static PolyMesh Extrude( IReadOnlyList<Vec2> outline, float height, int material )
	{
		var bottom = outline.Select( p => new Vec3( p.x, p.y, 0f ) ).ToList();
		var top = outline.Select( p => new Vec3( p.x, p.y, height ) ).ToList();
		return Skinner.Skin( new[] { bottom, top }, capEnds: true, material );
	}

	static PolyMesh Cog( float radius, float height, int teeth, float depth, int material )
	{
		depth = MathF.Min( MathF.Max( depth, radius * 0.05f ), radius * 0.6f );
		var inner = radius - depth;
		var outline = new List<Vec2>();

		// Four points per tooth: root, rise, crown, fall — a trapezoid tooth, which is what a
		// drawn cog looks like and what reads as one at any size.
		for ( var t = 0; t < teeth; t++ )
		{
			var a0 = t * MathF.PI * 2f / teeth;
			var step = MathF.PI * 2f / teeth;

			outline.Add( Polar( inner, a0 ) );
			outline.Add( Polar( inner, a0 + step * 0.35f ) );
			outline.Add( Polar( radius, a0 + step * 0.45f ) );
			outline.Add( Polar( radius, a0 + step * 0.80f ) );
		}

		return Extrude( outline, height, material );
	}

	static Vec2 Polar( float r, float a ) => new( MathF.Cos( a ) * r, MathF.Sin( a ) * r );

	static PolyMesh Bolt( float headRadius, float shankLength, float shankRadius, int sides, int material )
	{
		shankRadius = MathF.Min( shankRadius, headRadius * 0.9f );
		var headHeight = headRadius * 0.7f;

		return Lathe( new (float, float)[]
		{
			(shankRadius, 0f),
			(shankRadius, shankLength),
			(headRadius, shankLength),
			(headRadius, shankLength + headHeight * 0.85f),
			(headRadius * 0.85f, shankLength + headHeight),
		}, sides, material );
	}

	static PolyMesh Rivet( float radius, float height, int sides, int material )
	{
		// A dome: a quarter circle of profile, standing on a short cylinder.
		var profile = new List<(float, float)> { (radius, 0f), (radius, height * 0.25f) };

		for ( var i = 1; i <= 5; i++ )
		{
			var a = i / 5f * MathF.PI * 0.5f;
			profile.Add( (MathF.Cos( a ) * radius, height * 0.25f + MathF.Sin( a ) * height * 0.75f) );
		}

		return Lathe( profile, sides, material );
	}

	static PolyMesh Knob( float radius, float height, int sides, int material ) =>
		Lathe( new (float, float)[]
		{
			(radius * 0.8f, 0f),
			(radius * 0.8f, height * 0.15f),
			(radius * 0.6f, height * 0.2f),
			(radius * 0.6f, height * 0.5f),
			(radius, height * 0.6f),
			(radius, height * 0.9f),
			(radius * 0.7f, height),
		}, sides, material );

	static PolyMesh Hinge( float radius, float length, float leaf, int knuckles, float openDegrees, int material )
	{
		var mesh = new PolyMesh();
		var pitch = length / knuckles;
		var gap = pitch * 0.08f;

		// Knuckles along y, alternating leaves; the pin axis runs along y at the origin.
		for ( var k = 0; k < knuckles; k++ )
		{
			var barrel = Primitives.Cylinder( radius, pitch - gap, 12, material );
			MeshTransform.Apply( barrel, Xform.Rotate( new Vec3( 1, 0, 0 ), -MathF.PI * 0.5f ) );
			MeshTransform.Apply( barrel, Xform.Translate( new Vec3( 0, -length * 0.5f + k * pitch + gap * 0.5f + (pitch - gap) * 0.5f, 0 ) ) );
			MeshTransform.Append( mesh, barrel );
		}

		var leafWidth = radius * 4f;
		var open = openDegrees * MathF.PI / 180f;

		var left = Primitives.Box( leafWidth, length, leaf, material );
		MeshTransform.Apply( left, Xform.Translate( new Vec3( -leafWidth * 0.5f - radius * 0.6f, 0, 0 ) ) );
		MeshTransform.Append( mesh, left );

		var right = Primitives.Box( leafWidth, length, leaf, material );
		MeshTransform.Apply( right, Xform.Translate( new Vec3( leafWidth * 0.5f + radius * 0.6f, 0, 0 ) ) );
		MeshTransform.Apply( right, Xform.Rotate( new Vec3( 0, 1, 0 ), open ) );
		MeshTransform.Append( mesh, right );

		return Grounded( mesh );
	}

	static PolyMesh PipeElbow( float radius, float bendRadius, float degrees, int sides, int material )
	{
		bendRadius = MathF.Max( bendRadius, radius * 1.05f );
		var angle = Math.Clamp( degrees, 5f, 355f ) * MathF.PI / 180f;
		var stations = Math.Max( 4, (int)MathF.Ceiling( angle / (MathF.PI / 16f) ) );
		var path = new List<Vec3>();

		// From the origin, running along +x, bending up toward +z about a centre above the start.
		for ( var i = 0; i <= stations; i++ )
		{
			var a = angle * i / stations;
			path.Add( new Vec3( MathF.Sin( a ) * bendRadius, 0f, bendRadius - MathF.Cos( a ) * bendRadius ) );
		}

		return Grounded( SplineTube.Build( path, false, radius, radius, sides, 0f, material ) );
	}

	static PolyMesh Buckle( float width, float height, float bar, int material )
	{
		var mesh = new PolyMesh();
		bar = MathF.Min( bar, MathF.Min( width, height ) * 0.4f );

		void Piece( float sx, float sy, float x, float y )
		{
			var box = Primitives.Box( sx, sy, bar, material );
			MeshTransform.Apply( box, Xform.Translate( new Vec3( x, y, bar * 0.5f ) ) );
			MeshTransform.Append( mesh, box );
		}

		// A frame lying flat: two long bars along y, two short across x, plus the centre bar.
		Piece( bar, height, -width * 0.5f + bar * 0.5f, 0f );
		Piece( bar, height, width * 0.5f - bar * 0.5f, 0f );
		Piece( width - bar * 2f, bar, 0f, height * 0.5f - bar * 0.5f );
		Piece( width - bar * 2f, bar, 0f, -height * 0.5f + bar * 0.5f );
		Piece( width - bar * 2f, bar * 0.7f, 0f, 0f );

		return mesh;
	}
	static PolyMesh Louvre( float width, float height, float depth, int slats, float tiltDegrees, int material )
	{
		var mesh = new PolyMesh();
		var rail = depth;

		void Piece( PolyMesh piece, Vec3 at )
		{
			MeshTransform.Apply( piece, Xform.Translate( at ) );
			MeshTransform.Append( mesh, piece );
		}

		// Frame: two stiles, top and bottom rails, all standing on z = 0.
		Piece( Primitives.Box( rail, rail, height, material ), new Vec3( -width * 0.5f + rail * 0.5f, 0f, height * 0.5f ) );
		Piece( Primitives.Box( rail, rail, height, material ), new Vec3( width * 0.5f - rail * 0.5f, 0f, height * 0.5f ) );
		Piece( Primitives.Box( width, rail, rail, material ), new Vec3( 0f, 0f, rail * 0.5f ) );
		Piece( Primitives.Box( width, rail, rail, material ), new Vec3( 0f, 0f, height - rail * 0.5f ) );

		var innerH = height - rail * 2f;
		var tilt = tiltDegrees * MathF.PI / 180f;

		for ( var i = 0; i < slats; i++ )
		{
			var z = rail + innerH * (slats == 1 ? 0.5f : (i + 0.5f) / slats);
			var slat = Primitives.Box( width - rail * 2f, rail * 0.6f, innerH / Math.Max( 1, slats ) * 0.9f, material );
			MeshTransform.Apply( slat, Xform.Rotate( new Vec3( 1, 0, 0 ), tilt ) );
			Piece( slat, new Vec3( 0f, 0f, z ) );
		}

		return Grounded( mesh );
	}

	static PolyMesh Grille( float width, float height, float depth, int bars, int material )
	{
		var mesh = new PolyMesh();
		var bar = depth * 0.5f;

		void Piece( PolyMesh piece, Vec3 at )
		{
			MeshTransform.Apply( piece, Xform.Translate( at ) );
			MeshTransform.Append( mesh, piece );
		}

		Piece( Primitives.Box( width, bar, bar, material ), new Vec3( 0f, 0f, bar * 0.5f ) );
		Piece( Primitives.Box( width, bar, bar, material ), new Vec3( 0f, 0f, height - bar * 0.5f ) );
		Piece( Primitives.Box( bar, bar, height, material ), new Vec3( -width * 0.5f + bar * 0.5f, 0f, height * 0.5f ) );
		Piece( Primitives.Box( bar, bar, height, material ), new Vec3( width * 0.5f - bar * 0.5f, 0f, height * 0.5f ) );

		// Bar grate (the roadmap also names honeycomb; bars are the grate half of that
		// name, and a true hex honeycomb is a separate part, not a silent substitution).
		for ( var i = 1; i < bars; i++ )
		{
			var x = -width * 0.5f + width * i / bars;
			Piece( Primitives.Box( bar, bar, height - bar * 2f, material ), new Vec3( x, 0f, height * 0.5f ) );
		}

		var rows = Math.Max( 2, bars / 2 );

		for ( var i = 1; i < rows; i++ )
		{
			var z = height * i / rows;
			Piece( Primitives.Box( width - bar * 2f, bar, bar, material ), new Vec3( 0f, 0f, z ) );
		}

		return Grounded( mesh );
	}

	static PolyMesh Picatinny( float width, float length, float height, int teeth, int material )
	{
		var mesh = new PolyMesh();
		var baseH = height * 0.55f;
		var toothH = height - baseH;
		var pitch = length / Math.Max( 1, teeth );

		var bar = Primitives.Box( length, width, baseH, material );
		MeshTransform.Apply( bar, Xform.Translate( new Vec3( 0f, 0f, baseH * 0.5f ) ) );
		MeshTransform.Append( mesh, bar );

		for ( var i = 0; i < teeth; i++ )
		{
			var x = -length * 0.5f + pitch * (i + 0.5f);
			var tooth = Primitives.Box( pitch * 0.55f, width, toothH, material );
			MeshTransform.Apply( tooth, Xform.Translate( new Vec3( x, 0f, baseH + toothH * 0.5f ) ) );
			MeshTransform.Append( mesh, tooth );
		}

		return Grounded( mesh );
	}

	static PolyMesh SpringCoil( float radius, float length, float wire, int turns, int material )
	{
		var path = new List<Vec3>();
		var steps = Math.Max( 16, turns * 16 );

		for ( var i = 0; i <= steps; i++ )
		{
			var a = turns * MathF.PI * 2f * i / steps;
			path.Add( new Vec3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, length * i / steps ) );
		}

		return Grounded( SplineTube.Build( path, false, wire, wire, 8, 0f, material ) );
	}

	static PolyMesh CableTie( float width, float length, float thickness, int material )
	{
		var mesh = new PolyMesh();

		var strap = Primitives.Box( length, width, thickness, material );
		MeshTransform.Apply( strap, Xform.Translate( new Vec3( length * 0.5f, 0f, thickness * 0.5f ) ) );
		MeshTransform.Append( mesh, strap );

		var head = Primitives.Box( width * 1.8f, width * 1.3f, thickness * 2.6f, material );
		MeshTransform.Apply( head, Xform.Translate( new Vec3( length, 0f, thickness * 1.3f ) ) );
		MeshTransform.Append( mesh, head );

		return Grounded( mesh );
	}

	static PolyMesh Flange( float radius, float pipeLength, float thickness, int material ) =>
		Lathe( new (float, float)[]
		{
			(0.01f, 0f),
			(radius, 0f),
			(radius, thickness),
			(radius * 0.55f, thickness),
			(radius * 0.55f, thickness + pipeLength),
			(0.01f, thickness + pipeLength),
		}, 24, material );

	static PolyMesh ToggleSwitch( float radius, float leverLength, float baseThickness, int sides, float tiltDegrees, int material )
	{
		var mesh = new PolyMesh();

		var bas = Lathe( new (float, float)[]
		{
			(0.01f, 0f),
			(radius, 0f),
			(radius, baseThickness),
			(0.01f, baseThickness),
		}, Math.Max( sides, 12 ), material );
		MeshTransform.Append( mesh, bas );

		var leverR = radius * 0.32f;
		var lever = Lathe( new (float, float)[]
		{
			(0.01f, 0f),
			(leverR, 0f),
			(leverR * 0.85f, leverLength * 0.7f),
			(leverR * 0.5f, leverLength),
			(0.01f, leverLength),
		}, Math.Max( sides, 8 ), material );
		MeshTransform.Apply( lever, Xform.Translate( new Vec3( 0f, 0f, baseThickness ) ) );
		MeshTransform.Apply( lever, Xform.RotateAbout( new Vec3( 0f, 0f, baseThickness ), new Vec3( 1, 0, 0 ), tiltDegrees * MathF.PI / 180f ) );
		MeshTransform.Append( mesh, lever );

		return Grounded( mesh );
	}

	static PolyMesh HandleBar( float radius, float width, int material )
	{
		var rise = width * 0.18f;
		var back = width * 0.12f;
		var path = new List<Vec3>
		{
			new( -width * 0.5f, -back, 0f ),
			new( -width * 0.28f, 0f, rise ),
			new( 0f, 0f, rise ),
			new( width * 0.28f, 0f, rise ),
			new( width * 0.5f, -back, 0f ),
		};

		var mesh = SplineTube.Build( path, false, radius, radius, 10, 0f, material );

		// Grip sleeves over both ends, fatter than the bar itself.
		var left = SplineTube.Build( new List<Vec3> { path[0], path[1] }, false, radius * 1.45f, radius * 1.45f, 10, 0f, material );
		MeshTransform.Append( mesh, left );
		var right = SplineTube.Build( new List<Vec3> { path[3], path[4] }, false, radius * 1.45f, radius * 1.45f, 10, 0f, material );
		MeshTransform.Append( mesh, right );

		return Grounded( mesh );
	}

	static PolyMesh WeldBead( float radius, float length, int material )
	{
		var mesh = new PolyMesh();
		var count = Math.Max( 3, (int)MathF.Ceiling( length / (radius * 1.1f) ) );

		for ( var i = 0; i < count; i++ )
		{
			var x = count == 1 ? length * 0.5f : length * i / (count - 1);
			var bead = Primitives.QuadSphere( radius, 2, material );
			MeshTransform.Apply( bead, Xform.Scale( new Vec3( 1f, 1f, 0.62f ) ) );
			MeshTransform.Apply( bead, Xform.Translate( new Vec3( x, 0f, radius * 0.62f ) ) );
			MeshTransform.Append( mesh, bead );
		}

		return Grounded( mesh );
	}

	static PolyMesh Piston( float barrelRadius, float totalLength, float rodRadius, int material )
	{
		var mesh = new PolyMesh();
		var barrelH = totalLength * 0.6f;

		var barrel = Lathe( new (float, float)[]
		{
			(0.01f, 0f),
			(barrelRadius, 0f),
			(barrelRadius, barrelH),
			(0.01f, barrelH),
		}, 20, material );
		MeshTransform.Append( mesh, barrel );

		var rod = Lathe( new (float, float)[]
		{
			(0.01f, barrelH * 0.5f),
			(rodRadius, barrelH * 0.5f),
			(rodRadius, totalLength),
			(0.01f, totalLength),
		}, 16, material );
		MeshTransform.Append( mesh, rod );

		return mesh;
	}

	static PolyMesh EyeBall( float radius, float irisRadius, int sides, int material )
	{
		var mesh = new PolyMesh();
		var r = radius;

		// Sclera: a lathed sphere standing on z = 0, centre at z = r.
		var profile = new List<(float, float)>();

		for ( var i = 0; i <= 8; i++ )
		{
			var a = -MathF.PI * 0.5f + i / 8f * MathF.PI;
			profile.Add( (MathF.Max( 0.01f, r * MathF.Cos( a ) ), r + r * MathF.Sin( a )) );
		}

		MeshTransform.Append( mesh, Lathe( profile, Math.Max( sides, 16 ), material ) );

		// Iris: a shallow disc set into the top pole, slightly proud of the sphere.
		var irisTop = r + MathF.Sqrt( MathF.Max( 0.01f, r * r - irisRadius * irisRadius ) );
		var iris = Lathe( new (float, float)[]
		{
			(0.01f, irisTop - r * 0.08f),
			(irisRadius, irisTop - r * 0.08f),
			(irisRadius, irisTop + r * 0.04f),
			(0.01f, irisTop + r * 0.04f),
		}, Math.Max( sides, 16 ), material );
		MeshTransform.Append( mesh, iris );

		// Cornea: a squashed sphere cap over the iris, convex and proud of it.
		var cornea = Primitives.QuadSphere( irisRadius * 0.95f, 2, material );
		MeshTransform.Apply( cornea, Xform.Scale( new Vec3( 1f, 1f, 0.45f ) ) );
		MeshTransform.Apply( cornea, Xform.Translate( new Vec3( 0f, 0f, irisTop + r * 0.02f ) ) );
		MeshTransform.Append( mesh, cornea );

		return Grounded( mesh );
	}

	static PolyMesh RingLoft( List<List<Vec3>> rings, int material ) =>
		Skinner.Skin( rings, capEnds: true, material );

	static PolyMesh ToothCone( float baseRadius, float length, float flatten, float curveDegrees, int sides, int material )
	{
		var rings = new List<List<Vec3>>();
		var stations = 9;
		var tipOffset = length * (curveDegrees / 90f) * 0.5f;

		for ( var i = 0; i < stations; i++ )
		{
			var f = i / (float)(stations - 1);
			var radius = baseRadius * MathF.Pow( MathF.Max( 0f, 1f - f ), 0.8f );
			radius *= 1f + 0.4f * MathF.Max( 0f, 1f - f / 0.3f );
			radius = MathF.Max( radius, 0.01f );

			var ring = new List<Vec3>();

			for ( var k = 0; k < sides; k++ )
			{
				var a = k * MathF.PI * 2f / sides;
				ring.Add( new Vec3( tipOffset * f * f + MathF.Cos( a ) * radius, MathF.Sin( a ) * radius * flatten, f * length ) );
			}

			rings.Add( ring );
		}

		return RingLoft( rings, material );
	}

	static PolyMesh Claw( float baseRadius, float length, float flatten, float curveDegrees, int sides, int material )
	{
		var rings = new List<List<Vec3>>();
		var stations = 9;
		var tipOffset = length * (0.3f + curveDegrees / 90f);

		for ( var i = 0; i < stations; i++ )
		{
			var f = i / (float)(stations - 1);
			var radius = baseRadius * MathF.Pow( MathF.Max( 0f, 1f - f ), 1.2f );
			radius *= 1f + 0.25f * MathF.Max( 0f, 1f - f / 0.3f );
			radius = MathF.Max( radius, 0.01f );

			var ring = new List<Vec3>();

			for ( var k = 0; k < sides; k++ )
			{
				var a = k * MathF.PI * 2f / sides;
				// Ventral blood groove, approximated as a pinch on the -y side of each
				// ring: a true channelled groove would need a second surface, and this
				// reads as the groove at kitbash scale without doubling the part.
				var pinch = 1f - 0.3f * MathF.Max( 0f, -MathF.Sin( a ) ) * (1f - f * 0.5f);
				ring.Add( new Vec3( tipOffset * f * f + MathF.Cos( a ) * radius * pinch, MathF.Sin( a ) * radius * flatten * pinch, f * length ) );
			}

			rings.Add( ring );
		}

		return RingLoft( rings, material );
	}

	static PolyMesh ScalePlate( float radius, float length, float domeHeight, int material )
	{
		var plate = Lathe( new (float, float)[]
		{
			(0.01f, 0f),
			(radius, 0f),
			(radius * 0.8f, domeHeight * 0.7f),
			(radius * 0.4f, domeHeight),
			(0.01f, domeHeight),
		}, 20, material );

		// Elongate along x: Length is the plate's long side, Size its half-width.
		MeshTransform.Apply( plate, Xform.Scale( new Vec3( MathF.Max( length, 0.01f ) / (2f * radius), 1f, 1f ) ) );
		return plate;
	}

	static PolyMesh SuckerDisc( float radius, float cupDepth, int material ) =>
		Lathe( new (float, float)[]
		{
			(0.01f, 0f),
			(radius, 0f),
			(radius, cupDepth * 0.4f),
			(radius * 0.9f, cupDepth),
			(radius * 0.6f, cupDepth * 0.9f),
			(radius * 0.55f, cupDepth * 0.35f),
			(0.01f, cupDepth * 0.35f),
		}, 24, material );

	static PolyMesh HornSpiral( float baseRadius, float length, float sweepDegrees, int rings, int sides, int material )
	{
		var stations = 13;
		var sweep = sweepDegrees * MathF.PI / 180f;
		var ringsOut = new List<List<Vec3>>();

		for ( var i = 0; i < stations; i++ )
		{
			var f = i / (float)(stations - 1);
			var centre = new Vec3( MathF.Sin( f * sweep ) * length * 0.45f, 0f, f * length );
			var radius = baseRadius * MathF.Pow( MathF.Max( 0f, 1f - f ), 0.7f );
			radius *= 1f + 0.18f * MathF.Sin( f * rings * MathF.PI * 2f );
			radius = MathF.Max( radius, 0.01f );

			var ring = new List<Vec3>();

			for ( var k = 0; k < sides; k++ )
			{
				var a = k * MathF.PI * 2f / sides;
				ring.Add( centre + new Vec3( MathF.Cos( a ) * radius, MathF.Sin( a ) * radius, 0f ) );
			}

			ringsOut.Add( ring );
		}

		return RingLoft( ringsOut, material );
	}
}

/// <summary>A part from the library, placed. See <see cref="PartLibrary"/> for what each dial means.</summary>
public sealed class PartFeature : Feature
{
	public override string TypeName => "Part";

	public readonly ChoiceParam Part = new( "Part", PartLibrary.Names );
	public readonly FloatParam Size = new( "Size", 4f, 0.01f, unit: "u" );
	public readonly FloatParam Length = new( "Length", 8f, 0.01f, unit: "u" );
	public readonly FloatParam Thickness = new( "Thickness", 1f, 0.01f, unit: "u" );
	public readonly IntParam Count = new( "Count", 12, 3, 128 );
	public readonly FloatParam Angle = new( "Angle", 90f, 0f, 360f, unit: "deg" );
	public readonly Vec3Param Position = new( "Position", Vec3.Zero );
	public readonly Vec3Param RotationAxis = new( "Rotation axis", new Vec3( 0, 0, 1 ) );
	public readonly FloatParam RotationAngle = new( "Rotation", 0f, unit: "deg" );
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	/// <summary>
	/// Strap only: a Spline feature to lay the strap along, its face following the curve. A strap
	/// is never straight on the thing it holds; a box bent afterwards has no stations to bend at.
	/// The curve is in model space, so Position and Rotation do not move a strap on one.
	/// </summary>
	public string Along = "";

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Part, Size, Length, Thickness, Count, Angle, Position, RotationAxis, RotationAngle, Material };

	public override IReadOnlyList<IParam> AdvancedParameters => new IParam[] { RotationAxis, Material };

	protected override void Execute( FeatureContext ctx )
	{
		if ( !string.IsNullOrEmpty( Along ) )
		{
			if ( Part.Value != "Strap" )
				Fail( $"Only a strap follows a curve; a {Part.Value} does not.",
					"along= lays a Strap on a Spline.",
					"Set part=Strap", "Or remove along=" );

			if ( !ctx.Splines.TryGetValue( Along, out var spline ) )
				Fail( "The strap's curve is not above it.",
					$"along= names '{Along}', which is not a Spline feature that has run before this one.",
					"Add the Spline first (tube=0 keeps it a bare path)", "Check the name in along=" );

			var path = spline.Sample( 8 );
			if ( path.Count < 2 )
				Fail( "The strap's curve has fewer than two points.", "A strap needs a path to lie on.", "Give the Spline two or more points" );

			var band = SplineBand.Build( path, spline.Closed, Size.Clamped, Size.Clamped, Thickness.Clamped, Vec3.Zero, 0f, Material.Clamped );
			ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? Part.Value, band ) );
			return;
		}

		var mesh = PartLibrary.Build( Part.Value, Size.Clamped, Length.Clamped, Thickness.Clamped, Count.Clamped, Angle.Clamped, Material.Clamped );

		if ( RotationAngle.Value != 0f && RotationAxis.Value.LengthSquared > 1e-8f )
			MeshTransform.Apply( mesh, Xform.Rotate( RotationAxis.Value.Normal, RotationAngle.Value * MathF.PI / 180f ) );

		if ( Position.Value.LengthSquared > 0f )
			MeshTransform.Apply( mesh, Xform.Translate( Position.Value ) );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? Part.Value, mesh ) );
	}
}
