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


def mating_pair(a,b):
    """Only documented screw/head intersections are intentional geometry."""
    names={a.name,b.name}
    for prefix in ("anchor_", "module_"):
        for name in names:
            if name.startswith(prefix+"m"):
                suffix=name.split("_",2)[2]
                return prefix+"head_"+suffix in names
    return False


def collisions(items):
    result=[]
    cached={i.name:bounds(i.shape) for i in items}
    for a,b in itertools.combinations(items,2):
        if mating_pair(a,b):
            continue
        if not overlaps(cached[a.name],cached[b.name]):
            continue
        try:
            volume=a.shape.intersect(b.shape).Volume()
        except ValueError as error:
            raise ValueError(f'BREP intersection failed: {a.name} / {b.name}') from error
        if volume>0.03:
            result.append([a.name,b.name,round(volume,4)])
    return result


def scene(angle=0,lid=105,covers=True,transport=False):
    result=chassis.items(lid)+layout.items(covers,not transport)+retention.hardware(lid,transport)
    for i in range(mechanism.M["count"]):
        result+=mechanism.module(i,angle if i==2 else 0,not transport)
    return result


if __name__ == "__main__":
    result={"rest":collisions(scene()),"raised":collisions(scene(160)),"closed_transport":collisions(scene(lid=0,transport=True))}
    (ROOT/"fit-debug.json").write_text(json.dumps(result,indent=2),encoding="utf-8")
    print(json.dumps(result,indent=2))
