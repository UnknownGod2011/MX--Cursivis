using Cursivis.Companion.Models;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cursivis.Companion.Services;

public sealed class HapticEventHub : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<WebSocket> _clients = [];
    private readonly object _sync = new();
    private Task? _acceptLoopTask;

    public HapticEventHub(string prefix = "http://127.0.0.1:48712/cursivis-haptics/")
    {
        _listener.Prefixes.Add(prefix);
    }

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

        lock (_sync)
        {
            foreach (var socket in _clients.ToList())
            {
                try
                {
                    socket.Abort();
                    socket.Dispose();
                }
                catch
                {
                    // Ignore disposal failures.
                }
            }

            _clients.Clear();
        }
    }

    public async Task BroadcastAsync(HapticEventPayload payload, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        List<WebSocket> clients;
        lock (_sync)
        {
            clients = _clients.ToList();
        }

        foreach (var client in clients)
        {
            if (client.State != WebSocketState.Open)
            {
                RemoveClient(client);
                continue;
            }

            try
            {
                await client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationToken);
            }
            catch
            {
                RemoveClient(client);
            }
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
            AddClient(socket);
            await ReceiveUntilCloseAsync(socket, cancellationToken);
        }
        catch
        {
            // Keep hub alive for other clients.
        }
        finally
        {
            if (socket is not null)
            {
                RemoveClient(socket);
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

    private async Task ReceiveUntilCloseAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }
        }
    }

    private void AddClient(WebSocket socket)
    {
        lock (_sync)
        {
            _clients.Add(socket);
        }
    }

    private void RemoveClient(WebSocket socket)
    {
        lock (_sync)
        {
            _clients.Remove(socket);
        }
    }
}
