#nullable enable

namespace Loupedeck.CursivisPlugin
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;

    internal enum CompanionAvailability
    {
        NotInstalled,
        InstalledNotRunning,
        Running,
    }

    internal readonly struct CompanionRuntimeSnapshot
    {
        public CompanionRuntimeSnapshot(CompanionAvailability availability, String? executablePath)
        {
            this.Availability = availability;
            this.ExecutablePath = executablePath;
        }

        public CompanionAvailability Availability { get; }

        public String? ExecutablePath { get; }

        public Boolean IsInstalled => this.Availability != CompanionAvailability.NotInstalled;
    }

    internal static class CompanionRuntimeState
    {
        internal const String DownloadUrl = "https://mxcursivis.vercel.app";

        private static readonly Object SyncRoot = new Object();
        private static CompanionRuntimeSnapshot _cachedSnapshot = new CompanionRuntimeSnapshot(CompanionAvailability.NotInstalled, null);
        private static DateTime _cachedAtUtc = DateTime.MinValue;

        public static CompanionRuntimeSnapshot GetSnapshot(Boolean refresh = false)
        {
            lock (SyncRoot)
            {
                if (!refresh && DateTime.UtcNow - _cachedAtUtc < TimeSpan.FromSeconds(2))
                {
                    return _cachedSnapshot;
                }

                _cachedSnapshot = ReadSnapshot();
                _cachedAtUtc = DateTime.UtcNow;
                return _cachedSnapshot;
            }
        }

        public static Boolean TryStartCompanion()
        {
            var snapshot = GetSnapshot(refresh: true);
            if (!snapshot.IsInstalled || String.IsNullOrWhiteSpace(snapshot.ExecutablePath))
            {
                return false;
            }

            if (snapshot.Availability == CompanionAvailability.Running)
            {
                return true;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = snapshot.ExecutablePath,
                    Arguments = "--background",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                Invalidate();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static Boolean OpenDownloadPage()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var explorerPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "explorer.exe");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = explorerPath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    startInfo.ArgumentList.Add(DownloadUrl);

                    using var explorerProcess = Process.Start(startInfo);
                    if (explorerProcess is not null)
                    {
                        PluginLog.Info("Opened the Cursivis Companion download page.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Windows Explorer could not open the Companion download page; trying the registered browser.");
            }

            try
            {
                using var browserProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = DownloadUrl,
                    UseShellExecute = true,
                });
                if (browserProcess is not null)
                {
                    PluginLog.Info("Opened the Cursivis Companion download page with the registered browser.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"Could not open the Companion download page. Setup: {DownloadUrl}");
            }

            return false;
        }

        public static void Invalidate()
        {
            lock (SyncRoot)
            {
                _cachedAtUtc = DateTime.MinValue;
            }
        }

        private static CompanionRuntimeSnapshot ReadSnapshot()
        {
            var profilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cursivis",
                "runtime-profile.json");

            if (!File.Exists(profilePath))
            {
                return new CompanionRuntimeSnapshot(CompanionAvailability.NotInstalled, null);
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(profilePath));
                if (!document.RootElement.TryGetProperty("companionExecutable", out var executableElement))
                {
                    return new CompanionRuntimeSnapshot(CompanionAvailability.NotInstalled, null);
                }

                var executablePath = executableElement.GetString();
                if (String.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    return new CompanionRuntimeSnapshot(CompanionAvailability.NotInstalled, null);
                }

                var isRunning = Process.GetProcessesByName("Cursivis.Companion")
                    .Any(process =>
                    {
                        try
                        {
                            return String.Equals(
                                process.MainModule?.FileName,
                                executablePath,
                                StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return false;
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    });

                return new CompanionRuntimeSnapshot(
                    isRunning ? CompanionAvailability.Running : CompanionAvailability.InstalledNotRunning,
                    executablePath);
            }
            catch
            {
                return new CompanionRuntimeSnapshot(CompanionAvailability.NotInstalled, null);
            }
        }
    }
}
