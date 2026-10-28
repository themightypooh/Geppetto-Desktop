using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Effigy;

/// <summary>
/// Script: <c>add Hair name=Hair bodies=Head region=Scalp guides=40 children=4 length=3
/// part=center clump=0:0,0.4:0.55,1:0.92 curlradius=0 curlfreq=1 geometry=Cards width=0.3
/// material=0 sheet=/tmp/hair-sheet seed=42</c>.
///
/// A parametric groom on an existing body (a head for the Deep-Sea Diver), not a
/// standalone blob: roots are sampled from the target body's surface inside the
/// named region (see <see cref="HairGroom.SampleRegion"/>), guides are groomed by
/// <see cref="HairGroom"/>, children clump root-preservingly around them, and the
/// result is one body of cards or tubes. When <c>sheet=</c> names a folder, the
/// groom's card texture set is baked there by <see cref="HairTextureBaker"/> under
/// this feature's name — cards without a sheet still render, they just have no
/// fiber texture to sample.
///
/// Deliberately NOT a complete diver: Vellum, Marvelous stitching and skin SSS are
/// separate pieces, and collision is bounding spheres (see HairGroom), not MeshBVH.
/// </summary>
public sealed class HairFeature : Feature
{
	public override string TypeName => "Hair";

	public readonly BodySelectionParam Bodies = new( "Bodies" );
	public readonly ChoiceParam Region = new( "Region",
		new[] { "Scalp", "Beard", "Moustache", "Brows", "Eyelashes", "Mane", "Tail", "Custom" } );
	public readonly IntParam Guides = new( "Guides", 40, 1, 10000 );
	public readonly IntParam Children = new( "Children", 4, 0, 64 );
	public readonly FloatParam Length = new( "Length", 3f, 0.1f, 100f, unit: "u" );
	public readonly IntParam Segments = new( "Segments", 8, 1, 64 );
	public readonly ChoiceParam Part = new( "Part",
		new[] { "Center", "SideLeft", "SideRight", "Radial", "None" } );
	public readonly StringParam Clump = new( "Clump", "0:0,0.4:0.55,1:0.92" );
	public readonly FloatParam Spread = new( "Spread", 0.25f, 0f, 10f, unit: "u" );
	public readonly FloatParam CurlRadius = new( "Curl radius", 0f, 0f, 10f, unit: "u" );
	public readonly FloatParam CurlFreq = new( "Curl freq", 1f, 0f, 64f );
	public readonly FloatParam WaveAmp = new( "Wave amp", 0f, 0f, 10f, unit: "u" );
	public readonly FloatParam WaveFreq = new( "Wave freq", 1f, 0f, 64f );
	public readonly FloatParam KinkAmp = new( "Kink amp", 0f, 0f, 10f, unit: "u" );
	public readonly FloatParam Frizz = new( "Frizz", 0f, 0f, 10f, unit: "u" );
	public readonly FloatParam Flyaway = new( "Flyaway", 0f, 0f, 1f );
	public readonly BoolParam Braid = new( "Braid", false );
	public readonly IntParam BraidLobes = new( "Braid lobes", 12, 1, 128 );
	public readonly FloatParam BraidWidth = new( "Braid width", 0.6f, 0.01f, 10f, unit: "u" );
	public readonly FloatParam BraidTaper = new( "Braid taper", 0.5f, 0f, 1f );
	public readonly StringParam Gather = new( "Gather", "" );
	public readonly ChoiceParam Geometry = new( "Geometry", new[] { "Cards", "Tubes" } );
	public readonly FloatParam Width = new( "Card width", 0.3f, 0.01f, 10f, unit: "u" );
	public readonly FloatParam Radius = new( "Radius", 0.08f, 0.001f, 10f, unit: "u" );
	public readonly FloatParam TipRatio = new( "Tip ratio", 0.3f, 0f, 1f );
	public readonly IntParam Sides = new( "Sides", 6, 3, 16 );
	public readonly IntParam Steps = new( "Steps", 30, 0, 200 );
	public readonly FloatParam Gravity = new( "Gravity", -9.8f, -100f, 100f );
	public readonly StringParam Wind = new( "Wind", "0,0,0" );
	public readonly StringParam Comb = new( "Comb", "0,0,0" );
	public readonly FloatParam ZMin = new( "Z min", 0f, 0f, 1f );
	public readonly FloatParam ZMax = new( "Z max", 1f, 0f, 1f );
	public readonly IntParam Seed = new( "Seed", 42, 0, int.MaxValue ) { Slider = false };
	public readonly StringParam Sheet = new( "Sheet", "" );
	public readonly BoolParam Interpolate = new( "Interpolate", false );
	public readonly FloatParam LengthVar = new( "Length variation", 0f, 0f, 0.5f );
	public readonly FloatParam WidthVar = new( "Width variation", 0f, 0f, 0.5f );
	public readonly StringParam DensityMap = new( "Density map", "" );
	public readonly StringParam LengthMap = new( "Length map", "" );
	public readonly StringParam FlowMap = new( "Flow map", "" );
	public readonly BoolParam BindRig = new( "Bind rig", false );
	public readonly IntParam MaxStrands = new( "Max strands", 0, 0, 1000000 ) { Slider = false };
	public readonly IntParam Material = new( "Material slot", 0, 0, 63 ) { Slider = false };

	public override IReadOnlyList<IParam> Parameters => new IParam[]
		{ Bodies, Region, Guides, Children, Length, Segments, Part, Clump, Spread,
			CurlRadius, CurlFreq, WaveAmp, WaveFreq, KinkAmp, Frizz, Flyaway, Braid,
			BraidLobes, BraidWidth, BraidTaper, Gather, Geometry, Width, Radius, TipRatio,
			Sides, Steps, Gravity, Wind, Comb, ZMin, ZMax, Seed, Sheet, Interpolate,
			LengthVar, WidthVar, DensityMap, LengthMap, FlowMap, BindRig, MaxStrands, Material };

	/// <summary>Strands from the last Execute, for tests and for the sheet baker's caller. Empty until Execute runs.</summary>
	public IReadOnlyList<HairGroom.Strand> LastStrands => _strands;

	List<HairGroom.Strand> _strands = new();

	protected override void Execute( FeatureContext ctx )
	{
		if ( !StrandSolver.TryParseVec3( Wind.Value, out var wind ) )
			FailOn( Wind.Label, $"'{Wind.Value}' is not a vector x,y,z.", "Wind pushes every strand sideways.", "Write it like wind=0.5,0,0" );

		if ( !StrandSolver.TryParseVec3( Comb.Value, out var comb ) )
			FailOn( Comb.Label, $"'{Comb.Value}' is not a vector x,y,z.", "Comb shears the settled strands.", "Write it like comb=0,1,-0.2" );

		Vec3? gather = null;

		if ( !string.IsNullOrWhiteSpace( Gather.Value ) )
		{
			if ( !StrandSolver.TryParseVec3( Gather.Value, out var g ) )
				FailOn( Gather.Label, $"'{Gather.Value}' is not a vector x,y,z.", "Gather pulls strands through a tie point.", "Write it like gather=0,0,8, or leave it empty" );

			gather = g;
		}

		var targets = RequireBodies( ctx, Bodies );
		var meshes = targets.Where( b => b.Mesh is not null ).Select( b => b.Mesh ).ToList();

		if ( meshes.Count == 0 )
			Fail( "Hair needs a body to grow on.", "The selected bodies have no mesh.", "Add a head body first, or pick bodies=Head" );

		(int W, int H, int[] Rgb)? LoadMap( string path, string label )
		{
			if ( string.IsNullOrWhiteSpace( path ) ) return null;
			try { var (w, h, rgb, _) = Effigy.Render.Raster.LoadPng( path ); return (w, h, rgb); }
			catch ( System.Exception e ) { Fail( $"{label} map could not be loaded: {path} ({e.Message})", "The map file is missing or not a PNG.", "Give a readable PNG path, or leave the map empty" ); return null; }
		}

		var density = LoadMap( DensityMap.Value, "Density" );
		var lengthMap = LoadMap( LengthMap.Value, "Length" );
		var flowMap = LoadMap( FlowMap.Value, "Flow" );
		var samples = HairGroom.SampleRegion( meshes, Region.Value, Guides.Clamped, Seed.Value, ZMin.Value, ZMax.Value, density, lengthMap, flowMap );

		if ( samples.Count == 0 )
			Fail( $"Region {Region.Value} found no scalp surface to root hair on.",
				"The target bodies have no faces in that region band.",
				"Pick a region that covers the body, or region=Custom with zmin/zmax over it" );

		// Bounding spheres, same approximation as StrandsFeature: keep strands out of
		// the bulk of the head without a BVH walk per point.
		var colliders = new List<(Vec3, float)>();

		var targetIds = new System.Collections.Generic.HashSet<string>( targets.Select( b => b.Id ) );
		foreach ( var body in ctx.Bodies )
		{
			if ( targetIds.Contains( body.Id ) )
				continue;

			if ( body.Mesh is null || body.Mesh.VertexCount == 0 )
				continue;

			var centre = Vec3.Zero;

			foreach ( var p in body.Mesh.Positions )
				centre += p;

			centre /= body.Mesh.VertexCount;
			var radius = 0f;

			foreach ( var p in body.Mesh.Positions )
				radius = Math.Max( radius, (p - centre).Length );

			colliders.Add( (centre, radius + Radius.Clamped) );
		}

		_strands = HairGroom.Build(
			samples.Select( s => s.Point ).ToList(),
			samples.Select( s => s.Normal ).ToList(),
			colliders,
			new HairGroom.Options
			{
				GuideCount = Guides.Clamped,
				ChildrenPerGuide = Children.Clamped,
				Length = Length.Clamped,
				Segments = Segments.Clamped,
				Steps = Steps.Clamped,
				Gravity = Gravity.Value,
				Wind = wind,
				Comb = comb,
				Part = Part.Value,
				ClumpCurve = Clump.Value,
				Spread = Spread.Clamped,
				CurlRadius = CurlRadius.Clamped,
				CurlFreq = CurlFreq.Value,
				WaveAmp = WaveAmp.Clamped,
				WaveFreq = WaveFreq.Value,
				KinkAmp = KinkAmp.Clamped,
				Frizz = Frizz.Clamped,
				FlyawayRatio = Flyaway.Clamped,
				Braid = Braid.Value,
				BraidLobes = BraidLobes.Clamped,
				BraidWidth = BraidWidth.Clamped,
				BraidTaper = BraidTaper.Clamped,
				GatherPoint = gather,
				Seed = Seed.Value,
				Interpolate = Interpolate.Value,
				LengthVariation = LengthVar.Clamped,
				WidthVariation = WidthVar.Clamped,
			}, samples.Select( s => s.Flow ).ToList(), samples.Select( s => s.LengthScale ).ToList(), meshes, Radius.Clamped );

		// LOD cap: guides always survive; children are thinned in order. A far-view
		// groom sets maxstrands low instead of paying for full density at 20 m.
		if ( MaxStrands.Clamped > 0 && _strands.Count > MaxStrands.Clamped )
		{
			var guides = _strands.Where( s => s.IsGuide ).ToList();
			var kids = _strands.Where( s => !s.IsGuide ).ToList();
			_strands = guides.Count >= MaxStrands.Clamped
				? guides.Take( MaxStrands.Clamped ).ToList()
				: guides.Concat( kids.Take( MaxStrands.Clamped - guides.Count ) ).ToList();
		}

		var bandCount = !string.IsNullOrWhiteSpace( Sheet.Value ) && Geometry.Value == "Cards" ? 5 : 0;
		var mesh = Geometry.Value == "Tubes"
			? HairGroom.BuildTubesMesh( _strands, Radius.Clamped, TipRatio.Clamped, Sides.Clamped, Material.Clamped )
			: HairGroom.BuildCardsMesh( _strands, Width.Clamped, TipRatio.Clamped, Material.Clamped, bandCount );

		if ( BindRig.Value && ctx.Rig is not null )
			mesh.Skin = SkinBinder.BindSmooth( mesh, ctx.Rig );

		ctx.Bodies.Add( new Body( ctx.NewBodyId(), Name, mesh ) );

		if ( !string.IsNullOrWhiteSpace( Sheet.Value ) && Geometry.Value == "Cards" )
		{
			var safe = new string( (Name ?? "Hair").Select( c => char.IsLetterOrDigit( c ) ? c : '_' ).ToArray() );
			var files = HairTextureBaker.Bake( Sheet.Value, safe, new HairTextureBaker.Options { Seed = Seed.Value } );
			string Find( string suffix ) => files.FirstOrDefault( f => f.EndsWith( suffix, System.StringComparison.OrdinalIgnoreCase ) );
			// Wire the baked sheet into the hair slot (Rank 2): albedo + normal +
			// roughness from the baker, preset hair_aniso so the fibre shader is on.
			// A user `material` line for this slot still wins on any field it set.
			if ( !SlotLook.TryPreset( "hair_aniso", out var look ) ) look = new SlotLook { Preset = "hair_aniso" };
			look.AlbedoMap = Find( "_color.png" );
			look.NormalMap = Find( "_normal.png" );
			look.RoughnessMap = Find( "_rough.png" );
			ctx.SlotLooks[Material.Clamped] = look;
		}
	}
}
