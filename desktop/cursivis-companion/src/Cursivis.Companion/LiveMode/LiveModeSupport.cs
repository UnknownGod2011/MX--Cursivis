using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cursivis.Companion.LiveMode;

using Cursivis.Companion.Services;

public enum LiveModePermissionMode
{
    AlwaysAsk,
    AutoExecute
}

public enum LiveModeVoicePhase
{
    Idle,
    Listening,
    Thinking,
    Executing,
    Speaking,
    Done,
    Error
}

public enum LiveModeAssistantTone
{
    Balanced,
    Concise,
    Friendly,
    Professional
}

public sealed class LiveModeWorkflowSettings
{
    public string Name { get; set; } = string.Empty;
    public string Apps { get; set; } = string.Empty;
    public string Urls { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
}

public sealed class LiveModeSettings
{
    public bool Enabled { get; set; } = true;
    public string Hotkey { get; set; } = "Ctrl+Alt+Q";
    public string CancelHotkey { get; set; } = "Ctrl+Alt+X";
    public int MicrophoneDevice { get; set; } = -1;
    public string AssistantName { get; set; } = "Cursivis";
    public LiveModeAssistantTone AssistantTone { get; set; } = LiveModeAssistantTone.Balanced;
    public string GeminiVoice { get; set; } = "Kore";
    public LiveModePermissionMode LiveModePermissionMode { get; set; } = LiveModePermissionMode.AutoExecute;
    public string PreferredBrowser { get; set; } = string.Empty;
    public List<LiveModeWorkflowSettings> LiveModeWorkflows { get; set; } = [];
}

public sealed class LiveModeSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cursivis");

    public string DataDir { get; }

    public string SettingsPath { get; }

    public LiveModeSettings Current { get; private set; } = new();

    public LiveModeSettingsService(string? appDataDir = null)
    {
        DataDir = string.IsNullOrWhiteSpace(appDataDir)
            ? AppDataDir
            : Path.GetFullPath(appDataDir);
        SettingsPath = Path.Combine(DataDir, "live-mode.json");
    }

    public void Load()
    {
        Directory.CreateDirectory(DataDir);
        try
        {
            if (File.Exists(SettingsPath))
            {
                Current = JsonSerializer.Deserialize<LiveModeSettings>(
                    File.ReadAllText(SettingsPath),
                    JsonOptions) ?? new LiveModeSettings();
            }
        }
        catch (Exception ex)
        {
            LiveModeLog.Warning(ex, "Live Mode settings load failed; using defaults");
            Current = new LiveModeSettings();
        }

        Current.LiveModeWorkflows ??= [];
        Current.Hotkey = SanitizeHotkey(Current.Hotkey, "Ctrl+Alt+Q");
        Current.CancelHotkey = SanitizeHotkey(Current.CancelHotkey, "Ctrl+Alt+X");
        Current.AssistantName = SanitizeAssistantName(Current.AssistantName);
        Current.GeminiVoice = SanitizeGeminiVoice(Current.GeminiVoice);
        Current.PreferredBrowser = BrowserLauncherService.NormalizeBrowser(Current.PreferredBrowser);
        ApplyToState();
        Save();
    }

    public void ApplyToState()
    {
        LiveModeState.SelectedMicrophoneIndex = Current.MicrophoneDevice;
        LiveModeState.AssistantName = Current.AssistantName;
        LiveModeState.AssistantTone = Current.AssistantTone;
        LiveModeState.GeminiVoice = Current.GeminiVoice;
        LiveModeState.CancelHotkey = Current.CancelHotkey;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
    }

    public void SavePermissionMode(LiveModePermissionMode value)
    {
        Current.LiveModePermissionMode = value;
        Save();
    }

    public void SaveEnabled(bool value)
    {
        Current.Enabled = value;
        Save();
    }

    public void SaveHotkey(string value)
    {
        Current.Hotkey = SanitizeHotkey(value, "Ctrl+Alt+Q");
        Save();
    }

    public void SaveCancelHotkey(string value)
    {
        Current.CancelHotkey = SanitizeHotkey(value, "Ctrl+Alt+X");
        LiveModeState.CancelHotkey = Current.CancelHotkey;
        Save();
    }

    public void SaveMicrophoneDevice(int value)
    {
        Current.MicrophoneDevice = value;
        LiveModeState.SelectedMicrophoneIndex = value;
        Save();
    }

    public void SaveAssistantTone(LiveModeAssistantTone value)
    {
        Current.AssistantTone = value;
        LiveModeState.AssistantTone = value;
        Save();
    }

    public void SaveGeminiVoice(string value)
    {
        Current.GeminiVoice = SanitizeGeminiVoice(value);
        LiveModeState.GeminiVoice = Current.GeminiVoice;
        Save();
    }

    public void SavePreferredBrowser(string value)
    {
        Current.PreferredBrowser = BrowserLauncherService.NormalizeBrowser(value);
        Save();
    }

    public LiveModeWorkflowSettings SaveWorkflow(string name, string apps, string urls, string folder)
    {
        var cleanName = CleanInline(name, 48);
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            throw new InvalidOperationException("Workflow name is required.");
        }

        if (string.IsNullOrWhiteSpace(apps) &&
            string.IsNullOrWhiteSpace(urls) &&
            string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("A workflow needs at least one app, website, or folder.");
        }

        var workflow = Current.LiveModeWorkflows.FirstOrDefault(item =>
            string.Equals(item.Name, cleanName, StringComparison.OrdinalIgnoreCase));
        if (workflow is null)
        {
            workflow = new LiveModeWorkflowSettings { Name = cleanName };
            Current.LiveModeWorkflows.Add(workflow);
        }

        workflow.Apps = CleanInline(apps, 300);
        workflow.Urls = CleanInline(urls, 800);
        workflow.Folder = CleanInline(folder, 260);
        Save();
        return workflow;
    }

    public bool DeleteWorkflow(string name)
    {
        var removed = Current.LiveModeWorkflows.RemoveAll(item =>
            string.Equals(item.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    private static string SanitizeAssistantName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Cursivis" : value.Trim();
        name = new string(name.Where(character => !char.IsControl(character)).ToArray());
        return name.Length <= 32 ? name : name[..32];
    }

    private static string SanitizeGeminiVoice(string? value)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Kore", "Orus", "Puck", "Charon", "Aoede", "Zephyr",
            "Leda", "Fenrir", "Achird", "Gacrux", "Sulafat", "Iapetus"
        };
        return supported.Contains(value ?? string.Empty) ? value!.Trim() : "Kore";
    }

    private static string SanitizeHotkey(string? value, string fallback)
    {
        var clean = CleanInline(value, 48);
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
    }

    private static string CleanInline(string? value, int maxLength)
    {
        var clean = string.Join(
            " ",
            (value ?? string.Empty)
                .ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }
}

public static class LiveModeState
{
    private static LiveModeVoicePhase _lastLoggedPhase = LiveModeVoicePhase.Idle;
    private static string _lastLoggedTitle = "Cursivis Live Mode";

    public static event EventHandler<LiveModeStatusChangedEventArgs>? StatusChanged;

    public static string GeminiApiKey { get; set; } = string.Empty;
    public static int SelectedMicrophoneIndex { get; set; } = -1;
    public static double InputLevelDb { get; set; } = -96;
    public static string AssistantName { get; set; } = "Cursivis";
    public static LiveModeAssistantTone AssistantTone { get; set; } = LiveModeAssistantTone.Balanced;
    public static string GeminiVoice { get; set; } = "Kore";
    public static string CancelHotkey { get; set; } = "Ctrl+Alt+X";
    public static LiveModeVoicePhase Phase { get; private set; } = LiveModeVoicePhase.Idle;
    public static string Title { get; private set; } = "Cursivis Live Mode";
    public static string Detail { get; private set; } = "Ready";
    public static DateTime PhaseStartedUtc { get; private set; } = DateTime.UtcNow;

    public static string EffectiveVoiceNoteSavePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Cursivis");

    public static void SetUi(LiveModeVoicePhase phase, string? title, string? detail)
    {
        Phase = phase;
        Title = string.IsNullOrWhiteSpace(title) ? "Cursivis Live Mode" : title.Trim();
        Detail = detail?.Trim() ?? string.Empty;
        PhaseStartedUtc = DateTime.UtcNow;
        if (phase != _lastLoggedPhase ||
            !string.Equals(Title, _lastLoggedTitle, StringComparison.Ordinal))
        {
            LiveModeLog.Info($"boundary=state phase={phase} title={Title}");
            _lastLoggedPhase = phase;
            _lastLoggedTitle = Title;
        }

        StatusChanged?.Invoke(null, new LiveModeStatusChangedEventArgs(phase, Title, Detail));
    }
}

public sealed record LiveModeStatusChangedEventArgs(
    LiveModeVoicePhase Phase,
    string Title,
    string Detail);

public enum LiveModeNotificationKind
{
    Info,
    Warning,
    Error
}

public sealed class LiveModeNotificationService
{
    public event EventHandler<LiveModeNotificationEventArgs>? NotificationRaised;

    public void Info(string title, string message) => Show(LiveModeNotificationKind.Info, title, message);
    public void Warning(string title, string message) => Show(LiveModeNotificationKind.Warning, title, message);
    public void Error(string title, string message) => Show(LiveModeNotificationKind.Error, title, message);

    private void Show(LiveModeNotificationKind kind, string title, string message)
    {
        LiveModeLog.Info($"{title}: {message}");
        NotificationRaised?.Invoke(this, new LiveModeNotificationEventArgs(kind, title, message));
    }
}

public sealed record LiveModeNotificationEventArgs(
    LiveModeNotificationKind Kind,
    string Title,
    string Message);

internal static class LiveModeLog
{
    private static readonly object Lock = new();
    private static readonly string LogFile = Path.Combine(
        LiveModeSettingsService.AppDataDir,
        "Logs",
        "live-mode.log");

    public static void Info(string text) => Write("INFO", text);
    public static void Warning(string text) => Write("WARN", text);
    public static void Warning(Exception ex, string text) => Write("WARN", $"{text}: {ex.Message}");
    public static void Error(Exception ex, string text) => Write("ERROR", $"{text}: {ex.Message}");

    private static void Write(string level, string text)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {CredentialRedactor.Redact(text)}";
        Debug.WriteLine(line);
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Live Mode logging must never interrupt a voice session.
        }
    }
}
