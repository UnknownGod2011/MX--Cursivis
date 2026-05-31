using System.Text.Json;
using System.IO;

namespace Cursivis.Companion.Services;

public sealed class IntentMemoryService
{
    private readonly string _storePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public IntentMemoryService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cursivis");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "intent-memory.json");
    }

    public async Task RecordAsync(string contentType, string action)
    {
        await _syncLock.WaitAsync();
        try
        {
            var memory = await LoadInternalAsync();
            var existing = memory.Entries.FirstOrDefault(e =>
                string.Equals(e.ContentType, contentType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Action, action, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                memory.Entries.Add(new IntentEntry
                {
                    ContentType = contentType,
                    Action = action,
                    Count = 1,
                    LastUsedUtc = DateTime.UtcNow.ToString("O")
                });
            }
            else
            {
                existing.Count += 1;
                existing.LastUsedUtc = DateTime.UtcNow.ToString("O");
            }

            memory.UpdatedUtc = DateTime.UtcNow.ToString("O");
            var json = JsonSerializer.Serialize(memory, _jsonOptions);
            await File.WriteAllTextAsync(_storePath, json);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> RankActionsAsync(string contentType, IEnumerable<string> actions)
    {
        await _syncLock.WaitAsync();
        try
        {
            var memory = await LoadInternalAsync();
            var lookup = memory.Entries
                .Where(e => string.Equals(e.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(e => e.Action, e => e.Count, StringComparer.OrdinalIgnoreCase);
            var orderedInput = actions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var inputIndex = orderedInput
                .Select((action, index) => new { action, index })
                .ToDictionary(x => x.action, x => x.index, StringComparer.OrdinalIgnoreCase);

            return orderedInput
                .OrderByDescending(a => lookup.TryGetValue(a, out var count) ? count : 0)
                .ThenBy(a => inputIndex[a])
                .ToList();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<IntentMemoryStore> LoadInternalAsync()
    {
        if (!File.Exists(_storePath))
        {
            return new IntentMemoryStore();
        }

        var json = await File.ReadAllTextAsync(_storePath);
        return JsonSerializer.Deserialize<IntentMemoryStore>(json, _jsonOptions) ?? new IntentMemoryStore();
    }

    private sealed class IntentMemoryStore
    {
        public string Version { get; set; } = "1.0.0";

        public string UpdatedUtc { get; set; } = DateTime.UtcNow.ToString("O");

        public List<IntentEntry> Entries { get; set; } = [];
    }

    private sealed class IntentEntry
    {
        public string ContentType { get; set; } = "text";

        public string Action { get; set; } = "summarize";

        public int Count { get; set; }

        public string LastUsedUtc { get; set; } = DateTime.UtcNow.ToString("O");
    }
}
