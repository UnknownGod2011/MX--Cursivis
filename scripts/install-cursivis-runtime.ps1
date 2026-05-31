param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\Cursivis"),
    [string]$NodeVersion = "v22.22.0",
    [switch]$InstallLogitechPlugin,
    [switch]$NoStartupShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message"
}

function Resolve-PackageRoot {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }

    return Split-Path -Parent $scriptPath
}

function Copy-RuntimePayload {
    param(
        [string]$Source,
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Runtime payload was not found at '$Source'. Extract the full CursivisRuntime zip before running setup."
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Ensure-PortableNode {
    param(
        [string]$Destination,
        [string]$Version
    )

    $nodeDir = Join-Path $Destination "node"
    $nodeExe = Join-Path $nodeDir "node.exe"
    if (Test-Path -LiteralPath $nodeExe) {
        return $nodeExe
    }

    New-Item -ItemType Directory -Force -Path $nodeDir | Out-Null
    $archiveName = "node-$Version-win-x64.zip"
    $archiveUrl = "https://nodejs.org/dist/$Version/$archiveName"
    $downloadPath = Join-Path $env:TEMP $archiveName
    $extractRoot = Join-Path $env:TEMP "cursivis-node-$Version"

    Write-Step "Downloading portable Node.js $Version"
    Invoke-WebRequest -Uri $archiveUrl -OutFile $downloadPath

    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }

    Expand-Archive -LiteralPath $downloadPath -DestinationPath $extractRoot -Force
    $expandedDir = Get-ChildItem -LiteralPath $extractRoot -Directory | Select-Object -First 1
    if ($null -eq $expandedDir) {
        throw "Node.js archive did not contain the expected folder."
    }

    Get-ChildItem -LiteralPath $expandedDir.FullName -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $nodeDir -Recurse -Force
    }
    return $nodeExe
}

function Invoke-NpmCi {
    param(
        [string]$NodeExe,
        [string]$ProjectDir,
        [switch]$ProductionOnly
    )

    if (-not (Test-Path -LiteralPath (Join-Path $ProjectDir "package.json"))) {
        return
    }

    $npmCli = Join-Path (Split-Path -Parent $NodeExe) "node_modules\npm\bin\npm-cli.js"
    if (-not (Test-Path -LiteralPath $npmCli)) {
        throw "npm was not found in the portable Node.js folder."
    }

    Push-Location $ProjectDir
    try {
        if (Test-Path -LiteralPath (Join-Path $ProjectDir "package-lock.json")) {
            if ($ProductionOnly) {
                & $NodeExe $npmCli ci --omit=dev
            }
            else {
                & $NodeExe $npmCli ci
            }
        }
        else {
            if ($ProductionOnly) {
                & $NodeExe $npmCli install --omit=dev
            }
            else {
                & $NodeExe $npmCli install
            }
        }

        if ($LASTEXITCODE -ne 0) {
            throw "npm dependency setup failed in '$ProjectDir'."
        }
    }
    finally {
        Pop-Location
    }
}

function Write-RuntimeProfile {
    param([string]$Root)

    $profileDir = Join-Path $env:LOCALAPPDATA "Cursivis"
    $profilePath = Join-Path $profileDir "runtime-profile.json"
    $companionExe = Join-Path $Root "app\companion\Cursivis.Companion.exe"
    $hotkeyExe = Join-Path $Root "app\hotkey-host\Cursivis.HotkeyHost.exe"
    $backendDir = Join-Path $Root "backend\gemini-agent"
    $browserAgentDir = Join-Path $Root "desktop\browser-action-agent"
    $extensionBridgeDir = Join-Path $Root "desktop\browser-native-host"

    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
    $existingProfile = $null
    if (Test-Path -LiteralPath $profilePath) {
        try {
            $existingProfile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
        }
        catch {
            $existingProfile = $null
        }
    }

    function Get-ExistingProfileValue {
        param(
            [string]$Name,
            $DefaultValue
        )

        if ($null -ne $existingProfile) {
            $property = $existingProfile.PSObject.Properties[$Name]
            if ($null -ne $property -and $null -ne $property.Value) {
                if ($property.Value -is [string]) {
                    if (-not [string]::IsNullOrWhiteSpace($property.Value)) {
                        return $property.Value
                    }
                }
                else {
                    return $property.Value
                }
            }
        }

        return $DefaultValue
    }

    $profile = [ordered]@{
        backendDir = $backendDir
        browserAgentDir = $browserAgentDir
        extensionBridgeDir = $extensionBridgeDir
        companionProject = ""
        companionExecutable = $companionExe
        hotkeyHostExecutable = $hotkeyExe
        backendUrl = "http://127.0.0.1:8080"
        browserAgentUrl = "http://127.0.0.1:48820"
        extensionBridgeUrl = "http://127.0.0.1:48830"
        aiProvider = Get-ExistingProfileValue -Name "aiProvider" -DefaultValue "gemini"
        openAiBaseUrl = Get-ExistingProfileValue -Name "openAiBaseUrl" -DefaultValue "https://api.openai.com/v1"
        openAiApiKey = Get-ExistingProfileValue -Name "openAiApiKey" -DefaultValue ""
        openAiModel = Get-ExistingProfileValue -Name "openAiModel" -DefaultValue "gpt-4.1-mini"
        hostedApiUrl = Get-ExistingProfileValue -Name "hostedApiUrl" -DefaultValue ""
        hostedToken = Get-ExistingProfileValue -Name "hostedToken" -DefaultValue ""
        ollamaUrl = Get-ExistingProfileValue -Name "ollamaUrl" -DefaultValue "http://127.0.0.1:11434"
        localModel = Get-ExistingProfileValue -Name "localModel" -DefaultValue "granite3.2-vision:2b"
        apiKey = Get-ExistingProfileValue -Name "apiKey" -DefaultValue ""
        apiKeys = Get-ExistingProfileValue -Name "apiKeys" -DefaultValue ""
        enableStreamingTranscription = Get-ExistingProfileValue -Name "enableStreamingTranscription" -DefaultValue $false
        enableAutoReplace = Get-ExistingProfileValue -Name "enableAutoReplace" -DefaultValue $false
        autoReplaceConfidence = Get-ExistingProfileValue -Name "autoReplaceConfidence" -DefaultValue 0.9
        enableManagedBrowserFallback = Get-ExistingProfileValue -Name "enableManagedBrowserFallback" -DefaultValue $false
    }

    $profile | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $profilePath -Encoding UTF8
    return $profilePath
}

function Register-StartupShortcut {
    param(
        [string]$Target,
        [string]$Arguments
    )

    $startupDir = [Environment]::GetFolderPath("Startup")
    $shortcutPath = Join-Path $startupDir "Cursivis Companion.lnk"
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $Target
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = Split-Path -Parent $Target
    $shortcut.WindowStyle = 7
    $shortcut.Description = "Start Cursivis Companion in the background"
    $shortcut.Save()
    return $shortcutPath
}

function Register-HotkeyHostStartup {
    param([string]$Target)

    if (-not (Test-Path -LiteralPath $Target)) {
        return $null
    }

    $runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    New-Item -Path $runKeyPath -Force | Out-Null
    $launchCommand = "`"$Target`""
    New-ItemProperty -Path $runKeyPath -Name "CursivisHotkeyHost" -Value $launchCommand -PropertyType String -Force | Out-Null
    return $launchCommand
}

function Try-InstallLogitechPackage {
    param([string]$PackageRoot)

    $packagePath = Get-ChildItem -LiteralPath $PackageRoot -Filter "Cursivis_*.lplug4" -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($packagePath) -or -not (Test-Path -LiteralPath $packagePath)) {
        Write-Warning "Cursivis .lplug4 package was not found in this setup folder."
        return
    }

    Write-Step "Opening Logitech plugin package installer"
    Start-Process -FilePath $packagePath
}

$packageRoot = Resolve-PackageRoot
$payloadRoot = Join-Path $packageRoot "runtime"
$installRoot = $InstallDir.TrimEnd("\")

Write-Host "Cursivis Runtime Setup"
Write-Host "Install location: $installRoot"

Write-Step "Installing Cursivis runtime files"
Copy-RuntimePayload -Source $payloadRoot -Destination $installRoot

$nodeExe = Ensure-PortableNode -Destination $installRoot -Version $NodeVersion

Write-Step "Preparing backend dependencies"
Invoke-NpmCi -NodeExe $nodeExe -ProjectDir (Join-Path $installRoot "backend\gemini-agent") -ProductionOnly

Write-Step "Preparing browser action dependencies"
Invoke-NpmCi -NodeExe $nodeExe -ProjectDir (Join-Path $installRoot "desktop\browser-action-agent") -ProductionOnly

Write-Step "Writing Cursivis runtime profile"
$profilePath = Write-RuntimeProfile -Root $installRoot
Write-Host "Runtime profile: $profilePath"

$companionExe = Join-Path $installRoot "app\companion\Cursivis.Companion.exe"
if (-not (Test-Path -LiteralPath $companionExe)) {
    throw "Companion executable was not found after install: $companionExe"
}

if (-not $NoStartupShortcut) {
    Write-Step "Registering startup shortcut"
    $shortcutPath = Register-StartupShortcut -Target $companionExe -Arguments "--background"
    Write-Host "Startup shortcut: $shortcutPath"

    $hotkeyHostExe = Join-Path $installRoot "app\hotkey-host\Cursivis.HotkeyHost.exe"
    $hotkeyStartup = Register-HotkeyHostStartup -Target $hotkeyHostExe
    if ($null -ne $hotkeyStartup) {
        Write-Host "Hotkey host startup: $hotkeyStartup"
    }
}

if ($InstallLogitechPlugin) {
    Try-InstallLogitechPackage -PackageRoot $packageRoot
}

if (-not $NoLaunch) {
    Write-Step "Launching Cursivis Companion"
    Start-Process -FilePath $companionExe
}

Write-Host ""
Write-Host "Cursivis runtime setup complete."
Write-Host "Open Cursivis Settings, paste your Gemini API key, or choose Local LLM and Download & Use."
