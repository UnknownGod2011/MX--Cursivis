namespace Cursivis.Companion.Models;

public sealed class RuntimeLaunchProfile
{
    public string BackendDir { get; set; } = string.Empty;

    public string BrowserAgentDir { get; set; } = string.Empty;

    public string ExtensionBridgeDir { get; set; } = string.Empty;

    public string CompanionProject { get; set; } = string.Empty;

    public string CompanionExecutable { get; set; } = string.Empty;

    public string HotkeyHostExecutable { get; set; } = string.Empty;

    public string BackendUrl { get; set; } = "http://127.0.0.1:8080";

    public string BrowserAgentUrl { get; set; } = "http://127.0.0.1:48820";

    public string ExtensionBridgeUrl { get; set; } = "http://127.0.0.1:48830";

    public string AiProvider { get; set; } = "gemini";

    public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string OpenAiModel { get; set; } = "gpt-4.1-mini";

    public string HostedApiUrl { get; set; } = string.Empty;

    public string HostedToken { get; set; } = string.Empty;

    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";

    public string LocalModel { get; set; } = "granite3.2-vision:2b";

    public string ApiKey { get; set; } = string.Empty;

    public string ApiKeys { get; set; } = string.Empty;

    public bool EnableStreamingTranscription { get; set; }

    public bool EnableAutoReplace { get; set; }

    public double AutoReplaceConfidence { get; set; } = 0.9;

    public bool EnableManagedBrowserFallback { get; set; }
}
