using Clean.Core;
using Clean.Testing;

namespace Clean.Core.Tests;

public sealed class WorkflowTests
{
    [Fact] public async Task ScanFiltersAgeSizeAndExclusionsWithoutWrites()
    {
        var fs = new FakeFiles(); fs.Add(@"C:\Test\old.tmp"); fs.Add(@"C:\Test\new.tmp", days: 1);
        fs.Add(@"C:\Test\private.tmp");
        var result = await new ScanService(fs, new()).ScanAsync(ScanRule.Temporary(@"C:\Test"), [@"C:\Test\private.tmp"]);
        Assert.Equal(@"C:\Test\old.tmp", Assert.Single(result.Candidates).Path); Assert.Empty(fs.Executed);
        Assert.Equal(2, result.Skipped); Assert.Equal(3, fs.Files.Count);
    }
    [Fact] public async Task LargeResultsSortedAndNotAgeFiltered()
    {
        var fs = new FakeFiles(); fs.Add(@"C:\Test\a", days: 0, bytes: 200); fs.Add(@"C:\Test\b", bytes: 100); fs.Add(@"C:\Test\c", bytes: 50);
        var result = await new ScanService(fs, new()).ScanAsync(ScanRule.LargeFiles(@"C:\Test", 100), []);
        Assert.Equal(new long[] { 200, 100 }, result.Candidates.Select(x => x.Bytes));
    }
    [Fact] public async Task ReparseEntryIsNeverTraversed()
    {
        var fs = new FakeFiles(); fs.Directories[@"C:\Test"] = [new(@"C:\Test\link", true, true)];
        fs.Add(@"C:\Test\link\secret");
        var result = await new ScanService(fs, new()).ScanAsync(ScanRule.Temporary(@"C:\Test"), []);
        Assert.Empty(result.Candidates); Assert.Equal(1, result.Skipped);
    }
    [Fact] public async Task ScanCancellationKeepsPartialResults()
    {
        var fs = new FakeFiles(); fs.Add(@"C:\Test\a"); fs.Add(@"C:\Test\b"); using var cancel = new CancellationTokenSource();
        fs.OnRead = _ => cancel.Cancel();
        var result = await new ScanService(fs, new()).ScanAsync(ScanRule.Temporary(@"C:\Test"), [], cancellationToken: cancel.Token);
        Assert.True(result.Cancelled); Assert.Single(result.Candidates); Assert.Empty(fs.Executed);
    }
    [Fact] public async Task AccessFailureDoesNotStopScan()
    {
        var fs = new FakeFiles(); fs.Add(@"C:\Test\a"); fs.Add(@"C:\Test\b");
        fs.OnRead = path => { if (path.EndsWith('a')) throw new UnauthorizedAccessException(); };
        var result = await new ScanService(fs, new()).ScanAsync(ScanRule.Temporary(@"C:\Test"), []);
        Assert.Single(result.Candidates); Assert.Equal(ErrorKind.AccessDenied, Assert.Single(result.Issues).Kind);
    }
    [Fact] public async Task ChangedIdentitySkippedEvenWithSameSizeAndTime()
    {
        var fs = new FakeFiles(); var item = fs.Add(@"C:\Test\a"); fs.Files[item.Path] = item.File with { Identity = "replacement" };
        var report = await Execute(fs, [item]); Assert.Equal(1, report.Skipped); Assert.Empty(fs.Executed);
    }
    [Fact] public async Task ChangedAncestorSkipped()
    {
        var fs = new FakeFiles(); var item = fs.Add(@"C:\Test\a"); fs.Files[item.Path] = item.File with { AncestorIdentity = "other" };
        Assert.Equal(1, (await Execute(fs, [item])).Skipped); Assert.Empty(fs.Executed);
    }
    [Fact] public async Task ExclusionsRecheckedAtExecution()
    {
        var fs = new FakeFiles(); var item = fs.Add(@"C:\Test\a");
        Assert.Equal(1, (await Execute(fs, [item], exclusions: [@"C:\Test"])).Skipped); Assert.Empty(fs.Executed);
    }
    [Fact] public async Task CancellationDoesNotRollBackCompletedItem()
    {
        var fs = new FakeFiles(); var a = fs.Add(@"C:\Test\a"); var b = fs.Add(@"C:\Test\b");
        using var cancel = new CancellationTokenSource(); fs.OnExecute = cancel.Cancel;
        var report = await Execute(fs, [a, b], token: cancel.Token);
        Assert.Equal(1, report.Recycled); Assert.Equal(1, report.NotExecuted); Assert.Equal(2, report.Items.Length); Assert.Single(fs.Executed);
    }
    [Fact] public async Task MixedReportAccountsForEveryItem()
    {
        var fs = new FakeFiles(); var a = fs.Add(@"C:\Test\a"); var b = fs.Add(@"C:\Test\b"); var c = fs.Add(@"C:\Test\c");
        fs.Files.Remove(b.Path); fs.OnRead = path => { if (path.EndsWith('c')) throw new IOException("locked", unchecked((int)0x80070020)); };
        var report = await Execute(fs, [a, b, c]);
        Assert.Equal(1, report.Recycled); Assert.Equal(1, report.Skipped); Assert.Equal(1, report.Failed); Assert.Equal(100, report.ProcessedBytes);
    }
    [Fact] public async Task RecycleFailureNeverFallsBackToPermanent()
    {
        var fs = new FakeFiles { Outcome = new(ItemStatus.Failed, ErrorKind.Unavailable, "unsupported") };
        var report = await Execute(fs, [fs.Add(@"C:\Test\a")]);
        Assert.Equal(0, report.Deleted); Assert.Equal(1, report.Failed); Assert.Single(fs.Executed);
    }
    [Fact] public async Task WrongConfirmationAndReplayAreRejected()
    {
        var fs = new FakeFiles(); var plan = new CleanupPlan([fs.Add(@"C:\Test\a")], [], CleanupMode.Permanent);
        var service = new CleanupService(fs, new(), new FakeMetrics(), new FakeHistory());
        Assert.Throws<InvalidOperationException>(() => service.ExecuteAsync(plan, new(plan.Id, CleanupMode.Recycle)));
        await service.ExecuteAsync(plan, new(plan.Id, plan.Mode));
        Assert.Throws<InvalidOperationException>(() => service.ExecuteAsync(plan, new(plan.Id, plan.Mode)));
        Assert.Single(fs.Executed);
    }
    [Fact] public async Task JournalFailureStopsFutureDeletion()
    {
        var fs = new FakeFiles(); var items = new[] { fs.Add(@"C:\Test\a"), fs.Add(@"C:\Test\b") };
        var report = await Execute(fs, items, history: new FakeHistory { FailAfter = 1 });
        Assert.Single(fs.Executed); Assert.Equal(1, report.NotExecuted); Assert.NotNull(report.PersistenceError);
    }
    [Fact] public async Task InitialJournalFailurePreventsAnyDeletion()
    {
        var fs = new FakeFiles(); var item = fs.Add(@"C:\Test\a");
        await Assert.ThrowsAsync<IOException>(() => Execute(fs, [item], history: new FakeHistory { FailAfter = 0 })); Assert.Empty(fs.Executed);
    }
    [Fact] public void PlanSnapshotsSelectionAndDeduplicates()
    {
        var fs = new FakeFiles(); var item = fs.Add(@"C:\Test\a"); var list = new List<ScanCandidate> { item, item };
        var plan = new CleanupPlan(list, [], CleanupMode.Recycle); list.Clear(); Assert.Single(plan.Items);
    }
    private static Task<CleanupReport> Execute(FakeFiles fs, IEnumerable<ScanCandidate> items, string[]? exclusions = null,
        CancellationToken token = default, FakeHistory? history = null)
    {
        var plan = new CleanupPlan(items, exclusions ?? [], CleanupMode.Recycle);
        return new CleanupService(fs, new(), new FakeMetrics(), history ?? new()).ExecuteAsync(plan, new(plan.Id, plan.Mode), cancellationToken: token);
    }
}
