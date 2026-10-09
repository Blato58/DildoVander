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
    results=[]
    for p in all_parts():
        s=print_position(p.shape,p.rotation)
        b=bounds(s); size=[b[i+3]-b[i] for i in range(3)]
        assert s.isValid() and len(s.Solids())==1 and s.Volume()>0
        assert size[0]+2*PARAMS["print"]["brim"]<=220.001
        assert size[1]+2*PARAMS["print"]["brim"]<=220.001
        assert abs(b[2])<1e-5
        step=cq.importers.importStep(str(OUT/"step"/(p.name+".step"))).val()
        assert step.isValid() and len(step.Solids())==1
        assert abs(step.Volume()-s.Volume())/s.Volume()<1e-7
        results.append({"name":p.name,"quantity":p.quantity,"print_box_mm":[round(v,3) for v in size],
                        "step_roundtrip":True,**stl_check(OUT/"stl"/(p.name+".stl"),s)})
        print(f"Verified STL/STEP {p.name}",flush=True)
    for path in (OUT/"assembly").glob("*.step"):
        s=cq.importers.importStep(str(path)).val()
        assert s.isValid() and len(s.Solids())>0,f"Invalid assembly {path.name}"
    return results


def mechanical_checks():
    M,W=PARAMS["mechanism"],PARAMS["wand"]
    assert W["length"]+12<=M["length"]<=214
    assert W["head_width"]+2*W["fabric"]+2<=M["tray_width"]
    assert W["shaft_diameter"]<=16
    assert M["count"]==6 and M["pitch"]>=48
    errors={}
    for name,items in (("rest",scene()),("raised",scene(160)),("transport",scene(lid=0,transport=True))):
        hit=collisions(items)
        if hit:errors[name]=hit
        print(f"Assembly intersections {name}: {len(hit)}",flush=True)
    motion=[]
    neighbours=mech.module(0)+mech.module(2)
    neighbour_bounds={i.name:bounds(i.shape) for i in neighbours}
    for angle in range(0,161,2):
        # Include both resting neighbours: their LED strips must clear every nut.
        moving=mech.module(1,angle)
        hit=collisions(moving)
        # Their internal static pairs were already checked in the complete rest scene.
        for a in moving:
            if a.group=="fastener":continue
            ab=bounds(a.shape)
            for b in neighbours:
                if b.group=="fastener" or not overlaps(ab,neighbour_bounds[b.name]):continue
                volume=a.shape.intersect(b.shape).Volume()
                if volume>0.03:hit.append([a.name,b.name,round(volume,4)])
        if hit:errors[f"motion_{angle}"]=hit
        motion.append({"degrees":angle,"lift_mm":round(mech.cam_law(angle)[0]-M["cam_low_radius"],6)})
        if angle%40==0:print(f"Motion and neighbouring bays checked through {angle} degrees",flush=True)
    # The prescribed groove has actual constant-radius arcs for +/-20 degree dwells.
    for a in range(-19,20):
        assert abs(mech.cam_law(a)[1])<1e-12
        assert abs(mech.cam_law(a+160)[1])<1e-12
    print(f"Mechanism motion samples: {len(motion)}",flush=True)
    # Lid sweep checks material rather than testing only end poses.
    fixed=[i for i in scene(lid=0,transport=True) if not i.group.startswith("lid") and i.group!="restraint"]
    body=[i for i in fixed if i.group!="fastener"]
    for angle in range(0,106,3):
        lid=[Item(f"lid_motion_{j}",chassis.lid_position(s,angle),"lid","shell") for j,s in enumerate(chassis.tiles(True))]
        for a in lid:
            ab=bounds(a.shape)
            for b in body:
                if overlaps(ab,bounds(b.shape)) and a.shape.intersect(b.shape).Volume()>0.03:
                    errors.setdefault("lid_motion",[]).append([angle,a.name,b.name])
    # Bank alone moves; only its cover is removed, all other components stay.
    service=[i for i in scene() if i.name not in ("powerbank","bank_cover") and not i.group.startswith("lid") and i.group!="fastener"]
    bank=next(i for i in layout.hardware() if i.name=="powerbank")
    for z in range(0,181,5):
        moved=bank.shape.translate((0,0,z))
        bb=bounds(moved)
        for item in service:
            if overlaps(bb,bounds(item.shape)) and moved.intersect(item.shape).Volume()>0.03:
                errors.setdefault("bank_removal",[]).append([z,item.name])
    # Connector airspace includes plug lengths, not merely bare PCB rectangles.
    allow={"esp_usb":{"esp32"},"pd_usb":{"pd_trigger"},"bank_usb":{"powerbank"},"buck_air":{"buck"},
           "camera_fpc":{"camera","esp32","camera_angle"},"power_trunk":set(),"front_bus":set()}
    complete=[i for i in scene() if not i.group.startswith("lid") and i.group!="fastener"]
    for r in layout.reservations():
        if r.name not in allow:continue
        for i in complete:
            if i.name in allow[r.name]:continue
            if overlaps(bounds(r.shape),bounds(i.shape)) and r.shape.intersect(i.shape).Volume()>0.03:
                errors.setdefault("connector_and_cable_reservations",[]).append([r.name,i.name])
    # Include real nut/washer envelopes at floor mounts; boards may not cover them.
    occupied=[i for i in complete if i.group in ("hardware","moving_hw")]
    from geometry import cylinder
    nuts=[(x,y,13,3.4,5) for x,y in chassis.module_holes()]
    nuts += [(x,y,4,4.8,5) for x,y in chassis.seam_floor_holes()]
    nuts += [(x,y,z,3.4,3.2) for x,ys,z in ((12,(83,103),75),(66,(83,103),75),(374,(32,115,158,250),71),(429,(32,115,158,250),71)) for y in ys]
    for x,y,z,r,h in nuts:
        nut=cylinder(r,h,(x,y,z))
        for item in occupied:
            if overlaps(bounds(nut),bounds(item.shape)) and nut.intersect(item.shape).Volume()>0.03:
                errors.setdefault("floor_fastener_keepouts",[]).append([[x,y,z],item.name])
        for reserve in layout.reservations():
            if reserve.name not in allow:continue
            if overlaps(bounds(nut),bounds(reserve.shape)) and nut.intersect(reserve.shape).Volume()>0.03:
                errors.setdefault("fastener_connector_keepouts",[]).append([[x,y,z],reserve.name])
    (OUT/"collision-details.json").write_text(json.dumps(errors,indent=2),encoding="utf-8")
    assert not errors,f"CAD intersections found; see {OUT/'collision-details.json'}"
    max_derivative=max(abs(mech.cam_law(a/10)[1]) for a in range(1601))
    moving_mass=(mech.tray().Volume()/1000*1.27+W["design_payload_g"]+25)/1000
    gravity=moving_mass*9.81
    brake=1.3*gravity
    ideal_torque=(gravity+brake)*max_derivative/1000
    return {"static_assemblies":3,"motion_material_samples":len(motion),"motion_includes_both_resting_neighbours":True,"lid_material_samples":36,"bank_removal_samples":37,
            "collisions":[],"dwell_radius_mm":[M["cam_low_radius"],M["cam_low_radius"]+M["lift"]],
            "lift_mm":M["lift"],"groove_roller_diametral_clearance_mm":M["groove_width"]-M["roller_diameter"],
            "brake_test_target_N":round(brake,2),"moving_mass_design_kg":round(moving_mass,3),
            "ideal_torque_Nm":round(ideal_torque,4),"minimum_test_torque_with_1_5_margin_Nm":round(1.5*ideal_torque,4),
            "limitations":["Sampled BREP motion is not a continuous collision proof","Cable swept envelopes do not certify actual harness bend radius",
                            "Brake friction, servo torque, textile adhesion, creep and proof load not physically tested",
                            "Camera and horn shaft/hole details remain provisional"]}


def main():
    initial_sources=source_hashes()
    parts=export_checks()
    mech_result=mechanical_checks()
    assert initial_sources==source_hashes(),"Sources changed during verification; regenerate"
    report={"result":"PASS_CAD_NOT_PHYSICAL_ACCEPTANCE","revision":PARAMS["revision"],
            "physical_test_performed":False,"stl_types":len(parts),"print_count":sum(p["quantity"] for p in parts),
            "parts":parts,"mechanics":mech_result,"sources":source_hashes()}
    (OUT/"verification.json").write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding="utf-8")
    print(json.dumps({k:v for k,v in report.items() if k not in ("parts","sources")},indent=2),flush=True)


if __name__=="__main__":
    main()
