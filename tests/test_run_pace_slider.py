"""Per-pane step-speed control, exercised with Qt's offscreen platform only."""
import os
os.environ['QT_QPA_PLATFORM'] = 'offscreen'
os.environ['QSG_RHI_BACKEND'] = 'software'
import sys,time,tempfile,unittest
from pathlib import Path
from PySide6.QtCore import QObject,Slot,QUrl,QPointF,Qt,QMetaObject
from PySide6.QtGui import QGuiApplication,QColor
from PySide6.QtQml import QQmlEngine,QQmlComponent
from PySide6.QtQuick import QQuickWindow
from PySide6.QtTest import QTest
CABINET=Path(__file__).resolve().parents[1]/'cabinet'
sys.path.insert(0,str(CABINET))

class Receiver(QObject):
    def __init__(self): super().__init__(); self.calls=[]
    @Slot(str,float)
    def setPace(self,d,seconds): self.calls.append((d,seconds))

class PaceSliderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): cls.app=QGuiApplication.instance() or QGuiApplication([])
    def settle(self,seconds=.18):
        end=time.monotonic()+seconds
        while time.monotonic()<end: self.app.processEvents(); QTest.qWait(5)
    def setUp(self):
        self.engine=QQmlEngine();self.component=QQmlComponent(self.engine,QUrl.fromLocalFile(str(CABINET/'RunPaceSlider.qml')))
        self.assertFalse(self.component.isError(),str(self.component.errors()))
        self.item=self.component.create();self.assertIsNotNone(self.item,str(self.component.errors()))
        self.receiver=Receiver();self.item.setProperty('controller',self.receiver)
        self.item.setProperty('run',{'dir':'/owned/pane-3','pace':4.0,'status':'running'})
        self.window=QQuickWindow();self.window.setGeometry(0,0,320,140)
        self.window.setColor(QColor('#16191c'))
        self.item.setParentItem(self.window.contentItem());self.item.setWidth(300);self.item.setHeight(100)
        self.window.show();self.settle()
        self.slider=self.item.findChild(QObject,'runPaceSlider')
    def tearDown(self):
        self.window.close();self.item.deleteLater();self.engine.deleteLater();self.settle(.03)
    def point(self,fraction):
        return self.slider.mapToScene(QPointF(1+(self.slider.width()-2)*fraction,self.slider.height()/2)).toPoint()
    def test_drag_routes_only_its_pane_and_reaches_fast_endpoint(self):
        QTest.mousePress(self.window,Qt.LeftButton,Qt.NoModifier,self.point(.5))
        QTest.mouseMove(self.window,self.point(1),20)
        QTest.mouseRelease(self.window,Qt.LeftButton,Qt.NoModifier,self.point(1))
        self.settle()
        self.assertTrue(self.receiver.calls)
        self.assertTrue(all(d=='/owned/pane-3' for d,_ in self.receiver.calls))
        self.assertEqual(self.receiver.calls[-1][1],0)
        self.assertEqual(self.item.property('paceText'),'Full speed')
    def test_keyboard_changes_delay_and_external_state_syncs(self):
        self.slider.forceActiveFocus()
        QTest.keyClick(self.window,Qt.Key_Left);self.settle()
        self.assertEqual(self.receiver.calls[-1][0],'/owned/pane-3')
        self.assertAlmostEqual(self.receiver.calls[-1][1],4.1,places=4)
        self.item.setProperty('run',{'dir':'/owned/pane-3','pace':10.0,'status':'running'});self.settle()
        self.assertAlmostEqual(self.slider.property('value'),20.0)
    def test_keyboard_edit_never_retargets_a_new_run(self):
        self.slider.forceActiveFocus();QTest.keyClick(self.window,Qt.Key_Left)
        self.item.setProperty('run',{'dir':'/different/pane-3','pace':2.0,'status':'running'});self.settle()
        self.assertTrue(all(d=='/owned/pane-3' for d,_ in self.receiver.calls))
        self.assertAlmostEqual(self.slider.property('value'),28.0)
    def test_disabled_without_run(self):
        self.item.setProperty('run',None);self.settle()
        self.assertFalse(self.item.property('enabled'))
    def test_layout_and_screenshots_at_three_scales(self):
        out=Path(os.environ.get('PACE_TEST_ARTIFACTS',tempfile.mkdtemp(prefix='pace-qa-')));out.mkdir(parents=True,exist_ok=True)
        for scale in (.75,1,1.5):
            for width in (220,300):
                self.item.setWidth(width);self.item.setProperty('textSize',round(12*scale));self.item.setProperty('run',{'dir':'/owned/pane-3','pace':30.0,'status':'running'});self.settle()
                label=self.item.findChild(QObject,'runPaceLabel')
                self.assertLessEqual(label.width(),self.item.width())
                for obj in self.item.childItems():
                    self.assertLessEqual(obj.x()+obj.width(),self.item.width()+.1)
                self.assertGreater(self.slider.width(),20)
                self.window.grabWindow().save(str(out/f'pace-{width}-{scale}.png'))
        print('pace screenshots:',out)

class PaceHostTests(unittest.TestCase):
    def test_exact_pace_goes_through_existing_validated_protocol(self):
        import host
        class Fake:
            def __init__(self):self.calls=[]
            def live_command(self,*args,**kwargs):self.calls.append((args,kwargs))
        fake=Fake();host.Host.setPace(fake,'/owned/pane-2',.3)
        self.assertEqual(fake.calls[0][0][:2],('/owned/pane-2','pace'))
        self.assertEqual(fake.calls[0][1],{'seconds':.3})

    def test_drawer_pace_routes_all_four_panes(self):
        # The old viewport-header pace controls (panePace1-4) no longer exist:
        # pace now lives once in the TIMELINE drawer's BUILD page, and the
        # pane keys retarget it. Same routing assertion, new surface.
        import test_cabinet_qml as fixture
        fixture.CabinetQmlTests.setUpClass()
        f=fixture.CabinetQmlTests();host=f.host;root=f.root
        host.runTimer.stop();host._layout=4
        host._runs=[{'dir':f'/speed-test/pane-{n}','status':'running','pace':float(n),
                     'who':'Test modeller','name':'speed test','watch':{},'pane':n,
                     'paused':False,'bench':False,'latestRevision':1,'openState':'open'} for n in range(1,5)]
        host.watching='/speed-test/pane-1'
        for n,pane in enumerate(host.panes,2):pane.run=f'/speed-test/pane-{n}';pane.changed.emit()
        host.panesChanged.emit();host.runsChanged.emit();f.settle(.5)
        calls=[];host.live_command=lambda *a,**kw:calls.append((a,kw))
        # The drawer lives under the window Overlay, not contentItem, so the
        # probe searches from buildPage itself (found via QObject).
        probe_component=QQmlComponent(f.engine)
        probe_component.setData(b'''import QtQml
QtObject {
    property string result: ""
    property var searchRoot: null
    function find(item, name) {
        if (!item) return null;
        if (item.objectName === name) return item;
        const children = item.children || [];
        for (let i=0; i<children.length; i++) {
            const found = find(children[i], name);
            if (found) return found;
        }
        return null;
    }
    function exercise() {
        let dirs = [];
        const page = find(searchRoot, "buildPage");
        const control = find(searchRoot, "buildPace");
        for (let n=1; n<=4; n++) {
            page.selectPane(n);
            dirs.push(control ? control.runDir : "MISSING");
            if (control) {
                control.shownPace = .5*n;
                control.pendingDir = control.runDir;
                control.submit();
            }
        }
        result = JSON.stringify(dirs);
    }
}''',QUrl())
        probe=probe_component.create()
        self.assertIsNotNone(probe,str(probe_component.errors()))
        probe.setProperty('searchRoot',root.findChild(QObject,'buildPage'))
        QMetaObject.invokeMethod(probe,'exercise')
        import json
        self.assertEqual(json.loads(probe.property('result')),[f'/speed-test/pane-{n}' for n in range(1,5)])
        self.assertEqual([a[0] for a,_ in calls],[f'/speed-test/pane-{n}' for n in range(1,5)])
        self.assertEqual([k['seconds'] for _,k in calls],[.5,1.,1.5,2.])
        self.assertEqual(f.qml_errors(),[])
        out=Path(os.environ.get('PACE_TEST_ARTIFACTS',tempfile.mkdtemp(prefix='pace-qa-')));out.mkdir(parents=True,exist_ok=True)
        root.grabWindow().save(str(out/'drawer-pace-four-panes.png'))
        root.hide()

if __name__=='__main__':unittest.main()
