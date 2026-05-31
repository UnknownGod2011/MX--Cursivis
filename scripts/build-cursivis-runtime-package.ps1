param(
    [string]$Version = "1_4",
    [string]$NodeVersion = "v22.22.0",
    [switch]$SkipDotnetPublish,
    [switch]$SkipZip
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
$extensionBridgeDir = Join-Path $root "desktop\browser-native-host"
$sharedDir = Join-Path $root "shared"
$docsDir = Join-Path $root "docs"
$pluginPackage = Join-Path $root "artifacts\logitech-marketplace\Cursivis_$Version.lplug4"

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

Write-Host "Building Cursivis runtime package..."
Write-Host "Output: $packageRoot"

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

if (-not $SkipDotnetPublish) {
    dotnet publish $companionProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $runtimeRoot "app\companion")
    dotnet publish $hotkeyProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $runtimeRoot "app\hotkey-host")
    dotnet publish $triggerProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o (Join-Path $runtimeRoot "app\trigger-launcher")
}

Copy-CleanDirectory -Source $backendDir -Destination (Join-Path $runtimeRoot "backend\gemini-agent") -Exclude @("node_modules")
Copy-CleanDirectory -Source $browserAgentDir -Destination (Join-Path $runtimeRoot "desktop\browser-action-agent") -Exclude @("node_modules")
Copy-CleanDirectory -Source $extensionBridgeDir -Destination (Join-Path $runtimeRoot "desktop\browser-native-host")
Copy-CleanDirectory -Source $sharedDir -Destination (Join-Path $runtimeRoot "shared")

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-cursivis-runtime.ps1") -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install-cursivis-runtime.cmd") -Destination $packageRoot -Force

if (Test-Path -LiteralPath $pluginPackage) {
    Copy-Item -LiteralPath $pluginPackage -Destination (Join-Path $packageRoot "Cursivis_$Version.lplug4") -Force
}

if (Test-Path -LiteralPath $docsDir) {
    $packageDocsDir = Join-Path $packageRoot "docs"
    New-Item -ItemType Directory -Force -Path $packageDocsDir | Out-Null
    foreach ($docName in @("PRIVACY_POLICY.md", "EULA.md", "MARKETPLACE_SUBMISSION_CHECKLIST.md", "MARKETPLACE_READINESS.md")) {
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

This runtime package does not include any private API keys or local model weights.
Local LLM setup downloads models only after the user chooses that option.
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
Write-Host "Node.js $NodeVersion will be downloaded by the installer only if portable Node is not already installed in the Cursivis runtime folder."
