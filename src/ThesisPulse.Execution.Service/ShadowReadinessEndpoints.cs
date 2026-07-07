using Microsoft.AspNetCore.Routing;

namespace ThesisPulse.Execution.Service;

public static class ShadowReadinessEndpoints
{
    public static IEndpointRouteBuilder MapShadowReadinessEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool liveExecutionEnabled)
    {
        endpoints.MapGet(
            "/api/v1/shadow/readiness/status",
            (ShadowReadinessOptions options) => Results.Ok(BuildStatus(options, liveExecutionEnabled)));

        return endpoints;
    }

    private static ShadowReadinessStatusV1 BuildStatus(
        ShadowReadinessOptions options,
        bool liveExecutionEnabled)
    {
        var checks = new List<ShadowReadinessCheckV1>
        {
            new(
                "environment-boundary",
                string.Equals(options.Environment, "SHADOW", StringComparison.OrdinalIgnoreCase) &&
                !liveExecutionEnabled
                    ? ShadowReadinessContractV1.CheckPass
                    : ShadowReadinessContractV1.CheckFail,
                "SHADOW readiness requires SHADOW metadata and live execution disabled."),
            new(
                "broker-adapter-boundary",
                string.IsNullOrWhiteSpace(options.BrokerAdapter)
                    ? ShadowReadinessContractV1.CheckFail
                    : ShadowReadinessContractV1.CheckPass,
                "Broker adapter is identified by name only and remains behind the adapter boundary."),
            new(
                "broker-order-authority",
                !options.AllowBrokerOrderSubmission &&
                !options.AllowBrokerOrderModification &&
                !options.AllowBrokerOrderCancellation
                    ? ShadowReadinessContractV1.CheckPass
                    : ShadowReadinessContractV1.CheckFail,
                "Broker order submission, modification and cancellation authority must remain disabled."),
            new(
                "portfolio-mutation-authority",
                !options.AllowPortfolioMutation
                    ? ShadowReadinessContractV1.CheckPass
                    : ShadowReadinessContractV1.CheckFail,
                "Broker dry-run evidence must not mutate portfolio state."),
            new(
                "instrument-universe",
                options.InstrumentUniverse.Count > 0
                    ? ShadowReadinessContractV1.CheckPass
                    : ShadowReadinessContractV1.CheckFail,
                $"Configured canonical instruments: {options.InstrumentUniverse.Count}."),
            new(
                "broker-configuration-readiness",
                options.RequireBrokerConfigurationCheck
                    ? ShadowReadinessContractV1.CheckNotEvaluated
                    : ShadowReadinessContractV1.CheckPass,
                "This Phase 7.0 slice records the required check but does not read broker configuration values."),
            new(
                "instrument-mapping-readiness",
                options.RequireInstrumentMappingCheck
                    ? ShadowReadinessContractV1.CheckNotEvaluated
                    : ShadowReadinessContractV1.CheckPass,
                "This Phase 7.0 slice records the required check before broker mapping verification is wired."),
            new(
                "market-session-readiness",
                options.RequireMarketSessionCheck
                    ? ShadowReadinessContractV1.CheckNotEvaluated
                    : ShadowReadinessContractV1.CheckPass,
                "This Phase 7.0 slice records the required check before exchange-calendar verification is wired."),
        };

        var hasFailed = checks.Any(check => check.Status == ShadowReadinessContractV1.CheckFail);
        var hasPending = checks.Any(check => check.Status == ShadowReadinessContractV1.CheckNotEvaluated);
        var overallStatus = options.Enabled switch
        {
            false => ShadowReadinessContractV1.StatusDisabled,
            true when hasFailed || hasPending => ShadowReadinessContractV1.StatusNotReady,
            _ => ShadowReadinessContractV1.StatusReady,
        };

        return new ShadowReadinessStatusV1(
            ContractVersion: ShadowReadinessContractV1.ContractVersion,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            ReadinessVersion: options.ReadinessVersion,
            Environment: options.Environment,
            Mode: options.Mode,
            BrokerAdapter: options.BrokerAdapter,
            Enabled: options.Enabled,
            OverallStatus: overallStatus,
            Checks: checks,
            Authority: new ShadowReadinessAuthorityV1(
                BrokerOrderSubmission: false,
                BrokerOrderModification: false,
                BrokerOrderCancellation: false,
                PortfolioMutation: false,
                RiskOverride: false,
                LiveExecution: false));
    }
}
