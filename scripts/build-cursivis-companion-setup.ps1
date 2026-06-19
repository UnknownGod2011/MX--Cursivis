param(
    [string]$Version = "1_5_0",
    [string]$RuntimeUrl = "https://7laoth4l2ecu5n2m.public.blob.vercel-storage.com/runtime/CursivisRuntime_1_5_0-E9A859F6ABC699AE-fvZFRZoPBY2rtHWWUUXv4ln30BNFAU.zip"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "desktop\cursivis-setup\src\Cursivis.Setup\Cursivis.Setup.csproj"
$publishDir = Join-Path $root "artifacts\qa-candidate\setup-publish"
$outputDir = Join-Path $root "artifacts\qa-candidate"
$outputPath = Join-Path $outputDir "CursivisCompanionSetup_${Version}.exe"
$runtimeZip = Join-Path $root "artifacts\cursivis-runtime\CursivisRuntime_${Version}.zip"

if (-not (Test-Path -LiteralPath $runtimeZip)) {
    throw "Build the matching runtime package before the setup executable: $runtimeZip"
}

$runtimeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeZip).Hash
$runtimeUri = $null
if (-not [Uri]::TryCreate($RuntimeUrl, [UriKind]::Absolute, [ref]$runtimeUri) -or
    $runtimeUri.Scheme -ne "https" -or
    -not $runtimeUri.Host.EndsWith(".public.blob.vercel-storage.com", [StringComparison]::OrdinalIgnoreCase)) {
    throw "RuntimeUrl must be an HTTPS URL hosted by Vercel Blob."
}

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
