param(
    [ValidatePattern('^\d+_\d+_\d+$')]
    [string]$Version = "1_5_4",
    [string]$RuntimeUrl,
    [switch]$AllowLocalRuntimeUrl
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "desktop\cursivis-setup\src\Cursivis.Setup\Cursivis.Setup.csproj"
$runtimeZip = Join-Path $root "artifacts\cursivis-runtime\CursivisRuntime_${Version}.zip"

if (-not (Test-Path -LiteralPath $runtimeZip)) {
    throw "Build the matching runtime package before the setup executable: $runtimeZip"
}

$runtimeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeZip).Hash
$normalizedVersion = $Version.Replace('_', '.')
if ([string]::IsNullOrWhiteSpace($RuntimeUrl)) {
    $RuntimeUrl = "https://github.com/UnknownGod2011/MX--Cursivis/releases/download/v$normalizedVersion/CursivisRuntime_$Version.zip"
}
$runtimeUri = $null
if (-not [Uri]::TryCreate($RuntimeUrl, [UriKind]::Absolute, [ref]$runtimeUri)) {
    throw "RuntimeUrl must be an absolute URL."
}

$expectedReleasePath = "/UnknownGod2011/MX--Cursivis/releases/download/v$normalizedVersion/CursivisRuntime_$Version.zip"
$isPinnedGitHubRelease =
    $runtimeUri.Scheme -eq "https" -and
    $runtimeUri.Host.Equals("github.com", [StringComparison]::OrdinalIgnoreCase) -and
    $runtimeUri.AbsolutePath.Equals($expectedReleasePath, [StringComparison]::Ordinal)
$isAllowedLocalUrl =
    $AllowLocalRuntimeUrl -and
    $runtimeUri.IsLoopback -and
    ($runtimeUri.Scheme -eq "http" -or $runtimeUri.Scheme -eq "https")

if (-not $isPinnedGitHubRelease -and -not $isAllowedLocalUrl) {
    throw "RuntimeUrl must be the version-pinned MX--Cursivis GitHub Releases asset. Loopback URLs require -AllowLocalRuntimeUrl."
}

$outputDir = Join-Path $root $(if ($isAllowedLocalUrl) { "artifacts\security-qa" } else { "artifacts\qa-candidate" })
$publishDir = Join-Path $outputDir "setup-publish"
$outputPath = Join-Path $outputDir "CursivisCompanionSetup_${Version}.exe"

if (Test-Path -LiteralPath $publishDir) {
    $resolvedPublishDir = [System.IO.Path]::GetFullPath($publishDir)
    $resolvedRoot = [System.IO.Path]::GetFullPath($root)
    if (-not $resolvedPublishDir.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a publish directory outside the workspace."
    }

    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir, $outputDir | Out-Null

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:CursivisRuntimeSha256=$runtimeSha256 `
    -p:CursivisRuntimeUrl=$RuntimeUrl `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "Cursivis Companion Setup publish failed with exit code $LASTEXITCODE."
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -File)
if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Extension -ine ".exe") {
    $names = $publishedFiles.Name -join ", "
    throw "Setup must publish as one standalone EXE. Published files: $names"
}

Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination $outputPath -Force

Write-Host "Standalone setup candidate: $outputPath"
Write-Host "Pinned runtime URL: $RuntimeUrl"
Write-Host "Pinned runtime SHA256: $runtimeSha256"
Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath
