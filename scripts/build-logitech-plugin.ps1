param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$InstallPackage
)

$ErrorActionPreference = "Stop"

function Resolve-ShortPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    $escaped = $resolved.Path.Replace('"', '""')
    $short = cmd /c "for %I in (""$escaped"") do @echo %~sI"

    if ([string]::IsNullOrWhiteSpace($short)) {
        return $resolved.Path
    }

    return $short.Trim()
}

$root = Split-Path -Parent $PSScriptRoot
$pluginRoot = Join-Path $root "plugin\logitech-plugin\src\CursivisPlugin"
$pluginProject = Join-Path $pluginRoot "src\CursivisPlugin.csproj"
$buildOutputDir = Join-Path $pluginRoot "bin\$Configuration"
$distDir = Join-Path $root "plugin\logitech-plugin\dist"
$packagePath = Join-Path $distDir "Cursivis.lplug4"
$pluginApiPath = Join-Path ${env:ProgramFiles} "Logi\LogiPluginService\PluginApi.dll"
$pluginLinkPath = Join-Path $env:LOCALAPPDATA "Logi\LogiPluginService\Plugins\CursivisPlugin.link"
$pluginLogPath = Join-Path $env:LOCALAPPDATA "Logi\LogiPluginService\Logs\plugin_logs\Cursivis.log"
$toolPath = Join-Path $env:USERPROFILE ".dotnet\tools\logiplugintool.exe"

Write-Host "Preparing Logitech plugin build..."
Write-Host "Plugin project: $pluginProject"

if (-not (Test-Path $pluginApiPath)) {
    throw "Logi Plugin Service SDK runtime was not found at '$pluginApiPath'. Install Logi Options+ before building the real plugin."
}

if (-not (Test-Path $toolPath)) {
    throw "LogiPluginTool was not found at '$toolPath'. Install the Logitech plugin tooling first."
}

if (-not $SkipBuild) {
    Write-Host "Building Cursivis Logitech plugin ($Configuration)..."
    dotnet build $pluginProject -c $Configuration
}

if (-not (Test-Path $buildOutputDir)) {
    throw "Build output directory '$buildOutputDir' was not found."
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $packagePath) {
    Remove-Item -Force $packagePath
}

$packInput = Resolve-ShortPath -Path $buildOutputDir
$packOutput = Join-Path (Resolve-ShortPath -Path $distDir) "Cursivis.lplug4"

Write-Host "Packing plugin from $buildOutputDir"
& $toolPath pack $packInput $packOutput

Write-Host "Verifying plugin package..."
& $toolPath verify $packOutput

if ($InstallPackage) {
    Write-Host "Installing package into Logi Plugin Service..."
    & $toolPath install $packOutput
}

Write-Host ""
Write-Host "Logitech plugin workflow complete."
Write-Host "Package: $packagePath"

if (Test-Path $pluginLinkPath) {
    Write-Host "Debug link: $pluginLinkPath"
}

if (Test-Path $pluginLogPath) {
    Write-Host "Plugin log: $pluginLogPath"
    Write-Host "Recent plugin log lines:"
    Get-Content -Tail 12 $pluginLogPath | Out-Host
}
