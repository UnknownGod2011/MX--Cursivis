param(
    [switch]$Help
)

$ErrorActionPreference = "Stop"

if ($Help) {
    Write-Host "Usage:"
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\scripts\stop-demo.ps1"
    Write-Host "Stops local Cursivis demo processes started via dotnet run / node."
    return
}

$patterns = @(
    "backend\gemini-agent\src\server.js",
    "node src/server.js",
    "cmd.exe /d /s /c node src/server.js",
    "desktop\browser-action-agent\src\server.js",
    "desktop\browser-native-host\src\host.js",
    "desktop\cursivis-companion\src\Cursivis.Companion\Cursivis.Companion.csproj",
    "plugin\logitech-plugin\src\Cursivis.Logitech.Bridge\Cursivis.Logitech.Bridge.csproj",
    "desktop\cursivis-companion\src\Cursivis.Companion\bin\Debug\net8.0-windows\Cursivis.Companion.exe",
    "desktop\cursivis-hotkey-host\src\Cursivis.HotkeyHost\bin\Debug\net8.0-windows\Cursivis.HotkeyHost.exe",
    "plugin\logitech-plugin\src\Cursivis.Logitech.Bridge\bin\Debug\net8.0\Cursivis.Logitech.Bridge.exe"
)

$targets = Get-CimInstance Win32_Process | Where-Object {
    $cmd = $_.CommandLine
    if ([string]::IsNullOrWhiteSpace($cmd)) {
        return $false
    }

    foreach ($pattern in $patterns) {
        if ($cmd -like "*$pattern*") {
            return $true
        }
    }

    return $false
}

if (-not $targets) {
    Write-Host "No running Cursivis demo processes were found."
    return
}

$stopped = 0
foreach ($proc in $targets) {
    try {
        Invoke-CimMethod -InputObject $proc -MethodName Terminate | Out-Null
        $stopped += 1
        Write-Host "Stopped PID $($proc.ProcessId): $($proc.Name)"
    }
    catch {
        Write-Warning "Failed to stop PID $($proc.ProcessId): $($_.Exception.Message)"
    }
}

try {
    $listening = netstat -ano | Select-String "LISTENING" | Select-String ":8080|:48820|:48830"
    $handledPortPids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($entry in $listening) {
        $parts = ($entry -split '\s+') | Where-Object { $_ -ne '' }
        if ($parts.Count -lt 5) {
            continue
        }

        $portPid = [int]$parts[-1]
        if ($portPid -le 0) {
            continue
        }

        if (-not $handledPortPids.Add($portPid)) {
            continue
        }

        Stop-Process -Id $portPid -Force -ErrorAction Stop
        $stopped += 1
        Write-Host "Stopped PID $portPid (listening port cleanup)"
    }
}
catch {
    # Ignore if port check fails or process exits during stop.
}

Write-Host "Done. Stopped $stopped process(es)."
