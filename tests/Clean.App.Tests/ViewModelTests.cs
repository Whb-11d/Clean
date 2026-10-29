using Clean.App;
using Clean.Core;
using Clean.Testing;

namespace Clean.App.Tests;

public sealed class ViewModelTests
{
    private sealed class Dialogs : IUserDialogs
    {
        public bool Confirm { get; set; }
        public int Confirmations { get; private set; }
        public string? PickFolder() => @"C:\Test";
        public bool ConfirmCleanup(CleanupPlan plan) { Confirmations++; return Confirm; }
        public bool ConfirmEmptyBin() => Confirm;
        public void OpenRecycleBin() { }
    }
    private static (MainViewModel Vm, FakeFiles Files, Dialogs Dialogs, FakeSettings Settings, FakeBin Bin) Create()
    {
        var files = new FakeFiles(); files.Add(@"C:\Test\a", bytes: 200 * 1048576); files.Add(@"C:\Test\b", bytes: 150 * 1048576);
        var metrics = new FakeMetrics(); var history = new FakeHistory(); var dialogs = new Dialogs(); var settings = new FakeSettings(); var bin = new FakeBin();
        return (new(new(files, new()), new(files, new(), metrics, history), metrics, metrics, bin, settings, history, new FakeLog(), dialogs,
            new(@"C:\Test", @"C:\State")), files, dialogs, settings, bin);
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
        Assert.Single(vm.Results.Where(x => x.Status == "未执行")); Assert.Contains("已停止", vm.QuickSummary);
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
