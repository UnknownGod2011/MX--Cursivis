using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cursivis.Companion.Services;

public sealed class GlobalMouseWheelService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const int VerticalWheelDelta = 120;
    private const int HorizontalWheelDelta = 240;
    private readonly LowLevelMouseProc _hookProc;
    private IntPtr _hookHandle;
    private int _verticalResidualDelta;
    private int _horizontalResidualDelta;

    public GlobalMouseWheelService()
    {
        _hookProc = HookCallback;
    }

    public event EventHandler<GlobalMouseWheelEventArgs>? WheelMoved;

    public event EventHandler<GlobalMouseButtonEventArgs>? MouseButtonPressed;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
        _hookHandle = SetWindowsHookEx(WhMouseLl, _hookProc, moduleHandle, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to install the global mouse wheel hook.");
        }
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var payload = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            if (wParam == (IntPtr)WmMouseWheel || wParam == (IntPtr)WmMouseHWheel)
            {
                var rawDelta = (short)((payload.mouseData >> 16) & 0xffff);
                if (rawDelta != 0)
                {
                    var axis = wParam == (IntPtr)WmMouseHWheel ? MouseWheelAxis.Horizontal : MouseWheelAxis.Vertical;
                    var stepDelta = ConsumeWheelSteps(rawDelta, axis);
                    if (stepDelta == 0)
                    {
                        return CallNextHookEx(_hookHandle, code, wParam, lParam);
                    }

                    var args = new GlobalMouseWheelEventArgs(
                        stepDelta,
                        axis);
                    WheelMoved?.Invoke(this, args);
                    if (args.Handled)
                    {
                        return new IntPtr(1);
                    }
                }
            }
            else if (wParam == (IntPtr)WmLButtonDown ||
                     wParam == (IntPtr)WmRButtonDown ||
                     wParam == (IntPtr)WmMButtonDown ||
                     wParam == (IntPtr)WmXButtonDown)
            {
                MouseButtonPressed?.Invoke(this, new GlobalMouseButtonEventArgs(
                    new System.Windows.Point(payload.pt.x, payload.pt.y)));
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private int ConsumeWheelSteps(int rawDelta, MouseWheelAxis axis)
    {
        ref var residual = ref axis == MouseWheelAxis.Horizontal
            ? ref _horizontalResidualDelta
            : ref _verticalResidualDelta;
        var threshold = axis == MouseWheelAxis.Horizontal ? HorizontalWheelDelta : VerticalWheelDelta;

        if (residual != 0 && Math.Sign(residual) != Math.Sign(rawDelta))
        {
            residual = 0;
        }

        residual += rawDelta;
        var steps = residual / threshold;
        if (steps != 0)
        {
            residual %= threshold;
        }

        return steps;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public PointStruct pt;
        public int mouseData;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}

public enum MouseWheelAxis
{
    Vertical = 0,
    Horizontal = 1
}

public sealed class GlobalMouseWheelEventArgs(int deltaStep, MouseWheelAxis axis) : EventArgs
{
    public int DeltaStep { get; } = deltaStep;

    public MouseWheelAxis Axis { get; } = axis;

    public bool Handled { get; set; }
}

public sealed class GlobalMouseButtonEventArgs(System.Windows.Point screenPoint) : EventArgs
{
    public System.Windows.Point ScreenPoint { get; } = screenPoint;
}
