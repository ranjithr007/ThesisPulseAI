using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Risk.V1;
using ThesisPulse.Shared.Contracts.Signals.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Risk.Service;

public sealed class CanonicalSignalRiskIntakeOptions
{
    public const string SectionName = "CanonicalSignalRiskIntake";

    public bool Enabled { get; init; }
    public string PortfolioServiceBaseUrl { get; init; } = "http://localhost:5107";
    public string PortfolioCode { get; init; } = "PRIMARY-PAPER";
    public string AccountKey { get; init; } = "PRIMARY-PAPER";
    public string RiskPolicyVersion { get; init; } = "risk-policy-v1.0.0";
    public int PollIntervalSeconds { get; init; } = 5;
    public int BatchSize { get; init; } = 50;
    public decimal CurrentDrawdownPercent { get; init; }
    public bool KillSwitchActive { get; init; }
    public bool TradingHalted { get; init; }
    public bool MarketOpen { get; init; }
    public bool MarketDataHealthy { get; init; }
    public bool PortfolioStateHealthy { get; init; }
    public bool BrokerConnectivityHealthy { get; init; }

    public void Validate()
    {
        if (!Enabled)
            return;

        if (!Uri.TryCreate(PortfolioServiceBaseUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("CanonicalSignalRiskIntake:PortfolioServiceBaseUrl is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(PortfolioCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(RiskPolicyVersion);
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("CanonicalSignalRiskIntake:PollIntervalSeconds must be between 1 and 300.");
        if (BatchSize is < 1 or > 500)
            throw new InvalidOperationException("CanonicalSignalRiskIntake:BatchSize must be between 1 and 500.");
        if (CurrentDrawdownPercent < 0)
            throw new InvalidOperationException("CanonicalSignalRiskIntake:CurrentDrawdownPercent cannot be negative.");
    }
}

public sealed record CanonicalSignalRiskCandidate(
    Guid MessageUid,
    string CorrelationId,
    Guid? CausationMessageUid,
    SignalGeneratedV1 Signal,
    FusionSignalLineageV1 Lineage);

public interface ICanonicalSignalRiskCandidateStore
{
    Task<IReadOnlyCollection<CanonicalSignalRiskCandidate>> ReadPendingAsync(
        int maximumCount,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken);
}

public sealed class SqlServerCanonicalSignalRiskCandidateStore(
    SignalRiskPersistenceOptions persistenceOptions) : ICanonicalSignalRiskCandidateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyCollection<CanonicalSignalRiskCandidate>> ReadPendingAsync(
        int maximumCount,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@maximum_count)
                s.[message_uid], s.[correlation_id], s.[causation_id],
                s.[raw_contract_json],
                l.[thesis_uid], l.[thesis_request_uid], l.[candidate_signal_uid],
                l.[fusion_evidence_uid], l.[source_candle_message_uid],
                l.[confirmation_output_uid], l.[confirmation_message_uid],
                l.[fusion_engine_version], l.[fusion_policy_version],
                l.[weight_configuration_version]
            FROM [intelligence].[signals] s WITH (READPAST)
            INNER JOIN [intelligence].[signal_fusion_lineage] l
                ON l.[signal_id] = s.[signal_id]
            OUTER APPLY
            (
                SELECT TOP (1) se.[status]
                FROM [intelligence].[signal_status_events] se
                WHERE se.[signal_id] = s.[signal_id]
                ORDER BY se.[event_sequence] DESC
            ) current_status
            WHERE COALESCE(current_status.[status], s.[initial_status]) IN ('CANDIDATE','VALIDATED')
              AND s.[valid_until_utc] > @as_of_utc
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [risk].[signal_risk_work_items] w
                  WHERE w.[message_uid] = s.[message_uid]
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [risk].[signal_risk_evaluations] e
                  WHERE e.[source_message_uid] = s.[message_uid]
              )
            ORDER BY s.[generated_at_utc], s.[signal_id];
            """;

        await using var connection = new SqlConnection(persistenceOptions.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@maximum_count", SqlDbType.Int).Value = maximumCount;
        command.Parameters.Add("@as_of_utc", SqlDbType.DateTime2).Value = asOfUtc.UtcDateTime;

        var candidates = new List<CanonicalSignalRiskCandidate>(maximumCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var signal = JsonSerializer.Deserialize<SignalGeneratedV1>(reader.GetString(3), JsonOptions)
                ?? throw new InvalidOperationException("Canonical signal payload could not be deserialized.");
            var lineage = new FusionSignalLineageV1(
                reader.GetGuid(4),
                reader.GetGuid(5),
                reader.GetGuid(6),
                reader.GetGuid(7),
                reader.GetGuid(8),
                reader.GetGuid(9),
                reader.GetGuid(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.GetString(13));
            candidates.Add(new CanonicalSignalRiskCandidate(
                reader.GetGuid(0),
                reader.GetGuid(1).ToString("D"),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                signal,
                lineage));
        }

        return candidates;
    }
}

public interface ICanonicalSignalRiskContextProvider
{
    Task<PortfolioLedgerSnapshotV1?> GetPortfolioAsync(
        string portfolioCode,
        CancellationToken cancellationToken);
}

public sealed class HttpCanonicalSignalRiskContextProvider(
    HttpClient client) : ICanonicalSignalRiskContextProvider
{
    public async Task<PortfolioLedgerSnapshotV1?> GetPortfolioAsync(
        string portfolioCode,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"/api/v1/portfolio/{Uri.EscapeDataString(portfolioCode)}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PortfolioLedgerSnapshotV1>(
            cancellationToken: cancellationToken);
    }
}

public sealed class CanonicalSignalRiskIntakeWorker(
    CanonicalSignalRiskIntakeOptions options,
    ICanonicalSignalRiskCandidateStore candidateStore,
    ICanonicalSignalRiskContextProvider contextProvider,
    ISignalRiskWorkQueue queue,
    ILogger<CanonicalSignalRiskIntakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
            return;

        var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DiscoverAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Canonical Signal-to-Risk discovery failed closed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var portfolio = await contextProvider.GetPortfolioAsync(options.PortfolioCode, cancellationToken);
        if (portfolio is null)
        {
            logger.LogWarning("Canonical Signal-to-Risk discovery skipped because portfolio {PortfolioCode} was not found.", options.PortfolioCode);
            return;
        }

        var candidates = await candidateStore.ReadPendingAsync(options.BatchSize, now, cancellationToken);
        foreach (var candidate in candidates)
        {
            var intake = new SignalRiskEvaluationIntakeV1(
                candidate.MessageUid,
                candidate.CorrelationId,
                candidate.CausationMessageUid,
                candidate.Signal,
                candidate.Lineage,
                BuildPortfolio(portfolio, now),
                new OperationalRiskStateV1(
                    options.KillSwitchActive,
                    options.TradingHalted,
                    options.MarketOpen,
                    options.MarketDataHealthy,
                    options.PortfolioStateHealthy,
                    options.BrokerConnectivityHealthy,
                    now),
                options.RiskPolicyVersion,
                now);
            await queue.EnqueueAsync(intake, cancellationToken);
        }
    }

    private PortfolioRiskSnapshotV1 BuildPortfolio(
        PortfolioLedgerSnapshotV1 portfolio,
        DateTimeOffset now)
    {
        var availableCash = portfolio.CashBalances.Sum(item => item.AvailableAmount);
        var totalCash = portfolio.CashBalances.Sum(item => item.TotalBalanceAmount);
        return new PortfolioRiskSnapshotV1(
            options.AccountKey,
            portfolio.Environment,
            totalCash + portfolio.NetExposureAmount,
            availableCash,
            portfolio.GrossExposureAmount,
            portfolio.NetExposureAmount,
            portfolio.RealizedPnlAmount,
            portfolio.UnrealizedPnlAmount,
            options.CurrentDrawdownPercent,
            portfolio.OpenPositionCount,
            portfolio.Positions
                .Where(item => string.Equals(item.Status, "OPEN", StringComparison.Ordinal))
                .Select(item => new PortfolioPositionV1(
                    item.InstrumentKey,
                    item.Direction,
                    item.MarketValueAmount,
                    item.OpenedAtUtc ?? item.UpdatedAtUtc))
                .ToArray(),
            now);
    }
}
