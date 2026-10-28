using System.Diagnostics;
using Clean.Core;
using Clean.Windows;
using Xunit.Abstractions;

namespace Clean.Windows.Tests;

public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute() { if (Environment.GetEnvironmentVariable("CLEAN_RUN_BENCHMARK") != "1") Skip = "设置 CLEAN_RUN_BENCHMARK=1 运行十万文件测试。"; }
}
public sealed class PerformanceTests(ITestOutputHelper output)
{
    [BenchmarkFact]
    public async Task HundredThousandFilesAndPromptCancellation()
    {
        using var dir = new TestDirectory(); var creation = Stopwatch.StartNew();
        for (int folder = 0; folder < 100; folder++)
        {
            var child = Path.Combine(dir.Root, folder.ToString()); Directory.CreateDirectory(child);
            for (int file = 0; file < 1000; file++) File.WriteAllText(Path.Combine(child, file + ".tmp"), "x");
        }
        output.WriteLine($"Creation: {creation.Elapsed.TotalSeconds:F2}s");
        var scan = new ScanService(new WindowsFileSystem(), new()); var timer = Stopwatch.StartNew();
        var result = await scan.ScanAsync(ScanRule.LargeFiles(dir.Root, 1), []);
        output.WriteLine($"Scan: {timer.Elapsed.TotalSeconds:F2}s; files={result.Candidates.Length}; workingSet={Process.GetCurrentProcess().WorkingSet64}; peak={Process.GetCurrentProcess().PeakWorkingSet64}");
        Assert.Equal(100000, result.Candidates.Length); Assert.Empty(result.Issues); Assert.True(result.Truncated);
        using var cancellation = new CancellationTokenSource();
        var task = scan.ScanAsync(ScanRule.LargeFiles(dir.Root, 1), [], cancellationToken: cancellation.Token);
        await Task.Delay(150); timer.Restart(); cancellation.Cancel(); var partial = await task;
        output.WriteLine($"Cancel latency: {timer.Elapsed.TotalMilliseconds:F0}ms; partial={partial.Candidates.Length}");
        Assert.True(partial.Cancelled); Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
    }
}
