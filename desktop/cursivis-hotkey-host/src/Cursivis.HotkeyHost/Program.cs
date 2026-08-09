using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

using var mutex = new Mutex(true, @"Local\Cursivis.HotkeyHost.SingleInstance", out var createdNew);
if (!createdNew)
{
    return;
}

ApplicationConfiguration.Initialize();
Application.Run(new HotkeyHostContext());

internal sealed class HotkeyHostContext : ApplicationContext
{
    private readonly HotkeyWindow _window = new();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _window.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int TriggerHotkeyId = 0xCA11;
    private const int TakeActionHotkeyId = 0xCA12;
    private const int VoiceHotkeyId = 0xCA13;
    private const int LiveModeHotkeyId = 0xCA14;
    private const int CancelLiveModeHotkeyId = 0xCA15;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const string DefaultGoTriggerShortcut = "Ctrl+Alt+Space";
    private const string DefaultLiveModeShortcut = "Ctrl+Alt+Q";
    private const string DefaultCancelLiveModeShortcut = "Ctrl+Alt+X";
    private static readonly string RegistrationStatusPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cursivis",
        "hotkey-registration.json");
    private bool _disposed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
        var triggerShortcut = LoadGoTriggerShortcut();
        if (!TryParseShortcut(triggerShortcut, out var triggerModifiers, out var triggerKey))
        {
            triggerModifiers = ModControl | ModAlt;
            triggerKey = Keys.Space;
        }

        var goRegistered = RegisterHotKey(Handle, TriggerHotkeyId, triggerModifiers, (uint)triggerKey);
        if (!goRegistered)
        {
            goRegistered = RegisterHotKey(Handle, TriggerHotkeyId, ModControl | ModAlt, (uint)Keys.Space);
        }

        var takeActionRegistered = RegisterHotKey(Handle, TakeActionHotkeyId, ModControl | ModAlt, (uint)Keys.A);
        var voiceRegistered = RegisterHotKey(Handle, VoiceHotkeyId, ModControl | ModAlt, (uint)Keys.V);

        var liveModeSettings = LoadLiveModeHotkeys();
        bool? liveModeRegistered = null;
        bool? cancelLiveModeRegistered = null;
        if (liveModeSettings.Enabled)
        {
            liveModeRegistered = RegisterConfiguredHotkey(
                LiveModeHotkeyId,
                liveModeSettings.Hotkey,
                DefaultLiveModeShortcut);
            cancelLiveModeRegistered = RegisterConfiguredHotkey(
                CancelLiveModeHotkeyId,
                liveModeSettings.CancelHotkey,
                DefaultCancelLiveModeShortcut);
        }

        WriteRegistrationStatus(new HotkeyRegistrationStatus(
            DateTime.UtcNow,
            goRegistered,
            takeActionRegistered,
            voiceRegistered,
            liveModeRegistered,
            cancelLiveModeRegistered,
            liveModeSettings.Hotkey,
            liveModeSettings.CancelHotkey));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotKey)
        {
            var pressType = m.WParam.ToInt32() switch
            {
                TriggerHotkeyId => "tap",
                TakeActionHotkeyId => "action",
                VoiceHotkeyId => "long_press",
                LiveModeHotkeyId => "live_mode",
                CancelLiveModeHotkeyId => "live_mode_stop",
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(pressType))
            {
                _ = TriggerDispatchClient.SendAsync(pressType);
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Handle != IntPtr.Zero)
            {
                UnregisterHotKey(Handle, TriggerHotkeyId);
                UnregisterHotKey(Handle, TakeActionHotkeyId);
                UnregisterHotKey(Handle, VoiceHotkeyId);
                UnregisterHotKey(Handle, LiveModeHotkeyId);
                UnregisterHotKey(Handle, CancelLiveModeHotkeyId);
                DestroyHandle();
            }
        }
        catch
        {
            // Ignore cleanup races on shutdown.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static string LoadGoTriggerShortcut()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cursivis",
                "settings.json");

            if (!File.Exists(settingsPath))
            {
                return DefaultGoTriggerShortcut;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("goTriggerShortcut", out var camelCase) &&
                !string.IsNullOrWhiteSpace(camelCase.GetString()))
            {
                return camelCase.GetString()!;
            }

            if (document.RootElement.TryGetProperty("GoTriggerShortcut", out var pascalCase) &&
                !string.IsNullOrWhiteSpace(pascalCase.GetString()))
            {
                return pascalCase.GetString()!;
            }
        }
        catch
        {
            // Fall back to the known Logitech gesture shortcut.
        }

        return DefaultGoTriggerShortcut;
    }

    private bool RegisterConfiguredHotkey(int id, string shortcut, string fallback)
    {
        if (!TryParseShortcut(shortcut, out var modifiers, out var key) &&
            !TryParseShortcut(fallback, out modifiers, out key))
        {
            return false;
        }

        return RegisterHotKey(Handle, id, modifiers, (uint)key);
    }

    private static void WriteRegistrationStatus(HotkeyRegistrationStatus status)
    {
        try
        {
            var directory = Path.GetDirectoryName(RegistrationStatusPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = RegistrationStatusPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(status));
            File.Move(temporaryPath, RegistrationStatusPath, overwrite: true);
        }
        catch
        {
            // Hotkeys remain usable even if the optional status file cannot be written.
        }
    }

    private static LiveModeHotkeySettings LoadLiveModeHotkeys()
    {
        var settings = new LiveModeHotkeySettings();
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cursivis",
                "live-mode.json");

            if (!File.Exists(settingsPath))
            {
                return settings;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = document.RootElement;
            settings.Enabled = ReadBoolean(root, "Enabled", "enabled", fallback: true);
            settings.Hotkey = ReadString(root, "Hotkey", "hotkey", DefaultLiveModeShortcut);
            settings.CancelHotkey = ReadString(
                root,
                "CancelHotkey",
                "cancelHotkey",
                DefaultCancelLiveModeShortcut);
        }
        catch
        {
            // Keep the stable defaults if settings are unavailable.
        }

        return settings;
    }

    private static bool ReadBoolean(JsonElement root, string pascalName, string camelName, bool fallback)
    {
        if (root.TryGetProperty(pascalName, out var pascalValue) &&
            pascalValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return pascalValue.GetBoolean();
        }

        if (root.TryGetProperty(camelName, out var camelValue) &&
            camelValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return camelValue.GetBoolean();
        }

        return fallback;
    }

    private static string ReadString(
        JsonElement root,
        string pascalName,
        string camelName,
        string fallback)
    {
        if (root.TryGetProperty(pascalName, out var pascalValue) &&
            !string.IsNullOrWhiteSpace(pascalValue.GetString()))
        {
            return pascalValue.GetString()!;
        }

        if (root.TryGetProperty(camelName, out var camelValue) &&
            !string.IsNullOrWhiteSpace(camelValue.GetString()))
        {
            return camelValue.GetString()!;
        }

        return fallback;
    }

    private static bool TryParseShortcut(string value, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        var parts = (value ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        foreach (var part in parts)
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    continue;
                case "alt":
                    modifiers |= ModAlt;
                    continue;
                case "shift":
                    modifiers |= ModShift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    continue;
            }

            if (key != Keys.None || !TryParseKeyToken(part, out key))
            {
                return false;
            }
        }

        return (modifiers & (ModControl | ModAlt)) != 0 &&
               key is not Keys.None and not Keys.Escape and not Keys.Tab and not Keys.Enter and not Keys.ControlKey and not Keys.Menu and not Keys.ShiftKey and not Keys.LWin and not Keys.RWin;
    }

    private static bool TryParseKeyToken(string token, out Keys key)
    {
        key = Keys.None;
        var normalized = token.Trim();
        if (string.Equals(normalized, "Space", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Space;
            return true;
        }

        if (normalized is "`" ||
            string.Equals(normalized, "Backtick", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Grave", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "GraveAccent", StringComparison.OrdinalIgnoreCase))
        {
            key = Keys.Oemtilde;
            return true;
        }

        if (normalized.Length == 1 && char.IsLetterOrDigit(normalized[0]))
        {
            return Enum.TryParse(normalized.ToUpperInvariant(), out key);
        }

        return Enum.TryParse(normalized, true, out key) && key != Keys.None;
    }

    private sealed class LiveModeHotkeySettings
    {
        public bool Enabled { get; set; } = true;
        public string Hotkey { get; set; } = DefaultLiveModeShortcut;
        public string CancelHotkey { get; set; } = DefaultCancelLiveModeShortcut;
    }
}

internal sealed record HotkeyRegistrationStatus(
    DateTime UpdatedUtc,
    bool GoRegistered,
    bool TakeActionRegistered,
    bool VoiceRegistered,
    bool? LiveModeRegistered,
    bool? CancelLiveModeRegistered,
    string LiveModeHotkey,
    string CancelLiveModeHotkey);

internal static class TriggerDispatchClient
{
    private static readonly Uri IpcUri = new("ws://127.0.0.1:48711/cursivis-trigger/");

    public static async Task SendAsync(string pressType)
    {
        try
        {
            using var socket = await ConnectOrWakeAsync();

            var payload = new
            {
                protocolVersion = "1.0.0",
                eventType = "trigger",
                requestId = Guid.NewGuid(),
                source = "hotkey-host",
                pressType,
                dialDelta = (int?)null,
                cursor = new { x = 0, y = 0 },
                timestampUtc = DateTime.UtcNow.ToString("O")
            };

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
        }
        catch
        {
            // Keep host alive even if a single dispatch fails.
        }
    }

    private static async Task<ClientWebSocket> ConnectOrWakeAsync()
    {
        var socket = await TryConnectAsync();
        if (socket is not null)
        {
            return socket;
        }

        TryStartCompanion();

        // Allow a cold Companion start, but keep the background hotkey listener responsive.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            socket = await TryConnectAsync();
            if (socket is not null)
            {
                return socket;
            }

            await Task.Delay(700);
        }

        throw new InvalidOperationException("Cursivis Companion could not be reached.");
    }

    private static async Task<ClientWebSocket?> TryConnectAsync()
    {
        var socket = new ClientWebSocket();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(IpcUri, cts.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }

    private static void TryStartCompanion()
    {
        try
        {
            var profilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cursivis",
                "runtime-profile.json");

            if (!File.Exists(profilePath))
            {
                return;
            }

            var json = File.ReadAllText(profilePath);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryStartExecutable(root))
            {
                return;
            }

            TryStartProject(root);
        }
        catch
        {
            // Fail later if the companion cannot be reached.
        }
    }

    private static bool TryStartExecutable(JsonElement root)
    {
        if (!root.TryGetProperty("companionExecutable", out var executableElement))
        {
            return false;
        }

        var executablePath = executableElement.GetString();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--background",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        return true;
    }

    private static void TryStartProject(JsonElement root)
    {
        if (!root.TryGetProperty("companionProject", out var projectElement))
        {
            return;
        }

        var projectPath = projectElement.GetString();
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
        {
            return;
        }

        var escapedProjectPath = projectPath.Replace("'", "''");
        var command = $"dotnet run --project '{escapedProjectPath}' -- --background";
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
