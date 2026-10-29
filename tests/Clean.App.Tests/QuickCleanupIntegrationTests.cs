using System.IO;
using Clean.App;
using Clean.Core;
using Clean.Testing;
using Clean.Windows;

namespace Clean.App.Tests;

public sealed class QuickCleanupIntegrationTests
{
    private sealed class NoDialogs : IUserDialogs
    {
        public string? PickFolder() => throw new InvalidOperationException("Unexpected folder dialog");
        public bool ConfirmCleanup(CleanupPlan plan) => throw new InvalidOperationException("One-click must not prompt");
        public bool ConfirmEmptyBin() => throw new InvalidOperationException("Must not touch recycle bin");
        public void OpenRecycleBin() => throw new InvalidOperationException("Must not touch recycle bin");
    }
    [Fact]
    public async Task RealOneClickDeletesOnlyOldFilesInInjectedTestTempAndPersistsHistory()
    {
        var testBase = Path.Combine(Path.GetTempPath(), "Clean.Quick.Tests");
        var owned = Path.Combine(testBase, Guid.NewGuid().ToString("N"));
        var tempRoot = Path.Combine(owned, "temp");
        var stateRoot = Path.Combine(owned, "state");
        Directory.CreateDirectory(tempRoot);
        string Make(string name, int days)
        {
            var path = Path.Combine(tempRoot, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "dedicated quick-clean test"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days)); return path;
        }
        try
        {
            var old = Make("old.tmp", 10); var recent = Make("recent.tmp", 1); var excluded = Make("excluded/keep.tmp", 10);
            var outside = Path.Combine(owned, "outside.txt"); File.WriteAllText(outside, "must remain");
            var fs = new WindowsFileSystem(); var metrics = new FakeMetrics(); var history = new JsonHistoryStore(stateRoot);
            var vm = new MainViewModel(new(fs, new()), new(fs, new(), metrics, history), metrics, metrics,
                new FakeBin(), new JsonSettingsStore(stateRoot), history, new FakeLog(), new NoDialogs(), new(tempRoot, stateRoot));
            vm.ExclusionsText = Path.GetDirectoryName(excluded)!;
            await vm.QuickCleanCommand.ExecuteAsync(null);
            Assert.False(File.Exists(old)); Assert.True(File.Exists(recent)); Assert.True(File.Exists(excluded)); Assert.True(File.Exists(outside));
            Assert.Equal("成功", Assert.Single(vm.Results).Status); Assert.Equal("永久删除", vm.Results[0].Mode);
            var entry = Assert.Single(history.Load()); Assert.Equal(1, entry.Deleted); Assert.Equal(0, entry.Recycled); Assert.Equal("已完成", entry.State);
            await vm.QuickCleanCommand.ExecuteAsync(null);
            Assert.Empty(vm.Results); Assert.Contains("没有需要清理", vm.QuickSummary); Assert.Single(history.Load());
        }
        finally
        {
            if (!PathPolicy.Within(owned, testBase) || owned == testBase) throw new InvalidOperationException("Test boundary violated");
            if (Directory.Exists(owned)) Directory.Delete(owned, true);
        }
    }
}
