using ThesisPulse.Shared.Contracts.TradePlans.V1;

namespace ThesisPulse.Risk.Service;

public sealed class AutomaticTradePlanWorkProcessor(
    AutomaticTradePlanWorkerOptions options,
    IAutomaticTradePlanWorkQueue queue,
    IAutomaticTradePlanProjector projector,
    ITradePlanBuilder builder,
    IAutomaticTradePlanResultStore resultStore,
    AutomaticTradePlanWorkerState state,
    ILogger<AutomaticTradePlanWorkProcessor> logger)
{
    public async Task ProcessAsync(
        AutomaticTradePlanWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var projection = projector.Project(item.Intake);
            if (projection.Command is null)
            {
                var reason = string.Join(',', projection.Reasons);
                if (projection.Reasons.Any(code => code is "SIGNAL_EXPIRED" or "RISK_BUDGET_EXPIRED_OR_MISSING"))
                {
                    await queue.ExpireAsync(item.WorkItemId, reason, cancellationToken);
                    state.Expired();
                }
                else
                {
                    await queue.RejectAsync(item.WorkItemId, reason, cancellationToken);
                    state.Rejected();
                }
                return;
            }

            var buildResult = builder.Build(projection.Command.Request);
            if (buildResult.Status != TradePlanContractV1.Ready || buildResult.TradePlan is null)
            {
                await queue.RejectAsync(
                    item.WorkItemId,
                    string.Join(',', buildResult.Reasons),
                    cancellationToken);
                state.Rejected();
                return;
            }

            var persistence = await resultStore.PersistReadyAsync(
                projection.Command,
                buildResult,
                cancellationToken);
            await queue.CompleteAsync(item.WorkItemId, cancellationToken);
            state.Completed(string.Equals(persistence.Outcome, "DUPLICATE", StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            if (item.AttemptCount >= options.MaximumAttempts)
            {
                await queue.FailAsync(item.WorkItemId, exception.Message, cancellationToken);
                state.Failed();
            }
            else
            {
                var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(item.AttemptCount - 1, 6)));
                await queue.RetryAsync(
                    item.WorkItemId,
                    exception.Message,
                    DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
                    cancellationToken);
                state.Retried();
            }

            logger.LogWarning(
                exception,
                "Automatic Trade Plan work item {WorkItemId} failed on attempt {AttemptCount}.",
                item.WorkItemId,
                item.AttemptCount);
        }
    }
}
