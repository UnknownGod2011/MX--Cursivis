using Microsoft.Win32;
using System.IO;

namespace Cursivis.Companion.Services;

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CursivisHotkeyHost";
    private readonly HotkeyHostService _hotkeyHostService = new();

    public async Task EnsureRegisteredAsync()
    {
        var launchCommand = await BuildLaunchCommandAsync();
        if (string.IsNullOrWhiteSpace(launchCommand))
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        var currentValue = key.GetValue(ValueName) as string;
        if (string.Equals(currentValue, launchCommand, StringComparison.Ordinal))
        {
            return;
        }

        key.SetValue(ValueName, launchCommand, RegistryValueKind.String);
    }

    private async Task<string?> BuildLaunchCommandAsync()
    {
        var executablePath = await _hotkeyHostService.TryResolveExecutablePathAsync();

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        return $"\"{executablePath}\"";
    }
}
