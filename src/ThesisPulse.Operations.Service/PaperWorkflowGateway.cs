using System.Net;
using System.Net.Http.Json;
using ThesisPulse.Shared.Contracts.Execution.V1;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;
using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Operations.Service;

public sealed record PaperWorkflowGatewayOptions
{
    public required Uri ThesisServiceBaseUrl { get; init; }

    public required Uri RiskServiceBaseUrl { get; init; }

    public required Uri ExecutionServiceBaseUrl { get; init; }

    public required Uri PortfolioServiceBaseUrl { get; init; }

    public string? InternalApiKey { get; init; }

    public int TimeoutSeconds { get; init; } = 15;
}

public sealed class PaperWorkflowGatewayException : Exception
{
    public PaperWorkflowGatewayException(
        string message,
        bool retryable,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Retryable = retryable;
        StatusCode = statusCode;
    }

    public bool Retryable { get; }

    public HttpStatusCode? StatusCode { get; }
}

public interface IPaperWorkflowGateway
{
    Task<ThesisFusionResultV1> EvaluateThesisAsync(
        ThesisFusionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken);

    Task<RiskDecisionV1> EvaluateRiskAsync(
        RiskDecisionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken);

    Task<TradePlanBuildResultV1> BuildTradePlanAsync(
        TradePlanBuildRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken);

    Task<ExecutionCommandResultV1> AuthorizeExecutionAsync(
        ExecutionCommandRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken);

    Task<PaperOrderTransitionResultV1> ApplyOrderEventAsync(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken);

    Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken);

    Task<LedgerReconciliationResultV1> ReconcileAsync(
        LedgerReconciliationRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class HttpPaperWorkflowGateway(
    IHttpClientFactory httpClientFactory,
    PaperWorkflowGatewayOptions options) : IPaperWorkflowGateway
{
    public Task<ThesisFusionResultV1> EvaluateThesisAsync(
        ThesisFusionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendAsync<ThesisFusionResultV1>(
            "PaperWorkflow.Thesis",
            "/api/v1/theses/evaluate",
            request,
            correlationId,
            cancellationToken);

    public Task<RiskDecisionV1> EvaluateRiskAsync(
        RiskDecisionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendAsync<RiskDecisionV1>(
            "PaperWorkflow.Risk",
            "/api/v1/risk/evaluate",
            request,
            correlationId,
            cancellationToken);

    public Task<TradePlanBuildResultV1> BuildTradePlanAsync(
        TradePlanBuildRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendAsync<TradePlanBuildResultV1>(
            "PaperWorkflow.Risk",
            "/api/v1/trade-plans/build",
            request,
            correlationId,
            cancellationToken);

    public Task<ExecutionCommandResultV1> AuthorizeExecutionAsync(
        ExecutionCommandRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendAsync<ExecutionCommandResultV1>(
            "PaperWorkflow.Execution",
            "/api/v1/execution/commands",
            request,
            correlationId,
            cancellationToken);

    public Task<PaperOrderTransitionResultV1> ApplyOrderEventAsync(
        Guid paperOrderUid,
        PaperOrderEventRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendAsync<PaperOrderTransitionResultV1>(
            "PaperWorkflow.Execution",
            $"/api/v1/paper-orders/{paperOrderUid:D}/events",
            request,
            correlationId,
            cancellationToken);

    public Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendAsync<PortfolioFillProjectionResultV1>(
            "PaperWorkflow.Portfolio",
            "/api/v1/portfolio/fills/project",
            request,
            correlationId,
            cancellationToken);

    public Task<LedgerReconciliationResultV1> ReconcileAsync(
        LedgerReconciliationRequestV1 request,
        string correlationId,
        CancellationToken cancellationToken) =>
        SendAsync<LedgerReconciliationResultV1>(
            "PaperWorkflow.Portfolio",
            "/api/v1/portfolio/reconcile",
            request,
            correlationId,
            cancellationToken);

    private async Task<T> SendAsync<T>(
        string clientName,
        string path,
        object payload,
        string correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(clientName);
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload),
            };
            message.Headers.Add("X-Correlation-ID", correlationId);
            if (!string.IsNullOrWhiteSpace(options.InternalApiKey))
            {
                message.Headers.Add("X-ThesisPulse-Internal-Key", options.InternalApiKey);
            }

            using var response = await client.SendAsync(message, cancellationToken);
            var result = await response.Content.ReadFromJsonAsync<T>(
                cancellationToken: cancellationToken);
            if (result is not null)
            {
                return result;
            }

            var retryable = response.StatusCode == HttpStatusCode.RequestTimeout ||
                (int)response.StatusCode >= 500;
            throw new PaperWorkflowGatewayException(
                $"{clientName} returned HTTP {(int)response.StatusCode} with no response body.",
                retryable,
                response.StatusCode);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PaperWorkflowGatewayException(
                $"{clientName} timed out.",
                true,
                null,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new PaperWorkflowGatewayException(
                $"{clientName} transport failed: {exception.Message}",
                true,
                exception.StatusCode,
                exception);
        }
    }
}
