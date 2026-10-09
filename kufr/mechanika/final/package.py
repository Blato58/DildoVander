"""Package exactly the verified local result, including reproducible sources."""
import hashlib
import json
import zipfile

from geometry import ROOT
from build import OUT,source_hashes
from artifacts import package_files


def main():
    report=json.loads((OUT/"verification.json").read_text(encoding="utf-8"))
    assert report["result"]=="PASS_CAD_NOT_PHYSICAL_ACCEPTANCE"
    assert report["sources"]==source_hashes(),"Sources changed since verification"
    manifest=json.loads((OUT/"manifest.json").read_text(encoding="utf-8"))
    assert manifest["sources"]==report["sources"],"Export and verification source manifests differ"
    files=package_files(ROOT,report["sources"],manifest)
    target=OUT/"F1-kouzelny-kufr.zip"
    with zipfile.ZipFile(target,"w",zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
        for p in files:archive.write(p,p.relative_to(ROOT).as_posix())
    with zipfile.ZipFile(target) as archive:
        assert archive.testzip() is None
    digest=hashlib.sha256(target.read_bytes()).hexdigest()
    (OUT/"package-sha256.txt").write_text(f"{digest}  {target.name}\n",encoding="ascii")
    print(f"Verified ZIP: {target.stat().st_size} bytes; SHA256 {digest}",flush=True)


if __name__=="__main__":main()
