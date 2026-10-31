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
public sealed record NavigationItem(int Page, string Label, string Icon);

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
    private int _returnPage;
    private bool _changingSelection;

    [ObservableProperty] private string _candidateSearch = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NoMatchingCandidates)), NotifyPropertyChangedFor(nameof(HasFilteredCandidates)), NotifyPropertyChangedFor(nameof(FilterSummary))]
    private IReadOnlyList<CandidateRow> _filteredCandidates = Array.Empty<CandidateRow>();

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private int _selectedPage;
    [ObservableProperty] private NavigationItem? _selectedNavigation;
    [ObservableProperty] private bool _hasQuickSession;
    [ObservableProperty] private string _status = "就绪";
    [ObservableProperty] private bool _quickCleaning;
    [ObservableProperty] private string _quickSummary = "准备就绪，点击即可清理";
    [ObservableProperty] private string _scanSummary = "尚未扫描。请先选择范围，查看结果后逐项勾选。";
    [ObservableProperty] private string _largeFolder = "";
    [ObservableProperty] private string _ageDaysText = "7";
    [ObservableProperty] private string _minimumMBText = "100";
    [ObservableProperty] private string _exclusionsText = "";
    [ObservableProperty] private bool _permanent;
    [ObservableProperty] private string _planSummary = "尚未生成计划。请在扫描结果中勾选文件。";
    [ObservableProperty] private string _reportSummary = "尚无执行结果。";
    [ObservableProperty] private string _spaceSummary = "逻辑大小不是实际释放空间；移入回收站通常不释放所在卷空间。";
    [ObservableProperty] private string _memorySummary = "等待采样";
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _memoryTimestamp = "尚未采样";
    [ObservableProperty] private string _binSummary = "等待查询";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCandidates)), NotifyPropertyChangedFor(nameof(NoCandidates))]
    private IReadOnlyList<CandidateRow> _candidates = Array.Empty<CandidateRow>();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasScanIssues))]
    private IReadOnlyList<ScanIssue> _scanIssues = Array.Empty<ScanIssue>();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPlanItems)), NotifyPropertyChangedFor(nameof(NoCleanupDetails))]
    private IReadOnlyList<ScanCandidate> _planItems = Array.Empty<ScanCandidate>();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResults)), NotifyPropertyChangedFor(nameof(NoCleanupDetails))]
    private IReadOnlyList<ResultRow> _results = Array.Empty<ResultRow>();
    [ObservableProperty] private IReadOnlyList<DiskRow> _diskRows = Array.Empty<DiskRow>();
    [ObservableProperty] private IReadOnlyList<ProcessRow> _processRows = Array.Empty<ProcessRow>();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHistory)), NotifyPropertyChangedFor(nameof(NoHistory))]
    private IReadOnlyList<HistoryEntry> _historyRows = Array.Empty<HistoryEntry>();

    public MainViewModel(ScanService scanner, CleanupService cleaner, IDiskInfoProvider disks, IMemoryInfoProvider memory,
        IRecycleBinService bin, ISettingsStore settings, ICleanupHistoryStore history, IDiagnosticLog log, IUserDialogs dialogs, AppLocations locations)
    {
        (_scanner, _cleaner, _disks, _memory, _bin, _settings, _history, _log, _dialogs, _locations) =
            (scanner, cleaner, disks, memory, bin, settings, history, log, dialogs, locations);
        var loaded = settings.Load();
        AgeDaysText = loaded.Settings.MinimumAgeDays.ToString();
        MinimumMBText = loaded.Settings.LargeFileMB.ToString();
        ExclusionsText = string.Join(Environment.NewLine, loaded.Settings.ExcludedDirectories);
        // Details belong to a live task and must never be restored as a landing page.
        SelectedNavigation = NavigationItems.FirstOrDefault(x => x.Page == loaded.Settings.SelectedPage) ?? NavigationItems[0];
        if (loaded.Warning is not null) Status = loaded.Warning;
        HistoryRows = history.Load();
    }
    public bool Idle => !Busy;
    public IReadOnlyList<NavigationItem> NavigationItems { get; } =
    [new(0, "一键清理", "\uE80F"), new(1, "临时文件", "\uE74D"), new(3, "大文件分析", "\uE8B7"),
     new(4, "内存监控", "\uE9D9"), new(5, "设置与历史", "\uE713")];
    partial void OnSelectedNavigationChanged(NavigationItem? value)
    {
        if (value is not null) SelectedPage = value.Page;
    }
    partial void OnSelectedPageChanged(int value)
    {
        if (value == 2) { SelectedNavigation = null; return; }
        _returnPage = value;
        SelectedNavigation = NavigationItems.FirstOrDefault(x => x.Page == value) ?? NavigationItems[0];
    }
    public bool HasCandidates => Candidates.Count > 0;
    public bool NoCandidates => !HasCandidates;
    public bool NoMatchingCandidates => HasCandidates && FilteredCandidates.Count == 0;
    public bool HasFilteredCandidates => FilteredCandidates.Count > 0;
    public string FilterSummary => $"显示 {FilteredCandidates.Count:N0} / {Candidates.Count:N0} 项";
    partial void OnCandidateSearchChanged(string value) => FilterCandidates();
    partial void OnCandidatesChanged(IReadOnlyList<CandidateRow>? oldValue, IReadOnlyList<CandidateRow> newValue)
    {
        if (oldValue is not null)
            foreach (var row in oldValue) row.PropertyChanged -= CandidateSelectionChanged;
        foreach (var row in newValue) row.PropertyChanged += CandidateSelectionChanged;
        CandidateSearch = "";
        FilterCandidates();
    }
    private void CandidateSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_changingSelection) InvalidatePlan();
    }
    private void FilterCandidates()
    {
        var query = CandidateSearch.Trim();
        FilteredCandidates = query.Length == 0 ? Candidates : Candidates.Where(x => x.Path.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        OnPropertyChanged(nameof(NoMatchingCandidates));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(SelectionSummary));
        SelectFilteredCommand.NotifyCanExecuteChanged();
    }
    public bool HasScanIssues => ScanIssues.Count > 0;
    public bool HasPlanItems => PlanItems.Count > 0;
    public bool HasResults => Results.Count > 0;
    public bool NoCleanupDetails => !HasPlanItems && !HasResults;
    public bool HasHistory => HistoryRows.Count > 0;
    public bool NoHistory => !HasHistory;
    public string QuickScope => $"永久删除 {QuickAgeDays} 天前修改的用户临时文件，不进入回收站。";
    private int QuickAgeDays => int.TryParse(AgeDaysText, out var days) ? Math.Max(7, days) : 7;
    public string TemporaryDirectory => _locations.TemporaryDirectory;
    public string StateDirectory => _locations.StateDirectory;
    public string SelectionSummary
    {
        get
        {
            var selected = Candidates.Where(x => x.Selected).ToArray();
            var hidden = selected.Length - FilteredCandidates.Count(x => x.Selected);
            return $"已选 {selected.Length:N0} / {Candidates.Count:N0} 项 · 逻辑大小 {FormatSize.Bytes(selected.Sum(x => x.Bytes))}" +
                (hidden > 0 ? $" · 含 {hidden:N0} 项被筛选隐藏（仍在清理范围内）" : "");
        }
    }
    private bool CanSelectFiltered() => !Busy && FilteredCandidates.Count > 0;
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
        QuickCleanCommand.NotifyCanExecuteChanged();
        SelectFilteredCommand.NotifyCanExecuteChanged();
    }
    partial void OnPermanentChanged(bool value) => InvalidatePlan();
    partial void OnExclusionsTextChanged(string value) => InvalidatePlan();
    partial void OnAgeDaysTextChanged(string value) { InvalidatePlan(); OnPropertyChanged(nameof(QuickScope)); }
    partial void OnMinimumMBTextChanged(string value) => InvalidatePlan();
    partial void OnLargeFolderChanged(string value) => InvalidatePlan();
    private void InvalidatePlan()
    {
        _plan = null; PlanItems = Array.Empty<ScanCandidate>();
        PlanSummary = "选择、规则或操作方式变化后，需要重新生成计划。";
        ExecutePlanCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(SelectionSummary));
    }
    private AppSettings ReadSettings()
    {
        if (!int.TryParse(AgeDaysText, out var days) || days is < 0 or > 3650) throw new ArgumentException("临时文件时间阈值须为 0～3650 天。");
        if (!decimal.TryParse(MinimumMBText, out var mb) || mb is < 1 or > 1100000 || decimal.Truncate(mb * 1000000m) != mb * 1000000m)
            throw new ArgumentException("最低大小须为 1～1100000 MB，最多保留 6 位小数。");
        var exclusions = ExclusionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PathPolicy.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var settings = new AppSettings { MinimumAgeDays = days, LargeFileMB = mb, ExcludedDirectories = exclusions, SelectedPage = SelectedPage == 2 ? _returnPage : SelectedPage };
        if (!settings.IsValid) throw new ArgumentException("设置无效或排除目录过多。");
        return settings;
    }
    private async Task Run(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; _operation = new();
        try { await action(); }
        catch (Exception e)
        {
            Status = Errors.Describe(e) + (e is ArgumentException ? " " + e.Message : "");
            if (QuickCleaning) QuickSummary = "清理未完成：" + Status;
            _log.Write("operation-failed", e.GetType().Name);
        }
        finally { _operation.Dispose(); _operation = null; QuickCleaning = false; Busy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task QuickClean() => Run(async () =>
    {
        QuickCleaning = true;
        SelectedPage = 0;
        HasQuickSession = true;
        InvalidatePlan();
        Candidates = Array.Empty<CandidateRow>(); Results = Array.Empty<ResultRow>(); ScanIssues = Array.Empty<ScanIssue>();
        ReportSummary = "尚无执行结果。";
        var settings = ReadSettings();
        QuickSummary = Status = "正在查找过期临时文件…";
        var active = true;
        var progressGate = new object();
        void StopProgress() { lock (progressGate) active = false; }
        var progress = new Progress<WorkProgress>(p =>
        {
            lock (progressGate)
            {
                if (active) QuickSummary = Status = p.Phase == "清理"
                    ? $"正在清理 · 已处理 {p.Processed:N0} / {p.Candidates:N0} 项"
                    : $"正在扫描 · 已检查 {p.Processed:N0} 项 · 找到 {p.Candidates:N0} 个过期文件";
            }
        });
        try
        {
            var scan = await _scanner.ScanAsync(ScanRule.Temporary(_locations.TemporaryDirectory, Math.Max(7, settings.MinimumAgeDays)),
                settings.ExcludedDirectories, progress, _operation!.Token);
            ScanIssues = scan.Issues;
            var note = (scan.ErrorCount > 0 ? $" · {scan.ErrorCount:N0} 处无法扫描" : "") +
                (scan.Truncated ? " · 本次仅处理扫描上限内文件，可再次清理" : "");
            if (scan.Cancelled || _operation.IsCancellationRequested)
            {
                StopProgress(); QuickSummary = Status = "已停止扫描，没有删除任何文件。"; return;
            }
            if (scan.Candidates.Length == 0)
            {
                StopProgress();
                QuickSummary = Status = (scan.ErrorCount > 0 ? "扫描未完整完成，未找到可清理文件" : "暂时没有需要清理的过期临时文件") + note;
                return;
            }
            // The explicit one-click button authorizes only this fresh temporary-rule snapshot.
            // Manual selections and the manual deletion-mode checkbox never affect this flow.
            var plan = new CleanupPlan(scan.Candidates, settings.ExcludedDirectories, CleanupMode.Permanent);
            var report = await _cleaner.ExecuteAsync(plan, new(plan.Id, plan.Mode), progress, _operation.Token);
            StopProgress();
            ApplyReport(report);
            QuickSummary = $"{(_operation.IsCancellationRequested ? "已停止" : "清理结束")} · 已删除 {report.Deleted:N0} 个临时文件 · 逻辑大小 {FormatSize.Bytes(report.ProcessedBytes)}" +
                $" · 跳过 {report.Skipped:N0} · 失败 {report.Failed:N0} · 未执行 {report.NotExecuted:N0}" + note;
            if (report.PersistenceError is not null) QuickSummary += " · " + report.PersistenceError;
            Status = QuickSummary;
            _log.Write("quick-clean-completed");
        }
        finally { StopProgress(); HistoryRows = _history.Load(); }
    });
    [RelayCommand] private void ShowReport()
    {
        if (HasQuickSession || HasResults || HasPlanItems) SelectedPage = 2;
    }
    [RelayCommand] private void CloseReport() => SelectedPage = _returnPage;
    [RelayCommand] private void ShowManualScan() => SelectedPage = 1;
    [RelayCommand] private void ShowLargeFiles() => SelectedPage = 3;
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task ScanTemporary() => Scan(false);
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task ScanLarge() => Scan(true);
    private Task Scan(bool large) => Run(async () =>
    {
        var settings = ReadSettings();
        var rule = large ? ScanRule.LargeFiles(PathPolicy.Normalize(LargeFolder), settings.GetLargeFileMinimumBytes()) :
            ScanRule.Temporary(_locations.TemporaryDirectory, settings.MinimumAgeDays);
        HasQuickSession = false;
        InvalidatePlan(); Results = Array.Empty<ResultRow>(); ReportSummary = "尚无执行结果。";
        Candidates = Array.Empty<CandidateRow>(); ScanIssues = Array.Empty<ScanIssue>();
        Status = "正在只读扫描…";
        var progress = new Progress<WorkProgress>(p => Status = $"{p.Phase} · 已访问 {p.Processed:N0} 项 · 候选 {p.Candidates:N0} 项 · 跳过 {p.Skipped:N0} 项");
        var result = await _scanner.ScanAsync(rule, settings.ExcludedDirectories, progress, _operation!.Token);
        Candidates = result.Candidates.Select(x => new CandidateRow(x)).ToArray();
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
    private void ClearSelection() => ChangeSelection(Candidates, false);
    [RelayCommand(CanExecute = nameof(CanSelectFiltered))]
    private void SelectFiltered() => ChangeSelection(FilteredCandidates, true);
    private void ChangeSelection(IReadOnlyList<CandidateRow> rows, bool selected)
    {
        _changingSelection = true;
        try { foreach (var row in rows) row.Selected = selected; }
        finally { _changingSelection = false; InvalidatePlan(); }
    }
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void BuildPlan()
    {
        try
        {
            var settings = ReadSettings();
            foreach (var row in Candidates.Where(x => x.Selected))
            {
                var rule = row.Candidate.Rule;
                if ((rule.Kind == ScanKind.Temporary && rule.MinimumAgeDays != settings.MinimumAgeDays) ||
                    (rule.Kind == ScanKind.LargeFiles && (rule.MinimumBytes != settings.GetLargeFileMinimumBytes() ||
                        !rule.Root.Equals(PathPolicy.Normalize(LargeFolder), StringComparison.OrdinalIgnoreCase))))
                    throw new ArgumentException("扫描范围或阈值已改变，请重新扫描后再生成计划。");
            }
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
                ApplyReport(report);
                Status = report.PersistenceError ?? "执行结束。详细结果如下；已完成操作不会因停止而回滚。";
                _log.Write("cleanup-completed");
            }
            finally { _plan = null; PlanItems = Array.Empty<ScanCandidate>(); ExecutePlanCommand.NotifyCanExecuteChanged(); HistoryRows = _history.Load(); }
        });
    }
    private void ApplyReport(CleanupReport report)
    {
        ApplyDisks(report.DisksAfter);
        Results = report.Items.Select(x => new ResultRow(x.Candidate.Path, x.Status switch
        { ItemStatus.Succeeded => "成功", ItemStatus.Skipped => "跳过", ItemStatus.Failed => "失败", _ => "未执行" },
            x.Mode == CleanupMode.Recycle ? "回收站" : "永久删除", FormatSize.Bytes(x.Candidate.Bytes), x.Message)).ToArray();
        ReportSummary = $"移入回收站 {report.Recycled} · 永久删除 {report.Deleted} · 跳过 {report.Skipped} · 失败 {report.Failed} · 未执行 {report.NotExecuted}" +
            $"\n已处理逻辑大小：{FormatSize.Bytes(report.ProcessedBytes)}";
        SpaceSummary = "磁盘可用空间变化（可能受其他程序影响）：" + string.Join("；", report.DisksAfter.Where(x => x.Error is null).Select(after =>
        {
            var before = report.DisksBefore.FirstOrDefault(x => x.Name == after.Name && x.Error is null);
            return before is null ? $"{after.Name} 无可比数据" : $"{after.Name} {FormatSize.Bytes(after.AvailableBytes - before.AvailableBytes)}";
        }));
    }
    private void ApplyDisks(IEnumerable<DiskSnapshot> disks) =>
        DiskRows = disks.Select(x => new DiskRow(x.Name, FormatSize.Bytes(x.TotalBytes), FormatSize.Bytes(x.AvailableBytes),
            x.TotalBytes == 0 ? 0 : 100d * (x.TotalBytes - x.AvailableBytes) / x.TotalBytes, x.Error)).ToArray();
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task RefreshOverview() => Run(async () =>
    {
        var disks = await Task.Run(_disks.GetDisks);
        ApplyDisks(disks);
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


