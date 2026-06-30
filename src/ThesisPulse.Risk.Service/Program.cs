using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;
using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Risk.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.Configure<DeterministicRiskOptions>(
    builder.Configuration.GetSection(DeterministicRiskOptions.SectionName));
builder.Services.Configure<DeterministicTradePlanOptions>(
    builder.Configuration.GetSection(DeterministicTradePlanOptions.SectionName));
builder.Services.AddSingleton<IRiskDecisionEngine, DeterministicRiskDecisionEngine>();
builder.Services.AddSingleton<ITradePlanBuilder, DeterministicTradePlanBuilder>();

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Risk.Service");
app.MapGet("/api/v1/status", () => Results.Ok(new
{
    mode = "DETERMINISTIC_RISK_AND_TRADE_PLAN",
    environment = "PAPER",
    failClosed = true,
    defaultRiskDecision = RiskDecisionContractV1.Rejected,
    riskDecisionAuthority = true,
    tradePlanAuthority = true,
    positionSizingAuthority = true,
    executionAuthority = false,
    brokerSubmissionAuthority = false,
}));
app.MapPost("/api/v1/risk/evaluate", (RiskDecisionRequestV1 request, IRiskDecisionEngine engine) =>
{
    var decision = engine.Evaluate(request);
    return decision.Decision == RiskDecisionContractV1.Approved
        ? Results.Ok(decision)
        : Results.UnprocessableEntity(decision);
});
app.MapPost("/api/v1/trade-plans/build", (TradePlanBuildRequestV1 request, ITradePlanBuilder planBuilder) =>
{
    var result = planBuilder.Build(request);
    return result.Status == TradePlanContractV1.Ready
        ? Results.Ok(result)
        : Results.UnprocessableEntity(result);
});
app.Run();

public partial class Program
{
}
