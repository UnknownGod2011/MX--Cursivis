namespace Cursivis.Setup;

using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

public sealed class SetupForm : Form
{
    private const string DisplayVersion = "1.5.0";
    private const string PackageVersion = "1_5_0";
    private const string DefaultRuntimeZipUrl =
        "https://7laoth4l2ecu5n2m.public.blob.vercel-storage.com/runtime/CursivisRuntime_1_5_0-E9A859F6ABC699AE-fvZFRZoPBY2rtHWWUUXv4ln30BNFAU.zip";
    private const string NodeVersion = "v22.22.0";
    private static readonly string RuntimeZipUrl =
        typeof(SetupForm).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, "CursivisRuntimeUrl", StringComparison.Ordinal))
            ?.Value
        ?? DefaultRuntimeZipUrl;
    private static readonly string RuntimeZipSha256 =
        typeof(SetupForm).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, "CursivisRuntimeSha256", StringComparison.Ordinal))
            ?.Value
            ?.Trim()
        ?? string.Empty;

    private readonly Label statusLabel = new();
    private readonly Label detailLabel = new();
    private readonly ProgressBar progressBar = new();
    private readonly TextBox logBox = new();
    private readonly Button closeButton = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly object logFileLock = new();
    private readonly string logFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cursivis",
        "Logs",
        "setup.log");

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
            Text = "Thanks for installing the Cursivis Logitech plugin. This setup adds Companion, AI runtime services, Live Mode, and the startup connection used by your Logitech triggers.",
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

        PrepareLogFile();
        Log($"Setup {DisplayVersion} started.");
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
            SetStatus("Setup complete", "Cursivis is ready. Open Settings, add a Gemini API key, then use Talk, Go, Snip, Prompt Optimizer, or Live Mode.");
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
        await DownloadFileAsync(RuntimeZipUrl, zipPath, RuntimeZipSha256, token);

        SetStatus("Step 2 of 5: Extracting runtime", "Preparing Companion, Live Mode, backend, and trigger helpers.");
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
        ValidateRuntimePayload(payloadRoot);

        SetStatus("Step 3 of 5: Installing runtime files", "Copying Cursivis into your local app data folder.");
        StopInstalledRuntimeProcesses(installRoot);
        StopCursivisPortListeners(installRoot);
        ReplaceRuntimePayloadDirectories(installRoot);
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
        SetStatus("Step 5 of 5: Verifying local services", "Checking the Companion backend and browser connections.");
        await WaitForRuntimeServicesAsync(token);
        Log("Companion backend and browser services are ready.");
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
        await DownloadFileAsync(archiveUrl, downloadPath, expectedSha256: null, token);

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

    private async Task DownloadFileAsync(
        string url,
        string destination,
        string? expectedSha256,
        CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partialPath = destination + ".partial";
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                File.Delete(partialPath);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Cursivis-Companion-Setup/{DisplayVersion}");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength;
                long readTotal = 0;
                {
                    await using var source = await response.Content.ReadAsStreamAsync(token);
                    await using var target = File.Create(partialPath);

                    var buffer = new byte[1024 * 128];
                    int read;
                    progressBar.Style = total.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                    BeginInvoke(() => progressBar.Value = 0);

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

                    await target.FlushAsync(token);
                }

                if (total.HasValue && readTotal != total.Value)
                {
                    throw new InvalidDataException($"The download was incomplete ({readTotal} of {total.Value} bytes).");
                }

                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    VerifySha256(partialPath, expectedSha256);
                }

                File.Move(partialPath, destination, overwrite: true);
                Log($"Downloaded {Path.GetFileName(destination)}.");
                return;
            }
            catch (OperationCanceledException)
            {
                File.Delete(partialPath);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                File.Delete(partialPath);
                Log($"Download attempt {attempt} failed: {ex.Message}");
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), token);
                }
            }
        }

        throw new InvalidOperationException(
            "The download could not be completed after three attempts. Check your connection and run Setup again.",
            lastError);
    }

    private static void VerifySha256(string path, string expectedSha256)
    {
        if (expectedSha256.Length != 64 ||
            expectedSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "This setup build is missing a valid runtime integrity fingerprint.");
        }

        using var stream = File.OpenRead(path);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The downloaded Cursivis runtime failed its integrity check. Delete the download and run Setup again.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ValidateRuntimePayload(string payloadRoot)
    {
        var requiredFiles = new[]
        {
            Path.Combine("app", "companion", "Cursivis.Companion.exe"),
            Path.Combine("app", "hotkey-host", "Cursivis.HotkeyHost.exe"),
            Path.Combine("app", "trigger-launcher", "Cursivis.TriggerLauncher.exe"),
            Path.Combine("backend", "gemini-agent", "src", "server.js"),
            Path.Combine("desktop", "browser-action-agent", "src", "server.js"),
            Path.Combine("desktop", "browser-extension-chromium", "manifest.json"),
            Path.Combine("desktop", "browser-native-host", "src", "host.js"),
            Path.Combine("shared", "ipc-protocol", "schema", "agent-request.schema.json")
        };

        var missing = requiredFiles
            .Where(relativePath => !File.Exists(Path.Combine(payloadRoot, relativePath)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                "The downloaded runtime package is incomplete. Missing: " + string.Join(", ", missing));
        }
    }

    private static void ReplaceRuntimePayloadDirectories(string installRoot)
    {
        Directory.CreateDirectory(installRoot);
        var normalizedRoot = Path.GetFullPath(installRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var name in new[] { "app", "backend", "desktop", "shared" })
        {
            var target = Path.GetFullPath(Path.Combine(installRoot, name));
            if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to replace a runtime directory outside '{installRoot}'.");
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    private void StopInstalledRuntimeProcesses(string installRoot)
    {
        if (!Directory.Exists(installRoot))
        {
            return;
        }

        var normalizedRoot = Path.GetFullPath(installRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
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

    private void StopCursivisPortListeners(string installRoot)
    {
        var reservedPorts = new HashSet<string>(StringComparer.Ordinal) { "8080", "48820", "48830" };
        var pids = new HashSet<int>();
        var normalizedRoot = Path.GetFullPath(installRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

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
                var executablePath = TryGetProcessPath(process);
                var commandLine = TryGetProcessCommandLine(pid);
                var belongsToCursivis =
                    (!string.IsNullOrWhiteSpace(executablePath) &&
                     Path.GetFullPath(executablePath).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(commandLine) &&
                     commandLine.Contains(normalizedRoot, StringComparison.OrdinalIgnoreCase));

                if (!belongsToCursivis)
                {
                    throw new InvalidOperationException(
                        $"A different application is using a Cursivis local port ({process.ProcessName}, PID {pid}). " +
                        "Close that application and run Setup again.");
                }

                Log("Stopping previous Cursivis port listener: " + process.ProcessName);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch
            {
                // The process may have already exited or may deny termination.
            }
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessCommandLine(int processId)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId = {processId}').CommandLine\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task WaitForRuntimeServicesAsync(CancellationToken token)
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "http://127.0.0.1:8080/health",
            "http://127.0.0.1:48820/health",
            "http://127.0.0.1:48830/health"
        };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            foreach (var endpoint in pending.ToArray())
            {
                try
                {
                    using var response = await http.GetAsync(endpoint, token);
                    if (response.IsSuccessStatusCode)
                    {
                        pending.Remove(endpoint);
                    }
                }
                catch when (!token.IsCancellationRequested)
                {
                    // The Companion may still be starting this local service.
                }
            }

            if (pending.Count > 0)
            {
                await Task.Delay(750, token);
            }
        }

        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                "Cursivis installed, but one or more local services did not start. " +
                "Restart Windows and run Setup again if the issue continues.");
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
        try
        {
            lock (logFileLock)
            {
                File.AppendAllText(
                    logFilePath,
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Setup logging must never interrupt installation or recovery.
        }
    }

    private void PrepareLogFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            if (File.Exists(logFilePath) && new FileInfo(logFilePath).Length > 1024 * 1024)
            {
                File.Delete(logFilePath);
            }
        }
        catch
        {
            // The on-screen log remains available if the file cannot be created.
        }
    }
}
