using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>SkinGen as a feature: <c>add SkinDetail freckles=0.5 scar="0,0,5..2,0,5"</c>.
///
/// What it does, per body: estimates face landmarks from the body's bounds, runs a
/// <see cref="SkinGenStack"/> (3-zone chromophore tint, micro-skin roughness zones,
/// Poisson freckles, keloid scars, sub-dermal tattoos, vellus sheen), writes the result
/// as vertex colours, and displaces scarred vertices along their normals. With
/// <c>Folder</c> set it also bakes colour + roughness maps there (same files as
/// <see cref="FleshMaterialFeature"/> writes, <c>skin_&lt;body&gt;_color.png</c> /
/// <c>_rough.png</c>), so a painter can take the maps further.
///
/// Landmarks are estimated, not measured: on a real head they land on the face; on a
/// plain block they land on the block, and the zones follow the block. A scar string
/// that does not parse (<c>"x,y,z..x,y,z"</c>) fails the rebuild honestly instead of
/// painting nothing.</summary>
public sealed class SkinDetailFeature : Feature
{
	public override string TypeName => "Skin Detail";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly Vec3Param BaseColour = new( "Base colour", new Vec3( 0.6f, 0.3f, 0.3f ) );
	public readonly BoolParam Zones = new( "Zones", true );
	public readonly FloatParam Freckles = new( "Freckles", 0f, 0f, 1f );
	public readonly FloatParam Stubble = new( "Stubble", 0f, 0f, 1f );
	public readonly FloatParam Vellus = new( "Vellus", 0f, 0f, 1f );
	public readonly IntParam Seed = new( "Seed", 1, 0, int.MaxValue ) { Slider = false };
	public readonly StringParam Scar = new( "Scar", "" );
	public readonly StringParam Folder = new( "Folder", "" );
	public readonly IntParam Size = new( "Bake size", 512, 64, 1024 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, BaseColour, Zones, Freckles, Stubble, Vellus, Seed, Scar, Folder, Size };

	/// <summary>Files the last rebuild wrote (empty when Folder is unset).</summary>
	public IReadOnlyList<string> Written { get; private set; } = Array.Empty<string>();

	/// <summary>Freckle spots the last rebuild placed, across all bodies.</summary>
	public int LastFreckleSpots { get; private set; }

	/// <summary>Vertices the last rebuild displaced for scars, across all bodies.</summary>
	public int LastDisplacedVertices { get; private set; }

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );
		var meshes = targets.Where( b => b.Mesh is not null && b.Mesh.VertexCount > 0 && b.Mesh.FaceCount > 0 ).ToList();

		if ( meshes.Count == 0 )
			Fail( "Skin Detail needs a body to paint.", "The selected bodies have no mesh with faces.", "Add a head body first, or pick bodies=Head" );

		var gen = new SkinGenStack
		{
			Zones = Zones.Value,
			Freckles = Freckles.Clamped,
			Stubble = Stubble.Clamped,
			Vellus = Vellus.Clamped,
			Seed = Seed.Value,
		};

		if ( !string.IsNullOrWhiteSpace( Scar.Value ) )
		{
			var scar = SkinScar.Parse( Scar.Value );
			if ( scar is null )
				Fail( "Skin Detail scar does not parse.",
					$"Scar is '{Scar.Value}', not two points.",
					"Write a scar as \"x,y,z..x,y,z\", e.g. scar=\"0,0,5..2,0,5\"" );
			gen.Scars.Add( scar );
		}

		var written = new List<string>();
		var freckles = 0;
		var displaced = 0;
		var folder = Folder.Value.Trim().Replace( '\\', '/' ).TrimEnd( '/' );

		foreach ( var body in meshes )
		{
			var mesh = body.Mesh;
			var (min, max) = ModelMeasure.Bounds( mesh );
			var landmarks = FaceBounds.FromHeadBounds( min, max );
			var result = gen.Evaluate( mesh, landmarks, BaseColour.Value );
			mesh.VertexColors = result.Colour.Select( c => new Vec4( c.x, c.y, c.z, 1f ) ).ToArray();
			freckles += result.FreckleSpots.Count;
			if ( gen.Scars.Count > 0 )
				displaced += gen.DisplaceScars( mesh );

			if ( folder.Length == 0 )
				continue;

			var maps = SkinGenBake.Bake( mesh, gen, landmarks, Size.Clamped, BaseColour.Value );
			var dir = Path.GetFullPath( folder );
			Directory.CreateDirectory( dir );
			var tag = new string( (body.Name ?? body.Id).Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() );
			var color = Path.Combine( dir, $"skin_{tag}_color.png" );
			var rough = Path.Combine( dir, $"skin_{tag}_rough.png" );
			WriteIfChanged( color, PngWriter.ToBytesRgba( maps.Albedo, maps.Size, maps.Size ) );
			WriteIfChanged( rough, PngWriter.ToBytesRgba( maps.Roughness, maps.Size, maps.Size ) );
			written.Add( color );
			written.Add( rough );
		}

		Written = written;
		LastFreckleSpots = freckles;
		LastDisplacedVertices = displaced;
	}

	static void WriteIfChanged( string path, byte[] bytes )
	{
		if ( File.Exists( path ) && File.ReadAllBytes( path ).SequenceEqual( bytes ) )
			return;
		File.WriteAllBytes( path, bytes );
	}
}
