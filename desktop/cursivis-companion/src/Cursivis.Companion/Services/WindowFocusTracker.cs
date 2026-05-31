using Cursivis.Companion.Infrastructure;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Cursivis.Companion.Services;

public sealed class WindowFocusTracker : IDisposable
{
    private readonly HashSet<IntPtr> _companionHandles = [];
    private readonly int _currentProcessId = Environment.ProcessId;
    private readonly DispatcherTimer _timer;
    private IntPtr _lastObservedHandle = IntPtr.Zero;

    public WindowFocusTracker()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _timer.Tick += OnTick;
    }

    public IntPtr LastExternalWindowHandle { get; private set; } = IntPtr.Zero;

    public string? LastExternalProcessName { get; private set; }

    public event EventHandler<IntPtr>? ExternalWindowActivated;

    public void RegisterCompanionWindow(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                _companionHandles.Add(handle);
            }
        };

        window.Closed += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            _companionHandles.Remove(handle);
        };
    }

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
        var handle = NativeMethods.GetActiveWindowHandle();
        if (handle == IntPtr.Zero || handle == _lastObservedHandle)
        {
            return;
        }

        _lastObservedHandle = handle;

        if (_companionHandles.Contains(handle))
        {
            return;
        }

        var processId = NativeMethods.GetProcessIdForWindow(handle);
        if (processId == _currentProcessId)
        {
            return;
        }

        LastExternalWindowHandle = handle;
        LastExternalProcessName = NativeMethods.GetProcessNameForWindow(handle);
        ExternalWindowActivated?.Invoke(this, handle);
    }
}
