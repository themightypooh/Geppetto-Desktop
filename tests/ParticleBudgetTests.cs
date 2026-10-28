using System;
using Marionette.Desktop;

internal static class ParticleBudgetTests
{
	public static void Register( TestRegistry registry, TestSettings settings )
	{
		void Check( bool ok, string reason ) { if ( !ok ) throw new Exception( reason ); }
		void Near( double got, double want, double tol, string what ) { if ( Math.Abs( got - want ) > tol ) throw new Exception( $"{what}: got {got}, want {want} ± {tol}" ); }
		void Throws( Action body, string what )
		{
			try { body(); }
			catch ( ArgumentException ) { return; } // also covers ArgumentOutOfRangeException + ArgumentNullException
			throw new Exception( $"{what}: expected an exception, none thrown" );
		}
		ParticleBudgetInput Inp() => new()
		{
			ParticleCount = 100, MeanSize = 10, MaxSize = 10,
			ScreenArea = 1_000_000, OverdrawFactor = 1,
			RenderType = ParticleRenderType.Billboard,
		};

		registry.Add( "particle-budget", "roster example is exact", () =>
		{
			// 100 particles of size 10 px on a 1000x1000 screen, overdraw 1:
			// 100 x 10^2 x 1 / 1,000,000 = 0.01.
			var e = ParticleBudget.Estimate( Inp() );
			Near( e.CoveredArea, 10_000, 0, "covered area" );
			Near( e.OverdrawRatio, 0.01, 0, "overdraw ratio" );
			Near( e.QuadCount, 100, 0, "quads" );
			Near( e.TriangleCount, 200, 0, "triangles" );
			Near( e.VertexCount, 400, 0, "vertices" );
			Near( e.InstanceCount, 0, 0, "billboards count no instances" );
			Check( e.Verdict == ParticleBudgetVerdict.Under, "ratio 0.01 is Under" );
			// Scores tie Count 100 == Size 10^2; tie-break is Count.
			Check( e.LargestContributor == ParticleBudgetContributor.Count, "tie breaks to Count" );
		} );

		registry.Add( "particle-budget", "hand-computed overdraw with overlap", () =>
		{
			var i = Inp();
			i.ParticleCount = 500; i.MeanSize = 20; i.MaxSize = 20;
			i.ScreenArea = 800 * 600; i.OverdrawFactor = 2;
			var e = ParticleBudget.Estimate( i );
			Near( e.CoveredArea, 500 * 400 * 2, 0, "covered area" );
			Near( e.OverdrawRatio, 400_000.0 / 480_000.0, 1e-12, "overdraw ratio" );
			Check( e.Verdict == ParticleBudgetVerdict.Under, "ratio 0.833 is Under at defaults" );
		} );

		registry.Add( "particle-budget", "worst case uses max size", () =>
		{
			var i = Inp();
			i.MeanSize = 10; i.MaxSize = 20;
			var e = ParticleBudget.Estimate( i );
			Near( e.OverdrawRatio, 0.01, 0, "mean ratio" );
			Near( e.WorstCoveredArea, 100 * 400, 0, "worst covered area" );
			Near( e.WorstOverdrawRatio, 0.04, 0, "worst ratio" );
		} );

		registry.Add( "particle-budget", "strand quads are segments per particle", () =>
		{
			var i = Inp();
			i.RenderType = ParticleRenderType.Strand; i.StrandPoints = 8;
			var e = ParticleBudget.Estimate( i );
			Near( e.QuadsPerParticle, 7, 0, "quads per particle = points - 1" );
			Near( e.QuadCount, 700, 0, "strand quads" );
			Near( e.TriangleCount, 1400, 0, "strand triangles" );
			Near( e.VertexCount, 2800, 0, "strand vertices" );
			Near( e.InstanceCount, 0, 0, "strands count no instances" );
			Check( e.Summary.Contains( "ribbon quads" ), "summary names ribbon quads" );
		} );

		registry.Add( "particle-budget", "mesh counts instances not billboards", () =>
		{
			var i = Inp();
			i.RenderType = ParticleRenderType.MeshInstance; i.TemplateTriangleCount = 12;
			var e = ParticleBudget.Estimate( i );
			Near( e.QuadCount, 0, 0, "mesh draws zero quads" );
			Near( e.InstanceCount, 100, 0, "mesh instances = particle count" );
			Near( e.TriangleCount, 1200, 0, "mesh triangles" );
			Near( e.VertexCount, 3600, 0, "mesh vertices" );
			Check( e.Summary.Contains( "instances" ), "summary names instances" );
		} );

		registry.Add( "particle-budget", "verdict boundaries exact at defaults", () =>
		{
			var atNear = Inp();
			atNear.ParticleCount = 10_000; // 10000 x 100 / 1e6 = 1.0 exactly
			Check( ParticleBudget.Estimate( atNear ).Verdict == ParticleBudgetVerdict.Near, "ratio exactly 1.0 is Near, not Under" );
			var atOver = Inp();
			atOver.ParticleCount = 20_000; // ratio exactly 2.0
			Check( ParticleBudget.Estimate( atOver ).Verdict == ParticleBudgetVerdict.Over, "ratio exactly 2.0 is Over, not Near" );
			var belowNear = Inp();
			belowNear.ParticleCount = 9_900; // ratio 0.99
			Check( ParticleBudget.Estimate( belowNear ).Verdict == ParticleBudgetVerdict.Under, "ratio 0.99 is Under" );
			var aboveOver = Inp();
			aboveOver.ParticleCount = 20_001;
			Check( ParticleBudget.Estimate( aboveOver ).Verdict == ParticleBudgetVerdict.Over, "ratio above 2.0 is Over" );
		} );

		registry.Add( "particle-budget", "verdict boundaries exact at custom thresholds", () =>
		{
			var t = new ParticleBudgetThresholds { NearRatio = 0.5, OverRatio = 1.5 };
			var atNear = Inp(); atNear.ParticleCount = 5_000; atNear.Thresholds = t; // ratio 0.5
			Check( ParticleBudget.Estimate( atNear ).Verdict == ParticleBudgetVerdict.Near, "custom: ratio exactly 0.5 is Near" );
			var atOver = Inp(); atOver.ParticleCount = 15_000; atOver.Thresholds = t; // ratio 1.5
			Check( ParticleBudget.Estimate( atOver ).Verdict == ParticleBudgetVerdict.Over, "custom: ratio exactly 1.5 is Over" );
			var below = Inp(); below.ParticleCount = 4_900; below.Thresholds = t; // ratio 0.49
			Check( ParticleBudget.Estimate( below ).Verdict == ParticleBudgetVerdict.Under, "custom: ratio 0.49 is Under" );
		} );

		registry.Add( "particle-budget", "largest contributor count-dominated", () =>
		{
			var i = Inp();
			i.ParticleCount = 1_000_000; i.MeanSize = 1; i.MaxSize = 1; i.OverdrawFactor = 1;
			Check( ParticleBudget.Estimate( i ).LargestContributor == ParticleBudgetContributor.Count, "1M tiny particles, no overlap -> Count" );
		} );

		registry.Add( "particle-budget", "largest contributor size-dominated", () =>
		{
			var i = Inp();
			i.ParticleCount = 10; i.MeanSize = 900; i.MaxSize = 900; i.OverdrawFactor = 1;
			Check( ParticleBudget.Estimate( i ).LargestContributor == ParticleBudgetContributor.Size, "10 huge particles -> Size" );
		} );

		registry.Add( "particle-budget", "largest contributor overdraw-dominated", () =>
		{
			var i = Inp();
			i.ParticleCount = 50; i.MeanSize = 5; i.MaxSize = 5; i.OverdrawFactor = 200;
			Check( ParticleBudget.Estimate( i ).LargestContributor == ParticleBudgetContributor.Overdraw, "50 small particles x200 overlap -> Overdraw" );
		} );

		registry.Add( "particle-budget", "zero particles is zero cost never NaN", () =>
		{
			var i = Inp(); i.ParticleCount = 0;
			var e = ParticleBudget.Estimate( i );
			Near( e.QuadCount, 0, 0, "quads" );
			Near( e.TriangleCount, 0, 0, "triangles" );
			Near( e.VertexCount, 0, 0, "vertices" );
			Near( e.CoveredArea, 0, 0, "covered" );
			Near( e.OverdrawRatio, 0, 0, "ratio" );
			Check( !double.IsNaN( e.OverdrawRatio ) && !double.IsNaN( e.WorstOverdrawRatio ), "no NaN at zero" );
			Check( e.Verdict == ParticleBudgetVerdict.Under, "zero is Under" );
			Check( e.LargestContributor == ParticleBudgetContributor.None, "zero has no contributor" );
		} );

		registry.Add( "particle-budget", "loud failures on bad inputs", () =>
		{
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = -1, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000 } ), "negative count" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = -1, MaxSize = 10, ScreenArea = 1_000_000 } ), "negative mean size" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = -1, ScreenArea = 1_000_000 } ), "negative max size" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 20, MaxSize = 10, ScreenArea = 1_000_000 } ), "max below mean" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 0 } ), "zero screen area (never assumed)" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = -5 } ), "negative screen area" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = double.NaN, MaxSize = 10, ScreenArea = 1_000_000 } ), "NaN mean size" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, OverdrawFactor = double.NaN } ), "NaN overdraw" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, OverdrawFactor = -1 } ), "negative overdraw" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, RenderType = (ParticleRenderType)999 } ), "unknown render type" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, RenderType = ParticleRenderType.Strand, StrandPoints = 1 } ), "strand with < 2 points" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, RenderType = ParticleRenderType.MeshInstance, TemplateTriangleCount = 0 } ), "mesh without template triangles" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, LodCountMultiplier = 1.5 } ), "LOD count multiplier above 1" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, LodSizeMultiplier = 2.5 } ), "LOD size multiplier above 2" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, Thresholds = new ParticleBudgetThresholds { NearRatio = 2.0, OverRatio = 1.0 } } ), "over threshold below near" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, Thresholds = new ParticleBudgetThresholds { NearRatio = 0, OverRatio = 1.0 } } ), "non-positive near threshold" );
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10, ScreenArea = 1_000_000, Thresholds = null! } ), "null thresholds" );
			Throws( () => ParticleBudget.Estimate( null! ), "null input" );
		} );

		registry.Add( "particle-budget", "determinism is bit-identical", () =>
		{
			var i = Inp();
			i.ParticleCount = 12345; i.MeanSize = 7.5; i.MaxSize = 33.25;
			i.OverdrawFactor = 3.7; i.RenderType = ParticleRenderType.Strand; i.StrandPoints = 17;
			i.LodCountMultiplier = 0.5; i.LodSizeMultiplier = 0.85;
			var a = ParticleBudget.Estimate( i );
			var b = ParticleBudget.Estimate( i );
			Check( a.EffectiveParticleCount == b.EffectiveParticleCount, "count identical" );
			Check( a.EffectiveMeanSize == b.EffectiveMeanSize, "mean identical" );
			Check( a.QuadCount == b.QuadCount && a.TriangleCount == b.TriangleCount && a.VertexCount == b.VertexCount, "draw counts identical" );
			Check( a.CoveredArea == b.CoveredArea && a.OverdrawRatio == b.OverdrawRatio && a.WorstOverdrawRatio == b.WorstOverdrawRatio, "ratios identical" );
			Check( a.Verdict == b.Verdict && a.LargestContributor == b.LargestContributor, "verdict/contributor identical" );
			Check( a.Summary == b.Summary, "summary identical" );
		} );

		registry.Add( "particle-budget", "LOD scaling shrinks count and size", () =>
		{
			var i = Inp();
			i.ParticleCount = 1000; i.MeanSize = 10; i.MaxSize = 10;
			i.LodCountMultiplier = 0.5; i.LodSizeMultiplier = 0.5;
			var e = ParticleBudget.Estimate( i );
			Near( e.EffectiveParticleCount, 500, 0, "effective count" );
			Near( e.EffectiveMeanSize, 5, 0, "effective size" );
			Near( e.CoveredArea, 500 * 25, 0, "LOD covered area" );
			Near( e.OverdrawRatio, 0.0125, 0, "LOD ratio" );
			// P35 far-band shape: rate 0.25, size 0.7.
			var f = Inp();
			f.ParticleCount = 4000; f.MeanSize = 10; f.MaxSize = 10;
			f.LodCountMultiplier = 0.25; f.LodSizeMultiplier = 0.7;
			var g = ParticleBudget.Estimate( f );
			Near( g.EffectiveParticleCount, 1000, 0, "far-band count" );
			Near( g.EffectiveMeanSize, 7, 0, "far-band size" );
			Near( g.CoveredArea, 1000 * 49, 0, "far-band covered" );
		} );

		registry.Add( "particle-budget", "units label travels and screen area is explicit", () =>
		{
			var i = Inp(); i.SizeUnits = ParticleBudgetUnits.Inches;
			Check( ParticleBudget.Estimate( i ).SizeUnits == ParticleBudgetUnits.Inches, "inches label travels" );
			var p = Inp(); p.SizeUnits = ParticleBudgetUnits.Pixels;
			Check( ParticleBudget.Estimate( p ).SizeUnits == ParticleBudgetUnits.Pixels, "pixels label travels" );
			// A fresh input with no ScreenArea supplied is 0 and must fail.
			Throws( () => ParticleBudget.Estimate( new ParticleBudgetInput { ParticleCount = 10, MeanSize = 10, MaxSize = 10 } ), "unsupplied screen area fails" );
		} );
	}
}
