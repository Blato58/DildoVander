"""Package exactly the verified local result, including reproducible sources."""
import hashlib
import json
import zipfile

from geometry import ROOT, PARAMS
from build import OUT,source_hashes
from artifacts import package_files, assert_cad_hashes, assert_verified_exports
from slicer_validation import assert_saved_evidence


def main():
    report_bytes=(OUT/"verification.json").read_bytes()
    report=json.loads(report_bytes)
    assert report["result"]=="PASS_CAD_NOT_PHYSICAL_ACCEPTANCE"
    assert report["sources"]==source_hashes(),"Sources changed since verification"
    manifest_bytes=(OUT/"manifest.json").read_bytes()
    manifest=json.loads(manifest_bytes)
    assert manifest["sources"]==report["sources"],"Export and verification source manifests differ"
    assert_cad_hashes(ROOT,manifest,report)
    assert_verified_exports(OUT,manifest,report)
    if report.get('printing',{}).get('slicing')=='PASS_SLICER':
        slicing=json.loads((OUT/'slicer-validation.json').read_bytes())
        for part in slicing['parts']:
            assert_saved_evidence(OUT,part)
        assert hashlib.sha256((OUT/'slicing/tool.log').read_bytes()).hexdigest()==slicing['tool']['log_sha256']
    hashes={**report['export_sha256'],'verification.json':hashlib.sha256(report_bytes).hexdigest()}
    assert hashes['manifest.json']==hashlib.sha256(manifest_bytes).hexdigest(), 'Manifest changed while packaging'
    index_bytes=(json.dumps(hashes,indent=2)+'\n').encode('utf-8')
    (OUT/'artifact-sha256.json').write_bytes(index_bytes)
    files=package_files(ROOT,report["sources"],manifest)
    # Bind every archived byte to the already accepted snapshot. Re-reading
    # exports here would silently admit a concurrent edit after hashes was made.
    expected_bytes=dict(report['sources'])
    expected_bytes.update({'exports/'+name:digest for name,digest in hashes.items()})
    expected_bytes['exports/artifact-sha256.json']=hashlib.sha256(index_bytes).hexdigest()
    target=OUT/(PARAMS['revision']+"-kouzelny-kufr.zip")
    with zipfile.ZipFile(target,"w",zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
        for p in files:archive.write(p,p.relative_to(ROOT).as_posix())
    with zipfile.ZipFile(target) as archive:
        assert archive.testzip() is None
        assert set(archive.namelist())==set(expected_bytes)
        for name,digest in expected_bytes.items():
            assert hashlib.sha256(archive.read(name)).hexdigest()==digest, 'ZIP readback mismatch: '+name
    assert report['sources']==source_hashes(), 'Sources changed during packaging'
    assert_cad_hashes(ROOT,manifest,report)
    for name,digest in expected_bytes.items():
        assert hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==digest, 'File changed during packaging: '+name
    digest=hashlib.sha256(target.read_bytes()).hexdigest()
    (OUT/"package-sha256.txt").write_text(f"{digest}  {target.name}\n",encoding="ascii")
    print(f"Verified ZIP: {target.stat().st_size} bytes; SHA256 {digest}",flush=True)


if __name__=="__main__":main()
