// The tool wheel over the viewport, after Linework's tool dial: a ring of category slices round a
// paper core. It moves with the model and only with it: each time the viewport shows a new revision
// the wheel lights that step's category, fans it out and lights the tool (the same tool again presses
// the core in), and between revisions it holds still. It goes a while after the run stops.
// It only shows what the run does: no mouse, so the viewport under it still orbits and picks.
// Opened by hand (`manual`, the WHEEL key in the tool drawer) it is yours, whatever a run is doing:
// it takes the mouse, click a category to open it, a tool to pick it (`picked`, which brings up the
// tool card to use it on the model), the core to put it away.
import QtQuick
import QtQuick.Shapes

Item {
    id: wheel
    // the operation of the change on screen, "line 12: add Blob name=Torso ..." (a script run) or a tool
    // name: the wheel lands on its tool as the model changes, not before
    property string op: ""
    property string working: ""             // the step the run is building now, not yet on screen: the ring creeps
    onWorkingChanged: if (working !== "") lap()
    property int hold: 4200                 // ms the wheel stays up after the last change
    property real pace: 0                   // s between the run's steps, when it is paced: the core ring counts it down
    property bool stepping: false           // the run is going (not paused, not done): the tool keeps working
    property bool manual: false             // opened by hand: it stays up and takes clicks until put away
    signal picked(string key, string label)
    onManualChanged: {
        stepper.stop();
        openGroup = -1; litGroup = -1; tool = ""; info = null; hot = -1;
        shown = manual;
    }
    width: 420; height: 420
    readonly property real c: width / 2
    readonly property real rCore: 82        // the paper centre
    readonly property real rRing: 204       // the ring runs rCore..rRing

    readonly property color steel: "#2b2f36"
    readonly property color steelHi: "#3d424b"
    readonly property color steelLo: "#1f2228"
    readonly property color edge: "#0b0c0e"
    readonly property color bevelHi: "#767c86"
    readonly property color bevelLo: "#101215"
    readonly property color lit: "#f2a91c"
    readonly property color text: "#c9ccd0"
    readonly property color ink: "#1d2318"
    readonly property color paper: "#f4f7ee"
    readonly property string mono: "DejaVu Sans Mono"

    // [key, label]: keys are Effigy feature types (`add Blob`), `edit` operations (`edit:bevel`) and
    // script verbs, lower case with spaces and underscores taken out, as BuildScript reads them
    readonly property var groups: [
        { name: "FORM", tools: [["primitive", "PRIMITIVE"], ["blob", "BLOB"], ["part", "PART"], ["sketch", "SKETCH"], ["profile", "PROFILE"],
                                ["spline", "SPLINE"], ["plane", "PLANE"], ["import", "IMPORT"], ["bodyfrombones", "BODY FROM BONES"]] },
        { name: "BUILD", tools: [["extrude", "EXTRUDE"], ["revolve", "REVOLVE"], ["loft", "LOFT"], ["sweep", "SWEEP"], ["boolean", "BOOLEAN"],
                                 ["mirror", "MIRROR"], ["linearpattern", "ROW PATTERN"], ["circularpattern", "CIRCLE PATTERN"], ["shell", "SHELL"], ["hole", "HOLE"]] },
        { name: "REFINE", tools: [["fillet", "FILLET"], ["chamfer", "CHAMFER"], ["draft", "DRAFT"], ["trim", "TRIM"], ["subdivide", "SUBDIVIDE"],
                                  ["remesh", "REMESH"], ["detail", "DETAIL"], ["stroke", "STROKE"]] },
        { name: "DEFORM", tools: [["sculpt", "SCULPT"], ["transform", "MOVE"], ["lattice", "LATTICE"], ["simpledeform", "BEND"],
                                  ["curvedeform", "CURVE"], ["moveface", "PUSH FACE"]] },
        { name: "MESH", tools: [["edit:extrude", "EXTRUDE FACES"], ["edit:inset", "INSET"], ["edit:bevel", "BEVEL"], ["edit:loopcut", "LOOP CUT"],
                                ["edit:bridge", "BRIDGE"], ["edit:symmetrize", "SYMMETRIZE"], ["edit:dissolve", "DISSOLVE"], ["edit:delete", "DELETE"],
                                ["meshedit", "MESH EDIT"], ["deleteface", "DELETE FACE"]] },
        { name: "CLOTH", tools: [["garment", "GARMENT"], ["fabric", "FABRIC"], ["padding", "PADDING"], ["pocket", "POCKET"],
                                 ["hardware", "HARDWARE"], ["wear", "WEAR"], ["wearer", "WEARER"], ["fur", "FUR"]] },
        { name: "SURFACE", tools: [["material", "MATERIAL"], ["paint", "PAINT"], ["facematerial", "FACE MATERIAL"], ["fleshmaterial", "FLESH"],
                                   ["skin", "SKIN"], ["glow", "GLOW"], ["uvproject", "UV PROJECT"]] },
        { name: "RIG", tools: [["bind", "BIND"], ["shapekey", "SHAPE KEY"], ["shapekeyend", "KEY END"], ["shapekeysplit", "SPLIT KEY"], ["correctiveshapekey", "CORRECTIVE"]] },
        { name: "TWEAK", tools: [["set", "SET"], ["remove", "REMOVE"], ["remedy", "REMEDY"], ["fix", "FIX MESH"]] },
        { name: "CHECK", tools: [["measure", "MEASURE"], ["distance", "DISTANCE"], ["symmetry", "SYMMETRY"], ["clearance", "CLEARANCE"],
                                 ["topology", "TOPOLOGY"], ["section", "SECTION"], ["flex", "FLEX"], ["match", "MATCH"], ["describe", "DESCRIBE"]] },
        { name: "OUTPUT", tools: [["render", "RENDER"], ["turnaround", "TURNAROUND"], ["save", "SAVE"], ["export", "EXPORT"],
                                  ["bake", "BAKE"], ["paintmaps", "PAINT MAPS"], ["pivot", "PIVOT"], ["rebuild", "REBUILD"]] },
    ]
    // a few spellings that land on the same tile
    readonly property var aliases: ({ "edit:extrudeindividual": "edit:extrude", "edit:extrudealongnormals": "edit:extrude",
                                      "edit:limiteddissolve": "edit:dissolve", "editop": "meshedit", "garmentdetail": "garment",
                                      "at": "measure", "corrective": "correctiveshapekey" })
    function groupOf(key) {
        for (let g = 0; g < groups.length; g++)
            for (const t of groups[g].tools) if (t[0] === key) return g;
        return -1;
    }
    function labelOf(key) {
        const g = groupOf(key);
        if (g >= 0) for (const t of groups[g].tools) if (t[0] === key) return t[1];
        return key.replace(/^edit:/, "").toUpperCase();
    }
    // the tool's icon: modelled in Geppetto (cabinet/model/toolicons), baked to art/tools/<key>.png
    function iconOf(key) { return key && key !== "__back" ? Qt.resolvedUrl("art/tools/" + key.replace(":", "_") + ".png") : "" }
    function norm(s) { return String(s || "").toLowerCase().replace(/[ _']/g, "") }

    // "line 12: add Blob name=Torso" -> {key: "blob", line: 12, target: "Torso"}; null when there is no tool in it
    function parse(op) {
        let s = String(op || "").trim(), line = 0;
        const m = /^line (\d+):\s*(.*)$/.exec(s);
        if (m) { line = parseInt(m[1]); s = m[2]; }
        s = s.replace(/^undo\s+/i, "");      // a step undone (the bench's UNDO): the tool it took back
        if (/^start:/.test(s)) return { key: "import", line: line, target: s.slice(6).trim() };   // a bench: what it was copied from
        if (/^initial$/.test(s)) return null;  // the empty document a run starts on: nothing on screen changed
        const words = s.split(/\s+/).filter(w => w.length);
        if (!words.length) return null;
        const verb = norm(words[0]);
        let key = verb, target = "";
        const named = /\bname=("([^"]*)"|(\S+))/.exec(s);
        if (verb === "add" && words.length > 1) {   // add Blob name=Grip, add BlobFeature, add Blob Grip (an AI run)
            key = norm(words[1]).replace(/(.)feature$/, "$1");
            target = named ? (named[2] || named[3]) : words[2] && words[2].indexOf("=") < 0 ? words[2] : "";
        }
        else if (verb === "edit" && words.length > 2) {   // edit Box bevel, or two words: edit Box Loop cut
            const two = "edit:" + norm(words[2] + (words[3] || ""));
            key = words.length > 3 && words[3].indexOf("=") < 0 && groupOf(aliases[two] || two) >= 0 ? two : "edit:" + norm(words[2]);
            target = words[1];
        }
        else target = words[1] || "";           // a verb (set, material, measure ...), or one the wheel has no tile for: named in the core
        key = aliases[key] || key;
        return { key: key, line: line, target: target.replace(/^"|"$/g, "") };
    }

    // ---- state the sequence below steps through
    property string tool: ""                // the lit tool
    property int litGroup: -1               // the lit category slice, while the ring shows categories
    property int openGroup: -1              // which category is fanned out (-1: the category slices)
    property int shownGroup: 0              // the category the tool tiles belong to (kept while folding back)
    onOpenGroupChanged: if (openGroup >= 0) shownGroup = openGroup
    property real t: openGroup >= 0 ? 1 : 0
    Behavior on t { NumberAnimation { duration: 300; easing.type: Easing.OutCubic } }
    property bool shown: false
    property var info: null                 // the parsed operation the core shows

    opacity: shown ? 1 : 0
    scale: shown ? 1 : 0.86
    visible: opacity > 0.01
    Behavior on opacity { NumberAnimation { duration: 260 } }
    Behavior on scale { NumberAnimation { duration: 320; easing.type: Easing.OutBack } }

    // in lockstep with the model: the op changes exactly when the viewport shows a new revision, and
    // the wheel moves then and only then. A new tool is lit at once (no reach played out over time, which
    // would run behind the model); the same tool again presses in and the line moves on
    onOpChanged: {
        if (manual) return;                     // opened by hand: it's yours, the run's tools wait
        // the run's tool stays off the model. the wheel lives in the tools drawer, and only when opened
        shown = false;
    }
    // each change: the core presses in, and the wheel stays up `hold` ms past the last one
    signal stroke()
    onStroke: { press.restart(); putAway() }
    function putAway() {
        if (manual) return;
        stepper.restart();
    }
    onSteppingChanged: if (!stepping && shown) putAway()
    Timer {
        id: stepper
        interval: wheel.hold
        onTriggered: {
            if (wheel.stepping) { restart(); return; }   // never while the run is still going
            wheel.shown = false;
        }
    }

    // ---- geometry, as Linework's dial
    readonly property real catSweep: 360 / groups.length
    function catMid(g) { return -90 + g * catSweep }
    readonly property var tiles: groups[shownGroup].tools.concat([["__back", ""]])
    readonly property real tileSweep: 360 / tiles.length
    function lerpAngle(a, b, k) { let d = b - a; while (d > 180) d -= 360; while (d < -180) d += 360; return a + d * k }
    function tileMid(k) {   // fanning out of the category's slice
        const from = catMid(shownGroup) + (k - (tiles.length - 1) / 2) * (catSweep / tiles.length);
        return lerpAngle(from, -90 + k * tileSweep, t);
    }
    function tileSpan() { return catSweep / tiles.length + (tileSweep - catSweep / tiles.length) * t }
    function polar(a, r) { const ar = a * Math.PI / 180; return Qt.point(c + Math.cos(ar) * r, c + Math.sin(ar) * r) }
    readonly property real rMid: (rCore + rRing) / 2 + 2
    property var lastIn: ({})              // category -> the tool last used there, shown under its name
    onToolChanged: if (tool !== "") { const l = Object.assign({}, lastIn); l[groupOf(tool)] = tool; lastIn = l; stroke(); }

    // ---- the disc
    Rectangle { x: 6; y: 6; width: parent.width; height: parent.height; radius: width / 2; color: "#000000"; opacity: 0.35 }
    Rectangle { anchors.fill: parent; radius: width / 2; color: wheel.steelLo; border.color: wheel.edge; border.width: 2 }

    // an annular slice with a black outline and a bevel: light on the outer edge, dark on the inner
    component Slice: Shape {
        id: sl
        property real mid: 0
        property real sweep: 30
        property color fill: wheel.steel
        property bool down: false
        anchors.fill: parent
        preferredRendererType: Shape.CurveRenderer
        ShapePath {
            strokeColor: wheel.edge; strokeWidth: 2; joinStyle: ShapePath.MiterJoin
            fillColor: sl.fill
            PathAngleArc { centerX: wheel.c; centerY: wheel.c; radiusX: wheel.rRing; radiusY: wheel.rRing; startAngle: sl.mid - sl.sweep / 2; sweepAngle: sl.sweep }
            PathAngleArc { moveToStart: false; centerX: wheel.c; centerY: wheel.c; radiusX: wheel.rCore; radiusY: wheel.rCore; startAngle: sl.mid + sl.sweep / 2; sweepAngle: -sl.sweep }
            PathLine { x: wheel.polar(sl.mid - sl.sweep / 2, wheel.rRing).x; y: wheel.polar(sl.mid - sl.sweep / 2, wheel.rRing).y }
        }
        ShapePath {
            strokeColor: sl.down ? wheel.bevelLo : wheel.bevelHi; strokeWidth: 2; fillColor: "transparent"; capStyle: ShapePath.FlatCap
            PathAngleArc { centerX: wheel.c; centerY: wheel.c; radiusX: wheel.rRing - 3; radiusY: wheel.rRing - 3; startAngle: sl.mid - sl.sweep / 2 + 1.2; sweepAngle: Math.max(0, sl.sweep - 2.4) }
        }
        ShapePath {
            strokeColor: sl.down ? wheel.bevelHi : wheel.bevelLo; strokeWidth: 2; fillColor: "transparent"; capStyle: ShapePath.FlatCap
            PathAngleArc { centerX: wheel.c; centerY: wheel.c; radiusX: wheel.rCore + 3; radiusY: wheel.rCore + 3; startAngle: sl.mid - sl.sweep / 2 + 2; sweepAngle: Math.max(0, sl.sweep - 4) }
        }
    }
    component Label: Text {
        font.family: wheel.mono; font.bold: true; font.pixelSize: 12
        renderType: Text.QtRendering
        horizontalAlignment: Text.AlignHCenter
    }

    // ---- the category slices (fade out as a category opens)
    Item {
        anchors.fill: parent
        opacity: Math.max(0, 1 - wheel.t / 0.5)
        visible: opacity > 0.01
        Repeater {
            model: wheel.groups.length
            Item {
                id: cat
                required property int index
                readonly property bool on: wheel.litGroup === cat.index
                anchors.fill: parent
                Slice { mid: wheel.catMid(cat.index); sweep: wheel.catSweep; fill: cat.on ? wheel.lit : wheel.hot === cat.index ? wheel.steelHi : wheel.steel; down: cat.on }
                Column {
                    readonly property point p: wheel.polar(wheel.catMid(cat.index), wheel.rMid)
                    x: p.x - width / 2; y: p.y - height / 2
                    spacing: 2
                    Label { anchors.horizontalCenter: parent.horizontalCenter; text: wheel.groups[cat.index].name; color: cat.on ? wheel.ink : wheel.text }
                    Image {
                        anchors.horizontalCenter: parent.horizontalCenter
                        readonly property string last: wheel.lastIn[cat.index] || wheel.groups[cat.index].tools[0][0]   // its first tool until one is used
                        width: 30; height: 30; source: wheel.iconOf(last)
                        visible: last !== "" && status === Image.Ready
                        smooth: false
                    }
                }
            }
        }
    }
    // ---- the open category's tools, fanning out of its slice to fill the ring
    Item {
        anchors.fill: parent
        opacity: Math.max(0, (wheel.t - 0.45) / 0.55)   // gone before the slices come back: no smear
        visible: opacity > 0.01
        Repeater {
            model: wheel.tiles
            Item {
                id: tile
                required property var modelData
                required property int index
                readonly property bool back: modelData[0] === "__back"
                readonly property bool on: !back && wheel.tool === modelData[0]
                readonly property real mid: wheel.tileMid(index)
                anchors.fill: parent
                Slice { mid: tile.mid; sweep: wheel.tileSpan(); fill: tile.on ? wheel.lit : wheel.hot === tile.index ? wheel.steelHi : tile.back ? wheel.steelLo : wheel.steel; down: tile.on }
                Column {
                    readonly property point p: wheel.polar(tile.mid, wheel.rMid)
                    x: p.x - width / 2; y: p.y - height / 2
                    width: 74; spacing: 1
                    Image {
                        anchors.horizontalCenter: parent.horizontalCenter
                        visible: !tile.back && status === Image.Ready
                        width: 60; height: 60; source: wheel.iconOf(tile.modelData[0])
                        smooth: false; fillMode: Image.PreserveAspectFit
                        scale: tile.on ? 1.15 : 1
                        Behavior on scale { NumberAnimation { duration: 200 } }
                    }
                    Label {
                        width: parent.width; wrapMode: Text.WordWrap; lineHeight: 0.9
                        text: tile.back ? wheel.groups[wheel.shownGroup].name : tile.modelData[1]
                        font.pixelSize: tile.back ? 11 : 10
                        color: tile.on ? wheel.ink : tile.back ? "#7a7f88" : wheel.text
                    }
                }
            }
        }
    }

    // ---- between steps: a ring round the core runs down to the next step (or circles, unpaced)
    // the ring is full the moment the model changes, never before: it starts when a change lands
    // (`revision`), fills to `wait` over exactly the pace the run holds for, then creeps on while the
    // next step is worked out (as long as steps have been taking), and closes when that one lands
    property int revision: 0                // the run's latest model revision
    property real revAt: 0                  // ms, when the model last changed
    property real opAt: 0                   // ms, when the run started on its next step
    property real workAvg: 800              // ms a step takes to work out, learned as the run goes
    property real done: 0                   // 0 as the model changes .. 1 as it changes again
    readonly property real wait: pace > 0 ? 0.8 : 0
    function lap() { opAt = Date.now() }
    onRevisionChanged: {
        const now = Date.now();
        if (opAt > revAt && now - opAt < 120000) workAvg = workAvg * 0.6 + Math.max(100, now - opAt) * 0.4;
        revAt = now; done = 0;
    }
    Timer {
        interval: 33; repeat: true
        running: wheel.shown && wheel.stepping && wheel.revAt > 0
        onTriggered: {
            const now = Date.now();
            if (wheel.opAt > wheel.revAt)       // working out the next step: creep, never quite full
                wheel.done = wheel.wait + (1 - wheel.wait) * 0.97 * (1 - Math.exp(-2 * (now - wheel.opAt) / wheel.workAvg));
            else                                // holding for the pace
                wheel.done = wheel.wait * Math.min(1, (now - wheel.revAt) / Math.max(1, wheel.pace * 1000));
        }
    }
    Shape {
        anchors.fill: parent
        visible: wheel.stepping && wheel.tool !== ""
        preferredRendererType: Shape.CurveRenderer
        ShapePath {
            strokeColor: wheel.lit; strokeWidth: 5; fillColor: "transparent"; capStyle: ShapePath.RoundCap
            PathAngleArc {
                centerX: wheel.c; centerY: wheel.c; radiusX: wheel.rCore + 7; radiusY: wheel.rCore + 7
                startAngle: -90
                sweepAngle: 360 * wheel.done
            }
        }
    }

    // ---- core: the paper centre names the tool, the line it is on and what it works
    Item {
        id: core
        objectName: "wheelCore"
        anchors.centerIn: parent
        width: wheel.rCore * 2 - 4; height: width
        property real squash: 0             // a step: the core dips in, as under a press
        scale: 1 - 0.06 * squash
        SequentialAnimation {
            id: press
            NumberAnimation { target: core; property: "squash"; to: 1; duration: 90; easing.type: Easing.OutQuad }
            NumberAnimation { target: core; property: "squash"; to: 0; duration: 380; easing.type: Easing.OutBack }
        }
        Rectangle { anchors.fill: parent; radius: width / 2; color: wheel.paper; border.color: core.squash > 0.2 ? wheel.lit : wheel.edge; border.width: 2 + 2 * core.squash }
        Label {
            anchors.horizontalCenter: parent.horizontalCenter; anchors.top: parent.top; anchors.topMargin: 10
            font.pixelSize: 10; color: "#7a7f88"
            text: wheel.info && wheel.info.line ? "LINE " + wheel.info.line : ""
        }
        Column {
            anchors.centerIn: parent; anchors.verticalCenterOffset: 4
            width: parent.width - 18; spacing: 0
            Image {
                id: coreIcon
                anchors.horizontalCenter: parent.horizontalCenter
                width: 60; height: 60; source: wheel.iconOf(wheel.tool)
                visible: wheel.tool !== "" && status === Image.Ready
                smooth: false; fillMode: Image.PreserveAspectFit
            }
            Label {
                width: parent.width; wrapMode: Text.WordWrap; lineHeight: 0.9
                font.pixelSize: 13; color: wheel.ink
                text: wheel.tool !== "" ? wheel.labelOf(wheel.tool) : wheel.litGroup >= 0 ? wheel.groups[wheel.litGroup].name : ""
            }
        }
        Label {
            anchors.horizontalCenter: parent.horizontalCenter; anchors.bottom: parent.bottom; anchors.bottomMargin: 10
            width: parent.width - 24; elide: Text.ElideRight
            font.pixelSize: 9; font.bold: false; color: "#7a7f88"
            text: wheel.tool !== "" && wheel.info && wheel.info.target ? "on " + wheel.info.target : ""
        }
    }



    // ---- by hand: which slice is under the mouse, by angle round the centre
    property int hot: -1                    // the slice under the mouse (a category, or a tile when one is open)
    function sliceAt(x, y) {
        const dx = x - c, dy = y - c, r = Math.sqrt(dx * dx + dy * dy);
        if (r < rCore) return -2;           // the core
        if (r > rRing) return -1;
        const n = openGroup >= 0 ? tiles.length : groups.length;
        const a = Math.atan2(dy, dx) * 180 / Math.PI + 90;
        return ((Math.round(a / (360 / n)) % n) + n) % n;
    }
    MouseArea {
        anchors.fill: parent
        enabled: wheel.manual && wheel.shown
        hoverEnabled: true
        cursorShape: wheel.hot !== -1 ? Qt.PointingHandCursor : Qt.ArrowCursor
        onPositionChanged: (m) => wheel.hot = wheel.sliceAt(m.x, m.y)
        onExited: wheel.hot = -1
        onPressed: (m) => { if (wheel.sliceAt(m.x, m.y) === -1) m.accepted = false }   // off the disc: the viewport's
        onClicked: (m) => {
            const k = wheel.sliceAt(m.x, m.y);
            if (k === -2) { wheel.manual = false; return; }
            if (k < 0) return;
            if (wheel.openGroup < 0) { wheel.litGroup = k; wheel.openGroup = k; wheel.hot = -1; return; }
            const t = wheel.tiles[k];
            if (t[0] === "__back") { wheel.openGroup = -1; wheel.litGroup = -1; wheel.tool = ""; wheel.hot = -1; return; }
            wheel.tool = t[0];
            wheel.picked(t[0], t[1]);
        }
    }
}
