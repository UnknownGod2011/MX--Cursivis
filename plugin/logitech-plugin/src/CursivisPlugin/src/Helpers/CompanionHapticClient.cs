namespace Loupedeck.CursivisPlugin
{
    using System;
    using System.IO;
    using System.Net.WebSockets;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;

    internal sealed class CompanionHapticClient : IDisposable
    {
        private static readonly Uri HapticUri = new("ws://127.0.0.1:48712/cursivis-haptics/");

        private readonly CursivisPlugin _plugin;
        private readonly CancellationTokenSource _cts = new();
        private Task _worker;

        public CompanionHapticClient(CursivisPlugin plugin)
        {
            this._plugin = plugin;
        }

        public void Start()
        {
            if (this._worker is not null)
            {
                return;
            }

            this._worker = Task.Run(() => this.RunAsync(this._cts.Token));
        }

        public void Dispose()
        {
            try
            {
                this._cts.Cancel();
            }
            catch
            {
                // Ignore shutdown races.
            }

            try
            {
                this._worker?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore worker failures on shutdown.
            }

            this._cts.Dispose();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var retryDelay = TimeSpan.FromSeconds(3);
            var missingCompanionLogged = false;
            var unavailableLogged = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!CompanionRuntimeState.GetSnapshot().IsInstalled)
                {
                    if (!missingCompanionLogged)
                    {
                        PluginLog.Info("Cursivis Companion is not installed; haptic events will stay paused until setup completes.");
                        missingCompanionLogged = true;
                    }

                    await DelayAsync(TimeSpan.FromSeconds(60), cancellationToken);
                    continue;
                }

                missingCompanionLogged = false;
                try
                {
                    using var socket = new ClientWebSocket();
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(2));
                    await socket.ConnectAsync(HapticUri, connectCts.Token);
                    PluginLog.Info("Connected to companion haptic channel.");
                    unavailableLogged = false;
                    retryDelay = TimeSpan.FromSeconds(3);
                    await this.ReceiveLoopAsync(socket, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    if (!unavailableLogged)
                    {
                        PluginLog.Info("Companion haptic channel is unavailable; retrying quietly while Cursivis starts.");
                        unavailableLogged = true;
                    }
                }

                await DelayAsync(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
        }

        private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown is expected while Logi Plugin Service unloads the plugin.
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var payload = JsonSerializer.Deserialize<HapticEventPayload>(Encoding.UTF8.GetString(ms.ToArray()));
                if (payload?.HapticType is null)
                {
                    continue;
                }

                switch (payload.HapticType)
                {
                    case "action_change":
                    case "action_execute":
                    case "processing_start":
                    case "processing_complete":
                        this._plugin.PluginEvents.RaiseEvent(payload.HapticType);
                        PluginLog.Verbose($"Raised Logitech haptic event '{payload.HapticType}'.");
                        break;
                }
            }
        }

        private sealed class HapticEventPayload
        {
            [JsonPropertyName("hapticType")]
            public String HapticType { get; set; } = String.Empty;

            [JsonPropertyName("intensity")]
            public String Intensity { get; set; } = String.Empty;
        }
    }
}
