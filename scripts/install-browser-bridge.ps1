param(
    [string]$ExtensionId,
    [string[]]$Browsers = @("Chrome", "Edge", "Brave", "Vivaldi", "Opera", "Chromium"),
    [switch]$Help
)

$ErrorActionPreference = "Stop"

if ($Help) {
    Write-Host "Usage:"
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\scripts\install-browser-bridge.ps1 -ExtensionId <EXTENSION_ID>"
    Write-Host "Registers the Cursivis native messaging host for common Chromium-family browsers."
    return
}

if ([string]::IsNullOrWhiteSpace($ExtensionId)) {
    throw "ExtensionId is required. Run with -Help for usage."
}

$root = Split-Path -Parent $PSScriptRoot
$extensionId = $ExtensionId.Trim()
if ($extensionId -cnotmatch '^[a-p]{32}$') {
    throw "ExtensionId does not look like a valid unpacked Chromium extension ID."
}

$hostLauncher = Join-Path $env:LOCALAPPDATA "Programs\Cursivis\app\browser-native-host\Cursivis.BrowserNativeHost.exe"
if (-not (Test-Path -LiteralPath $hostLauncher)) {
    $hostDir = Join-Path $root "desktop\cursivis-browser-native-host\src\Cursivis.BrowserNativeHost"
    dotnet build (Join-Path $hostDir "Cursivis.BrowserNativeHost.csproj") -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw "Building the development browser host failed." }
    $hostLauncher = Join-Path $hostDir "bin\Debug\net8.0-windows\Cursivis.BrowserNativeHost.exe"
}

$manifestDir = Join-Path $env:LOCALAPPDATA "Cursivis\browser-native-host"
New-Item -ItemType Directory -Force $manifestDir | Out-Null
$manifestPath = Join-Path $manifestDir "com.cursivis.browser_bridge.json"

$allowedOrigin = "chrome-extension://$extensionId/"
$origins = @($allowedOrigin)
if (Test-Path -LiteralPath $manifestPath) {
    try {
        $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($previous.name -eq 'com.cursivis.browser_bridge') {
            $origins += @($previous.allowed_origins | Where-Object { $_ -cmatch '^chrome-extension://[a-p]{32}/$' })
        }
    }
    catch { Write-Warning "Replacing unreadable browser registration." }
}
$manifest = @{
    name = "com.cursivis.browser_bridge"
    description = "Cursivis current-tab browser bridge"
    path = $hostLauncher
    type = "stdio"
    allowed_origins = @($origins | Sort-Object -Unique)
} | ConvertTo-Json -Depth 4

$staged = "$manifestPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText($staged, $manifest)
    Move-Item -LiteralPath $staged -Destination $manifestPath -Force
}
finally {
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged }
}

$registryTargets = @{
    Chrome   = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.cursivis.browser_bridge"
    Edge     = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.cursivis.browser_bridge"
    Brave    = "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.cursivis.browser_bridge"
    Vivaldi  = "HKCU:\Software\Vivaldi\NativeMessagingHosts\com.cursivis.browser_bridge"
    Opera    = "HKCU:\Software\Opera Software\NativeMessagingHosts\com.cursivis.browser_bridge"
    Chromium = "HKCU:\Software\Chromium\NativeMessagingHosts\com.cursivis.browser_bridge"
}

foreach ($browser in $Browsers) {
    $normalized = $browser.Trim()
    if (-not $registryTargets.ContainsKey($normalized)) {
        Write-Warning "Skipping unknown browser target '$browser'."
        continue
    }

    $keyPath = $registryTargets[$normalized]
    $regPath = $keyPath -replace '^HKCU:', 'HKCU'
    & reg.exe add $regPath /ve /t REG_SZ /d $manifestPath /f | Out-Null
    Write-Host "Registered native host for $normalized"
}

Write-Host ""
Write-Host "Done."
Write-Host "Manifest: $manifestPath"
Write-Host "Extension origin: $allowedOrigin"
Write-Host "Next: reload the browser extension or restart the browser."
