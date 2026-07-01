from decimal import Decimal

from app.contracts.v1.option_chain import OptionChainOiWallV1
from app.option_chain.common import TWO, ZERO, quantize
from app.option_chain.definitions import OptionChainIntelligenceOptions
from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)


def calculate_pcr(
    entries: list[OptionContractObservation],
    field_name: str,
) -> tuple[Decimal, Decimal, Decimal | None, str | None]:
    calls = sum(
        (
            getattr(entry, field_name) or ZERO
            for entry in entries
            if entry.option_type == "CALL"
        ),
        ZERO,
    )
    puts = sum(
        (
            getattr(entry, field_name) or ZERO
            for entry in entries
            if entry.option_type == "PUT"
        ),
        ZERO,
    )
    if calls == ZERO:
        warning = (
            "PCR_OI_DENOMINATOR_ZERO"
            if field_name == "open_interest"
            else "PCR_VOLUME_DENOMINATOR_ZERO"
        )
        return calls, puts, None, warning
    return calls, puts, quantize(puts / calls), None


def rank_oi_walls(
    snapshot: OptionChainSnapshotObservation,
    entries: list[OptionContractObservation],
    options: OptionChainIntelligenceOptions,
) -> tuple[list[OptionChainOiWallV1], list[OptionChainOiWallV1]]:
    result: dict[str, list[OptionChainOiWallV1]] = {"CALL": [], "PUT": []}
    for option_type in ("CALL", "PUT"):
        side = [
            entry
            for entry in entries
            if entry.option_type == option_type
            and entry.open_interest is not None
            and abs(entry.strike_price - snapshot.underlying_price)
            / snapshot.underlying_price
            <= options.oi_wall_moneyness_fraction
        ]
        total = sum((entry.open_interest or ZERO for entry in side), ZERO)
        maximum = max((entry.open_interest or ZERO for entry in side), default=ZERO)
        ranked = sorted(
            side,
            key=lambda entry: (
                -(entry.open_interest or ZERO),
                abs(entry.strike_price - snapshot.underlying_price),
                entry.strike_price,
            ),
        )[: options.oi_wall_count]
        for index, entry in enumerate(ranked, start=1):
            oi = entry.open_interest or ZERO
            share = oi / total if total > ZERO else ZERO
            relative = oi / maximum if maximum > ZERO else ZERO
            result[option_type].append(
                OptionChainOiWallV1(
                    expiry_date=snapshot.expiry_date,
                    option_type=option_type,
                    role="RESISTANCE" if option_type == "CALL" else "SUPPORT",
                    strike_price=entry.strike_price,
                    open_interest=oi,
                    same_side_oi_share=quantize(share),
                    wall_strength=quantize((share + relative) / TWO),
                    distance_fraction=quantize(
                        abs(entry.strike_price - snapshot.underlying_price)
                        / snapshot.underlying_price
                    ),
                    rank=index,
                )
            )
    return result["CALL"], result["PUT"]
