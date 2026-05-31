using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cursivis.Companion.Infrastructure;

public static class NativeMethods
{
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const int KeyeventfKeyup = 0x0002;
    private const int MouseeventfLeftdown = 0x0002;
    private const int MouseeventfLeftup = 0x0004;
    private const int MouseeventfWheel = 0x0800;
    private const int SwRestore = 9;
    private const byte VkMenu = 0x12;
    private const byte VkShift = 0x10;
    private const byte VkControl = 0x11;
    private const byte VkA = 0x41;
    private const byte VkC = 0x43;
    private const byte VkEnter = 0x0D;
    private const byte VkL = 0x4C;
    private const byte VkTab = 0x09;
    private const byte VkT = 0x54;
    private const byte VkV = 0x56;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const byte VkZ = 0x5A;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static System.Windows.Point GetCursorPosition()
    {
        if (!GetCursorPos(out var point))
        {
            return new System.Windows.Point();
        }

        return new System.Windows.Point(point.X, point.Y);
    }

    public static IntPtr GetActiveWindowHandle()
    {
        return GetForegroundWindow();
    }

    public static uint GetCurrentClipboardSequenceNumber()
    {
        return GetClipboardSequenceNumber();
    }

    public static System.Windows.Int32Rect GetVirtualScreenBounds()
    {
        var left = GetSystemMetrics(SmXvirtualscreen);
        var top = GetSystemMetrics(SmYvirtualscreen);
        var width = GetSystemMetrics(SmCxvirtualscreen);
        var height = GetSystemMetrics(SmCyvirtualscreen);

        return width <= 0 || height <= 0
            ? default
            : new System.Windows.Int32Rect(left, top, width, height);
    }

    public static void BringToFront(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(handle))
        {
            ShowWindow(handle, SwRestore);
        }

        SetForegroundWindow(handle);
    }

    public static string? GetProcessNameForWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(handle, out var pid);
        if (pid == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public static int GetProcessIdForWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(handle, out var pid);
        return (int)pid;
    }

    public static void SendCtrlC()
    {
        SendCtrlCombination(VkC);
    }

    public static void SendCtrlA()
    {
        SendCtrlCombination(VkA);
    }

    public static void SendCtrlV()
    {
        SendCtrlCombination(VkV);
    }

    public static void SendCtrlL()
    {
        SendCtrlCombination(VkL);
    }

    public static void SendCtrlT()
    {
        SendCtrlCombination(VkT);
    }

    public static void SendCtrlTab()
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        SendKey(VkTab);
        keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    public static void SendCtrlZ()
    {
        SendCtrlCombination(VkZ);
    }

    public static void SendEnter()
    {
        SendKey(VkEnter);
    }

    public static void SendTab(bool withShift = false)
    {
        if (withShift)
        {
            keybd_event(VkShift, 0, 0, UIntPtr.Zero);
        }

        SendKey(VkTab);

        if (withShift)
        {
            keybd_event(VkShift, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
    }

    public static void SendKeyChord(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
        {
            return;
        }

        var normalized = chord.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "enter":
                SendEnter();
                return;
            case "tab":
                SendTab();
                return;
            case "shift+tab":
                SendTab(withShift: true);
                return;
            case "control+z":
            case "ctrl+z":
                SendCtrlZ();
                return;
            case "control+v":
            case "ctrl+v":
                SendCtrlV();
                return;
            case "control+c":
            case "ctrl+c":
                SendCtrlC();
                return;
            case "control+l":
            case "ctrl+l":
                SendCtrlL();
                return;
            case "control+t":
            case "ctrl+t":
                SendCtrlT();
                return;
            case "control+tab":
            case "ctrl+tab":
                SendCtrlTab();
                return;
            case "control+a":
            case "ctrl+a":
                SendCtrlA();
                return;
        }

        if (normalized.Length == 1)
        {
            var character = char.ToUpperInvariant(normalized[0]);
            SendKey((byte)character);
        }
    }

    public static void LeftClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(MouseeventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseeventfLeftup, 0, 0, 0, UIntPtr.Zero);
    }

    public static void Scroll(string direction)
    {
        var normalized = direction?.Trim().ToLowerInvariant() ?? "down";
        var delta = normalized.Contains("up", StringComparison.Ordinal)
            ? unchecked((uint)-120)
            : 120u;

        if (normalized.Contains("top", StringComparison.Ordinal))
        {
            for (var index = 0; index < 8; index += 1)
            {
                mouse_event(MouseeventfWheel, 0, 0, unchecked((uint)-120), UIntPtr.Zero);
            }

            return;
        }

        if (normalized.Contains("bottom", StringComparison.Ordinal))
        {
            for (var index = 0; index < 8; index += 1)
            {
                mouse_event(MouseeventfWheel, 0, 0, 120, UIntPtr.Zero);
            }

            return;
        }

        mouse_event(MouseeventfWheel, 0, 0, delta, UIntPtr.Zero);
    }

    public static bool RegisterGlobalHotKey(IntPtr handle, int id, uint modifiers, uint key)
    {
        return RegisterHotKey(handle, id, modifiers, key);
    }

    public static void UnregisterGlobalHotKey(IntPtr handle, int id)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotKey(handle, id);
    }

    private static void SendCtrlCombination(byte key)
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyeventfKeyup, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    private static void SendKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    public static void SendVolumeStep(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        SendKey(delta > 0 ? VkVolumeUp : VkVolumeDown);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
