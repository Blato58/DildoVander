"""Compact bolted service carriers; purchased envelopes remain source-derived."""
from functools import lru_cache
import math
import cadquery as cq
import flex_routing
from geometry import PARAMS, Part, Item, box, cylinder, cut, fuse, hole_z, curve_tube
from service_geometry import SERVICE_MOUNTS, NUT_Z, SEAT_Z, HEAD_RADIUS

COMPONENTS = PARAMS["components"]
Z = PARAMS["mounting"]["cover_z"]
RACKS = {}
LEFT_STACK = ("breaker", "buck", "amplifier_left")
RIGHT_STACK = ("perfboard_1", "perfboard_2", "degson_1", "degson_2", "degson_3")
RIGHT_SERVICE_MOUNTS = SERVICE_MOUNTS["right_service_carrier"]


def hex_nut(x,y,z,pocket=False):
    shape=cq.Workplane("XY",origin=(x,y,z)).polygon(6,6.7 if pocket else 6.4).extrude(2.4).val()
    return shape if pocket else shape.cut(hole_z(x,y,3,z=z-.1,height=3))


def pocket(name):
    """Unmeasured board holes are avoided: perimeter stops and two ties."""
    spec=COMPONENTS[name]
    x,y,z=spec["origin"]
    w,d,h=spec["size"]
    # These short alignment stops carry no mounting load; straps retain the
    # component and its 3 mm floor carries the weight. A 1 mm stop is a real
    # two-line PETG wall, rather than the former sub-nozzle 0.3 mm edge.
    margin = 1.0 if name in ("speaker_left","speaker_right","powerbank","pd_trigger","amplifier_left","amplifier_right","breaker") else 1.5
    if name in ("speaker_left", "powerbank"):
        # Their purchased envelopes leave only 1 mm longitudinally. Preserve
        # two one-line locating walls and the remaining 0.2 mm separation.
        margin = 0.4
    y0,y1 = y-margin,y+d+margin
    if name=="microphone":
        y0,y1 = 13.35,20
    if name=="amplifier_right":
        y0=y
    if name=="pca9685":
        y0=233.5
    if name=="speaker_right":
        y1=y+d
    pad=box(w+2*margin,y1-y0,3,(x-margin,y0,z-3))
    lips=[box(margin,y1-y0,4,(xx,y0,z)) for xx in (x-margin,x+w)]
    if y-y0>1e-6:
        lips.append(box(w,y-y0,4,(x,y0,z)))
    if y1-y-d>1e-6:
        lips.append(box(w,y1-y-d,4,(x,y+d,z)))
    holes=[]
    for yy in (() if name=="microphone" else (y+d*.25,y+d*.75)):
        slot_width=8.4 if name=="powerbank" else 2.9 if name=="microphone" else 4
        # Open the short locating lip above each tie slot. The unchanged tie
        # route stays clear, without a narrow unsupported header across it.
        holes += [box(2.8,slot_width,9,(xx,yy-slot_width/2,z-4)) for xx in (x-1.5,x+w-1.3)]
    if name=="microphone":
        # Two ties run along the short board depth. Their bottom tunnels are
        # 2.5 mm wide, with a real anchor on each side of the roof. Leave the
        # acoustic centre and the component's original pose unchanged.
        holes += [box(2.9,1.8,5,(x+w*fraction-1.45,yy,z))
                  for fraction in (.25,.75) for yy in (13.35,19)]
    result=cut(fuse([pad,*lips]),holes)
    if name == "amplifier_right":
        # The shifter occupies the neighbouring rear-left corner. The rear
        # stop remains at the amplifier's right corner, with 0.25 mm clearance.
        result = result.cut(box(29, 19, 8, (372.25, 134, 17)))
        # The independent speaker's rear mounting post reaches this floor
        # corner. Preserve its screw axis and leave 0.25 mm radial clearance.
        result = result.cut(cylinder(3.75,3.3,(410,125,14.75)))
    if name == "pd_trigger":
        # The bank's finite 8.4 mm strap crosses only the last 1.7 mm of this
        # side stop. Keep the long stop, but leave the actual wrap unobstructed.
        result = result.cut(box(2, 5, 8, (46, 144.65, z - 3)))
    return cut(result,[t.shape for t in ties(name)])


def mount_positions(name):
    x,y,z=COMPONENTS[name]["origin"]
    w,d,h=COMPONENTS[name]["size"]
    if w<18:
        return [(x+w/2,yy) for yy in (y+5,y+d-5)]
    if d<18 or name=="amplifier_right":
        return [(xx,y+d/2) for xx in (x+5,x+w-5)]
    ys=(17, y+d-4) if name.startswith("speaker_") else (y+4,y+d-4)
    if name=="powerbank": ys=(y+4,249.8)
    result=[(xx,yy) for xx in (x+5,x+w-5) for yy in ys]
    if name=="powerbank": result += [(51.5,152.5),(51.5,249.5)]
    if name=="speaker_right": result += [(410,45),(410,125)]
    return result


def cover_post(x,y,z0,width=7.5,depth=7):
    shape=box(width,depth,108-z0,(x,y,z0))
    cx,cy=x+width/2,y+depth/2
    return cut(shape,[hole_z(cx,cy,3.4,z=101,height=9),hex_nut(cx,cy,102.6,True)])


def cover_nut_entry(x, y, direction):
    """Side-load a cover nut with 0.3 mm total vertical print clearance."""
    assert direction in (-1, 1)
    seat=cq.Workplane("XY",origin=(x,y,102.3)).polygon(6,6.7).extrude(2.7).val()
    mouth=box(6.7,4.3,2.7,(x-3.35,y-4.3 if direction<0 else y,102.3))
    # The loaded nut bears at Z105; the entry keeps both X-side roof anchors.
    return seat.fuse(mouth)


def hole_y_supported(radius, length, origin):
    """Circular clearance with a 26.6 degree roof for intersecting tapers."""
    x, y, z = origin
    # This line is tangent to the circle at dx/dz=0.5. Even where the
    # neighbouring outside taper moves laterally too, their shared corner
    # stays inside the permitted 45 degree layer support cone.
    side_x = 2*radius/math.sqrt(5)
    side_z = radius/math.sqrt(5)
    roof = (cq.Workplane("XZ", origin=(x,y,z))
            .polyline([(-side_x,side_z),(0,radius*math.sqrt(5)),(side_x,side_z)])
            .close().extrude(-length).val())
    return cylinder(radius,length,origin,(0,1,0)).fuse(roof)


def shaft_vent(x, y, outside_y):
    """Open a blind shaft above its staged roof, retaining the bearing face."""
    start=min(y,outside_y)
    length=abs(outside_y-y)
    assert length>0
    return (cq.Workplane("XZ",origin=(x,start,15.3))
            .polyline([(-.4,0),(.4,0),(.1,.8),(-.1,.8)])
            .close().extrude(-length).val())


def free_carrier(name):
    x,y,z=COMPONENTS[name]["origin"]
    w,d,h=COMPONENTS[name]["size"]
    shape=pocket(name)
    if name=="mcp23017":
        # Both original tie tunnels cross this unusually shallow board. The
        # 3 mm permanent margins provide deposited anchors beyond four roof
        # perimeters; 1.5 mm margins left the bridge strokes inside the void.
        # Keep the board pose, screw axes, tie routes and 3 mm floor thickness.
        shape=shape.fuse(box(w+3,d+6,3,(x-1.5,y-3,z-3)))
    holes=[]
    if name=="microphone":
        shape=shape.fuse(box(w+3,2,z-15,(x-1.5,17,12)))
        # The shallow microphone floor widens from its backing at 45 degrees.
        taper=(cq.Workplane("XY",origin=(x+w/2,18,z-6.65)).rect(w+3,2)
               .workplane(offset=3.65).center(0,(13.35+20)/2-18)
               .rect(w+3,6.65).loft().val())
        shape=shape.fuse(taper)
        for xx in (x+3,x+w-3):
            shape=shape.cut(hole_y_supported(1.7,5,(xx,15,24)))
        return cut(shape,[t.shape for t in ties(name)]).clean()
    if name in ("esp32","pca9685","mcp23017","pixel_shifter","amplifier_right","pd_trigger","speaker_left","speaker_right","powerbank"):
        # A permanent plinth carries the unchanged 3 mm pocket floor. Its
        # infill is slicer-defined; no roof starts above isolated round posts.
        pad_bounds=shape.BoundingBox()
        if name=="esp32":
            # The camera's hardware/feet occupy both rear corners up to Z32.
            # A narrower lower plinth clears them, then a four-mm loft expands
            # to the existing floor above them with less than 45 degree slope.
            lower_x,lower_y,lower_w,lower_d=187.45,234.5,56.1,14.5
            shape=shape.fuse(box(lower_w,lower_d,25,(lower_x,lower_y,12)))
            taper=(cq.Workplane("XY",origin=(lower_x+lower_w/2,lower_y+lower_d/2,37))
                   .rect(lower_w,lower_d).workplane(offset=4)
                   .center(pad_bounds.center.x-(lower_x+lower_w/2),
                           pad_bounds.center.y-(lower_y+lower_d/2))
                   .rect(pad_bounds.xlen,pad_bounds.ylen).loft().val())
            shape=shape.fuse(taper)
        else:
            shape=shape.fuse(box(pad_bounds.xlen,pad_bounds.ylen,z-15,
                                 (pad_bounds.xmin,pad_bounds.ymin,12)))
    # Mount screws enter from below before the component/ties are fitted.
    # Captive nuts stop at z-3; purchased boards never have imaginary holes.
    for xx,yy in mount_positions(name):
        shape=shape.fuse(cylinder(3.5,z-12,(xx,yy,12)))
        if name in ("esp32","pca9685","mcp23017","pixel_shifter","amplifier_right","pd_trigger","speaker_left","speaker_right","powerbank"):
            # A 6.7 mm corner-diameter nut in a 7 mm round post leaves only
            # 0.15 mm at its corners. Reinforce the actual nut-height region.
            collar_height=2.8 if name in ("speaker_left","speaker_right","powerbank") else 2.4
            shape=shape.fuse(cylinder(4.2,collar_height,(xx,yy,12)))
        holes.append(hole_z(xx,yy,3.4,z=11,height=z-10.9 if name in
                           ("speaker_left","speaker_right","powerbank","esp32",
                            "pca9685","pixel_shifter","amplifier_right") else z-12.5))
        holes.append(hex_nut(xx,yy,12,True))
    if name=="esp32":
        # Preserve the neighbouring camera's rear M3 nut/driver corridors.
        holes += [hole_y_supported(3.45,10,(xx,244.5,24)) for xx in (184,247)]
    if name=="pd_trigger":
        holes += [tie.shape for tie in ties("powerbank")]
    if name=="amplifier_right":
        holes += [box(29,19,15,(372.25,134,11)),cylinder(3.75,15,(410,125,11)),
                  # Clear the speaker cover foot by 0.25 mm. The last 0.25 mm
                  # of this short board edge overhangs the floor locally;
                  # its full remaining floor and two ties carry the board.
                  box(20,7.5,15,(405.75,121.25,11))]
    if name.startswith("speaker_"):
        # The finite 200 mm ties retain their existing short-axis route.
        # Open each bottom tunnel through the top of the floor; the remaining
        # permanent plinth and floor strips carry the purchased speaker.
        holes += [box(w+2,2.9,1.1,(x-1,yy-1.45,z-1))
                  for yy in (y+d*.25,y+d*.75)]
    result=cut(cut(shape,holes),[t.shape for t in ties(name)])
    if name in ("mcp23017","pd_trigger"):
        # Opening the top removes a required tie-roof bridge anchor here.
        # A small side vent leaves that floor and its shaft clearance intact.
        bb=result.BoundingBox()
        vents=[shaft_vent(xx,yy,bb.ymin-.1 if yy-bb.ymin<=bb.ymax-yy else bb.ymax+.1)
               for xx,yy in mount_positions(name)]
        result=result.cut(*vents)
    return result


@lru_cache
def service_platforms():
    left=[pocket(n) for n in LEFT_STACK]
    for xx in (15.9,66):
        left.append(box(3,47.5 if xx==15.9 else 49.35,62,(xx,64,12)))
    left.append(box(29.1,13,3,(15.9,98.5,57)))
    # These shelves join edge rails that miss the upward-facing speaker.
    for n in LEFT_STACK:
        x,y,z=COMPONENTS[n]["origin"]
        w,d,h=COMPONENTS[n]["size"]
        compact=n in ("amplifier_left","breaker")
        depth=d+.6 if compact else d+3
        start=y-.3 if compact else y-1.5
        px=45 if n=="amplifier_left" else 15.9
        left.append(box(69-px,depth,3,(px,start,z-3)))
    right=[pocket(n) for n in RIGHT_STACK]
    for xx in (360.1,413):
        right.append(box(2.9,93,61,(xx,158.5,12)))
    for n in RIGHT_STACK:
        x,y,z=COMPONENTS[n]["origin"]
        w,d,h=COMPONENTS[n]["size"]
        right.append(box(55.8,d+3,3,(360.1,y-1.5,z-3)))
    for yy in (160,246):
        post=box(7.5,8,35,(408,yy-4,73))
        post=post.cut(hole_z(411.75,yy,3.4,z=102,height=8))
        post=post.cut(cylinder(3.2,2.4,(411.75,yy,105.6)))
        right.append(post)
    for xx,yy in RIGHT_SERVICE_MOUNTS:
        right.append(box(9.9,8,3,(360.1 if xx==365 else 406,yy-4,12)))
    left_shape=cut(fuse(left),[t.shape for n in LEFT_STACK+("speaker_left",) for t in ties(n)])
    # The narrow bank margin is preserved by ending the amp carrier ahead of it.
    left_shape=left_shape.cut(box(53.1,2,80,(15.9,113.35,40))).clean()
    left_shape=left_shape.cut(box(2,59,70,(68,60,12))).clean()
    # Rear support sits below the AMP shelf; the finite USB bundle needs the
    # outer corridor above z40. Remove the old web rather than leave a thin fin.
    left_shape=left_shape.cut(box(4,25.35,62,(66,94,12))).clean()
    left_shape=left_shape.fuse(box(23,2,3,(45,92,46)),box(21,1,3,(45,94,46))).clean()
    # The finite bank USB tail turns below the breaker; both mounting webs
    # and the full shelf ahead of this small rear-edge opening remain intact.
    left_shape=left_shape.cut(box(5.3,3.7,9.2,(30.6,91.7,39.4))).clean()
    right_shape=cut(fuse(right),[t.shape for n in RIGHT_STACK for t in ties(n)])
    # Separate finite cable windows preserve the actual middle PCB shelf.
    right_shape=cut(right_shape,[box(4,28,19,(360,210,24)),box(4,28,20,(360,210,51))])
    from chassis import rack_holes
    right_shape=cut(right_shape,[cylinder(3.6,3,(xx,yy,12)) for xx,yy in rack_holes()])
    # Peripheral flanges permit removal of the entire prewired cassette from
    # above. The old screws beneath the populated shelves are eliminated.
    # A full-section post joins the front shelf face ahead of the USB bend.
    # Its 2 x 2 mm contact face avoids an edge-only mesh joint.
    left_shape=left_shape.fuse(box(3,20,3,(66,92,12)),box(2,3,34,(66,91,12)))
    for xx,yy in SERVICE_MOUNTS["left_service_carrier"]:
        foot=box(7,8,3,(max(15.9,xx-3.5),yy-4,12))
        foot=foot.cut(cylinder(3.5,3,(25,109.35,12)))
        left_shape=left_shape.fuse(foot)
    # Reach the front flange along the outer edge, clear of the control panel.
    left_shape=left_shape.fuse(box(2,9,3,(66,59,12)))
    for shape_name,shape,rows in (("left",left_shape,SERVICE_MOUNTS["left_service_carrier"]),
                                  ("right",right_shape,RIGHT_SERVICE_MOUNTS)):
        tools=[hole_z(xx,yy,3.4,z=11,height=5) for xx,yy in rows]
        tools += [hole_z(xx,yy,6.1,z=SEAT_Z,height=100) for xx,yy in rows]
        if shape_name=="left": left_shape=cut(shape,tools)
        else: right_shape=cut(shape,tools)
    return {"left_service_carrier":left_shape,"right_service_carrier":right_shape}


def bank_holder():
    x,y,z=COMPONENTS["powerbank"]["origin"]
    w,d,h=COMPONENTS["powerbank"]["size"]
    result=free_carrier("powerbank")
    for yy in (149,246):
        result=result.fuse(cover_post(48,yy,12,7,7),box(12,7,3,(43,yy,12)))
        # Two-line bridge anchors continue through both nut-pocket roofs.
        collar=(cq.Workplane("XY",origin=(51.5,yy+3.5,101.2)).rect(7,7)
                .workplane(offset=1.4).rect(8.4,8.4).loft().val())
        result=result.fuse(box(8.4,8.4,2.8,(47.3,yy-.7,12)),collar,
                           box(8.4,8.4,5.4,(47.3,yy-.7,102.6)))
        cx,cy=51.5,yy+3.5
        result=cut(result,[hole_z(cx,cy,3.4,z=11,height=7.2),hex_nut(cx,cy,12,True),
                           hole_z(cx,cy,3.4,z=101,height=10),
                           cover_nut_entry(cx,cy,-1 if yy==149 else 1)])
    # Low stops locate the removable content. Both straps release upwards;
    # neither shelf, electronics nor decorative roof rests on the bank.
    from chassis import rack_holes
    result=cut(result,[tool for xx,yy in mount_positions("powerbank") for tool in
               (hole_z(xx,yy,3.4,z=11,height=7.1),hex_nut(xx,yy,12,True))])
    # Open the complete M4 head/driver corridor through the floor. The bank
    # is removed before reaching these screws; no closed annular roof remains.
    result=cut(result,[cylinder(3.6,7.2,(xx,yy,11)) for xx,yy in rack_holes()])
    return result.cut(shaft_vent(51.5,152.5,148.2),shaft_vent(51.5,249.5,253.8))


def switch_shelf():
    return cq.Compound.makeCompound(list(switch_parts().values()))


SWITCH_JOINTS=((26,17.2,"left"),(64.5,17.2,"right"),
               (19.65,58,"left"),(67,54.5,"right"))


def switch_parts():
    """Flat controls plate and two outer-face-down, independent side ladders."""
    from chassis import rack_holes
    plate=box(53.1,42,3,(15.9,15,102)).fuse(box(49.5,8,3,(19.5,55,102)))
    frames={side:fuse([box(3 if side=="left" else 4.5,3,90,(xx,yy,12)) for yy in (15,54)]+
                     [box(3 if side=="left" else 4.5,48.5 if side=="left" else 42,3,(xx,15,12))])
            for side,xx in (("left",15.9),("right",66))}
    for xx,yy in ((20,27),(22,60)):
        width=7.5 if yy==27 else 10.1
        frames["left"]=frames["left"].fuse(box(width,7,6,(15.9,yy-3.5,12)))
        frames["left"]=cut(frames["left"],[box(3.4,3.4,8,(xx-1.7,yy-1.7,11)),
            box(12,5.8,2.5,(xx-3.35,yy-2.9,11.9))])
    for xx,yy,side in SWITCH_JOINTS:
        x0,x1=(15.9,xx+3.75) if side=="left" else (xx-3.75,70.5)
        boss=box(x1-x0,7.5,10,(x0,yy-3.75,92))
        if (xx,yy)==(26,17.2):
            # This corner shares the removable skirt's leg opening. A real
            # 45-degree in-plane fan reaches the wider seat after that skirt.
            boss=boss.intersect(cq.Workplane("XY",origin=(0,0,92)).polyline(
                [(15.9,15),(18.25,15),(21,13.45),(29.75,13.45),
                 (29.75,20.95),(21,20.95),(18.25,18),(15.9,18)]).close().extrude(10).val())
        # Inboard-loaded rectangular socket captures both AF5.5 nut flats.
        # It opens in the print direction, avoiding a horizontal hex roof.
        mouth_x=xx-3.35 if side=="left" else xx-8
        pocket_tool=box(12,5.8,2.7,(mouth_x,yy-2.9,94.9))
        if (xx,yy)==(67,54.5):
            boss=boss.cut(box(2,2.2,10.2,(63.2,50.7,91.9)))
            boss=boss.cut(box(10,5,10.2,(61,57.9,91.9)))
            pocket_tool=pocket_tool.fuse(box(11,5,2.7,(61,54.5,94.9)))
        shaft_slot=box(8.3,3.4,9,(61,yy-1.7,94)) if (xx,yy)==(67,54.5) else box(3.4,3.4,9,(xx-1.7,yy-1.7,94))
        frames[side]=frames[side].fuse(boss).cut(pocket_tool,shaft_slot)
        plate=plate.fuse(box(7.5,7.5,3,(xx-3.75,yy-3.75,102)))
        plate=plate.cut(hole_z(xx,yy,3.4,z=101.9,height=3.2))
        if yy>50:
            plate=plate.cut(cq.Solid.makeCone(1.7,3.1,1.6,cq.Vector(xx,yy,103.4)))
    for xx,yy in cover_mounts()["control_cover"]:
        width=7.5 if xx==19.65 else 8.4
        post=box(width,8.4,6,(xx-width/2,yy-4.2,102))
        plate=plate.fuse(post).cut(hole_z(xx,yy,3.4,z=101.9,height=7),hex_nut(xx,yy,102.6,True))
    clearances=[hole_z(xx,yy,19.4 if name!="white_ptt" else 12.4,z=77,height=31)
                for name,(xx,yy,zz) in PARAMS["panel"].items()]
    clearances += [hole_z(xx,yy,6.1,z=SEAT_Z,height=100)
                   for xx,yy in SERVICE_MOUNTS["left_service_carrier"]]
    clearances += [tool for xx,yy in rack_holes() for tool in
                   (cylinder(3.6,3.6,(xx,yy,12)),cylinder(2.1,100,(xx,yy,15.5)))]
    plate=cut(plate,clearances+[covers()["control_cover"],
        *[box(7.2,8.2,9,(max(15.9,xx-3.5)-.1,yy-4.1,102))
          for xx,yy in SERVICE_MOUNTS["left_service_carrier"]]])
    # The existing barrel grazed the plate edge leaving a 0.3 mm crescent.
    # Open that edge; the two corner joints retain the right side's load path.
    plate=plate.cut(box(2,12,3.2,(68,37,101.9)))
    # Extend skirt reliefs through the whole plate, eliminating the former
    # 0.2 mm films above the leg windows at Z102.2.
    plate=cut(plate,[box(2.1,end-start,6.2,(15.9,start,101.9))
                     for start,end in ((13.35,19.3),(26.7,54.7),(63.7,97.35))])
    return {"switch_shelf":plate.clean(),
            **{f"switch_frame_{side}":cut(shape,clearances+[covers()["control_cover"]]).clean()
               for side,shape in frames.items()}}


def switch_joint_hardware():
    result=[]
    for index,(xx,yy,side) in enumerate(SWITCH_JOINTS,1):
        screw=(cylinder(1.5,8.5,(xx,yy,95)).fuse(cq.Solid.makeCone(1.5,3,1.5,cq.Vector(xx,yy,103.5)))
               if yy>50 else cylinder(1.5,10,(xx,yy,95)).fuse(cylinder(2.85,1.65,(xx,yy,105))))
        result += [Item(f"switch_joint_screw_{index}",
                        screw,"fastener","metal",
                        "M3x10 DIN7991; flush rear seat, fasten before controls" if yy>50 else
                        "M3x10 ISO7380; accessible front corner joint"),
                   Item(f"switch_joint_nut_{index}",hex_nut(xx,yy,95),
                        "hardware","metal","M3 ISO4032; bench-load open lower boss mouth")]
    return result


@lru_cache
def covers():
    # Covers stay separate from all structural load paths. Removable over
    # bank; their broad flat faces carry fabric but no electronics.
    result={"bank_cover":box(53.1,149,3,(15.9,104.35,Z)),
            "control_cover":box(53.1,84,3,(15.9,13.35,Z)),
            "right_front_cover":box(55.8,132,3,(360.1,13.35,Z)),
            "right_back_cover":box(55.8,101,3,(360.1,152.35,Z))}
    panel=PARAMS["panel"]
    px,py,_=panel["white_ptt"]
    # The decorative cover carries no button load. A full face opening avoids
    # the unsupported square-to-round counterbore roof in its inverted print;
    # the separate structural plate retains the 12.4 mm button barrel mount.
    holes=[box(14.5,14.5,5,(px-7.25,py-7.25,Z-1))]
    holes += [box(25,25,5,(panel[n][0]-12.5,panel[n][1]-12.5,Z-1)) for n in ("red_main","green_drives")]
    # Remove the 0.25 mm cosmetic web between PTT and the green face opening.
    holes.append(box(3.75,.25,5,(46.5,30.25,Z-1)))
    result["control_cover"]=cut(result["control_cover"],holes)
    result["control_cover"]=cut(result["control_cover"],[box(3,31,5,(xx,65,Z-1)) for xx in (23,31,39,47,55)])
    side_rows={"control_cover":(22,(23,60)),"bank_cover":(22,()),
               "right_front_cover":(410,(45,125))}
    for name,(xx,rows) in side_rows.items():
        bb=result[name].BoundingBox()
        side_x=15.9 if xx==22 else 414
        skirt=box(2 if xx==22 else 1.9,bb.ylen,96,(side_x,bb.ymin,12))
        if name=="control_cover":
            skirt=cut(skirt,[box(4,3.4,90.2,(15,yy,12)) for yy in (14.8,53.8)]+
                       [box(4,50,69.05,(15,63.45,12))])
        if name=="bank_cover":
            # Extend the existing open front window past the cassette foot/head.
            # 0.25 mm beyond Y115.5 preserves top access without moving a seat.
            skirt=skirt.cut(box(4,11.4,62,(15,104.35,12)))
        if name=="control_cover":
            skirt=cut(skirt,[box(4,7.4,96,(15,19.3,12)),box(4,9,96,(15,54.7,12))])
            skirt=skirt.cut(box(4,7.4,6.25,(15,23.3,12)))
            skirt=skirt.cut(box(4,48.5,3.1,(15,15,12)))
        if name=="right_front_cover":
            skirt=cut(skirt,[box(3,8.8,96,(413,yy-4.4,12)) for yy in (45,125)])
            skirt=skirt.cut(box(3,132,3.1,(413,13.35,12)),
                             box(2.4,3.45,42.35,(413.75,142.15,14.85)))
        s=fuse([result[name],skirt])
        result[name]=s.clean()
    for name,points in cover_mounts().items():
        result[name]=cut(result[name],[hole_z(xx,yy,3.4,z=107,height=6) for xx,yy in points])
    return result


def cover_mounts():
    return {"control_cover":((19.65,23),(59.65,60)),"bank_cover":((51.5,152.5),(51.5,249.5)),
            "right_front_cover":((411.75,45),(411.75,125)),"right_back_cover":((411.75,160),(411.75,246))}


def camera_bracket():
    """Assembled three-body camera carrier; manufacture camera_parts separately."""
    return cq.Compound.makeCompound(list(camera_parts().values()))


def camera_parts():
    """Two side-face-down posts and flat platform; original rail stays intact."""
    posts={}
    plate=pocket("camera").fuse(box(65,16,3,(183,237,81)))
    for side,xx in (("left",184),("right",247)):
        # Feet remain in front of the existing rear rail below Z29. Separate
        # posts give every upright and foot the same flat outer X bed face.
        post=fuse([box(7.5,2,12,(xx-3.75,248,20)),
                   box(7.5,5,3,(xx-3.75,248,29)),
                   box(7.5,2.25,49,(xx-3.75,250.75,32)),
                   box(7.5,7.5,6,(xx-3.75,245.5,75))])
        mouth_x=xx-3.35 if side=="left" else xx-8
        post=cut(post,[box(3.4,3.4,12,(xx-1.7,247.55,73)),
                       box(12,5.8,2.7,(mouth_x,246.35,74.9)),
                       box(3.4,2.2,3.4,(xx-1.7,247.9,22.3)),
                       box(8,.5,2,(xx-4,250.75,73))])
        # The ESP plinth occupies the neighbouring front corners below Z37.
        # Keep a real 0.25 mm gap without changing its purchased envelope.
        corner=box(4,1.25,17,(187.2,248,20)) if side=="left" else box(4,1.25,17,(239.75,248,20))
        posts[f"camera_post_{side}"]=post.cut(corner).clean()
        plate=plate.fuse(box(7.5,7.5,3,(xx-3.75,245.5,81)))
        plate=plate.cut(hole_z(xx,249.25,3.4,z=80.9,height=3.2))
    plate=cut(plate,[tie.shape for tie in ties("camera")])
    return {**posts,"camera_angle":plate.clean()}


def camera_joint_hardware():
    result=[]
    for index,xx in enumerate((184,247),1):
        result += [Item(f"camera_joint_screw_{index}",
                        cylinder(1.5,10,(xx,249.25,74)).fuse(cylinder(2.85,1.65,(xx,249.25,84))),
                        "fastener","metal","M3x10 ISO7380; accessible beside camera"),
                   Item(f"camera_joint_nut_{index}",hex_nut(xx,249.25,75),
                        "hardware","metal","M3 ISO4032; inboard-entry boss mouth, fit on bench")]
    return result


def fit_coupon():
    from chassis import (anchor_nut_clearances, anchor_nuts,
                         floor_anchor_specs, floor_anchor_key_shape,
                         carrier_head_clearance, frame_head_clearance)

    shape=box(85,58,4).fuse(box(8,10,22,(5,34,4)))
    # Coupon-only seat reproduces the one-mm lug-to-cam-face axial gap.
    shape=fuse([shape, cylinder(5,1,(78,51,4)), follower_coupon_peg()])
    holes=[hole_z(x,12,d,z=-1,height=6) for x,d in zip((10,25,41,61),(3.4,4.5,12.4,19.4))]
    # Standard head-fit samples share the production relief tools. Align the
    # actual anchor/panel top faces, retaining the cutters' harmless overrun.
    m3_head=carrier_head_clearance(10,12,pilot=False)
    floor=PARAMS["frame"]["floor_anchors"]
    holes.append(m3_head.translate((0,0,4-floor["z"]-floor["height"])))
    m4_head=frame_head_clearance(25,12)
    panel_top=PARAMS["frame"]["origin"][2]+PARAMS["frame"]["size"][2]
    holes.append(m4_head.translate((0,0,4-panel_top)))
    from mechanism import guide_opening
    holes += [guide_opening(x+4+c,38+c,c,6,-1) for x,c in ((25,.3),(44,.45),(64,.6))]
    return cut(shape,holes)


def floor_anchor_coupon():
    """Independent gauge prints top-down exactly like the production anchors.

    Keeping this separate from the upward peg coupon avoids printing either
    gauge in the opposite orientation to the component it qualifies.
    """
    from chassis import (anchor_nut_clearances, anchor_nuts, floor_anchor_specs,
                         floor_anchor_key_shape, carrier_head_clearance, case_floor_space)
    shape=box(30,58,7.5,(85,0,0))
    holes=[]
    anchor="floor_anchor_front_left"
    edge=floor_anchor_specs()[anchor]["y_range"][0]
    for kind,target_x in (("module",93),("frame",107)):
        nut=next(n for n in anchor_nuts(anchor) if n["kind"]==kind)
        shift=(target_x-nut["x"],-edge,-PARAMS["frame"]["floor_anchors"]["z"])
        holes.extend(tool.translate(shift) for tool in anchor_nut_clearances(nut,edge))
        # Reproduce the production underside beside each nut as well as its
        # cutters. A flat blank below the curved cutter would add a different,
        # unsupported internal ledge and would not qualify the real anchor.
        sample=box(14,28,7.5,(target_x-7,0,0))
        source_floor=case_floor_space(PARAMS["frame"]["floor_anchors"]["z"]).translate(shift)
        holes.append(sample.cut(source_floor))
    # This third internal-bridge sample checks the production epoxy chamber,
    # underside neck and both 2.5 mm vents before large anchor prints.
    holes.append(floor_anchor_key_shape(100,40).translate(
        (0,0,-PARAMS["frame"]["floor_anchors"]["z"])))
    holes.append(carrier_head_clearance(89,40).translate((0,0,-.5)))
    return cut(shape,holes)


def follower_coupon_peg():
    """Copy the final rounded peg in its prescribed upward print orientation."""
    from geometry import print_position
    from mechanism import sliding_follower
    return print_position(sliding_follower(), ((0,1,0),90)).translate((75,48,4))


def follower_groove_coupon():
    """Straight slot for clearance only; actual cam axial depth and print face."""
    m=PARAMS["mechanism"]
    start=m["cam_x"]
    floor=start+m["cam_thickness"]-m["groove_depth"]
    shape=box(m["cam_thickness"],20,20,(start,0,0))
    groove=box(m["groove_depth"]+1,m["groove_width"],22,
               (floor,10-m["groove_width"]/2,-1))
    return shape.cut(groove).clean()


def usb_clip():
    # Split open parking jaws, screw-mounted at front edge of bank zone.
    shape=fuse([box(8,10,1,(18.9,98,39)),box(8,2,7,(18.9,98,40)),
                box(8,2,7,(18.9,106,40)),box(3,10,7,(18.9,98,40))])
    countersink=cq.Solid.makeCone(3.1,1.5,1.6,cq.Vector(21.9,103,43),cq.Vector(-1,0,0))
    # The fixed carrier now provides a two-mm nut bearing face. Relieve this
    # rear leg for the wider seat, retaining the original flush screw head.
    return cut(shape,[cylinder(1.7,4,(18,103,43),(1,0,0)),countersink,
                      box(20.55,20,20,(0,93,33))])


@lru_cache
def _carrier_source_shapes():
    from chassis import rack_holes
    result=service_platforms().copy()
    for name in COMPONENTS:
        if name in LEFT_STACK+RIGHT_STACK or name=="powerbank":
            continue
        result["camera_angle" if name=="camera" else f"carrier_{name}"]=free_carrier(name)
    result["bank_holder"]=bank_holder()
    # Boundary stops cannot protrude beyond the owner-confirmed safe box.
    result["bank_holder"]=result["bank_holder"].cut(box(60,1,100,(15.9,253.35,12))).clean()
    result["carrier_speaker_left"]=result["carrier_speaker_left"].cut(box(60,1,20,(15.9,12.35,10))).clean()
    result["carrier_speaker_left"]=cut(result["carrier_speaker_left"],
        [cylinder(3.6,7.2,(xx,yy,11)) for xx,yy in rack_holes()])
    # The neighbouring cassette's rear flange screw has its own through
    # corridor; the speaker floor must not close above the flange below it.
    result["carrier_speaker_left"]=result["carrier_speaker_left"].cut(cylinder(3.6,20,(64,92,11)))
    # The integral frame upright occupies the rear margin of this carrier.
    from chassis import rail_shape
    rear=next(s for s in PARAMS["frame"]["stiffeners"] if s["id"]=="rear_crossrail")
    result["carrier_pca9685"]=result["carrier_pca9685"].cut(rail_shape(rear)).clean()
    result.update(switch_parts())
    result["usb_park_clip"]=usb_clip()
    speaker=result["carrier_speaker_right"]
    speaker=speaker.fuse(box(2.45,132,3,(413.45,13.35,12)))
    for yy in (41.5,121.5):
        speaker=speaker.fuse(box(2.45,7,87,(413.45,yy,12)),cover_post(408,yy,99),box(9.9,7,3,(406,yy,12)))
        # Permanent diagonal support carries the inward cover-post shoulder.
        speaker=speaker.fuse(cq.Workplane("XZ",origin=(0,yy+7,0)).polyline(
            [(413.45,93.55),(408,99),(413.45,99)]).close().extrude(7).val(),
            cylinder(4.2,2.8,(410,yy+3.5,12)),box(8.4,8.4,5.4,(407.55,yy-.7,102.6)))
        speaker=speaker.fuse(cq.Workplane("XY",origin=(411.75,yy+3.5,101.2)).rect(7.5,7)
                             .workplane(offset=1.4).rect(8.4,8.4).loft().val())
        speaker=cut(speaker,[hole_z(410,yy+3.5,3.4,z=11,height=7.2),hex_nut(410,yy+3.5,12,True),
                            hole_z(411.75,yy+3.5,3.4,z=101,height=10),
                            cover_nut_entry(411.75,yy+3.5,-1 if yy==41.5 else 1)])
    speaker=cut(speaker,[cylinder(3.6,7.2,(xx,yy,11)) for xx,yy in rack_holes()])
    speaker=speaker.cut(box(1,145,100,(415.9,12,12)),
                        box(60,1.35,100,(360,12,12)))
    result["carrier_speaker_right"]=speaker.clean()
    result.update(camera_parts())
    for i,(x,y,z) in enumerate(PARAMS["capacitors"]):
        ring=cylinder(6.3,4,(x,y,z)).cut(cylinder(5.3,5,(x,y,z)))
        arms=[]
        for sign in (-1,1):
            ax=x+5.3 if sign==1 else x-6.3
            arms.append(box(1,3,17.7,(ax,y-1.5,z)))
            lipx=x+4.6 if sign==1 else x-6.3
            arms.append(box(1.7,3,.8,(lipx,y-1.5,z+17.7)))
            ramp_points = ([(x+5.3,z+17),(x+4.6,z+17.7),(x+5.3,z+17.7)]
                           if sign==1 else
                           [(x-5.3,z+17),(x-4.6,z+17.7),(x-5.3,z+17.7)])
            arms.append(cq.Workplane("XZ",origin=(0,y+1.5,0)).polyline(ramp_points)
                        .close().extrude(3).val())
        ring=fuse([ring,*arms]).intersect(box(12,14,18.5,(x-6,y-7,z)))
        stem=cylinder(3.5,z-57 if i<2 else z-12,(x,y,57 if i<2 else 12)).cut(hole_z(x,y,3.4,z=11,height=z-13))
        if i<2:
            result["left_service_carrier"]=result["left_service_carrier"].fuse(ring)
            target="left_service_carrier"
        else:
            target=f"capacitor_holder_{i+1}"
            foot=cylinder(4.2,3.4,(x,y,12))
            foot_taper=cq.Solid.makeCone(4.2,5.5,1.6,cq.Vector(x,y,z-4.6))
            # Use a 26.6 degree outer taper: where it intersects a lead-bore
            # roof, the combined corner motion also stays below 45 degrees.
            ring_taper=cq.Solid.makeCone(5.5,6.3,1.6,cq.Vector(x,y,z-1.6))
            # Adjacent clips are 12 mm apart. Their ring already has the same
            # clipped side faces; the lead-in taper must stay inside them too.
            ring_taper=ring_taper.intersect(box(12,14,1.6,(x-6,y-7,z-1.6)))
            result[target]=fuse([cylinder(5.5,3,(x,y,z-3)),ring,stem,foot,foot_taper,ring_taper])
            # M3x8 enters at Z8, so its tip ends at Z16. Keep 0.25 mm
            # clearance and a permanent closed roof below the lead channels.
            # Extending this blind bore to Z18 made its roof intersect the
            # two vertical lead bores and start an unsupported inner sliver.
            result[target]=result[target].cut(hole_z(x,y,3.4,z=11,height=5.25))
            result[target]=result[target].cut(hex_nut(x,y,12,True))
        channels=[]
        for xx in (x-2,x+2):
            # Unequal diameters avoid a tangent trim cusp where the two
            # perpendicular bores meet, while preserving finite wire clearance.
            channels += [hole_y_supported(1.7,8,(xx,y-7,z-1.5)),
                         cylinder(1.8,4,(xx,y,z-3))]
        # Keep the distinct bored faces: OCC's unifier misidentifies the two
        # overlapping lead bores when a cleanup follows this multi-tool cut.
        result[target]=result[target].cut(*channels)
        if i>=2:
            result[target]=result[target].cut(hex_nut(x,y,12,True))
            result[target]=result[target].cut(shaft_vent(x,y,y-6.4))
    # Four identical clips share one exact BREP template. OCC's absolute-
    # coordinate boolean tolerance produces a sliver at the first location;
    # translating the same valid template avoids a different manufactured part.
    px,py,pz=PARAMS["capacitors"][3]
    prototype=result["capacitor_holder_4"]
    assert prototype.isValid() and len(prototype.Solids())==1
    for i,(x,y,z) in enumerate(PARAMS["capacitors"][2:],start=3):
        assert z==pz,"Standalone capacitor clips must share mounting height"
        result[f"capacitor_holder_{i}"]=prototype.translate((x-px,y-py,0))
    # Keep the floor above the entire staged M3 roof. Ending within the last
    # layer left sub-nozzle crescents around the printed shaft closure.
    coil=box(18,80,3.4,(49,165,12))
    coil=coil.fuse(box(2,80,6,(49,165,15)),box(2,80,6,(65,165,15)))
    result["usb_coil_cradle"]=cut(coil,[box(20,4.2,2,(48,yy,12)) for yy in (180,230)])
    result["usb_coil_cradle"]=cut(result["usb_coil_cradle"],
        [tool for xx,yy in ((54,170),(62,240)) for tool in (hole_z(xx,yy,3.4,z=11,height=5.5),hex_nut(xx,yy,12,True))])
    clip_pocket=cq.Workplane("YZ",origin=(15.9,103,43)).polygon(6,6.7).extrude(2.4).val()
    result["left_service_carrier"]=result["left_service_carrier"].cut(
        cylinder(1.7,8,(15.9,103,43),(1,0,0)),clip_pocket)
    # Independent service screws pass the speaker carrier without clamping it.
    bypasses=[cylinder(1.7,4,(xx,yy,12)) for xx in (22,64) for yy in (68,92)]
    bypasses += [cylinder(1.7,4,(xx,yy,12)) for xx,yy in ((20,27),(22,60))]
    result["carrier_speaker_left"]=cut(result["carrier_speaker_left"],bypasses)
    # Top service screw heads occupy open recesses in the independent speaker
    # pad. The speaker is released from its two straps for cassette service.
    result["carrier_speaker_left"]=cut(result["carrier_speaker_left"],
        [hole_z(xx,yy,6.1,z=15,height=3.1) for xx,yy in SERVICE_MOUNTS['left_service_carrier']])
    result["carrier_speaker_left"]=cut(result["carrier_speaker_left"],
        [hole_z(xx,yy,3.4,z=18,height=10) for xx,yy in SERVICE_MOUNTS['left_service_carrier']])
    # Three rectangular service feet travel vertically through these open
    # slots, with 0.1 mm CAD clearance per side. No coincident-face projection
    # booleans are used: those produce unstable OCC same-domain cleanup.
    foot_access=[box(7.2,8.2,16.1,(max(15.9,xx-3.5)-.1,yy-4.1,11))
                 for xx,yy in SERVICE_MOUNTS['left_service_carrier']]
    result['carrier_speaker_left']=cut(result['carrier_speaker_left'],foot_access)
    result['bank_holder']=cut(result['bank_holder'],foot_access)
    # Original deck/control feet bear on the deck itself. Their neighboring
    # speaker plinth has open underside recesses, rather than overlapping feet.
    deck_recesses=[box(7.5,8.5,4.25,(max(15.9,xx-3.5)-.25,yy-4.25,11))
                  for xx,yy in SERVICE_MOUNTS['left_service_carrier']]
    control_recesses=[box(10.6,7.5,120,(15.65,yy-3.75,11)) for yy in (27,60)]
    result['carrier_speaker_left']=cut(result['carrier_speaker_left'],deck_recesses+control_recesses)
    # The rear service-foot mouth ends beside a speaker nut. Its remaining
    # non-load-bearing edge skin would be narrower than one extrusion line;
    # open that edge instead of leaving a sub-nozzle fragment beside the bore.
    result['carrier_speaker_left']=result['carrier_speaker_left'].cut(box(23.6,9.75,120,(0,106,11)))
    result['carrier_speaker_right']=result['carrier_speaker_right'].cut(box(2,1.5,4.05,(405.25,115.75,11)))
    reservations=PARAMS.get("connector_reservations",{})
    if "pd_usb" in reservations:
        spec=reservations["pd_usb"]
        result["carrier_pd_trigger"]=result["carrier_pd_trigger"].cut(box(*spec["size"],spec["origin"])).clean()
    # Final bores remain open after structural roof/feet unions.
    owners={"control_cover":"switch_shelf","bank_cover":"bank_holder",
            "right_front_cover":"carrier_speaker_right","right_back_cover":"right_service_carrier"}
    for name,points in cover_mounts().items():
        nut_z=105.6 if name=="right_back_cover" else 102.6
        tools=[tool for xx,yy in points for tool in (hole_z(xx,yy,3.4,z=100,height=10),hex_nut(xx,yy,nut_z,True))]
        result[owners[name]]=cut(result[owners[name]],tools)
    # ISO4762 M4 heads reach Z15.5, 0.5 mm above the former low-head
    # envelope. Leave 0.25 mm above/around each actual purchased head.
    from chassis import rack_holes
    head_z=PARAMS["frame"]["floor_anchors"]["frame_head_z"]
    head_clearances=[cylinder(3.75,4.25,(x,y,head_z)) for x,y in rack_holes()]
    driver_passages=[hole_z(x,y,4.2,z=head_z+4,height=105) for x,y in rack_holes()]
    for name in ("carrier_speaker_left","carrier_speaker_right","bank_holder","right_service_carrier"):
        result[name]=cut(result[name],head_clearances+driver_passages)
    return result


def hole_x_supported(radius,length,origin,roof_rise=2):
    """Circular screw clearance with a shallow roof along the X axis."""
    x,y,z=origin
    divisor=math.sqrt(roof_rise**2+1)
    side_y=roof_rise*radius/divisor
    side_z=radius/divisor
    roof=(cq.Workplane("YZ",origin=origin)
          .polyline([(-side_y,side_z),(0,radius*divisor),(side_y,side_z)])
          .close().extrude(length).val())
    return cylinder(radius,length,origin,(1,0,0)).fuse(roof)


def _xz_ramp(points,y,depth):
    return (cq.Workplane("XZ",origin=(0,y+depth,0)).polyline(points)
            .close().extrude(depth).val())


def _nut_x_clearance(x,y,z,depth=2.4):
    """Outside-loaded hex with a permanent 26.6 degree upper clearance."""
    radius=3.35
    shoulder=radius*math.sqrt(3)/2
    roof=(cq.Workplane("YZ",origin=(x,y,z))
          .polyline([(-radius/2,shoulder),(0,shoulder+radius),(radius/2,shoulder)])
          .close().extrude(depth).val())
    return cq.Workplane("YZ",origin=(x,y,z)).polygon(6,6.7).extrude(depth).val().fuse(roof).clean()



@lru_cache
def _usb_tail_tools():
    """Exact cable clearance plus an open underside for intact-cable placement.

    The constant-height return bend has vertical walls and a 5.5-mm-wide
    bridge roof. The descending connector tail is opened separately below.
    """
    m=PARAMS['mechanism']
    route=flex_routing.usb_bank_tail(PARAMS['connector_reservations']['bank_usb']['endpoint'],m['origin_x']-2.4,PARAMS['usb_coil']['origin'][1])
    tube=curve_tube(route['primitives'],2.55)
    frame_tube=curve_tube(route['primitives'],2.75)
    section=tube.intersect(box(450,300,.01,(0,0,41.99)))
    openings=[]
    for face in section.Faces():
        if face.geomType()=='PLANE' and face.normalAt().z<-.99:
            openings.append(cq.Solid.extrudeLinear(face.outerWire(),face.innerWires(),cq.Vector(0,0,-4.84)))
    assert openings
    # Extend the planar footprint to Z44.75: its roof remains 4.25 mm
    # below the breaker floor, with no rounded unsupported lower lips.
    caps=[opening.translate((0,0,2.76)) for opening in openings]
    return tube,frame_tube,openings+caps

@lru_cache
def service_components():
    """Two rigid side frames, independent tiers and reversible clamp joints.

    Child coordinates are assembly coordinates. Side frames print upright;
    tiers print with their unchanged three-mm floor on the bed. Clamp tabs
    sit outside purchased envelopes; nuts enter permanent roofed outside bays.
    """
    source=_carrier_source_shapes()
    result={}
    joints=[]
    specifications={
        "left_service_carrier":{
            "edges":(15.9,18.9,66,68),
            "zones":[box(80,70,14.75,(0,55,39.25)),
                     box(31.6,14.85,28,(15.9,98.5,54)),
                     box(80,43.5,12,(0,55,70))],
            "floors":(46,57,74),"clamps":((80,43.25),(96.6,55.5),(80,71.25)),
            "keys":((67,84),(97,106),(68.25,92)),
        },
        "right_service_carrier":{
            "edges":(360.1,363,413,415.9),
            "zones":[box(70,105,13,(350,149,15)),
                     box(70,105,15,(350,149,43)),
                     box(70,93,11,(350,149,71))],
            "floors":(17,45,73),"clamps":((149,21.7),(149,49.7),(182,77.7)),
            "keys":((180,200),(180,200),(175,200)),
        },
    }
    for carrier,spec in specifications.items():
        original=source[carrier]
        outer_left,inner_left,inner_right,outer_right=spec["edges"]
        left=original.intersect(box(inner_left-outer_left,300,120,(outer_left,0,0)))
        right=original.intersect(box(outer_right-inner_right,300,120,(inner_right,0,0)))
        # Keep each original deck foot and its open service passage with its
        # side frame, rather than making a tier depend on a purchased PCB.
        for x,y in SERVICE_MOUNTS[carrier]:
            foot=original.intersect(box(12,8,3,(x-6,y-4,12)))
            if x<(inner_left+inner_right)/2: left=left.fuse(foot)
            else: right=right.fuse(foot)
        if carrier.startswith("left"):
            left=left.fuse(box(3,48,62,(15.9,63.5,12)),
                           box(3,34,7,(15.9,63.5,74)))
            right=right.fuse(box(2,30.5,69,(66,63.5,12)))
            # Reinforce the original parking-clip nut's former 0.6 mm seat.
            # The ramp clears the speaker; the unchanged cap floor is above.
            left=left.fuse(box(4.4,10,9,(15.9,98,40.25)),
                           _xz_ramp([(18.9,37.45),(18.9,40.25),(20.3,40.25)],98,10))
            # Reopen the parking clip axis after the continuous rail union.
            left=left.cut(hole_x_supported(1.7,8,(15.9,103,43)),
                         _nut_x_clearance(15.9,103,43))
        else:
            left=left.fuse(box(2.9,19.85,72.7,(360.1,138.65,12)))
            right=right.fuse(box(3,15.85,72.7,(410.2,142.65,12)),
                             _xz_ramp([(413.2,12),(413.2,17.4),(415.9,17.4)],145.6,12.9),
                             box(2.7,12.9,67.3,(413.2,145.6,17.4)))
            # The same cable windows are open above their lower edge. Removing
            # their unsupported headers preserves, rather than fills, routing.
            left=left.cut(box(4,28,90,(360,210,24)))
            for y in (156,242):
                post=original.intersect(box(7.9,8,35,(408,y,73)))
                ramp=_xz_ramp([(413,63),(413,73),(408,73)],y,8)
                right=right.fuse(post,ramp)
        frames={"left":left,"right":right}
        tiers=[]
        for number,zone in enumerate(spec["zones"],1):
            floor=spec["floors"][number-1]
            y,z=spec["clamps"][number-1]
            tier=original.intersect(zone).cut(left,right)
            tier=tier.cut(box(450,300,floor,(0,0,0)))
            base=(floor-(7.5 if number==2 else 6.75)) if carrier.startswith("left") else floor
            if carrier.startswith("left"):
                # Native-bed plinths stay below the original PCB floor. Only
                # the capacitor plinth stops ahead of the neighbouring amp.
                floor_section=tier.intersect(box(450,300,.1,(0,0,floor)))
                if number==2:
                    floor_section=floor_section.cut(box(400,300,120,(45,0,0)))
                if number==2:
                    # Lead channels begin at Z56.8. Copying their Z57 floor
                    # section into the lower plinth invents long open bores
                    # below that height and removes bridge anchors.
                    tier=tier.fuse(box(45-inner_left-.25,13,floor-base,
                                       (inner_left+.25,98.5,base)),
                                   _xz_ramp([(45,58.25),(45,60),(46.5,60)],98.5,13))
                    for cap_x,cap_y,cap_z in PARAMS["capacitors"][:2]:
                        for lead_x in (cap_x-2,cap_x+2):
                            tier=tier.cut(hole_y_supported(1.7,8,(lead_x,cap_y-7,cap_z-1.5)),
                                          cylinder(1.8,4,(lead_x,cap_y,cap_z-3)))
                else:
                    for face in floor_section.Faces():
                        if abs(face.normalAt().z+1)<1e-6:
                            tier=tier.fuse(cq.Solid.extrudeLinear(face.outerWire(),face.innerWires(),cq.Vector(0,0,base-floor)))
            if carrier.startswith("right") and number==3:
                tier=tier.fuse(box(3,85,3,(363.25,154.5,73)))
            # A quarter-mm gap allows assembly without filing either face.
            tier=tier.cut(box(inner_left+.25,300,120,(0,0,0)),
                          box(450-inner_right+.25,300,120,(inner_right-.25,0,0)))
            sides=("left",) if carrier.startswith("left") and number==2 else ("left","right")
            head_faces={}
            for side in sides:
                # The deeper outer nut seat needs a 45-degree lower bevel
                # above the speaker. Raise only this local clamp half a mm.
                z=spec["clamps"][number-1][1]+(.5 if carrier.startswith("left") and number==1 and side=="left" else 0)
                sign=1 if side=="left" else -1
                edge=inner_left if side=="left" else inner_right
                # 2.4 mm nut depth plus a full 2 mm bearing face. These local
                # bosses are outside the purchased boards, never thin backing.
                recessed=(carrier.startswith("left") and side=="left") or (carrier.startswith("right") and side=="right")
                bearing_depth=5.55 if recessed else 4.4
                clamp_edge=outer_left+bearing_depth if sign==1 else outer_right-bearing_depth
                tab_depth=7.5 if carrier.startswith("right") and number==3 else 9.4
                boss_depth=7.5 if carrier.startswith("right") and number==3 else 12.7
                boss_x=outer_left if sign==1 else outer_right-bearing_depth
                boss_top=floor+3 if carrier.startswith("left") else floor+11.7
                boss_base=base
                if carrier.startswith("left"):
                    # The speaker body ends at Z39 and the parking clip at
                    # Z47. These raised boss ramps clear both bought routes.
                    if number==1: boss_base=40.25 if side=="left" else 41
                    elif number==2: boss_base=50
                    if number==2: boss_top=63.5; tab_depth=14
                if carrier.startswith("right") and number==1: boss_base=17.4
                boss=box(bearing_depth,boss_depth,boss_top-boss_base,(boss_x,y-boss_depth/2,boss_base))
                # Grow inward at 26.6 degrees into the continuous side rail.
                ramp_rise=abs(clamp_edge-edge)*(1 if carrier.startswith("left") and number==1 and side=="right" else 2)
                boss_ramp=_xz_ramp([(edge,max(12,boss_base-ramp_rise)),
                                   (edge,boss_base),(clamp_edge,boss_base)],y-boss_depth/2,boss_depth)
                frames[side]=frames[side].fuse(boss,boss_ramp)
                if carrier.startswith("left") and number==1 and side=="left":
                    bevel=_xz_ramp([(19.75,34),(21.45,34),(21.45,40.95),(19.75,39.25)],y-boss_depth/2,boss_depth)
                    frames[side]=frames[side].cut(bevel)
                if carrier.startswith("right") and number in (1,2) and side=="right":
                    frames[side]=frames[side].fuse(_xz_ramp([(413.2,boss_base-5.4),(413.2,boss_base),(415.9,boss_base)],142.65,2.95))
                if carrier.startswith("left") and number==2 and side=="left":
                    frames[side]=frames[side].cut(box(60,28.5,100,(20.75,64.75,48.75)))
                width=8 if carrier.startswith("right") and number in (1,2) and side=="right" else 7
                if carrier.startswith("right") and number==1 and side=="left":
                    width=5.25
                if recessed: width=6.55
                face=clamp_edge+sign*(width+.25)
                head_faces[side]=face
                tab_x=clamp_edge+.25 if sign==1 else face
                tab_top=floor+3 if carrier.startswith("left") else floor+11.7
                tab=box(width,tab_depth,tab_top-base,(tab_x,y-tab_depth/2,base))
                # A short planar tongue connects the forward clamp to the
                # original floor without changing its underside elevation.
                if carrier.startswith("right") and number in (1,2):
                    tab=tab.fuse(box(max(width,7),15,3,(tab_x,149,floor)))
                tier=tier.fuse(tab)
                if carrier.startswith("left") and number==2:
                    tier=tier.cut(box(60,28.5,100,(20.75,64.75,48.75)))
                    tier=tier.fuse(box(width,6.5,3,(tab_x,93.25,60)))
                shaft=hole_x_supported(1.7,30,(clamp_edge-15,y,z),roof_rise=1)
                # Head seats from the inside, accessible after releasing its
                # board ties; a temporary nut is fitted on the outside rail.
                tier=tier.cut(shaft)
                nut_x=(outer_left+1.15 if sign==1 else outer_right-3.55) if recessed else (outer_left if sign==1 else outer_right-2.4)
                mouth_x=outer_left if sign==1 else nut_x
                nut=_nut_x_clearance(mouth_x,y,z,3.55 if recessed else 2.4)
                frames[side]=frames[side].cut(shaft,nut)
                # Keep an explicit gap around the entire clamp boss. Cutting
                # only its bored shape leaves loose slivers in the nut void.
                notch_x=0 if sign==1 else clamp_edge-.25
                notch_width=clamp_edge+.25 if sign==1 else 450-notch_x
                tier=tier.cut(box(notch_width,boss_depth+.5,120,
                                  (notch_x,y-boss_depth/2-.25,base-.25)))
                actual_key_centers=[]
                for key_y in spec["keys"][number-1]:
                    if carrier.startswith("left") and number==1 and side=="right" and key_y==67: key_y=70
                    if carrier.startswith("left") and number==3 and side=="right" and key_y==92: key_y=82
                    key_z=floor+(4.5 if carrier.startswith("left") and number==2 and key_y==97 else 1.5)
                    key_edge=clamp_edge if abs(key_y-y)<boss_depth/2+2 else edge
                    actual_key_centers.append([key_edge+sign*.5,key_y,key_z])
                    key_x=key_edge if sign==1 else key_edge-1
                    key=box(1,3,1,(key_x,key_y-1.5,key_z-.5))
                    ramp=_xz_ramp([(key_edge,key_z-2.5),(key_edge,key_z-.5),
                                   (key_edge+sign,key_z-.5)],key_y-1.5,3)
                    frames[side]=frames[side].fuse(key,ramp)
                    pocket_x=key_edge-.25 if sign==1 else key_edge-1.25
                    tier=tier.cut(box(1.5,3.5,120,(pocket_x,key_y-1.75,key_z-.75)))
                # Keys must not refill a nominal screw or nut passage.
                frames[side]=frames[side].cut(shaft,nut)
                joints.append({"id":f"{carrier}_tier_{number}_{side}",
                               "carrier":carrier,"frame":f"{carrier}_frame_{side}",
                               "tier":f"{carrier}_tier_{number}","head_face":[face,y,z],
                               "nut_origin":[nut_x,y,z],"axis":[-sign,0,0],
                               "bolt":f"M3x{16 if width==8 else 10 if width==5.25 else 12} ISO7380","nut":"M3 ISO4032",
                               "quantity":1,"key_count":2,"clearance_mm":.25,
                               "key_centers":actual_key_centers,
                               "nut_bearing_mm":2,
                               "minimum_nut_bearing_mm":1.48 if carrier.startswith("left") and number==1 and side=="right" else 2,
                               "track":"Roofed outside bay; bench insertion before boards",
                               "access":"Underside head channel on bench" if carrier.startswith("left") else "Inward head face on bench"})
            tier=tier.cut(*frames.values())
            if carrier.startswith("left"):
                left_face=head_faces["left"]
                right_face=head_faces.get("right",45)
                roof_z=max(joint["head_face"][2] for joint in joints if joint["tier"]==f"{carrier}_tier_{number}")+3.1
                if number==2:
                    tier=tier.cut(hole_x_supported(3.1,2,(left_face,y,z),roof_rise=1))
                else:
                    tier=tier.cut(box(right_face-left_face,6.2,roof_z-base+1,
                                      (left_face,y-3.1,base-1)))
                if number==1:
                    # The speaker's upper tie crosses below the breaker.
                    # An open underside groove preserves that finite route;
                    # it has no sub-layer film left across its bed opening.
                    tier=tier.cut(box(70,4.5,2.25,(0,86.1,38)),
                                   cq.Workplane("YZ").polyline([(86.1,40.25),(88.1,42.25),(88.6,42.25),(90.6,40.25)]).close().extrude(70).val())
                    tier=tier.cut(box(21.7,4.5,120,(0,86.1,38)),
                                  box(400,4.5,120,(63.35,86.1,38)))
            if carrier.startswith("right") and number==3:
                tier=tier.cut(box(7,6,15,(406.75,234.25,72)))
                for side,face in head_faces.items():
                    relief_x=face if side=="left" else face-2
                    tier=tier.cut(box(2,6.2,120,(relief_x,y-3.1,floor)))
            if carrier.startswith("right") and number==1:
                tier=tier.cut(box(2,6.2,120,(head_faces["left"],y-3.1,floor)))
            if carrier.startswith("left") and number==1:
                tier=tier.cut(box(5.3,3.7,10.6,(30.6,91.7,38)))
            tier=tier.cut(*[hole_z(x,y,6.1,z=15,height=100) for x,y in SERVICE_MOUNTS[carrier]])
            tiers.append(tier)
        # Structural unions never close the original seven vertical deck
        # shafts or the continuous top-service head/tool passages.
        for side,frame in frames.items():
            frame=cut(frame,[tool for x,y in SERVICE_MOUNTS[carrier] for tool in
                            (hole_z(x,y,3.4,z=11,height=5),hole_z(x,y,6.1,z=15,height=100))])
            if carrier.startswith("right") and side=="right":
                # Keep the reinforced fore foot above the independent speaker
                # floor; the two-mm nut bearing plane remains untouched.
                frame=frame.cut(_xz_ramp([(413.2,12),(415.9,12),
                    (415.9,20.65),(413.2,15.25)],142.65,2.95))
            if carrier.startswith("left") and side=="right":
                # The rear fork carried only a redundant locating key. Keep
                # that key on the supported main rail, not a fragile upright.
                frame=frame.cut(box(10,3.5,40,(66,94,70)),
                                box(4,6,40,(65,88,48)))
            names=LEFT_STACK+("speaker_left",) if carrier.startswith("left") else RIGHT_STACK
            frame=cut(frame,[t.shape for name in names for t in ties(name)])
            if carrier.startswith("left"):
                for name in LEFT_STACK:
                    x,yy,zz=COMPONENTS[name]["origin"]
                    w,d,h=COMPONENTS[name]["size"]
                    frame=frame.cut(*[box(w+4,4,120,(x-2,yy+d*t-2,zz-1)) for t in (.25,.75)])
            if carrier.startswith("left") and side=="left":
                frame=frame.cut(hole_x_supported(1.7,8,(15.9,103,43)),_nut_x_clearance(15.9,103,43))
            frames[side]=frame
        names=LEFT_STACK+("speaker_left",) if carrier.startswith("left") else RIGHT_STACK
        # Later-tier bosses can reach the previous floor; retain the actual
        # final receiving frame clearance rather than an earlier snapshot.
        tiers=[tier.cut(*frames.values(),*[t.shape for name in names for t in ties(name)]).clean() for tier in tiers]
        if carrier.startswith('left'):
            # Clamp-tab unions must not close the capacitor's fixed terminal
            # openings. Restore the original lead channels after every union.
            lead_tools=[]
            for cap_x,cap_y,cap_z in PARAMS['capacitors'][:2]:
                for lead_x in (cap_x-2,cap_x+2):
                    lead_tools.append(hole_y_supported(1.7,8,(lead_x,cap_y-7,cap_z-1.5)))
                # One planar well avoids intersecting tessellated circular
                # trim edges, and removes the fragile divider between leads.
                lead_tools.append(box(7.8,3.8,4,(cap_x-3.9,cap_y-1.9,cap_z-3)))
            tiers[1]=tiers[1].cut(*lead_tools)
            usb=box(12.5,20.5,12.5,(27,94.1,39.75))
            roof=(cq.Workplane("XZ",origin=(0,94.1,0)).polyline([(27,52.25),(27.65,52.9),(38.85,52.9),(39.5,52.25)]).close().extrude(-20.5).val())
            front_tip=_xz_ramp([(27,52.25),(28.25,53.5),(28.25,52.25)],94.1,4.4)
            # Open the rear wire route to the top: a roof above the Z58.5
            # conductors would leave a sub-nozzle film under the Z60 caps.
            # The four spring arms and forward bearing region remain whole.
            tiers[1]=tiers[1].cut(usb,roof,front_tip,
                hole_y_supported(1.7,5.2,(26,105,58.5)),
                hole_y_supported(1.7,7.9,(30,105,58.5)),
                hole_x_supported(1.7,22.2,(24.3,110.2,58.5),roof_rise=1),
                hole_x_supported(1.7,18.2,(28.3,112.9,58.5),roof_rise=1),
                box(24.5,6.1,30,(22,108.5,56.8)))
            # The new deep plinth must preserve the existing USB-PD passage.
            # Its 45-degree roof peaks below the unchanged PCB floor at Z46.
            tiers[0]=tiers[0].cut(hole_x_supported(2.75,52,(18,97,42),roof_rise=1),
                                 box(52,5.5,3,(18,94.25,39)))
        if carrier.startswith("left"):
            # Place the intact cable before the tiers. Both frame mouths
            # reach an edge; the moulded plugs never thread a small window.
            usb_tube,usb_frame_tube,underside_openings=_usb_tail_tools()
            tiers[0]=tiers[0].cut(usb_tube,*underside_openings,
                                  box(5.5,39.2,10,(30.5,75.3,38.55)),
                                  box(1,1,15,(65,64,38)),
                                  box(21,3.4,10,(0,90.6,49)),
                                  box(21.7,1,10,(0,93,49)),
                                  box(7.05,1.25,4,(21.45,93,49.25))).clean()
            front_mouth=box(2,4.5,5.5,(66,63.5,39.25))
            front_roof=(cq.Workplane("YZ",origin=(66,0,0)).polyline([(63.5,44.75),(63.5,49.25),(68,44.75)]).close().extrude(2).val())
            rear_mouth=box(2,8.5,5.5,(66,85.5,39.25))
            rear_roof=(cq.Workplane("YZ",origin=(66,0,0)).polyline([(85.5,44.75),(94,53.25),(94,44.75)]).close().extrude(2).val())
            frames["right"]=frames["right"].cut(usb_frame_tube,front_mouth,front_roof,rear_mouth,rear_roof).clean()
        owner_names=(("breaker","amplifier_left"),(),("buck",)) if carrier.startswith("left") else (("perfboard_1",),("perfboard_2",),("degson_1","degson_2","degson_3"))
        for index,tier in enumerate(tiers):
            for name in owner_names[index]:
                x,yy,zz=COMPONENTS[name]["origin"]
                w,d,h=COMPONENTS[name]["size"]
                tier=tier.cut(*[box(w+4,4,8,(x-2,yy+d*t-2,zz-1)) for t in (.25,.75)])
            tiers[index]=tier
        for side,frame in frames.items():
            result[f"{carrier}_frame_{side}"]=Part(f"{carrier}_frame_{side}",frame,1,
                "Upright side frame: original deck feet on bed; keyed M3-clamped tiers; no support")
        for number,tier in enumerate(tiers,1):
            name=f"{carrier}_tier_{number}"
            bed="Permanent flat underside plinth on bed" if carrier.startswith("left") else "Original flat floor on bed"
            result[name]=Part(name,tier,1,bed+"; two locating keys per M3 clamp; bench assembly; no support or sacrificial membrane")
    return result,tuple(joints)


def carrier_shapes():
    """Assembled carriers remain available to existing service-path callers."""
    result=_carrier_source_shapes().copy()
    children,_=service_components()
    for carrier in SERVICE_MOUNTS:
        result[carrier]=cq.Compound.makeCompound([part.shape for name,part in children.items()
                                                  if name.startswith(carrier+"_")])
    for name,shape in result.items():
        if name not in SERVICE_MOUNTS:
            result[name]=installed_shape(name,shape)
    return result


def service_joint_hardware():
    """Standard bench-assembled fasteners for the reversible cassette joints."""
    result=[]
    for joint in service_components()[1]:
        origin=cq.Vector(*joint["head_face"])
        axis=cq.Vector(*joint["axis"])
        length=int(joint["bolt"].split()[0].removeprefix("M3x"))
        shaft=cylinder(1.5,length,origin.toTuple(),axis.toTuple())
        head=cylinder(2.85,1.65,(origin-axis*1.65).toTuple(),axis.toTuple())
        name=joint["id"].replace("_tier_","_joint_tier_")
        result.append(Item(name+"_screw",shaft.fuse(head),"fastener","metal",
                           joint["bolt"]+"; inward face accessible before tying PCB; bench release"))
        x,y,z=joint["nut_origin"]
        nut=cq.Workplane("YZ",origin=(x,y,z)).polygon(6,6.4).extrude(2.4).val()
        nut=nut.cut(cylinder(1.5,2.6,(x-.1,y,z),(1,0,0)))
        result.append(Item(name+"_nut",nut,"hardware","metal",
                           "M3 ISO4032; outside-loaded captive hex; 2 mm printed bearing face"))
    return result


def parts():
    result=[Part(n,manufactured_shape(n,s),1,"Positive perimeter stops; mounted independently to frame" + print_finishing(n),
                 ((0,1,0),-90) if n=="usb_park_clip" else
                 ((0,1,0),-90) if n in ("switch_frame_left","camera_post_left") else
                 ((0,1,0),90) if n in ("switch_frame_right","camera_post_right") else None) for n,s in carrier_shapes().items()]
    result=[part for part in result if part.name not in SERVICE_MOUNTS]
    result += list(service_components()[0].values())
    result += [Part(n,s,1,"Removable non-load-bearing textile cover; broad roof on bed", ((1,0,0),180)) for n,s in covers().items()]
    result += [Part("fit_coupon",fit_coupon(),1,"Fastener/guide coupon plus upward integral peg and 1 mm axial seat; adjust printer and reprint, never file/sand")]
    result += [Part("floor_anchor_coupon",floor_anchor_coupon(),1,
                   "Inverted actual anchor nut/epoxy roof gauge; temporary screw-seat nuts; no support",((1,0,0),180))]
    result += [Part("follower_groove_coupon",follower_groove_coupon(),1,"6.8 mm straight clearance slot, 6 mm depth; print groove-up like cam; pair with peg coupon, not motion proof; reprint to correct fit",((0,1,0),-90))]
    return result


def membrane_locations(name):
    """Exact native-coordinate finishing records shared by CAD and manifest."""
    axes = []
    if name.startswith("carrier_"):
        component = name.removeprefix("carrier_")
        if component in COMPONENTS and component != "microphone":
            axes = mount_positions(component)
    elif name == "bank_holder":
        axes = mount_positions("powerbank")
    elif name == "usb_coil_cradle":
        axes = [(54,170),(62,240)]
    elif name.startswith("capacitor_holder_"):
        index = int(name.removeprefix("capacitor_holder_")) - 1
        axes = [PARAMS["capacitors"][index][:2]]
    if name == "carrier_speaker_right":
        axes += [(410,45),(410,125)]
    pilot_x,pilot_y = (6.7,2) if name in (
        "carrier_mcp23017","bank_holder","carrier_speaker_left","carrier_speaker_right") else (2,6.7)
    records = [(x,y,14.4,"base") for x,y in sorted(set(tuple(axis) for axis in axes))]
    owner = {"bank_holder":"bank_cover", "switch_shelf":"control_cover",
             "carrier_speaker_right":"right_front_cover"}.get(name)
    if owner:
        records += [(x,y,105,"cover") for x,y in cover_mounts()[owner]]
    def pilot_size(x,y):
        # The independent rear cassette foot removes this nut's left anchor.
        # Its roof uses front/rear anchors; the other speaker nuts use X.
        if name=="carrier_speaker_left" and (x,y)==tuple(mount_positions("speaker_left")[1]):
            # A three-nozzle-width slit keeps more metal bearing at the
            # clipped rear seat without narrowing the final 3.4 mm bore.
            return [1.2,6.7]
        return [pilot_x,pilot_y]
    return [{"kind":kind,"coordinate_mm":[x,y,z+.4],
             "pilot_bottom_z_mm":z,"pilot_height_mm":.4,
             "pilot_relief_size_mm":pilot_size(x,y),
             "filled_stage_bottom_z_mm":z,"filled_stage_height_mm":.8,
             "final_puncture_thickness_mm":.4,"open_diameter_mm":3.4,
             "method":"Smooth hand awl before fitting screws; clear the nominal bore without drilling or filing"}
            for x,y,z,kind in records]


def print_finishing(name):
    """Describe only finishing actually required by this manufactured part."""
    locations = membrane_locations(name)
    return (f"; open {len(locations)} staged screw pilots with a smooth hand awl before fitting; "
            "0.4 mm final closure and 0.4 mm pilot ears (0.8 mm maximum local material), "
            "nominal bore 3.4 mm; no drill/file"
            if locations else "")


def _pilot_tools(name):
    tools=[]
    for location in membrane_locations(name):
        x,y,_ = location["coordinate_mm"]
        z = location["pilot_bottom_z_mm"]
        pilot_x,pilot_y = location["pilot_relief_size_mm"]
        tools.append(box(pilot_x,pilot_y,.4,(x-pilot_x/2,y-pilot_y/2,z)))
    return tools


def installed_shape(name,shape):
    """Permanent pilot relief stays in the assembled, punctured CAD shape."""
    tools=_pilot_tools(name)
    return shape.cut(*tools) if tools else shape


def manufactured_shape(name, shape):
    """Stage nut-bearing roofs; installed scene shapes retain open bores."""
    locations = membrane_locations(name)
    # Preserve flat nut-bearing wings before closing the small pilot. A full
    # circular closure at the nut's roof made Prusa start infill inside the
    # hexagonal void, clear of both anchors. The first 0.4 mm roof instead
    # spans a narrow slit between the retained bearing wings.
    pilots=_pilot_tools(name)
    caps=[]
    for location in locations:
        x,y,_ = location["coordinate_mm"]
        z = location["pilot_bottom_z_mm"]
        # Fill exactly the nominal shaft: after puncture the printed part
        # must equal the installed scene, including a floor ending mid-stage.
        caps.append(cylinder(1.7,.8,(x,y,z)))
    if name=="carrier_speaker_left":
        # A diaphragm closes only the shaft inside this printed holder. It
        # must never restore plastic in the independent service-foot mouth.
        caps=[cap.cut(box(23.6,9.75,.8,(0,106,14.4))) for cap in caps]
    # Cut last: a diaphragm must not refill the earlier pilot relief.
    manufactured = shape.fuse(*caps) if caps else shape
    return manufactured.cut(*pilots) if pilots else manufactured


def switch(name,center,z,rocker=True):
    x,y=center
    r,h,face=(9.5,27,24) if rocker else (6,32,14)
    return Item(name,cylinder(r,h,(x,y,z)).fuse(box(face,face,3,(x-face/2,y-face/2,z+h))),"hardware","black" if rocker else "white","Purchased switch; face/barrel allowances")


def hardware():
    result=[Item(n,box(*s["size"],s["origin"]),"hardware","black" if n.startswith(("speaker","powerbank")) else "pcb",s["orientation"])
            for n,s in COMPONENTS.items()]
    for i,(x,y,z) in enumerate(PARAMS["capacitors"]):
        result.append(Item(f"capacitor_{i+1}",cylinder(5,17,(x,y,z)),"hardware","black","470uF D10 H17; strapped positive holder"))
    result += [switch(n,p[:2],p[2],n!="white_ptt") for n,p in PARAMS["panel"].items()]
    return result


def ties(name):
    x,y,z=COMPONENTS[name]["origin"]
    w,d,h=COMPONENTS[name]["size"]
    result=[]
    for j,yy in enumerate((y+d*.25,y+d*.75)):
        if name in ("esp32","microphone"):
            xx=x+w*(.25 if j==0 else .75)
            # Shift the short microphone wrap by 0.35 mm without thinning
            # its 0.5 mm strap or moving the purchased board. Both legs still
            # clear the board and its original open carrier slots.
            tie_y=max(y-1,13.35) if name=="microphone" else y-1
            outer=box(2.5,d+2,h+2,(xx-1.25,tie_y,z-1))
            inner=box(4,d+1,h+1,(xx-2,tie_y+.5,z-.5))
        elif name=="powerbank":
            outer=box(w+2.2,8,h+2,(x-1.1,yy-4,z-1))
            inner=box(w+.2,10,h,(x-.1,yy-5,z))
        else:
            outer=box(w+2,2.5,h+2,(x-1,yy-1.25,z-1))
            inner=box(w+1,4,h+1,(x-.5,yy-2,z-.5))
        source="8 mm releasable hook-loop wrap x300; daily bank removal" if name=="powerbank" else "Two ties; edge stops resist in-plane loads"
        result.append(Item(f"component_tie_{name}_{j+1}",outer.cut(inner),"restraint","black",source))
    return result


def items(with_covers=True,with_bank=True):
    result=[Item(n,s,"fixed","mechanism") for n,s in carrier_shapes().items() if n not in SERVICE_MOUNTS]
    result += [Item(n,p.shape,"fixed","mechanism","Reversible keyed cassette subassembly")
               for n,p in service_components()[0].items()]
    result += service_joint_hardware()
    result += camera_joint_hardware()
    result += switch_joint_hardware()
    result += [i for i in hardware() if with_bank or i.name!="powerbank"]
    for n in COMPONENTS:
        if with_bank or n!="powerbank":
            result += ties(n)
    excluded=set(LEFT_STACK+RIGHT_STACK+("camera","microphone"))
    mounts=[(f"{name}_{j+1}",xx,yy) for name in COMPONENTS if name not in excluded
            for j,(xx,yy) in enumerate(mount_positions(name))]
    mounts += [(f"switch_shelf_{j+1}",xx,yy) for j,(xx,yy) in enumerate(((20,27),(22,60)))]
    mounts += [(f"coil_cradle_{j+1}",xx,yy) for j,(xx,yy) in enumerate(((54,170),(62,240)))]
    mounts += [(f"capacitor_holder_{j+3}",xx,yy) for j,(xx,yy,zz) in enumerate(PARAMS["capacitors"][2:])]
    for name,x,y in mounts:
        result += [Item(f"carrier_shaft_{name}",cylinder(1.5,8,(x,y,8)),"fastener","metal","M3x8, fit carrier before purchased item"),
                   Item(f"carrier_head_{name}",cylinder(2.85,1.65,(x,y,6.35)),"hardware","metal","M3 ISO7380; underside head clears 5 mm brace"),
                   Item(f"carrier_nut_{name}",hex_nut(x,y,12),"hardware","metal","Captive M3 hex nut in 6.7 mm corner pocket")]
    for cassette,rows in SERVICE_MOUNTS.items():
        for j,(x,y) in enumerate(rows,1):
            name=f"{cassette.removesuffix('_carrier')}_{j}"
            result += [Item(f"carrier_shaft_{name}",cylinder(1.5,8,(x,y,SEAT_Z-8)),"fastener","metal","M3x8; top service screw; narrow hex shaft, handle above rim"),
                       Item(f"carrier_head_{name}",cylinder(HEAD_RADIUS,1.65,(x,y,SEAT_Z)),"hardware","metal"),
                       Item(f"carrier_nut_{name}",hex_nut(x,y,NUT_Z),"hardware","metal","Deck captive nut; fit from underside on bench before case installation")]
    if with_covers:
        result += [Item(n,s,"cover","cover") for n,s in covers().items()]
        for name,points in cover_mounts().items():
            for j,(xx,yy) in enumerate(points):
                nut_z=105.6 if name=="right_back_cover" else 102.6
                result += [Item(f"cover_shaft_{name}_{j+1}",cylinder(1.5,10,(xx,yy,101)),"fastener","metal","M3x10 accessible from above"),
                           Item(f"cover_head_{name}_{j+1}",cylinder(2.85,1.65,(xx,yy,111)),"hardware","metal"),
                           Item(f"cover_nut_{name}_{j+1}",hex_nut(xx,yy,nut_z),"hardware","metal","Captive hex nut in independent post")]
    sx,sy,sz=COMPONENTS["microphone"]["origin"]
    sw=COMPONENTS["microphone"]["size"][0]
    for j,xx in enumerate((sx+3,sx+sw-3)):
        screw=fuse([cylinder(1.5,8,(xx,14,24),(0,1,0)),cq.Solid.makeCone(3,1.5,1.5,cq.Vector(xx,14,24),cq.Vector(0,1,0))])
        result += [Item(f"mic_mount_shaft_{j+1}",screw,"fastener","metal","M3x8 countersunk flush with front rail Y14"),
                   Item(f"mic_mount_nut_{j+1}",cylinder(3.2,2.4,(xx,19,24),(0,1,0)).cut(cylinder(1.5,3,(xx,18.9,24),(0,1,0))),"hardware","metal")]
    for j,xx in enumerate((184,247)):
        screw=fuse([cylinder(1.5,8,(xx,245,24),(0,1,0)),cq.Solid.makeCone(3,1.5,1.5,cq.Vector(xx,253,24),cq.Vector(0,-1,0))])
        result += [Item(f"camera_mount_shaft_{j+1}",screw,"fastener","metal","M3x8 countersunk flush with rear rail Y253"),
                   Item(f"camera_mount_nut_{j+1}",cylinder(3.2,2.4,(xx,245.6,24),(0,1,0)).cut(cylinder(1.5,3,(xx,245.5,24),(0,1,0))),"hardware","metal")]
    clip_screw=fuse([cylinder(1.5,6,(15.9,103,43),(1,0,0)),cq.Solid.makeCone(3,1.5,1.5,cq.Vector(21.9,103,43),cq.Vector(-1,0,0))])
    clip_nut=cq.Workplane("YZ",origin=(15.9,103,43)).polygon(6,6.4).extrude(2.4).val().cut(cylinder(1.5,3,(15.8,103,43),(1,0,0)))
    result += [Item("usb_clip_shaft",clip_screw,"fastener","metal","M3x6 countersunk; head flush, captive nut in fixed carrier"),
               Item("usb_clip_nut",clip_nut,"hardware","metal")]
    for j,yy in enumerate((180,230)):
        wrap=box(19.4,3.6,74,(48.3,yy,12)).cut(box(18.4,5.1,72.5,(48.8,yy-.75,13)))
        result.append(Item(f"usb_coil_wrap_{j+1}",wrap,"restraint","black",
                           "3.6 x 250 mm PA66 tie through independent cradle slots; replace for service, do not flatten USB"))
    return result


def reservations():
    result=[]
    for name,spec in PARAMS["connector_reservations"].items():
        result.append(Item("esp_usb" if name=="esp32_usb" else name,box(*spec["size"],spec["origin"]),
                           "access","white",spec.get("status","Provisional actual plug dry fit pending")))
    m=PARAMS["mechanism"]
    for i in range(m["count"]):
        reserve=flex_routing.loop_reservation(i+1,m["origin_x"],m["pitch"])
        result.append(Item(reserve["id"],box(*reserve["size"],reserve["origin"]),
                           "cable","black",reserve["status"]))
    return result
