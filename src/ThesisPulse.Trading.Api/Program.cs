using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Trading.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddTradingSignalStream(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();

app.MapThesisPulsePlatformEndpoints(
    serviceName: "ThesisPulse.Trading.Api",
    contractVersion: "v1");
app.MapTradingSignalStream();

app.MapGet(
    "/api/v1/platform",
    () => Results.Ok(new
    {
        name = "ThesisPulse AI",
        tagline = "Intelligent signals. Validated theses. Adaptive decisions.",
        environment = "PAPER",
        liveExecutionEnabled = false,
    }));

app.Run();

public partial class Program
{
}
