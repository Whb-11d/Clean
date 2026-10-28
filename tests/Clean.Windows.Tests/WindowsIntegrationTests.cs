using System.Diagnostics;
using System.Runtime.InteropServices;
using Clean.Core;
using Clean.Windows;

namespace Clean.Windows.Tests;

public sealed class TestDirectory : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "Clean.Tests");
    public string Root { get; }
    public TestDirectory() { Root = Path.Combine(_base, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
    public string File(string name, string contents = "test data", int age = 10)
    {
        var path = Path.Combine(Root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents); System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age)); return path;
    }
    public void Dispose()
    {
        if (!PathPolicy.Within(Root, _base) || Root == _base) throw new InvalidOperationException("Test cleanup boundary violated");
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

public sealed class WindowsIntegrationTests
{
    [Fact] public async Task RealScanIsReadOnlyAndRespectsAgeExclusion()
    {
        using var dir = new TestDirectory();
        var old = dir.File("old.tmp"); dir.File("new.tmp", age: 1); var excluded = dir.File("excluded/private.tmp");
        var before = File.ReadAllBytes(old); var modified = File.GetLastWriteTimeUtc(old);
        var result = await new ScanService(new WindowsFileSystem(), new()).ScanAsync(ScanRule.Temporary(dir.Root), [Path.GetDirectoryName(excluded)!]);
        Assert.Empty(result.Issues); Assert.Equal(old, Assert.Single(result.Candidates).Path);
        Assert.Equal(before, File.ReadAllBytes(old)); Assert.Equal(modified, File.GetLastWriteTimeUtc(old)); Assert.True(File.Exists(excluded));
    }
    [Fact] public void PermanentDeleteUsesVerifiedFile()
    {
        using var dir = new TestDirectory(); var path = dir.File("delete.tmp"); var fs = new WindowsFileSystem();
        var result = fs.ExecuteVerified(fs.ReadSnapshot(path), CleanupMode.Permanent, default);
        Assert.Equal(ItemStatus.Succeeded, result.Status); Assert.False(File.Exists(path));
    }
    [Fact] public void ReplacedFileWithSameMetadataIsSkipped()
    {
        using var dir = new TestDirectory(); var path = dir.File("replace.tmp"); var fs = new WindowsFileSystem(); var snapshot = fs.ReadSnapshot(path);
        File.Move(path, path + ".original"); dir.File("replace.tmp");
        File.SetCreationTimeUtc(path, snapshot.CreatedUtc.UtcDateTime); File.SetLastWriteTimeUtc(path, snapshot.LastWriteUtc.UtcDateTime);
        var result = fs.ExecuteVerified(snapshot, CleanupMode.Permanent, default);
        Assert.Equal(ItemStatus.Skipped, result.Status); Assert.True(File.Exists(path)); Assert.True(File.Exists(path + ".original"));
    }
    [Fact] public void MovedFileIntoReplacedParentIsSkipped()
    {
        using var dir = new TestDirectory(); var path = dir.File("parent/file.tmp"); var fs = new WindowsFileSystem(); var snapshot = fs.ReadSnapshot(path);
        Directory.Move(Path.Combine(dir.Root, "parent"), Path.Combine(dir.Root, "old-parent")); Directory.CreateDirectory(Path.Combine(dir.Root, "parent"));
        File.Move(Path.Combine(dir.Root, "old-parent/file.tmp"), path);
        Assert.Equal(ItemStatus.Skipped, fs.ExecuteVerified(snapshot, CleanupMode.Permanent, default).Status); Assert.True(File.Exists(path));
    }
    [Fact] public void LockedFileFailsWithoutDeletion()
    {
        using var dir = new TestDirectory(); var path = dir.File("locked.tmp"); var fs = new WindowsFileSystem(); var snapshot = fs.ReadSnapshot(path);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = fs.ExecuteVerified(snapshot, CleanupMode.Permanent, default);
        Assert.Equal(ItemStatus.Failed, result.Status); Assert.Equal(ErrorKind.InUse, result.Error); Assert.True(File.Exists(path));
    }
    [Fact] public void ReadOnlyFileIsSkipped()
    {
        using var dir = new TestDirectory(); var path = dir.File("readonly.tmp"); File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var fs = new WindowsFileSystem(); Assert.Equal(ItemStatus.Skipped, fs.ExecuteVerified(fs.ReadSnapshot(path), CleanupMode.Permanent, default).Status);
            Assert.True(File.Exists(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
    [Fact] public void HardLinkedFileIsSkipped()
    {
        using var dir = new TestDirectory(); var path = dir.File("linked.tmp"); var link = Path.Combine(dir.Root, "second.tmp");
        Assert.True(CreateHardLinkW(link, path, 0)); var fs = new WindowsFileSystem();
        Assert.Equal(ItemStatus.Skipped, fs.ExecuteVerified(fs.ReadSnapshot(path), CleanupMode.Permanent, default).Status);
        Assert.True(File.Exists(path)); Assert.True(File.Exists(link));
    }
    [Fact] public async Task JunctionCannotEscapeSelectedRoot()
    {
        using var selected = new TestDirectory(); using var outside = new TestDirectory(); outside.File("private.tmp");
        var link = Path.Combine(selected.Root, "junction");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        { ArgumentList = { "/d", "/c", "mklink", "/J", link, outside.Root }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        try
        {
            var fs = new WindowsFileSystem();
            var result = await new ScanService(fs, new()).ScanAsync(ScanRule.Temporary(selected.Root), []);
            Assert.Empty(result.Candidates); Assert.Equal(1, result.Skipped);
            Assert.Throws<PolicyException>(() => fs.ReadSnapshot(Path.Combine(link, "private.tmp")));
        }
        finally { Directory.Delete(link); }
        Assert.True(File.Exists(Path.Combine(outside.Root, "private.tmp")));
    }
    [Fact] public void CancellationBeforeOperationDoesNotDelete()
    {
        using var dir = new TestDirectory(); var path = dir.File("cancel.tmp"); var fs = new WindowsFileSystem();
        Assert.Equal(ItemStatus.NotExecuted, fs.ExecuteVerified(fs.ReadSnapshot(path), CleanupMode.Permanent, new CancellationToken(true)).Status);
        Assert.True(File.Exists(path));
    }
    [Fact] public void RealRecycleMovesOnlyDedicatedTestFile()
    {
        using var dir = new TestDirectory(); var path = dir.File("Clean-recycle-test-" + Guid.NewGuid().ToString("N") + ".txt");
        var fs = new WindowsFileSystem(); var result = fs.ExecuteVerified(fs.ReadSnapshot(path), CleanupMode.Recycle, default);
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message); Assert.False(File.Exists(path));
        // Intentionally do NOT empty the developer's recycle bin. This tiny generated file may be restored there.
    }
    [Fact] public void MetricsSampleAndReleaseProcessHandles()
    {
        var metrics = new SystemMetrics(); var before = Process.GetCurrentProcess().HandleCount;
        for (int i = 0; i < 5; i++)
        {
            var memory = metrics.Sample(true); Assert.True(memory.TotalBytes > 0); Assert.InRange(memory.AvailableBytes, 0ul, memory.TotalBytes);
            Assert.NotEmpty(memory.Processes); Assert.Equal(memory.Processes.OrderByDescending(x => x.WorkingSetBytes), memory.Processes);
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        Assert.True(Process.GetCurrentProcess().HandleCount - before < 100);
        Assert.NotEmpty(metrics.GetDisks()); Assert.Null(new ShellRecycleBin().Query().Error);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
}
