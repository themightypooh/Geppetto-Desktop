using System;
using System.Collections.Generic;

namespace Effigy;

/// <summary>
/// A paint layer in the feature tree.
///
/// WHERE THE PAINT LIVES: strokes in object space, replayed onto whatever the mesh currently is.
/// The texture atlas is a derived artifact — the same bet the rest of the kernel already made by
/// keeping the mesh a function of the feature history. Nothing else in the document holds paint, undo
/// is the feature tree's undo, and a stroke is one entry in the list.
///
/// EXECUTE REPLAYS THE STROKES ONTO A TEXTURE ATLAS. The dab — faces in radius, reject the far side
/// by its normal, falloff-weighted coverage — lives in PaintReplay, shared with the live session so a
/// stroke painted by hand and the same stroke rebuilt later produce identical texels. A texture atlas
/// rather than vertex colours because paint resolution must not equal mesh density: a bare box paints
/// at the same texel resolution a sculpted part does, which is the whole reason the vertex-colour
/// path was replaced.
///
/// THE ATLAS IS KEYED TO THE UV LAYOUT, which is the trap the vertex-colour cache never had. A
/// re-unwrap keeps the topology and moves every island, so a canvas cached on topology alone would be
/// handed back against a rearranged atlas — paint scattered onto unrelated faces, silently. The cache
/// is keyed on both the topology id and <see cref="AtlasId"/>.
///
/// ITS STALENESS GUARD IS COPIED FROM SculptFeature FOR THE SAME REASON. A paint session appends
/// strokes nowhere near the studio, so nothing calls MarkDirty and the rebuild would happily reuse
/// the cached body from before the stroke — the paint would stop following the brush, which reads as
/// "the paint tool does nothing" rather than as a caching bug. A revision counter bumped when the
/// stroke list changes, compared here to what the last rebuild built from, is the guard.
/// </summary>
public sealed class PaintFeature : Feature
{
	/// <summary>
	/// How many texels across the replayed canvas is.
	///
	/// A PARAMETER RATHER THAN A CONSTANT because it belongs to the document, the same way a sculpt
	/// cage's level does: a part with one small painted detail and one big painted wall wants two
	/// answers, and 1024 texels across a matchbox is enormous while across a 4000-unit part it is
	/// four texels per inch. Strokes are resolution-independent — points and radii, replayed — so a
	/// change re-replays and loses nothing.
	/// </summary>
	public readonly IntParam Resolution = new( "Resolution", 1024, 64, 4096 );

	public override string TypeName => "Paint";

	public override GeometryKind Accepts => GeometryKind.Body;

	public readonly BodySelectionParam Bodies = new( "Body" );

	/// <summary>
	/// LEGACY, READ BY NOTHING. Tint/Replace from the vertex-colour era, kept as a field only so
	/// documents saved with it still load and round-trip unchanged. A layer's real blend is
	/// <see cref="Mode"/>; this name could not be reused because a ChoiceParam saves its INDEX, and an
	/// old "Replace" (1) would have loaded as Multiply.
	/// </summary>
	public readonly ChoiceParam Blend = new( "Blend", new[] { "Tint", "Replace" } );

	/// <summary>How this layer combines with the paint below it. See <see cref="PaintLayers"/>.</summary>
	public readonly ChoiceParam Mode = new( "Blend Mode", Enum.GetNames<PaintBlendMode>() );

	/// <summary>How much of the layer shows, 0..1, on top of its own coverage and mask.</summary>
	public readonly FloatParam Opacity = new( "Opacity", 1f, 0f, 1f );

	/// <summary>
	/// Clipping mask: the layer shows only where the layer below it has paint (that layer's own
	/// coverage after its opacity and mask, or, when the layer below is clipped too, the base it clips
	/// to). Off by default, so every older document builds exactly as before.
	/// </summary>
	public readonly BoolParam ClipToBelow = new( "Clip To Below", false );

	/// <summary>Colour adjustment on this layer's colour, before it composites (non-destructive).
	/// Hue shift in degrees; Saturation and Brightness are multipliers; Levels are input black/white
	/// points and a midtone gamma. Defaults change nothing.</summary>
	public readonly FloatParam AdjustHue = new( "Adjust Hue", 0f, -180f, 180f );
	public readonly FloatParam AdjustSaturation = new( "Adjust Saturation", 1f, 0f, 2f );
	public readonly FloatParam AdjustBrightness = new( "Adjust Brightness", 1f, 0f, 2f );
	public readonly FloatParam LevelsBlack = new( "Levels Black", 0f, 0f, 1f );
	public readonly FloatParam LevelsWhite = new( "Levels White", 1f, 0f, 1f );
	public readonly FloatParam LevelsGamma = new( "Levels Gamma", 1f, 0.1f, 4f );

	public PaintAdjust Adjust => new( AdjustHue.Clamped, AdjustSaturation.Clamped, AdjustBrightness.Clamped,
		LevelsBlack.Clamped, MathF.Max( LevelsWhite.Clamped, LevelsBlack.Clamped + 0.01f ), LevelsGamma.Clamped );

	/// <summary>The clip coverage the last build used (resolution x resolution), or null when the layer
	/// is not clipped. The live paint session multiplies it into its preview.</summary>
	public float[] ClipMask { get; private set; }

	/// <summary>
	/// Whether the layer starts fully covered with the fill values below, rather than empty. A fill
	/// layer is how a whole surface gets one material — rust, dirt, wet sheen — that a mask then
	/// confines; strokes on the layer still paint over the fill.
	/// </summary>
	public readonly BoolParam Fill = new( "Fill Layer", false );

	public readonly BoolParam FillUseColor = new( "Use Color", true );
	public readonly Vec3Param FillColor = new( "Color", new Vec3( 0.42f, 0.32f, 0.24f ) );
	public readonly BoolParam FillUseRoughness = new( "Use Roughness", false );
	public readonly FloatParam FillRoughness = new( "Roughness", 0.8f, 0f, 1f );
	public readonly BoolParam FillUseMetalness = new( "Use Metallic", false );
	public readonly FloatParam FillMetalness = new( "Metallic", 0f, 0f, 1f );
	public readonly BoolParam FillUseHeight = new( "Use Height", false );
	public readonly FloatParam FillHeight = new( "Height", 0.5f, 0f, 1f );

	/// <summary>Where the layer's mask comes from, before mask painting. Option text matches
	/// <see cref="PaintMaskGenerator"/>, spaced for reading.</summary>
	public readonly ChoiceParam MaskGenerator = new( "Mask Generator", MaskOptions );

	public static readonly string[] MaskOptions = { "None", "Edge Wear", "Dirt", "Gradient", "Material ID",
		"Gradient Axis", "Bands", "Spots", "Cavity Ridge" };

	/// <summary>Where the generated mask switches from hidden to shown, 0..1.</summary>
	public readonly FloatParam MaskLevel = new( "Mask Level", 0.5f, 0f, 1f );

	/// <summary>How wide the switch is: 0 is a hard edge, 1 a long fade.</summary>
	public readonly FloatParam MaskSoftness = new( "Mask Softness", 0.3f, 0f, 1f );

	/// <summary>Swap what the mask shows and hides.</summary>
	public readonly BoolParam InvertMask = new( "Invert Mask", false );

	/// <summary>The material slot the Material ID generator selects.</summary>
	public readonly IntParam MaskSlot = new( "Mask Material Slot", 0, 0, 63 ) { Slider = false };

	/// <summary>One-line layer recipe, Lane B. Empty means "use the dialog parameters as they are".
	/// When set, Execute parses it first and it wins for every field it mentions, so a script can
	/// author one layer in a single string instead of a dozen parameters or a binary stroke array.
	/// Directives are separated by ';'. In a script, quote the whole recipe value, because a
	/// directive is itself a run of key=value tokens separated by spaces. Forms:
	///   fill color=#7d95a6 rough=0.22 metal=0.18 height=0.5
	///   gradient axis=0,0,1 from=-2.2 to=1.8 color=#dce4ea rough=0.18
	///   bands axis=0,1,0 from=-10 to=16 count=18 width=0.35 warp=0.18 color=#16222e
	///   spots radius=0.5 spacing=2 jitter=0.35 color=#ffffff
	///   edgewear | dirt | cavityridge   (each takes color=/rough=/metal= too)
	///   mask=gradientaxis | opacity=0.8 | blend=multiply | size=2048
	/// A "gradient"/"bands"/"spots"/"edgewear"/"dirt"/"cavityridge" directive is one layer: its
	/// colour is the layer's fill and its pattern is the layer's mask. A multi-layer paint is
	/// therefore several Paint features in the tree, one recipe each, stacked in tree order —
	/// that is how the kernel already stacks layers, and it is what the 4-layer gate builds.
	/// </summary>
	public readonly StringParam Recipe = new( "Recipe", "" );

	/// <summary>Direction the Gradient Axis generator measures along. A zero vector falls back to +Z.</summary>
	public readonly Vec3Param GradientAxis = new( "Gradient Axis", new Vec3( 0, 0, 1 ) );
	/// <summary>Axis coordinate the Gradient Axis generator maps to 0. Equal to End means "use the mesh extent".</summary>
	public readonly FloatParam GradientStart = new( "Gradient Start", 0f );
	/// <summary>Axis coordinate the Gradient Axis generator maps to 1. Equal to Start means "use the mesh extent".</summary>
	public readonly FloatParam GradientEnd = new( "Gradient End", 0f );
	/// <summary>Curve offset on the Gradient Axis generator: raw t becomes t^(1+Falloff), so a positive
	/// falloff holds the low end longer and a negative one brings the high end in sooner. 0 is linear.</summary>
	public readonly FloatParam GradientFalloff = new( "Gradient Falloff", 0f, -0.9f, 2f );

	/// <summary>Direction the Bands generator measures along. A zero vector falls back to +Y.</summary>
	public readonly Vec3Param BandsAxis = new( "Bands Axis", new Vec3( 0, 1, 0 ) );
	/// <summary>Distance between band starts, in mesh units.</summary>
	public readonly FloatParam BandsSpacing = new( "Bands Spacing", 2f, 0.01f );
	/// <summary>Band width as a fraction of one period, 0..1.</summary>
	public readonly FloatParam BandsWidth = new( "Bands Width", 0.35f, 0f, 1f );
	/// <summary>Direction along which bands narrow, from full width to half width across the mesh extent.
	/// Zero means no taper.</summary>
	public readonly Vec3Param BandsTaperAxis = new( "Bands Taper Axis", Vec3.Zero );
	/// <summary>Domain warp amplitude in mesh units: bands wobble sinusoidally across the surface instead
	/// of running straight, so a stripe reads organic rather than machined.</summary>
	public readonly FloatParam BandsWarp = new( "Bands Warp", 0f );
	/// <summary>Axis coordinate bands start at. Equal to Range Max means "no range limit".</summary>
	public readonly FloatParam BandsRangeMin = new( "Bands Range Min", 0f );
	/// <summary>Axis coordinate bands stop at. Equal to Range Min means "no range limit".</summary>
	public readonly FloatParam BandsRangeMax = new( "Bands Range Max", 0f );

	/// <summary>Spot radius in mesh units.</summary>
	public readonly FloatParam SpotsRadius = new( "Spots Radius", 0.5f, 0.01f );
	/// <summary>Spacing of the spot grid in mesh units.</summary>
	public readonly FloatParam SpotsSpacing = new( "Spots Spacing", 2f, 0.1f );
	/// <summary>How far a spot centre wanders from its grid point, as a fraction of the spacing.</summary>
	public readonly FloatParam SpotsJitter = new( "Spots Jitter", 0.35f, 0f, 1f );
	/// <summary>Ring width in mesh units. 0 means filled discs; above 0 the spot is a ring of this width
	/// centred on the radius, so scales and freckle rings are one parameter, not a second feature.</summary>
	public readonly FloatParam SpotsRingWidth = new( "Spots Ring Width", 0f, 0f );
	/// <summary>Seed for the spot grid jitter, so the same recipe always lands the same spots.</summary>
	public readonly IntParam SpotsSeed = new( "Spots Seed", 1, 0 ) { Slider = false };

	/// <summary>
	/// Whether stroke patterns are sampled tri-planar (see <see cref="TriPlanar"/>) instead of
	/// plain: with a pattern set, each texel's coverage is its falloff weight times the pattern
	/// sampled in world space at that texel's surface point.
	/// </summary>
	public readonly BoolParam TriPlanar = new( "Tri-Planar Pattern", false );

	/// <summary>The pattern a tri-planar layer samples. None means "no pattern", even with Tri-Planar on.</summary>
	public readonly ChoiceParam Pattern = new( "Pattern", new[] { "None", "Stripes", "Checker", "Spots" } );

	/// <summary>World units per pattern repeat for the pattern.</summary>
	public readonly FloatParam PatternScale = new( "Pattern Scale", 1f, 0.01f );

	/// <summary>Blend sharpness for the pattern.</summary>
	public readonly FloatParam PatternSharpness = new( "Pattern Sharpness", 4f, 0.5f, 16f );

	/// <summary>The tri-planar pattern this layer samples, or None.</summary>
	public TriPlanarPattern PatternKind => (TriPlanarPattern)Math.Clamp( Pattern.Index, 0, Enum.GetValues<TriPlanarPattern>().Length - 1 );

	/// <summary>World-space tri-planar pattern modulating this layer's strokes (P2).
	/// None is the plain path, exactly as before; Fbm/Worley/Ridged are the 3D
	/// noise patterns from TriPlanar, sampled on world position so they do not
	/// restart at a UV island boundary. Order matches TriPlanarPattern.</summary>
	public readonly ChoiceParam TriPattern = new( "Tri Pattern", new[] { "None", "Stripes", "Checker", "Spots", "Fbm", "Worley", "Ridged" } );

	/// <summary>World units per pattern repeat for the tri-planar pattern.</summary>
	public readonly FloatParam TriScale = new( "Tri Scale", 1f, 0.01f );

	/// <summary>Blend sharpness for the tri-planar pattern (see TriPlanar.Weights).</summary>
	public readonly FloatParam TriSharpness = new( "Tri Sharpness", 4f, 0.5f, 16f );

	/// <summary>Cross-layer anchor (synced from s&box): None leaves the mask alone; Below Height reads
	/// the painted height of the layers below and confines this layer to it.</summary>
	public readonly ChoiceParam AnchorSource = new( "Anchor Source", new[] { "None", "Below Height" } );

	/// <summary>Peaks shows where the anchor is high, Valleys where it is low.</summary>
	public readonly ChoiceParam AnchorMode = new( "Anchor Mode", new[] { "Peaks", "Valleys" } );

	/// <summary>Where the anchor's shaped range switches from hidden to shown, 0..1.</summary>
	public readonly FloatParam AnchorLevel = new( "Anchor Level", 0.5f, 0f, 1f );

	/// <summary>How wide the anchor's switch is: 0 is a hard edge, 1 a long fade.</summary>
	public readonly FloatParam AnchorSoftness = new( "Anchor Softness", 0.3f, 0f, 1f );

	/// <summary>Whether the anchor reads the painted height of the layers below.</summary>
	public bool AnchorOn => AnchorSource.Index == Array.IndexOf( AnchorSource.Options, "Below Height" );

	/// <summary>The anchor's shaped factor, or null with no anchor / no height below.</summary>
	public float[] AnchorMask { get; private set; }

	/// <summary>
	/// Every layer setting, for the script and the feature dialog. Blend is carried for old documents
	/// and deliberately absent: showing a combo that changes nothing is a bug report waiting to be filed.
	/// </summary>
	public override IReadOnlyList<IParam> Parameters => new IParam[]
	{
		Bodies, Resolution, Mode, Opacity, ClipToBelow,
		AdjustHue, AdjustSaturation, AdjustBrightness, LevelsBlack, LevelsWhite, LevelsGamma,
		Fill, FillUseColor, FillColor, FillUseRoughness, FillRoughness, FillUseMetalness, FillMetalness, FillUseHeight, FillHeight,
		MaskGenerator, MaskLevel, MaskSoftness, InvertMask, MaskSlot,
		Recipe, GradientAxis, GradientStart, GradientEnd, GradientFalloff,
		BandsAxis, BandsSpacing, BandsWidth, BandsTaperAxis, BandsWarp, BandsRangeMin, BandsRangeMax,
		SpotsRadius, SpotsSpacing, SpotsJitter, SpotsRingWidth, SpotsSeed,
		TriPlanar, Pattern, PatternScale, PatternSharpness,
		TriPattern, TriScale, TriSharpness,
		AnchorSource, AnchorMode, AnchorLevel, AnchorSoftness,
	};

	public PaintBlendMode BlendMode => (PaintBlendMode)Math.Clamp( Mode.Index, 0, Enum.GetValues<PaintBlendMode>().Length - 1 );

	public PaintMaskGenerator Generator => (PaintMaskGenerator)Math.Clamp( MaskGenerator.Index, 0, MaskOptions.Length - 1 );

	/// <summary>
	/// The strokes, in the order they were painted.
	///
	/// NULL UNTIL THE FIRST STROKE LANDS, the same "not yet populated" idiom SculptFeature uses for
	/// its levels. A never-painted feature serialises to nothing at all: StudioDocument writes a null
	/// field as absent, and the reflection sweep in DocumentTests round-trips a null list as null.
	/// </summary>
	public List<PaintStroke> Strokes;

	/// <summary>
	/// The texel strokes on this layer, in the order they were painted — the durable half of
	/// texture painting. A texel stroke names texels of the layer's canvas directly (see
	/// <see cref="TexelStroke"/>), and Execute replays it after the mesh strokes on every
	/// rebuild, so a direct texel scrub is no longer undone by the next rebuild: the scrub
	/// itself is the log entry. Null until the first one lands, like <see cref="Strokes"/>.
	/// Serialised by StudioDocument as texstroke/texerase lines, one per stroke.
	/// </summary>
	public List<TexelStroke> TexelStrokes;

	/// <summary>Bumped each time the stroke list changes, so <see cref="IsStale"/> can notice
	/// without anyone remembering to call MarkDirty.</summary>
	public int Revision { get; private set; }

	// The revision this feature last built from. See the class comment — a stroke lands nowhere near
	// the studio, so nothing calls MarkDirty and this is what catches it.
	int _builtRevision = -1;

	// The replay cache: the layer's own maps last produced, and the topology + atlas + revision +
	// resolution they were produced from. Keyed on topology (vertex count and face indices,
	// deliberately not positions), the atlas (every corner UV, so a re-unwrap invalidates it), the
	// revision (so a new stroke does) and the resolution (so changing it re-replays rather than serving
	// a stale canvas at the old size). A parametric edit that moves geometry without changing
	// structure, UVs or resolution reuses the maps rather than re-replaying.
	PaintLayerMaps _strokeMaps;
	long _topologyId;
	long _atlasId;
	int _canvasRevision = -1;
	int _canvasResolution = -1;
	string _triKey;

	// The layer with its fill under the strokes, keyed on the stroke maps and the fill settings.
	PaintLayerMaps _layerMaps;

	// The texel-applied copy of _layerMaps, and the _layerMaps it was copied from. See
	// Execute: the copy exists so a texel stroke is applied once per build, not once per
	// rebuild, and so the cached _layerMaps stays pristine for the next build to copy from.
	PaintLayerMaps _texelMaps;
	PaintLayerMaps _texelFrom;
	PaintLayerMaps _layerFrom;
	string _fillKey;

	// The generated mask, which is the expensive part (a bake), keyed on everything it reads —
	// including positions, because curvature and occlusion move when the surface does.
	float[] _generated;
	string _generatedKey;

	// The composite last put on the body, and what it was composited from. The backdrop is keyed by
	// REFERENCE: the layer below hands back its own cached canvas while nothing below changed, and
	// a new object whenever something did.
	PaintCanvas _cachedCanvas;
	PaintCanvas _compositeBelow;
	PaintLayerMaps _compositeLayer;
	string _compositeKey;

	// 2026-11-14: a suppressed layer never runs, so it never records a build; counting it stale made every
	// rebuild re-run everything from the first switched-off layer down. Turning it back on still reads stale.
	public override bool IsStale => !Suppressed && Revision != _builtRevision;

	/// <summary>The paint this feature put on its body — the layers below with this one composited
	/// over them — or null before a build. The editor shows it after a rebuild.</summary>
	public PaintCanvas Canvas => _cachedCanvas;

	/// <summary>The body's paint from the layers BELOW this one, as the last build found it. The live
	/// paint session shows this under the layer being painted, so painting a layer in the middle of a
	/// stack shows the stack.</summary>
	public PaintCanvas Below { get; private set; }

	/// <summary>This layer's own content (fill and strokes), before compositing, or null before a build.</summary>
	public PaintLayerMaps Layer => TexelStrokes is { Count: > 0 } && _texelMaps is not null ? _texelMaps : _layerMaps;

	/// <summary>The final mask (generator, mask paint, invert) the last build used, or null when
	/// the layer shows everywhere.</summary>
	public float[] Mask { get; private set; }

	/// <summary>The generator's mask after Mask Level and Softness, without mask paint or invert —
	/// what the live session combines with its own mask strokes. Null with no generator.</summary>
	public float[] GeneratorMask { get; private set; }

	/// <summary>Append a stroke and mark the feature stale, so the next rebuild replays it. The list
	/// is lazily created here so a fresh feature never has to check for null before painting.</summary>
	public void AddStroke( PaintStroke stroke )
	{
		(Strokes ??= new()).Add( stroke );
		Revision++;
	}

	/// <summary>Append a texel stroke and mark the feature stale, exactly like
	/// <see cref="AddStroke"/>: the revision bump is what moves the desktop's paint
	/// fingerprint, so the next snapshot is built fresh instead of serving the pre-stroke
	/// one. This is the commit a texture-paint caller makes — scrubbing
	/// <c>body.Mesh.Paint</c> directly is preview only and does not call this.</summary>
	public void AddTexelStroke( TexelStroke stroke )
	{
		(TexelStrokes ??= new()).Add( stroke );
		Revision++;
	}

	/// <summary>
	/// Replace the whole stroke list — undo/redo's route in.
	///
	/// The revision is bumped, not merely the list swapped, because the replay cache is keyed on it: a
	/// plain assignment would leave <see cref="Revision"/> unchanged, the cache would see no reason to
	/// re-render, and the model would keep serving paint the restored strokes do not describe. The
	/// strokes themselves are copied by reference — they are immutable once painted, so sharing them
	/// across undo snapshots is the correct and cheapest read.
	/// </summary>
	public void ReplaceStrokes( IReadOnlyList<PaintStroke> strokes )
	{
		Strokes = strokes is null ? null : new List<PaintStroke>( strokes );
		Revision++;
	}

	protected override void Execute( FeatureContext ctx )
	{
		var targets = RequireBodies( ctx, Bodies );

		// ONE BODY, LOUDLY. An empty selection matches every body, so a two-body studio used to
		// walk in here, skip the replay, and leave both canvases untouched with no error — paint
		// that looks like it ran and did nothing. Fail at the door so the editor's "pick one"
		// prompt and the kernel agree.
		if ( targets.Count != 1 )
		{
			Fail(
				"Paint paints one body at a time",
				$"This feature matched {targets.Count} bodies and a paint layer is one stroke list, one atlas.",
				"Pick one body in the Parts list",
				"Add a Paint per body rather than one Paint over all of them" );
		}

		var mesh = targets[0].Mesh;
		var below = mesh.Paint;
		Below = below;

		// A recipe is the layer in one string; it wins for every field it mentions, and it is
		// parsed here — before fill/mask are read — so the rest of Execute is unchanged.
		ApplyRecipe();

		var hasStrokes = Strokes is { Count: > 0 };
		var hasTexel = TexelStrokes is { Count: > 0 };
		var fill = Fill.Value && (FillUseColor.Value || FillUseRoughness.Value || FillUseMetalness.Value || FillUseHeight.Value);
		var generator = Generator;

		if ( Fill.Value && !fill )
		{
			Warn( "This fill layer fills nothing",
				"Fill Layer is on but every channel (Use Color, Use Roughness, Use Metallic, Use Height) is off.",
				"Turn on the channels the fill should write",
				"Turn Fill Layer off if you meant to paint strokes only" );
		}

		if ( (fill || generator != PaintMaskGenerator.None) && !PaintMasks.HasUVs( mesh ) )
		{
			FailOn( fill ? "Fill Layer" : "Mask Generator",
				"This paint layer has nowhere to go",
				"The body has no UV layout, so no texel belongs to any face — a fill or a generated mask needs one.",
				"Add a UV Project in Unwrap mode above this layer",
				"Or enter Paint on the body once — it unwraps it for you" );
		}

		var resolution = Resolution.Clamped;
		var topology = MultiresSculpt.TopologyId( mesh );
		var atlas = AtlasId.Of( mesh );

		// The generated mask is made even for a layer with nothing on it yet, so the live session
		// can show it the moment the first stroke lands rather than after the next rebuild.
		// The Lane B generators (indices past the old PaintMaskGenerator enum) are generated here,
		// in this file, because PaintMasks.Generate only knows the old enum: its switch would
		// answer null for an unnamed value. Their cache key carries every parameter they read, so
		// changing a spacing, a seed or a taper re-renders instead of serving the old mask.
		bool laneBGenerator = MaskGenerator.Index > (int)PaintMaskGenerator.MaterialId;
		var generatedKey = generator == PaintMaskGenerator.None
			? "none"
			: laneBGenerator
				? $"laneb:{MaskGenerator.Index}|{GradientAxis.Value.x},{GradientAxis.Value.y},{GradientAxis.Value.z}|"
					+ $"{GradientStart.Value}|{GradientEnd.Value}|{GradientFalloff.Value}|"
					+ $"{BandsAxis.Value.x},{BandsAxis.Value.y},{BandsAxis.Value.z}|{BandsSpacing.Clamped}|{BandsWidth.Clamped}|"
					+ $"{BandsTaperAxis.Value.x},{BandsTaperAxis.Value.y},{BandsTaperAxis.Value.z}|{BandsWarp.Value}|"
					+ $"{BandsRangeMin.Value}|{BandsRangeMax.Value}|"
					+ $"{SpotsRadius.Clamped}|{SpotsSpacing.Clamped}|{SpotsJitter.Clamped}|{SpotsRingWidth.Clamped}|{SpotsSeed.Clamped}|"
					+ $"{resolution}|{topology}|{atlas}|{PositionHash( mesh )}"
				: $"{generator}|{MaskSlot.Clamped}|{resolution}|{topology}|{atlas}|{PositionHash( mesh )}";

		if ( _generatedKey != generatedKey )
		{
			_generated = laneBGenerator ? GenerateLaneBMask( mesh, resolution ) : PaintMasks.Generate( mesh, resolution, generator, MaskSlot.Clamped );
			_generatedKey = generatedKey;
		}

		var shaped = _generated is null ? null : PaintLayers.Shape( (float[])_generated.Clone(), MaskLevel.Clamped, MaskSoftness.Clamped );
		GeneratorMask = shaped;

		// Clipping mask: the base coverage the canvas below carries, at this layer's resolution.
		// Clipped to a stack with no base (nothing painted below) the layer shows nowhere.
		ClipMask = null;

		if ( ClipToBelow.Value )
		{
			var cb = below?.ClipBase;
			ClipMask = cb is null
				? new float[resolution * resolution]
				: PaintLayers.Resample( cb, below.Width, below.Height, resolution, resolution );
		}

		if ( !hasStrokes && !hasTexel && !fill )
		{
			// A fresh layer with nothing on it yet. Not a failure — it is how every layer starts —
			// and the body's paint passes through untouched.
			_cachedCanvas = below;
			AnchorMask = null;
			Mask = null;
			_builtRevision = Revision;
			return;
		}

		var useTri = (TriPlanar.Value && PatternKind != TriPlanarPattern.None) || TriPattern.Index > 0;
		var triKind = (TriPlanar.Value && PatternKind != TriPlanarPattern.None) ? PatternKind : (TriPlanarPattern)TriPattern.Index;
		var triScale = (TriPlanar.Value && PatternKind != TriPlanarPattern.None) ? PatternScale.Clamped : TriScale.Clamped;
		var triSharpness = (TriPlanar.Value && PatternKind != TriPlanarPattern.None) ? PatternSharpness.Clamped : TriSharpness.Clamped;
		var triKey = $"{TriPlanar.Value}|{Pattern.Index}|{PatternScale.Clamped}|{PatternSharpness.Clamped}|{TriPattern.Index}|{TriScale.Clamped}|{TriSharpness.Clamped}";

		if ( _strokeMaps is null || _topologyId != topology || _atlasId != atlas
			|| _canvasRevision != Revision || _canvasResolution != resolution || _triKey != triKey )
		{
			_strokeMaps = useTri
				? PaintLayers.ReplayTriPlanar( mesh, Strokes, resolution, triKind, triScale, triSharpness )
				: PaintLayers.Replay( mesh, Strokes, resolution );
			_triKey = triKey;
			_topologyId = topology;
			_atlasId = atlas;
			_canvasRevision = Revision;
			_canvasResolution = resolution;
		}

		var fillKey = fill
			? $"{FillUseColor.Value}|{FillColor.Value.x}|{FillColor.Value.y}|{FillColor.Value.z}|{FillUseRoughness.Value}|{FillRoughness.Clamped}|"
				+ $"{FillUseMetalness.Value}|{FillMetalness.Clamped}|{FillUseHeight.Value}|{FillHeight.Clamped}"
			: "none";

		if ( _layerMaps is null || !ReferenceEquals( _layerFrom, _strokeMaps ) || _fillKey != fillKey )
		{
			_layerMaps = fill ? WithFill( _strokeMaps, resolution ) : _strokeMaps;
			_layerFrom = _strokeMaps;
			_fillKey = fillKey;
		}

		// Texel strokes land on a COPY of the layer maps, never on _layerMaps itself:
		// _layerMaps is cached and reused across rebuilds, and painting into it in place
		// would re-apply the same stroke on every rebuild after the first. The copy is
		// rebuilt exactly when _layerMaps is, so a texel stroke is applied once per build
		// and the cached copy is reused, unmodified, until something actually changes.
		// A layer with no texel strokes skips the copy entirely and behaves byte-for-byte
		// as before.
		var effectiveMaps = _layerMaps;

		if ( hasTexel )
		{
			if ( _texelMaps is null || !ReferenceEquals( _texelFrom, _layerMaps ) )
			{
				_texelMaps = CloneMaps( _layerMaps );
				_texelFrom = _layerMaps;
				TexelReplay.ApplyAll( _texelMaps, TexelStrokes );
			}

			effectiveMaps = _texelMaps;
		}

		Mask = PaintLayers.FinalMask( shaped, effectiveMaps.MaskPaint, InvertMask.Value, resolution, resolution );

		// Cross-layer anchor: the painted height below (grey times coverage) multiplies the mask.
		AnchorMask = null;

		if ( AnchorOn )
		{
			var height = below?.Channel( PaintChannel.Height );

			if ( height is null )
			{
				Warn( "Anchor has no height below",
					"Anchor Source is Below Height but no layer below this one painted height, so the anchor has nothing to read.",
					"Paint height on a lower layer first",
					"Or turn Anchor Source back to None" );
			}
			else
			{
				var h = height.Width == resolution && height.Height == resolution
					? height
					: PaintLayers.Resample( height, resolution, resolution );
				var vals = new float[resolution * resolution];
				var valleys = AnchorMode.Index == Array.IndexOf( AnchorMode.Options, "Valleys" );

				for ( var i = 0; i < vals.Length; i++ )
				{
					var o = i * 4;
					var v = (h.Rgba[o] / 255f) * (h.Rgba[o + 3] / 255f);
					vals[i] = valleys ? 1f - v : v;
				}

				AnchorMask = PaintLayers.Shape( vals, AnchorLevel.Clamped, AnchorSoftness.Clamped );
				Mask = Mask is null ? AnchorMask : MultiplyMasks( Mask, AnchorMask );
			}
		}

		if ( ClipMask is not null )
		{
			Mask = Mask is null ? ClipMask : MultiplyMasks( Mask, ClipMask );

			if ( below?.ClipBase is null )
			{
				Warn( "Clipped to nothing",
					"Clip To Below is on but no layer below this one has paint, so there is nothing to clip to and this layer shows nowhere.",
					"Paint on the layer below first",
					"Or turn Clip To Below off" );
			}
		}

		if ( !effectiveMaps.HasSurface )
		{
			Warn( "This layer only has mask paint",
				"Every stroke on it was painted on the Layer Mask, so there is nothing for the mask to show.",
				"Switch the channel to Base Color (or another surface channel) and paint",
				"Or turn on Fill Layer so the mask confines a fill" );
		}
		else if ( Opacity.Clamped <= 0f )
		{
			Warn( "This layer is invisible",
				"Its Opacity is 0, so it adds nothing to the surface.",
				"Raise Opacity",
				"Suppress the layer instead if you meant to hide it" );
		}
		else if ( Mask is not null && MaxOf( Mask ) < 1e-3f )
		{
			Warn( "The layer mask hides the whole layer", MaskEmptyCause( mesh ),
				generator == PaintMaskGenerator.MaterialId ? "Pick a Mask Material Slot a face is on" : "Lower Mask Level or raise Mask Softness",
				"Turn Invert Mask off (or on)",
				"Paint white on the Layer Mask where it should show" );
		}

		var compositeKey = $"{BlendMode}|{Opacity.Clamped}|{MaskLevel.Clamped}|{MaskSoftness.Clamped}|{InvertMask.Value}|{generatedKey}|{AnchorSource.Index}|{AnchorMode.Index}|{AnchorLevel.Clamped}|{AnchorSoftness.Clamped}|{ClipToBelow.Value}|{Adjust}";

		if ( _cachedCanvas is null || !ReferenceEquals( _compositeBelow, below ) || !ReferenceEquals( _compositeLayer, effectiveMaps )
			|| _compositeKey != compositeKey )
		{
			_cachedCanvas = PaintLayers.CompositeSet( below, Adjust.Apply( effectiveMaps ), BlendMode, Opacity.Clamped, Mask );

			// What a clipped layer above clips to: a clipped layer hands its base on, an unclipped one
			// becomes the base. Only set on a canvas this layer made (CompositeSet hands the backdrop
			// back unchanged when the layer has no surface, and that one belongs to the layer below).
			if ( !ReferenceEquals( _cachedCanvas, below ) )
				_cachedCanvas.ClipBase = ClipToBelow.Value
					? below?.ClipBase is { } baseCov ? PaintLayers.Resample( baseCov, below.Width, below.Height, _cachedCanvas.Width, _cachedCanvas.Height ) : null
					: OwnCoverage( effectiveMaps, Opacity.Clamped, Mask, _cachedCanvas.Width, _cachedCanvas.Height );

			_compositeBelow = below;
			_compositeLayer = effectiveMaps;
			_compositeKey = compositeKey;
		}

		mesh.Paint = _cachedCanvas;

		// Last, so a failure above leaves the feature stale and the next rebuild tries again.
		_builtRevision = Revision;
	}


	/// <summary>A deep copy of a layer's maps, channel by channel. PaintCanvas.Clone also
	/// clones companions, but layer maps carry their channels as separate fields with null
	/// companions, so this is just the canvases, copied — colour, roughness, metalness, height,
	/// emissive, opacity and normal, plus the mask. Normal is cloned here too: Mobile's
	/// CloneMaps drops it, which loses painted normals on a copy; Desktop keeps them.</summary>
	static PaintLayerMaps CloneMaps( PaintLayerMaps maps )
	{
		if ( maps is null )
			return null;

		return new PaintLayerMaps
		{
			Color = maps.Color?.Clone(),
			Roughness = maps.Roughness?.Clone(),
			Metalness = maps.Metalness?.Clone(),
			Height = maps.Height?.Clone(),
			Emissive = maps.Emissive?.Clone(),
			Opacity = maps.Opacity?.Clone(),
			Normal = maps.Normal?.Clone(),
			MaskPaint = maps.MaskPaint?.Clone(),
			Size = maps.Size,
		};
	}

	void SetAnchorChoice( ChoiceParam param, string name, string what, string options )
	{
		var key = name.Replace( "_", " " ).Replace( "-", " " ).Trim();
		var i = Array.FindIndex( param.Options, o => string.Equals( o, key, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( o.Replace( " ", "" ), key.Replace( " ", "" ), StringComparison.OrdinalIgnoreCase ) );

		if ( i >= 0 ) param.Index = i;
		else FailOn( "Recipe", $"Unknown {what} '{name}'.", $"Choices are: {options}.", "Check the spelling, or clear the Recipe" );
	}

	/// <summary>Element-wise product of two masks into a new array; neither input is touched.</summary>
	/// <summary>This layer's own alpha per texel at width x height: the most covered surface channel,
	/// times opacity and mask. What a clipped layer above is confined to.</summary>
	static float[] OwnCoverage( PaintLayerMaps maps, float opacity, float[] mask, int width, int height )
	{
		var n = width * height;
		var cov = new float[n];

		foreach ( var c in new[] { maps.Color, maps.Roughness, maps.Metalness, maps.Height, maps.Emissive, maps.Opacity } )
		{
			if ( c is null ) continue;
			var src = c.Width == width && c.Height == height ? c : PaintLayers.Resample( c, width, height );

			for ( var i = 0; i < n; i++ )
				cov[i] = MathF.Max( cov[i], src.Rgba[i * 4 + 3] / 255f );
		}

		var m = mask is not null && mask.Length != n ? null : mask;

		for ( var i = 0; i < n; i++ )
			cov[i] *= opacity * (m is null ? 1f : m[i]);

		return cov;
	}

	static float[] MultiplyMasks( float[] a, float[] b )
	{
		var n = Math.Min( a.Length, b.Length );
		var r = new float[n];

		for ( var i = 0; i < n; i++ )
			r[i] = a[i] * b[i];

		return r;
	}

	/// <summary>Parse <see cref="Recipe"/> into the dialog parameters. No-op when the recipe is empty,
	/// so every existing document and script builds exactly as before.</summary>
	void ApplyRecipe()
	{
		var recipe = Recipe.Value;

		if ( string.IsNullOrWhiteSpace( recipe ) )
			return;

		foreach ( var directive in recipe.Split( ';' ) )
		{
			var tokens = directive.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries );

			if ( tokens.Length == 0 )
				continue;

			// A directive is either a bare word (fill, gradient, bands, ...) followed by key=value
			// arguments, or a single key=value on its own (mask=bands, opacity=0.8, size=2048).
			var head = tokens[0].ToLowerInvariant();
			var args = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
			var firstArg = 1;

			if ( tokens[0].IndexOf( '=' ) > 0 )
			{
				var eq0 = tokens[0].IndexOf( '=' );
				head = tokens[0][..eq0].ToLowerInvariant();
				args[head] = tokens[0][(eq0 + 1)..];
				firstArg = 1;
			}

			for ( var i = firstArg; i < tokens.Length; i++ )
			{
				var eq = tokens[i].IndexOf( '=' );

				if ( eq <= 0 )
					FailOn( "Recipe", $"Recipe directive '{tokens[0]}' has a token '{tokens[i]}' with no '='.",
						"Every argument in a recipe directive is key=value.",
						"Write it as e.g. fill color=#7d95a6 rough=0.22",
						"Or clear the Recipe and use the dialog parameters instead" );

				args[tokens[i][..eq]] = tokens[i][(eq + 1)..];
			}

			switch ( head )
			{
				case "fill":
					ApplyFillArgs( args );
					break;
				case "gradient":
					SetMask( "Gradient Axis" );
					ApplyAxisArgs( args, GradientAxis, GradientStart, GradientEnd, GradientFalloff );
					ApplyFillArgs( args );
					break;
				case "bands":
					SetMask( "Bands" );
					if ( args.TryGetValue( "axis", out var bAxis ) ) BandsAxis.Value = ParseVec3( bAxis );
					if ( args.TryGetValue( "spacing", out var bSpacing ) ) BandsSpacing.Value = ParseFloat( bSpacing );
					if ( args.TryGetValue( "width", out var bWidth ) ) BandsWidth.Value = ParseFloat( bWidth );
					if ( args.TryGetValue( "warp", out var bWarp ) ) BandsWarp.Value = ParseFloat( bWarp );
					if ( args.TryGetValue( "taper", out var bTaper ) ) BandsTaperAxis.Value = ParseVec3( bTaper );
					if ( args.TryGetValue( "taperaxis", out var bTaperAxis ) ) BandsTaperAxis.Value = ParseVec3( bTaperAxis );
					if ( args.TryGetValue( "from", out var bFrom ) ) BandsRangeMin.Value = ParseFloat( bFrom );
					if ( args.TryGetValue( "to", out var bTo ) ) BandsRangeMax.Value = ParseFloat( bTo );
					if ( args.TryGetValue( "count", out var bCount ) && args.TryGetValue( "from", out _) && args.TryGetValue( "to", out _ )
						&& ParseFloat( bCount ) > 0 && BandsRangeMax.Value > BandsRangeMin.Value )
						BandsSpacing.Value = (BandsRangeMax.Value - BandsRangeMin.Value) / ParseFloat( bCount );
					ApplyFillArgs( args );
					break;
				case "spots":
					SetMask( "Spots" );
					if ( args.TryGetValue( "radius", out var sRadius ) ) SpotsRadius.Value = ParseFloat( sRadius );
					if ( args.TryGetValue( "spacing", out var sSpacing ) ) SpotsSpacing.Value = ParseFloat( sSpacing );
					if ( args.TryGetValue( "jitter", out var sJitter ) ) SpotsJitter.Value = ParseFloat( sJitter );
					if ( args.TryGetValue( "ringwidth", out var sRing ) ) SpotsRingWidth.Value = ParseFloat( sRing );
					if ( args.TryGetValue( "seed", out var sSeed ) ) SpotsSeed.Value = (int)ParseFloat( sSeed );
					ApplyFillArgs( args );
					break;
				case "edgewear":
					SetMask( "Edge Wear" );
					ApplyFillArgs( args );
					break;
				case "dirt":
					SetMask( "Dirt" );
					ApplyFillArgs( args );
					break;
				case "cavityridge":
					SetMask( "Cavity Ridge" );
					ApplyFillArgs( args );
					break;
				case "mask":
					if ( !args.TryGetValue( "mask", out var maskName ) && !args.TryGetValue( "generator", out maskName ) )
						FailOn( "Recipe", "A mask directive needs mask=<name>.",
							"Write e.g. mask=bands or mask=gradientaxis.",
							"Or use a pattern directive (bands, spots, gradient) instead" );
					SetMask( maskName );
					break;
				case "opacity":
					if ( args.TryGetValue( "opacity", out var op ) ) Opacity.Value = ParseFloat( op );
					break;
				case "blend":
					if ( args.TryGetValue( "blend", out var blend ) && Enum.TryParse<PaintBlendMode>( blend, true, out var mode ) )
						Mode.Index = (int)mode;
					break;
				case "size":
				case "resolution":
					if ( args.TryGetValue( "size", out var size ) ) Resolution.Value = (int)ParseFloat( size );
					if ( args.TryGetValue( "resolution", out var res ) ) Resolution.Value = (int)ParseFloat( res );
					break;
				case "triplanar":
					if ( !args.TryGetValue( "pattern", out var tPat ) ) args.TryGetValue( "triplanar", out tPat );
					if ( tPat is not null ) SetAnchorChoice( Pattern, tPat, "pattern", "None, Stripes, Checker, Spots" );
					if ( args.TryGetValue( "scale", out var tScale ) ) PatternScale.Value = ParseFloat( tScale );
					if ( args.TryGetValue( "sharpness", out var tSharp ) ) PatternSharpness.Value = ParseFloat( tSharp );
					TriPlanar.Value = true;
					break;
				case "anchor":
					if ( !args.TryGetValue( "source", out var aSrc ) ) args.TryGetValue( "anchor", out aSrc );
					if ( aSrc is not null ) SetAnchorChoice( AnchorSource, aSrc, "anchor source", "None, Below Height" );
					if ( args.TryGetValue( "mode", out var aMode ) ) SetAnchorChoice( AnchorMode, aMode, "anchor mode", "Peaks, Valleys" );
					if ( args.TryGetValue( "level", out var aLevel ) ) AnchorLevel.Value = ParseFloat( aLevel );
					if ( args.TryGetValue( "softness", out var aSoft ) ) AnchorSoftness.Value = ParseFloat( aSoft );
					break;
				default:
					FailOn( "Recipe", $"Unknown recipe directive '{tokens[0]}'.",
						"Directives are: fill, gradient, bands, spots, edgewear, dirt, cavityridge, mask, opacity, blend, size, triplanar, anchor.",
						"Check the spelling, or clear the Recipe and use the dialog parameters instead" );
					break;
			}
		}
	}

	void ApplyFillArgs( Dictionary<string, string> args )
	{
		Fill.Value = true;

		if ( args.TryGetValue( "color", out var color ) ) { FillUseColor.Value = true; FillColor.Value = ParseColor( color ); }
		if ( args.TryGetValue( "rough", out var rough ) ) { FillUseRoughness.Value = true; FillRoughness.Value = ParseFloat( rough ); }
		if ( args.TryGetValue( "roughness", out var rough2 ) ) { FillUseRoughness.Value = true; FillRoughness.Value = ParseFloat( rough2 ); }
		if ( args.TryGetValue( "metal", out var metal ) ) { FillUseMetalness.Value = true; FillMetalness.Value = ParseFloat( metal ); }
		if ( args.TryGetValue( "metalness", out var metal2 ) ) { FillUseMetalness.Value = true; FillMetalness.Value = ParseFloat( metal2 ); }
		if ( args.TryGetValue( "height", out var height ) ) { FillUseHeight.Value = true; FillHeight.Value = ParseFloat( height ); }
	}

	void ApplyAxisArgs( Dictionary<string, string> args, Vec3Param axis, FloatParam start, FloatParam end, FloatParam falloff )
	{
		if ( args.TryGetValue( "axis", out var a ) ) axis.Value = ParseVec3( a );
		if ( args.TryGetValue( "from", out var f ) ) start.Value = ParseFloat( f );
		if ( args.TryGetValue( "to", out var t ) ) end.Value = ParseFloat( t );
		if ( args.TryGetValue( "falloff", out var fo ) ) falloff.Value = ParseFloat( fo );
	}

	void SetMask( string name )
	{
		var wanted = name.Replace( " ", "" ).Replace( "_", "" ).ToLowerInvariant();

		for ( var i = 0; i < MaskOptions.Length; i++ )
			if ( MaskOptions[i].Replace( " ", "" ).Replace( "_", "" ).ToLowerInvariant() == wanted )
			{
				MaskGenerator.Index = i;
				return;
			}

		FailOn( "Recipe", $"Unknown mask '{name}'.",
			"Masks are: None, Edge Wear, Dirt, Gradient, Material ID, Gradient Axis, Bands, Spots, Cavity Ridge.",
			"Check the spelling, or clear the Recipe" );
	}

	static float ParseFloat( string text )
	{
		if ( float.TryParse( text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v ) )
			return v;

		FailOn( "Recipe", $"'{text}' is not a number.", "Recipe numbers are plain decimals, e.g. 0.22 or -2.2.", "Fix the number, or clear the Recipe" );
		return 0f;
	}

	static Vec3 ParseVec3( string text )
	{
		var parts = text.Split( ',' );

		if ( parts.Length == 3 )
			return new Vec3( ParseFloat( parts[0] ), ParseFloat( parts[1] ), ParseFloat( parts[2] ) );

		FailOn( "Recipe", $"'{text}' is not a vector.", "Vectors are x,y,z, e.g. 0,1,0.", "Fix the vector, or clear the Recipe" );
		return Vec3.Zero;
	}

	static Vec3 ParseColor( string text )
	{
		var t = text.Trim();

		if ( t.StartsWith( "#" ) && t.Length == 7
			&& int.TryParse( t[1..3], System.Globalization.NumberStyles.HexNumber, null, out var r )
			&& int.TryParse( t[3..5], System.Globalization.NumberStyles.HexNumber, null, out var g )
			&& int.TryParse( t[5..7], System.Globalization.NumberStyles.HexNumber, null, out var b ) )
			return new Vec3( r / 255f, g / 255f, b / 255f );

		if ( t.Contains( ',' ) )
			return ParseVec3( t );

		FailOn( "Recipe", $"'{text}' is not a colour.", "Colours are #rrggbb, e.g. #7d95a6, or r,g,b in 0..1.", "Fix the colour, or clear the Recipe" );
		return Vec3.Zero;
	}

	/// <summary>The raw 0..1 mask for a Lane B generator, per texel on the body's own UV atlas — the
	/// same walk PaintMasks uses, so a mask and a stroke agree about which texel a face owns.</summary>
	float[] GenerateLaneBMask( PolyMesh mesh, int size )
	{
		var index = MaskGenerator.Index;

		if ( index == Array.IndexOf( MaskOptions, "Gradient Axis" ) )
		{
			var axis = GradientAxis.Value.Normal;
			if ( axis.LengthSquared == 0f ) axis = new Vec3( 0, 0, 1 );
			var (lo, hi) = AxisExtent( mesh, axis );
			var start = GradientStart.Value == GradientEnd.Value ? lo : GradientStart.Value;
			var end = GradientStart.Value == GradientEnd.Value ? hi : GradientEnd.Value;
			var span = end - start > 1e-6f ? end - start : 1f;
			var falloff = GradientFalloff.Value;
			return RasteriseValues( mesh, size, ( p, _, _, _, _, _, _, _ ) =>
			{
				var t = Math.Clamp( (Vec3.Dot( p, axis ) - start) / span, 0f, 1f );
				return falloff == 0f ? t : MathF.Pow( t, MathF.Max( 0.1f, 1f + falloff ) );
			} );
		}

		if ( index == Array.IndexOf( MaskOptions, "Bands" ) )
		{
			var axis = BandsAxis.Value.Normal;
			if ( axis.LengthSquared == 0f ) axis = new Vec3( 0, 1, 0 );
			var spacing = MathF.Max( BandsSpacing.Clamped, 1e-3f );
			var width = BandsWidth.Clamped;
			var warp = BandsWarp.Value;
			var taper = BandsTaperAxis.Value.Normal;
			var (tLo, tHi) = taper.LengthSquared == 0f ? (0f, 1f) : AxisExtent( mesh, taper );
			var tSpan = tHi - tLo > 1e-6f ? tHi - tLo : 1f;
			// A perpendicular for the warp wobble: any axis that is not parallel to the band axis.
			var perp = Vec3.Cross( axis, MathF.Abs( axis.z ) < 0.9f ? new Vec3( 0, 0, 1 ) : new Vec3( 0, 1, 0 ) ).Normal;
			var limited = BandsRangeMin.Value != BandsRangeMax.Value;
			return RasteriseValues( mesh, size, ( p, _, _, _, _, _, _, _ ) =>
			{
				var s = Vec3.Dot( p, axis );
				if ( limited && (s < BandsRangeMin.Value || s > BandsRangeMax.Value) ) return 0f;
				var sWarp = s + warp * MathF.Sin( 2f * MathF.PI * Vec3.Dot( p, perp ) / spacing );
				var phase = (sWarp - (limited ? BandsRangeMin.Value : 0f)) / spacing;
				var frac = phase - MathF.Floor( phase );
				var wEff = width;
				if ( taper.LengthSquared != 0f )
					wEff = width * (1f - 0.5f * Math.Clamp( (Vec3.Dot( p, taper ) - tLo) / tSpan, 0f, 1f ));
				return frac < wEff ? 1f : 0f;
			} );
		}

		if ( index == Array.IndexOf( MaskOptions, "Spots" ) )
		{
			var spacing = MathF.Max( SpotsSpacing.Clamped, 1e-3f );
			var radius = SpotsRadius.Clamped;
			var jitter = SpotsJitter.Clamped;
			var ring = SpotsRingWidth.Clamped;
			var seed = SpotsSeed.Clamped;
			return RasteriseValues( mesh, size, ( p, _, _, _, _, _, _, _ ) =>
			{
				// The nearest grid centre can sit in a neighbouring cell, so check all 27.
				var bx = MathF.Floor( p.x / spacing );
				var by = MathF.Floor( p.y / spacing );
				var bz = MathF.Floor( p.z / spacing );
				var best = float.MaxValue;
				for ( var dx = -1; dx <= 1; dx++ )
				for ( var dy = -1; dy <= 1; dy++ )
				for ( var dz = -1; dz <= 1; dz++ )
				{
					var cx = bx + dx; var cy = by + dy; var cz = bz + dz;
					var jx = (Hash01( (int)cx, (int)cy, (int)cz, seed, 0 ) - 0.5f) * jitter * spacing;
					var jy = (Hash01( (int)cx, (int)cy, (int)cz, seed, 1 ) - 0.5f) * jitter * spacing;
					var jz = (Hash01( (int)cx, (int)cy, (int)cz, seed, 2 ) - 0.5f) * jitter * spacing;
					var c = new Vec3( (cx + 0.5f) * spacing + jx, (cy + 0.5f) * spacing + jy, (cz + 0.5f) * spacing + jz );
					best = MathF.Min( best, (p - c).Length );
				}
				return ring <= 0f ? (best < radius ? 1f : 0f) : (MathF.Abs( best - radius ) < ring * 0.5f ? 1f : 0f);
			} );
		}

		if ( index == Array.IndexOf( MaskOptions, "Cavity Ridge" ) )
		{
			// Cavity is in inches, positive in creases and negative on ridges, on an unknown scale
			// for any given mesh — so it is normalised over this mesh: 0 is its sharpest ridge, 1
			// its deepest crease. Mask Level/Softness (and Invert) then pick which side shows.
			var cavity = SurfaceDetail.Cavity( mesh );
			var lo = float.MaxValue; var hi = float.MinValue;
			foreach ( var v in cavity ) { lo = MathF.Min( lo, v ); hi = MathF.Max( hi, v ); }
			var span = hi - lo > 1e-6f ? hi - lo : 1f;
			return RasteriseValues( mesh, size, ( _, face, a, b, c, wa, wb, wc ) =>
			{
				var v = cavity[face.Indices[a]] * wa + cavity[face.Indices[b]] * wb + cavity[face.Indices[c]] * wc;
				return Math.Clamp( (v - lo) / span, 0f, 1f );
			} );
		}

		FailOn( "Mask Generator", $"Mask '{MaskOptions[Math.Clamp( MaskGenerator.Index, 0, MaskOptions.Length - 1 )]}' has no Lane B generator.",
			"This is a Lane B mask index with no generation code behind it.",
			"Pick a different mask, or report the mask name" );
		return null;
	}

	static (float Lo, float Hi) AxisExtent( PolyMesh mesh, Vec3 axis )
	{
		var lo = float.MaxValue; var hi = float.MinValue;
		foreach ( var p in mesh.Positions ) { var v = Vec3.Dot( p, axis ); lo = MathF.Min( lo, v ); hi = MathF.Max( hi, v ); }
		if ( lo == float.MaxValue ) { lo = 0f; hi = 1f; }
		return (lo, hi);
	}

	static float Hash01( int x, int y, int z, int seed, int channel )
	{
		unchecked
		{
			uint h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647 + seed * 974634211 + channel * 2654435761);
			h = (h ^ (h >> 13)) * 1274126177;
			h ^= h >> 16;
			return (h & 0xFFFFFF) / (float)0xFFFFFF;
		}
	}

	static float[] RasteriseValues( PolyMesh mesh, int size, Func<Vec3, Face, int, int, int, float, float, float, float> value )
	{
		var result = new float[size * size];
		var filled = new bool[size * size];

		foreach ( var face in mesh.Faces )
		{
			if ( face.Count < 3 || face.UVs is null || face.UVs.Length != face.Count )
				continue;

			var corners = new List<Vec3>( face.Count );
			for ( var k = 0; k < face.Count; k++ )
				corners.Add( mesh.Positions[face.Indices[k]] );

			foreach ( var (ia, ib, ic) in Triangulate.Face( corners ) )
			{
				var f = face;
				NormalBake.Rasterise( face.UVs[ia], face.UVs[ib], face.UVs[ic], size, size, ( x, y, wa, wb, wc ) =>
				{
					var p = mesh.Positions[f.Indices[ia]] * wa + mesh.Positions[f.Indices[ib]] * wb + mesh.Positions[f.Indices[ic]] * wc;
					var i = y * size + x;
					result[i] = value( p, f, ia, ib, ic, wa, wb, wc );
					filled[i] = true;
				} );
			}
		}

		BleedValues( result, filled, size, 4 );
		return result;
	}

	static void BleedValues( float[] values, bool[] filled, int size, int passes )
	{
		// Same reason PaintMasks bleeds: a texel just past an island edge is what filtering samples
		// at a seam, so it should carry its neighbour's value, not 0.
		for ( var pass = 0; pass < passes; pass++ )
		{
			var added = new List<(int, float)>();

			for ( var y = 0; y < size; y++ )
			for ( var x = 0; x < size; x++ )
			{
				var i = y * size + x;
				if ( filled[i] ) continue;
				float sum = 0f; var n = 0;
				for ( var dy = -1; dy <= 1; dy++ )
				for ( var dx = -1; dx <= 1; dx++ )
				{
					int nx = x + dx, ny = y + dy;
					if ( nx < 0 || ny < 0 || nx >= size || ny >= size || !filled[ny * size + nx] ) continue;
					sum += values[ny * size + nx]; n++;
				}
				if ( n > 0 ) added.Add( (i, sum / n) );
			}

			if ( added.Count == 0 ) return;
			foreach ( var (i, v) in added ) { values[i] = v; filled[i] = true; }
		}
	}

	/// <summary>The layer's stroke maps over a solid fill for every channel the fill writes. The fill
	/// is the layer's base; strokes on the same channel paint over it in Normal.</summary>
	PaintLayerMaps WithFill( PaintLayerMaps strokes, int size )
	{
		var maps = new PaintLayerMaps { Size = size, MaskPaint = strokes.MaskPaint };

		maps.Color = Over( FillUseColor.Value
			? PaintLayers.Solid( size, size, Byte( FillColor.Value.x ), Byte( FillColor.Value.y ), Byte( FillColor.Value.z ) )
			: null, strokes.Color );
		maps.Roughness = Over( FillUseRoughness.Value ? Grey( FillRoughness.Clamped, size ) : null, strokes.Roughness );
		maps.Metalness = Over( FillUseMetalness.Value ? Grey( FillMetalness.Clamped, size ) : null, strokes.Metalness );
		maps.Height = Over( FillUseHeight.Value ? Grey( FillHeight.Clamped, size ) : null, strokes.Height );
		// Emissive and opacity have no fill of their own; their strokes pass through.
		maps.Emissive = strokes.Emissive;
		maps.Opacity = strokes.Opacity;

		return maps;
	}

	static PaintCanvas Over( PaintCanvas fill, PaintCanvas strokes ) =>
		fill is null ? strokes : PaintLayers.Composite( fill, strokes, PaintBlendMode.Normal, 1f, null );

	static PaintCanvas Grey( float value, int size )
	{
		var b = Byte( value );
		return PaintLayers.Solid( size, size, b, b, b );
	}

	static byte Byte( float v ) => PaintReplay.ToByte( v );

	static float MaxOf( float[] values )
	{
		var max = 0f;

		foreach ( var v in values )
			max = MathF.Max( max, v );

		return max;
	}

	string MaskEmptyCause( PolyMesh mesh )
	{
		if ( Generator == PaintMaskGenerator.MaterialId )
		{
			foreach ( var face in mesh.Faces )
			{
				if ( face.Material == MaskSlot.Clamped )
					return $"Mask Level {MaskLevel.Clamped:0.##} with Invert Mask on leaves nothing showing on slot {MaskSlot.Clamped}.";
			}

			return $"No face of this body is on material slot {MaskSlot.Clamped}, so Material ID selects nothing.";
		}

		return Generator == PaintMaskGenerator.None
			? "The Layer Mask is painted black everywhere it covers, and Invert Mask hides the rest."
			: $"Mask Level {MaskLevel.Clamped:0.##} is above every value the {MaskOptions[(int)Generator]} generator found on this body.";
	}

	/// <summary>A cheap hash of the vertex positions, so a generated mask follows a sculpt or a move.</summary>
	static long PositionHash( PolyMesh mesh )
	{
		unchecked
		{
			long hash = 1469598103934665603;

			foreach ( var p in mesh.Positions )
			{
				hash = (hash ^ BitConverter.SingleToInt32Bits( p.x )) * 1099511628211;
				hash = (hash ^ BitConverter.SingleToInt32Bits( p.y )) * 1099511628211;
				hash = (hash ^ BitConverter.SingleToInt32Bits( p.z )) * 1099511628211;
			}

			return hash;
		}
	}
}

/// <summary>
/// Layer-stack operations on a body's paint layers — the kernel half of Mobile's
/// PaintOps layer commands (AddPaintLayer / RemovePaintLayer / MovePaintLayer /
/// MergePaintLayerDown / FlattenPaintLayers / SetClipToBelow), without the app's
/// session, undo-checkpoint or UI concerns.
///
/// UNDO: these mutate the studio directly, exactly like Mobile's ops mutate it
/// after _doc.Checkpoint(). The caller must checkpoint first (the BuildScript
/// `checkpoint` verb / EditHistory) and rebuild after, or the op is not undoable.
/// Every op marks the studio dirty from the earliest changed feature, and stroke
/// folds go through ReplaceStrokes / AddTexelStroke so the feature Revision moves.
/// A layer that is not in the studio is refused, never silently added.
/// </summary>
public static class PaintLayerOps
{
	/// <summary>Every Paint on the same body, bottom to top in tree order (s&amp;box PaintLayersOf).</summary>
	public static List<PaintFeature> LayersOf( PartStudio studio, PaintFeature feature )
	{
		var layers = new List<PaintFeature>();
		if ( studio is null || feature is null )
			return layers;

		Body body = null;
		foreach ( var b in studio.Bodies )
		{
			if ( feature.Bodies.Matches( b ) )
			{
				body = b;
				break;
			}
		}

		if ( body is null )
		{
			layers.Add( feature );
			return layers;
		}

		foreach ( var f in studio.Features )
		{
			if ( f is PaintFeature p && ( ReferenceEquals( p, feature ) || p.Bodies.BodyIds.Count == 0 || p.Bodies.BodyIds.Contains( body.Id ) ) )
				layers.Add( p );
		}

		return layers;
	}

	/// <summary>"New Layer" / "Fill Layer" above <paramref name="current"/> (or at the
	/// rollback bar when there is none): same body and resolution as the layer above it.
	/// A fill needs UVs — the caller checks that, as Mobile's AddPaintLayer does.</summary>
	public static PaintFeature AddLayer( PartStudio studio, PaintFeature current, bool fill, string bodyId, out string error )
	{
		error = null;
		if ( studio is null )
		{
			error = "No studio.";
			return null;
		}

		var layer = new PaintFeature();
		layer.Fill.Value = fill;

		if ( current is not null && studio.Features.Contains( current ) )
		{
			layer.Bodies.BodyIds = new List<string>( current.Bodies.BodyIds );
			layer.Resolution.Value = current.Resolution.Value;
			var at = studio.Features.IndexOf( current ) + 1;
			if ( studio.RollbackIndex < studio.Features.Count && studio.RollbackIndex >= at )
				studio.RollbackIndex++;
			studio.Insert( at, layer );
		}
		else
		{
			if ( bodyId is not null )
				layer.Bodies.BodyIds = new List<string> { bodyId };
			studio.Insert( Math.Min( studio.RollbackIndex, studio.Features.Count ), layer );
		}

		return layer;
	}

	/// <summary>Remove a layer. Returns the neighbour that should stay open (below, else
	/// above, else null), like Mobile's RemovePaintLayer. Refused when the layer is not
	/// in the studio.</summary>
	public static PaintFeature RemoveLayer( PartStudio studio, PaintFeature layer, out string error )
	{
		error = null;
		if ( studio is null || layer is null || !studio.Features.Contains( layer ) )
		{
			error = "That layer is not in the history.";
			return null;
		}

		var stack = LayersOf( studio, layer );
		var i = stack.IndexOf( layer );
		PaintFeature next = null;
		if ( i > 0 )
			next = stack[i - 1];
		else if ( stack.Count > 1 )
			next = stack[1];

		studio.Remove( layer );
		return next;
	}

	/// <summary>Move a layer one step in the stack: +1 up (painted over the next), -1 down.
	/// Swaps tree positions with the stack neighbour via PartStudio.Move.</summary>
	public static bool MoveLayer( PartStudio studio, PaintFeature layer, int direction, out string error )
	{
		error = null;
		if ( studio is null || layer is null )
		{
			error = "No layer.";
			return false;
		}

		var stack = LayersOf( studio, layer );
		var i = stack.IndexOf( layer );
		var n = i + Math.Sign( direction );
		if ( i < 0 || n < 0 || n >= stack.Count )
		{
			error = direction > 0 ? "That layer is already on top." : "That layer is already at the bottom.";
			return false;
		}

		studio.Move( studio.Features.IndexOf( layer ), studio.Features.IndexOf( stack[n] ) );
		return true;
	}

	/// <summary>Merge a layer into the one below it: strokes and texel strokes are appended
	/// (upper opacity baked into Strength), a fill on the upper is taken if the below has
	/// none, then the upper is removed. Returns the surviving layer below.</summary>
	public static PaintFeature MergeLayerDown( PartStudio studio, PaintFeature layer, out string error )
	{
		error = null;
		if ( studio is null || layer is null || !studio.Features.Contains( layer ) )
		{
			error = "That layer is not in the history.";
			return null;
		}

		var stack = LayersOf( studio, layer );
		var i = stack.IndexOf( layer );
		if ( i <= 0 )
		{
			error = "That layer is already at the bottom — nothing below to merge into.";
			return null;
		}

		var below = stack[i - 1];
		FoldLayerInto( below, layer );
		studio.MarkDirty( below );
		studio.Remove( layer );
		return below;
	}

	/// <summary>Fold every paint layer on the same body into the bottom one (strokes / texels /
	/// fill), remove the rest. Returns the surviving bottom layer. Needs at least two layers.</summary>
	public static PaintFeature FlattenLayers( PartStudio studio, PaintFeature any, out string error )
	{
		error = null;
		if ( studio is null )
		{
			error = "No studio.";
			return null;
		}

		if ( any is null )
		{
			for ( var k = studio.Features.Count - 1; k >= 0; k-- )
			{
				if ( studio.Features[k] is PaintFeature p )
				{
					any = p;
					break;
				}
			}
		}

		if ( any is null )
		{
			error = "No paint layers to flatten.";
			return null;
		}

		var stack = LayersOf( studio, any );
		if ( stack.Count < 2 )
		{
			error = "Need at least two layers to flatten.";
			return null;
		}

		var bottom = stack[0];
		for ( var i = 1; i < stack.Count; i++ )
			FoldLayerInto( bottom, stack[i] );

		studio.MarkDirty( bottom );
		for ( var i = stack.Count - 1; i >= 1; i-- )
			studio.Remove( stack[i] );

		return bottom;
	}

	/// <summary>Turn Clip To Below on or off. Refused on the bottom layer: there is no layer
	/// below to clip to. A no-op when already in the requested state.</summary>
	public static bool SetClipToBelow( PartStudio studio, PaintFeature layer, bool on, out string error )
	{
		error = null;
		if ( studio is null || layer is null )
		{
			error = "No layer.";
			return false;
		}

		if ( layer.ClipToBelow.Value == on )
			return true;

		if ( on && LayersOf( studio, layer ).IndexOf( layer ) <= 0 )
		{
			error = "This is the bottom layer: there is no layer below to clip to. Move it up or add a layer under it first.";
			return false;
		}

		layer.ClipToBelow.Value = on;
		studio.MarkDirty( layer );
		return true;
	}

	/// <summary>Set a layer's colour adjustment (HSV / levels). The feature's Adjust getter
	/// clamps the values, so out-of-range input is clamped, never stored raw.</summary>
	public static bool SetAdjust( PartStudio studio, PaintFeature layer, PaintAdjust adjust, out string error )
	{
		error = null;
		if ( studio is null || layer is null || !studio.Features.Contains( layer ) )
		{
			error = "That layer is not in the history.";
			return false;
		}

		layer.AdjustHue.Value = adjust.Hue;
		layer.AdjustSaturation.Value = adjust.Saturation;
		layer.AdjustBrightness.Value = adjust.Brightness;
		layer.LevelsBlack.Value = adjust.Black;
		layer.LevelsWhite.Value = adjust.White;
		layer.LevelsGamma.Value = adjust.Gamma;
		studio.MarkDirty( layer );
		return true;
	}

	/// <summary>Append upper's strokes / texels into below; inherit fill when below has none.
	/// Does not checkpoint — the caller does that before the op that calls this.</summary>
	static void FoldLayerInto( PaintFeature below, PaintFeature upper )
	{
		if ( below is null || upper is null )
			return;

		float uOp = upper.Opacity.Value;
		float bOp = Math.Max( below.Opacity.Value, 1e-3f );
		bool scale = Math.Abs( uOp - 1f ) > 1e-6f;

		if ( upper.Strokes is { Count: > 0 } )
		{
			var list = below.Strokes != null ? new List<PaintStroke>( below.Strokes ) : new List<PaintStroke>();
			foreach ( var s in upper.Strokes )
			{
				var c = CopyPaintStroke( s );
				if ( scale )
					c.Strength = Math.Clamp( c.Strength * uOp / bOp, 0f, 1f );
				list.Add( c );
			}
			below.ReplaceStrokes( list );
		}

		if ( upper.TexelStrokes is { Count: > 0 } )
		{
			foreach ( var s in upper.TexelStrokes )
				below.AddTexelStroke( CopyTexelStroke( s ) );
		}

		// Fill sits under strokes in Execute: take the upper's fill only when below has none.
		if ( upper.Fill.Value && !below.Fill.Value )
		{
			below.Fill.Value = true;
			below.FillUseColor.Value = upper.FillUseColor.Value;
			below.FillColor.Value = upper.FillColor.Value;
			below.FillUseRoughness.Value = upper.FillUseRoughness.Value;
			below.FillRoughness.Value = upper.FillRoughness.Value;
			below.FillUseMetalness.Value = upper.FillUseMetalness.Value;
			below.FillMetalness.Value = upper.FillMetalness.Value;
			below.FillUseHeight.Value = upper.FillUseHeight.Value;
			below.FillHeight.Value = upper.FillHeight.Value;
		}

		// Fill-only fold (no strokes) still needs a rebuild: bump via ReplaceStrokes of the current list.
		if ( ( upper.Strokes == null || upper.Strokes.Count == 0 ) && ( upper.TexelStrokes == null || upper.TexelStrokes.Count == 0 ) )
			below.ReplaceStrokes( below.Strokes );
	}

	static PaintStroke CopyPaintStroke( PaintStroke s )
	{
		var c = new PaintStroke
		{
			R = s.R, G = s.G, B = s.B, A = s.A,
			Radius = s.Radius, Strength = s.Strength, Falloff = s.Falloff, Spacing = s.Spacing,
			Erase = s.Erase, Clone = s.Clone, CloneOffset = s.CloneOffset, Smudge = s.Smudge, Blur = s.Blur,
			Channel = s.Channel,
		};
		c.Path.AddRange( s.Path );
		return c;
	}

	static TexelStroke CopyTexelStroke( TexelStroke s )
	{
		var c = new TexelStroke
		{
			R = s.R, G = s.G, B = s.B, A = s.A,
			Radius = s.Radius, Strength = s.Strength, Falloff = s.Falloff, Spacing = s.Spacing,
			Erase = s.Erase, Channel = s.Channel,
		};
		c.Path.AddRange( s.Path );
		return c;
	}
}
