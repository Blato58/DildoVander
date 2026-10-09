param([Parameter(Mandatory = $true)][string]$Python)
$ErrorActionPreference = 'Stop'
$taskPython = (Get-Command $Python -ErrorAction Stop).Source
Push-Location $PSScriptRoot
try {
    & $taskPython generate_effects.py --check
    if ($LASTEXITCODE -ne 0) { throw 'Choreography header is stale; run generate_effects.py' }
    & node --check viewer.js
    if ($LASTEXITCODE -ne 0) { throw 'F2 regeneration failed: viewer JavaScript syntax' }
    & node --test test_viewer.cjs
    if ($LASTEXITCODE -ne 0) { throw 'F2 regeneration failed: viewer regression tests' }
    foreach ($taskStep in @('test_effects.py', 'test_packaging.py', 'test_verification_gate.py', 'test_inspect_data.py', 'test_harness.py', 'test_f2_cad.py', 'validate_assembly.py', 'build.py', 'verify.py', 'preview.py', 'drawings.py', 'package.py')) {
        & $taskPython $taskStep
        if ($LASTEXITCODE -ne 0) { throw "F2 regeneration failed: $taskStep" }
    }
}
finally { Pop-Location }
