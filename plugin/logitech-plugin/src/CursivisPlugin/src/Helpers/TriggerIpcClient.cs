#nullable enable

namespace Loupedeck.CursivisPlugin
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    internal static class TriggerIpcClient
    {
        private static readonly Uri IpcUri = new Uri("ws://127.0.0.1:48711/cursivis-trigger/");
        public static async Task SendAsync(String pressType, Int32? dialDelta = null)
        {
            using var socket = await ConnectOrWakeAsync();

            var payload = new
            {
                protocolVersion = "1.0.0",
                eventType = "trigger",
                requestId = Guid.NewGuid(),
                source = "logitech-plugin",
                pressType,
                dialDelta,
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

            throw new InvalidOperationException("Cursivis Companion is not running and could not be started automatically.");
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
                // Keep plugin responsive; send will fail later if startup cannot be completed.
            }
        }

        private static Boolean TryStartExecutable(JsonElement root)
        {
            if (!root.TryGetProperty("companionExecutable", out var executableElement))
            {
                return false;
            }

            var executablePath = executableElement.GetString();
            if (String.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--background",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);
            return true;
        }

        private static void TryStartProject(JsonElement root)
        {
            if (!root.TryGetProperty("companionProject", out var projectElement))
            {
                return;
            }

            var projectPath = projectElement.GetString();
            if (String.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("--background");

            Process.Start(startInfo);
        }
    }
}
