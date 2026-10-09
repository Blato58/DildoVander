param([Parameter(Mandatory = $true)][string]$Python)
$ErrorActionPreference = 'Stop'
$taskPython = (Get-Command $Python -ErrorAction Stop).Source
Push-Location $PSScriptRoot
try {
    & node --check viewer.js
    if ($LASTEXITCODE -ne 0) { throw 'F1 regeneration failed: viewer JavaScript syntax' }
    & node --test test_viewer.cjs
    if ($LASTEXITCODE -ne 0) { throw 'F1 regeneration failed: viewer regression tests' }
    foreach ($taskStep in @('test_packaging.py', 'test_inspect_data.py', 'build.py', 'verify.py', 'preview.py', 'drawings.py', 'package.py')) {
        & $taskPython $taskStep
        if ($LASTEXITCODE -ne 0) { throw "F1 regeneration failed: $taskStep" }
    }
}
finally { Pop-Location }
