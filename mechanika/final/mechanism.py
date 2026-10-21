"""Original positive-return grooved cam. No legacy geometry imports.

Two circular dwells and lower hard stops carry static loads. Intermediate power
loss requires manual inspection/support; no friction brake or descent claim.
"""
from __future__ import annotations

import math
from functools import lru_cache

import cadquery as cq
from geometry import PARAMS, Part, Item, box, cylinder, fuse, cut, hole_z, strap_slots
from servo_reference import servo, servo_at_angle
import servo_horn

M = PARAMS["mechanism"]
W = PARAMS["wand"]
GUIDES = ((8.0, 87.0), (38.0, 191.0))
MOUNT_HOLES = ((8.0, 9.0), (38.0, 9.0), (8.0, 203.0), (38.0, 203.0))
U059_TIE_ROWS = (29.0, 42.5)
LED_TIE_ROWS = (29.0, 180.0)
SERVO_TIE_CENTRES = (10, 22)
SERVO_SUPPORT_Z = M["cam_z"] - 6.25 - 0.25


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


def retaining_ties(kind):
    ties=[]
    if kind=="servo":
        for x in SERVO_TIE_CENTRES:
            outer=box(2.5,28.8,M["cam_z"]+7.25-24,(x-1.25,103.5,24))
            inner=box(4,27.8,M["cam_z"]+6.75-24.5,(x-2,104,24.5))
            ties.append(outer.cut(inner))
    elif kind=="u059":
        for y in U059_TIE_ROWS:
            outer=box(35.5,2.5,9,(6,y-1.25,-10.5))
            inner=box(34.5,4,8,(6.5,y-2,-10))
            ties.append(outer.cut(inner))
    else:
        for y in LED_TIE_ROWS:
            outer=box(7,2.5,14,(41.5,y-1.25,-10.5))
            inner=box(6,4,13,(42,y-2,-10))
            ties.append(outer.cut(inner))
    return ties


@lru_cache
def cam():
    shape = cq.Solid.extrudeLinear(cam_wire(M["groove_width"]/2+3),[],(M["cam_thickness"],0,0))
    # True normal offsets preserve sliding-peg clearance at the steep flank.
    floor = M["cam_thickness"]-M["groove_depth"]
    groove = cq.Solid.extrudeLinear(cam_wire(M["groove_width"]/2,floor),[cam_wire(-M["groove_width"]/2,floor)],(M["groove_depth"]+1,0,0))
    through = M["cam_thickness"]+2
    cutters = [groove, box(through,6.4,6.4,(-1,-3.2,-3.2))]
    # Successive anchored bridges close the horn/hub pocket without roofing
    # an annulus around a round bore. The final square clearance still admits
    # the original 6.4mm circular envelope and needs no puncturing or support.
    cutters += [box(.4,36,6.4,(2.4,-18,-3.2)),
                box(.4,6.4,10,(2.8,-3.2,-5))]
    # Through slots accept actual holes on the purchased 35 mm double horn.
    # These are adjustable slots, not an invented horn drilling pattern.
    for sign in (-1, 1):
        inner, outer = M["horn_slot_inner_radius"], M["horn_slot_outer_radius"]
        y0 = inner if sign > 0 else -outer
        y0 = inner-1.2 if sign>0 else -outer-1.2
        cutters.append(box(through,outer-inner+2.4,M["horn_screw_clearance"],(-1,y0,-1.2)))
        cutters.append(box(.4,outer-inner+2.4,6.4,(2.8,y0,-3.2)))
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
        pieces.append(box(14,16 if y==191 else 20,25,(x-7,y-10,4)))
    # The supplied STEP is 12.5 mm thick along module Z. Leave 0.25 mm
    # below it; its shaft, cam and horn now share the raised 37 mm axis.
    pieces.append(box(27.3, 27.6, SERVO_SUPPORT_Z-4, (0.2, 104.2, 4)))
    pieces.append(box(27.3, 2.5, 17.5, (0.2, 103.5, 29)))
    pieces.append(box(27.3, 2.5, 17.5, (0.2, 129.5, 29)))
    # Grow the cradle lips at 45 degrees from the solid pedestal. Their old
    # abrupt 0.7/0.2 mm ledges started unsupported above the servo tie tunnels.
    for points in (((103.5,29),(104.2,28.3),(104.2,29)),
                   ((131.8,28.8),(132,29),(131.8,29))):
        pieces.append(cq.Workplane("YZ", origin=(.2,0,0)).polyline(points).close().extrude(27.3).val())
    # Raised edge ribs stiffen the thin floor without intersecting the cam.
    pieces.extend([box(3, length, 4, (0, 0, 4)), box(3, length, 4, (width-3, 0, 4))])
    # Sleeve bottoms bear directly on these broad feet at the lower dwell.
    stop_z = M["cam_z"]+M["cam_low_radius"]+M["follower_drop"]-M["sleeve_length"]
    for x,y in GUIDES:
        for xx in (x-7,x+4.5):
            pieces.append(box(2.5,14 if y==191 else 16,stop_z-4,(xx,y-8,4)))
    result = fuse(pieces)
    holes = [hole_z(x,y,3.4) for x,y in MOUNT_HOLES]
    holes += [hole_z(x,y,6.2,z=4,height=2.5) for x,y in MOUNT_HOLES]
    # Vertical driver shafts fit with the neighbouring module still bolted.
    holes += [hole_z(x,y,12,z=6,height=35) for x,y in MOUNT_HOLES]
    holes += [hole_z(x,y,4.5) for x,y in GUIDES]
    # Two releasable 2.5 mm cable ties retain the servo against the hard cradle.
    for x in SERVO_TIE_CENTRES:
        holes.append(box(3, 31, 3, (x-1.5, 103, 24)))
    # Open-top reliefs clear both mounting ears and the cable outlet. The
    # first tie is beyond the outlet so it cannot pinch the servo wires.
    holes.append(box(3, 33, 20, (16.25, 101, SERVO_SUPPORT_Z)))
    holes.append(box(2, 4, 14, (5.6, 103, M["cam_z"]-2.1)))
    # Transport restraint threads through the structural base, independently of tray.
    holes += [box(3, 18, 10, (x, 58, -1)) for x in (4, 39)]
    # Shallow open pocket clears the enlarged cam rim; 2.5 mm of floor remains.
    holes.append(box(10.5,70,2,(29,77,2.5)))
    # Independent light capacitor carriers bolt to the frame through clearance
    # passages. No capacitor depends on another module's mounting screws.
    for gx,gy,_ in PARAMS["capacitors"]:
        index=int((gx-M["origin_x"])/M["pitch"])
        if 0<=index<M["count"]:
            xx=gx-M["origin_x"]-index*M["pitch"]
            yy=gy-M["origin_y"]
            if 0<xx<width and 0<yy<length:
                # The independent holder's permanent foot is wider than its
                # shaft. Use the existing upper clearance through the floor
                # too; the removable module never carries the capacitor.
                holes.append(hole_z(xx,yy,12.2,z=-1,height=6))
                holes.append(hole_z(xx,yy,13.1,z=3.9,height=4.3))
    holes += retaining_ties("servo")
    # Capacitor feedthroughs clear the base rib as well as their own holders.
    for gx,gy,gz in PARAMS["capacitors"][2:]:
        index=int((gx-M["origin_x"])/M["pitch"])
        xx=gx-M["origin_x"]-index*M["pitch"]
        yy=gy-M["origin_y"]
        for lead in (-2,2):
            holes.append(box(3.4,11.7,4.9,(xx+lead-1.7,yy-10,gz-M["origin_z"]-3.2)))
    return cut(result, holes)


@lru_cache
def sliding_follower():
    """Integral circular PETG peg; rounded entry tip, no axle or bore."""
    tip, root = M["sliding_follower_tip_x"], M["sliding_follower_root_x"]
    peg = cylinder(M["sliding_follower_diameter"]/2, root-tip,
                   (tip, 112, -M["follower_drop"]), (1, 0, 0))
    tip_edges = [edge for edge in peg.Edges()
                 if edge.geomType() == "CIRCLE" and abs(edge.Center().x-tip) < 1e-6]
    # Only the axial entry edge is rounded; the cam-contact section stays OD6.
    return peg.fillet(.5, tip_edges)


@lru_cache
def tray_blank():
    """Top at z=3; print on the x45 side so the integral peg points upward.

    Guide bridges and PETG fit need coupon/slicer validation and reprinting;
    dimensional corrections must not depend on filing or drilling.
    """
    pieces = [box(M["tray_width"], M["length"], M["tray_thickness"], (1, 0, 0))]
    holes = []
    for x, y in GUIDES:
        sleeve_y = 24 if y == 87 else 16
        pieces.append(box(14, sleeve_y, M["sleeve_length"], (x-7, y-sleeve_y/2, -M["sleeve_length"])))
        c = M["guide_clearance"]
        holes.append(guide_opening(x,y,c,32,-25))
    # Positive head/side retention stays outside the padded wand envelope.
    pieces.extend([box(2,M["length"],6,(1,0,3)),
                   box(2,M["length"],6,(43,0,3)),
                   box(40,3,6,(3,0,3))])
    # The existing lug carries the integral peg. Its x40 root stays one mm
    # clear of the x39 cam face, including the entire positive-return stroke.
    pieces.append(box(4,12,23,(40,106,-23)))
    pieces.append(sliding_follower())
    pieces.append(box(3,200,10,(42,6,-10)))
    # A flat central spine resists bending.
    pieces.append(box(4, 196, 8, (20, 8, -8)))
    # Stops surround U059 without relying solely on adhesive retention.
    for x in (5, 38):
        pieces.append(box(2.0, 26, 11, (x, 24, -11)))
    pieces.append(box(35, 2, 11, (5, 22, -11)))
    pieces.append(box(2,26,11,(40,24,-11)))
    result = fuse(pieces)
    # The spine is interrupted at the actual U059 body and at its wire exit.
    result = result.cut(box(33, 39, 12, (7, 23, -12))).clean()
    for y in (16, 64, 167):
        holes.extend(strap_slots(23, y, 37, width=8, z=-12, height=17))
    holes.extend(strap_slots(23, 38, 37, width=12, z=-12, height=17))
    holes += retaining_ties("u059")+retaining_ties("led")
    return cut(result, holes)


def guide_opening(x,y,clearance,height,z):
    """Original 8x10 guide, unchanged face clearances, printable corner roofs.

    At c=.45 the bounding opening is8.9x10.9 and the .6 mm corner chamfer
    retains .212 mm diagonal clearance to the actual rectangular column.
    """
    a,b=4+clearance,5+clearance
    chamfer=4*clearance/3
    return (cq.Workplane("XY",origin=(x,y,z)).polyline([
        (-a+chamfer,-b),(a-chamfer,-b),(a,-b+chamfer),(a,b-chamfer),
        (a-chamfer,b),(-a+chamfer,b),(-a,b-chamfer),(-a,-b+chamfer)])
        .close().extrude(height).val())


TRAY_SEAM_X = 16.0
TRAY_JOINTS = (74.0, 205.0)
TRAY_LOWER_BEAMS = ((59,20),(196,16))


def lower_beam_solids(start,depth):
    """Source stock for the added lower joint beam and both deck webs."""
    return (box(44,depth,11,(1,start,-20)),
            box(2,depth,20,(1,start,-20)),box(3,depth,20,(42,start,-20)))


def tray_joint_tools():
    tools = []
    for y in TRAY_JOINTS:
        tools.append(box(50,3.4,3.4,(0,y-1.7,-16.2)))
        # Two open nut bays, each only 6.8mm wide, retain a bridge anchor
        # between lanes. A full through-bolt stays inside its module footprint.
        tools.append(box(3,6.8,9,(0,y-3.4,-19)))
        # A permanent pilot at the first nut-bay roof bridges along Z, then
        # the final slot closes its ends across Y. Both spans have real anchors.
        tools.append(box(.4,3.4,9,(3,y-1.7,-19)))
    return tools


@lru_cache
def tray_halves():
    """Two full-depth side prints, joined on the bench across a keyed seam.

    The peg and rear guide stay integral with the right half; the front guide
    stays integral with the left. Bolts clamp broad bosses, never the 3mm deck.
    """
    shape = tray_blank()
    # Extend the peg root to the actual right build face. It remains outside
    # the cam's X39 face and below the LED mounting surface.
    shape = shape.fuse(box(1,12,23,(44,106,-23)))
    # A full left root carries the U059 edge stop without a floating shelf.
    shape = shape.fuse(box(6,28,11,(1,22,-11)))
    # Self-supporting spine and joint roots; these ramps grow at 45 degrees
    # in the two opposing side-print directions.
    shape = shape.fuse(cq.Workplane("XZ",origin=(0,204,0)).polyline(
        [(24,-8),(32,0),(24,0)]).close().extrude(142).val())
    shape = shape.fuse(cq.Workplane("XZ",origin=(0,23,0)).polyline(
        [(24,-8),(32,0),(24,0)]).close().extrude(15).val())
    shape = shape.fuse(cq.Workplane("XZ",origin=(0,50,0)).polyline(
        [(42,-11),(43,-10),(42,0)]).close().extrude(28).val(),box(2,2,11,(40,22,-11)))
    shape = shape.fuse(cq.Workplane("XZ",origin=(0,24,0)).polyline(
        [(44,-11),(45,-10),(44,0)]).close().extrude(2).val())
    for start,depth in TRAY_LOWER_BEAMS:
        shape = shape.fuse(*lower_beam_solids(start,depth))
    # Relief preserves the actual purchased U059 envelope and retaining ties.
    shape = shape.cut(box(33,39,12,(7,23,-12))).clean()
    shape = cut(shape, retaining_ties("u059")+retaining_ties("led"))
    # Open the half-mm remnants below the U059 tie passages instead of
    # retaining unprintable skins. The real tie still threads through the deck.
    shape = cut(shape,[box(35.5,2.5,1.1,(6,y-1.25,-11.1)) for y in U059_TIE_ROWS])
    shape = cut(shape,[box(1.1,2.5,11,(6,y-1.25,-11)) for y in U059_TIE_ROWS])
    shape = cut(shape,[box(2,2.5,34,(41,y-1.25,-24)) for y in LED_TIE_ROWS])
    shape = cut(shape,[cq.Workplane("XY",origin=(0,0,-24)).polyline(
        [(41,y-1.25),(39.75,y),(41,y+1.25)]).close().extrude(34).val()
        for y in LED_TIE_ROWS])
    shape = cut(shape,[box(3,wide,17,(40,y-wide/2,-12))
                       for y,wide in ((16,8),(64,8),(167,8),(38,12))])
    # The existing empty-bed transport wrap needs a through path through the
    # new lower beam as well. Its roof is kept open across the whole print.
    shape = cut(shape,[box(width,8,29,(x,60,-24)) for x,width in ((3.25,2.5),(40,3))])
    # Slicer infill clipped the ends of the rectangular strap-roof bridge.
    # Directional teardrop roofs close at 45 degrees in each side print while
    # retaining the complete original 2.5 x 8 mm transport-wrap passage.
    shape = cut(shape,[cq.Workplane("XY",origin=(0,0,-24)).polyline(points)
                       .close().extrude(29).val() for points in (
                           [(5.75,60),(9.75,64),(5.75,68)],
                           [(40,60),(36,64),(40,68)])])
    shape = shape.cut(box(44,8,12,(1,60,-12)))
    shape = cut(shape,tray_joint_tools())
    gap = .25
    left = shape.intersect(box(TRAY_SEAM_X-1-gap/2,212,40,(1,0,-24)))
    right = shape.intersect(box(45-TRAY_SEAM_X-gap/2,212,40,(TRAY_SEAM_X+gap/2,0,-24)))
    # Two short locating tongues transfer shear. The sockets have 0.25mm
    # clearance per face and the halves approach directly along X.
    for y in TRAY_JOINTS:
        tongue = box(2.5,4,1.2,(TRAY_SEAM_X-.5,y-2,-18.6))
        left = left.fuse(tongue)
        right = right.cut(box(2.8,4.5,1.7,(TRAY_SEAM_X-.5,y-2.25,-18.85)))
    return left.clean(),right.clean()


@lru_cache
def tray():
    return fuse(tray_halves())


def wand():
    """Conservative silhouette envelope, not a reconstructed ornament."""
    head = box(W["head_width"], W["head_length"], W["head_height"],
               (23-W["head_width"]/2, 6, 0))
    shaft = cylinder(W["shaft_diameter"]/2, W["length"]-W["head_length"],
                     (23, 6+W["head_length"], W["shaft_diameter"]/2), (0, 1, 0))
    return head.fuse(shaft).clean()


@lru_cache
def stock_horn():
    # Retain the assembly identity consumed by the twin's rotation bindings.
    return servo_horn.horn()


def parts():
    return [Part("lift_base", base(), 6, "100% infill; servo cradle, two guides and transport strap anchors in one print"),
            Part("wand_bed_left", tray_halves()[0], 6, "Keyed left bed half; front guide integral; print left edge down; join with two M3x45 button screws and thin nuts", ((0,1,0),-90)),
            Part("wand_bed_right", tray_halves()[1], 6, "Keyed right bed half; peg and rear guide integral; print right edge down; supports NONE", ((0,1,0),90)),
            Part("dwell_cam", cam(), 6, "100% infill; captured integral sliding peg drives both directions; two circular dwells", ((0,1,0),-90))] + servo_horn.parts()


def module(index=0, angle=0, with_wand=True):
    x0 = M["origin_x"] + M["pitch"]*index
    location = (x0, M["origin_y"], M["origin_z"])
    radius = cam_law(angle)[0]
    tray_z = M["cam_z"] + radius + M["follower_drop"]
    shapes = [Item(f"base_{index+1}", base(), "fixed", "mechanism"),
              Item(f"tray_{index+1}", tray_halves()[1].translate((0,0,tray_z)), "moving", "tray"),
              Item(f"tray_left_{index+1}", tray_halves()[0].translate((0,0,tray_z)), "moving", "tray"),
              Item(f"cam_{index+1}", cam().rotate((0,0,0),(1,0,0),angle).translate((M["cam_x"],M["cam_y"],M["cam_z"])), "moving", "metal"),
              Item(f"servo_{index+1}", servo_at_angle(angle), "hardware", "black", "Owner-supplied SG90-Servo.stp; output shaft follows horn angle; physical fit pending"),
              Item(f"stock_horn_{index+1}", stock_horn().rotate((0,0,0),(1,0,0),angle).translate((M["cam_x"],M["cam_y"],M["cam_z"])), "moving_hw", "white", "Printed PETG servo_horn; STEP-derived 20-groove socket; physical spline grip, retaining screw and load pending"),
              Item(f"u059_{index+1}", box(32,24,8,(7.5,24,tray_z-10)), "moving_hw", "pcb", "32 x 24 x 8 catalog"),
              Item(f"led_{index+1}", box(3,200,10,(45,6,tray_z-7)), "moving_hw", "light", "10 x 200; thickness 3 provisional")]
    for j,y in enumerate(TRAY_JOINTS,start=1):
        nut = cq.Workplane("YZ",origin=(1.2,y,tray_z-14.5)).polygon(6,5.5/math.cos(math.pi/6)).extrude(1.8).val()
        nut = nut.cut(cylinder(1.5,2,(1.1,y,tray_z-14.5),(1,0,0)))
        shapes += [Item(f"tray_joint_shaft_{j}_{index+1}",cylinder(1.5,45,(0,y,tray_z-14.5),(1,0,0)),"moving_hw","metal","M3x45 ISO7380-1; no protrusion into neighbouring modules; bench assembly"),
                   Item(f"tray_joint_head_{j}_{index+1}",cylinder(2.85,1.65,(45,y,tray_z-14.5),(1,0,0)),"moving_hw","metal","ISO7380-1 M3 head envelope diameter5.7 height1.65"),
                   Item(f"tray_joint_nut_{j}_{index+1}",nut,"moving_hw","metal","M3 DIN439 thin nut AF5.5 H1.8; low-strength threadlocker")]
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
        shapes.append(Item(f"guide_bolt_{index+1}_{int(y)}", cylinder(2,100,(x,y,-4)), "fastener", "metal","M4x100 DIN931; 86 mm smooth shank covers complete bearing travel, availability/dry fit pending"))
        shapes.append(Item(f"guide_head_{index+1}_{int(y)}",cylinder(3.85,2.8,(x,y,-6.8)),"hardware","metal","M4 DIN931 AF7 head allowance OD7.7 H2.8; actual corner fit pending"))
        guide_top = M["guide_height"]
        shapes.append(Item(f"guide_washer_{index+1}_{int(y)}", cylinder(4.5,.8,(x,y,guide_top)).cut(cylinder(2.3,3,(x,y,guide_top-1))), "hardware", "metal","M4 DIN125 9 mm OD washer; guide retention above full lift"))
        shapes.append(Item(f"guide_nut_{index+1}_{int(y)}",cylinder(4,3.2,(x,y,guide_top+.8)).cut(cylinder(2,4,(x,y,guide_top+.5))),"hardware","metal","M4 DIN934 H3.2 + low-strength threadlocker; tighten without sleeve preload"))
    liner=fuse([box(38,W["head_length"],W["foam"]+W["fabric"],(4,6,tray_z+3)),
                box(16,W["length"]-W["head_length"],W["foam"]+W["fabric"],(15,6+W["head_length"],tray_z+3))])
    if with_wand:
        liner=cut(liner,[t.translate((0,0,tray_z)) for t in retaining_ties("led")])
        shapes.append(Item(f"soft_liner_{index+1}",liner,"moving_hw","cover","Removable 1.2 mm foam + 0.8 mm textile; relieved around LED ties"))
    for j,(x,y) in enumerate(MOUNT_HOLES):
        nut_z = PARAMS["frame"]["floor_anchors"]["module_nut_z"]-M["origin_z"]
        screw_length = PARAMS["frame"]["floor_anchors"]["module_screw_length"]
        nut = cq.Workplane("XY", origin=(x,y,nut_z)).polygon(6,5.5/math.cos(math.pi/6)).extrude(2.4).val()
        nut = nut.cut(cylinder(1.5,3,(x,y,nut_z-.1)))
        shapes += [Item(f"module_m3_{index+1}_{j+1}",cylinder(1.5,screw_length,(x,y,4-screw_length)),"fastener","metal","M3 x14; four independent module screws into bonded floor cradle"),
                   Item(f"module_head_{index+1}_{j+1}",cylinder(2.85,2,(x,y,4)),"hardware","metal"),
                   Item(f"module_nut_{index+1}_{j+1}",nut,"hardware","metal","M3 AF5.5 H2.4 hex nut; captive side loading below z5 roof")]
    for kind in ("servo","u059","led"):
        for j,tie in enumerate(retaining_ties(kind)):
            shapes.append(Item(f"{kind}_tie_{j+1}_{index+1}",tie if kind=="servo" else tie.translate((0,0,tray_z)),
                               "hardware" if kind=="servo" else "moving_hw","black","2.5 mm tie captured through physical cradle/bed slots"))
    return [Item(i.name, i.shape.translate(location), i.group, i.color, i.source, i.allowance) for i in shapes]
