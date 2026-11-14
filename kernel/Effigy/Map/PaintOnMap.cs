// Paint / PBR material layers on map faces — Geppetto Desktop, backend only (no UI).
//
// WHAT THIS IS (hammer-interop-paint-layers-on-faces). The sculpt side already has a
// PBR paint-layer stack (PaintLayerStack / PaintLayer / PaintLayers in
// kernel/Effigy/PaintLayers.cs: colour/roughness/metalness/height/emissive/opacity/
// normal channels, blend modes, opacity, masks, groups). This file is the bridge that
// puts that same stack on Hammer-style map faces without replacing the face's base
// material:
//
//   - A brush face keeps its FaceMaterial (material NAME + FaceUv, kernel/Effigy/Map/
//     FaceMaterial.cs, via MapBrush in kernel/Effigy/Map/MapBrush.cs). Paint is a
//     FacePaint layered OVER it: the composite canvas is what the face looks like
//     painted, the base material name is still the base material name.
//   - A displacement keeps its grid (Offsets/Alphas, kernel/Effigy/Map/Displacement.cs).
//     The layer stack's per-texel COVERAGE drives the displacement's blend alpha
//     between its two materials, with the same move-toward-target maths the sculpt
//     brush uses (see ApplyPaintAlpha).
//
// SEAMS, READ BEFORE TOUCHING.
//   - PaintMaterial.cs (kernel/Effigy/PaintMaterial.cs) is NOT this system: it is the
//     painted-canvas -> PNG -> .vmat chain for sculpt paint, a static helper with no
//     per-face record. A brush face references its material by name; this file composes
//     with that name, it does not route through PaintMaterial.
//   - Displacement.cs landed with a DIFFERENT shape than the filed spec assumed: flat
//     float[] Offsets/Alphas, Create(MapBrushFace, power), no normals/distances/
//     triangle_tags. This file builds against the real on-disk shape (row-major
//     Offsets/Alphas, UvAt(row, col) for grid-vertex UVs, SetAlpha validating 0..1).
//   - NAME COLLISION, integrator's job: the brush core's face type is Effigy.MapBrushFace
//     (plane + FaceMaterial, vertices solved from planes, no stored corners) while
//     Displacement binds Effigy.Map.MapBrushFace (4 stored corners). This file uses the
//     Effigy one for brush faces (ForBrushFace) and the Effigy.Map one only through
//     Displacement itself, which already owns it.
//   - DisplacementSculpt.cs (hammer-displacement-sculpt-brush-modes, IN PROGRESS by a
//     sibling worker) exists on disk and is read-only here. ApplyPaintAlpha is written
//     to match its PaintAlpha dab exactly (same formula, same clamps); if that file
//     changes its blend, this file's alpha test names the mismatch.
//   - There IS a MapDocument type in the tree (kernel/Effigy/Map/MapDocument.cs,
//     hammer-map-document-model, DONE) — and its format is NOT JSON: it is .gmap
//     history-op text via MapDocumentIo ("gmap <version>", one op per history
//     entry, e.g. `op set-brush-material <id> <material, rest of line>`).
//     Serialisation here is the lossless PAYLOAD for that document, the way
//     Displacement.Snapshot()/FromSnapshot() is the lossless primitive for a
//     displacement: FacePaint.ToJson()/FromJson() and
//     PaintOnMap.StackToJson()/StackFromJson() produce the JSON a future paint
//     history op would carry (following the set-brush-material pattern, e.g.
//     `op set-face-paint <brushId> <faceIndex> <FacePaint JSON>`).
//     Wiring that op into MapDocument.cs / MapDocumentIo.cs is the integrator's
//     job — those are shared, coordinator-owned files, not this ticket's — so the
//     round-trip test below validates the payload, not the .gmap wiring. Canvas
//     texels travel as base64 RGBA.
//
// SAMPLING. Face UVs tile: FaceUv documents that tiling continues outside the 0..1
// square, so SampleColor/SampleChannel/CoverageAt wrap UVs (u = 1.25 samples the same
// texel as u = 0.25; negative UVs wrap too, which is what displacement corner UVs
// need — Displacement's ProjectUVs can hand a face V = -1..0). Nearest texel; no
// filtering is promised.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Effigy.Map;

/// <summary>
/// One brush face's paint: its base <see cref="FaceMaterial"/> (never replaced by
/// paint — paint layers over it), the optional second material a displacement blends
/// toward (alpha 1 = this material; null when the face has no blend target), and the
/// <see cref="PaintLayerStack"/> that paints it. Immutable: WithStack /
/// WithSecondMaterial return a new value; the stack itself is a live list, as
/// <see cref="PaintLayerStack"/> documents.
/// </summary>
public sealed class FacePaint : IEquatable<FacePaint>
{
	public FaceMaterial Base { get; }
	public string SecondMaterial { get; }
	public PaintLayerStack Stack { get; }

	public FacePaint( FaceMaterial baseMaterial, PaintLayerStack stack, string secondMaterial = null )
	{
		Base = baseMaterial ?? throw new ArgumentNullException( nameof( baseMaterial ) );
		Stack = stack ?? throw new ArgumentNullException( nameof( stack ) );
		if ( secondMaterial is not null && string.IsNullOrWhiteSpace( secondMaterial ) )
			throw new ArgumentException( "A second material must name a material; a blank name is not a material.", nameof( secondMaterial ) );
		SecondMaterial = secondMaterial?.Trim();
	}

	/// <summary>The same face with a different layer stack; base and second material untouched.</summary>
	public FacePaint WithStack( PaintLayerStack stack ) => new( Base, stack, SecondMaterial );

	/// <summary>The same face with a different second material (null clears it); base and stack untouched.</summary>
	public FacePaint WithSecondMaterial( string secondMaterial ) => new( Base, Stack, secondMaterial );

	/// <summary>
	/// The stack composited over a bare surface, bottom to top, through the stack's one
	/// compositing path (<see cref="PaintLayerStack.Composite()"/>). Null when nothing
	/// enabled in the stack has surface content — no paint, not a transparent canvas.
	/// The base material's colour is NOT baked in: a material name has no texels, and
	/// inventing a colour for "brick/brickwall001" would lie about the material.
	/// </summary>
	public PaintCanvas Composite() => Stack.Composite();

	/// <summary>Named defects, same convention as FaceMaterial: empty means usable.</summary>
	public IReadOnlyList<string> Validate()
	{
		var defects = new List<string>();
		if ( Base is null )
			defects.Add( "paint-base-missing" );
		if ( SecondMaterial is not null && string.IsNullOrWhiteSpace( SecondMaterial ) )
			defects.Add( "paint-second-material-blank" );
		if ( Stack is null )
		{
			defects.Add( "paint-stack-missing" );
			return defects;
		}
		foreach ( var layer in Stack.Layers )
			ValidateLayer( layer, defects );
		return defects;
	}

	static void ValidateLayer( PaintLayer layer, List<string> defects )
	{
		if ( layer is null )
		{
			defects.Add( "paint-layer-null" );
			return;
		}
		if ( layer.Opacity < 0f || layer.Opacity > 1f )
			defects.Add( $"paint-layer-opacity:{layer.Name}" );
		if ( !Enum.IsDefined( layer.Blend ) )
			defects.Add( $"paint-layer-blend:{layer.Name}" );
		if ( layer.ChannelOpacity is not null )
			foreach ( var kv in layer.ChannelOpacity )
				if ( kv.Value < 0f || kv.Value > 1f )
					defects.Add( $"paint-layer-channel-opacity:{layer.Name}:{kv.Key}" );
		if ( layer.Maps is not null && layer.Maps.HasSurface && layer.Mask is not null )
		{
			var w = layer.Maps.Color?.Width ?? layer.Maps.Size;
			var h = layer.Maps.Color?.Height ?? layer.Maps.Size;
			if ( w > 0 && h > 0 && layer.Mask.Length != w * h )
				defects.Add( $"paint-layer-mask-length:{layer.Name}" );
		}
		if ( layer.Children is not null )
			foreach ( var child in layer.Children )
				ValidateLayer( child, defects );
	}

	public bool IsValid => Validate().Count == 0;

	/// <summary>
	/// JSON form: the base FaceMaterial's {"material","uv"} plus "secondMaterial"
	/// (null when none) and "layers": the stack as an array of layer objects (see
	/// <see cref="PaintOnMap.LayerToJson(PaintLayer)"/>). This JSON is the payload a
	/// map-document paint history op would carry (see the file header); the .gmap
	/// op wiring itself belongs to the integrator.
	/// </summary>
	public JsonObject ToJson()
	{
		var json = Base.ToJson();
		json["secondMaterial"] = SecondMaterial is null ? null : JsonValue.Create( SecondMaterial );
		json["layers"] = PaintOnMap.StackToJson( Stack );
		return json;
	}

	public string ToJsonString() => ToJson().ToJsonString();

	/// <summary>
	/// Parse <see cref="ToJson"/> output. A missing "layers" means an empty stack; a
	/// missing or blank "material" throws, like <see cref="FaceMaterial.FromJson"/>.
	/// Unknown fields are ignored so older documents keep loading.
	/// </summary>
	public static FacePaint FromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var baseMaterial = FaceMaterial.FromJson( json );
		var second = json["secondMaterial"]?.GetValue<string>();
		var stack = json["layers"] is JsonArray layers
			? PaintOnMap.StackFromJson( layers )
			: new PaintLayerStack();
		return new FacePaint( baseMaterial, stack, second );
	}

	public static FacePaint FromJsonString( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "FacePaint JSON text is blank." );
		if ( JsonNode.Parse( json ) is not JsonObject obj )
			throw new ArgumentException( "FacePaint JSON text is not an object." );
		return FromJson( obj );
	}

	/// <summary>Same base, same second material, same layer count, and byte-equal composites — so two stacks that paint the same texels are equal even when they were built differently.</summary>
	public bool Equals( FacePaint other )
	{
		if ( other is null || !Base.Equals( other.Base ) || SecondMaterial != other.SecondMaterial
			|| Stack.Count != other.Stack.Count )
			return false;
		var a = Composite();
		var b = other.Composite();
		return a is null ? b is null : a.SameAs( b );
	}

	public override bool Equals( object obj ) => obj is FacePaint other && Equals( other );
	public override int GetHashCode() => HashCode.Combine( Base, SecondMaterial, Stack.Count );
	public override string ToString() =>
		SecondMaterial is null ? $"{Base} + {Stack.Count} paint layers" : $"{Base} -> {SecondMaterial} + {Stack.Count} paint layers";
}

/// <summary>
/// What one point of a painted face is made of: the base material, the second
/// material (null when the face has no blend target), and how much paint covers that
/// point (0 = bare base material, 1 = fully painted). The factor is the composite
/// canvas's coverage at the point — the same number <see cref="PaintOnMap.ApplyPaintAlpha"/>
/// uses as a displacement's blend target.
/// </summary>
public readonly struct FaceMaterialBlend
{
	public readonly string BaseMaterial;
	public readonly string SecondMaterial;
	public readonly float Factor;

	public FaceMaterialBlend( string baseMaterial, string secondMaterial, float factor )
	{
		BaseMaterial = baseMaterial;
		SecondMaterial = secondMaterial;
		Factor = factor;
	}

	public override string ToString() =>
		SecondMaterial is null
			? $"{BaseMaterial} (paint {Factor:0.###})"
			: $"{BaseMaterial} -> {SecondMaterial} (paint {Factor:0.###})";
}

/// <summary>What one paint-alpha application changed on a displacement grid.</summary>
public readonly struct PaintAlphaResult
{
	public readonly int VerticesTouched;
	public readonly float MaxAlphaDelta;

	public PaintAlphaResult( int verticesTouched, float maxAlphaDelta )
	{
		VerticesTouched = verticesTouched;
		MaxAlphaDelta = maxAlphaDelta;
	}

	public override string ToString() => $"{VerticesTouched} vertices, max alpha delta {MaxAlphaDelta:0.###}";
}

/// <summary>
/// The PBR paint-layer stack, on map faces. All operations here are backend-only
/// (no UI): they compose canvases, sample them, move displacement alphas, and
/// serialise stacks. Nothing here edits a <see cref="MapBrush"/> or a
/// <see cref="Displacement"/>'s geometry — paint never moves a vertex or renames a
/// material, so a painted brush still bakes, compiles and seals exactly like the
/// unpainted one (a displacement still never seals the map).
/// </summary>
public static class PaintOnMap
{
	/// <summary>
	/// The paint record for one face of a brush: base is the face's own FaceMaterial,
	/// stack starts empty, second material is the caller's (null when the face has no
	/// blend target). The brush is NOT modified and no face is re-textured — paint
	/// lives beside the brush, on the FacePaint, until a map document stores it.
	/// </summary>
	public static FacePaint ForBrushFace( MapBrush brush, int faceIndex, string secondMaterial = null )
	{
		if ( brush is null ) throw new ArgumentNullException( nameof( brush ) );
		if ( faceIndex < 0 || faceIndex >= brush.Faces.Count )
			throw new ArgumentOutOfRangeException( nameof( faceIndex ), faceIndex,
				$"Brush has {brush.Faces.Count} faces; face {faceIndex} does not exist." );
		return new FacePaint( brush.Faces[faceIndex].Material, new PaintLayerStack(), secondMaterial );
	}

	/// <summary>
	/// One texel of a composited canvas, nearest, with tiling UVs: u = 1.25 samples the
	/// same texel as u = 0.25, and negative UVs wrap the same way. Row 0 is the TOP of
	/// the image, like <see cref="PaintCanvas"/> itself. Throws for a null canvas or a
	/// non-finite UV.
	/// </summary>
	public static (byte R, byte G, byte B, byte A) SampleColor( PaintCanvas canvas, float u, float v )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		if ( !float.IsFinite( u ) || !float.IsFinite( v ) )
			throw new ArgumentException( $"Cannot sample a canvas at non-finite UV ({u}, {v})." );
		var x = WrapIndex( u, canvas.Width );
		var y = WrapIndex( v, canvas.Height );
		var i = ( y * canvas.Width + x ) * 4;
		var rgba = canvas.Rgba;
		return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
	}

	/// <summary>
	/// One scalar channel of a composited canvas at a UV, 0..1: the channel canvas's
	/// red component (grey channels carry their value in R/G/B). A channel nobody
	/// painted returns <see cref="PaintChannels.Base(PaintChannel)"/> — roughness 0.6,
	/// metalness 0, height 0.5, emissive 0, opacity 1 — the same unpainted default the
	/// sculpt side exports. A null composite is an unpainted surface, so every channel
	/// returns its base. For <see cref="PaintChannel.Color"/> this is the red
	/// component; use <see cref="SampleColor"/> for colour.
	/// </summary>
	public static float SampleChannel( PaintCanvas composite, PaintChannel channel, float u, float v )
	{
		var channelCanvas = composite?.Channel( channel );
		if ( channelCanvas is null )
			return PaintChannels.Base( channel );
		return SampleColor( channelCanvas, u, v ).R / 255f;
	}

	/// <summary>
	/// How much paint covers one point of a composited canvas, 0..1: the composite
	/// colour canvas's alpha there (coverage x layer opacity x layer mask, as the
	/// compositing path already multiplied them). 0 for a null composite — no paint.
	/// </summary>
	public static float CoverageAt( PaintCanvas composite, float u, float v ) =>
		composite is null ? 0f : SampleColor( composite, u, v ).A / 255f;

	/// <summary>
	/// What one point of a painted face is made of: base material, second material,
	/// and the paint coverage there (see <see cref="FaceMaterialBlend"/>). Composites
	/// the stack fresh; callers sampling many points should composite once and use
	/// <see cref="CoverageAt(PaintCanvas, float, float)"/> instead.
	/// </summary>
	public static FaceMaterialBlend MaterialAt( FacePaint paint, float u, float v )
	{
		if ( paint is null ) throw new ArgumentNullException( nameof( paint ) );
		return MaterialAt( paint, paint.Composite(), u, v );
	}

	/// <summary>Same as <see cref="MaterialAt(FacePaint, float, float)"/>, over a canvas the caller already composited.</summary>
	public static FaceMaterialBlend MaterialAt( FacePaint paint, PaintCanvas composite, float u, float v )
	{
		if ( paint is null ) throw new ArgumentNullException( nameof( paint ) );
		return new FaceMaterialBlend( paint.Base.MaterialName, paint.SecondMaterial, CoverageAt( composite, u, v ) );
	}

	/// <summary>
	/// Paint a displacement's blend alpha from a face's layer stack: for every grid
	/// vertex, the target alpha is the stack's coverage at that vertex's UV
	/// (<see cref="Displacement.UvAt(int, int)"/>), and the vertex moves toward it
	/// exactly the way a sculpt PaintAlpha dab moves it (see
	/// <see cref="DisplacementSculpt"/>): next = clamp(alpha + (target - alpha) *
	/// strength, 0, 1). Strength 1 lands exactly on the target; strength 0 changes
	/// nothing; repeated applications converge on the target and never pass it, and
	/// alpha never leaves 0..1. The displacement's offsets are untouched, its sewn
	/// edges are NOT re-sewn here (a paint-alpha change moves no vertex, so it cannot
	/// tear an edge — re-sew belongs to the sculpt path that moves offsets), and the
	/// second material's identity stays on the FacePaint, not in the grid: the grid
	/// only ever holds the blend factor, as Displacement documents.
	/// </summary>
	public static PaintAlphaResult ApplyPaintAlpha( Displacement displacement, FacePaint paint, float strength = 1f )
	{
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		if ( paint is null ) throw new ArgumentNullException( nameof( paint ) );
		return ApplyPaintAlpha( displacement, paint.Composite(), strength );
	}

	/// <summary>Same as <see cref="ApplyPaintAlpha(Displacement, FacePaint, float)"/>, over a layer stack with no face record attached.</summary>
	public static PaintAlphaResult ApplyPaintAlpha( Displacement displacement, PaintLayerStack stack, float strength = 1f )
	{
		if ( displacement is null ) throw new ArgumentNullException( nameof( displacement ) );
		if ( stack is null ) throw new ArgumentNullException( nameof( stack ) );
		return ApplyPaintAlpha( displacement, stack.Composite(), strength );
	}

	static PaintAlphaResult ApplyPaintAlpha( Displacement displacement, PaintCanvas composite, float strength )
	{
		if ( strength < 0f || strength > 1f )
			throw new ArgumentOutOfRangeException( nameof( strength ), strength, "Paint-alpha strength must be 0..1." );
		if ( composite is null )
			return new PaintAlphaResult( 0, 0f );
		var touched = 0;
		var maxDelta = 0f;
		var alphas = displacement.Alphas;
		for ( var r = 0; r < displacement.GridSize; r++ )
			for ( var c = 0; c < displacement.GridSize; c++ )
			{
				var i = r * displacement.GridSize + c;
				var uv = displacement.UvAt( r, c );
				var target = CoverageAt( composite, uv.x, uv.y );
				var next = Math.Clamp( alphas[i] + ( target - alphas[i] ) * strength, 0f, 1f );
				var delta = MathF.Abs( next - alphas[i] );
				if ( delta > 1e-6f )
					touched++;
				if ( delta > maxDelta )
					maxDelta = delta;
				alphas[i] = next;
			}
		return new PaintAlphaResult( touched, maxDelta );
	}

	/// <summary>
	/// A whole layer stack as a JSON array of layer objects, bottom to top. This is
	/// the payload form a map-document paint op carries per painted face (see
	/// <see cref="FacePaint.ToJson"/>). Layer content is lossless: every channel canvas
	/// travels as base64 RGBA with its size. NOT serialised: the stack's Revision
	/// (runtime structure counter), a layer canvas's own companions (a layer map's
	/// channels are separate canvases; the composite's companions are rebuilt by
	/// compositing), and PaintCanvas.ClipBase (derived on every rebuild, never saved).
	/// </summary>
	public static JsonArray StackToJson( PaintLayerStack stack )
	{
		if ( stack is null ) throw new ArgumentNullException( nameof( stack ) );
		var layers = new JsonArray();
		foreach ( var layer in stack.Layers )
			layers.Add( LayerToJson( layer ) );
		return layers;
	}

	/// <summary>Parse <see cref="StackToJson"/> output. A null array throws; an empty array is an empty stack.</summary>
	public static PaintLayerStack StackFromJson( JsonArray json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var stack = new PaintLayerStack();
		foreach ( var node in json )
		{
			if ( node is not JsonObject layerJson )
				throw new ArgumentException( "Paint layer stack JSON: every layer must be an object." );
			stack.Add( LayerFromJson( layerJson ) );
		}
		return stack;
	}

	/// <summary>
	/// One layer as JSON: {"name","blend","opacity","enabled","mask":[floats]|null,
	/// "channels":{"color":{canvas},"roughness":{canvas},...},
	/// "channelOpacity":{"roughness":0.5},"channelBlend":{"color":"Multiply"},
	/// "children":[layer...]|null}. A canvas is {"width","height","rgba":"base64"}.
	/// Only channels the layer touches are stored; a group stores its children and
	/// may also carry its own channels. A layer's painted mask canvas (MaskPaint) is
	/// NOT stored: only the final float[] Mask is what the composite reads, and the
	/// painted mask is its pre-final input.
	/// </summary>
	public static JsonObject LayerToJson( PaintLayer layer )
	{
		if ( layer is null ) throw new ArgumentNullException( nameof( layer ) );
		var json = new JsonObject
		{
			["name"] = layer.Name,
			["blend"] = layer.Blend.ToString(),
			["opacity"] = (double)layer.Opacity,
			["enabled"] = layer.Enabled,
		};
		if ( layer.Mask is null )
		{
			json["mask"] = null;
		}
		else
		{
			var mask = new JsonArray();
			foreach ( var m in layer.Mask )
				mask.Add( JsonValue.Create( (double)m ) );
			json["mask"] = mask;
		}
		var channels = new JsonObject();
		if ( layer.Maps is not null )
			foreach ( var channel in PaintChannels.Surface )
			{
				var canvas = layer.Maps.Get( channel );
				if ( canvas is not null )
					channels[PaintChannels.Tag( channel ) ?? channel.ToString().ToLowerInvariant()] = CanvasToJson( canvas );
			}
		json["channels"] = channels;
		if ( layer.ChannelOpacity is not null )
		{
			var opacities = new JsonObject();
			foreach ( var kv in layer.ChannelOpacity )
				opacities[kv.Key.ToString()] = (double)kv.Value;
			json["channelOpacity"] = opacities;
		}
		if ( layer.ChannelBlend is not null )
		{
			var blends = new JsonObject();
			foreach ( var kv in layer.ChannelBlend )
				blends[kv.Key.ToString()] = kv.Value.ToString();
			json["channelBlend"] = blends;
		}
		if ( layer.Children is not null )
		{
			var children = new JsonArray();
			foreach ( var child in layer.Children )
				children.Add( LayerToJson( child ) );
			json["children"] = children;
		}
		return json;
	}

	/// <summary>
	/// Parse <see cref="LayerToJson"/> output. Missing "name" is "" (PaintLayer's own
	/// default); a bad blend name, a canvas whose base64 does not match its size, or a
	/// mask whose length does not match a stored channel's size throws naming the
	/// problem. Unknown fields are ignored.
	/// </summary>
	public static PaintLayer LayerFromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var name = json["name"]?.GetValue<string>() ?? "";
		var blend = PaintBlendMode.Normal;
		if ( json["blend"]?.GetValue<string>() is { } blendName
			&& !Enum.TryParse( blendName, ignoreCase: true, out blend ) )
			throw new ArgumentException( $"Paint layer JSON: unknown blend mode '{blendName}'." );
		var opacity = Num( json, "opacity", 1f );
		var enabled = json["enabled"] is null || Bool( json, "enabled", true );

		float[] mask = null;
		if ( json["mask"] is JsonArray maskJson )
		{
			mask = new float[maskJson.Count];
			for ( var i = 0; i < maskJson.Count; i++ )
				try { mask[i] = (float)maskJson[i]!.GetValue<double>(); }
				catch ( Exception ex ) { throw new ArgumentException( $"Paint layer JSON: mask[{i}] is not a number.", ex ); }
		}

		PaintLayerMaps maps = null;
		if ( json["channels"] is JsonObject channelsJson )
		{
			foreach ( var kv in channelsJson )
			{
				if ( kv.Value is not JsonObject canvasJson )
					throw new ArgumentException( $"Paint layer JSON: channel '{kv.Key}' is not a canvas object." );
				var channel = ChannelFromKey( kv.Key );
				maps ??= new PaintLayerMaps();
				var canvas = CanvasFromJson( canvasJson );
				maps.Set( channel, canvas );
				if ( canvas.Width > maps.Size )
					maps.Size = canvas.Width;
			}
		}
		if ( mask is not null && maps is not null && maps.HasSurface )
		{
			var w = maps.Color?.Width ?? maps.Size;
			var h = maps.Color?.Height ?? maps.Size;
			if ( w > 0 && h > 0 && mask.Length != w * h )
				throw new ArgumentException(
					$"Paint layer JSON: mask has {mask.Length} entries but the layer is {w}x{h}." );
		}

		var layer = new PaintLayer( name, maps, blend, opacity, mask ) { Enabled = enabled };
		if ( json["channelOpacity"] is JsonObject opacities )
			foreach ( var kv in opacities )
			{
				if ( !Enum.TryParse<PaintChannel>( kv.Key, ignoreCase: true, out var channel ) )
					throw new ArgumentException( $"Paint layer JSON: unknown channel '{kv.Key}' in channelOpacity." );
				try { layer.SetChannelOpacity( channel, (float)kv.Value!.GetValue<double>() ); }
				catch ( Exception ex ) { throw new ArgumentException( $"Paint layer JSON: channelOpacity '{kv.Key}' is not a number.", ex ); }
			}
		if ( json["channelBlend"] is JsonObject blends )
			foreach ( var kv in blends )
			{
				if ( !Enum.TryParse<PaintChannel>( kv.Key, ignoreCase: true, out var channel ) )
					throw new ArgumentException( $"Paint layer JSON: unknown channel '{kv.Key}' in channelBlend." );
				if ( !Enum.TryParse<PaintBlendMode>( kv.Value!.GetValue<string>(), ignoreCase: true, out var mode ) )
					throw new ArgumentException( $"Paint layer JSON: unknown blend mode '{kv.Value!.GetValue<string>()}' in channelBlend." );
				layer.SetChannelBlend( channel, mode );
			}
		if ( json["children"] is JsonArray childrenJson )
		{
			var children = new List<PaintLayer>();
			foreach ( var node in childrenJson )
			{
				if ( node is not JsonObject childJson )
					throw new ArgumentException( "Paint layer JSON: every child layer must be an object." );
				children.Add( LayerFromJson( childJson ) );
			}
			layer.Children = children;
		}
		return layer;
	}

	/// <summary>One canvas as {"width","height","rgba":"base64"} — the RGBA bytes, straight alpha, row-major, row 0 at the top.</summary>
	public static JsonObject CanvasToJson( PaintCanvas canvas )
	{
		if ( canvas is null ) throw new ArgumentNullException( nameof( canvas ) );
		return new JsonObject
		{
			["width"] = canvas.Width,
			["height"] = canvas.Height,
			["rgba"] = Convert.ToBase64String( canvas.Rgba ),
		};
	}

	/// <summary>Parse <see cref="CanvasToJson"/> output. Throws when the base64 does not decode to exactly width*height*4 bytes.</summary>
	public static PaintCanvas CanvasFromJson( JsonObject json )
	{
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var width = json["width"]?.GetValue<int>() ?? throw new ArgumentException( "Paint canvas JSON is missing 'width'." );
		var height = json["height"]?.GetValue<int>() ?? throw new ArgumentException( "Paint canvas JSON is missing 'height'." );
		if ( width <= 0 || height <= 0 )
			throw new ArgumentException( $"Paint canvas JSON: size {width}x{height} is not positive." );
		var rgba = json["rgba"]?.GetValue<string>() ?? throw new ArgumentException( "Paint canvas JSON is missing 'rgba'." );
		byte[] bytes;
		try { bytes = Convert.FromBase64String( rgba ); }
		catch ( Exception ex ) { throw new ArgumentException( "Paint canvas JSON: 'rgba' is not valid base64.", ex ); }
		if ( bytes.Length != width * height * 4 )
			throw new ArgumentException(
				$"Paint canvas JSON: 'rgba' decodes to {bytes.Length} bytes but {width}x{height} needs {width * height * 4}." );
		var canvas = new PaintCanvas( width, height );
		Array.Copy( bytes, canvas.Rgba, bytes.Length );
		return canvas;
	}

	/// <summary>The channel a stored key names: a <see cref="PaintChannels.Tag(PaintChannel)"/> ("rough", "metal", "height", "emit", "opacity", "normal", "mask") or the channel name itself ("Color", "Roughness", ...). Colour has no tag, so it is stored under "color".</summary>
	static PaintChannel ChannelFromKey( string key )
	{
		if ( PaintChannels.FromTag( key ) is { } tagged )
			return tagged;
		if ( Enum.TryParse<PaintChannel>( key, ignoreCase: true, out var channel ) )
			return channel;
		throw new ArgumentException( $"Paint layer JSON: unknown channel '{key}'." );
	}

	/// <summary>A JSON number field, or the fallback when it is missing. A present but non-numeric value throws naming the field — the FaceUv convention.</summary>
	static float Num( JsonObject json, string key, float fallback )
	{
		if ( json[key] is null ) return fallback;
		try { return (float)json[key]!.GetValue<double>(); }
		catch ( Exception ex ) { throw new ArgumentException( $"Paint layer JSON: '{key}' is not a number.", ex ); }
	}

	static bool Bool( JsonObject json, string key, bool fallback )
	{
		if ( json[key] is null ) return fallback;
		try { return json[key]!.GetValue<bool>(); }
		catch ( Exception ex ) { throw new ArgumentException( $"Paint layer JSON: '{key}' is not a boolean.", ex ); }
	}

	static int WrapIndex( float t, int n )
	{
		var i = (int)MathF.Floor( t * n ) % n;
		return i < 0 ? i + n : i;
	}
}
