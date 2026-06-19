#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public enum LearnedMappingKind
{
    App,
    File,
    Folder,
    Browser,
    Workflow,
    Link,
}

public sealed class LearnedMappingEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LearnedMappingKind Kind { get; set; }
    public string Alias { get; set; } = "";
    public string Target { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LearnedMappingService
{
    public const int MaxEntries = 80;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly List<LearnedMappingEntry> _entries = new();

    public LearnedMappingService(string path = null)
    {
        MappingPath = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(LiveModeSettingsService.AppDataDir, "learned-mappings.json")
            : path;
    }

    public string MappingPath { get; }

    public void Load()
    {
        lock (_lock)
        {
            _entries.Clear();
            if (!File.Exists(MappingPath))
                return;

            try
            {
                var entries = JsonSerializer.Deserialize<List<LearnedMappingEntry>>(
                    File.ReadAllText(MappingPath),
                    JsonOptions) ?? new();
                _entries.AddRange(entries
                    .Where(IsValid)
                    .OrderByDescending(entry => entry.UpdatedAt)
                    .Take(MaxEntries)
                    .Select(Clone));
            }
            catch (Exception ex)
            {
                LiveModeLog.Warning(ex, "Learned mappings load failed; starting empty");
            }
        }
    }

    public IReadOnlyList<LearnedMappingEntry> Snapshot(LearnedMappingKind? kind = null)
    {
        lock (_lock)
        {
            return _entries
                .Where(entry => kind == null || entry.Kind == kind)
                .OrderByDescending(entry => entry.UpdatedAt)
                .Select(Clone)
                .ToArray();
        }
    }

    public LearnedMappingEntry Remember(
        LearnedMappingKind kind,
        string alias,
        string target,
        string displayName = null)
    {
        var cleanAlias = Clean(alias, 80);
        var cleanTarget = CleanTarget(target);
        if (string.IsNullOrWhiteSpace(cleanAlias))
            throw new InvalidOperationException("A learned mapping needs an alias.");
        if (string.IsNullOrWhiteSpace(cleanTarget))
            throw new InvalidOperationException("A learned mapping needs a target.");
        ValidateTarget(kind, cleanTarget);

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var existing = _entries.FirstOrDefault(entry =>
                entry.Kind == kind
                && string.Equals(entry.Alias, cleanAlias, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new LearnedMappingEntry
                {
                    Kind = kind,
                    Alias = cleanAlias,
                    CreatedAt = now,
                };
                _entries.Add(existing);
            }

            existing.Target = cleanTarget;
            existing.DisplayName = Clean(displayName, 120);
            existing.UpdatedAt = now;
            TrimLocked();
            SaveLocked();
            return Clone(existing);
        }
    }

    public LearnedMappingEntry FindExact(LearnedMappingKind kind, string alias)
    {
        var normalized = FuzzyMatcher.Normalize(alias);
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(candidate =>
                candidate.Kind == kind
                && FuzzyMatcher.Normalize(candidate.Alias) == normalized);
            return entry == null ? null : Clone(entry);
        }
    }

    public IReadOnlyList<LearnedMappingEntry> Search(string query, LearnedMappingKind? kind = null, int limit = 8)
    {
        lock (_lock)
        {
            return _entries
                .Where(entry => kind == null || entry.Kind == kind)
                .Select(entry => new
                {
                    Entry = entry,
                    Score = Math.Max(
                        FuzzyMatcher.Score(query, entry.Alias),
                        FuzzyMatcher.Score(query, entry.DisplayName)),
                })
                .Where(item => string.IsNullOrWhiteSpace(query) || item.Score >= 0.45)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Entry.UpdatedAt)
                .Take(Math.Clamp(limit, 1, 20))
                .Select(item => Clone(item.Entry))
                .ToArray();
        }
    }

    public bool Forget(string idOrAlias, LearnedMappingKind? kind = null)
    {
        var needle = (idOrAlias ?? "").Trim();
        if (needle.Length == 0)
            return false;

        lock (_lock)
        {
            var removed = _entries.RemoveAll(entry =>
                (kind == null || entry.Kind == kind)
                && (string.Equals(entry.Id, needle, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Alias, needle, StringComparison.OrdinalIgnoreCase))) > 0;
            if (removed)
                SaveLocked();
            return removed;
        }
    }

    public int Clear(LearnedMappingKind? kind = null)
    {
        lock (_lock)
        {
            var removed = kind == null
                ? _entries.Count
                : _entries.Count(entry => entry.Kind == kind);
            if (kind == null)
                _entries.Clear();
            else
                _entries.RemoveAll(entry => entry.Kind == kind);
            SaveLocked();
            return removed;
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MappingPath)!);
        var json = JsonSerializer.Serialize(
            _entries.OrderByDescending(entry => entry.UpdatedAt).ToArray(),
            JsonOptions);
        var temporaryPath = MappingPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, MappingPath, overwrite: true);
    }

    private void TrimLocked()
    {
        while (_entries.Count > MaxEntries)
        {
            var oldest = _entries.OrderBy(entry => entry.UpdatedAt).First();
            _entries.Remove(oldest);
        }
    }

    private static void ValidateTarget(LearnedMappingKind kind, string target)
    {
        if (kind is LearnedMappingKind.File or LearnedMappingKind.Folder)
        {
            if (!Path.IsPathFullyQualified(target))
                throw new InvalidOperationException("File and folder mappings require an absolute local path.");
        }
        else if (kind == LearnedMappingKind.Link
            && (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException("Link mappings require an http or https URL.");
        }
    }

    private static bool IsValid(LearnedMappingEntry entry) =>
        entry != null
        && !string.IsNullOrWhiteSpace(entry.Alias)
        && !string.IsNullOrWhiteSpace(entry.Target);

    private static string Clean(string value, int maxLength)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    private static string CleanTarget(string value)
    {
        var target = (value ?? "").Trim().Trim('"');
        return target.Length <= 2048 ? target : target[..2048];
    }

    private static LearnedMappingEntry Clone(LearnedMappingEntry entry) => new()
    {
        Id = entry.Id,
        Kind = entry.Kind,
        Alias = entry.Alias,
        Target = entry.Target,
        DisplayName = entry.DisplayName,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
    };
}

