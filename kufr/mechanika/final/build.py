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
from check_fit import scene

OUT=ROOT/"exports"


def all_parts():
    return chassis.parts()+mechanism.parts()+layout.parts()


def source_hashes():
    candidates=list(ROOT.iterdir())+list((ROOT/"reference").glob("*"))
    return {p.relative_to(ROOT).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(candidates)
            if p.is_file() and p.suffix in (".py",".json",".md",".txt",".ps1",".html",".csv",".png") and p.name!="fit-debug.json"}


def main():
    for directory in (OUT,OUT/"stl",OUT/"step",OUT/"assembly",OUT/"images"):
        directory.mkdir(exist_ok=True)
    expected={p.name for p in all_parts()}
    old_manifest=OUT/"manifest.json"
    if old_manifest.exists():
        for old in json.loads(old_manifest.read_text(encoding="utf-8"))["parts"]:
            if old["name"] not in expected:
                for folder,extension in (("stl",".stl"),("step",".step")):
                    old_file=(OUT/folder/(old["name"]+extension)).resolve()
                    assert old_file.is_relative_to(OUT.resolve())
                    old_file.unlink(missing_ok=True)
    manifest=[]
    for p in all_parts():
        printed=print_position(p.shape,p.rotation)
        if not printed.isValid() or len(printed.Solids())!=1:
            raise ValueError(f"Invalid or disconnected printable part: {p.name}")
        cq.exporters.export(printed,str(OUT/"stl"/(p.name+".stl")),tolerance=0.03,angularTolerance=0.08)
        cq.exporters.export(printed,str(OUT/"step"/(p.name+".step")))
        b=bounds(printed)
        manifest.append({"name":p.name,"quantity":p.quantity,"size_mm":[round(b[i+3]-b[i],4) for i in range(3)],
                         "solid_volume_mm3":printed.Volume(),"purpose":p.reason,
                         "orientation":"Already placed on Z=0 in exported STL", "material":"PETG"})
        print(f"Exported {p.name}",flush=True)
    scenes={"open_rest":scene(),"open_presenting":scene(160),"service":scene(covers=False),
            "closed_transport":scene(lid=0,transport=True),"one_module":mechanism.module()}
    for name,items in scenes.items():
        assembly(items,name).export(str(OUT/"assembly"/(name+".step")))
        print(f"Assembly {name}",flush=True)
    inventory=[]
    for i in scenes["open_rest"]:
        if i.group in ("hardware","moving_hw","wand"):
            inventory.append({"name":i.name,"bounds_mm":[round(v,4) for v in bounds(i.shape)],"source":i.source})
    (OUT/"manifest.json").write_text(json.dumps({"revision":PARAMS["revision"],"parts":manifest,"inventory":inventory,"sources":source_hashes()},indent=2,ensure_ascii=False),encoding="utf-8")
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
