using System.Collections.Immutable;

namespace Clean.Core;

public sealed class CleanupService(IFileSystem fileSystem, PathPolicy policy, IDiskInfoProvider disks, ICleanupHistoryStore history)
{
    public Task<CleanupReport> ExecuteAsync(CleanupPlan plan, CleanupConfirmation confirmation,
        IProgress<WorkProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        plan.Begin(confirmation);
        return Task.Run(() => Execute(plan, progress, cancellationToken), CancellationToken.None);
    }
    private CleanupReport Execute(CleanupPlan plan, IProgress<WorkProgress>? progress, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var before = disks.GetDisks();
        var results = ImmutableArray.CreateBuilder<CleanupItemResult>();
        string? persistenceError = null;
        HistoryEntry Summary(string state) => new(plan.Id, started, state, plan.Items.Length,
            results.Count(x => x.Status == ItemStatus.Succeeded && x.Mode == CleanupMode.Recycle),
            results.Count(x => x.Status == ItemStatus.Succeeded && x.Mode == CleanupMode.Permanent),
            results.Count(x => x.Status == ItemStatus.Failed), results.Count(x => x.Status == ItemStatus.Skipped),
            plan.Items.Length - results.Count(x => x.Status != ItemStatus.NotExecuted),
            results.Where(x => x.Status == ItemStatus.Succeeded).Sum(x => x.Candidate.Bytes));
        // A durable intent is a prerequisite for any deletion.
        history.Save(Summary("进行中"));
        foreach (var candidate in plan.Items)
        {
            ActionOutcome outcome;
            if (token.IsCancellationRequested || persistenceError is not null)
                outcome = new(ItemStatus.NotExecuted, ErrorKind.Cancelled, persistenceError ?? "已停止后续调度；已完成操作不会回滚。");
            else
            {
                try
                {
                    candidate.Rule.Validate();
                    if (!policy.Allows(candidate.Path, candidate.Rule.Root, plan.Exclusions))
                        outcome = new(ItemStatus.Skipped, ErrorKind.Policy, "路径超出规则范围、受保护或已排除。");
                    else
                    {
                        var current = fileSystem.ReadSnapshot(candidate.Path);
                        if (current != candidate.File || !candidate.Rule.Matches(current, DateTimeOffset.UtcNow))
                            outcome = new(ItemStatus.Skipped, ErrorKind.Changed, "文件身份、目录身份或元数据已变化，请重新扫描。");
                        else if (token.IsCancellationRequested)
                            outcome = new(ItemStatus.NotExecuted, ErrorKind.Cancelled, "已取消。");
                        else outcome = fileSystem.ExecuteVerified(candidate.File, plan.Mode, token);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
                {
                    outcome = new(e is PolicyException or FileNotFoundException or DirectoryNotFoundException ? ItemStatus.Skipped : ItemStatus.Failed,
                        Errors.Classify(e), Errors.Describe(e));
                }
            }
            results.Add(new(candidate, plan.Mode, outcome.Status, outcome.Error, outcome.Message));
            progress?.Report(new("清理", results.Count, plan.Items.Length, results.Count(x => x.Status == ItemStatus.Skipped)));
            if (persistenceError is null)
            {
                try { history.Save(Summary("进行中")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { persistenceError = "历史写入失败，已停止后续清理：" + Errors.Describe(e); }
            }
        }
        if (persistenceError is null)
        {
            try { history.Save(Summary(token.IsCancellationRequested ? "已停止" : "已完成")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { persistenceError = "最终历史写入失败：" + Errors.Describe(e); }
        }
        return new(plan.Id, started, DateTimeOffset.UtcNow, results.ToImmutable(), before, disks.GetDisks(), persistenceError);
    }
}
