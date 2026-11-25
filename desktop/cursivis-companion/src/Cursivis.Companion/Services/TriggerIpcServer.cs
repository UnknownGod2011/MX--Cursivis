using Cursivis.Companion.Models;
using Cursivis.Security;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace Cursivis.Companion.Services;

public sealed class TriggerIpcServer : IDisposable
{
    private const int MaxMessageBytes = 4096;
    private static readonly HashSet<string> PressTypes = new(StringComparer.Ordinal)
    {
        "tap", "action", "snip-it", "prompt_optimizer", "live_mode", "live_mode_stop", "settings",
        "long_press", "long_press_start", "long_press_end", "dial_press", "dial_tick"
    };

    private readonly HttpListener _listener = new();
    private readonly LocalWebSocketAccess _access;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoopTask;

    public TriggerIpcServer(string prefix = "http://127.0.0.1:48711/cursivis-trigger/")
        : this(prefix, LocalIpc.GetServiceKey(LocalIpc.Trigger))
    {
    }

    internal TriggerIpcServer(string prefix, byte[] key)
    {
        _access = new LocalWebSocketAccess(new Uri(prefix), key);
        _listener.Prefixes.Add(prefix);
    }

    public event EventHandler<TriggerEventPayload>? TriggerReceived;

    public bool IsListening => _listener.IsListening;

    public void Start()
    {
        _listener.Start();
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts.Cancel();

        if (_listener.IsListening)
        {
            _listener.Stop();
        }
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                if (!_listener.IsListening)
                {
                    break;
                }

                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!_access.TryAuthorize(context))
        {
            return;
        }

        WebSocket? socket = null;
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            socket = wsContext.WebSocket;
            await ReceiveLoopAsync(socket, cancellationToken);
        }
        catch
        {
            // Keep server alive even if a single client fails.
        }
        finally
        {
            if (socket is not null)
            {
                try
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", closeTimeout.Token);
                }
                catch
                {
                    // Ignore close failures.
                }

                socket.Dispose();
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageBytes];

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

                if (result.MessageType != WebSocketMessageType.Text || ms.Length + result.Count > MaxMessageBytes)
                {
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var payload = JsonSerializer.Deserialize<TriggerEventPayload>(
                ms.GetBuffer().AsSpan(0, (int)ms.Length), _jsonOptions);
            if (payload is null || payload.ProtocolVersion != "1.0.0" || payload.EventType != "trigger" ||
                payload.PressType?.Trim().ToLowerInvariant() is not { } pressType || !PressTypes.Contains(pressType) ||
                payload.Source is not { Length: > 0 and <= 64 } source ||
                !source.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                continue;
            }

            payload.PressType = pressType;
            TriggerReceived?.Invoke(this, payload);
        }
    }
}
