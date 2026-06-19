using Cursivis.Companion.Controllers;
using Cursivis.Companion.Infrastructure;
using Cursivis.Companion.LiveMode;
using Cursivis.Companion.Models;
using Cursivis.Companion.Services;
using NAudio.Wave;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Cursivis.Companion;

public partial class MainWindow : Window
{
    private const int TriggerHotkeyId = 0xCA11;
    private const int TakeActionHotkeyId = 0xCA12;
    private const int VoiceHotkeyId = 0xCA13;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private readonly TriggerController _triggerController;
    private readonly SettingsService _settingsService;
    private readonly LogitechRuntimeStatusService _logitechRuntimeStatusService;
    private readonly RuntimeLaunchProfileService _runtimeLaunchProfileService;
    private readonly GeminiClient _runtimeGeminiClient;
    private readonly LiveModeCoordinator? _liveModeCoordinator;
    private int _lastDialValue;
    private bool _suppressDialEvents;
    private bool _isModeInitialized;
    private CancellationTokenSource? _longPressHoldCts;
    private Task? _longPressHoldTask;
    private HwndSource? _hwndSource;
    private readonly DispatcherTimer _logitechStatusTimer;
    private bool _showOrbDuringWorkflow;
    private TakeActionPromptPreference _takeActionPromptPreference;
    private CompanionThemeMode _themeMode;
    private TalkTriggerInputMode _talkTriggerInputMode;
    private bool _playHapticSound;
    private string _goTriggerShortcut;
    private bool _isCapturingGoShortcut;
    private bool _isCapturingLiveModeHotkey;
    private bool _isCapturingLiveModeCancelHotkey;
    private bool _isLiveModeUiInitialized;
    private bool _isUpdatingThemeSelection;
    private bool _isUpdatingApiKey;
    private bool _isUpdatingAiBackend;
    private CancellationTokenSource? _localModelDownloadCts;
    private bool _allowWindowClose;

    public MainWindow(
        TriggerController triggerController,
        SettingsService settingsService,
        CompanionSettings initialSettings,
        LiveModeCoordinator? liveModeCoordinator = null)
    {
        _triggerController = triggerController;
        _settingsService = settingsService;
        _logitechRuntimeStatusService = new LogitechRuntimeStatusService();
        _runtimeLaunchProfileService = new RuntimeLaunchProfileService();
        _runtimeGeminiClient = new GeminiClient();
        _liveModeCoordinator = liveModeCoordinator;
        _showOrbDuringWorkflow = initialSettings.ShowOrbDuringWorkflow;
        _takeActionPromptPreference = initialSettings.TakeActionPromptPreference;
        _themeMode = initialSettings.ThemeMode;
        _talkTriggerInputMode = initialSettings.TalkTriggerInputMode;
        _playHapticSound = initialSettings.PlayHapticSound;
        _goTriggerShortcut = NormalizeShortcutDisplay(initialSettings.GoTriggerShortcut) ?? SettingsService.DefaultGoTriggerShortcut;
        InitializeComponent();

        _triggerController.OnActionChange += TriggerControllerOnActionChange;
        _triggerController.OnProcessingStart += TriggerControllerOnProcessingStart;
        _triggerController.OnProcessingComplete += TriggerControllerOnProcessingComplete;
        _triggerController.OnModeChanged += TriggerControllerOnModeChanged;
        CompanionThemeService.ThemeChanged += CompanionThemeServiceOnThemeChanged;
        _triggerController.SetShowOrbDuringWorkflow(_showOrbDuringWorkflow);
        _triggerController.SetTakeActionPromptPreference(_takeActionPromptPreference);
        _triggerController.SetTalkTriggerInputMode(_talkTriggerInputMode);

        SetModeCombo(initialSettings.Mode);
        SetTakeActionPromptCombo(_takeActionPromptPreference);
        SetThemeCombo(_themeMode);
        SetTalkTriggerInputCombo(_talkTriggerInputMode);
        ShowOrbDuringWorkflowCheckBox.IsChecked = _showOrbDuringWorkflow;
        PlayHapticSoundCheckBox.IsChecked = _playHapticSound;
        GoShortcutTextBox.Text = _goTriggerShortcut;
        GoShortcutStatusText.Text = $"Gesture shortcut ready: {_goTriggerShortcut}.";
        UpdateTalkTriggerUi();
        SetAiBackendCombo("gemini");
        SetLocalModelCombo("granite3.2-vision:2b");
        UpdateApiKeyLineNumbers();
        ApiKeyTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ApiKeyTextBox_OnScrollChanged));
        DataObject.AddPastingHandler(ApiKeyTextBox, ApiKeyTextBox_OnPaste);
        _ = LoadRuntimeApiKeyIntoTextboxAsync();
        _ = LoadRuntimeAiBackendAsync();
        SetLiveModePermissionCombo(_liveModeCoordinator?.PermissionMode ?? LiveModePermissionMode.AutoExecute);
        InitializeLiveModeControls();
        UpdateLiveModeStatus(new LiveModeStatusChangedEventArgs(
            LiveModeVoicePhase.Idle,
            "Cursivis Live Mode",
            "Ready. Press the Live Mode action to start a voice session."));
        _isModeInitialized = true;
        StatusText.Text = $"Status: Ready in {initialSettings.Mode} mode. Press Trigger for text flow.";
        _logitechStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _logitechStatusTimer.Tick += LogitechStatusTimerOnTick;
        RefreshLogitechRuntimeStatus();
        _logitechStatusTimer.Start();
        SourceInitialized += MainWindow_OnSourceInitialized;
        Closing += MainWindow_OnClosing;
    }

    public event EventHandler<bool>? HapticSoundPreferenceChanged;

    public void UpdateLiveModeStatus(LiveModeStatusChangedEventArgs status)
    {
        LiveModeStatusText.Text = $"{status.Title}: {status.Detail}".Trim();
        ToggleLiveModeButton.Content = status.Phase is LiveModeVoicePhase.Listening
            or LiveModeVoicePhase.Thinking
            or LiveModeVoicePhase.Executing
            or LiveModeVoicePhase.Speaking
            ? "Stop Live Mode"
            : "Start Live Mode";
    }

    private async void ToggleLiveModeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_liveModeCoordinator is null)
        {
            LiveModeStatusText.Text = "Live Mode is unavailable in this runtime.";
            return;
        }

        ToggleLiveModeButton.IsEnabled = false;
        try
        {
            await _liveModeCoordinator.ToggleAsync();
        }
        finally
        {
            ToggleLiveModeButton.IsEnabled = true;
        }
    }

    private void LiveModePermissionCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_liveModeCoordinator is null ||
            LiveModePermissionCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value ||
            !Enum.TryParse<LiveModePermissionMode>(value, true, out var permissionMode))
        {
            return;
        }

        _liveModeCoordinator.SetPermissionMode(permissionMode);
    }

    private void SetLiveModePermissionCombo(LiveModePermissionMode permissionMode)
    {
        foreach (var item in LiveModePermissionCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string value &&
                string.Equals(value, permissionMode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                LiveModePermissionCombo.SelectedItem = item;
                return;
            }
        }

        LiveModePermissionCombo.SelectedIndex = 0;
    }

    private void InitializeLiveModeControls()
    {
        var enabled = _liveModeCoordinator?.Enabled ?? false;
        LiveModeEnabledCheckBox.IsChecked = enabled;
        LiveModeHotkeyTextBox.Text = _liveModeCoordinator?.Hotkey ?? "Ctrl+Alt+Q";
        LiveModeCancelHotkeyTextBox.Text = _liveModeCoordinator?.CancelHotkey ?? "Ctrl+Alt+X";

        LiveModeMicrophoneCombo.Items.Clear();
        LiveModeMicrophoneCombo.Items.Add(new ComboBoxItem
        {
            Content = "System default microphone",
            Tag = -1,
        });
        try
        {
            for (var index = 0; index < WaveInEvent.DeviceCount; index++)
            {
                var capabilities = WaveInEvent.GetCapabilities(index);
                LiveModeMicrophoneCombo.Items.Add(new ComboBoxItem
                {
                    Content = capabilities.ProductName,
                    Tag = index,
                });
            }
        }
        catch
        {
            // The default microphone remains available if device enumeration fails.
        }

        SelectComboItemByTag(
            LiveModeMicrophoneCombo,
            (_liveModeCoordinator?.MicrophoneDevice ?? -1).ToString());
        SelectComboItemByTag(
            LiveModeVoiceCombo,
            _liveModeCoordinator?.GeminiVoice ?? "Kore");
        SelectComboItemByTag(
            LiveModeToneCombo,
            (_liveModeCoordinator?.AssistantTone ?? LiveModeAssistantTone.Balanced).ToString());
        SelectComboItemByTag(
            LiveModeBrowserCombo,
            string.IsNullOrWhiteSpace(_liveModeCoordinator?.PreferredBrowser)
                ? "default"
                : _liveModeCoordinator.PreferredBrowser);
        _isLiveModeUiInitialized = true;
        UpdateLiveModeControlAvailability(enabled);
    }

    private static void SelectComboItemByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private async void LiveModeEnabledCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLiveModeUiInitialized || _liveModeCoordinator is null)
        {
            return;
        }

        var enabled = LiveModeEnabledCheckBox.IsChecked == true;
        await _liveModeCoordinator.SetEnabledAsync(enabled);
        UpdateLiveModeControlAvailability(enabled);
        var registrationMessage = await RestartHotkeyHostForLiveModeAsync();
        if (!string.IsNullOrWhiteSpace(registrationMessage))
        {
            LiveModeStatusText.Text = registrationMessage;
        }
    }

    private void UpdateLiveModeControlAvailability(bool enabled)
    {
        LiveModeHotkeyTextBox.IsEnabled = enabled;
        CaptureLiveModeHotkeyButton.IsEnabled = enabled;
        ConnectLiveModeHotkeyButton.IsEnabled = enabled;
        LiveModeCancelHotkeyTextBox.IsEnabled = enabled;
        CaptureLiveModeCancelHotkeyButton.IsEnabled = enabled;
        ConnectLiveModeCancelHotkeyButton.IsEnabled = enabled;
        LiveModeMicrophoneCombo.IsEnabled = enabled;
        LiveModeVoiceCombo.IsEnabled = enabled;
        LiveModeToneCombo.IsEnabled = enabled;
        LiveModeBrowserCombo.IsEnabled = enabled;
        LiveModePermissionCombo.IsEnabled = enabled;
        ToggleLiveModeButton.IsEnabled = enabled;
    }

    private void CaptureLiveModeHotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isCapturingLiveModeHotkey = true;
        LiveModeHotkeyTextBox.Focus();
        LiveModeHotkeyTextBox.SelectAll();
        LiveModeStatusText.Text = "Press the shortcut you want to use for Cursivis Live Mode.";
    }

    private void LiveModeHotkeyTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingLiveModeHotkey && !LiveModeHotkeyTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _isCapturingLiveModeHotkey = false;
            LiveModeHotkeyTextBox.Text = _liveModeCoordinator?.Hotkey ?? "Ctrl+Alt+Q";
            LiveModeStatusText.Text = "Live Mode shortcut unchanged.";
            return;
        }

        if (!TryShortcutFromKeyEvent(e, out var shortcut, out var validationMessage))
        {
            LiveModeStatusText.Text = validationMessage;
            return;
        }

        _isCapturingLiveModeHotkey = false;
        LiveModeHotkeyTextBox.Text = shortcut;
        LiveModeStatusText.Text = $"Detected {shortcut}. Click Connect to activate it.";
    }

    private async void ConnectLiveModeHotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_liveModeCoordinator is null)
        {
            return;
        }

        var shortcut = NormalizeShortcutDisplay(LiveModeHotkeyTextBox.Text);
        var validationMessage = "Shortcut must include Ctrl or Alt plus a key.";
        if (shortcut is null ||
            !TryParseShortcut(shortcut, out _, out _, out validationMessage))
        {
            LiveModeStatusText.Text = validationMessage;
            return;
        }

        var reserved = new[]
        {
            _goTriggerShortcut,
            "Ctrl+Alt+A",
            "Ctrl+Alt+V",
            _liveModeCoordinator.CancelHotkey,
        };
        if (reserved.Any(value => string.Equals(
                NormalizeShortcutDisplay(value),
                shortcut,
                StringComparison.OrdinalIgnoreCase)))
        {
            LiveModeStatusText.Text =
                $"{shortcut} is already used by another Cursivis action. Choose a different shortcut.";
            return;
        }

        _liveModeCoordinator.SetHotkey(shortcut);
        LiveModeHotkeyTextBox.Text = shortcut;
        var registrationMessage = await RestartHotkeyHostForLiveModeAsync();
        LiveModeStatusText.Text = string.IsNullOrWhiteSpace(registrationMessage)
            ? $"Live Mode shortcut connected: {shortcut}. {_liveModeCoordinator.CancelHotkey} stops the current session."
            : registrationMessage;
    }

    private void CaptureLiveModeCancelHotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isCapturingLiveModeCancelHotkey = true;
        LiveModeCancelHotkeyTextBox.Focus();
        LiveModeCancelHotkeyTextBox.SelectAll();
        LiveModeStatusText.Text = "Press the shortcut you want to use to stop Cursivis Live Mode.";
    }

    private void LiveModeCancelHotkeyTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingLiveModeCancelHotkey && !LiveModeCancelHotkeyTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _isCapturingLiveModeCancelHotkey = false;
            LiveModeCancelHotkeyTextBox.Text = _liveModeCoordinator?.CancelHotkey ?? "Ctrl+Alt+X";
            LiveModeStatusText.Text = "Live Mode stop shortcut unchanged.";
            return;
        }

        if (!TryShortcutFromKeyEvent(e, out var shortcut, out var validationMessage))
        {
            LiveModeStatusText.Text = validationMessage;
            return;
        }

        _isCapturingLiveModeCancelHotkey = false;
        LiveModeCancelHotkeyTextBox.Text = shortcut;
        LiveModeStatusText.Text = $"Detected {shortcut}. Click Connect to activate it.";
    }

    private async void ConnectLiveModeCancelHotkeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_liveModeCoordinator is null)
        {
            return;
        }

        var shortcut = NormalizeShortcutDisplay(LiveModeCancelHotkeyTextBox.Text);
        var validationMessage = "Shortcut must include Ctrl or Alt plus a key.";
        if (shortcut is null ||
            !TryParseShortcut(shortcut, out _, out _, out validationMessage))
        {
            LiveModeStatusText.Text = validationMessage;
            return;
        }

        var reserved = new[]
        {
            _goTriggerShortcut,
            "Ctrl+Alt+A",
            "Ctrl+Alt+V",
            _liveModeCoordinator.Hotkey,
        };
        if (reserved.Any(value => string.Equals(
                NormalizeShortcutDisplay(value),
                shortcut,
                StringComparison.OrdinalIgnoreCase)))
        {
            LiveModeStatusText.Text =
                $"{shortcut} is already used by another Cursivis action. Choose a different shortcut.";
            return;
        }

        _liveModeCoordinator.SetCancelHotkey(shortcut);
        LiveModeCancelHotkeyTextBox.Text = shortcut;
        var registrationMessage = await RestartHotkeyHostForLiveModeAsync();
        LiveModeStatusText.Text = string.IsNullOrWhiteSpace(registrationMessage)
            ? $"Live Mode stop shortcut connected: {shortcut}."
            : registrationMessage;
    }

    private void LiveModeMicrophoneCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLiveModeUiInitialized ||
            _liveModeCoordinator is null ||
            LiveModeMicrophoneCombo.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var deviceNumber))
        {
            return;
        }

        _liveModeCoordinator.SetMicrophoneDevice(deviceNumber);
        LiveModeStatusText.Text =
            "Microphone saved. It will be used the next time Live Mode starts.";
    }

    private void LiveModeVoiceCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLiveModeUiInitialized ||
            _liveModeCoordinator is null ||
            LiveModeVoiceCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string voice)
        {
            return;
        }

        _liveModeCoordinator.SetGeminiVoice(voice);
        LiveModeStatusText.Text =
            "Assistant voice saved. It will be used the next time Live Mode starts.";
    }

    private void LiveModeToneCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLiveModeUiInitialized ||
            _liveModeCoordinator is null ||
            LiveModeToneCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value ||
            !Enum.TryParse<LiveModeAssistantTone>(value, true, out var tone))
        {
            return;
        }

        _liveModeCoordinator.SetAssistantTone(tone);
        LiveModeStatusText.Text = "Live Mode response style saved.";
    }

    private void LiveModeBrowserCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLiveModeUiInitialized ||
            _liveModeCoordinator is null ||
            LiveModeBrowserCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string browser)
        {
            return;
        }

        _liveModeCoordinator.SetPreferredBrowser(browser);
        LiveModeStatusText.Text = browser == "default"
            ? "Links will use the Windows default browser."
            : $"Live Mode browser preference saved: {item.Content}.";
    }

    private async Task<string?> RestartHotkeyHostForLiveModeAsync()
    {
        try
        {
            var restartStartedUtc = DateTime.UtcNow;
            var startupRegistrationService = new StartupRegistrationService();
            await startupRegistrationService.EnsureRegisteredAsync();
            var hotkeyHostService = new HotkeyHostService();
            await hotkeyHostService.RestartAsync();
            var registration = await WaitForHotkeyRegistrationStatusAsync(restartStartedUtc);
            if (registration?.LiveModeRegistered == false)
            {
                return $"{registration.LiveModeHotkey} is already used by Windows or another app. Choose another Live Mode shortcut.";
            }

            if (registration?.CancelLiveModeRegistered == false)
            {
                return $"Live Mode is connected, but {registration.CancelLiveModeHotkey} could not be registered as the stop shortcut.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Live Mode setting saved, but the shortcut host needs attention: {ex.Message}";
        }
    }

    private static async Task<HotkeyRegistrationStatus?> WaitForHotkeyRegistrationStatusAsync(
        DateTime restartStartedUtc)
    {
        var statusPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cursivis",
            "hotkey-registration.json");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(statusPath))
                {
                    var json = await File.ReadAllTextAsync(statusPath);
                    var status = JsonSerializer.Deserialize<HotkeyRegistrationStatus>(
                        json,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (status is not null &&
                        status.UpdatedUtc >= restartStartedUtc.AddSeconds(-1))
                    {
                        return status;
                    }
                }
            }
            catch
            {
                // Retry while the host replaces its status file.
            }

            await Task.Delay(100);
        }

        return null;
    }

    private sealed record HotkeyRegistrationStatus(
        DateTime UpdatedUtc,
        bool GoRegistered,
        bool TakeActionRegistered,
        bool VoiceRegistered,
        bool? LiveModeRegistered,
        bool? CancelLiveModeRegistered,
        string LiveModeHotkey,
        string CancelLiveModeHotkey);

    protected override void OnClosed(EventArgs e)
    {
        CancelLongPressSession();
        Closing -= MainWindow_OnClosing;
        UnregisterHotkeys();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        SourceInitialized -= MainWindow_OnSourceInitialized;
        _logitechStatusTimer.Stop();
        _logitechStatusTimer.Tick -= LogitechStatusTimerOnTick;
        _triggerController.OnActionChange -= TriggerControllerOnActionChange;
        _triggerController.OnProcessingStart -= TriggerControllerOnProcessingStart;
        _triggerController.OnProcessingComplete -= TriggerControllerOnProcessingComplete;
        _triggerController.OnModeChanged -= TriggerControllerOnModeChanged;
        CompanionThemeService.ThemeChanged -= CompanionThemeServiceOnThemeChanged;
        ApiKeyTextBox.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ApiKeyTextBox_OnScrollChanged));
        DataObject.RemovePastingHandler(ApiKeyTextBox, ApiKeyTextBox_OnPaste);
        _runtimeGeminiClient.Dispose();
        base.OnClosed(e);
    }

    private async void TriggerButton_OnClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Status: Trigger pressed.";
        await _triggerController.HandleTapAsync(CancellationToken.None);
    }

    private async void TakeActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Status: Take Action pressed.";
        await _triggerController.HandleTakeActionAsync(CancellationToken.None);
    }

    private void LongPressButton_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_talkTriggerInputMode == TalkTriggerInputMode.Text)
        {
            StatusText.Text = "Status: Text prompt opened for the talk trigger.";
            _ = _triggerController.HandleLongPressAsync(CancellationToken.None);
            e.Handled = true;
            return;
        }

        if (_longPressHoldTask is not null && !_longPressHoldTask.IsCompleted)
        {
            return;
        }

        CancelLongPressSession();
        _longPressHoldCts = new CancellationTokenSource();
        _longPressHoldTask = _triggerController.HandleLongPressAsync(_longPressHoldCts.Token);
        StatusText.Text = "Status: Listening... hold button, release to send.";
        if (sender is ButtonBase button)
        {
            button.CaptureMouse();
        }

        e.Handled = true;
    }

    private async void LongPressButton_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_talkTriggerInputMode == TalkTriggerInputMode.Text)
        {
            e.Handled = true;
            return;
        }

        await FinalizeLongPressSessionAsync();
        if (sender is ButtonBase button)
        {
            button.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    private async void LongPressButton_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_talkTriggerInputMode == TalkTriggerInputMode.Text)
        {
            return;
        }

        if (sender is not ButtonBase button || button.IsPressed)
        {
            return;
        }

        await FinalizeLongPressSessionAsync();
    }

    private async void MainWindow_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_talkTriggerInputMode == TalkTriggerInputMode.Text)
        {
            return;
        }

        if (_longPressHoldTask is null)
        {
            return;
        }

        await FinalizeLongPressSessionAsync();
    }

    private async void DialPressButton_OnClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Status: Image selection started.";
        await _triggerController.HandleImageSelectionAsync(CancellationToken.None);
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        _allowWindowClose = true;
        Application.Current.Shutdown();
    }

    private async void SetApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingApiKey)
        {
            return;
        }

        _isUpdatingApiKey = true;
        SetApiKeyButton.IsEnabled = false;
        var originalContent = SetApiKeyButton.Content;
        SetApiKeyButton.Content = "Saving...";

        try
        {
            await SaveApiKeyPoolFromUiAsync(requireKey: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Status: Failed to update API key pool. {ex.Message}";
            AiBackendStatusText.Text = $"AI backend: API key pool save failed. {ex.Message}";
        }
        finally
        {
            _isUpdatingApiKey = false;
            SetApiKeyButton.IsEnabled = true;
            SetApiKeyButton.Content = originalContent;
        }
    }

    private async void UseApiBackendButton_OnClick(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedAiProvider();
        if (provider == "local_ollama")
        {
            await UseLocalBackendAsync();
            return;
        }

        if (provider == "hosted_cursivis")
        {
            AiBackendStatusText.Text = "AI backend: Hosted Cursivis AI is coming soon. Use API LLM or Local LLM for now.";
            AiBackendHealthText.Text = "Health: Hosted service is not enabled in this build.";
            return;
        }

        if (provider == "gemini")
        {
            try
            {
                await SaveApiKeyPoolFromUiAsync(requireKey: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AiBackendStatusText.Text = $"AI backend: API key pool save failed. {ex.Message}";
                return;
            }
        }

        await ApplyAiBackendAsync(provider, testAfterApply: false);
    }

    private void CaptureGoShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isCapturingGoShortcut = true;
        GoShortcutTextBox.Focus();
        GoShortcutTextBox.SelectAll();
        GoShortcutStatusText.Text = "Press the shortcut you want to use for Cursivis Go.";
        StatusText.Text = "Status: Waiting for Go trigger shortcut.";
    }

    private void GoShortcutTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturingGoShortcut && !GoShortcutTextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _isCapturingGoShortcut = false;
            GoShortcutTextBox.Text = _goTriggerShortcut;
            GoShortcutStatusText.Text = $"Gesture shortcut unchanged: {_goTriggerShortcut}.";
            return;
        }

        if (!TryShortcutFromKeyEvent(e, out var shortcut, out var validationMessage))
        {
            GoShortcutStatusText.Text = validationMessage;
            return;
        }

        _isCapturingGoShortcut = false;
        GoShortcutTextBox.Text = shortcut;
        GoShortcutStatusText.Text = $"Detected shortcut: {shortcut}. Click Connect to activate it.";
    }

    private async void ConnectGoShortcutButton_OnClick(object sender, RoutedEventArgs e)
    {
        var shortcut = NormalizeShortcutDisplay(GoShortcutTextBox.Text);
        var validationMessage = "Shortcut must include Ctrl or Alt plus a key.";
        var isValidShortcut = shortcut is not null && TryParseShortcut(shortcut, out _, out _, out validationMessage);
        if (!isValidShortcut)
        {
            GoShortcutStatusText.Text = validationMessage;
            StatusText.Text = "Status: Choose a valid shortcut before connecting.";
            return;
        }

        var connectedShortcut = shortcut ?? SettingsService.DefaultGoTriggerShortcut;
        _goTriggerShortcut = connectedShortcut;
        GoShortcutTextBox.Text = connectedShortcut;
        await _settingsService.SaveGoTriggerShortcutAsync(connectedShortcut);
        ReconnectGlobalHotkeys();

        try
        {
            var startupRegistrationService = new StartupRegistrationService();
            await startupRegistrationService.EnsureRegisteredAsync();
            var hotkeyHostService = new HotkeyHostService();
            await hotkeyHostService.RestartAsync();
            GoShortcutStatusText.Text = $"Connected: map your MX gesture button to {connectedShortcut} in Logi Options+.";
            StatusText.Text = $"Status: Cursivis Go shortcut connected to {connectedShortcut}.";
        }
        catch (Exception ex)
        {
            GoShortcutStatusText.Text = $"Saved {connectedShortcut}, but hotkey host restart needs attention: {ex.Message}";
            StatusText.Text = "Status: Shortcut saved. Restart Cursivis if the gesture button does not respond.";
        }

        UpdateTalkTriggerUi();
    }

    private async void TestApiBackendButton_OnClick(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedAiProvider();
        if (provider == "hosted_cursivis")
        {
            AiBackendStatusText.Text = "AI backend: Hosted Cursivis AI is coming soon.";
            AiBackendHealthText.Text = "Health: Hosted service is not enabled in this build.";
            return;
        }

        if (provider == "gemini")
        {
            try
            {
                await SaveApiKeyPoolFromUiAsync(requireKey: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AiBackendStatusText.Text = $"AI backend: API key pool save failed. {ex.Message}";
                return;
            }
        }

        await TestAiBackendAsync(provider);
    }

    private void AiBackendCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var provider = GetSelectedAiProvider();
        UpdateAiBackendModeUi(provider);
        if (provider == "local_ollama")
        {
            _ = CheckSelectedLocalModelStatusAsync();
        }
    }

    private void LocalModelCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAiBackendActiveIndicator(GetSelectedAiProvider());
        if (GetSelectedAiProvider() == "local_ollama")
        {
            _ = CheckSelectedLocalModelStatusAsync();
        }
    }

    private async void UseLocalBackendButton_OnClick(object sender, RoutedEventArgs e)
    {
        await UseLocalBackendAsync();
    }

    private async void CheckLocalBackendButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingAiBackend)
        {
            return;
        }

        _isUpdatingAiBackend = true;
        SetAiBackendControlsEnabled(false);
        AiBackendStatusText.Text = "AI backend: Checking Ollama and the selected local model...";
        try
        {
            var status = await _runtimeGeminiClient.GetLocalLlmStatusAsync(
                GetOllamaUrl(),
                GetSelectedLocalModel(),
                CancellationToken.None);
            AiBackendStatusText.Text = FormatLocalStatus(status);
            AiBackendHealthText.Text = FormatLocalHealth(status);
        }
        catch (Exception ex)
        {
            AiBackendStatusText.Text = $"AI backend: Local check failed. {ex.Message}";
            AiBackendHealthText.Text = "Health: Local LLM check failed.";
        }
        finally
        {
            _isUpdatingAiBackend = false;
            SetAiBackendControlsEnabled(true);
        }
    }

    private async Task CheckSelectedLocalModelStatusAsync()
    {
        if (_isUpdatingAiBackend || _localModelDownloadCts is not null)
        {
            return;
        }

        var model = GetSelectedLocalModel();
        AiBackendHealthText.Text = $"Health: checking {model}...";
        try
        {
            var status = await _runtimeGeminiClient.GetLocalLlmStatusAsync(
                GetOllamaUrl(),
                model,
                CancellationToken.None);
            AiBackendStatusText.Text = FormatLocalStatus(status);
            AiBackendHealthText.Text = FormatLocalHealth(status);
            UpdateLocalModelDownloadButton(status);
        }
        catch (Exception ex)
        {
            AiBackendStatusText.Text = $"AI backend: Local check failed. {ex.Message}";
            AiBackendHealthText.Text = "Health: Local LLM check failed.";
            DownloadLocalModelButton.Content = "Download & Use";
        }
    }

    private async void DownloadLocalModelButton_OnClick(object sender, RoutedEventArgs e)
    {
        await DownloadSelectedLocalModelAsync(activateAfterSuccess: true);
    }

    private void CancelDownloadModelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _localModelDownloadCts?.Cancel();
        CancelDownloadModelButton.IsEnabled = false;
        AiBackendStatusText.Text = "AI backend: Cancelling model download...";
        DownloadLocalModelProgressText.Text = "Cancelling download...";
    }

    private async void OpenOllamaDownloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        await InstallOrOpenOllamaAsync();
    }

    private void OpenApiKeyHelpButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl("https://aistudio.google.com/app/apikey");
    }

    private async Task<bool> SaveApiKeyPoolFromUiAsync(bool requireKey, CancellationToken cancellationToken)
    {
        var apiKeys = NormalizeApiKeyPoolInput(ApiKeyTextBox.Text);
        if (string.IsNullOrWhiteSpace(apiKeys))
        {
            if (requireKey)
            {
                StatusText.Text = "Status: Paste one or more Gemini API keys before saving.";
                AiBackendStatusText.Text = "AI backend: Paste one or more Gemini API keys, then click Save API Keys.";
            }

            return !requireKey;
        }

        var keyCount = CountApiKeys(apiKeys);
        await _runtimeGeminiClient.UpdateRuntimeApiKeyAsync(apiKeys, cancellationToken);
        var saved = await _runtimeLaunchProfileService.UpdateApiKeysAsync(apiKeys);
        ApiKeyTextBox.Text = FormatApiKeyPoolForDisplay(apiKeys);
        UpdateApiKeyLineNumbers();
        ApiKeyPoolSummaryText.Text = FormatApiKeyPoolSummary(keyCount);
        StatusText.Text = saved
            ? $"Status: API key pool saved with {keyCount} key{(keyCount == 1 ? string.Empty : "s")} for this session and future restarts."
            : $"Status: API key pool updated with {keyCount} key{(keyCount == 1 ? string.Empty : "s")} for this session.";
        AiBackendStatusText.Text = $"AI backend: API key pool replaced with {keyCount} key{(keyCount == 1 ? string.Empty : "s")}. Rotation is active for API LLM mode.";
        AiBackendHealthText.Text = "Health: API key pool saved locally with Windows DPAPI protection.";
        return true;
    }

    private async Task UseLocalBackendAsync()
    {
        if (_isUpdatingAiBackend)
        {
            return;
        }

        _isUpdatingAiBackend = true;
        SetAiBackendControlsEnabled(false);

        var model = GetSelectedLocalModel();
        try
        {
            AiBackendStatusText.Text = $"AI backend: Checking Ollama and {model}...";
            var status = await EnsureOllamaReachableAsync(model, CancellationToken.None);
            if (status is null)
            {
                return;
            }

            if (!status.Reachable)
            {
                AiBackendStatusText.Text = "AI backend: Ollama is not running yet. Click Download Ollama, finish setup, then Use Local again.";
                AiBackendHealthText.Text = "Health: Ollama not reachable.";
                return;
            }

            if (!status.ModelInstalled)
            {
                if (!ConfirmLocalModelDownload(model))
                {
                    AiBackendStatusText.Text = $"AI backend: Local setup paused. {model} was not downloaded.";
                    return;
                }

                AiBackendStatusText.Text = $"AI backend: Downloading {model}. Cursivis will switch to Local LLM when it finishes.";
                var pullResult = await PullLocalModelWithUiProgressAsync(model);

                if (!pullResult.Ok)
                {
                    AiBackendStatusText.Text = $"AI backend: Model download failed. {FirstNonEmpty(pullResult.Error, pullResult.Details, pullResult.Status)}";
                    AiBackendHealthText.Text = "Health: model download failed.";
                    return;
                }

                status = await _runtimeGeminiClient.GetLocalLlmStatusAsync(
                    GetOllamaUrl(),
                    model,
                    CancellationToken.None);
                AiBackendHealthText.Text = FormatLocalHealth(status);
                if (!status.ModelInstalled)
                {
                    AiBackendStatusText.Text = $"AI backend: Download finished, but {model} was not found in Ollama yet. Click Check Local.";
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            AiBackendStatusText.Text = $"AI backend: Local setup failed. {ex.Message}";
            AiBackendHealthText.Text = "Health: Local setup failed.";
            return;
        }
        finally
        {
            _isUpdatingAiBackend = false;
            SetAiBackendControlsEnabled(true);
        }

        await ApplyAiBackendAsync("local_ollama", testAfterApply: true);
    }

    private async Task DownloadSelectedLocalModelAsync(bool activateAfterSuccess)
    {
        if (_isUpdatingAiBackend)
        {
            return;
        }

        _isUpdatingAiBackend = true;
        SetAiBackendControlsEnabled(false);

        var model = GetSelectedLocalModel();
        try
        {
            var status = await EnsureOllamaReachableAsync(model, CancellationToken.None);
            if (status is null)
            {
                return;
            }

            if (status.ModelInstalled)
            {
                AiBackendStatusText.Text = activateAfterSuccess
                    ? $"AI backend: {model} is already downloaded. Switching Local LLM to this model..."
                    : $"AI backend: {model} is already downloaded.";
                AiBackendHealthText.Text = FormatLocalHealth(status);
                DownloadModelProgressGrid.Visibility = Visibility.Visible;
                DownloadLocalModelProgressBar.Value = 100;
                DownloadLocalModelProgressText.Text = $"{model} is already downloaded and ready to use.";
                UpdateLocalModelDownloadButton(status);
            }
            else
            {
                if (!ConfirmLocalModelDownload(model))
                {
                    AiBackendStatusText.Text = $"AI backend: Download cancelled. {model} was not changed.";
                    return;
                }

                AiBackendStatusText.Text = $"AI backend: Downloading {model}. Cursivis will verify it automatically.";
                var result = await PullLocalModelWithUiProgressAsync(model);

                if (!result.Ok)
                {
                    AiBackendStatusText.Text = $"AI backend: Model download failed. {FirstNonEmpty(result.Error, result.Details, result.Status)}";
                    AiBackendHealthText.Text = "Health: model download failed.";
                    return;
                }

                status = await _runtimeGeminiClient.GetLocalLlmStatusAsync(
                    GetOllamaUrl(),
                    model,
                    CancellationToken.None);

                if (!status.ModelInstalled)
                {
                    AiBackendStatusText.Text = $"AI backend: Download finished, but {model} was not found in Ollama yet. Click Check Local.";
                    AiBackendHealthText.Text = FormatLocalHealth(status);
                    return;
                }

                AiBackendStatusText.Text = activateAfterSuccess
                    ? $"AI backend: {model} is ready locally. Switching Local LLM to this model..."
                    : $"AI backend: {model} is ready locally.";
                AiBackendHealthText.Text = FormatLocalHealth(status);
                UpdateLocalModelDownloadButton(status);
            }
        }
        catch (Exception ex)
        {
            AiBackendStatusText.Text = $"AI backend: Model download failed. {ex.Message}";
            AiBackendHealthText.Text = "Health: model download failed.";
            return;
        }
        finally
        {
            _isUpdatingAiBackend = false;
            SetAiBackendControlsEnabled(true);
        }

        if (activateAfterSuccess)
        {
            await ApplyAiBackendAsync("local_ollama", testAfterApply: true);
        }
    }

    private async Task<LocalModelPullResult> PullLocalModelWithUiProgressAsync(string model)
    {
        _localModelDownloadCts?.Dispose();
        _localModelDownloadCts = new CancellationTokenSource();
        DownloadModelProgressGrid.Visibility = Visibility.Visible;
        DownloadLocalModelProgressBar.Value = 0;
        DownloadLocalModelProgressText.Text = $"Preparing {model} download...";
        CancelDownloadModelButton.IsEnabled = true;

        try
        {
            var progress = new Progress<LocalModelPullProgress>(UpdateLocalModelDownloadProgress);
            var result = await _runtimeGeminiClient.PullLocalModelWithProgressAsync(
                GetOllamaUrl(),
                model,
                progress,
                _localModelDownloadCts.Token);

            if (result.Ok)
            {
                DownloadLocalModelProgressBar.Value = 100;
                DownloadLocalModelProgressText.Text = $"{model} download complete.";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            DownloadLocalModelProgressText.Text = "Download cancelled.";
            return new LocalModelPullResult
            {
                Ok = false,
                Model = model,
                Error = "Download cancelled."
            };
        }
        finally
        {
            _localModelDownloadCts?.Dispose();
            _localModelDownloadCts = null;
            CancelDownloadModelButton.IsEnabled = false;
        }
    }

    private void UpdateLocalModelDownloadProgress(LocalModelPullProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Error))
        {
            DownloadLocalModelProgressText.Text = FirstNonEmpty(progress.Error, progress.Details, "Download failed.");
            return;
        }

        if (progress.Total > 0)
        {
            DownloadLocalModelProgressBar.Value = progress.Percent;
            DownloadLocalModelProgressText.Text =
                $"{FirstNonEmpty(progress.Status, "Downloading")} - {progress.Percent:0}% ({FormatBytes(progress.Completed)} of {FormatBytes(progress.Total)})";
            return;
        }

        DownloadLocalModelProgressText.Text = FirstNonEmpty(progress.Status, "Downloading model...");
    }

    private void UpdateLocalModelDownloadButton(LocalLlmStatus status)
    {
        DownloadLocalModelButton.Content = status.Reachable && status.ModelInstalled
            ? "Use Downloaded"
            : "Download & Use";
    }

    private async Task<LocalLlmStatus?> EnsureOllamaReachableAsync(string model, CancellationToken cancellationToken)
    {
        var status = await _runtimeGeminiClient.GetLocalLlmStatusAsync(GetOllamaUrl(), model, cancellationToken);
        if (status.Reachable)
        {
            AiBackendHealthText.Text = FormatLocalHealth(status);
            UpdateLocalModelDownloadButton(status);
            return status;
        }

        if (TryStartInstalledOllama())
        {
            AiBackendStatusText.Text = "AI backend: Starting Ollama...";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                status = await _runtimeGeminiClient.GetLocalLlmStatusAsync(GetOllamaUrl(), model, cancellationToken);
                if (status.Reachable)
                {
                    AiBackendHealthText.Text = FormatLocalHealth(status);
                    UpdateLocalModelDownloadButton(status);
                    return status;
                }
            }
        }

        AiBackendHealthText.Text = "Health: Ollama is not reachable.";
        var response = MessageBox.Show(
            this,
            "Local LLM needs Ollama installed and running on this PC.\n\nClick OK to open the official Ollama for Windows download page. If the Ollama app asks for account setup, finish that step there. After installation finishes, return to Cursivis and click Use Local again.",
            "Download Ollama",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);

        if (response == MessageBoxResult.OK)
        {
            OpenExternalUrl("https://ollama.com/download/windows");
        }

        return null;
    }

    private async Task InstallOrOpenOllamaAsync()
    {
        if (_isUpdatingAiBackend)
        {
            return;
        }

        _isUpdatingAiBackend = true;
        SetAiBackendControlsEnabled(false);
        var model = GetSelectedLocalModel();

        try
        {
            AiBackendStatusText.Text = "AI backend: Checking whether Ollama is already available...";
            var status = await _runtimeGeminiClient.GetLocalLlmStatusAsync(GetOllamaUrl(), model, CancellationToken.None);
            if (status.Reachable)
            {
                AiBackendStatusText.Text = "AI backend: Ollama is already running. You can download or use the selected model now.";
                AiBackendHealthText.Text = FormatLocalHealth(status);
                return;
            }

            var response = MessageBox.Show(
                this,
                "Cursivis will download a fresh OllamaSetup.exe from the official Ollama download endpoint and open it.\n\nFinish the installer if Windows asks for confirmation, then Cursivis will check the local runtime again.",
                "Install Ollama",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (response != MessageBoxResult.OK)
            {
                AiBackendStatusText.Text = "AI backend: Ollama install was cancelled.";
                AiBackendHealthText.Text = "Health: Ollama is not reachable.";
                return;
            }

            var installerPath = await DownloadOllamaInstallerWithUiProgressAsync();
            if (string.IsNullOrWhiteSpace(installerPath))
            {
                return;
            }

            AiBackendStatusText.Text = "AI backend: Opening Ollama installer. Complete the installer window, then return to Cursivis.";
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });

            if (process is not null)
            {
                await process.WaitForExitAsync();
            }

            if (TryStartInstalledOllama())
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000);
                    status = await _runtimeGeminiClient.GetLocalLlmStatusAsync(GetOllamaUrl(), model, CancellationToken.None);
                    if (status.Reachable)
                    {
                        AiBackendStatusText.Text = "AI backend: Ollama is installed and running. Choose a model and click Download & Use.";
                        AiBackendHealthText.Text = FormatLocalHealth(status);
                        return;
                    }
                }
            }

            AiBackendStatusText.Text = "AI backend: Ollama installer opened. If setup is still running, finish it and click Check Local.";
            AiBackendHealthText.Text = "Health: Ollama is not reachable yet.";
        }
        catch (Exception ex)
        {
            AiBackendStatusText.Text = $"AI backend: Ollama setup failed. {ex.Message}";
            AiBackendHealthText.Text = "Health: Ollama setup failed.";
            OpenExternalUrl("https://ollama.com/download/windows");
        }
        finally
        {
            _isUpdatingAiBackend = false;
            SetAiBackendControlsEnabled(true);
            CancelDownloadModelButton.IsEnabled = false;
            _localModelDownloadCts?.Dispose();
            _localModelDownloadCts = null;
        }
    }

    private async Task<string?> DownloadOllamaInstallerWithUiProgressAsync()
    {
        _localModelDownloadCts?.Dispose();
        _localModelDownloadCts = new CancellationTokenSource();
        DownloadModelProgressGrid.Visibility = Visibility.Visible;
        DownloadLocalModelProgressBar.Value = 0;
        DownloadLocalModelProgressText.Text = "Downloading official Ollama installer...";
        CancelDownloadModelButton.IsEnabled = true;

        var setupDir = Path.Combine(Path.GetTempPath(), "Cursivis");
        Directory.CreateDirectory(setupDir);
        var installerPath = Path.Combine(setupDir, "OllamaSetup.exe");
        if (File.Exists(installerPath))
        {
            File.Delete(installerPath);
        }

        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            using var response = await client.GetAsync(
                "https://ollama.com/download/OllamaSetup.exe",
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                _localModelDownloadCts.Token);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var remote = await response.Content.ReadAsStreamAsync(_localModelDownloadCts.Token);
            await using var local = File.Create(installerPath);

            var buffer = new byte[1024 * 128];
            long completed = 0;
            while (true)
            {
                var read = await remote.ReadAsync(buffer, _localModelDownloadCts.Token);
                if (read <= 0)
                {
                    break;
                }

                await local.WriteAsync(buffer.AsMemory(0, read), _localModelDownloadCts.Token);
                completed += read;

                if (total > 0)
                {
                    var percent = Math.Clamp(completed * 100.0 / total, 0, 100);
                    DownloadLocalModelProgressBar.Value = percent;
                    DownloadLocalModelProgressText.Text = $"Downloading Ollama installer - {percent:0}% ({FormatBytes(completed)} of {FormatBytes(total)})";
                }
                else
                {
                    DownloadLocalModelProgressText.Text = $"Downloading Ollama installer - {FormatBytes(completed)}";
                }
            }

            DownloadLocalModelProgressBar.Value = 100;
            DownloadLocalModelProgressText.Text = "Ollama installer download complete.";
            return installerPath;
        }
        catch (OperationCanceledException)
        {
            DownloadLocalModelProgressText.Text = "Ollama installer download cancelled.";
            AiBackendStatusText.Text = "AI backend: Ollama installer download cancelled.";
            return null;
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

    private bool ConfirmLocalModelDownload(string model)
    {
        var message = BuildLocalModelDownloadMessage(model);
        return MessageBox.Show(
            this,
            message,
            "Download Local LLM Model",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information) == MessageBoxResult.OK;
    }

    private static string BuildLocalModelDownloadMessage(string model)
    {
        var (label, ram, storage, note) = GetLocalModelSpecs(model);
        return
            $"{label}\n\n" +
            $"Recommended RAM: {ram}\n" +
            $"Approx. download/storage: {storage}\n\n" +
            "Local privacy: selected text/images are sent only to your configured Ollama endpoint, normally this PC.\n\n" +
            "Performance: Cursivis uses balanced local settings and releases the model shortly after use, so normal computer usage should stay responsive. The first response after a cold start can take longer.\n\n" +
            $"{note}\n\n" +
            "Continue with download and activate Local LLM after it is ready?";
    }

    private static (string Label, string Ram, string Storage, string Note) GetLocalModelSpecs(string model)
    {
        return model.Trim().ToLowerInvariant() switch
        {
            "granite3.2-vision:2b" => ("Granite Vision 2B", "6 GB+ recommended", "about 2.4 GB", "Fastest local multimodal option for average Windows laptops."),
            "gemma3:4b" => ("Gemma 3 4B", "8 GB+ recommended", "about 3.3 GB", "Balanced multimodal option when quality matters more than cold-start speed."),
            "gemma4:e2b" => ("Gemma 4 E2B", "12 GB+ recommended", "about 7.2 GB", "Advanced local option for PCs with more available memory."),
            "gemma4:e4b" => ("Gemma 4 E4B", "16 GB+ recommended", "about 9.6 GB", "Best for stronger PCs when higher quality matters more than startup speed."),
            "gemma4:26b" => ("Gemma 4 26B", "48 GB+ recommended", "large workstation-tier download", "Only choose this on a workstation-class machine."),
            "gemma4:31b" => ("Gemma 4 31B", "48 GB+ recommended", "large workstation-tier download", "Only choose this on a workstation-class machine."),
            _ => ("Granite Vision 2B", "6 GB+ recommended", "about 2.4 GB", "Recommended default for most Windows laptops.")
        };
    }

    private void DialSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressDialEvents)
        {
            return;
        }

        var current = (int)e.NewValue;
        var delta = current - _lastDialValue;
        if (delta == 0)
        {
            return;
        }

        _lastDialValue = current;
        _triggerController.HandleDialTick(delta);
    }

    private void MainWindow_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        _triggerController.CancelLassoPlaceholder();
        StatusText.Text = "Status: Lasso canceled.";
        e.Handled = true;
    }

    private void MainWindow_OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);

        RegisterConfiguredHotkeys(handle);
    }

    private void RegisterConfiguredHotkeys(IntPtr handle)
    {
        if (!TryParseShortcut(_goTriggerShortcut, out var triggerModifiers, out var triggerKey, out _))
        {
            triggerModifiers = ModControl | ModAlt;
            triggerKey = Key.Space;
        }

        RegisterHotkey(handle, TriggerHotkeyId, triggerModifiers, KeyInterop.VirtualKeyFromKey(triggerKey));
        RegisterHotkey(handle, TakeActionHotkeyId, ModControl | ModAlt, KeyInterop.VirtualKeyFromKey(Key.A));
        RegisterHotkey(handle, VoiceHotkeyId, ModControl | ModAlt, KeyInterop.VirtualKeyFromKey(Key.V));
    }

    private bool RegisterHotkey(IntPtr handle, int id, uint modifiers, int virtualKey)
    {
        if (!NativeMethods.RegisterGlobalHotKey(handle, id, modifiers, (uint)virtualKey))
        {
            StatusText.Text = "Status: Some global hotkeys were unavailable. Buttons still work.";
            return false;
        }

        return true;
    }

    private void ReconnectGlobalHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotkeys();
        RegisterConfiguredHotkeys(handle);
    }

    private void UnregisterHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.UnregisterGlobalHotKey(handle, TriggerHotkeyId);
        NativeMethods.UnregisterGlobalHotKey(handle, TakeActionHotkeyId);
        NativeMethods.UnregisterGlobalHotKey(handle, VoiceHotkeyId);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotKey)
        {
            return IntPtr.Zero;
        }

        handled = true;
        switch (wParam.ToInt32())
        {
            case TriggerHotkeyId:
                StatusText.Text = "Status: Hotkey trigger pressed.";
                _ = _triggerController.HandleTapAsync(CancellationToken.None);
                break;
            case TakeActionHotkeyId:
                StatusText.Text = "Status: Hotkey take action pressed.";
                _ = _triggerController.HandleTakeActionAsync(CancellationToken.None);
                break;
            case VoiceHotkeyId:
                StatusText.Text = _talkTriggerInputMode == TalkTriggerInputMode.Text
                    ? "Status: Hotkey text prompt opened."
                    : "Status: Hotkey talk trigger pressed.";
                _ = _triggerController.HandleLongPressAsync(CancellationToken.None);
                break;
        }

        return IntPtr.Zero;
    }

    private void TriggerControllerOnActionChange(object? sender, string action)
    {
        SelectedActionText.Text = $"Selected action: {action}";
    }

    private void TriggerControllerOnProcessingStart(object? sender, EventArgs e)
    {
        StatusText.Text = "Status: Processing...";
    }

    private void TriggerControllerOnProcessingComplete(object? sender, EventArgs e)
    {
        StatusText.Text = "Status: Completed and copied.";
        _suppressDialEvents = true;
        try
        {
            _lastDialValue = 0;
            DialSlider.Value = 0;
        }
        finally
        {
            _suppressDialEvents = false;
        }
    }

    private void CancelLongPressSession()
    {
        try
        {
            _longPressHoldCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation race.
        }
    }

    private async Task FinalizeLongPressSessionAsync()
    {
        if (_longPressHoldTask is null)
        {
            return;
        }

        CancelLongPressSession();
        try
        {
            await _longPressHoldTask;
        }
        catch
        {
            // Trigger controller handles its own status updates/errors.
        }
        finally
        {
            _longPressHoldCts?.Dispose();
            _longPressHoldCts = null;
            _longPressHoldTask = null;
        }
    }

    private async void ModeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isModeInitialized)
        {
            return;
        }

        if (ModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse<InteractionMode>(tag, ignoreCase: true, out var mode))
        {
            return;
        }

        if (!_showOrbDuringWorkflow && mode == InteractionMode.Guided)
        {
            SetModeCombo(InteractionMode.Smart);
            StatusText.Text = "Status: Guided mode requires orb visibility, so Smart mode stayed active.";
            return;
        }

        _triggerController.SetInteractionMode(mode);
        await _settingsService.SaveModeAsync(mode);
        StatusText.Text = $"Status: Mode switched to {mode}.";
    }

    private async void ShowOrbDuringWorkflowCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_isModeInitialized)
        {
            return;
        }

        _showOrbDuringWorkflow = ShowOrbDuringWorkflowCheckBox.IsChecked == true;
        _triggerController.SetShowOrbDuringWorkflow(_showOrbDuringWorkflow);

        if (!_showOrbDuringWorkflow && ModeCombo.SelectedItem is ComboBoxItem item && string.Equals(item.Tag as string, "Guided", StringComparison.OrdinalIgnoreCase))
        {
            SetModeCombo(InteractionMode.Smart);
            _triggerController.SetInteractionMode(InteractionMode.Smart);
            await _settingsService.SaveModeAsync(InteractionMode.Smart);
        }

        await _settingsService.SaveShowOrbDuringWorkflowAsync(_showOrbDuringWorkflow);
        StatusText.Text = _showOrbDuringWorkflow
            ? "Status: Orb will appear during workflows and hide after completion."
            : "Status: Orb hidden for normal smart workflows; result panel stays primary.";
    }

    private async void TakeActionPromptCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isModeInitialized)
        {
            return;
        }

        if (TakeActionPromptCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse<TakeActionPromptPreference>(tag, true, out var preference))
        {
            return;
        }

        _takeActionPromptPreference = preference;
        _triggerController.SetTakeActionPromptPreference(preference);
        await _settingsService.SaveTakeActionPromptPreferenceAsync(preference);
        StatusText.Text = preference == TakeActionPromptPreference.AlwaysAskToRun
            ? "Status: Result-panel Take Action will always show Run preview."
            : "Status: Result-panel Take Action will show confirmation without the Run preview.";
    }

    private async void ThemeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isModeInitialized || _isUpdatingThemeSelection)
        {
            return;
        }

        if (ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse<CompanionThemeMode>(tag, true, out var themeMode))
        {
            return;
        }

        _themeMode = themeMode;
        CompanionThemeService.Apply(themeMode);
        await _settingsService.SaveThemeModeAsync(themeMode);
        StatusText.Text = themeMode == CompanionThemeMode.Dark
            ? "Status: Dark appearance enabled."
            : "Status: Light appearance enabled.";
    }

    private async void TalkTriggerInputCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isModeInitialized)
        {
            return;
        }

        if (TalkTriggerInputCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse<TalkTriggerInputMode>(tag, true, out var talkTriggerInputMode))
        {
            return;
        }

        _talkTriggerInputMode = talkTriggerInputMode;
        _triggerController.SetTalkTriggerInputMode(talkTriggerInputMode);
        UpdateTalkTriggerUi();
        await _settingsService.SaveTalkTriggerInputModeAsync(talkTriggerInputMode);
        StatusText.Text = talkTriggerInputMode == TalkTriggerInputMode.Text
            ? "Status: Talk trigger now opens a typed prompt beside the orb."
            : "Status: Talk trigger now records voice input again.";
    }

    private async void PlayHapticSoundCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_isModeInitialized)
        {
            return;
        }

        _playHapticSound = PlayHapticSoundCheckBox.IsChecked == true;
        HapticSoundPreferenceChanged?.Invoke(this, _playHapticSound);
        await _settingsService.SavePlayHapticSoundAsync(_playHapticSound);
        StatusText.Text = _playHapticSound
            ? "Status: Companion sound will play alongside Logitech haptics."
            : "Status: Companion sound muted. Logitech haptics remain active.";
    }

    private async Task ApplyAiBackendAsync(string provider, bool testAfterApply)
    {
        if (_isUpdatingAiBackend)
        {
            return;
        }

        _isUpdatingAiBackend = true;
        SetAiBackendControlsEnabled(false);
        var normalizedProvider = NormalizeAiProvider(provider);
        AiBackendStatusText.Text = $"AI backend: Switching to {ProviderDisplayName(normalizedProvider)}...";

        try
        {
            var request = BuildProviderUpdateRequest(normalizedProvider);
            await _runtimeGeminiClient.UpdateRuntimeAiProviderAsync(request, CancellationToken.None);
            await _runtimeLaunchProfileService.SaveAiProviderAsync(
                request.Provider,
                request.OllamaUrl,
                request.LocalModel,
                request.OpenAiBaseUrl,
                request.OpenAiApiKey,
                request.OpenAiModel,
                request.HostedApiUrl,
                request.HostedToken);

            SetAiBackendCombo(request.Provider);
            UpdateAiBackendActiveIndicator(request.Provider);
            StatusText.Text = $"Status: {ProviderDisplayName(request.Provider)} backend selected for new requests.";

            if (testAfterApply)
            {
                var result = await _runtimeGeminiClient.TestRuntimeAiProviderAsync(request, CancellationToken.None);
                AiBackendStatusText.Text = FormatProviderTestResult(result);
                AiBackendHealthText.Text = result.Ok
                    ? $"Health: {ProviderDisplayName(request.Provider)} responded successfully."
                    : "Health: backend test failed.";
            }
            else
            {
                AiBackendStatusText.Text = request.Provider switch
                {
                    "local_ollama" => "AI backend: Local LLM is active. Check local setup if this is the first run.",
                    "hosted_cursivis" => "AI backend: Hosted Cursivis AI is selected. Use Test when your hosted URL and license token are ready.",
                    _ => "AI backend: API LLM is active. Use Test to validate the current API key."
                };
                AiBackendHealthText.Text = request.Provider switch
                {
                    "local_ollama" => "Health: Local LLM selected. Use Check Local for Ollama/model status.",
                    "hosted_cursivis" => "Health: Hosted service selected. Use Test after entering your service URL and license token.",
                    _ => "Health: API LLM selected. Existing cloud workflow remains active."
                };
            }
        }
        catch (Exception ex)
        {
            AiBackendStatusText.Text = $"AI backend: Switch failed. {ex.Message}";
        }
        finally
        {
            _isUpdatingAiBackend = false;
            SetAiBackendControlsEnabled(true);
        }
    }

    private async Task TestAiBackendAsync(string provider)
    {
        if (_isUpdatingAiBackend)
        {
            return;
        }

        _isUpdatingAiBackend = true;
        SetAiBackendControlsEnabled(false);
        var normalizedProvider = NormalizeAiProvider(provider);
        AiBackendStatusText.Text = $"AI backend: Testing {ProviderDisplayName(normalizedProvider)}...";

        try
        {
            var result = await _runtimeGeminiClient.TestRuntimeAiProviderAsync(
                BuildProviderUpdateRequest(normalizedProvider),
                CancellationToken.None);
            AiBackendStatusText.Text = FormatProviderTestResult(result);
            AiBackendHealthText.Text = result.Ok
                ? $"Health: {ProviderDisplayName(normalizedProvider)} responded successfully."
                : "Health: backend test failed.";
        }
        catch (Exception ex)
        {
            AiBackendStatusText.Text = $"AI backend: Test failed. {ex.Message}";
            AiBackendHealthText.Text = "Health: backend test failed.";
        }
        finally
        {
            _isUpdatingAiBackend = false;
            SetAiBackendControlsEnabled(true);
        }
    }

    private RuntimeAiProviderUpdateRequest BuildProviderUpdateRequest(string provider)
    {
        return new RuntimeAiProviderUpdateRequest
        {
            Provider = NormalizeAiProvider(provider),
            OllamaUrl = GetOllamaUrl(),
            LocalModel = GetSelectedLocalModel(),
            HostedApiUrl = HostedApiUrlTextBox.Text?.Trim() ?? string.Empty,
            HostedToken = HostedTokenBox.Password?.Trim() ?? string.Empty
        };
    }

    private static string FormatProviderTestResult(RuntimeAiProviderTestResult result)
    {
        if (result.Ok)
        {
            var model = string.IsNullOrWhiteSpace(result.Model) ? "selected model" : result.Model;
            return $"AI backend: Ready on {model} in {Math.Max(1, result.LatencyMs)} ms.";
        }

        var details = FirstNonEmpty(result.Error, result.Details, "Check provider setup.");
        if (details.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            return "AI backend: Selected local model is not downloaded yet. Click Download & Use to download, verify, and switch automatically.";
        }

        if (details.Contains("Ollama is not reachable", StringComparison.OrdinalIgnoreCase))
        {
            return "AI backend: Ollama is not running yet. Click Download Ollama, finish setup, then test again.";
        }

        if (details.Contains("No Gemini API keys are configured", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("GOOGLE_API_KEY", StringComparison.OrdinalIgnoreCase))
        {
            return "AI backend: No API keys are saved yet. Paste one or more keys and click Save API Keys.";
        }

        if (details.Contains("All Gemini API keys are temporarily unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "AI backend: All saved API keys failed or are temporarily limited. Replace exhausted/invalid keys, then test again.";
        }

        return $"AI backend: Test failed. {details}";
    }

    private static string FormatLocalStatus(LocalLlmStatus status)
    {
        if (!status.Reachable)
        {
            return "AI backend: Ollama is not running yet. Click Download Ollama, finish setup, then check again.";
        }

        if (!status.ModelInstalled)
        {
            return $"AI backend: Ollama is running. Click Download & Use to download {status.Model}, verify it, and switch automatically.";
        }

        return $"AI backend: Local LLM ready with {status.Model}. Models are released shortly after use so your PC stays responsive.";
    }

    private static string FormatLocalHealth(LocalLlmStatus status)
    {
        if (!status.Reachable)
        {
            return $"Health: Ollama offline at {status.OllamaUrl}.";
        }

        if (!status.ModelInstalled)
        {
            return $"Health: Ollama online, {status.Model} not downloaded.";
        }

        return $"Health: Ollama online, {status.Model} installed.";
    }

    private void SetAiBackendControlsEnabled(bool enabled)
    {
        AiBackendCombo.IsEnabled = enabled;
        UseApiBackendButton.IsEnabled = enabled;
        TestApiBackendButton.IsEnabled = enabled;
        SetApiKeyButton.IsEnabled = enabled && !_isUpdatingApiKey;
        OpenApiKeyHelpButton.IsEnabled = enabled;
        UseLocalBackendButton.IsEnabled = enabled;
        CheckLocalBackendButton.IsEnabled = enabled;
        DownloadLocalModelButton.IsEnabled = enabled;
        OpenOllamaDownloadButton.IsEnabled = enabled;
        CancelDownloadModelButton.IsEnabled = _localModelDownloadCts is not null;
        LocalModelCombo.IsEnabled = enabled;
        OllamaUrlTextBox.IsEnabled = enabled;
        HostedApiUrlTextBox.IsEnabled = enabled;
        HostedTokenBox.IsEnabled = enabled;
    }

    private void CompanionThemeServiceOnThemeChanged(object? sender, CompanionThemeMode themeMode)
    {
        _themeMode = themeMode;

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SetThemeCombo(themeMode));
            return;
        }

        SetThemeCombo(themeMode);
    }

    private void TriggerControllerOnModeChanged(object? sender, InteractionMode mode)
    {
        SetModeCombo(mode);
    }

    private void LogitechStatusTimerOnTick(object? sender, EventArgs e)
    {
        RefreshLogitechRuntimeStatus();
    }

    private void RefreshLogitechRuntimeStatus()
    {
        var snapshot = _logitechRuntimeStatusService.GetSnapshot();

        LogitechOptionsStatusText.Text = snapshot.OptionsRunning
            ? "Running"
            : snapshot.OptionsInstalled
                ? "Installed"
                : "Missing";

        LogitechPluginServiceStatusText.Text = snapshot.PluginServiceRunning ? "Running" : "Offline";

        LogitechPluginStatusText.Text = snapshot.PluginLoaded
            ? "Loaded"
            : snapshot.PluginInstalled
                ? "Installed"
                : snapshot.DebugLinkPresent
                    ? "Debug Link"
                    : "Not Found";

        LogitechHapticStatusText.Text = snapshot.HapticConnected ? "Connected" : "Waiting";
        LogitechRuntimeModeText.Text = snapshot.RuntimeMode;
    }

    private void SetModeCombo(InteractionMode mode)
    {
        foreach (var item in ModeCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, mode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                ModeCombo.SelectedItem = item;
                return;
            }
        }

        ModeCombo.SelectedIndex = 0;
    }

    private void SetTakeActionPromptCombo(TakeActionPromptPreference preference)
    {
        foreach (var item in TakeActionPromptCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, preference.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                TakeActionPromptCombo.SelectedItem = item;
                return;
            }
        }

        TakeActionPromptCombo.SelectedIndex = 0;
    }

    private void SetThemeCombo(CompanionThemeMode themeMode)
    {
        _isUpdatingThemeSelection = true;
        try
        {
            foreach (var item in ThemeCombo.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string tag && string.Equals(tag, themeMode.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    ThemeCombo.SelectedItem = item;
                    return;
                }
            }

            ThemeCombo.SelectedIndex = 0;
        }
        finally
        {
            _isUpdatingThemeSelection = false;
        }
    }

    private void SetTalkTriggerInputCombo(TalkTriggerInputMode talkTriggerInputMode)
    {
        foreach (var item in TalkTriggerInputCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, talkTriggerInputMode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                TalkTriggerInputCombo.SelectedItem = item;
                return;
            }
        }

        TalkTriggerInputCombo.SelectedIndex = 0;
    }

    private void SetAiBackendCombo(string provider)
    {
        var normalizedProvider = NormalizeAiProvider(provider);
        foreach (var item in AiBackendCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(NormalizeAiProvider(tag), normalizedProvider, StringComparison.OrdinalIgnoreCase))
            {
                AiBackendCombo.SelectedItem = item;
                UpdateAiBackendActiveIndicator(normalizedProvider);
                UpdateAiBackendModeUi(normalizedProvider);
                return;
            }
        }

        AiBackendCombo.SelectedIndex = 0;
        UpdateAiBackendActiveIndicator("gemini");
        UpdateAiBackendModeUi("gemini");
    }

    private void SetLocalModelCombo(string localModel)
    {
        foreach (var item in LocalModelCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && string.Equals(tag, localModel, StringComparison.OrdinalIgnoreCase))
            {
                LocalModelCombo.SelectedItem = item;
                return;
            }
        }

        LocalModelCombo.SelectedIndex = 0;
    }

    private string GetSelectedAiProvider()
    {
        if (AiBackendCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return NormalizeAiProvider(tag);
        }

        return "gemini";
    }

    private string GetSelectedLocalModel()
    {
        if (LocalModelCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
        {
            return tag.Trim();
        }

        return "granite3.2-vision:2b";
    }

    private string GetOllamaUrl()
    {
        var value = OllamaUrlTextBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:11434" : value;
    }

    private void UpdateAiBackendActiveIndicator(string provider)
    {
        var normalizedProvider = NormalizeAiProvider(provider);
        AiBackendActiveText.Text = normalizedProvider switch
        {
            "local_ollama" => $"Active backend: Local LLM ({GetSelectedLocalModel()})",
            "hosted_cursivis" => "Active backend: Hosted Cursivis AI",
            "openai_compatible" => "Active backend: API LLM (Recommended, OpenAI-compatible)",
            _ => "Active backend: API LLM (Recommended)"
        };
    }

    private void UpdateAiBackendModeUi(string provider)
    {
        var normalizedProvider = NormalizeAiProvider(provider);
        ApiLlmSection.Visibility = normalizedProvider is "gemini" or "openai_compatible"
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalLlmSection.Visibility = normalizedProvider == "local_ollama"
            ? Visibility.Visible
            : Visibility.Collapsed;
        HostedCursivisSection.Visibility = normalizedProvider == "hosted_cursivis"
            ? Visibility.Visible
            : Visibility.Collapsed;

        UseApiBackendButton.Content = normalizedProvider switch
        {
            "local_ollama" => "Use Local",
            "hosted_cursivis" => "Coming Soon",
            _ => "Use API"
        };

        AiBackendStatusText.Text = normalizedProvider switch
        {
            "local_ollama" => "AI backend: Local LLM is private and idle until called, but for better answers use API LLM or Cursivis LLM.",
            "hosted_cursivis" => "AI backend: Hosted Cursivis AI is reserved for your paid backend endpoint.",
            _ => "AI backend: API LLM (Recommended) uses the saved key pool and keeps the existing cloud workflow intact."
        };
    }

    private void UpdateTalkTriggerUi()
    {
        LongPressButton.Content = _talkTriggerInputMode == TalkTriggerInputMode.Text
            ? "Text Trigger"
            : "Hold to Talk";
        HotkeysText.Text = _talkTriggerInputMode == TalkTriggerInputMode.Text
            ? $"Hotkeys: {_goTriggerShortcut} = Cursivis Go   |   Ctrl+Alt+A = Take Action   |   Ctrl+Alt+V = Text Trigger"
            : $"Hotkeys: {_goTriggerShortcut} = Cursivis Go   |   Ctrl+Alt+A = Take Action   |   Ctrl+Alt+V = Talk";
    }

    private static bool TryShortcutFromKeyEvent(KeyEventArgs e, out string shortcut, out string message)
    {
        shortcut = string.Empty;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed)
        {
            key = e.ImeProcessedKey;
        }

        var modifiers = Keyboard.Modifiers;
        if (IsModifierKey(key))
        {
            message = "Press a normal key together with Ctrl or Alt.";
            return false;
        }

        if (!modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt))
        {
            message = "Use Ctrl or Alt with the shortcut so it does not trigger accidentally.";
            return false;
        }

        if (key is Key.Escape or Key.Tab or Key.Enter)
        {
            message = "Choose a non-navigation key such as Space, A, B, or an F-key.";
            return false;
        }

        shortcut = BuildShortcutDisplay(modifiers, key);
        message = $"Detected shortcut: {shortcut}.";
        return true;
    }

    private static bool TryParseShortcut(string value, out uint modifiers, out Key key, out string message)
    {
        modifiers = 0;
        key = Key.None;
        message = "Shortcut is invalid.";

        var parts = (value ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            message = "Shortcut must include Ctrl or Alt plus a key.";
            return false;
        }

        foreach (var part in parts)
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    continue;
                case "alt":
                    modifiers |= ModAlt;
                    continue;
                case "shift":
                    modifiers |= 0x0004;
                    continue;
                case "win":
                case "windows":
                    modifiers |= 0x0008;
                    continue;
            }

            if (key != Key.None)
            {
                message = "Shortcut can only include one non-modifier key.";
                return false;
            }

            if (!TryParseKeyToken(part, out key))
            {
                message = $"'{part}' is not a supported shortcut key.";
                return false;
            }
        }

        if ((modifiers & (ModControl | ModAlt)) == 0)
        {
            message = "Use Ctrl or Alt with the shortcut so it does not trigger accidentally.";
            return false;
        }

        if (key == Key.None || IsModifierKey(key) || key is Key.Escape or Key.Tab or Key.Enter)
        {
            message = "Choose a normal shortcut key such as Space, A, B, or an F-key.";
            return false;
        }

        message = "Shortcut is valid.";
        return true;
    }

    private static string? NormalizeShortcutDisplay(string? value)
    {
        return TryParseShortcut(value ?? string.Empty, out var modifiers, out var key, out _)
            ? BuildShortcutDisplay(ToModifierKeys(modifiers), key)
            : null;
    }

    private static ModifierKeys ToModifierKeys(uint modifiers)
    {
        var result = ModifierKeys.None;
        if ((modifiers & ModControl) != 0)
        {
            result |= ModifierKeys.Control;
        }

        if ((modifiers & ModAlt) != 0)
        {
            result |= ModifierKeys.Alt;
        }

        if ((modifiers & 0x0004) != 0)
        {
            result |= ModifierKeys.Shift;
        }

        if ((modifiers & 0x0008) != 0)
        {
            result |= ModifierKeys.Windows;
        }

        return result;
    }

    private static string BuildShortcutDisplay(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(DisplayKey(key));
        return string.Join("+", parts);
    }

    private static bool TryParseKeyToken(string token, out Key key)
    {
        key = Key.None;
        var normalized = token.Trim();
        if (string.Equals(normalized, "Space", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Space;
            return true;
        }

        if (normalized is "`" ||
            string.Equals(normalized, "Backtick", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Grave", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "GraveAccent", StringComparison.OrdinalIgnoreCase))
        {
            key = Key.Oem3;
            return true;
        }

        if (normalized.Length == 1 && char.IsLetter(normalized[0]))
        {
            return Enum.TryParse(normalized.ToUpperInvariant(), out key);
        }

        if (normalized.Length == 1 && char.IsDigit(normalized[0]))
        {
            return Enum.TryParse($"D{normalized}", out key);
        }

        return Enum.TryParse(normalized, true, out key) && key != Key.None;
    }

    private static string DisplayKey(Key key)
    {
        return key switch
        {
            Key.Space => "Space",
            Key.Oem3 => "`",
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            _ => key.ToString()
        };
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
    }

    private async Task LoadRuntimeApiKeyIntoTextboxAsync()
    {
        try
        {
            var profile = await _runtimeLaunchProfileService.TryLoadAsync();
            if (profile is null)
            {
                return;
            }

            var apiKeys = !string.IsNullOrWhiteSpace(profile.ApiKeys)
                ? profile.ApiKeys
                : profile.ApiKey;
            ApiKeyTextBox.Text = FormatApiKeyPoolForDisplay(apiKeys);
            UpdateApiKeyLineNumbers();
            ApiKeyPoolSummaryText.Text = FormatApiKeyPoolSummary(CountApiKeys(apiKeys));
            ResetApiKeyViewport();
        }
        catch
        {
            ApiKeyPoolSummaryText.Text = "Saved API keys: unavailable. Paste keys to replace the pool.";
        }
    }

    private async Task LoadRuntimeAiBackendAsync()
    {
        RuntimeLaunchProfile? profile = null;
        try
        {
            profile = await _runtimeLaunchProfileService.TryLoadAsync();

            var runtimeStatus = await _runtimeGeminiClient.GetRuntimeAiProviderAsync(CancellationToken.None);
            SetAiBackendCombo(runtimeStatus.Provider);
            SetLocalModelCombo(string.IsNullOrWhiteSpace(runtimeStatus.LocalModel)
                ? FirstNonEmpty(profile?.LocalModel, "granite3.2-vision:2b")
                : runtimeStatus.LocalModel);
            OllamaUrlTextBox.Text = FirstNonEmpty(runtimeStatus.OllamaUrl, profile?.OllamaUrl, "http://127.0.0.1:11434");
            HostedApiUrlTextBox.Text = FirstNonEmpty(profile?.HostedApiUrl, runtimeStatus.HostedApiUrl);
            HostedTokenBox.Password = profile?.HostedToken ?? string.Empty;
            UpdateAiBackendActiveIndicator(runtimeStatus.Provider);
            AiBackendStatusText.Text = runtimeStatus.Provider switch
            {
                "local_ollama" => "AI backend: Local LLM is active in the backend.",
                "hosted_cursivis" => "AI backend: Hosted Cursivis AI is active in the backend.",
                _ => "AI backend: API LLM is active in the backend."
            };
            AiBackendHealthText.Text = runtimeStatus.Provider switch
            {
                "local_ollama" => "Health: Local LLM active. Use Check Local for Ollama/model status.",
                "hosted_cursivis" => "Health: Hosted service active. Use Test to validate access.",
                _ => "Health: API LLM active."
            };
        }
        catch
        {
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.AiProvider))
            {
                SetAiBackendCombo(profile.AiProvider);
                SetLocalModelCombo(string.IsNullOrWhiteSpace(profile.LocalModel) ? "granite3.2-vision:2b" : profile.LocalModel);
                OllamaUrlTextBox.Text = string.IsNullOrWhiteSpace(profile.OllamaUrl)
                    ? "http://127.0.0.1:11434"
                    : profile.OllamaUrl;
                HostedApiUrlTextBox.Text = profile.HostedApiUrl;
                HostedTokenBox.Password = profile.HostedToken;
                UpdateAiBackendActiveIndicator(profile.AiProvider);
                AiBackendStatusText.Text = NormalizeAiProvider(profile.AiProvider) switch
                {
                    "local_ollama" => "AI backend: Local LLM is selected. Check local setup before first use.",
                    "hosted_cursivis" => "AI backend: Hosted Cursivis AI is coming soon.",
                    _ => "AI backend: API LLM is selected. Existing Gemini/API behavior is preserved."
                };
                AiBackendHealthText.Text = NormalizeAiProvider(profile.AiProvider) switch
                {
                    "local_ollama" => "Health: Local LLM selected. Use Check Local for Ollama/model status.",
                    "hosted_cursivis" => "Health: Hosted service is not enabled in this build.",
                    _ => "Health: API LLM selected. Existing cloud workflow remains active."
                };
                return;
            }

            SetLocalModelCombo("granite3.2-vision:2b");
            AiBackendStatusText.Text = "AI backend: API LLM is active by default.";
            AiBackendHealthText.Text = "Health: backend status unavailable; using API LLM default.";
        }
    }

    public void ShowForSettings()
    {
        Opacity = 1;
        ShowInTaskbar = false;

        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        Focus();

        _ = Dispatcher.BeginInvoke(() =>
        {
            AiBackendSection.BringIntoView();
            AiBackendCombo.BringIntoView();
            AiBackendCombo.Focus();
        }, DispatcherPriority.Input);
    }

    private static string NormalizeAiProvider(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_") switch
        {
            "local" or "ollama" or "local_ollama" => "local_ollama",
            "openai" or "openai_compatible" or "compatible" => "openai_compatible",
            "hosted" or "cursivis" or "hosted_cursivis" or "cursivis_hosted" => "hosted_cursivis",
            _ => "gemini"
        };
    }

    private static string ProviderDisplayName(string provider)
    {
        return NormalizeAiProvider(provider) switch
        {
            "local_ollama" => "Local LLM",
            "hosted_cursivis" => "Hosted Cursivis AI",
            "openai_compatible" => "OpenAI-compatible API LLM (Recommended)",
            _ => "API LLM (Recommended)"
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static int CountApiKeys(string value)
    {
        return NormalizeApiKeyPoolInput(value)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(part => !string.IsNullOrWhiteSpace(part));
    }

    private static string FormatApiKeyPoolForDisplay(string value)
    {
        return string.Join(
            Environment.NewLine,
            NormalizeApiKeyPoolInput(value)
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string NormalizeApiKeyPoolInput(string? value)
    {
        static string RemoveLineNumberPrefix(string part)
        {
            var trimmed = part.Trim();
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0)
            {
                return trimmed;
            }

            for (var index = 0; index < colonIndex; index++)
            {
                if (!char.IsDigit(trimmed[index]))
                {
                    return trimmed;
                }
            }

            return trimmed[(colonIndex + 1)..].Trim();
        }

        return string.Join(
            Environment.NewLine,
            (value ?? string.Empty)
                .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(RemoveLineNumberPrefix)
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string FormatApiKeyPoolSummary(int keyCount)
    {
        return keyCount > 0
            ? $"Saved API keys: {keyCount} key{(keyCount == 1 ? string.Empty : "s")} in rotation. Paste new keys only when you want to replace the pool."
            : "Saved API keys: none yet. Paste one or more keys to enable API LLM mode.";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.0} {units[unit]}";
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // User can still install manually if Windows blocks the browser launch.
        }
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowWindowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void ApiKeyTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                UpdateApiKeyLineNumbers();
                ResetApiKeyViewport();
            },
            DispatcherPriority.Background);
    }

    private void ApiKeyTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateApiKeyLineNumbers();
        SyncApiKeyLineNumberScroll();
    }

    private void ApiKeyTextBox_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        SyncApiKeyLineNumberScroll();
    }

    private void UpdateApiKeyLineNumbers()
    {
        if (ApiKeyLineNumbersText is null || ApiKeyTextBox is null)
        {
            return;
        }

        var text = ApiKeyTextBox.Text ?? string.Empty;
        var lineCount = Math.Max(1, text.Split('\n').Length);
        ApiKeyLineNumbersText.Text = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, lineCount).Select(line => $"{line}:"));
    }

    private void SyncApiKeyLineNumberScroll()
    {
        if (ApiKeyLineNumbersText is null || ApiKeyTextBox is null)
        {
            return;
        }

        var firstVisibleLine = Math.Max(0, ApiKeyTextBox.GetFirstVisibleLineIndex());
        ApiKeyLineNumbersText.Margin = new Thickness(0, 8 - (firstVisibleLine * 18), 7, 0);
    }

    private void ResetApiKeyViewport()
    {
        ApiKeyTextBox.CaretIndex = 0;
        ApiKeyTextBox.Select(0, 0);
        ApiKeyTextBox.ScrollToHome();
        SyncApiKeyLineNumberScroll();
    }
}
