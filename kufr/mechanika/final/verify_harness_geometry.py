"""Finite-wire BREP audit. No export or physical acceptance claims."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import heapq
from copy import deepcopy
from pathlib import Path

import cadquery as cq

import harness
from check_fit import scene, overlaps
import chassis
import layout
import retention
import mechanism
from geometry import bounds


ROOT = Path(__file__).resolve().parent
END_EXIT_MM = 8.0
CLEARANCE_MM = 0.3


def tube(start, end, radius):
    delta = cq.Vector(*(b-a for a,b in zip(start,end)))
    if delta.Length < 1e-7:
        return cq.Solid.makeSphere(radius,cq.Vector(*start),angleDegrees1=-90,angleDegrees2=90)
    return cq.Solid.makeCylinder(radius,delta.Length,cq.Vector(*start),delta.normalized())


def endpoint_zone(wire, component, points, incident):
    """Allow only the owning terminal's first 8mm, never a whole component."""
    zones=[]
    for key,point in (("from",points[0]),("to",points[-1])):
        endpoint=harness.canonical_endpoint(wire[key])
        if key in incident and endpoint.split(":",1)[0] == component:
            zones.append(cq.Solid.makeSphere(END_EXIT_MM,cq.Vector(*point),angleDegrees1=-90,angleDegrees2=90))
    return zones


def obstacles(items):
    # Access, reserved air and soft wands remain obstacles if represented as
    # physical geometry. Only deliberately nonphysical cable/access volumes
    # are omitted; the caller passes the real scene rather than reservations.
    return [(item.name,item.shape,bounds(item.shape)) for item in items]


def audit(wires=None, items=None, clearance=CLEARANCE_MM, max_collisions=None, scope="static rest with installed covers", cache=None):
    wires = harness.HARNESSES if wires is None else wires
    items = scene(covers=True) if items is None else items
    cache={} if cache is None else cache
    solids=obstacles(items)
    collisions=[]
    contacts=[]
    checked=0
    bounds_failures=[]
    for wire in wires:
        points=wire["points"]
        radius=wire["outer_diameter_mm"]/2+clearance
        limits=(harness.PARAMS["frame"]["origin"][0]+harness.PARAMS["frame"]["size"][0],
                harness.PARAMS["frame"]["origin"][1]+harness.PARAMS["frame"]["size"][1],
                harness.PARAMS["case"]["safe_origin"][2]+harness.PARAMS["case"]["safe_zone"][2])
        for point in points:
            if any(point[i]+radius>limits[i]+1e-7 or point[i]-radius<(harness.PARAMS["frame"]["origin"][i] if i<2 else 0) for i in range(3)):
                bounds_failures.append({"wire":wire["id"],"point":point,"radius_mm":radius,"limits":limits})
        primitives=[(index,start,end,({"from"} if index==0 else set()) | ({"to"} if index==len(points)-2 else set()))
                    for index,(start,end) in enumerate(zip(points,points[1:]))]
        primitives += [(f"corner-{index}",point,point,{"from"} if index==0 else {"to"} if index==len(points)-1 else set())
                       for index,point in enumerate(points)]
        for index,start,end,incident in primitives:
            shape=tube(start,end,radius)
            envelope=bounds(shape)
            for name,body,body_bounds in solids:
                if not overlaps(envelope,body_bounds):
                    continue
                checked+=1
                key=(tuple(start),tuple(end),radius,id(body))
                cached=cache.get(key)
                if cached is None or cached[0] is not body:
                    hit=shape.intersect(body)
                    cache[key]=(body,hit)
                else:
                    hit=cached[1]
                if hit.Volume() < 1e-5:
                    continue
                allowed=endpoint_zone(wire,name,points,incident)
                original=hit.Volume()
                for zone in allowed:
                    hit=hit.cut(zone)
                if original-hit.Volume()>1e-5:
                    contacts.append({"wire":wire["id"],"segment":index,"component":name,
                                     "volume_mm3":round(original-hit.Volume(),5),
                                     "allowed_zone_radius_mm":END_EXIT_MM})
                if hit.Volume()>1e-5:
                    collisions.append({"wire":wire["id"],"segment":index,"obstacle":name,
                                       "volume_mm3":round(hit.Volume(),5),"from":start,"to":end,
                                       "obstacle_bounds":body_bounds,"radius_mm":radius})
                    if max_collisions and len(collisions)>=max_collisions:
                        return {"status":"FAIL","collisions":collisions,"endpoint_contacts":contacts,
                                "brep_pairs_checked":checked,"truncated":True,"scope":scope}
    return {"status":"PASS" if not collisions and not bounds_failures else "FAIL","collisions":collisions,
            "bounds_failures":bounds_failures,
            "endpoint_contacts":contacts,"brep_pairs_checked":checked,"truncated":False,
            "wire_count":len(wires),"clearance_mm":clearance,"scope":scope,
            "physical_verification":"NOT PERFORMED"}


def search_route(start, end, items, radius, step=3.0, max_nodes=50000):
    """Bounded orthogonal A* candidate; final capsule BREP audit is mandatory.

    Occupancy samples centre plus 26 radial points, so this is a route proposal,
    never an alternative to full solid clearance verification.
    """
    solids=obstacles(items)
    low=(18.5,10.0,14.0)
    high=(413.0,252.0,106.0)
    origin=tuple(start)
    target=tuple(round((b-a)/step) for a,b in zip(start,end))
    sampled={}
    directions=((1,0,0),(-1,0,0),(0,1,0),(0,-1,0),(0,0,1),(0,0,-1))
    samples=[(0,0,0)]+[tuple(radius*v/math.sqrt(sum(t*t for t in offset)) for v in offset)
                              for offset in __import__("itertools").product((-1,0,1),repeat=3) if any(offset)]
    def point(node):
        return tuple(a+step*i for a,i in zip(origin,node))
    def free(node):
        if node in sampled:
            return sampled[node]
        p=point(node)
        if any(p[i]-radius<low[i] or p[i]+radius>high[i] for i in range(3)):
            sampled[node]=False
            return False
        for name,body,box in solids:
            if not all(box[i]-radius<=p[i]<=box[i+3]+radius for i in range(3)):
                continue
            if any(body.isInside(cq.Vector(*(p[i]+offset[i] for i in range(3))),1e-6)
                   for offset in samples):
                sampled[node]=False
                return False
        sampled[node]=True
        return True
    initial=(0,0,0)
    heap=[(0,0,initial)]
    cost={initial:0}
    previous={}
    while heap and len(cost)<=max_nodes:
        _,length,node=heapq.heappop(heap)
        if node==target:
            result=[list(end),list(point(node))]
            while node in previous:
                node=previous[node]
                result.append(list(point(node)))
            result.reverse()
            clean=[]
            for p in result:
                if clean and p==clean[-1]:
                    continue
                if len(clean)>1 and sum(clean[-2][i]!=clean[-1][i] for i in range(3))==1 and all(
                    (clean[-2][i]==clean[-1][i]==p[i]) or
                    (clean[-2][i]!=clean[-1][i] and clean[-1][i]!=p[i]) for i in range(3)):
                    clean[-1]=p
                else:
                    clean.append(p)
            return harness._orthogonal(clean)
        for delta in directions:
            neighbour=tuple(a+b for a,b in zip(node,delta))
            new=length+1
            if new>=cost.get(neighbour,float("inf")) or not free(neighbour):
                continue
            cost[neighbour]=new
            previous[neighbour]=node
            heuristic=sum(abs(a-b) for a,b in zip(neighbour,target))
            heapq.heappush(heap,(new+heuristic,new,neighbour))
    raise ValueError(f"No bounded cable corridor between {start} and {end}; explored {len(cost)} cells")


def segment_distance(a,b,c,d):
    """Closest distance between finite three-dimensional centreline segments."""
    u=[y-x for x,y in zip(a,b)]
    v=[y-x for x,y in zip(c,d)]
    w=[x-y for x,y in zip(a,c)]
    dot=lambda x,y:sum(a*b for a,b in zip(x,y))
    aa,bb,cc,dd,ee=dot(u,u),dot(u,v),dot(v,v),dot(u,w),dot(v,w)
    clamp=lambda value:max(0,min(1,value))
    if aa<1e-12:
        t=clamp(ee/cc) if cc else 0
        return math.dist(a,[c[i]+t*v[i] for i in range(3)])
    if cc<1e-12:
        return segment_distance(c,d,a,b)
    determinant=aa*cc-bb*bb
    ss=clamp((bb*ee-cc*dd)/determinant) if determinant>1e-12 else 0
    tt=(bb*ss+ee)/cc
    if tt<0:
        tt=0
        ss=clamp(-dd/aa)
    elif tt>1:
        tt=1
        ss=clamp((bb-dd)/aa)
    return math.dist([a[i]+ss*u[i] for i in range(3)],[c[i]+tt*v[i] for i in range(3)])


def self_clearance(wire,clearance=.3):
    """Check nonlocal capsule runs; contiguous curved neighbours form one cable.

    Segments less than two capsule diameters apart along the cable are local
    neighbours. This avoids treating sampled pieces of one smooth bend as two
    cables; complete turns and folded-back tails are always nonlocal.
    """
    points=wire["points"]
    needed=wire["outer_diameter_mm"]+2*clearance
    cumulative=[0]
    for a,b in zip(points,points[1:]):
        cumulative.append(cumulative[-1]+math.dist(a,b))
    failures=[]
    minimum=float("inf")
    for i,(a,b) in enumerate(zip(points,points[1:])):
        for j in range(i+2,len(points)-1):
            if cumulative[j]-cumulative[i+1]<2*needed:
                continue
            distance=segment_distance(a,b,points[j],points[j+1])
            minimum=min(minimum,distance)
            if distance+1e-5<needed:
                failures.append({"wire":wire["id"],"segments":[i,j],"distance_mm":round(distance,5),
                                 "required_mm":needed,"along_cable_separation_mm":round(cumulative[j]-cumulative[i+1],3)})
    return {"status":"FAIL" if failures else "PASS","collisions":failures,
            "minimum_nonlocal_distance_mm":round(minimum,5) if math.isfinite(minimum) else None,"required_distance_mm":needed,
            "local_neighbour_exclusion_mm":2*needed}


def verify_self_geometry():
    """Check complete finite cable centrelines in each declared sampled state."""
    results=[]
    def check(wires,state):
        for wire in wires:
            results.append(dict(self_clearance(wire),wire=wire["id"],state=state))
    check(harness.HARNESSES,"rest")
    transport=deepcopy(harness.HARNESSES)
    for wire in transport:
        if wire["transport_points"] is not None:
            wire["points"]=wire["transport_points"]
    check(transport,"transport")
    for module in range(1,7):
        for angle in (80,160):
            changed=deepcopy([wire for wire in harness.HARNESSES if any(
                int(row["component"].split("_")[-1])==module for row in wire["moving_endpoints"])])
            for wire in changed:
                wire["points"]=points_for_pose(wire,module,angle)
            check(changed,f"module {module} at {angle} degrees")
    return {"status":"FAIL" if any(row["status"]=="FAIL" for row in results) else "PASS",
            "coverage":{"rest_wire_count":len(harness.HARNESSES),"transport_wire_count":len(transport),
                        "wire_ids":[wire["id"] for wire in harness.HARNESSES],
                        "motion_angles":[0,80,160],"modules":list(range(1,7))},
            "collisions":[dict(hit,state=row["state"]) for row in results for hit in row["collisions"]],
            "results":results,"scope":"same-wire nonlocal capsule separation; local continuous neighbours excluded by arc distance"}


def source_fingerprint():
    return {name:hashlib.sha256((ROOT/name).read_bytes()).hexdigest()
            for name in ("parameters.json","harness.py","mechanism.py","layout.py","chassis.py","service_geometry.py","retention.py",
                         "verify_harness_geometry.py","geometry.py","check_fit.py")}


def points_for_pose(wire, module, angle):
    """Move the tray-side route to each sampled cam height, keeping its tie fixed."""
    points=deepcopy(wire["points"])
    lift=mechanism.cam_law(angle)[0]-mechanism.cam_law(0)[0]
    for moving in wire["moving_endpoints"]:
        if int(moving["component"].split("_")[-1])!=module:
            continue
        fixed=moving["fixed_point"]
        indices=[i for i,point in enumerate(points) if point==fixed]
        if not indices:
            raise ValueError(f"Missing fixed flex anchor: {wire['id']}")
        if harness.canonical_endpoint(wire["from"])==moving["endpoint"]:
            selected=range(0,min(indices))
        else:
            selected=range(max(indices)+1,len(points))
        for i in selected:
            points[i][2]+=lift
    return points


def bundle_fit(wires):
    """Physical packing check on overlapping runs; structure clearance is separate."""
    rows=[wire for wire in wires if "rear_corridor_slot" in wire]
    conflicts=[]
    for i,wire in enumerate(rows):
        a=wire["rear_corridor_slot"]
        for other in rows[i+1:]:
            b=other["rear_corridor_slot"]
            if max(a["x_span"][0],b["x_span"][0])>=min(a["x_span"][1],b["x_span"][1]):
                continue
            separation=math.hypot(a["y"]-b["y"],a["z"]-b["z"])
            needed=(a["physical_diameter_mm"]+b["physical_diameter_mm"])/2
            if separation+1e-7<needed:
                conflicts.append([wire["id"],other["id"],separation,needed])
    local=[]
    for module in range(1,7):
        slots=[(wire["id"],slot) for wire in wires for slot in wire.get("local_corridor_slots",[])
               if slot["module"]==module]
        for i,(identifier,a) in enumerate(slots):
            for other,b in slots[i+1:]:
                needed=(a["physical_diameter_mm"]+b["physical_diameter_mm"])/2
                if abs(a["z"]-b["z"])+1e-7<needed:
                    conflicts.append([identifier,other,abs(a["z"]-b["z"]),needed])
        local.append({"module":module,"conductors":len(slots),
                      "occupied_area_mm2":round(sum(math.pi*slot["physical_diameter_mm"]**2/4 for _,slot in slots),2),
                      "slots":slots,"physical_gap_mm":0.1})
    peak_area=max((sum(math.pi*row["outer_diameter_mm"]**2/4 for row in rows
                       if row["rear_corridor_slot"]["x_span"][0]<=x<=row["rear_corridor_slot"]["x_span"][1])
                   for x in range(71,367)),default=0)
    return {"status":"FAIL" if conflicts else "PASS","conflicts":conflicts,
            "rear_wire_count":len(rows),"peak_conductor_area_mm2":round(peak_area,2),"local_lanes":local,
            "window_area_mm2":28*(19+20),"physical_gap_mm":0.1,
            "scope":"rear field and six longitudinal lanes packed; terminal/splice zones and free flex curves still require dry fit"}


def verify_all_states():
    initial=source_fingerprint()
    fixed=chassis.items(105)+layout.items(True,True)+retention.hardware(105,False)
    modules={i:mechanism.module(i,0,True) for i in range(mechanism.M["count"])}
    rest=fixed+[item for items in modules.values() for item in items]
    cache={}
    results=[audit(items=rest,cache=cache)]
    print(f"rest: {len(results[-1]['collisions'])} collisions",flush=True)
    transport=deepcopy(harness.HARNESSES)
    for wire in transport:
        if wire["transport_points"] is not None:
            wire["points"]=wire["transport_points"]
            wire["from"]=wire["transport_from"]
    results.append(audit(transport,scene(lid=0,covers=True,transport=True),
                         scope="closed transport with bank removed and source plug parked",cache=cache))
    print(f"transport: {len(results[-1]['collisions'])} collisions",flush=True)
    for module in range(1,7):
        for angle in (80,160):
            changed=[wire for wire in harness.HARNESSES if any(
                int(row["component"].split("_")[-1])==module for row in wire["moving_endpoints"])]
            moving_items=mechanism.module(module-1,angle,True)
            pose=fixed+[item for i,items in modules.items() if i!=module-1 for item in items]+moving_items
            changed_ids={wire["id"] for wire in changed}
            wires=deepcopy(changed)
            for wire in wires:
                wire["points"]=points_for_pose(wire,module,angle)
            scope=f"module {module} at {angle} degrees, sampled motion"
            results.append(audit(wires,pose,scope=scope,cache=cache))
            results.append(audit([wire for wire in harness.HARNESSES if wire["id"] not in changed_ids],
                                 moving_items,scope=scope+" stationary wires vs moving hardware",cache=cache))
            print(f"module {module} angle {angle}: {sum(len(row['collisions']) for row in results[-2:])} collisions",flush=True)
    collision_rows=[dict(row,state=result["scope"]) for result in results for row in result["collisions"]]
    stable=initial==source_fingerprint()
    packing=bundle_fit(harness.HARNESSES)
    self_geometry=verify_self_geometry()
    return {"status":"PASS" if all(result["status"]=="PASS" for result in results) and stable and packing["status"]=="PASS" and self_geometry["status"]=="PASS" else "FAIL",
            "coverage":{"rest_covers":True,"transport":True,"motion_angles":[0,80,160],"modules":list(range(1,7)),
                        "motion":"sampled positions, not a continuous cable bend or swept-curve proof",
                        "capsule_elbows":True},
            "collisions":collision_rows,"bundle_fit":packing,"self_geometry":self_geometry,"sources":initial,
            "bounds_failures":[dict(row,state=result["scope"]) for result in results for row in result["bounds_failures"]],
            "sources_unchanged_during_audit":stable,"truncated":False,
            "endpoint_contacts":[dict(row,state=result["scope"]) for result in results for row in result["endpoint_contacts"]],
            "brep_pairs_checked":sum(result["brep_pairs_checked"] for result in results),
            "physical_verification":"NOT PERFORMED"}


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--limit",type=int)
    parser.add_argument("--all-states",action="store_true")
    parser.add_argument("--output",type=Path)
    args=parser.parse_args()
    initial=source_fingerprint()
    result=verify_all_states() if args.all_states else audit(max_collisions=args.limit)
    result["sources"]=initial
    result["sources_unchanged_during_audit"]=initial==source_fingerprint()
    if not result["sources_unchanged_during_audit"]:
        result["status"]="STALE"
    if args.output:
        args.output.write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    print(json.dumps({k:v for k,v in result.items() if k not in ("collisions","endpoint_contacts","sources")},indent=2))
    print("collisions",len(result["collisions"]),"contacts",len(result["endpoint_contacts"]))
    return 0 if result["status"]=="PASS" else 1


if __name__=="__main__":
    raise SystemExit(main())
