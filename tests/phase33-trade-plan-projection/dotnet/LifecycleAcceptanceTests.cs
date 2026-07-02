using Microsoft.Extensions.Options;
using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

internal static class LifecycleAcceptanceTests
{
    public static IReadOnlyCollection<string> Run()
    {
        var failures = new List<string>();
        var projector = new DeterministicAutomaticTradePlanProjector();
        var builder = new DeterministicTradePlanBuilder(Options.Create(new DeterministicTradePlanOptions()));

        Check(failures, "lifecycle persists BUILDING then READY and duplicate replay", () =>
        {
            var store = new InMemoryAutomaticTradePlanLifecycleStore();
            var coordinator = new AutomaticTradePlanCoordinator(projector, builder, store);
            var intake = CreateIntake(DateTimeOffset.UtcNow);
            var first = coordinator.Build(intake);
            var second = coordinator.Build(intake);

            Equal(AutomaticTradePlanLifecycleStatus.Ready, first.Status);
            Equal(2, first.Events.Count);
            Equal(AutomaticTradePlanLifecycleStatus.Building, first.Events.ElementAt(0).Status);
            Equal(AutomaticTradePlanLifecycleStatus.Ready, first.Events.ElementAt(1).Status);
            Require(second.Duplicate);
            Equal(first.Result!.TradePlan!.TradePlanUid, second.Result!.TradePlan!.TradePlanUid);
            Require(!first.Result.TradePlan.ExecutionAuthorized);
        });

        Check(failures, "expired intake terminates without BUILDING", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var intake = CreateIntake(now);
            intake = intake with
            {
                Signal = intake.Signal with
                {
                    EntryClosesAtUtc = now.AddSeconds(-1),
                    ValidUntilUtc = now.AddSeconds(-1),
                },
            };
            var store = new InMemoryAutomaticTradePlanLifecycleStore();
            var result = new AutomaticTradePlanCoordinator(projector, builder, store).Build(intake);

            Equal(AutomaticTradePlanLifecycleStatus.Expired, result.Status);
            Equal(1, result.Events.Count);
            if (result.Result is not null) throw new InvalidOperationException("Expired intake must not have a build result.");
        });

        Check(failures, "terminal Trade Plan status cannot reopen", () =>
        {
            Require(!AutomaticTradePlanTransitionMatrix.CanTransition(
                AutomaticTradePlanLifecycleStatus.Ready,
                AutomaticTradePlanLifecycleStatus.Building));
            Require(!AutomaticTradePlanTransitionMatrix.CanTransition(
                AutomaticTradePlanLifecycleStatus.Rejected,
                AutomaticTradePlanLifecycleStatus.RetryPending));
            Require(AutomaticTradePlanTransitionMatrix.CanTransition(
                AutomaticTradePlanLifecycleStatus.RetryPending,
                AutomaticTradePlanLifecycleStatus.Building));
        });

        Check(failures, "concurrent duplicate builds produce one effective result", () =>
        {
            var store = new InMemoryAutomaticTradePlanLifecycleStore();
            var coordinator = new AutomaticTradePlanCoordinator(projector, builder, store);
            var intake = CreateIntake(DateTimeOffset.UtcNow);
            var results = Enumerable.Range(0, 20)
                .AsParallel()
                .Select(_ => coordinator.Build(intake))
                .ToArray();
            var terminal = store.Read(intake.RiskDecision.RiskDecisionUid, intake.MessageUid);

            Equal(AutomaticTradePlanLifecycleStatus.Ready, terminal.Status);
            Equal(2, terminal.Events.Count);
            Require(results.Count(result => result.Duplicate) >= 19);
        });

        return failures;
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
        var execution = new TradePlanExecutionContextV1(
            "INTRADAY", "MARKET", "STOP_MARKET", null, 0.001m, "DAY",
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
            "execution-policy-v1.0.0");

        return new AutomaticTradePlanIntakeV1(
            Guid.NewGuid(), decision.CorrelationId, Guid.NewGuid(), decision, signal,
            new TradePlanInstrumentContextV1(1m, null, 1m, true), execution, now);
    }

    private static void Check(ICollection<string> failures, string name, Action test)
    {
        try { test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception exception) { failures.Add($"{name}: {exception.Message}"); }
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
}
