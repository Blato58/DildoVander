param([Parameter(Mandatory = $true)][string]$Python, [switch]$Force)
$ErrorActionPreference = 'Stop'
$taskPython = (Get-Command $Python -ErrorAction Stop).Source
Push-Location $PSScriptRoot
try {
    $taskArguments = @('regenerate.py')
    if ($Force) { $taskArguments += '--force' }
    & $taskPython @taskArguments
    if ($LASTEXITCODE -ne 0) { throw 'F2 regeneration failed; see the Python stage above' }
}
finally { Pop-Location }
