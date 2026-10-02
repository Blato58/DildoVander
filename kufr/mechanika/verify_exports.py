"""Check actual binary STL files for closed edges and positive signed volume."""
import argparse
from collections import Counter
import json
from pathlib import Path
import struct


def verify(path):
    raw = path.read_bytes()
    count = struct.unpack_from("<I", raw, 80)[0]
    if len(raw) != 84 + 50 * count or not count:
        raise ValueError(f"Invalid binary STL length: {path}")
    edges = Counter()
    volume = 0.0
    for index in range(count):
        numbers = struct.unpack_from("<12fH", raw, 84 + index * 50)
        a,b,c = [numbers[start:start+3] for start in [3,6,9]]
        points = [tuple(round(coordinate, 4) for coordinate in v) for v in [a,b,c]]
        for i,j in [(0,1),(1,2),(2,0)]:
            edges[tuple(sorted((points[i],points[j])))] += 1
        volume += (a[0]*(b[1]*c[2]-b[2]*c[1]) +
                   a[1]*(b[2]*c[0]-b[0]*c[2]) +
                   a[2]*(b[0]*c[1]-b[1]*c[0])) / 6
    bad_edges = sum(value != 2 for value in edges.values())
    if bad_edges or volume <= 0:
        raise ValueError(f"{path.name}: {bad_edges} open/nonmanifold edges, signed volume {volume}")
    return dict(triangles=count, closed_edges=True, signed_volume_mm3=round(volume,3))


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output",type=Path,nargs="?",default=Path(__file__).parent/"output")
    args=parser.parse_args()
    report=json.loads((args.output/"verification.json").read_text(encoding="utf-8"))
    results={file.stem:verify(file) for file in sorted((args.output/"stl").glob("*.stl"))}
    if set(results) != set(report["parts"]):
        raise ValueError("STL inventory does not match CAD inventory")
    for name,result in results.items():
        exact=report["parts"][name]["volume_mm3"]
        relative=abs(result["signed_volume_mm3"]-exact)/exact
        if relative > .02:
            raise ValueError(f"{name}: STL/CAD volume mismatch {relative:.2%}")
    (args.output/"stl-verification.json").write_text(json.dumps(results,indent=2)+"\n",encoding="utf-8")
    print(f"PASS: {len(results)} closed STL meshes; positive volume; CAD volume deviation <= 2%")


if __name__=="__main__":
    main()
