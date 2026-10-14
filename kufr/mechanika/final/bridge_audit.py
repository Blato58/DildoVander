"""Audit nominal support beneath long Prusa bridge strokes, without warning waivers.

This is a G-code geometry check, not evidence of adhesion, strength or a successful
print. The caller must still fail Floating bridge anchors and unknown warnings.
Only linear, single-tool, millimetre deposition is supported. Extrusion mode is
independent of XYZ mode in the selected RepRap dialect.
"""
from bisect import bisect_left, insort
from dataclasses import dataclass, field
import math
import re

import numpy as np
import shapely
from shapely.geometry import LineString, Point


_MAX_SAMPLES = 40
_MAX_GAPS = 10
_WORD = re.compile(r"([A-Za-z])\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+)|[+-]?(?:nan|inf(?:inity)?))", re.I)
_KNOWN_TYPES = {
    "Perimeter", "External perimeter", "Internal infill", "Solid infill",
    "Top solid infill", "Bridge infill", "Overhang perimeter", "Gap fill", "Skirt/Brim",
    "Support material", "Support material interface", "Custom",
}


@dataclass
class _Findings:
    samples: list = field(default_factory=list)
    count: int = 0

    def add(self, kind, line, detail):
        self.count += 1
        if len(self.samples) < _MAX_SAMPLES:
            self.samples.append({"kind": kind, "line": line, "detail": detail})


@dataclass(frozen=True)
class _Stroke:
    line: int
    start: tuple
    end: tuple
    width: float
    previous_z: float | None = None
    previous_count: int = 0

    @property
    def length(self):
        return math.hypot(self.end[0]-self.start[0], self.end[1]-self.start[1])


def _words(code):
    """Reject leftovers and duplicate axes rather than partially parsing moves."""
    if "*" in code:
        code, checksum = code.rsplit("*", 1)
        if not checksum.strip().isdigit():
            raise ValueError("Invalid G-code checksum suffix")
    matches = list(_WORD.finditer(code))
    cursor = 0
    result = {}
    for match in matches:
        if code[cursor:match.start()].strip():
            raise ValueError("Unparsed G-code text")
        key, value = match.group(1).upper(), float(match.group(2))
        if key in result:
            raise ValueError(f"Duplicate G-code word {key}")
        if not math.isfinite(value):
            raise ValueError(f"Nonfinite G-code word {key}")
        result[key] = value
        cursor = match.end()
    if code[cursor:].strip():
        raise ValueError("Unparsed G-code text")
    return result


def _parse(text, max_gap, findings):
    position = {axis: None for axis in "XYZ"}
    offset = {axis: 0.0 for axis in "XYZ"}
    xyz_absolute = extrusion_absolute = None
    millimetres = False
    extrusion = None
    retraction_debt = 0.0
    path_type = None
    width = None
    layers = {}
    heights = []
    long_strokes = []
    bridge_count = 0
    max_bridge_length = 0.0

    for number, raw in enumerate(text.splitlines(), 1):
        code, _, comment = raw.partition(";")
        comment = comment.strip()
        if comment.startswith("TYPE:"):
            path_type = comment[5:].strip()
            if path_type not in _KNOWN_TYPES:
                findings.add("unclassified_type", number, f"Unsupported path type {path_type!r}")
        elif comment.startswith("WIDTH:"):
            try:
                width = float(comment[6:].strip())
                if not math.isfinite(width) or width <= 0:
                    raise ValueError()
            except ValueError:
                width = None
                findings.add("invalid_width", number, "WIDTH must be a finite positive millimetre value")
        elif comment.startswith("HEIGHT:"):
            try:
                height = float(comment[7:].strip())
                if not math.isfinite(height) or height <= 0:
                    raise ValueError()
            except ValueError:
                findings.add("invalid_height", number, "HEIGHT must be finite and positive")
        if "floating bridge" in comment.lower():
            findings.add("floating_warning", number, "Floating warning cannot be cleared by this audit")
        elif "warning" in comment.lower():
            if not re.fullmatch(r"(?:print )?warning:\s*long bridging extrusions\.?",comment,re.I):
                findings.add("unknown_warning", number, "Unknown warning cannot be cleared by this audit")
        if not code.strip():
            continue
        try:
            words = _words(code)
        except ValueError as error:
            findings.add("unparseable_gcode", number, str(error))
            continue
        commands = [key for key in ("G", "M", "T") if key in words]
        if len(commands) != 1:
            findings.add("unparseable_command", number, "Exactly one explicit command is required")
            continue
        command_type = commands[0]
        value = words[command_type]
        if not value.is_integer():
            findings.add("unsupported_command", number, "Fractional command number is unsupported")
            continue
        command = int(value)
        if command_type == "T":
            if command != 0:
                findings.add("unsupported_tool", number, "Only one extruder/tool T0 is supported")
            continue
        if command_type == "M":
            if command in (82, 83):
                if set(words)-{"M","N"}:
                    findings.add("unparseable_command",number,"Extrusion mode command has unexpected parameters")
                extrusion_absolute = command == 82
            elif any(axis in words for axis in "XYZE"):
                findings.add("unsupported_command", number, "Unmodelled M command with motion/extrusion words")
            continue
        if command in (90, 91):
            xyz_absolute = command == 90
            continue
        if command == 21:
            millimetres = True
            continue
        if command == 92:
            for axis in "XYZ":
                if axis in words:
                    if position[axis] is None:
                        position[axis] = words[axis]
                    else:
                        offset[axis] = position[axis]-words[axis]
            if "E" in words:
                extrusion = words["E"]
            # G92 changes the coordinate, not the unretracted filament debt.
            continue
        if command == 28:
            for axis in "XYZ":
                if not any(key in words for key in "XYZ") or axis in words:
                    position[axis] = None
                    offset[axis] = 0.0
            continue
        if command == 4:
            continue
        if command not in (0, 1):
            findings.add("unsupported_motion", number,
                         "Only G0/G1 paths are modelled; arcs, firmware retraction and other G commands fail")
            continue
        if set(words)-{"G","N","X","Y","Z","E","F"}:
            findings.add("unsupported_motion_words",number,"Unmodelled axis/parameter in linear move")
            continue

        start = tuple(position[axis] for axis in "XYZ")
        for axis in "XYZ":
            if axis in words:
                if xyz_absolute is None:
                    findings.add("missing_position_mode", number, "G90/G91 required before axis motion")
                elif xyz_absolute:
                    position[axis] = words[axis]+offset[axis]
                elif position[axis] is None:
                    findings.add("missing_position", number, "Relative move has no preceding axis position")
                else:
                    position[axis] += words[axis]
        end = tuple(position[axis] for axis in "XYZ")
        if any(value is not None and not math.isfinite(value) for value in end):
            findings.add("nonfinite_position",number,"Computed axis position is nonfinite")
            continue
        if "E" not in words:
            continue
        if extrusion_absolute is None:
            findings.add("missing_extrusion_mode", number, "M82/M83 required before extrusion")
            continue
        if extrusion_absolute and extrusion is None:
            findings.add("missing_extrusion_reference", number, "Absolute extrusion requires G92 or a known E position")
            extrusion = words["E"]
            continue
        delta = words["E"]-extrusion if extrusion_absolute else words["E"]
        if not math.isfinite(delta):
            findings.add("nonfinite_extrusion",number,"Computed extrusion delta is nonfinite")
            continue
        extrusion = words["E"] if extrusion_absolute else (extrusion or 0)+words["E"]
        if delta <= 0:
            retraction_debt -= delta
            if not math.isfinite(retraction_debt):
                findings.add("nonfinite_extrusion",number,"Computed retraction debt is nonfinite")
            continue
        restored = min(delta, retraction_debt)
        retraction_debt -= restored
        if delta-restored <= 1e-10:
            continue
        # Priming without XY motion is not a deposited line or bridge.
        if start[:2] == end[:2]:
            continue
        if not millimetres or None in start or None in end:
            findings.add("missing_position", number, "Deposition requires G21 and known XYZ endpoints")
            continue
        if abs(end[2]-start[2]) > 1e-6:
            findings.add("nonplanar_deposition", number, "Changing Z during deposition is unsupported")
            continue
        if path_type not in _KNOWN_TYPES or path_type == "Custom":
            findings.add("unclassified_deposition", number, "Deposition must have a classified TYPE annotation")
        if width is None:
            findings.add("missing_width", number, "Deposition requires a valid WIDTH annotation")
            continue
        fraction = restored/delta
        deposited_start = tuple(a+(b-a)*fraction for a,b in zip(start,end))
        z = round(end[2], 6)
        index = bisect_left(heights, z)
        previous_z = heights[index-1] if index else None
        stroke = _Stroke(number, deposited_start, end, width, previous_z,
                         len(layers[previous_z]) if previous_z is not None else 0)
        if not math.isfinite(stroke.length):
            findings.add("nonfinite_segment",number,"Computed segment length is nonfinite")
            continue
        if path_type == "Bridge infill":
            bridge_count += 1
            max_bridge_length = max(max_bridge_length, stroke.length)
            if stroke.length > max_gap:
                long_strokes.append(stroke)
        if z not in layers:
            layers[z] = []
            insort(heights, z)
        layers[z].append(stroke)
    return layers, long_strokes, bridge_count, max_bridge_length


def _support_geometry(strokes):
    """Union only requested previous layers; conservative inscribed round ends."""
    coordinates = np.asarray([[stroke.start[:2],stroke.end[:2]] for stroke in strokes])
    radii = np.asarray([stroke.width/2 for stroke in strokes])
    lines = shapely.linestrings(coordinates)
    return shapely.union_all(shapely.buffer(lines,radii,quad_segs=8))


def _line_components(geometry):
    if geometry.is_empty:
        return
    if geometry.geom_type == "LineString":
        yield geometry
    elif hasattr(geometry,"geoms"):
        for child in geometry.geoms:
            yield from _line_components(child)


def _audit_stroke(stroke, support, max_gap, tolerance):
    line = LineString([stroke.start[:2],stroke.end[:2]])
    gap_pieces = list(_line_components(line.difference(support)))
    intervals = []
    for gap in gap_pieces:
        distances = sorted(line.project(Point(point)) for point in (gap.coords[0],gap.coords[-1]))
        if distances[1]-distances[0] > 1e-9:
            intervals.append(distances)
    intervals.sort()
    max_length = max((end-start for start,end in intervals),default=0.0)
    expanded = support.buffer(tolerance)
    supported_start = expanded.covers(Point(stroke.start[:2]))
    supported_end = expanded.covers(Point(stroke.end[:2]))
    accepted = supported_start and supported_end and max_length <= max_gap+tolerance
    samples = []
    for start,end in intervals[:_MAX_GAPS]:
        samples.append({"start_mm":list(line.interpolate(start).coords[0]),
                        "end_mm":list(line.interpolate(end).coords[0]),"length_mm":end-start})
    return {
        "line":stroke.line,"layer_z_mm":stroke.end[2],
        "previous_deposition_z_mm":stroke.previous_z,
        "previous_deposition_segment_count":stroke.previous_count,
        "layer_step_mm":None if stroke.previous_z is None else stroke.end[2]-stroke.previous_z,
        "start_mm":list(stroke.start),"end_mm":list(stroke.end),
        "xy_length_mm":stroke.length,"width_mm":stroke.width,
        "supported_start":bool(supported_start),"supported_end":bool(supported_end),
        "start_support_distance_mm":Point(stroke.start[:2]).distance(support) if not support.is_empty else None,
        "end_support_distance_mm":Point(stroke.end[:2]).distance(support) if not support.is_empty else None,
        "gap_count":len(intervals),"max_unsupported_gap_mm":max_length,
        "unsupported_intervals":samples,"unsupported_intervals_truncated":len(intervals)>len(samples),
        "status":"PASS" if accepted else "FAIL",
    }


def audit_long_bridges(gcodeText, max_gap_mm=12, nozzle_width_mm=.4, tolerance_mm=.03):
    """Measure every positive Bridge infill segment longer than ``max_gap_mm``.

    Paths are buffered by their declared WIDTH/2, using the nearest lower Z
    that actually received deposition. Thick bridge layer jumps are recorded.
    This result cannot override Floating/unknown warnings or profile/mesh gates.
    ``nozzle_width_mm`` validates the numerical tolerance; declared WIDTH is
    mandatory, so no missing path width is silently replaced with nozzle size.
    """
    findings = _Findings()
    try:
        max_gap, nozzle, tolerance = map(float,(max_gap_mm,nozzle_width_mm,tolerance_mm))
        if not all(math.isfinite(value) for value in (max_gap,nozzle,tolerance)):
            raise ValueError()
        if max_gap<=0 or nozzle<=0 or not 0<=tolerance<=nozzle/10:
            raise ValueError()
    except (TypeError,ValueError):
        return {"status":"FAIL_LONG_BRIDGE_AUDIT","physical_validation":False,
                "findings":[{"kind":"invalid_profile","line":None,
                             "detail":"Finite positive gap/nozzle and tolerance no larger than nozzle/10 required"}],
                "findings_count":1,"findings_truncated":False,"evidence":[],"evidence_truncated":False}
    if not isinstance(gcodeText,str):
        gcodeText = ""
        findings.add("invalid_gcode",None,"G-code must be text")
    layers, strokes, bridge_count, max_length = _parse(gcodeText,max_gap,findings)
    if not strokes:
        findings.add("no_qualifying_bridge",None,"No positive classified bridge segment exceeds the configured gap limit")
    cache = {}
    evidence = []
    failures = 0
    max_measured_gap = 0.0
    for stroke in strokes:
        cache_key = (stroke.previous_z,stroke.previous_count)
        try:
            if cache_key not in cache:
                previous = layers.get(stroke.previous_z,[])[:stroke.previous_count]
                cache[cache_key] = _support_geometry(previous) if previous else shapely.GeometryCollection()
            item = _audit_stroke(stroke,cache[cache_key],max_gap,tolerance)
        except (shapely.GEOSException,ValueError,OverflowError) as error:
            findings.add("invalid_support_geometry",stroke.line,str(error))
            failures += 1
            continue
        max_measured_gap = max(max_measured_gap,item["max_unsupported_gap_mm"])
        if item["status"] == "FAIL":
            failures += 1
            detail = ("Both endpoints require previous-layer deposition support" if
                      not item["supported_start"] or not item["supported_end"] else
                      f"Unsupported gap {item['max_unsupported_gap_mm']:.6f} mm exceeds limit")
            findings.add("unsupported_long_bridge",stroke.line,detail)
        if len(evidence)<_MAX_SAMPLES:
            evidence.append(item)
        elif item["status"] == "FAIL":
            replacement = next((index for index,value in enumerate(evidence) if value["status"]=="PASS"),None)
            if replacement is not None:
                evidence[replacement] = item
        elif item["max_unsupported_gap_mm"]>max(value["max_unsupported_gap_mm"] for value in evidence):
            replacement = next((index for index,value in enumerate(evidence) if value["status"]=="PASS"),None)
            if replacement is not None:
                evidence[replacement] = item
    return {
        "status":"PASS_LONG_BRIDGE_AUDIT" if not findings.count else "FAIL_LONG_BRIDGE_AUDIT",
        "physical_validation":False,
        "warning_scope":"Long bridging extrusions only; Floating and unknown warnings remain failures",
        "profile":{"max_gap_mm":max_gap,"nozzle_width_mm":nozzle,"tolerance_mm":tolerance},
        "positive_bridge_segment_count":bridge_count,"long_bridge_segment_count":len(strokes),
        "failed_segment_count":failures,"max_bridge_segment_mm":max_length,
        "max_unsupported_gap_mm":max_measured_gap,"deposited_layer_count":len(layers),
        "support_layer_rule":"Nearest actually deposited Z below bridge Z; declared widths buffered by half width",
        "support_buffer":"Nominal WIDTH/2 round buffers, inscribed 32-sided end caps; no filled material assumed between paths",
        "extrusion_dialect":"RepRap: independent XYZ and E modes; G92 preserves unretracted filament debt",
        "evidence":sorted(evidence,key=lambda item:item["line"]),"evidence_truncated":len(strokes)>len(evidence),
        "findings":findings.samples,"findings_count":findings.count,
        "findings_truncated":findings.count>len(findings.samples),
    }
