using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Text.Json;
using Cursivis.Security;

const int maxBrowserMessage = 8 * 1024 * 1024;
const int maxNativeMessage = 1024 * 1024;
const string profileName = "runtime-profile.json";

try
{
    if (args is ["--register-extension", var registeredId])
    {
        BrowserNativeRegistration.Register(Environment.ProcessPath!, registeredId);
        return 0;
    }
    if (args is ["--register-directory", var extensionDirectory])
    {
        BrowserNativeRegistration.Register(Environment.ProcessPath!, BrowserNativeRegistration.GetUnpackedExtensionId(extensionDirectory));
        return 0;
    }
    if (args.Length == 0 || BrowserNativeRegistration.ParseCallerId(args[0]) is not { } id ||
        !BrowserNativeRegistration.IsAllowedCaller(id))
    {
        return 1;
    }

    using var input = Console.OpenStandardInput();
    using var output = Console.OpenStandardOutput();
    using var helloTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var hello = await ReadMessageAsync(input, helloTimeout.Token);
    if (hello?.RootElement is not { ValueKind: JsonValueKind.Object } handshake ||
        ReadString(handshake, "type") != "hello" || ReadString(handshake, "extensionId") != id)
    {
        return 1;
    }

    var address = ReadBridgeAddress();
    using var http = LocalIpc.CreateHttpClient(address, LocalIpc.BrowserRelay, TimeSpan.FromSeconds(25));
    var sessionId = Guid.NewGuid().ToString("N");
    var capabilities = handshake.TryGetProperty("capabilities", out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Take(12)
            .Select(item => item.GetString()).ToArray()
        : [];
    var session = new { sessionId, extensionId = id, browserName = ReadString(handshake, "browserName"), capabilities };

    using var stop = new CancellationTokenSource();
    var read = Task.Run(() => ReceiveBrowserRepliesAsync(input, http, sessionId, stop.Token));
    var poll = PollAsync(output, http, session, sessionId, stop.Token);
    var finished = await Task.WhenAny(read, poll);
    stop.Cancel();
    try
    {
        await finished;
    }
    finally
    {
        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await http.PostAsJsonAsync("/extension/disconnect", new { sessionId }, closing.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // A disconnected or restarting bridge will discard the session when it expires.
        }
    }
    return 0;
}
catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or
                              InvalidOperationException or UnauthorizedAccessException or HttpRequestException)
{
    // stdout is exclusively for the browser protocol. Never print request data or credentials.
    Console.Error.WriteLine("Cursivis could not start its browser connection.");
    return 1;
}

static async Task PollAsync(Stream output, HttpClient http, object session, string sessionId, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("/extension/pull", session, token);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsByteArrayAsync(token);
            using var message = JsonDocument.Parse(json);
            var requestId = ReadString(message.RootElement, "requestId");
            if (!Guid.TryParseExact(requestId, "D", out _))
            {
                continue;
            }
            if (json.Length > maxNativeMessage)
            {
                using var rejected = await http.PostAsJsonAsync("/extension/response", new { sessionId, requestId, ok = false }, token);
                continue;
            }

            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
            await output.WriteAsync(header, token);
            await output.WriteAsync(json, token);
            await output.FlushAsync(token);
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or TaskCanceledException)
        {
            await Task.Delay(1000, token);
        }
    }
}

static async Task ReceiveBrowserRepliesAsync(Stream input, HttpClient http, string sessionId, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        using var message = await ReadMessageAsync(input, token);
        if (message is null)
        {
            return;
        }
        var root = message.RootElement;
        var requestId = ReadString(root, "requestId");
        if (ReadString(root, "type") != "response" || !Guid.TryParseExact(requestId, "D", out _))
        {
            continue;
        }
        var ok = root.TryGetProperty("ok", out var success) && success.ValueKind == JsonValueKind.True;
        var payload = root.TryGetProperty("payload", out var data) ? data : JsonSerializer.SerializeToElement(new { });
        try
        {
            using var response = await http.PostAsJsonAsync("/extension/response", new { sessionId, requestId, ok, payload }, token);
        }
        catch (HttpRequestException)
        {
            // Keep the registered browser connected while Companion restarts.
        }
    }
}

static async Task<JsonDocument?> ReadMessageAsync(Stream input, CancellationToken token)
{
    var header = new byte[4];
    if (await input.ReadAtLeastAsync(header, header.Length, false, token) != header.Length)
    {
        return null;
    }
    var size = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (size is <= 0 or > maxBrowserMessage)
    {
        throw new IOException("Invalid native message size.");
    }
    var bytes = new byte[size];
    await input.ReadExactlyAsync(bytes, token);
    return JsonDocument.Parse(bytes);
}

static string? ReadString(JsonElement element, string property) =>
    element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
    value.ValueKind == JsonValueKind.String ? value.GetString() : null;

static Uri ReadBridgeAddress()
{
    var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cursivis", profileName);
    if (File.Exists(profile))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(profile));
        if (Uri.TryCreate(ReadString(document.RootElement, "extensionBridgeUrl"), UriKind.Absolute, out var address) &&
            address.Scheme == "http" && LocalIpc.IsLocal(address))
        {
            return address;
        }
    }
    return new Uri("http://127.0.0.1:48830");
}
