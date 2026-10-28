import QtQuick
import QtQuick.Controls as C
import QtQuick.Layouts

Item {
    id: control
    objectName: "runPaceControl"
    property var run: null
    property var controller: null
    property int textSize: 12
    readonly property string runDir: run && run.dir ? run.dir : ""
    readonly property real reportedPace: run && Number.isFinite(run.pace) ? Math.max(0, Math.min(30, run.pace)) : 0
    property real shownPace: reportedPace
    property string pendingDir: ""
    readonly property string paceText: shownPace < 0.05 ? "Full speed" : shownPace.toFixed(1) + " s / step"
    implicitWidth: 280
    implicitHeight: label.implicitHeight + speedRow.implicitHeight + 2
    enabled: runDir !== "" && (!run.status || run.status === "running")
    opacity: enabled ? 1 : 0.5

    function submit() {
        flush.stop()
        const dir = pendingDir
        pendingDir = ""
        if (dir && dir === runDir && controller) controller.setPace(dir, shownPace)
    }
    onReportedPaceChanged: if (!speed.pressed && !pendingDir) shownPace = reportedPace
    onRunDirChanged: { flush.stop(); pendingDir = ""; shownPace = reportedPace }

    Text {
        id: label
        objectName: "runPaceLabel"
        width: parent.width
        text: "STEP SPEED · " + control.paceText
        color: "#f2a91c"
        font.family: "monospace"
        font.pixelSize: control.textSize
        wrapMode: Text.Wrap
        maximumLineCount: 2
        elide: Text.ElideRight
    }
    RowLayout {
        id: speedRow
        anchors.top: label.bottom
        anchors.topMargin: 2
        width: parent.width
        spacing: 6
        Text { text: "Slow"; color: "#c2c8ca"; font.pixelSize: control.textSize }
        C.Slider {
            id: speed
            objectName: "runPaceSlider"
            Layout.fillWidth: true
            Layout.minimumWidth: 20
            Layout.minimumHeight: 28
            from: 0; to: 30; stepSize: 0.1
            value: 30 - control.shownPace
            Accessible.name: "Build step speed"
            Accessible.description: "Slow on the left, full speed on the right. Changes only this viewport's run."
            onMoved: {
                control.shownPace = Math.round((30 - value) * 10) / 10
                control.pendingDir = control.runDir
                flush.restart()
            }
            onPressedChanged: if (!pressed && control.pendingDir) control.submit()
            C.ToolTip.visible: hovered || pressed
            C.ToolTip.delay: 350
            C.ToolTip.text: control.paceText + " — this viewport only"
        }
        Text { text: "Fast"; color: "#c2c8ca"; font.pixelSize: control.textSize }
    }
    Timer { id: flush; interval: 120; onTriggered: control.submit() }
}
