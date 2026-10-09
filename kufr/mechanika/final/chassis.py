"""New six-tile shell and six-tile empty lid with bolted half-lap seams."""
from functools import lru_cache

from geometry import PARAMS, Part, Item, box, cylinder, fuse, cut, hole_z
from mechanism import MOUNT_HOLES, M
import cadquery as cq

C = PARAMS["case"]
X = C["inside_x"] + 2*C["wall"]
Y = C["inside_y"] + 2*C["wall"]
HINGE = (0, Y+3, C["base_height"]+C["lid_gap"]/2)


def seam_floor_holes():
    return [(x,Yp) for x in (X/3,2*X/3) for Yp in (24,80,190,250)] + [(xp,Y/2) for xp in (30,100,220,340,410)]


def rack_holes():
    return [(x,y) for x in (13,65,374,426) for y in (14,123,147,259)]


def module_holes():
    return [(M["origin_x"]+i*M["pitch"]+x,M["origin_y"]+y)
            for i in range(6) for x,y in MOUNT_HOLES]


def ring(outer_x, outer_y, thickness, height, z):
    return box(outer_x,outer_y,height,(0,0,z)).cut(box(outer_x-2*thickness,outer_y-2*thickness,height+2,(thickness,thickness,z-1)))


@lru_cache
def shell(lid=False):
    h = C["lid_height"] if lid else C["base_height"]
    result = fuse([box(X,Y,4), ring(X,Y,4,h-4,4)])
    holes = []
    for x,y in seam_floor_holes():
        holes.append(hole_z(x,y,4.5,z=-1,height=7))
        holes.append(cq.Solid.makeCone(4.5,2.25,2.25,cq.Vector(x,y,0)))
    # Wall half-laps have two bolts along their height, independent of floor seams.
    wall_z = (10,h-8)
    for x in (X/3,2*X/3):
        for y in (-1,Y-5):
            for z in wall_z:
                holes.append(cylinder(2.25,6,(x,y,z),(0,1,0)))
    for y in (Y/2,):
        for x in (-1,X-5):
            for z in wall_z:
                holes.append(cylinder(2.25,6,(x,y,z),(1,0,0)))
    # Standard steel hinge mounting: slots accept the actual leaf-hole pattern.
    hinge_z = 20.5 if lid else 108.0
    for x in (70,220,370):
        result = result.fuse(box(48,4,22,(x-24,Y-8,hinge_z-10)))
        for dx in (-12,12):
            holes.append(box(4.5,12,7,(x+dx-2.25,Y-10,hinge_z-3.5)))
            # Bury the inside button head; covers need not be cut around hinges.
            holes.append(box(9,3,11,(x+dx-4.5,Y-8,hinge_z-5.5)))
    if not lid:
        holes += [cylinder(8,6,(399.5,-1,89),(0,1,0)),cylinder(2.5,6,(301.25,-1,83.35),(0,1,0))]
        for x,y in rack_holes()+module_holes():
            result = result.fuse(cylinder(5,5,(x,y,4)))
            holes.append(hole_z(x,y,3.4,z=-1,height=12))
            holes.append(cq.Solid.makeCone(3.3,1.7,1.6,cq.Vector(x,y,0)))
        # Side-facing speakers have slots whose short 4 mm bridges print unaided.
        for xx in (-1,X-5):
            for y in range(22,108,8):
                holes.append(box(6,4,36,(xx,y,24)))
        # Unlined thermal vents beside the converter, high and low.
        for y in range(151,260,9):
            for z in (39,88):
                holes.append(box(6,4,16,(X-5,y,z)))
        # Header cover ledges, 45 degree lower faces, not a closed electronics roof.
        for y,sign in ((4,1),(Y-4,-1)):
            pts = [(y,97),(y,105),(y+sign*8,105)]
            ledge = cq.Workplane("YZ",origin=(75,0,0)).polyline(pts).close().extrude(293).val()
            result = result.fuse(ledge)
    # Limit-strap holes and two latch mounting pairs. Positions are design choices.
    for xx in (-1,X-5):
        for z in ((18,) if lid else (106,)):
            holes.append(box(6,16,3,(xx,190,z)))
    for x in (100,340):
        for dx in (-8,8):
            holes.append(cylinder(1.7,6,(x+dx,-1,18 if lid else 105),(0,1,0)))
    return cut(result,holes)


def tile_bounds(index, limit, count, shift):
    gap=C["joint_clearance"]/2
    start=0 if index==0 else limit*index/count+shift+gap
    end=limit if index==count-1 else limit*(index+1)/count+shift-gap
    return start,end


def mask_tile(ix,iy,shift,z,h):
    x0,x1=tile_bounds(ix,X,3,shift)
    y0,y1=tile_bounds(iy,Y,2,shift)
    return box(x1-x0,y1-y0,h,(x0,y0,z))


@lru_cache
def tiles(lid=False):
    full=shell(lid)
    h=C["lid_height"] if lid else C["base_height"]
    outside=ring(X,Y,2,h-4,4)
    inner=ring(X,Y,4,h-4,4).cut(outside)
    wall=outside.fuse(inner)
    extras=full.cut(box(X,Y,4)).cut(wall)
    result=[]
    for iy in range(2):
        for ix in range(3):
            # Both floor and walls overlap by 12 mm; 0.25 mm lateral assembly gap.
            chunks=[full.intersect(mask_tile(ix,iy,-6,0,2)),
                    full.intersect(mask_tile(ix,iy,6,2,2)),
                    full.intersect(outside).intersect(mask_tile(ix,iy,-6,4,h-4)),
                    full.intersect(inner).intersect(mask_tile(ix,iy,6,4,h-4)),
                    extras.intersect(mask_tile(ix,iy,0,4,h-4))]
            chunks=[s for s in chunks if s.Volume()>1e-5]
            result.append(fuse(chunks))
    return result


def lid_position(shape,angle=0):
    # Print-side floor becomes the upper outside surface of the installed lid.
    result=shape.rotate((0,0,0),(0,1,0),180).translate((X,0,C["base_height"]+C["lid_gap"]+C["lid_height"]))
    return result.rotate(HINGE,(1,HINGE[1],HINGE[2]),-angle)


def parts():
    return [Part(f"{'lid' if lid else 'body'}_{i+1}",shape,1,
                 "Half-lap tile within 220 mm bed; bolted seam, no snap fits")
            for lid in (False,True) for i,shape in enumerate(tiles(lid))]


def items(angle=105):
    result=[Item(f"body_{i+1}",s,"case","shell") for i,s in enumerate(tiles(False))]
    result += [Item(f"lid_{i+1}",lid_position(s,angle),"lid","shell") for i,s in enumerate(tiles(True))]
    for x in (70,220,370):
        result.append(Item(f"hinge_pin_{x}",cylinder(2.5,40,(x-20,Y+3,HINGE[2]),(1,0,0)),"hardware","metal","generic steel butt hinge; final leaf-hole pattern must be matched"))
    return result
