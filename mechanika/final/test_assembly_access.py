"""Regression checks for manufacturing order and removable service mounts."""
import csv
import json
import unittest

from geometry import ROOT, PARAMS, Item, bounds, box, cylinder
from service_geometry import SERVICE_MOUNTS, NUT_Z, SEAT_Z, HEAD_RADIUS, SERVICE_LIFT
from validate_assembly import validate_contract
import layout
import chassis


class AssemblyContractTests(unittest.TestCase):
    def setUp(self):
        self.guide=json.loads((ROOT/'assembly-guide.json').read_text(encoding='utf-8'))
        with (ROOT/'mechanicky-material.csv').open(encoding='utf-8-sig',newline='') as file:
            self.materials={row['id']:float(row['mnozstvi']) for row in csv.DictReader(file)}

    def test_electronics_after_lifts_is_rejected(self):
        original=self.guide['steps']
        electronics=next(step for step in original if step['id']=='electronics')
        original.remove(electronics)
        original.insert(11,electronics)
        with self.assertRaisesRegex(AssertionError,'Bench electronics'):
            validate_contract(self.guide,self.materials)

    def test_base_screws_cannot_be_deferred_until_upper_groups(self):
        steps={step['id']:step for step in self.guide['steps']}
        screws=next(item for item in steps['install']['materials'] if item['id']=='M3x14')
        steps['install']['materials'].remove(screws)
        steps['mechanisms']['materials'].append(screws)
        with self.assertRaises(AssertionError):
            validate_contract(self.guide,self.materials)

    def test_case_deck_is_transferred_once_after_anchor_preparation(self):
        steps={step['id']:step for step in self.guide['steps']}
        for key in ('frame','carriers','electronics','power','signals','bases'):
            self.assertEqual(steps[key]['caseParts'],[])
            self.assertTrue(steps[key]['benchParts'])
        self.assertNotIn('frame_panel*',steps['anchors']['caseParts'])
        self.assertIn('frame_panel*',steps['install']['caseParts'])
        self.assertEqual(steps['moving']['caseParts'],[])
        self.assertTrue(steps['moving']['preparedParts'])

    def test_harness_audit_fingerprint_covers_cassette_mount_geometry(self):
        from verify_harness_geometry import source_fingerprint
        self.assertIn('service_geometry.py',source_fingerprint())

    def test_left_cassette_service_matches_step_and_viewer_inspector(self):
        step=next(step for step in self.guide['steps'] if step['id']=='power')
        inspector=next(group for group in self.guide['subassemblies'] if group['id']=='left_power')
        self.assertEqual(step['removal'],inspector['removal'])
        self.assertIn('všechny čtyři horní spoje ovladačů',inspector['removal'])
        self.assertIn('Levý rám a jeho spodní spoje zůstávají',inspector['removal'])


class ServiceMountTests(unittest.TestCase):
    def test_controls_release_and_cassette_extraction_keep_bolted_left_ladder(self):
        from assembly_access import (cassette_items,control_panel_service_paths,intersection_errors,
                                     service_fixture,service_obstacles)
        items=service_fixture()
        result=control_panel_service_paths(items)
        self.assertEqual(result['status'],'PASS')
        self.assertIn('switch_frame_left',result['retained_original_deck_fixture'])
        fixed=service_obstacles('left_service_carrier',items)
        self.assertIn('switch_frame_left',{item.name for item in fixed})
        self.assertIn('carrier_shaft_switch_shelf_1',{item.name for item in fixed})
        moving=cassette_items('left_service_carrier',items)
        for offset in range(0,121,5):
            shifted=[Item(item.name,item.shape.translate((0,0,offset)),item.group,item.color) for item in moving]
            self.assertEqual(intersection_errors(shifted,fixed),[],offset)

    def test_left_cassette_cannot_skip_control_plate_release(self):
        from assembly_access import cassette_items,intersection_errors,service_fixture,service_obstacles
        items=service_fixture()
        fixed=service_obstacles('left_service_carrier',items)
        fixed += [item for item in items if item.name=='switch_shelf']
        moving=[Item(item.name,item.shape.translate((0,0,30)),item.group,item.color)
                for item in cassette_items('left_service_carrier',items)]
        self.assertTrue(any(hit[1]=='switch_shelf' for hit in intersection_errors(moving,fixed)))

    def test_blocked_control_plate_service_is_rejected(self):
        from assembly_access import control_panel_service_paths,service_fixture
        items=service_fixture()+[Item('blocked_control_release',box(8,8,2,(36,31,109)),'fixed','white')]
        with self.assertRaisesRegex(AssertionError,'Controls service intersections'):
            control_panel_service_paths(items)

    def test_blocked_open_usb_edge_is_rejected(self):
        from assembly_access import usb_tail_installation_paths
        obstruction=Item('blocked_fore_usb_edge',box(1,1,1,(67,64,41.5)),'fixed','white')
        with self.assertRaisesRegex(AssertionError,'Blocked USB edge insertion'):
            usb_tail_installation_paths([obstruction])

    def test_rear_left_screw_head_leaves_one_mm_to_amplifier_straps(self):
        x,y=SERVICE_MOUNTS['left_service_carrier'][-1]
        sweep=cylinder(HEAD_RADIUS,SERVICE_LIFT,(x,y,SEAT_Z))
        straps=layout.ties('amplifier_left')
        self.assertEqual(len(straps),2)
        for strap in straps:
            with self.subTest(strap=strap.name):
                self.assertGreaterEqual(sweep.distance(strap.shape),1.0)

    def test_retained_cassette_nuts_fit_insert_envelope(self):
        low=PARAMS['case']['safe_origin']
        high=[a+b for a,b in zip(low,PARAMS['case']['safe_zone'])]
        nuts=[item for item in layout.items(False,False)
              if item.name.startswith(('carrier_nut_left_service_','carrier_nut_right_service_'))]
        self.assertEqual(len(nuts),7)
        for nut in nuts:
            with self.subTest(nut=nut.name):
                actual=bounds(nut.shape)
                self.assertTrue(all(actual[i]>=low[i]-.001 and actual[i+3]<=high[i]+.001
                                    for i in range(3)))

    def test_front_left_anchor_driver_clears_controls(self):
        from assembly_access import intersection_errors
        from geometry import Item, cylinder
        controls=[Item('switch_shelf',layout.switch_shelf(),'fixed','white')]
        controls += [item for item in layout.hardware()
                     if item.name in ('red_main','green_drives','white_ptt')]
        tool=Item('driver',cylinder(2,100,(27,30,15.5)),'access','white')
        self.assertEqual(intersection_errors([tool],controls),[])

    def test_all_seven_mounts_have_real_captive_deck_nut_voids(self):
        frame=chassis.frame()
        for rows in SERVICE_MOUNTS.values():
            for x,y in rows:
                with self.subTest(x=x,y=y):
                    nut=layout.hex_nut(x,y,NUT_Z)
                    self.assertLess(frame.intersect(nut).Volume(),1e-6)
                    self.assertAlmostEqual(bounds(nut)[2],NUT_Z)
        hardware=layout.items(False,False)
        screws=[item for item in hardware if item.name.startswith(('carrier_head_left_service_',
                                                                  'carrier_head_right_service_'))]
        self.assertEqual(len(screws),7)
        self.assertTrue(all(abs(bounds(item.shape)[2]-SEAT_Z)<1e-6 for item in screws))


if __name__=='__main__':
    unittest.main()
