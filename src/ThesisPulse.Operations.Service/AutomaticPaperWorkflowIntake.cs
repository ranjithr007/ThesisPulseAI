using System.Net;
using System.Net.Http.Json;
using ThesisPulse.Shared.Contracts.Common.V1;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Intelligence.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Contracts.Workflows.V1;

namespace ThesisPulse.Operations.Service;

public sealed record AutomaticPaperWorkflowOptions
{
    public bool Enabled { get; init; }
    public string PortfolioCode { get; init; } = "PRIMARY-PAPER";
    public string AccountKey { get; init; } = "PRIMARY-PAPER";
    public decimal LotSize { get; init; } = 1m;
    public decimal? RequestedQuantity { get; init; }
    public decimal? MinimumExecutionQuantity { get; init; } = 1m;
    public bool AllowPartialFill { get; init; }
    public string PositionIntent { get; init; } = "INTRADAY";
    public string TimeInForce { get; init; } = "DAY";
    public string RiskPolicyVersion { get; init; } = "risk-policy-v1.0.0";
    public string ExecutionPolicyVersion { get; init; } = "execution-policy-v1.0.0";
    public string ExitPolicyVersion { get; init; } = "exit-policy-v1.0.0";
    public string MarketTimeZone { get; init; } = "Asia/Kolkata";
    public TimeOnly MarketOpenLocal { get; init; } = new(9, 15);
    public TimeOnly NewEntryCutoffLocal { get; init; } = new(15, 0);
    public TimeOnly MandatoryExitLocal { get; init; } = new(15, 20);
    public int MaximumEvidenceAgeSeconds { get; init; } = 120;
    public decimal CurrentDrawdownPercent { get; init; }
    public bool NewExposureEnabled { get; init; }
    public bool SessionCalendarHealthy { get; init; }
    public bool KillSwitchActive { get; init; }
    public bool TradingHalted { get; init; }
    public bool MarketDataHealthy { get; init; }
    public bool PortfolioStateHealthy { get; init; }
    public bool BrokerConnectivityHealthy { get; init; }
    public bool PaperGatewayHealthy { get; init; }
    public bool AllowTrailingStop { get; init; } = true;
    public bool AllowBreakEvenMove { get; init; } = true;
    public bool AllowTimeExit { get; init; } = true;
    public bool AllowSignalExit { get; init; } = true;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(PortfolioCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(PositionIntent);
        ArgumentException.ThrowIfNullOrWhiteSpace(TimeInForce);
        ArgumentException.ThrowIfNullOrWhiteSpace(RiskPolicyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExecutionPolicyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExitPolicyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(MarketTimeZone);
        if (LotSize <= 0 || MaximumEvidenceAgeSeconds is < 1 or > 600)
        {
            throw new InvalidOperationException(
                "Automatic PAPER workflow quantity or freshness configuration is invalid.");
        }
        if (RequestedQuantity is <= 0 || MinimumExecutionQuantity is <= 0)
        {
            throw new InvalidOperationException(
                "Configured quantities must be positive when supplied.");
        }
        if (CurrentDrawdownPercent < 0)
        {
            throw new InvalidOperationException(
                "Current drawdown percent cannot be negative.");
        }
        if (NewEntryCutoffLocal <= MarketOpenLocal ||
            MandatoryExitLocal <= NewEntryCutoffLocal)
        {
            throw new InvalidOperationException(
                "Automatic PAPER workflow session times are invalid.");
        }
    }
}

public interface IAutomaticPaperWorkflowContextProvider
{
    Task<PortfolioLedgerSnapshotV1?> GetPortfolioAsync(
        string portfolioCode,
        CancellationToken cancellationToken);
}

public sealed class HttpAutomaticPaperWorkflowContextProvider(
    HttpClient client) : IAutomaticPaperWorkflowContextProvider
{
    public async Task<PortfolioLedgerSnapshotV1?> GetPortfolioAsync(
        string portfolioCode,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"/api/v1/portfolio/{Uri.EscapeDataString(portfolioCode)}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PortfolioLedgerSnapshotV1>(
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "Portfolio Service returned an empty portfolio snapshot.");
    }
}

public sealed class AutomaticPaperWorkflowIntakeService(
    AutomaticPaperWorkflowOptions options,
    IAutomaticPaperWorkflowContextProvider contextProvider,
    PaperWorkflowCoordinator coordinator)
{
    public async Task<AutomaticPaperWorkflowIntakeResultV1> IntakeAsync(
        FusionReadyEvidenceV1 evidence,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var reasons = ValidateEvidence(evidence, now);
        if (!options.Enabled)
        {
            reasons.Add("AUTOMATIC_WORKFLOW_DISABLED");
            return Result(evidence, FusionReadyEvidenceContractV1.Ignored, reasons, null, now);
        }
        if (reasons.Count > 0)
        {
            return Result(evidence, FusionReadyEvidenceContractV1.Rejected, reasons, null, now);
        }

        var portfolio = await contextProvider.GetPortfolioAsync(
            options.PortfolioCode,
            cancellationToken);
        if (portfolio is null)
        {
            return Result(
                evidence,
                FusionReadyEvidenceContractV1.Rejected,
                ["PORTFOLIO_NOT_FOUND"],
                null,
                now);
        }

        var session = BuildSession(evidence, now, reasons);
        if (session is null || reasons.Count > 0)
        {
            return Result(evidence, FusionReadyEvidenceContractV1.Rejected, reasons, null, now);
        }

        var request = new PaperWorkflowStartRequestV1(
            DeterministicGuidV1.Create(evidence.EvidenceUid, "paper-workflow-request.v1"),
            $"fusion-ready:{evidence.EvidenceUid:N}",
            evidence.CorrelationId,
            evidence.SourceCandleMessageUid,
            BuildThesisRequest(evidence, now),
            BuildRiskSnapshot(portfolio, now),
            new OperationalRiskStateV1(
                options.KillSwitchActive,
                options.TradingHalted,
                true,
                options.MarketDataHealthy,
                options.PortfolioStateHealthy,
                options.BrokerConnectivityHealthy,
                now),
            options.RiskPolicyVersion,
            BuildTradePlan(evidence, session),
            new ExecutionOperationalStateV1(
                options.KillSwitchActive,
                options.TradingHalted,
                true,
                options.MarketDataHealthy,
                options.PaperGatewayHealthy,
                now),
            new PaperFillSimulationV1(
                [new PaperFillSliceV1(1, 1m, evidence.TradeProposal.ReferencePrice)],
                options.PortfolioCode,
                "PERIODIC"),
            now);

        var workflow = await coordinator.RunAsync(request, cancellationToken);
        var status = workflow.Workflow.Status switch
        {
            PaperWorkflowContractV1.Completed => FusionReadyEvidenceContractV1.Started,
            PaperWorkflowContractV1.RetryPending => FusionReadyEvidenceContractV1.RetryPending,
            PaperWorkflowContractV1.Rejected => FusionReadyEvidenceContractV1.Rejected,
            PaperWorkflowContractV1.Failed => FusionReadyEvidenceContractV1.Failed,
            _ => FusionReadyEvidenceContractV1.Started,
        };
        var workflowReasons = workflow.Workflow.LastErrorCode is null
            ? Array.Empty<string>()
            : new[] { workflow.Workflow.LastErrorCode };
        return Result(evidence, status, workflowReasons, workflow.Workflow, now);
    }

    private List<string> ValidateEvidence(
        FusionReadyEvidenceV1 evidence,
        DateTimeOffset now)
    {
        var reasons = new List<string>();
        if (evidence.EvidenceUid == Guid.Empty ||
            evidence.SourceCandleMessageUid == Guid.Empty ||
            evidence.ConfirmationOutputUid == Guid.Empty ||
            evidence.ConfirmationMessageUid == Guid.Empty)
            reasons.Add("EVIDENCE_LINEAGE_REQUIRED");
        if (!Guid.TryParse(evidence.CorrelationId, out _))
            reasons.Add("CORRELATION_ID_INVALID");
        if (!evidence.IsEligibleForWorkflow)
            reasons.Add("EVIDENCE_NOT_ELIGIBLE");
        if (!string.Equals(evidence.PrimaryTimeframe, "5m", StringComparison.OrdinalIgnoreCase))
            reasons.Add("PRIMARY_TIMEFRAME_MUST_BE_5M");
        if (evidence.AsOfUtc > now)
            reasons.Add("EVIDENCE_FUTURE_DATED");
        var age = (now - evidence.AsOfUtc).TotalSeconds;
        if (age > options.MaximumEvidenceAgeSeconds)
            reasons.Add("EVIDENCE_STALE");
        if (evidence.DirectionalEvidence.Count < 4)
            reasons.Add("INSUFFICIENT_DIRECTIONAL_EVIDENCE");
        if (evidence.TimeframeConfirmations.Count(item =>
                item.Direction is "LONG" or "SHORT") < 2)
            reasons.Add("INSUFFICIENT_TIMEFRAME_CONFIRMATION");
        if (!options.NewExposureEnabled)
            reasons.Add("NEW_EXPOSURE_DISABLED");
        if (!options.SessionCalendarHealthy)
            reasons.Add("SESSION_CALENDAR_UNHEALTHY");
        if (options.KillSwitchActive)
            reasons.Add("KILL_SWITCH_ACTIVE");
        if (options.TradingHalted)
            reasons.Add("TRADING_HALTED");
        if (!options.MarketDataHealthy)
            reasons.Add("MARKET_DATA_UNHEALTHY");
        if (!options.PortfolioStateHealthy)
            reasons.Add("PORTFOLIO_STATE_UNHEALTHY");
        if (!options.BrokerConnectivityHealthy)
            reasons.Add("BROKER_CONNECTIVITY_UNHEALTHY");
        if (!options.PaperGatewayHealthy)
            reasons.Add("PAPER_GATEWAY_UNHEALTHY");
        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    private TradeSessionV1? BuildSession(
        FusionReadyEvidenceV1 evidence,
        DateTimeOffset now,
        ICollection<string> reasons)
    {
        var timeZone = ResolveTimeZone(options.MarketTimeZone);
        var local = TimeZoneInfo.ConvertTime(evidence.AsOfUtc, timeZone);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            reasons.Add("MARKET_SESSION_CLOSED");
            return null;
        }

        var date = DateOnly.FromDateTime(local.DateTime);
        var open = ToUtc(date, options.MarketOpenLocal, timeZone);
        var cutoff = ToUtc(date, options.NewEntryCutoffLocal, timeZone);
        var mandatoryExit = ToUtc(date, options.MandatoryExitLocal, timeZone);
        if (evidence.AsOfUtc < open || now >= cutoff)
        {
            reasons.Add("ENTRY_WINDOW_CLOSED");
            return null;
        }
        return new TradeSessionV1(date, now, cutoff, mandatoryExit);
    }

    private ThesisFusionRequestV1 BuildThesisRequest(
        FusionReadyEvidenceV1 evidence,
        DateTimeOffset now) =>
        new(
            DeterministicGuidV1.Create(evidence.EvidenceUid, "thesis-request.v1"),
            evidence.CorrelationId,
            evidence.InstrumentKey,
            evidence.PrimaryTimeframe,
            now,
            evidence.WeightConfigurationVersion,
            evidence.DirectionalEvidence.Select(item => new DirectionalEvidenceV1(
                item.EngineCode,
                item.EngineVersion,
                item.Timeframe,
                Direction(item.Direction),
                item.Score,
                item.Confidence,
                item.ObservedAtUtc,
                item.Reasons)).ToArray(),
            new RegimeEvidenceV1(
                evidence.Regime.RegimeCode,
                evidence.Regime.EngineVersion,
                Direction(evidence.Regime.DirectionalBias),
                evidence.Regime.Confidence,
                evidence.Regime.ObservedAtUtc,
                evidence.Regime.Reasons),
            evidence.TimeframeConfirmations.Select(item =>
                new TimeframeConfirmationV1(
                    item.Timeframe,
                    Direction(item.Direction),
                    item.Score,
                    item.Confidence,
                    item.IsClosedCandle,
                    item.ObservedAtUtc,
                    item.Reasons)).ToArray());

    private PortfolioRiskSnapshotV1 BuildRiskSnapshot(
        PortfolioLedgerSnapshotV1 portfolio,
        DateTimeOffset now)
    {
        var cash = portfolio.CashBalances.Sum(item => item.AvailableAmount);
        var totalCash = portfolio.CashBalances.Sum(item => item.TotalBalanceAmount);
        var equity = totalCash + portfolio.NetExposureAmount;
        return new PortfolioRiskSnapshotV1(
            options.AccountKey,
            portfolio.Environment,
            equity,
            cash,
            portfolio.GrossExposureAmount,
            portfolio.NetExposureAmount,
            portfolio.RealizedPnlAmount,
            portfolio.UnrealizedPnlAmount,
            options.CurrentDrawdownPercent,
            portfolio.OpenPositionCount,
            portfolio.Positions
                .Where(item => string.Equals(item.Status, "OPEN", StringComparison.Ordinal))
                .Select(item => new PortfolioPositionV1(
                    item.InstrumentKey,
                    item.Direction,
                    item.MarketValueAmount,
                    item.OpenedAtUtc ?? item.UpdatedAtUtc))
                .ToArray(),
            now);
    }

    private TradePlanTemplateV1 BuildTradePlan(
        FusionReadyEvidenceV1 evidence,
        TradeSessionV1 session) =>
        new(
            options.PositionIntent,
            new TradeEntryProposalV1(
                "MARKET",
                evidence.TradeProposal.ReferencePrice,
                null,
                null,
                evidence.TradeProposal.MinimumAcceptablePrice,
                evidence.TradeProposal.MaximumAcceptablePrice),
            evidence.TradeProposal.StopLossPrice,
            "STOP_MARKET",
            null,
            evidence.TradeProposal.Targets.Select(item =>
                new TradeTargetProposalV1(
                    item.Sequence,
                    item.Price,
                    item.QuantityFraction)).ToArray(),
            options.LotSize,
            options.RequestedQuantity,
            options.MinimumExecutionQuantity,
            options.AllowPartialFill,
            evidence.TradeProposal.MaximumSlippageFraction,
            options.TimeInForce,
            session,
            new ExitPolicyV1(
                options.AllowTrailingStop,
                options.AllowBreakEvenMove,
                options.AllowTimeExit,
                options.AllowSignalExit,
                options.ExitPolicyVersion),
            options.ExecutionPolicyVersion);

    private static EvidenceDirectionV1 Direction(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "LONG" => EvidenceDirectionV1.Long,
            "SHORT" => EvidenceDirectionV1.Short,
            _ => EvidenceDirectionV1.Neutral,
        };

    private static AutomaticPaperWorkflowIntakeResultV1 Result(
        FusionReadyEvidenceV1 evidence,
        string status,
        IReadOnlyCollection<string> reasons,
        PaperWorkflowSnapshotV1? workflow,
        DateTimeOffset evaluatedAtUtc) =>
        new(
            evidence.EvidenceUid,
            status,
            reasons,
            workflow?.WorkflowUid,
            workflow?.Status,
            evaluatedAtUtc);

    private static TimeZoneInfo ResolveTimeZone(string identifier)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(identifier);
        }
        catch (TimeZoneNotFoundException) when (
            string.Equals(identifier, "Asia/Kolkata", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
    }

    private static DateTimeOffset ToUtc(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone));
    }
}

public static class AutomaticPaperWorkflowIntakeExtensions
{
    public static IServiceCollection AddAutomaticPaperWorkflowIntake(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection("AutomaticPaperWorkflow");
        var options = section.Get<AutomaticPaperWorkflowOptions>()
            ?? new AutomaticPaperWorkflowOptions();
        options.Validate();
        services.AddSingleton(options);

        var portfolioUrl = configuration["PaperWorkflow:PortfolioServiceBaseUrl"]
            ?? "http://localhost:5106";
        if (!Uri.TryCreate(portfolioUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException(
                "PaperWorkflow:PortfolioServiceBaseUrl must be an absolute URI.");
        }
        var timeoutSeconds = configuration.GetValue("PaperWorkflow:TimeoutSeconds", 15);
        services.AddHttpClient<IAutomaticPaperWorkflowContextProvider,
            HttpAutomaticPaperWorkflowContextProvider>(client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });
        services.AddSingleton<AutomaticPaperWorkflowIntakeService>();
        return services;
    }

    public static IEndpointRouteBuilder MapAutomaticPaperWorkflowIntake(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/v1/paper-workflows/intelligence", async (
            FusionReadyEvidenceV1 evidence,
            HttpRequest request,
            IConfiguration configuration,
            AutomaticPaperWorkflowIntakeService service,
            CancellationToken cancellationToken) =>
        {
            if (!Authorized(request, configuration))
            {
                return Results.Unauthorized();
            }
            var result = await service.IntakeAsync(evidence, cancellationToken);
            return result.Status == FusionReadyEvidenceContractV1.RetryPending
                ? Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(result);
        });
        return endpoints;
    }

    private static bool Authorized(
        HttpRequest request,
        IConfiguration configuration)
    {
        var expected = configuration["PaperWorkflow:InternalApiKey"];
        return !string.IsNullOrWhiteSpace(expected) &&
            request.Headers.TryGetValue("X-ThesisPulse-Internal-Key", out var supplied) &&
            string.Equals(expected, supplied.ToString(), StringComparison.Ordinal);
    }
}
