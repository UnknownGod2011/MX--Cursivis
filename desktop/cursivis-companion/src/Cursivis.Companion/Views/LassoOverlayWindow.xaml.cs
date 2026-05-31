using Cursivis.Companion.Models;
using Cursivis.Companion.Infrastructure;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Cursivis.Companion.Views;

public partial class LassoOverlayWindow : Window
{
    private Point? _startPoint;
    private Rectangle _rect => SelectionRect;

    public LassoOverlayWindow()
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public event EventHandler<LassoSelectionResult>? SelectionCompleted;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(this);
        _rect.Visibility = Visibility.Visible;
        Canvas.SetLeft(_rect, _startPoint.Value.X);
        Canvas.SetTop(_rect, _startPoint.Value.Y);
        _rect.Width = 0;
        _rect.Height = 0;
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_startPoint is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        var x = Math.Min(_startPoint.Value.X, current.X);
        var y = Math.Min(_startPoint.Value.Y, current.Y);
        var width = Math.Abs(current.X - _startPoint.Value.X);
        var height = Math.Abs(current.Y - _startPoint.Value.Y);

        Canvas.SetLeft(_rect, x);
        Canvas.SetTop(_rect, y);
        _rect.Width = width;
        _rect.Height = height;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_startPoint is null)
        {
            return;
        }

        ReleaseMouseCapture();
        var end = e.GetPosition(this);
        var x = Math.Min(_startPoint.Value.X, end.X);
        var y = Math.Min(_startPoint.Value.Y, end.Y);
        var width = Math.Abs(end.X - _startPoint.Value.X);
        var height = Math.Abs(end.Y - _startPoint.Value.Y);
        _startPoint = null;

        if (width < 8 || height < 8)
        {
            Hide();
            SelectionCompleted?.Invoke(this, new LassoSelectionResult
            {
                IsCanceled = true,
                Region = default,
                CancelPoint = NativeMethods.GetCursorPosition()
            });
            Close();
            return;
        }

        var region = BuildPhysicalScreenRegion(x, y, width, height);

        Hide();
        SelectionCompleted?.Invoke(this, new LassoSelectionResult
        {
            IsCanceled = false,
            Region = region,
            CancelPoint = null
        });

        Close();
    }

    private Point BuildPhysicalScreenPoint(double x, double y)
    {
        var dipPoint = new Point(Left + x, Top + y);
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            var transform = source.CompositionTarget.TransformToDevice;
            return transform.Transform(dipPoint);
        }

        return PointToScreen(new Point(x, y));
    }

    private Int32Rect BuildPhysicalScreenRegion(double x, double y, double width, double height)
    {
        var topLeft = new Point(Left + x, Top + y);
        var bottomRight = new Point(Left + x + width, Top + y + height);
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            var transform = source.CompositionTarget.TransformToDevice;
            topLeft = transform.Transform(topLeft);
            bottomRight = transform.Transform(bottomRight);
            return CreateScreenRegion(topLeft, bottomRight);
        }

        return CreateScreenRegion(
            PointToScreen(new Point(x, y)),
            PointToScreen(new Point(x + width, y + height)));
    }

    private static Int32Rect CreateScreenRegion(Point firstPoint, Point secondPoint)
    {
        var left = (int)Math.Floor(Math.Min(firstPoint.X, secondPoint.X));
        var top = (int)Math.Floor(Math.Min(firstPoint.Y, secondPoint.Y));
        var right = (int)Math.Ceiling(Math.Max(firstPoint.X, secondPoint.X));
        var bottom = (int)Math.Ceiling(Math.Max(firstPoint.Y, secondPoint.Y));

        return new Int32Rect(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        Hide();
        SelectionCompleted?.Invoke(this, new LassoSelectionResult
        {
            IsCanceled = true,
            Region = default,
            CancelPoint = null
        });
        Close();
    }
}
