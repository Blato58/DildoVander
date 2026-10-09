"""Original positive-return grooved cam. No legacy geometry imports.

Two circular dwells carry static loads; an adjustable felt guide brake retains
intermediate positions. Positive groove capture also drives the return against
that brake. Brake force and available servo torque require physical acceptance.
"""
from __future__ import annotations

import math
from functools import lru_cache

import cadquery as cq
from geometry import PARAMS, Part, Item, box, cylinder, fuse, cut, hole_z, strap_slots

M = PARAMS["mechanism"]
W = PARAMS["wand"]
GUIDES = ((8.0, 87.0), (38.0, 191.0))
MOUNT_HOLES = ((8.0, 9.0), (38.0, 9.0), (8.0, 203.0), (38.0, 203.0))


def smooth(t):
    return 10*t**3 - 15*t**4 + 6*t**5


def cam_law(degrees):
    """Groove centre radius and derivatives with respect to radians."""
    a = degrees % 360
    d, travel = M["dwell_degrees"], M["travel_degrees"]
    if a <= d or a >= 360 - d:
        return M["cam_low_radius"], 0.0, 0.0
    if travel - d <= a <= travel + d:
        return M["cam_low_radius"] + M["lift"], 0.0, 0.0
    if a < travel - d:
        start, span, sign, offset = d, travel - 2*d, 1, 0
    else:
        start, span, sign, offset = travel + d, 360 - travel - 2*d, -1, 1
    t = (a - start) / span
    r = math.radians(span)
    s = offset + sign * smooth(t)
    derivative = sign * 30*t*t*(1-t)*(1-t) / r
    second = sign * (60*t - 180*t*t + 120*t*t*t) / (r*r)
    return M["cam_low_radius"] + M["lift"]*s, M["lift"]*derivative, M["lift"]*second


def cam_points(step=1):
    points = []
    for degree in range(0, 360, step):
        phi = math.radians(90 - degree)
        h, _, _ = cam_law(degree)
        points.append((h*math.cos(phi), h*math.sin(phi)))
    return points


def cam_wire(offset=0.0, x=0.0):
    """Circular dwells plus smooth normal-offset flanks, without micro-facets."""
    def point(degree):
        phi = math.radians(90-degree)
        radius, derivative, _ = cam_law(degree)
        speed = math.hypot(radius, derivative)
        ny = (radius*math.cos(phi)-derivative*math.sin(phi))/speed
        nz = (radius*math.sin(phi)+derivative*math.cos(phi))/speed
        return cq.Vector(x, radius*math.cos(phi)+offset*ny, radius*math.sin(phi)+offset*nz)

    dwell, travel = M["dwell_degrees"], M["travel_degrees"]
    edges = []
    for start, end, circular in ((-dwell,dwell,True), (dwell,travel-dwell,False),
                                 (travel-dwell,travel+dwell,True), (travel+dwell,360-dwell,False)):
        if circular:
            edges.append(cq.Edge.makeThreePointArc(point(start),point((start+end)/2),point(end)))
        else:
            segments = math.ceil(end-start)
            edges.append(cq.Edge.makeSpline([point(start+(end-start)*i/segments) for i in range(segments+1)]))
    return cq.Wire.assembleEdges(edges)


@lru_cache
def cam():
    shape = cq.Solid.extrudeLinear(cam_wire(M["groove_width"]/2+3),[],(M["cam_thickness"],0,0))
    # True normal offsets preserve the roller clearance at the steep flank.
    floor = M["cam_thickness"]-M["groove_depth"]
    groove = cq.Solid.extrudeLinear(cam_wire(M["groove_width"]/2,floor),[cam_wire(-M["groove_width"]/2,floor)],(M["groove_depth"]+1,0,0))
    through = M["cam_thickness"]+2
    cutters = [groove, cylinder(3.2, through, (-1, 0, 0), (1, 0, 0))]
    # Through slots accept actual holes on the purchased 35 mm double horn.
    # These are adjustable slots, not an invented horn drilling pattern.
    for sign in (-1, 1):
        inner, outer = M["horn_slot_inner_radius"], M["horn_slot_outer_radius"]
        y0 = inner if sign > 0 else -outer
        cutters.append(box(through, outer-inner, M["horn_screw_clearance"], (-1, y0, -1.2)))
        for y in (sign*inner, sign*outer):
            cutters.append(cylinder(1.2, through, (-1, y, 0), (1, 0, 0)))
        # Nut sits below the front face and cannot sweep into the follower lug.
        cutters.append(cylinder(2.4,1.9,(M["cam_thickness"]-1.8,sign*M["horn_fastener_radius"],0),(1,0,0)))
    # Stock horn on the rear side; the groove is on the opposite face.
    cutters.append(box(2.4, 36, 6.4, (0, -18, -3.2)))
    cutters.append(cylinder(5,2.4,axis=(1,0,0)))
    return cut(shape, cutters)


@lru_cache
def base():
    length, width = M["length"], M["base_width"]
    pieces = [box(width, length, 4)]
    for x, y in GUIDES:
        pieces.append(box(8, 10, M["guide_height"]-4, (x-4, y-5, 4)))
        # Broad roots below the lowest travel of the follower sleeves.
        pieces.append(box(14, 20, 25, (x-7, y-10, 4)))
    # Anti-rotation servo cradle. The known body is 27 X / 22 Y / 11.5 Z.
    # The body-to-shaft offset remains a mounting/calibration assumption.
    pieces.append(box(27.3, 27.6, 26.0, (0.2, 104.2, 4)))
    pieces.append(box(27.3, 2.5, 15.5, (0.2, 103.5, 29)))
    pieces.append(box(27.3, 2.5, 15.5, (0.2, 129.5, 29)))
    # Raised edge ribs stiffen the thin floor without intersecting the cam.
    pieces.extend([box(3, length, 4, (0, 0, 4)), box(3, length, 4, (width-3, 0, 4))])
    result = fuse(pieces)
    holes = [hole_z(x, y, 4.5) for x, y in MOUNT_HOLES + GUIDES]
    # Two releasable 2.5 mm cable ties retain the servo against the hard cradle.
    for x in (7, 21):
        holes.append(box(3, 31, 3, (x-1.5, 103, 24)))
    # Transport restraint threads through the structural base, independently of tray.
    holes += [box(3, 18, 10, (x, 58, -1)) for x in (4, 39)]
    # Shallow open pocket clears the enlarged cam rim; 2.5 mm of floor remains.
    holes.append(box(10.5,70,2,(29,77,2.5)))
    return cut(result, holes)


@lru_cache
def tray():
    """Top at z=3; upside-down printing puts the flat fabric face on the bed."""
    pieces = [box(M["tray_width"], M["length"], M["tray_thickness"], (1, 0, 0))]
    holes = []
    for x, y in GUIDES:
        sleeve_y = 24 if y == 87 else 16
        pieces.append(box(14, sleeve_y, M["sleeve_length"], (x-7, y-sleeve_y/2, -M["sleeve_length"])))
        c = M["guide_clearance"]
        holes.append(box(8+2*c, 10+2*c, 32, (x-4-c, y-5-c, -25)))
    # Split front sleeve and transverse screw set the felt brake preload.
    holes.append(box(0.8,24,22,(7.6,75,-22)))
    holes.append(cylinder(1.7,18,(-1,78.5,-11),(1,0,0)))
    # Four-mm lug leaves room for a washer, ordinary M3 nut and screw tip.
    pieces.append(box(4,12,23,(40,106,-23)))
    holes.append(cylinder(1.7,6,(39,112,-M["follower_drop"]),(1,0,0)))
    pieces.append(box(3,200,10,(42,6,-10)))
    # A flat central spine resists bending.
    pieces.append(box(4, 196, 8, (20, 8, -8)))
    # Stops surround U059 without relying solely on adhesive retention.
    for x in (5, 38):
        pieces.append(box(2.0, 26, 11, (x, 24, -11)))
    pieces.append(box(35, 2, 11, (5, 22, -11)))
    result = fuse(pieces)
    # The spine is interrupted at the actual U059 body and at its wire exit.
    result = result.cut(box(33, 39, 12, (7, 23, -12))).clean()
    for y in (16, 64, 167):
        holes.extend(strap_slots(23, y, 37, width=8, z=-12, height=17))
    holes.extend(strap_slots(23, 38, 37, width=12, z=-12, height=17))
    return cut(result, holes)


def wand():
    """Conservative silhouette envelope, not a reconstructed ornament."""
    head = box(W["head_width"], W["head_length"], W["head_height"],
               (23-W["head_width"]/2, 6, 0))
    shaft = cylinder(W["shaft_diameter"]/2, W["length"]-W["head_length"],
                     (23, 6+W["head_length"], W["shaft_diameter"]/2), (0, 1, 0))
    return head.fuse(shaft).clean()


def parts():
    return [Part("lift_base", base(), 6, "Servo cradle, two guides and transport strap anchors in one print"),
            Part("wand_bed", tray(), 6, "Single 212 mm bed with integral guide sleeves and U059 stops", ((1,0,0),180)),
            Part("dwell_cam", cam(), 6, "Captured roller drives both directions; two circular dwells", ((0,1,0),-90))]


def module(index=0, angle=0, with_wand=True):
    x0 = M["origin_x"] + M["pitch"]*index
    location = (x0, M["origin_y"], M["origin_z"])
    radius = cam_law(angle)[0]
    tray_z = M["cam_z"] + radius + M["follower_drop"]
    shapes = [Item(f"base_{index+1}", base(), "fixed", "mechanism"),
              Item(f"tray_{index+1}", tray().translate((0,0,tray_z)), "moving", "tray"),
              Item(f"cam_{index+1}", cam().rotate((0,0,0),(1,0,0),angle).translate((M["cam_x"],M["cam_y"],M["cam_z"])), "moving", "metal"),
              Item(f"servo_{index+1}", box(27,22,11.5,(0.5,106.5,30.25)), "hardware", "black", "22 x 11.5 x 27 catalog; shaft offset provisional"),
              Item(f"stock_horn_{index+1}", fuse([box(2,35,6,(0.2,-17.5,-3)),cylinder(4.5,2,(0.2,0,0),(1,0,0))]).rotate((0,0,0),(1,0,0),angle).translate((29,112,36)), "moving_hw", "white", "35 mm double horn; width/hub/thickness are mounting allowances, not measured"),
              Item(f"u059_{index+1}", box(32,24,8,(7.5,24,tray_z-10)), "moving_hw", "pcb", "32 x 24 x 8 catalog"),
              Item(f"led_{index+1}", box(3,200,10,(45,6,tray_z-7)), "moving_hw", "light", "10 x 200; thickness 3 provisional"),
              Item(f"roller_{index+1}", cylinder(M["roller_diameter"]/2,3,(35.7,112,M["cam_z"]+radius),(1,0,0)).cut(cylinder(2.1,3,(35.7,112,M["cam_z"]+radius),(1,0,0))), "moving_hw", "white", "6 mm OD / 4.2 ID x 3 nylon roller on brass sleeve"),
              Item(f"follower_head_{index+1}", cylinder(2.85,1.65,(33.85,112,M["cam_z"]+radius),(1,0,0)), "moving_hw", "metal", "M3 ISO7380 head envelope"),
              Item(f"follower_sleeve_{index+1}", cylinder(2,4.5,(35.5,112,M["cam_z"]+radius),(1,0,0)).cut(cylinder(1.6,4.5,(35.5,112,M["cam_z"]+radius),(1,0,0))), "moving_hw", "metal", "4 / 3.2 x 4.5 brass bearing sleeve carries clamp force"),
              Item(f"follower_spacer_{index+1}", cylinder(3,1.1,(38.9,112,M["cam_z"]+radius),(1,0,0)).cut(cylinder(2.1,1.1,(38.9,112,M["cam_z"]+radius),(1,0,0))), "moving_hw", "white", "6 / 4.2 x 1.1 spacer; 0.4 mm total roller axial clearance"),
              Item(f"follower_washer_{index+1}", cylinder(3.5,.5,(44,112,M["cam_z"]+radius),(1,0,0)).cut(cylinder(1.6,.5,(44,112,M["cam_z"]+radius),(1,0,0))), "moving_hw", "metal"),
              Item(f"follower_nut_{index+1}", cylinder(3.2,2.4,(44.5,112,M["cam_z"]+radius),(1,0,0)).cut(cylinder(1.5,2.4,(44.5,112,M["cam_z"]+radius),(1,0,0))), "moving_hw", "metal", "M3 nut circumscribed envelope; metal thread retention"),
              Item(f"follower_pin_{index+1}", cylinder(1.5,12,(35.5,112,M["cam_z"]+radius),(1,0,0)), "fastener", "metal")]
    for sign in (-1,1):
        y = sign*M["horn_fastener_radius"]
        for name,shape,group in (
            ("head",cylinder(1.75,1.3,(-1.1,y,0),(1,0,0)),"moving_hw"),
            ("nut",cylinder(2.31,1.6,(8.3,y,0),(1,0,0)).cut(cylinder(1.1,1.6,(8.3,y,0),(1,0,0))),"moving_hw"),
            ("shaft",cylinder(1,10,(.2,y,0),(1,0,0)),"fastener")):
            shapes.append(Item(f"coupler_{name}_{sign}_{index+1}",shape.rotate((0,0,0),(1,0,0),angle).translate((M["cam_x"],M["cam_y"],M["cam_z"])),group,"metal","M2 x 10; nut in recessed pocket; actual horn pitch pending"))
    if with_wand:
        shapes.append(Item(f"wand_{index+1}", wand().translate((0,0,tray_z+3+W["fabric"]+W["foam"])), "wand", "wand", W["dimensions_status"]))
    for x, y in GUIDES:
        shapes.append(Item(f"guide_bolt_{index+1}_{int(y)}", cylinder(2,90,(x,y,0)), "fastener", "metal"))
        shapes.append(Item(f"guide_washer_{index+1}_{int(y)}", cylinder(6,1.5,(x,y,83)).cut(cylinder(2.3,3,(x,y,82))), "fastener", "metal"))
    return [Item(i.name, i.shape.translate(location), i.group, i.color, i.source, i.allowance) for i in shapes]
