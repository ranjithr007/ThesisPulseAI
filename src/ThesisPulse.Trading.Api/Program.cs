using ThesisPulse.Shared.Observability.Hosting;
using ThesisPulse.Trading.Api;

const string frontendCorsPolicy = "Frontend";

var builder = WebApplication.CreateBuilder(args);

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .GetChildren()
    .Select(item => item.Value)
    .Where(value => !string.IsNullOrWhiteSpace(value))
    .Cast<string>()
    .ToArray();

if (allowedOrigins.Length == 0)
{
    allowedOrigins = new[] { "http://localhost:5173" };
}

builder.Services.AddThesisPulsePlatformFoundation();
builder.Services.AddTradingSignalStream(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddCors(options =>
{
    options.AddPolicy(frontendCorsPolicy, policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseThesisPulsePlatformFoundation();
app.UseCors(frontendCorsPolicy);

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
