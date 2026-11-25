param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidatePattern('^\d+_\d+_\d+$')]
    [string]$Version = "1_5_4",
    [switch]$SkipBuild,
    [switch]$InstallPackage,
    [switch]$CandidateOnly,
    [string]$LogiPluginToolPath,
    [string]$LogiPluginInstallerPath
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
$artifactDir = Join-Path $root "artifacts\logitech-marketplace"
$artifactPath = Join-Path $artifactDir "Cursivis_$Version.lplug4"
$packagePath = if ($CandidateOnly) { $artifactPath } else { Join-Path $distDir "Cursivis.lplug4" }
$pluginApiPath = Join-Path ${env:ProgramFiles} "Logi\LogiPluginService\PluginApi.dll"
$pluginLinkPath = Join-Path $env:LOCALAPPDATA "Logi\LogiPluginService\Plugins\CursivisPlugin.link"
$pluginLogPath = Join-Path $env:LOCALAPPDATA "Logi\LogiPluginService\Logs\plugin_logs\Cursivis.log"
$toolPath = $LogiPluginToolPath
if ([string]::IsNullOrWhiteSpace($toolPath)) {
    $toolPath = (Get-Command logiplugintool.exe -CommandType Application -ErrorAction SilentlyContinue).Source
    if (-not $toolPath) {
        $toolPath = Join-Path $env:USERPROFILE ".dotnet\tools\logiplugintool.exe"
    }
}

function Invoke-LogiPluginTool {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [string]$ExpectedLine,
        [string]$Executable = $toolPath
    )

    $output = @(& $Executable @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    $lines = @($output | ForEach-Object { $_.ToString().Trim() })

    # Some Logitech tool failures are printed even though the process returns zero.
    if ($exitCode -ne 0 -or ($lines -match '^(ERROR:|Operation failed\b)') -or
        ($ExpectedLine -and $lines -notcontains $ExpectedLine)) {
        throw "LogiPluginTool $($Arguments[0]) did not report success (exit $exitCode). For installation, use a tool compatible with the installed Logi Plugin Service."
    }
}

function Assert-UniversalPluginContract {
    $pluginSource = Get-Content -LiteralPath (Join-Path $pluginRoot "src\CursivisPlugin.cs") -Raw
    $applicationSource = Get-Content -LiteralPath (Join-Path $pluginRoot "src\CursivisApplication.cs") -Raw
    $metadataPath = Join-Path $pluginRoot "src\package\metadata\LoupedeckPackage.yaml"
    $metadata = Get-Content -LiteralPath $metadataPath -Raw
    $expectedVersion = $Version.Replace("_", ".")

    if ($pluginSource -notmatch 'UsesApplicationApiOnly\s*=>\s*true\s*;' -or
        $pluginSource -notmatch 'HasNoApplication\s*=>\s*true\s*;') {
        throw "Cursivis must remain an API-only universal Logitech plugin."
    }

    if ($applicationSource -notmatch 'GetProcessName\(\)\s*=>\s*""\s*;' -or
        $applicationSource -notmatch 'GetApplicationStatus\(\)\s*=>\s*ClientApplicationStatus\.Unknown\s*;') {
        throw "The universal plugin ClientApplication must remain unbound to any foreground process."
    }

    if ($metadata -match '(?m)^\s*-\s*HasApplication\s*$') {
        throw "Universal plugin metadata must not include HasApplication."
    }

    if ($metadata -notmatch '(?m)^\s*-\s*HasHapticsMapping\s*$') {
        throw "Required HasHapticsMapping metadata is missing."
    }

    if ($metadata -notmatch "(?m)^version:\s*$([regex]::Escape($expectedVersion))\s*$") {
        throw "Plugin metadata version must match package version $expectedVersion."
    }
}

function Assert-ForbiddenFilesAbsentFromDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    $matches = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ieq "PluginApi.dll" -or $_.Extension -ieq ".pdb" })

    if ($matches.Count -gt 0) {
        $paths = $matches.FullName -join [Environment]::NewLine
        throw "PluginApi.dll and debug symbols must not be included in plugin build output. Found:$([Environment]::NewLine)$paths"
    }
}

function Assert-ForbiddenFilesAbsentFromPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Package
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
    try {
        $matches = @($archive.Entries | Where-Object {
            $fileName = [System.IO.Path]::GetFileName($_.FullName)
            $fileName -ieq "PluginApi.dll" -or $fileName.EndsWith(".pdb", [System.StringComparison]::OrdinalIgnoreCase)
        })

        if ($matches.Count -gt 0) {
            $entries = $matches.FullName -join [Environment]::NewLine
            throw "PluginApi.dll and debug symbols must not be included in the .lplug4 package. Found:$([Environment]::NewLine)$entries"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-InternalActionsAbsentFromPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Package
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
    try {
        $matches = @($archive.Entries | Where-Object {
            $_.FullName -match 'CursivisDialAdjustment|CursivisLongPressStartCommand|CursivisLongPressEndCommand'
        })

        if ($matches.Count -gt 0) {
            $entries = $matches.FullName -join [Environment]::NewLine
            throw "Internal Logitech actions must not be user-selectable. Found:$([Environment]::NewLine)$entries"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-PackagedHapticsCapability {
    param([Parameter(Mandatory = $true)][string]$Package)

    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
    try {
        $entry = $archive.GetEntry("metadata/LoupedeckPackage.yaml")
        if (-not $entry) { throw "The package is missing LoupedeckPackage.yaml." }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($metadata -notmatch '(?m)^\s*-\s*HasHapticsMapping\s*$' -or
            $metadata -match '(?m)^\s*-\s*HasHapticMapping\s*$') {
            throw "Packaged metadata did not preserve HasHapticsMapping without the legacy HasHapticMapping alias. Select a compatible packer with -LogiPluginToolPath and, if needed, a separate installer with -LogiPluginInstallerPath."
        }
    }
    finally { $archive.Dispose() }
}

Write-Host "Preparing Logitech plugin build..."
Write-Host "Plugin project: $pluginProject"

Assert-UniversalPluginContract

if (-not (Test-Path $pluginApiPath)) {
    throw "Logi Plugin Service SDK runtime was not found at '$pluginApiPath'. Install Logi Options+ before building the real plugin."
}

if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
    throw "LogiPluginTool was not found at '$toolPath'. Install the Logitech plugin tooling first."
}
$toolPath = (Resolve-Path -LiteralPath $toolPath).Path
Write-Host "LogiPluginTool: $toolPath"
$installerPath = $toolPath
if ($InstallPackage -and $LogiPluginInstallerPath) {
    if (-not (Test-Path -LiteralPath $LogiPluginInstallerPath -PathType Leaf)) {
        throw "LogiPluginTool installer was not found at '$LogiPluginInstallerPath'."
    }
    $installerPath = (Resolve-Path -LiteralPath $LogiPluginInstallerPath).Path
}

if (-not $SkipBuild) {
    if (Test-Path $buildOutputDir) {
        $resolvedBuildOutput = [System.IO.Path]::GetFullPath($buildOutputDir)
        $resolvedPluginRoot = [System.IO.Path]::GetFullPath($pluginRoot)
        if (-not $resolvedBuildOutput.StartsWith($resolvedPluginRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean build output outside the plugin workspace: '$resolvedBuildOutput'."
        }

        Remove-Item -LiteralPath $resolvedBuildOutput -Recurse -Force
    }

    Write-Host "Building Cursivis Logitech plugin ($Configuration)..."
    dotnet build $pluginProject -c $Configuration "-p:SkipLocalPluginRegistration=$([bool]$CandidateOnly)"
    if ($LASTEXITCODE -ne 0) {
        throw "Cursivis Logitech plugin build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $buildOutputDir)) {
    throw "Build output directory '$buildOutputDir' was not found."
}

Assert-ForbiddenFilesAbsentFromDirectory -Directory $buildOutputDir

New-Item -ItemType Directory -Force -Path $distDir, $artifactDir | Out-Null
if (Test-Path $packagePath) {
    Remove-Item -Force $packagePath
}

$packInput = Resolve-ShortPath -Path $buildOutputDir
$packOutput = Join-Path (Resolve-ShortPath -Path (Split-Path -Parent $packagePath)) (Split-Path -Leaf $packagePath)

Write-Host "Packing plugin from $buildOutputDir"
Invoke-LogiPluginTool -Arguments @("pack", $packInput, $packOutput)

Write-Host "Verifying plugin package..."
Invoke-LogiPluginTool -Arguments @("verify", $packOutput)

Assert-ForbiddenFilesAbsentFromPackage -Package $packOutput
Assert-InternalActionsAbsentFromPackage -Package $packOutput
Assert-PackagedHapticsCapability -Package $packOutput
Write-Host "Confirmed: PluginApi.dll and debug symbols are absent from the final package."

if (-not $CandidateOnly) {
    Copy-Item -LiteralPath $packOutput -Destination $artifactPath -Force
}
Assert-ForbiddenFilesAbsentFromPackage -Package $artifactPath
Assert-InternalActionsAbsentFromPackage -Package $artifactPath
Write-Host "Versioned marketplace artifact: $artifactPath"

if ($InstallPackage) {
    Write-Host "Installing package into Logi Plugin Service using $installerPath"
    Invoke-LogiPluginTool -Arguments @("install", $packOutput) -ExpectedLine "Plugin was successfully installed" -Executable $installerPath
}

Write-Host ""
Write-Host "Logitech plugin workflow complete."
Write-Host "Package: $packagePath"
Write-Host "Versioned package: $artifactPath"

if (Test-Path $pluginLinkPath) {
    Write-Host "Debug link: $pluginLinkPath"
}

if (Test-Path $pluginLogPath) {
    Write-Host "Plugin log: $pluginLogPath"
    Write-Host "Recent plugin log lines:"
    Get-Content -Tail 12 $pluginLogPath | Out-Host
}
