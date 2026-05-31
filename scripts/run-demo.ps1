param(
    [switch]$WithBridge,
    [string]$ApiKey,
    [string[]]$ApiKeys,
    [string]$BackendUrl = "http://127.0.0.1:8080",
    [switch]$EnableStreamingTranscription,
    [switch]$EnableAutoReplace,
    [double]$AutoReplaceConfidence = 0.90,
    [switch]$EnableManagedBrowserFallback,
    [switch]$WarmManagedBrowser,
    [switch]$ShowWindows,
    [switch]$SkipNpmInstall,
    [switch]$SkipCleanup,
    [switch]$NoHealthCheck,
    [switch]$Help
)

$ErrorActionPreference = "Stop"

if ($Help) {
    Write-Host "Usage:"
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\scripts\run-demo.ps1 [-WithBridge] [-ApiKey <KEY>] [-ApiKeys <KEY1,KEY2,...>] [-BackendUrl <URL>] [-EnableStreamingTranscription] [-EnableAutoReplace] [-AutoReplaceConfidence <0-1>] [-EnableManagedBrowserFallback] [-WarmManagedBrowser] [-ShowWindows] [-SkipNpmInstall] [-SkipCleanup] [-NoHealthCheck]"
    return
}

$root = Split-Path -Parent $PSScriptRoot
$backendDir = Join-Path $root "backend\gemini-agent"
$browserAgentDir = Join-Path $root "desktop\browser-action-agent"
$extensionBridgeDir = Join-Path $root "desktop\browser-native-host"
$extensionBridgeLauncher = Join-Path $extensionBridgeDir "launch.cmd"
$companionProject = Join-Path $root "desktop\cursivis-companion\src\Cursivis.Companion\Cursivis.Companion.csproj"
$hotkeyHostProject = Join-Path $root "desktop\cursivis-hotkey-host\src\Cursivis.HotkeyHost\Cursivis.HotkeyHost.csproj"
$bridgeProject = Join-Path $root "plugin\logitech-plugin\src\Cursivis.Logitech.Bridge\Cursivis.Logitech.Bridge.csproj"
$companionProjectDir = Split-Path -Parent $companionProject
$companionExecutable = Join-Path $companionProjectDir "bin\Debug\net8.0-windows\Cursivis.Companion.exe"
$hotkeyHostProjectDir = Split-Path -Parent $hotkeyHostProject
$hotkeyHostExecutable = Join-Path $hotkeyHostProjectDir "bin\Debug\net8.0-windows\Cursivis.HotkeyHost.exe"
$profileDir = Join-Path $env:LOCALAPPDATA "Cursivis"
$profilePath = Join-Path $profileDir "runtime-profile.json"

function Expand-KeyInputs {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Values
    )

    $expanded = New-Object System.Collections.Generic.List[string]
    foreach ($value in @($Values)) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        foreach ($part in ($value -split "[,;`r`n]+")) {
            $trimmed = "$part".Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                $expanded.Add($trimmed)
            }
        }
    }

    return $expanded
}

Write-Host "Starting Cursivis demo stack..."
Write-Host "Backend: $backendDir"
Write-Host "Browser action agent: $browserAgentDir"
Write-Host "Extension bridge host: $extensionBridgeDir"
Write-Host "Companion project: $companionProject"

if (-not $SkipCleanup) {
    try {
        Write-Host "Running pre-launch cleanup..."
        & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "stop-demo.ps1") | Out-Host
    }
    catch {
        Write-Warning "Cleanup step failed: $($_.Exception.Message)"
    }
}

$embeddedRotationKeys = @()

$rotationKeyCandidates = New-Object System.Collections.Generic.List[string]

foreach ($candidate in (Expand-KeyInputs @($ApiKey))) { $rotationKeyCandidates.Add($candidate) }
foreach ($candidate in (Expand-KeyInputs @($env:GOOGLE_API_KEY))) { $rotationKeyCandidates.Add($candidate) }
foreach ($candidate in (Expand-KeyInputs @($ApiKeys))) { $rotationKeyCandidates.Add($candidate) }
foreach ($candidate in (Expand-KeyInputs @($env:GOOGLE_API_KEYS))) { $rotationKeyCandidates.Add($candidate) }
foreach ($candidate in (Expand-KeyInputs $embeddedRotationKeys)) { $rotationKeyCandidates.Add($candidate) }

$rotationKeys = @(
    $rotationKeyCandidates |
        ForEach-Object { "$_".Trim() } |
        Where-Object { $_ } |
        Select-Object -Unique
)

if ($rotationKeys.Count -eq 0) {
    Write-Warning "GOOGLE_API_KEY is not set in this terminal. Backend Gemini calls will fail."
}

$effectiveApiKey = if ($rotationKeys.Count -gt 0) { $rotationKeys[0] } else { "" }
$effectiveApiKeysJoined = ($rotationKeys -join ",")

if ($rotationKeys.Count -gt 0) {
    $env:GOOGLE_API_KEY = $effectiveApiKey
    $env:GOOGLE_API_KEYS = $effectiveApiKeysJoined
}

New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
$runtimeProfile = [ordered]@{
    backendDir = $backendDir
    browserAgentDir = $browserAgentDir
    extensionBridgeDir = $extensionBridgeDir
    companionProject = $companionProject
    companionExecutable = $companionExecutable
    hotkeyHostExecutable = $hotkeyHostExecutable
    backendUrl = $BackendUrl
    browserAgentUrl = "http://127.0.0.1:48820"
    extensionBridgeUrl = "http://127.0.0.1:48830"
    apiKey = $effectiveApiKey
    apiKeys = $effectiveApiKeysJoined
    enableStreamingTranscription = [bool]$EnableStreamingTranscription
    enableAutoReplace = [bool]$EnableAutoReplace
    autoReplaceConfidence = $AutoReplaceConfidence
    enableManagedBrowserFallback = [bool]$EnableManagedBrowserFallback
}
($runtimeProfile | ConvertTo-Json -Depth 4) | Set-Content -Path $profilePath -Encoding UTF8

$apiKeyEscaped = $effectiveApiKey.Replace("'", "''")
$apiKeysEscaped = $effectiveApiKeysJoined.Replace("'", "''")
$backendCmdParts = @(
    "`$env:GOOGLE_API_KEY='$apiKeyEscaped'",
    "`$env:GOOGLE_API_KEYS='$apiKeysEscaped'",
    "`$env:GEMINI_ROUTER_MODEL='gemini-2.5-flash-lite'",
    "`$env:GEMINI_OPTIONS_MODEL='gemini-2.5-flash-lite'",
    "`$env:GEMINI_FALLBACK_MODELS='gemini-2.5-flash-lite,gemini-2.0-flash'",
    "Set-Location -LiteralPath '$backendDir'"
)

if (-not $SkipNpmInstall) {
    $backendCmdParts += "npm install"
}

$backendCmdParts += "npm start"
$backendCmd = $backendCmdParts -join "; "
$backendProcess = Start-Process powershell -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $backendCmd -PassThru -WindowStyle $(if ($ShowWindows) { "Normal" } else { "Hidden" })

$browserAgentCmdParts = @(
    "`$env:CURSIVIS_BROWSER_CHANNEL='chrome'",
    "Set-Location -LiteralPath '$browserAgentDir'"
)

if (-not $SkipNpmInstall) {
    $browserAgentCmdParts += "npm install"
}

$browserAgentCmdParts += "npm start"
$browserAgentCmd = $browserAgentCmdParts -join "; "
$browserAgentProcess = Start-Process powershell -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $browserAgentCmd -PassThru -WindowStyle $(if ($ShowWindows) { "Normal" } else { "Hidden" })

$extensionBridgeCmd = "Set-Location -LiteralPath '$extensionBridgeDir'; .\launch.cmd"
$extensionBridgeProcess = Start-Process powershell -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $extensionBridgeCmd -PassThru -WindowStyle $(if ($ShowWindows) { "Normal" } else { "Hidden" })

Start-Sleep -Seconds 2

$streamingValue = if ($EnableStreamingTranscription) { "true" } else { "false" }
$autoReplaceConfidenceInvariant = $AutoReplaceConfidence.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$managedBrowserFallbackValue = if ($EnableManagedBrowserFallback) { "true" } else { "false" }

$companionCmdParts = @(
    "`$env:CURSIVIS_BACKEND_URL='$($BackendUrl.Replace("'", "''"))'",
    "`$env:CURSIVIS_ENABLE_STREAMING_TRANSCRIPTION='$streamingValue'",
    "`$env:CURSIVIS_ENABLE_MANAGED_BROWSER_FALLBACK='$managedBrowserFallbackValue'"
)

if ($EnableAutoReplace) {
    $companionCmdParts += "`$env:CURSIVIS_ENABLE_AUTO_REPLACE='true'"
    $companionCmdParts += "`$env:CURSIVIS_AUTO_REPLACE_CONFIDENCE='$autoReplaceConfidenceInvariant'"
}

$companionCmdParts += "dotnet run --project '$companionProject' -- --background"
$companionCmd = $companionCmdParts -join "; "

$companionProcess = Start-Process powershell -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $companionCmd -PassThru -WindowStyle $(if ($ShowWindows) { "Normal" } else { "Hidden" })

if ($WithBridge) {
    Start-Sleep -Seconds 1
    $bridgeProcess = Start-Process powershell -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "dotnet run --project '$bridgeProject'" -PassThru -WindowStyle $(if ($ShowWindows) { "Normal" } else { "Hidden" })
    Write-Host "Bridge PID: $($bridgeProcess.Id)"
}

if (-not $NoHealthCheck) {
    $healthOk = $false
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        try {
            $health = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:8080/health" -TimeoutSec 4
            if ($health.StatusCode -eq 200) {
                $healthOk = $true
                Write-Host "Backend health: OK"
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 700
        }
    }

    if (-not $healthOk) {
        Write-Warning "Backend health check did not return 200 yet. Check backend terminal output."
    }

    $browserHealthOk = $false
    $browserDeadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $browserDeadline) {
        try {
            $browserHealth = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:48820/health" -TimeoutSec 4
            if ($browserHealth.StatusCode -eq 200) {
                $browserHealthOk = $true
                Write-Host "Browser action agent health: OK"
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    if (-not $browserHealthOk) {
        Write-Warning "Browser action agent health check did not return 200 yet. Check browser action terminal output."
    }
    elseif ($WarmManagedBrowser -and $EnableManagedBrowserFallback) {
        try {
            Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:48820/ensure-browser" -Method Post -ContentType "application/json" -Body "{}" -TimeoutSec 12 | Out-Null
            Write-Host "Managed action browser session: ready"
        }
        catch {
            Write-Warning "Could not warm the managed action browser session yet. It will initialize on first Take Action use."
        }
    }
    elseif ($WarmManagedBrowser) {
        Write-Host "Managed action browser warm-up skipped because current-browser-only mode is enabled"
    }
    else {
        Write-Host "Managed action browser session: disabled by default and deferred until opt-in fallback use"
    }

    $extensionBridgeHealthOk = $false
    $extensionBridgeDeadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $extensionBridgeDeadline) {
        try {
            $extensionBridgeHealth = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:48830/health" -TimeoutSec 4
            if ($extensionBridgeHealth.StatusCode -eq 200) {
                $extensionBridgeHealthOk = $true
                Write-Host "Extension bridge host health: OK"
                break
            }
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }

    if (-not $extensionBridgeHealthOk) {
        Write-Warning "Extension bridge host health check did not return 200 yet. Check extension bridge terminal output."
    }
}

if ($effectiveApiKey) {
    Write-Host "Launched with API key rotation pool injected into backend process. Keys loaded: $($rotationKeys.Count)"
}
else {
    Write-Host "Launched. Make sure GOOGLE_API_KEY is set for backend terminal."
}
Write-Host "Backend PID: $($backendProcess.Id)"
Write-Host "Browser action agent PID: $($browserAgentProcess.Id)"
Write-Host "Extension bridge host PID: $($extensionBridgeProcess.Id)"
Write-Host "Companion PID: $($companionProcess.Id)"
Write-Host "Runtime profile saved: $profilePath"
if ($ShowWindows) {
    Write-Host "Tip: close the spawned PowerShell windows to stop each component."
}
