"""Compact bolted service carriers; purchased envelopes remain source-derived."""
from functools import lru_cache
import cadquery as cq
from geometry import PARAMS, Part, Item, box, cylinder, cut, fuse, hole_z

COMPONENTS = PARAMS["components"]
Z = PARAMS["mounting"]["cover_z"]
RACKS = {}
LEFT_STACK = ("breaker", "buck", "amplifier_left")
RIGHT_STACK = ("perfboard_1", "perfboard_2", "degson_1", "degson_2", "degson_3")


def hex_nut(x,y,z,pocket=False):
    shape=cq.Workplane("XY",origin=(x,y,z)).polygon(6,6.7 if pocket else 6.4).extrude(2.4).val()
    return shape if pocket else shape.cut(hole_z(x,y,3,z=z-.1,height=3))


def pocket(name):
    """Unmeasured board holes are avoided: perimeter stops and two ties."""
    spec=COMPONENTS[name]
    x,y,z=spec["origin"]
    w,d,h=spec["size"]
    margin = 0.3 if name in ("speaker_left","speaker_right","powerbank","pd_trigger","amplifier_left","amplifier_right","breaker") else 1.5
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
    for yy in (y+d*.25,y+d*.75):
        slot_width=8.4 if name=="powerbank" else 4
        holes += [box(2.8,slot_width,6,(xx,yy-slot_width/2,z-4)) for xx in (x-1.5,x+w-1.3)]
    result=cut(fuse([pad,*lips]),holes)
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


def free_carrier(name):
    x,y,z=COMPONENTS[name]["origin"]
    w,d,h=COMPONENTS[name]["size"]
    shape=pocket(name)
    holes=[]
    if name=="microphone":
        shape=shape.fuse(box(w+3,2,z-15,(x-1.5,17,12)))
        for xx in (x+3,x+w-3):
            shape=shape.cut(cylinder(1.7,5,(xx,15,24),(0,1,0)))
        return shape.clean()
    # Mount screws enter from below before the component/ties are fitted.
    # Captive nuts stop at z-3; purchased boards never have imaginary holes.
    for xx,yy in mount_positions(name):
        shape=shape.fuse(cylinder(3.5,z-12,(xx,yy,12)))
        holes.append(hole_z(xx,yy,3.4,z=11,height=z-12.5))
        holes.append(hex_nut(xx,yy,12,True))
    return cut(cut(shape,holes),[t.shape for t in ties(name)])


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
    for xx,px in ((22,15.9),(64,59)):
        for yy in (68,92):
            left.append(box(10,6,3,(px,yy-3,12)))
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
    for xx,px in ((365,360.1),(410,406)):
        for yy in (160,247):
            right.append(box(9.9,6,3,(px,yy-3,12)))
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
    for shape_name,shape,rows in (("left",left_shape,[(xx,yy) for xx in (22,64) for yy in (68,92)]),
                                  ("right",right_shape,[(xx,yy) for xx in (365,410) for yy in (160,247)])):
        tools=[hole_z(xx,yy,3.4,z=11,height=5) for xx,yy in rows]
        tools += [hex_nut(xx,yy,12,True) for xx,yy in rows]
        if shape_name=="left": left_shape=cut(shape,tools)
        else: right_shape=cut(shape,tools)
    return {"left_service_carrier":left_shape,"right_service_carrier":right_shape}


def bank_holder():
    x,y,z=COMPONENTS["powerbank"]["origin"]
    w,d,h=COMPONENTS["powerbank"]["size"]
    result=free_carrier("powerbank")
    for yy in (149,246):
        result=result.fuse(cover_post(48,yy,12,7,7),box(12,7,3,(43,yy,12)))
        cx,cy=51.5,yy+3.5
        result=cut(result,[hole_z(cx,cy,3.4,z=11,height=5.5),hex_nut(cx,cy,12,True)])
    # Low stops locate the removable content. Both straps release upwards;
    # neither shelf, electronics nor decorative roof rests on the bank.
    from chassis import rack_holes
    result=cut(result,[tool for xx,yy in mount_positions("powerbank") for tool in
               (hole_z(xx,yy,3.4,z=11,height=5.5),hex_nut(xx,yy,12,True))])
    return cut(result,[cylinder(3.6,3,(xx,yy,12)) for xx,yy in rack_holes()])


def switch_shelf():
    points=PARAMS["panel"]
    shape=box(53.1,42,3,(15.9,15,102))
    holes=[]
    for name,(x,y,z) in points.items():
        holes.append(hole_z(x,y,19.4 if name!="white_ptt" else 12.4,z=101,height=6))
    # Dedicated corner legs avoid speaker face and carry only controls.
    for xx in (15.9,66):
        for yy in (15,54):
            shape=shape.fuse(box(3 if xx==15.9 else 2,3,90,(xx,yy,12)))
    holes.append(box(2.05,42,4,(15.9,15,102)))
    shape=cut(shape,holes)
    shape=shape.fuse(box(3,48.5,3,(15.9,15,12)))
    for yy in (19.5,56.5):
        shape=shape.fuse(box(3,7,87,(15.9,yy,12)),cover_post(15.9,yy,99))
    for xx,yy in ((20,27),(22,60)):
        shape=shape.fuse(box(10.1,7,3,(15.9,yy-3.5,12)))
        shape=cut(shape,[hole_z(xx,yy,3.4,z=11,height=5.5),hex_nut(xx,yy,12,True)])
    from chassis import rack_holes
    return cut(shape,[cylinder(3.6,3,(xx,yy,12)) for xx,yy in rack_holes()])


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
    holes=[hole_z(px,py,12.4),box(14.5,14.5,1.2,(px-7.25,py-7.25,109.8))]
    holes += [box(25,25,5,(panel[n][0]-12.5,panel[n][1]-12.5,Z-1)) for n in ("red_main","green_drives")]
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
            skirt=skirt.cut(box(4,7.15,62,(15,104.35,12)))
        if name=="control_cover":
            skirt=cut(skirt,[box(4,7.4,96,(15,yy-3.7,12)) for yy in (23,60)])
            skirt=skirt.cut(box(4,48.5,3.1,(15,15,12)))
        if name=="right_front_cover":
            skirt=cut(skirt,[box(3,7.4,96,(413,yy-3.7,12)) for yy in (45,125)])
            skirt=skirt.cut(box(3,132,3.1,(413,13.35,12)))
        s=fuse([result[name],skirt])
        result[name]=s.clean()
    for name,points in cover_mounts().items():
        result[name]=cut(result[name],[hole_z(xx,yy,3.4,z=107,height=6) for xx,yy in points])
    return result


def cover_mounts():
    return {"control_cover":((19.65,23),(19.65,60)),"bank_cover":((51.5,152.5),(51.5,249.5)),
            "right_front_cover":((411.75,45),(411.75,125)),"right_back_cover":((411.75,160),(411.75,246))}


def camera_bracket():
    return free_carrier("camera")


def fit_coupon():
    shape=box(85,58,4).fuse(box(8,10,22,(5,34,4)))
    holes=[hole_z(x,12,d,z=-1,height=6) for x,d in zip((10,25,41,61),(3.4,4.5,12.4,19.4))]
    holes += [box(8+2*c,10+2*c,6,(x,33,-1)) for x,c in ((25,.3),(44,.45),(64,.6))]
    return cut(shape,holes)


def usb_clip():
    # Split open parking jaws, screw-mounted at front edge of bank zone.
    shape=fuse([box(8,10,1,(18.9,98,39)),box(8,2,7,(18.9,98,40)),
                box(8,2,7,(18.9,106,40)),box(3,10,7,(18.9,98,40))])
    countersink=cq.Solid.makeCone(3.1,1.5,1.6,cq.Vector(21.9,103,43),cq.Vector(-1,0,0))
    return cut(shape,[cylinder(1.7,4,(18,103,43),(1,0,0)),countersink])


def carrier_shapes():
    result=service_platforms().copy()
    for name in COMPONENTS:
        if name in LEFT_STACK+RIGHT_STACK or name=="powerbank":
            continue
        result["camera_angle" if name=="camera" else f"carrier_{name}"]=free_carrier(name)
    result["bank_holder"]=bank_holder()
    # Boundary stops cannot protrude beyond the owner-confirmed safe box.
    result["bank_holder"]=result["bank_holder"].cut(box(60,1,100,(15.9,253.35,12))).clean()
    result["carrier_speaker_left"]=result["carrier_speaker_left"].cut(box(60,1,20,(15.9,12.35,10))).clean()
    # The metal upright occupies the rear margin of this shallow carrier.
    from chassis import rail_shape
    rear=next(s for s in PARAMS["frame"]["stiffeners"] if s["id"]=="rear_crossrail")
    result["carrier_pca9685"]=result["carrier_pca9685"].cut(rail_shape(rear)).clean()
    result["switch_shelf"]=switch_shelf()
    result["usb_park_clip"]=usb_clip()
    speaker=result["carrier_speaker_right"]
    speaker=speaker.fuse(box(2.45,132,3,(413.45,13.35,12)))
    for yy in (41.5,121.5):
        speaker=speaker.fuse(box(2.45,7,87,(413.45,yy,12)),cover_post(408,yy,99),box(9.9,7,3,(406,yy,12)))
        speaker=cut(speaker,[hole_z(410,yy+3.5,3.4,z=11,height=5.5),hex_nut(410,yy+3.5,12,True)])
    result["carrier_speaker_right"]=speaker.clean()
    # Camera uses one rear rail behind ESP; its delivered FPC is unmeasured.
    x,y,z=COMPONENTS["camera"]["origin"]
    rails=[box(2,3,52,(xx,250,29)) for xx in (183,246)]
    feet=[box(6,2,12,(xx,248,20)) for xx in (181,244)]
    feet += [box(6,5,3,(xx,248,29)) for xx in (181,244)]
    camera_support=fuse([pocket("camera"),*rails,*feet,box(65,16,3,(183,237,81))])
    camera_support=cut(camera_support,[cylinder(1.7,5,(xx,248,24),(0,1,0)) for xx in (184,247)])
    result["camera_angle"]=cut(camera_support,[t.shape for t in ties("camera")])
    for i,(x,y,z) in enumerate(PARAMS["capacitors"]):
        ring=cylinder(6.3,4,(x,y,z)).cut(cylinder(5.3,5,(x,y,z)))
        arms=[]
        for sign in (-1,1):
            ax=x+5.3 if sign==1 else x-6.3
            arms.append(box(1,3,17,(ax,y-1.5,z)))
            lipx=x+4.6 if sign==1 else x-6.3
            arms.append(box(1.7,3,.8,(lipx,y-1.5,z+17)))
        ring=fuse([ring,*arms]).intersect(box(12,14,18,(x-6,y-7,z)))
        stem=cylinder(3.5,z-57 if i<2 else z-12,(x,y,57 if i<2 else 12)).cut(hole_z(x,y,3.4,z=11,height=z-13))
        if i<2:
            result["left_service_carrier"]=result["left_service_carrier"].fuse(ring)
            target="left_service_carrier"
        else:
            target=f"capacitor_holder_{i+1}"
            result[target]=fuse([cylinder(5.5,3,(x,y,z-3)),ring,stem])
            result[target]=result[target].cut(hex_nut(x,y,12,True))
        channels=[]
        for xx in (x-2,x+2):
            # Unequal diameters avoid a tangent trim cusp where the two
            # perpendicular bores meet, while preserving finite wire clearance.
            channels += [cylinder(1.7,8,(xx,y-7,z-1.5),(0,1,0)),
                         cylinder(1.8,4,(xx,y,z-3))]
        # Keep the distinct bored faces: OCC's unifier misidentifies the two
        # overlapping lead bores when a cleanup follows this multi-tool cut.
        result[target]=result[target].cut(*channels)
        if i>=2:
            result[target]=result[target].cut(hex_nut(x,y,12,True))
    # Four identical clips share one exact BREP template. OCC's absolute-
    # coordinate boolean tolerance produces a sliver at the first location;
    # translating the same valid template avoids a different manufactured part.
    px,py,pz=PARAMS["capacitors"][3]
    prototype=result["capacitor_holder_4"]
    assert prototype.isValid() and len(prototype.Solids())==1
    for i,(x,y,z) in enumerate(PARAMS["capacitors"][2:],start=3):
        assert z==pz,"Standalone capacitor clips must share mounting height"
        result[f"capacitor_holder_{i}"]=prototype.translate((x-px,y-py,0))
    coil=box(18,80,3,(49,165,12))
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
    return result


def parts():
    result=[Part(n,s,1,"Positive perimeter stops, two ties; mounted independently to frame") for n,s in carrier_shapes().items()]
    result += [Part(n,s,1,"Removable non-load-bearing textile cover") for n,s in covers().items()]
    result += [Part("fit_coupon",fit_coupon(),1,"Printer/fastener/guide clearance coupon")]
    return result


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
        if name=="esp32":
            xx=x+w*(.25 if j==0 else .75)
            outer=box(2.5,d+2,h+2,(xx-1.25,y-1,z-1))
            inner=box(4,d+1,h+1,(xx-2,y-.5,z-.5))
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
    result=[Item(n,s,"fixed","mechanism") for n,s in carrier_shapes().items()]
    result += [i for i in hardware() if with_bank or i.name!="powerbank"]
    for n in COMPONENTS:
        if with_bank or n!="powerbank":
            result += ties(n)
    excluded=set(LEFT_STACK+RIGHT_STACK+("camera","microphone"))
    mounts=[(f"{name}_{j+1}",xx,yy) for name in COMPONENTS if name not in excluded
            for j,(xx,yy) in enumerate(mount_positions(name))]
    mounts += [(f"left_service_{j+1}",xx,yy) for j,(xx,yy) in enumerate(( (xx,yy) for xx in (22,64) for yy in (68,92)))]
    mounts += [(f"right_service_{j+1}",xx,yy) for j,(xx,yy) in enumerate(( (xx,yy) for xx in (365,410) for yy in (160,247)))]
    mounts += [(f"switch_shelf_{j+1}",xx,yy) for j,(xx,yy) in enumerate(((20,27),(22,60)))]
    mounts += [(f"coil_cradle_{j+1}",xx,yy) for j,(xx,yy) in enumerate(((54,170),(62,240)))]
    mounts += [(f"capacitor_holder_{j+3}",xx,yy) for j,(xx,yy,zz) in enumerate(PARAMS["capacitors"][2:])]
    for name,x,y in mounts:
        result += [Item(f"carrier_shaft_{name}",cylinder(1.5,8,(x,y,8)),"fastener","metal","M3x8, fit carrier before purchased item"),
                   Item(f"carrier_head_{name}",cylinder(2.85,1.65,(x,y,6.35)),"hardware","metal","M3 ISO7380; underside head clears 5 mm brace"),
                   Item(f"carrier_nut_{name}",hex_nut(x,y,12),"hardware","metal","Captive M3 hex nut in 6.7 mm corner pocket")]
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
        result.append(Item(f"moving_loop_{i+1}",box(24,26,24,(m["origin_x"]+i*m["pitch"]+17,m["origin_y"]+52,36)),"cable","black","Finite flex bundle reserve; physical trial pending"))
    return result
