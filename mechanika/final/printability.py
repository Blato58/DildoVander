"""Conservative FDM geometry screen of an already positioned CadQuery shape.

PASS is an analytical result, not a slicer, adhesion, strength or physical-print
acceptance. All dimensions are millimetres. No part-name exemptions are used.
"""
from __future__ import annotations

import math
from dataclasses import asdict, dataclass, replace

import numpy as np
from shapely import affinity, linestrings
from shapely.geometry import GeometryCollection, LineString, Polygon
from shapely.ops import polygonize_full, unary_union


@dataclass(frozen=True)
class Profile:
    layer_height_mm: float = 0.2
    max_overhang_degrees: float = 45.0
    max_bridge_mm: float = 8.0
    min_bridge_anchor_mm: float = 0.8
    min_wall_mm: float = 0.8
    tolerance_mm: float = 0.03
    bed_size_mm: tuple = (220.0, 220.0, 250.0)
    bridge_sample_mm: float = 0.2
    wall_sample_mm: float = 0.8
    max_issue_samples: int = 30


def _profile(value):
    if value is None:
        return Profile()
    if isinstance(value, Profile):
        result = value
    else:
        values = dict(value)
        if "layer" in values:
            values.setdefault("layer_height_mm", values.pop("layer"))
        if "bed_x" in values or "bed_y" in values:
            values.setdefault("bed_size_mm", (
                values.pop("bed_x", 220.0), values.pop("bed_y", 220.0),
                values.pop("bed_z", 250.0)))
        fields = Profile.__dataclass_fields__
        result = replace(Profile(), **{k: v for k, v in values.items() if k in fields})
    positive = (result.layer_height_mm, result.max_bridge_mm,
                result.min_bridge_anchor_mm, result.min_wall_mm,
                result.bridge_sample_mm, result.wall_sample_mm, *result.bed_size_mm)
    if any(not math.isfinite(v) or v <= 0 for v in positive):
        raise ValueError("Profile dimensions and sampling intervals must be positive")
    if not 0 <= result.tolerance_mm <= 0.03:
        raise ValueError("Numerical tolerance must be between 0 and 0.03 mm")
    if not 0 < result.max_overhang_degrees <= 45:
        raise ValueError("This screen supports overhang angles in (0, 45] degrees")
    if result.max_issue_samples < 1:
        raise ValueError("At least one issue sample is required")
    return result


def _polygons(geometry):
    if geometry.geom_type == "Polygon":
        return [geometry]
    if hasattr(geometry, "geoms"):
        return [part for item in geometry.geoms for part in _polygons(item)]
    return []


class _Mesh:
    def __init__(self, shape, tolerance):
        if hasattr(shape, "val"):
            shape = shape.val()
        solids = shape.Solids()
        if not solids or not shape.isValid():
            raise ValueError("Analysis requires valid closed CAD solids")
        triangles = []
        owners = []
        for owner, solid in enumerate(solids):
            vertices, faces = solid.tessellate(max(tolerance / 2, 0.001), 0.1)
            triangles.append(np.asarray([v.toTuple() for v in vertices])[np.asarray(faces)])
            owners.extend([owner] * len(faces))
        self.triangles = np.concatenate(triangles)
        self.owners = np.asarray(owners)
        self.solid_count = len(solids)
        self.low = self.triangles.min(axis=(0, 1))
        self.high = self.triangles.max(axis=(0, 1))
        self.volume = float(shape.Volume())

    def section(self, coordinate, axis=2):
        triangles = self.triangles
        heights = triangles[:, :, axis]
        # Match the edge crossing rule: include the lower endpoint, exclude the
        # upper endpoint. A strict lower bound drops complete contour edges at
        # a tessellation vertex and can falsely empty a supported layer.
        active = (heights.min(axis=1) <= coordinate) & (heights.max(axis=1) > coordinate)
        triangles = triangles[active]
        heights = heights[active]
        owners = self.owners[active]
        plane_axes = [i for i in range(3) if i != axis]
        # Identical triangle edges are rounded only by 1e-6 mm to join their
        # endpoints. The process tolerance is applied later to support tests.
        starts = triangles
        ends = triangles[:, [1, 2, 0], :]
        end_heights = heights[:, [1, 2, 0]]
        crossings = ((heights <= coordinate) & (coordinate < end_heights)
                     | (end_heights <= coordinate) & (coordinate < heights))
        # Interpolate a shared mesh edge in the same direction for both faces.
        # Opposite windings otherwise straddle a rounding tie and leave a
        # one-micrometre gap, which can turn a cavity into apparent material.
        reverse = heights > end_heights
        lower = np.where(reverse[:, :, None], ends, starts)
        upper = np.where(reverse[:, :, None], starts, ends)
        lower_heights = np.minimum(heights, end_heights)
        denominators = np.abs(end_heights - heights)
        fractions = np.divide(coordinate - lower_heights, denominators,
                              out=np.zeros_like(heights), where=denominators != 0)
        points = lower + fractions[:, :, None] * (upper - lower)
        segments = np.round(points[:, :, plane_axes][crossings], 6).reshape((-1, 2, 2))
        valid = np.any(segments[:, 0] != segments[:, 1], axis=1)
        segments = segments[valid]
        owners = owners[valid]
        if not len(segments):
            return GeometryCollection()
        # Polygonize also returns cavity interiors. XOR of unique exterior
        # rings preserves nesting within each solid. Union the solids afterward
        # so overlapping members of a compound never cancel material by parity.
        sections = []
        for owner in np.unique(owners):
            owned = segments[owners == owner].copy()
            # A section tangent to a curved pocket can collapse a tiny loop to
            # two identical, opposite edges. They enclose no material or void.
            # Cancel pairs only within the same solid; real open contours still
            # fail below, and overlapping solids remain independent.
            swap = (owned[:, 0, 0] > owned[:, 1, 0]) | (
                (owned[:, 0, 0] == owned[:, 1, 0]) & (owned[:, 0, 1] > owned[:, 1, 1]))
            owned[swap] = owned[swap, ::-1, :]
            unique, counts = np.unique(owned.reshape((-1, 4)), axis=0, return_counts=True)
            owned = unique[counts % 2 == 1].reshape((-1, 2, 2))
            polygons, cuts, dangles, invalid = polygonize_full(
                unary_union(linestrings(owned)))
            if cuts.length + dangles.length + invalid.length > 1e-5:
                raise ValueError(f"Incomplete mesh section at axis {axis}, coordinate {coordinate}")
            result = GeometryCollection()
            for cell in polygons.geoms:
                result = result.symmetric_difference(Polygon(cell.exterior))
            sections.append(result)
        return unary_union(sections)


def _intervals(geometry):
    if geometry.is_empty:
        return []
    if geometry.geom_type == "LineString":
        coordinates = list(geometry.coords)
        return [(min(p[0] for p in coordinates), max(p[0] for p in coordinates))]
    if hasattr(geometry, "geoms"):
        return sorted(pair for item in geometry.geoms for pair in _intervals(item))
    return []


def _bridge_span(region, current, previous, profile):
    """Measure a candidate only if every sampled chord has anchors at both ends."""
    hull = list(region.convex_hull.exterior.coords)
    angles = {0.0, 90.0}
    for a, b in zip(hull, hull[1:]):
        angles.add(round(math.degrees(math.atan2(b[1] - a[1], b[0] - a[0])) % 180, 4))
    coordinates = np.asarray(hull)

    def width(angle):
        radians = math.radians(angle)
        projected = coordinates @ np.asarray([math.cos(radians), math.sin(radians)])
        return float(np.ptp(projected))

    best = None
    # Try the shortest geometric width first. A valid direction below the limit
    # already proves acceptance; searching every nearly identical tangent of a
    # circular cap adds no evidence. Failed regions still retain the exhaustive
    # search so their reported best anchored candidate remains meaningful.
    for angle in sorted(angles, key=lambda candidate: (width(candidate), candidate)):
        gap = affinity.rotate(region, -angle, origin=(0, 0))
        material = affinity.rotate(current, -angle, origin=(0, 0))
        support = affinity.rotate(previous, -angle, origin=(0, 0))
        xmin, ymin, xmax, ymax = gap.bounds
        width = ymax - ymin
        count = max(3, math.ceil(width / profile.bridge_sample_mm) + 1)
        # Include the near-edge chords as well as evenly spaced interior ones.
        epsilon = min(1e-5, width / 100)
        largest = 0.0
        valid = True
        for y in np.linspace(ymin + epsilon, ymax - epsilon, count):
            line = LineString([(material.bounds[0] - 1, y), (material.bounds[2] + 1, y)])
            gaps = _intervals(gap.intersection(line))
            material_intervals = _intervals(material.intersection(line))
            supports = _intervals(support.intersection(line))
            for start, end in gaps:
                containing = next((pair for pair in material_intervals
                                   if pair[0] <= start + 1e-5 and pair[1] >= end - 1e-5), None)
                # Tangent chords can contain only a sub-tolerance facet tip.
                # There is no representable extrusion there. Every resolved
                # chord still needs both full anchors, and largest>0 below
                # prevents an entirely unresolved region receiving a pass.
                if containing is not None and containing[1]-containing[0] < profile.tolerance_mm:
                    continue
                if containing is None:
                    valid = False
                    break
                anchors = [(max(a, containing[0]), min(b, containing[1])) for a, b in supports]
                anchors = [(a, b) for a, b in anchors
                           if b - a >= profile.min_bridge_anchor_mm - profile.tolerance_mm]
                left = [b for a, b in anchors if b <= start + profile.tolerance_mm]
                right = [a for a, b in anchors if a >= end - profile.tolerance_mm]
                if not left or not right:
                    valid = False
                    break
                span = min(right) - max(left)
                largest = max(largest, span)
            if not valid:
                break
        if valid and largest > 0:
            if largest <= profile.max_bridge_mm + profile.tolerance_mm:
                return largest
            best = largest if best is None else min(best, largest)
    return best


def _thin_regions(section, profile):
    radius = (profile.min_wall_mm - profile.tolerance_mm) / 2
    opened = section.buffer(-radius, join_style="mitre").buffer(radius, join_style="mitre")
    removed = section.difference(opened.buffer(profile.tolerance_mm / 2))
    return [region for region in _polygons(removed)
            if region.area > profile.min_wall_mm ** 2 / 2]


def analyze(shape, profile=None):
    """Screen print-position geometry, returning a bounded JSON-safe report."""
    profile = _profile(profile)
    mesh = _Mesh(shape, profile.tolerance_mm)
    dimensions = mesh.high - mesh.low
    issues = []
    issue_count = 0
    unsupported_count = 0
    thin_count = 0
    floating_count = 0
    bridge_count = 0
    max_bridge = 0.0

    def issue(kind, coordinate, region=None, detail=None, axis=2):
        nonlocal issue_count
        issue_count += 1
        if len(issues) >= profile.max_issue_samples:
            return
        point = [float(v) for v in mesh.low]
        point[axis] = float(coordinate)
        sample = {"kind": kind, "coordinate_mm": point}
        if region is not None and not region.is_empty:
            centre = region.representative_point()
            for index, value in zip([i for i in range(3) if i != axis], centre.coords[0]):
                point[index] = round(float(value), 4)
            sample["area_mm2"] = round(float(region.area), 4)
        if detail is not None:
            sample["detail"] = detail
        issues.append(sample)

    if any(dimensions[i] > profile.bed_size_mm[i] + profile.tolerance_mm for i in range(3)):
        issue("build_volume", mesh.low[2], detail="Part exceeds configured build dimensions")
    if abs(mesh.low[2]) > profile.tolerance_mm:
        issue("bed_position", mesh.low[2], detail="Apply the intended print_position before analysis")

    epsilon = min(0.001, dimensions[2] / 100)
    bed = mesh.section(mesh.low[2] + epsilon)
    first = mesh.section(mesh.low[2] + min(profile.layer_height_mm / 2, dimensions[2] / 2))
    bottom_triangles = mesh.triangles[
        np.all(np.abs(mesh.triangles[:, :, 2] - mesh.low[2]) < 1e-6, axis=1)]
    edge_a = bottom_triangles[:, 1, :2] - bottom_triangles[:, 0, :2]
    edge_b = bottom_triangles[:, 2, :2] - bottom_triangles[:, 0, :2]
    bed_area = float(np.abs(edge_a[:, 0] * edge_b[:, 1]
                            - edge_a[:, 1] * edge_b[:, 0]).sum() / 2)
    if mesh.solid_count > 1:
        bed_area = float(unary_union([Polygon(t[:, :2]) for t in bottom_triangles]).area)
    # Flat faces at z=0 establish contact. Mid-layer expansion still follows the
    # angle rule, so a valid chamfer starting at that flat face remains printable.
    first_allowance = (profile.layer_height_mm / 2 * math.tan(
        math.radians(profile.max_overhang_degrees)) + profile.tolerance_mm)
    flat_contact = bed_area > 1e-6 and not first.is_empty and first.difference(
        bed.buffer(first_allowance)).area <= profile.tolerance_mm ** 2
    if bed.is_empty or not flat_contact:
        issue("bed_contact", mesh.low[2], first, "Base is not flat at the bed plane")

    count = max(1, math.ceil(dimensions[2] / profile.layer_height_mm - 1e-7))
    previous = GeometryCollection()
    thin_previous = GeometryCollection()
    thin_depth = 0.0
    lateral_allowance = profile.layer_height_mm * math.tan(
        math.radians(profile.max_overhang_degrees)) + profile.tolerance_mm
    for layer in range(count):
        z = mesh.low[2] + min((layer + 0.5) * profile.layer_height_mm,
                            dimensions[2] - epsilon)
        current = mesh.section(z)
        if layer > 0:
            for component in _polygons(current):
                if component.intersection(previous.buffer(profile.tolerance_mm)).is_empty:
                    floating_count += 1
                    issue("floating_start", z, component)
            unsupported = current.difference(previous.buffer(lateral_allowance))
            for region in _polygons(unsupported):
                if region.area <= profile.tolerance_mm ** 2:
                    continue
                span = _bridge_span(region, current, previous, profile)
                if span is not None:
                    max_bridge = max(max_bridge, span)
                if span is None or span > profile.max_bridge_mm + profile.tolerance_mm:
                    unsupported_count += 1
                    detail = ("No two-ended bridge anchors or permitted layer support"
                              if span is None else
                              f"Two-ended bridge span {span:.3f} mm exceeds configured maximum")
                    issue("unsupported_overhang", z, region, detail)
                else:
                    bridge_count += 1
        thin = unary_union(_thin_regions(current, profile))
        thin_depth = (thin_depth + profile.layer_height_mm
                      if not thin.is_empty and not thin.intersection(thin_previous).is_empty
                      else profile.layer_height_mm if not thin.is_empty else 0.0)
        if thin_depth + 1e-7 >= profile.min_wall_mm:
            thin_count += 1
            issue("thin_wall", z, thin, "Persistent XY wall narrower than minimum")
        thin_previous = thin
        previous = current

    # Orthogonal sections additionally detect thin horizontal membranes. This
    # is a sampled geometry screen, not a claim of an exact minimum-wall solver.
    for axis in (0, 1):
        previous_thin = GeometryCollection()
        persistent = 0.0
        samples = max(1, math.ceil(dimensions[axis] / profile.wall_sample_mm))
        for index in range(samples):
            coordinate = mesh.low[axis] + min((index + 0.5) * profile.wall_sample_mm,
                                             dimensions[axis] - epsilon)
            thin = unary_union(_thin_regions(mesh.section(coordinate, axis), profile))
            persistent = (persistent + profile.wall_sample_mm
                          if not thin.is_empty and not thin.intersection(previous_thin).is_empty
                          else 0.0)
            if persistent + 1e-7 >= profile.min_wall_mm:
                thin_count += 1
                issue("thin_wall", coordinate, thin,
                      "Persistent orthogonal membrane narrower than minimum", axis)
            previous_thin = thin

    return {
        "status": "PASS" if not issue_count else "FAIL_ANALYTICAL",
        "layer_count": count,
        "bed_contact_mm2": round(bed_area, 4),
        "flat_bed_contact": flat_contact,
        "dimensions_mm": [round(float(v), 4) for v in dimensions],
        "volume_mm3": round(mesh.volume, 4),
        "max_bridge_mm": round(max_bridge, 4),
        "bridge_region_count": bridge_count,
        "unsupported_count": unsupported_count,
        "floating_start_count": floating_count,
        "thin_wall_count": thin_count,
        "issue_count": issue_count,
        "issues": issues,
        "issues_truncated": issue_count > len(issues),
        "profile": asdict(profile),
        "evidence": "tessellated geometry analysis; not slicer or physical-print validation",
        "assumptions": [
            "Overhang angle is measured from vertical; nominal 0.2 mm layers.",
            "Bridges require material anchors at both ends of every sampled chord.",
            "Thin walls use morphological cross-sections and persistence; local defects below sampling may remain.",
            "Adhesion, material tuning, extrusion width, cooling and strength require slicer/print validation.",
        ],
    }
