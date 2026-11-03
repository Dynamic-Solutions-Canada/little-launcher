"""Machine-local frame coordinates and native anchor choice boundaries."""
from pathlib import Path
import tempfile
import unittest
try:
    from PySide6.QtWidgets import QApplication
    from little_launcher.controller import LauncherController
    from little_launcher.positions import PlacementBridge, valid_position
    from little_launcher.anchor_picker import AnchorPicker
    from little_launcher.web_preferences import valid_preference
    QT=True
except ImportError:
    QT=False

@unittest.skipUnless(QT, 'Native position checks require QtWidgets/QtDBus')
class PositionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.app=QApplication.instance() or QApplication([])

    def test_dragged_coordinates_are_local_and_require_current_enabled_last_position_web_launcher(self):
        with tempfile.TemporaryDirectory() as temporary:
            controller=LauncherController(Path(temporary))
            controller.edit(lambda document: document['Launchers'].append({'Id':'web','Name':'Web','Kind':1,'WebAnchor':10,'WebFlyoutPosition':'99,88','Future':7}))
            bridge=PlacementBridge(controller)
            original=controller.store.path.read_bytes();revision=controller.revision
            bridge.Record('web',-100,240)
            self.assertEqual(bridge.get('web'),[-100,240])
            self.assertEqual(controller.store.path.read_bytes(),original)
            self.assertEqual(controller.revision,revision)
            self.assertEqual(PlacementBridge(LauncherController(Path(temporary))).get('web'),[-100,240])
            controller.set_enabled('web',False);bridge.Record('web',200,200)
            bridge.Record('unknown',200,200)
            self.assertEqual(bridge.get('web'),[-100,240])
            controller.set_enabled('web',True)
            controller.edit(lambda document:document['Launchers'][-1].update(WebAnchor=8))
            bridge.Record('web',200,200)
            self.assertIsNone(bridge.get('web'))
            controller.edit(lambda document:document['Launchers'][-1].update(WebAnchor=10))
            self.assertIsNone(bridge.get('web'))

    def test_coordinate_bounds_and_anchor_wire_bounds(self):
        for value in [None,{},[],[True,0],[0,'1'],[1_000_001,0]]:self.assertFalse(valid_position(value))
        self.assertTrue(valid_position([-2000,0]));self.assertTrue(valid_preference('WebAnchor',10))
        self.assertFalse(valid_preference('WebAnchor',11))

    def test_native_grid_restores_selection_and_preserves_pending_cancel(self):
        picker=AnchorPicker(8,position_supported=True)
        self.assertTrue(picker.buttons.button(8).isChecked())
        picker.buttons.button(1).click();self.assertEqual(picker.selected,1)
        picker.reject();self.assertEqual(picker.result(),0)
        unsupported=AnchorPicker(0,position_supported=False)
        self.assertFalse(unsupported.buttons.button(10).isEnabled())
