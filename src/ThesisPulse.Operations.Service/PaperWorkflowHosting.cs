using System.Security.Cryptography;
using System.Text;
using ThesisPulse.Shared.Contracts.Workflows.V1;

namespace ThesisPulse.Operations.Service;

public sealed class PaperWorkflowRecoveryWorker(
    PaperWorkflowOptions options,
    IPaperWorkflowStore store,
    PaperWorkflowCoordinator coordinator,
    ILogger<PaperWorkflowRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("PAPER workflow orchestration is disabled.");
            return;
        }

        await RecoverAsync(stoppingToken);
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.RecoveryIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RecoverAsync(stoppingToken);
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var due = await store.GetDueWorkflowUidsAsync(
            DateTimeOffset.UtcNow,
            options.RecoveryBatchSize,
            cancellationToken);
        foreach (var workflowUid in due)
        {
            try
            {
                await coordinator.ResumeAsync(workflowUid, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "PAPER workflow recovery failed. WorkflowUid={WorkflowUid}",
                    workflowUid);
            }
        }
    }
}

public static class PaperWorkflowHostingExtensions
{
    public static IServiceCollection AddPaperWorkflowOrchestration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new PaperWorkflowOptions
        {
            Enabled = configuration.GetValue("PaperWorkflow:Enabled", false),
            MaximumAttempts = configuration.GetValue("PaperWorkflow:MaximumAttempts", 3),
            RetryDelaySeconds = configuration.GetValue("PaperWorkflow:RetryDelaySeconds", 15),
            RecoveryIntervalSeconds = configuration.GetValue(
                "PaperWorkflow:RecoveryIntervalSeconds",
                15),
            RecoveryBatchSize = configuration.GetValue("PaperWorkflow:RecoveryBatchSize", 25),
        };
        Validate(options);
        services.AddSingleton(options);

        var gatewayOptions = new PaperWorkflowGatewayOptions
        {
            ThesisServiceBaseUrl = AbsoluteUri(
                configuration["PaperWorkflow:ThesisServiceBaseUrl"]
                    ?? "http://localhost:5103",
                "PaperWorkflow:ThesisServiceBaseUrl"),
            RiskServiceBaseUrl = AbsoluteUri(
                configuration["PaperWorkflow:RiskServiceBaseUrl"]
                    ?? "http://localhost:5104",
                "PaperWorkflow:RiskServiceBaseUrl"),
            ExecutionServiceBaseUrl = AbsoluteUri(
                configuration["PaperWorkflow:ExecutionServiceBaseUrl"]
                    ?? "http://localhost:5105",
                "PaperWorkflow:ExecutionServiceBaseUrl"),
            PortfolioServiceBaseUrl = AbsoluteUri(
                configuration["PaperWorkflow:PortfolioServiceBaseUrl"]
                    ?? "http://localhost:5106",
                "PaperWorkflow:PortfolioServiceBaseUrl"),
            InternalApiKey = configuration["PaperWorkflow:InternalApiKey"],
            TimeoutSeconds = configuration.GetValue("PaperWorkflow:TimeoutSeconds", 15),
        };
        if (gatewayOptions.TimeoutSeconds < 1)
        {
            throw new InvalidOperationException(
                "PaperWorkflow:TimeoutSeconds must be at least one second.");
        }
        if (options.Enabled && string.IsNullOrWhiteSpace(gatewayOptions.InternalApiKey))
        {
            throw new InvalidOperationException(
                "PaperWorkflow:InternalApiKey is required when orchestration is enabled.");
        }

        services.AddSingleton(gatewayOptions);
        AddClient(services, "PaperWorkflow.Thesis", gatewayOptions.ThesisServiceBaseUrl, gatewayOptions.TimeoutSeconds);
        AddClient(services, "PaperWorkflow.Risk", gatewayOptions.RiskServiceBaseUrl, gatewayOptions.TimeoutSeconds);
        AddClient(services, "PaperWorkflow.Execution", gatewayOptions.ExecutionServiceBaseUrl, gatewayOptions.TimeoutSeconds);
        AddClient(services, "PaperWorkflow.Portfolio", gatewayOptions.PortfolioServiceBaseUrl, gatewayOptions.TimeoutSeconds);
        services.AddSingleton<IPaperWorkflowGateway, HttpPaperWorkflowGateway>();

        var provider = configuration["PaperWorkflowPersistence:Provider"] ?? "InMemory";
        if (provider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IPaperWorkflowStore, InMemoryPaperWorkflowStore>();
        }
        else if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("OperationalDatabase");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "OperationalDatabase connection string is required for SQL workflow persistence.");
            }

            var storeOptions = new SqlServerPaperWorkflowStoreOptions
            {
                ConnectionString = connectionString,
                Actor = configuration["PaperWorkflowPersistence:Actor"]
                    ?? "ThesisPulse.Operations.Service",
                CommandTimeoutSeconds = configuration.GetValue(
                    "PaperWorkflowPersistence:CommandTimeoutSeconds",
                    30),
            };
            storeOptions.Validate();
            services.AddSingleton(storeOptions);
            services.AddSingleton<IPaperWorkflowStore, SqlServerPaperWorkflowStore>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported workflow persistence provider '{provider}'.");
        }

        services.AddSingleton<PaperWorkflowCoordinator>();
        services.AddHostedService<PaperWorkflowRecoveryWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapPaperWorkflowEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/v1/paper-workflows/run", async (
            PaperWorkflowStartRequestV1 request,
            HttpRequest httpRequest,
            IConfiguration configuration,
            PaperWorkflowCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (!Authorized(httpRequest, configuration))
            {
                return Results.Unauthorized();
            }

            try
            {
                var result = await coordinator.RunAsync(request, cancellationToken);
                return Result(result);
            }
            catch (PaperWorkflowValidationException exception)
            {
                return Results.BadRequest(new { reasons = exception.Reasons });
            }
            catch (PaperWorkflowIdempotencyException exception)
            {
                return Results.Conflict(new { reason = exception.Message });
            }
        });

        endpoints.MapPost("/internal/v1/paper-workflows/{workflowUid:guid}/resume", async (
            Guid workflowUid,
            HttpRequest httpRequest,
            IConfiguration configuration,
            PaperWorkflowCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            if (!Authorized(httpRequest, configuration))
            {
                return Results.Unauthorized();
            }

            try
            {
                return Result(await coordinator.ResumeAsync(workflowUid, cancellationToken));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        endpoints.MapGet("/api/v1/paper-workflows/{workflowUid:guid}", async (
            Guid workflowUid,
            PaperWorkflowCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            var result = await coordinator.GetAsync(workflowUid, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        return endpoints;
    }

    private static IResult Result(PaperWorkflowResultV1 result) =>
        result.Workflow.Status switch
        {
            PaperWorkflowContractV1.Completed => Results.Ok(result),
            PaperWorkflowContractV1.RetryPending => Results.Json(
                result,
                statusCode: StatusCodes.Status503ServiceUnavailable),
            PaperWorkflowContractV1.Rejected => Results.UnprocessableEntity(result),
            PaperWorkflowContractV1.Failed => Results.Json(
                result,
                statusCode: StatusCodes.Status500InternalServerError),
            _ => Results.Accepted(value: result),
        };

    private static bool Authorized(
        HttpRequest request,
        IConfiguration configuration)
    {
        if (!configuration.GetValue("PaperWorkflow:Enabled", false))
        {
            return false;
        }

        var expected = configuration["PaperWorkflow:InternalApiKey"];
        if (string.IsNullOrWhiteSpace(expected) ||
            !request.Headers.TryGetValue("X-ThesisPulse-Internal-Key", out var supplied))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(supplied.ToString());
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length &&
            CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static void AddClient(
        IServiceCollection services,
        string name,
        Uri baseUri,
        int timeoutSeconds) =>
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });

    private static Uri AbsoluteUri(string value, string settingName) =>
        Uri.TryCreate(value, UriKind.Absolute, out var result)
            ? result
            : throw new InvalidOperationException(
                $"{settingName} must be an absolute URI.");

    private static void Validate(PaperWorkflowOptions options)
    {
        if (options.MaximumAttempts is < 1 or > 10)
            throw new InvalidOperationException("PaperWorkflow:MaximumAttempts must be between 1 and 10.");
        if (options.RetryDelaySeconds < 1)
            throw new InvalidOperationException("PaperWorkflow:RetryDelaySeconds must be at least one second.");
        if (options.RecoveryIntervalSeconds < 5)
            throw new InvalidOperationException("PaperWorkflow:RecoveryIntervalSeconds must be at least five seconds.");
        if (options.RecoveryBatchSize is < 1 or > 500)
            throw new InvalidOperationException("PaperWorkflow:RecoveryBatchSize must be between 1 and 500.");
    }
}
