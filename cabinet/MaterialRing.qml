// The material browser, after Linework's colour ring: it opens round the tool wheel's centre and fans
// up and to the left over the viewport. The outer ring is the marker shades (every hue as columns of
// stacked shades, darkest inside, greys round the inside; scroll or drag to spin it), or three arc
// sliders (HSL, or FINISH: rough, metal, glow), or the shipped PRESETS. The steel disc carries the
// mode tabs, the model's material slots as numbered chips, and the hex code; the core is the slot
// you're on, click it to put the ring away.
// Every pick is a `material SLOT ...` line run on the bench (host.benchApply), so the viewport shows
// the real look the moment you let go, and UNDO steps back. Nothing here touches a run: the bench
// works on a copy (see host.py, "the bench").
import QtQuick

Item {
    id: mr
    property bool open: false
    property var bench: ({})
    property real cx: 0                    // the wheel's centre, in this item's coordinates
    property real cy: 0
    property real k: 1                     // scale of the ring
    signal apply(string line)
    signal undo()
    signal closed()

    visible: open
    opacity: open ? 1 : 0
    Behavior on opacity { NumberAnimation { duration: 160 } }

    readonly property color steel: "#2b2f36"
    readonly property color steelHi: "#3d424b"
    readonly property color steelLo: "#1f2228"
    readonly property color edge: "#0b0c0e"
    readonly property color ink: "#141418"
    readonly property color paper: "#efeee6"
    readonly property color lit: "#f2a91c"
    readonly property color text: "#c9ccd0"
    readonly property color dim: "#7a7f88"
    readonly property color bad: "#e0604a"
    readonly property string mono: "DejaVu Sans Mono"

    // ring geometry, px from the centre (before k), as Linework's ColorRing
    readonly property real rDisc0: 104
    readonly property real rDisc1: 182
    readonly property real rGrey0: 184
    readonly property real rGrey1: 214
    readonly property real rPet0: 222
    readonly property real cell: 27
    readonly property real gap: 1.4
    readonly property real extent: rPet0 + 9 * cell + 8
    // the arc sliders sweep the upper-left quarter: value 0 at the top, 1 at the left
    readonly property real a0: -80
    readonly property real a1: -192
    readonly property var bands: [[184, 214], [222, 252], [260, 290]]

    property string mode: "shades"         // shades, hsl, finish, presets
    onModeChanged: { arcs.requestPaint(); presetsCanvas.requestPaint(); hit.hover = -1; hit.hoverPreset = -1 }

    // ---- the model's slots, and the one you're on ----
    readonly property var slots: bench.slots || []
    readonly property var presets: bench.presets || []
    property int slot: -1
    function slotInfo(n) { return slots.find(s => s.slot === n) || null }
    readonly property var cur: slotInfo(slot)
    onSlotsChanged: {
        if (!slots.length) return;
        if (!cur) slot = (slots.find(s => s.bodies.length) || slots[0]).slot;
        syncLook();
    }
    onSlotChanged: syncLook()
    onOpenChanged: if (open) { syncLook(); if (mode === "shades") spinTo(look.color) }

    // the look on screen: the slot's, or what you're dragging before you let go
    property var look: ({ color: "#9e9991", rough: 0.5, metal: 0, emit: "" })
    property real hh: 0                    // hue kept apart, so it survives greys
    property real ss: 0
    property real ll: 0.6
    function syncLook() {
        const c = cur;
        if (!c) return;
        look = { color: c.color, rough: c.rough, metal: c.metal, emit: c.emit || "" };
        const q = Qt.color(c.color);
        if (q.hslHue >= 0 && q.hslSaturation > 0) hh = q.hslHue;
        ss = q.hslSaturation; ll = q.hslLightness;
        arcs.requestPaint(); marks.requestPaint(); presetsCanvas.requestPaint();
    }
    function setLook(o) { look = Object.assign({}, look, o); arcs.requestPaint(); marks.requestPaint(); presetsCanvas.requestPaint() }
    function hex(c) { return Qt.color(c).toString().slice(0, 7) }
    function num(v) { return (Math.round(v * 1000) / 1000).toString() }
    // the glow, as a fraction of the slot's colour
    function glowOf(l) {
        if (!l.emit) return 0;
        const e = Qt.color(l.emit), c = Qt.color(l.color);
        const m = Math.max(c.r, c.g, c.b);
        return m > 0 ? Math.min(1, Math.max(e.r, e.g, e.b) / m) : 0;
    }
    function glowHex(c, v) { const q = Qt.color(c); return hex(Qt.rgba(q.r * v, q.g * v, q.b * v, 1)) }

    function send(args) { if (slot >= 0) apply("material " + slot + " " + args) }
    // faces picked in the viewport (host.subSelection, kind "faces") can be put on a slot: right-click a
    // chip, or the → SEL button on a card row. host.assignFacesToSlot sends `facematerial slot=N faces=…`.
    readonly property bool facesPicked: typeof host !== "undefined" && !!host && !!host.subSelection
        && host.subSelection.kind === "faces" && typeof host.assignFacesToSlot === "function"
    function facesToSlot(n) { if (facesPicked) host.assignFacesToSlot(n) }
    function pickColor(c) {
        const g = glowOf(look);
        setLook({ color: hex(c), emit: g > 0 ? glowHex(c, g) : "" });
        send("color=" + hex(c) + (g > 0 ? " emit=" + glowHex(c, g) : ""));
    }

    // ---- maths ----
    function polar(r, deg) { const a = deg * Math.PI / 180; return Qt.point(Math.cos(a) * r, Math.sin(a) * r) }
    function norm360(a) { a = a % 360; return a < 0 ? a + 360 : a }
    // OKLCH -> sRGB, chroma pulled in until it fits the gamut, so every step is evenly light
    function oklch(L, C, hDeg) {
        function conv(c) {
            const h = hDeg * Math.PI / 180, a = c * Math.cos(h), b = c * Math.sin(h);
            const l_ = L + 0.3963377774 * a + 0.2158037573 * b, m_ = L - 0.1055613458 * a - 0.0638541728 * b, s_ = L - 0.0894841775 * a - 1.2914855480 * b;
            const l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
            const o = [4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                       -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                       -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s];
            o.ok = o.every(x => x >= -1e-4 && x <= 1.0001);
            return o;
        }
        let rgb = conv(C);
        if (!rgb.ok) {
            let lo = 0, hi = C;
            for (let i = 0; i < 20; i++) { const mid = (lo + hi) / 2; if (conv(mid).ok) lo = mid; else hi = mid; }
            rgb = conv(lo);
        }
        const gam = x => { x = Math.min(1, Math.max(0, x)); return x <= 0.0031308 ? 12.92 * x : 1.055 * Math.pow(x, 1 / 2.4) - 0.055 };
        const h2 = v => ("0" + Math.round(gam(v) * 255).toString(16)).slice(-2);
        return "#" + h2(rgb[0]) + h2(rgb[1]) + h2(rgb[2]);
    }
    function lightness(c) { return Qt.color(c).hslLightness }

    // families of shades round the circle: three columns each (vivid, softer, greyed), light outside,
    // dark inside, named like markers (B05, YR17 ...); a grey under every column
    readonly property var cells: {
        const fams = [["R", 25, .21], ["YR", 52, .19], ["Y", 98, .18], ["YG", 124, .18], ["G", 150, .17],
                      ["BG", 182, .14], ["B", 236, .16], ["BV", 268, .16], ["V", 305, .18], ["RV", 345, .20], ["E", 58, .075]];
        const chromaK = [1, .58, .3];
        const codes = [["9", "7", "5", "4", "3", "2", "1", "0", "00"], ["9", "7", "5", "3", "1", "0", "00"], ["9", "6", "3", "1", "0"]];
        const Ldark = [.34, .42, .50], Llight = [.965, .95, .93];
        const nf = fams.length, cols = nf * 3, step = (360 - nf * gap) / cols;
        const out = [];
        for (let f = 0; f < nf; f++) for (let g = 0; g < 3; g++) {
            const kk = f * 3 + g, b0 = f * (3 * step + gap) + g * step, b1 = b0 + step;
            const gf = Math.floor(kk / 11), j = kk % 11, L = .965 - j * .074;
            out.push({ a0: b0, a1: b1, r0: rGrey0, r1: rGrey1, label: ["N", "W", "C"][gf] + j,
                       c: gf === 0 ? oklch(L, 0, 0) : gf === 1 ? oklch(L, .014, 70) : oklch(L, .016, 250) });
            const cs = codes[g], n = cs.length;
            for (let s = 0; s < n; s++) {
                const t = s / (n - 1);
                const Ls = Ldark[g] + (Llight[g] - Ldark[g]) * t;
                const Cs = fams[f][2] * chromaK[g] * (1 - Math.pow(Math.abs(t - .45) / .6, 2) * .55);
                const r0 = rPet0 + s * cell;
                out.push({ a0: b0, a1: b1, r0: r0, r1: r0 + cell, c: oklch(Ls, Math.max(0, Cs), fams[f][1]), label: fams[f][0] + g + cs[s] });
            }
        }
        return out;
    }
    readonly property real colStep: (360 - 11 * gap) / 33

    // ---- spinning ----
    property real spin: 0
    property real spinTarget: 0
    property real velocity: 0
    Timer {
        id: spinner
        interval: 16; repeat: true
        onTriggered: {
            if (!hit.down && Math.abs(mr.velocity) > .02) { mr.spinTarget += mr.velocity * 16; mr.velocity *= .9 }
            const d = mr.spinTarget - mr.spin;
            if (Math.abs(d) < .05 && Math.abs(mr.velocity) <= .02) { mr.spin = mr.spinTarget; stop(); return }
            mr.spin += d * .3;
        }
    }
    // bring the nearest swatch round to the upper left
    function spinTo(c) {
        const w = Qt.color(c);
        let best = -1, bd = 1e9;
        for (let i = 0; i < cells.length; i++) {
            const x = Qt.color(cells[i].c);
            const d = (x.r - w.r) ** 2 + (x.g - w.g) ** 2 + (x.b - w.b) ** 2;
            if (d < bd) { bd = d; best = i }
        }
        if (best < 0) return;
        let target = -135 - (cells[best].a0 + cells[best].a1) / 2;
        while (target - spin > 180) target -= 360;
        while (target - spin < -180) target += 360;
        spinTarget = target; spinner.start();
    }

    // ---- what's under a point (in dial coordinates, centre at 0,0) ----
    function angleOf(p) { return Math.atan2(p.y, p.x) * 180 / Math.PI }
    // the slider sweep runs over -180, so angles are read on -270..90
    function sweepAngle(p) { let a = angleOf(p); if (a > 90) a -= 360; return a }
    function cellAt(p) {
        const r = Math.hypot(p.x, p.y);
        if (r < rGrey0) return -1;
        const a = norm360(angleOf(p) - spin);
        for (let i = 0; i < cells.length; i++) {
            const c = cells[i];
            if (r >= c.r0 && r < c.r1 && a >= c.a0 && a < c.a1) return i;
        }
        return -1;
    }
    function sliderAt(p) {
        const r = Math.hypot(p.x, p.y), a = sweepAngle(p);
        if (a > a0 + 14 || a < a1 - 14) return -1;
        for (let i = 0; i < 3; i++) if (r >= bands[i][0] - 3 && r <= bands[i][1] + 3) return i;
        return -1;
    }
    // presets fan over the slider sweep, one wedge each
    readonly property real presetSweep: presets.length ? (a0 - a1) / presets.length : 1
    function presetAt(p) {
        const r = Math.hypot(p.x, p.y), a = sweepAngle(p);
        if (r < rGrey0 || r > 300 || a > a0 || a < a1) return -1;
        return Math.min(presets.length - 1, Math.floor((a0 - a) / presetSweep));
    }
    function sliderValue(i) {
        if (mode === "hsl") return i === 0 ? hh : i === 1 ? ss : ll;
        return i === 0 ? look.rough : i === 1 ? look.metal : glowOf(look);
    }
    function setSlider(i, v, final) {
        v = Math.min(1, Math.max(0, v));
        if (mode === "hsl") {
            if (i === 0) hh = Math.min(v, .9999); else if (i === 1) ss = v; else ll = v;
            const c = hex(Qt.hsla(hh, ss, ll, 1));
            if (final) pickColor(c); else setLook({ color: c });
            return;
        }
        v = Math.round(v * 100) / 100;
        if (i === 0) { setLook({ rough: v }); if (final) send("rough=" + num(v)) }
        else if (i === 1) { setLook({ metal: v }); if (final) send("metal=" + num(v)) }
        else { const e = v > 0 ? glowHex(look.color, v) : ""; setLook({ emit: e }); if (final) send("emit=" + (e || "none")) }
    }

    // ---- the dial ----
    Item {
        id: dial
        width: 2 * mr.extent; height: 2 * mr.extent
        x: mr.cx - mr.extent; y: mr.cy - mr.extent
        scale: mr.k
        // only the disc, the ring under the pointer and the card take the mouse: the rest of the
        // viewport still orbits
        containmentMask: QtObject {
            function contains(point: point): bool {
                const p = Qt.point(point.x - mr.extent, point.y - mr.extent), r = Math.hypot(p.x, p.y);
                if (hit.down) return true;
                if (r <= mr.rDisc1 + 2) return true;
                return mr.mode === "shades" ? mr.cellAt(p) >= 0 : mr.mode === "presets" ? mr.presetAt(p) >= 0 : mr.sliderAt(p) >= 0;
            }
        }

        // the steel disc and its shadow
        Canvas {
            id: disc
            anchors.fill: parent
            onPaint: {
                const g = getContext("2d"), E = mr.extent;
                g.reset();
                g.translate(E, E);
                const ring = (dx, dy, fill) => {
                    g.beginPath(); g.arc(dx, dy, mr.rDisc1, 0, 2 * Math.PI); g.arc(dx, dy, mr.rDisc0, 2 * Math.PI, 0, true);
                    g.fillStyle = fill; g.fill();
                };
                ring(6, 6, "rgba(0,0,0,0.45)");
                ring(0, 0, "#8f949a");
                // a fine dither, as Linework's disc
                g.save();
                g.beginPath(); g.arc(0, 0, mr.rDisc1, 0, 2 * Math.PI); g.arc(0, 0, mr.rDisc0, 2 * Math.PI, 0, true); g.clip();
                g.fillStyle = "rgba(20,20,24,0.10)";
                for (let y = -mr.rDisc1; y < mr.rDisc1; y += 2) for (let x = -mr.rDisc1 + (y / 2 & 1); x < mr.rDisc1; x += 2) g.fillRect(x, y, 1, 1);
                g.restore();
                // light from the upper left
                g.lineWidth = 3;
                g.strokeStyle = "#f4f5f6"; g.beginPath(); g.arc(0, 0, mr.rDisc1 - 3.5, Math.PI * 0.75, Math.PI * 1.75); g.stroke();
                g.strokeStyle = "#42454b"; g.beginPath(); g.arc(0, 0, mr.rDisc1 - 3.5, Math.PI * 1.75, Math.PI * 2.75); g.stroke();
                g.lineWidth = 2; g.strokeStyle = mr.ink;
                g.beginPath(); g.arc(0, 0, mr.rDisc1, 0, 2 * Math.PI); g.stroke();
                g.beginPath(); g.arc(0, 0, mr.rDisc0, 0, 2 * Math.PI); g.stroke();
            }
        }

        // SHADES: the swatches, painted once, turned as the ring spins
        Item {
            id: turn
            anchors.fill: parent
            visible: mr.mode === "shades"
            rotation: mr.spin
            function sector(g, r0, r1, b0, b1) {
                const A = b0 * Math.PI / 180, B = b1 * Math.PI / 180;
                g.beginPath(); g.arc(0, 0, r1, A, B); g.arc(0, 0, r0, B, A, true); g.closePath();
            }
            Canvas {
                id: swatches
                anchors.fill: parent
                renderStrategy: Canvas.Cooperative
                onPaint: {
                    const g = getContext("2d"), E = mr.extent;
                    g.reset(); g.translate(E, E);
                    g.save(); g.translate(5, 5);
                    g.fillStyle = "rgba(0,0,0,0.45)";
                    for (const c of mr.cells) { turn.sector(g, c.r0, c.r1, c.a0, c.a1); g.fill() }
                    g.restore();
                    g.lineWidth = 1.2; g.strokeStyle = mr.ink;
                    g.font = "bold 9px '" + mr.mono + "'"; g.textAlign = "center"; g.textBaseline = "middle";
                    for (const c of mr.cells) {
                        turn.sector(g, c.r0, c.r1, c.a0, c.a1);
                        g.fillStyle = c.c; g.fill(); g.stroke();
                        const am = (c.a0 + c.a1) / 2, rm = (c.r0 + c.r1) / 2;
                        const room = (c.a1 - c.a0) * Math.PI / 180 * c.r0 - 4;
                        if (g.measureText(c.label).width > room) continue;
                        g.save(); g.rotate(am * Math.PI / 180); g.translate(rm, 0); g.rotate(Math.PI / 2);
                        g.fillStyle = mr.lightness(c.c) > .6 ? mr.ink : "#f4f5f6";
                        g.fillText(c.label, 0, 0);
                        g.restore();
                    }
                }
            }
            // the slot's colour gets a star, the swatch under the pointer an amber frame
            Canvas {
                id: marks
                anchors.fill: parent
                onPaint: {
                    const g = getContext("2d"), E = mr.extent;
                    g.reset(); g.translate(E, E);
                    const want = mr.hex(mr.look.color);
                    for (const c of mr.cells) {
                        if (c.c !== want) continue;
                        const am = (c.a0 + c.a1) / 2 * Math.PI / 180, rm = (c.r0 + c.r1) / 2;
                        const at = Qt.point(Math.cos(am) * rm, Math.sin(am) * rm);
                        g.beginPath();
                        for (let i = 0; i < 10; i++) {
                            const a = -Math.PI / 2 + i * Math.PI / 5, rr = i % 2 ? 7 * .45 : 7;
                            g.lineTo(at.x + Math.cos(a) * rr, at.y + Math.sin(a) * rr);
                        }
                        g.closePath();
                        g.fillStyle = mr.lightness(c.c) > .6 ? mr.ink : "#f4f5f6"; g.fill();
                        g.lineWidth = 1.5; g.strokeStyle = mr.ink; g.stroke();
                    }
                    if (hit.hover >= 0) {
                        const c = mr.cells[hit.hover];
                        turn.sector(g, c.r0, c.r1, c.a0, c.a1);
                        g.lineWidth = 5; g.strokeStyle = mr.ink; g.stroke();
                        g.lineWidth = 2.5; g.strokeStyle = mr.lit; g.stroke();
                    }
                }
            }
        }

        // HSL / FINISH: three arc sliders
        Canvas {
            id: arcs
            anchors.fill: parent
            onVisibleChanged: if (visible) requestPaint()
            visible: mr.mode === "hsl" || mr.mode === "finish"
            onPaint: {
                const g = getContext("2d"), E = mr.extent;
                g.reset(); g.translate(E, E);
                if (!visible) return;
                const hsl = mr.mode === "hsl";
                const names = hsl ? ["H", "S", "L"] : ["RUF", "MTL", "GLO"];
                const sector = (r0, r1, b0, b1) => {
                    const A = b0 * Math.PI / 180, B = b1 * Math.PI / 180;
                    g.beginPath(); g.arc(0, 0, r1, A, B, B < A); g.arc(0, 0, r0, B, A, !(B < A)); g.closePath();
                };
                const base = Qt.color(mr.look.color);
                for (let i = 0; i < 3; i++) {
                    const r0 = mr.bands[i][0], r1 = mr.bands[i][1];
                    g.save(); g.translate(5, 5); sector(r0, r1, mr.a0, mr.a1); g.fillStyle = "rgba(0,0,0,0.45)"; g.fill(); g.restore();
                    const n = 112;
                    for (let kk = 0; kk < n; kk++) {
                        const t = (kk + .5) / n;
                        let c;
                        if (hsl) c = i === 0 ? Qt.hsla(t, 1, .5, 1) : i === 1 ? Qt.hsla(mr.hh, t, mr.ll, 1) : Qt.hsla(mr.hh, mr.ss, t, 1);
                        else if (i === 0) c = Qt.hsla(0, 0, .85 - t * .55, 1);                        // glossy .. matte
                        else if (i === 1) c = Qt.rgba(.25 + t * .5, .25 + t * .52, .27 + t * .55, 1);    // dielectric .. metal
                        else c = Qt.rgba(base.r * t, base.g * t, base.b * t, 1);                     // dark .. full glow
                        const b0 = mr.a0 + (mr.a1 - mr.a0) * kk / n, b1 = mr.a0 + (mr.a1 - mr.a0) * (kk + 1) / n;
                        sector(r0, r1, b0, b1 - .3); g.fillStyle = String(c); g.fill();
                    }
                    g.lineWidth = 2; g.strokeStyle = mr.ink; sector(r0, r1, mr.a0, mr.a1); g.stroke();
                    // thumb
                    const v = mr.sliderValue(i), a = (mr.a0 + (mr.a1 - mr.a0) * v) * Math.PI / 180, rm = (r0 + r1) / 2;
                    const at = Qt.point(Math.cos(a) * rm, Math.sin(a) * rm);
                    g.fillStyle = mr.ink; g.fillRect(at.x - 9, at.y - 9, 18, 18);
                    g.fillStyle = "#f4f5f6"; g.fillRect(at.x - 7, at.y - 7, 14, 14);
                    g.fillStyle = hsl ? mr.look.color : String(i === 0 ? Qt.hsla(0, 0, .85 - v * .55, 1) : i === 1 ? Qt.rgba(.25 + v * .5, .25 + v * .52, .27 + v * .55, 1) : Qt.rgba(base.r * v, base.g * v, base.b * v, 1));
                    g.fillRect(at.x - 5, at.y - 5, 10, 10);
                    // name at the start, value at the end
                    g.font = "bold 11px '" + mr.mono + "'"; g.textAlign = "center"; g.textBaseline = "middle";
                    const label = (deg, s) => {
                        const ar = deg * Math.PI / 180, c = Qt.point(Math.cos(ar) * rm, Math.sin(ar) * rm);
                        g.fillStyle = mr.ink; g.fillRect(c.x - 20, c.y - 11, 40, 22);
                        g.fillStyle = mr.paper; g.fillRect(c.x - 18, c.y - 9, 36, 18);
                        g.fillStyle = mr.ink; g.fillText(s, c.x, c.y + 1);
                    };
                    label(mr.a0 + 10, names[i]);
                    label(mr.a1 - 10, String(hsl && i === 0 ? Math.round(v * 360) : Math.round(v * 100)));
                }
            }
        }

        // PRESETS: one wedge per shipped look, its colour, how rough and how metal
        Canvas {
            id: presetsCanvas
            anchors.fill: parent
            onVisibleChanged: if (visible) requestPaint()
            visible: mr.mode === "presets"
            onPaint: {
                const g = getContext("2d"), E = mr.extent;
                g.reset(); g.translate(E, E);
                if (!visible) return;
                const sector = (r0, r1, b0, b1) => {
                    const A = b0 * Math.PI / 180, B = b1 * Math.PI / 180;
                    g.beginPath(); g.arc(0, 0, r1, A, B, true); g.arc(0, 0, r0, B, A, false); g.closePath();
                };
                g.save(); g.translate(5, 5); sector(mr.rGrey0, 300, mr.a0, mr.a1); g.fillStyle = "rgba(0,0,0,0.45)"; g.fill(); g.restore();
                g.font = "bold 11px '" + mr.mono + "'"; g.textAlign = "left"; g.textBaseline = "middle";
                for (let i = 0; i < mr.presets.length; i++) {
                    const p = mr.presets[i], b0 = mr.a0 - i * mr.presetSweep, b1 = b0 - mr.presetSweep;
                    sector(mr.rGrey0, 300, b0, b1);
                    g.fillStyle = p.color; g.fill();
                    // a sheen band: narrow and bright when glossy, broad and faint when rough
                    const am = (b0 + b1) / 2;
                    const w = 1 - p.rough;
                    sector(284, 294, am + mr.presetSweep * (.1 + .35 * w), am - mr.presetSweep * (.1 + .35 * w));
                    g.fillStyle = p.metal > .5 ? "rgba(220,230,255," + (0.15 + 0.45 * (1 - p.rough)) + ")" : "rgba(255,255,255," + (0.08 + 0.4 * (1 - p.rough)) + ")"; g.fill();
                    if (p.emit) { sector(mr.rGrey0, mr.rGrey0 + 6, b0, b1); g.fillStyle = p.emit; g.fill() }
                    g.lineWidth = 1.5; g.strokeStyle = mr.ink; sector(mr.rGrey0, 300, b0, b1); g.stroke();
                    const on = mr.cur && mr.cur.preset === p.name;
                    if (on || hit.hoverPreset === i) {
                        sector(mr.rGrey0, 300, b0, b1);
                        g.lineWidth = 5; g.strokeStyle = mr.ink; g.stroke();
                        g.lineWidth = 2.5; g.strokeStyle = on ? mr.lit : "#f4f5f6"; g.stroke();
                    }
                    g.save(); g.rotate(am * Math.PI / 180); g.translate(mr.rGrey0 + 12, 0);
                    // text reads outward on the left half, so turn it the right way up
                    if (Math.abs(am) > 90 && Math.abs(am) < 270) { g.rotate(Math.PI); g.textAlign = "right" } else g.textAlign = "left";
                    g.fillStyle = mr.lightness(p.color) > .6 ? mr.ink : "#f4f5f6";
                    g.fillText(p.name.replace(/_/g, " ").toUpperCase(), 0, 0);
                    g.restore();
                }
            }
        }

        // the ring's mouse: click a swatch, drag or scroll to spin, drag a slider
        MouseArea {
            id: hit
            anchors.fill: parent
            hoverEnabled: true
            property bool down: false
            property bool dragging: false
            property int slider: -1
            property int hover: -1
            property int hoverPreset: -1
            property point pressAt
            property real lastAngle: 0
            onHoverChanged: marks.requestPaint()
            onHoverPresetChanged: presetsCanvas.requestPaint()
            function local(m) { return Qt.point(m.x - mr.extent, m.y - mr.extent) }
            cursorShape: hover >= 0 || hoverPreset >= 0 || slider >= 0 ? Qt.PointingHandCursor : Qt.ArrowCursor
            onPressed: m => {
                const p = local(m);
                if (Math.hypot(p.x, p.y) <= mr.rDisc1 + 2) { m.accepted = false; return }    // the disc's own controls
                down = true; dragging = false; pressAt = Qt.point(m.x, m.y); lastAngle = mr.angleOf(p); mr.velocity = 0;
                slider = mr.mode === "hsl" || mr.mode === "finish" ? mr.sliderAt(p) : -1;
                if (slider >= 0) mr.setSlider(slider, (mr.a0 - mr.sweepAngle(p)) / (mr.a0 - mr.a1), false);
            }
            onPositionChanged: m => {
                const p = local(m);
                if (!down) {
                    hover = mr.mode === "shades" ? mr.cellAt(p) : -1;
                    hoverPreset = mr.mode === "presets" ? mr.presetAt(p) : -1;
                    return;
                }
                if (slider >= 0) { mr.setSlider(slider, (mr.a0 - mr.sweepAngle(p)) / (mr.a0 - mr.a1), false); return }
                if (mr.mode !== "shades") return;
                if (!dragging && Math.hypot(m.x - pressAt.x, m.y - pressAt.y) > 5) dragging = true;
                if (!dragging) return;
                let a = mr.angleOf(p), d = a - lastAngle;
                while (d > 180) d -= 360;
                while (d < -180) d += 360;
                lastAngle = a;
                mr.spin += d; mr.spinTarget = mr.spin;
                mr.velocity = mr.velocity * .5 + d / 16 * .5;
                hover = -1;
            }
            onReleased: m => {
                const p = local(m);
                down = false;
                if (slider >= 0) { mr.setSlider(slider, (mr.a0 - mr.sweepAngle(p)) / (mr.a0 - mr.a1), true); slider = -1; return }
                if (dragging) { dragging = false; if (Math.abs(mr.velocity) > .05) spinner.start(); else mr.velocity = 0; return }
                if (mr.mode === "shades") { const i = mr.cellAt(p); if (i >= 0) mr.pickColor(mr.cells[i].c) }
                else if (mr.mode === "presets") {
                    const i = mr.presetAt(p);
                    if (i >= 0) { const q = mr.presets[i]; mr.setLook({ color: q.color, rough: q.rough, metal: q.metal, emit: q.emit }); mr.send("preset=" + q.name) }
                }
            }
            onExited: { hover = -1; hoverPreset = -1 }
            onWheel: w => {
                const n = (w.angleDelta.y || w.angleDelta.x) / 120;
                if (mr.mode === "shades") { mr.velocity = 0; mr.spinTarget += n * mr.colStep; spinner.start(); hover = -1; return }
                const i = mr.sliderAt(local(w));
                if (i >= 0) mr.setSlider(i, mr.sliderValue(i) + n * (mr.mode === "hsl" && i === 0 ? 1 / 120 : 1 / 100), true);
            }
        }

        // ---- on the disc ----
        // mode tabs and UNDO: tiles round the upper left of the disc
        Repeater {
            model: [
                { key: "shades", text: "SHADES", a0: -178, a1: -150 },
                { key: "hsl", text: "HSL", a0: -149, a1: -134 },
                { key: "finish", text: "FINISH", a0: -133, a1: -110 },
                { key: "presets", text: "PRESETS", a0: -109, a1: -82 },
                { key: "undo", text: "UNDO", a0: -81, a1: -62 },
            ]
            Item {
                id: tab
                required property var modelData
                readonly property bool on: mr.mode === modelData.key
                readonly property bool usable: modelData.key !== "undo" || (!!mr.bench.canUndo && !mr.bench.busy)
                readonly property real mid: (modelData.a0 + modelData.a1) / 2
                anchors.fill: parent
                Canvas {
                    anchors.fill: parent
                    property bool on: tab.on
                    property bool over: tabMa.containsMouse
                    property bool press: tabMa.pressed
                    property bool usable: tab.usable
                    onOnChanged: requestPaint(); onOverChanged: requestPaint(); onPressChanged: requestPaint(); onUsableChanged: requestPaint()
                    onPaint: {
                        const g = getContext("2d"), E = mr.extent;
                        g.reset(); g.translate(E, E);
                        const A = tab.modelData.a0 * Math.PI / 180, B = tab.modelData.a1 * Math.PI / 180;
                        g.beginPath(); g.arc(0, 0, 174, A, B); g.arc(0, 0, 148, B, A, true); g.closePath();
                        g.fillStyle = on ? mr.lit : !usable ? "#7d8288" : over ? "#c6cace" : "#b3b8bd"; g.fill();
                        g.lineWidth = 2; g.strokeStyle = mr.ink; g.stroke();
                        // bevel: light edge outside unless pressed
                        g.lineWidth = 2; g.strokeStyle = on || press ? "#5a5e64" : "#f4f5f6";
                        g.beginPath(); g.arc(0, 0, 172, A + .02, B - .02); g.stroke();
                    }
                }
                Item {
                    readonly property point at: mr.polar(161, tab.mid)
                    x: mr.extent + at.x; y: mr.extent + at.y
                    rotation: tab.mid + 90
                    Text {
                        anchors.centerIn: parent
                        text: tab.modelData.text
                        opacity: tab.usable ? 1 : .45
                        font.family: mr.mono; font.pixelSize: 10; font.bold: true; color: mr.ink
                    }
                }
                MouseArea {
                    id: tabMa
                    readonly property point at: mr.polar(161, tab.mid)
                    readonly property real span: Math.max(24, (tab.modelData.a1 - tab.modelData.a0) * Math.PI / 180 * 161)
                    x: mr.extent + at.x - span / 2; y: mr.extent + at.y - 13; width: span; height: 26
                    rotation: tab.mid + 90
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: {
                        const k = tab.modelData.key;
                        if (k === "undo") { if (tab.usable) mr.undo(); return }
                        mr.mode = k;
                        if (k === "shades") mr.spinTo(mr.look.color);
                        arcs.requestPaint(); presetsCanvas.requestPaint();
                    }
                }
            }
        }

        // the model's slots, numbered chips on the inner circle
        Repeater {
            model: mr.slots
            Rectangle {
                id: chip
                required property var modelData
                required property int index
                readonly property point at: mr.polar(124, -176 + index * 14)
                x: mr.extent + at.x - 11; y: mr.extent + at.y - 11
                width: 22; height: 22; radius: 11
                color: modelData.color; border.color: mr.ink; border.width: 2
                Rectangle { visible: mr.slot === chip.modelData.slot; anchors.fill: parent; anchors.margins: -4; radius: width / 2; color: "transparent"; border.color: mr.lit; border.width: 2 }
                Text { anchors.centerIn: parent; text: chip.modelData.slot; font.family: mr.mono; font.pixelSize: 9; font.bold: true
                       color: mr.lightness(chip.modelData.color) > .6 ? mr.ink : "#f4f5f6" }
                MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; acceptedButtons: Qt.LeftButton | Qt.RightButton
                    onClicked: m => { if (m.button === Qt.RightButton) { mr.facesToSlot(chip.modelData.slot); return } mr.slot = chip.modelData.slot; if (mr.mode === "shades") mr.spinTo(mr.look.color) } }
            }
        }

        // hex code, or the name of the swatch under the pointer
        Rectangle {
            readonly property point at: mr.polar(160, 118)
            x: mr.extent + at.x - width / 2; y: mr.extent + at.y - height / 2
            width: 84; height: 24
            color: mr.paper; border.color: mr.ink; border.width: 2
            TextInput {
                anchors.fill: parent; anchors.leftMargin: 6; verticalAlignment: TextInput.AlignVCenter
                font.family: mr.mono; font.pixelSize: 11; font.bold: true; color: mr.ink
                text: hit.hover >= 0 && !activeFocus ? mr.cells[hit.hover].label : mr.hex(mr.look.color).toUpperCase()
                selectByMouse: true; maximumLength: 7
                onAccepted: { const t = text.startsWith("#") ? text : "#" + text; if (/^#[0-9a-fA-F]{6}$/.test(t)) { mr.pickColor(t.toLowerCase()); if (mr.mode === "shades") mr.spinTo(t) } focus = false }
                Keys.onEscapePressed: focus = false
            }
        }

        // the core: the slot you're on, as it looks; click it to put the ring away
        Rectangle {
            id: core
            x: mr.extent - 96; y: mr.extent - 96; width: 192; height: 192; radius: 96
            color: mr.steelLo; border.color: coreMa.containsMouse ? mr.lit : mr.ink; border.width: 2
            Rectangle {
                anchors.centerIn: parent; width: 112; height: 112; radius: 56
                color: mr.look.color; border.color: mr.ink; border.width: 2
                // the glow, as a ring round it
                Rectangle { visible: !!mr.look.emit; anchors.fill: parent; anchors.margins: -7; radius: width / 2; color: "transparent"; border.color: mr.look.emit || "transparent"; border.width: 5 }
                // metal: a cool rim; gloss: a highlight that tightens as it gets smoother
                Rectangle {
                    x: parent.width * .2; y: parent.height * .16
                    width: parent.width * (.18 + .35 * mr.look.rough); height: width * .62; radius: height / 2
                    rotation: -35
                    color: mr.look.metal > .5 ? "#dfe8ff" : "#ffffff"
                    opacity: .12 + .6 * (1 - mr.look.rough)
                }
            }
            Text {
                anchors.horizontalCenter: parent.horizontalCenter; y: 10
                text: mr.slot >= 0 ? "SLOT " + mr.slot : "NO MODEL"
                color: mr.lit; font.family: mr.mono; font.bold: true; font.pixelSize: 12
            }
            Text {
                anchors.horizontalCenter: parent.horizontalCenter; anchors.bottom: parent.bottom; anchors.bottomMargin: 12
                width: 150; horizontalAlignment: Text.AlignHCenter; elide: Text.ElideRight
                text: mr.bench.busy ? "WORKING" : mr.cur && mr.cur.preset ? mr.cur.preset.replace(/_/g, " ").toUpperCase() : "R " + Math.round(mr.look.rough * 100) + "  M " + Math.round(mr.look.metal * 100)
                color: mr.text; font.family: mr.mono; font.pixelSize: 10; font.bold: true
            }
            MouseArea { id: coreMa; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: mr.closed() }
        }
    }

    // ---- the slot card: which bodies wear the slot, and giving one a slot of its own ----
    Rectangle {
        id: card
        x: 14; y: 14
        width: 300
        height: cardCol.implicitHeight + 16
        color: mr.steelLo; border.color: mr.edge; border.width: 2; radius: 6
        MouseArea { anchors.fill: parent }      // the card is solid: clicks on it don't reach the viewport
        Column {
            id: cardCol
            x: 8; y: 8; width: card.width - 16
            spacing: 5
            Text { text: "MATERIALS"; color: mr.lit; font.family: mr.mono; font.bold: true; font.pixelSize: 15 }
            Text {
                width: parent.width; elide: Text.ElideRight
                text: mr.bench.ready === false ? "getting your copy ready…" : "on a copy of " + (mr.bench.from || "nothing")
                color: mr.dim; font.family: mr.mono; font.pixelSize: 11
            }
            Text {
                visible: mr.bench.ready !== false && !mr.slots.length
                width: parent.width; wrapMode: Text.Wrap
                text: "nothing on screen to dress: add something first"
                color: mr.text; font.family: mr.mono; font.pixelSize: 12
            }
            Repeater {
                model: mr.slots
                Column {
                    id: srow
                    required property var modelData
                    width: cardCol.width
                    spacing: 2
                    Rectangle {
                        width: parent.width; height: 26; radius: 3
                        color: mr.slot === srow.modelData.slot ? mr.steelHi : sma.containsMouse ? mr.steel : "transparent"
                        border.color: mr.slot === srow.modelData.slot ? mr.lit : "transparent"
                        Rectangle { x: 6; anchors.verticalCenter: parent.verticalCenter; width: 16; height: 16; radius: 8; color: srow.modelData.color; border.color: mr.ink }
                        Text {
                            x: 30; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 36; elide: Text.ElideRight
                            text: "SLOT " + srow.modelData.slot + "  " + (srow.modelData.preset || (srow.modelData.hasLook ? srow.modelData.color : "no look yet"))
                            color: mr.slot === srow.modelData.slot ? mr.lit : mr.text; font.family: mr.mono; font.pixelSize: 12; font.bold: true
                        }
                        MouseArea { id: sma; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: { mr.slot = srow.modelData.slot; if (mr.mode === "shades") mr.spinTo(mr.look.color) } }
                        Rectangle {
                            visible: mr.facesPicked
                            anchors.right: parent.right; anchors.rightMargin: 3; anchors.verticalCenter: parent.verticalCenter
                            width: 54; height: 20; radius: 3
                            color: fma.pressed ? mr.steelLo : fma.containsMouse ? mr.steelHi : mr.steel; border.color: mr.lit
                            Text { anchors.centerIn: parent; text: "\u2192 SEL"; color: mr.lit; font.family: mr.mono; font.bold: true; font.pixelSize: 10 }
                            MouseArea { id: fma; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: mr.facesToSlot(srow.modelData.slot) }
                        }
                    }
                    // the bodies on it; OWN SLOT moves one onto a fresh slot, so it can wear its own look
                    Repeater {
                        model: srow.modelData.bodies
                        Item {
                            id: brow
                            required property var modelData
                            width: cardCol.width; height: 22
                            Text {
                                x: 30; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 120; elide: Text.ElideRight
                                text: brow.modelData.name; color: mr.dim; font.family: mr.mono; font.pixelSize: 12
                            }
                            Rectangle {
                                visible: !!brow.modelData.feature && (srow.modelData.bodies.length > 1 || srow.modelData.slot === 0)
                                anchors.right: parent.right; anchors.verticalCenter: parent.verticalCenter
                                width: 84; height: 20; radius: 3
                                color: oma.pressed ? mr.steelLo : oma.containsMouse ? mr.steelHi : mr.steel; border.color: mr.edge
                                Text { anchors.centerIn: parent; text: "OWN SLOT"; color: mr.text; font.family: mr.mono; font.bold: true; font.pixelSize: 10 }
                                MouseArea {
                                    id: oma
                                    anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor
                                    onClicked: {
                                        let n = 1;
                                        while (mr.slots.some(s => s.slot === n)) n++;
                                        mr.slot = n;
                                        mr.apply("set " + brow.modelData.feature + " material=" + n);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            Text {
                width: parent.width; wrapMode: Text.Wrap; maximumLineCount: 5; elide: Text.ElideRight
                visible: text !== ""
                text: mr.bench.error || ""
                color: mr.bad; font.family: mr.mono; font.pixelSize: 11
            }
        }
    }
}
