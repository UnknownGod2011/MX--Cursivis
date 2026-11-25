using System.Net;
using Cursivis.Companion.Services;
using Xunit;

namespace Cursivis.Companion.Tests;

public sealed class BackendErrorPresentationTests
{
    [Fact]
    public void DesktopDisplaysStructuredTimeoutWithoutDumpingTheJsonResponse()
    {
        const string body = """{"error":"Image intent routing took too long. Please try again.","timestampUtc":"2026-11-25T12:55:50Z"}""";
        Assert.Equal("Image intent routing took too long. Please try again.",
            GeminiClient.FormatBackendError(HttpStatusCode.GatewayTimeout, body));
    }

    [Fact]
    public void DesktopDisplaysProviderRetryDelayFromCamelCaseResponse()
    {
        const string body = """{"error":"Key pool cooldown.","retryAfterSec":42}""";
        Assert.Contains("about 42 seconds", GeminiClient.FormatBackendError(HttpStatusCode.TooManyRequests, body));
    }
}
