using Cursivis.Companion.Models;
using System.IO;
using System.Text.Json;

namespace Cursivis.Companion.Services;

public sealed class SettingsService
{
    private const string SettingsFileName = "settings.json";
    public const string DefaultGoTriggerShortcut = "Ctrl+Alt+Space";
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _settingsDir;
    private readonly string _settingsPath;

    public SettingsService()
    {
        _settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cursivis");
        _settingsPath = Path.Combine(_settingsDir, SettingsFileName);
    }

    public async Task<InteractionMode?> TryLoadModeAsync()
    {
        var settings = await TryLoadSettingsAsync();
        return settings?.Mode;
    }

    public async Task<CompanionSettings?> TryLoadSettingsAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(_settingsPath);
        var settings = JsonSerializer.Deserialize<SettingsData>(json, _jsonOptions);
        if (settings is null)
        {
            return null;
        }

        var mode = Enum.TryParse<InteractionMode>(settings.Mode, true, out var parsedMode)
            ? parsedMode
            : InteractionMode.Smart;

        var takeActionPreference = Enum.TryParse<TakeActionPromptPreference>(settings.TakeActionPromptPreference, true, out var parsedPreference)
            ? parsedPreference
            : TakeActionPromptPreference.AlwaysAskToRun;
        var themeMode = Enum.TryParse<CompanionThemeMode>(settings.ThemeMode, true, out var parsedThemeMode)
            ? parsedThemeMode
            : CompanionThemeMode.Dark;
        var talkTriggerInputMode = Enum.TryParse<TalkTriggerInputMode>(settings.TalkTriggerInputMode, true, out var parsedTalkTriggerInputMode)
            ? parsedTalkTriggerInputMode
            : TalkTriggerInputMode.Voice;
        var playHapticSound = settings.PlayHapticSound ?? false;

        var showOrbDuringWorkflow = settings.ShowOrbDuringWorkflow ?? true;
        var goTriggerShortcut = string.IsNullOrWhiteSpace(settings.GoTriggerShortcut)
            ? DefaultGoTriggerShortcut
            : settings.GoTriggerShortcut.Trim();

        return new CompanionSettings(mode, showOrbDuringWorkflow, takeActionPreference, themeMode, talkTriggerInputMode, playHapticSound, goTriggerShortcut);
    }

    private static CompanionSettings DefaultSettings() =>
        new(
            InteractionMode.Smart,
            ShowOrbDuringWorkflow: true,
            TakeActionPromptPreference.AlwaysAskToRun,
            CompanionThemeMode.Dark,
            TalkTriggerInputMode.Voice,
            PlayHapticSound: false,
            GoTriggerShortcut: DefaultGoTriggerShortcut);

    public async Task SaveModeAsync(InteractionMode mode)
    {
        var settings = await TryLoadSettingsAsync()
            ?? DefaultSettings();

        await SaveSettingsAsync(settings with { Mode = mode });
    }

    public async Task SaveShowOrbDuringWorkflowAsync(bool showOrbDuringWorkflow)
    {
        var settings = await TryLoadSettingsAsync()
            ?? DefaultSettings();

        await SaveSettingsAsync(settings with { ShowOrbDuringWorkflow = showOrbDuringWorkflow });
    }

    public async Task SaveTakeActionPromptPreferenceAsync(TakeActionPromptPreference preference)
    {
        var settings = await TryLoadSettingsAsync()
            ?? DefaultSettings();

        await SaveSettingsAsync(settings with { TakeActionPromptPreference = preference });
    }

    public async Task SaveThemeModeAsync(CompanionThemeMode themeMode)
    {
        var settings = await TryLoadSettingsAsync()
            ?? DefaultSettings();

        await SaveSettingsAsync(settings with { ThemeMode = themeMode });
    }

    public async Task SaveTalkTriggerInputModeAsync(TalkTriggerInputMode talkTriggerInputMode)
    {
        var settings = await TryLoadSettingsAsync()
            ?? DefaultSettings();

        await SaveSettingsAsync(settings with { TalkTriggerInputMode = talkTriggerInputMode });
    }

    public async Task SavePlayHapticSoundAsync(bool playHapticSound)
    {
        var settings = await TryLoadSettingsAsync()
            ?? DefaultSettings();

        await SaveSettingsAsync(settings with { PlayHapticSound = playHapticSound });
    }

    public async Task SaveGoTriggerShortcutAsync(string shortcut)
    {
        var settings = await TryLoadSettingsAsync()
            ?? DefaultSettings();

        await SaveSettingsAsync(settings with { GoTriggerShortcut = string.IsNullOrWhiteSpace(shortcut) ? DefaultGoTriggerShortcut : shortcut.Trim() });
    }

    public async Task SaveSettingsAsync(CompanionSettings settings)
    {
        Directory.CreateDirectory(_settingsDir);
        var payload = new SettingsData
        {
                Mode = settings.Mode.ToString(),
                ShowOrbDuringWorkflow = settings.ShowOrbDuringWorkflow,
                TakeActionPromptPreference = settings.TakeActionPromptPreference.ToString(),
                ThemeMode = settings.ThemeMode.ToString(),
                TalkTriggerInputMode = settings.TalkTriggerInputMode.ToString(),
                PlayHapticSound = settings.PlayHapticSound,
                GoTriggerShortcut = string.IsNullOrWhiteSpace(settings.GoTriggerShortcut)
                    ? DefaultGoTriggerShortcut
                    : settings.GoTriggerShortcut.Trim()
            };
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json);
    }

    private sealed class SettingsData
    {
        public string Mode { get; set; } = InteractionMode.Smart.ToString();

        public bool? ShowOrbDuringWorkflow { get; set; }

        public string TakeActionPromptPreference { get; set; } = Models.TakeActionPromptPreference.AlwaysAskToRun.ToString();

        public string ThemeMode { get; set; } = Models.CompanionThemeMode.Dark.ToString();

        public string TalkTriggerInputMode { get; set; } = Models.TalkTriggerInputMode.Voice.ToString();

        public bool? PlayHapticSound { get; set; }

        public string GoTriggerShortcut { get; set; } = DefaultGoTriggerShortcut;
    }
}
