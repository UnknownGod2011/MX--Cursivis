using Cursivis.Companion.Infrastructure;
using Cursivis.Companion.Models;
using Cursivis.Companion.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Cursivis.Companion.Views;

public partial class OrbOverlayWindow : Window
{
    private readonly Border[] _actionChips;
    private readonly TextBlock[] _actionTexts;
    private readonly Ellipse[] _magicRings;
    private readonly string[] _idleCommands = ["Trigger", "Talk", "Snip-it", "Action"];
    private readonly DispatcherTimer _actionRingHideTimer;
    private readonly DispatcherTimer _workflowHideTimer;
    private Storyboard? _pulseStoryboard;
    private Storyboard? _rotationStoryboard;
    private Storyboard? _completionStoryboard;
    private OrbState _currentState = OrbState.Idle;
    private bool _isUserPositioned;
    private bool _hasPosition;
    private bool _isActionRingVisible;
    private bool _isMenuMode;
    private bool _showOrbDuringWorkflow = true;
    private bool _showListeningStopButton = true;
    private string _modeDisplay = "Smart";
    private int _idleCommandIndex;
    private List<string> _menuOptions = [];
    private int _selectedMenuIndex;
    private CompanionThemeMode _themeMode = CompanionThemeService.CurrentMode;

    public OrbOverlayWindow()
    {
        InitializeComponent();

        _actionChips =
        [
            ActionChipTop,
            ActionChipUpperRight,
            ActionChipLowerRight,
            ActionChipLowerLeft,
            ActionChipUpperLeft
        ];

        _actionTexts =
        [
            ActionTopText,
            ActionUpperRightText,
            ActionLowerRightText,
            ActionLowerLeftText,
            ActionUpperLeftText
        ];

        _magicRings =
        [
            MagicRing1,
            MagicRing2,
            MagicRing3,
            MagicRing4
        ];

        foreach (var ring in _magicRings)
        {
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            ring.RenderTransform = new ScaleTransform(0.72, 0.72);
        }

        foreach (var chip in _actionChips)
        {
            chip.Visibility = Visibility.Collapsed;
            chip.Opacity = 0;
            chip.IsHitTestVisible = false;
        }

        _actionRingHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1250)
        };
        _actionRingHideTimer.Tick += (_, _) => HideActionRing();
        _workflowHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _workflowHideTimer.Tick += (_, _) =>
        {
            _workflowHideTimer.Stop();
            if (!_isMenuMode)
            {
                Hide();
            }
        };

        CompanionThemeService.ThemeChanged += CompanionThemeServiceOnThemeChanged;
        UiPresentation.ApplyShinyText(StatusText, ColorFromHex("#D0D6DD"), Colors.White, 2.6, 0.48, 1.1);
        ResetListeningLevelVisual();
        ApplyPalette(OrbState.Idle);
        ApplyThemeChrome();
        UpdateIdleTexts();
        UpdatePresentationMode();
        Closed += (_, _) => CompanionThemeService.ThemeChanged -= CompanionThemeServiceOnThemeChanged;
    }

    public event EventHandler<string>? MenuOptionSelected;

    public event EventHandler<string>? IdleCommandInvoked;

    public event EventHandler<int>? ModeStepRequested;

    public event EventHandler? ListeningStopRequested;

    public bool IsMenuVisible => _isMenuMode && _menuOptions.Count > 0;

    public string CurrentIdleCommand => _idleCommands[_idleCommandIndex];

    public Rect GetPromptAnchorBounds()
    {
        if (!IsLoaded)
        {
            return new Rect(Left, Top, Width, Height);
        }

        UpdateLayout();
        if (OrbCore.ActualWidth <= 0 || OrbCore.ActualHeight <= 0)
        {
            return new Rect(Left, Top, Width, Height);
        }

        var orbOrigin = OrbCore.TranslatePoint(new Point(0, 0), this);
        return new Rect(
            Left + orbOrigin.X,
            Top + orbOrigin.Y,
            OrbCore.ActualWidth,
            OrbCore.ActualHeight);
    }

    public void MoveNearCursor(Point cursor)
    {
        if (!_hasPosition)
        {
            MoveToTopRight();
        }
    }

    public void MoveToTopRight(bool force = false)
    {
        if (_isUserPositioned && !force)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Top + 20;
        _hasPosition = true;
    }

    public void SetModeDisplay(string modeDisplay)
    {
        _modeDisplay = string.IsNullOrWhiteSpace(modeDisplay) ? "Smart" : modeDisplay;
        UpdateIdleTexts();
        if (_currentState == OrbState.Idle && !_isMenuMode)
        {
            StateText.Text = _modeDisplay;
            StatusText.Text = $"Ready ({_modeDisplay})";
        }
    }

    public void SetShowOrbDuringWorkflow(bool showOrbDuringWorkflow)
    {
        _showOrbDuringWorkflow = showOrbDuringWorkflow;

        if (!_showOrbDuringWorkflow)
        {
            _workflowHideTimer.Stop();
            Hide();
        }
    }

    public void SetListeningStopButtonVisible(bool isVisible)
    {
        _showListeningStopButton = isVisible;
        var shouldShow = _currentState == OrbState.Listening && _showListeningStopButton;
        ListeningStopButton.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        ListeningStopButton.IsHitTestVisible = shouldShow;
    }

    public void SetState(OrbState state, string status)
    {
        _currentState = state;
        StateText.Text = state == OrbState.Idle ? _modeDisplay : state.ToString();
        StatusText.Text = status;
        var showListeningStop = state == OrbState.Listening && _showListeningStopButton;
        ListeningStopButton.Visibility = showListeningStop ? Visibility.Visible : Visibility.Collapsed;
        ListeningStopButton.IsHitTestVisible = showListeningStop;
        ApplyPalette(state);

        switch (state)
        {
            case OrbState.Processing:
                PresentForWorkflow();
                StartPulse(isListening: false);
                AnimateBaseScale(1.0);
                break;
            case OrbState.Listening:
                PresentForWorkflow();
                StartPulse(isListening: true);
                AnimateBaseScale(1.0);
                break;
            case OrbState.Completed:
                StopPulse();
                ResetListeningLevelVisual();
                PlayCompletionBurst();
                AnimateBaseScale(0.98);
                ScheduleHideAfterWorkflow();
                break;
            default:
                StopPulse();
                ResetListeningLevelVisual();
                AnimateBaseScale(_isMenuMode ? 1.0 : 0.88);
                ScheduleHideAfterWorkflow();
                break;
        }

        UpdatePresentationMode();
    }

    public void SetListeningLevel(double level)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => SetListeningLevel(level));
            return;
        }

        if (_currentState != OrbState.Listening)
        {
            ResetListeningLevelVisual();
            return;
        }

        var clamped = Math.Clamp(level, 0, 1);
        VoiceGlowHalo.Opacity = 0;
        VoiceGlowScaleTransform.ScaleX = 0.98 + (clamped * 0.16);
        VoiceGlowScaleTransform.ScaleY = 0.98 + (clamped * 0.16);
    }

    public void UpdateActionRing(IReadOnlyList<string> actions, int selectedIndex)
    {
        var visibleEntries = BuildVisibleEntries(actions, selectedIndex);
        var isDark = _themeMode == CompanionThemeMode.Dark;

        for (var i = 0; i < _actionChips.Length; i++)
        {
            if (i >= visibleEntries.Count)
            {
                _actionTexts[i].Text = string.Empty;
                _actionChips[i].Tag = null;
                continue;
            }

            var entry = visibleEntries[i];
            _actionTexts[i].Text = entry.Label;
            _actionChips[i].Tag = entry.ActualIndex;

            var isSelected = entry.ActualIndex == selectedIndex;
            _actionChips[i].Background = isSelected
                ? isDark
                    ? CreateChipBrush(Color.FromArgb(214, 19, 23, 28), Color.FromArgb(196, 28, 33, 39), Color.FromArgb(214, 17, 21, 25))
                    : CreateChipBrush(Color.FromArgb(240, 255, 255, 255), Color.FromArgb(228, 245, 246, 247), Color.FromArgb(240, 255, 255, 255))
                : isDark
                    ? CreateChipBrush(Color.FromArgb(168, 17, 21, 26), Color.FromArgb(148, 20, 24, 29), Color.FromArgb(168, 15, 19, 24))
                    : CreateChipBrush(Color.FromArgb(225, 255, 255, 255), Color.FromArgb(214, 244, 245, 246), Color.FromArgb(225, 255, 255, 255));
            _actionChips[i].BorderBrush = isSelected
                ? new SolidColorBrush(isDark ? ColorFromHex("#FFF5F7FA") : ColorFromHex("#FF1A1F24"))
                : new SolidColorBrush(isDark ? Color.FromArgb(72, 255, 255, 255) : Color.FromArgb(34, 32, 32, 32));
            _actionTexts[i].Foreground = isSelected
                ? new SolidColorBrush(isDark ? Colors.White : ColorFromHex("#FF12161A"))
                : new SolidColorBrush(isDark ? ColorFromHex("#FFD9DEE4") : ColorFromHex("#FF525861"));
        }
    }

    public void SetActionRingVisible(bool isVisible)
    {
        foreach (var chip in _actionChips)
        {
            AnimateActionChip(chip, isVisible);
            chip.IsHitTestVisible = isVisible;
        }

        _isActionRingVisible = isVisible;
        UpdatePresentationMode();
    }

    public void ShowActionRingTemporarily()
    {
        _isMenuMode = false;
        PresentForWorkflow();
        SetActionRingVisible(true);
        _actionRingHideTimer.Stop();
        _actionRingHideTimer.Start();
    }

    public void HideActionRing()
    {
        _actionRingHideTimer.Stop();
        if (_isActionRingVisible)
        {
            SetActionRingVisible(false);
        }
    }

    public void ShowOptionMenu(IReadOnlyList<string> options, int selectedIndex = 0)
    {
        PresentForWorkflow();
        _isMenuMode = true;
        _menuOptions = options
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _selectedMenuIndex = _menuOptions.Count == 0
            ? 0
            : Math.Clamp(selectedIndex, 0, _menuOptions.Count - 1);

        StateText.Text = "Guided";
        StatusText.Text = "Choose an action";
        AnimateBaseScale(1.0);
        UpdateActionRing(_menuOptions, _selectedMenuIndex);
        SetActionRingVisible(_menuOptions.Count > 0);
    }

    public void UpdateOptionMenu(IReadOnlyList<string> options, int? selectedIndex = null)
    {
        if (!_isMenuMode)
        {
            ShowOptionMenu(options, selectedIndex ?? 0);
            return;
        }

        var selectedLabel = selectedIndex is null && _selectedMenuIndex >= 0 && _selectedMenuIndex < _menuOptions.Count
            ? _menuOptions[_selectedMenuIndex]
            : null;

        _menuOptions = options
            .Where(option => !string.IsNullOrWhiteSpace(option))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_menuOptions.Count == 0)
        {
            HideOptionMenu();
            return;
        }

        if (selectedIndex.HasValue)
        {
            _selectedMenuIndex = Math.Clamp(selectedIndex.Value, 0, _menuOptions.Count - 1);
        }
        else if (!string.IsNullOrWhiteSpace(selectedLabel))
        {
            var preservedIndex = _menuOptions.FindIndex(option => string.Equals(option, selectedLabel, StringComparison.OrdinalIgnoreCase));
            _selectedMenuIndex = preservedIndex >= 0 ? preservedIndex : 0;
        }
        else
        {
            _selectedMenuIndex = 0;
        }

        UpdateActionRing(_menuOptions, _selectedMenuIndex);
        SetActionRingVisible(true);
    }

    public void HideOptionMenu()
    {
        _isMenuMode = false;
        _menuOptions.Clear();
        _selectedMenuIndex = 0;
        HideActionRing();
        UpdatePresentationMode();
    }

    public void NavigateOptionMenu(int delta)
    {
        if (!_isMenuMode || _menuOptions.Count == 0 || delta == 0)
        {
            return;
        }

        _selectedMenuIndex = (_selectedMenuIndex + Math.Sign(delta) + _menuOptions.Count) % _menuOptions.Count;
        UpdateActionRing(_menuOptions, _selectedMenuIndex);
        SetActionRingVisible(true);
    }

    public bool TryConfirmMenuSelection()
    {
        if (!_isMenuMode || _menuOptions.Count == 0)
        {
            return false;
        }

        MenuOptionSelected?.Invoke(this, _menuOptions[_selectedMenuIndex]);
        return true;
    }

    public void NavigateIdleCommand(int delta)
    {
        if (_isMenuMode || delta == 0)
        {
            return;
        }

        _idleCommandIndex = (_idleCommandIndex + Math.Sign(delta) + _idleCommands.Length) % _idleCommands.Length;
        UpdateIdleTexts();
    }

    public void CollapseToIdleShell()
    {
        HideOptionMenu();
        SetState(OrbState.Idle, $"Ready ({_modeDisplay})");
    }

    private void PresentForWorkflow()
    {
        _workflowHideTimer.Stop();
        if (!_showOrbDuringWorkflow)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    private void ScheduleHideAfterWorkflow()
    {
        _workflowHideTimer.Stop();
        if (!_showOrbDuringWorkflow || _isMenuMode)
        {
            if (!_showOrbDuringWorkflow)
            {
                Hide();
            }

            return;
        }

        _workflowHideTimer.Start();
    }

    private void StartPulse(bool isListening)
    {
        _pulseStoryboard?.Stop();

        var baseScale = Math.Max(OrbScaleTransform.ScaleX, 0.96);
        var xAnim = new DoubleAnimation
        {
            From = baseScale,
            To = baseScale + 0.07,
            Duration = TimeSpan.FromMilliseconds(620),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        var yAnim = xAnim.Clone();
        Storyboard.SetTarget(xAnim, OrbScaleTransform);
        Storyboard.SetTargetProperty(xAnim, new PropertyPath(ScaleTransform.ScaleXProperty));
        Storyboard.SetTarget(yAnim, OrbScaleTransform);
        Storyboard.SetTargetProperty(yAnim, new PropertyPath(ScaleTransform.ScaleYProperty));

        var glowX = new DoubleAnimation
        {
            From = 1.0,
            To = isListening ? 1.06 : 1.14,
            Duration = TimeSpan.FromMilliseconds(760),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var glowY = glowX.Clone();
        var glowOpacity = new DoubleAnimation
        {
            From = isListening ? 0.42 : 0.68,
            To = isListening ? 0.62 : 0.98,
            Duration = TimeSpan.FromMilliseconds(760),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };

        Storyboard.SetTarget(glowX, GlowScaleTransform);
        Storyboard.SetTargetProperty(glowX, new PropertyPath(ScaleTransform.ScaleXProperty));
        Storyboard.SetTarget(glowY, GlowScaleTransform);
        Storyboard.SetTargetProperty(glowY, new PropertyPath(ScaleTransform.ScaleYProperty));
        Storyboard.SetTarget(glowOpacity, GlowHalo);
        Storyboard.SetTargetProperty(glowOpacity, new PropertyPath(UIElement.OpacityProperty));

        _pulseStoryboard = new Storyboard();
        _pulseStoryboard.Children.Add(xAnim);
        _pulseStoryboard.Children.Add(yAnim);
        _pulseStoryboard.Children.Add(glowX);
        _pulseStoryboard.Children.Add(glowY);
        _pulseStoryboard.Children.Add(glowOpacity);

        UiPresentation.ApplyShinyText(
            StatusText,
            _themeMode == CompanionThemeMode.Dark
                ? (isListening ? ColorFromHex("#FFF5F7FA") : ColorFromHex("#FFC0C8D0"))
                : (isListening ? ColorFromHex("#FF2A3036") : ColorFromHex("#FF5F666E")),
            _themeMode == CompanionThemeMode.Dark ? Colors.White : ColorFromHex("#FFB9C0C8"),
            isListening ? 2.0 : 2.35,
            _themeMode == CompanionThemeMode.Dark ? 0.64 : 0.28,
            _themeMode == CompanionThemeMode.Dark ? 1.1 : 1.03);
        _pulseStoryboard.Begin();
        StartOrbitRotation(isListening ? 4.0 : 7.5);
    }

    private void StopPulse()
    {
        _pulseStoryboard?.Stop();
        _rotationStoryboard?.Stop();
        GlowScaleTransform.ScaleX = 1;
        GlowScaleTransform.ScaleY = 1;
        GlowHalo.Opacity = 0;
        UiPresentation.SetFlatText(StatusText, _themeMode == CompanionThemeMode.Dark ? Colors.White : ColorFromHex("#FF2A3037"));
    }

    private void ResetListeningLevelVisual()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(ResetListeningLevelVisual);
            return;
        }

        VoiceGlowHalo.Opacity = 0;
        VoiceGlowScaleTransform.ScaleX = 0.98;
        VoiceGlowScaleTransform.ScaleY = 0.98;
    }

    private void UpdatePresentationMode()
    {
        var showCompact = _currentState == OrbState.Idle && !_isActionRingVisible && !_isMenuMode;
        CompactIdlePanel.Visibility = showCompact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedStatePanel.Visibility = showCompact ? Visibility.Collapsed : Visibility.Visible;
        IdleGlowHalo.Visibility = Visibility.Collapsed;
        GlowHalo.Visibility = Visibility.Collapsed;
        OrbitRingCanvas.Visibility = Visibility.Collapsed;
        AnimateBaseScale(showCompact ? 0.88 : 1.0);
    }

    private void UpdateIdleTexts()
    {
        ModeText.Text = _modeDisplay;
        IdleRunButton.Content = _idleCommands[_idleCommandIndex];
    }

    private static IReadOnlyList<(int ActualIndex, string Label)> BuildVisibleEntries(IReadOnlyList<string> options, int selectedIndex)
    {
        if (options.Count == 0)
        {
            return [];
        }

        var clampedSelected = Math.Clamp(selectedIndex, 0, Math.Max(options.Count - 1, 0));
        var startIndex = Math.Max(0, Math.Min(clampedSelected - 2, Math.Max(0, options.Count - 5)));
        var entries = new List<(int ActualIndex, string Label)>();
        for (var index = startIndex; index < Math.Min(startIndex + 5, options.Count); index++)
        {
            entries.Add((index, CompactLabel(options[index])));
        }

        return entries;
    }

    private static string CompactLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.StartsWith("__deferred__", StringComparison.Ordinal))
        {
            return "...";
        }

        var compact = value.Trim()
            .Replace("Custom Voice Command", "Custom Task", StringComparison.OrdinalIgnoreCase)
            .Replace("Extract Insights", "Insights", StringComparison.OrdinalIgnoreCase)
            .Replace("Bullet Points", "Bullet Pts", StringComparison.OrdinalIgnoreCase)
            .Replace("Answer Question", "Answer", StringComparison.OrdinalIgnoreCase)
            .Replace("Rewrite Structured", "Rewrite", StringComparison.OrdinalIgnoreCase)
            .Replace("Generate Captions", "Captions", StringComparison.OrdinalIgnoreCase)
            .Replace("Extract Dominant Colors", "Colors", StringComparison.OrdinalIgnoreCase);

        return compact.Length <= 18 ? compact : $"{compact[..15]}...";
    }

    private static void AnimateActionChip(Border chip, bool show)
    {
        if (show)
        {
            chip.Visibility = Visibility.Visible;
        }

        var animation = new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = TimeSpan.FromMilliseconds(show ? 180 : 220),
            EasingFunction = new QuadraticEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        if (!show)
        {
            animation.Completed += (_, _) => chip.Visibility = Visibility.Collapsed;
        }

        chip.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        MoveToTopRight();
    }

    private void OrbCore_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) is not null)
        {
            return;
        }

        _isUserPositioned = true;
        _hasPosition = true;
        try
        {
            DragMove();
        }
        catch
        {
            // Ignore drag interruptions.
        }
    }

    private void ActionChip_OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isMenuMode || sender is not Border chip || chip.Tag is not int actualIndex || actualIndex < 0 || actualIndex >= _menuOptions.Count)
        {
            return;
        }

        _selectedMenuIndex = actualIndex;
        UpdateActionRing(_menuOptions, _selectedMenuIndex);
        MenuOptionSelected?.Invoke(this, _menuOptions[_selectedMenuIndex]);
        e.Handled = true;
    }

    private void ModePrevButton_OnClick(object sender, RoutedEventArgs e)
    {
        ModeStepRequested?.Invoke(this, -1);
    }

    private void ModeNextButton_OnClick(object sender, RoutedEventArgs e)
    {
        ModeStepRequested?.Invoke(this, 1);
    }

    private void CommandPrevButton_OnClick(object sender, RoutedEventArgs e)
    {
        NavigateIdleCommand(-1);
    }

    private void CommandNextButton_OnClick(object sender, RoutedEventArgs e)
    {
        NavigateIdleCommand(1);
    }

    private void IdleRunButton_OnClick(object sender, RoutedEventArgs e)
    {
        IdleCommandInvoked?.Invoke(this, _idleCommands[_idleCommandIndex]);
    }

    private void ListeningStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        ListeningStopRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void AnimateBaseScale(double targetScale)
    {
        if (_pulseStoryboard is not null && _currentState is OrbState.Processing or OrbState.Listening)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);

        var animationY = animation.Clone();
        OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animationY, HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyPalette(OrbState state)
    {
        var isDark = _themeMode == CompanionThemeMode.Dark;
        OrbCore.BorderBrush = new SolidColorBrush(isDark ? ColorFromHex("#60FFFFFF") : ColorFromHex("#14000000"));

        switch (state)
        {
            case OrbState.Processing:
                OrbCore.Background = isDark
                    ? CreateOrbBrush(Color.FromArgb(222, 24, 28, 33), Color.FromArgb(206, 17, 21, 25), Color.FromArgb(188, 10, 13, 17))
                    : CreateOrbBrush(Color.FromArgb(238, 255, 255, 255), Color.FromArgb(232, 252, 253, 254), Color.FromArgb(224, 245, 247, 249));
                StateText.Foreground = new SolidColorBrush(isDark ? ColorFromHex("#FFF5F7FA") : ColorFromHex("#FF2C3137"));
                break;
            case OrbState.Listening:
                OrbCore.Background = isDark
                    ? CreateOrbBrush(Color.FromArgb(224, 28, 32, 37), Color.FromArgb(210, 19, 23, 28), Color.FromArgb(194, 12, 16, 21))
                    : CreateOrbBrush(Color.FromArgb(240, 255, 255, 255), Color.FromArgb(234, 252, 253, 254), Color.FromArgb(226, 246, 248, 250));
                StateText.Foreground = new SolidColorBrush(isDark ? ColorFromHex("#FFFFFFFF") : ColorFromHex("#FF252A31"));
                break;
            case OrbState.Completed:
                OrbCore.Background = isDark
                    ? CreateOrbBrush(Color.FromArgb(222, 26, 30, 35), Color.FromArgb(208, 18, 22, 27), Color.FromArgb(192, 11, 15, 19))
                    : CreateOrbBrush(Color.FromArgb(239, 255, 255, 255), Color.FromArgb(233, 252, 253, 254), Color.FromArgb(225, 246, 248, 250));
                StateText.Foreground = new SolidColorBrush(isDark ? ColorFromHex("#FFFFFFFF") : ColorFromHex("#FF20242A"));
                break;
            default:
                OrbCore.Background = isDark
                    ? CreateOrbBrush(Color.FromArgb(220, 22, 26, 31), Color.FromArgb(204, 15, 19, 23), Color.FromArgb(188, 9, 13, 17))
                    : CreateOrbBrush(Color.FromArgb(236, 255, 255, 255), Color.FromArgb(230, 251, 252, 254), Color.FromArgb(222, 244, 246, 249));
                StateText.Foreground = new SolidColorBrush(isDark ? ColorFromHex("#FFF3F5F8") : ColorFromHex("#FF3C4250"));
                break;
        }

        OrbInnerSurface.Fill = isDark
            ? CreateOrbBrush(Color.FromArgb(246, 20, 29, 40), Color.FromArgb(238, 14, 21, 31), Color.FromArgb(230, 9, 14, 22))
            : CreateOrbBrush(Color.FromArgb(252, 255, 255, 255), Color.FromArgb(246, 252, 253, 254), Color.FromArgb(238, 245, 247, 250));
        OrbInnerSurface.Stroke = new SolidColorBrush(isDark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0));
        StateBadge.Background = new SolidColorBrush(isDark ? Color.FromArgb(88, 20, 27, 35) : Color.FromArgb(232, 255, 255, 255));
        StateBadge.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(45, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0));

        ApplyThemeChrome();
    }

    private void StartOrbitRotation(double secondsPerRotation)
    {
        _rotationStoryboard ??= new Storyboard();
        _rotationStoryboard.Stop();
        _rotationStoryboard.Children.Clear();

        var angleAnimation = new DoubleAnimation
        {
            From = OrbitRotateTransform.Angle,
            To = OrbitRotateTransform.Angle + 360,
            Duration = TimeSpan.FromSeconds(secondsPerRotation),
            RepeatBehavior = RepeatBehavior.Forever
        };

        Storyboard.SetTarget(angleAnimation, OrbitRotateTransform);
        Storyboard.SetTargetProperty(angleAnimation, new PropertyPath(RotateTransform.AngleProperty));
        _rotationStoryboard.Children.Add(angleAnimation);
        _rotationStoryboard.Begin();
    }

    private void PlayCompletionBurst()
    {
        _completionStoryboard ??= new Storyboard();
        _completionStoryboard.Stop();
        _completionStoryboard.Children.Clear();

        for (var i = 0; i < _magicRings.Length; i++)
        {
            var ring = _magicRings[i];
            ring.Opacity = 0;
            ring.Stroke = new SolidColorBrush(i % 2 == 0 ? ColorFromHex("#FFF5F7FA") : ColorFromHex("#FFB6BEC6"));

            if (ring.RenderTransform is not ScaleTransform scaleTransform)
            {
                scaleTransform = new ScaleTransform(0.72, 0.72);
                ring.RenderTransform = scaleTransform;
            }

            scaleTransform.ScaleX = 0.72;
            scaleTransform.ScaleY = 0.72;

            var beginTime = TimeSpan.FromMilliseconds(i * 80);
            var opacityAnimation = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = beginTime
            };
            opacityAnimation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            opacityAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160))));
            opacityAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(820))));

            var scaleAnimation = new DoubleAnimation
            {
                BeginTime = beginTime,
                From = 0.72,
                To = 1.24 + (i * 0.05),
                Duration = TimeSpan.FromMilliseconds(860),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            Storyboard.SetTarget(opacityAnimation, ring);
            Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath(UIElement.OpacityProperty));
            Storyboard.SetTarget(scaleAnimation, scaleTransform);
            Storyboard.SetTargetProperty(scaleAnimation, new PropertyPath(ScaleTransform.ScaleXProperty));

            var scaleYAnimation = scaleAnimation.Clone();
            Storyboard.SetTarget(scaleYAnimation, scaleTransform);
            Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath(ScaleTransform.ScaleYProperty));

            _completionStoryboard.Children.Add(opacityAnimation);
            _completionStoryboard.Children.Add(scaleAnimation);
            _completionStoryboard.Children.Add(scaleYAnimation);
        }

        _completionStoryboard.Begin();
    }

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static Brush CreateOrbBrush(Color inner, Color mid, Color outer)
    {
        return new RadialGradientBrush
        {
            GradientOrigin = new Point(0.3, 0.25),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.7,
            RadiusY = 0.7,
            GradientStops =
            {
                new GradientStop(inner, 0),
                new GradientStop(mid, 0.58),
                new GradientStop(outer, 1)
            }
        };
    }

    private static Brush CreateGlowBrush(Color center, Color mid, Color outer)
    {
        return new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops =
            {
                new GradientStop(ScaleAlpha(center, 0.96), 0),
                new GradientStop(ScaleAlpha(mid, 0.78), 0.34),
                new GradientStop(ScaleAlpha(mid, 0.36), 0.62),
                new GradientStop(ScaleAlpha(outer, 0.46), 0.88),
                new GradientStop(ScaleAlpha(outer, 0), 1)
            }
        };
    }

    private static Color ScaleAlpha(Color color, double factor)
    {
        var alpha = (byte)Math.Clamp((int)Math.Round(color.A * factor), 0, 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static Brush CreateChipBrush(Color left, Color center, Color right)
    {
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new(left, 0),
                new(center, 0.5),
                new(right, 1)
            },
            new Point(0, 0),
            new Point(1, 1));
    }

    private static Color ColorFromHex(string value)
    {
        return (Color)ColorConverter.ConvertFromString(value);
    }

    private void CompanionThemeServiceOnThemeChanged(object? sender, CompanionThemeMode themeMode)
    {
        _themeMode = themeMode;
        ApplyPalette(_currentState);
        if (_isMenuMode && _menuOptions.Count > 0)
        {
            UpdateActionRing(_menuOptions, _selectedMenuIndex);
        }
    }

    private void ApplyThemeChrome()
    {
        var isDark = _themeMode == CompanionThemeMode.Dark;

        if (VoiceGlowHalo.Stroke is SolidColorBrush voiceStroke)
        {
            voiceStroke.Color = Colors.Transparent;
        }

        ModeText.Foreground = new SolidColorBrush(isDark ? ColorFromHex("#FFF5F7FA") : ColorFromHex("#FF343A46"));
        BrandText.Foreground = new SolidColorBrush(isDark ? ColorFromHex("#FFB7BFC8") : ColorFromHex("#FF6B727A"));
        IdleRunButton.Background = new SolidColorBrush(isDark ? Color.FromArgb(108, 24, 29, 35) : Color.FromArgb(206, 255, 255, 255));
        IdleRunButton.BorderBrush = new SolidColorBrush(isDark ? ColorFromHex("#2EFFFFFF") : ColorFromHex("#18000000"));
        IdleRunButton.Foreground = new SolidColorBrush(isDark ? Colors.White : ColorFromHex("#FF22272D"));
        UiPresentation.SetFlatText(StatusText, isDark ? Colors.White : ColorFromHex("#FF2A3037"));

        if (OrbCore.Effect is DropShadowEffect orbShadow)
        {
            orbShadow.Color = isDark ? ColorFromHex("#CC08131F") : ColorFromHex("#33000000");
            orbShadow.Opacity = isDark ? 0.18 : 0.05;
        }

        var navForeground = new SolidColorBrush(isDark ? ColorFromHex("#FFD8DDE3") : ColorFromHex("#FF626973"));
        foreach (var button in new[] { ModePrevButton, ModeNextButton, CommandPrevButton, CommandNextButton })
        {
            button.Foreground = navForeground;
        }
    }
}
