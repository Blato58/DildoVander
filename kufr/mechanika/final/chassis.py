"""Existing wooden case and four bolted insert panels. No case drilling."""
from functools import lru_cache
import math
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
    from service_geometry import mount_points
    holes += mount_points()
    holes += [(20,27),(22,60)]
    holes += [(17.4,102),(54,170),(62,240)]
    return holes


def carrier_head_clearance(x,y):
    """Actual underside M3 head relief tool, including 0.1 mm overrun."""
    return hole_z(x,y,6.2,z=6.35,height=1.75)


def frame_head_clearance(x,y):
    """Actual M4 panel head tool; the panel clips it to 0.5 mm depth."""
    return hole_z(x,y,7.5,z=F["floor_anchors"]["frame_head_z"],height=5)


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


def rounded_case_volume(width, x, front, rear, floor, radius, top):
    """Extrude the measured lower YZ tangents as two quarter-circle arcs."""
    diagonal=radius/math.sqrt(2)
    profile=(cq.Workplane("YZ",origin=(x,0,0)).moveTo(front,top)
             .lineTo(front,floor+radius)
             .threePointArc((front+radius-diagonal,floor+radius-diagonal),(front+radius,floor))
             .lineTo(rear-radius,floor)
             .threePointArc((rear-radius+diagonal,floor+radius-diagonal),(rear,floor+radius))
             .lineTo(rear,top).close())
    return profile.extrude(width).val()


@lru_cache
def case_floor_space(offset=0):
    """Interior above the curved floor; offset is a vertical adhesive gap.

    XY zero is the front-left corner of the CLEAR rim rectangle. The main
    front/rear walls are 15 mm beyond that rectangle, below the rim ledges.
    """
    inset=C["reinforcement_width"]-C["wall"]
    return rounded_case_volume(X,0,-inset,Y+inset,offset,C["bottom_radius"],C["base_height"]+1)


@lru_cache
def shell(lid=False):
    wall=C["wall"]; rim=C["reinforcement_width"]
    if lid:
        # The unmeasured lid uses the matching reference profile. It is not a
        # manufacturing drawing; the measured base remains the fit authority.
        outer=rounded_case_volume(X+2*wall,-wall,-rim,Y+rim,0,C["bottom_radius"]+wall,C["lid_height"])
        void=case_floor_space(C["floor"])
        return outer.cut(void).clean()
    outer=rounded_case_volume(X+2*wall,-wall,-rim,Y+rim,-C["floor"],
                              C["bottom_radius"]+C["floor"],C["base_height"])
    ledges=[box(X,rim-wall,height,(0,y,C["base_height"]-height))
            for y,height in ((wall-rim,C["front_reinforcement_height"]),
                             (Y,C["rear_reinforcement_height"]))]
    return fuse([outer.cut(case_floor_space())]+ledges)


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
    # Top-removable electronics cassettes have deck nuts loaded on the bench.
    from service_geometry import mount_points, NUT_Z
    for xx,yy in mount_points():
        holes.append(hex_prism(xx,yy,NUT_Z-.1,6.7,2.5))
    for xx,yy in seam_floor_holes():
        holes.append(cq.Solid.makeCone(3.3,1.7,1.6,cq.Vector(xx,yy,z+t),cq.Vector(0,0,-1)))
    for xx,yy in rack_holes():
        # ISO4762 M4 head sits 0.5 mm into the panel, on a real annular seat.
        holes.append(frame_head_clearance(xx,yy))
    result=fuse([result]+[rail_shape(spec) for spec in F["stiffeners"]])
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
            # The integral uprights butt at the central gap; floor cradles carry
            # the module load directly, without an unsupported plastic beam.
            x0=x if ix==0 else x+w/2+gap/2
            x1=x+w if ix==1 else x+w/2-gap/2
            y0=y if iy==0 else y+d/2+gap/2
            y1=y+d if iy==1 else y+d/2-gap/2
            chunks.append(frame().intersect(box(x1-x0,y1-y0,30,(x0,y0,z+t))))
            panels.append(fuse(chunks))
    return panels


def hex_prism(x,y,z,diameter,height):
    return cq.Workplane("XY",origin=(x,y,z)).polygon(6,diameter).extrude(height).val()


def floor_anchor_specs():
    a=F["floor_anchors"]
    x,y,_=F["origin"]
    w,_,_=F["size"]
    middle=x+w/2
    result={}
    for end in ("front","rear"):
        y0,y1=a[end+"_range"]
        cross0,cross1=a[end+"_cross_range"]
        for side in ("left","right"):
            x0=x if side=="left" else middle+a["split_gap"]/2
            x1=middle-a["split_gap"]/2 if side=="left" else x+w
            leg0=x if side=="left" else x+w-a["side_width"]
            result[f"floor_anchor_{end}_{side}"]={
                "end":end,"side":side,"x_range":(x0,x1),"y_range":(y0,y1),
                "cross_range":(cross0,cross1),"leg_x":leg0}
    return result


@lru_cache
def floor_anchor_planar_blank(name):
    a=F["floor_anchors"]; spec=floor_anchor_specs()[name]
    x0,x1=spec["x_range"]; y0,y1=spec["y_range"]
    c0,c1=spec["cross_range"]
    return fuse([box(x1-x0,c1-c0,a["height"],(x0,c0,a["z"])),
                 box(a["side_width"],y1-y0,a["height"],(spec["leg_x"],y0,a["z"]))])


@lru_cache
def floor_anchor_blank(name):
    # Keep the printable anchor top at Z8 and conform only its underside to
    # the measured curve, leaving a constant 0.5 mm vertical epoxy film.
    return floor_anchor_planar_blank(name).intersect(case_floor_space(F["floor_anchors"]["z"])).clean()


def anchor_nuts(name):
    spec=floor_anchor_specs()[name]; a=F["floor_anchors"]
    x0,x1=spec["x_range"]; c0,c1=spec["cross_range"]
    result=[]
    for kind,points,diameter,z,pocket,bore in (
            ("module",module_holes(),5.5/math.cos(math.pi/6),a["module_nut_z"],6.7,3.4),
            ("frame",rack_holes(),7/math.cos(math.pi/6),a["frame_nut_z"],8.5,4.5)):
        for x,y in points:
            if x0<x<x1 and c0<y<c1:
                result.append({"kind":kind,"x":x,"y":y,"z":z,
                               "diameter":diameter,"pocket":pocket,"bore":bore})
    return result


def anchor_nut_clearances(nut, edge):
    """Production hex pocket, side-loading tunnel and blind screw-tip bore.

    Coupon callers translate these exact tools rather than reconstructing a
    similar pocket, so printer fit and unsupported roof bridges match anchors.
    """
    x,y=nut["x"],nut["y"]
    bottom=nut["z"]-.1
    roof=F["floor_anchors"]["nut_roof_z"]
    diameter=nut["pocket"]
    # Tunnel ends at the nut centre so the far hex half prevents rotation.
    return (hex_prism(x,y,bottom,diameter,roof-bottom),
            box(diameter,abs(y-edge),roof-bottom,
                (x-diameter/2,min(y,edge),bottom)),
            hole_z(x,y,nut["bore"],z=1.0,height=7.1).intersect(case_floor_space(1)))


@lru_cache
def floor_anchor_clearances(name):
    """Closed roofs, side insertion, blind tips and explicit underside reliefs."""
    spec=floor_anchor_specs()[name]
    tools=[]
    edge=spec["y_range"][0] if spec["end"]=="front" else spec["y_range"][1]
    for nut in anchor_nuts(name):
        tools.extend(anchor_nut_clearances(nut,edge))
    # Carriers/guide bolts are preassembled outside the case. These cavities
    # allow their real heads/nuts without opening any module/frame nut roof.
    tools += [carrier_head_clearance(x,y) for x,y in carrier_holes()]
    from service_geometry import mount_points, NUT_Z
    tools += [hex_prism(x,y,NUT_Z-.1,6.7,.2) for x,y in mount_points()]
    tools += [hole_z(x,y,8.2,z=5.2,height=2.9) for x,y in guide_holes()]
    tools += [hex_prism(x,y,5.6,6.7,2.5) for x,y in seam_floor_holes()]
    return tuple(tools)


def floor_anchor_key_shape(x,y):
    """Production epoxy key void, shared with the printer-fit coupon."""
    a=F["floor_anchors"]
    neck=cylinder(a["key_neck_diameter"]/2,a["key_head_bottom"]-a["z"],(x,y,a["z"]))
    head=cylinder(a["key_head_diameter"]/2,a["key_head_top"]-a["key_head_bottom"],(x,y,a["key_head_bottom"]))
    # Two 2.5 mm vents/witnesses reach the top. Paste is prepacked through
    # the open underside neck, never syringe-injected.
    mouths=[cylinder(1.25,8-a["key_head_top"],(x+dx,y,a["key_head_top"])) for dx in (-2,2)]
    return fuse([neck,head]+mouths)


@lru_cache
def floor_anchor_keys(name):
    """Epoxy mushroom keys; prepack through bottom neck before bonding."""
    a=F["floor_anchors"]; blank=floor_anchor_blank(name)
    spec=floor_anchor_specs()[name]
    x0,x1=spec["x_range"]; y0,y1=spec["y_range"]
    radius=a["key_head_diameter"]/2
    exclusions=floor_anchor_clearances(name)
    layout=a["key_layouts"][name]
    keys=[]
    # Regular candidates are accepted by actual solid intersection, not by an
    # assumed full rectangular footprint. A 2 mm radial web is mandatory.
    for row in range(int((y1-y0-layout["offset_y"])/layout["pitch_y"])+1):
        y=y0+layout["offset_y"]+row*layout["pitch_y"]
        for column in range(int((x1-x0-layout["offset_x"])/layout["pitch_x"])+1):
            x=x0+layout["offset_x"]+column*layout["pitch_x"]+(row%2)*layout["row_stagger_x"]
            clearance=cylinder(radius+2,a["height"],(x,y,a["z"]))
            if abs(clearance.intersect(blank).Volume()-clearance.Volume())>1e-5:
                continue
            if any(clearance.intersect(tool).Volume()>1e-6 for tool in exclusions):
                continue
            keys.append({"x":x,"y":y,"shape":floor_anchor_key_shape(x,y)})
    return tuple(keys)


@lru_cache
def floor_anchor_shape(name):
    return cut(floor_anchor_blank(name),list(floor_anchor_clearances(name))+
               [key["shape"] for key in floor_anchor_keys(name)])


@lru_cache
def floor_bond_projected_film(name):
    """Planar XY projection used by the conservative bond load screen."""
    a=F["floor_anchors"]
    footprint=floor_anchor_planar_blank(name).intersect(box(X,Y,a["z"],(0,0,a["z"])))
    return footprint.translate((0,0,-a["z"]))


@lru_cache
def floor_bond_film(name):
    a=F["floor_anchors"]
    # A curved film has the same projected XY area as the flat calculation,
    # but its actual solid follows the wooden surface instead of filling air.
    return floor_anchor_planar_blank(name).translate((0,0,-a["z"])).intersect(
        case_floor_space()).cut(case_floor_space(a["z"])).clean()


@lru_cache
def floor_bond_shape(name):
    film=floor_bond_film(name)
    return fuse([film]+[key["shape"] for key in floor_anchor_keys(name)])


def floor_anchor_metrics():
    a=F["floor_anchors"]; metrics={}
    for name in floor_anchor_specs():
        shape=floor_anchor_shape(name); keys=floor_anchor_keys(name)
        film=floor_bond_projected_film(name)
        area=film.Volume()/a["z"]
        centroid=film.Center(); inertia=cq.Shape.matrixOfInertia(film)
        bb=film.BoundingBox()
        # Volume inertia of a uniform film / thickness gives planar moments.
        ix=(inertia[0][0]-area*a["z"]**3/12)/a["z"]
        iy=(inertia[1][1]-area*a["z"]**3/12)/a["z"]
        sx=ix/max(centroid.y-bb.ymin,bb.ymax-centroid.y)
        sy=iy/max(centroid.x-bb.xmin,bb.xmax-centroid.x)
        ixy=-inertia[0][1]/a["z"]
        bottom=next(face for face in film.Faces() if face.geomType()=="PLANE"
                    and abs(face.Center().z-bb.zmin)<1e-6 and face.normalAt().z<-.9)
        boundary=sorted({(vertex.Center().x,vertex.Center().y) for vertex in bottom.outerWire().Vertices()})
        # PETG/film interface is net of the actual key neck openings.
        interface=shape.intersect(case_floor_space(a["z"])).cut(case_floor_space(a["z"]+.01))
        net=interface.Volume()/.01
        roof=[]
        for nut in anchor_nuts(name):
            contact=hex_prism(nut["x"],nut["y"],5,nut["diameter"],.01)
            roof.append({"kind":nut["kind"],"x":nut["x"],"y":nut["y"],
                         "bearing_area_mm2":shape.intersect(contact).Volume()/.01,
                         "pocket_perimeter_mm":3*nut["pocket"],"roof_mm":3.0})
        rows=sorted({nut["y"] for nut in anchor_nuts(name)})
        # Source-derived net transverse roof sections through actual bolt rows.
        # This is a nominal section, not a crack/notch or beam qualification.
        net_sections={str(row):shape.intersect(box(X,.01,3,(0,row-.005,5))).Volume()/.01
                      for row in rows}
        metrics[name]={"wood_bond_area_mm2":area,"petg_film_contact_mm2":net,
                       "key_count":len(keys),"epoxy_volume_mm3":floor_bond_shape(name).Volume(),
                       "key_head_retaining_area_mm2":len(keys)*math.pi*((a["key_head_diameter"]/2)**2-(a["key_neck_diameter"]/2)**2),
                       "printed_100_percent_volume_mm3":shape.Volume(),
                       "printed_100_percent_mass_g":shape.Volume()/1000*PARAMS["print"]["density_g_cm3"],
                       "bond_section_modulus_min_mm3":min(sx,sy),
                       "wood_bond_centroid_xy_mm":[centroid.x,centroid.y],
                       "wood_bond_area_second_moment_tensor_mm4":[[iy,ixy],[ixy,ix]],
                       "wood_bond_boundary_vertices_xy_mm":[list(point) for point in boundary],
                       "wood_bond_tensor_convention":"projected XY integral r*r^T dA; curved-surface traction is not physically qualified; CadQuery Ixy negated, x/y diagonals swap",
                       "nut_row_roof_net_sections_mm2":net_sections,
                       "nut_roofs":roof}
    return metrics


def parts():
    return [Part(f"frame_panel_{i+1}",s,1,"12 mm bolted half-lap; independent four-M3 module anchors")
            for i,s in enumerate(tiles())] + [
                Part(name,floor_anchor_shape(name),1,"100% infill PETG; flat top on bed, curved underside upward; captive nuts and cast epoxy keys; inspect internal bridges",((1,0,0),180))
                for name in floor_anchor_specs()]


def items(angle=105):
    result=[Item("wooden_case",shell(),"case","shell",C["geometry_status"]),
            Item("wooden_lid",lid_position(shell(True),angle),"lid","shell",C["geometry_status"]),
            Item("floor_brace",box(X,C["brace_width"],C["brace_height"],(0,C["brace_y"],0)),"case","shell","Owner measured full-width centered brace 80 x 5 mm on 2026-10-12")]
    result += [Item(f"frame_panel_{i+1}",s,"fixed","mechanism") for i,s in enumerate(tiles())]
    for name in floor_anchor_specs():
        result.append(Item(name,floor_anchor_shape(name),"fixed","mechanism","Printed floor cradle; no wooden holes"))
        result.append(Item(name.replace("floor_anchor_","wood_bond_"),floor_bond_shape(name),"bond","white","Bare-wood epoxy film plus mechanically captured cast keys; proof pending"))
    a=F["floor_anchors"]
    for side,xx in enumerate(F["anchor_x"]):
        for j,hy in enumerate(F["anchor_hole_y"]):
            ident=f"{side+1}_{j+1}"
            head=a["frame_head_z"]
            nut=hex_prism(xx,hy,a["frame_nut_z"],7/math.cos(math.pi/6),3.2).cut(hole_z(xx,hy,4,z=1.7,height=3.4))
            result += [Item(f"anchor_m4_{ident}",cylinder(2,a["frame_screw_length"],(xx,hy,head-a["frame_screw_length"])),"fastener","metal","ISO4762 M4x10; blind tip, captive hex nut"),
                       Item(f"anchor_head_{ident}",cylinder(3.5,4,(xx,hy,head)),"hardware","metal","ISO4762 diameter 7 height 4"),
                       Item(f"anchor_nut_{ident}",nut,"hardware","metal","ISO4032 M4 AF7 height3.2; roof prevents pullout")]
    for j,(x,y) in enumerate(seam_floor_holes()):
        result += [Item(f"frame_seam_m3_{j+1}",cylinder(1.5,6,(x,y,6)),"fastener","metal","M3 countersunk x6"),
                   Item(f"frame_seam_nut_{j+1}",hex_prism(x,y,5.6,5.5/math.cos(math.pi/6),2.4).cut(hole_z(x,y,3,z=5.5,height=3)),"hardware","metal","M3 hex AF5.5, height2.4; clearance pocket in floor cradle")]
    return result
