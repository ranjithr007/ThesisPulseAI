from collections import defaultdict
from decimal import Decimal

from app.contracts.v1.option_chain import OptionChainIvTermPointV1
from app.option_chain.common import (
    TWO,
    ZERO,
    eligible_entries,
    is_snapshot_eligible,
    quantize,
)
from app.option_chain.definitions import OptionChainIntelligenceOptions
from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)


def calculate_skew(
    snapshot: OptionChainSnapshotObservation,
    entries: list[OptionContractObservation],
    options: OptionChainIntelligenceOptions,
) -> tuple[
    Decimal | None,
    Decimal | None,
    Decimal | None,
    Decimal | None,
    list[str],
]:
    warnings: list[str] = []
    atm_pair = atm_pair_for_snapshot(snapshot, entries, options)
    if atm_pair is None:
        warnings.append("ATM_IV_PAIR_UNAVAILABLE")
        atm_call_iv = atm_put_iv = atm_skew = None
    else:
        call, put = atm_pair
        atm_call_iv = call.implied_volatility
        atm_put_iv = put.implied_volatility
        atm_skew = (atm_put_iv or ZERO) - (atm_call_iv or ZERO)

    rr25_skew: Decimal | None = None
    delta_entries = [
        entry
        for entry in entries
        if entry.delta is not None
        and entry.implied_volatility is not None
        and entry.implied_volatility >= ZERO
        and entry.greeks_source_version
    ]
    call25 = nearest_delta(delta_entries, "CALL", Decimal("0.25"), options)
    put25 = nearest_delta(delta_entries, "PUT", Decimal("-0.25"), options)
    if call25 is None or put25 is None:
        warnings.append("DELTA_UNAVAILABLE_FOR_RR25")
    else:
        rr25_skew = (put25.implied_volatility or ZERO) - (
            call25.implied_volatility or ZERO
        )
    if snapshot.calculation_source_version is None:
        warnings.append("IV_SOURCE_VERSION_UNAVAILABLE")
    return atm_call_iv, atm_put_iv, atm_skew, rr25_skew, warnings


def calculate_term_structure(
    snapshots: list[OptionChainSnapshotObservation],
    options: OptionChainIntelligenceOptions,
) -> tuple[
    list[OptionChainIvTermPointV1],
    Decimal | None,
    Decimal | None,
    str,
    list[str],
]:
    points: list[OptionChainIvTermPointV1] = []
    warnings: list[str] = []
    for snapshot in snapshots:
        if not is_snapshot_eligible(snapshot):
            continue
        pair = atm_pair_for_snapshot(snapshot, eligible_entries(snapshot), options)
        if pair is None:
            continue
        call, put = pair
        call_iv = call.implied_volatility or ZERO
        put_iv = put.implied_volatility or ZERO
        points.append(
            OptionChainIvTermPointV1(
                snapshot_uid=snapshot.snapshot_uid,
                expiry_date=snapshot.expiry_date,
                days_to_expiry=max(
                    0,
                    (snapshot.expiry_date - snapshot.event_at_utc.date()).days,
                ),
                atm_strike_price=call.strike_price,
                call_implied_volatility=call_iv,
                put_implied_volatility=put_iv,
                atm_implied_volatility=quantize((call_iv + put_iv) / TWO),
                pair_method="EXACT_ATM",
            )
        )

    points.sort(key=lambda item: (item.days_to_expiry, item.expiry_date))
    if len(points) < options.minimum_expiry_count_for_term_structure:
        warnings.append("INSUFFICIENT_EXPIRIES_FOR_TERM_STRUCTURE")
        return points, None, None, "INSUFFICIENT", warnings

    near = points[0]
    next_point = points[1]
    far = points[-1]
    near_to_next = relative_difference(
        next_point.atm_implied_volatility,
        near.atm_implied_volatility,
    )
    near_to_far = relative_difference(
        far.atm_implied_volatility,
        near.atm_implied_volatility,
    )
    reference = near_to_far if near_to_far is not None else near_to_next
    if reference is None:
        state = "INSUFFICIENT"
        warnings.append("INSUFFICIENT_EXPIRIES_FOR_TERM_STRUCTURE")
    elif reference > options.iv_flat_relative_slope_threshold:
        state = "CONTANGO"
    elif reference < -options.iv_flat_relative_slope_threshold:
        state = "BACKWARDATION"
    else:
        state = "FLAT"
    return (
        points,
        None if near_to_next is None else quantize(near_to_next),
        None if near_to_far is None else quantize(near_to_far),
        state,
        warnings,
    )


def atm_pair_for_snapshot(
    snapshot: OptionChainSnapshotObservation,
    entries: list[OptionContractObservation],
    options: OptionChainIntelligenceOptions,
) -> tuple[OptionContractObservation, OptionContractObservation] | None:
    by_strike: dict[Decimal, dict[str, OptionContractObservation]] = defaultdict(dict)
    for entry in entries:
        if entry.implied_volatility is not None and entry.implied_volatility >= ZERO:
            by_strike[entry.strike_price][entry.option_type] = entry
    candidates = [
        (strike, pair["CALL"], pair["PUT"])
        for strike, pair in by_strike.items()
        if "CALL" in pair and "PUT" in pair
    ]
    if not candidates:
        return None
    strike, call, put = min(
        candidates,
        key=lambda item: (abs(item[0] - snapshot.underlying_price), item[0]),
    )
    distance = abs(strike - snapshot.underlying_price) / snapshot.underlying_price
    if distance > options.atm_pair_strike_tolerance_fraction:
        return None
    return call, put


def nearest_delta(
    entries: list[OptionContractObservation],
    option_type: str,
    target: Decimal,
    options: OptionChainIntelligenceOptions,
) -> OptionContractObservation | None:
    candidates = [entry for entry in entries if entry.option_type == option_type]
    if not candidates:
        return None
    winner = min(
        candidates,
        key=lambda entry: (
            abs((entry.delta or ZERO) - target),
            entry.strike_price,
        ),
    )
    if abs((winner.delta or ZERO) - target) > options.delta_match_tolerance:
        return None
    return winner


def relative_difference(current: Decimal, base: Decimal) -> Decimal | None:
    if base == ZERO:
        return None
    return (current - base) / base
