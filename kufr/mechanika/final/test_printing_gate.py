"""Reject stale, missing or weakened manufacturing evidence before packaging."""
from dataclasses import asdict
import hashlib
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import cadquery as cq
import manufacturing
from printability import Profile
import verify


class PrintingGateTests(unittest.TestCase):
    def setUp(self):
        self.directory=tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.out=Path(self.directory.name)
        (self.out/'stl').mkdir(); (self.out/'3mf').mkdir()
        shape=cq.Solid.makeBox(10,8,4)
        cq.exporters.export(shape,str(self.out/'stl/lift_base.stl'),tolerance=.03,angularTolerance=.08)
        cq.exporters.export(shape,str(self.out/'3mf/lift_base.3mf'),exportType=cq.exporters.ExportTypes.THREEMF,
                            tolerance=.03,angularTolerance=.08)
        self.contract=manufacturing.record(SimpleNamespace(name='lift_base',quantity=6,rotation=None,reason='fixture'),self.out)
        self.manifest={'parts':[{'name':'lift_base','manufacturing':self.contract}]}
        stl=hashlib.sha256((self.out/'stl/lift_base.stl').read_bytes()).hexdigest()
        self.analytical={'status':'PASS_ANALYTICAL','parts':[{
            'name':'lift_base','stl_sha256':stl,'status':'PASS','issue_count':0,'issues':[],
            'unsupported_count':0,'floating_start_count':0,'thin_wall_count':0,
            'flat_bed_contact':True,'issues_truncated':False,
            'profile':asdict(Profile(**manufacturing.ANALYTICAL_PROFILE))}]}
        self.slicing={'status':'SKIP_SLICER_UNAVAILABLE','parts':[{
            'name':'lift_base','stl_sha256':stl,'status':'SKIP_SLICER_UNAVAILABLE',
            'profile_sha256':self.contract['slicer_profile_sha256']}]}
        self.save()

    def save(self):
        path=self.out/'manifest.json'
        path.write_text(json.dumps(self.manifest),encoding='utf-8')
        digest=hashlib.sha256(path.read_bytes()).hexdigest()
        for name,report in (('printability.json',self.analytical),('slicer-validation.json',self.slicing)):
            report['manifest_sha256']=digest
            (self.out/name).write_text(json.dumps(report),encoding='utf-8')

    def check(self):
        with patch.object(verify,'OUT',self.out):
            return verify.print_checks()

    def test_unavailable_slicer_stays_distinct_from_verified_geometry(self):
        result=self.check()
        self.assertEqual(result['slicing'],'SKIP_SLICER_UNAVAILABLE')
        self.assertFalse(result['physical_print_performed'])

    def test_incomplete_inventory_or_weakened_analytical_profile_is_rejected(self):
        self.analytical['parts'][0]['profile']['max_bridge_mm']=200
        self.save()
        with self.assertRaises(AssertionError): self.check()
        self.analytical['parts']=[]
        self.save()
        with self.assertRaises(AssertionError): self.check()

    def test_changed_profile_cannot_pass_under_fresh_hashes_and_manifest(self):
        profile=self.out/self.contract['slicer_profile']
        profile.write_text(profile.read_text(encoding='utf-8').replace('fill_density = 100%','fill_density = 39%'),encoding='utf-8')
        digest=hashlib.sha256(profile.read_bytes()).hexdigest()
        self.contract['slicer_profile_sha256']=digest
        self.slicing['parts'][0]['profile_sha256']=digest
        self.save()
        with self.assertRaises(ValueError): self.check()

    def test_changed_mesh_or_false_slicer_pass_cannot_be_blessed(self):
        (self.out/'stl/lift_base.stl').write_bytes(b'changed mesh')
        with self.assertRaises(AssertionError): self.check()
        self.slicing['status']='PASS_SLICER'
        self.save()
        with self.assertRaises(AssertionError): self.check()


if __name__=='__main__':
    unittest.main()
