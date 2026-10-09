param([string]$Python = "", [switch]$SkipRenders, [switch]$CheckOnly)
$ErrorActionPreference = "Stop"
$mechanicsRoot = Split-Path -Parent $PSScriptRoot
if (-not $Python) {
    $Python = Join-Path $mechanicsRoot '.venv/Scripts/python.exe'
}
if (-not (Test-Path -LiteralPath $Python)) {
    throw "Python/CadQuery environment missing. Create mechanika/.venv and install mechanika/requirements.txt, or pass -Python <python.exe>."
}
$generator = Join-Path $PSScriptRoot 'scripts/generate.py'
$generatorArgs = @($generator)
if ($SkipRenders) { $generatorArgs += '--skip-renders' }
if ($CheckOnly) { $generatorArgs += '--check-only' }
& $Python @generatorArgs
if ($LASTEXITCODE -ne 0) { throw "CAD regeneration failed (exit $LASTEXITCODE)." }
if (-not $CheckOnly) {
    & $Python (Join-Path $PSScriptRoot 'scripts/verify_delivery.py')
    if ($LASTEXITCODE -ne 0) { throw "CAD delivery verification failed (exit $LASTEXITCODE)." }
}
