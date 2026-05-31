using Cursivis.Companion.Infrastructure;
using System.Windows.Threading;

namespace Cursivis.Companion.Services;

public sealed class CursorTracker : IDisposable
{
    private readonly DispatcherTimer _timer;

    public CursorTracker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _timer.Tick += OnTick;
    }

    public event EventHandler<System.Windows.Point>? PositionChanged;

    public System.Windows.Point CurrentPosition { get; private set; }

    public void Start()
    {
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void Dispose()
    {
        _timer.Tick -= OnTick;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var cursor = NativeMethods.GetCursorPosition();
        CurrentPosition = cursor;
        PositionChanged?.Invoke(this, cursor);
    }
}
