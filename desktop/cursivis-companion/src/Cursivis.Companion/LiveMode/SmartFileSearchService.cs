#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.Diagnostics;
using System.IO;

public enum FileResolutionStatus
{
    Found,
    Ambiguous,
    NotFound,
    Cancelled,
}

public sealed class FileSearchCandidate
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public double Score { get; init; }
    public string RootLabel { get; init; } = "";
}

public sealed class FileSearchResult
{
    public FileResolutionStatus Status { get; init; }
    public FileSearchCandidate Best { get; init; }
    public IReadOnlyList<FileSearchCandidate> Candidates { get; init; } = Array.Empty<FileSearchCandidate>();
    public int ScannedEntries { get; init; }
    public bool TimedOut { get; init; }
    public string SearchSummary { get; init; } = "";
}

public sealed class SmartFileSearchService
{
    public const int DefaultMaxDepth = 7;
    public const int DefaultMaxResults = 12;
    public const int DefaultMaxScannedEntries = 50000;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(7);

    private static readonly HashSet<string> RiskyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".msi", ".msix", ".appx",
        ".scr", ".reg", ".lnk", ".url", ".hta", ".js", ".jse", ".vbs", ".vbe", ".wsf",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v",
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".txt", ".md", ".rtf", ".ppt", ".pptx", ".xls", ".xlsx",
    };

    private readonly LearnedMappingService _mappings;
    private readonly IReadOnlyList<SearchRoot> _rootOverrides;

    public SmartFileSearchService(
        LearnedMappingService mappings,
        IReadOnlyList<(string Path, string Label, int Priority)> roots = null)
    {
        _mappings = mappings;
        _rootOverrides = roots?.Select(root => new SearchRoot(root.Path, root.Label, root.Priority)).ToArray();
    }

    public async Task<FileSearchResult> SearchAsync(
        string query,
        string location,
        bool includeFiles,
        bool includeFolders,
        CancellationToken token,
        int maxDepth = DefaultMaxDepth,
        int maxResults = DefaultMaxResults,
        TimeSpan? timeout = null)
    {
        var cleanQuery = CleanQuery(query);
        if (cleanQuery.Length == 0)
            return new FileSearchResult { Status = FileResolutionStatus.NotFound };

        var learnedKind = includeFolders && !includeFiles
            ? LearnedMappingKind.Folder
            : includeFiles && !includeFolders
                ? LearnedMappingKind.File
                : (LearnedMappingKind?)null;
        if (learnedKind != null)
        {
            var learned = _mappings.FindExact(learnedKind.Value, cleanQuery);
            if (learned != null && ExistsForKind(learned))
            {
                var candidate = CandidateFromPath(learned.Target, "Learned", 1);
                return new FileSearchResult
                {
                    Status = FileResolutionStatus.Found,
                    Best = candidate,
                    Candidates = [candidate],
                    SearchSummary = "a user-approved learned mapping",
                };
            }
        }
        else
        {
            foreach (var kind in new[] { LearnedMappingKind.File, LearnedMappingKind.Folder })
            {
                var learned = _mappings.FindExact(kind, cleanQuery);
                if (learned != null && ExistsForKind(learned))
                {
                    var candidate = CandidateFromPath(learned.Target, "Learned", 1);
                    return new FileSearchResult
                    {
                        Status = FileResolutionStatus.Found,
                        Best = candidate,
                        Candidates = [candidate],
                        SearchSummary = "a user-approved learned mapping",
                    };
                }
            }
        }

        var learnedMatches = new[] { LearnedMappingKind.File, LearnedMappingKind.Folder }
            .Where(kind => (kind == LearnedMappingKind.File && includeFiles)
                || (kind == LearnedMappingKind.Folder && includeFolders))
            .SelectMany(kind => _mappings.Search(cleanQuery, kind, 4))
            .Where(ExistsForKind)
            .Select(entry => new
            {
                Entry = entry,
                Score = Math.Max(
                    FuzzyMatcher.Score(cleanQuery, entry.Alias),
                    FuzzyMatcher.Score(cleanQuery, entry.DisplayName)),
            })
            .OrderByDescending(item => item.Score)
            .ToArray();
        if (learnedMatches.Length > 0)
        {
            var bestLearned = learnedMatches[0];
            var secondScore = learnedMatches.Length > 1 ? learnedMatches[1].Score : 0;
            var candidate = CandidateFromPath(bestLearned.Entry.Target, "Learned", bestLearned.Score);
            if (bestLearned.Score >= 0.90
                || (bestLearned.Score >= 0.68
                    && (learnedMatches.Length == 1 || bestLearned.Score - secondScore >= 0.08)))
            {
                return new FileSearchResult
                {
                    Status = FileResolutionStatus.Found,
                    Best = candidate,
                    Candidates = [candidate],
                    SearchSummary = "a fuzzy user-approved learned mapping",
                };
            }
        }

        var known = ResolveKnownFolder(cleanQuery);
        if (known != null && includeFolders)
        {
            var candidate = CandidateFromPath(known.Value.Path, known.Value.Label, 1);
            return new FileSearchResult
            {
                Status = FileResolutionStatus.Found,
                Best = candidate,
                Candidates = [candidate],
                SearchSummary = "known Windows user folders",
            };
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            return await Task.Run(
                () => SearchCore(
                    cleanQuery,
                    location,
                    includeFiles,
                    includeFolders,
                    Math.Clamp(maxDepth, 1, 10),
                    Math.Clamp(maxResults, 1, 25),
                    timeoutCts.Token),
                timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new FileSearchResult
            {
                Status = FileResolutionStatus.NotFound,
                TimedOut = true,
                SearchSummary = "common user folders with a bounded five-second search",
            };
        }
        catch (OperationCanceledException)
        {
            return new FileSearchResult { Status = FileResolutionStatus.Cancelled };
        }
    }

    public static bool IsRiskyFile(string path) =>
        File.Exists(path) && RiskyExtensions.Contains(Path.GetExtension(path));

    public static bool TryOpenSafe(string path, out string error)
    {
        error = "";
        var expanded = Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));
        if (!File.Exists(expanded) && !Directory.Exists(expanded))
        {
            error = "That file or folder no longer exists.";
            return false;
        }
        if (IsRiskyFile(expanded))
        {
            error = "Executable, script, installer, shortcut, and registry files are blocked from automatic opening.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = expanded, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public IReadOnlyList<string> SearchRootPaths() => BuildRoots("").Select(root => root.Path).ToArray();

    private FileSearchResult SearchCore(
        string query,
        string location,
        bool includeFiles,
        bool includeFolders,
        int maxDepth,
        int maxResults,
        CancellationToken token)
    {
        var roots = BuildRoots(location);
        var candidates = new List<FileSearchCandidate>();
        var scanned = 0;
        var wantsLatest = query.Contains("latest", StringComparison.OrdinalIgnoreCase)
            || query.Contains("recent", StringComparison.OrdinalIgnoreCase)
            || query.Contains("last ", StringComparison.OrdinalIgnoreCase);
        var typeHint = DetectTypeHint(query);
        var queue = new Queue<(SearchRoot Root, string Path, int Depth)>();
        foreach (var root in roots.OrderByDescending(root => root.Priority))
        {
            if (Directory.Exists(root.Path))
                queue.Enqueue((root, root.Path, 0));
        }

        while (queue.Count > 0 && scanned < DefaultMaxScannedEntries)
        {
            token.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current.Path);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                scanned++;
                if (scanned >= DefaultMaxScannedEntries)
                    break;

                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch { continue; }
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                if (attributes.HasFlag(FileAttributes.System)
                    || attributes.HasFlag(FileAttributes.ReparsePoint)
                    || ShouldSkip(entry, isDirectory))
                    continue;

                if ((isDirectory && includeFolders) || (!isDirectory && includeFiles))
                {
                    var score = ScoreEntry(query, entry, isDirectory, current.Root.Priority, typeHint, wantsLatest);
                    if (score >= 0.42)
                    {
                        candidates.Add(new FileSearchCandidate
                        {
                            Path = entry,
                            Name = Path.GetFileName(entry),
                            IsDirectory = isDirectory,
                            ModifiedAt = GetModifiedAt(entry, isDirectory),
                            Score = score,
                            RootLabel = current.Root.Label,
                        });
                    }
                }

                if (isDirectory && current.Depth < maxDepth)
                    queue.Enqueue((current.Root, entry, current.Depth + 1));
            }
        }

        var ranked = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.ModifiedAt)
            .Take(maxResults)
            .ToArray();
        if (ranked.Length == 0)
        {
            return new FileSearchResult
            {
                Status = FileResolutionStatus.NotFound,
                ScannedEntries = scanned,
                SearchSummary = RootSummary(roots),
            };
        }

        var best = ranked[0];
        var second = ranked.Length > 1 ? ranked[1] : null;
        var highConfidence = best.Score >= 0.88
            || (best.Score >= 0.79 && (second == null || best.Score - second.Score >= 0.11));
        return new FileSearchResult
        {
            Status = highConfidence ? FileResolutionStatus.Found : FileResolutionStatus.Ambiguous,
            Best = best,
            Candidates = ranked,
            ScannedEntries = scanned,
            SearchSummary = RootSummary(roots),
        };
    }

    private IReadOnlyList<SearchRoot> BuildRoots(string location)
    {
        if (_rootOverrides != null)
            return _rootOverrides.Where(root => Directory.Exists(root.Path)).ToArray();

        var explicitPath = Environment.ExpandEnvironmentVariables((location ?? "").Trim().Trim('"'));
        if (Directory.Exists(explicitPath))
            return [new SearchRoot(explicitPath, Path.GetFileName(explicitPath), 10)];

        var normalizedLocation = FuzzyMatcher.Normalize(location);
        var known = ResolveKnownFolder(normalizedLocation);
        if (known != null)
            return [new SearchRoot(known.Value.Path, known.Value.Label, 10)];

        var roots = new List<SearchRoot>
        {
            new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop", 10),
            new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), "Downloads", 9),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents", 8),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Pictures", 8),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos", 7),
            new(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Music", 6),
        };
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        roots.AddRange(new[]
        {
            new SearchRoot(Path.Combine(pictures, "Screenshots"), "Windows Screenshots", 10),
            new SearchRoot(Path.Combine(pictures, "Cursivis", "Screenshots"), "Cursivis Screenshots", 10),
            new SearchRoot(Path.Combine(pictures, "Cursivis", "Camera"), "Cursivis Camera", 10),
            new SearchRoot(Path.Combine(videos, "Captures"), "Game Bar Captures", 10),
            new SearchRoot(Path.Combine(videos, "Screen Recordings"), "Screen Recordings", 10),
        });
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive))
            roots.Add(new SearchRoot(oneDrive, "OneDrive", 7));
        foreach (var mapping in _mappings.Snapshot(LearnedMappingKind.Folder))
        {
            if (Directory.Exists(mapping.Target))
                roots.Add(new SearchRoot(mapping.Target, mapping.Alias, 9));
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root.Path) && Directory.Exists(root.Path))
            .DistinctBy(root => Path.GetFullPath(root.Path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static (string Path, string Label)? ResolveKnownFolder(string query)
    {
        var normalized = FuzzyMatcher.Normalize(query)
            .Replace("my ", "", StringComparison.Ordinal)
            .Replace(" folder", "", StringComparison.Ordinal);
        var path = normalized switch
        {
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "downloads" or "download" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "documents" or "document" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pictures" or "photos" or "images" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "videos" or "video" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "onedrive" or "one drive" => Environment.GetEnvironmentVariable("OneDrive"),
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? (path, normalized)
            : null;
    }

    private static double ScoreEntry(
        string query,
        string path,
        bool isDirectory,
        int rootPriority,
        string typeHint,
        bool wantsLatest)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var fuzzy = Math.Max(FuzzyMatcher.Score(query, name), FuzzyMatcher.Score(query, Path.GetFileName(path)));
        var modified = GetModifiedAt(path, isDirectory);
        var ageDays = Math.Max(0, (DateTimeOffset.Now - modified).TotalDays);
        var recency = Math.Exp(-ageDays / 45);
        var type = TypeScore(path, isDirectory, typeHint);
        var root = Math.Clamp(rootPriority / 10.0, 0, 1);
        var exactPartial = FuzzyMatcher.Normalize(name).Contains(FuzzyMatcher.Normalize(RemoveSearchWords(query)), StringComparison.Ordinal)
            ? 1
            : 0;

        var score = fuzzy * 0.62 + type * 0.12 + root * 0.08 + exactPartial * 0.08;
        score += recency * (wantsLatest ? 0.25 : 0.1);
        return Math.Clamp(score, 0, 1);
    }

    private static double TypeScore(string path, bool isDirectory, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return 0.5;
        if (hint == "folder")
            return isDirectory ? 1 : 0;
        if (isDirectory)
            return 0;
        var extension = Path.GetExtension(path);
        return hint switch
        {
            "image" => ImageExtensions.Contains(extension) ? 1 : 0,
            "video" => VideoExtensions.Contains(extension) ? 1 : 0,
            "document" => DocumentExtensions.Contains(extension) ? 1 : 0,
            "installer" => extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".msi", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            _ => 0.5,
        };
    }

    private static string DetectTypeHint(string query)
    {
        var normalized = FuzzyMatcher.Normalize(query);
        if (new[] { "folder", "directory" }.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
            return "folder";
        if (new[] { "image", "photo", "picture", "screenshot", "thumbnail" }.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
            return "image";
        if (new[] { "video", "movie", "recording", "clip" }.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
            return "video";
        if (new[] { "document", "pdf", "resume", "presentation", "spreadsheet" }.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
            return "document";
        if (new[] { "installer", "setup" }.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
            return "installer";
        return "";
    }

    private static string RemoveSearchWords(string query)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal)
        {
            "open", "find", "search", "for", "the", "my", "latest", "recent", "last", "file",
            "folder", "image", "photo", "picture", "video", "document", "inside", "from",
            "downloaded", "used", "recently", "called", "named",
        };
        return string.Join(" ", FuzzyMatcher.Normalize(query)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !ignored.Contains(word)));
    }

    private static string CleanQuery(string query) =>
        string.Join(" ", (query ?? "").ReplaceLineEndings(" ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool ShouldSkip(string path, bool isDirectory)
    {
        if (!isDirectory)
            return false;
        var name = Path.GetFileName(path);
        return name.StartsWith("$", StringComparison.Ordinal)
            || name is "Windows" or "Program Files" or "Program Files (x86)" or "ProgramData"
                or "AppData" or "System Volume Information" or "Recovery" or ".git"
                or "node_modules" or "bin" or "obj";
    }

    private static bool ExistsForKind(LearnedMappingEntry entry) =>
        entry.Kind == LearnedMappingKind.Folder
            ? Directory.Exists(entry.Target)
            : File.Exists(entry.Target);

    private static FileSearchCandidate CandidateFromPath(string path, string rootLabel, double score)
    {
        var isDirectory = Directory.Exists(path);
        return new FileSearchCandidate
        {
            Path = path,
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
            IsDirectory = isDirectory,
            ModifiedAt = GetModifiedAt(path, isDirectory),
            Score = score,
            RootLabel = rootLabel,
        };
    }

    private static DateTimeOffset GetModifiedAt(string path, bool isDirectory)
    {
        try
        {
            return isDirectory
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string RootSummary(IReadOnlyList<SearchRoot> roots) =>
        string.Join(", ", roots.Select(root => root.Label).Distinct(StringComparer.OrdinalIgnoreCase));

    private sealed record SearchRoot(string Path, string Label, int Priority);
}

