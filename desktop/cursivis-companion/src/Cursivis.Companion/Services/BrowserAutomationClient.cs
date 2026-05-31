using Cursivis.Companion.Models;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Cursivis.Companion.Services;

public sealed class BrowserAutomationClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public BrowserAutomationClient()
    {
        var browserAgentUrl = Environment.GetEnvironmentVariable("CURSIVIS_BROWSER_AGENT_URL")
            ?? "http://127.0.0.1:48820";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(browserAgentUrl),
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    public async Task<BrowserPageContextResponse> EnsureBrowserAsync(
        string? preferredChannel,
        string? openUrl,
        CancellationToken cancellationToken)
    {
        var payload = new BrowserEnsureRequest
        {
            PreferredChannel = string.IsNullOrWhiteSpace(preferredChannel) ? null : preferredChannel,
            OpenUrl = string.IsNullOrWhiteSpace(openUrl) ? null : openUrl
        };

        var json = JsonSerializer.Serialize(payload);
        using var request = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("/ensure-browser", request, cancellationToken);
        return await ReadJsonOrThrowAsync<BrowserPageContextResponse>(response, cancellationToken);
    }

    public async Task<BrowserPageContextResponse> GetPageContextAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync("/page-context", cancellationToken);
        return await ReadJsonOrThrowAsync<BrowserPageContextResponse>(response, cancellationToken);
    }

    public async Task<BrowserExecutionResponse> ExecutePlanAsync(BrowserActionPlanResponse plan, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/execute-plan",
            new BrowserExecutionRequest
            {
                Steps = plan.Steps
            },
            cancellationToken);

        return await ReadJsonOrThrowAsync<BrowserExecutionResponse>(response, cancellationToken);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static async Task<T> ReadJsonOrThrowAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(FormatError(response.StatusCode, body));
        }

        var parsed = JsonSerializer.Deserialize<T>(body);
        if (parsed is null)
        {
            throw new InvalidOperationException("Browser action agent returned an empty payload.");
        }

        return parsed;
    }

    private static string FormatError(HttpStatusCode statusCode, string responseBody)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BrowserAgentErrorResponse>(responseBody);
            if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Error))
            {
                return string.IsNullOrWhiteSpace(parsed.Details)
                    ? parsed.Error
                    : $"{parsed.Error} {parsed.Details}";
            }
        }
        catch
        {
            // Fall through to raw response below.
        }

        return $"Browser action agent error {(int)statusCode}: {responseBody}";
    }

    private sealed class BrowserAgentErrorResponse
    {
        public string? Error { get; init; }

        public string? Details { get; init; }
    }
}
