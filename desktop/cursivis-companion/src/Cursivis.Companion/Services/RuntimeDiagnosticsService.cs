using Cursivis.Companion.Models;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace Cursivis.Companion.Services;

public sealed class RuntimeDiagnosticsService
{
    private readonly RuntimeLaunchProfileService _profileService = new();

    public async Task<RuntimeDiagnosticsResult> DiagnoseAndRepairAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _profileService.TryLoadAsync();
        if (profile is null)
        {
            return RuntimeDiagnosticsResult.NeedsRepair(
                "Cursivis setup is incomplete. Download and run the latest Companion Setup from mxcursivis.vercel.app.");
        }

        var missing = FindMissingRuntimeFiles(profile);
        if (missing.Count > 0)
        {
            return RuntimeDiagnosticsResult.NeedsRepair(
                "The installed Cursivis runtime is incomplete. Run the latest Companion Setup to repair it.",
                missing);
        }

        var repaired = new List<string>();
        var startupRegistration = new StartupRegistrationService();
        await startupRegistration.EnsureRegisteredAsync();
        repaired.Add("startup registration checked");

        var hotkeyHost = new HotkeyHostService();
        if (!IsProcessRunning(profile.HotkeyHostExecutable))
        {
            await hotkeyHost.EnsureRunningAsync();
            repaired.Add("Hotkey Host restarted");
        }

        var bootstrapper = new RuntimeBootstrapper();
        var backendWasHealthy = await IsHttpHealthyAsync(profile.BackendUrl, cancellationToken);
        var browserWasHealthy = await IsHttpHealthyAsync(profile.BrowserAgentUrl, cancellationToken);
        await bootstrapper.EnsureRuntimeReadyAsync(cancellationToken);
        if (!backendWasHealthy)
        {
            repaired.Add("backend restart requested");
        }

        if (!browserWasHealthy)
        {
            repaired.Add("browser service restart requested");
        }

        var healthy =
            IsProcessRunning(profile.CompanionExecutable) &&
            IsProcessRunning(profile.HotkeyHostExecutable) &&
            await IsHttpHealthyAsync(profile.BackendUrl, cancellationToken) &&
            await IsTcpPortOpenAsync(48711, cancellationToken) &&
            await IsTcpPortOpenAsync(48712, cancellationToken);

        return healthy
            ? RuntimeDiagnosticsResult.Healthy(
                repaired.Count > 1
                    ? "Cursivis is healthy. Diagnostics repaired the components that needed attention."
                    : "Cursivis is healthy. Companion, Hotkey Host, backend, and Logitech channels are ready.",
                repaired)
            : RuntimeDiagnosticsResult.NeedsRepair(
                "Cursivis could not restore every local service. Run the latest Companion Setup to repair the runtime.",
                repaired);
    }

    private static List<string> FindMissingRuntimeFiles(RuntimeLaunchProfile profile)
    {
        var required = new[]
        {
            ("Companion", profile.CompanionExecutable),
            ("Hotkey Host", profile.HotkeyHostExecutable),
            ("Portable Node", FindNodeExe(profile.CompanionExecutable)),
            ("Backend", Path.Combine(profile.BackendDir, "src", "server.js")),
            ("Backend dependencies", Path.Combine(profile.BackendDir, "node_modules")),
            ("Browser service", Path.Combine(profile.BrowserAgentDir, "src", "server.js")),
            ("Browser service dependencies", Path.Combine(profile.BrowserAgentDir, "node_modules"))
        };

        return required
            .Where(item => string.IsNullOrWhiteSpace(item.Item2) ||
                           (item.Item1.EndsWith("dependencies", StringComparison.Ordinal) && !Directory.Exists(item.Item2)) ||
                           (!item.Item1.EndsWith("dependencies", StringComparison.Ordinal) && !File.Exists(item.Item2)))
            .Select(item => item.Item1)
            .ToList();
    }

    private static string FindNodeExe(string companionExecutable)
    {
        var companionDirectory = Path.GetDirectoryName(companionExecutable) ?? string.Empty;
        return Path.GetFullPath(Path.Combine(companionDirectory, "..", "..", "node", "node.exe"));
    }

    private static bool IsProcessRunning(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        var processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // A protected process is not a Cursivis process we can repair.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static async Task<bool> IsHttpHealthyAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"{baseUrl.TrimEnd('/')}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsTcpPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record RuntimeDiagnosticsResult(bool IsHealthy, string Summary, IReadOnlyList<string> Details)
{
    public static RuntimeDiagnosticsResult Healthy(string summary, IReadOnlyList<string> details) => new(true, summary, details);

    public static RuntimeDiagnosticsResult NeedsRepair(string summary, IReadOnlyList<string>? details = null) =>
        new(false, summary, details ?? []);
}
