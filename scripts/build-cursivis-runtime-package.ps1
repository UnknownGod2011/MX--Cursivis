param(
    [string]$Version = "1_5_3",
    [string]$NodeVersion = "v22.22.0",
    [string]$NodeSourceDirectory = "",
    [switch]$SkipDotnetPublish,
    [switch]$SkipZip,
    [switch]$SkipPluginPackage,
    [switch]$IncludePluginPackage
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root "artifacts\cursivis-runtime"
$packageRoot = Join-Path $artifactRoot "CursivisRuntime_$Version"
$runtimeRoot = Join-Path $packageRoot "runtime"
$zipPath = Join-Path $artifactRoot "CursivisRuntime_$Version.zip"

$companionProject = Join-Path $root "desktop\cursivis-companion\src\Cursivis.Companion\Cursivis.Companion.csproj"
$hotkeyProject = Join-Path $root "desktop\cursivis-hotkey-host\src\Cursivis.HotkeyHost\Cursivis.HotkeyHost.csproj"
$triggerProject = Join-Path $root "desktop\cursivis-trigger-launcher\src\Cursivis.TriggerLauncher\Cursivis.TriggerLauncher.csproj"
$backendDir = Join-Path $root "backend\gemini-agent"
$browserAgentDir = Join-Path $root "desktop\browser-action-agent"
$browserExtensionDir = Join-Path $root "desktop\browser-extension-chromium"
$extensionBridgeDir = Join-Path $root "desktop\browser-native-host"
$sharedDir = Join-Path $root "shared"
$docsDir = Join-Path $root "docs"
$pluginPackage = Join-Path $root "artifacts\logitech-marketplace\Cursivis_$Version.lplug4"

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
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
            throw "Refusing to include a Logitech plugin package that contains PluginApi.dll or debug symbols."
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Copy-CleanDirectory {
    param(
        [string]$Source,
        [string]$Destination,
        [string[]]$Exclude = @()
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Source directory was not found: $Source"
    }

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        if ($Exclude -contains $_.Name) {
            return
        }

        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-NodeVersion {
    param([Parameter(Mandatory = $true)][string]$NodeExe)

    if (-not (Test-Path -LiteralPath $NodeExe)) {
        return $null
    }

    try {
        return ((& $NodeExe --version 2>$null | Select-Object -First 1).Trim())
    }
    catch {
        return $null
    }
}

function Test-NodeHasNpm {
    param([Parameter(Mandatory = $true)][string]$NodeExe)

    $npmCli = Join-Path (Split-Path -Parent $NodeExe) "node_modules\npm\bin\npm-cli.js"
    return Test-Path -LiteralPath $npmCli
}

function Resolve-PortableNodeSource {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$PreferredDirectory
    )

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($PreferredDirectory)) {
        $candidates += $PreferredDirectory
    }

    $candidates += Join-Path $env:LOCALAPPDATA "Programs\Cursivis\node"
    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    if ($nodeCommand) {
        $candidates += Split-Path -Parent $nodeCommand.Source
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $nodeExe = Join-Path $candidate "node.exe"
        if ((Get-NodeVersion -NodeExe $nodeExe) -eq $Version -and (Test-NodeHasNpm -NodeExe $nodeExe)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $cacheRoot = Join-Path $artifactRoot "node-cache"
    $archiveName = "node-$Version-win-x64.zip"
    $archivePath = Join-Path $cacheRoot $archiveName
    $baseUrl = "https://nodejs.org/dist/$Version"
    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Host "Downloading build-time portable Node.js $Version..."
        Invoke-WebRequest -Uri "$baseUrl/$archiveName" -OutFile $archivePath
    }

    $checksums = (Invoke-WebRequest -Uri "$baseUrl/SHASUMS256.txt" -UseBasicParsing).Content
    $expectedLine = $checksums -split "`n" | Where-Object { $_ -match "\s$([regex]::Escape($archiveName))$" } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($expectedLine)) {
        throw "Could not find an official checksum for $archiveName."
    }

    $expectedHash = ($expectedLine -split "\s+")[0].Trim()
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash.ToLowerInvariant()) {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        throw "The build-time Node.js archive did not match Node.js's published SHA-256 checksum."
    }

    $expanded = Join-Path $cacheRoot "node-$Version-win-x64"
    if (Test-Path -LiteralPath $expanded) {
        Remove-Item -LiteralPath $expanded -Recurse -Force
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $cacheRoot -Force
    $expandedNode = Join-Path $expanded "node.exe"
    if ((Get-NodeVersion -NodeExe $expandedNode) -ne $Version -or -not (Test-NodeHasNpm -NodeExe $expandedNode)) {
        throw "The extracted Node.js runtime did not provide the expected Node.js version and npm tooling."
    }

    return $expanded
}

function Install-ProductionDependencies {
    param(
        [Parameter(Mandatory = $true)][string]$NodeExe,
        [Parameter(Mandatory = $true)][string]$ProjectDirectory
    )

    if (-not (Test-Path -LiteralPath (Join-Path $ProjectDirectory "package.json"))) {
        return
    }

    $npmCli = Join-Path (Split-Path -Parent $NodeExe) "node_modules\npm\bin\npm-cli.js"
    if (-not (Test-Path -LiteralPath $npmCli)) {
        throw "The bundled Node.js runtime does not contain npm."
    }

    $originalPath = $env:Path
    try {
        # npm lifecycle scripts invoke `node` by name. Keep the bundled runtime first in PATH.
        $env:Path = "$(Split-Path -Parent $NodeExe);$originalPath"
        Push-Location $ProjectDirectory
        & $NodeExe $npmCli ci --omit=dev --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci failed while preparing production dependencies for '$ProjectDirectory'."
        }
    }
    finally {
        Pop-Location -ErrorAction SilentlyContinue
        $env:Path = $originalPath
    }
}

function Remove-BuildTimeNodeTooling {
    param([Parameter(Mandatory = $true)][string]$NodeDirectory)

    foreach ($name in @(
        "node_modules",
        "corepack", "corepack.cmd",
        "npm", "npm.cmd", "npm.ps1",
        "npx", "npx.cmd", "npx.ps1",
        "install_tools.bat", "nodevars.bat"
    )) {
        $path = Join-Path $NodeDirectory $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

function Assert-ReleaseRuntimeContents {
    param([Parameter(Mandatory = $true)][string]$RuntimeDirectory)

    $required = @(
        "app\companion\Cursivis.Companion.exe",
        "app\hotkey-host\Cursivis.HotkeyHost.exe",
        "app\trigger-launcher\Cursivis.TriggerLauncher.exe",
        "node\node.exe",
        "backend\gemini-agent\src\server.js",
        "backend\gemini-agent\node_modules",
        "desktop\browser-action-agent\src\server.js",
        "desktop\browser-action-agent\node_modules",
        "desktop\browser-native-host\src\host.js"
    )

    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $RuntimeDirectory $_)) })
    if ($missing.Count -gt 0) {
        throw "The self-contained runtime is incomplete. Missing: $($missing -join ', ')"
    }

    $forbidden = @(
        Get-ChildItem -LiteralPath $RuntimeDirectory -File -Recurse |
            Where-Object {
                $_.Name -ieq "PluginApi.dll" -or
                $_.Extension -ieq ".pdb" -or
                $_.Name -ieq ".env" -or
                $_.Name -match '^\.env\.'
            }
    )
    if ($forbidden.Count -gt 0) {
        throw "Refusing to package forbidden runtime files: $($forbidden.FullName -join ', ')"
    }
}

Write-Host "Building Cursivis runtime package..."
Write-Host "Output: $packageRoot"

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

if (-not $SkipDotnetPublish) {
    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "publish", $companionProject, "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-p:PublishSingleFile=true",
        "-p:EnableCompressionInSingleFile=true", "-p:DebugType=None",
        "-p:DebugSymbols=false", "-o", (Join-Path $runtimeRoot "app\companion"))
    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "publish", $hotkeyProject, "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-p:PublishSingleFile=true",
        "-p:EnableCompressionInSingleFile=true", "-p:DebugType=None",
        "-p:DebugSymbols=false", "-o", (Join-Path $runtimeRoot "app\hotkey-host"))
    Invoke-NativeCommand -FilePath "dotnet" -Arguments @(
        "publish", $triggerProject, "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-p:PublishSingleFile=true",
        "-p:EnableCompressionInSingleFile=true", "-p:DebugType=None",
        "-p:DebugSymbols=false", "-o", (Join-Path $runtimeRoot "app\trigger-launcher"))
}

Copy-CleanDirectory -Source $backendDir -Destination (Join-Path $runtimeRoot "backend\gemini-agent") -Exclude @(
    "node_modules",
    "tests",
    "infra",
    ".env.example",
    "Dockerfile",
    "README.md"
)
Copy-CleanDirectory -Source $browserAgentDir -Destination (Join-Path $runtimeRoot "desktop\browser-action-agent") -Exclude @(
    "node_modules",
    "README.md"
)
Copy-CleanDirectory -Source $browserExtensionDir -Destination (Join-Path $runtimeRoot "desktop\browser-extension-chromium")
Copy-CleanDirectory -Source $extensionBridgeDir -Destination (Join-Path $runtimeRoot "desktop\browser-native-host")
Copy-CleanDirectory -Source $sharedDir -Destination (Join-Path $runtimeRoot "shared")

$nodeSource = Resolve-PortableNodeSource -Version $NodeVersion -PreferredDirectory $NodeSourceDirectory
Copy-CleanDirectory -Source $nodeSource -Destination (Join-Path $runtimeRoot "node")
$runtimeNode = Join-Path $runtimeRoot "node\node.exe"
if ((Get-NodeVersion -NodeExe $runtimeNode) -ne $NodeVersion) {
    throw "The packaged portable Node.js runtime did not report $NodeVersion."
}

Install-ProductionDependencies -NodeExe $runtimeNode -ProjectDirectory (Join-Path $runtimeRoot "backend\gemini-agent")
Install-ProductionDependencies -NodeExe $runtimeNode -ProjectDirectory (Join-Path $runtimeRoot "desktop\browser-action-agent")
Remove-BuildTimeNodeTooling -NodeDirectory (Split-Path -Parent $runtimeNode)
Assert-ReleaseRuntimeContents -RuntimeDirectory $runtimeRoot

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-cursivis-runtime.ps1") -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-cursivis-runtime.cmd") -Destination $packageRoot -Force

if ($IncludePluginPackage -and -not $SkipPluginPackage) {
    if (-not (Test-Path -LiteralPath $pluginPackage)) {
        throw "Matching Logitech plugin package was not found: $pluginPackage"
    }

    Assert-ForbiddenFilesAbsentFromPackage -Package $pluginPackage
    Copy-Item -LiteralPath $pluginPackage -Destination (Join-Path $packageRoot "Cursivis_$Version.lplug4") -Force
}

if (Test-Path -LiteralPath $docsDir) {
    $packageDocsDir = Join-Path $packageRoot "docs"
    New-Item -ItemType Directory -Force -Path $packageDocsDir | Out-Null
    foreach ($docName in @("PRIVACY_POLICY.md", "EULA.md", "LIVE_MODE_MARKETPLACE_REVIEW.md", "MARKETPLACE_SUBMISSION_CHECKLIST.md", "MARKETPLACE_READINESS.md")) {
        $docPath = Join-Path $docsDir $docName
        if (Test-Path -LiteralPath $docPath) {
            Copy-Item -LiteralPath $docPath -Destination (Join-Path $packageDocsDir $docName) -Force
        }
    }
}

@"
Cursivis Runtime Setup

1. Extract this zip.
2. Run install-cursivis-runtime.cmd.
3. Cursivis Companion opens automatically.
4. Paste your Gemini API keys in API LLM mode, or choose Local LLM and click Download & Use.
5. Add Cursivis Live Mode to Actions Ring for permission-aware voice control.

This self-contained runtime includes a private portable Node.js runtime and verified production dependencies.
Setup never needs Node.js, npm, Visual Studio, or developer tools already installed on this PC.
This runtime package does not include any private API keys or local model weights.
Local LLM setup downloads models only after the user chooses that option.
Live Mode uses the user's saved Gemini API key pool. Routine actions use Auto Execute by default; Require Confirmation remains available in Settings.
Advanced control of an already-open Chromium tab uses the included browser extension.
Chromium requires the user to approve that extension; basic Cursivis and managed-browser workflows do not require it.
Privacy policy, EULA, and Marketplace submission notes are included in the docs folder.
"@ | Set-Content -LiteralPath (Join-Path $packageRoot "README.txt") -Encoding UTF8

if (-not $SkipZip) {
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -Force
    Write-Host "Runtime zip: $zipPath"
}

Write-Host "Runtime package folder: $packageRoot"
Write-Host "The runtime includes portable Node.js $NodeVersion and production dependencies. Customer setup will not run npm."
