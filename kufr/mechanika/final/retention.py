"""Existing case references and removable transport restraints, no new case holes."""
from geometry import PARAMS, Item,box,fuse,cylinder
from chassis import X,Y,C,HINGE,lid_position,case_floor_space
from mechanism import M,cam_law


def hardware(lid_angle=105,transport=False):
    lining=box(X-16,Y-16,60,(8,8,0)).intersect(case_floor_space(6)).cut(case_floor_space(6.8))
    result=[] if transport else [Item("lid_lining",lid_position(lining,lid_angle),"lid_fabric","cover","Removable non-load-bearing textile follows the reference curved lid; no electronics in lid")]
    if transport:
        top=M["origin_z"]+M["cam_z"]+cam_law(0)[0]+M["follower_drop"]+3
        for i in range(M["count"]):
            x=M["origin_x"]+i*M["pitch"]
            y=M["origin_y"]+60
            # The strap passes through the open base/tray slots. It is routed
            # outside the structural solids, carries inversion with no servo load.
            strap=fuse([box(36.8,8,1,(x+4.2,y,top)),box(.5,8,top-16,(x+4.2,y,16)),
                        box(.5,8,top-16,(x+40.5,y,16)),box(36.8,8,1,(x+4.2,y,16))])
            result.append(Item(f"bed_transport_tie_{i+1}",strap,"restraint","red","8 mm reusable wrap through base and empty tray; remove before operation"))
    return result
