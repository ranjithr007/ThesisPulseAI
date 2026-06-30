using Microsoft.Extensions.Options;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public interface ITradePlanBuilder
{
    TradePlanBuildResultV1 Build(TradePlanBuildRequestV1 request);
}

public sealed class DeterministicTradePlanBuilder : ITradePlanBuilder
{
    private static readonly IReadOnlySet<string> EntryOrderTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MARKET",
            "LIMIT",
            "STOP_MARKET",
            "STOP_LIMIT",
        };

    private static readonly IReadOnlySet<string> StopOrderTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "STOP_MARKET",
            "STOP_LIMIT",
            "SYNTHETIC",
        };

    private static readonly IReadOnlySet<string> TimeInForceValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DAY",
            "IOC",
        };

    private readonly DeterministicTradePlanOptions _policy;

    public DeterministicTradePlanBuilder(IOptions<DeterministicTradePlanOptions> options)
    {
        _policy = options.Value;
    }

    public TradePlanBuildResultV1 Build(TradePlanBuildRequestV1 request)
    {
        var checks = new List<TradePlanCheckV1>();
        var decision = request.RiskDecision;
        var budget = decision.Budget;
        var now = request.AsOfUtc;

        AddCheck(
            checks,
            "RISK_DECISION_APPROVED",
            string.Equals(decision.Decision, RiskDecisionContractV1.Approved, StringComparison.Ordinal) &&
            decision.Reasons.Count == 0,
            null,
            null,
            "A trade plan requires an unqualified APPROVED risk decision.");
        AddCheck(
            checks,
            "RISK_DECISION_LINEAGE",
            decision.RiskDecisionUid != Guid.Empty &&
            decision.SignalUid != Guid.Empty &&
            decision.ThesisUid != Guid.Empty &&
            !string.IsNullOrWhiteSpace(decision.InstrumentKey) &&
            !string.IsNullOrWhiteSpace(decision.CorrelationId),
            null,
            null,
            "Risk decision, signal, thesis, instrument and correlation lineage are mandatory.");
        AddCheck(
            checks,
            "RISK_BUDGET_PRESENT",
            budget is not null && budget.MaximumRiskAmount > 0 && budget.MaximumCapitalAllocation > 0,
            budget?.MaximumRiskAmount,
            0,
            "An approved positive risk and capital budget is mandatory.");
        AddCheck(
            checks,
            "RISK_DECISION_CURRENT",
            budget is not null && now >= decision.EvaluatedAtUtc && now < budget.ExpiresAtUtc,
            budget is null ? null : (decimal)(budget.ExpiresAtUtc - now).TotalSeconds,
            0,
            "Risk decision budget must be current and unexpired.");
        AddCheck(
            checks,
            "ENVIRONMENT_ALLOWED",
            _policy.AllowedEnvironments.Contains(decision.Environment, StringComparer.OrdinalIgnoreCase),
            null,
            null,
            $"Environment '{decision.Environment}' is not enabled for trade-plan construction.");
        AddCheck(
            checks,
            "DIRECTION_SUPPORTED",
            decision.Direction is EvidenceDirectionV1.Long or EvidenceDirectionV1.Short,
            (decimal)decision.Direction,
            null,
            "Trade plans require a LONG or SHORT approved direction.");
        AddCheck(
            checks,
            "EXECUTION_POLICY_VERSION",
            string.Equals(request.ExecutionPolicyVersion, _policy.ExecutionPolicyVersion, StringComparison.Ordinal),
            null,
            null,
            $"Requested '{request.ExecutionPolicyVersion}', active '{_policy.ExecutionPolicyVersion}'.");
        AddCheck(
            checks,
            "POSITION_INTENT_ALLOWED",
            _policy.AllowedPositionIntents.Contains(request.PositionIntent, StringComparer.OrdinalIgnoreCase),
            null,
            null,
            $"Position intent '{request.PositionIntent}' is not enabled.");

        ValidateEntry(request, checks);
        ValidateStop(request, checks);
        ValidateTargets(request, checks);
        ValidateExecutionControls(request, checks);
        ValidateSession(request, checks);

        var side = decision.Direction == EvidenceDirectionV1.Long ? "BUY" : "SELL";
        var worstEntryPrice = decision.Direction == EvidenceDirectionV1.Long
            ? request.Entry.MaximumAcceptablePrice
            : request.Entry.MinimumAcceptablePrice;
        var riskPerUnit = decision.Direction == EvidenceDirectionV1.Long
            ? worstEntryPrice - request.StopLossPrice
            : request.StopLossPrice - worstEntryPrice;

        var riskLimitedQuantity = budget is not null && riskPerUnit > 0
            ? budget.MaximumRiskAmount / riskPerUnit
            : 0m;
        var capitalLimitedQuantity = budget is not null && worstEntryPrice > 0
            ? budget.MaximumCapitalAllocation / worstEntryPrice
            : 0m;
        var rawQuantity = Math.Min(riskLimitedQuantity, capitalLimitedQuantity);

        if (request.RequestedQuantity is not null)
        {
            AddCheck(
                checks,
                "REQUESTED_QUANTITY_POSITIVE",
                request.RequestedQuantity > 0,
                request.RequestedQuantity,
                0,
                "Requested quantity must be positive when supplied.");
            if (request.RequestedQuantity > 0)
            {
                rawQuantity = Math.Min(rawQuantity, request.RequestedQuantity.Value);
            }
        }

        var approvedQuantity = request.LotSize > 0
            ? Math.Floor(rawQuantity / request.LotSize) * request.LotSize
            : 0m;
        var riskAmountAtStop = approvedQuantity * Math.Max(0m, riskPerUnit);
        var capitalAtReference = approvedQuantity * Math.Max(0m, worstEntryPrice);

        AddCheck(
            checks,
            "LOT_SIZE_POSITIVE",
            request.LotSize > 0,
            request.LotSize,
            0,
            "Instrument lot size must be positive.");
        AddCheck(
            checks,
            "APPROVED_QUANTITY_POSITIVE",
            approvedQuantity > 0,
            approvedQuantity,
            request.LotSize,
            "Risk and capital ceilings must permit at least one lot.");
        AddCheck(
            checks,
            "RISK_BUDGET_RESPECTED",
            budget is not null && riskAmountAtStop <= budget.MaximumRiskAmount,
            riskAmountAtStop,
            budget?.MaximumRiskAmount,
            "Quantity must not exceed the approved loss-at-stop budget.");
        AddCheck(
            checks,
            "CAPITAL_BUDGET_RESPECTED",
            budget is not null && capitalAtReference <= budget.MaximumCapitalAllocation,
            capitalAtReference,
            budget?.MaximumCapitalAllocation,
            "Quantity must not exceed the approved capital allocation.");

        var minimumExecutionQuantity = request.MinimumExecutionQuantity;
        AddCheck(
            checks,
            "MINIMUM_EXECUTION_QUANTITY",
            minimumExecutionQuantity is null ||
            (minimumExecutionQuantity > 0 && minimumExecutionQuantity <= approvedQuantity),
            minimumExecutionQuantity,
            approvedQuantity,
            "Minimum execution quantity must be positive and no greater than approved quantity.");
        AddCheck(
            checks,
            "PARTIAL_FILL_POLICY",
            request.AllowPartialFill || minimumExecutionQuantity is null || minimumExecutionQuantity == approvedQuantity,
            minimumExecutionQuantity,
            approvedQuantity,
            "When partial fills are disabled, minimum execution quantity must equal approved quantity.");

        var firstTarget = request.Targets.OrderBy(target => target.Sequence).FirstOrDefault();
        var referenceRiskPerUnit = decision.Direction == EvidenceDirectionV1.Long
            ? request.Entry.ReferencePrice - request.StopLossPrice
            : request.StopLossPrice - request.Entry.ReferencePrice;
        var firstTargetReward = firstTarget is null
            ? 0m
            : decision.Direction == EvidenceDirectionV1.Long
                ? firstTarget.Price - request.Entry.ReferencePrice
                : request.Entry.ReferencePrice - firstTarget.Price;
        var firstTargetRiskReward = referenceRiskPerUnit > 0
            ? firstTargetReward / referenceRiskPerUnit
            : 0m;
        AddCheck(
            checks,
            "MINIMUM_RISK_REWARD",
            firstTargetRiskReward >= _policy.MinimumFirstTargetRiskReward,
            Math.Round(firstTargetRiskReward, 4),
            _policy.MinimumFirstTargetRiskReward,
            "The first target must satisfy the configured minimum reward-to-risk ratio.");

        var reasons = checks
            .Where(check => !check.Passed)
            .Select(check => check.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var validUntilUtc = budget is null
            ? now
            : Min(
                budget.ExpiresAtUtc,
                request.Session.NewEntryCutoffUtc,
                now.AddSeconds(_policy.MaximumPlanValiditySeconds));
        var ready = reasons.Length == 0 && validUntilUtc > now;
        if (!ready && reasons.Length == 0)
        {
            reasons = ["PLAN_VALIDITY_EMPTY"];
            checks.Add(new TradePlanCheckV1(
                "PLAN_VALIDITY_EMPTY",
                false,
                0,
                0,
                "No positive validity window remains for the trade plan."));
        }

        TradePlanV1? plan = null;
        if (ready)
        {
            plan = new TradePlanV1(
                Guid.NewGuid(),
                decision.RiskDecisionUid,
                decision.ThesisUid,
                decision.SignalUid,
                request.CorrelationId,
                decision.Environment,
                decision.InstrumentKey,
                decision.Direction,
                side,
                request.PositionIntent.ToUpperInvariant(),
                new TradePlanEntryV1(
                    request.Entry.OrderType.ToUpperInvariant(),
                    request.Entry.ReferencePrice,
                    request.Entry.LimitPrice,
                    request.Entry.TriggerPrice,
                    request.Entry.MinimumAcceptablePrice,
                    request.Entry.MaximumAcceptablePrice),
                approvedQuantity,
                minimumExecutionQuantity,
                request.AllowPartialFill,
                new TradePlanStopLossV1(
                    request.StopLossPrice,
                    request.StopOrderType.ToUpperInvariant(),
                    request.StopLimitPrice,
                    true),
                request.Targets
                    .OrderBy(target => target.Sequence)
                    .Select(target => new TradePlanTargetV1(
                        target.Sequence,
                        target.Price,
                        target.QuantityFraction))
                    .ToArray(),
                request.MaximumSlippageFraction,
                request.TimeInForce.ToUpperInvariant(),
                request.Session,
                request.ExitPolicy,
                Math.Round(riskAmountAtStop, 2),
                Math.Round(capitalAtReference, 2),
                Math.Round(firstTargetRiskReward, 4),
                request.ExecutionPolicyVersion,
                TradePlanContractV1.Ready,
                false,
                now,
                validUntilUtc);
        }

        return new TradePlanBuildResultV1(
            request.RequestUid,
            request.CorrelationId,
            ready ? TradePlanContractV1.Ready : TradePlanContractV1.Rejected,
            reasons,
            checks,
            plan,
            _policy.BuilderVersion,
            request.ExecutionPolicyVersion,
            now);
    }

    private void ValidateEntry(TradePlanBuildRequestV1 request, ICollection<TradePlanCheckV1> checks)
    {
        var entry = request.Entry;
        var orderTypeValid = EntryOrderTypes.Contains(entry.OrderType);
        AddCheck(checks, "ENTRY_ORDER_TYPE", orderTypeValid, null, null, "Unsupported entry order type.");
        AddCheck(
            checks,
            "ENTRY_PRICE_BAND",
            entry.MinimumAcceptablePrice > 0 &&
            entry.MaximumAcceptablePrice >= entry.MinimumAcceptablePrice &&
            entry.ReferencePrice >= entry.MinimumAcceptablePrice &&
            entry.ReferencePrice <= entry.MaximumAcceptablePrice,
            entry.ReferencePrice,
            entry.MaximumAcceptablePrice,
            "Reference price must lie inside a positive acceptable price band.");

        var normalized = entry.OrderType.ToUpperInvariant();
        var fieldsValid = normalized switch
        {
            "MARKET" => entry.LimitPrice is null && entry.TriggerPrice is null,
            "LIMIT" => entry.LimitPrice > 0 && entry.TriggerPrice is null,
            "STOP_MARKET" => entry.LimitPrice is null && entry.TriggerPrice > 0,
            "STOP_LIMIT" => entry.LimitPrice > 0 && entry.TriggerPrice > 0,
            _ => false,
        };
        AddCheck(
            checks,
            "ENTRY_ORDER_FIELDS",
            fieldsValid,
            null,
            null,
            "Limit and trigger fields must match the selected order type.");
    }

    private static void ValidateStop(TradePlanBuildRequestV1 request, ICollection<TradePlanCheckV1> checks)
    {
        var decision = request.RiskDecision;
        AddCheck(
            checks,
            "STOP_ORDER_TYPE",
            StopOrderTypes.Contains(request.StopOrderType),
            null,
            null,
            "Unsupported stop-loss order type.");
        var normalized = request.StopOrderType.ToUpperInvariant();
        AddCheck(
            checks,
            "STOP_ORDER_FIELDS",
            normalized == "STOP_LIMIT" ? request.StopLimitPrice > 0 : request.StopLimitPrice is null,
            request.StopLimitPrice,
            null,
            "Stop-limit requires a limit price; other stop types must not provide one.");
        var directionalStopValid = decision.Direction == EvidenceDirectionV1.Long
            ? request.StopLossPrice > 0 && request.StopLossPrice < request.Entry.MinimumAcceptablePrice
            : request.StopLossPrice > request.Entry.MaximumAcceptablePrice;
        AddCheck(
            checks,
            "STOP_DIRECTION",
            directionalStopValid,
            request.StopLossPrice,
            request.Entry.ReferencePrice,
            "BUY stops must be below the entry band; SELL stops must be above it.");
    }

    private static void ValidateTargets(TradePlanBuildRequestV1 request, ICollection<TradePlanCheckV1> checks)
    {
        var targets = request.Targets.OrderBy(target => target.Sequence).ToArray();
        AddCheck(checks, "TARGETS_PRESENT", targets.Length > 0, targets.Length, 1, "At least one target is required.");
        var sequencesValid = targets.Length > 0 &&
            targets.Select((target, index) => target.Sequence == index + 1).All(valid => valid);
        AddCheck(checks, "TARGET_SEQUENCE", sequencesValid, null, null, "Target sequence must be unique and contiguous from one.");
        var fractionSum = targets.Sum(target => target.QuantityFraction);
        AddCheck(
            checks,
            "TARGET_FRACTIONS",
            targets.All(target => target.QuantityFraction > 0 && target.QuantityFraction <= 1) && fractionSum == 1m,
            fractionSum,
            1m,
            "Target quantity fractions must be positive and total exactly 1.0.");
        var directionalTargetsValid = request.RiskDecision.Direction == EvidenceDirectionV1.Long
            ? targets.All(target => target.Price > request.Entry.ReferencePrice)
            : targets.All(target => target.Price > 0 && target.Price < request.Entry.ReferencePrice);
        AddCheck(
            checks,
            "TARGET_DIRECTION",
            targets.Length > 0 && directionalTargetsValid,
            null,
            request.Entry.ReferencePrice,
            "BUY targets must be above reference; SELL targets must be below reference.");
    }

    private void ValidateExecutionControls(TradePlanBuildRequestV1 request, ICollection<TradePlanCheckV1> checks)
    {
        AddCheck(
            checks,
            "MAXIMUM_SLIPPAGE",
            request.MaximumSlippageFraction >= 0 &&
            request.MaximumSlippageFraction <= _policy.MaximumSlippageFraction,
            request.MaximumSlippageFraction,
            _policy.MaximumSlippageFraction,
            "Maximum slippage must remain inside the active execution-policy ceiling.");
        AddCheck(
            checks,
            "TIME_IN_FORCE",
            TimeInForceValues.Contains(request.TimeInForce),
            null,
            null,
            "Unsupported time-in-force value.");
        AddCheck(
            checks,
            "EXIT_POLICY_VERSION",
            !string.IsNullOrWhiteSpace(request.ExitPolicy.PolicyVersion),
            null,
            null,
            "Exit policy version is mandatory.");
        AddCheck(
            checks,
            "RISK_REDUCING_EXIT_AVAILABLE",
            request.ExitPolicy.AllowTimeExit ||
            request.ExitPolicy.AllowSignalExit ||
            request.ExitPolicy.AllowTrailingStop ||
            request.ExitPolicy.AllowBreakEvenMove,
            null,
            null,
            "At least one additional risk-reducing exit behavior must be enabled.");
    }

    private static void ValidateSession(TradePlanBuildRequestV1 request, ICollection<TradePlanCheckV1> checks)
    {
        var session = request.Session;
        var orderingValid =
            (session.NotBeforeUtc is null || session.NotBeforeUtc <= session.NewEntryCutoffUtc) &&
            session.NewEntryCutoffUtc < session.MandatoryExitByUtc;
        AddCheck(
            checks,
            "SESSION_ORDERING",
            orderingValid,
            null,
            null,
            "Session boundaries must be ordered: not-before, entry cutoff, mandatory exit.");
        AddCheck(
            checks,
            "ENTRY_WINDOW_OPEN",
            request.AsOfUtc < session.NewEntryCutoffUtc &&
            (session.NotBeforeUtc is null || request.AsOfUtc >= session.NotBeforeUtc),
            null,
            null,
            "Plan construction requires an open entry window.");
    }

    private static DateTimeOffset Min(params DateTimeOffset[] values) => values.Min();

    private static void AddCheck(
        ICollection<TradePlanCheckV1> checks,
        string code,
        bool passed,
        decimal? observedValue,
        decimal? limitValue,
        string detail) =>
        checks.Add(new TradePlanCheckV1(code, passed, observedValue, limitValue, detail));
}
