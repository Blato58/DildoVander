param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'deploy', 'publish', 'verify', 'rollback', 'status')]
    [string]$Command = 'build',
    [string]$Python = $env:KUFR_CAD_PYTHON,
    [switch]$Force,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Options
)
$ErrorActionPreference = 'Stop'
if (-not $Python) {
    $localPython = Join-Path $PSScriptRoot '../mechanika/final/.venv/Scripts/python.exe'
    if (Test-Path -LiteralPath $localPython) { $Python = $localPython }
    else { $Python = 'python' }
}
$taskPython = (Get-Command $Python -ErrorAction Stop).Source
$taskArguments = @('-X', 'utf8', (Join-Path $PSScriptRoot 'cad.py'), $Command)
if ($Force) { $taskArguments += '--force' }
$taskArguments += $Options
& $taskPython @taskArguments
if ($LASTEXITCODE -ne 0) { throw "CAD $Command failed (exit $LASTEXITCODE)" }
