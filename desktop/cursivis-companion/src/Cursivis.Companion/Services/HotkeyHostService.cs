using Cursivis.Companion.Models;
using System.Diagnostics;
using System.IO;

namespace Cursivis.Companion.Services;

public sealed class HotkeyHostService
{
    private readonly RuntimeLaunchProfileService _runtimeLaunchProfileService = new();

    public async Task EnsureRunningAsync()
    {
        var executablePath = await TryResolveExecutablePathAsync();
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return;
        }

        var existing = Process.GetProcessesByName("Cursivis.HotkeyHost")
            .FirstOrDefault(process =>
            {
                try
                {
                    return string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });

        if (existing is not null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public async Task RestartAsync()
    {
        var executablePath = await TryResolveExecutablePathAsync();
        var existingProcesses = Process.GetProcessesByName("Cursivis.HotkeyHost");
        foreach (var process in existingProcesses)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(executablePath) &&
                    !string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch
            {
                // Keep reconnect usable if one stale process cannot be inspected.
            }
        }

        await EnsureRunningAsync();
    }

    public async Task<string?> TryResolveExecutablePathAsync()
    {
        var profile = await _runtimeLaunchProfileService.TryLoadAsync();
        if (!string.IsNullOrWhiteSpace(profile?.HotkeyHostExecutable) &&
            File.Exists(profile.HotkeyHostExecutable))
        {
            return profile.HotkeyHostExecutable;
        }

        if (!string.IsNullOrWhiteSpace(profile?.CompanionProject) &&
            File.Exists(profile.CompanionProject))
        {
            var companionProjectDir = Path.GetDirectoryName(profile.CompanionProject);
            if (!string.IsNullOrWhiteSpace(companionProjectDir))
            {
                var candidate = Path.GetFullPath(Path.Combine(
                    companionProjectDir,
                    "..", "..", "..",
                    "cursivis-hotkey-host", "src", "Cursivis.HotkeyHost", "bin", "Debug", "net8.0-windows", "Cursivis.HotkeyHost.exe"));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var companionExecutable = profile?.CompanionExecutable;
        if (!string.IsNullOrWhiteSpace(companionExecutable))
        {
            var companionDir = Path.GetDirectoryName(companionExecutable);
            if (!string.IsNullOrWhiteSpace(companionDir))
            {
                var candidate = Path.GetFullPath(Path.Combine(
                    companionDir,
                    "..", "..", "..", "..", "..", "..",
                    "cursivis-hotkey-host", "src", "Cursivis.HotkeyHost", "bin", "Debug", "net8.0-windows", "Cursivis.HotkeyHost.exe"));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var currentBaseDir = AppContext.BaseDirectory;
        var fallbackCandidate = Path.GetFullPath(Path.Combine(
            currentBaseDir,
            "..", "..", "..", "..", "..", "..",
            "cursivis-hotkey-host", "src", "Cursivis.HotkeyHost", "bin", "Debug", "net8.0-windows", "Cursivis.HotkeyHost.exe"));
        return File.Exists(fallbackCandidate) ? fallbackCandidate : null;
    }
}
