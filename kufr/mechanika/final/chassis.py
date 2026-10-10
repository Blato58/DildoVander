"""Existing wooden case and four bolted insert panels. No case drilling."""
from functools import lru_cache
import cadquery as cq
from geometry import PARAMS, Part, Item, box, cylinder, fuse, cut, hole_z
from mechanism import MOUNT_HOLES, GUIDES, M

C = PARAMS["case"]
F = PARAMS["frame"]
X, Y = C["inside_x"], C["inside_y"]
HINGE = (0, Y + (C["outer_y"]-Y)/2, C["base_height"])


def module_holes():
    return [(M["origin_x"]+i*M["pitch"]+x, M["origin_y"]+y)
            for i in range(M["count"]) for x, y in MOUNT_HOLES]


def guide_holes():
    return [(M["origin_x"]+i*M["pitch"]+x,M["origin_y"]+y)
            for i in range(M["count"]) for x,y in GUIDES]


def seam_floor_holes():
    x, y, _ = F["origin"]
    w, d, _ = F["size"]
    return [(x+w/2, yy) for yy in (y+12, y+d/2-30, y+d/2+30, y+d-12)] + [
        (xx, y+d/2) for xx in (x+12, x+70, x+w-70, x+w-12)]


def rack_holes():
    return [(x, y) for x in F["anchor_x"] for y in F["anchor_hole_y"]]


def carrier_holes():
    excluded={"breaker","buck","amplifier_left","perfboard_1","perfboard_2","degson_1","degson_2","degson_3","camera","microphone"}
    from layout import mount_positions
    holes=[]
    for name,spec in PARAMS["components"].items():
        if name in excluded:
            continue
        holes += mount_positions(name)
    holes += [(x,y) for x,y,_ in PARAMS["capacitors"][2:]]
    holes += [(xx,yy) for xx in (22,64) for yy in (68,92)]
    holes += [(xx,yy) for xx in (365,410) for yy in (160,247)]
    holes += [(20,27),(22,60)]
    holes += [(17.4,102),(54,170),(62,240)]
    return holes


def rail_shape(spec):
    x,y,z=spec["origin"]
    t,leg=spec["thickness"],spec["leg"]
    x0,x1=spec["upright_x"]
    result=fuse([box(spec["length"],leg,t,(x,y,z)),
                 box(x1-x0,t,leg-t,(x0,spec["upright_y"],z+t))])
    holes=[hole_z(xx,yy,3.4,z=z-1,height=t+2) for xx,yy in module_holes()+carrier_holes()+seam_floor_holes()]
    holes += [hole_z(xx,yy,4.5,z=z-1,height=t+2) for xx,yy in rack_holes()]
    if spec["id"]=="front_crossrail":
        sx,sy,sz=PARAMS["components"]["microphone"]["origin"]
        sw=PARAMS["components"]["microphone"]["size"][0]
        holes += [cylinder(1.7,6,(xx,13,24),(0,1,0)) for xx in (sx+3,sx+sw-3)]
        holes += [cq.Solid.makeCone(3.1,1.5,1.6,cq.Vector(xx,14,24),cq.Vector(0,1,0)) for xx in (sx+3,sx+sw-3)]
    else:
        holes += [cylinder(1.7,6,(xx,248,24),(0,1,0)) for xx in (184,247)]
        holes += [cq.Solid.makeCone(3.1,1.5,1.6,cq.Vector(xx,253,24),cq.Vector(0,-1,0)) for xx in (184,247)]
    return cut(result,holes)


@lru_cache
def shell(lid=False):
    # Only outer/inner envelopes are confirmed. Wall distribution and hinge
    # axis are visualization assumptions, not woodworking instructions.
    dx, dy = (C["outer_x"]-X)/2, (C["outer_y"]-Y)/2
    if lid:
        outer = box(C["outer_x"], C["outer_y"], C["lid_height"], (-dx,-dy,0))
        void = box(X,Y,C["lid_height"], (0,0,C["floor"]))
    else:
        outer = box(C["outer_x"],C["outer_y"],C["base_height"]+C["floor"],(-dx,-dy,-C["floor"]))
        void = box(X,Y,C["base_height"]+1)
    return outer.cut(void).clean()


def lid_position(shape, angle=0):
    installed = shape.rotate((0,0,0),(0,1,0),180).translate(
        (X,0,C["base_height"]+C["lid_height"]))
    return installed.rotate(HINGE,(1,HINGE[1],HINGE[2]),-angle)


@lru_cache
def frame():
    x,y,z = F["origin"]
    w,d,t = F["size"]
    result = box(w,d,t,(x,y,z))
    holes = [hole_z(xx,yy,PARAMS["mounting"]["m3_clearance"],z=z-1,height=t+2)
             for xx,yy in module_holes()+seam_floor_holes()+carrier_holes()]
    holes += [hole_z(xx,yy,PARAMS["mounting"]["m4_clearance"],z=z-1,height=t+2)
              for xx,yy in rack_holes()+guide_holes()]
    for xx,yy in seam_floor_holes():
        holes.append(cq.Solid.makeCone(3.3,1.7,1.6,cq.Vector(xx,yy,z+t),cq.Vector(0,0,-1)))
    for spec in F["stiffeners"]:
        xx,yy,zz=spec["origin"]
        holes.append(box(spec["length"],spec["leg"],spec["thickness"],(xx,yy,zz)))
    return cut(result,holes)


@lru_cache
def tiles(lid=False):
    if lid:
        return [shell(True)]
    x,y,z = F["origin"]
    w,d,t = F["size"]
    lap, gap = F["joint_lap"], F["joint_clearance"]
    panels=[]
    for iy in range(2):
        for ix in range(2):
            chunks=[]
            for layer,shift in ((0,-lap/2),(1,lap/2)):
                x0=x if ix==0 else x+w/2+shift+gap/2
                x1=x+w if ix==1 else x+w/2+shift-gap/2
                y0=y if iy==0 else y+d/2+shift+gap/2
                y1=y+d if iy==1 else y+d/2+shift-gap/2
                chunks.append(frame().intersect(box(x1-x0,y1-y0,t/2,(x0,y0,z+layer*t/2))))
            panels.append(fuse(chunks))
    return panels


def angle_bracket(side, y):
    t,h,w,length = F["angle_thickness"],F["angle_vertical"],F["angle_horizontal"],F["angle_length"]
    z = F["origin"][2]-t
    gap = F["bond_gap"]
    x0 = gap if side==0 else X-gap-w
    wall_x = gap if side==0 else X-gap-t
    shape=fuse([box(w,length,t,(x0,y-length/2,z)),box(t,length,h,(wall_x,y-length/2,z))])
    hole_y=F["anchor_hole_y"][F["anchor_y"].index(y)]
    holes=[hole_z(F["anchor_x"][side],hole_y,PARAMS["mounting"]["m4_clearance"],z=z-1,height=t+2)]
    holes += [hole_z(xx,yy,6.2,z=z-1,height=t+2) for xx,yy in carrier_holes()]
    return cut(shape,holes)


def parts():
    return [Part(f"frame_panel_{i+1}",s,1,"12 mm bolted half-lap; independent four-M3 module anchors")
            for i,s in enumerate(tiles())]


def items(angle=105):
    result=[Item("wooden_case",shell(),"case","shell",C["geometry_status"]),
            Item("wooden_lid",lid_position(shell(True),angle),"lid","shell",C["geometry_status"]),
            Item("floor_brace",box(X,C["brace_width"],C["brace_height"],(0,C["brace_y"],0)),"case","shell","5 mm height confirmed; XY provisional")]
    result += [Item(f"frame_panel_{i+1}",s,"fixed","mechanism") for i,s in enumerate(tiles())]
    result += [Item(s["id"],rail_shape(s),"hardware","metal","Aluminium L20x20x3 EN AW6060-T66; cut upright ends, drill new rail only") for s in F["stiffeners"]]
    for side in range(2):
        for j,y in enumerate(F["anchor_y"]):
            ident=f"{side+1}_{j+1}"
            result.append(Item(f"bonded_angle_{ident}",angle_bracket(side,y),"hardware","metal","Aluminium 40x40x3 x50; drilled angle only"))
            x=0 if side==0 else X-F["bond_thickness"]
            result.append(Item(f"wood_bond_{ident}",box(F["bond_thickness"],F["angle_length"],F["angle_vertical"],(x,y-25,5)),"bond","white","2000 mm2 bare-wood interface; physical proof pending"))
            xx=F["anchor_x"][side]
            hy=F["anchor_hole_y"][j]
            result += [Item(f"anchor_m4_{ident}",cylinder(2,12,(xx,hy,2)),"fastener","metal","M4 x12 through new frame/rail/angle"),
                       Item(f"anchor_head_{ident}",cylinder(3.5,2,(xx,hy,12)),"hardware","metal"),
                       Item(f"anchor_nut_{ident}",cylinder(4,3.2,(xx,hy,1.8)).cut(cylinder(2,4,(xx,hy,1))),"hardware","metal")]
    for j,(x,y) in enumerate(seam_floor_holes()):
        result += [Item(f"frame_seam_m3_{j+1}",cylinder(1.5,6,(x,y,6)),"fastener","metal","M3 countersunk x6"),
                   Item(f"frame_seam_nut_{j+1}",cylinder(3.2,2.4,(x,y,5.6)).cut(cylinder(1.5,3,(x,y,5.5))),"hardware","metal")]
    return result
