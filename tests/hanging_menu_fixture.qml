import QtQuick
import QtQuick.Window
import "../cabinet"

Window {
    id: window
    width: 460
    height: 300
    visible: true
    color: "#333333"
    QtObject {
        id: palette
        objectName: "menuPalette"
        property string mono: "DejaVu Sans Mono"
        property color amber: "#f2a91c"
    }
    HangingFileMenu {
        id: menu
        cabinetRoot: palette
        x: 120
        y: 55
        z: 1
    }
}
