"""Source-bound PrusaSlicer CLI audit. G-code remains a local audit artifact."""
from __future__ import annotations

import argparse
from collections import defaultdict
import hashlib
import json
import math
import os
import itertools
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile
import numpy as np
from bridge_audit import audit_long_bridges


ROOT = Path(__file__).resolve().parent
PROFILE = ROOT / "support-free-petg.ini"
WARNING = re.compile(r"\b(?:warning|error|floating|empty|no layers detected|no printable|long bridging extrusions|loose extrusions|low bed adhesion)\b", re.I)
WORDS = re.compile(r"([XYZEF])([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)")


def _hash(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def profile_values(text):
    values = {}
    for line in text.splitlines():
        if "=" in line and not line.strip().startswith("#"):
            key, value = line.split("=", 1)
            key = key.strip()
            if key in values:
                raise ValueError(f"Duplicate profile setting: {key}")
            values[key] = value.strip().strip('"')
    return values


def equivalent_triangles(reference, actual, tolerance=.0003):
    """Match all triangles despite last-bit coordinate/order changes in 3MF."""
    if len(reference)!=len(actual):
        return False
    cells=defaultdict(list)
    for index,centre in enumerate(np.floor(reference.mean(axis=1)/tolerance).astype(np.int64)):
        cells[tuple(centre)].append(index)
    neighbours=tuple(itertools.product((-1,0,1),repeat=3))
    for triangle,centre in zip(actual,np.floor(actual.mean(axis=1)/tolerance).astype(np.int64)):
        found=False
        for offset in neighbours:
            key=tuple(centre+offset)
            for index in cells.get(key,()):
                distances=np.linalg.norm(triangle[:,None,:]-reference[index][None,:,:],axis=2)
                if max(distances.min(axis=0).max(),distances.min(axis=1).max())<=tolerance:
                    cells[key].remove(index)
                    found=True
                    break
            if found: break
        if not found: return False
    return True


def audit_profile(gcode, profile_text, saved_configuration=""):
    """Verify effective G-code settings, including empty custom machine macros."""
    expected = {}
    actual = {}
    for line in profile_text.splitlines():
        if "=" in line and not line.strip().startswith("#"):
            key, value = line.split("=", 1)
            expected[key.strip()] = value.strip().strip('"')
    for line in saved_configuration.splitlines():
        if "=" in line and not line.strip().startswith("#"):
            key, value = line.split("=", 1)
            actual[key.strip()] = value.strip().strip('"')
    for line in gcode.splitlines():
        if line.startswith("; ") and "=" in line:
            key, value = line[2:].split("=", 1)
            actual[key.strip()] = value.strip().strip('"')
    mismatches = {key: {"expected": value, "actual": actual.get(key)}
                  for key, value in expected.items() if actual.get(key) != value}
    return {"status": "PASS_EFFECTIVE_PROFILE" if not mismatches else "FAIL_EFFECTIVE_PROFILE",
            "settings": {key: actual.get(key) for key in expected}, "mismatches": mismatches}


def inspect_project(path, stl_path=None, bed=(220,220,250)):
    """Check that exported 3MF keeps the input model orientation and millimetres."""
    with zipfile.ZipFile(path) as archive:
        model = ET.fromstring(archive.read("3D/3dmodel.model"))
        namespace = "{http://schemas.microsoft.com/3dmanufacturing/core/2015/02}"
        items = model.findall(f"{namespace}build/{namespace}item")
        identity = [1, 0, 0, 0, 1, 0, 0, 0, 1]
        objects = {obj.get("id"): obj for obj in model.findall(f"{namespace}resources/{namespace}object")}
        oriented = len(items) == 1 and model.get("unit") == "millimeter"
        transforms = []
        mesh = None
        def resolve(object_id, seen=frozenset()):
            if object_id in seen or object_id not in objects:
                raise ValueError("Missing or recursive 3MF build object")
            obj=objects[object_id]
            vertices=obj.findall(f"{namespace}mesh/{namespace}vertices/{namespace}vertex")
            faces=obj.findall(f"{namespace}mesh/{namespace}triangles/{namespace}triangle")
            if vertices and faces:
                return (np.asarray([[float(v.get(axis)) for axis in "xyz"] for v in vertices]),
                        np.asarray([[int(face.get(f"v{i}")) for i in (1,2,3)] for face in faces]))
            points=[]; triangles=[]; offset=0
            for child in obj.findall(f"{namespace}components/{namespace}component"):
                vertices,faces=resolve(child.get("objectid"),seen|{object_id})
                matrix=[float(v) for v in child.get("transform","1 0 0 0 1 0 0 0 1 0 0 0").split()]
                if len(matrix)!=12 or not np.isfinite(matrix).all():
                    raise ValueError("Invalid 3MF component transform")
                points.append(vertices @ np.asarray(matrix[:9]).reshape((3,3))+np.asarray(matrix[9:]))
                triangles.append(faces+offset)
                offset+=len(vertices)
            if not points:
                raise ValueError("3MF build object contains no mesh")
            return np.concatenate(points),np.concatenate(triangles)

        for item in items:
            matrix = [float(v) for v in item.get("transform", "1 0 0 0 1 0 0 0 1 0 0 0").split()]
            transforms.append(matrix)
            oriented &= len(matrix) == 12 and all(abs(a - b) < 1e-6 for a, b in zip(matrix, identity))
            points,indices=resolve(item.get("objectid"))
            if len(points) and len(indices) and len(matrix) == 12:
                if indices.min() < 0 or indices.max() >= len(points) or not np.isfinite(points).all():
                    oriented = False
                    continue
                points = points @ np.asarray(matrix[:9]).reshape((3,3)) + np.asarray(matrix[9:])
                mesh = points[indices]
                extent = np.concatenate((points.min(axis=0), points.max(axis=0)))
                oriented &= abs(extent[2]) <= .03 and all(extent[i] >= -.03 and extent[i+3] <= bed[i]+.03 for i in range(3))
                area = np.linalg.norm(np.cross(mesh[:,1]-mesh[:,0], mesh[:,2]-mesh[:,0]),axis=1)
                oriented &= bool(np.all(area > 1e-10))
                edges = np.concatenate((mesh[:,[0,1]],mesh[:,[1,2]],mesh[:,[2,0]]))
                keys = np.sort(np.round(edges,5).view([("x","f8"),("y","f8"),("z","f8")]).reshape((-1,2)),axis=1)
                _, counts = np.unique(keys,axis=0,return_counts=True)
                oriented &= bool(np.all(counts==2))
                if stl_path is not None:
                    dtype=np.dtype([("normal","<f4",3),("vertices","<f4",(3,3)),("attribute","<u2")])
                    raw=Path(stl_path).read_bytes()
                    count=int.from_bytes(raw[80:84],"little")
                    if len(raw)!=84+50*count:
                        oriented=False
                    else:
                        reference=np.frombuffer(raw,dtype=dtype,count=count,offset=84)["vertices"].astype(float)
                        oriented &= equivalent_triangles(reference,mesh)
        return {"status": "PASS_ORIENTED_3MF" if oriented else "FAIL_ORIENTED_3MF",
                "unit": model.get("unit"), "build_transforms": transforms,
                "triangle_count": 0 if mesh is None else len(mesh),
                "stl_equivalence_checked": stl_path is not None,
                "configuration": "Model-only 3MF; load profiles/<part-name>.ini from this manufacturing package"}


def parse_gcode(text, bed=None):
    """Audit positive deposition, rather than counting config/comment mentions."""
    position = {"X": 0.0, "Y": 0.0, "Z": 0.0, "E": 0.0}
    absolute_xyz = True
    absolute_e = True
    role = None
    roles = set()
    extrusion_count = 0
    support_count = 0
    bridge_count = 0
    max_bridge = 0.0
    first_z = None
    layer_count = 0
    problems = []
    width=.4
    low=[math.inf]*3
    high=[-math.inf]*3
    for number, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if stripped.startswith(";TYPE:"):
            role = stripped.split(":", 1)[1].strip()
            roles.add(role)
        if stripped.startswith(';WIDTH:'):
            try:
                width=float(stripped.split(':',1)[1])
                if not math.isfinite(width) or width<=0:
                    raise ValueError('Invalid width')
            except ValueError:
                problems.append(f'Invalid extrusion width at line {number}')
                width=.4
        if stripped == ";LAYER_CHANGE":
            layer_count += 1
        if stripped.startswith(";") and WARNING.search(stripped[1:]):
            problems.append(f"G-code warning at line {number}: {stripped[:180]}")
        code = stripped.split(";", 1)[0].strip()
        if not code:
            continue
        command = code.split()[0].upper()
        values = {key: float(value) for key, value in WORDS.findall(code)}
        if any(not math.isfinite(value) for value in values.values()):
            problems.append(f"Nonfinite G-code value at line {number}")
            continue
        if command == "M82":
            absolute_e = True
        elif command == "M83":
            absolute_e = False
        elif command == "G90":
            absolute_xyz = True
        elif command == "G91":
            absolute_xyz = False
        elif command == "G92":
            position.update({key: value for key, value in values.items() if key in position})
        elif command in ("G0", "G00", "G1", "G01", "G2", "G02", "G3", "G03"):
            end = dict(position)
            for key in ("X", "Y", "Z"):
                if key in values:
                    end[key] = values[key] if absolute_xyz else position[key] + values[key]
            delta_e = 0.0
            if "E" in values:
                end["E"] = values["E"] if absolute_e else position["E"] + values["E"]
                delta_e = end["E"] - position["E"]
            distance = math.hypot(end["X"] - position["X"], end["Y"] - position["Y"])
            if delta_e > 1e-7 and distance > 1e-7:
                extrusion_count += 1
                for i,key in enumerate(('X','Y','Z')):
                    margin=width/2 if i<2 else 0
                    low[i]=min(low[i],position[key]-margin,end[key]-margin)
                    high[i]=max(high[i],position[key]+margin,end[key]+margin)
                if first_z is None:
                    first_z = end["Z"]
                if role is None:
                    problems.append(f"Unclassified deposition at line {number}")
                elif any(token in role.lower() for token in ("support", "raft")):
                    support_count += 1
                elif "bridge" in role.lower():
                    bridge_count += 1
                    max_bridge = max(max_bridge, distance)
                if command in ("G2", "G02", "G3", "G03"):
                    problems.append(f"Arc deposition cannot be audited at line {number}")
            position = end
        elif command == "G20":
            problems.append(f"Inch units cannot be audited at line {number}")
    if not extrusion_count:
        problems.append("Empty G-code: no positive XY deposition")
    if not roles or not layer_count:
        problems.append("Missing PrusaSlicer role or layer metadata")
    if first_z is not None and not 0 < first_z <= 0.23:
        problems.append(f"First deposition at Z={first_z:g} is not the configured bed layer")
    if support_count:
        problems.append(f"Support/raft deposition found: {support_count} segments")
    if bed is not None and extrusion_count and any(low[i]<-.03 or high[i]>bed[i]+.03 for i in range(3)):
        problems.append('Deposited material, including brim and line width, leaves the selected build volume')
    return {
        "status": "PASS_TOOLPATH_AUDIT" if not problems else "FAIL_SLICER",
        "deposition_segments": extrusion_count,
        "support_deposition_segments": support_count,
        "bridge_deposition_segments": bridge_count,
        "max_bridge_toolpath_segment_mm": round(max_bridge, 4),
        "bridge_candidate_review": max_bridge > 12.03,
        "bridge_measurement_note": "Toolpath segment includes anchors; not the unsupported gap",
        "first_deposition_z_mm": first_z,
        "deposition_bounds_mm":[round(value,4) for value in (*low,*high)] if extrusion_count else None,
        "checked_build_volume_mm":list(bed) if bed is not None else None,
        "layer_count": layer_count,
        "roles": sorted(roles),
        "problems": problems[:30],
        "problems_truncated": len(problems) > 30,
    }


def stability_types(output):
    """Read the entire Prusa warning block, including unfamiliar warning names."""
    kinds=[]
    active=False
    for raw in output.splitlines():
        line=raw.strip()
        if line=='print warning: Detected print stability issues:':
            if active:
                kinds.append('INCOMPLETE_STABILITY_BLOCK')
            active=True
        elif active and line=='Consider enabling supports.':
            active=False
        elif active and line and not line.lower().endswith('.stl'):
            kinds.append(line)
    if active:
        kinds.append('INCOMPLETE_STABILITY_BLOCK')
    return kinds


def _run(command, log, scan_warnings=True):
    try:
        result = subprocess.run(command, capture_output=True, text=True, errors="replace", timeout=240)
        stdout, stderr, exit_code = result.stdout, result.stderr, result.returncode
    except subprocess.TimeoutExpired as error:
        stdout = error.stdout or b""
        stderr = error.stderr or b""
        stdout = stdout.decode(errors="replace") if isinstance(stdout, bytes) else stdout
        stderr = stderr.decode(errors="replace") if isinstance(stderr, bytes) else stderr
        stderr += "\nERROR: slicer exceeded 240 second timeout"
        exit_code = -1
    except OSError as error:
        stdout, stderr, exit_code = "", f"ERROR: could not execute slicer: {error}", -1
    log.write_text(stdout + "\nSTDERR:\n" + stderr, encoding="utf-8")
    warnings = ([line[:300] for line in (stdout + "\n" + stderr).splitlines() if WARNING.search(line)]
                if scan_warnings else [])
    return {"exit_code": exit_code, "stdout_summary": stdout[-2000:],
            "stderr_summary": stderr[-2000:], "warnings": warnings[:30],
            "warnings_truncated": len(warnings) > 30,
            "stability_types": stability_types(stdout+'\n'+stderr) if scan_warnings else []}


def resolve_warnings(run, gcode):
    """Keep raw warnings; accept only proven, two-ended multi-gap long strokes."""
    warnings=run.get("warnings",[])
    kinds=run.get('stability_types',[])
    if not warnings and not kinds:
        return {"status":"PASS_NO_WARNINGS"}
    allowed={"print warning: Detected print stability issues:","Long bridging extrusions"}
    if (run.get("warnings_truncated") or set(line.strip() for line in warnings)!=allowed
            or kinds!=['Long bridging extrusions']):
        return {"status":"FAIL_UNRESOLVED_WARNINGS","raw_warnings":warnings,
                "stability_types":kinds}
    audit=audit_long_bridges(gcode,max_gap_mm=12,nozzle_width_mm=.4,tolerance_mm=.03)
    return {"status":"PASS_LONG_WARNING_REVIEW" if audit["status"]=="PASS_LONG_BRIDGE_AUDIT" else "FAIL_UNRESOLVED_WARNINGS",
            "raw_warnings":warnings,"long_bridge_audit":audit,
            "explanation":"Full strokes cross supported islands; audit measures each nominal deposited-material gap and both endpoints. Floating/unknown warnings never qualify."}


def assert_saved_evidence(out, part):
    """Re-read local slicing evidence; stored PASS labels alone cannot qualify it."""
    root=Path(out)/'slicing'
    name=part['name']
    if not re.fullmatch(r'[a-z][a-z0-9_]*',name):
        raise ValueError('Unsafe slicing evidence name')
    paths={key:root/f'{name}{suffix}' for key,suffix in (
        ('gcode_sha256','.gcode'),('effective_profile_sha256','-effective.ini'),
        ('slice_log_sha256','-slice.log'),('project_log_sha256','-project.log'))}
    for key,path in paths.items():
        if not path.is_file() or _hash(path)!=part.get(key):
            raise ValueError('Missing or changed slicer evidence: '+path.name)
    gcode=paths['gcode_sha256'].read_text(encoding='utf-8',errors='replace')
    configuration=paths['effective_profile_sha256'].read_text(encoding='utf-8')
    log=paths['slice_log_sha256'].read_text(encoding='utf-8')
    current={'warnings':[line[:300] for line in log.splitlines() if WARNING.search(line)],
             'stability_types':stability_types(log),'warnings_truncated':False}
    if any(current[key]!=part['slice'].get(key) for key in ('warnings','stability_types','warnings_truncated')):
        raise ValueError('Slicer warning record differs from actual log: '+name)
    expected_profile=(Path(out)/part['profile']).read_text(encoding='utf-8')
    if (parse_gcode(gcode,bed=(220,220,250))!=part['toolpath'] or audit_profile(gcode,expected_profile,configuration)!=part['effective_profile']
            or resolve_warnings(current,gcode)!=part['warning_resolution']):
        raise ValueError('Slicer audit differs from actual saved evidence: '+name)
    project_log=paths['project_log_sha256'].read_text(encoding='utf-8')
    if any(WARNING.search(line) for line in project_log.splitlines()):
        raise ValueError('Warnings in saved 3MF export log: '+name)


def validate_exports(out, slicer_path=None):
    """Slice every current manifest STL with its hash-bound per-part profile."""
    out = Path(out).resolve()
    manifest = json.loads((out / "manifest.json").read_text(encoding="utf-8"))
    names = [part["name"] for part in manifest["parts"]]
    if not names or len(set(names)) != len(names) or any(
            not re.fullmatch(r"[a-z][a-z0-9_]*", name) for name in names):
        raise ValueError("Manifest printable part names must be unique and safe")
    inputs = []
    for entry in manifest["parts"]:
        name = entry["name"]
        path = out / "stl" / f"{name}.stl"
        relative = entry.get("manufacturing", {}).get("slicer_profile")
        profile = (out / relative).resolve() if relative else PROFILE
        if relative and (not profile.is_relative_to(out) or profile.suffix != ".ini"):
            raise ValueError("Unsafe per-part slicer profile")
        digest = _hash(profile)
        expected = entry.get("manufacturing", {}).get("slicer_profile_sha256", digest)
        if digest != expected:
            raise ValueError("Slicer profile differs from manufacturing manifest")
        inputs.append({"name": name, "stl": f"stl/{name}.stl", "stl_sha256": _hash(path),
                       "profile": relative or "support-free-petg.ini", "profile_sha256": digest})
    profile_hash = _hash(PROFILE)
    report = {"schema": 1, "profile": "support-free-petg.ini", "profile_sha256": profile_hash,
              "manifest_sha256": _hash(out / "manifest.json"), "parts": inputs,
              "evidence": "Real slicing and toolpath audit; not physical print acceptance",
              "geometric_bridge_gate_mm": 12.0,
              "acceptance_note": "Requires separate geometric gap validation; toolpaths include anchors",
              "machine_gcode_shipping": False}
    configured = slicer_path or os.environ.get("KUFR_SLICER")
    executable = shutil.which(str(configured)) if configured else None
    if not executable:
        report.update(status="SKIP_SLICER_UNAVAILABLE", tool=None,
                      reason="Supply --slicer or KUFR_SLICER pointing to PrusaSlicer console")
        for part in inputs:
            part["status"] = "SKIP_SLICER_UNAVAILABLE"
        return report
    audit = out / "slicing"
    projects = out / "3mf"
    audit.mkdir(exist_ok=True)
    projects.mkdir(exist_ok=True)
    shutil.copyfile(PROFILE, out / "support-free-petg.ini")
    version = _run([executable, "--help"], audit / "tool.log", scan_warnings=False)
    # Version is at the beginning, while the bounded summary keeps the tail.
    full_header = next((line for line in (audit / "tool.log").read_text(encoding="utf-8").splitlines()
                        if line.startswith("PrusaSlicer-")), "")
    report["tool"] = {"name": "PrusaSlicer", "version": full_header,
                      "executable_sha256": _hash(executable), "probe": version,
                      "log_sha256": _hash(audit/'tool.log')}
    if version["exit_code"] != 0 or not full_header.startswith("PrusaSlicer-"):
        report["status"] = "FAIL_SLICER_TOOL"
        return report
    for part in inputs:
        name = part["name"]
        print(f"Slicing {name}",flush=True)
        profile = out / part["profile"]
        stl = out / part["stl"]
        gcode = audit / f"{name}.gcode"
        effective = audit / f"{name}-effective.ini"
        project = projects / f"{name}.3mf"
        # Remove stale outputs before invoking the tool: a failed run must never
        # accidentally audit an older success under the current input hash.
        gcode.unlink(missing_ok=True)
        effective.unlink(missing_ok=True)
        project.unlink(missing_ok=True)
        base = [executable, "--load", str(profile), "--config-compatibility", "disable",
                "--dont-arrange", "--no-ensure-on-bed", "--threads", "2"]
        # Arrange XY within the selected bed, including each part's brim.
        # The model-only 3MF below preserves the original oriented STL mesh.
        slice_base=[argument for argument in base if argument!='--dont-arrange']+['--center','110,110']
        part['gcode_xy_placement']='Arranged about selected 220 mm bed centre110,110; orientation unchanged'
        part["slice"] = _run(slice_base + ["--save", str(effective), "--export-gcode",
                                     "--output", str(gcode), str(stl)],
                             audit / f"{name}-slice.log")
        part["project"] = _run(base + ["--export-3mf", "--output", str(project), str(stl)],
                               audit / f"{name}-project.log")
        part['slice_log_sha256']=_hash(audit/f'{name}-slice.log')
        part['project_log_sha256']=_hash(audit/f'{name}-project.log')
        if gcode.is_file():
            part["gcode_sha256"] = _hash(gcode)
            text = gcode.read_text(encoding="utf-8", errors="replace")
            part["toolpath"] = parse_gcode(text,bed=(220,220,250))
            part["warning_resolution"] = resolve_warnings(part["slice"],text)
            estimates={}
            for key,pattern in (("filament_g",r"; filament used \[g\] = ([0-9.]+)"),
                                ("time",r"; estimated printing time \(normal mode\) = (.+)")):
                match=re.search(pattern,text)
                if match: estimates[key]=float(match.group(1)) if key=="filament_g" else match.group(1).strip()
            part["slicer_estimates"]=estimates
            configuration = effective.read_text(encoding="utf-8") if effective.is_file() else ""
            part["effective_profile"] = audit_profile(text, profile.read_text(encoding="utf-8"), configuration)
            if effective.is_file():
                part["effective_profile_sha256"] = _hash(effective)
        if project.is_file():
            part["project_3mf"] = f"3mf/{name}.3mf"
            part["project_sha256"] = _hash(project)
            try:
                part["project_geometry"] = inspect_project(project,stl)
            except (zipfile.BadZipFile, ET.ParseError, KeyError, ValueError) as error:
                part["project_geometry"] = {"status": "FAIL_ORIENTED_3MF", "error": str(error)}
        success = (part["slice"]["exit_code"] == 0 and part.get("warning_resolution",{}).get("status") in ("PASS_NO_WARNINGS","PASS_LONG_WARNING_REVIEW")
                   and part["project"]["exit_code"] == 0 and not part["project"]["warnings"]
                   and part.get("project_geometry", {}).get("status") == "PASS_ORIENTED_3MF"
                   and part.get("effective_profile", {}).get("status") == "PASS_EFFECTIVE_PROFILE"
                   and part.get("toolpath", {}).get("status") == "PASS_TOOLPATH_AUDIT")
        if _hash(stl) != part["stl_sha256"] or _hash(profile) != part["profile_sha256"] or _hash(PROFILE) != profile_hash:
            success = False
            part["input_changed"] = True
        part["status"] = "PASS_SLICER" if success else "FAIL_SLICER"
    report["status"] = ("PASS_SLICER" if all(part["status"] == "PASS_SLICER" for part in inputs)
                        else "FAIL_SLICER")
    if _hash(out / "manifest.json") != report["manifest_sha256"]:
        report.update(status="FAIL_INPUT_CHANGED", manifest_changed=True)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("out", type=Path)
    parser.add_argument("--slicer", default=os.environ.get("KUFR_SLICER"))
    arguments = parser.parse_args()
    report = validate_exports(arguments.out, arguments.slicer)
    (arguments.out / "slicer-validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(report["status"])
    return 0 if report["status"] == "PASS_SLICER" else 1


if __name__ == "__main__":
    raise SystemExit(main())
