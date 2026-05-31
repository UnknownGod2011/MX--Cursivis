using System.Diagnostics;
using System.IO;

namespace Cursivis.Companion.Services;

public sealed class LogitechRuntimeStatusService
{
    private readonly string _pluginApiPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Logi",
        "LogiPluginService",
        "PluginApi.dll");

    private readonly string _pluginInstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Logi",
        "LogiPluginService",
        "Plugins",
        "Cursivis");

    private readonly string _pluginLinkPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Logi",
        "LogiPluginService",
        "Plugins",
        "CursivisPlugin.link");

    private readonly string _pluginLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Logi",
        "LogiPluginService",
        "Logs",
        "plugin_logs",
        "Cursivis.log");

    public LogitechRuntimeSnapshot GetSnapshot()
    {
        var processNames = SafeGetProcessNames();
        var optionsInstalled = File.Exists(_pluginApiPath);
        var optionsRunning =
            processNames.Contains("logioptionsplus_agent", StringComparer.OrdinalIgnoreCase) ||
            processNames.Contains("logioptionsplus_appbroker", StringComparer.OrdinalIgnoreCase);
        var pluginServiceRunning =
            processNames.Contains("LogiPluginService", StringComparer.OrdinalIgnoreCase) ||
            processNames.Contains("LogiPluginServiceExt", StringComparer.OrdinalIgnoreCase);

        var pluginInstalled = Directory.Exists(_pluginInstallDirectory);
        var debugLinkPresent = File.Exists(_pluginLinkPath);

        var pluginLoaded = false;
        var hapticConnected = false;
        var loadSource = string.Empty;

        if (File.Exists(_pluginLogPath))
        {
            var tail = ReadTailLines(_pluginLogPath, 200);
            var loadLine = tail.LastOrDefault(line => line.Contains("Plugin 'Cursivis' version", StringComparison.OrdinalIgnoreCase));
            pluginLoaded = !string.IsNullOrWhiteSpace(loadLine);
            hapticConnected = tail.Any(line => line.Contains("Connected to companion haptic channel.", StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(loadLine))
            {
                var marker = "loaded from '";
                var start = loadLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    start += marker.Length;
                    var end = loadLine.IndexOf('\'', start);
                    if (end > start)
                    {
                        loadSource = loadLine[start..end];
                    }
                }
            }
        }

        var runtimeMode = pluginInstalled
            ? "Installed package"
            : debugLinkPresent
                ? "Debug link"
                : "Not installed";

        if (!string.IsNullOrWhiteSpace(loadSource) && loadSource.Contains(@"\Plugins\Cursivis", StringComparison.OrdinalIgnoreCase))
        {
            runtimeMode = "Installed package";
        }
        else if (!string.IsNullOrWhiteSpace(loadSource) && loadSource.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
        {
            runtimeMode = "Debug link";
        }

        return new LogitechRuntimeSnapshot(
            OptionsInstalled: optionsInstalled,
            OptionsRunning: optionsRunning,
            PluginServiceRunning: pluginServiceRunning,
            PluginInstalled: pluginInstalled,
            DebugLinkPresent: debugLinkPresent,
            PluginLoaded: pluginLoaded,
            HapticConnected: hapticConnected,
            RuntimeMode: runtimeMode,
            LoadSource: loadSource);
    }

    private static HashSet<string> SafeGetProcessNames()
    {
        try
        {
            return Process.GetProcesses()
                .Select(process => process.ProcessName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ReadTailLines(string path, int maxLines)
    {
        var queue = new Queue<string>(maxLines);
        foreach (var line in File.ReadLines(path))
        {
            if (queue.Count == maxLines)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line);
        }

        return [.. queue];
    }
}

public readonly record struct LogitechRuntimeSnapshot(
    bool OptionsInstalled,
    bool OptionsRunning,
    bool PluginServiceRunning,
    bool PluginInstalled,
    bool DebugLinkPresent,
    bool PluginLoaded,
    bool HapticConnected,
    string RuntimeMode,
    string LoadSource);
