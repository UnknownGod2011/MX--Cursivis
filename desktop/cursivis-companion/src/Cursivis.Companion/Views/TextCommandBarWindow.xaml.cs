using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
namespace Cursivis.Companion.Views;

public partial class TextCommandBarWindow : Window
{
    private const double PromptGapFromOrb = 96;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private bool _isClosingInternally;
    private bool _isDragging;
    private bool _isUserPositioned;
    private Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private Window? _anchorWindow;
    private readonly DispatcherTimer _anchorFollowTimer;

    public TextCommandBarWindow(string? initialCommand = null, string? placeholder = null)
    {
        InitializeComponent();
        _anchorFollowTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _anchorFollowTimer.Tick += AnchorFollowTimer_OnTick;
        PlaceholderText.Text = string.IsNullOrWhiteSpace(placeholder)
            ? "Type a refinement or follow-up..."
            : placeholder;
        CommandTextBox.Text = string.IsNullOrWhiteSpace(initialCommand)
            ? string.Empty
            : initialCommand.Trim();

        Loaded += (_, _) =>
        {
            CommandTextBox.Focus();
            if (string.IsNullOrWhiteSpace(CommandTextBox.Text))
            {
                CommandTextBox.CaretIndex = 0;
            }
            else
            {
                CommandTextBox.SelectAll();
            }

            UpdatePlaceholderVisibility();
            if (_anchorWindow is not null)
            {
                _ = Dispatcher.BeginInvoke(() => PositionNextTo(_anchorWindow), DispatcherPriority.Loaded);
                _ = Dispatcher.BeginInvoke(() => PositionNextTo(_anchorWindow), DispatcherPriority.Background);
            }
        };
        ContentRendered += (_, _) =>
        {
            if (_anchorWindow is not null)
            {
                PositionNextTo(_anchorWindow);
            }
        };
    }

    public string? CommandText { get; private set; }

    public void AttachToAnchor(Window anchorWindow)
    {
        if (ReferenceEquals(_anchorWindow, anchorWindow))
        {
            PositionNextTo(anchorWindow);
            return;
        }

        DetachFromAnchor();
        _anchorWindow = anchorWindow;
        _anchorWindow.LocationChanged += AnchorWindow_OnPositionChanged;
        _anchorWindow.SizeChanged += AnchorWindow_OnSizeChanged;
        _anchorWindow.StateChanged += AnchorWindow_OnStateChanged;
        _anchorWindow.Closed += AnchorWindow_OnClosed;
        _anchorFollowTimer.Start();
        PositionNextTo(anchorWindow);
    }

    public void PositionNextTo(Window anchorWindow)
    {
        if (_isUserPositioned)
        {
            return;
        }

        var workArea = SystemParameters.WorkArea;
        var anchorBounds = anchorWindow is OrbOverlayWindow orbOverlayWindow
            ? orbOverlayWindow.GetPromptAnchorBounds()
            : new Rect(
                anchorWindow.Left,
                anchorWindow.Top,
                Math.Max(anchorWindow.ActualWidth, anchorWindow.Width),
                Math.Max(anchorWindow.ActualHeight, anchorWindow.Height));
        var popupWidth = Math.Max(ActualWidth, Width);
        var popupHeight = Math.Max(ActualHeight, Height);
        var targetLeft = anchorBounds.Left - popupWidth - PromptGapFromOrb;
        var targetTop = anchorBounds.Top + ((anchorBounds.Height - popupHeight) / 2);

        var finalLeft = targetLeft;
        var finalTop = Math.Max(workArea.Top + 16, Math.Min(targetTop, workArea.Bottom - popupHeight - 16));
        ApplyNativePosition(finalLeft, finalTop);
    }

    private void CommandTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdatePlaceholderVisibility();
    }

    private void SendButton_OnClick(object sender, RoutedEventArgs e)
    {
        Submit();
    }

    private void Window_OnDeactivated(object sender, EventArgs e)
    {
        if (_isClosingInternally || !IsVisible)
        {
            return;
        }

        Cancel();
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
    }

    private void DragHandle_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginDrag(e, DragHandle);
    }

    private void DragHandle_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        ContinueDrag(e);
    }

    private void DragHandle_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopDragging();
        e.Handled = true;
    }

    private void ShellBorder_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (FindParent<System.Windows.Controls.TextBox>(source) is not null ||
             FindParent<System.Windows.Controls.Button>(source) is not null))
        {
            return;
        }

        BeginDrag(e, ShellBorder);
    }

    private void ShellBorder_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        ContinueDrag(e);
    }

    private void ShellBorder_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopDragging();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopDragging();
        DetachFromAnchor();
        _anchorFollowTimer.Stop();
        base.OnClosed(e);
    }

    private void Submit()
    {
        var command = CommandTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            CommandTextBox.Focus();
            return;
        }

        CloseWithResult(command);
    }

    private void Cancel()
    {
        CloseWithResult(null);
    }

    private void CloseWithResult(string? command)
    {
        CommandText = command;
        _isClosingInternally = true;
        try
        {
            DialogResult = !string.IsNullOrWhiteSpace(command);
            return;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    private void UpdatePlaceholderVisibility()
    {
        PlaceholderText.Visibility = string.IsNullOrWhiteSpace(CommandTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void AnchorWindow_OnPositionChanged(object? sender, EventArgs e)
    {
        if (_anchorWindow is not null && !_isUserPositioned)
        {
            PositionNextTo(_anchorWindow);
        }
    }

    private void AnchorWindow_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_anchorWindow is not null && !_isUserPositioned)
        {
            PositionNextTo(_anchorWindow);
        }
    }

    private void AnchorWindow_OnStateChanged(object? sender, EventArgs e)
    {
        if (_anchorWindow is null)
        {
            return;
        }

        if (_anchorWindow.WindowState == WindowState.Minimized)
        {
            StopDragging();
            Hide();
            return;
        }

        Show();
        if (!_isUserPositioned)
        {
            PositionNextTo(_anchorWindow);
        }
    }

    private void AnchorWindow_OnClosed(object? sender, EventArgs e)
    {
        if (!_isClosingInternally && IsVisible)
        {
            Cancel();
        }
    }

    private void AnchorFollowTimer_OnTick(object? sender, EventArgs e)
    {
        if (_anchorWindow is null || _isUserPositioned || !IsVisible)
        {
            return;
        }

        if (_anchorWindow.WindowState == WindowState.Minimized)
        {
            return;
        }

        PositionNextTo(_anchorWindow);
    }

    private void DetachFromAnchor()
    {
        if (_anchorWindow is null)
        {
            return;
        }

        _anchorFollowTimer.Stop();
        _anchorWindow.LocationChanged -= AnchorWindow_OnPositionChanged;
        _anchorWindow.SizeChanged -= AnchorWindow_OnSizeChanged;
        _anchorWindow.StateChanged -= AnchorWindow_OnStateChanged;
        _anchorWindow.Closed -= AnchorWindow_OnClosed;
        _anchorWindow = null;
    }

    private void ApplyNativePosition(double left, double top)
    {
        Left = left;
        Top = top;

        if (!IsLoaded)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(left),
            (int)Math.Round(top),
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private Point GetMouseScreenDip(MouseEventArgs e)
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        return transform.Transform(PointToScreen(e.GetPosition(this)));
    }

    private void BeginDrag(MouseEventArgs e, UIElement dragSource)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _isDragging = true;
        _isUserPositioned = true;
        _dragStartCursor = GetMouseScreenDip(e);
        _dragStartLeft = Left;
        _dragStartTop = Top;
        dragSource.CaptureMouse();
        Mouse.Capture(dragSource);
        e.Handled = true;
    }

    private void ContinueDrag(MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentCursor = GetMouseScreenDip(e);
        Left = _dragStartLeft + (currentCursor.X - _dragStartCursor.X);
        Top = _dragStartTop + (currentCursor.Y - _dragStartCursor.Y);
        e.Handled = true;
    }

    private void StopDragging()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        if (Mouse.Captured is not null)
        {
            Mouse.Capture(null);
        }

        if (DragHandle.IsMouseCaptured)
        {
            DragHandle.ReleaseMouseCapture();
        }

        if (ShellBorder.IsMouseCaptured)
        {
            ShellBorder.ReleaseMouseCapture();
        }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
