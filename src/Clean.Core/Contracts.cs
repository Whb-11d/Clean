using System.Collections.Immutable;

namespace Clean.Core;

public interface IFileSystem
{
    void ValidateRoot(string path);
    IEnumerable<FileEntry> Enumerate(string directory);
    FileSnapshot ReadSnapshot(string path);
    ActionOutcome ExecuteVerified(FileSnapshot expected, CleanupMode mode, CancellationToken cancellationToken);
}
public interface IDiskInfoProvider { ImmutableArray<DiskSnapshot> GetDisks(); }
public interface IMemoryInfoProvider { MemorySnapshot Sample(bool includeProcesses); }
public interface IRecycleBinService
{
    RecycleBinSnapshot Query();
    EmptyBinResult Empty();
}
public interface ISettingsStore
{
    SettingsLoadResult Load();
    void Save(AppSettings settings);
}
public interface ICleanupHistoryStore
{
    ImmutableArray<HistoryEntry> Load();
    void Save(HistoryEntry entry);
    void Clear();
}
public interface IDiagnosticLog
{
    void Write(string eventName, string? errorCategory = null);
    void Clear();
}

public sealed class PolicyException(string message) : IOException(message);
public static class Errors
{
    public static ErrorKind Classify(Exception exception) => exception switch
    {
        PolicyException => ErrorKind.Policy,
        UnauthorizedAccessException => ErrorKind.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => ErrorKind.Missing,
        OperationCanceledException => ErrorKind.Cancelled,
        IOException io when (io.HResult & 0xffff) is 32 or 33 => ErrorKind.InUse,
        IOException => ErrorKind.Unavailable,
        _ => ErrorKind.Other
    };
    public static string Describe(Exception exception) => Classify(exception) switch
    {
        ErrorKind.Policy => exception.Message,
        ErrorKind.AccessDenied => "访问被拒绝，未更改权限。",
        ErrorKind.Missing => "文件或目录已不存在。",
        ErrorKind.InUse => "文件正在使用，未强制处理。",
        ErrorKind.Cancelled => "已取消。",
        ErrorKind.Unavailable => "磁盘或文件系统操作失败。",
        _ => $"操作失败（{exception.GetType().Name}）。"
    };
}
