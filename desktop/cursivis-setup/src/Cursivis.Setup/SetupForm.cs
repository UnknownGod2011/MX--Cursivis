namespace Cursivis.Setup;

using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

public sealed class SetupForm : Form
{
    private const string DisplayVersion = "1.4.1";
    private const string PackageVersion = "1_4_1";
    private const string RuntimeZipUrl = "https://github.com/UnknownGod2011/MX--Cursivis/releases/download/v1.4.1/CursivisRuntime_1_4_1.zip";
    private const string NodeVersion = "v22.22.0";

    private readonly Label statusLabel = new();
    private readonly Label detailLabel = new();
    private readonly ProgressBar progressBar = new();
    private readonly TextBox logBox = new();
    private readonly Button closeButton = new();
    private readonly CancellationTokenSource cancellation = new();

    private bool started;

    public SetupForm()
    {
        Text = "Cursivis Companion Setup";
        Width = 720;
        Height = 520;
        MinimumSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 18, 24);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);

        var title = new Label
        {
            Text = "Cursivis Companion Setup",
            AutoSize = false,
            Height = 44,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI Semibold", 20F),
            ForeColor = Color.White
        };

        var intro = new Label
        {
            Text = "Thanks for installing the Cursivis Logitech plugin. This setup adds the Companion runtime, backend, and startup connection so your Logitech triggers can talk to Cursivis automatically.",
            AutoSize = false,
            Height = 62,
            Dock = DockStyle.Top,
            ForeColor = Color.FromArgb(198, 205, 216)
        };

        statusLabel.Text = "Preparing setup...";
        statusLabel.AutoSize = false;
        statusLabel.Height = 30;
        statusLabel.Dock = DockStyle.Top;
        statusLabel.Font = new Font("Segoe UI Semibold", 12F);

        detailLabel.Text = "This may take a few minutes on the first install.";
        detailLabel.AutoSize = false;
        detailLabel.Height = 28;
        detailLabel.Dock = DockStyle.Top;
        detailLabel.ForeColor = Color.FromArgb(178, 185, 196);

        progressBar.Dock = DockStyle.Top;
        progressBar.Height = 10;
        progressBar.Style = ProgressBarStyle.Continuous;

        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.BorderStyle = BorderStyle.FixedSingle;
        logBox.BackColor = Color.FromArgb(22, 27, 35);
        logBox.ForeColor = Color.FromArgb(226, 231, 240);
        logBox.Font = new Font("Consolas", 9F);

        closeButton.Text = "Close";
        closeButton.Enabled = false;
        closeButton.Width = 120;
        closeButton.Height = 38;
        closeButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        closeButton.Click += (_, _) => Close();

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(0, 10, 0, 0)
        };
        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Resize += (_, _) =>
        {
            closeButton.Left = buttonPanel.Width - closeButton.Width;
            closeButton.Top = 10;
        };

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28)
        };
        content.Controls.Add(logBox);
        content.Controls.Add(buttonPanel);
        content.Controls.Add(progressBar);
        content.Controls.Add(detailLabel);
        content.Controls.Add(statusLabel);
        content.Controls.Add(intro);
        content.Controls.Add(title);

        Controls.Add(content);
        Shown += async (_, _) => await StartInstallOnceAsync();
        FormClosing += (_, _) => cancellation.Cancel();
    }

    private async Task StartInstallOnceAsync()
    {
        if (started)
        {
            return;
        }

        started = true;
        try
        {
            await InstallAsync(cancellation.Token);
            SetStatus("Setup complete", "Cursivis Companion is installed and ready. Open Settings to add API keys or choose a Local LLM.");
            Log("Done. You can close this setup window.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Setup cancelled", "No further changes are being made.");
            Log("Setup cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("Setup needs attention", ex.Message);
            Log("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Cursivis setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            closeButton.Enabled = true;
        }
    }

    private async Task InstallAsync(CancellationToken token)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "CursivisSetup", PackageVersion);
        var zipPath = Path.Combine(tempRoot, $"CursivisRuntime_{PackageVersion}.zip");
        var extractRoot = Path.Combine(tempRoot, "extracted");
        var installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Cursivis");

        Directory.CreateDirectory(tempRoot);

        SetStatus("Step 1 of 5: Downloading Companion runtime", "Downloading the Cursivis Companion package.");
        await DownloadFileAsync(RuntimeZipUrl, zipPath, token);

        SetStatus("Step 2 of 5: Extracting runtime", "Preparing Companion, backend, and trigger helpers.");
        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, recursive: true);
        }
        ZipFile.ExtractToDirectory(zipPath, extractRoot);

        var packageRoot = Path.Combine(extractRoot, $"CursivisRuntime_{PackageVersion}");
        var payloadRoot = Path.Combine(packageRoot, "runtime");
        if (!Directory.Exists(payloadRoot))
        {
            throw new InvalidOperationException("The downloaded runtime package is missing its runtime folder.");
        }

        SetStatus("Step 3 of 5: Installing runtime files", "Copying Cursivis into your local app data folder.");
        StopInstalledRuntimeProcesses(installRoot);
        StopCursivisPortListeners();
        CopyDirectory(payloadRoot, installRoot);

        SetStatus("Step 4 of 5: Preparing backend", "Installing local backend dependencies. This is the longest first-time step.");
        var nodeExe = await EnsurePortableNodeAsync(installRoot, token);
        await InvokeNpmAsync(nodeExe, Path.Combine(installRoot, "backend", "gemini-agent"), token);
        await InvokeNpmAsync(nodeExe, Path.Combine(installRoot, "desktop", "browser-action-agent"), token);

        SetStatus("Step 5 of 5: Connecting Logitech triggers", "Writing runtime profile, startup entries, and launching Companion.");
        var profilePath = WriteRuntimeProfile(installRoot);
        Log("Runtime profile: " + profilePath);
        RegisterStartup(installRoot);
        LaunchCompanion(installRoot);
        LaunchHotkeyHost(installRoot);
    }

    private async Task<string> EnsurePortableNodeAsync(string installRoot, CancellationToken token)
    {
        var nodeDir = Path.Combine(installRoot, "node");
        var nodeExe = Path.Combine(nodeDir, "node.exe");
        if (File.Exists(nodeExe))
        {
            Log("Portable Node.js already installed.");
            return nodeExe;
        }

        Directory.CreateDirectory(nodeDir);
        var archiveName = $"node-{NodeVersion}-win-x64.zip";
        var archiveUrl = $"https://nodejs.org/dist/{NodeVersion}/{archiveName}";
        var downloadPath = Path.Combine(Path.GetTempPath(), archiveName);
        var extractRoot = Path.Combine(Path.GetTempPath(), $"cursivis-node-{NodeVersion}");

        SetStatus("Step 4 of 5: Downloading portable Node.js", "Cursivis uses a private portable Node runtime so users do not need developer tools.");
        await DownloadFileAsync(archiveUrl, downloadPath, token);

        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, recursive: true);
        }
        ZipFile.ExtractToDirectory(downloadPath, extractRoot);

        var expanded = Directory.GetDirectories(extractRoot).FirstOrDefault()
            ?? throw new InvalidOperationException("Node.js archive did not contain the expected folder.");
        CopyDirectory(expanded, nodeDir);
        return nodeExe;
    }

    private async Task InvokeNpmAsync(string nodeExe, string projectDir, CancellationToken token)
    {
        var packageJson = Path.Combine(projectDir, "package.json");
        if (!File.Exists(packageJson))
        {
            return;
        }

        var npmCli = Path.Combine(Path.GetDirectoryName(nodeExe)!, "node_modules", "npm", "bin", "npm-cli.js");
        if (!File.Exists(npmCli))
        {
            throw new InvalidOperationException("npm was not found in the portable Node.js folder.");
        }

        var hasLock = File.Exists(Path.Combine(projectDir, "package-lock.json"));
        var args = hasLock
            ? $"\"{npmCli}\" ci --omit=dev"
            : $"\"{npmCli}\" install --omit=dev";

        await RunProcessAsync(nodeExe, args, projectDir, token);
    }

    private async Task RunProcessAsync(string fileName, string arguments, string workingDirectory, CancellationToken token)
    {
        Log($"> {Path.GetFileName(fileName)} {arguments}");
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} failed with exit code {process.ExitCode}.");
        }
    }

    private async Task DownloadFileAsync(string url, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = File.Create(destination);

        var buffer = new byte[1024 * 128];
        long readTotal = 0;
        int read;
        progressBar.Style = total.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;

        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            readTotal += read;
            if (total.HasValue && total.Value > 0)
            {
                var percent = (int)Math.Min(100, readTotal * 100 / total.Value);
                BeginInvoke(() => progressBar.Value = percent);
            }
        }

        Log($"Downloaded {Path.GetFileName(destination)}.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private void StopInstalledRuntimeProcesses(string installRoot)
    {
        if (!Directory.Exists(installRoot))
        {
            return;
        }

        var normalizedRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var currentProcessId = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            if (process.Id == currentProcessId)
            {
                continue;
            }

            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // Some system processes deny module access. They are unrelated to Cursivis setup.
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Log("Stopping previous runtime process: " + process.ProcessName);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // If Windows already closed the process or access is denied, continue the install.
            }
        }
    }

    private void StopCursivisPortListeners()
    {
        var reservedPorts = new HashSet<string>(StringComparer.Ordinal) { "8080", "48820", "48830" };
        var pids = new HashSet<int>();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p TCP",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var netstat = Process.Start(startInfo);
            if (netstat == null)
            {
                return;
            }

            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(5000);

            foreach (var rawLine in output.Split(Environment.NewLine))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || !line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                {
                    continue;
                }

                var localAddress = parts[1];
                var portIndex = localAddress.LastIndexOf(':');
                if (portIndex < 0)
                {
                    continue;
                }

                var port = localAddress[(portIndex + 1)..];
                if (!reservedPorts.Contains(port))
                {
                    continue;
                }

                if (int.TryParse(parts[^1], out var pid) && pid != Environment.ProcessId)
                {
                    pids.Add(pid);
                }
            }
        }
        catch
        {
            return;
        }

        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                Log("Stopping previous Cursivis port listener: " + process.ProcessName);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // The process may have already exited or may deny termination.
            }
        }
    }

    private static string WriteRuntimeProfile(string root)
    {
        var profileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cursivis");
        var profilePath = Path.Combine(profileDir, "runtime-profile.json");
        Directory.CreateDirectory(profileDir);

        using var existing = TryReadJson(profilePath);

        string ExistingString(string name, string fallback)
        {
            if (existing is not null &&
                existing.RootElement.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }

            return fallback;
        }

        object ExistingValue(string name, object fallback)
        {
            if (existing is not null && existing.RootElement.TryGetProperty(name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when value.TryGetDouble(out var number) => number,
                    JsonValueKind.String => value.GetString() ?? fallback,
                    _ => fallback
                };
            }

            return fallback;
        }

        var profile = new Dictionary<string, object?>
        {
            ["backendDir"] = Path.Combine(root, "backend", "gemini-agent"),
            ["browserAgentDir"] = Path.Combine(root, "desktop", "browser-action-agent"),
            ["extensionBridgeDir"] = Path.Combine(root, "desktop", "browser-native-host"),
            ["companionProject"] = "",
            ["companionExecutable"] = Path.Combine(root, "app", "companion", "Cursivis.Companion.exe"),
            ["hotkeyHostExecutable"] = Path.Combine(root, "app", "hotkey-host", "Cursivis.HotkeyHost.exe"),
            ["backendUrl"] = "http://127.0.0.1:8080",
            ["browserAgentUrl"] = "http://127.0.0.1:48820",
            ["extensionBridgeUrl"] = "http://127.0.0.1:48830",
            ["aiProvider"] = ExistingString("aiProvider", "gemini"),
            ["openAiBaseUrl"] = ExistingString("openAiBaseUrl", "https://api.openai.com/v1"),
            ["openAiApiKey"] = ExistingString("openAiApiKey", ""),
            ["openAiModel"] = ExistingString("openAiModel", "gpt-4.1-mini"),
            ["hostedApiUrl"] = ExistingString("hostedApiUrl", ""),
            ["hostedToken"] = ExistingString("hostedToken", ""),
            ["ollamaUrl"] = ExistingString("ollamaUrl", "http://127.0.0.1:11434"),
            ["localModel"] = ExistingString("localModel", "granite3.2-vision:2b"),
            ["apiKey"] = ExistingString("apiKey", ""),
            ["apiKeys"] = ExistingString("apiKeys", ""),
            ["enableStreamingTranscription"] = ExistingValue("enableStreamingTranscription", false),
            ["enableAutoReplace"] = ExistingValue("enableAutoReplace", false),
            ["autoReplaceConfidence"] = ExistingValue("autoReplaceConfidence", 0.9),
            ["enableManagedBrowserFallback"] = ExistingValue("enableManagedBrowserFallback", false)
        };

        File.WriteAllText(profilePath, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        return profilePath;
    }

    private static JsonDocument? TryReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RegisterStartup(string root)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var companionExe = Path.Combine(root, "app", "companion", "Cursivis.Companion.exe");
        var hotkeyExe = Path.Combine(root, "app", "hotkey-host", "Cursivis.HotkeyHost.exe");

        if (File.Exists(companionExe))
        {
            key?.SetValue("CursivisCompanion", $"\"{companionExe}\" --background", RegistryValueKind.String);
        }

        if (File.Exists(hotkeyExe))
        {
            key?.SetValue("CursivisHotkeyHost", $"\"{hotkeyExe}\"", RegistryValueKind.String);
        }
    }

    private void LaunchCompanion(string root)
    {
        var companionExe = Path.Combine(root, "app", "companion", "Cursivis.Companion.exe");
        if (!File.Exists(companionExe))
        {
            throw new InvalidOperationException("Companion executable was not found after install.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = companionExe,
            UseShellExecute = true
        });
        Log("Launched Cursivis Companion.");
    }

    private void LaunchHotkeyHost(string root)
    {
        var hotkeyExe = Path.Combine(root, "app", "hotkey-host", "Cursivis.HotkeyHost.exe");
        if (!File.Exists(hotkeyExe))
        {
            Log("Hotkey host executable was not found after install.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = hotkeyExe,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        Log("Launched Cursivis hotkey host.");
    }

    private void SetStatus(string status, string detail)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(status, detail));
            return;
        }

        statusLabel.Text = status;
        detailLabel.Text = detail;
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }

        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }
}
