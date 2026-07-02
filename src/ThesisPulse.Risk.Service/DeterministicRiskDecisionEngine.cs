using Microsoft.Extensions.Options;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Risk.Service;

public interface IRiskDecisionEngine
{
    RiskDecisionV1 Evaluate(RiskDecisionRequestV1 request);
}

public sealed class DeterministicRiskDecisionEngine : IRiskDecisionEngine
{
    private readonly DeterministicRiskOptions _policy;

    public DeterministicRiskDecisionEngine(IOptions<DeterministicRiskOptions> options)
    {
        _policy = options.Value;
    }

    public RiskDecisionV1 Evaluate(RiskDecisionRequestV1 request)
    {
        var checks = new List<RiskCheckV1>();
        var now = request.AsOfUtc;

        AddCheck(checks, "RISK_POLICY_VERSION",
            string.Equals(request.RiskPolicyVersion, _policy.RiskPolicyVersion, StringComparison.Ordinal),
            null, null,
            $"Requested '{request.RiskPolicyVersion}', active '{_policy.RiskPolicyVersion}'.");

        AddCheck(checks, "CANONICAL_CANDIDATE_REQUIRED",
            string.Equals(request.Candidate.Status, ThesisFusionContractV1.CandidateStatus, StringComparison.Ordinal) &&
            request.Candidate.SignalUid != Guid.Empty &&
            request.Candidate.ThesisUid != Guid.Empty &&
            request.Candidate.Direction != EvidenceDirectionV1.Neutral &&
            !string.IsNullOrWhiteSpace(request.Candidate.InstrumentKey) &&
            !string.IsNullOrWhiteSpace(request.Candidate.FusionPolicyVersion),
            null, null,
            "Risk evaluates only a non-neutral canonical CANDIDATE linked to a thesis and fusion policy.");

        var candidateAgeSeconds = (decimal)(now - request.Candidate.GeneratedAtUtc).TotalSeconds;
        AddCheck(checks, "CANDIDATE_FRESHNESS",
            candidateAgeSeconds >= 0 && candidateAgeSeconds <= _policy.MaximumCandidateAgeSeconds,
            candidateAgeSeconds, _policy.MaximumCandidateAgeSeconds,
            "Candidate must not be future-dated or stale.");

        AddCheck(checks, "CANDIDATE_STRENGTH",
            request.Candidate.Strength >= _policy.MinimumCandidateStrength,
            request.Candidate.Strength, _policy.MinimumCandidateStrength,
            "Candidate strength must satisfy the operational risk floor.");

        AddCheck(checks, "CANDIDATE_CONFIDENCE",
            request.Candidate.Confidence >= _policy.MinimumCandidateConfidence,
            request.Candidate.Confidence, _policy.MinimumCandidateConfidence,
            "Candidate confidence must satisfy the operational risk floor.");

        AddCheck(checks, "ENVIRONMENT_ALLOWED",
            _policy.AllowedEnvironments.Contains(request.Portfolio.Environment, StringComparer.OrdinalIgnoreCase),
            null, null,
            $"Environment '{request.Portfolio.Environment}' is not enabled by this risk policy.");

        AddCheck(checks, "PORTFOLIO_RISK_SNAPSHOT_REQUIRED",
            request.Portfolio.PortfolioRiskSnapshotUid is not null,
            null, null,
            "Canonical Signal-to-Risk intake requires authoritative portfolio risk state.");

        AddCheck(checks, "PORTFOLIO_OPERATING_MODE_ALLOWED",
            request.Portfolio.NewExposureAllowed &&
            request.Portfolio.OperatingMode is PortfolioRiskContractV1.Normal or PortfolioRiskContractV1.Restricted,
            request.Portfolio.NewExposureAllowed ? 1 : 0,
            1,
            $"Portfolio operating mode '{request.Portfolio.OperatingMode}' controls new exposure.");

        AddCheck(checks, "PORTFOLIO_RISK_MULTIPLIER_VALID",
            request.Portfolio.EffectiveRiskMultiplier is > 0m and <= 1m,
            request.Portfolio.EffectiveRiskMultiplier,
            1m,
            "Effective risk multiplier must be greater than zero and no more than one.");

        AddCheck(checks, "KILL_SWITCH_CLEAR", !request.Operations.KillSwitchActive, null, null, "Kill switch blocks every new exposure.");
        AddCheck(checks, "TRADING_NOT_HALTED", !request.Operations.TradingHalted, null, null, "Operational trading halt blocks every new exposure.");
        AddCheck(checks, "MARKET_OPEN", request.Operations.MarketOpen, null, null, "New exposure requires an open market session.");
        AddCheck(checks, "MARKET_DATA_HEALTHY", request.Operations.MarketDataHealthy, null, null, "Unhealthy market data fails closed.");
        AddCheck(checks, "PORTFOLIO_STATE_HEALTHY", request.Operations.PortfolioStateHealthy, null, null, "Unhealthy portfolio state fails closed.");
        AddCheck(checks, "BROKER_CONNECTIVITY_HEALTHY", request.Operations.BrokerConnectivityHealthy, null, null, "Unavailable broker connectivity blocks new exposure.");

        var portfolioAgeSeconds = (decimal)(now - request.Portfolio.ObservedAtUtc).TotalSeconds;
        var operationsAgeSeconds = (decimal)(now - request.Operations.ObservedAtUtc).TotalSeconds;
        AddCheck(checks, "PORTFOLIO_SNAPSHOT_FRESHNESS",
            portfolioAgeSeconds >= 0 && portfolioAgeSeconds <= _policy.MaximumSnapshotAgeSeconds,
            portfolioAgeSeconds, _policy.MaximumSnapshotAgeSeconds,
            "Portfolio snapshot must be current.");
        AddCheck(checks, "OPERATIONS_SNAPSHOT_FRESHNESS",
            operationsAgeSeconds >= 0 && operationsAgeSeconds <= _policy.MaximumSnapshotAgeSeconds,
            operationsAgeSeconds, _policy.MaximumSnapshotAgeSeconds,
            "Operational state must be current.");

        AddCheck(checks, "POSITIVE_EQUITY", request.Portfolio.Equity > 0, request.Portfolio.Equity, 0, "Account equity must be positive.");
        AddCheck(checks, "NON_NEGATIVE_AVAILABLE_CASH", request.Portfolio.AvailableCash >= 0, request.Portfolio.AvailableCash, 0, "Available cash cannot be negative.");
        AddCheck(checks, "NON_NEGATIVE_GROSS_EXPOSURE", request.Portfolio.GrossExposure >= 0, request.Portfolio.GrossExposure, 0, "Gross exposure cannot be negative.");
        AddCheck(checks, "POSITION_COUNT_CONSISTENT",
            request.Portfolio.OpenPositionCount >= 0 && request.Portfolio.OpenPositionCount == request.Portfolio.Positions.Count,
            request.Portfolio.OpenPositionCount, request.Portfolio.Positions.Count,
            "Position count must match the supplied position snapshot.");

        var equity = request.Portfolio.Equity;
        var dailyPnl = request.Portfolio.RealizedPnlToday + request.Portfolio.UnrealizedPnlToday;
        var dailyLossAmount = dailyPnl < 0 ? -dailyPnl : 0m;
        var dailyLossPercent = equity > 0 ? dailyLossAmount / equity * 100m : 100m;
        var grossExposurePercent = equity > 0 ? request.Portfolio.GrossExposure / equity * 100m : 100m;

        AddCheck(checks, "DAILY_LOSS_LIMIT",
            dailyLossPercent < _policy.MaximumDailyLossPercent,
            Math.Round(dailyLossPercent, 4), _policy.MaximumDailyLossPercent,
            "Daily realized and unrealized loss must remain below the configured limit.");
        AddCheck(checks, "DRAWDOWN_LIMIT",
            request.Portfolio.CurrentDrawdownPercent >= 0 && request.Portfolio.CurrentDrawdownPercent < _policy.MaximumDrawdownPercent,
            request.Portfolio.CurrentDrawdownPercent, _policy.MaximumDrawdownPercent,
            "Current account drawdown must remain below the configured limit.");
        AddCheck(checks, "GROSS_EXPOSURE_LIMIT",
            grossExposurePercent < _policy.MaximumGrossExposurePercent,
            Math.Round(grossExposurePercent, 4), _policy.MaximumGrossExposurePercent,
            "Current gross exposure must leave capacity for new exposure.");
        AddCheck(checks, "OPEN_POSITION_LIMIT",
            request.Portfolio.OpenPositionCount < _policy.MaximumOpenPositions,
            request.Portfolio.OpenPositionCount, _policy.MaximumOpenPositions,
            "Open position count must remain below the configured maximum.");

        var existingInstrumentPosition = request.Portfolio.Positions.Any(position =>
            string.Equals(position.InstrumentKey, request.Candidate.InstrumentKey, StringComparison.OrdinalIgnoreCase));
        AddCheck(checks, "INSTRUMENT_CONCENTRATION",
            _policy.AllowPyramiding || !existingInstrumentPosition,
            existingInstrumentPosition ? 1 : 0,
            _policy.AllowPyramiding ? 1 : 0,
            "A pre-existing instrument position blocks additional exposure when pyramiding is disabled.");

        var multiplier = request.Portfolio.EffectiveRiskMultiplier;
        var maximumDailyLossAmount = equity > 0 ? equity * _policy.MaximumDailyLossPercent / 100m : 0m;
        var remainingDailyLossCapacity = Math.Max(0m, maximumDailyLossAmount - dailyLossAmount);
        var policyRiskAmount = equity > 0 ? equity * _policy.MaximumRiskPerTradePercent / 100m : 0m;
        var maximumRiskAmount = Math.Min(policyRiskAmount, remainingDailyLossCapacity) * multiplier;
        var grossExposureCapacity = equity > 0
            ? Math.Max(0m, equity * _policy.MaximumGrossExposurePercent / 100m - request.Portfolio.GrossExposure)
            : 0m;
        var singlePositionCap = equity > 0 ? equity * _policy.MaximumSinglePositionExposurePercent / 100m : 0m;
        var maximumCapitalAllocation =
            Math.Min(request.Portfolio.AvailableCash, Math.Min(grossExposureCapacity, singlePositionCap)) * multiplier;

        AddCheck(checks, "RISK_BUDGET_AVAILABLE", maximumRiskAmount > 0, maximumRiskAmount, 0, "No risk-loss capacity remains under the active policy.");
        AddCheck(checks, "CAPITAL_CAPACITY_AVAILABLE", maximumCapitalAllocation > 0, maximumCapitalAllocation, 0, "No capital or gross-exposure capacity remains.");

        var failures = checks
            .Where(check => !check.Passed)
            .Select(check => check.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var approved = failures.Length == 0;
        var budget = approved
            ? new RiskBudgetV1(
                Math.Round(maximumRiskAmount, 2),
                Math.Round(maximumCapitalAllocation, 2),
                _policy.MaximumGrossExposurePercent,
                now.AddSeconds(_policy.BudgetValiditySeconds))
            : null;

        return new RiskDecisionV1(
            DeterministicGuidV1.Create(request.RequestUid, "risk-decision.v1"),
            request.RequestUid,
            request.CorrelationId,
            request.Candidate.SignalUid,
            request.Candidate.ThesisUid,
            request.Candidate.InstrumentKey,
            request.Portfolio.Environment,
            request.Candidate.Direction,
            approved ? RiskDecisionContractV1.Approved : RiskDecisionContractV1.Rejected,
            failures,
            checks,
            budget,
            request.RiskPolicyVersion,
            _policy.EngineVersion,
            now);
    }

    private static void AddCheck(
        ICollection<RiskCheckV1> checks,
        string code,
        bool passed,
        decimal? observedValue,
        decimal? limitValue,
        string detail) =>
        checks.Add(new RiskCheckV1(code, passed, observedValue, limitValue, detail));
}
