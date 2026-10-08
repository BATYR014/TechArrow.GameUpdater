using System.Text.Json;

namespace TechArrow.GameUpdater.Infrastructure.Services;

public sealed record RunHistoryEntry(Guid Id, DateTimeOffset Started, DateTimeOffset? Finished,
    string Kind, string Result, IReadOnlyList<string> Details);

public sealed class RunHistoryService
{
    private readonly object _gate = new();
    private readonly string _file;
    private List<RunHistoryEntry> _entries;
    public RunHistoryService(AppPaths paths)
    {
        _file = Path.Combine(paths.Root, "History", "runs.json");
        _entries = File.Exists(_file) ? JsonSerializer.Deserialize<List<RunHistoryEntry>>(File.ReadAllText(_file))
            ?? throw new InvalidDataException("История запусков пуста или повреждена.") : [];
        // An unfinished run from a previous process is interruption, never success.
        if (_entries.Any(entry => entry.Finished is null))
        {
            _entries = _entries.Select(entry => entry.Finished is null
                ? entry with { Finished = DateTimeOffset.UtcNow, Result = "Прервано: приложение завершилось" } : entry).ToList();
            Save();
        }
    }
    public IReadOnlyList<RunHistoryEntry> Snapshot { get { lock (_gate) return _entries.ToArray(); } }
    public Guid Begin(string kind)
    {
        lock (_gate)
        {
            var entry = new RunHistoryEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, null, kind, "Выполняется", []);
            _entries.Insert(0, entry);
            if (_entries.Count > 500) _entries.RemoveRange(500, _entries.Count - 500);
            Save(); return entry.Id;
        }
    }
    public void Add(Guid id, string detail)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0) return;
            var entry = _entries[index];
            _entries[index] = entry with { Details = entry.Details.Append(detail).TakeLast(200).ToArray() };
            Save();
        }
    }
    public void Finish(Guid id, string result)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == id);
            if (index < 0) return;
            _entries[index] = _entries[index] with { Finished = DateTimeOffset.UtcNow, Result = result };
            Save();
        }
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
            File.Move(temporary, _file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
