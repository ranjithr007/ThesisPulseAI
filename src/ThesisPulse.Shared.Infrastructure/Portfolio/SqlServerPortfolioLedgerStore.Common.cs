using System.Data;
using Microsoft.Data.SqlClient;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed partial class SqlServerPortfolioLedgerStore : IPortfolioLedgerStore
{
    private static readonly IReadOnlySet<string> ReconciliationTriggers =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "UNKNOWN_OUTCOME",
            "STARTUP",
            "PERIODIC",
            "STREAM_RECOVERY",
            "QUANTITY_MISMATCH",
            "SESSION_SHUTDOWN",
            "OPERATOR_REQUEST",
        };

    private readonly SqlServerPortfolioLedgerOptions _options;

    public SqlServerPortfolioLedgerStore(SqlServerPortfolioLedgerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    private SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = _options.CommandTimeoutSeconds };

    private static EvidenceDirectionV1 ParseDirection(string value) => value switch
    {
        "LONG" => EvidenceDirectionV1.Long,
        "SHORT" => EvidenceDirectionV1.Short,
        "FLAT" => EvidenceDirectionV1.Neutral,
        _ => throw new InvalidOperationException($"Unknown position side '{value}'."),
    };

    private static string PositionSide(EvidenceDirectionV1 direction) => direction switch
    {
        EvidenceDirectionV1.Long => "LONG",
        EvidenceDirectionV1.Short => "SHORT",
        EvidenceDirectionV1.Neutral => "FLAT",
        _ => throw new ArgumentOutOfRangeException(nameof(direction)),
    };

    private static void AddDateTime(SqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(name, SqlDbType.DateTime2).Value = value.UtcDateTime;

    private static void AddNullableDateTime(SqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(name, SqlDbType.DateTime2).Value =
            value is null ? DBNull.Value : value.Value.UtcDateTime;

    private static void AddDecimal(
        SqlCommand command,
        string name,
        decimal value,
        byte precision = 19,
        byte scale = 6)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = value;
    }

    private static void AddNullableDecimal(
        SqlCommand command,
        string name,
        decimal? value,
        byte precision = 19,
        byte scale = 6)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = precision;
        parameter.Scale = scale;
        parameter.Value = (object?)value ?? DBNull.Value;
    }

    private static DateTimeOffset ReadUtc(SqlDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private sealed record FillRow(
        long FillId,
        Guid FillUid,
        long OrderId,
        long BrokerAccountId,
        long InstrumentId,
        long PortfolioId,
        string PortfolioCode,
        string Environment,
        string CurrencyCode,
        string InstrumentKey,
        string ProductType,
        string Side,
        decimal Quantity,
        decimal Price,
        decimal FeesAmount,
        decimal TaxesAmount,
        DateTimeOffset FillAtUtc,
        Guid CorrelationId);

    private sealed record PositionRow(
        long? PositionId,
        Guid PositionUid,
        PositionAccountingState State,
        IReadOnlyCollection<PositionLotState> Lots,
        DateTimeOffset? OpenedAtUtc);

    private sealed record PortfolioRow(
        long PortfolioId,
        Guid PortfolioUid,
        string PortfolioCode,
        string Environment,
        long BrokerAccountId,
        string StrategyCode,
        string CurrencyCode,
        string AccountingMethod);
}
