using Microsoft.Extensions.Options;
using ThesisPulse.Execution.Service;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

var failures = new List<string>();
var service = new DeterministicPaperExecutionService(
    Options.Create(new DeterministicPaperExecutionOptions()));

Run("ready paper plan creates bounded command and order", () =>
{
    var now = DateTimeOffset.UtcNow;
    var result = service.Authorize(CreateRequest(now));

    AssertEqual(ExecutionCommandContractV1.Authorized, result.Status);
    var command = result.Command ?? throw new InvalidOperationException("Authorized result must contain a command.");
    var order = result.PaperOrder ?? throw new InvalidOperationException("Authorized result must contain a paper order.");
    AssertTrue(command.PaperSubmissionAuthorized, "Paper submission must be authorized.");
    AssertFalse(command.BrokerSubmissionAuthorized, "Broker submission must remain disabled.");
    AssertFalse(command.LiveExecutionAuthorized, "Live execution must remain disabled.");
    AssertEqual(PaperOrderStateContractV1.Created, order.State);
    AssertEqual(100m, order.RemainingQuantity);
    AssertTrue(command.ValidUntilUtc <= now.AddSeconds(30), "Command validity must be policy bounded.");
});

Run("successful command authorization is idempotent", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);

    var first = service.Authorize(request);
    var second = service.Authorize(request);

    AssertEqual(first.Command?.ExecutionCommandUid, second.Command?.ExecutionCommandUid);
    AssertEqual(first.PaperOrder?.PaperOrderUid, second.PaperOrder?.PaperOrderUid);
});

Run("idempotency key cannot be reused for another plan", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);
    var first = service.Authorize(request);
    AssertEqual(ExecutionCommandContractV1.Authorized, first.Status);

    var conflicting = request with
    {
        RequestUid = Guid.NewGuid(),
        TradePlan = request.TradePlan with { TradePlanUid = Guid.NewGuid() },
    };
    var second = service.Authorize(conflicting);

    AssertEqual(ExecutionCommandContractV1.Rejected, second.Status);
    AssertContains("IDEMPOTENCY_KEY_AVAILABLE", second.Reasons);
});

Run("expired trade plan is rejected", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now) with
    {
        TradePlan = CreateRequest(now).TradePlan with { ValidUntilUtc = now.AddSeconds(-1) },
    };

    var result = service.Authorize(request);

    AssertEqual(ExecutionCommandContractV1.Rejected, result.Status);
    AssertContains("TRADE_PLAN_CURRENT", result.Reasons);
});

Run("live environment is rejected", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);
    request = request with
    {
        TradePlan = request.TradePlan with { Environment = "LIVE" },
    };

    var result = service.Authorize(request);

    AssertEqual(ExecutionCommandContractV1.Rejected, result.Status);
    AssertContains("PAPER_ENVIRONMENT_ONLY", result.Reasons);
});

Run("kill switch blocks command authorization", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now);
    request = request with
    {
        Operations = request.Operations with { KillSwitchActive = true },
    };

    var result = service.Authorize(request);

    AssertEqual(ExecutionCommandContractV1.Rejected, result.Status);
    AssertContains("KILL_SWITCH_CLEAR", result.Reasons);
});

Run("paper order follows submit acknowledge and fill lifecycle", () =>
{
    var now = DateTimeOffset.UtcNow;
    var result = service.Authorize(CreateRequest(now));
    var order = result.PaperOrder ?? throw new InvalidOperationException("Paper order required.");

    var submitted = Apply(order.PaperOrderUid, PaperOrderEventContractV1.Submit, now.AddSeconds(1));
    AssertEqual(PaperOrderStateContractV1.Submitted, submitted.State);

    var acknowledged = Apply(order.PaperOrderUid, PaperOrderEventContractV1.Acknowledge, now.AddSeconds(2));
    AssertEqual(PaperOrderStateContractV1.Acknowledged, acknowledged.State);

    var partial = Apply(
        order.PaperOrderUid,
        PaperOrderEventContractV1.Fill,
        now.AddSeconds(3),
        40m,
        101m);
    AssertEqual(PaperOrderStateContractV1.PartiallyFilled, partial.State);
    AssertEqual(40m, partial.FilledQuantity);
    AssertEqual(60m, partial.RemainingQuantity);

    var filled = Apply(
        order.PaperOrderUid,
        PaperOrderEventContractV1.Fill,
        now.AddSeconds(4),
        60m,
        102m);
    AssertEqual(PaperOrderStateContractV1.Filled, filled.State);
    AssertEqual(100m, filled.FilledQuantity);
    AssertEqual(0m, filled.RemainingQuantity);
    AssertEqual(101.6m, filled.AverageFillPrice ?? 0m);
    AssertTrue(filled.TerminalAtUtc is not null, "Filled order must be terminal.");
});

Run("fill before acknowledgement is rejected", () =>
{
    var now = DateTimeOffset.UtcNow;
    var result = service.Authorize(CreateRequest(now));
    var order = result.PaperOrder ?? throw new InvalidOperationException("Paper order required.");

    var transition = service.ApplyEvent(
        order.PaperOrderUid,
        new PaperOrderEventRequestV1(
            Guid.NewGuid(),
            PaperOrderEventContractV1.Fill,
            10m,
            101m,
            null,
            now.AddSeconds(1)));

    AssertFalse(transition.Applied, "Fill before acknowledgement must fail.");
    AssertContains("INVALID_STATE_TRANSITION", transition.Reasons);
});

Run("overfill is rejected without changing order", () =>
{
    var now = DateTimeOffset.UtcNow;
    var result = service.Authorize(CreateRequest(now));
    var order = result.PaperOrder ?? throw new InvalidOperationException("Paper order required.");
    Apply(order.PaperOrderUid, PaperOrderEventContractV1.Submit, now.AddSeconds(1));
    Apply(order.PaperOrderUid, PaperOrderEventContractV1.Acknowledge, now.AddSeconds(2));

    var transition = service.ApplyEvent(
        order.PaperOrderUid,
        new PaperOrderEventRequestV1(
            Guid.NewGuid(),
            PaperOrderEventContractV1.Fill,
            101m,
            101m,
            null,
            now.AddSeconds(3)));

    AssertFalse(transition.Applied, "Overfill must fail.");
    AssertContains("FILL_EXCEEDS_REMAINING_QUANTITY", transition.Reasons);
    AssertEqual(PaperOrderStateContractV1.Acknowledged, transition.PaperOrder?.State ?? string.Empty);
    AssertEqual(0m, transition.PaperOrder?.FilledQuantity ?? -1m);
});

Run("partial fill is rejected when disabled", () =>
{
    var now = DateTimeOffset.UtcNow;
    var request = CreateRequest(now, allowPartialFill: false);
    var result = service.Authorize(request);
    var order = result.PaperOrder ?? throw new InvalidOperationException("Paper order required.");
    Apply(order.PaperOrderUid, PaperOrderEventContractV1.Submit, now.AddSeconds(1));
    Apply(order.PaperOrderUid, PaperOrderEventContractV1.Acknowledge, now.AddSeconds(2));

    var transition = service.ApplyEvent(
        order.PaperOrderUid,
        new PaperOrderEventRequestV1(
            Guid.NewGuid(),
            PaperOrderEventContractV1.Fill,
            50m,
            101m,
            null,
            now.AddSeconds(3)));

    AssertFalse(transition.Applied, "Partial fill must fail when disabled.");
    AssertContains("PARTIAL_FILL_NOT_ALLOWED", transition.Reasons);
});

Run("duplicate paper event is an idempotent replay", () =>
{
    var now = DateTimeOffset.UtcNow;
    var result = service.Authorize(CreateRequest(now));
    var order = result.PaperOrder ?? throw new InvalidOperationException("Paper order required.");
    var eventUid = Guid.NewGuid();
    var request = new PaperOrderEventRequestV1(
        eventUid,
        PaperOrderEventContractV1.Submit,
        null,
        null,
        null,
        now.AddSeconds(1));

    var first = service.ApplyEvent(order.PaperOrderUid, request);
    var second = service.ApplyEvent(order.PaperOrderUid, request);

    AssertTrue(first.Applied, "Initial event must apply.");
    AssertTrue(second.Applied, "Duplicate event must be accepted idempotently.");
    AssertTrue(second.IdempotentReplay, "Duplicate event must be marked as replay.");
    AssertEqual(first.PaperOrder?.Version ?? 0, second.PaperOrder?.Version ?? -1);
});

Run("terminal order rejects further transitions", () =>
{
    var now = DateTimeOffset.UtcNow;
    var result = service.Authorize(CreateRequest(now));
    var order = result.PaperOrder ?? throw new InvalidOperationException("Paper order required.");
    Apply(order.PaperOrderUid, PaperOrderEventContractV1.Cancel, now.AddSeconds(1));

    var transition = service.ApplyEvent(
        order.PaperOrderUid,
        new PaperOrderEventRequestV1(
            Guid.NewGuid(),
            PaperOrderEventContractV1.Submit,
            null,
            null,
            null,
            now.AddSeconds(2)));

    AssertFalse(transition.Applied, "Terminal order must reject new transitions.");
    AssertContains("ORDER_ALREADY_TERMINAL", transition.Reasons);
});

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} paper execution test(s) failed.");
    return 1;
}

Console.WriteLine("All deterministic paper execution tests passed.");
return 0;

PaperOrderSnapshotV1 Apply(
    Guid paperOrderUid,
    string eventType,
    DateTimeOffset occurredAtUtc,
    decimal? fillQuantity = null,
    decimal? fillPrice = null)
{
    var result = service.ApplyEvent(
        paperOrderUid,
        new PaperOrderEventRequestV1(
            Guid.NewGuid(),
            eventType,
            fillQuantity,
            fillPrice,
            null,
            occurredAtUtc));

    if (!result.Applied || result.PaperOrder is null)
    {
        throw new InvalidOperationException(
            $"Expected event '{eventType}' to apply. Reasons: {string.Join(", ", result.Reasons)}");
    }

    return result.PaperOrder;
}

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static ExecutionCommandRequestV1 CreateRequest(
    DateTimeOffset now,
    bool allowPartialFill = true)
{
    var correlationId = Guid.NewGuid().ToString("D");
    var plan = new TradePlanV1(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        correlationId,
        ExecutionCommandContractV1.PaperEnvironment,
        "NSE_EQ|INE002A01018",
        EvidenceDirectionV1.Long,
        "BUY",
        "INTRADAY",
        new TradePlanEntryV1(
            "MARKET",
            100m,
            null,
            null,
            99.5m,
            100.5m),
        100m,
        allowPartialFill ? 1m : 100m,
        allowPartialFill,
        new TradePlanStopLossV1(
            95m,
            "STOP_MARKET",
            null,
            true),
        new[]
        {
            new TradePlanTargetV1(1, 110m, 0.5m),
            new TradePlanTargetV1(2, 115m, 0.5m),
        },
        0.001m,
        "DAY",
        new TradeSessionV1(
            DateOnly.FromDateTime(now.UtcDateTime),
            now.AddMinutes(-1),
            now.AddMinutes(30),
            now.AddHours(5)),
        new ExitPolicyV1(
            true,
            true,
            true,
            true,
            "exit-policy-v1.0.0"),
        500m,
        10050m,
        2m,
        "execution-policy-v1.0.0",
        TradePlanContractV1.Ready,
        false,
        now.AddSeconds(-1),
        now.AddSeconds(60));

    return new ExecutionCommandRequestV1(
        Guid.NewGuid(),
        Guid.NewGuid().ToString("N"),
        correlationId,
        plan,
        new ExecutionOperationalStateV1(
            false,
            false,
            true,
            true,
            true,
            now.AddSeconds(-1)),
        "execution-policy-v1.0.0",
        now);
}

static void AssertContains(string expected, IReadOnlyCollection<string> values)
{
    if (!values.Contains(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
    }
}

static void AssertTrue(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool value, string message) => AssertTrue(!value, message);

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
    }
}
