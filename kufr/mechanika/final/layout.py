"""Purchased envelopes, connector reservations and service access, all original F1."""
from functools import lru_cache

from geometry import PARAMS, Part, Item, box, cylinder, cut, fuse, hole_z, strap_slots
from chassis import X,Y

RACKS = {"left_front": (7,7,64,126), "left_back": (7,140,64,128),
         "right_front": (368,7,64,126), "right_back": (368,140,64,128)}
Z = PARAMS["mounting"]["cover_z"]


@lru_cache
def rack(name):
    x,y,w,d=RACKS[name]
    s=box(w,d,3,(x,y,9))
    holes=[]
    for px in (x+6,x+w-6):
        for py in (y+7,y+d-10 if name.endswith("front") else y+d-9):
            s=s.fuse(box(6,6,93,(px-3,py-3,12)))
            s=s.fuse(cylinder(5,9,(px,py,9)))
            # M3 insert from below. Its 4.6 mm OD is surrounded by a 10 mm root.
            holes.extend([hole_z(px,py,4.2,z=8,height=6.2)])
    if name == "left_front":
        for px in (13,65):
            s=s.fuse(box(6,6,90.5,(px-3,53,12)))
        # The forward control shelf rests on shorter shoulders.
        s=s.cut(box(64,13,3,(7,7,102.5)))
        # Front lower stop of the vertical bank cradle.
        s=s.fuse(box(38,3,12,(14,120,12)))
        for px in (13,65):
            for py in (14,56):
                s=s.fuse(cylinder(4,8,(px,py,94.5)))
                holes.append(hole_z(px,py,4.2,z=97.3,height=6))
    if name == "left_back":
        s=s.fuse(box(3,123,18,(15,140,12)),box(3,123,18,(45.5,140,12)),box(33.5,3,18,(15,264,12)))
        for py in (160,239):
            holes+=strap_slots(32,py,38,width=18,z=8,height=7)
    # Universal tie slots back up the adhesive on light electronics/speakers.
    for py in (y+25,y+d-27):
        for px in (x+15,x+w-15):
            holes.append(box(3,14,6,(px-1.5,py-7,8)))
    table_feet={"left_front": [(px,py) for px in (12,66) for py in (83,103)],
                "right_front": [(px,py) for px in (374,429) for py in (32,115)],
                "right_back": [(px,py) for px in (374,429) for py in (158,250)]}
    holes += [hole_z(px,py,3.4,z=8,height=6) for px,py in table_feet.get(name,[])]
    return cut(s,holes)


def platform(x,y,w,d,z,height):
    # Printed upside down: roof first, then four vertical legs, no supported ceiling.
    s=box(w,d,3,(x,y,z))
    for xx in (x,x+w-6):
        for yy in (y+15,y+d-21):
            s=s.fuse(box(6,6,height,(xx,yy,z-height)))
            s=s.cut(hole_z(xx+3,yy+3,3.4,z=z-height-1,height=height+5))
    holes=[]
    for yy in (y+15,y+d-15):
        for xx in (x+9,x+w-9):
            holes.append(box(2.5,12,5,(xx-1.25,yy-6,z-1)))
    return cut(s,holes)


@lru_cache
def service_platforms():
    shapes={"left_service_table": platform(9,65,60,56,72,60),
            "right_front_table": platform(371,14,61,119,68,56),
            "right_back_table": platform(371,140,61,128,68,56)}
    notches=[]
    for name,(x,y,w,d) in RACKS.items():
        for px in (x+6,x+w-6):
            for py in (y+7,y+d-10 if name.endswith("front") else y+d-9):
                notches.append(box(7.2,7.2,110,(px-3.6,py-3.6,11.9)))
    shapes={name:cut(shape,notches) for name,shape in shapes.items()}
    shapes["right_back_table"]=shapes["right_back_table"].cut(box(6,10,5,(371,164,67))).clean()
    shapes["right_front_table"]=cut(shapes["right_front_table"],[hole_z(x,20,3.4,z=67,height=6) for x in (389,410)])
    return shapes


@lru_cache
def switch_shelf():
    s=box(60,52,2.5,(9,8,102.5))
    holes=[hole_z(22,40.5,19.4),hole_z(56,40.5,19.4),hole_z(38,19,18)]
    holes += [hole_z(x,y,3.4) for x in (13,65) for y in (14,56)]
    import cadquery as cq
    for x in (13,65):
        for y in (14,56):
            holes.append(cq.Solid.makeCone(3.3,1.7,1.6,cq.Vector(x,y,105),cq.Vector(0,0,-1)))
    return cut(s,holes)


@lru_cache
def covers():
    control=box(64,53,3,(7,7,Z))
    control=cut(control,[hole_z(38,19,12.4),box(28,27,5,(8,30.5,Z-1)),box(28,27,5,(42,30.5,Z-1))])
    # Loose opaque fabric membranes span the openings, 3 mm above the rockers.
    # No rigid plunger rests on a rocker; both ON and OFF halves remain pressable.
    bank=box(64,201,3,(7,63,Z))
    right_front=box(64,126,3,(368,7,Z))
    right_back=box(64,124,3,(368,140,Z))
    # Converter exhaust is deliberately not covered with velvet.
    right_back=cut(right_back,[box(3,50,5,(xx,151,Z-1)) for xx in (378,386,394,402,410,418)])
    # Integral inner skirts prevent a seated visitor seeing under the high lids.
    control=control.fuse(box(2,53,90,(69,7,18))).clean()
    bank=bank.fuse(box(2,201,90,(69,63,18))).clean()
    right_front=right_front.fuse(box(2,126,90,(368,7,18))).clean()
    right_front=right_front.cut(box(4,16,24,(367,20,22))).clean()
    right_back=right_back.fuse(box(2,124,90,(368,140,18))).clean()
    right_back=right_back.cut(box(4,18,27,(367,205,18))).clean()
    # Each front/rear header is integral to its adjacent side cover. The biggest
    # cover is 214 mm wide, leaving exactly 3 mm brim on a 220 mm bed.
    control=control.fuse(header().translate((75,9,Z)),box(4.1,18,3,(70.9,9,Z))).clean()
    right_front=right_front.fuse(header().translate((222,9,Z))).clean()
    rear=header().rotate((0,0,0),(0,0,1),180).translate((146,18,0))
    bank=bank.fuse(rear.translate((75,247.7,Z)),box(4.1,18,3,(70.9,247.7,Z))).clean()
    right_back=right_back.fuse(rear.translate((222,247.7,Z))).clean()
    return {"control_cover":control,"bank_cover":bank,"right_front_cover":right_front,"right_back_cover":right_back}


def header():
    return box(146,18,3).fuse(box(146,2,63,(0,16,-63))).clean()


def camera_bracket():
    # Unmeasured camera: universal pad and slots, not a fabricated hole pattern.
    s=fuse([box(31,14,3),box(31,3,29,(0,0,3))])
    return cut(s,[box(3,5,15,(x,-1,10)) for x in (5,23)]+[cylinder(7,5,(15.5,-1,18),(0,1,0)),hole_z(5,9,3.4),hole_z(26,9,3.4)])


def fit_coupon():
    s=box(85,58,4)
    holes=[hole_z(x,12,d,z=-1,height=6) for x,d in zip((10,25,41,61),(3.4,4.5,12.4,19.4))]
    # A short matching guide tests the actual printer's sliding fit.
    s=s.fuse(box(8,10,22,(5,34,4)))
    for x,c in ((25,0.3),(44,0.45),(64,0.6)):
        holes.append(box(8+2*c,10+2*c,6,(x,33,-1)))
    return cut(s,holes)


def parts():
    p=[Part(f"rack_{name}",rack(name),1,"Flat hook-and-loop base with integral cover supports and tie slots") for name in RACKS]
    p += [Part(name,s,1,"Removable platform above lower components",((1,0,0),180)) for name,s in service_platforms().items()]
    p += [Part("switch_shelf",switch_shelf(),1,"Real rocker mount below removable fabric membranes")]
    p += [Part(name,s,1,"Fabric cover and opaque inner skirt; print face down",((1,0,0),180)) for name,s in covers().items()]
    p += [Part("camera_angle",camera_bracket(),1,"Replaceable slotted camera pad next to ESP; final camera fit pending"),
          Part("fit_coupon",fit_coupon(),1,"Check sliding, screw, PTT and rocker clearances before full printing")]
    return p


def switch(name,center,z,rocker=True):
    x,y=center
    r,h,face= (9.5,27,24) if rocker else (6,32,14)
    shape=cylinder(r,h,(x,y,z)).fuse(box(face,face,3,(x-face/2,y-face/2,z+h)))
    return Item(name,shape,"hardware","black" if rocker else "white","purchased panel switch; nominal barrel and face envelope")


def hardware():
    rows=[
        ("esp32",(28,57,13),(396.5,31,73),"57 x 28 x 13; USB at rear, camera FPC at front"),
        ("pca9685",(70,25,15),(174,8,6),"owner 70 x 25 x 15"),
        ("mcp23017",(43,18,5),(118,10,12),"43 x 18 x 5; 6 mm insulating pad plus 2 mm hook-and-loop clears seam nuts"),
        ("pixel_shifter",(25.5,15,10.2),(262,10,6),"25.5 x 15 x 10.2"),
        ("speaker_left",(21,100,45),(18,19,18),"100 x 45 x 21, acoustic face towards -X"),
        ("speaker_right",(21,100,45),(403,19,18),"100 x 45 x 21, acoustic face towards +X"),
        ("amplifier_left",(18,18,8),(42,102,77),"18 x 18, provisional height 8"),
        ("amplifier_right",(18,18,8),(380,111,73),"18 x 18, provisional height 8"),
        ("breaker",(45,28,13),(17,70,77),"45 x 28 x 13; strapped to service table, reset accessible with bank cover removed"),
        ("buck",(31,46,19),(379,149,73),"31 x 46 x 19; uncovered above and side vents"),
        ("pd_trigger",(14,28,11),(383,146,17),"28 x 14 x 11; USB towards -Y, switch accessible below removable table"),
        ("perfboard_1",(50,70,1.6),(375,176,16),"50 x 70 footprint; 1.6 thickness assumption"),
        ("perfboard_2",(50,70,1.6),(375,176,64.4),"50 x 70 footprint; 1.6 thickness assumption; underside of removable table"),
        ("camera",(24,12,24),(387,16,77),"provisional 24 x 24 x 12; exact camera and FPC unknown"),
        ("microphone",(16.51,5,12.7),(293,6,77),"16.51 x 12.7 footprint, 5 mm thickness allowance; hook-and-loop on inside front wall"),
        ("powerbank",(26.5,139,80),(18.5,123,18),"owner 139 x 80 x 26.5; ports at -Y; direct upward removal"),
        ("spare_pca",(70,25,15),(89,183,19),"second purchased PCA; disconnected and bagged across bases 1-2, clear of screw heads"),
        ("spare_red_rocker",(24,24,30),(130,80,15),"second red rocker; disconnected, tied under bed 2"),
    ]
    result=[Item(n,box(*size,origin),"hardware","black" if n.startswith(("speaker","powerbank")) else "pcb",src) for n,size,origin,src in rows]
    for i,x in enumerate((178,226,274)):
        result.append(Item(f"degson_{i+1}",box(35,22,16,(x,184,15)),"hardware","white","provisional 35 x 22 x 16 adjustable reserve; three separate electrical nets"))
    for i,(x,y,z) in enumerate(((45,111,52),(58,111,52),(385,186,19),(398,186,19),(385,218,46.4),(398,218,46.4))):
        result.append(Item(f"capacitor_{i+1}",cylinder(5,17,(x,y,z)),"hardware","black","470 uF diameter 10 height 17"))
    result += [switch("red_main",(22,40.5),78),switch("green_drives",(56,40.5),78),switch("white_ptt",(38,19),80,False)]
    for i in range(7):
        lane=i if i<6 else 5
        yy=49 if i<6 else 184
        result.append(Item(f"spare_degson_{i+1}",box(35,22,16,(82+48*lane,yy,15)),"hardware","white","unused purchased terminal, isolated and tied; same provisional envelope"))
    for i,(xx,yy) in enumerate(((88,83),(101,83),(88,96),(101,96))):
        result.append(Item(f"spare_capacitor_{i+1}",cylinder(5,17,(xx,yy,15)),"hardware","black","remaining four capacitors; isolated leads in pouch, tied under bed 1"))
    return result


def items(with_covers=True,with_bank=True):
    result=[Item(f"rack_{name}",rack(name),"fixed","mechanism") for name in RACKS]
    result += [Item(name,s,"fixed","mechanism") for name,s in service_platforms().items()]
    result.append(Item("switch_shelf",switch_shelf(),"fixed","mechanism"))
    result.append(Item("camera_angle",camera_bracket().translate((384,11,71)),"fixed","mechanism"))
    result+= [i for i in hardware() if with_bank or i.name!="powerbank"]
    if with_covers:
        result += [Item(name,s,"cover","cover") for name,s in covers().items()]
        # Membranes are textile reference geometry, not printable parts.
        for name,x in (("red_membrane",8),("green_membrane",42)):
            result.append(Item(name,box(28,27,0.8,(x,30.5,111)),"fabric","cover","loose opaque velvet; 3 mm empty gap above rocker"))
    return result


def reservations():
    return [
        Item("bank_removal",box(29,143,180,(17,121,18)),"access","white","bank and fingers travel upward; remove only bank cover and two straps"),
        Item("esp_usb",box(30,22,15,(395.5,88,73)),"access","white","22 mm straight plug and bend reserve"),
        Item("pd_usb",box(16,22,16,(382,124,17)),"access","white","USB-C plug and upward bend; exact plug envelope unmeasured"),
        Item("bank_usb",box(16,22,14,(24,101,80)),"access","white","orient C1 towards front/top; connector centre assumption requires fit check"),
        Item("camera_fpc",box(28,5,16,(386,27,74)),"access","white","adjacent pads; actual connector/length unknown"),
        Item("buck_air",box(35,50,14,(377,147,93)),"access","white","air volume below unlined grille"),
        Item("power_trunk",box(288,6,6,(82,208,35)),"cable","black","1.5 mm2 trunk reservation across fixed beds"),
        Item("front_bus",box(285,9,12,(80,22,32)),"cable","black","signals above boards, below front cover fascia"),
    ]
