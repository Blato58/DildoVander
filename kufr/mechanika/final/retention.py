"""Non-printed restraint geometry. Purchased ratings remain procurement checks."""
import math
from geometry import Item,box,fuse,cylinder
from chassis import X,Y,C,HINGE,lid_position
from mechanism import M,cam_law


def hardware(lid_angle=105,transport=False):
    result=[]
    for x in (70,220,370):
        pin_clearance=cylinder(2.7,42,(x-21,Y+3,HINGE[2]),(1,0,0))
        result.append(Item(f"hinge_lower_{x}",box(40,2,16,(x-20,Y,100.8)).cut(pin_clearance),"hardware","metal","40 mm steel hinge leaf envelope; choose hole pattern within slots"))
        upper=box(40,2,16,(x-20,Y,120.8)).cut(pin_clearance).rotate(HINGE,(1,HINGE[1],HINGE[2]),-lid_angle)
        result.append(Item(f"hinge_upper_{x}",upper,"lid_hardware","metal","steel hinge allowance"))
    # Keepers and draw latches are reference envelopes; catalog hardware to match.
    for x in (100,340):
        result.append(Item(f"latch_{x}",box(24,2,22,(x-12,-2,94)),"hardware","metal","standard metal draw latch allowance"))
        keeper=box(24,2,10,(x-12,-2,126)).rotate(HINGE,(1,HINGE[1],HINGE[2]),-lid_angle)
        result.append(Item(f"keeper_{x}",keeper,"lid_hardware","metal","standard keeper allowance"))
    result.append(Item("lid_lining",lid_position(box(X-16,Y-16,0.8,(8,8,9)),lid_angle),"lid_fabric","cover","removable tensioned velvet on 5 mm perimeter hook-and-loop pads; no electronics in lid"))
    if lid_angle==105:
        # Taut design length at 105 degrees; folded slack in the closed case is not
        # misrepresented as a rigid collision-checked textile simulation.
        for xx in (5.5,X-5.5):
            p=(xx,198,107.5)
            a=math.radians(-105)
            dy,dz=198-HINGE[1],131.3-HINGE[2]
            q=(xx,HINGE[1]+dy*math.cos(a)-dz*math.sin(a),HINGE[2]+dy*math.sin(a)+dz*math.cos(a))
            length=math.hypot(q[1]-p[1],q[2]-p[2])
            slope=math.degrees(math.atan2(q[2]-p[2],q[1]-p[1]))
            strap=box(1.2,length,8,(-0.6,0,-4)).rotate((0,0,0),(1,0,0),slope).translate(p)
            result.append(Item(f"lid_limit_{xx}",strap,"flexible_reference","black","approx 141 mm eye-to-eye strap; adjust both sides to 105 degrees, store slack by side walls"))
    if transport:
        height=C["base_height"]+C["lid_gap"]+C["lid_height"]
        for x in (130,310):
            strap=box(25,Y+8,height+4,(x-12.5,-4,-2)).cut(box(27,Y+4,height,(x-13.5,-2,0)))
            result.append(Item(f"carry_belt_{x}",strap,"restraint","black","25 mm closed webbing harness; body supported from below"))
        grip=fuse([box(180,25,3,(130,125,177)),box(3,25,28,(130,125,height+2)),box(3,25,28,(307,125,height+2))])
        result.append(Item("carry_handle",grip,"restraint","black","rated textile bridge handle joining both belts; no printed handle load path"))
        top=M["origin_z"]+M["cam_z"]+cam_law(0)[0]+M["follower_drop"]+3
        for i in range(6):
            x=M["origin_x"]+i*M["pitch"]
            y=M["origin_y"]+60
            strap=fuse([box(38,8,1,(x+4,y,top)),box(1,8,top-8,(x+4,y,8)),box(1,8,top-8,(x+41,y,8)),box(38,8,1,(x+4,y,8))])
            result.append(Item(f"bed_transport_tie_{i+1}",strap,"restraint","black","8 mm reusable tie through the bed and base slots; only when wands removed"))
    return result
