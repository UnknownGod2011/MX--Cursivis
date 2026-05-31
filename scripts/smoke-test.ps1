param(
    [string]$ApiKey,
    [string]$BackendUrl = "http://127.0.0.1:8080",
    [string]$Text = "who is the richest person in the world"
)

$ErrorActionPreference = "Stop"

if ($ApiKey) {
    $env:GOOGLE_API_KEY = $ApiKey
}

Write-Host "Cursivis backend smoke test"
Write-Host "Backend URL: $BackendUrl"

$healthUrl = "$BackendUrl/health"
$analyzeUrl = "$BackendUrl/analyze"

try {
    $health = Invoke-RestMethod -Method Get -Uri $healthUrl -TimeoutSec 10
}
catch {
    Write-Error "Health check failed: $($_.Exception.Message)"
    exit 1
}

if (-not $health.ok) {
    Write-Error "Health check returned unexpected payload."
    exit 1
}

Write-Host "Health check: OK"

$request = @{
    protocolVersion = "1.0.0"
    requestId = [Guid]::NewGuid().ToString()
    mode = "smart"
    actionHint = "answer_question"
    selection = @{
        kind = "text"
        text = $Text
    }
    context = @{
        activeApp = "smoke-test"
        cursorX = 100
        cursorY = 100
    }
    timestampUtc = (Get-Date).ToUniversalTime().ToString("o")
}

try {
    $response = Invoke-RestMethod -Method Post -Uri $analyzeUrl -ContentType "application/json" -Body ($request | ConvertTo-Json -Depth 8) -TimeoutSec 60
}
catch {
    $statusCode = $null
    $bodyText = $null

    try {
        $httpResponse = $_.Exception.Response
        if ($httpResponse -and $httpResponse.StatusCode) {
            $statusCode = [int]$httpResponse.StatusCode
        }

        if ($httpResponse -and $httpResponse.GetResponseStream) {
            $reader = New-Object System.IO.StreamReader($httpResponse.GetResponseStream())
            $bodyText = $reader.ReadToEnd()
            $reader.Dispose()
        }
    }
    catch {
        # Ignore body parse failures.
    }

    if ($statusCode -eq 429) {
        Write-Warning "Analyze is rate-limited (429). Backend is healthy, but Gemini quota is temporarily exhausted."
        if ($bodyText) {
            Write-Host "Details: $bodyText"
        }

        Write-Host "Suggestion: wait and retry, or switch to a fresh key/project quota."
        exit 2
    }

    if ($bodyText) {
        Write-Error "Analyze request failed ($statusCode): $bodyText"
    }
    else {
        Write-Error "Analyze request failed: $($_.Exception.Message)"
    }

    exit 1
}

if (-not $response.result) {
    Write-Error "Analyze returned no result text."
    exit 1
}

$sample = [string]$response.result
if ($sample.Length -gt 220) {
    $sample = $sample.Substring(0, 220) + "..."
}

Write-Host "Analyze: OK"
Write-Host "Action: $($response.action)"
Write-Host "Confidence: $($response.confidence)"
Write-Host "Sample: $sample"
exit 0
