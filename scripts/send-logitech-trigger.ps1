param(
    [ValidateSet("tap", "long_press", "long_press_start", "long_press_end", "dial_press", "dial_tick")]
    [string]$PressType = "dial_tick",
    [int]$DialDelta = 1,
    [string]$Endpoint = "ws://127.0.0.1:48711/cursivis-trigger/",
    [string]$Source = "virtual-logitech-test"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "local-ipc.ps1")

$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$uri = [Uri]$Endpoint
$timeout = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
$token = $timeout.Token
$key = Get-CursivisServiceKey trigger

try {
    if (-not $socket.Options.PSObject.Properties['CollectHttpResponseDetails']) {
        throw 'Run the trigger diagnostic with PowerShell 7.4 or later.'
    }
    $proof = New-CursivisIpcProof $uri GET $key
    $socket.Options.Proxy = [Net.WebProxy]::new()
    $socket.Options.CollectHttpResponseDetails = $true
    $socket.Options.SetRequestHeader('X-Cursivis-IPC', $proof)
    $socket.ConnectAsync($uri, $token).GetAwaiter().GetResult()
    if ([string]($socket.HttpResponseHeaders['X-Cursivis-IPC-Ack'] | Select-Object -First 1) -cne (Get-CursivisIpcAck $key $proof)) {
        throw 'Could not verify the Cursivis trigger channel.'
    }

    $payload = [ordered]@{
        protocolVersion = "1.0.0"
        eventType       = "trigger"
        requestId       = [Guid]::NewGuid().ToString()
        source          = $Source
        pressType       = $PressType
        cursor          = @{
            x = 0
            y = 0
        }
        timestampUtc    = [DateTime]::UtcNow.ToString("O")
    }

    if ($PressType -eq "dial_tick") {
        $payload.dialDelta = $DialDelta
    }

    $json = $payload | ConvertTo-Json -Compress -Depth 4
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $segment = [System.ArraySegment[byte]]::new($bytes)
    $socket.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $token).GetAwaiter().GetResult()
    Write-Host "Sent Logitech-style trigger:" $json
}
finally {
    try {
        if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $socket.CloseOutputAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", $token).GetAwaiter().GetResult()
        }
    }
    finally {
        $socket.Dispose()
        $timeout.Dispose()
        [Array]::Clear($key, 0, $key.Length)
    }
}
