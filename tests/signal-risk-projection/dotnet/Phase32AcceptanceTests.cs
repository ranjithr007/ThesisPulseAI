using ThesisPulse.Risk.Service;
using ThesisPulse.Shared.Contracts.Risk.V1;

internal static class Phase32AcceptanceTests
{
    public static async Task<IReadOnlyCollection<string>> RunAsync()
    {
        var failures = new List<string>();

        Run(failures, "retry transitions stay fail closed", () =>
        {
            True(RiskStatusTransitionMatrix.CanTransition(
                RiskStatusTransitionMatrix.NotEvaluated,
                SignalRiskEvaluationContractV1.RiskRetryPending));
            True(RiskStatusTransitionMatrix.CanTransition(
                SignalRiskEvaluationContractV1.RiskRetryPending,
                SignalRiskEvaluationContractV1.RiskEvaluating));
            True(RiskStatusTransitionMatrix.CanTransition(
                SignalRiskEvaluationContractV1.RiskRetryPending,
                SignalRiskEvaluationContractV1.RiskExpired));
            False(RiskStatusTransitionMatrix.CanTransition(
                SignalRiskEvaluationContractV1.RiskApproved,
                SignalRiskEvaluationContractV1.RiskEvaluating));
            False(RiskStatusTransitionMatrix.CanTransition(
                SignalRiskEvaluationContractV1.RiskExpired,
                SignalRiskEvaluationContractV1.RiskRetryPending));
        });

        await RunAsync(failures, "worker counters are concurrency safe", async () =>
        {
            var state = new SignalRiskWorkerState();
            await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            {
                state.Leased(1);
                state.Completed(duplicate: true);
                state.Retried();
                state.Failed();
            })));

            var snapshot = state.Read();
            Equal(100L, snapshot.Leased);
            Equal(100L, snapshot.Completed);
            Equal(100L, snapshot.Duplicates);
            Equal(100L, snapshot.Retried);
            Equal(100L, snapshot.Failed);

            var metrics = await new InMemorySignalRiskMetricsStore(state)
                .ReadAsync(CancellationToken.None);
            Equal(100L, metrics.Completed);
            Equal(100L, metrics.Duplicates);
            Equal(100L, metrics.RetryPending);
            Equal(100L, metrics.Failed);
            Equal(0L, metrics.Approved);
        });

        Run(failures, "canonical intake defaults fail closed", () =>
        {
            var options = new CanonicalSignalRiskIntakeOptions();
            options.Validate();
            False(options.Enabled);
            False(options.MarketOpen);
            False(options.MarketDataHealthy);
            False(options.PortfolioStateHealthy);
            False(options.BrokerConnectivityHealthy);

            Throws<InvalidOperationException>(() => new CanonicalSignalRiskIntakeOptions
            {
                Enabled = true,
                PortfolioServiceBaseUrl = "not-a-url",
            }.Validate());
        });

        Run(failures, "worker settings reject unsafe bounds", () =>
        {
            Throws<InvalidOperationException>(() => new SignalRiskWorkerOptions
            {
                BatchSize = 0,
            }.Validate());
            Throws<InvalidOperationException>(() => new SignalRiskWorkerOptions
            {
                MaximumAttempts = 0,
            }.Validate());
            Throws<InvalidOperationException>(() => new SignalRiskWorkerOptions
            {
                PollIntervalSeconds = 0,
            }.Validate());
        });

        return failures;
    }

    private static void Run(ICollection<string> failures, string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    private static async Task RunAsync(
        ICollection<string> failures,
        string name,
        Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.Message}");
        }
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value)
    {
        if (value) throw new InvalidOperationException("Expected false.");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}' but received '{actual}'.");
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
