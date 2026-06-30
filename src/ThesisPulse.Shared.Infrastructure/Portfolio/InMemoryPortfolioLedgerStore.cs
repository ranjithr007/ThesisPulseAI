using System.Collections.Concurrent;
using ThesisPulse.Shared.Contracts.Portfolio.V1;
using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed class InMemoryPortfolioLedgerStore : IPortfolioLedgerStore
{
    private readonly ConcurrentDictionary<Guid, PortfolioFillRecord> _fills = new();
    private readonly ConcurrentDictionary<string, PortfolioAggregate> _portfolios =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterFill(PortfolioFillRecord fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (!_fills.TryAdd(fill.FillUid, fill))
        {
            throw new InvalidOperationException("Fill UID is already registered.");
        }
    }

    public void OverridePositionQuantity(
        string portfolioCode,
        string instrumentKey,
        decimal quantity)
    {
        var aggregate = GetAggregate(portfolioCode);
        lock (aggregate.SyncRoot)
        {
            if (!aggregate.Positions.TryGetValue(instrumentKey, out var position))
            {
                throw new InvalidOperationException("Position does not exist.");
            }

            aggregate.Positions[instrumentKey] = position with
            {
                State = position.State with { Quantity = quantity },
            };
        }
    }

    public Task<PortfolioFillProjectionResultV1> ProjectFillAsync(
        PortfolioFillProjectionRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_fills.TryGetValue(request.FillUid, out var fill))
        {
            return Task.FromResult(new PortfolioFillProjectionResultV1(
                request.RequestUid,
                request.FillUid,
                PortfolioLedgerContractV1.Rejected,
                ["FILL_NOT_FOUND"],
                null,
                null,
                request.AsOfUtc));
        }

        var aggregate = GetAggregate(request.PortfolioCode);
        lock (aggregate.SyncRoot)
        {
            if (aggregate.ProcessedFills.Contains(request.FillUid))
            {
                var existing = aggregate.Positions.TryGetValue(
                    fill.InstrumentKey,
                    out var duplicatePosition)
                    ? ToSnapshot(request.PortfolioCode, fill.Environment, fill.InstrumentKey, duplicatePosition)
                    : null;
                return Task.FromResult(new PortfolioFillProjectionResultV1(
                    request.RequestUid,
                    request.FillUid,
                    PortfolioLedgerContractV1.Duplicate,
                    Array.Empty<string>(),
                    existing,
                    BuildSnapshot(request.PortfolioCode, aggregate, request.AsOfUtc),
                    request.AsOfUtc));
            }

            var position = aggregate.Positions.GetValueOrDefault(fill.InstrumentKey)
                ?? PositionAggregate.Create(fill.ProductType);
            var accounting = DeterministicPositionAccounting.ApplyFill(
                position.State,
                position.Lots,
                new PositionFillInput(
                    fill.FillUid,
                    fill.Side,
                    fill.Quantity,
                    fill.Price,
                    fill.FeesAmount,
                    fill.TaxesAmount,
                    fill.FilledAtUtc));
            var lots = position.Lots
                .Select(lot => accounting.UpdatedExistingLots
                    .FirstOrDefault(updated => updated.LotUid == lot.LotUid) ?? lot)
                .Concat(accounting.NewLots)
                .ToArray();
            var updated = position with
            {
                State = accounting.After,
                Lots = lots,
                MarketValueAmount = accounting.MarketValueAmount,
                UnrealizedPnlAmount = accounting.UnrealizedPnlAmount,
                OpenedAtUtc = position.OpenedAtUtc ??
                    (accounting.After.Quantity > 0 ? fill.FilledAtUtc : null),
                LastFillAtUtc = fill.FilledAtUtc,
                ClosedAtUtc = accounting.After.Quantity == 0 ? fill.FilledAtUtc : null,
                UpdatedAtUtc = fill.FilledAtUtc,
            };
            aggregate.Positions[fill.InstrumentKey] = updated;
            aggregate.ProcessedFills.Add(fill.FillUid);

            var gross = fill.Quantity * fill.Price;
            var cashDelta = fill.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? -(gross + fill.FeesAmount + fill.TaxesAmount)
                : gross - fill.FeesAmount - fill.TaxesAmount;
            var cash = aggregate.Cash.GetValueOrDefault(fill.CurrencyCode)
                ?? new CashState(fill.CurrencyCode, 0m, 0, 0, fill.FilledAtUtc);
            aggregate.Cash[fill.CurrencyCode] = cash with
            {
                SettledAmount = cash.SettledAmount + cashDelta,
                Version = cash.Version + 1,
                Sequence = cash.Sequence + 1,
                AsOfUtc = fill.FilledAtUtc,
            };

            var positionSnapshot = ToSnapshot(
                request.PortfolioCode,
                fill.Environment,
                fill.InstrumentKey,
                updated);
            return Task.FromResult(new PortfolioFillProjectionResultV1(
                request.RequestUid,
                request.FillUid,
                PortfolioLedgerContractV1.Projected,
                Array.Empty<string>(),
                positionSnapshot,
                BuildSnapshot(request.PortfolioCode, aggregate, request.AsOfUtc),
                request.AsOfUtc));
        }
    }

    public Task<PortfolioLedgerSnapshotV1?> GetSnapshotAsync(
        string portfolioCode,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_portfolios.TryGetValue(portfolioCode, out var aggregate))
        {
            return Task.FromResult<PortfolioLedgerSnapshotV1?>(null);
        }

        lock (aggregate.SyncRoot)
        {
            return Task.FromResult<PortfolioLedgerSnapshotV1?>(
                BuildSnapshot(portfolioCode, aggregate, asOfUtc));
        }
    }

    public Task<LedgerReconciliationResultV1> ReconcileAsync(
        LedgerReconciliationRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = request.AsOfUtc;
        var aggregate = GetAggregate(request.PortfolioCode);
        lock (aggregate.SyncRoot)
        {
            var discrepancies = new List<LedgerDiscrepancyV1>();
            foreach (var pair in aggregate.Positions)
            {
                var position = pair.Value;
                var lotQuantity = position.Lots.Sum(lot => lot.RemainingQuantity);
                if (lotQuantity != position.State.Quantity)
                {
                    discrepancies.Add(new LedgerDiscrepancyV1(
                        Guid.NewGuid(),
                        "POSITION_MISMATCH",
                        "HIGH",
                        pair.Key,
                        "Position quantity does not equal the remaining FIFO lot quantity.",
                        lotQuantity,
                        position.State.Quantity,
                        null,
                        null,
                        true,
                        true));
                }
            }

            var status = discrepancies.Count == 0
                ? PortfolioLedgerContractV1.Reconciled
                : PortfolioLedgerContractV1.Discrepant;
            return Task.FromResult(new LedgerReconciliationResultV1(
                request.RequestUid,
                Guid.NewGuid(),
                request.PortfolioCode,
                status,
                aggregate.Positions.Count + aggregate.Cash.Count,
                discrepancies,
                discrepancies.Count > 0,
                true,
                started,
                request.AsOfUtc));
        }
    }

    private PortfolioAggregate GetAggregate(string portfolioCode) =>
        _portfolios.GetOrAdd(
            portfolioCode,
            _ => new PortfolioAggregate(Guid.NewGuid()));

    private static PortfolioLedgerSnapshotV1 BuildSnapshot(
        string portfolioCode,
        PortfolioAggregate aggregate,
        DateTimeOffset asOfUtc)
    {
        var positions = aggregate.Positions
            .Select(pair => ToSnapshot(
                portfolioCode,
                "PAPER",
                pair.Key,
                pair.Value))
            .ToArray();
        var cash = aggregate.Cash.Values
            .Select(value => new CashLedgerSnapshotV1(
                portfolioCode,
                value.CurrencyCode,
                value.SettledAmount,
                0m,
                0m,
                0m,
                value.SettledAmount,
                value.SettledAmount,
                value.Version,
                value.Sequence,
                value.AsOfUtc))
            .ToArray();
        var gross = positions.Sum(position => position.MarketValueAmount);
        var net = positions.Sum(position => position.Direction switch
        {
            EvidenceDirectionV1.Long => position.MarketValueAmount,
            EvidenceDirectionV1.Short => -position.MarketValueAmount,
            _ => 0m,
        });
        return new PortfolioLedgerSnapshotV1(
            aggregate.PortfolioUid,
            portfolioCode,
            "PAPER",
            "FIFO",
            cash.FirstOrDefault()?.CurrencyCode ?? "INR",
            positions,
            cash,
            gross,
            net,
            positions.Sum(position => position.RealizedPnlAmount),
            positions.Sum(position => position.UnrealizedPnlAmount),
            positions.Count(position => position.Status == "OPEN"),
            asOfUtc);
    }

    private static PositionLedgerSnapshotV1 ToSnapshot(
        string portfolioCode,
        string environment,
        string instrumentKey,
        PositionAggregate position) =>
        new(
            position.PositionUid,
            portfolioCode,
            environment,
            instrumentKey,
            position.ProductType,
            position.State.Direction,
            position.State.Quantity,
            position.State.AverageOpenPrice,
            position.State.CostBasisAmount,
            position.MarketValueAmount,
            position.State.RealizedPnlAmount,
            position.UnrealizedPnlAmount,
            position.State.AccruedFeesAmount,
            position.State.AccruedTaxesAmount,
            position.State.Quantity == 0 ? "CLOSED" : "OPEN",
            position.State.Version,
            position.OpenedAtUtc,
            position.LastFillAtUtc,
            position.ClosedAtUtc,
            position.UpdatedAtUtc);

    private sealed class PortfolioAggregate
    {
        public PortfolioAggregate(Guid portfolioUid)
        {
            PortfolioUid = portfolioUid;
        }

        public object SyncRoot { get; } = new();
        public Guid PortfolioUid { get; }
        public Dictionary<string, PositionAggregate> Positions { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CashState> Cash { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public HashSet<Guid> ProcessedFills { get; } = [];
    }

    private sealed record PositionAggregate(
        Guid PositionUid,
        string ProductType,
        PositionAccountingState State,
        IReadOnlyCollection<PositionLotState> Lots,
        decimal MarketValueAmount,
        decimal UnrealizedPnlAmount,
        DateTimeOffset? OpenedAtUtc,
        DateTimeOffset? LastFillAtUtc,
        DateTimeOffset? ClosedAtUtc,
        DateTimeOffset UpdatedAtUtc)
    {
        public static PositionAggregate Create(string productType) =>
            new(
                Guid.NewGuid(),
                productType,
                new PositionAccountingState(
                    EvidenceDirectionV1.Neutral,
                    0m,
                    null,
                    0m,
                    0m,
                    0m,
                    0m,
                    0),
                Array.Empty<PositionLotState>(),
                0m,
                0m,
                null,
                null,
                null,
                DateTimeOffset.MinValue);
    }

    private sealed record CashState(
        string CurrencyCode,
        decimal SettledAmount,
        int Version,
        long Sequence,
        DateTimeOffset AsOfUtc);
}
