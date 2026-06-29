using ThesisPulse.Shared.Observability.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();

app.MapThesisPulsePlatformEndpoints(
    serviceName: "ThesisPulse.Trading.Api",
    contractVersion: "v1");

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
