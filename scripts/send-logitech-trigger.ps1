param(
    [ValidateSet("tap", "long_press", "long_press_start", "long_press_end", "dial_press", "dial_tick")]
    [string]$PressType = "dial_tick",
    [int]$DialDelta = 1,
    [string]$Endpoint = "ws://127.0.0.1:48711/cursivis-trigger/",
    [string]$Source = "virtual-logitech-test"
)

$ErrorActionPreference = "Stop"

$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$uri = [Uri]$Endpoint
$token = [System.Threading.CancellationToken]::None

try {
    $socket.ConnectAsync($uri, $token).GetAwaiter().GetResult()

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
    if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $socket.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", $token).GetAwaiter().GetResult()
    }

    $socket.Dispose()
}
