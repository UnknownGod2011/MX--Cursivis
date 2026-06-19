using Cursivis.Companion.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cursivis.Companion.Services;

public sealed class RuntimeLaunchProfileService
{
    private const string ProfileFileName = "runtime-profile.json";
    private const string ProtectedValuePrefix = "dpapi:v1:";
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _profileDir;
    private readonly string _profilePath;

    public RuntimeLaunchProfileService(string? profileDir = null)
    {
        _profileDir = string.IsNullOrWhiteSpace(profileDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cursivis")
            : Path.GetFullPath(profileDir);
        _profilePath = Path.Combine(_profileDir, ProfileFileName);
    }

    public async Task<RuntimeLaunchProfile?> TryLoadAsync()
    {
        if (!File.Exists(_profilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_profilePath);
            var profile = JsonSerializer.Deserialize<RuntimeLaunchProfile>(json, _jsonOptions);
            if (profile is not null)
            {
                profile.ApiKey = UnprotectSecret(profile.ApiKey);
                profile.ApiKeys = UnprotectSecret(profile.ApiKeys);
                profile.OpenAiApiKey = UnprotectSecret(profile.OpenAiApiKey);
                profile.HostedToken = UnprotectSecret(profile.HostedToken);
            }

            return profile;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(RuntimeLaunchProfile profile)
    {
        Directory.CreateDirectory(_profileDir);
        var persistedProfile = new RuntimeLaunchProfile
        {
            BackendDir = profile.BackendDir,
            BrowserAgentDir = profile.BrowserAgentDir,
            ExtensionBridgeDir = profile.ExtensionBridgeDir,
            CompanionProject = profile.CompanionProject,
            CompanionExecutable = profile.CompanionExecutable,
            HotkeyHostExecutable = profile.HotkeyHostExecutable,
            BackendUrl = profile.BackendUrl,
            BrowserAgentUrl = profile.BrowserAgentUrl,
            ExtensionBridgeUrl = profile.ExtensionBridgeUrl,
            AiProvider = NormalizeProviderId(profile.AiProvider),
            OpenAiBaseUrl = string.IsNullOrWhiteSpace(profile.OpenAiBaseUrl) ? "https://api.openai.com/v1" : profile.OpenAiBaseUrl.Trim(),
            OpenAiApiKey = ProtectSecret(profile.OpenAiApiKey),
            OpenAiModel = string.IsNullOrWhiteSpace(profile.OpenAiModel) ? "gpt-4.1-mini" : profile.OpenAiModel.Trim(),
            HostedApiUrl = profile.HostedApiUrl.Trim(),
            HostedToken = ProtectSecret(profile.HostedToken),
            OllamaUrl = string.IsNullOrWhiteSpace(profile.OllamaUrl) ? "http://127.0.0.1:11434" : profile.OllamaUrl.Trim(),
            LocalModel = string.IsNullOrWhiteSpace(profile.LocalModel) ? "granite3.2-vision:2b" : profile.LocalModel.Trim(),
            ApiKey = ProtectSecret(profile.ApiKey),
            ApiKeys = ProtectSecret(profile.ApiKeys),
            EnableStreamingTranscription = profile.EnableStreamingTranscription,
            EnableAutoReplace = profile.EnableAutoReplace,
            AutoReplaceConfidence = profile.AutoReplaceConfidence,
            EnableManagedBrowserFallback = profile.EnableManagedBrowserFallback
        };
        var json = JsonSerializer.Serialize(persistedProfile, _jsonOptions);
        await File.WriteAllTextAsync(_profilePath, json);
    }

    public async Task<bool> UpdateApiKeysAsync(string apiKey)
    {
        var normalized = string.Join(
            ",",
            (apiKey ?? string.Empty)
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal));

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var profile = await TryLoadAsync();
        if (profile is null)
        {
            return false;
        }

        profile.ApiKey = normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? normalized;
        profile.ApiKeys = normalized;
        await SaveAsync(profile);
        return true;
    }

    public async Task SaveAiProviderAsync(
        string aiProvider,
        string ollamaUrl,
        string localModel,
        string openAiBaseUrl = "",
        string openAiApiKey = "",
        string openAiModel = "",
        string hostedApiUrl = "",
        string hostedToken = "")
    {
        var profile = await TryLoadAsync() ?? new RuntimeLaunchProfile();

        profile.AiProvider = NormalizeProviderId(aiProvider);
        profile.OllamaUrl = string.IsNullOrWhiteSpace(ollamaUrl) ? "http://127.0.0.1:11434" : ollamaUrl.Trim();
        profile.LocalModel = string.IsNullOrWhiteSpace(localModel) ? "granite3.2-vision:2b" : localModel.Trim();

        if (!string.IsNullOrWhiteSpace(openAiBaseUrl))
        {
            profile.OpenAiBaseUrl = openAiBaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(openAiApiKey))
        {
            profile.OpenAiApiKey = openAiApiKey.Trim();
        }

        if (!string.IsNullOrWhiteSpace(openAiModel))
        {
            profile.OpenAiModel = openAiModel.Trim();
        }

        if (!string.IsNullOrWhiteSpace(hostedApiUrl))
        {
            profile.HostedApiUrl = hostedApiUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(hostedToken))
        {
            profile.HostedToken = hostedToken.Trim();
        }

        await SaveAsync(profile);
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

    private static string ProtectSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith(ProtectedValuePrefix, StringComparison.Ordinal))
        {
            return value;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        return ProtectedValuePrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(ProtectedValuePrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value[ProtectedValuePrefix.Length..]);
            var unprotectedBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotectedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
