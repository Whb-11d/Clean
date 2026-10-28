using System.Collections.Immutable;
using System.Diagnostics;

namespace Clean.Core;

public sealed class ScanService(IFileSystem fileSystem, PathPolicy policy)
{
    public const int CandidateLimit = 100_000;
    public const int ErrorSampleLimit = 1000;
    public Task<ScanResult> ScanAsync(ScanRule rule, IEnumerable<string> exclusions,
        IProgress<WorkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        rule.Validate();
        var excluded = exclusions.Select(PathPolicy.Normalize).ToImmutableArray();
        rule = rule with { Root = PathPolicy.Normalize(rule.Root) };
        return Task.Run(() => Scan(rule, excluded, progress, cancellationToken), CancellationToken.None);
    }
    private ScanResult Scan(ScanRule rule, ImmutableArray<string> exclusions, IProgress<WorkProgress>? progress, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var candidates = ImmutableArray.CreateBuilder<ScanCandidate>();
        var issues = ImmutableArray.CreateBuilder<ScanIssue>();
        long visited = 0, skipped = 0, errors = 0;
        bool truncated = false;
        var watch = Stopwatch.StartNew();
        void Issue(string path, Exception e)
        {
            errors++;
            if (issues.Count < ErrorSampleLimit) issues.Add(new(path, Errors.Classify(e), Errors.Describe(e)));
        }
        void Walk(string directory, int depth)
        {
            if (token.IsCancellationRequested || truncated) return;
            if (depth > 128) { skipped++; Issue(directory, new PolicyException("超过目录深度上限 128。")); return; }
            try
            {
                fileSystem.ValidateRoot(directory);
                foreach (var entry in fileSystem.Enumerate(directory))
                {
                    if (token.IsCancellationRequested || truncated) break;
                    visited++;
                    if (watch.ElapsedMilliseconds >= 100)
                    {
                        progress?.Report(new("扫描", visited, candidates.Count, skipped));
                        watch.Restart();
                    }
                    try
                    {
                        if (entry.IsReparsePoint || !policy.Allows(entry.Path, rule.Root, exclusions)) { skipped++; continue; }
                        if (entry.IsDirectory) { Walk(entry.Path, depth + 1); continue; }
                        var snapshot = fileSystem.ReadSnapshot(entry.Path);
                        if (!rule.Matches(snapshot, started)) { skipped++; continue; }
                        candidates.Add(new(Guid.NewGuid(), snapshot, rule));
                        if (candidates.Count >= CandidateLimit) truncated = true;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Issue(entry.Path, e); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Issue(directory, e); }
        }
        if (!policy.Allows(rule.Root, rule.Root, exclusions)) Issue(rule.Root, new PolicyException("扫描根目录受保护或已排除。"));
        else Walk(rule.Root, 0);
        progress?.Report(new(token.IsCancellationRequested ? "已取消" : "扫描完成", visited, candidates.Count, skipped));
        return new(candidates.OrderByDescending(x => x.Bytes).ToImmutableArray(), issues.ToImmutable(), visited, skipped,
            errors, token.IsCancellationRequested, truncated, started, DateTimeOffset.UtcNow);
    }
}
