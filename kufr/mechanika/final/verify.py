"""Fail-closed CAD, round-trip, mesh, motion and service validation."""
from __future__ import annotations

from collections import Counter,defaultdict
import hashlib
import json
import math
import struct

import numpy as np
import cadquery as cq

from geometry import ROOT,PARAMS,bounds,print_position,Item
from build import OUT,all_parts,source_hashes
from check_fit import scene,collisions,overlaps
import chassis
import mechanism as mech
import layout
import harness
import strength
import follower_load
import validate_assembly
import verify_harness_geometry
from artifacts import (export_names, assert_generation_snapshot, export_hashes,
                       assert_export_hashes, GENERATION_SNAPSHOT, VERIFICATION_EXPORTS,
                       AUDIT_EXPORTS)


def stl_check(path,reference):
    data=path.read_bytes()
    count=struct.unpack_from("<I",data,80)[0]
    assert len(data)==84+count*50,f"Not a binary STL: {path}"
    vertices=[]
    for i in range(count):
        values=struct.unpack_from("<12fH",data,84+i*50)
        vertices.append(np.array(values[3:12]).reshape(3,3))
    tri=np.array(vertices,dtype=np.float64)
    assert np.isfinite(tri).all()
    area=np.linalg.norm(np.cross(tri[:,1]-tri[:,0],tri[:,2]-tri[:,0]),axis=1)
    assert area.min()>1e-10,f"Degenerate triangles: {path.name}"
    volume=float(np.sum(np.einsum("ij,ij->i",tri[:,0],np.cross(tri[:,1],tri[:,2])))/6)
    edge_faces=defaultdict(list)
    oriented=Counter()
    for i,t in enumerate(tri):
        keys=[tuple(np.round(v,5)) for v in t]
        for a,b in ((keys[0],keys[1]),(keys[1],keys[2]),(keys[2],keys[0])):
            edge_faces[tuple(sorted((a,b)))].append(i)
            oriented[(a,b)]+=1
    assert all(len(f)==2 for f in edge_faces.values()),f"Open/nonmanifold STL: {path.name}"
    assert all(n==oriented[(b,a)] for (a,b),n in oriented.items()),f"Inconsistent winding: {path.name}"
    adjacency=defaultdict(list)
    for a,b in edge_faces.values():
        adjacency[a].append(b); adjacency[b].append(a)
    seen={0}; todo=[0]
    while todo:
        for j in adjacency[todo.pop()]:
            if j not in seen:
                seen.add(j);todo.append(j)
    assert len(seen)==len(tri),f"Disconnected STL surfaces: {path.name}"
    expected=reference.Volume()
    assert volume>0 and abs(volume-expected)/expected<0.005,f"STL volume mismatch: {path.name}"
    actual=np.r_[tri.min(axis=(0,1)),tri.max(axis=(0,1))]
    assert max(abs(actual-np.array(bounds(reference))))<0.08
    return {"triangles":count,"closed":True,"consistent_winding":True,"connected":True,
            "volume_mm3":round(volume,3),"volume_error_percent":round(abs(volume-expected)/expected*100,5),
            "sha256":hashlib.sha256(data).hexdigest()}


def export_checks():
    manifest=json.loads((OUT/"manifest.json").read_text(encoding="utf-8"))
    # Added documentation/preview tools may change the source manifest: regenerate first.
    assert manifest["sources"]==source_hashes(),"Stale source manifest; run build.py again"
    export_names(manifest)  # Require exactly the current assembly names, never an old manifest.
    results=[]
    for p in all_parts():
        s=print_position(p.shape,p.rotation)
        b=bounds(s); size=[b[i+3]-b[i] for i in range(3)]
        assert s.isValid() and len(s.Solids())==1 and s.Volume()>0
        assert size[0]+2*PARAMS["print"]["brim"]<=PARAMS["print"]["bed_x"]+.001
        assert size[1]+2*PARAMS["print"]["brim"]<=PARAMS["print"]["bed_y"]+.001
        assert abs(b[2])<1e-5
        step=cq.importers.importStep(str(OUT/"step"/(p.name+".step"))).val()
        assert step.isValid() and len(step.Solids())==1
        assert abs(step.Volume()-s.Volume())/s.Volume()<1e-7
        results.append({"name":p.name,"quantity":p.quantity,"print_box_mm":[round(v,3) for v in size],
                        "step_roundtrip":True,**stl_check(OUT/"stl"/(p.name+".stl"),s)})
        print(f"Verified STL/STEP {p.name}",flush=True)
    for name in manifest["assemblies"]:
        path=OUT/"assembly"/(name+".step")
        s=cq.importers.importStep(str(path)).val()
        assert s.isValid() and len(s.Solids())>0,f"Invalid assembly {path.name}"
    return results


def mechanical_checks(report_path=None):
    """Source-derived sampled solid checks; never imply physical qualification."""
    M,W,F,C=PARAMS["mechanism"],PARAMS["wand"],PARAMS["frame"],PARAMS["case"]
    assert W["length"]+PARAMS["clearances"]["wand_length_total"]<=M["length"]
    assert W["head_width"]+2*W["fabric"]+2<=M["tray_width"]
    assert M["count"]==6 and M["pitch"]>=M["base_width"]+2
    assert F["origin"][2]-C["brace_height"]>=PARAMS["clearances"]["frame_to_brace"]
    assert sum(p.name.startswith("frame_panel_") for p in chassis.parts())==4
    assert sum(p.name.startswith("floor_anchor_") for p in chassis.parts())==4
    assert not any(p.name.startswith(("body_","lid_")) for p in all_parts())
    errors={}
    safe_low=C["safe_origin"]
    safe_high=[a+b for a,b in zip(safe_low,C["safe_zone"])]
    def check_safe_box(items,label):
        for item in items:
            # Printed floor anchors and adhesive must fit the same guaranteed
            # volume as the insert; only the reference suitcase is excluded.
            if item.group=="case" or item.group.startswith("lid"):
                continue
            bb=bounds(item.shape)
            if any(bb[k]<safe_low[k]-.001 or bb[k+3]>safe_high[k]+.001 for k in range(3)):
                errors.setdefault("safe_box_"+label,[]).append([item.name,list(bb)])
    for name,items in (("rest",scene()),("raised",scene(160)),("transport",scene(lid=0,transport=True))):
        check_safe_box(items,name)
        hit=collisions(items)
        if hit: errors[name]=hit
        print(f"Assembly intersections {name}: {len(hit)}",flush=True)
        if name=="rest":
            for item in items:
                if item.group.startswith("lid") or item.group in ("case","bond"):
                    continue
                bb=bounds(item.shape)
                if bb[5]>C["inside_z"]+.001:
                    errors.setdefault("rest_lower_case_height",[]).append([item.name,bb[5]])
    fixed=chassis.items(105)+layout.items()+[i for i in scene() if i.group=="lid_fabric"]
    fixed=[i for i in fixed if not i.group.startswith("lid")]
    fixed_bounds={i.name:bounds(i.shape) for i in fixed}
    sampled_angles=list(range(0,int(M["travel_degrees"])+1,10))
    motion=[]
    def compare(material,others,cache,label):
        for a in material:
            ab=bounds(a.shape)
            for b in others:
                if not overlaps(ab,cache[b.name]):
                    continue
                volume=a.shape.intersect(b.shape).Volume()
                if volume>.03:
                    errors.setdefault(label,[]).append([a.name,b.name,round(volume,4)])
    for index in range(M["count"]):
        adjacent=[j for j in (index-1,index+1) if 0<=j<M["count"]]
        neighbour_sets=[]
        for state in (0,M["travel_degrees"]):
            neighbours=[i for j in adjacent for i in mech.module(j,state)]
            neighbour_sets.append((neighbours,{i.name:bounds(i.shape) for i in neighbours},state))
        for angle in sampled_angles:
            moving=mech.module(index,angle)
            check_safe_box(moving,f"module_{index+1}_{angle}")
            hit=collisions(moving)
            if hit: errors[f"module_{index+1}_angle_{angle}"]=hit
            compare(moving,fixed,fixed_bounds,f"fixed_motion_{index+1}_{angle}")
            for neighbours,cache,state in neighbour_sets:
                compare(moving,neighbours,cache,f"neighbour_motion_{index+1}_{angle}_{state}")
            motion.append({"module":index+1,"degrees":angle,"lift_mm":round(mech.cam_law(angle)[0]-M["cam_low_radius"],6)})
        print(f"All material and both neighbour states checked for module {index+1}",flush=True)
    # Gravity is supported by the sleeve bottoms, not by servo torque. Prove
    # actual planar bearing contact area, rather than exempting overlapping solids.
    lower=mech.tray().translate((0,0,M["cam_z"]+M["cam_low_radius"]+M["follower_drop"]))
    support_area=mech.base().intersect(lower.translate((0,0,-.01))).Volume()/.01
    stop_z=M["cam_z"]+M["cam_low_radius"]+M["follower_drop"]-M["sleeve_length"]
    assert support_area>80, f"Lower hard stop contact missing: {support_area}"
    assert mech.base().intersect(lower).Volume()<.03
    for a in range(-19,20):
        assert abs(mech.cam_law(a)[1])<1e-12 and abs(mech.cam_law(a+160)[1])<1e-12
    body=[i for i in scene(lid=0,transport=True) if not i.group.startswith("lid")]
    body_bounds={i.name:bounds(i.shape) for i in body}
    lid_angles=list(range(0,106,3))
    for angle in lid_angles:
        lid=[Item("lid_sweep",chassis.lid_position(chassis.shell(True),angle),"lid","shell")]
        compare(lid,body,body_bounds,f"lid_motion_{angle}")
    service=[i for i in scene() if i.name not in ("powerbank","bank_cover")
             and not i.name.startswith("component_tie_powerbank_") and not i.group.startswith("lid")]
    service_bounds={i.name:bounds(i.shape) for i in service}
    bank=next(i for i in layout.hardware() if i.name=="powerbank")
    bank_offsets=list(range(0,181,5))
    for z in bank_offsets:
        compare([Item("bank_extraction",bank.shape.translate((0,0,z)),"access","black")],service,service_bounds,f"bank_removal_{z}")
    complete=[i for i in scene() if not i.group.startswith("lid")]
    complete_bounds={i.name:bounds(i.shape) for i in complete}
    mating={"esp_usb":{"esp32"},"pd_usb":{"pd_trigger"},"bank_usb":{"powerbank"}}
    for reservation in layout.reservations():
        owners=mating.get(reservation.name,set())
        targets=[i for i in complete if i.name not in owners]
        compare([reservation],targets,complete_bounds,"connector_and_bundle_paths")
    # Remove the owning tray before its base screws; adjacent modules and all
    # purchased hardware stay installed. The full driver diameter is checked.
    tool_count=0
    for index in range(M["count"]):
        owner=mech.module(index)
        targets=fixed+[i for j in range(M["count"]) if j!=index for i in mech.module(j)]
        targets += [i for i in owner if i.name.startswith(("base_","servo_","guide_"))]
        target_bounds={i.name:bounds(i.shape) for i in targets}
        for x,y in mech.MOUNT_HOLES:
            tool=Item("module_driver",chassis.cylinder(PARAMS["clearances"]["hand_tool_radius"],100,
                      (M["origin_x"]+index*M["pitch"]+x,M["origin_y"]+y,M["origin_z"]+6)),"access","white")
            compare([tool],targets,target_bounds,"module_tool_access")
            tool_count+=1
    if report_path is not None:
        report_path.write_text(json.dumps(errors,indent=2),encoding="utf-8")
    assert not errors, "CAD intersections found: "+json.dumps(errors)[:3000]
    return {"static_assemblies":3,"motion_material_samples":len(motion),"all_six_modules":True,
            "neighbour_states_degrees":[0,M["travel_degrees"]],"lid_material_samples":len(lid_angles),
            "bank_removal_samples":len(bank_offsets),"collisions":[],"lower_stop_contact_mm2":round(support_area,2),
            "lower_stop_nominal_gap_mm":0,"lower_stop_maximum_calibration_offset_mm":M["lower_stop_clearance"],
            "frame_anchor_count":len(chassis.rack_holes()),"case_holes_added":0,"module_tool_paths":tool_count,
            "groove_peg_diametral_clearance_mm":M["groove_width"]-M["sliding_follower_diameter"],"lift_mm":M["lift"],
            "sliding_follower_load":follower_load.calculate(),
            "manufacturing_ready":False,"physical_test_performed":False,
            "limitations":["10 degree sampled BREP motion is not a continuous collision proof",
                            "Safe-zone, brace XY, lid partition and hinge axis need dry-fit acceptance",
                            "Actual harness bend radius, glue/wood strength, creep, loaded torque and pulse-off untested",
                            "Intermediate power loss requires manual support/inspection; no automatic descent",
                            "Camera/FPC and horn details provisional; no manufacturing qualification"]}


def cad_hashes():
    manifest=json.loads((OUT/"manifest.json").read_text(encoding="utf-8"))
    return {name:hashlib.sha256((OUT/name).read_bytes()).hexdigest()
            for name in export_names(manifest) if name.endswith((".stl",".step"))}


def require_complete_wire_audit(result):
    coverage=result.get("coverage",{})
    assert result.get("status")=="PASS", "Finite-wire geometry audit failed"
    assert coverage.get("rest_covers") is True and coverage.get("transport") is True
    assert coverage.get("motion_angles")==[0,80,160] and coverage.get("modules")==list(range(1,7))
    assert coverage.get("capsule_elbows") is True
    assert result.get("sources_unchanged_during_audit") is True and result.get("truncated") is False
    assert result.get("collisions")==[] and result.get("bounds_failures")==[]
    assert result.get("bundle_fit",{}).get("status")=="PASS"
    self_geometry=result.get("self_geometry",{})
    assert self_geometry.get("status")=="PASS" and self_geometry.get("collisions")==[]
    self_coverage=self_geometry.get("coverage",{})
    assert self_coverage.get("rest_wire_count")==135 and self_coverage.get("transport_wire_count")==135
    assert self_coverage.get("motion_angles")==[0,80,160] and self_coverage.get("modules")==list(range(1,7))


def require_anchor_assumption_screen(result):
    assert result.get("normal_load_assumption_screen_passed") is True, "Printed anchor load exceeds stated design assumptions"


def main():
    (OUT/"verification.json").unlink(missing_ok=True)
    initial_sources=source_hashes()
    manifest=json.loads((OUT/'manifest.json').read_bytes())
    generated=assert_generation_snapshot(OUT,manifest,initial_sources)
    generation_digest=export_hashes(OUT,[GENERATION_SNAPSHOT])[GENERATION_SNAPSHOT]
    initial_cad=cad_hashes()
    parts=export_checks()
    strength_result=strength.calculate()
    require_anchor_assumption_screen(strength_result)
    mech_result=mechanical_checks(OUT/"collision-details.json")
    harness_result=harness.validate()
    assembly_result=validate_assembly.validate()
    wire_geometry=verify_harness_geometry.verify_all_states()
    # Save diagnostics even on failure, without issuing an acceptance report.
    (OUT/"wire-collision-details.json").write_text(json.dumps(wire_geometry,indent=2,allow_nan=False),encoding="utf-8")
    require_complete_wire_audit(wire_geometry)
    assert initial_sources==source_hashes(),"Sources changed during verification; regenerate"
    assert initial_cad==cad_hashes(),"CAD files changed during verification; regenerate"
    assert generated==assert_generation_snapshot(OUT,manifest,initial_sources),"Generation snapshot changed during verification"
    assert generation_digest==export_hashes(OUT,[GENERATION_SNAPSHOT])[GENERATION_SNAPSHOT],"Generation snapshot bytes changed during verification"
    verified_exports={**generated['sha256'],GENERATION_SNAPSHOT:generation_digest,
                      **export_hashes(OUT,AUDIT_EXPORTS)}
    assert_export_hashes(OUT,verified_exports,set(export_names(manifest))-VERIFICATION_EXPORTS)
    report={"result":"PASS_CAD_NOT_PHYSICAL_ACCEPTANCE","revision":PARAMS["revision"],
            "physical_test_performed":False,"stl_types":len(parts),"print_count":sum(p["quantity"] for p in parts),
            "parts":parts,"mechanics":mech_result,"harness":harness_result,"strength":strength_result,
            "assembly":assembly_result,"wire_geometry":wire_geometry,"cad_sha256":initial_cad,
            "export_sha256":verified_exports,"sources":initial_sources}
    (OUT/"verification.json").write_text(json.dumps(report,indent=2,ensure_ascii=False,allow_nan=False),encoding="utf-8")
    print(json.dumps({k:v for k,v in report.items() if k not in ("parts","sources","cad_sha256","export_sha256","wire_geometry")},indent=2),flush=True)


if __name__=="__main__":
    main()
