using Cursivis.Companion.Controllers;
using Cursivis.Companion.Models;
using Cursivis.Companion.Services;
using Cursivis.Companion.Views;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace Cursivis.Companion;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private CursorTracker? _cursorTracker;
    private WindowFocusTracker? _windowFocusTracker;
    private GeminiClient? _geminiClient;
    private BrowserAutomationClient? _browserAutomationClient;
    private ExtensionAutomationClient? _extensionAutomationClient;
    private TriggerController? _triggerController;
    private OrbOverlayWindow? _orbOverlayWindow;
    private ResultPanelWindow? _resultPanelWindow;
    private MainWindow? _mainWindow;
    private TriggerIpcServer? _triggerIpcServer;
    private HapticEventHub? _hapticEventHub;
    private SettingsService? _settingsService;
    private CompanionSettings? _companionSettings;
    private GlobalMouseWheelService? _globalMouseWheelService;
    private bool _showOrbDuringWorkflow = true;
    private TakeActionPromptPreference _takeActionPromptPreference = TakeActionPromptPreference.AlwaysAskToRun;
    private bool _playHapticSound;
    private CancellationTokenSource? _ipcLongPressCts;
    private Task? _ipcLongPressTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstanceMutex = new Mutex(true, @"Local\Cursivis.Companion.SingleInstance", out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            await ForwardSecondInstanceLaunchAsync(e.Args);
            Shutdown(0);
            return;
        }

        try
        {
            _settingsService = new SettingsService();
            var savedSettings = await _settingsService.TryLoadSettingsAsync()
                ?? new CompanionSettings(InteractionMode.Smart, ShowOrbDuringWorkflow: true, TakeActionPromptPreference.AlwaysAskToRun, CompanionThemeMode.Dark, TalkTriggerInputMode.Voice, PlayHapticSound: false, GoTriggerShortcut: SettingsService.DefaultGoTriggerShortcut);
            _companionSettings = savedSettings;
            var mode = savedSettings.Mode;
            _showOrbDuringWorkflow = savedSettings.ShowOrbDuringWorkflow;
            _takeActionPromptPreference = savedSettings.TakeActionPromptPreference;
            _playHapticSound = savedSettings.PlayHapticSound;
            CompanionThemeService.Apply(savedSettings.ThemeMode);
            await _settingsService.SaveSettingsAsync(savedSettings);
            try
            {
                var startupRegistrationService = new StartupRegistrationService();
                await startupRegistrationService.EnsureRegisteredAsync();
            }
            catch
            {
                // Keep the runtime usable even if startup registration fails.
            }

            try
            {
                var hotkeyHostService = new HotkeyHostService();
                await hotkeyHostService.EnsureRunningAsync();
            }
            catch
            {
                // Keep the runtime usable even if the hotkey host is unavailable.
            }

            var backgroundLaunch = e.Args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
            var runtimeBootstrapper = new RuntimeBootstrapper();
            await runtimeBootstrapper.EnsureRuntimeReadyAsync(CancellationToken.None);

            var clipboardService = new ClipboardService();
            var intentMemoryService = new IntentMemoryService();
            _cursorTracker = new CursorTracker();
            _windowFocusTracker = new WindowFocusTracker();
            var selectionDetector = new SelectionDetector(clipboardService);
            var lassoSelectionService = new LassoSelectionService();
            var screenCaptureService = new ScreenCaptureService();
            _geminiClient = new GeminiClient();
            _browserAutomationClient = new BrowserAutomationClient();
            _extensionAutomationClient = new ExtensionAutomationClient();
            var activeBrowserAutomationService = new ActiveBrowserAutomationService(clipboardService);
            var voiceCaptureService = new VoiceCaptureService();
            var voiceCommandPromptService = new VoiceCommandPromptService(_geminiClient, voiceCaptureService);
            _orbOverlayWindow = new OrbOverlayWindow();
            _resultPanelWindow = new ResultPanelWindow();
            _resultPanelWindow.SettingsRequested += ResultPanelWindowOnSettingsRequested;
            _resultPanelWindow.ThemeToggleRequested += ResultPanelWindowOnThemeToggleRequested;

            _triggerController = new TriggerController(
                _cursorTracker,
                selectionDetector,
                _orbOverlayWindow,
                _resultPanelWindow,
                clipboardService,
                _geminiClient,
                _browserAutomationClient,
                _extensionAutomationClient,
                activeBrowserAutomationService,
                lassoSelectionService,
                screenCaptureService,
                voiceCommandPromptService,
                _windowFocusTracker,
                intentMemoryService,
                mode);
            _triggerController.SetShowOrbDuringWorkflow(_showOrbDuringWorkflow);
            _triggerController.SetTakeActionPromptPreference(_takeActionPromptPreference);
            _triggerController.SetTalkTriggerInputMode(savedSettings.TalkTriggerInputMode);

            _windowFocusTracker.RegisterCompanionWindow(_orbOverlayWindow);
            _windowFocusTracker.RegisterCompanionWindow(_resultPanelWindow);
            _windowFocusTracker.ExternalWindowActivated += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _triggerController?.CollapseTransientUi();
                });
            };

            _globalMouseWheelService = new GlobalMouseWheelService();
            _globalMouseWheelService.WheelMoved += GlobalMouseWheelServiceOnWheelMoved;
            _globalMouseWheelService.MouseButtonPressed += GlobalMouseWheelServiceOnMouseButtonPressed;
            _globalMouseWheelService.Start();

            if (!backgroundLaunch)
            {
                CreateMainWindow()?.ShowForSettings();
            }

            _cursorTracker.Start();
            _windowFocusTracker.Start();

            try
            {
                _triggerIpcServer = new TriggerIpcServer();
                _triggerIpcServer.TriggerReceived += TriggerIpcServerOnTriggerReceived;
                _triggerIpcServer.Start();
            }
            catch (Exception ipcEx)
            {
                _resultPanelWindow.ShowInfo(
                    $"Trigger IPC unavailable: {ipcEx.Message}",
                    new Point(40, 40));
            }

            try
            {
                _hapticEventHub = new HapticEventHub();
                _hapticEventHub.Start();
                _triggerController.OnActionChange += TriggerControllerOnActionChange;
                _triggerController.OnActionExecute += TriggerControllerOnActionExecute;
                _triggerController.OnProcessingStart += TriggerControllerOnProcessingStart;
                _triggerController.OnProcessingComplete += TriggerControllerOnProcessingComplete;
            }
            catch (Exception hapticEx)
            {
                _resultPanelWindow.ShowInfo(
                    $"Haptic channel unavailable: {hapticEx.Message}",
                    new Point(40, 80));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Companion startup failed:\n{ex.Message}",
                "Cursivis",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private static async Task ForwardSecondInstanceLaunchAsync(string[] args)
    {
        var pressType = ResolveSecondInstancePressType(args);
        if (string.IsNullOrWhiteSpace(pressType))
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var socket = new ClientWebSocket();
                using var connectCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:48711/cursivis-trigger/"), connectCts.Token);

                var payload = new TriggerEventPayload
                {
                    Source = "companion-second-instance",
                    PressType = pressType,
                    TimestampUtc = DateTime.UtcNow.ToString("O")
                };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
                return;
            }
            catch
            {
                await Task.Delay(250);
            }
        }
    }

    private static string? ResolveSecondInstancePressType(string[] args)
    {
        var backgroundLaunch = args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));

        for (var i = 0; i < args.Length; i++)
        {
            var value = args[i]?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if ((string.Equals(value, "--press-type", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(value, "--trigger", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Length)
            {
                return NormalizeSecondInstancePressType(args[i + 1]);
            }

            if (string.Equals(value, "--show-settings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "--settings", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "settings", StringComparison.OrdinalIgnoreCase))
            {
                return "settings";
            }

            if (!value.StartsWith("-", StringComparison.Ordinal))
            {
                return NormalizeSecondInstancePressType(value);
            }
        }

        if (backgroundLaunch)
        {
            return null;
        }

        return args.Length == 0 ? "settings" : null;
    }

    private static string NormalizeSecondInstancePressType(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "go" => "tap",
            "trigger" => "tap",
            "normal" => "tap",
            "talk" => "long_press",
            "voice" => "long_press",
            "act" => "action",
            "take-action" => "action",
            "take_action" => "action",
            "snip" => "snip-it",
            "snipit" => "snip-it",
            "snip-it" => "snip-it",
            "settings" => "settings",
            "dial_press" => "dial_press",
            "dial_tick" => "dial_tick",
            "long_press_start" => "long_press_start",
            "long_press_end" => "long_press_end",
            "tap" or "action" or "long_press" => value.Trim().ToLowerInvariant(),
            _ => "tap"
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _triggerController?.Dispose();
        _cursorTracker?.Dispose();
        _windowFocusTracker?.Dispose();
        if (_triggerIpcServer is not null)
        {
            _triggerIpcServer.TriggerReceived -= TriggerIpcServerOnTriggerReceived;
            _triggerIpcServer.Dispose();
        }

        if (_resultPanelWindow is not null)
        {
            _resultPanelWindow.SettingsRequested -= ResultPanelWindowOnSettingsRequested;
            _resultPanelWindow.ThemeToggleRequested -= ResultPanelWindowOnThemeToggleRequested;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.HapticSoundPreferenceChanged -= MainWindowOnHapticSoundPreferenceChanged;
        }

        if (_triggerController is not null)
        {
            _triggerController.OnActionChange -= TriggerControllerOnActionChange;
            _triggerController.OnActionExecute -= TriggerControllerOnActionExecute;
            _triggerController.OnProcessingStart -= TriggerControllerOnProcessingStart;
            _triggerController.OnProcessingComplete -= TriggerControllerOnProcessingComplete;
        }

        _hapticEventHub?.Dispose();
        if (_globalMouseWheelService is not null)
        {
            _globalMouseWheelService.WheelMoved -= GlobalMouseWheelServiceOnWheelMoved;
            _globalMouseWheelService.MouseButtonPressed -= GlobalMouseWheelServiceOnMouseButtonPressed;
            _globalMouseWheelService.Dispose();
        }
        CancelIpcLongPress();

        if (_singleInstanceMutex is not null)
        {
            if (_ownsSingleInstanceMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch
                {
                    // Ignore release race on shutdown.
                }
            }

            _singleInstanceMutex.Dispose();
        }

        _geminiClient?.Dispose();
        _browserAutomationClient?.Dispose();
        _extensionAutomationClient?.Dispose();
        base.OnExit(e);
    }

    private async void TriggerIpcServerOnTriggerReceived(object? sender, TriggerEventPayload e)
    {
        if (_triggerController is null)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                var pressType = e.PressType.Trim().ToLowerInvariant();
                switch (pressType)
                {
                    case "tap":
                        _ = _triggerController.HandleTapAsync(CancellationToken.None);
                        break;
                    case "action":
                        _ = _triggerController.HandleDirectTakeActionAsync(CancellationToken.None);
                        break;
                    case "snip-it":
                        _ = _triggerController.HandleImageSelectionAsync(CancellationToken.None);
                        break;
                    case "prompt_optimizer":
                        _ = _triggerController.HandlePromptOptimizerAsync(CancellationToken.None);
                        break;
                    case "settings":
                        ShowSettingsWindow();
                        break;
                    case "long_press":
                        _ = _triggerController.HandleLongPressAsync(CancellationToken.None);
                        break;
                    case "long_press_start":
                        StartIpcLongPress();
                        break;
                    case "long_press_end":
                        _ = CompleteIpcLongPressAsync();
                        break;
                    case "dial_press":
                        _ = _triggerController.HandleDialPressAsync(CancellationToken.None);
                        break;
                    case "dial_tick":
                        _triggerController.HandleDialTick(e.DialDelta ?? 1);
                        break;
                }
            });
        }
        catch
        {
            // Keep app running even if an external IPC event is malformed.
        }
    }

    private void ShowSettingsWindow()
    {
        var mainWindow = CreateMainWindow();
        if (mainWindow is null)
        {
            return;
        }

        _triggerController?.CollapseTransientUi();
        _resultPanelWindow?.HidePanel();
        _orbOverlayWindow?.Hide();
        mainWindow.ShowForSettings();
    }

    private MainWindow? CreateMainWindow()
    {
        if (_mainWindow is not null)
        {
            return _mainWindow;
        }

        if (_triggerController is not { } triggerController ||
            _settingsService is not { } settingsService ||
            _companionSettings is not { } companionSettings)
        {
            return null;
        }

        _mainWindow = new MainWindow(triggerController, settingsService, companionSettings);
        _mainWindow.HapticSoundPreferenceChanged += MainWindowOnHapticSoundPreferenceChanged;
        _windowFocusTracker?.RegisterCompanionWindow(_mainWindow);
        MainWindow = _mainWindow;
        return _mainWindow;
    }

    private void ResultPanelWindowOnSettingsRequested(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(ShowSettingsWindow);
    }

    private async void ResultPanelWindowOnThemeToggleRequested(object? sender, CompanionThemeMode themeMode)
    {
        CompanionThemeService.Apply(themeMode);
        if (_settingsService is not null)
        {
            await _settingsService.SaveThemeModeAsync(themeMode);
        }
    }

    private void StartIpcLongPress()
    {
        if (_triggerController is null)
        {
            return;
        }

        if (_triggerController.UsesTextTalkTrigger)
        {
            if (_ipcLongPressTask is not null && !_ipcLongPressTask.IsCompleted)
            {
                return;
            }

            _ipcLongPressTask = RunTextTriggerAsync();
            return;
        }

        if (_ipcLongPressTask is not null && !_ipcLongPressTask.IsCompleted)
        {
            return;
        }

        CancelIpcLongPress();
        _ipcLongPressCts = new CancellationTokenSource();
        _ipcLongPressTask = _triggerController.HandleLongPressAsync(_ipcLongPressCts.Token);
    }

    private async Task CompleteIpcLongPressAsync()
    {
        if (_triggerController?.UsesTextTalkTrigger == true)
        {
            return;
        }

        if (_ipcLongPressTask is null)
        {
            return;
        }

        CancelIpcLongPress();
        try
        {
            await _ipcLongPressTask;
        }
        catch
        {
            // Errors are surfaced via companion UI.
        }
        finally
        {
            _ipcLongPressCts?.Dispose();
            _ipcLongPressCts = null;
            _ipcLongPressTask = null;
        }
    }

    private void CancelIpcLongPress()
    {
        try
        {
            _ipcLongPressCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation race.
        }
    }

    private async Task RunTextTriggerAsync()
    {
        try
        {
            if (_triggerController is not null)
            {
                await _triggerController.HandleLongPressAsync(CancellationToken.None);
            }
        }
        finally
        {
            _ipcLongPressTask = null;
        }
    }

    private async void TriggerControllerOnActionChange(object? sender, string action)
    {
        await PublishHapticAsync("action_change", "light", ("action", action));
    }

    private async void TriggerControllerOnActionExecute(object? sender, string action)
    {
        await PublishHapticAsync("action_execute", "medium", ("action", action));
    }

    private async void TriggerControllerOnProcessingStart(object? sender, EventArgs e)
    {
        await PublishHapticAsync("processing_start", "light");
    }

    private async void TriggerControllerOnProcessingComplete(object? sender, EventArgs e)
    {
        await PublishHapticAsync("processing_complete", "strong");
    }

    private void GlobalMouseWheelServiceOnWheelMoved(object? sender, GlobalMouseWheelEventArgs e)
    {
        if (_triggerController is null)
        {
            return;
        }

        e.Handled = _triggerController.HandleExternalScrollWheel(e.DeltaStep);
    }

    private void GlobalMouseWheelServiceOnMouseButtonPressed(object? sender, GlobalMouseButtonEventArgs e)
    {
        if (_resultPanelWindow is null || !_resultPanelWindow.IsVisible)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (_resultPanelWindow is null || !_resultPanelWindow.IsVisible)
            {
                return;
            }

            if (!_resultPanelWindow.ContainsScreenPoint(e.ScreenPoint))
            {
                _resultPanelWindow.HidePanel();
            }
        });
    }

    private async Task PublishHapticAsync(string hapticType, string intensity, params (string Key, string Value)[] metadataEntries)
    {
        if (_hapticEventHub is null)
        {
            return;
        }

        var metadata = metadataEntries.ToDictionary(x => x.Key, x => x.Value);
        metadata["playSound"] = _playHapticSound ? "true" : "false";
        await _hapticEventHub.BroadcastAsync(new HapticEventPayload
        {
            HapticType = hapticType,
            Intensity = intensity,
            Metadata = metadata.Count == 0 ? null : metadata,
            TimestampUtc = DateTime.UtcNow.ToString("O")
        });
    }

    private void MainWindowOnHapticSoundPreferenceChanged(object? sender, bool playHapticSound)
    {
        _playHapticSound = playHapticSound;
    }
}
