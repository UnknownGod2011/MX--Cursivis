using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var pressType = ParsePressType(args);

await TriggerDispatchClient.SendAsync(pressType);

return;

static string ParsePressType(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var value = args[i]?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        if (string.Equals(value, "--press-type", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return NormalizePressType(args[i + 1]);
        }

        if (string.Equals(value, "--trigger", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return NormalizePressType(args[i + 1]);
        }

        if (!value.StartsWith("-", StringComparison.Ordinal))
        {
            return NormalizePressType(value);
        }
    }

    return "tap";
}

static string NormalizePressType(string? value)
{
    return value?.Trim().ToLowerInvariant() switch
    {
        "go" => "tap",
        "trigger" => "tap",
        "normal" => "tap",
        "talk" => "long_press",
        "voice" => "long_press",
        "act" => "action",
        "take-action" => "action",
        "take_action" => "action",
        "snip" => "snip-it",
        "snipit" => "snip-it",
        "snip-it" => "snip-it",
        "settings" => "settings",
        "dial_press" => "dial_press",
        "tap" or "action" or "long_press" or "snip-it" => value.Trim().ToLowerInvariant(),
        _ => "tap"
    };
}

internal static class TriggerDispatchClient
{
    private static readonly Uri IpcUri = new("ws://127.0.0.1:48711/cursivis-trigger/");

    public static async Task SendAsync(string pressType)
    {
        using var socket = await ConnectOrWakeAsync();

        var payload = new
        {
            protocolVersion = "1.0.0",
            eventType = "trigger",
            requestId = Guid.NewGuid(),
            source = "gesture-launcher",
            pressType,
            dialDelta = (int?)null,
            cursor = new { x = 0, y = 0 },
            timestampUtc = DateTime.UtcNow.ToString("O")
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
    }

    private static async Task<ClientWebSocket> ConnectOrWakeAsync()
    {
        var socket = await TryConnectAsync();
        if (socket is not null)
        {
            return socket;
        }

        TryStartCompanion();

        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            socket = await TryConnectAsync();
            if (socket is not null)
            {
                return socket;
            }

            await Task.Delay(700);
        }

        throw new InvalidOperationException("Cursivis Companion could not be reached.");
    }

    private static async Task<ClientWebSocket?> TryConnectAsync()
    {
        var socket = new ClientWebSocket();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(IpcUri, cts.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }

    private static void TryStartCompanion()
    {
        try
        {
            var profilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cursivis",
                "runtime-profile.json");

            if (!File.Exists(profilePath))
            {
                return;
            }

            var json = File.ReadAllText(profilePath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryStartExecutable(root))
            {
                return;
            }

            TryStartProject(root);
        }
        catch
        {
            // Fail later if the companion cannot be reached.
        }
    }

    private static bool TryStartExecutable(JsonElement root)
    {
        if (!root.TryGetProperty("companionExecutable", out var executableElement))
        {
            return false;
        }

        var executablePath = executableElement.GetString();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--background",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        return true;
    }

    private static void TryStartProject(JsonElement root)
    {
        if (!root.TryGetProperty("companionProject", out var projectElement))
        {
            return;
        }

        var projectPath = projectElement.GetString();
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            return;
        }

        var escapedProjectPath = projectPath.Replace("'", "''");
        var command = $"dotnet run --project '{escapedProjectPath}' -- --background";
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
