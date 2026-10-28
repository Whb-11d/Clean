using System.Collections.Immutable;
using System.Text.Json;
using Clean.Core;

namespace Clean.Windows;

internal sealed class AtomicJson<T>(string path, Func<T, bool> validate) where T : class
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public T? Read(string? suffix = null)
    {
        try
        {
            var file = path + suffix;
            if (!File.Exists(file) || new FileInfo(file).Length > 2 * 1024 * 1024) return null;
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options);
            return value is not null && validate(value) ? value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    public bool Exists => File.Exists(path);
    public void Write(T value)
    {
        if (!validate(value)) throw new ArgumentException("配置值无效。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value, Options); stream.Flush(true); }
            if (File.Exists(path) && Read() is not null) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Clear()
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
    }
}

public sealed class JsonSettingsStore(string directory) : ISettingsStore
{
    private readonly AtomicJson<AppSettings> _file = new(Path.Combine(directory, "settings.json"), x => x.IsValid);
    private readonly object _gate = new();
    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            var settings = _file.Read();
            if (settings is not null) return new(settings, null);
            var backup = _file.Read(".bak");
            return new(backup ?? new(), backup is not null ? "配置不可读，已使用上次有效备份。保存后修复。" :
                _file.Exists ? "配置损坏或不可读，已恢复默认值。" : null);
        }
    }
    public void Save(AppSettings settings) { lock (_gate) _file.Write(settings); }
}
public sealed class JsonHistoryStore(string directory) : ICleanupHistoryStore
{
    private readonly AtomicJson<HistoryEntry[]> _file = new(Path.Combine(directory, "history.json"),
        x => x.Length <= 50 && x.All(e => e is not null && e.Planned >= 0));
    private readonly object _gate = new();
    private HistoryEntry[] Read() => _file.Read() ?? _file.Read(".bak") ?? [];
    public ImmutableArray<HistoryEntry> Load()
    {
        lock (_gate) return Read().Select(x => x.State == "进行中" ? x with { State = "中断（结果以最后记录为准）" } : x).ToImmutableArray();
    }
    public void Save(HistoryEntry entry)
    {
        lock (_gate) _file.Write(Read().Where(x => x.Id != entry.Id).Append(entry).OrderByDescending(x => x.Started).Take(50).ToArray());
    }
    public void Clear() { lock (_gate) _file.Clear(); }
}
public sealed class LocalDiagnosticLog(string directory) : IDiagnosticLog
{
    private readonly string _path = Path.Combine(directory, "diagnostics.log");
    private readonly object _gate = new();
    public void Write(string eventName, string? errorCategory = null)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(_path) && new FileInfo(_path).Length > 512 * 1024) File.Move(_path, _path + ".old", true);
                // Callers pass event/category identifiers, never exception messages or paths.
                File.AppendAllText(_path, $"{DateTimeOffset.UtcNow:O} {eventName} {errorCategory}\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Logging is best effort. */ }
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
            if (File.Exists(_path + ".old")) File.Delete(_path + ".old");
        }
    }
}
