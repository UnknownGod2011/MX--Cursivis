using Cursivis.Companion.Services;
using System.IO;

namespace Cursivis.Companion.LiveMode;

public sealed class LiveModeCoordinator : IDisposable
{
    private readonly RuntimeLaunchProfileService _runtimeProfileService;
    private readonly LiveModeSettingsService _settings;
    private readonly LiveModeNotificationService _notifications;
    private readonly IntentMemoryService _memory;
    private readonly LiveModeActionHistoryService _history;
    private readonly LearnedMappingService _learnedMappings;
    private readonly LiveModeAutomationService _automation;
    private readonly GeminiLiveConversationService _conversation;
    private readonly SemaphoreSlim _toggleGate = new(1, 1);
    private bool _disposed;

    public LiveModeCoordinator(
        RuntimeLaunchProfileService runtimeProfileService,
        ExtensionAutomationClient extensionAutomationClient,
        Action openSettings,
        string? appDataDir = null)
    {
        _runtimeProfileService = runtimeProfileService;
        _settings = new LiveModeSettingsService(appDataDir);
        _settings.Load();
        _memory = new IntentMemoryService(appDataDir);
        _memory.Load();
        _history = new LiveModeActionHistoryService(appDataDir);
        _history.Load();
        _learnedMappings = new LearnedMappingService(Path.Combine(_settings.DataDir, "learned-mappings.json"));
        _learnedMappings.Load();
        _notifications = new LiveModeNotificationService();
        _automation = new LiveModeAutomationService(
            _notifications,
            _settings,
            _history,
            _learnedMappings,
            extensionAutomationClient,
            openSettings);
        _conversation = new GeminiLiveConversationService(_notifications, _settings, _memory);
        _conversation.SetToolExecutor(ExecuteToolAsync);
        _conversation.SetUserTranscriptObserver(ObserveUserTranscript);
        _notifications.NotificationRaised += NotificationsOnNotificationRaised;
        LiveModeState.StatusChanged += LiveModeStateOnStatusChanged;
    }

    public event EventHandler<LiveModeStatusChangedEventArgs>? StatusChanged;

    public bool IsActive => _conversation.IsActive;

    public bool Enabled => _settings.Current.Enabled;

    public string Hotkey => _settings.Current.Hotkey;

    public string CancelHotkey => _settings.Current.CancelHotkey;

    public int MicrophoneDevice => _settings.Current.MicrophoneDevice;

    public string GeminiVoice => _settings.Current.GeminiVoice;

    public LiveModeAssistantTone AssistantTone => _settings.Current.AssistantTone;

    public LiveModePermissionMode PermissionMode => _settings.Current.LiveModePermissionMode;

    public string PreferredBrowser => _settings.Current.PreferredBrowser;

    public async Task ToggleAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _toggleGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            LiveModeLog.Info($"boundary=coordinator.toggle active={_conversation.IsActive}");

            if (_conversation.IsActive)
            {
                await _conversation.StopAsync();
                return;
            }

            if (!_settings.Current.Enabled)
            {
                LiveModeState.SetUi(
                    LiveModeVoicePhase.Error,
                    "Live Mode is disabled",
                    "Enable Cursivis Live Mode in Settings before starting a conversation.");
                return;
            }

            var keys = await LoadApiKeysAsync();
            if (keys.Count == 0)
            {
                LiveModeState.SetUi(
                    LiveModeVoicePhase.Error,
                    "Live Mode needs an API key",
                    "Open Cursivis Settings and add a Gemini API key in API LLM.");
                return;
            }

            for (var index = 0; index < keys.Count; index++)
            {
                LiveModeState.GeminiApiKey = keys[index];
                var started = await _conversation.StartAsync(suppressFailureNotification: true);
                if (started)
                {
                    return;
                }
            }

            LiveModeState.GeminiApiKey = string.Empty;
            const string detail =
                "None of the saved Gemini API keys could start a Live session. Check the keys and internet connection, then try again.";
            LiveModeState.SetUi(LiveModeVoicePhase.Error, "Live Mode could not connect", detail);
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _toggleGate.WaitAsync();
        try
        {
            if (_conversation.IsActive)
            {
                LiveModeLog.Info("boundary=coordinator.stop");
                await _conversation.StopAsync();
            }
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SaveEnabled(enabled);
        if (!enabled)
        {
            await StopAsync();
        }

        LiveModeState.SetUi(
            LiveModeVoicePhase.Idle,
            "Cursivis Live Mode",
            enabled ? "Enabled and ready." : "Disabled.");
    }

    public void SetHotkey(string hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SaveHotkey(hotkey);
    }

    public void SetMicrophoneDevice(int deviceNumber)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SaveMicrophoneDevice(deviceNumber);
    }

    public void SetGeminiVoice(string voice)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SaveGeminiVoice(voice);
    }

    public void SetAssistantTone(LiveModeAssistantTone tone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SaveAssistantTone(tone);
    }

    public void SetPermissionMode(LiveModePermissionMode permissionMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SavePermissionMode(permissionMode);
        LiveModeState.SetUi(
            LiveModeState.Phase,
            "Cursivis Live Mode",
            permissionMode == LiveModePermissionMode.AlwaysAsk
                ? "Permission mode: ask before computer actions."
                : "Permission mode: routine actions run automatically; irreversible system actions still require confirmation.");
    }

    public void SetCancelHotkey(string hotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SaveCancelHotkey(hotkey);
    }

    public void SetPreferredBrowser(string browser)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings.SavePreferredBrowser(browser);
    }

    private async Task<IReadOnlyList<string>> LoadApiKeysAsync()
    {
        var profile = await _runtimeProfileService.TryLoadAsync();
        var raw = !string.IsNullOrWhiteSpace(profile?.ApiKeys)
            ? profile.ApiKeys
            : profile?.ApiKey;

        return (raw ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<Dictionary<string, object>> ExecuteToolAsync(
        string toolName,
        System.Text.Json.JsonElement args,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var normalized = (toolName ?? string.Empty).Trim();
        LiveModeLog.Info($"boundary=tool.request name={normalized}");
        LiveModeState.SetUi(
            LiveModeVoicePhase.Executing,
            LiveModeState.AssistantName,
            HumanizeTool(normalized));

        try
        {
            var result = await _automation.ExecuteAsync(normalized, args, token);
            var ok = result.TryGetValue("ok", out var value) && value is true;
            LiveModeLog.Info($"boundary=tool.result name={normalized} ok={ok}");
            return result;
        }
        catch (OperationCanceledException)
        {
            LiveModeLog.Info($"boundary=tool.cancelled name={normalized}");
            throw;
        }
        catch (Exception ex)
        {
            LiveModeLog.Error(ex, $"Live Mode tool failed: {normalized}");
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["supported"] = true,
                ["needs_clarification"] = false,
                ["confirmation_required"] = false,
                ["message"] = $"{HumanizeTool(normalized)} failed: {ex.Message}",
            };
        }
    }

    private void ObserveUserTranscript(string text)
    {
        LiveModeLog.Info($"boundary=transcript.received chars={(text ?? string.Empty).Length}");
        _automation.ObserveUserTranscript(text);
    }

    private static string HumanizeTool(string toolName) => toolName switch
    {
        "open_app" => "Opening app",
        "open_url" => "Opening link",
        "open_folder" => "Opening folder",
        "open_path" => "Opening path",
        "window_action" => "Controlling window",
        "browser_action" => "Controlling browser",
        "web_search" => "Searching the web",
        "get_desktop_context" => "Reading desktop context",
        "get_browser_context" => "Reading browser context",
        "get_clipboard_text" => "Reading clipboard",
        "get_selected_text" => "Reading selected text",
        "replace_selected_text" => "Replacing selected text",
        "type_text" => "Typing text",
        "press_key" => "Pressing key",
        "search_files" => "Searching files",
        "save_note" => "Saving note",
        "add_todo" => "Adding task",
        "list_todos" => "Reading tasks",
        "complete_todo" => "Completing task",
        "set_timer" => "Setting timer",
        "system_control" => "Adjusting system",
        "system_status" => "Reading system status",
        "play_media" => "Opening media",
        "open_service_page" => "Opening service",
        "open_camera" => "Opening camera",
        "take_photo" => "Taking photo",
        "take_screenshot" => "Taking screenshot",
        "inspect_screen" => "Inspecting screen",
        "virtual_desktop_action" => "Controlling desktops",
        "windows_recording_action" => "Controlling recording",
        "create_workflow" => "Saving workflow",
        "remember_routine" => "Remembering routine",
        "list_workflows" => "Reading workflows",
        "run_workflow" => "Running workflow",
        "delete_workflow" => "Deleting workflow",
        "remember_app_alias" => "Remembering app name",
        "remember_path_alias" => "Remembering path",
        "remember_link_alias" => "Remembering link",
        "remember_workflow_alias" => "Remembering workflow",
        "list_learned_mappings" => "Reading learned choices",
        "forget_learned_mapping" => "Forgetting learned choice",
        "set_browser_preference" => "Saving browser preference",
        "request_sensitive_action" => "Requesting confirmation",
        "confirm_sensitive_action" => "Confirming action",
        "cancel_sensitive_action" => "Cancelling action",
        "open_gmail_draft" => "Preparing Gmail draft",
        "prepare_whatsapp_message" => "Preparing WhatsApp message",
        "copy_text" => "Copying text",
        _ => "Running safe action",
    };

    private void LiveModeStateOnStatusChanged(object? sender, LiveModeStatusChangedEventArgs e) =>
        StatusChanged?.Invoke(this, e);

    private void NotificationsOnNotificationRaised(object? sender, LiveModeNotificationEventArgs e)
    {
        if (e.Kind == LiveModeNotificationKind.Error)
        {
            StatusChanged?.Invoke(
                this,
                new LiveModeStatusChangedEventArgs(LiveModeVoicePhase.Error, e.Title, e.Message));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LiveModeState.StatusChanged -= LiveModeStateOnStatusChanged;
        _notifications.NotificationRaised -= NotificationsOnNotificationRaised;
        _conversation.Dispose();
        _automation.Dispose();
        LiveModeState.GeminiApiKey = string.Empty;
    }
}
