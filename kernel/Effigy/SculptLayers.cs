using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Effigy;

/// <summary>
/// How one sculpt layer's displacement combines with the layers below it.
///
/// These are displacement modes, not colour modes: a layer holds frame-space offset
/// vectors, so the only honest combinations are the ones defined on vectors. Nomad's
/// 25 paint blend modes do not apply here — a sculpt layer has no colour to blend.
/// </summary>
public enum SculptLayerBlend
{
	/// <summary>Add the layer's displacement, scaled by intensity and mask.</summary>
	Add = 0,

	/// <summary>Subtract the layer's displacement — carve what the layer raised.</summary>
	Subtract = 1,

	/// <summary>
	/// Multiply the accumulated displacement, component-wise, by (1 + layer * scale):
	/// a positive layer amplifies what is already there, a negative one damps it toward
	/// flat. This is the displacement analogue of a multiply blend, not a colour multiply.
	/// </summary>
	Multiply = 2,

	/// <summary>
	/// Replace the accumulated displacement with the layer's, lerped by intensity * mask.
	/// At full strength the layer below is invisible wherever this layer has a value.
	/// </summary>
	Replace = 3,
}

/// <summary>
/// One named sculpt layer: a <see cref="SculptLayer"/> of frame-space deltas at the
/// stack's level, plus the ZBrush-style controls — intensity, per-vertex mask, blend
/// mode, enabled flag.
///
/// Deltas are stored, never a mesh copy: one layer costs one <see cref="SculptLayer"/>
/// (12 bytes a vertex in memory, 6 in the blob), while a full mesh copy costs positions
/// plus faces plus corner UVs. See <see cref="SculptLayerStack.FullMeshCopyBytes"/>.
/// </summary>
public sealed class SculptLayerEntry
{
	float _intensity = 1f;
	float[] _mask;

	public SculptLayerEntry( string name, SculptLayer deltas )
	{
		Name = string.IsNullOrWhiteSpace( name ) ? "Layer" : name;
		Deltas = deltas ?? throw new ArgumentNullException( nameof( deltas ) );
	}

	public string Name { get; set; }

	public SculptLayer Deltas { get; }

	/// <summary>Layer strength, -1 to +1. Negative inverts the displacement. Clamped on set.</summary>
	public float Intensity
	{
		get => _intensity;
		set => _intensity = Math.Clamp( value, -1f, 1f );
	}

	/// <summary>
	/// Per-vertex mask, or null for unmasked. Values are 0..1 and multiply the intensity
	/// per vertex. Copied on set, so the caller's array cannot move the layer afterwards.
	/// </summary>
	public float[] Mask
	{
		get => _mask;
		set
		{
			if ( value is null )
			{
				_mask = null;
				return;
			}

			if ( value.Length != Deltas.Count )
				throw new ArgumentException(
					$"A layer mask needs one value per vertex: {value.Length} for {Deltas.Count}." );

			_mask = new float[value.Length];

			for ( var i = 0; i < value.Length; i++ )
				_mask[i] = Math.Clamp( value[i], 0f, 1f );
		}
	}

	public SculptLayerBlend Blend { get; set; } = SculptLayerBlend.Add;

	public bool Enabled { get; set; } = true;

	public int Count => Deltas.Count;

	/// <summary>Whether any delta in this layer is big enough to move the surface.</summary>
	public bool HasDetail( float epsilon = 1e-5f )
	{
		foreach ( var d in Deltas.Deltas )
		{
			if ( d.Length > epsilon )
				return true;
		}

		return false;
	}

	/// <summary>An independent copy: deltas, mask, intensity, blend and flag — editing the copy never moves the original.</summary>
	public SculptLayerEntry Clone( string name = null )
	{
		var copy = new SculptLayerEntry( name ?? Name, new SculptLayer( (Vec3[])Deltas.Deltas.Clone() ) )
		{
			Intensity = Intensity,
			Blend = Blend,
			Enabled = Enabled,
		};

		if ( _mask is not null )
			copy.Mask = (float[])_mask.Clone();

		return copy;
	}
}

/// <summary>
/// A morph target checkpointed from the sculpt: a named set of frame-space deltas plus
/// the weight the author meant it to play at.
///
/// Stored frame-space for the same reason sculpt deltas are — a morph rides a cage
/// rebase instead of sliding off it. Materialised to world-space
/// <see cref="MeshMorph"/>s (what GLB, FBX and DMX export read) by
/// <see cref="SculptLayerStack.AddMorphsToMesh"/>, at full strength: the weight here
/// is the author's preview weight for <see cref="SculptLayerStack.EvaluateWithMorphs"/>,
/// not the exported value — playback drives the weight, the file carries the shape.
/// </summary>
public sealed class MorphTarget
{
	public MorphTarget( string name, SculptLayer deltas, float weight = 1f )
	{
		Name = string.IsNullOrWhiteSpace( name ) ? "Morph" : name;
		Deltas = deltas ?? throw new ArgumentNullException( nameof( deltas ) );
		Weight = weight;
	}

	public string Name { get; set; }

	public SculptLayer Deltas { get; }

	float _weight = 1f;

	/// <summary>Preview weight, 0..1. Clamped on set.</summary>
	public float Weight
	{
		get => _weight;
		set => _weight = Math.Clamp( value, 0f, 1f );
	}

	public int Count => Deltas.Count;

	public bool HasDetail( float epsilon = 1e-5f )
	{
		foreach ( var d in Deltas.Deltas )
		{
			if ( d.Length > epsilon )
				return true;
		}

		return false;
	}

	public MorphTarget Clone( string name = null ) =>
		new( name ?? Name, new SculptLayer( (Vec3[])Deltas.Deltas.Clone() ), Weight );
}

/// <summary>
/// Non-destructive sculpt layers on one multires level — the ZBrush 3D Layers half of
/// godlike-sculpt-layers-morph-targets-polygroups.
///
/// THE MODEL. A <see cref="MultiresSculpt"/> level holds ONE <see cref="SculptLayer"/>:
/// the baked deltas. This stack sits beside it and owns the un-baked part. <see cref="Base"/>
/// is a snapshot of the level layer taken when the stack attached; the entries are the
/// layers on top of it. The level layer is never written by the stack except through
/// <see cref="ApplyStroke"/> and <see cref="CaptureNewLayer"/>, which both restore it to
/// <see cref="Base"/> afterwards — so the evaluated mesh is always Base + entries, never
/// Base counted twice, and a stroke that bypassed the stack stays visible in the level
/// layer instead of being silently eaten.
///
/// COMPOSITION. <see cref="ComposeEntries"/> folds the enabled entries (intensity,
/// per-vertex mask, blend mode) into one frame-space layer; the feature applies that on
/// top of the evaluated mesh. Baking folds entries into <see cref="Base"/> instead:
/// <see cref="BakeCurrent"/> folds the active entry, <see cref="BakeAll"/> (and
/// <see cref="Flatten"/>) folds every entry, and the entries go away — the sculpt keeps
/// exactly the shape it had, now as baked deltas.
///
/// PERSISTENCE. The stack rides the sculpt blob (see <see cref="SculptBlob"/> format 2):
/// base, entries, masks and morph targets, 16-bit quantised like the level layers.
/// A blob written before layers existed reads back with a null stack, and a stack with
/// no entries writes nothing extra.
/// </summary>
public sealed class SculptLayerStack
{
	readonly List<SculptLayerEntry> _entries = new();
	readonly List<MorphTarget> _morphs = new();

	SculptLayer _base;

	public SculptLayerStack( int level, int vertexCount, SculptLayer baseLayer )
	{
		if ( level < 0 )
			throw new ArgumentOutOfRangeException( nameof( level ), "A layer stack lives on a level that exists." );

		if ( vertexCount < 0 )
			throw new ArgumentOutOfRangeException( nameof( vertexCount ), "A layer stack needs a vertex count." );

		if ( baseLayer is null )
			throw new ArgumentNullException( nameof( baseLayer ) );

		if ( baseLayer.Count != vertexCount )
			throw new ArgumentException(
				$"The base layer holds {baseLayer.Count} deltas for a level with {vertexCount} vertices." );

		Level = level;
		VertexCount = vertexCount;
		_base = baseLayer;
	}

	/// <summary>The multires level this stack lives on — the top level at attach time.</summary>
	public int Level { get; }

	/// <summary>Vertices at that level. Every entry, mask and morph target is this long.</summary>
	public int VertexCount { get; }

	/// <summary>
	/// The baked deltas: the level layer as it was when the stack attached, plus whatever
	/// has been baked into it since. The evaluated mesh already carries these; the entries
	/// are everything on top.
	/// </summary>
	public SculptLayer Base => _base;

	/// <summary>Bumped by every change that alters the composed shape, so a feature knows its cached geometry is stale.</summary>
	public int Revision { get; private set; }

	public IReadOnlyList<SculptLayerEntry> Entries => _entries;

	public IReadOnlyList<MorphTarget> Morphs => _morphs;

	/// <summary>Index of the active entry, or -1 when none is active. Strokes land in the active entry.</summary>
	public int ActiveIndex { get; private set; } = -1;

	public SculptLayerEntry Active => ActiveIndex >= 0 && ActiveIndex < _entries.Count ? _entries[ActiveIndex] : null;

	/// <summary>
	/// Attach a stack to a sculpt's level, snapshotting that level's layer as the base.
	/// Strokes made before the attach stay in the base; strokes after it go to entries.
	/// </summary>
	public static SculptLayerStack Attach( MultiresSculpt sculpt, int level )
	{
		if ( sculpt is null )
			throw new ArgumentNullException( nameof( sculpt ) );

		if ( level < 0 || level > sculpt.TopLevel )
			throw new ArgumentOutOfRangeException( nameof( level ),
				$"Level {level} does not exist; this sculpt has levels 0 to {sculpt.TopLevel}." );

		var layer = sculpt.LayerAt( level );
		return new SculptLayerStack( level, layer.Count, new SculptLayer( (Vec3[])layer.Deltas.Clone() ) );
	}

	/// <summary>Add an empty layer (or one carrying <paramref name="deltas"/>) and make it active. Returns the entry.</summary>
	public SculptLayerEntry Add( string name, SculptLayer deltas = null )
	{
		var layer = deltas ?? new SculptLayer( new Vec3[VertexCount] );

		if ( layer.Count != VertexCount )
			throw new ArgumentException(
				$"A layer needs {VertexCount} deltas for this stack; got {layer.Count}." );

		var entry = new SculptLayerEntry( name, layer );
		_entries.Add( entry );
		ActiveIndex = _entries.Count - 1;
		Revision++;
		return entry;
	}

	/// <summary>Copy an entry under a new name. The copy is independent — editing it never moves the original.</summary>
	public SculptLayerEntry Duplicate( int index, string name = null )
	{
		var entry = EntryAt( index );
		var copy = entry.Clone( name ?? entry.Name + " copy" );
		_entries.Insert( index + 1, copy );
		ActiveIndex = index + 1;
		Revision++;
		return copy;
	}

	/// <summary>
	/// Branch an entry: duplicate it and make the copy active, so a variant can be tried
	/// without touching the original. The original keeps its place and its shape.
	/// </summary>
	public SculptLayerEntry Branch( int index, string name = null ) =>
		Duplicate( index, name ?? EntryAt( index ).Name + " branch" );

	/// <summary>Delete an entry. The composed shape loses exactly that layer's contribution — nothing else moves.</summary>
	public void Delete( int index )
	{
		EntryAt( index );
		_entries.RemoveAt( index );

		if ( ActiveIndex == index )
			ActiveIndex = _entries.Count == 0 ? -1 : Math.Min( index, _entries.Count - 1 );
		else if ( ActiveIndex > index )
			ActiveIndex--;

		Revision++;
	}

	/// <summary>Move an entry to a new position in the stack order. Blend order follows the list.</summary>
	public void Move( int from, int to )
	{
		var entry = EntryAt( from );

		if ( to < 0 || to >= _entries.Count )
			throw new ArgumentOutOfRangeException( nameof( to ), $"There is no position {to} in a stack of {_entries.Count}." );

		if ( from == to )
			return;

		_entries.RemoveAt( from );
		_entries.Insert( to, entry );

		if ( ActiveIndex == from )
			ActiveIndex = to;
		else if ( from < ActiveIndex && to >= ActiveIndex )
			ActiveIndex--;
		else if ( from > ActiveIndex && to <= ActiveIndex )
			ActiveIndex++;

		Revision++;
	}

	public void SetActive( int index )
	{
		if ( index < -1 || index >= _entries.Count )
			throw new ArgumentOutOfRangeException( nameof( index ), $"There is no entry {index} in a stack of {_entries.Count}." );

		ActiveIndex = index;
		Revision++;
	}

	public void SetIntensity( int index, float intensity )
	{
		EntryAt( index ).Intensity = intensity;
		Revision++;
	}

	public void SetBlend( int index, SculptLayerBlend blend )
	{
		EntryAt( index ).Blend = blend;
		Revision++;
	}

	public void SetEnabled( int index, bool enabled )
	{
		EntryAt( index ).Enabled = enabled;
		Revision++;
	}

	public void SetMask( int index, float[] mask )
	{
		EntryAt( index ).Mask = mask;
		Revision++;
	}

	/// <summary>Whether any enabled entry holds displacement that has not been baked yet.</summary>
	public bool HasUnbakedDetail
	{
		get
		{
			foreach ( var entry in _entries )
			{
				if ( entry.Enabled && entry.HasDetail() )
					return true;
			}

			return false;
		}
	}

	/// <summary>
	/// The mandatory bake warning, or null when there is nothing to lose. A topology
	/// change — a cage rebuild that no longer matches, a reproject, a level dropped
	/// underneath — cannot carry layer deltas: they are per-vertex against this level's
	/// rest surface. The warning names the bake that saves them; the caller decides
	/// whether the change was deliberate.
	/// </summary>
	public string TopologyChangeWarning()
	{
		if ( !HasUnbakedDetail )
			return null;

		var count = 0;

		foreach ( var entry in _entries )
		{
			if ( entry.Enabled && entry.HasDetail() )
				count++;
		}

		return $"Sculpt layers hold un-baked displacement in {count} layer{( count == 1 ? "" : "s" )}. "
			+ "Bake them (bake-all or flatten) before any topology-changing operation: a reproject or a "
			+ "cage rebuild will not carry layer deltas, and undoing the upstream edit will not bring them back.";
	}

	/// <summary>
	/// Whether the level layer has moved since the stack attached (or since the last
	/// bake) — a stroke that bypassed the stack. <see cref="ApplyStroke"/> refuses
	/// while this is true rather than wiping that stroke; <see cref="CaptureNewLayer"/>
	/// is the way to take it into the stack.
	/// </summary>
	public bool HasUncapturedChanges( MultiresSculpt sculpt )
	{
		if ( sculpt is null )
			throw new ArgumentNullException( nameof( sculpt ) );

		var layer = sculpt.LayerAt( Level );

		for ( var i = 0; i < VertexCount; i++ )
		{
			if ( ( layer.Deltas[i] - _base.Deltas[i] ).LengthSquared > 1e-20f )
				return true;
		}

		return false;
	}

	/// <summary>
	/// The entries folded into one frame-space layer, in stack order — the displacement
	/// the feature adds on top of the evaluated mesh. The base is NOT in here: the
	/// evaluated mesh already carries it. Computed as the effective deltas minus the
	/// base, so it is exactly what baking would add — one fold function serves
	/// composition and baking alike, which is what makes bake-all equal sequential bake.
	/// </summary>
	public SculptLayer ComposeEntries()
	{
		var effective = ComposeAll();
		var acc = new Vec3[VertexCount];

		for ( var v = 0; v < VertexCount; v++ )
			acc[v] = effective.Deltas[v] - _base.Deltas[v];

		return new SculptLayer( acc );
	}

	/// <summary>Base plus entries, folded in stack order: the whole displacement of the stack at this level, frame-space.</summary>
	public SculptLayer EffectiveDeltas() => ComposeAll();

	/// <summary>Fold every entry onto a copy of the base, in stack order. Always a fresh layer — the base is never mutated.</summary>
	SculptLayer ComposeAll()
	{
		var acc = new SculptLayer( (Vec3[])_base.Deltas.Clone() );

		foreach ( var entry in _entries )
			acc = Fold( acc, entry );

		return acc;
	}

	/// <summary>
	/// One entry folded into an accumulation, in stack order. The single primitive
	/// behind composition and baking: <see cref="ComposeAll"/>, <see cref="BakeCurrent"/>
	/// and <see cref="BakeAll"/> all fold through here, so baking all at once and baking
	/// one by one, in the same order, give bit-identical deltas for every blend mode.
	/// </summary>
	static SculptLayer Fold( SculptLayer acc, SculptLayerEntry entry )
	{
		if ( !entry.Enabled )
			return acc;

		var deltas = entry.Deltas.Deltas;
		var mask = entry.Mask;
		var result = (Vec3[])acc.Deltas.Clone();

		for ( var v = 0; v < result.Length; v++ )
		{
			var scale = entry.Intensity * ( mask is null ? 1f : mask[v] );

			if ( scale == 0f )
				continue;

			var d = deltas[v];

			switch ( entry.Blend )
			{
				case SculptLayerBlend.Add:
					result[v] += d * scale;
					break;
				case SculptLayerBlend.Subtract:
					result[v] -= d * scale;
					break;
				case SculptLayerBlend.Multiply:
					result[v] = new Vec3(
						result[v].x * ( 1f + d.x * scale ),
						result[v].y * ( 1f + d.y * scale ),
						result[v].z * ( 1f + d.z * scale ) );
					break;
				case SculptLayerBlend.Replace:
					var t = Math.Clamp( scale, 0f, 1f );
					result[v] = result[v] + ( d - result[v] ) * t;
					break;
			}
		}

		return new SculptLayer( result );
	}

	/// <summary>
	/// Add the entries' displacement to <paramref name="mesh"/>, which must be the level
	/// evaluated (so it already carries the base). <paramref name="frames"/> are that
	/// level's frames — <c>sculpt.FramesFor(Level)</c>.
	/// </summary>
	public void ApplyEntries( PolyMesh mesh, SculptFrames frames )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( frames is null )
			throw new ArgumentNullException( nameof( frames ) );

		if ( mesh.VertexCount != VertexCount || frames.Count != VertexCount )
			throw new ArgumentException(
				$"Layers need {VertexCount} vertices; the mesh has {mesh.VertexCount} and the frames {frames.Count}." );

		var entries = ComposeEntries();

		for ( var v = 0; v < VertexCount; v++ )
			mesh.Positions[v] += frames.At[v].ToWorld( entries.Deltas[v] );
	}

	/// <summary>
	/// Run a brush stroke at the stack's level and land it in the active entry — the
	/// layer-aware stroke. The level layer is restored to the base afterwards, so the
	/// stroke lives exactly once, in the entry. Returns how many vertices the stroke
	/// touched.
	///
	/// Refuses when there is no active entry, and when the level layer has moved outside
	/// the stack (a stroke that bypassed it): restoring the base then would wipe that
	/// stroke, so <see cref="CaptureNewLayer"/> has to take it into the stack first.
	/// The interactive session does not call this yet — it records into the level layer
	/// and the stack takes it over through <see cref="CaptureNewLayer"/> — so this is the
	/// script and test path, and the session hook is a named follow-up.
	/// </summary>
	public int ApplyStroke( MultiresSculpt sculpt, BrushStroke stroke, float[] mask = null )
	{
		if ( sculpt is null )
			throw new ArgumentNullException( nameof( sculpt ) );

		if ( stroke is null )
			throw new ArgumentNullException( nameof( stroke ) );

		if ( Active is null )
			throw new InvalidOperationException( "A layer-aware stroke needs an active layer; add one first." );

		if ( HasUncapturedChanges( sculpt ) )
			throw new InvalidOperationException(
				"The level has strokes outside the layer stack. Capture them first (CaptureNewLayer) — "
				+ "restoring the base now would wipe them." );

		var before = (Vec3[])sculpt.LayerAt( Level ).Deltas.Clone();
		var undo = sculpt.Stroke( Level, stroke, mask );
		var after = sculpt.LayerAt( Level ).Deltas;
		var entry = Active;

		foreach ( var vertex in undo.Previous.Keys )
		{
			if ( (uint)vertex >= (uint)VertexCount )
				continue;

			entry.Deltas.Deltas[vertex] += after[vertex] - before[vertex];
		}

		// A copy, never the base object itself: the level layer's deltas are mutated in
		// place by ApplyEdit, and sharing the array would let a later stroke rewrite the base.
		sculpt.SetLayer( Level, new SculptLayer( (Vec3[])_base.Deltas.Clone() ) );
		Revision++;
		return undo.Count;
	}

	/// <summary>
	/// Take strokes made directly on the level layer — the session's strokes, which do
	/// not know about layers — into a new entry, and restore the level layer to the
	/// base. The composed shape does not move: the same deltas now live in the entry
	/// instead of the level. Returns the new entry, made active.
	/// </summary>
	public SculptLayerEntry CaptureNewLayer( MultiresSculpt sculpt, string name )
	{
		if ( sculpt is null )
			throw new ArgumentNullException( nameof( sculpt ) );

		var layer = sculpt.LayerAt( Level );
		var deltas = new Vec3[VertexCount];

		for ( var v = 0; v < VertexCount; v++ )
			deltas[v] = layer.Deltas[v] - _base.Deltas[v];

		// A copy, never the base object itself — see ApplyStroke.
		sculpt.SetLayer( Level, new SculptLayer( (Vec3[])_base.Deltas.Clone() ) );
		var entry = Add( name, new SculptLayer( deltas ) );
		return entry;
	}

	/// <summary>
	/// Checkpoint the current composed displacement as a named morph target — the
	/// morph-target checkpoint of the ticket. Replaces a target of the same name, so a
	/// re-checkpoint never leaves two keys fighting over one name.
	/// </summary>
	public MorphTarget CaptureMorph( MultiresSculpt sculpt, string name )
	{
		if ( sculpt is null )
			throw new ArgumentNullException( nameof( sculpt ) );

		if ( HasUncapturedChanges( sculpt ) )
			throw new InvalidOperationException(
				"The level has strokes outside the layer stack. Capture them first (CaptureNewLayer) — "
				+ "a morph checkpoint taken now would silently leave them out." );

		_morphs.RemoveAll( m => m.Name == name );
		var target = new MorphTarget( name, EffectiveDeltas() );
		_morphs.Add( target );
		Revision++;
		return target;
	}

	/// <summary>Drop a morph target by name. Returns false when there was none.</summary>
	public bool DeleteMorph( string name )
	{
		var removed = _morphs.RemoveAll( m => m.Name == name ) > 0;

		if ( removed )
			Revision++;

		return removed;
	}

	public void SetMorphWeight( string name, float weight )
	{
		foreach ( var morph in _morphs )
		{
			if ( morph.Name == name )
			{
				morph.Weight = weight;
				Revision++;
				return;
			}
		}

		throw new ArgumentException( $"There is no morph target named '{name}' in this stack." );
	}

	/// <summary>
	/// The composed displacement plus every morph target at its preview weight — what a
	/// pose preview shows. Export uses the targets at full strength instead; see
	/// <see cref="AddMorphsToMesh"/>.
	/// </summary>
	public SculptLayer EvaluateWithMorphs()
	{
		var acc = EffectiveDeltas().Deltas;

		foreach ( var morph in _morphs )
		{
			if ( morph.Weight == 0f )
				continue;

			for ( var v = 0; v < VertexCount; v++ )
				acc[v] += morph.Deltas.Deltas[v] * morph.Weight;
		}

		return new SculptLayer( acc );
	}

	/// <summary>
	/// Materialise every morph target as a world-space <see cref="MeshMorph"/> on
	/// <paramref name="mesh"/> — the shape the GLB, FBX and DMX writers read
	/// (<c>mesh.ValidMorphs</c>). Targets replace same-named keys, so re-materialising
	/// after a re-checkpoint never duplicates one. The mesh must be the level evaluated.
	/// </summary>
	public void AddMorphsToMesh( PolyMesh mesh, SculptFrames frames )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		if ( frames is null )
			throw new ArgumentNullException( nameof( frames ) );

		if ( mesh.VertexCount != VertexCount || frames.Count != VertexCount )
			throw new ArgumentException(
				$"Morphs need {VertexCount} vertices; the mesh has {mesh.VertexCount} and the frames {frames.Count}." );

		foreach ( var target in _morphs )
		{
			var world = new Vec3[VertexCount];

			for ( var v = 0; v < VertexCount; v++ )
				world[v] = frames.At[v].ToWorld( target.Deltas.Deltas[v] );

			mesh.Morphs.RemoveAll( m => m?.Name == target.Name );
			mesh.Morphs.Add( new MeshMorph { Name = target.Name, Deltas = world } );
		}
	}

	/// <summary>Fold the active entry into the base and remove it. The composed shape does not move.</summary>
	public void BakeCurrent()
	{
		if ( Active is null )
			throw new InvalidOperationException( "There is no active layer to bake." );

		var entry = Active;
		_base = Fold( _base, entry );
		_entries.Remove( entry );
		ActiveIndex = _entries.Count == 0 ? -1 : Math.Min( ActiveIndex, _entries.Count - 1 );
		Revision++;
	}

	/// <summary>
	/// Fold every entry into the base, in stack order, and remove them all. Bit-identical
	/// to baking one by one in the same order — same fold, same order.
	/// </summary>
	public void BakeAll()
	{
		_base = ComposeAll();
		_entries.Clear();
		ActiveIndex = -1;
		Revision++;
	}

	/// <summary>Flatten is bake-all: every layer's displacement becomes baked deltas and the stack empties.</summary>
	public void Flatten() => BakeAll();

	SculptLayerEntry EntryAt( int index )
	{
		if ( index < 0 || index >= _entries.Count )
			throw new ArgumentOutOfRangeException( nameof( index ), $"There is no entry {index} in a stack of {_entries.Count}." );

		return _entries[index];
	}

	/// <summary>
	/// Bytes this stack (base, entries, masks, morph targets) costs in the blob — the
	/// delta-storage half of the ticket's size check. Quantised 16-bit per component,
	/// like the level layers.
	/// </summary>
	public int StoredBytes()
	{
		using var stream = new MemoryStream();
		// ASCII, like the blob's own writer: the byte count here must match the bytes the
		// blob actually writes for this stack.
		using var w = new BinaryWriter( stream, Encoding.ASCII, leaveOpen: true );
		WriteTo( w );
		w.Flush();
		return (int)stream.Length;
	}

	/// <summary>
	/// Bytes a full copy of <paramref name="mesh"/> costs: positions (12 a vertex) plus
	/// faces (a count plus one int per corner) plus corner UVs (8 a corner). Normals are
	/// derived and not counted — a copy that cannot reproduce the surface is not a copy.
	/// </summary>
	public static int FullMeshCopyBytes( PolyMesh mesh )
	{
		if ( mesh is null )
			throw new ArgumentNullException( nameof( mesh ) );

		var bytes = mesh.Positions.Count * 12;

		foreach ( var face in mesh.Faces )
			bytes += 4 + face.Indices.Length * 4 + face.Indices.Length * 8;

		return bytes;
	}

	/// <summary>Write the stack into a blob section. Read back with <see cref="ReadFrom"/>.</summary>
	internal void WriteTo( BinaryWriter w )
	{
		w.Write( Level );
		w.Write( VertexCount );
		SculptBlob.WriteLayer( w, _base );

		w.Write( _entries.Count );

		foreach ( var entry in _entries )
		{
			w.Write( entry.Name );
			w.Write( entry.Intensity );
			w.Write( (int)entry.Blend );
			w.Write( entry.Enabled );
			w.Write( entry.Mask is not null );

			if ( entry.Mask is not null )
			{
				foreach ( var value in entry.Mask )
					w.Write( value );
			}

			SculptBlob.WriteLayer( w, entry.Deltas );
		}

		w.Write( _morphs.Count );

		foreach ( var morph in _morphs )
		{
			w.Write( morph.Name );
			w.Write( morph.Weight );
			SculptBlob.WriteLayer( w, morph.Deltas );
		}
	}

	/// <summary>
	/// Read a stack written by <see cref="WriteTo"/>. The level and vertex count come
	/// from the stream; the caller checks them against the sculpt it belongs to.
	/// </summary>
	internal static SculptLayerStack ReadFrom( BinaryReader r )
	{
		var level = r.ReadInt32();
		var vertexCount = r.ReadInt32();

		if ( level < 0 || vertexCount < 0 || vertexCount > 1 << 26 )
			throw new InvalidDataException( $"A layer stack on level {level} with {vertexCount} vertices is not a layer stack." );

		var baseLayer = SculptBlob.ReadLayer( r, level, vertexCount );
		var stack = new SculptLayerStack( level, vertexCount, baseLayer );

		var entries = r.ReadInt32();

		if ( entries < 0 || entries > 1024 )
			throw new InvalidDataException( $"A layer stack with {entries} entries is not a layer stack." );

		for ( var i = 0; i < entries; i++ )
		{
			// Same order as WriteTo: name, intensity, blend, enabled, mask flag, mask, deltas.
			var name = r.ReadString();
			var intensity = r.ReadSingle();
			var blend = (SculptLayerBlend)r.ReadInt32();
			var enabled = r.ReadBoolean();
			var hasMask = r.ReadBoolean();
			float[] mask = null;

			if ( hasMask )
			{
				mask = new float[vertexCount];

				for ( var v = 0; v < vertexCount; v++ )
					mask[v] = r.ReadSingle();
			}

			var entry = new SculptLayerEntry( name, SculptBlob.ReadLayer( r, level, vertexCount ) )
			{
				Intensity = intensity,
				Blend = blend,
				Enabled = enabled,
			};

			if ( mask is not null )
				entry.Mask = mask;

			stack._entries.Add( entry );
		}

		if ( stack._entries.Count > 0 )
			stack.ActiveIndex = stack._entries.Count - 1;

		var morphs = r.ReadInt32();

		if ( morphs < 0 || morphs > 1024 )
			throw new InvalidDataException( $"A layer stack with {morphs} morph targets is not a layer stack." );

		for ( var i = 0; i < morphs; i++ )
		{
			var name = r.ReadString();
			var weight = r.ReadSingle();
			stack._morphs.Add( new MorphTarget( name, SculptBlob.ReadLayer( r, level, vertexCount ), weight ) );
		}

		return stack;
	}
}
