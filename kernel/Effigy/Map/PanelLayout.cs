using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Effigy;

/// <summary>
/// Docked panel layout data model (hammer-panels-docked-layout-model).
///
/// DATA MODEL ONLY — no UI, no panels built, no window code (UI deferred per
/// Pooh's 2026-11-13 no-Desktop-UI rule). This is the model a future UI layer
/// binds to. The architecture is docked, never modal, from day one (Reddit
/// round #19, grade A, r/hammer sjux8u: TrenchBroom is praised because "there
/// are no dialogs for entities and textures — stuff is docked comfortably in
/// the main window").
///
/// Three parts:
///   <see cref="PanelRegistry"/> — which panels exist (id, default dock zone,
///     default size, default order within the zone).
///   <see cref="LayoutDocument"/> — which panels are open, where docked, what
///     size, persisted per map / workspace as versioned JSON.
///   <see cref="NoModalContract"/> — every map operation that would open a
///     dialog instead declares a docked panel target; modal-only operations
///     are rejected with a named reason.
///
/// MAPDOCUMENT BOUNDARY: kernel/Effigy/Map/MapDocument.cs has NOT landed yet
/// (2026-11-13 ~16:00 EST: hammer-map-document-model is in-progress with
/// muse-3-desktop-hammer-12). This file touches no MapDocument type and binds
/// to a map by string id only (<see cref="LayoutDocument.MapId"/>), so it
/// compiles with or without it. The filed MapDocument spec (versioned schema,
/// Create/Open/Save, feature-ordered history with rollback, validation hook
/// on save) names no document-id member, so none is assumed here; reconcile
/// at integration if the landed document exposes one.
/// </summary>

/// <summary>Where a panel docks. There is no modal zone: a panel is always docked somewhere.</summary>
public enum DockZone
{
    Left,
    Right,
    Bottom,
    Top,
    /// <summary>
    /// A floating window that is still part of the docked layout: its
    /// rectangle is tracked in the layout document, it is never a modal
    /// dialog (it never blocks the editor).
    /// </summary>
    FloatingDocked
}

/// <summary>Named failure codes. Every rejection names its code and the panel / operation involved.</summary>
public enum PanelLayoutErrorCode
{
    DuplicatePanelId,
    UnknownPanelId,
    InvalidPanelSize,
    InvalidPanelOrder,
    InvalidFloatingRect,
    InvalidLayoutJson,
    UnknownSchemaVersion,
    MissingPanelTarget,
    ModalDialogNotAllowed,
    UnknownPanelTarget
}

/// <summary>Named layout error: <see cref="Code"/> says what failed, the message names the panel / operation.</summary>
public sealed class PanelLayoutError : Exception
{
    public PanelLayoutErrorCode Code { get; }

    public PanelLayoutError( PanelLayoutErrorCode code, string message ) : base( message )
    {
        Code = code;
    }
}

/// <summary>A panel that can be docked: its stable id, display title, default zone / size / order.</summary>
public sealed class PanelDefinition
{
    public string PanelId { get; }
    public string Title { get; }
    public DockZone DefaultZone { get; }
    public double DefaultSize { get; }
    public int DefaultOrder { get; }

    public PanelDefinition( string panelId, string title, DockZone defaultZone, double defaultSize, int defaultOrder = 0 )
    {
        if ( string.IsNullOrWhiteSpace( panelId ) ) throw new PanelLayoutError( PanelLayoutErrorCode.UnknownPanelId, "panel id must not be empty." );
        if ( string.IsNullOrWhiteSpace( title ) ) throw new ArgumentException( "panel title must not be empty.", nameof( title ) );
        if ( !Enum.IsDefined( defaultZone ) ) throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid dock zone '{defaultZone}' for panel '{panelId}'." );
        PanelState.ValidateSize( defaultSize, panelId );
        if ( defaultOrder < 0 ) throw new PanelLayoutError( PanelLayoutErrorCode.InvalidPanelOrder, $"invalid-panel-order: panel '{panelId}' order {defaultOrder} is negative." );
        PanelId = panelId;
        Title = title;
        DefaultZone = defaultZone;
        DefaultSize = defaultSize;
        DefaultOrder = defaultOrder;
    }
}

/// <summary>
/// One panel's place in a layout: dock zone, size (width for left/right,
/// height for top/bottom, width for floating-docked), visibility, order
/// within its zone, and — for <see cref="DockZone.FloatingDocked"/> — the
/// tracked floating rectangle. Hiding a panel never clears its zone, size
/// or order: reopen restores exactly where it was.
/// </summary>
public sealed class PanelState
{
    public string PanelId { get; set; }
    public DockZone Zone { get; set; }
    public double Size { get; set; }
    public bool Visible { get; set; } = true;
    public int Order { get; set; }
    public double FloatX { get; set; }
    public double FloatY { get; set; }
    public double FloatWidth { get; set; } = 480;
    public double FloatHeight { get; set; } = 360;

    public PanelState( string panelId, DockZone zone, double size, int order )
    {
        if ( string.IsNullOrWhiteSpace( panelId ) ) throw new PanelLayoutError( PanelLayoutErrorCode.UnknownPanelId, "panel id must not be empty." );
        if ( !Enum.IsDefined( zone ) ) throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid dock zone '{zone}' for panel '{panelId}'." );
        ValidateSize( size, panelId );
        if ( order < 0 ) throw new PanelLayoutError( PanelLayoutErrorCode.InvalidPanelOrder, $"invalid-panel-order: panel '{panelId}' order {order} is negative." );
        PanelId = panelId;
        Zone = zone;
        Size = size;
        Order = order;
    }

    internal static void ValidateSize( double size, string panelId )
    {
        if ( double.IsNaN( size ) || double.IsInfinity( size ) || size <= 0 )
            throw new PanelLayoutError( PanelLayoutErrorCode.InvalidPanelSize, $"invalid-panel-size: panel '{panelId}' size {size.ToString( CultureInfo.InvariantCulture )} must be a finite number greater than zero." );
    }

    internal void ValidateFloatingRect()
    {
        if ( Zone != DockZone.FloatingDocked ) return;
        if ( double.IsNaN( FloatX ) || double.IsInfinity( FloatX ) || double.IsNaN( FloatY ) || double.IsInfinity( FloatY )
            || double.IsNaN( FloatWidth ) || double.IsInfinity( FloatWidth ) || FloatWidth <= 0
            || double.IsNaN( FloatHeight ) || double.IsInfinity( FloatHeight ) || FloatHeight <= 0 )
            throw new PanelLayoutError( PanelLayoutErrorCode.InvalidFloatingRect, $"invalid-floating-rect: panel '{PanelId}' floating rect ({FloatX}, {FloatY}, {FloatWidth}x{FloatHeight}) must have a finite position and a positive size." );
    }

    public PanelState Clone() => new PanelState( PanelId, Zone, Size, Order )
    {
        Visible = Visible,
        FloatX = FloatX,
        FloatY = FloatY,
        FloatWidth = FloatWidth,
        FloatHeight = FloatHeight
    };
}

/// <summary>
/// The panel registry: which panels exist. Panel ids are stable strings a
/// future UI layer and the map tools bind to; registering the same id twice
/// is a named <see cref="PanelLayoutErrorCode.DuplicatePanelId"/> error.
/// </summary>
public sealed class PanelRegistry
{
    readonly Dictionary<string, PanelDefinition> _panels = new( StringComparer.Ordinal );

    public IReadOnlyList<PanelDefinition> Panels => _panels.Values
        .OrderBy( p => p.DefaultZone ).ThenBy( p => p.DefaultOrder ).ThenBy( p => p.PanelId, StringComparer.Ordinal ).ToList();

    public int Count => _panels.Count;

    public void Register( PanelDefinition definition )
    {
        if ( definition is null ) throw new ArgumentNullException( nameof( definition ) );
        if ( _panels.ContainsKey( definition.PanelId ) )
            throw new PanelLayoutError( PanelLayoutErrorCode.DuplicatePanelId, $"duplicate-panel-id: panel '{definition.PanelId}' is already registered." );
        _panels.Add( definition.PanelId, definition );
    }

    public bool IsRegistered( string panelId ) => !string.IsNullOrEmpty( panelId ) && _panels.ContainsKey( panelId );

    public PanelDefinition Get( string panelId )
    {
        if ( !TryGet( panelId, out var definition ) )
            throw new PanelLayoutError( PanelLayoutErrorCode.UnknownPanelId, $"unknown-panel-id: panel '{panelId}' is not registered." );
        return definition;
    }

    public bool TryGet( string panelId, out PanelDefinition definition )
    {
        definition = null;
        if ( string.IsNullOrEmpty( panelId ) ) return false;
        return _panels.TryGetValue( panelId, out definition );
    }

    /// <summary>
    /// The standard Hammer-style docked panels. Ids are the contract a future
    /// UI layer binds to; map tools declare one of these (or a panel they
    /// registered themselves) as their <see cref="MapOperationDeclaration.PanelTarget"/>.
    /// </summary>
    public static PanelRegistry Default()
    {
        var registry = new PanelRegistry();
        registry.Register( new PanelDefinition( "entities", "Entities", DockZone.Left, 280, 0 ) );
        registry.Register( new PanelDefinition( "layers", "Layers / Visgroups", DockZone.Left, 280, 1 ) );
        registry.Register( new PanelDefinition( "properties", "Properties", DockZone.Right, 280, 0 ) );
        registry.Register( new PanelDefinition( "textures", "Textures", DockZone.Right, 280, 1 ) );
        registry.Register( new PanelDefinition( "console", "Console", DockZone.Bottom, 200, 0 ) );
        registry.Register( new PanelDefinition( "build", "Build", DockZone.Bottom, 200, 1 ) );
        registry.Register( new PanelDefinition( "tool-options", "Tool Options", DockZone.Top, 48, 0 ) );
        registry.Register( new PanelDefinition( "face-edit", "Face Edit", DockZone.FloatingDocked, 480, 0 ) );
        return registry;
    }
}

/// <summary>
/// A persisted panel layout: which panels are open, where docked, what size
/// — saved per map (<see cref="MapId"/>) and workspace (<see cref="WorkspaceId"/>)
/// as versioned, diffable JSON. An old or unknown schema version loads with
/// a named error, never silent data loss; unknown JSON fields are ignored so
/// a newer layout still loads its known panels.
/// </summary>
public sealed class LayoutDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; private set; } = CurrentSchemaVersion;
    public string MapId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string ActivePanelId { get; set; } = "";
    public List<PanelState> Panels { get; } = new List<PanelState>();

    /// <summary>Fresh layout: every registered panel at its default zone / size / order, all visible.</summary>
    public static LayoutDocument Create( PanelRegistry registry, string mapId = "", string workspaceId = "" )
    {
        if ( registry is null ) throw new ArgumentNullException( nameof( registry ) );
        var document = new LayoutDocument { MapId = mapId ?? "", WorkspaceId = workspaceId ?? "" };
        foreach ( var definition in registry.Panels )
        {
            var state = new PanelState( definition.PanelId, definition.DefaultZone, definition.DefaultSize, definition.DefaultOrder );
            if ( definition.DefaultZone == DockZone.FloatingDocked ) { state.FloatX = 100; state.FloatY = 100; }
            document.Panels.Add( state );
        }
        document.ActivePanelId = document.Panels.Count > 0 ? document.Panels[0].PanelId : "";
        return document;
    }

    public static LayoutDocument CreateDefault( string mapId = "", string workspaceId = "" ) => Create( PanelRegistry.Default(), mapId, workspaceId );

    public PanelState GetPanel( string panelId )
    {
        if ( !TryGetPanel( panelId, out var state ) )
            throw new PanelLayoutError( PanelLayoutErrorCode.UnknownPanelId, $"unknown-panel-id: panel '{panelId}' is not in this layout." );
        return state;
    }

    public bool TryGetPanel( string panelId, out PanelState state )
    {
        state = null;
        if ( string.IsNullOrEmpty( panelId ) ) return false;
        state = Panels.FirstOrDefault( p => p.PanelId == panelId );
        return state is not null;
    }

    /// <summary>Hide / show a panel. Hiding keeps its dock zone, size and order for reopen.</summary>
    public void SetVisible( string panelId, bool visible ) => GetPanel( panelId ).Visible = visible;

    /// <summary>Move a panel to another dock zone (and order within that zone).</summary>
    public void MovePanel( string panelId, DockZone zone, int order )
    {
        if ( !Enum.IsDefined( zone ) ) throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid dock zone '{zone}' for panel '{panelId}'." );
        if ( order < 0 ) throw new PanelLayoutError( PanelLayoutErrorCode.InvalidPanelOrder, $"invalid-panel-order: panel '{panelId}' order {order} is negative." );
        var state = GetPanel( panelId );
        state.Zone = zone;
        state.Order = order;
    }

    public void ResizePanel( string panelId, double size )
    {
        PanelState.ValidateSize( size, panelId );
        GetPanel( panelId ).Size = size;
    }

    public void SetFloatingRect( string panelId, double x, double y, double width, double height )
    {
        var state = GetPanel( panelId );
        state.FloatX = x;
        state.FloatY = y;
        state.FloatWidth = width;
        state.FloatHeight = height;
        state.ValidateFloatingRect();
    }

    /// <summary>
    /// Named defects, empty when the layout is sound. Never throws and never
    /// drops a panel: unknown panels (not in <paramref name="registry"/>) are
    /// reported, not silently removed, so a load never loses data.
    /// </summary>
    public IReadOnlyList<string> Validate( PanelRegistry registry = null )
    {
        var defects = new List<string>();
        var seenIds = new HashSet<string>( StringComparer.Ordinal );
        var seenSlots = new HashSet<string>( StringComparer.Ordinal );
        foreach ( var panel in Panels )
        {
            if ( !seenIds.Add( panel.PanelId ) ) defects.Add( $"duplicate-panel-id: panel '{panel.PanelId}' appears twice in this layout." );
            if ( registry is not null && !registry.IsRegistered( panel.PanelId ) )
                defects.Add( $"unknown-panel-id: panel '{panel.PanelId}' is in this layout but not in the registry." );
            if ( double.IsNaN( panel.Size ) || double.IsInfinity( panel.Size ) || panel.Size <= 0 )
                defects.Add( $"invalid-panel-size: panel '{panel.PanelId}' size {panel.Size.ToString( CultureInfo.InvariantCulture )} must be a finite number greater than zero." );
            if ( panel.Order < 0 )
                defects.Add( $"invalid-panel-order: panel '{panel.PanelId}' order {panel.Order} is negative." );
            var slot = panel.Zone + "/" + panel.Order;
            if ( !seenSlots.Add( slot ) ) defects.Add( $"duplicate-order: two panels share dock slot {slot}." );
            if ( panel.Zone == DockZone.FloatingDocked
                && ( double.IsNaN( panel.FloatX ) || double.IsInfinity( panel.FloatX ) || double.IsNaN( panel.FloatY ) || double.IsInfinity( panel.FloatY )
                    || double.IsNaN( panel.FloatWidth ) || double.IsInfinity( panel.FloatWidth ) || panel.FloatWidth <= 0
                    || double.IsNaN( panel.FloatHeight ) || double.IsInfinity( panel.FloatHeight ) || panel.FloatHeight <= 0 ) )
                defects.Add( $"invalid-floating-rect: panel '{panel.PanelId}' floating rect must have a finite position and a positive size." );
        }
        if ( !string.IsNullOrEmpty( ActivePanelId ) && !TryGetPanel( ActivePanelId, out _ ) )
            defects.Add( $"unknown-panel-id: active panel '{ActivePanelId}' is not in this layout." );
        return defects;
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using ( var writer = new Utf8JsonWriter( stream, new JsonWriterOptions { Indented = true } ) )
        {
            writer.WriteStartObject();
            writer.WriteNumber( "schemaVersion", CurrentSchemaVersion );
            writer.WriteString( "mapId", MapId );
            writer.WriteString( "workspaceId", WorkspaceId );
            writer.WriteString( "activePanelId", ActivePanelId );
            writer.WriteStartArray( "panels" );
            foreach ( var panel in Panels )
            {
                writer.WriteStartObject();
                writer.WriteString( "panelId", panel.PanelId );
                writer.WriteString( "zone", panel.Zone.ToString() );
                writer.WriteNumber( "size", panel.Size );
                writer.WriteBoolean( "visible", panel.Visible );
                writer.WriteNumber( "order", panel.Order );
                writer.WriteNumber( "floatX", panel.FloatX );
                writer.WriteNumber( "floatY", panel.FloatY );
                writer.WriteNumber( "floatWidth", panel.FloatWidth );
                writer.WriteNumber( "floatHeight", panel.FloatHeight );
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString( stream.ToArray() );
    }

    public static LayoutDocument FromJson( string json )
    {
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse( json ); }
        catch ( JsonException ex ) { throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid-layout-json: not valid JSON ({ex.Message})." ); }
        using ( parsed )
        {
            var root = parsed.RootElement;
            if ( root.ValueKind != JsonValueKind.Object )
                throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, "invalid-layout-json: layout root must be a JSON object." );
            if ( !root.TryGetProperty( "schemaVersion", out var versionElement ) || versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32( out var version ) )
                throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, "invalid-layout-json: layout is missing an integer 'schemaVersion'." );
            if ( version != CurrentSchemaVersion )
                throw new PanelLayoutError( PanelLayoutErrorCode.UnknownSchemaVersion, $"unknown-schema-version: layout schema version {version} is not supported (this build reads version {CurrentSchemaVersion}); migrate or re-save, never load silently." );
            var document = new LayoutDocument { SchemaVersion = version };
            document.MapId = GetString( root, "mapId" );
            document.WorkspaceId = GetString( root, "workspaceId" );
            document.ActivePanelId = GetString( root, "activePanelId" );
            if ( !root.TryGetProperty( "panels", out var panelsElement ) || panelsElement.ValueKind != JsonValueKind.Array )
                throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, "invalid-layout-json: layout is missing a 'panels' array." );
            var seenIds = new HashSet<string>( StringComparer.Ordinal );
            foreach ( var element in panelsElement.EnumerateArray() )
            {
                if ( element.ValueKind != JsonValueKind.Object )
                    throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, "invalid-layout-json: every panel entry must be a JSON object." );
                var panelId = GetRequiredString( element, "panelId" );
                if ( !seenIds.Add( panelId ) )
                    throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid-layout-json: duplicate-panel-id: panel '{panelId}' appears twice in this layout." );
                var zoneText = GetRequiredString( element, "zone" );
                if ( !Enum.TryParse<DockZone>( zoneText, ignoreCase: true, out var zone ) || !Enum.IsDefined( zone ) )
                    throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid-layout-json: panel '{panelId}' has unknown dock zone '{zoneText}'." );
                if ( !element.TryGetProperty( "size", out var sizeElement ) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetDouble( out var size ) )
                    throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid-layout-json: panel '{panelId}' is missing a numeric 'size'." );
                var state = new PanelState( panelId, zone, size, GetInt( element, "order", panelId ) );
                state.Visible = GetBool( element, "visible", true, panelId );
                state.FloatX = GetDouble( element, "floatX", 0, panelId );
                state.FloatY = GetDouble( element, "floatY", 0, panelId );
                state.FloatWidth = GetDouble( element, "floatWidth", 480, panelId );
                state.FloatHeight = GetDouble( element, "floatHeight", 360, panelId );
                state.ValidateFloatingRect();
                document.Panels.Add( state );
            }
            return document;
        }
    }

    static string GetString( JsonElement root, string name )
        => root.TryGetProperty( name, out var element ) && element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : "";

    static string GetRequiredString( JsonElement element, string name )
    {
        if ( !element.TryGetProperty( name, out var value ) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace( value.GetString() ) )
            throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid-layout-json: panel entry is missing a non-empty string '{name}'." );
        return value.GetString();
    }

    static int GetInt( JsonElement element, string name, string panelId )
    {
        if ( !element.TryGetProperty( name, out var value ) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32( out var result ) )
            throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid-layout-json: panel '{panelId}' is missing an integer '{name}'." );
        return result;
    }

    static double GetDouble( JsonElement element, string name, double fallback, string panelId )
    {
        if ( !element.TryGetProperty( name, out var value ) ) return fallback;
        if ( value.ValueKind != JsonValueKind.Number || !value.TryGetDouble( out var result ) )
            throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid-layout-json: panel '{panelId}' has a non-numeric '{name}'." );
        return result;
    }

    static bool GetBool( JsonElement element, string name, bool fallback, string panelId )
    {
        if ( !element.TryGetProperty( name, out var value ) ) return fallback;
        if ( value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False )
            throw new PanelLayoutError( PanelLayoutErrorCode.InvalidLayoutJson, $"invalid-layout-json: panel '{panelId}' has a non-boolean '{name}'." );
        return value.GetBoolean();
    }

    /// <summary>
    /// Atomic save (same-directory temp + complete write/flush + rename, per
    /// handoffs/CONTRACTS.md): a failed save never clears or half-writes the
    /// previous layout.
    /// </summary>
    public void Save( string path )
    {
        if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "layout path must not be empty.", nameof( path ) );
        var fullPath = Path.GetFullPath( path );
        Directory.CreateDirectory( Path.GetDirectoryName( fullPath ) );
        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString( "N" );
        try
        {
            File.WriteAllText( tempPath, ToJson() );
            File.Move( tempPath, fullPath, overwrite: true );
        }
        finally
        {
            if ( File.Exists( tempPath ) ) File.Delete( tempPath );
        }
    }

    public static LayoutDocument Load( string path )
    {
        if ( string.IsNullOrWhiteSpace( path ) ) throw new ArgumentException( "layout path must not be empty.", nameof( path ) );
        return FromJson( File.ReadAllText( Path.GetFullPath( path ) ) );
    }
}

/// <summary>
/// A map operation's UI declaration: which docked panel it targets. Any new
/// map operation must declare its panel target; an operation that can only
/// work as a modal dialog fails the <see cref="NoModalContract"/> check.
/// </summary>
public sealed class MapOperationDeclaration
{
    public string OperationId { get; }
    public string PanelTarget { get; }
    public bool RequiresModalDialog { get; }
    public string ModalReason { get; }

    public MapOperationDeclaration( string operationId, string panelTarget, bool requiresModalDialog = false, string modalReason = "" )
    {
        if ( string.IsNullOrWhiteSpace( operationId ) ) throw new ArgumentException( "operation id must not be empty.", nameof( operationId ) );
        OperationId = operationId;
        PanelTarget = panelTarget ?? "";
        RequiresModalDialog = requiresModalDialog;
        ModalReason = modalReason ?? "";
    }
}

/// <summary>Outcome of one <see cref="NoModalContract"/> check: passed, or a named reason it failed.</summary>
public sealed class NoModalCheckResult
{
    public bool Passed { get; }
    public string Reason { get; }
    public PanelLayoutErrorCode? Code { get; }

    internal NoModalCheckResult( bool passed, string reason, PanelLayoutErrorCode? code )
    {
        Passed = passed;
        Reason = reason;
        Code = code;
    }

    internal static NoModalCheckResult Pass() => new NoModalCheckResult( true, "", null );

    internal static NoModalCheckResult Fail( PanelLayoutErrorCode code, string reason ) => new NoModalCheckResult( false, reason, code );
}

/// <summary>
/// The no-modal-dialogs contract, used by the map tools: every operation
/// that would open a dialog instead targets a docked panel id. A modal-only
/// operation, an operation with no panel target, or one targeting an
/// unregistered panel fails with a named reason.
/// </summary>
public static class NoModalContract
{
    public static NoModalCheckResult Check( MapOperationDeclaration operation, PanelRegistry registry )
    {
        if ( operation is null ) throw new ArgumentNullException( nameof( operation ) );
        if ( operation.RequiresModalDialog )
        {
            var why = string.IsNullOrWhiteSpace( operation.ModalReason ) ? "" : $" ({operation.ModalReason})";
            return NoModalCheckResult.Fail( PanelLayoutErrorCode.ModalDialogNotAllowed,
                $"modal-dialog: operation '{operation.OperationId}' requires a modal dialog{why}; dock it in a panel instead — modal dialogs are not allowed." );
        }
        if ( string.IsNullOrWhiteSpace( operation.PanelTarget ) )
            return NoModalCheckResult.Fail( PanelLayoutErrorCode.MissingPanelTarget,
                $"missing-panel-target: operation '{operation.OperationId}' declares no docked panel target; every map operation must declare its panel target." );
        if ( registry is not null && !registry.IsRegistered( operation.PanelTarget ) )
            return NoModalCheckResult.Fail( PanelLayoutErrorCode.UnknownPanelTarget,
                $"unknown-panel-target: operation '{operation.OperationId}' targets panel '{operation.PanelTarget}', which is not registered." );
        return NoModalCheckResult.Pass();
    }

    /// <summary>Same as <see cref="Check"/> but throws a named <see cref="PanelLayoutError"/> on failure.</summary>
    public static void Require( MapOperationDeclaration operation, PanelRegistry registry )
    {
        var result = Check( operation, registry );
        if ( !result.Passed ) throw new PanelLayoutError( result.Code.Value, result.Reason );
    }

    public static IReadOnlyList<NoModalCheckResult> CheckAll( IEnumerable<MapOperationDeclaration> operations, PanelRegistry registry )
    {
        if ( operations is null ) throw new ArgumentNullException( nameof( operations ) );
        return operations.Select( operation => Check( operation, registry ) ).ToList();
    }
}
