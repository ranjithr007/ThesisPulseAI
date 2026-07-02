using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

internal static class WorkProcessorAcceptanceTests
{
    public static async Task<IReadOnlyCollection<string>> RunAsync()
    {
        var failures = new List<string>();

        await CheckAsync(failures, "READY plan completes work item", async () =>
        {
            var queue = new RecordingQueue();
            var state = new AutomaticTradePlanWorkerState();
            var processor = CreateProcessor(queue, new RecordingResultStore("CREATED"), state, maximumAttempts: 3);
            await processor.ProcessAsync(CreateItem(attemptCount: 1), CancellationToken.None);

            Equal("COMPLETED", queue.LastStatus);
            Equal(1L, state.Snapshot().Completed);
            Equal(0L, state.Snapshot().Duplicates);
        });

        await CheckAsync(failures, "committed duplicate completes without second plan", async () =>
        {
            var queue = new RecordingQueue();
            var state = new AutomaticTradePlanWorkerState();
            var processor = CreateProcessor(queue, new RecordingResultStore("DUPLICATE"), state, maximumAttempts: 3);
            await processor.ProcessAsync(CreateItem(attemptCount: 2), CancellationToken.None);

            Equal("COMPLETED", queue.LastStatus);
            Equal(1L, state.Snapshot().Completed);
            Equal(1L, state.Snapshot().Duplicates);
        });

        await CheckAsync(failures, "transient persistence failure schedules retry", async () =>
        {
            var queue = new RecordingQueue();
            var state = new AutomaticTradePlanWorkerState();
            var before = DateTimeOffset.UtcNow;
            var processor = CreateProcessor(
                queue,
                new RecordingResultStore(new TimeoutException("transient")),
                state,
                maximumAttempts: 3);
            await processor.ProcessAsync(CreateItem(attemptCount: 1), CancellationToken.None);

            Equal("RETRY_PENDING", queue.LastStatus);
            Require(queue.AvailableAtUtc > before);
            Equal(1L, state.Snapshot().Retried);
            Equal(0L, state.Snapshot().Failed);
        });

        await CheckAsync(failures, "retry exhaustion fails terminally", async () =>
        {
            var queue = new RecordingQueue();
            var state = new AutomaticTradePlanWorkerState();
            var processor = CreateProcessor(
                queue,
                new RecordingResultStore(new TimeoutException("exhausted")),
                state,
                maximumAttempts: 3);
            await processor.ProcessAsync(CreateItem(attemptCount: 3), CancellationToken.None);

            Equal("FAILED", queue.LastStatus);
            Equal(1L, state.Snapshot().Failed);
            Equal(0L, state.Snapshot().Retried);
        });

        await CheckAsync(failures, "expired intake never reaches builder persistence", async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var item = CreateItem(attemptCount: 1, now) with
            {
                Intake = CreateIntake(now) with
                {
                    Signal = CreateIntake(now).Signal with
                    {
                        EntryClosesAtUtc = now.AddSeconds(-1),
                        ValidUntilUtc = now.AddSeconds(-1),
                    },
                },
            };
            var queue = new RecordingQueue();
            var store = new RecordingResultStore("CREATED");
            var state = new AutomaticTradePlanWorkerState();
            var processor = CreateProcessor(queue, store, state, maximumAttempts: 3);
            await processor.ProcessAsync(item, CancellationToken.None);

            Equal("EXPIRED", queue.LastStatus);
            Equal(0, store.Calls);
            Equal(1L, state.Snapshot().Expired);
        });

        await CheckAsync(failures, "in-memory metrics expose worker terminal counts", async () =>
        {
            var state = new AutomaticTradePlanWorkerState();
            state.Leased(4);
            state.Completed(false);
            state.Completed(true);
            state.Rejected();
            state.Expired();
            state.Retried();
            state.Failed();
            var snapshot = await new InMemoryAutomaticTradePlanMetricsStore(state)
                .ReadAsync(CancellationToken.None);

            Equal(4L, snapshot.Worker.Leased);
            Equal(2L, snapshot.Completed);
            Equal(1L, snapshot.Ready);
            Equal(1L, snapshot.Worker.Duplicates);
            Equal(1L, snapshot.Rejected);
            Equal(1L, snapshot.Expired);
            Equal(1L, snapshot.RetryPending);
            Equal(1L, snapshot.Failed);
        });

        return failures;
    }

    private static AutomaticTradePlanWorkProcessor CreateProcessor(
        RecordingQueue queue,
        RecordingResultStore store,
        AutomaticTradePlanWorkerState state,
        int maximumAttempts) => new(
            new AutomaticTradePlanWorkerOptions
            {
                Enabled = true,
                PollIntervalSeconds = 5,
                BatchSize = 10,
                MaximumAttempts = maximumAttempts,
            },
            queue,
            new DeterministicAutomaticTradePlanProjector(),
            new DeterministicTradePlanBuilder(Options.Create(new DeterministicTradePlanOptions())),
            store,
            state,
            NullLogger<AutomaticTradePlanWorkProcessor>.Instance);

    private static AutomaticTradePlanWorkItem CreateItem(
        int attemptCount,
        DateTimeOffset? now = null)
    {
        var intake = CreateIntake(now ?? DateTimeOffset.UtcNow);
        var command = new DeterministicAutomaticTradePlanProjector().Project(intake).Command
            ?? throw new InvalidOperationException("Acceptance intake did not project.");
        return new AutomaticTradePlanWorkItem(
            101,
            intake.MessageUid,
            command.CommandUid,
            intake.RiskDecision.RiskDecisionUid,
            intake,
            attemptCount);
    }

    private static AutomaticTradePlanIntakeV1 CreateIntake(DateTimeOffset now)
    {
        var signalUid = Guid.NewGuid();
        var thesisUid = Guid.NewGuid();
        var decision = new RiskDecisionV1(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("D"), signalUid, thesisUid,
            "NSE_EQ|INE002A01018", "PAPER", EvidenceDirectionV1.Long,
            RiskDecisionContractV1.Approved, Array.Empty<string>(), Array.Empty<RiskCheckV1>(),
            new RiskBudgetV1(10000m, 200000m, 100m, now.AddMinutes(2)),
            "risk-policy-v1.0.0", "deterministic-risk-v1.0.0", now.AddSeconds(-2));
        var signal = new SignalGeneratedV1(
            signalUid, decision.InstrumentKey, "THESIS_FUSION", "deterministic-thesis-fusion-v1.0.0",
            "LONG", "5m", new[] { "5m", "15m" }, 0.8m, 0.75m,
            now.AddSeconds(-10), now.AddMinutes(1), 100m, 99.5m, 100.5m, 95m,
            "Deterministic invalidation.", 30, now.AddSeconds(-10), now.AddMinutes(2),
            "fusion-policy-v1.0.0", Array.Empty<SignalEvidenceV1>());
        return new AutomaticTradePlanIntakeV1(
            Guid.NewGuid(),
            decision.CorrelationId,
            Guid.NewGuid(),
            decision,
            signal,
            new TradePlanInstrumentContextV1(1m, null, 1m, true),
            new TradePlanExecutionContextV1(
                "INTRADAY",
                "MARKET",
                "STOP_MARKET",
                null,
                0.001m,
                "DAY",
                new[]
                {
                    new TradePlanTargetPolicyV1(1, 2m, 0.5m),
                    new TradePlanTargetPolicyV1(2, 3m, 0.5m),
                },
                new TradeSessionV1(
                    DateOnly.FromDateTime(now.UtcDateTime),
                    now.AddMinutes(-1),
                    now.AddMinutes(30),
                    now.AddHours(5)),
                new ExitPolicyV1(true, true, true, true, "exit-policy-v1.0.0"),
                "execution-policy-v1.0.0"),
            now);
    }

    private static async Task CheckAsync(
        ICollection<string> failures,
        string name,
        Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Acceptance assertion failed.");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
    }

    private sealed class RecordingResultStore : IAutomaticTradePlanResultStore
    {
        private readonly string? _outcome;
        private readonly Exception? _exception;

        public RecordingResultStore(string outcome) => _outcome = outcome;
        public RecordingResultStore(Exception exception) => _exception = exception;
        public int Calls { get; private set; }

        public Task<AutomaticTradePlanPersistenceResult> PersistReadyAsync(
            AutomaticTradePlanCommandV1 command,
            TradePlanBuildResultV1 result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (_exception is not null) throw _exception;
            return Task.FromResult(new AutomaticTradePlanPersistenceResult(
                _outcome!,
                result.TradePlan!.TradePlanUid,
                501));
        }
    }

    private sealed class RecordingQueue : IAutomaticTradePlanWorkQueue
    {
        public string? LastStatus { get; private set; }
        public DateTimeOffset? AvailableAtUtc { get; private set; }

        public Task<AutomaticTradePlanEnqueueResult> EnqueueAsync(
            AutomaticTradePlanIntakeV1 intake,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AutomaticTradePlanEnqueueResult(
                "ENQUEUED", intake.MessageUid, intake.RiskDecision.RiskDecisionUid, Array.Empty<string>()));

        public Task<IReadOnlyCollection<AutomaticTradePlanWorkItem>> LeaseAsync(
            int maximumCount,
            string leaseOwner,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<AutomaticTradePlanWorkItem>>(Array.Empty<AutomaticTradePlanWorkItem>());

        public Task CompleteAsync(long workItemId, CancellationToken cancellationToken) =>
            Set("COMPLETED");

        public Task RetryAsync(
            long workItemId,
            string error,
            DateTimeOffset availableAtUtc,
            CancellationToken cancellationToken)
        {
            AvailableAtUtc = availableAtUtc;
            return Set("RETRY_PENDING");
        }

        public Task RejectAsync(long workItemId, string reason, CancellationToken cancellationToken) =>
            Set("REJECTED");

        public Task ExpireAsync(long workItemId, string reason, CancellationToken cancellationToken) =>
            Set("EXPIRED");

        public Task FailAsync(long workItemId, string error, CancellationToken cancellationToken) =>
            Set("FAILED");

        private Task Set(string status)
        {
            LastStatus = status;
            return Task.CompletedTask;
        }
    }
}
