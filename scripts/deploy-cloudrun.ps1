param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,
    [string]$Region = "us-central1",
    [string]$ServiceName = "cursivis-gemini-agent",
    [string]$ImageName = "cursivis-gemini-agent",
    [string]$GoogleApiKey,
    [switch]$UseSecretManager,
    [string]$SecretName = "cursivis-google-api-key",
    [int]$MinInstances = 0,
    [int]$TimeoutSeconds = 30,
    [switch]$AllowUnauthenticated = $true
)

$ErrorActionPreference = "Stop"

function Invoke-GCloud {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "gcloud $($Arguments -join ' ')"
    & gcloud @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gcloud command failed."
    }
}

function Secret-Exists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectId,
        [Parameter(Mandatory = $true)]
        [string]$SecretName
    )

    & gcloud secrets describe $SecretName "--project=$ProjectId" *> $null
    return $LASTEXITCODE -eq 0
}

$root = Split-Path -Parent $PSScriptRoot
$imageUri = "gcr.io/$ProjectId/$ImageName"
$serviceUrl = ""

Write-Host "Deploying Cursivis Gemini backend to Google Cloud Run..."
Write-Host "Project: $ProjectId"
Write-Host "Region: $Region"
Write-Host "Service: $ServiceName"
Write-Host "Image: $imageUri"

Invoke-GCloud -Arguments @("config", "set", "project", $ProjectId)
Invoke-GCloud -Arguments @(
    "services", "enable",
    "run.googleapis.com",
    "cloudbuild.googleapis.com",
    "artifactregistry.googleapis.com",
    "secretmanager.googleapis.com"
)

if ($UseSecretManager) {
    if ([string]::IsNullOrWhiteSpace($GoogleApiKey)) {
        throw "UseSecretManager requires -GoogleApiKey so the secret can be created or updated."
    }

    if (-not (Secret-Exists -ProjectId $ProjectId -SecretName $SecretName)) {
        Invoke-GCloud -Arguments @(
            "secrets", "create", $SecretName,
            "--replication-policy=automatic",
            "--project=$ProjectId"
        )
    }

    $tempKeyFile = Join-Path $env:TEMP ("cursivis-google-api-key-" + [Guid]::NewGuid().ToString("N") + ".txt")
    try {
        [System.IO.File]::WriteAllText($tempKeyFile, $GoogleApiKey)
        Invoke-GCloud -Arguments @(
            "secrets", "versions", "add", $SecretName,
            "--data-file=$tempKeyFile",
            "--project=$ProjectId"
        )
    }
    finally {
        if (Test-Path $tempKeyFile) {
            Remove-Item $tempKeyFile -Force
        }
    }
}

Push-Location $root
try {
    Invoke-GCloud -Arguments @(
        "builds", "submit",
        "--tag", $imageUri,
        "-f", "backend/gemini-agent/Dockerfile",
        "."
    )
}
finally {
    Pop-Location
}

$deployArgs = @(
    "run", "deploy", $ServiceName,
    "--image", $imageUri,
    "--region", $Region,
    "--platform", "managed",
    "--min-instances", $MinInstances,
    "--timeout", $TimeoutSeconds
)

if ($AllowUnauthenticated) {
    $deployArgs += "--allow-unauthenticated"
}

if ($UseSecretManager) {
    $deployArgs += "--update-secrets"
    $deployArgs += "GOOGLE_API_KEY=$SecretName:latest"
}
elseif (-not [string]::IsNullOrWhiteSpace($GoogleApiKey)) {
    $deployArgs += "--set-env-vars"
    $deployArgs += "GOOGLE_API_KEY=$GoogleApiKey,CURSIVIS_ENABLE_LIVE_GROUNDING=true,GEMINI_MODEL=gemini-2.5-flash"
}
else {
    throw "Provide either -GoogleApiKey or use -UseSecretManager with -GoogleApiKey."
}

Invoke-GCloud -Arguments $deployArgs

$serviceUrl = (& gcloud run services describe $ServiceName "--region=$Region" "--format=value(status.url)").Trim()
if (-not [string]::IsNullOrWhiteSpace($serviceUrl)) {
    Write-Host ""
    Write-Host "Cloud Run URL: $serviceUrl"
    Write-Host "Health check: $serviceUrl/health"
    Write-Host ""
    Write-Host "To use the cloud backend from your current local demo without changing defaults:"
    Write-Host "powershell -ExecutionPolicy Bypass -File .\\scripts\\run-demo.ps1 -ApiKey `"<LOCAL_OR_FALLBACK_KEY>`" -BackendUrl `"$serviceUrl`""
}
