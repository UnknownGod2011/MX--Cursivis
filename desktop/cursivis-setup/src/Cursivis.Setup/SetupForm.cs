namespace Cursivis.Setup;

using Cursivis.Security;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class SetupForm : Form
{
    private const string DisplayVersion = "1.5.4";
    private const string PackageVersion = "1_5_4";
    private const string DefaultRuntimeZipUrl =
        "https://github.com/UnknownGod2011/MX--Cursivis/releases/download/v1.5.4/CursivisRuntime_1_5_4.zip";
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
    private readonly Button detailsButton = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly object logFileLock = new();
    private readonly string logFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cursivis",
        "Logs",
        "setup.log");

    private bool started;
    private bool installationFinished;

    public SetupForm()
    {
        Text = "Cursivis Companion Setup";
        Width = 680;
        Height = 392;
        MinimumSize = new Size(580, 350);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = SystemColors.Window;
        ForeColor = Color.FromArgb(24, 28, 34);
        Font = new Font("Segoe UI", 10F);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath)
            ?? SystemIcons.Application;

        var title = new Label
        {
            Text = "Cursivis Companion Setup",
            AutoSize = false,
            Height = 44,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI Semibold", 20F),
            ForeColor = Color.FromArgb(20, 24, 30)
        };

        var intro = new Label
        {
            Text = "Thanks for installing the Cursivis Logitech plugin. This setup adds Companion, AI runtime services, Live Mode, and the startup connection used by your Logitech triggers.",
            AutoSize = false,
            Height = 64,
            Dock = DockStyle.Top,
            ForeColor = Color.FromArgb(82, 91, 102)
        };

        statusLabel.Text = "Preparing setup...";
        statusLabel.AutoSize = false;
        statusLabel.Height = 34;
        statusLabel.Dock = DockStyle.Top;
        statusLabel.Font = new Font("Segoe UI Semibold", 12F);

        detailLabel.Text = "This may take a few minutes on the first install.";
        detailLabel.AutoSize = false;
        detailLabel.Height = 46;
        detailLabel.Dock = DockStyle.Top;
        detailLabel.ForeColor = Color.FromArgb(92, 101, 112);

        progressBar.Dock = DockStyle.Top;
        progressBar.Height = 10;
        progressBar.Margin = new Padding(0, 4, 0, 8);
        progressBar.Style = ProgressBarStyle.Continuous;

        logBox.Dock = DockStyle.Fill;
        logBox.Multiline = true;
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.BorderStyle = BorderStyle.FixedSingle;
        logBox.BackColor = Color.FromArgb(248, 250, 252);
        logBox.ForeColor = Color.FromArgb(38, 45, 54);
        logBox.Font = new Font("Consolas", 9F);
        logBox.Visible = false;

        closeButton.Text = "Cancel";
        closeButton.Enabled = true;
        closeButton.Width = 120;
        closeButton.Height = 38;
        closeButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        closeButton.Click += (_, _) =>
        {
            if (!installationFinished)
            {
                cancellation.Cancel();
            }

            Close();
        };

        detailsButton.Text = "Details";
        detailsButton.Width = GetDetailsButtonWidth(detailsButton.Text);
        detailsButton.Height = 38;
        detailsButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        detailsButton.FlatStyle = FlatStyle.System;
        detailsButton.Click += (_, _) => ToggleDetails();

        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Padding = new Padding(0, 10, 0, 0)
        };
        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Controls.Add(detailsButton);
        buttonPanel.Resize += (_, _) =>
        {
            closeButton.Left = buttonPanel.Width - closeButton.Width;
            closeButton.Top = 10;
            detailsButton.Left = 0;
            detailsButton.Top = 10;
        };

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 22)
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
            HideDetails();
            SetProgress(100);
            SetStatus("Setup complete", "Cursivis is ready. Open Settings to add a Gemini API key, then start using Cursivis.");
            Log("Done. You can close this setup window.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Setup cancelled", "No changes were made to your active Cursivis installation.");
            Log("Setup cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus("Setup needs attention", $"{ex.Message} Select Details if you need to share the support log.");
            Log("ERROR: " + ex);
            ShowDetails();
        }
        finally
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            installationFinished = true;
            closeButton.Text = "Close";
            closeButton.Enabled = true;
            if (!IsDisposed && IsHandleCreated)
            {
                WindowState = FormWindowState.Normal;
                Show();
                Activate();
                BringToFront();
            }
        }
    }

    private async Task InstallAsync(CancellationToken token)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "CursivisSetup", PackageVersion, Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempRoot, $"CursivisRuntime_{PackageVersion}.zip");
        var extractRoot = Path.Combine(tempRoot, "extracted");
        var programsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        var installRoot = Path.Combine(programsRoot, "Cursivis");
        var stagingRoot = CreateUpdateRoot(programsRoot, $"Cursivis.staging.{PackageVersion}");
        var backupRoot = CreateUpdateRoot(programsRoot, $"Cursivis.backup.{PackageVersion}");
        var previousRuntimeExisted = false;
        RuntimeUpdateJournal? transaction = null;

        SetProgress(0);
        SetStatus("Preparing setup", "Checking the existing installation and preparing a safe update.");
        await Task.Run(() =>
        {
            RecoverInterruptedUpdates(programsRoot, installRoot);
            Directory.CreateDirectory(tempRoot);
        }, token);

        try
        {
            await AcquireRuntimeAsync(zipPath, token);
            SetProgress(20);

            SetStatus("Step 2 of 5: Extracting runtime", "Preparing Companion, Live Mode, backend, and trigger helpers.");
            var packageRoot = Path.Combine(extractRoot, $"CursivisRuntime_{PackageVersion}");
            var payloadRoot = Path.Combine(packageRoot, "runtime");
            await Task.Run(() =>
            {
                DeleteDirectoryOrThrow(extractRoot, "temporary extraction files");
                ZipFile.ExtractToDirectory(zipPath, extractRoot);
                if (!Directory.Exists(payloadRoot))
                {
                    throw new InvalidOperationException("The downloaded runtime package is missing its runtime folder.");
                }

                ValidateRuntimePayload(payloadRoot, requireSecureIpc: true);
            }, token);
            SetProgress(40);

            SetStatus("Step 3 of 5: Staging runtime files", "Preparing the update without changing your working installation.");
            await Task.Run(() =>
            {
                CopyDirectory(payloadRoot, stagingRoot);
                ValidateRuntimePayload(stagingRoot, requireSecureIpc: true);
            }, token);
            SetProgress(60);

            SetStatus("Step 4 of 5: Verifying runtime", "Checking the bundled backend and Live Mode dependencies.");
            await Task.Run(() => ValidateRuntimePayload(stagingRoot, requireSecureIpc: true), token);
            SetProgress(80);

            SetStatus("Step 5 of 5: Activating update", "Stopping the old runtime briefly and switching to the verified files.");
            try
            {
                await Task.Run(() =>
                {
                    previousRuntimeExisted = HasValidRuntimePayload(installRoot);
                    StopInstalledRuntimeProcesses(installRoot);
                    StopCursivisPortListeners(installRoot);
                    PreserveMutableRuntimeData(installRoot, stagingRoot);
                    transaction = RuntimeUpdateJournal.Create(installRoot, backupRoot, CaptureProtectedProfile());
                    RuntimeUpdateJournal.MoveIntoPlace(stagingRoot, installRoot, backupRoot);
                }, token);
                SetProgress(88);
            }
            catch (Exception commitError)
            {
                Log("Windows blocked the runtime replacement.");
                if (transaction is not null)
                {
                    await Task.Run(() => RestoreUpdate(transaction, installRoot, backupRoot), CancellationToken.None);
                }
                if (previousRuntimeExisted && HasValidRuntimePayload(installRoot))
                {
                    try
                    {
                        LaunchCompanion(installRoot);
                        LaunchHotkeyHost(installRoot);
                        await WaitForRuntimeServicesAsync(CancellationToken.None, UsesSecureIpc(installRoot));
                        Log("Previous runtime restarted successfully.");
                    }
                    catch (Exception recoveryError)
                    {
                        throw new InvalidOperationException(
                            "Windows blocked the update. The previous runtime was restored but could not restart automatically.",
                            new AggregateException(commitError, recoveryError));
                    }
                }

                throw new InvalidOperationException(
                    previousRuntimeExisted
                        ? "Windows blocked the update, so the previous runtime was restored. Close other Cursivis windows and retry."
                        : "Windows blocked installation. Close other Cursivis windows and run Setup again.",
                    commitError);
            }

            try
            {
                SetStatus("Step 5 of 5: Connecting Logitech triggers", "Preserving settings, refreshing startup entries, and launching Companion.");
                var profilePath = WriteRuntimeProfile(installRoot);
                Log("Runtime profile: " + profilePath);
                LaunchCompanion(installRoot);
                LaunchHotkeyHost(installRoot);
                SetProgress(92);
                SetStatus("Step 5 of 5: Verifying local services", "Checking the Companion backend and browser connections.");
                await WaitForRuntimeServicesAsync(token);
                Log("Companion backend and browser services are ready.");
                RuntimeUpdateJournal.MarkActivated(backupRoot);
            }
            catch (Exception activationError)
            {
                Log(previousRuntimeExisted
                    ? "Activation failed. Restoring the previous runtime."
                    : "Activation failed. Removing the incomplete runtime.");
                await Task.Run(() =>
                {
                    StopInstalledRuntimeProcesses(installRoot);
                    StopCursivisPortListeners(installRoot);
                    RestoreUpdate(transaction!, installRoot, backupRoot);
                }, CancellationToken.None);

                if (previousRuntimeExisted && HasValidRuntimePayload(installRoot))
                {
                    LaunchCompanion(installRoot);
                    LaunchHotkeyHost(installRoot);
                    await WaitForRuntimeServicesAsync(CancellationToken.None, UsesSecureIpc(installRoot));
                    Log("Previous runtime restored successfully.");
                }

                throw new InvalidOperationException(
                    previousRuntimeExisted
                        ? "Cursivis could not activate the update, so the previous working runtime was restored."
                        : "Cursivis could not finish its first launch. No incomplete runtime was left installed. Please run Setup again.",
                    activationError);
            }

            // Never register startup entries that point to a runtime that failed activation.
            RegisterStartup(installRoot);
            if (!await Task.Run(() => TryCleanBackup(backupRoot), CancellationToken.None))
            {
                Log("The previous runtime backup is still in use and will be cleaned up during a later update.");
            }
        }
        finally
        {
            await Task.Run(() =>
            {
                _ = TryDeleteDirectory(stagingRoot);
                _ = TryDeleteDirectory(tempRoot);
            }, CancellationToken.None);
        }
    }

    private async Task AcquireRuntimeAsync(string destination, CancellationToken token)
    {
        if (RuntimeZipSha256.Length != 64 || RuntimeZipSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("This setup build is missing a valid runtime integrity fingerprint.");
        }

        var localArchive = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(destination));
        if (!File.Exists(localArchive))
        {
            SetStatus("Step 1 of 5: Downloading Companion runtime", "Downloading the Cursivis Companion package from GitHub Releases.");
            await DownloadFileAsync(RuntimeZipUrl, destination, RuntimeZipSha256, token);
            return;
        }

        SetStatus("Step 1 of 5: Verifying Companion runtime", "Checking the runtime archive next to Setup.");
        await using (var source = File.OpenRead(localArchive))
        await using (var target = File.Create(destination))
        {
            await source.CopyToAsync(target, token);
        }
        await VerifySha256Async(destination, RuntimeZipSha256, token);
        Log($"Verified local {Path.GetFileName(destination)}.");
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        string expectedSha256,
        CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partialPath = destination + ".partial";
        Exception? lastError = null;
        File.Delete(destination);
        File.Delete(partialPath);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                File.Delete(partialPath);
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                attemptTimeout.CancelAfter(TimeSpan.FromMinutes(10));
                var attemptToken = attemptTimeout.Token;
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"Cursivis-Companion-Setup/{DisplayVersion}");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, attemptToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Download host returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                        inner: null,
                        response.StatusCode);
                }

                var total = response.Content.Headers.ContentLength;
                long readTotal = 0;
                {
                    await using var source = await response.Content.ReadAsStreamAsync(attemptToken);
                    await using var target = File.Create(partialPath);

                    var buffer = new byte[1024 * 128];
                    int read;
                    progressBar.Style = total.HasValue ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
                    BeginInvoke(() => progressBar.Value = 0);

                    while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), attemptToken)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), attemptToken);
                        readTotal += read;
                        if (total.HasValue && total.Value > 0)
                        {
                            var percent = (int)Math.Min(20, readTotal * 20 / total.Value);
                            BeginInvoke(() => progressBar.Value = percent);
                        }
                    }

                    await target.FlushAsync(attemptToken);
                }

                if (total.HasValue && readTotal != total.Value)
                {
                    throw new InvalidDataException($"The download was incomplete ({readTotal} of {total.Value} bytes).");
                }

                await VerifySha256Async(partialPath, expectedSha256, attemptToken);

                File.Move(partialPath, destination, overwrite: true);
                Log($"Downloaded {Path.GetFileName(destination)}.");
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                File.Delete(partialPath);
                throw;
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode.HasValue && IsPermanentDownloadStatus(ex.StatusCode.Value))
            {
                File.Delete(partialPath);
                File.Delete(destination);
                Log($"Permanent download failure from {new Uri(url).Host}: HTTP {(int)ex.StatusCode.Value}.");
                throw new InvalidOperationException(
                    $"A required Cursivis download is unavailable (HTTP {(int)ex.StatusCode.Value}). " +
                    "Download the latest installer from https://mxcursivis.vercel.app and run it again. " +
                    "If the issue continues, contact Cursivis support.",
                    ex);
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

    private static bool IsPermanentDownloadStatus(HttpStatusCode statusCode)
    {
        return statusCode is
            HttpStatusCode.BadRequest or
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.NotFound or
            HttpStatusCode.Gone;
    }

    private void ToggleDetails()
    {
        if (logBox.Visible)
        {
            HideDetails();
            return;
        }

        ShowDetails();
    }

    private void ShowDetails()
    {
        logBox.Visible = true;
        detailsButton.Text = "Hide Details";
        detailsButton.Width = GetDetailsButtonWidth(detailsButton.Text);
        Height = Math.Max(Height, 570);
    }

    private void HideDetails()
    {
        logBox.Visible = false;
        detailsButton.Text = "Details";
        detailsButton.Width = GetDetailsButtonWidth(detailsButton.Text);
        Height = Math.Max(MinimumSize.Height, 392);
    }

    private int GetDetailsButtonWidth(string text)
    {
        var textWidth = TextRenderer.MeasureText(text, detailsButton.Font).Width;
        return Math.Max(104, textWidth + 36);
    }

    private static async Task VerifySha256Async(string path, string expectedSha256, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The downloaded Cursivis runtime failed its integrity check. Delete the download and run Setup again.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        RuntimeUpdateJournal.RejectReparsePoint(source);
        RuntimeUpdateJournal.RejectReparsePoint(destination);
        Directory.CreateDirectory(destination);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ValidateRuntimePayload(string payloadRoot, bool requireSecureIpc = false)
    {
        var requiredFiles = new[]
        {
            Path.Combine("app", "companion", "Cursivis.Companion.exe"),
            Path.Combine("app", "hotkey-host", "Cursivis.HotkeyHost.exe"),
            Path.Combine("app", "trigger-launcher", "Cursivis.TriggerLauncher.exe"),
            Path.Combine("node", "node.exe"),
            Path.Combine("backend", "gemini-agent", "src", "server.js"),
            Path.Combine("desktop", "browser-action-agent", "src", "server.js"),
            Path.Combine("desktop", "browser-extension-chromium", "manifest.json"),
            Path.Combine("desktop", "browser-native-host", "src", "host.js"),
            Path.Combine("shared", "ipc-protocol", "schema", "agent-request.schema.json")
        };
        if (requireSecureIpc)
        {
            requiredFiles = [
                ..requiredFiles,
                Path.Combine("app", "browser-native-host", "Cursivis.BrowserNativeHost.exe"),
                Path.Combine("shared", "local-ipc", "local-service-auth.cjs")
            ];
        }

        var missing = requiredFiles
            .Where(relativePath => !File.Exists(Path.Combine(payloadRoot, relativePath)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                "The downloaded runtime package is incomplete. Missing: " + string.Join(", ", missing));
        }

        var requiredDirectories = new[]
        {
            Path.Combine("backend", "gemini-agent", "node_modules"),
            Path.Combine("desktop", "browser-action-agent", "node_modules")
        };
        var missingDirectories = requiredDirectories
            .Where(relativePath => !Directory.Exists(Path.Combine(payloadRoot, relativePath)))
            .ToArray();
        if (missingDirectories.Length > 0)
        {
            throw new InvalidDataException(
                "The downloaded runtime package is missing prepared dependencies: " + string.Join(", ", missingDirectories));
        }
    }

    private void RecoverInterruptedUpdates(string programsRoot, string installRoot)
    {
        if (!Directory.Exists(programsRoot))
        {
            return;
        }

        var pending = new List<(string Path, RuntimeUpdateJournal Journal)>();
        foreach (var path in Directory.EnumerateDirectories(programsRoot, "Cursivis.*"))
        {
            var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path),
                @"^Cursivis\.(backup|staging)\.\d+_\d+_\d+(?:\.\d{14}\.[0-9a-f]{32})?$");
            if (!match.Success)
            {
                continue;
            }
            if (match.Groups[1].Value == "staging")
            {
                if (!TryDeleteDirectory(path))
                {
                    Log("A stale staging folder is still in use and was kept.");
                }
                continue;
            }
            RuntimeUpdateJournal.RejectReparsePoint(path);
            if (RuntimeUpdateJournal.WasActivated(path))
            {
                _ = TryCleanBackup(path);
            }
            else if (RuntimeUpdateJournal.Read(path) is { } plan)
            {
                pending.Add((path, plan));
            }
            else
            {
                // Older Setup versions did not record which original directories had moved.
                Log("Keeping a previous Setup backup that does not have a recovery record: " + path);
            }
        }

        if (pending.Count > 1)
        {
            throw new InvalidOperationException("Setup found multiple interrupted updates. The backups were kept. Please contact Cursivis support.");
        }
        if (pending.Count == 1)
        {
            Log("Recovering the previous runtime from an interrupted update.");
            StopInstalledRuntimeProcesses(installRoot);
            StopCursivisPortListeners(installRoot);
            RestoreUpdate(pending[0].Journal, installRoot, pending[0].Path);
            if (HasValidRuntimePayload(installRoot))
            {
                LaunchCompanion(installRoot);
                LaunchHotkeyHost(installRoot);
            }
        }
    }

    private static void PreserveMutableRuntimeData(string installRoot, string stagingRoot)
    {
        var relativeDataPaths = new[]
        {
            Path.Combine("desktop", "browser-action-agent", "data")
        };

        foreach (var relativePath in relativeDataPaths)
        {
            var source = Path.Combine(installRoot, relativePath);
            var destination = Path.Combine(stagingRoot, relativePath);
            if (!Directory.Exists(source))
            {
                continue;
            }

            DeleteDirectoryOrThrow(destination, "preserved browser data");
            CopyDirectory(source, destination);
        }
    }

    private void RestoreUpdate(RuntimeUpdateJournal journal, string installRoot, string backupRoot)
    {
        try
        {
            journal.RestoreDirectories(installRoot, backupRoot,
                path => DeleteDirectoryOrThrow(path, "incomplete runtime files"));
            RestoreProtectedProfile(journal.ProtectedProfile);
            RuntimeUpdateJournal.MarkActivated(backupRoot);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "Setup could not finish restoring the previous runtime. Its backup was kept. Close Cursivis and try Setup again.", error);
        }
        _ = TryCleanBackup(backupRoot);
    }

    private static bool TryCleanBackup(string backupRoot)
    {
        RuntimeUpdateJournal.RejectReparsePoint(backupRoot);
        // Retain the completion marker until all previous payload directories are gone.
        foreach (var name in RuntimeUpdateJournal.PayloadDirectories)
        {
            if (!TryDeleteDirectory(RequireChildPath(backupRoot, name)))
            {
                return false;
            }
        }
        return TryDeleteDirectory(backupRoot);
    }

    private static bool HasValidRuntimePayload(string root)
    {
        try
        {
            ValidateRuntimePayload(root);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string RequireChildPath(string parent, string child)
    {
        var normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(parent, child));
        if (!target.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to modify a path outside '{parent}'.");
        }

        return target;
    }

    private static string CreateUpdateRoot(string parent, string baseName)
    {
        var candidate = Path.Combine(parent, baseName);
        if (!Directory.Exists(candidate))
        {
            return candidate;
        }

        return Path.Combine(parent, $"{baseName}.{DateTime.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}");
    }

    private static void DeleteDirectoryOrThrow(string path, string description)
    {
        if (!TryDeleteDirectory(path))
        {
            throw new IOException($"Cursivis could not remove {description}. Close any Cursivis setup windows and try again.");
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                RuntimeUpdateJournal.RejectReparsePoint(path);
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return !Directory.Exists(path);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * (attempt + 1)));
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * (attempt + 1)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return !Directory.Exists(path);
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", options).Append(root))
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch (IOException)
                {
                    // A locked file will be retried by the caller's bounded delete loop.
                }
                catch (UnauthorizedAccessException)
                {
                    // A locked file will be retried by the caller's bounded delete loop.
                }
            }
        }
        catch (IOException)
        {
            // Enumeration can race with antivirus or a closing process; delete handles retries.
        }
        catch (UnauthorizedAccessException)
        {
            // Enumeration can race with antivirus or a closing process; delete handles retries.
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
        var reservedPorts = new HashSet<string>(StringComparer.Ordinal) { "51880", "48820", "48830" };
        var pids = new HashSet<int>();
        var normalizedRoot = Path.GetFullPath(installRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netstat.exe"),
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
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
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

    private static bool UsesSecureIpc(string installRoot) =>
        File.Exists(Path.Combine(installRoot, "shared", "local-ipc", "local-service-auth.cjs"));

    private async Task WaitForRuntimeServicesAsync(CancellationToken token, bool requireAuthenticatedServices = true)
    {
        var profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cursivis", "runtime-profile.json");
        using var profile = TryReadJson(profilePath);
        var backendAddress = profile?.RootElement.TryGetProperty("backendUrl", out var backend) == true &&
            backend.ValueKind == JsonValueKind.String ? backend.GetString() : null;
        if (!Uri.TryCreate(backendAddress, UriKind.Absolute, out var backendUri))
        {
            backendUri = new Uri("http://127.0.0.1:51880");
        }
        var pending = new Dictionary<string, (Uri Endpoint, string Scope)>(StringComparer.Ordinal)
        {
            ["the AI service"] = (new Uri(backendUri, "/health"), LocalIpc.Backend),
            ["the browser action service"] = (new Uri("http://127.0.0.1:48820/health"), LocalIpc.BrowserAgent),
            ["the browser connection service"] = (new Uri("http://127.0.0.1:48830/health"), LocalIpc.BrowserBridge)
        };
        if (requireAuthenticatedServices)
        {
            pending.Add("the Logitech trigger channel", (new Uri("ws://127.0.0.1:48711/cursivis-trigger/"), LocalIpc.Trigger));
            pending.Add("the haptic channel", (new Uri("ws://127.0.0.1:48712/cursivis-haptics/"), LocalIpc.Haptics));
        }
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt + TimeSpan.FromMinutes(3);
        var nextProgressLog = startedAt + TimeSpan.FromSeconds(45);

        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            foreach (var (name, (uri, scope)) in pending.ToArray())
            {
                try
                {
                    if (uri.Scheme == "ws")
                    {
                        using var socket = new ClientWebSocket();
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(2));
                        await LocalIpc.ConnectAsync(socket, uri, scope, timeout.Token);
                        pending.Remove(name);
                        continue;
                    }
                    using var http = requireAuthenticatedServices
                        ? LocalIpc.CreateHttpClient(uri, scope, TimeSpan.FromSeconds(3))
                        : new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
                            { Timeout = TimeSpan.FromSeconds(3) };
                    using var response = await http.GetAsync(uri, token);
                    if (response.IsSuccessStatusCode)
                    {
                        pending.Remove(name);
                    }
                }
                catch when (!token.IsCancellationRequested)
                {
                    // The Companion may still be starting this local service.
                }
            }

            if (pending.Count > 0)
            {
                if (DateTime.UtcNow >= nextProgressLog)
                {
                    var pendingServices = string.Join(", ", pending.Keys);
                    Log($"Still waiting for local services: {pendingServices}.");
                    SetStatus(
                        "Step 5 of 5: Finishing first launch",
                        "This PC is taking a little longer to start Cursivis. Setup is still working safely.");
                    SetProgress(95);
                    nextProgressLog = DateTime.UtcNow + TimeSpan.FromSeconds(45);
                }

                await Task.Delay(750, token);
            }
        }

        if (pending.Count > 0)
        {
            var pendingServices = string.Join(", ", pending.Keys);
            throw new InvalidOperationException(
                $"Cursivis installed, but {pendingServices} did not start. " +
                "Restart Windows and run Setup again if the issue continues.");
        }
    }

    private static string RuntimeProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cursivis", "runtime-profile.json");

    private static byte[]? CaptureProtectedProfile()
    {
        RuntimeUpdateJournal.RejectReparsePoint(RuntimeProfilePath);
        if (!File.Exists(RuntimeProfilePath))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(RuntimeProfilePath);
        try
        {
            return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void RestoreProtectedProfile(byte[]? protectedProfile)
    {
        RuntimeUpdateJournal.RejectReparsePoint(RuntimeProfilePath);
        if (protectedProfile is null)
        {
            File.Delete(RuntimeProfilePath);
            return;
        }
        var bytes = ProtectedData.Unprotect(protectedProfile, null, DataProtectionScope.CurrentUser);
        try
        {
            WriteRuntimeProfileBytes(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string WriteRuntimeProfile(string root)
    {
        using var existing = TryReadJson(RuntimeProfilePath);

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

        string ExistingBackendUrl()
        {
            var configured = ExistingString("backendUrl", string.Empty).TrimEnd('/');
            // Migrate the former common Docker port without overwriting intentional custom endpoints.
            return string.Equals(configured, "http://127.0.0.1:8080", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(configured, "http://localhost:8080", StringComparison.OrdinalIgnoreCase) ||
                   string.IsNullOrWhiteSpace(configured)
                ? "http://127.0.0.1:51880"
                : configured;
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
            ["backendUrl"] = ExistingBackendUrl(),
            ["browserAgentUrl"] = "http://127.0.0.1:48820",
            ["extensionBridgeUrl"] = "http://127.0.0.1:48830",
            ["aiProvider"] = ExistingString("aiProvider", "gemini"),
            ["openAiBaseUrl"] = ExistingString("openAiBaseUrl", "https://api.openai.com/v1"),
            ["openAiApiKey"] = ProtectExistingSecret(ExistingString("openAiApiKey", "")),
            ["openAiModel"] = ExistingString("openAiModel", "gpt-4.1-mini"),
            ["hostedApiUrl"] = ExistingString("hostedApiUrl", ""),
            ["hostedToken"] = ProtectExistingSecret(ExistingString("hostedToken", "")),
            ["ollamaUrl"] = ExistingString("ollamaUrl", "http://127.0.0.1:11434"),
            ["localModel"] = ExistingString("localModel", "granite3.2-vision:2b"),
            ["apiKey"] = ProtectExistingSecret(ExistingString("apiKey", "")),
            ["apiKeys"] = ProtectExistingSecret(ExistingString("apiKeys", "")),
            ["enableStreamingTranscription"] = ExistingValue("enableStreamingTranscription", false),
            ["enableAutoReplace"] = ExistingValue("enableAutoReplace", false),
            ["autoReplaceConfidence"] = ExistingValue("autoReplaceConfidence", 0.9),
            ["enableManagedBrowserFallback"] = ExistingValue("enableManagedBrowserFallback", false)
        };

        WriteRuntimeProfileBytes(JsonSerializer.SerializeToUtf8Bytes(profile, new JsonSerializerOptions { WriteIndented = true }));
        return RuntimeProfilePath;
    }

    private static void WriteRuntimeProfileBytes(byte[] bytes)
    {
        var path = RuntimeProfilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(staged, bytes);
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    private static string ProtectExistingSecret(string value)
    {
        const string prefix = "dpapi:v1:";
        if (value.Length == 0 || value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return value;
        }
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return prefix + Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
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

    private void RegisterStartup(string root)
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

        RemoveLegacyStartupShortcut();
    }

    private void RemoveLegacyStartupShortcut()
    {
        var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupDirectory))
        {
            return;
        }

        var shortcutPath = Path.Combine(startupDirectory, "Cursivis Companion.lnk");
        try
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
                Log("Removed the legacy Companion startup shortcut.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log($"The legacy Companion startup shortcut could not be removed: {ex.Message}");
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

    private void SetProgress(int value)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetProgress(value));
            return;
        }

        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Value = Math.Clamp(value, progressBar.Minimum, progressBar.Maximum);
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
