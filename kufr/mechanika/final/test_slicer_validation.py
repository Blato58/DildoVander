"""Regression tests audit deposition semantics, including reset/retraction cases."""
import json
import hashlib
from pathlib import Path
import tempfile
import unittest
import zipfile

from slicer_validation import audit_profile, inspect_project, parse_gcode, validate_exports, resolve_warnings, stability_types, assert_saved_evidence


def gcode(moves, role="External perimeter"):
    return f"M82\nG92 E0\n;LAYER_CHANGE\nG1 Z0.2\n;TYPE:{role}\n{moves}\n"


class SlicerValidationTests(unittest.TestCase):
    def test_saved_gcode_and_effective_configuration_are_reaudited(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory); audit=root/'slicing';audit.mkdir()
            profile='support_material = 0\n'
            (root/'profile.ini').write_text(profile,encoding='utf-8')
            text=gcode('G0 X10 Y10\nG1 X20 E1')+'; support_material = 0\n'
            run={'warnings':[],'stability_types':[],'warnings_truncated':False}
            part={'name':'cube','profile':'profile.ini','slice':run,
                  'toolpath':parse_gcode(text,bed=(220,220,250)),'effective_profile':audit_profile(text,profile,profile),
                  'warning_resolution':resolve_warnings(run,text)}
            files={'gcode_sha256':('cube.gcode',text),
                   'effective_profile_sha256':('cube-effective.ini',profile),
                   'slice_log_sha256':('cube-slice.log','Exported\nSTDERR:\n'),
                   'project_log_sha256':('cube-project.log','Exported\nSTDERR:\n')}
            for key,(name,content) in files.items():
                path=audit/name;path.write_text(content,encoding='utf-8')
                part[key]=hashlib.sha256(path.read_bytes()).hexdigest()
            assert_saved_evidence(root,part)
            path=audit/'cube.gcode'
            path.write_text(text.replace('X10','X20'),encoding='utf-8')
            with self.assertRaises(ValueError): assert_saved_evidence(root,part)
            part['gcode_sha256']=hashlib.sha256(path.read_bytes()).hexdigest()
            # A fresh digest cannot bless old audit fields.
            path.write_text(text.replace('X10','X20')+'G1 X25 E2\n',encoding='utf-8')
            part['gcode_sha256']=hashlib.sha256(path.read_bytes()).hexdigest()
            with self.assertRaises(ValueError): assert_saved_evidence(root,part)
            path.write_text(text,encoding='utf-8')
            part['gcode_sha256']=hashlib.sha256(path.read_bytes()).hexdigest()
            path=audit/'cube-effective.ini'
            path.write_text('support_material = 1\n',encoding='utf-8')
            with self.assertRaises(ValueError): assert_saved_evidence(root,part)

    def test_deposited_brim_and_width_must_stay_inside_actual_bed(self):
        valid=gcode('G0 X10 Y10\nG1 X20 E1',role='Skirt/Brim')
        result=parse_gcode(valid,bed=(220,220,250))
        self.assertEqual(result['status'],'PASS_TOOLPATH_AUDIT')
        self.assertEqual(result['deposition_bounds_mm'],[9.8,9.8,.2,20.2,10.2,.2])
        for changed in (valid.replace('X10','X-.1'),valid.replace('X20','X220'),
                        valid.replace('Z0.2','Z250.1')):
            self.assertEqual(parse_gcode(changed,bed=(220,220,250))['status'],'FAIL_SLICER')

    def test_only_complete_known_warning_with_supported_multigap_stroke_passes(self):
        header='print warning: Detected print stability issues:'
        output=f'{header}\n\ncube.stl\nLong bridging extrusions\n\nConsider enabling supports.'
        text='\n'.join(['G21','G90','M83','G0 X0 Y-2 Z.2',';TYPE:Perimeter',';WIDTH:.4',
                         'G1 Y2 E1','G0 X10 Y-2','G1 Y2 E1','G0 X20 Y-2','G1 Y2 E1',
                         'G0 X0 Y0 Z.4',';TYPE:Bridge infill','G1 X20 E1'])
        run={'warnings':[header,'Long bridging extrusions'],'stability_types':stability_types(output)}
        self.assertEqual(resolve_warnings(run,text)['status'],'PASS_LONG_WARNING_REVIEW')
        for changed in (output.replace('Consider enabling supports.',''),
                        output.replace('Long bridging extrusions','Long bridging extrusions\nUnknown defect')):
            run['stability_types']=stability_types(changed)
            self.assertEqual(resolve_warnings(run,text)['status'],'FAIL_UNRESOLVED_WARNINGS')
        run.pop('stability_types')
        self.assertEqual(resolve_warnings(run,text)['status'],'FAIL_UNRESOLVED_WARNINGS')

    def test_floating_unknown_and_short_only_warnings_cannot_be_waived(self):
        header='print warning: Detected print stability issues:'
        for kind in ('Floating bridge anchors','Loose extrusions','Low bed adhesion','Long bridging extrusions'):
            result=resolve_warnings({'warnings':[header,kind]},gcode('G1 X10 E1'))
            self.assertEqual(result['status'],'FAIL_UNRESOLVED_WARNINGS')
        self.assertEqual(resolve_warnings({'warnings':[]},'')['status'],'PASS_NO_WARNINGS')

    def test_ordinary_deposition_passes(self):
        result = parse_gcode(gcode("G1 X10 Y0 E1\nG1 X10 Y10 E2"))
        self.assertEqual(result["status"], "PASS_TOOLPATH_AUDIT")
        self.assertEqual(result["deposition_segments"], 2)

    def test_support_interface_deposition_fails(self):
        result = parse_gcode(gcode("G1 X10 E1", "Support material interface"))
        self.assertEqual(result["status"], "FAIL_SLICER")
        self.assertEqual(result["support_deposition_segments"], 1)

    def test_support_configuration_mentions_and_travel_do_not_fail(self):
        text = gcode("G1 X10 E1") + "; support_material = 0\n;TYPE:Support material\nG1 X20\n"
        self.assertEqual(parse_gcode(text)["support_deposition_segments"], 0)

    def test_relative_extrusion_reset_and_retraction(self):
        text = gcode("M83\nG1 X13 E1\nG1 X20 E-1\nG92 E0\nG1 X25 E0.5", "Bridge infill")
        result = parse_gcode(text)
        self.assertEqual(result["bridge_deposition_segments"], 2)
        self.assertEqual(result["max_bridge_toolpath_segment_mm"], 13)
        self.assertTrue(result["bridge_candidate_review"])

    def test_empty_unclassified_and_floating_first_layer_fail(self):
        for text in ("G1 X10\n", "G1 Z0.2\nG1 X10 E1\n", gcode("G1 Z2\nG1 X10 E1")):
            self.assertEqual(parse_gcode(text)["status"], "FAIL_SLICER")

    def test_warning_and_arc_deposition_fail(self):
        for text in (gcode("G1 X10 E1") + "; WARNING: floating object\n",
                     gcode("G2 X10 E1 I5")):
            self.assertEqual(parse_gcode(text)["status"], "FAIL_SLICER")

    def test_effective_supports_and_machine_macros_are_checked(self):
        profile = "support_material = 0\nstart_gcode =\n"
        valid = "; support_material = 0\n; start_gcode = \n"
        self.assertEqual(audit_profile(valid, profile)["status"], "PASS_EFFECTIVE_PROFILE")
        for changed in (valid.replace("= 0", "= 1"), valid.replace("start_gcode = ", "start_gcode = G28")):
            self.assertEqual(audit_profile(changed, profile)["status"], "FAIL_EFFECTIVE_PROFILE")

    def test_3mf_requires_mesh_and_preserves_rotation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "model.3mf"
            for transform, expected in (("1 0 0 0 1 0 0 0 1 0 0 0", "FAIL_ORIENTED_3MF"),
                                        ("0 1 0 -1 0 0 0 0 1 0 0 0", "FAIL_ORIENTED_3MF"),
                                        ("1 0 0 0 1 0 0 0 1 0 0 2", "FAIL_ORIENTED_3MF")):
                model = ('<model unit="millimeter" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">'
                         '<resources><object id="1"><mesh><vertices><vertex x="0" y="0" z="0"/>'
                         '</vertices><triangles><triangle v1="0" v2="0" v3="0"/></triangles></mesh></object></resources>'
                         f'<build><item objectid="1" transform="{transform}"/></build></model>')
                with zipfile.ZipFile(path, "w") as archive:
                    archive.writestr("3D/3dmodel.model", model)
                self.assertEqual(inspect_project(path)["status"], expected)

    def test_closed_3mf_matches_stl_and_rejects_out_of_bed_translation(self):
        import cadquery as cq
        import xml.etree.ElementTree as ET
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            shape=cq.Solid.makeBox(10,8,4)
            project=root/'cube.3mf'; stl=root/'cube.stl'
            cq.exporters.export(shape,str(project),exportType=cq.exporters.ExportTypes.THREEMF,
                                tolerance=.03,angularTolerance=.08)
            cq.exporters.export(shape,str(stl),tolerance=.03,angularTolerance=.08)
            self.assertEqual(inspect_project(project,stl)['status'],'PASS_ORIENTED_3MF')
            cq.exporters.export(cq.Solid.makeBox(9,8,4),str(stl))
            self.assertEqual(inspect_project(project,stl)['status'],'FAIL_ORIENTED_3MF')
            with zipfile.ZipFile(project) as archive:
                files={name:archive.read(name) for name in archive.namelist()}
            model=ET.fromstring(files['3D/3dmodel.model'])
            item=model.find('{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}build/{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}item')
            item.set('transform','1 0 0 0 1 0 0 0 1 1000 1000 0')
            files['3D/3dmodel.model']=ET.tostring(model)
            with zipfile.ZipFile(project,'w') as archive:
                for name,data in files.items():
                    archive.writestr(name,data)
            self.assertEqual(inspect_project(project)['status'],'FAIL_ORIENTED_3MF')

    def test_missing_tool_is_explicit_skip_and_inputs_are_hashed(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            (out / "stl").mkdir()
            (out / "stl/cube.stl").write_bytes(b"synthetic fixture; not sliced")
            (out / "manifest.json").write_text(json.dumps({"parts": [{"name": "cube"}]}))
            result = validate_exports(out, str(out / "missing-slicer"))
            self.assertEqual(result["status"], "SKIP_SLICER_UNAVAILABLE")
            self.assertEqual(len(result["parts"][0]["stl_sha256"]), 64)
            self.assertFalse((out / "slicing").exists())

    def test_unsafe_and_duplicate_manifest_entries_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            for names in (["../escape"], ["cube", "cube"], []):
                (out / "manifest.json").write_text(json.dumps({"parts": [{"name": name} for name in names]}))
                with self.assertRaises(ValueError):
                    validate_exports(out, "missing")


if __name__ == "__main__":
    unittest.main()
