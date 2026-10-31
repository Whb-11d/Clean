using Clean.App;
using Clean.Core;
using Clean.Testing;

namespace Clean.App.Tests;

public sealed class ViewModelTests
{
    [Fact] public async Task FilteredSelectionKeepsHiddenChoicesExplicitAndExecutesOnlyApprovedFiles()
    {
        var (vm, files, dialogs, _, _) = Create();
        await vm.ScanTemporaryCommand.ExecuteAsync(null);
        vm.CandidateSearch = @"  C:\TEST\a  ";
        Assert.Single(vm.FilteredCandidates); Assert.All(vm.Candidates, x => Assert.False(x.Selected));
        vm.SelectFilteredCommand.Execute(null);
        Assert.Single(vm.Candidates, x => x.Selected);
        vm.BuildPlanCommand.Execute(null); Assert.Single(vm.PlanItems);
        vm.CandidateSearch = "does-not-exist";
        Assert.True(vm.NoMatchingCandidates); Assert.False(vm.HasFilteredCandidates);
        Assert.False(vm.SelectFilteredCommand.CanExecute(null));
        Assert.Contains("含 1 项被筛选隐藏", vm.SelectionSummary);
        Assert.True(vm.ExecutePlanCommand.CanExecute(null));
        dialogs.Confirm = true;
        await vm.ExecutePlanCommand.ExecuteAsync(null);
        Assert.Equal(@"C:\Test\a", Assert.Single(files.Executed));
        Assert.NotEmpty(vm.DiskRows);
    }
    [Fact] public async Task BulkSelectionInvalidatesPlanAndClearIncludesHiddenRows()
    {
        var (vm, _, _, _, _) = Create(); await vm.ScanTemporaryCommand.ExecuteAsync(null);
        vm.Candidates[0].Selected = true; vm.BuildPlanCommand.Execute(null);
        vm.SelectFilteredCommand.Execute(null);
        Assert.All(vm.Candidates, x => Assert.True(x.Selected));
        Assert.False(vm.ExecutePlanCommand.CanExecute(null));
        vm.CandidateSearch = "no-match"; vm.ClearSelectionCommand.Execute(null);
        Assert.All(vm.Candidates, x => Assert.False(x.Selected));
        await vm.ScanTemporaryCommand.ExecuteAsync(null);
        Assert.Equal("", vm.CandidateSearch); Assert.Equal(vm.Candidates.Count, vm.FilteredCandidates.Count);
        Assert.All(vm.Candidates, x => Assert.False(x.Selected));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanupRefreshesOverviewFromAfterSnapshot(bool quick)
    {
        var files = new FakeFiles(); files.Add(@"C:\Test\a");
        var metrics = new FakeMetrics(); var disks = new ChangingDisks(); var history = new FakeHistory();
        var vm = new MainViewModel(new(files, new()), new(files, new(), disks, history), disks, metrics,
            new FakeBin(), new FakeSettings(), history, new FakeLog(), new Dialogs { Confirm = true }, new(@"C:\Test", @"C:\State"));
        await vm.RefreshOverviewCommand.ExecuteAsync(null);
        Assert.Equal(90d, Assert.Single(vm.DiskRows).UsedPercent);
        files.OnExecute = () => disks.Available = 8000;
        if (quick) await vm.QuickCleanCommand.ExecuteAsync(null);
        else
        {
            await vm.ScanTemporaryCommand.ExecuteAsync(null); vm.SelectFilteredCommand.Execute(null);
            vm.BuildPlanCommand.Execute(null); await vm.ExecutePlanCommand.ExecuteAsync(null);
        }
        Assert.Equal(20d, Assert.Single(vm.DiskRows).UsedPercent);
        Assert.Equal(FormatSize.Bytes(8000), vm.DiskRows[0].Available);
    }
    private sealed class ChangingDisks : IDiskInfoProvider
    {
        public long Available = 1000;
        public System.Collections.Immutable.ImmutableArray<DiskSnapshot> GetDisks() => [new("C:\\", 10000, Available, null)];
    }
    [Fact] public async Task LargeFileThresholdUsesDecimalMegabytesAtTheExactBoundary()
    {
        var (vm, files, _, settings, _) = Create();
        vm.LargeFolder = @"C:\Boundary"; vm.MinimumMBText = "100";
        files.Add(@"C:\Boundary\below.bin", bytes: 99999999);
        files.Add(@"C:\Boundary\included.bin", bytes: 100000000);
        await vm.ScanLargeCommand.ExecuteAsync(null);
        Assert.Equal(@"C:\Boundary\included.bin", Assert.Single(vm.Candidates).Path);
        Assert.Contains("100 MB", vm.Candidates[0].Reason);
        vm.SaveSettingsCommand.Execute(null);
        Assert.Equal(100000000, settings.Value.GetLargeFileMinimumBytes());
    }
    private sealed class Dialogs : IUserDialogs
    {
        public bool Confirm { get; set; }
        public int Confirmations { get; private set; }
        public string? PickFolder() => @"C:\Test";
        public bool ConfirmCleanup(CleanupPlan plan) { Confirmations++; return Confirm; }
        public bool ConfirmEmptyBin() => Confirm;
        public void OpenRecycleBin() { }
    }
    private static (MainViewModel Vm, FakeFiles Files, Dialogs Dialogs, FakeSettings Settings, FakeBin Bin) Create(int savedPage = 0)
    {
        var files = new FakeFiles(); files.Add(@"C:\Test\a", bytes: 200 * 1048576); files.Add(@"C:\Test\b", bytes: 150 * 1048576);
        var metrics = new FakeMetrics(); var history = new FakeHistory(); var dialogs = new Dialogs(); var settings = new FakeSettings(); var bin = new FakeBin();
        settings.Save(new AppSettings { SelectedPage = savedPage });
        return (new(new(files, new()), new(files, new(), metrics, history), metrics, metrics, bin, settings, history, new FakeLog(), dialogs,
            new(@"C:\Test", @"C:\State")), files, dialogs, settings, bin);
    }
    [Fact] public void NavigationContainsOnlyMainPagesAndRestoresLegacyDetailsToHome()
    {
        var (vm, _, _, _, _) = Create(2);
        Assert.Equal(new[] { 0, 1, 3, 4, 5 }, vm.NavigationItems.Select(x => x.Page));
        Assert.Equal(0, vm.SelectedPage); Assert.False(vm.HasQuickSession);
        vm.ShowReportCommand.Execute(null); Assert.Equal(0, vm.SelectedPage);
        foreach (var item in vm.NavigationItems)
        {
            vm.SelectedNavigation = item; Assert.Equal(item.Page, vm.SelectedPage);
        }
    }
    [Fact] public async Task ManualDetailsReturnToTheirSourceAndNeverPersistAsNavigation()
    {
        var (vm, _, _, settings, _) = Create(3); vm.LargeFolder = @"C:\Test";
        await vm.ScanLargeCommand.ExecuteAsync(null); vm.Candidates[0].Selected = true;
        vm.BuildPlanCommand.Execute(null);
        Assert.Equal(2, vm.SelectedPage); Assert.Null(vm.SelectedNavigation);
        vm.SaveSettingsCommand.Execute(null); Assert.Equal(3, settings.Value.SelectedPage);
        vm.CloseReportCommand.Execute(null); Assert.Equal(3, vm.SelectedPage);
        Assert.Equal(3, vm.SelectedNavigation!.Page);
    }
    [Fact] public async Task QuickDetailsAppearOnlyForTheSessionIncludingEmptyResults()
    {
        var (vm, _, _, _, _) = Create(); vm.AgeDaysText = "30";
        Assert.False(vm.HasQuickSession);
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.True(vm.HasQuickSession); Assert.Empty(vm.Results);
        vm.ShowReportCommand.Execute(null); Assert.Equal(2, vm.SelectedPage);
        // Clicking the same home item also exits the task view.
        vm.SelectedNavigation = vm.NavigationItems[0]; Assert.Equal(0, vm.SelectedPage);
        vm.ShowReportCommand.Execute(null); vm.CloseReportCommand.Execute(null);
        Assert.Equal(0, vm.SelectedPage);
        await vm.ScanTemporaryCommand.ExecuteAsync(null); Assert.False(vm.HasQuickSession);
    }
    [Fact] public async Task QuickSessionIsVisibleDuringScanningAndSurvivesCancellation()
    {
        var (vm, files, _, _, _) = Create();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        files.OnRead = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var task = vm.QuickCleanCommand.ExecuteAsync(null);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(vm.HasQuickSession); Assert.True(vm.QuickCleaning);
            vm.ShowReportCommand.Execute(null); Assert.Equal(2, vm.SelectedPage);
            vm.CancelCommand.Execute(null);
        }
        finally { release.Set(); }
        await task;
        Assert.True(vm.HasQuickSession); Assert.False(vm.QuickCleaning); Assert.Empty(files.Executed);
        Assert.Contains("没有删除", vm.QuickSummary);
    }
    [Fact] public async Task NewQuickSessionCannotDisplayResultsFromAnEarlierTaskOnValidationFailure()
    {
        var (vm, _, _, _, _) = Create();
        await vm.QuickCleanCommand.ExecuteAsync(null); Assert.True(vm.HasResults);
        vm.AgeDaysText = "invalid";
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Empty(vm.Results); Assert.True(vm.HasQuickSession);
        Assert.Contains("清理未完成", vm.QuickSummary); Assert.False(vm.Busy);
    }
    [Fact] public async Task ResultsStartUnselectedAndSelectionInvalidatesPreview()
    {
        var (vm, _, _, _, _) = Create(); await vm.ScanTemporaryCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Candidates.Count); Assert.All(vm.Candidates, row => Assert.False(row.Selected));
        vm.Candidates[0].Selected = true; vm.BuildPlanCommand.Execute(null); Assert.Single(vm.PlanItems); Assert.True(vm.ExecutePlanCommand.CanExecute(null));
        vm.Candidates[1].Selected = true; Assert.Empty(vm.PlanItems); Assert.False(vm.ExecutePlanCommand.CanExecute(null));
    }
    [Fact] public async Task ConfirmationDeclinedLeavesFilesUntouched()
    {
        var (vm, files, dialogs, _, _) = Create(); await vm.ScanTemporaryCommand.ExecuteAsync(null);
        vm.Candidates[0].Selected = true; vm.BuildPlanCommand.Execute(null); await vm.ExecutePlanCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.Confirmations); Assert.Empty(files.Executed); Assert.Contains("取消确认", vm.Status);
    }
    [Fact] public async Task ConfirmedPlanOnlyExecutesSelectionAndCannotReplay()
    {
        var (vm, files, dialogs, _, _) = Create(); dialogs.Confirm = true; await vm.ScanTemporaryCommand.ExecuteAsync(null);
        vm.Candidates[0].Selected = true; vm.BuildPlanCommand.Execute(null); await vm.ExecutePlanCommand.ExecuteAsync(null);
        Assert.Single(files.Executed); Assert.Single(vm.Results); Assert.False(vm.ExecutePlanCommand.CanExecute(null)); Assert.False(vm.Busy);
    }
    [Fact] public async Task ModeAndExclusionChangesInvalidatePlan()
    {
        var (vm, _, _, _, _) = Create(); await vm.ScanTemporaryCommand.ExecuteAsync(null); vm.Candidates[0].Selected = true;
        vm.BuildPlanCommand.Execute(null); vm.Permanent = true; Assert.False(vm.ExecutePlanCommand.CanExecute(null));
        vm.BuildPlanCommand.Execute(null); vm.ExclusionsText = @"C:\Private"; Assert.False(vm.ExecutePlanCommand.CanExecute(null));
    }
    [Fact] public async Task ChangedAgeThresholdRequiresRescanBeforePlanning()
    {
        var (vm, _, _, _, _) = Create(); await vm.ScanTemporaryCommand.ExecuteAsync(null); vm.Candidates[0].Selected = true;
        vm.AgeDaysText = "30"; vm.BuildPlanCommand.Execute(null);
        Assert.False(vm.ExecutePlanCommand.CanExecute(null)); Assert.Contains("重新扫描", vm.Status);
    }
    [Fact] public async Task InvalidThresholdIsReportedWithoutScanning()
    {
        var (vm, files, _, _, _) = Create(); vm.AgeDaysText = "-1"; await vm.ScanTemporaryCommand.ExecuteAsync(null);
        Assert.Empty(vm.Candidates); Assert.Empty(files.Executed); Assert.Contains("0～3650", vm.Status); Assert.False(vm.Busy);
    }
    [Fact] public void SettingsSaveAndEmptyBinRequireExplicitActions()
    {
        var (vm, _, _, settings, bin) = Create(); vm.ExclusionsText = "C:\\Private\r\nC:\\Private"; vm.SaveSettingsCommand.Execute(null);
        Assert.Single(settings.Value.ExcludedDirectories); Assert.Equal(0, bin.EmptyCalls);
    }
    [Fact] public async Task DecliningEmptyBinNeverCallsSystemOperation()
    {
        var (vm, _, _, _, bin) = Create(); await vm.EmptyBinCommand.ExecuteAsync(null); Assert.Equal(0, bin.EmptyCalls);
    }
    [Fact] public async Task BusyDisablesNewWorkButCancellationRemainsAvailable()
    {
        var (vm, files, _, _, _) = Create();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        files.OnRead = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var task = vm.ScanTemporaryCommand.ExecuteAsync(null); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(vm.Busy); Assert.False(vm.BuildPlanCommand.CanExecute(null)); Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        vm.CancelCommand.Execute(null); release.Set(); await task; Assert.False(vm.Busy); Assert.Contains("取消", vm.ScanSummary);
    }
    [Fact] public async Task OneClickScansAndDeletesWithoutManualSelectionOrDialog()
    {
        var (vm, files, dialogs, _, bin) = Create();
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Equal(2, files.Executed.Count); Assert.All(files.ExecutedModes, mode => Assert.Equal(CleanupMode.Permanent, mode));
        Assert.Equal(0, dialogs.Confirmations); Assert.Equal(0, bin.EmptyCalls);
        Assert.Equal(2, vm.Results.Count); Assert.Equal(0, vm.SelectedPage); Assert.True(vm.HasResults);
        Assert.False(vm.Busy); Assert.False(vm.QuickCleaning); Assert.Contains("已删除 2", vm.QuickSummary);
        Assert.False(vm.ExecutePlanCommand.CanExecute(null)); Assert.Empty(vm.PlanItems);
    }
    [Fact] public async Task OneClickKeepsRecentAndExcludedFilesEvenWhenManualAgeIsZero()
    {
        var (vm, files, _, _, _) = Create();
        files.Add(@"C:\Test\recent.tmp", days: 1); vm.AgeDaysText = "0"; vm.ExclusionsText = @"C:\Test\a";
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Equal(@"C:\Test\b", Assert.Single(files.Executed)); Assert.Contains("7 天", vm.QuickScope);
    }
    [Fact] public async Task OneClickHonorsLongerAgeAndDoesNotCreateEmptyPlan()
    {
        var (vm, files, dialogs, _, _) = Create(); vm.AgeDaysText = "30";
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Empty(files.Executed); Assert.Empty(vm.Results); Assert.False(vm.HasHistory);
        Assert.Equal(0, dialogs.Confirmations); Assert.Contains("没有需要清理", vm.QuickSummary);
    }
    [Fact] public async Task OneClickNeverUsesPreviousLargeFileSelection()
    {
        var (vm, files, _, _, _) = Create();
        files.Add(@"C:\Outside\valuable.bin", bytes: 300 * 1048576); vm.LargeFolder = @"C:\Outside";
        await vm.ScanLargeCommand.ExecuteAsync(null); Assert.Single(vm.Candidates);
        vm.Candidates[0].Selected = true; vm.BuildPlanCommand.Execute(null);
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Equal(2, files.Executed.Count); Assert.DoesNotContain(@"C:\Outside\valuable.bin", files.Executed);
        Assert.Empty(vm.Candidates); Assert.Empty(vm.PlanItems);
    }
    [Fact] public async Task CancellingOneClickScanNeverStartsDeletion()
    {
        var (vm, files, _, _, _) = Create(); files.OnRead = _ => vm.CancelCommand.Execute(null);
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Empty(files.Executed); Assert.Contains("没有删除任何文件", vm.QuickSummary); Assert.False(vm.Busy);
    }
    [Fact] public async Task CancellingOneClickDeletionRetainsAccuratePartialReport()
    {
        var (vm, files, _, _, _) = Create(); files.OnExecute = () => vm.CancelCommand.Execute(null);
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Single(files.Executed); Assert.Equal(2, vm.Results.Count);
        Assert.Single(vm.Results, x => x.Status == "未执行"); Assert.Contains("已停止", vm.QuickSummary);
    }
    [Fact] public async Task OneClickDoesNotMisreportInaccessibleScopeAsClean()
    {
        var (vm, files, _, _, _) = Create(); files.OnRead = _ => throw new UnauthorizedAccessException();
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Empty(files.Executed); Assert.True(vm.HasScanIssues); Assert.Contains("未完整完成", vm.QuickSummary);
    }
    [Fact] public async Task OneClickErrorRestoresControlsAndReportsFailure()
    {
        var (vm, files, _, _, _) = Create(); vm.AgeDaysText = "invalid";
        await vm.QuickCleanCommand.ExecuteAsync(null);
        Assert.Empty(files.Executed); Assert.False(vm.Busy); Assert.False(vm.QuickCleaning);
        Assert.True(vm.QuickCleanCommand.CanExecute(null)); Assert.Contains("未完成", vm.QuickSummary);
    }
}
