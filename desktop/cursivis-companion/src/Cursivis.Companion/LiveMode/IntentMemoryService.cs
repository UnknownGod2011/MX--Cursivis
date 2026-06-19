#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class IntentMemoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IntentMemorySnapshot
{
    public IReadOnlyList<IntentMemoryEntry> Entries { get; init; } = Array.Empty<IntentMemoryEntry>();
    public int Count { get; init; }
    public int MaxEntries { get; init; }
    public int MaxValueLength { get; init; }
}

public sealed class IntentMemoryService
{
    public const int MaxEntries = 20;
    public const int MaxKeyLength = 64;
    public const int MaxValueLength = 280;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly List<IntentMemoryEntry> _entries = new();
    private readonly string _appDataDir;

    public string MemoryPath { get; }

    public IntentMemoryService(string appDataDir = null)
    {
        _appDataDir = string.IsNullOrWhiteSpace(appDataDir)
            ? LiveModeSettingsService.AppDataDir
            : Path.GetFullPath(appDataDir);
        MemoryPath = Path.Combine(_appDataDir, "live-mode-intent-memory.json");
    }

    public void Load()
    {
        Directory.CreateDirectory(_appDataDir);
        if (!File.Exists(MemoryPath))
            return;

        try
        {
            var json = File.ReadAllText(MemoryPath);
            var entries = JsonSerializer.Deserialize<List<IntentMemoryEntry>>(json, JsonOptions) ?? new();
            lock (_lock)
            {
                _entries.Clear();
                _entries.AddRange(entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.Value))
                    .Select(NormalizeEntry)
                    .OrderByDescending(e => e.UpdatedAt)
                    .Take(MaxEntries));
            }
        }
        catch (Exception ex)
        {
            LiveModeLog.Warning(ex, "Intent memory load failed; starting empty");
        }
    }

    public IntentMemorySnapshot Snapshot()
    {
        lock (_lock)
        {
            return new IntentMemorySnapshot
            {
                Entries = _entries
                    .OrderByDescending(e => e.UpdatedAt)
                    .Select(Clone)
                    .ToArray(),
                Count = _entries.Count,
                MaxEntries = MaxEntries,
                MaxValueLength = MaxValueLength,
            };
        }
    }

    public IntentMemoryEntry Remember(string key, string value)
    {
        var cleanKey = CleanKey(key, value);
        var cleanValue = CleanValue(value);
        if (string.IsNullOrWhiteSpace(cleanValue))
            throw new InvalidOperationException("Nothing useful was provided to remember.");

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var existing = _entries.FirstOrDefault(e => string.Equals(e.Key, cleanKey, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Value = cleanValue;
                existing.UpdatedAt = now;
                SaveLocked();
                return Clone(existing);
            }

            var entry = new IntentMemoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Key = cleanKey,
                Value = cleanValue,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _entries.Add(entry);

            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries.OrderBy(e => e.UpdatedAt).First();
                _entries.Remove(oldest);
            }

            SaveLocked();
            return Clone(entry);
        }
    }

    public bool Forget(string idOrKey)
    {
        var needle = (idOrKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(needle))
            return false;

        lock (_lock)
        {
            var removed = _entries.RemoveAll(e =>
                string.Equals(e.Id, needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Key, needle, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                SaveLocked();
            return removed;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            SaveLocked();
        }
    }

    public IReadOnlyList<IntentMemoryEntry> Search(string query, int maxResults = 5)
    {
        var needle = (query ?? "").Trim();
        lock (_lock)
        {
            IEnumerable<IntentMemoryEntry> matches = _entries;
            if (!string.IsNullOrWhiteSpace(needle))
            {
                matches = matches.Where(e =>
                    e.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || e.Value.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            return matches
                .OrderByDescending(e => e.UpdatedAt)
                .Take(Math.Clamp(maxResults, 1, MaxEntries))
                .Select(Clone)
                .ToArray();
        }
    }

    public string BuildPromptDigest(int maxEntries = 10, int maxChars = 900)
    {
        var lines = Search("", maxEntries)
            .Select(e => $"- {e.Key}: {e.Value}")
            .ToList();
        if (lines.Count == 0)
            return "No saved intent memory.";

        var text = string.Join(Environment.NewLine, lines);
        return text.Length <= maxChars ? text : text[..maxChars] + "...";
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(_appDataDir);
        var ordered = _entries.OrderByDescending(e => e.UpdatedAt).ToArray();
        File.WriteAllText(MemoryPath, JsonSerializer.Serialize(ordered, JsonOptions));
    }

    private static IntentMemoryEntry NormalizeEntry(IntentMemoryEntry entry)
    {
        entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id.Trim();
        entry.Key = CleanKey(entry.Key, entry.Value);
        entry.Value = CleanValue(entry.Value);
        if (entry.CreatedAt == default)
            entry.CreatedAt = DateTimeOffset.UtcNow;
        if (entry.UpdatedAt == default)
            entry.UpdatedAt = entry.CreatedAt;
        return entry;
    }

    private static string CleanKey(string key, string value)
    {
        var clean = CleanInline(key);
        if (string.IsNullOrWhiteSpace(clean))
            clean = CleanInline(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6).Aggregate("", (a, b) => string.IsNullOrEmpty(a) ? b : a + " " + b);
        if (string.IsNullOrWhiteSpace(clean))
            clean = "memory";
        return clean.Length <= MaxKeyLength ? clean : clean[..MaxKeyLength].Trim();
    }

    private static string CleanValue(string value)
    {
        var clean = CleanInline(value);
        return clean.Length <= MaxValueLength ? clean : clean[..MaxValueLength].Trim();
    }

    private static string CleanInline(string value) =>
        string.Join(" ", (value ?? "")
            .ReplaceLineEndings(" ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static IntentMemoryEntry Clone(IntentMemoryEntry entry) => new()
    {
        Id = entry.Id,
        Key = entry.Key,
        Value = entry.Value,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
    };
}
