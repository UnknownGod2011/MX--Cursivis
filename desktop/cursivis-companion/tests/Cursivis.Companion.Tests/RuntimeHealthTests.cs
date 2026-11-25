using System.Net;
using System.Net.Sockets;
using Cursivis.Companion.Services;
using Xunit;

namespace Cursivis.Companion.Tests;

public sealed class RuntimeHealthTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(503, false)]
    public async Task ProbesConfiguredOllamaPath(int status, bool expectedHealthy)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var address = ReserveAddress();
        using var server = new HttpListener();
        server.Prefixes.Add(address.AbsoluteUri);
        server.Start();

        var request = server.GetContextAsync().WaitAsync(timeout.Token);
        var path = new Uri(address, "ollama/api/tags");
        var probe = RuntimeBootstrapper.IsHealthyAsync(path.AbsoluteUri, timeout.Token);
        Assert.Same(request, await Task.WhenAny(request, probe));
        var context = await request;
        Assert.Equal("/ollama/api/tags", context.Request.RawUrl);
        context.Response.StatusCode = status;
        context.Response.Close();
        Assert.Equal(expectedHealthy, await probe);
    }

    [Fact]
    public async Task UnreachableOllamaIsUnhealthy()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var path = new Uri(ReserveAddress(), "api/tags");
        Assert.False(await RuntimeBootstrapper.IsHealthyAsync(path.AbsoluteUri, timeout.Token));
    }

    [Fact]
    public async Task CallerCancellationIsNotAnUnhealthyService()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = new Uri(ReserveAddress(), "api/tags");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RuntimeBootstrapper.IsHealthyAsync(path.AbsoluteUri, cancellation.Token));
    }

    private static Uri ReserveAddress()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/");
    }
}
