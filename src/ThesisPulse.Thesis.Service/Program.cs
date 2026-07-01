using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Thesis.Service;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.Configure<DeterministicFusionOptions>(
    builder.Configuration.GetSection(DeterministicFusionOptions.SectionName));
builder.Services.AddSingleton<IThesisFusionEngine, DeterministicThesisFusionEngine>();
var signalProjectionOptions = builder.Configuration
    .GetSection(FusionSignalProjectorOptions.SectionName)
    .Get<FusionSignalProjectorOptions>() ?? new FusionSignalProjectorOptions();
builder.Services.AddSingleton(signalProjectionOptions);
builder.Services.AddSingleton<IFusionSignalProjector, DeterministicFusionSignalProjector>();

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Thesis.Service");
app.MapGet("/api/v1/status", () => Results.Ok(new
{
    mode = "DETERMINISTIC_FUSION",
    environment = "PAPER",
    immutableVersioningRequired = true,
    evaluationEnabled = true,
    signalProjectionEnabled = true,
    signalProjectionContractVersion = FusionSignalProjectionContractV1.ContractVersion,
    authority = "CANDIDATE_ONLY",
    riskAuthority = false,
    tradePlanAuthority = false,
    executionAuthority = false,
}));
app.MapPost("/api/v1/theses/evaluate", (ThesisFusionRequestV1 request, IThesisFusionEngine engine) =>
{
    var result = engine.Evaluate(request);
    return result.Candidate is null
        ? Results.UnprocessableEntity(result)
        : Results.Ok(result);
});
app.MapPost(
    "/api/v1/theses/project-signal",
    (FusionSignalProjectionRequestV1 request, IFusionSignalProjector projector) =>
    {
        var result = projector.Project(request);
        return result.Intake is null
            ? Results.UnprocessableEntity(result)
            : Results.Ok(result);
    });
app.Run();

public partial class Program
{
}
