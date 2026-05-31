using Cursivis.Companion.Models;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cursivis.Companion.Services;

public sealed class TriggerIpcServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoopTask;

    public TriggerIpcServer(string prefix = "http://127.0.0.1:48711/cursivis-trigger/")
    {
        _listener.Prefixes.Add(prefix);
    }

    public event EventHandler<TriggerEventPayload>? TriggerReceived;

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
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
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
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
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

            var json = Encoding.UTF8.GetString(ms.ToArray());
            var payload = JsonSerializer.Deserialize<TriggerEventPayload>(json, _jsonOptions);
            if (payload is null)
            {
                continue;
            }

            TriggerReceived?.Invoke(this, payload);
        }
    }
}
