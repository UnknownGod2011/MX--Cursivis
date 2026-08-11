#nullable enable

namespace Loupedeck.CursivisPlugin
{
    using System;
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

            var snapshot = CompanionRuntimeState.GetSnapshot(refresh: true);
            if (!snapshot.IsInstalled)
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
                {
                    CompanionRecoveryPrompt.Show();
                }
                else
                {
                    CompanionRuntimeState.OpenDownloadPage();
                }
                throw new InvalidOperationException(
                    "Cursivis Companion is not installed. Use Get Companion to install it, then press this action again.");
            }

            CompanionRuntimeState.TryStartCompanion();

            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                socket = await TryConnectAsync();
                if (socket is not null)
                {
                    return socket;
                }

                await Task.Delay(350);
            }

            throw new InvalidOperationException(
                "Cursivis Companion is installed but is not responding. Open Cursivis from the system tray and run Diagnostics & Repair.");
        }

        private static async Task<ClientWebSocket?> TryConnectAsync()
        {
            var socket = new ClientWebSocket();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
                await socket.ConnectAsync(IpcUri, cts.Token);
                return socket;
            }
            catch
            {
                socket.Dispose();
                return null;
            }
        }

    }
}
