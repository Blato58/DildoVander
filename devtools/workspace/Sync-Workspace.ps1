[CmdletBinding()]
param(
    [string]$WorkspacePath,
    [switch]$RefreshManagedTasks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$managedDetail = "Managed by the DildoVander workspace setup."
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Convert-JsonValue {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    if ($Value -is [System.Collections.IDictionary]) {
        $converted = [ordered]@{}
        foreach ($key in $Value.Keys) {
            $converted[$key] = Convert-JsonValue $Value[$key]
        }
        return ,$converted
    }

    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $converted = [ordered]@{}
        foreach ($property in $Value.PSObject.Properties) {
            $converted[$property.Name] = Convert-JsonValue $property.Value
        }
        return ,$converted
    }

    if ($Value -is [System.Array]) {
        $converted = @()
        foreach ($item in $Value) {
            $converted += ,(Convert-JsonValue $item)
        }
        return ,$converted
    }

    return $Value
}

function ConvertFrom-Jsonc {
    param(
        [string]$Text,
        [string]$Description
    )

    $withoutComments = [System.Text.StringBuilder]::new()
    $inString = $false
    $escaped = $false
    $lineComment = $false
    $blockComment = $false

    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        $nextCharacter = if ($index + 1 -lt $Text.Length) { $Text[$index + 1] } else { [char]0 }

        if ($lineComment) {
            if ($character -eq "`r" -or $character -eq "`n") {
                $null = $withoutComments.Append($character)
                $lineComment = $false
            }
            else {
                $null = $withoutComments.Append(" ")
            }
            continue
        }

        if ($blockComment) {
            if ($character -eq "*" -and $nextCharacter -eq "/") {
                $null = $withoutComments.Append("  ")
                $index++
                $blockComment = $false
            }
            elseif ($character -eq "`r" -or $character -eq "`n") {
                $null = $withoutComments.Append($character)
            }
            else {
                $null = $withoutComments.Append(" ")
            }
            continue
        }

        if ($inString) {
            $null = $withoutComments.Append($character)
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq "\") {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }

        if ($character -eq '"') {
            $inString = $true
            $null = $withoutComments.Append($character)
        }
        elseif ($character -eq "/" -and $nextCharacter -eq "/") {
            $null = $withoutComments.Append("  ")
            $index++
            $lineComment = $true
        }
        elseif ($character -eq "/" -and $nextCharacter -eq "*") {
            $null = $withoutComments.Append("  ")
            $index++
            $blockComment = $true
        }
        else {
            $null = $withoutComments.Append($character)
        }
    }

    $commentFreeText = $withoutComments.ToString()
    $withoutTrailingCommas = [System.Text.StringBuilder]::new()
    $inString = $false
    $escaped = $false

    for ($index = 0; $index -lt $commentFreeText.Length; $index++) {
        $character = $commentFreeText[$index]
        if ($inString) {
            $null = $withoutTrailingCommas.Append($character)
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq "\") {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }

        if ($character -eq '"') {
            $inString = $true
            $null = $withoutTrailingCommas.Append($character)
            continue
        }

        if ($character -eq ",") {
            $nextIndex = $index + 1
            while ($nextIndex -lt $commentFreeText.Length -and [char]::IsWhiteSpace($commentFreeText[$nextIndex])) {
                $nextIndex++
            }
            if ($nextIndex -lt $commentFreeText.Length -and ($commentFreeText[$nextIndex] -eq "}" -or $commentFreeText[$nextIndex] -eq "]")) {
                $null = $withoutTrailingCommas.Append(" ")
                continue
            }
        }

        $null = $withoutTrailingCommas.Append($character)
    }

    try {
        $parsed = ConvertFrom-Json -InputObject $withoutTrailingCommas.ToString() -ErrorAction Stop
    }
    catch {
        throw "Unable to parse JSON/JSONC in '$Description': $($_.Exception.Message)"
    }

    return Convert-JsonValue $parsed
}

function Read-JsonTemplate {
    param([string]$Path)

    return ConvertFrom-Jsonc -Text ([System.IO.File]::ReadAllText($Path)) -Description $Path
}

function Copy-JsonValue {
    param([object]$Value)

    return Convert-JsonValue $Value
}

function Merge-MissingProperties {
    param(
        [System.Collections.IDictionary]$Target,
        [System.Collections.IDictionary]$Defaults
    )

    foreach ($key in $Defaults.Keys) {
        if (-not $Target.Contains($key)) {
            $Target[$key] = Copy-JsonValue $Defaults[$key]
        }
        elseif ($Target[$key] -is [System.Collections.IDictionary] -and
            $Defaults[$key] -is [System.Collections.IDictionary]) {
            Merge-MissingProperties -Target $Target[$key] -Defaults $Defaults[$key]
        }
    }
}

function Merge-StringLists {
    param(
        [System.Collections.IDictionary]$Target,
        [System.Collections.IDictionary]$Defaults,
        [string[]]$Keys
    )

    foreach ($key in $Keys) {
        $items = [System.Collections.ArrayList]::new()
        if ($Target.Contains($key)) {
            foreach ($item in @($Target[$key])) {
                if (-not $items.Contains($item)) {
                    $null = $items.Add($item)
                }
            }
        }
        if ($Defaults.Contains($key)) {
            foreach ($item in @($Defaults[$key])) {
                if (-not $items.Contains($item)) {
                    $null = $items.Add($item)
                }
            }
        }
        $Target[$key] = @($items.ToArray())
    }
}

function Merge-Tasks {
    param(
        [System.Collections.IDictionary]$Target,
        [System.Collections.IDictionary]$Defaults,
        [switch]$Refresh
    )

    if (-not $Target.Contains("version")) {
        $Target["version"] = $Defaults["version"]
    }

    if ($Defaults.Contains("inputs")) {
        if (-not $Target.Contains("inputs")) {
            $Target["inputs"] = @()
        }
        $Target["inputs"] = Merge-NamedEntries `
            -Existing @($Target["inputs"]) `
            -Defaults @($Defaults["inputs"]) `
            -Key "id"
    }

    $tasks = [System.Collections.ArrayList]::new()
    if ($Target.Contains("tasks")) {
        foreach ($task in @($Target["tasks"])) {
            $null = $tasks.Add($task)
        }
    }

    foreach ($candidate in @($Defaults["tasks"])) {
        $existingIndex = -1
        for ($index = 0; $index -lt $tasks.Count; $index++) {
            if ($tasks[$index]["label"] -eq $candidate["label"]) {
                $existingIndex = $index
                break
            }
        }

        if ($existingIndex -lt 0) {
            $null = $tasks.Add((Copy-JsonValue $candidate))
        }
        elseif ($Refresh -and $tasks[$existingIndex]["detail"] -eq $managedDetail) {
            $tasks[$existingIndex] = Copy-JsonValue $candidate
        }
    }

    $Target["tasks"] = @($tasks.ToArray())
}

function Merge-NamedEntries {
    param(
        [object[]]$Existing,
        [object[]]$Defaults,
        [string]$Key
    )

    $entries = [System.Collections.ArrayList]::new()
    foreach ($entry in @($Existing)) {
        $null = $entries.Add($entry)
    }

    foreach ($candidate in @($Defaults)) {
        $existingIndex = -1
        for ($index = 0; $index -lt $entries.Count; $index++) {
            if ($entries[$index][$Key] -eq $candidate[$Key]) {
                $existingIndex = $index
                break
            }
        }
        if ($existingIndex -lt 0) {
            $null = $entries.Add((Copy-JsonValue $candidate))
        }
    }

    return ,@($entries.ToArray())
}

function Merge-Workspace {
    param(
        [System.Collections.IDictionary]$Target,
        [System.Collections.IDictionary]$Defaults,
        [string]$LegacyFirmwarePath,
        [switch]$Refresh
    )

    $folders = [System.Collections.ArrayList]::new()
    if ($Target.Contains("folders")) {
        foreach ($folder in @($Target["folders"])) {
            $null = $folders.Add($folder)
        }
    }
    foreach ($candidate in @($Defaults["folders"])) {
        $existingIndex = -1
        for ($index = 0; $index -lt $folders.Count; $index++) {
            if ($folders[$index]["name"] -eq $candidate["name"]) {
                if ($existingIndex -ge 0) {
                    throw "Workspace folder '$($candidate["name"])' appears more than once. Resolve the name conflict before synchronizing."
                }
                $existingIndex = $index
            }
        }
        if ($existingIndex -lt 0) {
            $null = $folders.Add((Copy-JsonValue $candidate))
        }
        else {
            $existingPath = $folders[$existingIndex]["path"].Replace("\", "/")
            $expectedPath = $candidate["path"].Replace("\", "/")
            $normalizedExistingPath = $existingPath.Replace("//", "/")
            # Only the former generated Firmware location is a known migration.
            $isLegacyFirmware = $candidate["name"] -eq "Firmware" -and `
                $normalizedExistingPath -eq $LegacyFirmwarePath
            if ($normalizedExistingPath -ne $expectedPath -and -not $isLegacyFirmware) {
                throw "Workspace folder '$($candidate["name"])' already points elsewhere. Resolve the name conflict before synchronizing."
            }
            $folders[$existingIndex]["path"] = $candidate["path"]
        }
    }
    $Target["folders"] = @($folders.ToArray())

    if (-not $Target.Contains("settings")) {
        $Target["settings"] = [ordered]@{}
    }
    Merge-MissingProperties -Target $Target["settings"] -Defaults $Defaults["settings"]

    if (-not $Target.Contains("tasks")) {
        $Target["tasks"] = [ordered]@{}
    }
    Merge-Tasks -Target $Target["tasks"] -Defaults $Defaults["tasks"] -Refresh:$Refresh

    if (-not $Target.Contains("launch")) {
        $Target["launch"] = [ordered]@{}
    }
    if (-not $Target["launch"].Contains("version")) {
        $Target["launch"]["version"] = $Defaults["launch"]["version"]
    }
    if (-not $Target["launch"].Contains("configurations")) {
        $Target["launch"]["configurations"] = @()
    }
    $Target["launch"]["configurations"] = Merge-NamedEntries `
        -Existing @($Target["launch"]["configurations"]) `
        -Defaults @($Defaults["launch"]["configurations"]) `
        -Key "name"
}

function Write-JsonIfChanged {
    param(
        [string]$Path,
        [object]$Value
    )

    $content = (ConvertTo-Json -InputObject $Value -Depth 100) + [Environment]::NewLine
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $existing = [System.IO.File]::ReadAllText($Path)
        if ($existing -ceq $content) {
            Write-Output "Unchanged: $Path"
            return
        }
    }

    $temporaryPath = "$Path.tmp.$([Guid]::NewGuid().ToString('N'))"
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $content, $utf8NoBom)
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
    Write-Output "Updated: $Path"
}

$appRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($WorkspacePath)) {
    $workspaceRoot = Split-Path $appRoot -Parent
}
else {
    if (-not (Test-Path -LiteralPath $WorkspacePath -PathType Container)) {
        throw "Workspace path does not exist: $WorkspacePath"
    }
    $workspaceRoot = (Resolve-Path -LiteralPath $WorkspacePath).Path
}

$appDirectoryName = Split-Path $appRoot -Leaf
if (-not (Test-Path -LiteralPath (Join-Path $workspaceRoot $appDirectoryName) -PathType Container)) {
    throw "Workspace path must be the existing parent of the application repository: $workspaceRoot"
}
$appRelativePath = $appDirectoryName
$appRelativeForConfig = $appRelativePath.Replace("\", "/")
$firmwareRelativePath = "$appRelativeForConfig/firmware"
$firmwareFolderTaskPath = '${workspaceFolder}/' + $firmwareRelativePath
$templateRoot = Join-Path $PSScriptRoot "templates"
$settingsDefaults = Read-JsonTemplate (Join-Path $templateRoot "settings.template.json")
$extensionDefaults = Read-JsonTemplate (Join-Path $templateRoot "extensions.template.json")
$taskTemplateText = [System.IO.File]::ReadAllText((Join-Path $templateRoot "tasks.template.json"))
$folderTasks = ConvertFrom-Jsonc `
    -Text $taskTemplateText.Replace("@@FIRMWARE_PATH@@", $firmwareFolderTaskPath).Replace("@@WORKSPACE_CWD@@", $firmwareFolderTaskPath) `
    -Description "tasks.template.json"
$workspaceTasksText = $taskTemplateText.Replace("@@FIRMWARE_PATH@@", '${workspaceFolder:Firmware}').Replace("@@WORKSPACE_CWD@@", '${workspaceFolder:Firmware}')
$workspaceTasks = ConvertFrom-Jsonc -Text $workspaceTasksText -Description "multi-root tasks template"
foreach ($task in $workspaceTasks["tasks"]) {
    $task["label"] = "$($task["label"]) (Multi-root)"
    if ($task.Contains("detail")) {
        $task["detail"] = $managedDetail
    }
    if ($task.Contains("dependsOn")) {
        if ($task["dependsOn"] -is [System.Array]) {
            $task["dependsOn"] = @($task["dependsOn"] | ForEach-Object { "$_ (Multi-root)" })
        }
        else {
            $task["dependsOn"] = "$($task["dependsOn"]) (Multi-root)"
        }
    }
}
$workspaceTemplateText = [System.IO.File]::ReadAllText((Join-Path $templateRoot "workspace.template.json"))
$workspaceTemplateText = $workspaceTemplateText.Replace("@@FIRMWARE_PATH@@", $firmwareRelativePath)
$workspaceDefaults = ConvertFrom-Jsonc -Text $workspaceTemplateText -Description "workspace.template.json"
$workspaceDefaults["settings"] = Copy-JsonValue $settingsDefaults
$workspaceDefaults["tasks"] = $workspaceTasks
$workspaceDefaults["launch"] = Read-JsonTemplate (Join-Path $templateRoot "launch.workspace.template.json")

$vscodeRoot = Join-Path $workspaceRoot ".vscode"
New-Item -ItemType Directory -Path $vscodeRoot -Force | Out-Null

$settingsPath = Join-Path $vscodeRoot "settings.json"
$settings = if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
    ConvertFrom-Jsonc -Text ([System.IO.File]::ReadAllText($settingsPath)) -Description $settingsPath
}
else {
    [ordered]@{}
}
Merge-MissingProperties -Target $settings -Defaults $settingsDefaults
Write-JsonIfChanged -Path $settingsPath -Value $settings

$tasksPath = Join-Path $vscodeRoot "tasks.json"
$tasks = if (Test-Path -LiteralPath $tasksPath -PathType Leaf) {
    ConvertFrom-Jsonc -Text ([System.IO.File]::ReadAllText($tasksPath)) -Description $tasksPath
}
else {
    [ordered]@{}
}
Merge-Tasks -Target $tasks -Defaults $folderTasks -Refresh:$RefreshManagedTasks
Write-JsonIfChanged -Path $tasksPath -Value $tasks

$extensionsPath = Join-Path $vscodeRoot "extensions.json"
$extensions = if (Test-Path -LiteralPath $extensionsPath -PathType Leaf) {
    ConvertFrom-Jsonc -Text ([System.IO.File]::ReadAllText($extensionsPath)) -Description $extensionsPath
}
else {
    [ordered]@{}
}
Merge-StringLists -Target $extensions -Defaults $extensionDefaults -Keys @("recommendations", "unwantedRecommendations")
Write-JsonIfChanged -Path $extensionsPath -Value $extensions

$workspacePath = Join-Path $workspaceRoot "DildoVander.code-workspace"
$workspace = if (Test-Path -LiteralPath $workspacePath -PathType Leaf) {
    ConvertFrom-Jsonc -Text ([System.IO.File]::ReadAllText($workspacePath)) -Description $workspacePath
}
else {
    [ordered]@{}
}
Merge-Workspace -Target $workspace -Defaults $workspaceDefaults `
    -LegacyFirmwarePath "$appRelativeForConfig/kufr/firmware" -Refresh:$RefreshManagedTasks
Write-JsonIfChanged -Path $workspacePath -Value $workspace

Write-Output "Workspace root: $workspaceRoot"
Write-Output "Application repository: $appRoot"
Write-Output "Firmware project: $firmwareRelativePath"
