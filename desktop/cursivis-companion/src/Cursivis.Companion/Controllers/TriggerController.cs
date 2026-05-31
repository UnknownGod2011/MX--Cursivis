using Cursivis.Companion.Infrastructure;
using Cursivis.Companion.Models;
using Cursivis.Companion.Services;
using Cursivis.Companion.Views;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace Cursivis.Companion.Controllers;

public sealed class TriggerController : IDisposable
{
    private const int MaxUndoHistory = 12;
    private const string AiSuggestPrefix = "AI: ";
    private const string CustomVoiceCommandOption = "Custom Voice Command";
    private const string DeferredOptionsPlaceholderPrefix = "__deferred__";
    private static readonly HashSet<string> AutoReplaceActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "rewrite",
        "rewrite_structured",
        "translate",
        "polish_email",
        "improve_code",
        "debug_code",
        "optimize_code"
    };

    private readonly CursorTracker _cursorTracker;
    private readonly SelectionDetector _selectionDetector;
    private readonly OrbOverlayWindow _orbOverlayWindow;
    private readonly ResultPanelWindow _resultPanelWindow;
    private readonly ClipboardService _clipboardService;
    private readonly GeminiClient _geminiClient;
    private readonly BrowserAutomationClient _browserAutomationClient;
    private readonly ExtensionAutomationClient _extensionAutomationClient;
    private readonly ActiveBrowserAutomationService _activeBrowserAutomationService;
    private readonly LassoSelectionService _lassoSelectionService;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly VoiceCommandPromptService _voiceCommandPromptService;
    private readonly WindowFocusTracker _windowFocusTracker;
    private readonly IntentMemoryService _intentMemoryService;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly Stack<UndoHistoryEntry> _undoHistory = new();
    private readonly string[] _actions = ["Summarize", "Rewrite", "Translate", "Explain", "Bullet Points"];
    private int _selectedActionIndex;
    private IntPtr _lastExternalWindow = IntPtr.Zero;
    private string _lastResult = string.Empty;
    private CapturedSelectionContext? _lastSelectionContext;
    private bool _takeActionContextReady;
    private InteractionMode _interactionMode;
    private bool _showOrbDuringWorkflow = true;
    private TakeActionPromptPreference _takeActionPromptPreference = TakeActionPromptPreference.AlwaysAskToRun;
    private TalkTriggerInputMode _talkTriggerInputMode = TalkTriggerInputMode.Voice;
    private readonly bool _autoReplaceEnabled;
    private readonly double _autoReplaceConfidenceThreshold;
    private readonly bool _managedBrowserFallbackEnabled;
    private readonly bool _textVisualContextEnabled;
    private readonly int _textVisualContextWidth;
    private readonly int _textVisualContextHeight;
    private readonly DispatcherTimer _guidedMenuAutoCommitTimer;
    public TriggerController(
        CursorTracker cursorTracker,
        SelectionDetector selectionDetector,
        OrbOverlayWindow orbOverlayWindow,
        ResultPanelWindow resultPanelWindow,
        ClipboardService clipboardService,
        GeminiClient geminiClient,
        BrowserAutomationClient browserAutomationClient,
        ExtensionAutomationClient extensionAutomationClient,
        ActiveBrowserAutomationService activeBrowserAutomationService,
        LassoSelectionService lassoSelectionService,
        ScreenCaptureService screenCaptureService,
        VoiceCommandPromptService voiceCommandPromptService,
        WindowFocusTracker windowFocusTracker,
        IntentMemoryService intentMemoryService,
        InteractionMode initialMode)
    {
        _cursorTracker = cursorTracker;
        _selectionDetector = selectionDetector;
        _orbOverlayWindow = orbOverlayWindow;
        _resultPanelWindow = resultPanelWindow;
        _clipboardService = clipboardService;
        _geminiClient = geminiClient;
        _browserAutomationClient = browserAutomationClient;
        _extensionAutomationClient = extensionAutomationClient;
        _activeBrowserAutomationService = activeBrowserAutomationService;
        _lassoSelectionService = lassoSelectionService;
        _screenCaptureService = screenCaptureService;
        _voiceCommandPromptService = voiceCommandPromptService;
        _windowFocusTracker = windowFocusTracker;
        _intentMemoryService = intentMemoryService;
        _interactionMode = initialMode;
        _autoReplaceEnabled = ParseBoolEnv("CURSIVIS_ENABLE_AUTO_REPLACE", defaultValue: true);
        _autoReplaceConfidenceThreshold = ParseDoubleEnv("CURSIVIS_AUTO_REPLACE_CONFIDENCE", defaultValue: 0.9);
        _managedBrowserFallbackEnabled = ParseBoolEnv("CURSIVIS_ENABLE_MANAGED_BROWSER_FALLBACK", defaultValue: false);
        _textVisualContextEnabled = ParseBoolEnv("CURSIVIS_ENABLE_TEXT_SCREEN_CONTEXT", defaultValue: false);
        _textVisualContextWidth = ParseIntEnv("CURSIVIS_TEXT_SCREEN_CONTEXT_WIDTH", defaultValue: 480);
        _textVisualContextHeight = ParseIntEnv("CURSIVIS_TEXT_SCREEN_CONTEXT_HEIGHT", defaultValue: 320);
        _guidedMenuAutoCommitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _guidedMenuAutoCommitTimer.Tick += GuidedMenuAutoCommitTimerOnTick;
        _cursorTracker.PositionChanged += CursorTrackerOnPositionChanged;
        _resultPanelWindow.InsertRequested += ResultPanelWindowOnInsertRequested;
        _resultPanelWindow.MoreOptionsRequested += ResultPanelWindowOnMoreOptionsRequested;
        _resultPanelWindow.TakeActionRequested += ResultPanelWindowOnTakeActionRequested;
        _resultPanelWindow.UndoRequested += ResultPanelWindowOnUndoRequested;
        _resultPanelWindow.SetUndoAvailable(false);
        _orbOverlayWindow.IdleCommandInvoked += OrbOverlayWindowOnIdleCommandInvoked;
        _orbOverlayWindow.ModeStepRequested += OrbOverlayWindowOnModeStepRequested;
        _orbOverlayWindow.UpdateActionRing(_actions, _selectedActionIndex);
        _orbOverlayWindow.MoveToTopRight();
        _orbOverlayWindow.SetModeDisplay(ModeToDisplay(_interactionMode));
        _orbOverlayWindow.SetState(OrbState.Idle, $"Ready ({ModeToDisplay(_interactionMode)})");
    }

    public event EventHandler<string>? OnActionChange;

    public event EventHandler<string>? OnActionExecute;

    public event EventHandler? OnProcessingStart;

    public event EventHandler? OnProcessingComplete;

    public event EventHandler<InteractionMode>? OnModeChanged;

    public string CurrentAction => _actions[_selectedActionIndex];

    public InteractionMode CurrentMode => _interactionMode;

    public TalkTriggerInputMode CurrentTalkTriggerInputMode => _talkTriggerInputMode;

    public bool UsesTextTalkTrigger => _talkTriggerInputMode == TalkTriggerInputMode.Text;

    public void SetShowOrbDuringWorkflow(bool showOrbDuringWorkflow)
    {
        _showOrbDuringWorkflow = showOrbDuringWorkflow;
        _orbOverlayWindow.SetShowOrbDuringWorkflow(showOrbDuringWorkflow);
    }

    public void SetTakeActionPromptPreference(TakeActionPromptPreference preference)
    {
        _takeActionPromptPreference = preference;
    }

    public void SetTalkTriggerInputMode(TalkTriggerInputMode talkTriggerInputMode)
    {
        _talkTriggerInputMode = talkTriggerInputMode;
        _orbOverlayWindow.SetListeningStopButtonVisible(talkTriggerInputMode == TalkTriggerInputMode.Voice);
    }

    public void SetInteractionMode(InteractionMode mode)
    {
        if (_interactionMode == mode)
        {
            return;
        }

        _interactionMode = mode;
        _orbOverlayWindow.SetModeDisplay(ModeToDisplay(mode));
        _orbOverlayWindow.SetState(OrbState.Idle, $"Ready ({ModeToDisplay(_interactionMode)})");
        OnModeChanged?.Invoke(this, mode);
    }

    public void CollapseTransientUi()
    {
        StopGuidedMenuAutoCommit();
        if (_runLock.CurrentCount == 0)
        {
            return;
        }

        _resultPanelWindow.HidePanel();
        _orbOverlayWindow.CollapseToIdleShell();
    }

    private async void OrbOverlayWindowOnIdleCommandInvoked(object? sender, string command)
    {
        switch (command.Trim().ToLowerInvariant())
        {
            case "talk":
                await HandleLongPressAsync(CancellationToken.None);
                break;
            case "snip-it":
                await HandleImageSelectionAsync(CancellationToken.None);
                break;
            case "action":
                await HandleDirectTakeActionAsync(CancellationToken.None);
                break;
            default:
                await HandleTapAsync(CancellationToken.None);
                break;
        }
    }

    private void OrbOverlayWindowOnModeStepRequested(object? sender, int delta)
    {
        var nextMode = _interactionMode == InteractionMode.Smart
            ? InteractionMode.Guided
            : InteractionMode.Smart;
        SetInteractionMode(nextMode);
    }

    public async Task HandleLongPressAsync(CancellationToken cancellationToken)
    {
        var selectionSource = ResolveSelectionSource();
        _orbOverlayWindow.MoveToTopRight();
        EnsureWindowVisible(_orbOverlayWindow);
        _lastExternalWindow = selectionSource.WindowHandle;

        try
        {
            var command = await PromptCommandFromOrbAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(command))
            {
                _orbOverlayWindow.SetState(OrbState.Idle, UsesTextTalkTrigger ? "Text prompt canceled" : "Voice command canceled");
                return;
            }

            await HandleTapAsync(
                CancellationToken.None,
                command,
                forceActionMenu: false,
                selectionSource.WindowHandle != IntPtr.Zero ? selectionSource : null);
        }
        finally
        {
            _orbOverlayWindow.SetListeningLevel(0);
        }
    }

    private Task<string?> PromptCommandFromOrbAsync(CancellationToken cancellationToken)
    {
        return UsesTextTalkTrigger
            ? PromptTextCommandFromOrbAsync()
            : PromptVoiceCommandFromOrbAsync(cancellationToken);
    }

    private async Task<string?> PromptVoiceCommandFromOrbAsync(CancellationToken cancellationToken)
    {
        using var stopRecordingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnListeningStopRequested(object? sender, EventArgs e)
        {
            if (!stopRecordingCts.IsCancellationRequested)
            {
                stopRecordingCts.Cancel();
            }
        }

        _orbOverlayWindow.ListeningStopRequested += OnListeningStopRequested;
        _orbOverlayWindow.SetState(OrbState.Listening, "Recording... tap stop");
        EnsureWindowVisible(_orbOverlayWindow);

        try
        {
            return await _voiceCommandPromptService.PromptAsync(
                (state, message) => _orbOverlayWindow.SetState(state, message),
                level => _orbOverlayWindow.SetListeningLevel(level),
                stopRecordingCts.Token);
        }
        finally
        {
            _orbOverlayWindow.ListeningStopRequested -= OnListeningStopRequested;
            _orbOverlayWindow.SetListeningLevel(0);
        }
    }

    private Task<string?> PromptTextCommandFromOrbAsync()
    {
        _orbOverlayWindow.SetState(OrbState.Listening, "Type and send your instruction");
        EnsureWindowVisible(_orbOverlayWindow);
        _orbOverlayWindow.Topmost = true;
        _orbOverlayWindow.UpdateLayout();

        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var promptWindow = new TextCommandBarWindow(
                initialCommand: null,
                placeholder: "Type a refinement or task...");
            _windowFocusTracker.RegisterCompanionWindow(promptWindow);
            var completionSource = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            promptWindow.Loaded += (_, _) =>
            {
                EnsureWindowVisible(_orbOverlayWindow);
                _orbOverlayWindow.Topmost = true;
                promptWindow.PositionNextTo(_orbOverlayWindow);
                promptWindow.Activate();
            };
            promptWindow.Closed += (_, _) => completionSource.TrySetResult(promptWindow.CommandText);
            promptWindow.AttachToAnchor(_orbOverlayWindow);
            promptWindow.Show();
            return completionSource.Task;
        }).Task.Unwrap();
    }

    public void HandleDialTick(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        if (_orbOverlayWindow.IsMenuVisible)
        {
            NavigateGuidedMenu(delta);
            return;
        }

        if (!_orbOverlayWindow.IsVisible)
        {
            NativeMethods.SendVolumeStep(delta);
            return;
        }

        _orbOverlayWindow.NavigateIdleCommand(delta);
        OnActionChange?.Invoke(this, _orbOverlayWindow.CurrentIdleCommand);
    }

    public bool HandleExternalScrollWheel(int delta)
    {
        if (delta == 0 || !_orbOverlayWindow.IsMenuVisible)
        {
            return false;
        }

        var stepDirection = Math.Sign(delta);
        var stepCount = Math.Abs(delta);
        for (var i = 0; i < stepCount; i++)
        {
            NavigateGuidedMenu(stepDirection);
        }

        return true;
    }

    public Task HandleDialPressAsync(CancellationToken cancellationToken)
    {
        StopGuidedMenuAutoCommit();
        if (_orbOverlayWindow.TryConfirmMenuSelection())
        {
            return Task.CompletedTask;
        }

        return HandleTapAsync(cancellationToken);
    }

    public async Task HandleTakeActionAsync(CancellationToken cancellationToken)
    {
        if (!HasPendingTakeActionContext)
        {
            _resultPanelWindow.ShowInfo(
                "No recent AI result is available for browser action execution yet.",
                _cursorTracker.CurrentPosition);
            return;
        }

        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            OnProcessingStart?.Invoke(this, EventArgs.Empty);
            await ExecuteTakeActionFlowAsync(autoTriggered: false, cancellationToken);
        }
        finally
        {
            OnProcessingComplete?.Invoke(this, EventArgs.Empty);
            _runLock.Release();
        }
    }

    public async Task HandleDirectTakeActionAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            OnProcessingStart?.Invoke(this, EventArgs.Empty);

            var cursor = _cursorTracker.CurrentPosition;
            var selectionSource = ResolveSelectionSource();
            if (selectionSource.WindowHandle == IntPtr.Zero)
            {
                _orbOverlayWindow.SetState(OrbState.Idle, "No app selection target found");
                _resultPanelWindow.ShowInfo(
                    "Select text or focus the app you want Cursivis to read, then try again.",
                    cursor);
                return;
            }

            _lastExternalWindow = selectionSource.WindowHandle;

            _orbOverlayWindow.MoveToTopRight();
            _orbOverlayWindow.HideActionRing();
            EnsureWindowVisible(_orbOverlayWindow);
            _orbOverlayWindow.SetState(OrbState.Processing, "Analyzing selection for Take Action...");

            var selection = await _selectionDetector.CaptureSelectionAsync(_lastExternalWindow, cancellationToken);
            if (!selection.HasAnyContent)
            {
                _orbOverlayWindow.SetState(OrbState.Idle, "No selection found");
                _resultPanelWindow.ShowInfo(
                    "Select some text or an image first, then press Take Action.",
                    cursor);
                return;
            }

            await PrepareTakeActionContextFromSelectionAsync(
                selection,
                selectionSource.ProcessName,
                selectionSource.WindowHandle,
                cursor,
                cancellationToken);

            await ExecuteTakeActionFlowAsync(autoTriggered: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Action canceled");
            _resultPanelWindow.ShowInfo(
                "Take Action was canceled before completion.",
                _cursorTracker.CurrentPosition);
        }
        catch (Exception ex)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Action failed");
            _resultPanelWindow.ShowInfo($"Error: {ex.Message}", _cursorTracker.CurrentPosition);
        }
        finally
        {
            OnProcessingComplete?.Invoke(this, EventArgs.Empty);
            _runLock.Release();
        }
    }

    public async Task HandleImageSelectionAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var cursor = _cursorTracker.CurrentPosition;
            var selectionSource = ResolveSelectionSource();
            _lastExternalWindow = selectionSource.WindowHandle;
            _orbOverlayWindow.MoveToTopRight();
            _orbOverlayWindow.HideActionRing();
            EnsureWindowVisible(_orbOverlayWindow);
            _orbOverlayWindow.SetState(OrbState.Processing, "Select an image region...");
            OnProcessingStart?.Invoke(this, EventArgs.Empty);

            await ExecuteDirectImageSelectionFlowAsync(
                voiceCommand: null,
                forceActionMenu: false,
                showHexFallbackOnCancel: false,
                selectionSource.ProcessName,
                selectionSource.WindowHandle,
                cursor,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _orbOverlayWindow.SetState(OrbState.Idle, "Image selection canceled");
            OnProcessingComplete?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Image selection failed");
            _resultPanelWindow.ShowInfo($"Error: {ex.Message}", _cursorTracker.CurrentPosition);
            OnProcessingComplete?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task HandlePromptOptimizerAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var cursor = _cursorTracker.CurrentPosition;
            var selectionSource = ResolveSelectionSource();
            if (selectionSource.WindowHandle == IntPtr.Zero)
            {
                _orbOverlayWindow.SetState(OrbState.Idle, "No prompt selection target found");
                _resultPanelWindow.ShowInfo(
                    "Select rough prompt text in another app first, then press Prompt Optimizer.",
                    cursor);
                return;
            }

            _lastExternalWindow = selectionSource.WindowHandle;
            _orbOverlayWindow.MoveToTopRight();
            _orbOverlayWindow.HideActionRing();
            EnsureWindowVisible(_orbOverlayWindow);
            _orbOverlayWindow.SetState(OrbState.Processing, "Optimizing prompt...");
            OnProcessingStart?.Invoke(this, EventArgs.Empty);

            var selection = await _selectionDetector.CaptureSelectionAsync(_lastExternalWindow, cancellationToken);
            if (!selection.HasText)
            {
                _orbOverlayWindow.SetState(OrbState.Idle, "No prompt text selected");
                _resultPanelWindow.ShowInfo(
                    "Select rough prompt text, notes, or a messy draft first, then press Prompt Optimizer.",
                    cursor);
                OnProcessingComplete?.Invoke(this, EventArgs.Empty);
                return;
            }

            const string actionHint = "optimize_prompt";
            var mode = ModeToProtocol(_interactionMode);
            var textVisualContext = !string.IsNullOrWhiteSpace(selection.ImageBase64) && !string.IsNullOrWhiteSpace(selection.ImageMimeType)
                ? (ImageBase64: selection.ImageBase64, ImageMimeType: selection.ImageMimeType)
                : TryCaptureTextVisualContext(cursor);
            var suggestion = new SuggestionResponse
            {
                ContentType = "prompt",
                BestAction = actionHint,
                RecommendedAction = actionHint,
                Confidence = 0.92,
                Alternatives = ["optimize_prompt", "rewrite_structured", "rewrite", "bullet_points"],
                ExtendedAlternatives = ["make_concise", "add_constraints", "add_output_format", "grammar_fix"]
            };

            OnActionExecute?.Invoke(this, "Prompt Optimizer");
            _orbOverlayWindow.SetState(OrbState.Processing, "Structuring prompt...");

            var response = await _geminiClient.AnalyzeTextAsync(
                selection.Text!,
                actionHint,
                mode,
                selectionSource.ProcessName,
                voiceCommand: null,
                cursor,
                textVisualContext.ImageBase64,
                textVisualContext.ImageMimeType,
                cancellationToken);

            await CompleteSuccessfulRunAsync(
                response,
                cursor,
                new CapturedSelectionContext
                {
                    Kind = string.IsNullOrWhiteSpace(textVisualContext.ImageBase64) ? "text" : "text_image",
                    Text = selection.Text,
                    ImageBase64 = textVisualContext.ImageBase64,
                    ImageMimeType = textVisualContext.ImageMimeType,
                    ContentType = "prompt",
                    VoiceCommand = null,
                    SourceWindowHandle = selectionSource.WindowHandle,
                    SourceProcessName = selectionSource.ProcessName,
                    Suggestion = suggestion
                });
        }
        catch (OperationCanceledException)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Prompt optimization timed out");
            _resultPanelWindow.ShowInfo(
                "Prompt optimization timed out. Try a shorter selection or retry.",
                _cursorTracker.CurrentPosition);
            OnProcessingComplete?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Prompt optimization failed");
            _resultPanelWindow.ShowInfo($"Error: {ex.Message}", _cursorTracker.CurrentPosition);
            OnProcessingComplete?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task HandleTapAsync(CancellationToken cancellationToken)
    {
        await HandleTapAsync(cancellationToken, voiceCommand: null, forceActionMenu: false, selectionSourceOverride: null);
    }

    private async Task HandleTapAsync(
        CancellationToken cancellationToken,
        string? voiceCommand,
        bool forceActionMenu,
        (IntPtr WindowHandle, string? ProcessName)? selectionSourceOverride)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var cursor = _cursorTracker.CurrentPosition;
            var selectionSource = selectionSourceOverride.HasValue &&
                                  selectionSourceOverride.Value.WindowHandle != IntPtr.Zero
                ? selectionSourceOverride.Value
                : ResolveSelectionSource();
            if (selectionSource.WindowHandle == IntPtr.Zero)
            {
                _orbOverlayWindow.SetState(OrbState.Idle, "No selection target found");
                _resultPanelWindow.ShowInfo(
                    "Select text or focus an app with content first, then trigger Cursivis.",
                    cursor);
                return;
            }

            _lastExternalWindow = selectionSource.WindowHandle;

            _orbOverlayWindow.MoveToTopRight();
            _orbOverlayWindow.HideActionRing();
            EnsureWindowVisible(_orbOverlayWindow);
            _orbOverlayWindow.SetState(OrbState.Processing, "Analyzing selection...");
            OnProcessingStart?.Invoke(this, EventArgs.Empty);

            var selection = await _selectionDetector.CaptureSelectionAsync(_lastExternalWindow, cancellationToken);
            if (!selection.HasText && !selection.HasImage)
            {
                await HandleImageFallbackAsync(
                    voiceCommand,
                    forceActionMenu,
                    selectionSource.ProcessName,
                    selectionSource.WindowHandle,
                    cancellationToken);
                return;
            }

            if (selection.HasText)
            {
                await ExecuteTextFlowAsync(
                    selection.Text!,
                    selection.ImageBase64,
                    selection.ImageMimeType,
                    voiceCommand,
                    selectionSource.ProcessName,
                    selectionSource.WindowHandle,
                    cursor,
                    forceActionMenu,
                    cancellationToken);
                return;
            }

            await ExecuteImageFlowAsync(
                selection.ImageBase64!,
                selection.ImageMimeType ?? "image/png",
                voiceCommand,
                selectionSource.ProcessName,
                selectionSource.WindowHandle,
                cursor,
                forceActionMenu,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Request timed out");
            _resultPanelWindow.ShowInfo(
                "Request timed out. Try a shorter selection or retry.",
                _cursorTracker.CurrentPosition);
        }
        catch (Exception ex)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Failed to process request");
            _resultPanelWindow.ShowInfo($"Error: {ex.Message}", _cursorTracker.CurrentPosition);
        }
        finally
        {
            _runLock.Release();
        }
    }

    public void CancelLassoPlaceholder()
    {
        _orbOverlayWindow.SetState(OrbState.Idle, $"Lasso mode canceled ({ModeToDisplay(_interactionMode)})");
    }

    public void Dispose()
    {
        _cursorTracker.PositionChanged -= CursorTrackerOnPositionChanged;
        _resultPanelWindow.InsertRequested -= ResultPanelWindowOnInsertRequested;
        _resultPanelWindow.MoreOptionsRequested -= ResultPanelWindowOnMoreOptionsRequested;
        _resultPanelWindow.TakeActionRequested -= ResultPanelWindowOnTakeActionRequested;
        _resultPanelWindow.UndoRequested -= ResultPanelWindowOnUndoRequested;
        _orbOverlayWindow.IdleCommandInvoked -= OrbOverlayWindowOnIdleCommandInvoked;
        _orbOverlayWindow.ModeStepRequested -= OrbOverlayWindowOnModeStepRequested;
        _guidedMenuAutoCommitTimer.Tick -= GuidedMenuAutoCommitTimerOnTick;
        _guidedMenuAutoCommitTimer.Stop();
        _runLock.Dispose();
    }

    private async Task ExecuteTextFlowAsync(
        string selectedText,
        string? selectedImageBase64,
        string? selectedImageMimeType,
        string? voiceCommand,
        string? sourceProcessName,
        IntPtr sourceWindowHandle,
        Point cursor,
        bool forceActionMenu,
        CancellationToken cancellationToken)
    {
        var mode = ModeToProtocol(_interactionMode);
        var textVisualContext = !string.IsNullOrWhiteSpace(selectedImageBase64) && !string.IsNullOrWhiteSpace(selectedImageMimeType)
            ? (ImageBase64: selectedImageBase64, ImageMimeType: selectedImageMimeType)
            : TryCaptureTextVisualContext(cursor);
        var suggestion = await _geminiClient.SuggestTextActionsAsync(
            selectedText,
            mode,
            sourceProcessName,
            cursor,
            textVisualContext.ImageBase64,
            textVisualContext.ImageMimeType,
            cancellationToken);

        var decision = await ResolveActionDecisionAsync(suggestion, voiceCommand, forceActionMenu, cancellationToken);
        if (decision is null)
        {
            _orbOverlayWindow.SetState(OrbState.Idle, "Action canceled");
            return;
        }

        var actionHint = decision.Value.ActionHint;
        if (!string.IsNullOrWhiteSpace(actionHint))
        {
            TrySyncActionRing(actionHint);
            var displayAction = ResolveExecutionDisplayAction(actionHint, decision.Value.VoiceCommand);
            OnActionExecute?.Invoke(this, displayAction);
            _orbOverlayWindow.SetState(OrbState.Processing, $"Running {displayAction.ToLowerInvariant()}...");
        }
        else
        {
            _orbOverlayWindow.SetState(OrbState.Processing, "Analyzing selection...");
        }

        var response = await _geminiClient.AnalyzeTextAsync(
            selectedText,
            actionHint,
            mode,
            sourceProcessName,
            decision.Value.VoiceCommand,
            cursor,
            textVisualContext.ImageBase64,
            textVisualContext.ImageMimeType,
            cancellationToken);

        await CompleteSuccessfulRunAsync(
            response,
            cursor,
            new CapturedSelectionContext
            {
                Kind = string.IsNullOrWhiteSpace(textVisualContext.ImageBase64) ? "text" : "text_image",
                Text = selectedText,
                ImageBase64 = textVisualContext.ImageBase64,
                ImageMimeType = textVisualContext.ImageMimeType,
                ContentType = suggestion.ContentType,
                VoiceCommand = decision.Value.VoiceCommand,
                SourceWindowHandle = sourceWindowHandle,
                SourceProcessName = sourceProcessName,
                Suggestion = suggestion
            });
    }

    private async Task HandleImageFallbackAsync(
        string? voiceCommand,
        bool forceActionMenu,
        string? sourceProcessName,
        IntPtr sourceWindowHandle,
        CancellationToken cancellationToken)
    {
        await ExecuteDirectImageSelectionFlowAsync(
            voiceCommand,
            forceActionMenu,
            showHexFallbackOnCancel: true,
            sourceProcessName,
            sourceWindowHandle,
            _cursorTracker.CurrentPosition,
            cancellationToken);
    }

    private async Task ExecuteDirectImageSelectionFlowAsync(
        string? voiceCommand,
        bool forceActionMenu,
        bool showHexFallbackOnCancel,
        string? sourceProcessName,
        IntPtr sourceWindowHandle,
        Point cursor,
        CancellationToken cancellationToken)
    {
        _orbOverlayWindow.SetState(OrbState.Processing, "Drag to select an image region...");
        var selectionResult = await _lassoSelectionService.CaptureSelectionAsync(cancellationToken);
        if (selectionResult.IsCanceled)
        {
            if (!showHexFallbackOnCancel || selectionResult.CancelPoint is null)
            {
                _orbOverlayWindow.SetState(OrbState.Idle, "Image selection canceled");
                _resultPanelWindow.ShowInfo("Image selection canceled.", cursor);
                OnProcessingComplete?.Invoke(this, EventArgs.Empty);
                return;
            }

            var samplePoint = selectionResult.CancelPoint.Value;
            await Task.Delay(80, cancellationToken);
            var hexColor = _screenCaptureService.SamplePixelHex(samplePoint);
            _lastResult = hexColor;
            _lastSelectionContext = null;
            _takeActionContextReady = false;
            await _clipboardService.SetTextAsync(hexColor);
            _orbOverlayWindow.SetState(OrbState.Completed, $"{hexColor} copied");
            _resultPanelWindow.ShowInfo($"{hexColor} copied to clipboard.", samplePoint);
            OnProcessingComplete?.Invoke(this, EventArgs.Empty);
            return;
        }

        await Task.Delay(80, cancellationToken);
        var imageBase64 = _screenCaptureService.CaptureRegionAsBase64Png(selectionResult.Region);
        await ExecuteImageFlowAsync(
            imageBase64,
            "image/png",
            voiceCommand,
            sourceProcessName,
            sourceWindowHandle,
            _cursorTracker.CurrentPosition,
            forceActionMenu,
            cancellationToken);
    }

    private async Task CompleteSuccessfulRunAsync(AgentResponse response, Point cursor, CapturedSelectionContext context)
    {
        var (normalizedAction, storedContext) = StoreSuccessfulRunContext(response, context);

        await _clipboardService.SetTextAsync(response.Result);
        await _intentMemoryService.RecordAsync(context.ContentType, normalizedAction);

        var didAutoReplace = await TryAutoReplaceAsync(normalizedAction, response.Confidence, storedContext);
        if (didAutoReplace && _lastExternalWindow != IntPtr.Zero)
        {
            PushUndoHistory(new UndoHistoryEntry(UndoTarget.ExternalWindow, _lastExternalWindow, $"Undo {ToDisplayAction(normalizedAction)} replace"));
        }

        _orbOverlayWindow.SetState(
            OrbState.Completed,
            didAutoReplace
                ? "Replaced in app + copied (Ctrl+Z to undo)"
                : "Result copied to clipboard");

        _resultPanelWindow.ShowResult(
            ResolveResultDisplayAction(response, context),
            didAutoReplace
                ? $"{response.Result}\n\n[Auto-replaced in app. Press Ctrl+Z in target app to undo.]"
                : response.Result,
            cursor);

        OnProcessingComplete?.Invoke(this, EventArgs.Empty);
    }

    private async Task PrepareTakeActionContextFromSelectionAsync(
        SelectionCaptureResult selection,
        string? sourceProcessName,
        IntPtr sourceWindowHandle,
        Point cursor,
        CancellationToken cancellationToken)
    {
        var mode = ModeToProtocol(_interactionMode);
        if (selection.HasText)
        {
            var textVisualContext = !string.IsNullOrWhiteSpace(selection.ImageBase64) && !string.IsNullOrWhiteSpace(selection.ImageMimeType)
                ? (ImageBase64: selection.ImageBase64, ImageMimeType: selection.ImageMimeType)
                : TryCaptureTextVisualContext(cursor);
            var suggestion = await _geminiClient.SuggestTextActionsAsync(
                selection.Text!,
                mode,
                sourceProcessName,
                cursor,
                textVisualContext.ImageBase64,
                textVisualContext.ImageMimeType,
                cancellationToken);

            var actionHint = ResolveAutomaticActionHint(suggestion);
            if (!string.IsNullOrWhiteSpace(actionHint))
            {
                TrySyncActionRing(actionHint);
                _orbOverlayWindow.SetState(OrbState.Processing, $"Preparing {ToDisplayAction(actionHint).ToLowerInvariant()} for Take Action...");
            }
            else
            {
                _orbOverlayWindow.SetState(OrbState.Processing, "Preparing Take Action...");
            }

            var response = await _geminiClient.AnalyzeTextAsync(
                selection.Text!,
                actionHint,
                mode,
                sourceProcessName,
                voiceCommand: null,
                cursor,
                textVisualContext.ImageBase64,
                textVisualContext.ImageMimeType,
                cancellationToken);

            StoreSuccessfulRunContext(
                response,
                new CapturedSelectionContext
                {
                    Kind = string.IsNullOrWhiteSpace(textVisualContext.ImageBase64) ? "text" : "text_image",
                    Text = selection.Text,
                    ImageBase64 = textVisualContext.ImageBase64,
                    ImageMimeType = textVisualContext.ImageMimeType,
                    ContentType = suggestion.ContentType,
                    SourceWindowHandle = sourceWindowHandle,
                    SourceProcessName = sourceProcessName,
                    Suggestion = suggestion
                });

            return;
        }

        if (selection.HasImage)
        {
            var suggestion = await _geminiClient.SuggestImageActionsAsync(
                selection.ImageBase64!,
                selection.ImageMimeType ?? "image/png",
                mode,
                sourceProcessName,
                cursor,
                cancellationToken);

            var actionHint = ResolveAutomaticActionHint(suggestion);
            if (!string.IsNullOrWhiteSpace(actionHint))
            {
                TrySyncActionRing(actionHint);
                _orbOverlayWindow.SetState(OrbState.Processing, $"Preparing {ToDisplayAction(actionHint).ToLowerInvariant()} for Take Action...");
            }
            else
            {
                _orbOverlayWindow.SetState(OrbState.Processing, "Preparing image Take Action...");
            }

            var response = await _geminiClient.AnalyzeImageAsync(
                selection.ImageBase64!,
                selection.ImageMimeType ?? "image/png",
                actionHint,
                mode,
                sourceProcessName,
                voiceCommand: null,
                cursor,
                cancellationToken);

            StoreSuccessfulRunContext(
                response,
                new CapturedSelectionContext
                {
                    Kind = "image",
                    ImageBase64 = selection.ImageBase64,
                    ImageMimeType = selection.ImageMimeType ?? "image/png",
                    ContentType = suggestion.ContentType,
                    SourceWindowHandle = sourceWindowHandle,
                    SourceProcessName = sourceProcessName,
                    Suggestion = suggestion
                });

            return;
        }

        throw new InvalidOperationException("No selection content is available for Take Action.");
    }

    private (string NormalizedAction, CapturedSelectionContext StoredContext) StoreSuccessfulRunContext(AgentResponse response, CapturedSelectionContext context)
    {
        var normalizedAction = NormalizeActionHint(response.Action);
        var storedContext = context with
        {
            Suggestion = new SuggestionResponse
            {
                ContentType = context.ContentType,
                BestAction = normalizedAction,
                RecommendedAction = normalizedAction,
                Confidence = response.Confidence,
                Alternatives = response.Alternatives
                    .Select(NormalizeActionHint)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ExtendedAlternatives = context.Suggestion.ExtendedAlternatives
                    .Select(NormalizeActionHint)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            },
            ExecutedAction = normalizedAction
        };

        _lastResult = response.Result;
        _lastSelectionContext = storedContext;
        _takeActionContextReady = true;

        return (normalizedAction, storedContext);
    }

    private async Task ExecuteImageFlowAsync(
        string imageBase64,
        string imageMimeType,
        string? voiceCommand,
        string? sourceProcessName,
        IntPtr sourceWindowHandle,
        Point cursorPosition,
        bool forceActionMenu,
        CancellationToken cancellationToken)
    {
        var mode = ModeToProtocol(_interactionMode);
        var suggestion = await _geminiClient.SuggestImageActionsAsync(
            imageBase64,
            imageMimeType,
            mode,
            sourceProcessName,
            cursorPosition,
            cancellationToken);

        var decision = await ResolveActionDecisionAsync(suggestion, voiceCommand, forceActionMenu, cancellationToken);
        if (decision is null)
        {
            _orbOverlayWindow.SetState(OrbState.Idle, "Action canceled");
            return;
        }

        var actionHint = decision.Value.ActionHint;
        if (!string.IsNullOrWhiteSpace(actionHint))
        {
            TrySyncActionRing(actionHint);
            var displayAction = ResolveExecutionDisplayAction(actionHint, decision.Value.VoiceCommand);
            OnActionExecute?.Invoke(this, displayAction);
            _orbOverlayWindow.SetState(OrbState.Processing, $"Analyzing image ({displayAction})...");
        }
        else
        {
            _orbOverlayWindow.SetState(OrbState.Processing, "Analyzing image...");
        }

        var response = await _geminiClient.AnalyzeImageAsync(
            imageBase64,
            imageMimeType,
            actionHint,
            mode,
            sourceProcessName,
            decision.Value.VoiceCommand,
            cursorPosition,
            cancellationToken);

        await CompleteSuccessfulRunAsync(
            response,
            cursorPosition,
            new CapturedSelectionContext
            {
                Kind = "image",
                ImageBase64 = imageBase64,
                ImageMimeType = imageMimeType,
                ContentType = suggestion.ContentType,
                VoiceCommand = decision.Value.VoiceCommand,
                SourceWindowHandle = sourceWindowHandle,
                SourceProcessName = sourceProcessName,
                Suggestion = suggestion
            });
    }

    private async Task<ActionDecision?> ResolveActionDecisionAsync(
        SuggestionResponse suggestion,
        string? voiceCommand,
        bool forceActionMenu,
        CancellationToken cancellationToken)
    {
        var normalizedSuggestion = NormalizeSuggestion(suggestion);
        var bestAction = !string.IsNullOrWhiteSpace(normalizedSuggestion.BestAction)
            ? normalizedSuggestion.BestAction
            : normalizedSuggestion.RecommendedAction;

        if (forceActionMenu || (_interactionMode == InteractionMode.Guided && string.IsNullOrWhiteSpace(voiceCommand)))
        {
            var menuOptions = await BuildActionMenuOptionsAsync(normalizedSuggestion, cancellationToken);
            var selectedOption = await ShowActionMenuAsync(menuOptions, normalizedSuggestion.ContentType, cancellationToken);
            if (string.IsNullOrWhiteSpace(selectedOption))
            {
                return null;
            }

            if (string.Equals(selectedOption, CustomVoiceCommandOption, StringComparison.OrdinalIgnoreCase))
            {
                var customVoice = await PromptCommandFromOrbAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(customVoice))
                {
                    return null;
                }

                return new ActionDecision(null, customVoice, "Custom Task");
            }

            if (selectedOption.StartsWith(AiSuggestPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new ActionDecision(bestAction, voiceCommand, bestAction);
            }

            var selectedAction = NormalizeActionHint(selectedOption);
            return new ActionDecision(selectedAction, voiceCommand, selectedAction);
        }

        // Smart mode: use Gemini's routed best action as the execution hint so the same decision is carried through.
        var smartAction = string.IsNullOrWhiteSpace(bestAction) ? null : NormalizeActionHint(bestAction);
        var displayAction = string.IsNullOrWhiteSpace(voiceCommand)
            ? smartAction
            : ResolveExecutionDisplayAction(smartAction, voiceCommand);
        return new ActionDecision(smartAction, voiceCommand, displayAction);
    }

    private Task<GuidedMenuOptions> BuildActionMenuOptionsAsync(SuggestionResponse suggestion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSuggestion = NormalizeSuggestion(suggestion);
        var bestActionHint = NormalizeActionHint(
            string.IsNullOrWhiteSpace(normalizedSuggestion.BestAction)
                ? normalizedSuggestion.RecommendedAction
                : normalizedSuggestion.BestAction);

        var primaryActionHints = new List<string>();
        if (!string.IsNullOrWhiteSpace(bestActionHint))
        {
            primaryActionHints.Add(bestActionHint);
        }

        foreach (var actionHint in normalizedSuggestion.Alternatives.Select(NormalizeActionHint))
        {
            if (string.IsNullOrWhiteSpace(actionHint) ||
                primaryActionHints.Contains(actionHint, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            primaryActionHints.Add(actionHint);
            if (primaryActionHints.Count >= 2)
            {
                break;
            }
        }

        foreach (var fallback in GetPrimaryDefaultsForContentType(normalizedSuggestion.ContentType))
        {
            if (primaryActionHints.Count >= 2)
            {
                break;
            }

            var fallbackHint = NormalizeActionHint(fallback);
            if (primaryActionHints.Contains(fallbackHint, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            primaryActionHints.Add(fallbackHint);
        }

        var initial = new List<string>();
        if (primaryActionHints.Count > 0)
        {
            AddUniqueOption(initial, $"{AiSuggestPrefix}{ToDisplayAction(primaryActionHints[0])}");
        }

        if (primaryActionHints.Count > 1)
        {
            AddUniqueOption(initial, ToDisplayAction(primaryActionHints[1]));
        }

        AddUniqueOption(initial, CustomVoiceCommandOption);

        var deferredHints = new List<string>();
        void AddDeferredHint(string? actionHint)
        {
            if (string.IsNullOrWhiteSpace(actionHint))
            {
                return;
            }

            var normalizedHint = NormalizeActionHint(actionHint);
            if (primaryActionHints.Contains(normalizedHint, StringComparer.OrdinalIgnoreCase) ||
                deferredHints.Contains(normalizedHint, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            deferredHints.Add(normalizedHint);
        }

        foreach (var actionHint in normalizedSuggestion.ExtendedAlternatives)
        {
            AddDeferredHint(actionHint);
        }

        foreach (var actionHint in normalizedSuggestion.Alternatives)
        {
            AddDeferredHint(actionHint);
        }

        foreach (var fallback in GetContextualExpandedDefaults(normalizedSuggestion.ContentType))
        {
            AddDeferredHint(fallback);
        }

        foreach (var fallback in GetBasicStageOneDefaults(normalizedSuggestion.ContentType))
        {
            AddDeferredHint(fallback);
        }

        var deferred = deferredHints
            .Select(ToDisplayAction)
            .Take(2)
            .ToList();

        return Task.FromResult(new GuidedMenuOptions(initial, deferred));
    }

    private static IReadOnlyList<string> BuildInitialOrbMenuOptions(GuidedMenuOptions options)
    {
        var initial = new List<string>();
        var primaryActions = options.InitialOptions
            .Where(option => !string.Equals(option, CustomVoiceCommandOption, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (primaryActions.Count > 0)
        {
            AddUniqueOption(initial, primaryActions[0]);
        }

        if (primaryActions.Count > 1)
        {
            AddUniqueOption(initial, primaryActions[1]);
        }

        AddUniqueOption(initial, CustomVoiceCommandOption);
        var placeholderCount = Math.Min(2, options.DeferredOptions.Count);
        for (var index = 0; index < placeholderCount; index++)
        {
            initial.Add($"{DeferredOptionsPlaceholderPrefix}{index + 1}");
        }

        return initial.Take(5).ToList();
    }

    private static IReadOnlyList<string> BuildExpandedOrbMenuOptions(GuidedMenuOptions options)
    {
        var expanded = new List<string>();
        var primaryActions = options.InitialOptions
            .Where(option => !string.Equals(option, CustomVoiceCommandOption, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (primaryActions.Count > 0)
        {
            AddUniqueOption(expanded, primaryActions[0]);
        }

        if (primaryActions.Count > 1)
        {
            AddUniqueOption(expanded, primaryActions[1]);
        }

        AddUniqueOption(expanded, CustomVoiceCommandOption);

        foreach (var option in options.DeferredOptions)
        {
            AddUniqueOption(expanded, option);
            if (expanded.Count >= 5)
            {
                break;
            }
        }

        return expanded.Take(5).ToList();
    }

    private async Task LoadDeferredMenuOptionsAsync(GuidedMenuOptions options, CancellationToken cancellationToken)
    {
        if (options.DeferredOptions.Count == 0)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!_orbOverlayWindow.IsMenuVisible)
                {
                    return;
                }

                _orbOverlayWindow.UpdateOptionMenu(BuildExpandedOrbMenuOptions(options));
                _orbOverlayWindow.SetState(OrbState.Idle, "More guided options ready");
            });
        }
        catch (OperationCanceledException)
        {
            // The user already made a choice.
        }
    }

    private async Task<string?> ShowActionMenuAsync(GuidedMenuOptions options, string contentType, CancellationToken cancellationToken)
    {
        TaskCompletionSource<string?>? selectionTcs = null;
        EventHandler<string>? menuHandler = null;
        using var deferredCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            selectionTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            menuHandler = (_, selectedOption) =>
            {
                if (selectedOption.StartsWith(DeferredOptionsPlaceholderPrefix, StringComparison.Ordinal))
                {
                    return;
                }

                selectionTcs.TrySetResult(selectedOption);
            };

            _orbOverlayWindow.MenuOptionSelected += menuHandler;
            _orbOverlayWindow.MoveToTopRight();
            EnsureWindowVisible(_orbOverlayWindow);
            _orbOverlayWindow.SetState(OrbState.Idle, $"Guided options ({contentType.Replace("_", " ", StringComparison.Ordinal)})");
            _orbOverlayWindow.ShowOptionMenu(BuildInitialOrbMenuOptions(options));
        });

        _ = LoadDeferredMenuOptionsAsync(options, deferredCts.Token);

        try
        {
            return await selectionTcs!.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            deferredCts.Cancel();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StopGuidedMenuAutoCommit();
                if (menuHandler is not null)
                {
                    _orbOverlayWindow.MenuOptionSelected -= menuHandler;
                }

                _orbOverlayWindow.HideOptionMenu();
                _orbOverlayWindow.SetState(OrbState.Idle, $"Ready ({ModeToDisplay(_interactionMode)})");
            });
        }
    }

    private static SuggestionResponse NormalizeSuggestion(SuggestionResponse suggestion)
    {
        var bestAction = NormalizeActionHint(
            string.IsNullOrWhiteSpace(suggestion.BestAction)
                ? suggestion.RecommendedAction
                : suggestion.BestAction);

        var alternatives = suggestion.Alternatives
            .Select(NormalizeActionHint)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!alternatives.Contains(bestAction, StringComparer.OrdinalIgnoreCase))
        {
            alternatives.Insert(0, bestAction);
        }

        var extended = suggestion.ExtendedAlternatives
            .Select(NormalizeActionHint)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Where(a => !alternatives.Contains(a, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SuggestionResponse
        {
            ContentType = string.IsNullOrWhiteSpace(suggestion.ContentType) ? "general_text" : suggestion.ContentType,
            BestAction = bestAction,
            RecommendedAction = bestAction,
            Confidence = suggestion.Confidence,
            Alternatives = alternatives,
            ExtendedAlternatives = extended
        };
    }

    private static IReadOnlyList<string> GetContextualExpandedDefaults(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "mcq" => ["Explain Step By Step", "Eliminate Wrong Options", "Create Answer Key", "Convert To Flashcards"],
            "report" => ["Extract Insights", "Executive Summary", "Extract Action Items", "Extract Metrics", "Create Slide Outline"],
            "code" => ["Write Tests", "Refactor Code", "Find Edge Cases", "Add Code Comments"],
            "email" => ["Draft Reply", "Change Tone", "Shorten Email", "Formal Version", "Friendly Version"],
            "product" => ["Pros and Cons", "Buyer Checklist", "Red Flags", "Best Use Cases"],
            "social_caption" => ["Generate Hashtags", "Hook Variants", "Short Caption", "Long Caption"],
            "question" => ["Fact Check", "Compare Answers", "Convert To Flashcards", "Explain Step By Step", "Extract Insights"],
            "image" => ["Generate Captions", "Create Alt Text", "Describe Scene", "Compare Visual Details"],
            _ => ["Extract Insights", "Grammar Fix", "Expand Text", "Simplify Language", "Convert To Table"]
        };
    }

    private static IReadOnlyList<string> GetBasicStageOneDefaults(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image" => ["Describe Image", "Extract Key Details", "Identify Objects", "Extract Dominant Colors", "Generate Captions", "Create Alt Text"],
            "code" => ["Explain Code", "Debug Code", "Improve Code", "Optimize Code", "Write Tests", "Translate"],
            "email" => ["Polish Email", "Draft Reply", "Rewrite", "Translate", "Bullet Points", "Extract Insights"],
            "question" => ["Answer Question", "Explain", "Extract Insights", "Bullet Points", "Translate", "Rewrite"],
            "mcq" => ["Answer Question", "Explain", "Create Answer Key", "Eliminate Wrong Options", "Bullet Points", "Translate"],
            "report" => ["Extract Insights", "Summarize", "Bullet Points", "Rewrite Structured", "Translate", "Expand"],
            "product" => ["Extract Product Info", "Compare Prices", "Find Reviews", "Show Product Details", "Bullet Points", "Translate"],
            _ => ["Summarize", "Rewrite", "Explain", "Translate", "Extract Insights", "Bullet Points", "Expand"]
        };
    }

    private static IReadOnlyList<string> GetPrimaryDefaultsForContentType(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "mcq" => ["Answer Question", "Explain", "Extract Insights", "Bullet Points", "Compare Answers", "Eliminate Wrong Options"],
            "question" => ["Answer Question", "Explain", "Extract Insights", "Fact Check", "Compare Answers", "Bullet Points"],
            "email" => ["Polish Email", "Draft Reply", "Rewrite", "Change Tone", "Shorten Email", "Translate"],
            "code" => ["Improve Code", "Debug Code", "Explain Code", "Optimize Code", "Write Tests"],
            "report" => ["Extract Insights", "Bullet Points", "Summarize", "Executive Summary", "Extract Action Items", "Extract Metrics"],
            "product" => ["Extract Product Info", "Compare Prices", "Find Reviews", "Extract Insights", "Pros and Cons", "Buyer Checklist"],
            "social_caption" => ["Suggest Captions", "Generate Captions", "Generate Hashtags", "Hook Variants", "Rewrite"],
            "image" => ["Describe Image", "Find Key Details", "Identify Objects", "Extract Dominant Colors", "Generate Captions"],
            _ => ["Extract Insights", "Summarize", "Expand", "Rewrite", "Translate", "Explain", "Bullet Points"]
        };
    }

    private static void AddUniqueOption(ICollection<string> options, string option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return;
        }

        if (!options.Contains(option, StringComparer.OrdinalIgnoreCase))
        {
            options.Add(option);
        }
    }

    private async Task<bool> TryAutoReplaceAsync(string normalizedAction, double confidence, CapturedSelectionContext context)
    {
        if (!_autoReplaceEnabled)
        {
            return false;
        }

        if (_interactionMode != InteractionMode.Smart)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.Text))
        {
            return false;
        }

        if (!AutoReplaceActions.Contains(normalizedAction))
        {
            return false;
        }

        if (confidence < _autoReplaceConfidenceThreshold)
        {
            return false;
        }

        if (_lastExternalWindow == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.BringToFront(_lastExternalWindow);
        await Task.Delay(60);
        NativeMethods.SendCtrlV();
        return true;
    }

    private void TrySyncActionRing(string actionHint)
    {
        var normalized = NormalizeActionHint(actionHint);
        var newIndex = Array.FindIndex(_actions, a =>
            string.Equals(NormalizeActionHint(a), normalized, StringComparison.OrdinalIgnoreCase));

        if (newIndex < 0)
        {
            return;
        }

        _selectedActionIndex = newIndex;
        _orbOverlayWindow.UpdateActionRing(_actions, _selectedActionIndex);
        OnActionChange?.Invoke(this, CurrentAction);
    }

    private bool HasPendingTakeActionContext =>
        _takeActionContextReady &&
        _lastSelectionContext is not null &&
        !string.IsNullOrWhiteSpace(_lastResult);

    private void ConsumeTakeActionContext()
    {
        _takeActionContextReady = false;
    }

    private static string? ResolveAutomaticActionHint(SuggestionResponse suggestion)
    {
        if (!string.IsNullOrWhiteSpace(suggestion.BestAction))
        {
            return NormalizeActionHint(suggestion.BestAction);
        }

        return string.IsNullOrWhiteSpace(suggestion.RecommendedAction)
            ? null
            : NormalizeActionHint(suggestion.RecommendedAction);
    }

    private void CursorTrackerOnPositionChanged(object? sender, System.Windows.Point e)
    {
        // Keep the orb anchored unless the user drags it manually.
    }

    private async void ResultPanelWindowOnInsertRequested(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastResult))
        {
            return;
        }

        await _clipboardService.SetTextAsync(_lastResult);
        if (_lastExternalWindow != IntPtr.Zero)
        {
            NativeMethods.BringToFront(_lastExternalWindow);
            await Task.Delay(40);
        }

        NativeMethods.SendCtrlV();
        _orbOverlayWindow.SetState(OrbState.Completed, "Inserted");
    }

    private async void ResultPanelWindowOnTakeActionRequested(object? sender, EventArgs e)
    {
        await HandleTakeActionAsync(CancellationToken.None);
    }

    private async Task ExecuteTakeActionFlowAsync(bool autoTriggered, CancellationToken cancellationToken)
    {
        if (!HasPendingTakeActionContext)
        {
            _resultPanelWindow.ShowInfo(
                "No recent AI result is available for browser action execution yet.",
                _cursorTracker.CurrentPosition);
            return;
        }

        ExtensionBridgeHealthResponse? extensionHealth = null;
        try
        {
            var selectionContext = _lastSelectionContext!;
            var cursor = _cursorTracker.CurrentPosition;
            EnsureWindowVisible(_orbOverlayWindow);
            _orbOverlayWindow.SetState(
                OrbState.Processing,
                autoTriggered ? "Preparing action..." : "Preparing Take Action...");

            var sourceIsBrowser =
                IsBrowserProcess(selectionContext.SourceProcessName) &&
                selectionContext.SourceWindowHandle != IntPtr.Zero;
            extensionHealth = sourceIsBrowser && IsChromiumBrowserProcess(selectionContext.SourceProcessName)
                ? await _extensionAutomationClient.TryGetHealthAsync(cancellationToken)
                : null;

            var extensionBrowserContext = sourceIsBrowser && IsChromiumBrowserProcess(selectionContext.SourceProcessName)
                ? await _extensionAutomationClient.TryGetActiveTabContextAsync(cancellationToken)
                : null;
            var extensionPageContext = extensionBrowserContext?.PageContext;
            var shouldUseExtensionBrowserSession = extensionBrowserContext?.Ok == true &&
                                                  extensionPageContext is not null &&
                                                  !string.IsNullOrWhiteSpace(extensionPageContext.Url);
            var activeBrowserContext = sourceIsBrowser
                ? _activeBrowserAutomationService.TryBuildPageContext(selectionContext.SourceWindowHandle)
                : null;
            var currentBrowserUrl = shouldUseExtensionBrowserSession
                ? extensionPageContext!.Url
                : string.IsNullOrWhiteSpace(activeBrowserContext?.Url)
                    ? null
                    : activeBrowserContext.Url;
            var targetUrl = ResolveTakeActionTargetUrl(selectionContext, currentBrowserUrl);
            var preferredBrowserChannel = ResolvePreferredBrowserChannel(selectionContext.SourceProcessName);

            BrowserPageContext planContext;
            BrowserPageContextResponse? managedBrowserReady = null;
            var canUseActiveBrowserSession = activeBrowserContext is not null && sourceIsBrowser;
            var shouldUseActiveBrowserSession = !shouldUseExtensionBrowserSession && canUseActiveBrowserSession;
            if (shouldUseExtensionBrowserSession)
            {
                planContext = extensionPageContext!;
                _orbOverlayWindow.SetState(OrbState.Processing, "Planning actions for current browser tab...");
            }
            else if (shouldUseActiveBrowserSession)
            {
                planContext = activeBrowserContext!;
                _orbOverlayWindow.SetState(OrbState.Processing, "Planning actions for current browser...");
            }
            else if (_managedBrowserFallbackEnabled)
            {
                _orbOverlayWindow.SetState(OrbState.Processing, "Opening managed action browser...");
                managedBrowserReady = await _browserAutomationClient.EnsureBrowserAsync(
                    preferredBrowserChannel,
                    targetUrl,
                    cancellationToken);
                planContext = managedBrowserReady.PageContext;
            }
            else
            {
                _orbOverlayWindow.SetState(OrbState.Completed, "Current browser required");
                _resultPanelWindow.ShowInfo(
                    BuildTakeActionRetryHint(
                        targetUrl,
                        currentBrowserUrl,
                        usingExtensionBrowserSession: false,
                        usingActiveBrowserSession: false,
                        extensionUnavailableForCurrentTab: IsExtensionUnavailable(extensionHealth),
                        managedBrowserFallbackEnabled: _managedBrowserFallbackEnabled),
                    cursor,
                    allowTakeAction: true);
                return;
            }

            _orbOverlayWindow.SetState(
                OrbState.Processing,
                autoTriggered ? "Planning auto action..." : "Planning Take Action...");

            var selectedAction = string.IsNullOrWhiteSpace(selectionContext.ExecutedAction)
                ? selectionContext.Suggestion.BestAction ?? selectionContext.Suggestion.RecommendedAction
                : selectionContext.ExecutedAction;
            var baseVoiceCommand = selectionContext.VoiceCommand;
            var executionInstruction = selectionContext.VoiceCommand;

            var plan = await BuildBrowserActionPlanAsync(
                planContext,
                selectedAction,
                baseVoiceCommand,
                executionInstruction,
                cancellationToken);
            var alreadyConfirmedByUser = false;

            if (plan.Steps.Count == 0)
            {
                _orbOverlayWindow.SetState(OrbState.Completed, "Take Action ready");
                _resultPanelWindow.ShowInfo(
                    $"Take Action paused.{Environment.NewLine}{Environment.NewLine}{plan.Summary}{Environment.NewLine}{Environment.NewLine}{BuildTakeActionRetryHint(targetUrl, currentBrowserUrl, shouldUseExtensionBrowserSession, shouldUseActiveBrowserSession, IsExtensionUnavailable(extensionHealth), _managedBrowserFallbackEnabled)}",
                    cursor,
                    allowTakeAction: true);
                return;
            }

            if (!autoTriggered)
            {
                if (_takeActionPromptPreference == TakeActionPromptPreference.AlwaysAskToRun)
                {
                    var previewDecision = await ShowActionPlanPreviewAsync(plan, ToDisplayAction(selectedAction), executionInstruction);
                    if (previewDecision.ChangeResultRequested)
                    {
                        var decision = await ResolveActionDecisionAsync(
                            selectionContext.Suggestion,
                            selectionContext.VoiceCommand,
                            forceActionMenu: true,
                            cancellationToken);

                        if (decision is null)
                        {
                            _orbOverlayWindow.SetState(OrbState.Idle, "Take Action change canceled");
                            return;
                        }

                        await ReRunLastSelectionAsync(decision.Value, cancellationToken);
                        return;
                    }

                    if (!previewDecision.Approved)
                    {
                        _orbOverlayWindow.SetState(OrbState.Idle, "Take Action canceled");
                        return;
                    }

                    alreadyConfirmedByUser = true;
                    executionInstruction = CombineExecutionInstruction(selectionContext.VoiceCommand, previewDecision.AdditionalInstruction);
                    if (!string.Equals(executionInstruction, selectionContext.VoiceCommand, StringComparison.Ordinal))
                    {
                        _orbOverlayWindow.SetState(OrbState.Processing, "Refining Take Action plan...");
                        plan = await RefineBrowserActionPlanAsync(
                            planContext,
                            selectedAction,
                            baseVoiceCommand,
                            executionInstruction,
                            plan,
                            cancellationToken);
                        if (plan.Steps.Count == 0)
                        {
                            _orbOverlayWindow.SetState(OrbState.Completed, "Take Action ready");
                            _resultPanelWindow.ShowInfo(
                                $"Take Action paused.{Environment.NewLine}{Environment.NewLine}{plan.Summary}{Environment.NewLine}{Environment.NewLine}{BuildTakeActionRetryHint(targetUrl, currentBrowserUrl, shouldUseExtensionBrowserSession, shouldUseActiveBrowserSession, IsExtensionUnavailable(extensionHealth), _managedBrowserFallbackEnabled)}",
                                cursor,
                                allowTakeAction: true);
                            return;
                        }
                    }
                }
                else
                {
                    alreadyConfirmedByUser = true;
                }
            }

            if (plan.RequiresConfirmation && !alreadyConfirmedByUser)
            {
                var confirmed = await Application.Current.Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(
                        $"{plan.Summary}{Environment.NewLine}{Environment.NewLine}This browser action needs confirmation before it continues.",
                        autoTriggered ? "Cursivis Auto Take Action" : "Cursivis Take Action",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question) == MessageBoxResult.OK);

                if (!confirmed)
                {
                    _orbOverlayWindow.SetState(
                        OrbState.Idle,
                        autoTriggered ? "Auto handoff canceled" : "Take Action canceled");
                    return;
                }
            }

            OnActionExecute?.Invoke(this, autoTriggered ? "Auto Take Action" : "Take Action");
            ConsumeTakeActionContext();
            _orbOverlayWindow.SetState(
                OrbState.Processing,
                autoTriggered
                    ? $"Auto-applying {plan.Steps.Count} step(s)..."
                    : $"Executing {plan.Steps.Count} step(s)...");

            BrowserExecutionResponse execution;
            var executedInExtensionBrowser = false;
            var executedInActiveBrowser = false;
            if (shouldUseExtensionBrowserSession)
            {
                try
                {
                    execution = await _extensionAutomationClient.ExecutePlanAsync(plan, cancellationToken);
                }
                catch (Exception ex)
                {
                    var recoveredExecution = await TryRecoverExtensionExecutionFailureAsync(plan, ex, cancellationToken);
                    if (recoveredExecution is null)
                    {
                        throw;
                    }

                    execution = recoveredExecution;
                }

                if (execution.Success)
                {
                    executedInExtensionBrowser = true;
                }
                else if (canUseActiveBrowserSession)
                {
                        _orbOverlayWindow.SetState(OrbState.Processing, "Retrying in current browser window...");
                    execution = await _activeBrowserAutomationService.ExecutePlanAsync(
                        selectionContext.SourceWindowHandle,
                        plan,
                        cancellationToken);

                    if (execution.Success)
                    {
                        executedInActiveBrowser = true;
                    }
                    else if (_managedBrowserFallbackEnabled)
                    {
                        _orbOverlayWindow.SetState(OrbState.Processing, "Retrying in managed browser...");
                        managedBrowserReady ??= await _browserAutomationClient.EnsureBrowserAsync(
                            preferredBrowserChannel,
                            targetUrl,
                            cancellationToken);
                        execution = await _browserAutomationClient.ExecutePlanAsync(plan, cancellationToken);
                    }
                }
                else if (_managedBrowserFallbackEnabled)
                {
                    _orbOverlayWindow.SetState(OrbState.Processing, "Retrying in managed browser...");
                    managedBrowserReady ??= await _browserAutomationClient.EnsureBrowserAsync(
                        preferredBrowserChannel,
                        targetUrl,
                        cancellationToken);
                    execution = await _browserAutomationClient.ExecutePlanAsync(plan, cancellationToken);
                }
            }
            else if (shouldUseActiveBrowserSession)
            {
                execution = await _activeBrowserAutomationService.ExecutePlanAsync(
                    selectionContext.SourceWindowHandle,
                    plan,
                    cancellationToken);

                if (execution.Success)
                {
                    executedInActiveBrowser = true;
                }
                else if (_managedBrowserFallbackEnabled)
                {
                    _orbOverlayWindow.SetState(OrbState.Processing, "Retrying in managed browser...");
                    managedBrowserReady ??= await _browserAutomationClient.EnsureBrowserAsync(
                        preferredBrowserChannel,
                        targetUrl,
                        cancellationToken);
                    execution = await _browserAutomationClient.ExecutePlanAsync(plan, cancellationToken);
                }
            }
            else
            {
                execution = await _browserAutomationClient.ExecutePlanAsync(plan, cancellationToken);
            }

            _orbOverlayWindow.SetState(
                OrbState.Completed,
                execution.Success
                    ? (executedInExtensionBrowser
                        ? "Applied in current tab"
                        : executedInActiveBrowser
                            ? "Applied in current browser"
                            : (autoTriggered ? "Browser action applied" : "Browser action complete"))
                    : "Browser action failed");
            if (execution.Success && execution.ExecutedSteps > 0)
            {
                PushUndoHistory(new UndoHistoryEntry(UndoTarget.Browser, IntPtr.Zero, $"Undo browser action: {plan.Summary}"));
            }

                _resultPanelWindow.ShowInfo(
                    BuildTakeActionSummary(plan, execution, extensionHealth),
                    cursor,
                    allowTakeAction: HasPendingTakeActionContext);
        }
        catch (Exception ex)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, autoTriggered ? "Auto handoff failed" : "Browser action failed");
            _resultPanelWindow.ShowInfo(
                $"{(autoTriggered ? "Auto Take Action" : "Take Action")} failed.{Environment.NewLine}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}{BuildTakeActionRetryHint(null, null, false, false, IsExtensionUnavailable(extensionHealth), _managedBrowserFallbackEnabled)}",
                _cursorTracker.CurrentPosition,
                allowTakeAction: HasPendingTakeActionContext);
        }
    }

    private static bool ContainsBrowserExecutionIntent(string? voiceCommand)
    {
        if (string.IsNullOrWhiteSpace(voiceCommand))
        {
            return false;
        }

        var normalized = voiceCommand.Trim().ToLowerInvariant();
        string[] executionKeywords =
        [
            "send",
            "schedule",
            "submit",
            "fill",
            "autofill",
            "apply",
            "check",
            "tick",
            "mark",
            "select",
            "book",
            "complete",
            "post",
            "click",
            "open",
            "paste into",
            "enter into"
        ];

        return executionKeywords.Any(keyword => normalized.Contains(keyword, StringComparison.Ordinal));
    }

    private static string? ResolvePreferredBrowserChannel(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "chrome";
        }

        return processName.Trim().ToLowerInvariant() switch
        {
            "chrome" => "chrome",
            "brave" => "chrome",
            "opera" => "chrome",
            "opera_gx" => "chrome",
            "vivaldi" => "chrome",
            "arc" => "chrome",
            "msedge" => "msedge",
            "microsoftedge" => "msedge",
            _ => "chrome"
        };
    }

    private static string? ResolveTakeActionTargetUrl(CapturedSelectionContext context, string? currentBrowserUrl)
    {
        if (LooksLikeGoogleFormsUrl(currentBrowserUrl) || LooksLikeMailUrl(currentBrowserUrl))
        {
            return currentBrowserUrl;
        }

        if (IsBrowserExecutionFormContext(context))
        {
            return currentBrowserUrl;
        }

        if (string.Equals(context.ContentType, "email", StringComparison.OrdinalIgnoreCase))
        {
            return LooksLikeMailUrl(currentBrowserUrl)
                ? currentBrowserUrl
                : "https://mail.google.com/mail/u/0/#inbox?compose=new";
        }

        return currentBrowserUrl;
    }

    private static bool IsBrowserExecutionFormContext(CapturedSelectionContext context)
    {
        return string.Equals(context.ContentType, "mcq", StringComparison.OrdinalIgnoreCase) ||
               (string.Equals(context.ContentType, "question", StringComparison.OrdinalIgnoreCase) &&
                ContainsBrowserExecutionIntent(context.VoiceCommand));
    }

    private static bool IsBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        return processName.Trim().ToLowerInvariant() switch
        {
            "chrome" => true,
            "chromium" => true,
            "msedge" => true,
            "microsoftedge" => true,
            "brave" => true,
            "firefox" => true,
            "opera" => true,
            "opera_gx" => true,
            "vivaldi" => true,
            "arc" => true,
            _ => false
        };
    }

    private static bool IsChromiumBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        return processName.Trim().ToLowerInvariant() switch
        {
            "chrome" => true,
            "chromium" => true,
            "msedge" => true,
            "microsoftedge" => true,
            "brave" => true,
            "opera" => true,
            "opera_gx" => true,
            "vivaldi" => true,
            "arc" => true,
            _ => false
        };
    }

    private static bool LooksLikeGoogleFormsUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               (url.Contains("docs.google.com/forms", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("forms.gle", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeMailUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) &&
               (url.Contains("mail.google.com", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("outlook.live.com", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("outlook.office.com", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildTakeActionRetryHint(
        string? targetUrl,
        string? currentBrowserUrl,
        bool usingExtensionBrowserSession,
        bool usingActiveBrowserSession,
        bool extensionUnavailableForCurrentTab,
        bool managedBrowserFallbackEnabled)
    {
        if (extensionUnavailableForCurrentTab)
        {
            return managedBrowserFallbackEnabled
                ? "The real current-tab browser bridge is not connected yet, so Cursivis is falling back to weaker browser automation. Reload the unpacked Cursivis extension, keep the browser open on your logged-in tab, then retry."
                : "The real current-tab browser bridge is not connected yet. Cursivis is staying out of the managed Chrome fallback, so reload the unpacked Cursivis extension, keep the browser open on your logged-in tab, then retry.";
        }

        if (usingExtensionBrowserSession)
        {
            if (LooksLikeMailUrl(targetUrl) || LooksLikeMailUrl(currentBrowserUrl))
            {
                return "Cursivis is acting through the real browser tab you already use. Keep the logged-in mail tab focused and retry once Gmail fully finishes loading.";
            }

            if (LooksLikeGoogleFormsUrl(targetUrl) || LooksLikeGoogleFormsUrl(currentBrowserUrl))
            {
                return "Cursivis is acting through the real browser tab you already use. Keep the Google Form tab open and retry once the form fully loads.";
            }

            return "Cursivis is using your real current browser tab first. Keep the target tab active and retry if the page is still loading.";
        }

        if (usingActiveBrowserSession)
        {
            if (LooksLikeMailUrl(targetUrl) || LooksLikeMailUrl(currentBrowserUrl))
            {
                return "Cursivis is trying to act inside your current logged-in browser session. Keep that mail tab open and press Take Action again if Gmail is still loading.";
            }

            if (LooksLikeGoogleFormsUrl(targetUrl) || LooksLikeGoogleFormsUrl(currentBrowserUrl))
            {
                return "Cursivis is trying to fill the form in your current browser session. Keep the form tab open and retry once the page is fully loaded.";
            }

            return "Cursivis is using your current browser session first. Keep the target tab visible and retry if the page is still loading.";
        }

        if (!managedBrowserFallbackEnabled)
        {
            return "Cursivis is set to work in your current browser session only. Keep the target tab open in your logged-in browser and connect the Chromium extension bridge for reliable DOM actions.";
        }

        if (LooksLikeMailUrl(targetUrl))
        {
            return "Cursivis opened the mail workflow in the managed Chrome window. If Gmail needs sign-in or Compose is still loading, complete that once and press Take Action again.";
        }

        if (LooksLikeGoogleFormsUrl(targetUrl) || LooksLikeGoogleFormsUrl(currentBrowserUrl))
        {
            return "Cursivis opened the form in the managed Chrome window. If the form is still loading, wait a moment and press Take Action again.";
        }

        return "Open the target workflow in the Cursivis managed Chrome window and retry.";
    }

    private async Task<AgentResponse> ReAnalyzeLastSelectionAsync(ActionDecision decision, CancellationToken cancellationToken)
    {
        var mode = ModeToProtocol(_interactionMode);
        var cursor = _cursorTracker.CurrentPosition;
        var sourceProcessName = _lastSelectionContext?.SourceProcessName ?? _windowFocusTracker.LastExternalProcessName;
        var actionHint = decision.ActionHint;
        if (!string.IsNullOrWhiteSpace(actionHint))
        {
            TrySyncActionRing(actionHint);
            var displayAction = ResolveExecutionDisplayAction(actionHint, decision.VoiceCommand);
            OnActionExecute?.Invoke(this, displayAction);
            _orbOverlayWindow.SetState(OrbState.Processing, $"Running {displayAction.ToLowerInvariant()}...");
        }
        else if (!string.IsNullOrWhiteSpace(decision.DisplayAction))
        {
            _orbOverlayWindow.SetState(OrbState.Processing, $"Re-evaluating {ToDisplayAction(decision.DisplayAction!).ToLowerInvariant()}...");
        }
        else
        {
            _orbOverlayWindow.SetState(OrbState.Processing, "Analyzing selection...");
        }

        if (string.Equals(_lastSelectionContext?.Kind, "image", StringComparison.OrdinalIgnoreCase))
        {
            return await _geminiClient.AnalyzeImageAsync(
                _lastSelectionContext!.ImageBase64!,
                _lastSelectionContext.ImageMimeType!,
                actionHint,
                mode,
                sourceProcessName,
                decision.VoiceCommand,
                cursor,
                cancellationToken);
        }

        return await _geminiClient.AnalyzeTextAsync(
            _lastSelectionContext!.Text!,
            actionHint,
            mode,
            sourceProcessName,
            decision.VoiceCommand,
            cursor,
            _lastSelectionContext.ImageBase64,
            _lastSelectionContext.ImageMimeType,
            cancellationToken);
    }

    private async Task ReRunLastSelectionAsync(ActionDecision decision, CancellationToken cancellationToken)
    {
        if (_lastSelectionContext is null)
        {
            return;
        }

        if (string.Equals(_lastSelectionContext.Kind, "image", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(_lastSelectionContext.ImageBase64) || string.IsNullOrWhiteSpace(_lastSelectionContext.ImageMimeType)))
        {
            _resultPanelWindow.ShowInfo("Stored image context is unavailable.", _cursorTracker.CurrentPosition);
            return;
        }

        if (!string.Equals(_lastSelectionContext.Kind, "image", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(_lastSelectionContext.Text))
        {
            _resultPanelWindow.ShowInfo("Stored text context is unavailable.", _cursorTracker.CurrentPosition);
            return;
        }

        var response = await ReAnalyzeLastSelectionAsync(decision, cancellationToken);
        var updatedContext = _lastSelectionContext with { VoiceCommand = decision.VoiceCommand };
        await CompleteSuccessfulRunAsync(response, _cursorTracker.CurrentPosition, updatedContext);
    }

    private async Task<BrowserActionPlanResponse> BuildBrowserActionPlanAsync(
        BrowserPageContext planContext,
        string selectedAction,
        string? voiceCommand,
        string? executionInstruction,
        CancellationToken cancellationToken)
    {
        return await _geminiClient.PlanBrowserActionAsync(
            new BrowserActionPlanRequest
            {
                OriginalText = _lastSelectionContext?.Text ?? string.Empty,
                ResultText = _lastResult,
                Action = selectedAction,
                VoiceCommand = voiceCommand,
                ExecutionInstruction = executionInstruction,
                ContentType = _lastSelectionContext?.ContentType ?? "general_text",
                BrowserContext = planContext
            },
            cancellationToken);
    }

    private async Task<BrowserActionPlanResponse> RefineBrowserActionPlanAsync(
        BrowserPageContext planContext,
        string selectedAction,
        string? voiceCommand,
        string? executionInstruction,
        BrowserActionPlanResponse currentPlan,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executionInstruction))
        {
            return currentPlan;
        }

        return await _geminiClient.RefineBrowserActionPlanAsync(
            new BrowserActionPlanRefineRequest
            {
                OriginalText = _lastSelectionContext?.Text ?? string.Empty,
                ResultText = _lastResult,
                Action = selectedAction,
                VoiceCommand = voiceCommand,
                ExecutionInstruction = executionInstruction,
                ContentType = _lastSelectionContext?.ContentType ?? "general_text",
                BrowserContext = planContext,
                CurrentPlan = currentPlan
            },
            cancellationToken);
    }

    private static string? CombineExecutionInstruction(string? baseInstruction, string? additionalInstruction)
    {
        var trimmedAdditional = additionalInstruction?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedAdditional))
        {
            return baseInstruction;
        }

        if (string.IsNullOrWhiteSpace(baseInstruction))
        {
            return trimmedAdditional;
        }

        if (baseInstruction.Contains(trimmedAdditional, StringComparison.OrdinalIgnoreCase))
        {
            return baseInstruction;
        }

        return $"{baseInstruction.Trim()}. Follow this execution instruction exactly: {trimmedAdditional}";
    }

    private static Task<ActionPlanPreviewDecision> ShowActionPlanPreviewAsync(BrowserActionPlanResponse plan, string currentAction, string? initialInstruction)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var preview = new ActionPlanPreviewWindow(plan, currentAction, initialInstruction);
            var accepted = preview.ShowDialog() == true;
            return new ActionPlanPreviewDecision(accepted, preview.ChangeResultRequested, preview.AdditionalInstruction);
        }).Task;
    }

    private async void ResultPanelWindowOnUndoRequested(object? sender, EventArgs e)
    {
        if (_undoHistory.Count == 0)
        {
            _resultPanelWindow.ShowInfo("Nothing to undo yet.", _cursorTracker.CurrentPosition, allowTakeAction: !string.IsNullOrWhiteSpace(_lastResult));
            return;
        }

        var undoEntry = _undoHistory.Pop();
        UpdateUndoAvailability();

        try
        {
            switch (undoEntry.Target)
            {
                case UndoTarget.ExternalWindow:
                    if (undoEntry.WindowHandle != IntPtr.Zero)
                    {
                        NativeMethods.BringToFront(undoEntry.WindowHandle);
                        await Task.Delay(50);
                    }

                    NativeMethods.SendCtrlZ();
                    break;
                case UndoTarget.Browser:
                    await _browserAutomationClient.ExecutePlanAsync(
                        new BrowserActionPlanResponse
                        {
                            Goal = "undo_last_browser_action",
                            Summary = undoEntry.Description,
                            Steps =
                            [
                                new BrowserActionStep
                                {
                                    Tool = "press_key",
                                    Key = "Control+Z"
                                }
                            ]
                        },
                        CancellationToken.None);
                    break;
            }

            _orbOverlayWindow.SetState(OrbState.Completed, "Undo applied");
            _resultPanelWindow.ShowInfo(
                $"Undo applied.{Environment.NewLine}{Environment.NewLine}{undoEntry.Description}",
                _cursorTracker.CurrentPosition,
                allowTakeAction: !string.IsNullOrWhiteSpace(_lastResult));
        }
        catch (Exception ex)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Undo failed");
            _resultPanelWindow.ShowInfo(
                $"Undo failed.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                _cursorTracker.CurrentPosition,
                allowTakeAction: !string.IsNullOrWhiteSpace(_lastResult));
        }
    }

    private void PushUndoHistory(UndoHistoryEntry entry)
    {
        _undoHistory.Push(entry);
        while (_undoHistory.Count > MaxUndoHistory)
        {
            var kept = _undoHistory.Take(MaxUndoHistory).Reverse().ToList();
            _undoHistory.Clear();
            foreach (var item in kept)
            {
                _undoHistory.Push(item);
            }
        }

        UpdateUndoAvailability();
    }

    private void UpdateUndoAvailability()
    {
        _resultPanelWindow.SetUndoAvailable(_undoHistory.Count > 0);
    }

    private async void ResultPanelWindowOnMoreOptionsRequested(object? sender, EventArgs e)
    {
        if (_lastSelectionContext is null)
        {
            _resultPanelWindow.ShowInfo("No recent selection to re-run.", _cursorTracker.CurrentPosition);
            return;
        }

        if (!await _runLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            _orbOverlayWindow.MoveToTopRight();
            EnsureWindowVisible(_orbOverlayWindow);
            OnProcessingStart?.Invoke(this, EventArgs.Empty);

            var decision = await ResolveActionDecisionAsync(
                _lastSelectionContext.Suggestion,
                _lastSelectionContext.VoiceCommand,
                forceActionMenu: true,
                CancellationToken.None);

            if (decision is null)
            {
                _orbOverlayWindow.SetState(OrbState.Idle, "Action menu canceled");
                return;
            }
            await ReRunLastSelectionAsync(decision.Value, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _orbOverlayWindow.SetState(OrbState.Completed, "Failed to process request");
            _resultPanelWindow.ShowInfo($"Error: {ex.Message}", _cursorTracker.CurrentPosition);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private static string NormalizeActionHint(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return "summarize";
        }

        var normalized = action.Trim().ToLowerInvariant();
        normalized = normalized
            .Replace(">", string.Empty, StringComparison.Ordinal)
            .Replace("...", string.Empty, StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);
        normalized = string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return normalized switch
        {
            "answer question" => "answer_question",
            "answer" => "answer_question",
            "summarise" => "summarize",
            "expand" => "expand_text",
            "expand text" => "expand_text",
            "bullet points" => "bullet_points",
            "extract bullet points" => "bullet_points",
            "convert to bullet points" => "bullet_points",
            "extract insights" => "extract_insights",
            "rewrite structured" => "rewrite_structured",
            "optimize prompt" => "optimize_prompt",
            "prompt optimizer" => "optimize_prompt",
            "prompt optimisation" => "optimize_prompt",
            "prompt optimization" => "optimize_prompt",
            "improve prompt" => "optimize_prompt",
            "structure prompt" => "optimize_prompt",
            "polish email" => "polish_email",
            "draft reply" => "draft_reply",
            "improve code" => "improve_code",
            "explain code" => "explain_code",
            "debug code" => "debug_code",
            "optimize code" => "optimize_code",
            "compare prices" => "compare_prices",
            "extract product info" => "extract_product_info",
            "show product details" => "show_product_details",
            "find reviews" => "find_reviews",
            "suggest captions" => "suggest_captions",
            "generate captions" => "generate_captions",
            "describe image" => "describe_image",
            "find key details" => "extract_key_details",
            "identify objects" => "identify_objects",
            "extract key details" => "extract_key_details",
            "extract dominant colors" => "extract_dominant_colors",
            "identify image colors" => "extract_dominant_colors",
            "dominant colors" => "extract_dominant_colors",
            "color palette" => "extract_dominant_colors",
            "ocr extract text" => "ocr_extract_text",
            "extract table data" => "extract_table_data",
            _ => normalized.Replace(" ", "_", StringComparison.Ordinal)
        };
    }

    private static string ToDisplayAction(string actionHint)
    {
        return NormalizeActionHint(actionHint) switch
        {
            "answer_question" => "Answer Question",
            "expand_text" => "Expand",
            "bullet_points" => "Bullet Points",
            "extract_insights" => "Extract Insights",
            "rewrite_structured" => "Rewrite Structured",
            "optimize_prompt" => "Prompt Optimizer",
            "polish_email" => "Polish Email",
            "draft_reply" => "Draft Reply",
            "improve_code" => "Improve Code",
            "explain_code" => "Explain Code",
            "debug_code" => "Debug Code",
            "optimize_code" => "Optimize Code",
            "extract_product_info" => "Extract Product Info",
            "compare_prices" => "Compare Prices",
            "show_product_details" => "Show Product Details",
            "find_reviews" => "Find Reviews",
            "suggest_captions" => "Suggest Captions",
            "generate_captions" => "Generate Captions",
            "describe_image" => "Describe Image",
            "identify_objects" => "Identify Objects",
            "extract_key_details" => "Extract Key Details",
            "extract_dominant_colors" => "Extract Dominant Colors",
            "ocr_extract_text" => "OCR Extract Text",
            "extract_table_data" => "Extract Table Data",
            var x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Replace("_", " ", StringComparison.Ordinal))
        };
    }

    private static string ResolveExecutionDisplayAction(string? actionHint, string? voiceCommand)
    {
        if (!string.IsNullOrWhiteSpace(voiceCommand) && IsBroadVoiceTaskAction(actionHint))
        {
            return "Custom Task";
        }

        return ToDisplayAction(actionHint ?? "summarize");
    }

    private static string ResolveResultDisplayAction(AgentResponse response, CapturedSelectionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.VoiceCommand) && IsBroadVoiceTaskAction(response.Action))
        {
            return "Custom Task";
        }

        return ToDisplayAction(response.Action);
    }

    private static bool IsBroadVoiceTaskAction(string? actionHint)
    {
        return NormalizeActionHint(actionHint ?? string.Empty) switch
        {
            "summarize" => true,
            "extract_insights" => true,
            "bullet_points" => true,
            "rewrite" => true,
            "rewrite_structured" => true,
            "expand_text" => true,
            "explain" => true,
            _ => false
        };
    }

    private static string ModeToProtocol(InteractionMode mode)
    {
        return mode == InteractionMode.Guided ? "guided" : "smart";
    }

    private static string ModeToDisplay(InteractionMode mode)
    {
        return mode == InteractionMode.Guided ? "Guided" : "Smart";
    }

    private (IntPtr WindowHandle, string? ProcessName) ResolveSelectionSource()
    {
        var activeHandle = NativeMethods.GetActiveWindowHandle();
        if (IsExternalWindowHandle(activeHandle))
        {
            return (activeHandle, NativeMethods.GetProcessNameForWindow(activeHandle));
        }

        var trackedHandle = _windowFocusTracker.LastExternalWindowHandle;
        if (IsExternalWindowHandle(trackedHandle))
        {
            return (trackedHandle, _windowFocusTracker.LastExternalProcessName ?? NativeMethods.GetProcessNameForWindow(trackedHandle));
        }

        if (IsExternalWindowHandle(_lastExternalWindow))
        {
            return (_lastExternalWindow, NativeMethods.GetProcessNameForWindow(_lastExternalWindow));
        }

        return (IntPtr.Zero, null);
    }

    private static bool IsExternalWindowHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var processId = NativeMethods.GetProcessIdForWindow(handle);
        return processId != 0 && processId != Environment.ProcessId;
    }

    private void EnsureWindowVisible(System.Windows.Window window)
    {
        if (ReferenceEquals(window, _orbOverlayWindow) && !_showOrbDuringWorkflow)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }
    }

    private void NavigateGuidedMenu(int delta)
    {
        _orbOverlayWindow.NavigateOptionMenu(delta);
        ScheduleGuidedMenuAutoCommit();
    }

    private void ScheduleGuidedMenuAutoCommit()
    {
        _guidedMenuAutoCommitTimer.Stop();
        if (_orbOverlayWindow.IsMenuVisible)
        {
            _guidedMenuAutoCommitTimer.Start();
        }
    }

    private void StopGuidedMenuAutoCommit()
    {
        _guidedMenuAutoCommitTimer.Stop();
    }

    private void GuidedMenuAutoCommitTimerOnTick(object? sender, EventArgs e)
    {
        _guidedMenuAutoCommitTimer.Stop();
        _ = _orbOverlayWindow.TryConfirmMenuSelection();
    }

    private readonly record struct ActionDecision(string? ActionHint, string? VoiceCommand, string? DisplayAction = null);

    private readonly record struct GuidedMenuOptions(IReadOnlyList<string> InitialOptions, IReadOnlyList<string> DeferredOptions);

    private readonly record struct ActionPlanPreviewDecision(bool Approved, bool ChangeResultRequested, string? AdditionalInstruction);

    private sealed record CapturedSelectionContext
    {
        public string Kind { get; init; } = "text";

        public string? Text { get; init; }

        public string? ImageBase64 { get; init; }

        public string? ImageMimeType { get; init; }

        public string ContentType { get; init; } = "general_text";

        public string? VoiceCommand { get; init; }

        public IntPtr SourceWindowHandle { get; init; } = IntPtr.Zero;

        public string? SourceProcessName { get; init; }

        public string ExecutedAction { get; init; } = string.Empty;

        public SuggestionResponse Suggestion { get; init; } = new();
    }

    private static string BuildTakeActionSummary(BrowserActionPlanResponse plan, BrowserExecutionResponse execution, ExtensionBridgeHealthResponse? extensionHealth)
    {
        var lines = new List<string>
        {
            "Take Action completed.",
            string.Empty,
            $"Plan: {plan.Summary}",
            $"Status: {execution.Message}",
            $"Steps executed: {execution.ExecutedSteps}"
        };

        if (!string.IsNullOrWhiteSpace(execution.Details))
        {
            lines.Add($"Details: {execution.Details}");
        }

        if (!string.IsNullOrWhiteSpace(execution.PageContext?.Title))
        {
            lines.Add($"Page: {execution.PageContext.Title}");
        }

        if (!string.IsNullOrWhiteSpace(execution.PageContext?.Url))
        {
            lines.Add(execution.PageContext.Url);
        }

        if (execution.Logs.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Executed tools:");
            foreach (var log in execution.Logs.Take(8))
            {
                lines.Add($"- {log}");
            }
        }

        if (IsExtensionUnavailable(extensionHealth))
        {
            lines.Add(string.Empty);
            lines.Add("Current-tab bridge:");
            lines.Add("The Chromium extension bridge is not connected, so Cursivis fell back to weaker browser automation. Reload the unpacked extension and keep your logged-in browser tab open for best Take Action reliability.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsExtensionUnavailable(ExtensionBridgeHealthResponse? extensionHealth)
    {
        return extensionHealth is null || !extensionHealth.Ok || !extensionHealth.ExtensionConnected;
    }

    private readonly record struct UndoHistoryEntry(UndoTarget Target, IntPtr WindowHandle, string Description);

    private enum UndoTarget
    {
        ExternalWindow,
        Browser
    }

    private static bool ParseBoolEnv(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" => true,
            "true" => true,
            "yes" => true,
            "on" => true,
            "0" => false,
            "false" => false,
            "no" => false,
            "off" => false,
            _ => defaultValue
        };
    }

    private static double ParseDoubleEnv(string name, double defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    private static int ParseIntEnv(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultValue;
    }

    private (string? ImageBase64, string? ImageMimeType) TryCaptureTextVisualContext(Point cursor)
    {
        if (!_textVisualContextEnabled)
        {
            return default;
        }

        var imageBase64 = _screenCaptureService.TryCaptureContextAroundCursorAsBase64Png(
            cursor,
            _textVisualContextWidth,
            _textVisualContextHeight);

        return string.IsNullOrWhiteSpace(imageBase64)
            ? default
            : (imageBase64, "image/png");
    }

    private async Task<BrowserExecutionResponse?> TryRecoverExtensionExecutionFailureAsync(
        BrowserActionPlanResponse plan,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (plan.Steps.Count == 0 || !plan.Steps.Any(step => string.Equals(step.Tool, "fill_editor", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var extensionContext = await _extensionAutomationClient.TryGetActiveTabContextAsync(cancellationToken);
        var pageContext = extensionContext?.PageContext;
        if (extensionContext?.Ok != true || pageContext is null || string.IsNullOrWhiteSpace(pageContext.VisibleText))
        {
            return null;
        }

        var matchingStep = plan.Steps
            .Where(step => string.Equals(step.Tool, "fill_editor", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(step.Text))
            .FirstOrDefault(step => VisibleTextContainsPlannedText(pageContext.VisibleText, step.Text!));

        if (matchingStep is null)
        {
            return null;
        }

        return new BrowserExecutionResponse
        {
            Ok = true,
            Success = true,
            ExecutedSteps = plan.Steps.Count,
            Message = "Applied in the current logged-in browser tab.",
            Details = $"Detected the drafted content in the page after the extension reported '{exception.Message}'. Cursivis treated the action as successful because the reply text is visibly present.",
            Logs = plan.Steps.Select(step => step.Tool).Take(8).ToList(),
            PageContext = pageContext
        };
    }

    private static bool VisibleTextContainsPlannedText(string visibleText, string expectedText)
    {
        if (string.IsNullOrWhiteSpace(visibleText) || string.IsNullOrWhiteSpace(expectedText))
        {
            return false;
        }

        var normalizedVisible = NormalizeVisibleText(visibleText);
        var normalizedExpected = NormalizeVisibleText(expectedText);
        if (normalizedExpected.Length < 24)
        {
            return false;
        }

        if (normalizedVisible.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lines = expectedText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeVisibleText)
            .Where(line => line.Length >= 18)
            .ToList();

        if (lines.Count == 0)
        {
            return ContainsEdgeSnippet(normalizedVisible, normalizedExpected);
        }

        var matchedLines = lines.Count(line => normalizedVisible.Contains(line, StringComparison.OrdinalIgnoreCase));
        return matchedLines >= Math.Min(2, lines.Count) || ContainsEdgeSnippet(normalizedVisible, normalizedExpected);
    }

    private static string NormalizeVisibleText(string value)
    {
        return string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsEdgeSnippet(string visibleText, string expectedText)
    {
        if (string.IsNullOrWhiteSpace(visibleText) || string.IsNullOrWhiteSpace(expectedText))
        {
            return false;
        }

        var firstSnippetLength = Math.Min(140, expectedText.Length);
        var lastSnippetLength = Math.Min(140, expectedText.Length);
        var firstSnippet = expectedText[..firstSnippetLength];
        var lastSnippet = expectedText[^lastSnippetLength..];

        return (firstSnippet.Length >= 40 && visibleText.Contains(firstSnippet, StringComparison.OrdinalIgnoreCase)) ||
               (lastSnippet.Length >= 40 && visibleText.Contains(lastSnippet, StringComparison.OrdinalIgnoreCase));
    }
}
