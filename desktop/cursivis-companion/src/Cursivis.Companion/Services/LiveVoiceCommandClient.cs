using Cursivis.Security;
using System.Net.WebSockets;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Cursivis.Companion.Services;

public sealed class LiveVoiceCommandClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _providerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _turnCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _latestInputTranscript;
    private string? _latestModelText;
    private Task? _receiveLoop;

    public event EventHandler<string>? TranscriptUpdated;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("CURSIVIS_LIVE_VOICE_URL");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            var profile = await new RuntimeLaunchProfileService().TryLoadAsync();
            var backendUrl = Environment.GetEnvironmentVariable("CURSIVIS_BACKEND_URL")
                ?? profile?.BackendUrl ?? "http://127.0.0.1:51880";
            var builder = new UriBuilder(backendUrl) { Path = "/live", Query = "" };
            builder.Scheme = builder.Scheme == "https" ? "wss" : "ws";
            endpoint = builder.Uri.AbsoluteUri;
        }

        await LocalIpc.ConnectAsync(_socket, new Uri(endpoint), LocalIpc.Backend, cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(CancellationToken.None));
        if (!await _providerReady.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken))
        {
            throw new InvalidOperationException("The realtime voice session did not start.");
        }
    }

    public async Task SendAudioChunkAsync(byte[] chunk, string mimeType, CancellationToken cancellationToken)
    {
        if (chunk.Length == 0 || _socket.State != WebSocketState.Open)
        {
            return;
        }

        await SendJsonAsync(
            new
            {
                type = "audio_chunk",
                mimeType,
                dataBase64 = Convert.ToBase64String(chunk)
            },
            cancellationToken);
    }

    public Task CompleteAudioAsync(CancellationToken cancellationToken)
    {
        return SendJsonAsync(new { type = "audio_end" }, cancellationToken);
    }

    public async Task<string?> WaitForFinalTranscriptAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _turnCompleted.Task.WaitAsync(timeoutCts.Token);
        }
        catch
        {
            // Return the best transcript available so far.
        }

        return !string.IsNullOrWhiteSpace(_latestInputTranscript)
            ? _latestInputTranscript
            : _latestModelText;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendJsonAsync(new { type = "close" }, closeTimeout.Token);
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeTimeout.Token);
            }
        }
        catch
        {
            // Ignore websocket close race.
        }

        _sendLock.Dispose();
        _socket.Dispose();
    }

    private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        while (_socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _providerReady.TrySetResult(false);
                    _turnCompleted.TrySetResult();
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            using var document = JsonDocument.Parse(ms.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            var type = typeElement.GetString();
            switch (type)
            {
                case "live_open":
                    _providerReady.TrySetResult(true);
                    break;
                case "input_transcription":
                    UpdateTranscript(root, "text", input: true);
                    break;
                case "model_text":
                case "output_transcription":
                    UpdateTranscript(root, "text", input: false);
                    break;
                case "turn_complete":
                    _turnCompleted.TrySetResult();
                    break;
                case "live_closed":
                case "error":
                    _providerReady.TrySetResult(false);
                    _turnCompleted.TrySetResult();
                    break;
            }
        }
    }

    private void UpdateTranscript(JsonElement root, string propertyName, bool input)
    {
        if (!root.TryGetProperty(propertyName, out var textElement))
        {
            return;
        }

        var text = textElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (input)
        {
            _latestInputTranscript = text;
            TranscriptUpdated?.Invoke(this, text);
        }
        else
        {
            _latestModelText = text;
            TranscriptUpdated?.Invoke(this, _latestInputTranscript ?? text);
        }
    }
}
