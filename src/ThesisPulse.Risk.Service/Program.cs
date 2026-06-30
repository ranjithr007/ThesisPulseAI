using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Risk.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.Configure<DeterministicRiskOptions>(
    builder.Configuration.GetSection(DeterministicRiskOptions.SectionName));
builder.Services.AddSingleton<IRiskDecisionEngine, DeterministicRiskDecisionEngine>();

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Risk.Service");
app.MapGet("/api/v1/status", () => Results.Ok(new
{
    mode = "DETERMINISTIC_RISK",
    environment = "PAPER",
    failClosed = true,
    defaultDecision = RiskDecisionContractV1.Rejected,
    approvalEnabled = true,
    authority = "RISK_DECISION_ONLY",
    tradePlanAuthority = false,
    positionSizingAuthority = false,
    executionAuthority = false,
}));
app.MapPost("/api/v1/risk/evaluate", (RiskDecisionRequestV1 request, IRiskDecisionEngine engine) =>
{
    var decision = engine.Evaluate(request);
    return decision.Decision == RiskDecisionContractV1.Approved
        ? Results.Ok(decision)
        : Results.UnprocessableEntity(decision);
});
app.Run();

public partial class Program
{
}
