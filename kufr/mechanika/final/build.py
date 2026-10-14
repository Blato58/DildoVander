"""Regenerate only final/exports. No access to previous CAD geometry."""
from __future__ import annotations

import csv
import hashlib
import json
from pathlib import Path
import time

import cadquery as cq
from geometry import ROOT, PARAMS, assembly, print_position, bounds
import chassis
import mechanism
import layout
import inspect_data
import harness
import manufacturing
import slicer_validation
from check_fit import scene
from artifacts import ASSEMBLY_NAMES
from viewer_style import snapshot_styles

OUT=ROOT/"exports"


def all_parts():
    return chassis.parts()+mechanism.parts()+layout.parts()


def source_hashes():
    snapshot_styles()
    inspect_data.assert_snapshot_current()
    candidates=list(ROOT.iterdir())+list((ROOT/"reference").glob("*"))
    return {p.relative_to(ROOT).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(candidates)
            if p.is_file() and p.suffix in (".py",".js",".cjs",".json",".md",".txt",".ps1",".html",".css",".csv",".ini") and p.name!="fit-debug.json"}


def main():
    inspect_data.snapshot_sources()
    harness.validate()
    for directory in (OUT,OUT/"stl",OUT/"step",OUT/"3mf",OUT/"assembly",OUT/"images"):
        directory.mkdir(exist_ok=True)
    parts=all_parts()
    expected={p.name for p in parts}
    old_manifest=OUT/"manifest.json"
    if old_manifest.exists():
        for old in json.loads(old_manifest.read_text(encoding="utf-8"))["parts"]:
            if old["name"] not in expected:
                for folder,extension in (("stl",".stl"),("step",".step"),("3mf",".3mf"),("profiles",".ini")):
                    old_file=(OUT/folder/(old["name"]+extension)).resolve()
                    assert old_file.is_relative_to(OUT.resolve())
                    old_file.unlink(missing_ok=True)
    manifest=[]
    printed_parts=[]
    for p in parts:
        printed=print_position(p.shape,p.rotation)
        if not printed.isValid() or len(printed.Solids())!=1:
            raise ValueError(f"Invalid or disconnected printable part: {p.name}")
        cq.exporters.export(printed,str(OUT/"stl"/(p.name+".stl")),tolerance=0.03,angularTolerance=0.08)
        cq.exporters.export(printed,str(OUT/"step"/(p.name+".step")))
        cq.exporters.export(printed,str(OUT/"3mf"/(p.name+".3mf")),exportType=cq.exporters.ExportTypes.THREEMF,
                            tolerance=0.03,angularTolerance=0.08)
        b=bounds(printed)
        manifest.append({"name":p.name,"quantity":p.quantity,"size_mm":[round(b[i+3]-b[i],4) for i in range(3)],
                         "solid_volume_mm3":printed.Volume(),"purpose":p.reason,
                         "orientation":"Already placed on Z=0 in exported STL", "material":"PETG",
                         "manufacturing":manufacturing.record(p,OUT)})
        printed_parts.append((p,printed))
        print(f"Exported {p.name}",flush=True)
    scenes={"open_rest":scene(),"open_presenting":scene(160),"service":scene(covers=False),
            "closed_transport":scene(lid=0,transport=True),"one_module":mechanism.module()}
    assert set(scenes)==set(ASSEMBLY_NAMES),"Update the current assembly inventory"
    for name,items in scenes.items():
        assembly(items,name).export(str(OUT/"assembly"/(name+".step")))
        print(f"Assembly {name}",flush=True)
    inventory=[]
    for i in scenes["open_rest"]:
        if i.group in ("hardware","moving_hw","wand","fastener","restraint","bond"):
            inventory.append({"name":i.name,"bounds_mm":[round(v,4) for v in bounds(i.shape)],"source":i.source})
    (OUT/"manifest.json").write_text(json.dumps({"revision":PARAMS["revision"],
        "manufacturing_revision":PARAMS['manufacturing_revision'],"parts":manifest,
        "assemblies":list(scenes),"inventory":inventory,"sources":source_hashes()},indent=2,ensure_ascii=False),encoding="utf-8")
    manufacturing.export_records(OUT,manifest)
    manufacturing.analyze_parts(OUT,printed_parts)
    slicing=slicer_validation.validate_exports(OUT)
    (OUT/"slicer-validation.json").write_text(json.dumps(slicing,indent=2),encoding="utf-8")
    if slicing["status"] not in ("PASS_SLICER","SKIP_SLICER_UNAVAILABLE"):
        # Staged exports are removed on failure. Preserve the actual failure
        # evidence outside the published manufacturing set before that cleanup.
        import shutil
        import tempfile
        audit_root=ROOT.parent/".venv"
        audit_root.mkdir(exist_ok=True)
        failure=Path(tempfile.mkdtemp(prefix="failed-slicer-",dir=audit_root))
        for filename in ("manifest.json","printability.json","slicer-validation.json"):
            shutil.copyfile(OUT/filename,failure/filename)
        if (OUT/'slicing/tool.log').is_file():
            shutil.copyfile(OUT/'slicing/tool.log',failure/'tool.log')
        details=[]
        for part in slicing.get("parts",[]):
            if part.get("status")=="PASS_SLICER":
                continue
            name=part["name"]
            for folder,filename in (("stl",name+".stl"),("3mf",name+".3mf"),("profiles",name+".ini"),
                *(('slicing',name+suffix) for suffix in ('.gcode','-effective.ini','-slice.log','-project.log'))):
                source=OUT/folder/filename
                if source.is_file():
                    shutil.copyfile(source,failure/filename)
            details.append({"name":name,"warnings":part.get("slice",{}).get("warnings",[])[:2],
                "warning_resolution":part.get("warning_resolution",{}),
                "toolpath_problems":part.get("toolpath",{}).get("problems",[])[:2],
                "project_geometry":part.get("project_geometry",{}).get("status")})
        detail_text=json.dumps(details)
        excerpt=detail_text[:4000]
        if len(detail_text)>4000:
            excerpt+=' [truncated; full details in retained slicer-validation.json]'
        raise ValueError(f"Slicer validation failed; evidence: {failure}; "+excerpt)
    harness.export(OUT)
    with (OUT/"print-bom.csv").open("w",newline="",encoding="utf-8-sig") as f:
        writer=csv.writer(f)
        writer.writerow(["part","quantity","X_mm","Y_mm","Z_mm","solid_cm3","purpose"])
        for p in manifest:
            writer.writerow([p["name"],p["quantity"],*p["size_mm"],round(p["solid_volume_mm3"]/1000,2),p["purpose"]])
    with (OUT/"installed-hardware.csv").open("w",newline="",encoding="utf-8-sig") as f:
        writer=csv.writer(f)
        writer.writerow(["name","min_x","min_y","min_z","max_x","max_y","max_z","dimension_evidence"])
        for item in inventory:
            writer.writerow([item["name"],*item["bounds_mm"],item["source"]])
    print(f"Export complete: {len(manifest)} STL types; {sum(p['quantity'] for p in manifest)} prints",flush=True)


if __name__=="__main__":
    main()
