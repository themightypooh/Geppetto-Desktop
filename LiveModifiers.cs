using System;
using System.Collections.Generic;
using System.Linq;
using Effigy;

namespace Marionette.Desktop;

/// <summary>R9 item 2: one entry of the live modifier stack. Takes a mesh it may mutate (the stack
/// hands each modifier its own copy) and returns the result, which may be a new mesh.</summary>
public abstract class MeshModifier
{
	public bool Enabled { get; set; } = true;
	public abstract string Name { get; }
	public abstract PolyMesh Apply( PolyMesh mesh );
}

/// <summary>Blender's Subdivision Surface: Catmull-Clark, skin weights interpolated through the
/// subdivision map so a rigged mesh stays rigged.</summary>
public sealed class SubdivisionModifier : MeshModifier
{
	public int Levels { get; set; } = 1;
	public override string Name => $"subdivision x{Levels}";
	public override PolyMesh Apply( PolyMesh mesh )
	{
		for ( int level = 0; level < Math.Clamp( Levels, 0, 4 ); level++ )
		{
			var (next, map) = CatmullClark.SubdivideWithMap( mesh );
			if ( mesh.IsRigged )
			{
				var skin = new SkinWeights( map.OutputVertexCount );
				for ( int v = 0; v < map.Vertices.Length; v++ )
				{
					var o = map.Vertices[v];
					skin[v] = o.Origin switch
					{
						SubdivisionOrigin.Original => mesh.Skin[o.A],
						SubdivisionOrigin.Edge => SkinWeights.Blend( (mesh.Skin[o.A], .5f), (mesh.Skin[o.B], .5f) ),
						_ => FaceBlend( mesh, o.A ),
					};
				}
				next.Skin = skin;
			}
			mesh = next;
		}
		return mesh;
	}

	static BoneWeight[] FaceBlend( PolyMesh mesh, int face )
	{
		var idx = mesh.Faces[face].Indices;
		var terms = new (BoneWeight[], float)[idx.Length];
		for ( int i = 0; i < idx.Length; i++ ) terms[i] = ( mesh.Skin[idx[i]], 1f / idx.Length );
		return SkinWeights.Blend( terms );
	}
}

/// <summary>Blender's Decimate (collapse): quadric decimation to a ratio; skin is carried by the kernel.</summary>
public sealed class DecimateModifier : MeshModifier
{
	public float Ratio { get; set; } = .5f;
	public override string Name => $"decimate {Ratio:0.##}";
	public override PolyMesh Apply( PolyMesh mesh ) => Decimate.ToRatio( mesh, Math.Clamp( Ratio, .01f, 1f ) );
}

/// <summary>RetopoService: triangle pairing into quads (optionally pre-decimated), skin preserved.</summary>
public sealed class RetopoModifier : MeshModifier
{
	public RetopoService.Options Options { get; set; } = new();
	public override string Name => "retopo";
	public override PolyMesh Apply( PolyMesh mesh ) => RetopoService.Retopo( mesh, Options ).Mesh;
}

/// <summary>Blender's Smooth / Laplacian Smooth family via the kernel's mesh filters.</summary>
public sealed class SmoothModifier : MeshModifier
{
	public MeshFilterKind Kind { get; set; } = MeshFilterKind.SurfaceSmooth;
	public float Strength { get; set; } = .5f;
	public int Iterations { get; set; } = 4;
	/// <summary>Optional per-vertex mask (vertex group); null = whole mesh.</summary>
	public Func<PolyMesh, float[]> Mask { get; set; }
	public override string Name => $"{Kind.ToString().ToLowerInvariant()} x{Iterations}";
	public override PolyMesh Apply( PolyMesh mesh )
	{
		MeshFilter.Apply( mesh, Kind, Strength, Iterations, Mask?.Invoke( mesh ) );
		return mesh;
	}
}

/// <summary>Blender's Displace along vertex normals, scaled by a vertex group. <see cref="BoneGroup"/>
/// uses a bone's skin influence as the group (the WeightPaintService view of the weights).</summary>
public sealed class DisplaceModifier : MeshModifier
{
	public float Distance { get; set; }
	public Func<PolyMesh, float[]> Weights { get; set; }
	public override string Name => $"displace {Distance:0.###}";

	public static Func<PolyMesh, float[]> BoneGroup( int bone ) =>
		m => m.Skin is null ? null : WeightPaintService.Influence( m.Skin, bone );

	public override PolyMesh Apply( PolyMesh mesh )
	{
		var weights = Weights?.Invoke( mesh );
		var normals = SkinStrain.VertexNormals( mesh.Positions, mesh.Faces );
		for ( int i = 0; i < mesh.Positions.Count; i++ )
		{
			float w = weights is null ? 1f : i < weights.Length ? weights[i] : 0f;
			if ( w != 0 ) mesh.Positions[i] = mesh.Positions[i] + normals[i] * ( Distance * w );
		}
		return mesh;
	}
}

/// <summary>Blender's Shrinkwrap onto another surface (ShrinkwrapService), with offset and max distance.</summary>
public sealed class ShrinkwrapModifier : MeshModifier
{
	public PolyMesh Target { get; set; }
	public ShrinkwrapService.Options Options { get; set; } = new();
	public override string Name => $"shrinkwrap {Options.Mode}";
	public override PolyMesh Apply( PolyMesh mesh )
	{
		if ( Target is null ) return mesh;
		var wrapped = ShrinkwrapService.Wrap( mesh, Target, Options ).Mesh;
		wrapped.Skin ??= mesh.Skin?.Clone();
		return wrapped;
	}
}

/// <summary>Bakes a shape-key mix (MorphEditService.Preview) into the positions, so later
/// topology-changing modifiers see the sculpted shape (Blender's "apply shape keys first").</summary>
public sealed class MorphModifier : MeshModifier
{
	public Dictionary<string, float> Weights { get; } = new( StringComparer.Ordinal );
	public override string Name => $"morph {string.Join( ",", Weights.Select( kv => $"{kv.Key}={kv.Value:0.##}" ) )}";
	public override PolyMesh Apply( PolyMesh mesh )
	{
		if ( Weights.Count == 0 || mesh.VertexCount == 0 ) return mesh;
		var service = new MorphEditService( mesh, new EditHistory() );
		var shaped = new Vec3[mesh.VertexCount];
		service.Preview( Weights, shaped );
		for ( int i = 0; i < shaped.Length; i++ ) mesh.Positions[i] = shaped[i];
		mesh.Morphs.Clear(); // baked: the result has no live shape keys left
		return mesh;
	}
}

/// <summary>R9 item 2: an ordered modifier stack evaluated on the CPU before upload. The source mesh
/// is never mutated; disabled modifiers are skipped; the result keeps the source's resolved
/// materials (<see cref="ViewportMesh.Remesh"/>).</summary>
public sealed class ModifierStack
{
	public List<MeshModifier> Modifiers { get; } = new();
	public double LastSeconds { get; private set; }
	public string LastSummary { get; private set; } = "";

	public bool IsEmpty => !Modifiers.Any( m => m.Enabled );

	public PolyMesh Evaluate( PolyMesh source )
	{
		ArgumentNullException.ThrowIfNull( source );
		var clock = System.Diagnostics.Stopwatch.StartNew();
		var mesh = source.Clone();
		var parts = new List<string>();
		foreach ( var modifier in Modifiers )
		{
			if ( !modifier.Enabled ) continue;
			int before = mesh.FaceCount;
			mesh = modifier.Apply( mesh ) ?? throw new InvalidOperationException( $"{modifier.Name} returned no mesh." );
			parts.Add( $"{modifier.Name} ({before}->{mesh.FaceCount} faces)" );
		}
		LastSeconds = clock.Elapsed.TotalSeconds;
		LastSummary = string.Join( " | ", parts );
		return mesh;
	}

	/// <summary>Evaluate on <paramref name="mesh"/>.SourceMesh and re-extract; returns <paramref name="mesh"/> when empty.</summary>
	public ViewportMesh Apply( ViewportMesh mesh )
	{
		if ( IsEmpty ) return mesh;
		if ( mesh.SourceMesh is null ) throw new InvalidOperationException( "Mesh has no retained source to modify." );
		return mesh.Remesh( Evaluate( mesh.SourceMesh ) );
	}

	/// <summary>`--modifiers subsurf,decimate,retopo,smooth,displace,shrinkwrap,morph` demo stack,
	/// sized from the mesh radius (inches). Displace uses the head bone's weights when rigged.</summary>
	public static readonly string[] Kinds = { "subsurf", "decimate", "retopo", "smooth", "displace", "shrinkwrap", "morph" };

	public static ModifierStack FromSpec( string spec, ViewportMesh mesh )
	{
		var stack = new ModifierStack();
		float r = MathF.Max( 1e-3f, mesh.Radius );
		foreach ( var raw in spec.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			switch ( raw.ToLowerInvariant() )
			{
				case "subsurf": stack.Modifiers.Add( new SubdivisionModifier() ); break;
				case "decimate": stack.Modifiers.Add( new DecimateModifier { Ratio = .25f } ); break;
				case "retopo": stack.Modifiers.Add( new RetopoModifier() ); break;
				case "smooth": stack.Modifiers.Add( new SmoothModifier() ); break;
				case "displace":
				{
					int bone = HeadBone( mesh.Rig );
					stack.Modifiers.Add( new DisplaceModifier { Distance = r * .03f, Weights = bone >= 0 ? DisplaceModifier.BoneGroup( bone ) : null } );
					break;
				}
				case "shrinkwrap":
				{
					// Wrap onto a coarse decimated copy of itself: a "low-poly cast" with a small offset.
					var target = Decimate.ToRatio( mesh.SourceMesh, .05f );
					stack.Modifiers.Add( new ShrinkwrapModifier { Target = target, Options = new ShrinkwrapService.Options { Offset = r * .004f, MaxDistance = r * .05f } } );
					break;
				}
				case "morph":
				{
					var morph = new MorphModifier();
					var first = mesh.SourceMesh?.ValidMorphs.FirstOrDefault();
					if ( first is not null ) morph.Weights[first.Name] = 1f;
					stack.Modifiers.Add( morph );
					break;
				}
				default: throw new ArgumentException( $"--modifiers: unknown kind '{raw}' (expected {string.Join( ", ", Kinds )})" );
			}
		}
		return stack;
	}

	static int HeadBone( Skeleton rig )
	{
		if ( rig is null ) return -1;
		for ( int i = 0; i < rig.Count; i++ )
			if ( rig.Bones[i].Name.Contains( "head", StringComparison.OrdinalIgnoreCase ) ) return i;
		return -1;
	}
}
