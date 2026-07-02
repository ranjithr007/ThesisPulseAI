using ThesisPulse.Shared.Contracts.Execution.V1;

namespace ThesisPulse.Operations.Service;

public static class ExecutionOperationalStateEndpoint
{
    public static IEndpointRouteBuilder MapExecutionOperationalState(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/internal/v1/execution/operations",
            (
                HttpRequest request,
                IConfiguration configuration,
                AutomaticPaperWorkflowOptions options) =>
            {
                if (!Authorized(request, configuration))
                    return Results.Unauthorized();

                var now = DateTimeOffset.UtcNow;
                var marketOpen = IsMarketOpen(options, now);
                return Results.Ok(new ExecutionOperationalStateV1(
                    options.KillSwitchActive,
                    options.TradingHalted,
                    marketOpen,
                    options.MarketDataHealthy,
                    options.PaperGatewayHealthy,
                    now));
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

    private static bool IsMarketOpen(
        AutomaticPaperWorkflowOptions options,
        DateTimeOffset now)
    {
        if (!options.NewExposureEnabled || !options.SessionCalendarHealthy)
            return false;

        try
        {
            var zone = ResolveTimeZone(options.MarketTimeZone);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                return false;

            var time = TimeOnly.FromDateTime(local.DateTime);
            return time >= options.MarketOpenLocal &&
                time < options.NewEntryCutoffLocal;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

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
}
