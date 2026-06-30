using ThesisPulse.Shared.Contracts.Thesis.V1;

namespace ThesisPulse.Shared.Infrastructure.Portfolio;

public sealed record PositionAccountingState(
    EvidenceDirectionV1 Direction,
    decimal Quantity,
    decimal? AverageOpenPrice,
    decimal CostBasisAmount,
    decimal RealizedPnlAmount,
    decimal AccruedFeesAmount,
    decimal AccruedTaxesAmount,
    int Version);

public sealed record PositionLotState(
    long? DatabaseId,
    Guid LotUid,
    int Sequence,
    EvidenceDirectionV1 Direction,
    decimal OpenedQuantity,
    decimal RemainingQuantity,
    decimal OpenPrice,
    decimal AllocatedOpenFeesAmount,
    decimal AllocatedOpenTaxesAmount,
    DateTimeOffset OpenedAtUtc);

public sealed record PositionFillInput(
    Guid FillUid,
    string Side,
    decimal Quantity,
    decimal Price,
    decimal FeesAmount,
    decimal TaxesAmount,
    DateTimeOffset FilledAtUtc);

public sealed record PositionLotClosure(
    PositionLotState Lot,
    int Sequence,
    decimal MatchedQuantity,
    decimal OpenPrice,
    decimal ClosePrice,
    decimal GrossRealizedPnlAmount,
    decimal AllocatedOpenFeesAmount,
    decimal AllocatedCloseFeesAmount,
    decimal AllocatedOpenTaxesAmount,
    decimal AllocatedCloseTaxesAmount,
    decimal NetRealizedPnlAmount);

public sealed record PositionAccountingResult(
    string EventType,
    PositionAccountingState Before,
    PositionAccountingState After,
    decimal QuantityDelta,
    decimal GrossRealizedPnlDelta,
    decimal NetRealizedPnlDelta,
    decimal FillFeesAmount,
    decimal FillTaxesAmount,
    IReadOnlyCollection<PositionLotState> UpdatedExistingLots,
    IReadOnlyCollection<PositionLotState> NewLots,
    IReadOnlyCollection<PositionLotClosure> Closures,
    decimal MarketValueAmount,
    decimal UnrealizedPnlAmount);

public static class DeterministicPositionAccounting
{
    public static PositionAccountingResult ApplyFill(
        PositionAccountingState before,
        IReadOnlyCollection<PositionLotState> currentLots,
        PositionFillInput fill)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(currentLots);
        ArgumentNullException.ThrowIfNull(fill);

        if (fill.FillUid == Guid.Empty)
        {
            throw new ArgumentException("Fill UID is required.", nameof(fill));
        }

        if (fill.Quantity <= 0 || fill.Price <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fill),
                "Fill quantity and price must be positive.");
        }

        if (fill.FeesAmount < 0 || fill.TaxesAmount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fill),
                "Fill fees and taxes cannot be negative.");
        }

        var fillDirection = fill.Side.ToUpperInvariant() switch
        {
            "BUY" => EvidenceDirectionV1.Long,
            "SELL" => EvidenceDirectionV1.Short,
            _ => throw new ArgumentException("Fill side must be BUY or SELL.", nameof(fill)),
        };

        ValidateState(before, currentLots);

        var lots = currentLots
            .OrderBy(lot => lot.Sequence)
            .Select(lot => lot with { })
            .ToList();
        var updatedExistingLots = new List<PositionLotState>();
        var newLots = new List<PositionLotState>();
        var closures = new List<PositionLotClosure>();
        decimal grossRealized = 0m;
        decimal netRealized = 0m;
        string eventType;

        if (before.Direction == EvidenceDirectionV1.Neutral || before.Quantity == 0)
        {
            eventType = "OPENED";
            newLots.Add(CreateLot(
                lots,
                fillDirection,
                fill.Quantity,
                fill.Price,
                fill.FeesAmount,
                fill.TaxesAmount,
                fill.FilledAtUtc));
        }
        else if (before.Direction == fillDirection)
        {
            eventType = "INCREASED";
            newLots.Add(CreateLot(
                lots,
                fillDirection,
                fill.Quantity,
                fill.Price,
                fill.FeesAmount,
                fill.TaxesAmount,
                fill.FilledAtUtc));
        }
        else
        {
            var closingQuantity = Math.Min(before.Quantity, fill.Quantity);
            var openingQuantity = fill.Quantity - closingQuantity;
            var closingFraction = closingQuantity / fill.Quantity;
            var closeFees = fill.FeesAmount * closingFraction;
            var closeTaxes = fill.TaxesAmount * closingFraction;
            var openFees = fill.FeesAmount - closeFees;
            var openTaxes = fill.TaxesAmount - closeTaxes;
            var quantityToMatch = closingQuantity;
            var closureSequence = 1;

            foreach (var lot in lots.Where(lot => lot.RemainingQuantity > 0))
            {
                if (quantityToMatch == 0)
                {
                    break;
                }

                if (lot.Direction != before.Direction)
                {
                    throw new InvalidOperationException(
                        "Open lot direction does not match current position direction.");
                }

                var matched = Math.Min(lot.RemainingQuantity, quantityToMatch);
                var matchedFractionOfClose = matched / closingQuantity;
                var matchedFractionOfLot = matched / lot.OpenedQuantity;
                var allocatedCloseFees = closeFees * matchedFractionOfClose;
                var allocatedCloseTaxes = closeTaxes * matchedFractionOfClose;
                var allocatedOpenFees = lot.AllocatedOpenFeesAmount * matchedFractionOfLot;
                var allocatedOpenTaxes = lot.AllocatedOpenTaxesAmount * matchedFractionOfLot;
                var gross = before.Direction == EvidenceDirectionV1.Long
                    ? (fill.Price - lot.OpenPrice) * matched
                    : (lot.OpenPrice - fill.Price) * matched;
                var net = gross
                    - allocatedOpenFees
                    - allocatedCloseFees
                    - allocatedOpenTaxes
                    - allocatedCloseTaxes;

                closures.Add(new PositionLotClosure(
                    lot,
                    closureSequence++,
                    matched,
                    lot.OpenPrice,
                    fill.Price,
                    gross,
                    allocatedOpenFees,
                    allocatedCloseFees,
                    allocatedOpenTaxes,
                    allocatedCloseTaxes,
                    net));
                grossRealized += gross;
                netRealized += net;
                quantityToMatch -= matched;

                updatedExistingLots.Add(lot with
                {
                    RemainingQuantity = lot.RemainingQuantity - matched,
                });
            }

            if (quantityToMatch != 0)
            {
                throw new InvalidOperationException(
                    "Open lots do not cover the current position quantity.");
            }

            if (openingQuantity > 0)
            {
                eventType = "REVERSED";
                newLots.Add(CreateLot(
                    lots.Concat(newLots),
                    fillDirection,
                    openingQuantity,
                    fill.Price,
                    openFees,
                    openTaxes,
                    fill.FilledAtUtc));
            }
            else if (closingQuantity == before.Quantity)
            {
                eventType = "CLOSED";
            }
            else
            {
                eventType = "REDUCED";
            }
        }

        var allLots = lots
            .Select(lot => updatedExistingLots.FirstOrDefault(updated => updated.LotUid == lot.LotUid) ?? lot)
            .Concat(newLots)
            .Where(lot => lot.RemainingQuantity > 0)
            .OrderBy(lot => lot.Sequence)
            .ToArray();
        var quantityAfter = allLots.Sum(lot => lot.RemainingQuantity);
        var directionAfter = quantityAfter == 0
            ? EvidenceDirectionV1.Neutral
            : allLots[0].Direction;

        if (allLots.Any(lot => lot.Direction != directionAfter))
        {
            throw new InvalidOperationException(
                "A projected position cannot contain open lots in opposing directions.");
        }

        var costBasisAfter = allLots.Sum(lot => lot.RemainingQuantity * lot.OpenPrice);
        var averageAfter = quantityAfter == 0
            ? null
            : costBasisAfter / quantityAfter;
        var marketValue = quantityAfter * fill.Price;
        var unrealized = directionAfter switch
        {
            EvidenceDirectionV1.Long => quantityAfter * (fill.Price - averageAfter!.Value),
            EvidenceDirectionV1.Short => quantityAfter * (averageAfter!.Value - fill.Price),
            _ => 0m,
        };
        var after = new PositionAccountingState(
            directionAfter,
            quantityAfter,
            averageAfter,
            costBasisAfter,
            before.RealizedPnlAmount + netRealized,
            before.AccruedFeesAmount + fill.FeesAmount,
            before.AccruedTaxesAmount + fill.TaxesAmount,
            before.Version + 1);

        return new PositionAccountingResult(
            eventType,
            before,
            after,
            quantityAfter - before.Quantity,
            grossRealized,
            netRealized,
            fill.FeesAmount,
            fill.TaxesAmount,
            updatedExistingLots,
            newLots,
            closures,
            marketValue,
            unrealized);
    }

    private static PositionLotState CreateLot(
        IEnumerable<PositionLotState> existingLots,
        EvidenceDirectionV1 direction,
        decimal quantity,
        decimal price,
        decimal fees,
        decimal taxes,
        DateTimeOffset openedAtUtc)
    {
        var sequence = existingLots.Select(lot => lot.Sequence).DefaultIfEmpty(0).Max() + 1;
        return new PositionLotState(
            null,
            Guid.NewGuid(),
            sequence,
            direction,
            quantity,
            quantity,
            price,
            fees,
            taxes,
            openedAtUtc);
    }

    private static void ValidateState(
        PositionAccountingState state,
        IReadOnlyCollection<PositionLotState> lots)
    {
        if (state.Quantity < 0 || state.CostBasisAmount < 0 ||
            state.AccruedFeesAmount < 0 || state.AccruedTaxesAmount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Position quantities, cost basis, fees and taxes cannot be negative.");
        }

        if (state.Quantity == 0)
        {
            if (state.Direction != EvidenceDirectionV1.Neutral || state.AverageOpenPrice is not null)
            {
                throw new InvalidOperationException(
                    "A flat position must be neutral and have no average open price.");
            }

            if (lots.Any(lot => lot.RemainingQuantity > 0))
            {
                throw new InvalidOperationException(
                    "A flat position cannot have open lots.");
            }

            return;
        }

        if (state.Direction is not EvidenceDirectionV1.Long and not EvidenceDirectionV1.Short ||
            state.AverageOpenPrice is null || state.AverageOpenPrice <= 0)
        {
            throw new InvalidOperationException(
                "An open position requires a direction and positive average open price.");
        }

        var openLots = lots.Where(lot => lot.RemainingQuantity > 0).ToArray();
        if (openLots.Length == 0 || openLots.Any(lot => lot.Direction != state.Direction))
        {
            throw new InvalidOperationException(
                "Open lots must exist and match the current position direction.");
        }

        var lotQuantity = openLots.Sum(lot => lot.RemainingQuantity);
        if (lotQuantity != state.Quantity)
        {
            throw new InvalidOperationException(
                "Open lot quantity must equal current position quantity.");
        }
    }
}
