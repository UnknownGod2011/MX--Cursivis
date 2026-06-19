using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Cursivis.Companion.LiveMode;

public sealed class LiveModeOverlayWindow : Window, IDisposable
{
    private const double ReferenceWidth = 460;
    private const double ReferenceHeight = 82;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private static readonly Brush IdleBrush = CreateFrozenBrush(90, 90, 90);
    private static readonly Brush ListeningBrush = CreateFrozenBrush(239, 68, 68);
    private static readonly Brush ThinkingBrush = CreateFrozenBrush(168, 85, 247);
    private static readonly Brush ExecutingBrush = CreateFrozenBrush(245, 158, 11);
    private static readonly Brush SpeakingBrush = CreateFrozenBrush(20, 184, 166);
    private static readonly Brush DoneBrush = CreateFrozenBrush(34, 197, 94);
    private static readonly Brush ErrorBrush = CreateFrozenBrush(220, 38, 38);

    private readonly DispatcherTimer _timer;
    private readonly TextBlock _statusText;
    private readonly TextBlock _detailText;
    private readonly Ellipse _outerOrb;
    private readonly Ellipse _innerOrb;
    private LiveModeVoicePhase _lastPhase = LiveModeVoicePhase.Idle;
    private double _lastLevelDb = -96;
    private bool _disposed;

    public LiveModeOverlayWindow()
    {
        Width = ReferenceWidth;
        Height = ReferenceHeight;
        MinWidth = ReferenceWidth;
        MaxWidth = ReferenceWidth;
        MinHeight = ReferenceHeight;
        MaxHeight = ReferenceHeight;
        SizeToContent = SizeToContent.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = false;

        var root = new Border
        {
            Width = ReferenceWidth,
            Height = ReferenceHeight,
            Background = new SolidColorBrush(Color.FromArgb(245, 18, 18, 18)),
            CornerRadius = new CornerRadius(12),
        };

        var layout = new Canvas();
        var orbCanvas = new Canvas
        {
            Width = 42,
            Height = 42,
        };
        Canvas.SetLeft(orbCanvas, 16);
        Canvas.SetTop(orbCanvas, 19);

        _outerOrb = new Ellipse
        {
            Width = 38,
            Height = 38,
            Fill = PhaseBrush(LiveModeVoicePhase.Idle),
        };
        Canvas.SetLeft(_outerOrb, 2);
        Canvas.SetTop(_outerOrb, 2);

        _innerOrb = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Brushes.White,
            Visibility = Visibility.Collapsed,
        };
        PositionInnerOrb(12);
        orbCanvas.Children.Add(_outerOrb);
        orbCanvas.Children.Add(_innerOrb);

        _statusText = new TextBlock
        {
            Text = "Cursivis Live Mode",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14.6666667,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 382,
            Height = 26,
        };
        Canvas.SetLeft(_statusText, 74);
        Canvas.SetTop(_statusText, 13);

        _detailText = new TextBlock
        {
            Text = "Press the Live Mode action to begin",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(205, 205, 205)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 382,
            Height = 24,
        };
        Canvas.SetLeft(_detailText, 74);
        Canvas.SetTop(_detailText, 40);

        layout.Children.Add(orbCanvas);
        layout.Children.Add(_statusText);
        layout.Children.Add(_detailText);
        root.Child = layout;
        Content = new Viewbox
        {
            Stretch = Stretch.Fill,
            Child = root,
        };

        SourceInitialized += OnSourceInitialized;
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _timer.Tick += TimerOnTick;
        _timer.Start();
    }

    public void RefreshNow()
    {
        if (_disposed)
        {
            return;
        }

        RefreshState();
    }

    private void TimerOnTick(object? sender, EventArgs e) => RefreshState();

    private void RefreshState()
    {
        var phase = LiveModeState.Phase;
        if (phase == LiveModeVoicePhase.Idle)
        {
            if (IsVisible)
            {
                Hide();
            }

            _lastPhase = phase;
            return;
        }

        var terminal = phase is LiveModeVoicePhase.Done or LiveModeVoicePhase.Error;
        if (terminal && DateTime.UtcNow - LiveModeState.PhaseStartedUtc > TimeSpan.FromSeconds(3.2))
        {
            if (IsVisible)
            {
                Hide();
                LiveModeLog.Info($"boundary=overlay.hidden phase={phase}");
            }

            _lastPhase = phase;
            return;
        }

        if (!IsVisible)
        {
            PositionOverlay();
            Show();
            LiveModeLog.Info($"boundary=overlay.visible phase={phase}");
        }

        _statusText.Text = LiveModeState.Title;
        _detailText.Text = BuildDetail(phase, LiveModeState.Detail);
        _outerOrb.Fill = PhaseBrush(phase);

        if (phase == LiveModeVoicePhase.Listening)
        {
            _innerOrb.Visibility = Visibility.Visible;
            if (phase != _lastPhase || Math.Abs(LiveModeState.InputLevelDb - _lastLevelDb) > 2)
            {
                var normalized = Math.Clamp((LiveModeState.InputLevelDb + 60) / 60, 0, 1);
                PositionInnerOrb(12 + (int)(normalized * 17));
            }
        }
        else
        {
            _innerOrb.Visibility = Visibility.Collapsed;
        }

        if (phase != _lastPhase)
        {
            LiveModeLog.Info($"boundary=overlay.phase phase={phase}");
        }

        _lastLevelDb = LiveModeState.InputLevelDb;
        _lastPhase = phase;
    }

    private void PositionInnerOrb(int diameter)
    {
        _innerOrb.Width = diameter;
        _innerOrb.Height = diameter;
        Canvas.SetLeft(_innerOrb, (42 - diameter) / 2d);
        Canvas.SetTop(_innerOrb, (42 - diameter) / 2d);
    }

    private void PositionOverlay()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 18;
    }

    private static string BuildDetail(LiveModeVoicePhase phase, string detail)
    {
        var elapsed = Math.Max(0, (int)(DateTime.UtcNow - LiveModeState.PhaseStartedUtc).TotalSeconds);
        return phase switch
        {
            LiveModeVoicePhase.Listening => Shorten(detail, 88),
            LiveModeVoicePhase.Thinking or LiveModeVoicePhase.Executing =>
                Shorten($"{detail}  {elapsed}s - {LiveModeState.CancelHotkey} cancels", 88),
            _ => Shorten(detail, 88),
        };
    }

    private static string Shorten(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "Ready for Cursivis Live Mode";
        }

        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..(maxLength - 3)] + "...";
    }

    private static Brush PhaseBrush(LiveModeVoicePhase phase) => phase switch
    {
        LiveModeVoicePhase.Listening => ListeningBrush,
        LiveModeVoicePhase.Thinking => ThinkingBrush,
        LiveModeVoicePhase.Executing => ExecutingBrush,
        LiveModeVoicePhase.Speaking => SpeakingBrush,
        LiveModeVoicePhase.Done => DoneBrush,
        LiveModeVoicePhase.Error => ErrorBrush,
        _ => IdleBrush,
    };

    private static Brush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle | WsExToolWindow | WsExNoActivate));

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = ReferenceWidth / Math.Max(1, dpi.DpiScaleX);
        var height = ReferenceHeight / Math.Max(1, dpi.DpiScaleY);
        MinWidth = MaxWidth = Width = width;
        MinHeight = MaxHeight = Height = height;
        Dispatcher.BeginInvoke(PositionOverlay, DispatcherPriority.Loaded);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= TimerOnTick;
        SourceInitialized -= OnSourceInitialized;
        Close();
        GC.SuppressFinalize(this);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr windowHandle, int index);

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : GetWindowLong32(windowHandle, index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr windowHandle, int index, IntPtr newLong);

    private static IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, newLong)
            : SetWindowLong32(windowHandle, index, newLong);
}
