using Cursivis.Companion.Models;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace Cursivis.Companion.Services;

public sealed class RuntimeBootstrapper
{
    private readonly RuntimeLaunchProfileService _profileService = new();

    public async Task EnsureRuntimeReadyAsync(CancellationToken cancellationToken)
    {
        var profile = await _profileService.TryLoadAsync();
        if (profile is null)
        {
            return;
        }

        await EnsureLocalOllamaReadyAsync(profile, cancellationToken);
        await EnsureBackendAsync(profile, cancellationToken);
        await EnsureBrowserAgentAsync(profile, cancellationToken);
        await EnsureExtensionBridgeAsync(profile, cancellationToken);
    }

    private static async Task EnsureLocalOllamaReadyAsync(RuntimeLaunchProfile profile, CancellationToken cancellationToken)
    {
        if (NormalizeProviderId(profile.AiProvider) != "local_ollama")
        {
            return;
        }

        var ollamaUrl = NormalizeBaseUrl(string.IsNullOrWhiteSpace(profile.OllamaUrl)
            ? "http://127.0.0.1:11434"
            : profile.OllamaUrl);
        var tagsUrl = $"{ollamaUrl}/api/tags";
        if (await IsHealthyAsync(tagsUrl, cancellationToken))
        {
            return;
        }

        if (!TryStartInstalledOllama())
        {
            return;
        }

        await WaitForHealthyAsync(tagsUrl, TimeSpan.FromSeconds(25), cancellationToken);
    }

    private static async Task EnsureBackendAsync(RuntimeLaunchProfile profile, CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync($"{profile.BackendUrl.TrimEnd('/')}/health", cancellationToken))
        {
            return;
        }

        if (!Directory.Exists(profile.BackendDir))
        {
            return;
        }

        var environment = new Dictionary<string, string>
        {
            ["PORT"] = ResolvePort(profile.BackendUrl).ToString(),
            ["CURSIVIS_AI_PROVIDER"] = NormalizeProviderId(profile.AiProvider),
            ["GEMINI_ROUTER_MODEL"] = "gemini-2.5-flash-lite",
            ["GEMINI_OPTIONS_MODEL"] = "gemini-2.5-flash-lite",
            ["GEMINI_FALLBACK_MODELS"] = "gemini-2.5-flash-lite,gemini-2.0-flash"
        };
        AddProviderEnvironment(environment, profile);

        StartNodeServer(profile, profile.BackendDir, environment);
        await WaitForHealthyAsync($"{profile.BackendUrl.TrimEnd('/')}/health", TimeSpan.FromSeconds(30), cancellationToken);
    }

    private static int ResolvePort(string backendUrl)
    {
        return Uri.TryCreate(backendUrl, UriKind.Absolute, out var uri) &&
               uri.Port is >= 1024 and <= 65535
            ? uri.Port
            : 51880;
    }

    private static async Task EnsureBrowserAgentAsync(RuntimeLaunchProfile profile, CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync($"{profile.BrowserAgentUrl.TrimEnd('/')}/health", cancellationToken))
        {
            return;
        }

        if (!Directory.Exists(profile.BrowserAgentDir))
        {
            return;
        }

        StartNodeServer(
            profile,
            profile.BrowserAgentDir,
            new Dictionary<string, string> { ["CURSIVIS_BROWSER_CHANNEL"] = "chrome" });

        await WaitForHealthyAsync($"{profile.BrowserAgentUrl.TrimEnd('/')}/health", TimeSpan.FromSeconds(20), cancellationToken);
    }

    private static async Task EnsureExtensionBridgeAsync(RuntimeLaunchProfile profile, CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync($"{profile.ExtensionBridgeUrl.TrimEnd('/')}/health", cancellationToken))
        {
            return;
        }

        var hostJs = Path.Combine(profile.ExtensionBridgeDir, "src", "host.js");
        if (!File.Exists(hostJs))
        {
            return;
        }

        StartNodeScript(profile, profile.ExtensionBridgeDir, hostJs);
        await WaitForHealthyAsync($"{profile.ExtensionBridgeUrl.TrimEnd('/')}/health", TimeSpan.FromSeconds(15), cancellationToken);
    }

    private static void StartNodeServer(RuntimeLaunchProfile profile, string projectDir, IReadOnlyDictionary<string, string>? environment = null)
    {
        var serverJs = Path.Combine(projectDir, "src", "server.js");
        if (!File.Exists(serverJs))
        {
            return;
        }

        StartNodeScript(profile, projectDir, serverJs, environment);
    }

    private static void StartNodeScript(
        RuntimeLaunchProfile profile,
        string projectDir,
        string scriptPath,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var nodeExe = TryResolvePortableNodeExe(profile, projectDir);
        if (string.IsNullOrWhiteSpace(nodeExe))
        {
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            Arguments = $"\"{scriptPath}\"",
            WorkingDirectory = projectDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        Process.Start(psi);
    }

    private static string? TryResolvePortableNodeExe(RuntimeLaunchProfile profile, string projectDir)
    {
        foreach (var root in CandidateRuntimeRoots(profile, projectDir))
        {
            var nodeExe = Path.Combine(root, "node", "node.exe");
            if (File.Exists(nodeExe))
            {
                return nodeExe;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateRuntimeRoots(RuntimeLaunchProfile profile, string projectDir)
    {
        var dirs = new[]
        {
            Path.GetDirectoryName(profile.CompanionExecutable),
            profile.BackendDir,
            profile.BrowserAgentDir,
            profile.ExtensionBridgeDir,
            projectDir
        };

        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var current = new DirectoryInfo(dir);
            for (var i = 0; current is not null && i < 8; i++, current = current.Parent)
            {
                yield return current.FullName;
            }
        }
    }

    private static bool TryStartInstalledOllama()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama", "ollama.exe")
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static void AddProviderEnvironment(IDictionary<string, string> environment, RuntimeLaunchProfile profile)
    {
        AddGeminiKeyEnvironment(environment, profile);

        switch (NormalizeProviderId(profile.AiProvider))
        {
            case "local_ollama":
                environment["CURSIVIS_OLLAMA_URL"] = string.IsNullOrWhiteSpace(profile.OllamaUrl) ? "http://127.0.0.1:11434" : profile.OllamaUrl;
                environment["CURSIVIS_LOCAL_MODEL"] = string.IsNullOrWhiteSpace(profile.LocalModel) ? "granite3.2-vision:2b" : profile.LocalModel;
                environment["CURSIVIS_LOCAL_KEEP_ALIVE"] = "90s";
                break;
            case "openai_compatible":
                environment["CURSIVIS_OPENAI_BASE_URL"] = string.IsNullOrWhiteSpace(profile.OpenAiBaseUrl) ? "https://api.openai.com/v1" : profile.OpenAiBaseUrl;
                environment["CURSIVIS_OPENAI_MODEL"] = string.IsNullOrWhiteSpace(profile.OpenAiModel) ? "gpt-4.1-mini" : profile.OpenAiModel;
                if (!string.IsNullOrWhiteSpace(profile.OpenAiApiKey))
                {
                    environment["CURSIVIS_OPENAI_API_KEY"] = profile.OpenAiApiKey;
                }
                break;
            case "hosted_cursivis":
                if (!string.IsNullOrWhiteSpace(profile.HostedApiUrl))
                {
                    environment["CURSIVIS_HOSTED_API_URL"] = profile.HostedApiUrl;
                }

                if (!string.IsNullOrWhiteSpace(profile.HostedToken))
                {
                    environment["CURSIVIS_HOSTED_TOKEN"] = profile.HostedToken;
                }
                break;
            default:
                break;
        }
    }

    private static void AddGeminiKeyEnvironment(IDictionary<string, string> environment, RuntimeLaunchProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            environment["GOOGLE_API_KEY"] = profile.ApiKey;
            environment["GEMINI_API_KEY"] = profile.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(profile.ApiKeys))
        {
            environment["GOOGLE_API_KEYS"] = profile.ApiKeys;
            environment["GEMINI_API_KEYS"] = profile.ApiKeys;
        }
    }

    private static string NormalizeProviderId(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_") switch
        {
            "local" or "ollama" or "local_ollama" => "local_ollama",
            "openai" or "openai_compatible" or "compatible" => "openai_compatible",
            "hosted" or "cursivis" or "hosted_cursivis" or "cursivis_hosted" => "hosted_cursivis",
            _ => "gemini"
        };
    }

    private static string NormalizeBaseUrl(string value)
    {
        return (value ?? string.Empty).Trim().TrimEnd('/');
    }

    private static async Task<bool> IsHealthyAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(url, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForHealthyAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await IsHealthyAsync(url, cancellationToken))
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }
    }

}
