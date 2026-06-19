#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

public enum AppResolutionStatus
{
    Found,
    Ambiguous,
    NotFound,
}

public sealed class AppCandidate
{
    public string Name { get; init; } = "";
    public string LaunchTarget { get; init; } = "";
    public string Source { get; init; } = "";
    public double Score { get; init; }
    public bool Trusted { get; init; }
}

public sealed class AppResolution
{
    public AppResolutionStatus Status { get; init; }
    public AppCandidate Best { get; init; }
    public IReadOnlyList<AppCandidate> Candidates { get; init; } = Array.Empty<AppCandidate>();
    public string SearchSummary { get; init; } = "";
}

public sealed class AppResolverService
{
    private static readonly Dictionary<string, string> NaturalAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"] = "Codex",
        ["openai codex"] = "Codex",
        ["vs code"] = "Visual Studio Code",
        ["visual code"] = "Visual Studio Code",
        ["code editor"] = "Visual Studio Code",
        ["chrome"] = "Google Chrome",
        ["edge"] = "Microsoft Edge",
        ["brave"] = "Brave",
        ["firefox"] = "Mozilla Firefox",
        ["apple music"] = "Apple Music",
        ["terminal"] = "Terminal",
        ["windows terminal"] = "Terminal",
        ["spotify"] = "Spotify",
        ["discord"] = "Discord",
        ["figma"] = "Figma",
        ["notion"] = "Notion",
        ["camera"] = "Camera",
        ["windows camera"] = "Camera",
        ["game bar"] = "Game Bar",
        ["xbox game bar"] = "Game Bar",
    };

    private static readonly Dictionary<string, string[]> KnownExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Notepad"] = ["notepad.exe"],
        ["Calculator"] = ["calc.exe"],
        ["Paint"] = ["mspaint.exe"],
        ["File Explorer"] = ["explorer.exe"],
        ["Terminal"] = ["wt.exe", "WindowsTerminal.exe"],
        ["PowerShell"] = ["powershell.exe", "pwsh.exe"],
        ["Command Prompt"] = ["cmd.exe"],
        ["Task Manager"] = ["taskmgr.exe"],
        ["Snipping Tool"] = ["snippingtool.exe"],
        ["Visual Studio Code"] = ["code.exe", "Code.exe"],
        ["Google Chrome"] = ["chrome.exe"],
        ["Microsoft Edge"] = ["msedge.exe"],
        ["Brave"] = ["brave.exe"],
        ["Mozilla Firefox"] = ["firefox.exe"],
        ["Opera"] = ["opera.exe"],
        ["Vivaldi"] = ["vivaldi.exe"],
        ["Arc"] = ["Arc.exe"],
        ["Spotify"] = ["spotify.exe", "Spotify.exe"],
        ["Discord"] = ["discord.exe", "Discord.exe"],
        ["Slack"] = ["slack.exe", "Slack.exe"],
        ["Microsoft Teams"] = ["ms-teams.exe", "teams.exe"],
        ["Outlook"] = ["outlook.exe"],
        ["Figma"] = ["figma.exe", "Figma.exe"],
        ["Notion"] = ["notion.exe", "Notion.exe"],
        ["Apple Music"] = ["AppleMusic.exe", "iTunes.exe"],
        ["Codex"] = ["Codex.exe"],
    };

    private readonly LearnedMappingService _mappings;
    private readonly object _cacheLock = new();
    private IReadOnlyList<AppCandidate> _cached = Array.Empty<AppCandidate>();
    private DateTimeOffset _cacheTime;

    public AppResolverService(LearnedMappingService mappings)
    {
        _mappings = mappings;
    }

    public AppResolution Resolve(string query, bool refresh = false)
    {
        var cleanQuery = (query ?? "").Trim();
        if (cleanQuery.Length == 0)
            return new AppResolution { Status = AppResolutionStatus.NotFound };

        var learned = _mappings.FindExact(LearnedMappingKind.App, cleanQuery);
        if (learned != null && TryCandidateFromLearned(learned, out var learnedCandidate))
        {
            return new AppResolution
            {
                Status = AppResolutionStatus.Found,
                Best = learnedCandidate.WithScore(1),
                Candidates = [learnedCandidate.WithScore(1)],
                SearchSummary = "a user-approved learned app mapping",
            };
        }

        var candidates = Discover(refresh);
        var canonicalQuery = CanonicalizeNaturalName(cleanQuery);
        var ranked = candidates
            .Select(candidate => new AppCandidate
            {
                Name = candidate.Name,
                LaunchTarget = candidate.LaunchTarget,
                Source = candidate.Source,
                Trusted = candidate.Trusted,
                Score = Math.Max(
                    FuzzyMatcher.Score(cleanQuery, candidate.Name),
                    FuzzyMatcher.Score(canonicalQuery, candidate.Name)),
            })
            .Where(candidate => candidate.Score >= 0.42)
            .GroupBy(candidate => FuzzyMatcher.Normalize(candidate.Name), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => SourcePriority(candidate.Source))
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => SourcePriority(candidate.Source))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        if (ranked.Length == 0)
        {
            return new AppResolution
            {
                Status = AppResolutionStatus.NotFound,
                SearchSummary = SearchSummary(candidates),
            };
        }

        var best = ranked[0];
        var second = ranked.Length > 1 ? ranked[1] : null;
        var exactNaturalAlias = NaturalAliases.ContainsKey(cleanQuery)
            && FuzzyMatcher.Normalize(best.Name) == FuzzyMatcher.Normalize(canonicalQuery);
        var highConfidence = best.Trusted
            && (best.Score >= 0.86
                || exactNaturalAlias
                || (best.Score >= 0.78 && (second == null || best.Score - second.Score >= 0.1)));

        return new AppResolution
        {
            Status = highConfidence ? AppResolutionStatus.Found : AppResolutionStatus.Ambiguous,
            Best = best,
            Candidates = ranked,
            SearchSummary = SearchSummary(candidates),
        };
    }

    public IReadOnlyList<AppCandidate> Discover(bool refresh = false)
    {
        lock (_cacheLock)
        {
            if (!refresh && _cached.Count > 0 && DateTimeOffset.UtcNow - _cacheTime < TimeSpan.FromMinutes(10))
                return _cached;

            var found = new List<AppCandidate>();
            AddStartApps(found);
            AddStartMenuShortcuts(found);
            AddRegisteredAppPaths(found);
            AddKnownExecutables(found);

            _cached = found
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name)
                    && !string.IsNullOrWhiteSpace(candidate.LaunchTarget))
                .GroupBy(candidate => candidate.LaunchTarget, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(candidate => SourcePriority(candidate.Source)).First())
                .ToArray();
            _cacheTime = DateTimeOffset.UtcNow;
            return _cached;
        }
    }

    public static string CanonicalizeNaturalName(string query)
    {
        var clean = (query ?? "").Trim();
        if (NaturalAliases.TryGetValue(clean, out var exact))
            return exact;

        var normalized = FuzzyMatcher.Normalize(clean);
        var tokenCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (normalized.Length < 5 || tokenCount == 0)
            return clean;

        var typo = NaturalAliases
            .Select(pair => new
            {
                pair.Value,
                Alias = FuzzyMatcher.Normalize(pair.Key),
                Score = FuzzyMatcher.Score(normalized, pair.Key),
            })
            .Where(item => item.Alias.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == tokenCount)
            .Where(item => item.Score >= 0.66)
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        return typo?.Value ?? clean;
    }

    public bool TryLaunch(AppCandidate candidate, out string error)
    {
        error = "";
        if (candidate == null || !candidate.Trusted)
        {
            error = "The app candidate was not trusted.";
            return false;
        }

        try
        {
            if (candidate.LaunchTarget.StartsWith("appx:", StringComparison.OrdinalIgnoreCase))
            {
                var appId = candidate.LaunchTarget[5..];
                var info = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                info.ArgumentList.Add($@"shell:AppsFolder\{appId}");
                Process.Start(info);
                return true;
            }

            var path = candidate.LaunchTarget.StartsWith("path:", StringComparison.OrdinalIgnoreCase)
                ? candidate.LaunchTarget[5..]
                : candidate.LaunchTarget;
            if (!File.Exists(path))
            {
                error = "The discovered app is no longer installed.";
                return false;
            }
            if (!IsTrustedLaunchPath(path))
            {
                error = "The discovered executable is outside trusted application locations.";
                return false;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsTrustedLaunchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;
        var full = Path.GetFullPath(path);
        if (Path.GetExtension(full).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu))
                || IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        if (!Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var trustedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
        }.Where(root => !string.IsNullOrWhiteSpace(root));

        if (trustedRoots.Any(root => IsUnder(full, root)))
            return true;

        return false;
    }

    private static void AddStartApps(List<AppCandidate> found)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress");
            using var process = Process.Start(info);
            if (process == null)
                return;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(6000))
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return;
            }

            var json = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(json))
                return;
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray()
                : new[] { doc.RootElement.Clone() };
            foreach (var item in items)
            {
                if (!item.TryGetProperty("Name", out var name)
                    || !item.TryGetProperty("AppID", out var appId))
                    continue;
                var appName = name.GetString();
                var id = appId.GetString();
                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(id))
                    continue;
                found.Add(new AppCandidate
                {
                    Name = appName,
                    LaunchTarget = "appx:" + id,
                    Source = "StartApps",
                    Trusted = true,
                });
            }
        }
        catch (Exception ex)
        {
            LiveModeLog.Warning(ex, "Start app discovery failed");
        }
    }

    private static void AddStartMenuShortcuts(List<AppCandidate> found)
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        }.Where(Directory.Exists))
        {
            try
            {
                foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).Take(800))
                {
                    found.Add(new AppCandidate
                    {
                        Name = Path.GetFileNameWithoutExtension(shortcut),
                        LaunchTarget = "path:" + shortcut,
                        Source = "StartMenu",
                        Trusted = true,
                    });
                }
            }
            catch { }
        }
    }

    private static void AddRegisteredAppPaths(List<AppCandidate> found)
    {
        var executableNames = KnownExecutables
            .SelectMany(pair => pair.Value.Select(fileName => (fileName, appName: pair.Key)))
            .GroupBy(item => item.fileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().appName, StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appPaths = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                        writable: false);
                    if (appPaths == null)
                        continue;

                    foreach (var executableName in executableNames.Keys)
                    {
                        using var appKey = appPaths.OpenSubKey(executableName, writable: false);
                        var path = appKey?.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(path))
                            continue;
                        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                        if (File.Exists(path) && IsTrustedLaunchPath(path))
                            AddExecutableCandidate(found, executableNames[executableName], path, "AppPaths");
                    }
                }
                catch (Exception ex)
                {
                    LiveModeLog.Warning(ex, "Registered app-path discovery failed");
                }
            }
        }
    }

    private static void AddKnownExecutables(List<AppCandidate> found)
    {
        var executableNames = KnownExecutables
            .SelectMany(pair => pair.Value.Select(fileName => (fileName, appName: pair.Key)))
            .GroupBy(item => item.fileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().appName, StringComparer.OrdinalIgnoreCase);
        var alreadyDiscovered = found.Select(candidate => candidate.Name).ToArray();
        var pathRequiredApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Google Chrome", "Microsoft Edge", "Brave", "Mozilla Firefox", "Opera", "Vivaldi", "Arc",
        };
        var needed = executableNames
            .Where(pair => pathRequiredApps.Contains(pair.Value)
                || !alreadyDiscovered.Any(name => FuzzyMatcher.Score(pair.Value, name) >= 0.96))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (needed.Count == 0)
            return;

        foreach (var knownPath in KnownExecutablePaths())
        {
            if (!File.Exists(knownPath.Path) || !IsTrustedLaunchPath(knownPath.Path))
                continue;
            AddExecutableCandidate(found, knownPath.AppName, knownPath.Path);
            needed.Remove(Path.GetFileName(knownPath.Path));
        }

        var shallowRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"),
        }
        .Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(Directory.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
        foreach (var root in shallowRoots)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
                {
                    if (needed.TryGetValue(Path.GetFileName(file), out var appName) && IsTrustedLaunchPath(file))
                        AddExecutableCandidate(found, appName, file);
                }
            }
            catch { }
        }

    }

    private static void AddExecutableCandidate(
        List<AppCandidate> found,
        string appName,
        string path,
        string source = null)
    {
        found.Add(new AppCandidate
        {
            Name = appName,
            LaunchTarget = "path:" + path,
            Source = source
                ?? (IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                    ? "LocalAppData"
                    : IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                        ? "Windows"
                        : "ProgramFiles"),
            Trusted = true,
        });
    }

    private static IEnumerable<(string AppName, string Path)> KnownExecutablePaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return ("Google Chrome", Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
        yield return ("Google Chrome", Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"));
        yield return ("Google Chrome", Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
        yield return ("Microsoft Edge", Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
        yield return ("Microsoft Edge", Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
        yield return ("Brave", Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        yield return ("Brave", Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        yield return ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"));
        yield return ("Mozilla Firefox", Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"));
        yield return ("Mozilla Firefox", Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe"));
        yield return ("Opera", Path.Combine(local, "Programs", "Opera", "opera.exe"));
        yield return ("Opera", Path.Combine(local, "Programs", "Opera", "launcher.exe"));
        yield return ("Vivaldi", Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe"));
        yield return ("Arc", Path.Combine(local, "Programs", "Arc", "Arc.exe"));
        yield return ("Visual Studio Code", Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"));
        yield return ("Notion", Path.Combine(local, "Programs", "Notion", "Notion.exe"));
        yield return ("Figma", Path.Combine(local, "Figma", "Figma.exe"));
        yield return ("Codex", Path.Combine(local, "Programs", "Codex", "Codex.exe"));
        yield return ("Codex", Path.Combine(local, "Programs", "OpenAI Codex", "Codex.exe"));
    }

    private static bool TryCandidateFromLearned(LearnedMappingEntry entry, out AppCandidate candidate)
    {
        candidate = null;
        if (entry.Target.StartsWith("appx:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = new AppCandidate
            {
                Name = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Alias : entry.DisplayName,
                LaunchTarget = entry.Target,
                Source = "Learned",
                Trusted = true,
            };
            return true;
        }

        var path = entry.Target.StartsWith("path:", StringComparison.OrdinalIgnoreCase)
            ? entry.Target[5..]
            : entry.Target;
        if (!File.Exists(path) || !IsTrustedLaunchPath(path))
            return false;
        candidate = new AppCandidate
        {
            Name = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Alias : entry.DisplayName,
            LaunchTarget = "path:" + path,
            Source = "Learned",
            Trusted = true,
        };
        return true;
    }

    private static int SourcePriority(string source) => source switch
    {
        "Learned" => 6,
        "StartApps" => 5,
        "AppPaths" => 4,
        "StartMenu" => 3,
        "ProgramFiles" => 2,
        "LocalAppData" => 2,
        "Windows" => 1,
        _ => 0,
    };

    private static string SearchSummary(IReadOnlyList<AppCandidate> candidates)
    {
        var sources = candidates.Select(candidate => candidate.Source).Distinct().OrderBy(value => value);
        return $"Windows Start apps, Start menu shortcuts, registered App Paths, and trusted Windows/Program Files/LocalAppData locations ({string.Join(", ", sources)})";
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class AppCandidateExtensions
{
    public static AppCandidate WithScore(this AppCandidate candidate, double score) => new()
    {
        Name = candidate.Name,
        LaunchTarget = candidate.LaunchTarget,
        Source = candidate.Source,
        Trusted = candidate.Trusted,
        Score = score,
    };
}

