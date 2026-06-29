using ThesisPulse.Shared.Observability.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["Platform:ConfigurationVersion"] ??= "platform-foundation-v1.0.0";
builder.Services.AddThesisPulsePlatformFoundation();

var app = builder.Build();
app.UseThesisPulsePlatformFoundation();
app.MapThesisPulsePlatformEndpoints("ThesisPulse.Operations.Service");
app.MapGet("/api/v1/status", () => Results.Ok(new
{
    mode = "FOUNDATION",
    environment = "PAPER",
    schedulerEnabled = false,
    healthAggregationEnabled = true,
}));
app.Run();

public partial class Program
{
}
