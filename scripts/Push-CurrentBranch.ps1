$ErrorActionPreference = 'Stop'

$expectedRemote = 'https://github.com/Blato58/DildoVander.git'
$actualRemote = (& git remote get-url --push origin).Trim()
if ($LASTEXITCODE -ne 0 -or $actualRemote -ne $expectedRemote) {
    throw "Push target must be $expectedRemote."
}

$branch = (& git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    throw 'Push requires a named branch.'
}

$changes = & git status --porcelain
if ($LASTEXITCODE -ne 0 -or $changes) {
    throw 'Commit or resolve all working-tree changes before pushing.'
}

$localHead = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Cannot read the local commit.'
}

$accountToken = & gh auth token --user Blato58
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accountToken)) {
    throw 'GitHub account Blato58 is not authenticated.'
}

$previousGhToken = $env:GH_TOKEN
$previousGitPrompt = $env:GIT_TERMINAL_PROMPT
$env:GH_TOKEN = $accountToken
$env:GIT_TERMINAL_PROMPT = '0'

try {
    git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push --set-upstream origin $branch
    if ($LASTEXITCODE -ne 0) {
        throw 'Git push failed.'
    }

    $remoteRef = "refs/heads/$branch"
    $remoteLine = & git -c credential.helper= -c 'credential.helper=!gh auth git-credential' ls-remote --exit-code origin $remoteRef
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remoteLine)) {
        throw 'Cannot verify the pushed branch.'
    }

    $remoteHead = ($remoteLine -split '\s+')[0]
    if ($remoteHead -ne $localHead) {
        throw 'Remote branch does not match the local commit.'
    }

    Write-Output "Pushed $branch at $localHead"
}
finally {
    if ($null -eq $previousGhToken) {
        Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue
    }
    else {
        $env:GH_TOKEN = $previousGhToken
    }

    if ($null -eq $previousGitPrompt) {
        Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue
    }
    else {
        $env:GIT_TERMINAL_PROMPT = $previousGitPrompt
    }

    $accountToken = $null
}
