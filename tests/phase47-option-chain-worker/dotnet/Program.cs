using ThesisPulse.Signal.Service;

var queue = new InMemoryOptionChainWorkQueue();
var now = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
var work = new OptionChainWorkItem(
    Guid.NewGuid(),
    Guid.NewGuid(),
    "NSE:NIFTY50",
    now,
    "option-chain-v1",
    "policy-v1",
    OptionChainWorkStatus.Pending,
    0,
    now,
    null,
    null,
    null,
    now,
    now);

Check(await queue.EnqueueAsync(work), "initial enqueue");
Check(!await queue.EnqueueAsync(work), "duplicate enqueue");

var firstLease = await queue.TryLeaseAsync("worker-a", now, TimeSpan.FromSeconds(30));
Check(firstLease is not null, "first lease");
Check(firstLease!.WorkItem.AttemptCount == 1, "first attempt count");
Check(await queue.TryLeaseAsync("worker-b", now.AddSeconds(10), TimeSpan.FromSeconds(30)) is null, "active lease isolation");

var recovered = await queue.TryLeaseAsync("worker-b", now.AddSeconds(31), TimeSpan.FromSeconds(30));
Check(recovered is not null, "expired lease recovery");
Check(recovered!.WorkItem.AttemptCount == 2, "recovered attempt count");
Check(!await queue.CompleteAsync(work.WorkUid, "worker-a", OptionChainWorkStatus.Completed, null, now.AddSeconds(32)), "stale owner rejected");

var retryAt = now.AddMinutes(1);
Check(await queue.RetryAsync(work.WorkUid, "worker-b", retryAt, "TRANSIENT_SQL"), "retry scheduling");
Check(await queue.TryLeaseAsync("worker-c", now.AddSeconds(40), TimeSpan.FromSeconds(30)) is null, "retry delay respected");

var thirdLease = await queue.TryLeaseAsync("worker-c", retryAt, TimeSpan.FromSeconds(30));
Check(thirdLease is not null, "retry lease");
Check(await queue.CompleteAsync(work.WorkUid, "worker-c", OptionChainWorkStatus.Completed, null, retryAt.AddSeconds(1)), "completion");

var metrics = await queue.GetMetricsAsync(retryAt.AddSeconds(2));
Check(metrics.Completed == 1, "completed metric");
Check(metrics.Pending == 0 && metrics.Leased == 0, "terminal queue metrics");

var policy = new OptionChainWorkerPolicy
{
    MaximumAttempts = 5,
    InitialRetryDelay = TimeSpan.FromSeconds(10),
    MaximumRetryDelay = TimeSpan.FromSeconds(60),
};
Check(policy.RetryDelayForAttempt(1) == TimeSpan.FromSeconds(10), "first retry delay");
Check(policy.RetryDelayForAttempt(4) == TimeSpan.FromSeconds(60), "retry delay cap");

Console.WriteLine("PASS: Phase 4.7 option-chain worker acceptance");
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
