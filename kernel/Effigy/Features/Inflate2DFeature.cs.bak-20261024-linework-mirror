using System;
using System.Collections.Generic;
using System.Linq;

namespace Effigy;

/// <summary>Monster-Mash-style 2D-to-3D inflation, roadmap Lane O (<c>add Inflate2D</c>):
/// a closed .linework profile (or inline rings) becomes a closed solid.
///
///   add Inflate2D linework=star.linework layer=Sketch thickness=2 profile=Cushion symmetry=FlatBack
///   add Inflate2D rings="0,0;10,0;10,10;0,10|3,3;7,3;7,7;3,7" thickness=1.5 profile=Flat
///
/// Profiles come from LineworkReader: a .linework file path or drawing id, optionally one
/// layer by name/id. <c>rings=</c> is the file-free alternative in the ShadowBox ring
/// grammar (outer ring, then hole rings, '|' separated) — the same grammar the ShadowBox
/// linework= form will eventually feed from parsed profiles, so a rings= shape and a
/// .linework shape are the same kind of object.
///
/// HONEST SCOPE. This is extrusion with stepped bevel / cushion / ridge sections — NOT
/// the roadmap's full screened-Poisson elevation field over a constrained-Delaunay +
/// Steiner-grid interior. That field (smooth chordal-axis height, quad remesh) is a
/// separate, much larger build; what lands here is the solid a script can actually use
/// today: flat caps ear-clipped with holes (Triangulate.WithHoles), side walls between
/// sections, and profile steps that approximate puff (Cushion/Round), chamfer
/// (FlatBevel) and peak (SharpRidge) by scaling rings about their own centroids.
/// Centroid scaling is NOT a constant-distance offset: on sharp concave corners the
/// bevel thins unevenly, and a star's arms narrow toward the middle faster than its
/// notches do. It never self-intersects (a positive scale of a simple ring stays
/// simple), which a naive offset would not guarantee.
///
/// Symmetry: FlatBack extrudes from a flat back at z = 0 to the profiled front at
/// z = Thickness. Bilateral mirrors the profile so the widest section sits at
/// z = Thickness / 2 and both faces are profiled (plush / cushion); Flat and FlatBevel
/// are already symmetric, so they build identically under either setting.</summary>
public sealed class Inflate2DFeature : Feature
{
	public override string TypeName => "Inflate2D";
	public override GeometryKind Accepts => GeometryKind.None;

	public readonly StringParam Linework = new( "Linework", "" );
	public readonly StringParam Layer = new( "Layer", "" );
	public readonly StringParam Rings = new( "Rings", "" );
	public readonly FloatParam Thickness = new( "Thickness", 1f, 0.01f, 1000f, "u" );
	public readonly FloatParam Bevel = new( "Bevel", 0f, 0f, 1000f, "u" );
	public readonly ChoiceParam Profile = new( "Profile", new[] { "Flat", "FlatBevel", "Cushion", "Round", "SharpRidge" } );
	public readonly ChoiceParam Symmetry = new( "Symmetry", new[] { "FlatBack", "Bilateral" } );
	public readonly FloatParam Scale = new( "Scale", 1f, 0.001f, 1000f );
	public readonly IntParam Slot = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters =>
		new IParam[] { Linework, Layer, Rings, Thickness, Bevel, Profile, Symmetry, Scale, Slot };

	/// <summary>Profiles the last Execute built from, for tests and for callers that want
	/// to know what actually inflated (mirrored symmetry profiles included).</summary>
	public List<LineworkProfile> ProfilesUsed { get; } = new();

	protected override void Execute( FeatureContext ctx )
	{
		ProfilesUsed.Clear();
		var profiles = ResolveProfiles();
		if ( profiles.Count == 0 )
			Fail( "Inflate2D found no closed profile to inflate.",
				"Linework profiles are closed strokes; open strokes are drawing lines, not shapes.",
				"Close the shape in Linework (closed=true) or give rings= with a closed ring" );

		var thickness = Thickness.Clamped;
		var bevel = Bevel.Clamped;
		var scale = Scale.Clamped;
		var profileName = Profile.Options[Profile.Index];
		var bilateral = Symmetry.Options[Symmetry.Index] == "Bilateral";
		var slot = Slot.Clamped > 0 ? Slot.Clamped
			: ctx.Bodies.SelectMany( b => b.Mesh.Faces ).Select( f => f.Material )
				.Concat( ctx.ShellSlots ).DefaultIfEmpty( 0 ).Max() + 1;

		var scaled = profiles.Select( p => ScaleProfile( p, scale ) ).ToList();
		var mesh = Build( scaled, thickness, bevel, profileName, bilateral, slot );
		if ( mesh is null )
			Fail( "Inflate2D could not triangulate a profile cap.",
				"A hole ring could not be bridged into its outer ring — usually a hole touching or crossing the outer ring.",
				"Move the hole fully inside the outer ring with clear space around it" );

		ProfilesUsed.AddRange( scaled );
		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name ?? "Inflated linework", mesh ) );
	}

	List<LineworkProfile> ResolveProfiles()
	{
		if ( !string.IsNullOrWhiteSpace( Rings.Value ) )
		{
			if ( !LineworkReader.TryParseRings( Rings.Value, out var ringProfile, out var rerr ) )
				Fail( $"Inflate2D rings= does not parse: {rerr}",
					"The inline ring grammar is outer ring, then hole rings, '|' separated.",
					"Use e.g. rings=\"0,0;10,0;10,10;0,10|3,3;7,3;7,7;3,7\"" );
			return new List<LineworkProfile> { ringProfile };
		}
		if ( string.IsNullOrWhiteSpace( Linework.Value ) )
			Fail( "Inflate2D needs a source.",
				"Neither linework= nor rings= was given, so there is no shape to inflate.",
				"Give linework=<path or drawing id> or rings=<outer|holes>" );
		if ( !LineworkReader.TryLoad( Linework.Value, out var doc, out _, out var lerr ) )
			Fail( $"Inflate2D cannot load its drawing: {lerr}",
				"The path or drawing id did not resolve to a .linework file.",
				"Check the path, or copy the drawing into ~/.local/share/linework/drawings/" );
		var layer = Layer.Value.Trim();
		if ( layer.Length > 0 && !doc.Layers.Any( l => l.Name.Equals( layer, StringComparison.OrdinalIgnoreCase ) || l.Id.Equals( layer, StringComparison.OrdinalIgnoreCase ) ) )
			Fail( $"Inflate2D found no layer '{layer}'.",
				$"Layers in this drawing: {string.Join( ", ", doc.Layers.Select( l => l.Name ) )}.",
				"Give layer= with one of those names, or leave layer= empty for all visible layers" );
		return LineworkReader.Profiles( doc, layer );
	}

	static LineworkProfile ScaleProfile( LineworkProfile p, float scale )
	{
		if ( MathF.Abs( scale - 1f ) < 1e-6f ) return p;
		List<Vec2> ScaleRing( List<Vec2> ring ) => ring.Select( q => q * scale ).ToList();
		return new LineworkProfile
		{
			Layer = p.Layer,
			StrokeId = p.StrokeId,
			Outer = ScaleRing( p.Outer ),
			Holes = p.Holes.Select( ScaleRing ).ToList(),
		};
	}

	/// <summary>One extrusion section: a height fraction of the thickness and the factor
	/// each ring is scaled about its own centroid at that height.</summary>
	readonly struct Section
	{
		public readonly float Z;
		public readonly float Scale;
		public Section( float z, float scale ) { Z = z; Scale = scale; }
	}

	static List<Section> Sections( string profile, bool bilateral, float thickness, float bevel, float outerRadius )
	{
		var t = thickness;
		switch ( profile )
		{
			case "Flat":
				return new List<Section> { new( 0f, 1f ), new( t, 1f ) };
			case "FlatBevel":
			{
				var b = bevel > 0f ? MathF.Min( bevel, t * 0.45f ) : t * 0.1f;
				// Inset as a centroid scale: a bevel of width b on a shape of radius R
				// shrinks the end rings by b / R, clamped so they never vanish.
				var f = Math.Clamp( 1f - b / MathF.Max( 1e-3f, outerRadius ), 0.05f, 0.95f );
				return new List<Section> { new( 0f, f ), new( b, 1f ), new( t - b, 1f ), new( t, f ) };
			}
			case "Cushion":
			case "Round":
				if ( bilateral )
					return new List<Section>
					{
						new( 0f, 0.62f ), new( t * 0.125f, 0.72f ), new( t * 0.25f, 0.82f ),
						new( t * 0.375f, 0.92f ), new( t * 0.5f, 1f ), new( t * 0.625f, 0.92f ),
						new( t * 0.75f, 0.82f ), new( t * 0.875f, 0.72f ), new( t, 0.62f ),
					};
				return new List<Section>
				{
					new( 0f, 1f ), new( t * 0.25f, 0.92f ), new( t * 0.5f, 0.82f ),
					new( t * 0.75f, 0.72f ), new( t, 0.62f ),
				};
			case "SharpRidge":
				if ( bilateral )
					return new List<Section> { new( 0f, 0.15f ), new( t * 0.5f, 1f ), new( t, 0.15f ) };
				return new List<Section> { new( 0f, 1f ), new( t * 0.5f, 0.55f ), new( t, 0.15f ) };
			default:
				return new List<Section> { new( 0f, 1f ), new( t, 1f ) };
		}
	}

	static Vec2 Centroid( List<Vec2> ring )
	{
		var c = Vec2.Zero;
		foreach ( var p in ring ) c += p;
		return c / MathF.Max( 1, ring.Count );
	}

	static List<Vec2> ScaledRing( List<Vec2> ring, float scale )
	{
		if ( MathF.Abs( scale - 1f ) < 1e-6f ) return new List<Vec2>( ring );
		var c = Centroid( ring );
		return ring.Select( p => c + (p - c) * scale ).ToList();
	}

	static List<Vec2> Normalized( List<Vec2> ring, bool wantCCW )
	{
		var r = new List<Vec2>( ring );
		var ccw = LineworkReader.SignedArea( r ) > 0f;
		if ( ccw != wantCCW ) r.Reverse();
		return r;
	}

	/// <summary>Builds the closed solid for already-scaled profiles. Null when a cap
	/// cannot be triangulated (unbridgeable hole). Winding is normalized first: outer
	/// rings counter-clockwise, hole rings clockwise, so caps and walls all face out.</summary>
	public static PolyMesh Build( IReadOnlyList<LineworkProfile> profiles, float thickness, float bevel, string profile, bool bilateral, int material = 0 )
	{
		var positions = new List<Vec3>();
		var faces = new List<Face>();

		// Planar UVs over the union bounds, so a later texture-projection step has
		// something to project onto (roadmap step 4 of Inflate2D, not done here).
		var minX = float.MaxValue; var minY = float.MaxValue;
		var maxX = float.MinValue; var maxY = float.MinValue;
		foreach ( var p in profiles )
			foreach ( var q in p.Outer )
			{
				minX = MathF.Min( minX, q.x ); maxX = MathF.Max( maxX, q.x );
				minY = MathF.Min( minY, q.y ); maxY = MathF.Max( maxY, q.y );
			}
		var spanX = MathF.Max( 1e-6f, maxX - minX );
		var spanY = MathF.Max( 1e-6f, maxY - minY );
		Vec2 Uv( Vec2 q ) => new( (q.x - minX) / spanX, (q.y - minY) / spanY );

		foreach ( var profile_ in profiles )
		{
			var outer = Normalized( profile_.Outer, wantCCW: true );
			var holes = profile_.Holes.Select( h => Normalized( h, wantCCW: false ) ).ToList();
			var outerRadius = outer.Max( q => (q - Centroid( outer )).Length );
			var sections = Sections( profile, bilateral, thickness, bevel, outerRadius );

			// Per-section vertex blocks: outer points, then each hole's points, in order —
			// exactly the concatenated layout Triangulate.WithHoles indexes into.
			var blocks = new List<List<int>>();
			var blockRings = new List<List<List<Vec2>>>();
			foreach ( var s in sections )
			{
				var rings = new List<List<Vec2>> { ScaledRing( outer, s.Scale ) };
				rings.AddRange( holes.Select( h => ScaledRing( h, s.Scale ) ) );
				var block = new List<int>();
				foreach ( var ring in rings )
					foreach ( var q in ring )
					{
						positions.Add( new Vec3( q.x, q.y, s.Z ) );
						block.Add( positions.Count - 1 );
					}
				blocks.Add( block );
				blockRings.Add( rings );
			}
			// Per-section UVs, aligned with positions.
			var uvs = new List<Vec2>();
			foreach ( var rings in blockRings )
				foreach ( var ring in rings )
					foreach ( var q in ring )
						uvs.Add( Uv( q ) );

			void AddFace( int[] idx, bool flip )
			{
				if ( flip ) Array.Reverse( idx );
				faces.Add( new Face( idx, idx.Select( i => uvs[i] ).ToArray(), material ) );
			}

			// Caps: WithHoles triangles are wound for the +Z face; the back cap flips.
			for ( var cap = 0; cap < 2; cap++ )
			{
				var si = cap == 0 ? 0 : sections.Count - 1;
				var rings = blockRings[si];
				var tris = Triangulate.WithHoles( rings[0], rings.Skip( 1 ).ToList() );
				if ( tris is null || tris.Count == 0 ) return null;
				var block = blocks[si];
				foreach ( var (a, b, c) in tris )
					AddFace( new[] { block[a], block[b], block[c] }, flip: cap == 0 );
			}

			// Walls between consecutive sections, ring by ring. The same triangle
			// pattern faces outward for the CCW outer ring and the CW hole rings.
			for ( var si = 0; si + 1 < sections.Count; si++ )
			{
				var lo = blocks[si];
				var hi = blocks[si + 1];
				var offset = 0;
				foreach ( var ring in blockRings[si] )
				{
					var n = ring.Count;
					for ( var i = 0; i < n; i++ )
					{
						var a = offset + i;
						var b = offset + (i + 1) % n;
						AddFace( new[] { lo[a], lo[b], hi[b] }, flip: false );
						AddFace( new[] { lo[a], hi[b], hi[a] }, flip: false );
					}
					offset += n;
				}
			}
		}

		var mesh = new PolyMesh( positions, faces );
		return mesh;
	}

	/// <summary>Signed volume of a closed mesh (positive when faces wind outward).
	/// Used by tests to prove caps face the right way, not just that edges match.</summary>
	public static float SignedVolume( PolyMesh mesh )
	{
		var vol = 0f;
		foreach ( var f in mesh.Faces )
		{
			for ( var i = 1; i + 1 < f.Indices.Length; i++ )
			{
				var a = mesh.Positions[f.Indices[0]];
				var b = mesh.Positions[f.Indices[i]];
				var c = mesh.Positions[f.Indices[i + 1]];
				vol += Vec3.Dot( a, Vec3.Cross( b, c ) ) / 6f;
			}
		}
		return vol;
	}
}
