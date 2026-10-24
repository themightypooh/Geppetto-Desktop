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

/// <summary>RetopoService: triangle pairing into quads (optionally pre-decimated), skin preserved.
/// With <see cref="LodTargets"/> set (plus-separated triangle counts, e.g. "4000+1500+500"),
/// runs <see cref="RetopoService.RetopoLodChain"/> and prints one
/// <see cref="RetopoService.CheckLodPreservation"/> summary per LOD, returning the smallest LOD.</summary>
public sealed class RetopoModifier : MeshModifier
{
	public RetopoService.Options Options { get; set; } = new();
	/// <summary>Plus-separated LOD triangle targets; null/empty = single Retopo pass. Plus, not
	/// comma: the --modifiers spec itself splits on commas, so a comma list would be torn apart
	/// into unknown kinds before it ever reached this modifier.</summary>
	public string LodTargets { get; set; }
	public List<string> LastLodSummaries { get; } = new();
	public override string Name => LodTargets is null ? "retopo" : $"retopo lod[{LodTargets}]";
	public override PolyMesh Apply( PolyMesh mesh )
	{
		if ( string.IsNullOrWhiteSpace( LodTargets ) ) return RetopoService.Retopo( mesh, Options ).Mesh;
		var targets = RetopoService.ParseLodTargets( LodTargets.Replace( '+', ',' ) );
		var chain = RetopoService.RetopoLodChain( mesh, targets, Options );
		LastLodSummaries.Clear();
		PolyMesh smallest = mesh;
		for ( int li = 0; li < chain.Count; li++ )
		{
			var lod = chain[ li ];
			var check = RetopoService.CheckLodPreservation( mesh, targets[ li ], lod, Options );
			LastLodSummaries.Add( check.Summary() );
			Console.WriteLine( $"retopo lod: {check.Summary()}" );
			smallest = lod.Mesh;
		}
		return smallest;
	}
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

/// <summary>Blender's Shrinkwrap onto another surface (ShrinkwrapService), with offset and max distance.
/// Routes through <see cref="ShrinkwrapService.WrapToSurface"/> (true closest-point projection),
/// NOT the legacy nearest-vertex <see cref="ShrinkwrapService.Wrap"/>: Wrap ignores
/// <see cref="ShrinkwrapService.Options.Mode"/> and <see cref="ShrinkwrapService.Options.SmoothIterations"/>
/// entirely, so the old modifier could never exercise TargetNormalProject or smoothing.</summary>
public sealed class ShrinkwrapModifier : MeshModifier
{
	public PolyMesh Target { get; set; }
	public ShrinkwrapService.Options Options { get; set; } = new();
	public override string Name => $"shrinkwrap {Options.Mode}";
	public override PolyMesh Apply( PolyMesh mesh )
	{
		if ( Target is null ) return mesh;
		var wrapped = ShrinkwrapService.WrapToSurface( mesh, Target, Options ).Mesh;
		wrapped.Skin ??= mesh.Skin?.Clone();
		return wrapped;
	}
}

/// <summary>DataTransferService barycentric transfer as a modifier: restores surface data
/// (UVs, skin weights, vertex colours, morph deltas) from <see cref="Source"/> onto the mesh
/// as it enters this modifier. The intended chain is retopo first (which changes topology
/// and drops per-vertex data) then datatransfer from the pre-retopo source, so the LOD keeps
/// the original's data interpolated barycentrically instead of nearest-vertex staircasing.</summary>
public sealed class DataTransferModifier : MeshModifier
{
	public PolyMesh Source { get; set; }
	public DataTransferService.Channel[] Channels { get; set; } = new[] { DataTransferService.Channel.SkinWeights, DataTransferService.Channel.UVs };
	public float MaxDistance { get; set; } = float.MaxValue;
	public override string Name => $"datatransfer {string.Join( "+", Channels.Select( c => c.ToString().ToLowerInvariant() ) )}";
	public override PolyMesh Apply( PolyMesh mesh )
	{
		if ( Source is null ) return mesh;
		return DataTransferService.Transfer( Source, mesh, new DataTransferService.Options { Channels = Channels, MaxDistance = MaxDistance } ).Mesh;
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

	/// <summary>`--modifiers subsurf,decimate,retopo,smooth,displace,shrinkwrap,morph,datatransfer` demo stack,
	/// sized from the mesh radius (inches). Displace uses the head bone's weights when rigged.
	/// Option syntax (colon segments, key=value; lists use '+' because the spec splits on commas):
	/// `shrinkwrap:mode=TargetNormalProject:smooth=2` (mode = NearestSurface|NormalProjection|
	/// TargetNormalProject; offset/maxdist in mesh units, defaulting to r*.004 / r*.05),
	/// `retopo:targets=4000+1500+500` (LOD chain + preservation summaries),
	/// `datatransfer:channels=uvs+colors` (channels = skin|uvs|colors|morphs; source = the
	/// mesh as loaded, so `retopo,datatransfer` restores the original's data onto the LOD).</summary>
	public static readonly string[] Kinds = { "subsurf", "decimate", "retopo", "smooth", "displace", "shrinkwrap", "morph", "datatransfer" };

	public static ModifierStack FromSpec( string spec, ViewportMesh mesh )
	{
		var stack = new ModifierStack();
		float r = MathF.Max( 1e-3f, mesh.Radius );
		foreach ( var raw in spec.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
		{
			int colon = raw.IndexOf( ':' );
			string kind = ( colon < 0 ? raw : raw[ ..colon ] ).ToLowerInvariant();
			var opts = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			if ( colon >= 0 )
				foreach ( var seg in raw[ ( colon + 1 ).. ].Split( ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
				{
					int eq = seg.IndexOf( '=' );
					if ( eq < 0 ) throw new ArgumentException( $"--modifiers: option '{seg}' in '{raw}' needs key=value." );
					opts[ seg[ ..eq ].Trim() ] = seg[ ( eq + 1 ).. ].Trim();
				}
			void NoOpts()
			{
				if ( opts.Count > 0 ) throw new ArgumentException( $"--modifiers: '{kind}' takes no options, got '{raw}'." );
			}
			switch ( kind )
			{
				case "subsurf": NoOpts(); stack.Modifiers.Add( new SubdivisionModifier() ); break;
				case "decimate": NoOpts(); stack.Modifiers.Add( new DecimateModifier { Ratio = .25f } ); break;
				case "retopo":
				{
					var mod = new RetopoModifier();
					foreach ( var kv in opts )
						if ( kv.Key == "targets" ) mod.LodTargets = kv.Value;
						else throw new ArgumentException( $"--modifiers: unknown retopo option '{kv.Key}' (expected targets)." );
					stack.Modifiers.Add( mod ); break;
				}
				case "smooth": NoOpts(); stack.Modifiers.Add( new SmoothModifier() ); break;
				case "displace": NoOpts();
				{
					int bone = HeadBone( mesh.Rig );
					stack.Modifiers.Add( new DisplaceModifier { Distance = r * .03f, Weights = bone >= 0 ? DisplaceModifier.BoneGroup( bone ) : null } );
					break;
				}
				case "shrinkwrap":
				{
					// Wrap onto a coarse decimated copy of itself: a "low-poly cast" with a small offset.
					var target = Decimate.ToRatio( mesh.SourceMesh, .05f );
					var swOpts = new ShrinkwrapService.Options { Offset = r * .004f, MaxDistance = r * .05f };
					foreach ( var kv in opts )
						if ( kv.Key == "mode" )
						{
							if ( !Enum.TryParse<ShrinkwrapService.Mode>( kv.Value, ignoreCase: true, out var mode ) )
								throw new ArgumentException( $"--modifiers: unknown shrinkwrap mode '{kv.Value}' (expected NearestSurface, NormalProjection, TargetNormalProject)." );
							swOpts.Mode = mode;
						}
						else if ( kv.Key == "smooth" ) swOpts.SmoothIterations = int.Parse( kv.Value );
						else if ( kv.Key == "offset" ) swOpts.Offset = float.Parse( kv.Value );
						else if ( kv.Key == "maxdist" ) swOpts.MaxDistance = float.Parse( kv.Value );
						else throw new ArgumentException( $"--modifiers: unknown shrinkwrap option '{kv.Key}' (expected mode, smooth, offset, maxdist)." );
					stack.Modifiers.Add( new ShrinkwrapModifier { Target = target, Options = swOpts } );
					break;
				}
				case "datatransfer":
				{
					if ( mesh.SourceMesh is null ) throw new ArgumentException( "--modifiers: datatransfer needs a retained source mesh (none loaded)." );
					var channels = new List<DataTransferService.Channel> { DataTransferService.Channel.SkinWeights, DataTransferService.Channel.UVs };
					float maxDist = float.MaxValue;
					foreach ( var kv in opts )
						if ( kv.Key == "channels" )
						{
							channels = new List<DataTransferService.Channel>();
							foreach ( var c in kv.Value.Split( '+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
								channels.Add( c.ToLowerInvariant() switch
								{
									"skin" => DataTransferService.Channel.SkinWeights,
									"uvs" or "uv" => DataTransferService.Channel.UVs,
									"colors" or "color" => DataTransferService.Channel.VertexColors,
									"morphs" or "morph" => DataTransferService.Channel.MorphDeltas,
									_ => throw new ArgumentException( $"--modifiers: unknown datatransfer channel '{c}' (expected skin, uvs, colors, morphs)." ),
								} );
						}
						else if ( kv.Key == "maxdist" ) maxDist = float.Parse( kv.Value );
						else throw new ArgumentException( $"--modifiers: unknown datatransfer option '{kv.Key}' (expected channels, maxdist)." );
					stack.Modifiers.Add( new DataTransferModifier { Source = mesh.SourceMesh, Channels = channels.ToArray(), MaxDistance = maxDist } );
					break;
				}
				case "morph": NoOpts();
				{
					var morph = new MorphModifier();
					var first = mesh.SourceMesh?.ValidMorphs.FirstOrDefault();
					if ( first is not null ) morph.Weights[first.Name] = 1f;
					stack.Modifiers.Add( morph );
					break;
				}
				default: throw new ArgumentException( $"--modifiers: unknown kind '{kind}' (expected {string.Join( ", ", Kinds )})" );
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
