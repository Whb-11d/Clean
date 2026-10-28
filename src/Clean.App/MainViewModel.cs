using System.Collections.Immutable;
using Clean.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clean.App;

public sealed partial class CandidateRow(ScanCandidate candidate) : ObservableObject
{
    public ScanCandidate Candidate { get; } = candidate;
    public string Path => Candidate.Path;
    public long Bytes => Candidate.Bytes;
    public string Size => FormatSize.Bytes(Bytes);
    public string Modified => Candidate.File.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Reason => Candidate.Reason;
    [ObservableProperty] private bool _selected;
}
public sealed record DiskRow(string Name, string Total, string Available, double UsedPercent, string? Error);
public sealed record ResultRow(string Path, string Status, string Mode, string Size, string Message);
public sealed record ProcessRow(int Id, string Name, string WorkingSet);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ScanService _scanner;
    private readonly CleanupService _cleaner;
    private readonly IDiskInfoProvider _disks;
    private readonly IMemoryInfoProvider _memory;
    private readonly IRecycleBinService _bin;
    private readonly ISettingsStore _settings;
    private readonly ICleanupHistoryStore _history;
    private readonly IDiagnosticLog _log;
    private readonly IUserDialogs _dialogs;
    private readonly AppLocations _locations;
    private CancellationTokenSource? _operation;
    private bool _sampling;
    private CleanupPlan? _plan;

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private int _selectedPage;
    [ObservableProperty] private string _status = "就绪 · 所有扫描均为只读";
    [ObservableProperty] private string _scanSummary = "尚未扫描。请先选择范围，查看结果后逐项勾选。";
    [ObservableProperty] private string _largeFolder = "";
    [ObservableProperty] private string _ageDaysText = "7";
    [ObservableProperty] private string _minimumMiBText = "100";
    [ObservableProperty] private string _exclusionsText = "";
    [ObservableProperty] private bool _permanent;
    [ObservableProperty] private string _planSummary = "尚未生成计划。请在扫描结果中勾选文件。";
    [ObservableProperty] private string _reportSummary = "尚无执行结果。";
    [ObservableProperty] private string _spaceSummary = "逻辑大小不是实际释放空间；移入回收站通常不释放所在卷空间。";
    [ObservableProperty] private string _memorySummary = "等待采样";
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _memoryTimestamp = "尚未采样";
    [ObservableProperty] private string _binSummary = "等待查询";
    [ObservableProperty] private IReadOnlyList<CandidateRow> _candidates = Array.Empty<CandidateRow>();
    [ObservableProperty] private IReadOnlyList<ScanIssue> _scanIssues = Array.Empty<ScanIssue>();
    [ObservableProperty] private IReadOnlyList<ScanCandidate> _planItems = Array.Empty<ScanCandidate>();
    [ObservableProperty] private IReadOnlyList<ResultRow> _results = Array.Empty<ResultRow>();
    [ObservableProperty] private IReadOnlyList<DiskRow> _diskRows = Array.Empty<DiskRow>();
    [ObservableProperty] private IReadOnlyList<ProcessRow> _processRows = Array.Empty<ProcessRow>();
    [ObservableProperty] private IReadOnlyList<HistoryEntry> _historyRows = Array.Empty<HistoryEntry>();

    public MainViewModel(ScanService scanner, CleanupService cleaner, IDiskInfoProvider disks, IMemoryInfoProvider memory,
        IRecycleBinService bin, ISettingsStore settings, ICleanupHistoryStore history, IDiagnosticLog log, IUserDialogs dialogs, AppLocations locations)
    {
        (_scanner, _cleaner, _disks, _memory, _bin, _settings, _history, _log, _dialogs, _locations) =
            (scanner, cleaner, disks, memory, bin, settings, history, log, dialogs, locations);
        var loaded = settings.Load();
        AgeDaysText = loaded.Settings.MinimumAgeDays.ToString();
        MinimumMiBText = loaded.Settings.LargeFileMiB.ToString();
        ExclusionsText = string.Join(Environment.NewLine, loaded.Settings.ExcludedDirectories);
        SelectedPage = loaded.Settings.SelectedPage;
        if (loaded.Warning is not null) Status = loaded.Warning;
        HistoryRows = history.Load();
    }
    public bool Idle => !Busy;
    public string TemporaryDirectory => _locations.TemporaryDirectory;
    public string StateDirectory => _locations.StateDirectory;
    public string SelectionSummary => $"已选 {Candidates.Count(x => x.Selected):N0} / {Candidates.Count:N0} 项 · 逻辑大小 {FormatSize.Bytes(Candidates.Where(x => x.Selected).Sum(x => x.Bytes))}";
    private bool CanWork() => !Busy;
    private bool CanExecutePlan() => !Busy && _plan is not null;
    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(Idle));
        ScanTemporaryCommand.NotifyCanExecuteChanged(); ScanLargeCommand.NotifyCanExecuteChanged();
        BrowseFolderCommand.NotifyCanExecuteChanged(); BuildPlanCommand.NotifyCanExecuteChanged();
        ExecutePlanCommand.NotifyCanExecuteChanged(); SaveSettingsCommand.NotifyCanExecuteChanged();
        ClearHistoryCommand.NotifyCanExecuteChanged(); EmptyBinCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged(); AddExclusionCommand.NotifyCanExecuteChanged();
        RefreshOverviewCommand.NotifyCanExecuteChanged();
    }
    partial void OnPermanentChanged(bool value) => InvalidatePlan();
    partial void OnExclusionsTextChanged(string value) => InvalidatePlan();
    partial void OnAgeDaysTextChanged(string value) => InvalidatePlan();
    partial void OnMinimumMiBTextChanged(string value) => InvalidatePlan();
    private void InvalidatePlan()
    {
        _plan = null; PlanItems = Array.Empty<ScanCandidate>();
        PlanSummary = "选择、规则或操作方式变化后，需要重新生成计划。";
        ExecutePlanCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(SelectionSummary));
    }
    private AppSettings ReadSettings()
    {
        if (!int.TryParse(AgeDaysText, out var days) || days is < 0 or > 3650) throw new ArgumentException("临时文件时间阈值须为 0～3650 天。");
        if (!long.TryParse(MinimumMiBText, out var mib) || mib is < 1 or > 1048576) throw new ArgumentException("最低大小须为 1～1048576 MiB。");
        var exclusions = ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PathPolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var settings = new AppSettings { MinimumAgeDays = days, LargeFileMiB = mib, ExcludedDirectories = exclusions, SelectedPage = SelectedPage };
        if (!settings.IsValid) throw new ArgumentException("设置无效或排除目录过多。");
        return settings;
    }
    private async Task Run(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; _operation = new();
        try { await action(); }
        catch (Exception e) { Status = Errors.Describe(e) + (e is ArgumentException ? " " + e.Message : ""); _log.Write("operation-failed", e.GetType().Name); }
        finally { _operation.Dispose(); _operation = null; Busy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task ScanTemporary() => Scan(false);
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task ScanLarge() => Scan(true);
    private Task Scan(bool large) => Run(async () =>
    {
        var settings = ReadSettings();
        var rule = large ? ScanRule.LargeFiles(PathPolicy.Normalize(LargeFolder), settings.LargeFileMiB * 1048576) :
            ScanRule.Temporary(_locations.TemporaryDirectory, settings.MinimumAgeDays);
        InvalidatePlan(); Results = Array.Empty<ResultRow>(); ReportSummary = "尚无执行结果。";
        Candidates = Array.Empty<CandidateRow>(); ScanIssues = Array.Empty<ScanIssue>();
        Status = "正在只读扫描…";
        var progress = new Progress<WorkProgress>(p => Status = $"{p.Phase} · 已访问 {p.Processed:N0} 项 · 候选 {p.Candidates:N0} 项 · 跳过 {p.Skipped:N0} 项");
        var result = await _scanner.ScanAsync(rule, settings.ExcludedDirectories, progress, _operation!.Token);
        Candidates = result.Candidates.Select(x => new CandidateRow(x)).ToArray();
        foreach (var row in Candidates) row.PropertyChanged += (_, _) => InvalidatePlan();
        ScanIssues = result.Issues;
        ScanSummary = $"{(large ? "大文件分析" : "临时文件扫描")} · {result.Candidates.Length:N0} 个候选 · 跳过 {result.Skipped:N0} · 错误 {result.ErrorCount:N0}" +
            $" · {(result.Ended - result.Started).TotalSeconds:N1} 秒" + (result.Cancelled ? " · 已取消，结果不完整" : "") +
            (result.Truncated ? " · 达到 100000 项上限，结果不完整；请缩小范围" : "");
        Status = ScanSummary; OnPropertyChanged(nameof(SelectionSummary));
        _log.Write("scan-completed");
    });
    [RelayCommand] private void Cancel() { _operation?.Cancel(); Status = "正在停止后续调度，等待当前系统操作返回…"; }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void BrowseFolder() { var folder = _dialogs.PickFolder(); if (folder is not null) LargeFolder = folder; }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void AddExclusion()
    { var folder = _dialogs.PickFolder(); if (folder is not null) ExclusionsText = string.IsNullOrWhiteSpace(ExclusionsText) ? folder : ExclusionsText + Environment.NewLine + folder; }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void ClearSelection() { foreach (var row in Candidates.Where(x => x.Selected)) row.Selected = false; }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void BuildPlan()
    {
        try
        {
            var settings = ReadSettings();
            _plan = new(Candidates.Where(x => x.Selected).Select(x => x.Candidate), settings.ExcludedDirectories,
                Permanent ? CleanupMode.Permanent : CleanupMode.Recycle);
            PlanItems = _plan.Items;
            PlanSummary = $"{_plan.Items.Length:N0} 个文件 · 逻辑大小 {FormatSize.Bytes(_plan.LogicalBytes)} · " + (Permanent ? "永久删除（不可恢复）" : "移入 Windows 回收站");
            SelectedPage = 2; ExecutePlanCommand.NotifyCanExecuteChanged(); Status = "请核对下方每个路径和依据，再确认执行。";
        }
        catch (Exception e) when (e is ArgumentException) { Status = e.Message; }
    }
    [RelayCommand(CanExecute = nameof(CanExecutePlan))]
    private async Task ExecutePlan()
    {
        var plan = _plan;
        if (plan is null || !_dialogs.ConfirmCleanup(plan)) { Status = "已取消确认，没有执行清理。"; return; }
        await Run(async () =>
        {
            var progress = new Progress<WorkProgress>(p => Status = $"清理 · 已处理 {p.Processed:N0} / {p.Candidates:N0} 项");
            try
            {
                var report = await _cleaner.ExecuteAsync(plan, new(plan.Id, plan.Mode), progress, _operation!.Token);
                Results = report.Items.Select(x => new ResultRow(x.Candidate.Path, x.Status switch
                { ItemStatus.Succeeded => "成功", ItemStatus.Skipped => "跳过", ItemStatus.Failed => "失败", _ => "未执行" },
                    x.Mode == CleanupMode.Recycle ? "回收站" : "永久删除", FormatSize.Bytes(x.Candidate.Bytes), x.Message)).ToArray();
                ReportSummary = $"移入回收站 {report.Recycled} · 永久删除 {report.Deleted} · 跳过 {report.Skipped} · 失败 {report.Failed} · 未执行 {report.NotExecuted}" +
                    $"\n已处理逻辑大小：{FormatSize.Bytes(report.ProcessedBytes)}";
                var deltas = report.DisksAfter.Where(x => x.Error is null).Select(after =>
                {
                    var before = report.DisksBefore.FirstOrDefault(x => x.Name == after.Name && x.Error is null);
                    return before is null ? $"{after.Name} 无可比数据" : $"{after.Name} {FormatSize.Bytes(after.AvailableBytes - before.AvailableBytes)}";
                });
                SpaceSummary = "磁盘可用空间观测变化（可受其他进程影响）：" + string.Join("；", deltas);
                Status = report.PersistenceError ?? "执行结束。详细结果如下；已完成操作不会因停止而回滚。";
                _log.Write("cleanup-completed");
            }
            finally { _plan = null; ExecutePlanCommand.NotifyCanExecuteChanged(); HistoryRows = _history.Load(); }
        });
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task RefreshOverview() => Run(async () =>
    {
        var disks = await Task.Run(_disks.GetDisks);
        DiskRows = disks.Select(x => new DiskRow(x.Name, FormatSize.Bytes(x.TotalBytes), FormatSize.Bytes(x.AvailableBytes),
            x.TotalBytes == 0 ? 0 : 100d * (x.TotalBytes - x.AvailableBytes) / x.TotalBytes, x.Error)).ToArray();
        var bin = await Task.Run(_bin.Query);
        BinSummary = bin.Error ?? $"{bin.Items:N0} 个项目 · 逻辑大小 {FormatSize.Bytes(bin.Bytes)}";
        await SampleMemoryAsync(false);
    });
    public async Task SampleMemoryAsync(bool includeProcesses = true)
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var snapshot = await Task.Run(() => _memory.Sample(includeProcesses));
            MemorySummary = $"已用 {FormatSize.Bytes(snapshot.UsedBytes)} / 总量 {FormatSize.Bytes(snapshot.TotalBytes)} · 可用 {FormatSize.Bytes(snapshot.AvailableBytes)}";
            MemoryPercent = snapshot.UsedPercent;
            MemoryTimestamp = $"采样时间 {snapshot.Timestamp.ToLocalTime():HH:mm:ss} · 不可访问或已退出进程 {snapshot.Inaccessible} 个";
            if (includeProcesses) ProcessRows = snapshot.Processes.Select(x => new ProcessRow(x.Id, x.Name, FormatSize.Bytes(x.WorkingSetBytes))).ToArray();
        }
        catch (Exception e) { MemoryTimestamp = "采样失败，保留上次数据：" + Errors.Describe(e); }
        finally { _sampling = false; }
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void SaveSettings()
    {
        try { _settings.Save(ReadSettings()); Status = "设置已保存，将用于下一次扫描；现有计划需重新生成。"; InvalidatePlan(); }
        catch (Exception e) { Status = "保存失败：" + (e is ArgumentException ? e.Message : Errors.Describe(e)); }
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void ClearHistory()
    {
        try { _history.Clear(); _log.Clear(); HistoryRows = _history.Load(); Status = "本地历史与诊断日志已清除。"; }
        catch (Exception e) { Status = "清除失败：" + Errors.Describe(e); }
    }
    [RelayCommand] private void OpenBin() { try { _dialogs.OpenRecycleBin(); } catch (Exception e) { Status = Errors.Describe(e); } }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task EmptyBin()
    {
        if (!_dialogs.ConfirmEmptyBin()) return Task.CompletedTask;
        return Run(async () =>
        {
            var result = await Task.Run(_bin.Empty);
            BinSummary = result.After.Error ?? $"{result.After.Items:N0} 个项目 · {FormatSize.Bytes(result.After.Bytes)}";
            Status = result.Message + $" 操作前 {result.Before.Items} 项，操作后 {result.After.Items} 项。";
            _log.Write("empty-bin", result.Succeeded ? "success" : "incomplete");
        });
    }
}
