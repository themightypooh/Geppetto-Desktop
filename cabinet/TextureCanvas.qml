// TEXTURE CANVAS (2D animation suite, lane 7) — flat-map paint canvas UI ONLY, standalone.
// It is not wired into cabinet.qml yet (deferred wiring, see the note at
// the bottom). A future host patch instantiates it where a 2D pane belongs
// and sets bodyName + baseSource + commandSink from the open document;
// until then it runs on whatever it is given, and shows an empty state
// when it has no body.
//
// WHAT IT PAINTS THROUGH (read before changing the commit path):
//   ServeMode "texpaint" (ServeMode.cs:1046) is the durable commit. It
//   builds an Effigy.TexelStroke from the command args and calls
//   PaintFeature.AddTexelStroke (kernel/Effigy/Features/PaintFeature.cs:273),
//   which bumps PaintFeature.Revision and rebuilds the canvas from the
//   stroke log. That is the ONLY durable path.
//   THE TRAP, stated in TexturePaintService.cs header and ServeMode.cs:1046:
//   scrubbing body.Mesh.Paint texels directly is PREVIEW-ONLY — the next
//   PaintFeature rebuild replays the stroke log and wipes it. This file
//   never touches a mesh canvas. It draws dabs on its own preview canvas
//   and, on stroke release, commits through the texpaint command string.
//   The preview dabs stay local until the host refreshes baseSource from
//   the fresh snapshot (see markCommittedRefreshed()).
//
// COMMAND SHAPE (must match ServeMode's parser exactly, ServeMode.cs:1061-1140):
//   texpaint <Body> path=X,Y;X,Y [channel=color|rough|metal|height|mask]
//     [color=R,G,B] [radius=12] [strength=0..1]
//     [falloff=smooth|linear|sharp|constant] [spacing=0.5] [erase=0|1]
//   Path points are TEXEL coordinates in the layer's canvas, row 0 at the
//   top — the same orientation as this canvas (y grows downward), so no
//   vertical flip is applied. Command coordinates are rounded to whole
//   texels; the preview keeps the fractional position.
//   LIMIT, honest: TexelStrokePoint is (x, y) only — the texpaint verb has
//   no per-point pressure argument, so stylus pressure modulates the
//   PREVIEW dab radius only. The committed stroke uses the uniform
//   radius/strength above. Per-point pressure in the verb is a ServeMode
//   change for a later lane, not something this file invents.
//
// LAYERS: paint lives as a layer type inside a drawing. layers[] entries
//   are { name, source, visible, opacity, isPaint }. The active paint
//   layer is the commit target for the preview; the server-side target is
//   the body's Paint layer (ServeMode finds it by body, and fails loudly
//   if the body has none — "add Paint bodies=<name>" first).
// ONION SKIN: onionPrevSource / onionNextSource are image URLs the host
//   sets to the adjacent frame render or the adjacent layer render. They
//   draw tinted at low opacity UNDER the paint layer so colour consistency
//   across frames/layers can be judged. Colours are NOT universal (Harmony
//   red=prev/green=next vs Animation Desk green=prev/red=next), so both
//   onionPrevColor and onionNextColor are configurable properties.
// DIRTY RECT: tracked in texels and reported on commit (dirtyRect), for
//   the host/service dirty-rect upload path. The preview canvas itself
//   repaints its whole surface on requestPaint — dirtyRect is a reported
//   region, not a GPU scissor, and this file does not claim otherwise.
// BOUNDARIES: 2D canvas/UI only. No particle sims, no brush engines —
//   it does not touch Brush.Apply or any brush internals, it only sends
//   brush parameters the existing verb already accepts.
import QtQuick
import QtQuick.Controls as C

Item {
    id: canvas
    // ---- document binding (set by the host wiring patch) ----
    property string bodyName: ""
    property string baseSource: ""          // flat UV map image for the body
    property int textureWidth: 1024
    property int textureHeight: 1024
    property var commandSink: null          // object with cmd(string); cabinet passes host

    // ---- paint layers inside the drawing ----
    property var layers: []                 // { name, source, visible, opacity, isPaint }
    property int activeLayer: -1            // index into layers; -1 = paint on base

    // ---- brush (maps 1:1 onto texpaint args) ----
    property color brushColor: "#ff0000"
    property real brushRadius: 12           // texels; texpaint radius=
    property real brushStrength: 1          // 0..1; texpaint strength=
    property string brushFalloff: "smooth"   // smooth | linear | sharp | constant
    property real brushSpacing: 0.5         // fraction of radius; texpaint spacing=
    property bool brushErase: false
    property string channel: "color"         // color | rough | metal | height | mask

    // ---- onion skin (ghosts of adjacent paint) ----
    property bool onionEnabled: true
    property string onionPrevSource: ""
    property string onionNextSource: ""
    property color onionPrevColor: "#ff0000"
    property color onionNextColor: "#00ff00"
    property real onionOpacity: 0.30
    property real onionTint: 0.5            // how far the ghost is pushed toward its onion colour

    // ---- state ----
    property string status: bodyName === "" ? "no body: set bodyName to paint" : "ready"
    property var dirtyRect: ({ x: 0, y: 0, w: 0, h: 0 })
    property var committedStrokes: []       // strokes committed, awaiting snapshot refresh
    property bool painting: false
    property var currentStroke: []          // { x, y, pressure } in TEXELS
    property var lastCursor: ({ x: 0, y: 0, inside: false })

    signal strokeCommitted(string command, var stroke)

    readonly property color steel: "#2b2f36"
    readonly property color steelHi: "#3d424b"
    readonly property color steelLo: "#1f2228"
    readonly property color edge: "#0b0c0e"
    readonly property color lit: "#f2a91c"
    readonly property color text: "#c9ccd0"
    readonly property color dim: "#7a7f88"
    readonly property string mono: "DejaVu Sans Mono"

    width: 480
    height: header.height + toolbar.height + paintStack.height + layerRow.height + footer.height + 40

    // ---- helpers: canvas <-> texel mapping ----
    function texelFromCanvas(cx, cy) {
        return { x: cx / paintCanvas.width * textureWidth,
                 y: cy / paintCanvas.height * textureHeight }
    }
    function canvasFromTexel(tx, ty) {
        return { x: tx / textureWidth * paintCanvas.width,
                 y: ty / textureHeight * paintCanvas.height }
    }
    function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)) }
    function validChannel(c) {
        return ["color", "rough", "metal", "height", "mask"].indexOf(c) >= 0
    }
    function validFalloff(f) {
        return ["smooth", "linear", "sharp", "constant"].indexOf(f) >= 0
    }
    function colorToRgb(c) {
        // texpaint color= is three 0..1 floats, not a hex string.
        return c.r.toFixed(3) + "," + c.g.toFixed(3) + "," + c.b.toFixed(3)
    }

    // ---- stroke capture ----
    function beginStroke(cx, cy, pressure) {
        if (painting) return
        var t = texelFromCanvas(cx, cy)
        painting = true
        currentStroke = [{ x: t.x, y: t.y, pressure: pressure > 0 ? pressure : 1 }]
        expandDirty(t.x, t.y)
        paintCanvas.requestPaint()
    }
    function addPoint(cx, cy, pressure) {
        if (!painting) return
        var t = texelFromCanvas(cx, cy)
        currentStroke.push({ x: t.x, y: t.y, pressure: pressure > 0 ? pressure : 1 })
        expandDirty(t.x, t.y)
        paintCanvas.requestPaint()
    }
    function expandDirty(tx, ty) {
        var r = brushRadius
        var x0 = Math.floor(tx - r), y0 = Math.floor(ty - r)
        var x1 = Math.ceil(tx + r), y1 = Math.ceil(ty + r)
        if (dirtyRect.w === 0 && dirtyRect.h === 0) {
            dirtyRect = { x: x0, y: y0, w: x1 - x0, h: y1 - y0 }
        } else {
            var nx = Math.min(dirtyRect.x, x0), ny = Math.min(dirtyRect.y, y0)
            var nx1 = Math.max(dirtyRect.x + dirtyRect.w, x1)
            var ny1 = Math.max(dirtyRect.y + dirtyRect.h, y1)
            dirtyRect = { x: nx, y: ny, w: nx1 - nx, h: ny1 - ny }
        }
    }

    // ---- commit: preview -> durable stroke log via texpaint ----
    function buildCommand(points) {
        var path = points.map(function (p) {
            return Math.round(p.x) + "," + Math.round(p.y)
        }).join(";")
        var cmd = "texpaint " + bodyName + " path=" + path
        cmd += " channel=" + (validChannel(channel) ? channel : "color")
        cmd += " color=" + colorToRgb(brushColor)
        cmd += " radius=" + brushRadius
        cmd += " strength=" + clamp(brushStrength, 0, 1)
        cmd += " falloff=" + (validFalloff(brushFalloff) ? brushFalloff : "smooth")
        cmd += " spacing=" + brushSpacing
        if (brushErase) cmd += " erase=1"
        return cmd
    }
    // ServeMode parses the whole path in one command; very long strokes
    // are split into sequential commits (same visual result, each its own
    // log entry) instead of one unbounded command line.
    function buildCommands(points) {
        var out = []
        var maxPoints = 2000
        for (var i = 0; i < points.length; i += maxPoints)
            out.push(buildCommand(points.slice(i, i + maxPoints)))
        return out
    }
    function endStroke() {
        if (!painting) return false
        painting = false
        var ok = commitStroke()
        paintCanvas.requestPaint()
        return ok
    }
    function commitStroke() {
        if (bodyName === "") { status = "cannot commit: no bodyName set"; return false }
        if (currentStroke.length === 0) { status = "nothing to commit"; return false }
        var stroke = { points: currentStroke.slice(0), channel: channel,
                       color: brushColor, radius: brushRadius,
                       strength: brushStrength, falloff: brushFalloff,
                       spacing: brushSpacing, erase: brushErase,
                       dirtyRect: dirtyRect }
        var commands = buildCommands(currentStroke)
        for (var i = 0; i < commands.length; i++) {
            strokeCommitted(commands[i], stroke)
            if (commandSink && typeof commandSink.cmd === "function")
                commandSink.cmd(commands[i])
        }
        committedStrokes.push(stroke)
        currentStroke = []
        status = "committed " + stroke.points.length + " point(s) in "
            + commands.length + " texpaint command(s); awaiting snapshot refresh"
        return true
    }
    // The host calls this after it refreshes baseSource from the fresh
    // snapshot (ServeMode swaps the mesh on texpaint). Until then the
    // committed dabs stay drawn locally so the painter sees their stroke.
    // It does NOT undo anything server-side: the stroke log keeps the
    // stroke, which is exactly why this path is durable.
    function markCommittedRefreshed() {
        committedStrokes = []
        dirtyRect = { x: 0, y: 0, w: 0, h: 0 }
        status = "refreshed from snapshot"
        paintCanvas.requestPaint()
    }
    function clearPreview() {
        currentStroke = []
        painting = false
        paintCanvas.requestPaint()
    }

    // ---- layers ----
    function addPaintLayer(name, source) {
        layers.push({ name: name, source: source || "", visible: true,
                      opacity: 1, isPaint: true })
        layers = layers.slice(0)   // notify: plain array push does not
        activeLayer = layers.length - 1
        return activeLayer
    }
    function setLayerVisible(i, v) {
        if (i < 0 || i >= layers.length) return
        var l = layers[i]
        layers[i] = { name: l.name, source: l.source, visible: v,
                      opacity: l.opacity, isPaint: l.isPaint }
        layers = layers.slice(0)
    }
    function activeLayerName() {
        if (activeLayer >= 0 && activeLayer < layers.length) return layers[activeLayer].name
        return "(base)"
    }

    Rectangle { anchors.fill: parent; color: steelLo; border.color: edge; border.width: 1; radius: 4 }

    Column {
        id: body
        width: parent.width - 16; x: 8; y: 8; spacing: 8

        Row {
            id: header
            width: parent.width; spacing: 8
            Text { text: "TEXTURE CANVAS"; color: lit; font.family: mono; font.pixelSize: 14; font.bold: true }
            Text { text: bodyName === "" ? "(no body)" : bodyName; color: dim; font.family: mono; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter }
            Text { text: "layer: " + activeLayerName(); color: dim; font.family: mono; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter }
        }

        // ---- toolbar: every control maps onto a texpaint arg ----
        Grid {
            id: toolbar
            width: parent.width; columns: 4; columnSpacing: 8; rowSpacing: 4
            Text { text: "body"; color: dim; font.family: mono; font.pixelSize: 12 }
            C.TextField { width: 150; text: canvas.bodyName; placeholderText: "body name"
                onEditingFinished: canvas.bodyName = text }
            Text { text: "channel"; color: dim; font.family: mono; font.pixelSize: 12 }
            C.ComboBox { width: 120; model: ["color", "rough", "metal", "height", "mask"]
                currentIndex: ["color", "rough", "metal", "height", "mask"].indexOf(canvas.channel)
                onActivated: i => canvas.channel = model[i] }
            Text { text: "colour"; color: dim; font.family: mono; font.pixelSize: 12 }
            C.TextField { width: 150; text: "#ff0000"; placeholderText: "#rrggbb"
                onEditingFinished: { if (/^#[0-9a-fA-F]{6}$/.test(text)) canvas.brushColor = text } }
            Text { text: "falloff"; color: dim; font.family: mono; font.pixelSize: 12 }
            C.ComboBox { width: 120; model: ["smooth", "linear", "sharp", "constant"]
                currentIndex: ["smooth", "linear", "sharp", "constant"].indexOf(canvas.brushFalloff)
                onActivated: i => canvas.brushFalloff = model[i] }
            Text { text: "radius " + Math.round(canvas.brushRadius) + "px"; color: dim; font.family: mono; font.pixelSize: 12 }
            C.Slider { width: 150; from: 1; to: 128; value: canvas.brushRadius
                onMoved: canvas.brushRadius = value }
            Text { text: "strength " + canvas.brushStrength.toFixed(2); color: dim; font.family: mono; font.pixelSize: 12 }
            C.Slider { width: 120; from: 0; to: 1; value: canvas.brushStrength
                onMoved: canvas.brushStrength = value }
            C.CheckBox { text: "erase"; checked: canvas.brushErase
                onCheckedChanged: canvas.brushErase = checked }
            C.CheckBox { text: "onion skin"; checked: canvas.onionEnabled
                onCheckedChanged: canvas.onionEnabled = checked }
            Text { text: "onion " + canvas.onionOpacity.toFixed(2); color: dim; font.family: mono; font.pixelSize: 12 }
            C.Slider { width: 120; from: 0; to: 1; value: canvas.onionOpacity
                onMoved: canvas.onionOpacity = value }
        }

        // ---- paint stack: checker, flat map, onion ghosts, layers, dabs ----
        Item {
            id: paintStack
            width: parent.width
            height: parent.width * textureHeight / textureWidth

            Canvas {
                id: checker
                anchors.fill: parent
                onPaint: {
                    var ctx = getContext("2d"); ctx.clearRect(0, 0, width, height)
                    var s = 16
                    for (var y = 0; y < height / s; y++) for (var x = 0; x < width / s; x++) {
                        ctx.fillStyle = (x + y) % 2 === 0 ? "#3a3f47" : "#24282e"
                        ctx.fillRect(x * s, y * s, s, s)
                    }
                }
                Component.onCompleted: requestPaint()
            }
            Image {
                id: baseImage
                anchors.fill: parent; source: canvas.baseSource
                fillMode: Image.Stretch; smooth: false; asynchronous: true; cache: false
            }
            // Ghost canvases paint on their own transparent surface, so the
            // source-atop tint touches only ghost pixels, never the flat map.
            Canvas {
                id: ghostPrev
                anchors.fill: parent; opacity: canvas.onionOpacity
                visible: canvas.onionEnabled && canvas.onionPrevSource !== ""
                onPaint: {
                    var ctx = getContext("2d"); ctx.clearRect(0, 0, width, height)
                    if (prevImg.status === Image.Ready) {
                        ctx.drawImage(prevImg, 0, 0, width, height)
                        ctx.globalCompositeOperation = "source-atop"
                        ctx.globalAlpha = canvas.onionTint
                        ctx.fillStyle = canvas.onionPrevColor
                        ctx.fillRect(0, 0, width, height)
                    }
                }
                onVisibleChanged: if (visible) requestPaint()
            }
            Canvas {
                id: ghostNext
                anchors.fill: parent; opacity: canvas.onionOpacity
                visible: canvas.onionEnabled && canvas.onionNextSource !== ""
                onPaint: {
                    var ctx = getContext("2d"); ctx.clearRect(0, 0, width, height)
                    if (nextImg.status === Image.Ready) {
                        ctx.drawImage(nextImg, 0, 0, width, height)
                        ctx.globalCompositeOperation = "source-atop"
                        ctx.globalAlpha = canvas.onionTint
                        ctx.fillStyle = canvas.onionNextColor
                        ctx.fillRect(0, 0, width, height)
                    }
                }
                onVisibleChanged: if (visible) requestPaint()
            }
            Image { id: prevImg; source: canvas.onionPrevSource; visible: false
                onStatusChanged: if (status === Image.Ready) ghostPrev.requestPaint() }
            Image { id: nextImg; source: canvas.onionNextSource; visible: false
                onStatusChanged: if (status === Image.Ready) ghostNext.requestPaint() }
            Repeater {
                model: canvas.layers
                Image {
                    required property var modelData
                    anchors.fill: parent
                    source: modelData.source; visible: modelData.visible
                    opacity: modelData.opacity
                    fillMode: Image.Stretch; smooth: false; asynchronous: true; cache: false
                }
            }
            Canvas {
                id: paintCanvas
                anchors.fill: parent
                onPaint: {
                    var ctx = getContext("2d"); ctx.clearRect(0, 0, width, height)
                    var sx = width / canvas.textureWidth
                    var sy = height / canvas.textureHeight
                    function drawStroke(st) {
                        if (st.points.length === 0) return
                        ctx.save()
                        if (st.erase) ctx.globalCompositeOperation = "destination-out"
                        for (var i = 0; i < st.points.length; i++) {
                            var p = st.points[i]
                            var cx = p.x * sx, cy = p.y * sy
                            // Pressure widens the preview dab only (see header:
                            // the verb has no per-point pressure argument).
                            var r = st.radius * sx * (p.pressure > 0 ? p.pressure : 1)
                            var g = ctx.createRadialGradient(cx, cy, 0, cx, cy, Math.max(r, 0.5))
                            var a = st.strength
                            if (st.falloff === "linear") {
                                g.addColorStop(0, Qt.rgba(st.color.r, st.color.g, st.color.b, a))
                                g.addColorStop(1, Qt.rgba(st.color.r, st.color.g, st.color.b, 0))
                            } else if (st.falloff === "sharp") {
                                g.addColorStop(0, Qt.rgba(st.color.r, st.color.g, st.color.b, a))
                                g.addColorStop(0.8, Qt.rgba(st.color.r, st.color.g, st.color.b, a))
                                g.addColorStop(1, Qt.rgba(st.color.r, st.color.g, st.color.b, 0))
                            } else if (st.falloff === "constant") {
                                g.addColorStop(0, Qt.rgba(st.color.r, st.color.g, st.color.b, a))
                                g.addColorStop(1, Qt.rgba(st.color.r, st.color.g, st.color.b, a))
                            } else { // smooth
                                g.addColorStop(0, Qt.rgba(st.color.r, st.color.g, st.color.b, a))
                                g.addColorStop(0.5, Qt.rgba(st.color.r, st.color.g, st.color.b, a * 0.6))
                                g.addColorStop(1, Qt.rgba(st.color.r, st.color.g, st.color.b, 0))
                            }
                            ctx.fillStyle = g
                            ctx.beginPath(); ctx.arc(cx, cy, Math.max(r, 0.5), 0, Math.PI * 2); ctx.fill()
                        }
                        ctx.restore()
                    }
                    for (var s = 0; s < canvas.committedStrokes.length; s++) drawStroke(canvas.committedStrokes[s])
                    if (canvas.currentStroke.length > 0) drawStroke({
                        points: canvas.currentStroke, color: canvas.brushColor,
                        radius: canvas.brushRadius, strength: canvas.brushStrength,
                        falloff: canvas.brushFalloff, erase: canvas.brushErase })
                    if (canvas.lastCursor.inside) {
                        var cr = canvas.brushRadius * sx
                        ctx.strokeStyle = canvas.lit; ctx.lineWidth = 1
                        ctx.beginPath(); ctx.arc(canvas.lastCursor.x, canvas.lastCursor.y, Math.max(cr, 1), 0, Math.PI * 2); ctx.stroke()
                    }
                }
            }
            MouseArea {
                anchors.fill: parent
                hoverEnabled: true
                onPressed: m => { canvas.lastCursor = { x: m.x, y: m.y, inside: true }; canvas.beginStroke(m.x, m.y, 1) }
                onPositionChanged: m => {
                    canvas.lastCursor = { x: m.x, y: m.y, inside: true }
                    if (canvas.painting) canvas.addPoint(m.x, m.y, 1)
                    else paintCanvas.requestPaint()
                }
                onReleased: canvas.endStroke()
                onExited: { canvas.lastCursor = { x: 0, y: 0, inside: false }; paintCanvas.requestPaint() }
            }
        }

        // ---- layer list: paint as a layer type inside the drawing ----
        Row {
            id: layerRow
            width: parent.width; spacing: 6
            Text { text: "layers"; color: dim; font.family: mono; font.pixelSize: 12; anchors.verticalCenter: parent.verticalCenter }
            Repeater {
                model: canvas.layers
                Row {
                    required property var modelData
                    required property int index
                    spacing: 4
                    C.CheckBox { checked: modelData.visible
                        onCheckedChanged: canvas.setLayerVisible(index, checked) }
                    C.Button { text: modelData.name
                        highlighted: canvas.activeLayer === index
                        onClicked: canvas.activeLayer = index }
                }
            }
            C.Button { text: "+ paint layer"
                onClicked: canvas.addPaintLayer("paint " + (canvas.layers.length + 1), "") }
            C.Button { text: "commit"
                enabled: canvas.currentStroke.length > 0
                onClicked: canvas.commitStroke() }
        }

        Column {
            id: footer
            width: parent.width; spacing: 2
            Text { text: canvas.status; color: canvas.text; font.family: mono; font.pixelSize: 12; wrapMode: Text.Wrap }
            Text { text: "dirty texels x=" + canvas.dirtyRect.x + " y=" + canvas.dirtyRect.y
                + " w=" + canvas.dirtyRect.w + " h=" + canvas.dirtyRect.h
                + "  ·  texture " + canvas.textureWidth + "×" + canvas.textureHeight
                + "  ·  onion prev " + canvas.onionPrevColor + " / next " + canvas.onionNextColor
                font.family: mono; font.pixelSize: 11; color: dim; wrapMode: Text.Wrap }
        }
    }
}

// DEFERRED WIRING (do not hand-edit cabinet.qml mid-race; report this patch):
//   In cabinet.qml, instantiate TextureCanvas where a 2D pane belongs and
//   set: bodyName = the open body's name, baseSource = the flat UV map
//   image for that body, commandSink = host (host.cmd forwards the line to
//   the desktop process stdin, where ServeMode's existing texpaint case
//   commits it — no new verb, no host.py run-control change, no notice
//   change). On the texpaint event, refresh baseSource from the fresh
//   snapshot and call markCommittedRefreshed(). Onion sources come from
//   the adjacent frame render / adjacent layer render. No Program.cs
//   dispatch line is owed by this file: texpaint already exists in
//   ServeMode. Until that patch lands this file is a standalone canvas
//   component, not a live cabinet pane — and a headless dab is NOT proof
//   of painting; the eye check happens when it is wired into a pane.
