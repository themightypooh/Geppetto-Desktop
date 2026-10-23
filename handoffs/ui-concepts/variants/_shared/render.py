# render a standalone QML mockup to PNG, offscreen (no window, no focus steal)
import os, sys
os.environ["QT_QPA_PLATFORM"] = "offscreen"
os.environ["QT_QUICK_BACKEND"] = "software"
from PySide6.QtCore import QUrl, QTimer
from PySide6.QtGui import QGuiApplication
from PySide6.QtQuick import QQuickView
qml, out = sys.argv[1], sys.argv[2]
app = QGuiApplication(sys.argv)
v = QQuickView(); v.setSource(QUrl.fromLocalFile(os.path.abspath(qml)))
if v.status() != QQuickView.Ready:
    for e in v.errors(): print(e.toString())
    sys.exit(1)
v.setColor("#2a2d31")
v.resize(v.rootObject().width(), v.rootObject().height()); v.show()
def grab():
    img = v.grabWindow(); img.save(out); print("saved", out, img.width(), img.height()); app.quit()
QTimer.singleShot(400, grab); app.exec()
