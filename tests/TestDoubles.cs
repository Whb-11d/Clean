using System.Collections.Immutable;
using System.IO;
using Clean.Core;

namespace Clean.Testing;

public sealed class FakeFiles : IFileSystem
{
    public Dictionary<string, FileSnapshot> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileEntry[]> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Executed { get; } = [];
    public List<CleanupMode> ExecutedModes { get; } = [];
    public Action<string>? OnRead { get; set; }
    public Action? OnExecute { get; set; }
    public ActionOutcome Outcome { get; set; } = new(ItemStatus.Succeeded, ErrorKind.None, "done");
    public void ValidateRoot(string path) { }
    public IEnumerable<FileEntry> Enumerate(string directory) => Directories.GetValueOrDefault(directory, []);
    public FileSnapshot ReadSnapshot(string path)
    {
        OnRead?.Invoke(path);
        return Files.TryGetValue(path, out var file) ? file : throw new FileNotFoundException();
    }
    public ActionOutcome ExecuteVerified(FileSnapshot expected, CleanupMode mode, CancellationToken cancellationToken)
    { Executed.Add(expected.Path); ExecutedModes.Add(mode); OnExecute?.Invoke(); return Outcome; }
    public ScanCandidate Add(string path, int days = 10, long bytes = 100, ScanRule? rule = null)
    {
        var file = new FileSnapshot(path, bytes, DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(-days),
            FileAttributes.Archive, Guid.NewGuid().ToString(), "ancestor", 1);
        Files[path] = file;
        var parent = Path.GetDirectoryName(path)!;
        Directories[parent] = Directories.GetValueOrDefault(parent, []).Append(new FileEntry(path, false, false)).ToArray();
        return new(Guid.NewGuid(), file, rule ?? ScanRule.Temporary(@"C:\Test"));
    }
}
public sealed class FakeHistory : ICleanupHistoryStore
{
    public List<HistoryEntry> Entries { get; } = [];
    public int? FailAfter { get; set; }
    public ImmutableArray<HistoryEntry> Load() => Entries.ToImmutableArray();
    public void Save(HistoryEntry entry)
    {
        if (FailAfter.HasValue && Entries.Count >= FailAfter.Value) throw new IOException("history full");
        Entries.Add(entry);
    }
    public void Clear() => Entries.Clear();
}
public sealed class FakeMetrics : IDiskInfoProvider, IMemoryInfoProvider
{
    public ImmutableArray<DiskSnapshot> GetDisks() => [new("C:\\", 10000, 3000, null)];
    public MemorySnapshot Sample(bool includeProcesses) => new(DateTimeOffset.UtcNow, 1000, 400, [], 0);
}
public sealed class FakeSettings : ISettingsStore
{
    public AppSettings Value { get; private set; } = new();
    public SettingsLoadResult Load() => new(Value, null);
    public void Save(AppSettings settings) => Value = settings;
}
public sealed class FakeLog : IDiagnosticLog
{
    public void Write(string eventName, string? errorCategory = null) { }
    public void Clear() { }
}
public sealed class FakeBin : IRecycleBinService
{
    public int EmptyCalls { get; private set; }
    public RecycleBinSnapshot Query() => new(0, 0);
    public EmptyBinResult Empty() { EmptyCalls++; return new(true, false, Query(), Query(), "done"); }
}
