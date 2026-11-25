using Cursivis.Companion.Models;
using Cursivis.Security;
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
        if (await IsHealthyAsync($"{profile.BackendUrl.TrimEnd('/')}/health", cancellationToken, LocalIpc.Backend))
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
            ["GEMINI_MODEL"] = "gemini-3.6-flash",
            ["GEMINI_ROUTER_MODEL"] = "gemini-3.5-flash-lite",
            ["GEMINI_OPTIONS_MODEL"] = "gemini-3.5-flash-lite",
            ["GEMINI_FALLBACK_MODELS"] = "gemini-3.5-flash,gemini-3.5-flash-lite"
        };
        AddProviderEnvironment(environment, profile);

        StartNodeServer(profile, profile.BackendDir, LocalIpc.Backend, environment);
        await WaitForHealthyAsync($"{profile.BackendUrl.TrimEnd('/')}/health", TimeSpan.FromSeconds(30), cancellationToken, LocalIpc.Backend);
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
        if (await IsHealthyAsync($"{profile.BrowserAgentUrl.TrimEnd('/')}/health", cancellationToken, LocalIpc.BrowserAgent))
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
            LocalIpc.BrowserAgent,
            new Dictionary<string, string>
            {
                ["CURSIVIS_BROWSER_CHANNEL"] = "chrome",
                ["CURSIVIS_BROWSER_AGENT_PORT"] = new Uri(profile.BrowserAgentUrl).Port.ToString()
            });

        await WaitForHealthyAsync($"{profile.BrowserAgentUrl.TrimEnd('/')}/health", TimeSpan.FromSeconds(20), cancellationToken, LocalIpc.BrowserAgent);
    }

    private static async Task EnsureExtensionBridgeAsync(RuntimeLaunchProfile profile, CancellationToken cancellationToken)
    {
        RegisterInstalledBrowserHost(profile);
        if (await IsHealthyAsync($"{profile.ExtensionBridgeUrl.TrimEnd('/')}/health", cancellationToken, LocalIpc.BrowserBridge))
        {
            return;
        }

        var hostJs = Path.Combine(profile.ExtensionBridgeDir, "src", "host.js");
        if (!File.Exists(hostJs))
        {
            return;
        }

        StartNodeScript(profile, profile.ExtensionBridgeDir, hostJs, LocalIpc.BrowserBridge, new Dictionary<string, string>
        {
            ["CURSIVIS_EXTENSION_BRIDGE_PORT"] = new Uri(profile.ExtensionBridgeUrl).Port.ToString()
        });
        await WaitForHealthyAsync($"{profile.ExtensionBridgeUrl.TrimEnd('/')}/health", TimeSpan.FromSeconds(15), cancellationToken, LocalIpc.BrowserBridge);
    }

    private static void RegisterInstalledBrowserHost(RuntimeLaunchProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ExtensionBridgeDir))
        {
            return;
        }
        var extensionDir = Path.GetFullPath(Path.Combine(profile.ExtensionBridgeDir, "..", "browser-extension-chromium"));
        var nativeHostExe = Path.GetFullPath(Path.Combine(profile.ExtensionBridgeDir, "..", "..", "app",
            "browser-native-host", "Cursivis.BrowserNativeHost.exe"));
        if (!File.Exists(nativeHostExe) || !File.Exists(Path.Combine(extensionDir, "manifest.json")))
        {
            return;
        }

        try
        {
            BrowserNativeRegistration.Register(nativeHostExe, BrowserNativeRegistration.GetUnpackedExtensionId(extensionDir));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      System.Security.SecurityException or InvalidOperationException)
        {
            Cursivis.Companion.LiveMode.LiveModeLog.Warning("Could not register the optional browser extension.");
        }
    }

    private static void StartNodeServer(RuntimeLaunchProfile profile, string projectDir, string scope, IReadOnlyDictionary<string, string> environment)
    {
        var serverJs = Path.Combine(projectDir, "src", "server.js");
        if (!File.Exists(serverJs))
        {
            return;
        }

        StartNodeScript(profile, projectDir, serverJs, scope, environment);
    }

    private static void StartNodeScript(
        RuntimeLaunchProfile profile,
        string projectDir,
        string scriptPath,
        string scope,
        IReadOnlyDictionary<string, string> environment)
    {
        var nodeExe = TryResolvePortableNodeExe(profile, projectDir);
        if (string.IsNullOrWhiteSpace(nodeExe))
        {
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            WorkingDirectory = projectDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add(scriptPath);
        psi.Environment.Remove("NODE_OPTIONS");
        psi.Environment.Remove("NODE_PATH");
        psi.Environment["NODE_ENV"] = "production";

        foreach (var (key, value) in environment)
        {
            psi.Environment[key] = value;
        }

        SetServiceCredential(psi, LocalIpc.ServiceKeyVariable, scope);
        psi.Environment.Remove(LocalIpc.RelayKeyVariable);
        if (scope == LocalIpc.BrowserBridge)
        {
            SetServiceCredential(psi, LocalIpc.RelayKeyVariable, LocalIpc.BrowserRelay);
        }

        using var process = Process.Start(psi);
    }

    private static void SetServiceCredential(ProcessStartInfo startInfo, string name, string scope)
    {
        var key = LocalIpc.GetServiceKey(scope);
        try
        {
            startInfo.Environment[name] = Convert.ToHexString(key).ToLowerInvariant();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
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

    internal static async Task<bool> IsHealthyAsync(string url, CancellationToken cancellationToken, string? scope = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            return false;
        }

        try
        {
            using var client = scope is null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(3) }
                : LocalIpc.CreateHttpClient(endpoint, scope, TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync(client.BaseAddress ?? endpoint, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task WaitForHealthyAsync(string url, TimeSpan timeout, CancellationToken cancellationToken, string? scope = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (await IsHealthyAsync(url, cancellationToken, scope))
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }
    }

}
