param(
    [switch]$WithBridge,
    [string]$ApiKey,
    [string[]]$ApiKeys,
    [string]$BackendUrl = "http://127.0.0.1:51880",
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
. (Join-Path $PSScriptRoot "local-ipc.ps1")
$backendDir = Join-Path $root "backend\gemini-agent"
$browserAgentDir = Join-Path $root "desktop\browser-action-agent"
$extensionBridgeDir = Join-Path $root "desktop\browser-native-host"
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
    apiKey = Protect-CursivisProfileValue $effectiveApiKey
    apiKeys = Protect-CursivisProfileValue $effectiveApiKeysJoined
    enableStreamingTranscription = [bool]$EnableStreamingTranscription
    enableAutoReplace = [bool]$EnableAutoReplace
    autoReplaceConfidence = $AutoReplaceConfidence
    enableManagedBrowserFallback = [bool]$EnableManagedBrowserFallback
}
($runtimeProfile | ConvertTo-Json -Depth 4) | Set-Content -Path $profilePath -Encoding UTF8

function Start-DemoNodeService {
    param([string]$Directory, [string]$EntryPoint, [string]$Scope, [hashtable]$Variables = @{})
    if (-not $SkipNpmInstall -and $Scope -ne "browser-bridge") {
        Push-Location -LiteralPath $Directory
        try {
            npm ci | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Dependency restore failed for $Scope." }
        }
        finally { Pop-Location }
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command node -CommandType Application -ErrorAction Stop).Source
    $start.Arguments = $EntryPoint
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = -not $ShowWindows
    $start.WindowStyle = if ($ShowWindows) { 'Normal' } else { 'Hidden' }
    $start.EnvironmentVariables['CURSIVIS_LOCAL_IPC_KEY'] = ConvertTo-CursivisHex (Get-CursivisServiceKey $Scope)
    foreach ($item in $Variables.GetEnumerator()) {
        $start.EnvironmentVariables[$item.Key] = $item.Value
    }
    return [Diagnostics.Process]::Start($start)
}

$backendProcess = Start-DemoNodeService $backendDir "src/server.js" "backend" @{
    PORT = ([Uri]$BackendUrl).Port.ToString()
    GOOGLE_API_KEY = $effectiveApiKey
    GOOGLE_API_KEYS = $effectiveApiKeysJoined
    GEMINI_MODEL = "gemini-3.6-flash"
    GEMINI_ROUTER_MODEL = "gemini-3.5-flash-lite"
    GEMINI_OPTIONS_MODEL = "gemini-3.5-flash-lite"
    GEMINI_FALLBACK_MODELS = "gemini-3.5-flash,gemini-3.5-flash-lite"
}
$browserAgentProcess = Start-DemoNodeService $browserAgentDir "src/server.js" "browser-agent" @{ CURSIVIS_BROWSER_CHANNEL = "chrome" }
$extensionBridgeProcess = Start-DemoNodeService $extensionBridgeDir "src/host.js" "browser-bridge" @{
    CURSIVIS_LOCAL_IPC_RELAY_KEY = ConvertTo-CursivisHex (Get-CursivisServiceKey "browser-relay")
}

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
            $health = Invoke-CursivisDiagnosticRequest -Uri ("http://127.0.0.1:" + ([Uri]$BackendUrl).Port + "/health") -Scope backend
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
            $browserHealth = Invoke-CursivisDiagnosticRequest -Uri "http://127.0.0.1:48820/health" -Scope browser-agent
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
            Invoke-CursivisDiagnosticRequest -Uri "http://127.0.0.1:48820/ensure-browser" -Scope browser-agent -Method POST -Body "{}" -TimeoutSec 12 | Out-Null
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
            $extensionBridgeHealth = Invoke-CursivisDiagnosticRequest -Uri "http://127.0.0.1:48830/health" -Scope browser-bridge
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
    Write-Host "Tip: close the demo windows to stop each component."
}
