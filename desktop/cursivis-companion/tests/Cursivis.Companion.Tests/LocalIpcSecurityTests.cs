using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Cursivis.Companion.Models;
using Cursivis.Companion.Services;
using Cursivis.Security;
using Microsoft.Win32;
using Xunit;

namespace Cursivis.Companion.Tests;

public sealed class LocalIpcSecurityTests
{
    [Fact]
    public void ProtectedCredentialIsStableAndRepairsCorruption()
    {
        var registryPath = $@"Software\Cursivis\Tests\{Guid.NewGuid():N}";
        try
        {
            var first = LocalIpc.ReadOrCreateCredential(registryPath);
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true)!;
            var protectedValue = Assert.IsType<byte[]>(key.GetValue(LocalIpc.CredentialValue));
            Assert.True(first.Length == 32 && !first.AsSpan().SequenceEqual(protectedValue));
            Assert.True(first.AsSpan().SequenceEqual(LocalIpc.ReadOrCreateCredential(registryPath)));

            key.SetValue(LocalIpc.CredentialValue, new byte[] { 1, 2, 3 });
            var repaired = LocalIpc.ReadOrCreateCredential(registryPath);
            Assert.True(repaired.Length == 32 && !first.AsSpan().SequenceEqual(repaired));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKey(registryPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task TriggerOnlyDispatchesAuthenticatedKnownMessages()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = testTimeout.Token;
        var key = RandomNumberGenerator.GetBytes(32);
        var address = ReserveAddress("cursivis-trigger");
        using var server = new TriggerIpcServer(address.AbsoluteUri, key);
        var received = new TaskCompletionSource<TriggerEventPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.TriggerReceived += (_, payload) => received.TrySetResult(payload);
        server.Start();

        var endpoint = new UriBuilder(address) { Scheme = "ws" }.Uri;
        await AssertRejectedAsync(endpoint, proof: null, origin: null, HttpStatusCode.Unauthorized, token);
        string Sign() => LocalIpc.SignRequest(key, "GET", endpoint.Authority, endpoint.AbsolutePath);
        await AssertRejectedAsync(endpoint, Sign(), "https://untrusted.example", HttpStatusCode.Forbidden, token);
        await AssertRejectedAsync(endpoint, Sign(), "null", HttpStatusCode.Forbidden, token);
        var invalidProof = LocalIpc.SignRequest(RandomNumberGenerator.GetBytes(32), "GET", endpoint.Authority, endpoint.AbsolutePath);
        await AssertRejectedAsync(endpoint, invalidProof, origin: null, HttpStatusCode.Unauthorized, token);
        Assert.False(received.Task.IsCompleted);

        var proof = Sign();
        using var socket = CreateClient(proof);
        await socket.ConnectAsync(endpoint, token);
        Assert.Equal(LocalIpc.CreateAck(key, proof), Assert.Single(socket.HttpResponseHeaders![LocalIpc.AckHeader]));
        await AssertRejectedAsync(endpoint, proof, origin: null, HttpStatusCode.Unauthorized, token);
        await SendAsync(socket, """
            {"protocolVersion":"1.0.0","eventType":"trigger","source":"test-client","pressType":"unknown"}
            """, token);
        await SendAsync(socket, """
            {"protocolVersion":"1.0.0","eventType":"trigger","source":"test-client","pressType":"dial_tick","dialDelta":-1}
            """, token);
        var actual = await received.Task.WaitAsync(token);
        Assert.Equal("dial_tick", actual.PressType);
        Assert.Equal(-1, actual.DialDelta);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", token);
    }

    [Fact]
    public async Task HapticSubscriptionsRequireAuthenticationAndStillDeliverEvents()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = testTimeout.Token;
        var key = RandomNumberGenerator.GetBytes(32);
        var address = ReserveAddress("cursivis-haptics");
        using var server = new HapticEventHub(address.AbsoluteUri, key);
        server.Start();
        var endpoint = new UriBuilder(address) { Scheme = "ws" }.Uri;
        var proof = LocalIpc.SignRequest(key, "GET", endpoint.Authority, endpoint.AbsolutePath);
        await AssertRejectedAsync(endpoint, proof, "https://untrusted.example", HttpStatusCode.Forbidden, token);
        await AssertRejectedAsync(endpoint, proof: null, origin: null, HttpStatusCode.Unauthorized, token);
        using var socket = CreateClient(proof);
        await socket.ConnectAsync(endpoint, token);
        await Task.Delay(50, token);
        await server.BroadcastAsync(new HapticEventPayload { HapticType = "processing_complete", Intensity = "subtle" }, token);
        var buffer = new byte[256];
        var message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        Assert.Contains("processing_complete", Encoding.UTF8.GetString(buffer, 0, message.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", token);
    }

    [Fact]
    public void RequestsAreBoundToEndpointAndCanOnlyBeUsedOnce()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var auth = new LocalIpc.RequestAuthenticator(key);
        const string host = "127.0.0.1:48711";
        const string path = "/cursivis-trigger/";
        var proof = LocalIpc.SignRequest(key, "GET", host, path);
        Assert.False(auth.TryAccept(proof, "POST", host, path, out _));
        Assert.False(auth.TryAccept(proof, "GET", "wrong.example:48711", path, out _));
        Assert.False(auth.TryAccept(proof, "GET", host, path + "extra", out _));
        Assert.False(auth.TryAccept(proof.Replace(proof.Split('.')[1], "1000000000", StringComparison.Ordinal), "GET", host, path, out _));
        Assert.True(auth.TryAccept(proof, "GET", host, path, out var ack));
        Assert.True(ack.Length == 64 && !ack.Contains(proof));
        Assert.False(auth.TryAccept(proof, "GET", host, path, out _));
    }

    private static Uri ReserveAddress(string path)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        return new Uri($"http://127.0.0.1:{port}/{path}/");
    }

    private static ClientWebSocket CreateClient(string? proof, string? origin = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.Proxy = new WebProxy();
        if (proof is not null)
        {
            socket.Options.SetRequestHeader(LocalIpc.Header, proof);
        }
        if (origin is not null)
        {
            socket.Options.SetRequestHeader("Origin", origin);
        }
        return socket;
    }

    private static async Task AssertRejectedAsync(
        Uri endpoint, string? proof, string? origin, HttpStatusCode expected, CancellationToken token)
    {
        using var socket = CreateClient(proof, origin);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(endpoint, token));
        Assert.Equal(expected, socket.HttpStatusCode);
    }

    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);
}
