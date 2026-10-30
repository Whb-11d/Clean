using System.Collections.Immutable;

namespace Clean.Core;

public enum ScanKind { Temporary, LargeFiles }
public enum CleanupMode { Recycle, Permanent }
public enum ItemStatus { Succeeded, Skipped, Failed, NotExecuted }
public enum ErrorKind { None, AccessDenied, InUse, Missing, Changed, Policy, Unavailable, Cancelled, Other }

public sealed record ScanRule(string Id, int Version, ScanKind Kind, string Root, int MinimumAgeDays, long MinimumBytes)
{
    public string Reason => Kind == ScanKind.Temporary
        ? $"用户临时目录 · 最后修改超过 {MinimumAgeDays} 天（仍需人工确认）"
        : $"至少 {MinimumBytes / 1000000m:0.######} MB · 大文件不等于垃圾";
    public static ScanRule Temporary(string root, int days = 7) => new("user-temp", 1, ScanKind.Temporary, root, days, 0);
    public static ScanRule LargeFiles(string root, long bytes) => new("large-files", 1, ScanKind.LargeFiles, root, 0, bytes);
    public bool Matches(FileSnapshot file, DateTimeOffset now) => file.Length >= MinimumBytes &&
        (Kind != ScanKind.Temporary || file.LastWriteUtc <= now.AddDays(-MinimumAgeDays));
    public void Validate()
    {
        if (Version != 1 || Id != (Kind == ScanKind.Temporary ? "user-temp" : "large-files") ||
            !Enum.IsDefined(Kind) || MinimumAgeDays is < 0 or > 3650 || MinimumBytes < 0)
            throw new ArgumentException("扫描规则或阈值无效。");
        PathPolicy.Normalize(Root);
    }
}

public sealed record FileSnapshot(string Path, long Length, DateTimeOffset CreatedUtc, DateTimeOffset LastWriteUtc,
    FileAttributes Attributes, string Identity, string AncestorIdentity, uint LinkCount);
public sealed record ScanCandidate(Guid Id, FileSnapshot File, ScanRule Rule)
{
    public string Path => File.Path;
    public long Bytes => File.Length;
    public string Reason => Rule.Reason;
}
public sealed record ScanIssue(string Path, ErrorKind Kind, string Message);
public sealed record WorkProgress(string Phase, long Processed, int Candidates, long Skipped);
public sealed record ScanResult(ImmutableArray<ScanCandidate> Candidates, ImmutableArray<ScanIssue> Issues,
    long Visited, long Skipped, long ErrorCount, bool Cancelled, bool Truncated, DateTimeOffset Started, DateTimeOffset Ended);
public sealed record FileEntry(string Path, bool IsDirectory, bool IsReparsePoint);
public sealed record ActionOutcome(ItemStatus Status, ErrorKind Error, string Message);
public sealed record CleanupItemResult(ScanCandidate Candidate, CleanupMode Mode, ItemStatus Status, ErrorKind Error, string Message);
public sealed record CleanupConfirmation(Guid PlanId, CleanupMode Mode);

public sealed class CleanupPlan
{
    private int _started;
    public Guid Id { get; } = Guid.NewGuid();
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    public ImmutableArray<ScanCandidate> Items { get; }
    public ImmutableArray<string> Exclusions { get; }
    public CleanupMode Mode { get; }
    public long LogicalBytes => Items.Sum(x => x.Bytes);
    public CleanupPlan(IEnumerable<ScanCandidate> items, IEnumerable<string> exclusions, CleanupMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("操作方式无效。");
        Items = items.DistinctBy(x => PathPolicy.Normalize(x.Path), StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (Items.Length == 0) throw new ArgumentException("请先勾选至少一个文件。");
        Exclusions = exclusions.Select(PathPolicy.Normalize).ToImmutableArray();
        Mode = mode;
    }
    internal void Begin(CleanupConfirmation confirmation)
    {
        if (confirmation.PlanId != Id || confirmation.Mode != Mode) throw new InvalidOperationException("确认与当前计划不匹配。");
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) throw new InvalidOperationException("此计划已执行，请重新扫描。");
    }
}

public sealed record DiskSnapshot(string Name, long TotalBytes, long AvailableBytes, string? Error);
public sealed record ProcessSnapshot(int Id, string Name, long WorkingSetBytes);
public sealed record MemorySnapshot(DateTimeOffset Timestamp, ulong TotalBytes, ulong AvailableBytes,
    ImmutableArray<ProcessSnapshot> Processes, int Inaccessible)
{
    public ulong UsedBytes => TotalBytes - AvailableBytes;
    public double UsedPercent => TotalBytes == 0 ? 0 : 100d * UsedBytes / TotalBytes;
}
public sealed record RecycleBinSnapshot(long Items, long Bytes, string? Error = null);
public sealed record EmptyBinResult(bool Succeeded, bool Cancelled, RecycleBinSnapshot Before, RecycleBinSnapshot After, string Message);
public sealed record CleanupReport(Guid PlanId, DateTimeOffset Started, DateTimeOffset Ended,
    ImmutableArray<CleanupItemResult> Items, ImmutableArray<DiskSnapshot> DisksBefore,
    ImmutableArray<DiskSnapshot> DisksAfter, string? PersistenceError)
{
    public int Recycled => Items.Count(x => x.Status == ItemStatus.Succeeded && x.Mode == CleanupMode.Recycle);
    public int Deleted => Items.Count(x => x.Status == ItemStatus.Succeeded && x.Mode == CleanupMode.Permanent);
    public int Failed => Items.Count(x => x.Status == ItemStatus.Failed);
    public int Skipped => Items.Count(x => x.Status == ItemStatus.Skipped);
    public int NotExecuted => Items.Count(x => x.Status == ItemStatus.NotExecuted);
    public long ProcessedBytes => Items.Where(x => x.Status == ItemStatus.Succeeded).Sum(x => x.Candidate.Bytes);
}
public sealed record HistoryEntry(Guid Id, DateTimeOffset Started, string State, int Planned, int Recycled,
    int Deleted, int Failed, int Skipped, int NotExecuted, long ProcessedBytes);

public sealed record AppSettings
{
    public int MinimumAgeDays { get; init; } = 7;
    public decimal LargeFileMB { get; init; } = 100;
    public string[] ExcludedDirectories { get; init; } = [];
    public int SelectedPage { get; init; }
    public bool IsValid => MinimumAgeDays is >= 0 and <= 3650 && LargeFileMB is >= 1 and <= 1100000 &&
        decimal.Truncate(LargeFileMB * 1000000m) == LargeFileMB * 1000000m &&
        SelectedPage is >= 0 and <= 5 && ExcludedDirectories is not null && ExcludedDirectories.Length <= 1000 &&
        ExcludedDirectories.All(PathPolicy.IsValid);
    public long GetLargeFileMinimumBytes() => (long)(LargeFileMB * 1000000m);
}
public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);
