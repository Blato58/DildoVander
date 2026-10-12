param([Parameter(Mandatory = $true)][string]$Python)
$ErrorActionPreference = 'Stop'
$taskPython = (Get-Command $Python -ErrorAction Stop).Source
Push-Location $PSScriptRoot
try {
    & $taskPython assembly_docs.py
    if ($LASTEXITCODE -ne 0) { throw 'F2 regeneration failed: assembly documentation' }
    & $taskPython generate_effects.py --check
    if ($LASTEXITCODE -ne 0) { throw 'Choreography header is stale; run generate_effects.py' }
    & node --check viewer.js
    if ($LASTEXITCODE -ne 0) { throw 'F2 regeneration failed: viewer JavaScript syntax' }
    & node --test test_viewer.cjs
    if ($LASTEXITCODE -ne 0) { throw 'F2 regeneration failed: viewer regression tests' }
    foreach ($taskStep in @('test_effects.py', 'test_viewer_style.py', 'test_packaging.py', 'test_verification_gate.py', 'test_inspect_data.py', 'test_harness.py', 'test_f2_cad.py', 'test_sliding_follower.py', 'test_floor_anchors.py', 'test_follower_load.py', 'test_strength.py', 'test_assembly_access.py', 'validate_assembly.py')) {
        & $taskPython $taskStep
        if ($LASTEXITCODE -ne 0) { throw "F2 regeneration failed: $taskStep" }
    }
    & $taskPython artifacts.py --generate
    if ($LASTEXITCODE -ne 0) { throw 'F2 regeneration failed: source-bound export generation' }
    foreach ($taskStep in @('verify.py', 'package.py')) {
        & $taskPython $taskStep
        if ($LASTEXITCODE -ne 0) { throw "F2 regeneration failed: $taskStep" }
    }
}
finally { Pop-Location }
