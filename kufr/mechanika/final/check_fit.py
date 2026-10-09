"""Actual BREP intersections; failure reports include body names and mm3."""
import itertools
import json
from pathlib import Path

from geometry import bounds, ROOT
import chassis
import mechanism
import layout
import retention


def overlaps(a,b):
    return all(min(a[i+3],b[i+3])-max(a[i],b[i])>1e-5 for i in range(3))


def collisions(items):
    result=[]
    cached={i.name:bounds(i.shape) for i in items}
    for a,b in itertools.combinations(items,2):
        if a.group=="fastener" or b.group=="fastener":
            continue  # Solid screw shanks intentionally occupy their mating hardware.
        if not overlaps(cached[a.name],cached[b.name]):
            continue
        volume=a.shape.intersect(b.shape).Volume()
        if volume>0.03:
            result.append([a.name,b.name,round(volume,4)])
    return result


def scene(angle=0,lid=105,covers=True,transport=False):
    result=chassis.items(lid)+layout.items(covers,not transport)+retention.hardware(lid,transport)
    for i in range(6):
        result+=mechanism.module(i,angle if i==2 else 0,not transport)
    return result


if __name__ == "__main__":
    result={"rest":collisions(scene()),"raised":collisions(scene(160)),"closed_transport":collisions(scene(lid=0,transport=True))}
    (ROOT/"fit-debug.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
    print(json.dumps(result,indent=2))
