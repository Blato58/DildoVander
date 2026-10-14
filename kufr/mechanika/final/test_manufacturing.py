"""Manufacturing contract regression: real profile content, not labels alone."""
import hashlib
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import manufacturing


class ManufacturingTests(unittest.TestCase):
    def test_base_format_and_density_cannot_weaken_structural_profile(self):
        original=manufacturing.BASE_PROFILE.read_text(encoding='utf-8')
        for line in ('fill_density = 39%','fill_density=40%'):
            with self.subTest(line=line), tempfile.TemporaryDirectory() as directory:
                out=Path(directory)
                base=out/'input.ini'
                base.write_text(original.replace('fill_density = 40%',line),encoding='utf-8')
                part=SimpleNamespace(name='lift_base',quantity=6,rotation=None,reason='test')
                with patch.object(manufacturing,'BASE_PROFILE',base):
                    contract=manufacturing.record(part,out)
                profile=out/contract['slicer_profile']
                manufacturing.validate_profile(profile.read_text(encoding='utf-8'),contract)
                self.assertEqual(contract['infill'],'100%')
                self.assertEqual(hashlib.sha256(profile.read_bytes()).hexdigest(),contract['slicer_profile_sha256'])
                with self.assertRaises(ValueError):
                    manufacturing.validate_profile(profile.read_text(encoding='utf-8').replace('support_material = 0','support_material = 1'),contract)

    def test_wrong_nozzle_fails_even_if_profile_hash_is_fresh(self):
        contract={'id':'lift_base','infill':'100%','perimeters':6,'infill_pattern':'rectilinear'}
        text=manufacturing.BASE_PROFILE.read_text(encoding='utf-8').replace('nozzle_diameter = 0.4','nozzle_diameter = 0.6')
        with self.assertRaises(ValueError):
            manufacturing.validate_profile(text,contract)

    def test_unknown_prints_cannot_escape_decision_inventory(self):
        with self.assertRaisesRegex(ValueError,'Missing explicit manufacturing contract'):
            manufacturing._contract('unreviewed_housing')

    def test_anchor_gauge_does_not_receive_installed_bonding_instructions(self):
        with tempfile.TemporaryDirectory() as directory:
            part=SimpleNamespace(name='floor_anchor_coupon',quantity=1,rotation=None,reason='test')
            contract=manufacturing.record(part,Path(directory))
        self.assertEqual(contract['strategy'],'SPLIT')
        self.assertIn('not installed in suitcase',contract['connection'])
        self.assertIn('no assembly hardware',contract['hardware'])
        installed=manufacturing._contract('floor_anchor_front_left')
        self.assertEqual(installed[0],'REDESIGN')
        self.assertIn('Epoxy bond',installed[3])

    def test_tie_tunnel_roofs_keep_verified_short_axis_bridge_direction(self):
        for name in ('carrier_pca9685','carrier_pixel_shifter'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                out=Path(directory)
                part=SimpleNamespace(name=name,quantity=1,rotation=None,reason='test')
                contract=manufacturing.record(part,out)
                text=(out/contract['slicer_profile']).read_text(encoding='utf-8')
                self.assertEqual(contract['bridge_angle_degrees'],90)
                manufacturing.validate_profile(text,contract)
                changed=text.replace('bridge_angle = 90','bridge_angle = 0')
                contract['slicer_profile_sha256']=hashlib.sha256(changed.encode('utf-8')).hexdigest()
                with self.assertRaises(ValueError):
                    manufacturing.validate_profile(changed,contract)


if __name__=='__main__':
    unittest.main()
