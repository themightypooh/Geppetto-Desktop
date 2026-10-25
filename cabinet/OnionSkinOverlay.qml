// OnionSkinOverlay.qml — 2D animation suite, lane 5/13.
//
// A standalone onion-skin overlay for the 2D canvas. It draws NOTHING by itself:
// the host feeds it ghostFrames (one entry per ghost, computed by Effigy.OnionSkin
// or by this file's own opacityFor() for a QML-side plan) and places this Item
// above the canvas background and BELOW the current frame, so ghosts never cover
// the drawing being edited.
//
// WIRING DEFERRED (do not hand-edit cabinet.qml for this lane): cabinet.qml must
// instantiate this overlay inside the 2D canvas, bind ghostFrames to the timeline's
// current frame via OnionSkin.Compute, and bind the drawing delegates (the host's
// existing frame renderer) into ghostDelegate. Until then this file is a complete,
// self-describing component — its controls, colours, and falloff are all here and
// match OnionSkin.cs number-for-number.
//
// Colours are configurable on purpose: Toon Boom Harmony defaults to red =
// previous, green = next, but Animation Desk swaps them, so a hardcoded pair
// would be wrong for half the users. Defaults below are the Harmony pair;
// swapColors() flips them, or set beforeColor/afterColor directly.
import QtQuick

Item {
    id: onion

    // ---- plan inputs (mirror OnionSettings) ----
    property bool onionEnabled: true
    property string mode: "keys"              // "keys" | "frames" | "selected"
    property int beforeCount: 1
    property int afterCount: 1
    property real masterOpacity: 0.5
    property real beforeOpacity: 1.0
    property real afterOpacity: 1.0
    // Per-offset multipliers, index 0 = distance 1; empty = pure linear falloff.
    property var perOffsetOpacity: []
    property bool useFalloff: true
    property color beforeColor: "#ff0000"     // Harmony default: previous = red
    property color afterColor: "#00ff00"      // Harmony default: next = green
    property real tintStrength: 1.0           // 1 = flat silhouette in side colour
    property bool showStartFrame: false       // loop joint: also show frame 0

    // ---- frames to ghost ----
    // Each entry: { frame, isBefore, distance, opacity, tint, isStartFrame }.
    // Opacity here is already folded (master x side x falloff) when the host uses
    // OnionSkin.Compute; opacityFor() below recomputes it for QML-side plans.
    property var ghostFrames: []

    // ---- helpers (same numbers as OnionSkin.cs) ----
    // rank = 1 for the nearest ghost, 2 for the next, … — NOT the frame distance:
    // in Keys mode two keys can be 20 frames apart, and a falloff in raw frames
    // would fade the second key to nothing. ghostFrames entries already carry
    // their host-computed opacity; this is only for QML-side plans.
    function opacityFor(rank, isBefore) {
        var count = isBefore ? beforeCount : afterCount
        if (count <= 0) return 0
        var factor = 1.0
        if (perOffsetOpacity.length >= rank)
            factor = perOffsetOpacity[rank - 1]
        else if (useFalloff)
            factor = Math.max(0, 1 - (rank - 1) / count)
        var side = isBefore ? beforeOpacity : afterOpacity
        return Math.min(1, Math.max(0, masterOpacity * side * factor))
    }

    function swapColors() {
        var c = beforeColor; beforeColor = afterColor; afterColor = c
        var o = beforeOpacity; beforeOpacity = afterOpacity; afterOpacity = o
    }

    visible: onionEnabled && ghostFrames.length > 0

    // Farthest ghosts first in the model = painted first = nearer ghosts win.
    // The host supplies ghostFrames already sorted farthest-first (OnionSkin.Compute
    // does this); this Repeater preserves that order.
    Repeater {
        model: onion.ghostFrames
        delegate: Item {
            required property var modelData
            // The host replaces this placeholder with its frame renderer and sets
            // its tint from modelData.tint / tintStrength. The placeholder below is
            // only the label + silhouette wash, so the overlay is readable even
            // before the renderer delegate lands.
            Rectangle {
                anchors.fill: parent
                color: modelData.isBefore ? onion.beforeColor : onion.afterColor
                opacity: modelData.opacity * onion.tintStrength
            }
            Text {
                text: (modelData.isBefore ? "−" : "+") + modelData.distance
                      + (modelData.isStartFrame ? "  ◐ loop" : "")
                color: modelData.isBefore ? onion.beforeColor : onion.afterColor
                font.pixelSize: 12
                opacity: 0.9
            }
        }
    }
}
