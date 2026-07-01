from decimal import Decimal

from app.contracts.v1.option_chain import OptionChainMaxPainPointV1
from app.option_chain.common import ZERO, clip, quantize
from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)


def calculate_max_pain(
    snapshot: OptionChainSnapshotObservation,
    entries: list[OptionContractObservation],
) -> tuple[
    Decimal | None,
    Decimal | None,
    Decimal | None,
    list[OptionChainMaxPainPointV1],
    list[str],
]:
    usable = [entry for entry in entries if (entry.open_interest or ZERO) > ZERO]
    if not usable:
        return None, None, None, [], ["MAX_PAIN_NOT_CALCULABLE"]
    if any(
        entry.contract_multiplier is None or entry.contract_multiplier <= ZERO
        for entry in usable
    ):
        return None, None, None, [], ["CONTRACT_MULTIPLIER_UNAVAILABLE"]

    strikes = sorted({entry.strike_price for entry in usable})
    curve: list[OptionChainMaxPainPointV1] = []
    for settlement in strikes:
        call_payout = sum(
            (
                max(ZERO, settlement - entry.strike_price)
                * (entry.open_interest or ZERO)
                * (entry.contract_multiplier or ZERO)
                for entry in usable
                if entry.option_type == "CALL"
            ),
            ZERO,
        )
        put_payout = sum(
            (
                max(ZERO, entry.strike_price - settlement)
                * (entry.open_interest or ZERO)
                * (entry.contract_multiplier or ZERO)
                for entry in usable
                if entry.option_type == "PUT"
            ),
            ZERO,
        )
        curve.append(
            OptionChainMaxPainPointV1(
                settlement_strike=settlement,
                call_payout=quantize(call_payout),
                put_payout=quantize(put_payout),
                total_payout=quantize(call_payout + put_payout),
            )
        )

    winner = min(
        curve,
        key=lambda point: (
            point.total_payout,
            abs(point.settlement_strike - snapshot.underlying_price),
            point.settlement_strike,
        ),
    )
    maximum_payout = max((point.total_payout for point in curve), default=ZERO)
    strength = (
        ZERO
        if maximum_payout == ZERO
        else (maximum_payout - winner.total_payout) / maximum_payout
    )
    distance = (winner.settlement_strike - snapshot.underlying_price) / (
        snapshot.underlying_price
    )
    return (
        winner.settlement_strike,
        quantize(distance),
        quantize(clip(strength)),
        curve,
        [],
    )
