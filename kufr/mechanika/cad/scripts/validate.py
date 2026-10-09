"""Independent checks of placed BREP shapes and the actual binary STL exports."""
from collections import Counter
import itertools
import math
from pathlib import Path
import struct

from common import bounds


def pairs(items, allowed=()):
    exceptions = {frozenset(pair) for pair in allowed}
    records = [(i, bounds(i.shape)) for i in items if i.kind != "space"]
    collisions, checked, accepted = [], 0, []
    for (a, aa), (b, bb) in itertools.combinations(records, 2):
        if any(min(aa[k + 3], bb[k + 3]) - max(aa[k], bb[k]) <= 0.001 for k in range(3)):
            continue
        checked += 1
        volume = a.shape.intersect(b.shape).Volume()
        if volume <= .1:
            continue
        entry = [a.name, b.name, round(volume, 4)]
        (accepted if frozenset([a.name, b.name]) in exceptions else collisions).append(entry)
    return dict(checked_intersections=checked, collisions=collisions, intended_overlaps=accepted)


def stl(path, shape):
    raw = path.read_bytes()
    if len(raw) < 84:
        raise ValueError(f"Truncated STL: {path}")
    count = struct.unpack_from("<I", raw, 80)[0]
    if len(raw) != 84 + count * 50 or not count:
        raise ValueError(f"Empty or invalid STL: {path}")
    edges, directed, vertices = Counter(), Counter(), []
    volume = 0
    for index in range(count):
        row = struct.unpack_from("<12fH", raw, 84 + 50 * index)
        triangle = [tuple(round(v, 4) for v in row[k:k + 3]) for k in [3, 6, 9]]
        if any(not math.isfinite(v) for vertex in triangle for v in vertex) or len(set(triangle)) != 3:
            raise ValueError(f"Nonfinite/degenerate STL triangle: {path.name}")
        a, b, c = triangle
        volume += (a[0] * (b[1] * c[2] - b[2] * c[1]) + a[1] * (b[2] * c[0] - b[0] * c[2]) + a[2] * (b[0] * c[1] - b[1] * c[0])) / 6
        vertices.extend(triangle)
        for i, j in [(0, 1), (1, 2), (2, 0)]:
            edges[tuple(sorted([triangle[i], triangle[j]]))] += 1
            directed[(triangle[i], triangle[j])] += 1
    if any(value != 2 for value in edges.values()):
        raise ValueError(f"Nonmanifold/open STL edges: {path.name}")
    if any(directed[(b, a)] != value for (a, b), value in directed.items()):
        raise ValueError(f"Inconsistent STL winding: {path.name}")
    exact_volume = shape.Volume()
    relative = abs(volume - exact_volume) / exact_volume
    if volume <= 0 or relative > .02:
        raise ValueError(f"STL/CAD volume mismatch {path.name}: {relative}")
    bb = [min(v[i] for v in vertices) for i in range(3)] + [max(v[i] for v in vertices) for i in range(3)]
    exact = bounds(shape)
    error = max(abs(a - b) for a, b in zip(bb, exact))
    if error > .15 or min(bb[:3]) < -.001:
        raise ValueError(f"STL position/bounds mismatch: {path.name} {bb}")
    return dict(triangles=count, manifold_edges=True, consistent_winding=True,
                signed_volume_mm3=round(volume, 3), volume_relative_error=relative,
                bounds_mm=bb, max_cad_bound_error_mm=error)


def containment(items, p):
    c = p["case"]
    limit = [c["inside_width"], c["inside_depth"], c["inside_height"]]
    failed = []
    for item in items:
        if item.kind == "space":
            continue
        b = bounds(item.shape)
        if any(b[k] < -.001 or b[k + 3] > limit[k] + .001 for k in range(3)):
            failed.append(dict(name=item.name, bounds_mm=b))
    return failed
