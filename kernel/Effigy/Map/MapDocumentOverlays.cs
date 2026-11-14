using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Effigy;

namespace Effigy.Map;

/// <summary>
/// Hammer Desktop P2: overlays, decals + path_track chains (hammer-overlays-decals-paths).
///
/// The MapDocument adapter for <see cref="OverlayDecalSet"/>.
///
/// MapDocument landed 2026-11-13 (after OverlaysDecals.cs was written, when no
/// MapDocument type existed yet). It holds brushes as <see cref="MapBrushData"/>,
/// entities as <see cref="MapEntityData"/> and history as <see cref="MapHistoryEntry"/>
/// — and it has NO slot for overlays, decals or path chains. This file is the
/// integration seam, and it deliberately does NOT edit MapDocument to add one:
/// MapDocument.cs / MapDocumentIo.cs are shared files owned by the
/// map-document-model ticket, and a new history-entry kind + IO section for the
/// overlay set is the final integrator's job (see PERSISTENCE below).
///
/// What this adapter does, against the REAL MapDocument API only:
/// <list type="bullet">
/// <item>Resolves a document's brushes to <see cref="MapBrush"/>es
/// (<see cref="BrushesOf"/>) so <see cref="OverlayDecalSet.ValidateAll"/>,
/// <see cref="OverlayDecalSet.ApplyClip"/> and
/// <see cref="OverlayDecalSet.ApplyVertexEdit"/> run against a real document
/// instead of caller-supplied brushes.</item>
/// <item>Validates the set against the document (<see cref="Validate"/>) and
/// supplies a <see cref="MapDocument.ValidationHook"/> for it
/// (<see cref="ValidationHook"/>) — the hook validates whatever document it is
/// called with, never a captured one.</item>
/// <item>Exports path_track chains as real <see cref="MapEntityData"/>s
/// (<see cref="PathEntities"/>, <see cref="AddPathsToDocument"/>) so chains
/// persist in the .gmap history and export to VMF through VmfIo, which writes
/// document entities. Overlays and decals have NO entity form here: a VMF
/// info_overlay names faces by id, and MapDocument faces carry no persistent
/// per-face id — inventing one would be a stub. They persist as the set JSON
/// (see PERSISTENCE).</item>
/// </list>
///
/// PERSISTENCE. The overlay/decal/path set is sidecar JSON, not a MapDocumentIo
/// section: <see cref="ToJson"/> stores {"documentId", "overlays": set-json} and
/// <see cref="FromJson"/> refuses to bind a set to a document with a different
/// id. Path chains additionally persist inside the document as entities (see
/// above). A future MapDocumentIo "overlays" section — history-entry kind +
/// section read/write + face identity — folds the set into the .gmap itself;
/// until then the sidecar is the persistence, and this file names the gap instead
/// of pretending the .gmap already carries it.
///
/// REGISTRY. <see cref="MapDocument.Validate(EntityRegistry)"/> reports an
/// unknown entity class when a registry IS supplied, and the registry
/// (hammer-entity-registry-fgd-json) does not define path_track / info_overlay /
/// infodecal yet — that is that ticket's follow-up, not this one. The document
/// itself never rejects an unknown class, and VmfIo writes entities without a
/// registry, so path_track entities export today; a registry-validated save
/// will flag them until the registry gains the definitions. The numbers in the
/// entities are copied verbatim from the chain; the only key NOT copied is
/// orientationtype (Hammer default 0 = no change).
///
/// SPEED 0. This model reads a node's speed 0 as "pause at this node"
/// (<see cref="PathTrackNode"/>). Hammer's own FGD reads path_track speed 0 as
/// "keep current speed". The adapter copies the number unchanged and takes no
/// side in that divergence — the chain owns the meaning, the entity carries the
/// number. The gate / registry ticket decides which reading the bake uses.
///
/// UI: none. Backend / data-model only, as the ticket requires.
/// </summary>
public sealed class MapDocumentOverlays
{
	/// <summary>The bound document. Never null.</summary>
	public MapDocument Document { get; }
	/// <summary>The bound overlay/decal/path set. Never null.</summary>
	public OverlayDecalSet Set { get; }

	public MapDocumentOverlays( MapDocument document, OverlayDecalSet set = null )
	{
		Document = document ?? throw new ArgumentNullException( nameof( document ) );
		Set = set ?? new OverlayDecalSet();
	}

	/// <summary>
	/// The document's brushes as plane-form <see cref="MapBrush"/>es, keyed by
	/// brush id (<see cref="MapBrushData.ToMapBrush"/>). Throws for a null document.
	/// A brush id the document holds twice cannot happen — AddBrush enforces it.
	/// </summary>
	public static IReadOnlyDictionary<string, MapBrush> BrushesOf( MapDocument document )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		var brushes = new Dictionary<string, MapBrush>( StringComparer.Ordinal );
		foreach ( var brush in document.Brushes )
			brushes[brush.Id] = brush.ToMapBrush();
		return brushes;
	}

	/// <summary>The bound document's brushes, as <see cref="BrushesOf"/> builds them.</summary>
	public IReadOnlyDictionary<string, MapBrush> Brushes() => BrushesOf( Document );

	/// <summary>
	/// Validate the bound set against the bound document's brushes: every overlay's
	/// own defects plus whether every target face still exists, every decal's
	/// geometry, every chain's connectedness. Empty means the set fits the document.
	/// </summary>
	public IReadOnlyList<OverlayDecalIssue> Validate() => Set.ValidateAll( Brushes() );

	/// <summary>
	/// A <see cref="MapDocument.ValidationHook"/> for a set: validates whatever
	/// document the hook is called with, returning the issues as strings. Install
	/// as <c>doc.ValidationHook = MapDocumentOverlays.ValidationHook( set );</c>.
	/// The hook validates the PASSED document, never a captured one — a hook
	/// installed on one document must not silently validate another.
	/// </summary>
	public static Func<MapDocument, IEnumerable<string>> ValidationHook( OverlayDecalSet set )
	{
		if ( set is null ) throw new ArgumentNullException( nameof( set ) );
		return doc => set.ValidateAll( BrushesOf( doc ) ).Select( i => i.ToString() );
	}

	/// <summary>Apply a real <see cref="MapClip"/> result to the bound set (see <see cref="OverlayDecalSet.ApplyClip"/>).</summary>
	public OverlayClipReport ApplyClip( string brushId, MapClipResult result ) => Set.ApplyClip( brushId, result );

	/// <summary>Apply a real <see cref="MapVertexEdit"/> to the bound set (see <see cref="OverlayDecalSet.ApplyVertexEdit"/>).</summary>
	public OverlayVertexEditReport ApplyVertexEdit( string brushId, MapVertexBrush before, MapVertexBrush after,
		IReadOnlyList<int> affectedFaces = null ) => Set.ApplyVertexEdit( brushId, before, after, affectedFaces );

	/// <summary>
	/// Every node of every chain in the set as a real <see cref="MapEntityData"/>:
	/// ClassName "path_track", Origin the node position, keyvalues targetname
	/// (the node name), target (the Next link, omitted at the end of the chain)
	/// and speed (invariant "R" float). Entity ids are fresh
	/// <see cref="MapDocument.NewId"/>s — the caller owns id uniqueness against
	/// the document when adding them (see <see cref="AddPathsToDocument"/>).
	/// The entities are NOT added to the document by this method: it only builds
	/// them, so the caller can inspect them before committing.
	/// </summary>
	public IReadOnlyList<MapEntityData> PathEntities()
	{
		var entities = new List<MapEntityData>();
		foreach ( var chain in Set.Paths )
			foreach ( var node in chain.Nodes )
			{
				var entity = new MapEntityData
				{
					Id = MapDocument.NewId(),
					ClassName = "path_track",
					Origin = node.Position,
				};
				entity.KeyValues["targetname"] = node.Name;
				if ( !string.IsNullOrEmpty( node.Next ) )
					entity.KeyValues["target"] = node.Next;
				entity.KeyValues["speed"] = node.Speed.ToString( "R", CultureInfo.InvariantCulture );
				entities.Add( entity );
			}
		return entities;
	}

	/// <summary>
	/// Add the set's path_track entities to the bound document (one history entry
	/// per node, so they persist in the .gmap and export to VMF) and return the
	/// added entity ids in chain order. Throws
	/// <see cref="InvalidOperationException"/> — adding nothing — when a node
	/// targetname already exists as an entity in the document: two entities
	/// sharing a targetname would make the Next link ambiguous, and a silent
	/// rename would break the chain. Remove or rename the existing entity first.
	/// </summary>
	public IReadOnlyList<string> AddPathsToDocument()
	{
		var entities = PathEntities();
		foreach ( var entity in entities )
			if ( Document.FindEntityByTargetName( entity.TargetName ) is not null )
				throw new InvalidOperationException(
					$"Cannot add path_track '{entity.TargetName}' to the document: an entity with that targetname already exists; two entities sharing a targetname would make the chain link ambiguous." );
		var added = new List<string>();
		foreach ( var entity in entities )
		{
			Document.AddEntity( entity );
			added.Add( entity.Id );
		}
		return added;
	}

	/// <summary>JSON form: {"documentId","overlays":{...set JSON...}}.</summary>
	public JsonObject ToJson() => new()
	{
		["documentId"] = Document.DocumentId,
		["overlays"] = Set.ToJson(),
	};

	/// <summary>
	/// Parse <see cref="ToJson"/> output and bind it to <paramref name="document"/>.
	/// A missing "documentId" or one that does not match the document throws,
	/// naming the problem — a set belongs to exactly one document. A missing
	/// "overlays" section reads as an empty set.
	/// </summary>
	public static MapDocumentOverlays FromJson( MapDocument document, JsonObject json )
	{
		if ( document is null ) throw new ArgumentNullException( nameof( document ) );
		if ( json is null ) throw new ArgumentNullException( nameof( json ) );
		var documentId = json["documentId"]?.GetValue<string>();
		if ( string.IsNullOrWhiteSpace( documentId ) )
			throw new ArgumentException( "MapDocumentOverlays JSON is missing 'documentId'." );
		if ( !string.Equals( documentId, document.DocumentId, StringComparison.Ordinal ) )
			throw new ArgumentException(
				$"MapDocumentOverlays JSON belongs to document '{documentId}', not to document '{document.DocumentId}'; a set belongs to exactly one document." );
		var set = json["overlays"] is JsonObject setJson
			? OverlayDecalSet.FromJson( setJson )
			: new OverlayDecalSet();
		return new MapDocumentOverlays( document, set );
	}

	/// <summary>Parse <see cref="ToJson"/> output from text. Blank text or a non-object throws, naming the problem.</summary>
	public static MapDocumentOverlays FromJsonString( MapDocument document, string json )
	{
		if ( string.IsNullOrWhiteSpace( json ) )
			throw new ArgumentException( "MapDocumentOverlays JSON text is blank." );
		if ( JsonNode.Parse( json ) is not JsonObject obj )
			throw new ArgumentException( "MapDocumentOverlays JSON text is not an object." );
		return FromJson( document, obj );
	}

	public override string ToString() => $"overlays for '{Document.Name}': {Set}";
}
