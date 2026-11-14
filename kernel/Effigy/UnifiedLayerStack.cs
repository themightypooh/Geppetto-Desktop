using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effigy;

/// <summary>Whether a unified layer entry carries paint or sculpt content.</summary>
public enum UnifiedLayerKind
{
	Paint,
	Sculpt,
}

/// <summary>One point on a layer's opacity timeline: the layer's opacity at <see cref="Time"/>.</summary>
public readonly record struct OpacityKeyframe( float Time, float Opacity );

/// <summary>
/// One entry's overrides inside one named variant (A/B/C): only the fields that are set differ
/// from the entry's own values, so a variant that only changes one layer's opacity leaves every
/// other entry — and every other variant — exactly as it was.
/// </summary>
public sealed class LayerVariantOverride
{
	public float? Opacity;
	public float? Intensity;
	public bool? Enabled;
}

/// <summary>
/// One entry in a <see cref="UnifiedLayerStack"/>: a paint layer or a sculpt layer, with the
/// presentation the two kinds share — group, enabled, blend, mask — and the hooks that make them
/// one system instead of two lists: per-channel overrides, instancing, opacity keyframes.
///
/// The entry does NOT duplicate what its content already carries. A paint entry's blend, opacity
/// and mask live on its <see cref="PaintLayer"/> (a variant override or a keyframe replaces them
/// at composite time, never by rewriting the layer); a sculpt entry's strength is
/// <see cref="Intensity"/> on the entry, because a <see cref="SculptLayer"/> is only deltas.
/// <see cref="Name"/> is authoritative and is mirrored onto <see cref="PaintLayer.Name"/> when a
/// paint layer is added, because instances and variant overrides address entries by name.
/// </summary>
public sealed class UnifiedLayerEntry
{
	public UnifiedLayerKind Kind;
	public string Name = "";
	public string Group = "";
	public bool Enabled = true;

	/// <summary>The paint layer, for a paint entry that owns its content. Null for an instance.</summary>
	public PaintLayer Paint;

	/// <summary>The sculpt deltas, for a sculpt entry that owns its content. Null for an instance.
/// Deltas only — never a mesh copy; see <see cref="UnifiedLayerStack"/>.</summary>
	public SculptLayer Sculpt;

	/// <summary>Sculpt only: how hard this layer's deltas land, -1..1. Paint entries ignore it.</summary>
	public float Intensity = 1f;

	/// <summary>
	/// Name of the entry this one instances, or null to own its content. An instance shows the
	/// source's maps / deltas live, so editing the source repaints every instance; the instance's
	/// own group, blend, opacity, mask and keyframes still apply on top. A missing or cyclic
	/// source contributes nothing and is reported by <see cref="UnifiedLayerStack.MissingInstances"/>.
	/// </summary>
	public string InstanceOf;

	/// <summary>Per-channel mask overrides: a channel listed here uses its own mask instead of the
	/// entry's mask, so colour can be masked while roughness is not. Null / absent = the entry mask.</summary>
	public Dictionary<PaintChannel, float[]> ChannelMasks;

	/// <summary>Per-channel blend and opacity overrides: a channel listed here composites with its
	/// own blend and opacity instead of the entry's. Absent = the entry's blend and opacity.</summary>
	public Dictionary<PaintChannel, (PaintBlendMode Blend, float Opacity)> ChannelOverrides;

	/// <summary>Opacity timeline, sorted by time. Empty = the entry's static opacity.</summary>
	public List<OpacityKeyframe> OpacityKeys = new();

	/// <summary>The entry's opacity at <paramref name="time"/>: the keyframed value when keys exist,
	/// linearly interpolated, clamped to the first / last key outside the range; the static opacity
	/// (<paramref name="staticOpacity"/>) when there are no keys.</summary>
	public float OpacityAt( float time, float staticOpacity )
	{
		if ( OpacityKeys.Count == 0 )
			return staticOpacity;

		if ( time <= OpacityKeys[0].Time )
			return OpacityKeys[0].Opacity;

		for ( var i = 1; i < OpacityKeys.Count; i++ )
		{
			if ( time <= OpacityKeys[i].Time )
			{
				var a = OpacityKeys[i - 1];
				var b = OpacityKeys[i];
				var span = b.Time - a.Time;
				var t = span < 1e-6f ? 1f : (time - a.Time) / span;
				return a.Opacity + (b.Opacity - a.Opacity) * Math.Clamp( t, 0f, 1f );
			}
		}

		return OpacityKeys[OpacityKeys.Count - 1].Opacity;
	}

	/// <summary>Keep <see cref="OpacityKeys"/> sorted by time after an edit that may have moved one.</summary>
	public void SortKeys() => OpacityKeys.Sort( ( a, b ) => a.Time.CompareTo( b.Time ) );
}

/// <summary>
/// ONE Layers system for the whole app: sculpt layers (displacement deltas) and paint layers
/// (colour / roughness / metal / height / emissive / opacity / normal) in a single ordered stack
/// with groups, per-channel opacity / blend modes, masks (painted / procedural / generator, via
/// <see cref="PaintLayers.FinalMask"/>), instancing, live reorder, variant stacks A/B/C and a
/// layer-to-rig bridge.
///
/// This owns the UNIFIED MODEL and the paint-layer stack. The sculpt-layer data model itself is
/// the existing <see cref="SculptLayer"/> — deltas on a <see cref="SculptFrames"/> basis, captured
/// by <see cref="SculptLayer.Capture"/> and applied by <see cref="SculptLayer.Apply"/> — which the
/// sibling godlike-sculpt-layers ticket manages; this stack wraps those layers, it does not
/// re-implement them. Paint compositing is exactly the feature-tree compositing in
/// <see cref="PaintLayers"/>: when an entry has no per-channel overrides the fast path calls
/// <see cref="PaintLayers.CompositeSet"/> directly, so a stack and a feature tree with the same
/// layers, blends, opacities and masks produce the same canvas.
///
/// STORAGE: a sculpt entry holds its <see cref="SculptLayer"/> deltas and nothing else — no mesh
/// copy, no rest positions. <see cref="ApplySculpt"/> returns a fresh summed array every call, so
/// mutating the result can never rewrite a stored layer. A 4K paint layer is still a canvas per
/// channel it touches, the same as a <see cref="PaintLayer"/>; the stack adds no copies of it.
/// </summary>
public sealed class UnifiedLayerStack
{
	public const int JsonVersion = 1;

	public List<UnifiedLayerEntry> Entries = new();

	/// <summary>Group names in panel order. "" is the ungrouped bucket and is never listed here.</summary>
	public List<string> Groups = new();

	/// <summary>The active variant (A, B, C, ...), or null / "" for the base values.</summary>
	public string ActiveVariant;

	/// <summary>Variant name -> entry name -> that entry's overrides in that variant.</summary>
	public Dictionary<string, Dictionary<string, LayerVariantOverride>> Variants = new();

	public int Count => Entries.Count;

	public UnifiedLayerEntry this[int index] => Entries[index];

	public UnifiedLayerEntry Find( string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return null;

		foreach ( var entry in Entries )
		{
			if ( entry.Name == name )
				return entry;
		}

		return null;
	}

	void RequireUniqueName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( "a layer entry needs a name", nameof( name ) );

		if ( Find( name ) is not null )
			throw new ArgumentException( $"the stack already has an entry named '{name}'", nameof( name ) );
	}

	/// <summary>Register a group name in panel order. Empty and duplicate names are ignored.</summary>
	public void AddGroup( string group )
	{
		if ( string.IsNullOrEmpty( group ) || Groups.Contains( group ) )
			return;

		Groups.Add( group );
	}

	/// <summary>Move the group at <paramref name="from"/> to <paramref name="to"/> (both clamped).</summary>
	public void MoveGroup( int from, int to )
	{
		if ( Groups.Count == 0 )
			return;

		from = Math.Clamp( from, 0, Groups.Count - 1 );
		to = Math.Clamp( to, 0, Groups.Count - 1 );

		if ( from == to )
			return;

		var group = Groups[from];
		Groups.RemoveAt( from );
		Groups.Insert( to, group );
	}

	/// <summary>Move the entry at <paramref name="from"/> to <paramref name="to"/> (both clamped),
	/// so reordering the stack is one call, not a remove-and-reinsert that can drop the entry.</summary>
	public void Move( int from, int to )
	{
		if ( Entries.Count == 0 )
			return;

		from = Math.Clamp( from, 0, Entries.Count - 1 );
		to = Math.Clamp( to, 0, Entries.Count - 1 );

		if ( from == to )
			return;

		var entry = Entries[from];
		Entries.RemoveAt( from );
		Entries.Insert( to, entry );
	}

	/// <summary>Add a paint layer as an entry. The layer's name becomes the entry name.</summary>
	public UnifiedLayerEntry AddPaint( PaintLayer paint, string group = "" )
	{
		if ( paint is null )
			throw new ArgumentNullException( nameof( paint ) );

		RequireUniqueName( paint.Name );
		AddGroup( group );

		var entry = new UnifiedLayerEntry
		{
			Kind = UnifiedLayerKind.Paint,
			Name = paint.Name,
			Group = group ?? "",
			Paint = paint,
		};
		Entries.Add( entry );
		return entry;
	}

	/// <summary>Add a sculpt layer as an entry. The deltas are stored as given; no mesh is kept.</summary>
	public UnifiedLayerEntry AddSculpt( string name, SculptLayer sculpt, string group = "", float intensity = 1f )
	{
		if ( sculpt is null )
			throw new ArgumentNullException( nameof( sculpt ) );

		RequireUniqueName( name );
		AddGroup( group );

		var entry = new UnifiedLayerEntry
		{
			Kind = UnifiedLayerKind.Sculpt,
			Name = name,
			Group = group ?? "",
			Sculpt = sculpt,
			Intensity = Math.Clamp( intensity, -1f, 1f ),
		};
		Entries.Add( entry );
		return entry;
	}

	/// <summary>Add an instance entry: it shows the source entry's content live, under its own
	/// name, group, blend, opacity and mask. The source must exist when the stack composites;
	/// <see cref="MissingInstances"/> reports entries whose source cannot be found.</summary>
	public UnifiedLayerEntry AddInstance( UnifiedLayerKind kind, string name, string sourceName, string group = "" )
	{
		RequireUniqueName( name );
		AddGroup( group );

		var entry = new UnifiedLayerEntry
		{
			Kind = kind,
			Name = name,
			Group = group ?? "",
			InstanceOf = sourceName,
		};

		if ( kind == UnifiedLayerKind.Paint )
		{
			// A shell layer: it carries this instance's own blend / opacity / mask, while
			// its content (Maps) always resolves from the source entry at composite time.
			entry.Paint = new PaintLayer( name, new PaintLayerMaps() );
		}

		Entries.Add( entry );
		return entry;
	}

	// -- variant overrides -------------------------------------------------

	/// <summary>The overrides for <paramref name="variant"/> (created when missing), or null when
	/// the variant was never given an override and <paramref name="create"/> is false.</summary>
	public Dictionary<string, LayerVariantOverride> VariantOverrides( string variant, bool create = false )
	{
		if ( string.IsNullOrEmpty( variant ) )
			return null;

		if ( Variants.TryGetValue( variant, out var overrides ) )
			return overrides;

		if ( !create )
			return null;

		overrides = new Dictionary<string, LayerVariantOverride>();
		Variants[variant] = overrides;
		return overrides;
	}

	public LayerVariantOverride VariantOverride( string variant, string entryName, bool create = false )
	{
		var overrides = VariantOverrides( variant, create );

		if ( overrides is null )
			return null;

		if ( overrides.TryGetValue( entryName, out var existing ) )
			return existing;

		if ( !create )
			return null;

		existing = new LayerVariantOverride();
		overrides[entryName] = existing;
		return existing;
	}

	/// <summary>Set one field of one entry's override inside one variant. Unset fields stay null,
	/// which means "use the entry's own value in this variant".</summary>
	public void SetVariantOverride( string variant, string entryName,
		float? opacity = null, float? intensity = null, bool? enabled = null )
	{
		if ( string.IsNullOrEmpty( variant ) )
			throw new ArgumentException( "a variant override needs a variant name", nameof( variant ) );

		if ( Find( entryName ) is null )
			throw new ArgumentException( $"the stack has no entry named '{entryName}'", nameof( entryName ) );

		var o = VariantOverride( variant, entryName, create: true );

		if ( opacity.HasValue ) o.Opacity = Math.Clamp( opacity.Value, 0f, 1f );
		if ( intensity.HasValue ) o.Intensity = Math.Clamp( intensity.Value, -1f, 1f );
		if ( enabled.HasValue ) o.Enabled = enabled.Value;
	}

	// -- effective values --------------------------------------------------

	float EffectiveOpacity( UnifiedLayerEntry entry, string variant )
	{
		var o = VariantOverride( variant, entry.Name );
		if ( o?.Opacity.HasValue == true )
			return o.Opacity.Value;

		return entry.Kind == UnifiedLayerKind.Paint && entry.Paint is not null
			? entry.Paint.Opacity
			: 1f;
	}

	float EffectiveIntensity( UnifiedLayerEntry entry, string variant )
	{
		var o = VariantOverride( variant, entry.Name );
		if ( o?.Intensity.HasValue == true )
			return o.Intensity.Value;

		return entry.Intensity;
	}

	bool EffectiveEnabled( UnifiedLayerEntry entry, string variant )
	{
		var o = VariantOverride( variant, entry.Name );
		if ( o?.Enabled.HasValue == true )
			return o.Enabled.Value;

		return entry.Enabled;
	}

	// -- instance resolution -----------------------------------------------

	PaintLayerMaps ResolvePaint( UnifiedLayerEntry entry )
	{
		var current = entry;
		var seen = new HashSet<string>();

		while ( current is not null )
		{
			if ( !seen.Add( current.Name ) )
				return null; // instance cycle: contributes nothing, reported by MissingInstances

			if ( current.InstanceOf is null )
				return current.Paint?.Maps;

			current = Find( current.InstanceOf );
		}

		return null;
	}

	SculptLayer ResolveSculpt( UnifiedLayerEntry entry )
	{
		var current = entry;
		var seen = new HashSet<string>();

		while ( current is not null )
		{
			if ( !seen.Add( current.Name ) )
				return null;

			if ( current.InstanceOf is null )
				return current.Sculpt;

			current = Find( current.InstanceOf );
		}

		return null;
	}

	/// <summary>Names of entries whose <c>InstanceOf</c> cannot be resolved — a missing source or
	/// an instance cycle. Such an entry contributes nothing when the stack composites.</summary>
	public List<string> MissingInstances()
	{
		var missing = new List<string>();

		foreach ( var entry in Entries )
		{
			if ( entry.InstanceOf is null )
				continue;

			var current = Find( entry.InstanceOf );
			var seen = new HashSet<string> { entry.Name };
			var ok = current is not null;

			while ( ok && current.InstanceOf is not null )
			{
				if ( !seen.Add( current.Name ) )
				{
					ok = false;
					break;
				}

				current = Find( current.InstanceOf );
				ok = current is not null;
			}

			if ( !ok )
				missing.Add( entry.Name );
		}

		return missing;
	}

	// -- paint compositing -------------------------------------------------

	/// <summary>Composite the whole stack over <paramref name="below"/> (null for a bare surface),
	/// bottom to top, under the active variant. Returns <paramref name="below"/> itself when
	/// nothing enabled has content.</summary>
	public PaintCanvas CompositePaint( PaintCanvas below ) => CompositePaint( ActiveVariant, below );

	/// <summary>Composite the whole stack under the named <paramref name="variant"/> without
	/// changing <see cref="ActiveVariant"/>: switching variants never mutates another variant's
	/// stored overrides, because overrides are only ever read here, never written.</summary>
	public PaintCanvas CompositePaint( string variant, PaintCanvas below )
	{
		var result = below;

		foreach ( var entry in Entries )
		{
			if ( entry.Kind != UnifiedLayerKind.Paint || !EffectiveEnabled( entry, variant ) )
				continue;

			var maps = ResolvePaint( entry );

			if ( maps is null || !maps.HasSurface )
				continue;

			result = CompositePaintEntry( result, entry, maps, variant, null );
		}

		return result;
	}

	/// <summary>Composite under the active variant at <paramref name="time"/> on each entry's
	/// opacity timeline: entries with keyframes take their interpolated opacity, the rest take
	/// their effective (variant-aware) opacity.</summary>
	public PaintCanvas CompositePaintAt( float time, PaintCanvas below ) => CompositePaintAt( ActiveVariant, time, below );

	/// <summary>Composite under the named <paramref name="variant"/> at <paramref name="time"/>.</summary>
	public PaintCanvas CompositePaintAt( string variant, float time, PaintCanvas below )
	{
		var result = below;

		foreach ( var entry in Entries )
		{
			if ( entry.Kind != UnifiedLayerKind.Paint || !EffectiveEnabled( entry, variant ) )
				continue;

			var maps = ResolvePaint( entry );

			if ( maps is null || !maps.HasSurface )
				continue;

			result = CompositePaintEntry( result, entry, maps, variant, time );
		}

		return result;
	}

	PaintCanvas CompositePaintEntry( PaintCanvas below, UnifiedLayerEntry entry, PaintLayerMaps maps,
		string variant, float? time )
	{
		var size = maps.Size > 0 ? maps.Size : (maps.Color?.Width ?? below?.Width ?? 0);

		if ( size <= 0 )
			return below;

		// The entry mask is shared by every channel; a per-channel mask or override needs the
		// channel-by-channel path below, where each channel gets its own mask and blend.
		var needsPerChannel = entry.ChannelMasks is not null || entry.ChannelOverrides is not null;

		if ( !needsPerChannel )
		{
			var opacity = time.HasValue
				? entry.OpacityAt( time.Value, EffectiveOpacity( entry, variant ) )
				: EffectiveOpacity( entry, variant );

			ValidateMask( entry.Paint?.Mask, size );
			var blend = entry.Paint?.Blend ?? PaintBlendMode.Normal;
			return PaintLayers.CompositeSet( below, maps, blend, opacity, entry.Paint?.Mask );
		}

		// Per-channel path: each surface channel composites on its own, with its own mask, blend
		// and opacity, and the result carries the channels as companions on the colour canvas —
		// the same shape CompositeSet returns, so callers cannot tell which path ran.
		PaintCanvas colour = null;
		var resultMaps = new PaintLayerMaps { Size = size };

		foreach ( var channel in PaintChannels.Surface )
		{
			var src = maps.Get( channel );

			if ( src is null && below?.Channel( channel ) is null )
				continue;

			var (blend, opacity) = ChannelSettings( entry, channel, variant, time );

			float[] mask = null;
			if ( entry.ChannelMasks is not null && entry.ChannelMasks.TryGetValue( channel, out var channelMask ) )
				mask = channelMask;
			else
				mask = entry.Paint?.Mask;

			ValidateMask( mask, size );
			var composited = PaintLayers.Composite( below?.Channel( channel ), src, blend, opacity, mask );
			resultMaps.Set( channel, composited );

			if ( channel == PaintChannel.Color )
				colour = composited;
		}

		if ( colour is null )
		{
			// No colour content anywhere: a layer that only paints roughness still needs a
			// colour canvas to carry the companions on, the way CompositeSet makes one.
			if ( !resultMaps.HasSurface )
				return below;

			colour = new PaintCanvas( size, size );

			if ( below is not null )
			{
				var baseCopy = PaintLayers.Resample( below, size, size );
				Array.Copy( baseCopy.Rgba, colour.Rgba, baseCopy.Rgba.Length );
			}
		}
		else if ( ReferenceEquals( colour, below ) )
		{
			// The colour channel had no content of its own, so the composite handed the
			// backdrop back unchanged; copy it before hanging companions on it, or the
			// caller's backdrop would gain channels it never had.
			var copy = new PaintCanvas( colour.Width, colour.Height );
			Array.Copy( colour.Rgba, copy.Rgba, colour.Rgba.Length );
			colour = copy;
		}

		colour.RoughnessMap = resultMaps.Roughness;
		colour.MetalnessMap = resultMaps.Metalness;
		colour.HeightMap = resultMaps.Height;
		colour.EmissiveMap = resultMaps.Emissive;
		colour.OpacityMap = resultMaps.Opacity;
		colour.NormalMap = resultMaps.Normal;
		colour.AmbientOcclusionMap = below?.AmbientOcclusionMap;
		return colour;
	}

	(PaintBlendMode Blend, float Opacity) ChannelSettings( UnifiedLayerEntry entry, PaintChannel channel,
		string variant, float? time )
	{
		var blend = entry.Paint?.Blend ?? PaintBlendMode.Normal;
		var opacity = time.HasValue
			? entry.OpacityAt( time.Value, EffectiveOpacity( entry, variant ) )
			: EffectiveOpacity( entry, variant );

		if ( entry.ChannelOverrides is not null && entry.ChannelOverrides.TryGetValue( channel, out var o ) )
		{
			blend = o.Blend;
			opacity = o.Opacity;
		}

		return (blend, opacity);
	}

	static void ValidateMask( float[] mask, int size )
	{
		if ( mask is not null && mask.Length != size * size )
			throw new ArgumentException(
				$"a layer mask has {mask.Length} texels for a {size}x{size} layer", nameof( mask ) );
	}

	// -- sculpt ------------------------------------------------------------

	/// <summary>
	/// The enabled sculpt entries summed into one frame-space delta array, each scaled by its
	/// effective intensity under the active variant. A fresh array every call: mutating the result
	/// never rewrites a stored layer. All enabled sculpt entries must agree on the vertex count.
	/// </summary>
	public Vec3[] ApplySculpt() => ApplySculpt( ActiveVariant );

	/// <summary>Summed sculpt deltas under the named <paramref name="variant"/>.</summary>
	public Vec3[] ApplySculpt( string variant )
	{
		var count = -1;

		foreach ( var entry in Entries )
		{
			if ( entry.Kind != UnifiedLayerKind.Sculpt || !EffectiveEnabled( entry, variant ) )
				continue;

			var sculpt = ResolveSculpt( entry );

			if ( sculpt is null )
				continue;

			if ( count < 0 )
				count = sculpt.Count;
			else if ( sculpt.Count != count )
				throw new ArgumentException(
					$"sculpt entry '{entry.Name}' has {sculpt.Count} deltas, the stack sums {count}", nameof( entry ) );
		}

		if ( count < 0 )
			return Array.Empty<Vec3>();

		var sum = new Vec3[count];

		foreach ( var entry in Entries )
		{
			if ( entry.Kind != UnifiedLayerKind.Sculpt || !EffectiveEnabled( entry, variant ) )
				continue;

			var sculpt = ResolveSculpt( entry );

			if ( sculpt is null )
				continue;

			var intensity = EffectiveIntensity( entry, variant );

			if ( MathF.Abs( intensity ) < 1e-6f )
				continue;

			for ( var i = 0; i < count; i++ )
				sum[i] += sculpt.Deltas[i] * intensity;
		}

		return sum;
	}

	/// <summary>Apply the summed sculpt deltas to <paramref name="mesh"/>, which must be at rest
	/// with the frames <paramref name="frames"/> built from it (see
	/// <see cref="SculptFrames.Build"/>). The mesh is the caller's; the stack keeps no copy.</summary>
	public void ApplyToMesh( PolyMesh mesh, SculptFrames frames )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( frames is null )
			throw new ArgumentNullException( nameof( frames ) );

		var sum = ApplySculpt();

		if ( sum.Length == 0 )
			return;

		if ( sum.Length != mesh.VertexCount || frames.Count != mesh.VertexCount )
			throw new ArgumentException(
				$"the summed deltas have {sum.Length} vertices, the mesh has {mesh.VertexCount}", nameof( mesh ) );

		for ( var i = 0; i < sum.Length; i++ )
			mesh.Positions[i] += frames.At[i].ToWorld( sum[i] );
	}

	// -- bake and bridge ---------------------------------------------------

	/// <summary>
	/// Bake only the approved variant — the active one — into its finished outputs, without
	/// destroying the source entries: the paint composite of the active variant as a
	/// <see cref="PaintLayerMaps"/>, and the summed sculpt deltas as a <see cref="SculptLayer"/>.
	/// Either side is null when that side has nothing enabled to bake. The other variants are
	/// untouched, so baking variant A never bakes — or changes — variant B.
	/// </summary>
	public (PaintLayerMaps Paint, SculptLayer Sculpt) BakeApprovedVariant()
	{
		PaintLayerMaps bakedPaint = null;
		var canvas = CompositePaint( null );

		if ( canvas is not null )
		{
			bakedPaint = new PaintLayerMaps
			{
				Size = canvas.Width,
				Color = canvas,
				Roughness = canvas.RoughnessMap,
				Metalness = canvas.MetalnessMap,
				Height = canvas.HeightMap,
				Emissive = canvas.EmissiveMap,
				Opacity = canvas.OpacityMap,
				Normal = canvas.NormalMap,
			};
		}

		SculptLayer bakedSculpt = null;
		var deltas = ApplySculpt();

		if ( deltas.Length > 0 )
			bakedSculpt = new SculptLayer( deltas );

		return (bakedPaint, bakedSculpt);
	}

	/// <summary>
	/// The masked texture variant for the named <paramref name="variant"/>: the stack composited
	/// under that variant as a <see cref="PaintLayerMaps"/>, without changing
	/// <see cref="ActiveVariant"/>. Null when the variant paints nothing.
	/// </summary>
	public PaintLayerMaps PaintVariantTexture( string variant )
	{
		var canvas = CompositePaint( variant, null );

		if ( canvas is null )
			return null;

		return new PaintLayerMaps
		{
			Size = canvas.Width,
			Color = canvas,
			Roughness = canvas.RoughnessMap,
			Metalness = canvas.MetalnessMap,
			Height = canvas.HeightMap,
			Emissive = canvas.EmissiveMap,
			Opacity = canvas.OpacityMap,
			Normal = canvas.NormalMap,
		};
	}

	/// <summary>
	/// The layer-to-rig bridge: a sculpt entry's deltas as a morph target, scaled by the entry's
	/// effective intensity. The morph is NOT added to a mesh here — use
	/// <see cref="ApplyMorphToMesh"/> for that. FBX export reads morphs from
	/// <c>PolyMesh.Morphs</c> (<c>FbxBlendShapes</c>), and GLB the same way, so a morph built here
	/// and put on a mesh exports as a blend shape without any further conversion.
	/// </summary>
	public MeshMorph ToMorph( string morphName, UnifiedLayerEntry sculptEntry )
	{
		if ( string.IsNullOrWhiteSpace( morphName ) )
			throw new ArgumentException( "a morph needs a name", nameof( morphName ) );

		if ( sculptEntry is null )
			throw new ArgumentNullException( nameof( sculptEntry ) );

		if ( sculptEntry.Kind != UnifiedLayerKind.Sculpt )
			throw new ArgumentException( $"entry '{sculptEntry.Name}' is paint, not sculpt", nameof( sculptEntry ) );

		var sculpt = ResolveSculpt( sculptEntry );

		if ( sculpt is null )
			throw new ArgumentException( $"sculpt entry '{sculptEntry.Name}' has no deltas to bridge", nameof( sculptEntry ) );

		var intensity = EffectiveIntensity( sculptEntry, ActiveVariant );
		var deltas = new Vec3[sculpt.Count];

		for ( var i = 0; i < deltas.Length; i++ )
			deltas[i] = sculpt.Deltas[i] * intensity;

		return new MeshMorph { Name = morphName, Deltas = deltas };
	}

	/// <summary>Build the morph for a sculpt entry and put it on <paramref name="mesh"/>, replacing
	/// any morph of the same name — the same replace rule <see cref="PolyMesh.AddMorph"/> uses.
	/// The morph is only valid while the mesh keeps its vertex count (see
	/// <c>PolyMesh.ValidMorphs</c>).</summary>
	public MeshMorph ApplyMorphToMesh( PolyMesh mesh, string morphName, UnifiedLayerEntry sculptEntry )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var morph = ToMorph( morphName, sculptEntry );

		if ( morph.Deltas.Length != mesh.VertexCount )
			throw new ArgumentException(
				$"morph '{morphName}' has {morph.Deltas.Length} deltas, the mesh has {mesh.VertexCount}", nameof( mesh ) );

		mesh.Morphs.RemoveAll( m => m?.Name == morphName );
		mesh.Morphs.Add( morph );
		return morph;
	}

	// -- JSON round-trip ---------------------------------------------------

	/// <summary>
	/// The whole stack as JSON: entries (kind, name, group, enabled, blend, opacity, intensity,
	/// masks, per-channel overrides, keyframes, instance refs, paint maps as base64 RGBA, sculpt
	/// deltas), groups, the active variant and every variant's overrides. Unknown fields are
	/// ignored on read, so a newer writer's stack still loads. This is the stack's interchange
	/// form; the document serializer owns the compact on-disk form.
	/// </summary>
	public string ToJson()
	{
		var root = new JsonObject
		{
			["version"] = JsonVersion,
			["activeVariant"] = ActiveVariant,
		};

		var groups = new JsonArray();
		foreach ( var group in Groups )
			groups.Add( group );
		root["groups"] = groups;

		var entries = new JsonArray();
		foreach ( var entry in Entries )
			entries.Add( EntryToJson( entry ) );
		root["entries"] = entries;

		var variants = new JsonObject();
		foreach ( var (variant, overrides) in Variants )
		{
			var v = new JsonObject();
			foreach ( var (entryName, o) in overrides )
			{
				var oJson = new JsonObject();
				if ( o.Opacity.HasValue ) oJson["opacity"] = o.Opacity.Value;
				if ( o.Intensity.HasValue ) oJson["intensity"] = o.Intensity.Value;
				if ( o.Enabled.HasValue ) oJson["enabled"] = o.Enabled.Value;
				v[entryName] = oJson;
			}
			variants[variant] = v;
		}
		root["variants"] = variants;

		return root.ToJsonString();
	}

	static JsonObject EntryToJson( UnifiedLayerEntry entry )
	{
		var o = new JsonObject
		{
			["kind"] = entry.Kind.ToString(),
			["name"] = entry.Name,
			["group"] = entry.Group,
			["enabled"] = entry.Enabled,
			["intensity"] = entry.Intensity,
			["instanceOf"] = entry.InstanceOf,
		};

		if ( entry.Paint is not null )
		{
			o["blend"] = entry.Paint.Blend.ToString();
			o["opacity"] = entry.Paint.Opacity;
			o["mask"] = MaskToJson( entry.Paint.Mask );
			o["maps"] = MapsToJson( entry.Paint.Maps );
		}

		if ( entry.ChannelMasks is not null )
		{
			var masks = new JsonObject();
			foreach ( var (channel, mask) in entry.ChannelMasks )
				masks[channel.ToString()] = MaskToJson( mask );
			o["channelMasks"] = masks;
		}

		if ( entry.ChannelOverrides is not null )
		{
			var overrides = new JsonObject();
			foreach ( var (channel, setting) in entry.ChannelOverrides )
			{
				overrides[channel.ToString()] = new JsonObject
				{
					["blend"] = setting.Blend.ToString(),
					["opacity"] = setting.Opacity,
				};
			}
			o["channelOverrides"] = overrides;
		}

		var keys = new JsonArray();
		foreach ( var key in entry.OpacityKeys )
			keys.Add( new JsonObject { ["time"] = key.Time, ["opacity"] = key.Opacity } );
		o["opacityKeys"] = keys;

		if ( entry.Sculpt is not null )
		{
			var deltas = new JsonArray();
			foreach ( var d in entry.Sculpt.Deltas )
				deltas.Add( new JsonArray( d.x, d.y, d.z ) );
			o["deltas"] = deltas;
		}

		return o;
	}

	static JsonNode MaskToJson( float[] mask )
	{
		if ( mask is null )
			return null;

		var a = new JsonArray();
		foreach ( var v in mask )
			a.Add( v );
		return a;
	}

	static JsonObject MapsToJson( PaintLayerMaps maps )
	{
		if ( maps is null )
			return null;

		var o = new JsonObject { ["size"] = maps.Size };

		foreach ( var channel in PaintChannels.Surface )
		{
			var canvas = maps.Get( channel );

			if ( canvas is null )
				continue;

			o[channel.ToString()] = new JsonObject
			{
				["width"] = canvas.Width,
				["height"] = canvas.Height,
				["rgba"] = Convert.ToBase64String( canvas.Rgba ),
			};
		}

		var maskPaint = maps.MaskPaint;

		if ( maskPaint is not null )
		{
			o[PaintChannel.Mask.ToString()] = new JsonObject
			{
				["width"] = maskPaint.Width,
				["height"] = maskPaint.Height,
				["rgba"] = Convert.ToBase64String( maskPaint.Rgba ),
			};
		}

		return o;
	}

	public static UnifiedLayerStack FromJson( string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "a stack needs JSON to read", nameof( json ) );

		var root = JsonNode.Parse( json )?.AsObject()
			?? throw new JsonException( "the stack JSON has no object at its root" );

		var version = root["version"]?.GetValue<int>() ?? 1;

		if ( version != JsonVersion )
			throw new JsonException( $"the stack JSON is version {version}, this reader takes {JsonVersion}" );

		var stack = new UnifiedLayerStack
		{
			ActiveVariant = root["activeVariant"]?.GetValue<string>(),
		};

		foreach ( var group in root["groups"]?.AsArray() ?? new JsonArray() )
		{
			var name = group?.GetValue<string>();
			if ( !string.IsNullOrEmpty( name ) )
				stack.Groups.Add( name );
		}

		foreach ( var node in root["entries"]?.AsArray() ?? new JsonArray() )
			stack.Entries.Add( EntryFromJson( node?.AsObject()
				?? throw new JsonException( "a stack entry is not an object" ) ) );

		foreach ( var (variant, node) in root["variants"]?.AsObject() ?? new JsonObject() )
		{
			var overrides = new Dictionary<string, LayerVariantOverride>();

			foreach ( var (entryName, oNode) in node?.AsObject() ?? new JsonObject() )
			{
				var oJson = oNode?.AsObject();
				var o = new LayerVariantOverride();

				if ( oJson is not null )
				{
					if ( oJson["opacity"] is not null ) o.Opacity = oJson["opacity"].GetValue<float>();
					if ( oJson["intensity"] is not null ) o.Intensity = oJson["intensity"].GetValue<float>();
					if ( oJson["enabled"] is not null ) o.Enabled = oJson["enabled"].GetValue<bool>();
				}

				overrides[entryName] = o;
			}

			stack.Variants[variant] = overrides;
		}

		return stack;
	}

	static UnifiedLayerEntry EntryFromJson( JsonObject o )
	{
		var kind = Enum.Parse<UnifiedLayerKind>( o["kind"]?.GetValue<string>() ?? "Paint" );

		var entry = new UnifiedLayerEntry
		{
			Kind = kind,
			Name = o["name"]?.GetValue<string>() ?? "",
			Group = o["group"]?.GetValue<string>() ?? "",
			Enabled = o["enabled"]?.GetValue<bool>() ?? true,
			Intensity = o["intensity"]?.GetValue<float>() ?? 1f,
			InstanceOf = o["instanceOf"]?.GetValue<string>(),
		};

		float[] entryMask = null;

		if ( o["mask"] is JsonArray maskJson )
			entryMask = MaskFromJson( maskJson );

		if ( o["channelMasks"] is JsonObject channelMasks )
		{
			entry.ChannelMasks = new Dictionary<PaintChannel, float[]>();
			foreach ( var (name, node) in channelMasks )
			{
				if ( Enum.TryParse<PaintChannel>( name, out var channel ) && node is JsonArray a )
					entry.ChannelMasks[channel] = MaskFromJson( a );
			}
		}

		if ( o["channelOverrides"] is JsonObject channelOverrides )
		{
			entry.ChannelOverrides = new Dictionary<PaintChannel, (PaintBlendMode, float)>();
			foreach ( var (name, node) in channelOverrides )
			{
				if ( Enum.TryParse<PaintChannel>( name, out var channel ) && node is JsonObject setting )
				{
					var blend = Enum.Parse<PaintBlendMode>( setting["blend"]?.GetValue<string>() ?? "Normal" );
					var opacity = setting["opacity"]?.GetValue<float>() ?? 1f;
					entry.ChannelOverrides[channel] = (blend, opacity);
				}
			}
		}

		foreach ( var node in o["opacityKeys"]?.AsArray() ?? new JsonArray() )
		{
			var key = node?.AsObject();
			if ( key is null )
				continue;

			entry.OpacityKeys.Add( new OpacityKeyframe(
				key["time"]?.GetValue<float>() ?? 0f,
				key["opacity"]?.GetValue<float>() ?? 1f ) );
		}
		entry.SortKeys();

		if ( kind == UnifiedLayerKind.Paint && o["maps"] is JsonObject mapsJson )
		{
			var maps = MapsFromJson( mapsJson );
			var blend = Enum.Parse<PaintBlendMode>( o["blend"]?.GetValue<string>() ?? "Normal" );
			var opacity = o["opacity"]?.GetValue<float>() ?? 1f;
			entry.Paint = new PaintLayer( entry.Name, maps, blend, opacity, entryMask );
		}
		else if ( kind == UnifiedLayerKind.Paint && entry.InstanceOf is not null )
		{
			// An instance carries no maps of its own; its mask still lives on the entry via
			// the paint layer's mask slot, so give it an empty layer to hold it.
			var blend = Enum.Parse<PaintBlendMode>( o["blend"]?.GetValue<string>() ?? "Normal" );
			var opacity = o["opacity"]?.GetValue<float>() ?? 1f;
			entry.Paint = new PaintLayer( entry.Name, new PaintLayerMaps(), blend, opacity, entryMask );
		}

		if ( o["deltas"] is JsonArray deltasJson )
		{
			var deltas = new Vec3[deltasJson.Count];
			for ( var i = 0; i < deltas.Length; i++ )
			{
				var d = deltasJson[i]?.AsArray();
				deltas[i] = d is null
					? Vec3.Zero
					: new Vec3(
						d.Count > 0 ? d[0]?.GetValue<float>() ?? 0f : 0f,
						d.Count > 1 ? d[1]?.GetValue<float>() ?? 0f : 0f,
						d.Count > 2 ? d[2]?.GetValue<float>() ?? 0f : 0f );
			}
			entry.Sculpt = new SculptLayer( deltas );
		}

		return entry;
	}

	static float[] MaskFromJson( JsonArray a )
	{
		var mask = new float[a.Count];
		for ( var i = 0; i < mask.Length; i++ )
			mask[i] = a[i]?.GetValue<float>() ?? 0f;
		return mask;
	}

	static PaintLayerMaps MapsFromJson( JsonObject o )
	{
		var maps = new PaintLayerMaps { Size = o["size"]?.GetValue<int>() ?? 0 };

		foreach ( var (name, node) in o )
		{
			if ( name == "size" || node is not JsonObject canvasJson )
				continue;

			if ( !Enum.TryParse<PaintChannel>( name, out var channel ) )
				continue;

			var width = canvasJson["width"]?.GetValue<int>() ?? 0;
			var height = canvasJson["height"]?.GetValue<int>() ?? 0;
			var rgba = Convert.FromBase64String( canvasJson["rgba"]?.GetValue<string>() ?? "" );

			if ( width <= 0 || height <= 0 || rgba.Length != width * height * 4 )
				throw new JsonException( $"the stack JSON has a bad canvas for channel {channel}" );

			var canvas = new PaintCanvas( width, height );
			Array.Copy( rgba, canvas.Rgba, rgba.Length );
			maps.Set( channel, canvas );
		}

		return maps;
	}
}
