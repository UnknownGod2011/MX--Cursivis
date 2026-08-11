param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Version = "1_5_2",
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
$artifactDir = Join-Path $root "artifacts\logitech-marketplace"
$artifactPath = Join-Path $artifactDir "Cursivis_$Version.lplug4"
$pluginApiPath = Join-Path ${env:ProgramFiles} "Logi\LogiPluginService\PluginApi.dll"
$pluginLinkPath = Join-Path $env:LOCALAPPDATA "Logi\LogiPluginService\Plugins\CursivisPlugin.link"
$pluginLogPath = Join-Path $env:LOCALAPPDATA "Logi\LogiPluginService\Logs\plugin_logs\Cursivis.log"
$toolPath = Join-Path $env:USERPROFILE ".dotnet\tools\logiplugintool.exe"

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

Write-Host "Preparing Logitech plugin build..."
Write-Host "Plugin project: $pluginProject"

if (-not (Test-Path $pluginApiPath)) {
    throw "Logi Plugin Service SDK runtime was not found at '$pluginApiPath'. Install Logi Options+ before building the real plugin."
}

if (-not (Test-Path $toolPath)) {
    throw "LogiPluginTool was not found at '$toolPath'. Install the Logitech plugin tooling first."
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
    dotnet build $pluginProject -c $Configuration
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
$packOutput = Join-Path (Resolve-ShortPath -Path $distDir) "Cursivis.lplug4"

Write-Host "Packing plugin from $buildOutputDir"
& $toolPath pack $packInput $packOutput
if ($LASTEXITCODE -ne 0) {
    throw "LogiPluginTool pack failed with exit code $LASTEXITCODE."
}

Write-Host "Verifying plugin package..."
& $toolPath verify $packOutput
if ($LASTEXITCODE -ne 0) {
    throw "LogiPluginTool verification failed with exit code $LASTEXITCODE."
}

Assert-ForbiddenFilesAbsentFromPackage -Package $packOutput
Assert-InternalActionsAbsentFromPackage -Package $packOutput
Write-Host "Confirmed: PluginApi.dll and debug symbols are absent from the final package."

Copy-Item -LiteralPath $packOutput -Destination $artifactPath -Force
Assert-ForbiddenFilesAbsentFromPackage -Package $artifactPath
Assert-InternalActionsAbsentFromPackage -Package $artifactPath
Write-Host "Versioned marketplace artifact: $artifactPath"

if ($InstallPackage) {
    Write-Host "Installing package into Logi Plugin Service..."
    & $toolPath install $packOutput
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
