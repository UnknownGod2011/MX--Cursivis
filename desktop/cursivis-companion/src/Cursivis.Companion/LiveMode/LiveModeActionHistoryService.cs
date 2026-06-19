#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.IO;
using System.Text.Json;

public sealed class LiveModeActionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Success { get; set; }
    public bool ConfirmationRequired { get; set; }
}

public sealed class LiveModeActionHistoryService
{
    public const int MaxEntries = 50;
    private readonly object _lock = new();
    private readonly List<LiveModeActionEntry> _entries = new();
    private readonly string _appDataDir;
    private readonly string _path;

    public LiveModeActionHistoryService(string appDataDir = null)
    {
        _appDataDir = string.IsNullOrWhiteSpace(appDataDir)
            ? LiveModeSettingsService.AppDataDir
            : Path.GetFullPath(appDataDir);
        _path = Path.Combine(_appDataDir, "live-mode-action-history.json");
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var entries = JsonSerializer.Deserialize<List<LiveModeActionEntry>>(File.ReadAllText(_path)) ?? new();
            lock (_lock)
            {
                _entries.Clear();
                _entries.AddRange(entries.OrderByDescending(e => e.Timestamp).Take(MaxEntries));
            }
        }
        catch (Exception ex)
        {
            LiveModeLog.Warning(ex, "Live Mode action history load failed");
        }
    }

    public void Add(string action, string detail, bool success, bool confirmationRequired = false)
    {
        lock (_lock)
        {
            _entries.Insert(0, new LiveModeActionEntry
            {
                Action = action ?? "",
                Detail = Shorten(detail, 500),
                Success = success,
                ConfirmationRequired = confirmationRequired,
            });
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            SaveLocked();
        }
    }

    public IReadOnlyList<LiveModeActionEntry> Snapshot()
    {
        lock (_lock)
            return _entries.Select(Clone).ToArray();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(_appDataDir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LiveModeLog.Warning(ex, "Live Mode action history save failed");
        }
    }

    private static LiveModeActionEntry Clone(LiveModeActionEntry entry) => new()
    {
        Id = entry.Id,
        Timestamp = entry.Timestamp,
        Action = entry.Action,
        Detail = entry.Detail,
        Success = entry.Success,
        ConfirmationRequired = entry.ConfirmationRequired,
    };

    private static string Shorten(string value, int maxLength)
    {
        var text = (value ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
