using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

internal static class AutomaticIntakeAcceptanceTests
{
    public static IReadOnlyCollection<string> Run()
    {
        var failures = new List<string>();

        Check(failures, "reference session builds deterministic automatic intake", () =>
        {
            var now = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
            var candidate = CreateCandidate(now, EvidenceDirectionV1.Long);
            var options = EnabledOptions();

            Require(AutomaticTradePlanContextAssembler.TryBuild(candidate, options, now, out var first, out var firstReason), firstReason);
            Require(AutomaticTradePlanContextAssembler.TryBuild(candidate, options, now, out var second, out var secondReason), secondReason);
            Equal(first!.MessageUid, second!.MessageUid);
            Equal(candidate.EvaluationSourceMessageUid, first.CausationMessageUid!.Value);
            Equal(candidate.LotSize, first.Instrument.MinimumExecutionQuantity!.Value);
            Equal(new DateTimeOffset(2026, 7, 2, 15, 0, 0, TimeSpan.Zero), first.Execution.Session.NewEntryCutoffUtc);
            Equal(new DateTimeOffset(2026, 7, 2, 16, 0, 0, TimeSpan.Zero), first.Execution.Session.MandatoryExitByUtc);
        });

        Check(failures, "holiday fails closed", () =>
        {
            var now = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
            var candidate = CreateCandidate(now, EvidenceDirectionV1.Long) with { IsTradingDay = false };
            Require(!AutomaticTradePlanContextAssembler.TryBuild(candidate, EnabledOptions(), now, out _, out var reason));
            Equal("EXCHANGE_NOT_TRADING", reason);
        });

        Check(failures, "short restriction fails closed", () =>
        {
            var now = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
            var candidate = CreateCandidate(now, EvidenceDirectionV1.Short) with { IsShortAllowed = false };
            Require(!AutomaticTradePlanContextAssembler.TryBuild(candidate, EnabledOptions(), now, out _, out var reason));
            Equal("SHORT_NOT_ALLOWED", reason);
        });

        Check(failures, "closed entry window fails closed", () =>
        {
            var now = new DateTimeOffset(2026, 7, 2, 15, 1, 0, TimeSpan.Zero);
            var candidate = CreateCandidate(now, EvidenceDirectionV1.Long);
            Require(!AutomaticTradePlanContextAssembler.TryBuild(candidate, EnabledOptions(), now, out _, out var reason));
            Equal("ENTRY_WINDOW_CLOSED", reason);
        });

        Check(failures, "invalid exchange timezone fails closed", () =>
        {
            var now = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
            var candidate = CreateCandidate(now, EvidenceDirectionV1.Long) with { TimeZoneId = "Invalid/Exchange" };
            Require(!AutomaticTradePlanContextAssembler.TryBuild(candidate, EnabledOptions(), now, out _, out var reason));
            Equal("SESSION_TIME_INVALID", reason);
        });

        Check(failures, "enabled intake rejects unsafe target fractions", () =>
        {
            var options = EnabledOptions();
            options.Targets.Clear();
            options.Targets.Add(new AutomaticTradePlanTargetOptions
            {
                Sequence = 1,
                RiskRewardMultiple = 2m,
                QuantityFraction = 0.8m,
            });
            Throws(options.Validate);
        });

        return failures;
    }

    private static AutomaticTradePlanIntakeOptions EnabledOptions() => new()
    {
        Enabled = true,
        PollIntervalSeconds = 5,
        BatchSize = 50,
        EntryCutoffBufferMinutes = 15,
        Targets =
        [
            new AutomaticTradePlanTargetOptions
            {
                Sequence = 1,
                RiskRewardMultiple = 2m,
                QuantityFraction = 0.5m,
            },
            new AutomaticTradePlanTargetOptions
            {
                Sequence = 2,
                RiskRewardMultiple = 3m,
                QuantityFraction = 0.5m,
            },
        ],
    };

    private static AutomaticTradePlanCandidate CreateCandidate(
        DateTimeOffset now,
        EvidenceDirectionV1 direction)
    {
        var signalUid = Guid.NewGuid();
        var thesisUid = Guid.NewGuid();
        var decision = new RiskDecisionV1(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            signalUid,
            thesisUid,
            "NSE_EQ|INE002A01018",
            "PAPER",
            direction,
            RiskDecisionContractV1.Approved,
            Array.Empty<string>(),
            Array.Empty<RiskCheckV1>(),
            new RiskBudgetV1(10000m, 200000m, 100m, now.AddMinutes(30)),
            "risk-policy-v1.0.0",
            "deterministic-risk-v1.0.0",
            now.AddSeconds(-2));
        var directionText = direction == EvidenceDirectionV1.Long ? "LONG" : "SHORT";
        var invalidation = direction == EvidenceDirectionV1.Long ? 95m : 105m;
        var signal = new SignalGeneratedV1(
            signalUid,
            decision.InstrumentKey,
            "THESIS_FUSION",
            "deterministic-thesis-fusion-v1.0.0",
            directionText,
            "5m",
            new[] { "5m", "15m" },
            0.8m,
            0.75m,
            now.AddMinutes(-1),
            new DateTimeOffset(2026, 7, 2, 15, 0, 0, TimeSpan.Zero),
            100m,
            99.5m,
            100.5m,
            invalidation,
            "Deterministic invalidation.",
            30,
            now.AddMinutes(-1),
            new DateTimeOffset(2026, 7, 2, 16, 0, 0, TimeSpan.Zero),
            "fusion-policy-v1.0.0",
            Array.Empty<SignalEvidenceV1>());

        return new AutomaticTradePlanCandidate(
            1001,
            Guid.NewGuid(),
            Guid.NewGuid(),
            decision.CorrelationId,
            decision,
            signal,
            1m,
            true,
            true,
            "UTC",
            new DateOnly(2026, 7, 2),
            true,
            new TimeOnly(9, 0),
            new TimeOnly(16, 0),
            false,
            true);
    }

    private static void Check(ICollection<string> failures, string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    private static void Throws(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Expected InvalidOperationException.");
    }

    private static void Require(bool condition, string? detail = null)
    {
        if (!condition)
            throw new InvalidOperationException(detail ?? "Acceptance assertion failed.");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
    }
}
