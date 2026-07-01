from decimal import Decimal
from statistics import median

from app.contracts.v1.option_chain import (
    OptionChainEvidenceV1,
    OptionChainIvTermPointV1,
    OptionChainOiFlowV1,
    OptionChainOiWallV1,
)
from app.option_chain.common import ONE, ZERO, clip, quantize
from app.option_chain.definitions import OptionChainIntelligenceOptions
from app.option_chain.flows import aggregate_flow, flow_evidence_warnings
from app.option_chain.models import OptionChainSnapshotObservation


def build_evidence(
    *,
    current: OptionChainSnapshotObservation,
    pcr_oi: Decimal | None,
    pcr_volume: Decimal | None,
    call_walls: list[OptionChainOiWallV1],
    put_walls: list[OptionChainOiWallV1],
    flows: list[OptionChainOiFlowV1],
    max_pain_distance: Decimal | None,
    max_pain_strength: Decimal | None,
    term_state: str,
    term_points: list[OptionChainIvTermPointV1],
    atm_skew: Decimal | None,
    rr25_skew: Decimal | None,
    options: OptionChainIntelligenceOptions,
) -> list[OptionChainEvidenceV1]:
    values: dict[str, tuple[Decimal | None, Decimal, str, list[str]]] = {}

    values["PCR_OI"] = (
        pcr_oi,
        pcr_signal(pcr_oi, options, invert=False),
        (
            "OI PCR uses the V1 positioning interpretation: higher put OI "
            "supports long and lower put OI supports short."
        ),
        [] if pcr_oi is not None else ["PCR_OI_DENOMINATOR_ZERO"],
    )
    values["PCR_VOLUME"] = (
        pcr_volume,
        pcr_signal(pcr_volume, options, invert=True),
        (
            "Volume PCR uses the V1 demand interpretation: higher put volume "
            "supports short and lower put volume supports long."
        ),
        [] if pcr_volume is not None else ["PCR_VOLUME_DENOMINATOR_ZERO"],
    )

    values["CALL_OI_WALL"] = (
        call_walls[0].strike_price if call_walls else None,
        wall_signal(call_walls, current, "CALL", options),
        "The strongest eligible call OI wall is potential overhead resistance.",
        [] if call_walls else ["CALL_OI_WALL_UNAVAILABLE"],
    )
    values["PUT_OI_WALL"] = (
        put_walls[0].strike_price if put_walls else None,
        wall_signal(put_walls, current, "PUT", options),
        "The strongest eligible put OI wall is potential downside support.",
        [] if put_walls else ["PUT_OI_WALL_UNAVAILABLE"],
    )

    call_side = [flow for flow in flows if flow.option_type == "CALL"]
    call_flow = aggregate_flow(flows, "CALL")
    values["CALL_OI_FLOW"] = (
        call_flow,
        call_flow,
        "Call premium and OI changes are aggregated from contract-level states.",
        flow_evidence_warnings(call_side),
    )
    put_side = [flow for flow in flows if flow.option_type == "PUT"]
    put_flow = aggregate_flow(flows, "PUT")
    values["PUT_OI_FLOW"] = (
        put_flow,
        put_flow,
        "Put premium and OI changes are aggregated from contract-level states.",
        flow_evidence_warnings(put_side),
    )

    max_pain_warnings: list[str] = []
    if max_pain_distance is None or max_pain_strength is None:
        max_pain_signal = ZERO
        max_pain_warnings.append("MAX_PAIN_NOT_CALCULABLE")
    else:
        max_pain_signal = (
            clip(max_pain_distance / options.oi_wall_moneyness_fraction)
            * max_pain_strength
        )
    values["MAX_PAIN_POSITION"] = (
        max_pain_distance,
        max_pain_signal,
        "Max pain contributes only as bounded positional context relative to spot.",
        max_pain_warnings,
    )

    term_confidence = (
        ONE
        if len(term_points) >= options.minimum_expiry_count_for_term_structure
        else ZERO
    )
    values["IV_TERM_STRUCTURE"] = (
        Decimal(len(term_points)),
        ZERO,
        (
            f"IV term structure is {term_state}; V1 preserves it as "
            "non-directional volatility context."
        ),
        [] if term_confidence > ZERO else ["INSUFFICIENT_EXPIRIES_FOR_TERM_STRUCTURE"],
    )

    values["IV_ATM_SKEW"] = (
        atm_skew,
        skew_signal(atm_skew, term_points, options),
        "Positive ATM put-minus-call skew supports short; negative supports long.",
        [] if atm_skew is not None else ["ATM_IV_PAIR_UNAVAILABLE"],
    )
    values["IV_RR25_SKEW"] = (
        rr25_skew,
        skew_signal(rr25_skew, term_points, options),
        (
            "Positive 25-delta put-minus-call risk reversal supports short; "
            "negative supports long."
        ),
        [] if rr25_skew is not None else ["DELTA_UNAVAILABLE_FOR_RR25"],
    )

    result: list[OptionChainEvidenceV1] = []
    for code, weight in options.weights.items():
        raw, normalized, message, component_warnings = values[code]
        confidence = ZERO if component_warnings else ONE
        if code == "IV_TERM_STRUCTURE":
            confidence = term_confidence
        contribution = quantize(clip(normalized) * weight)
        result.append(
            OptionChainEvidenceV1(
                code=code,
                message=message,
                impact=impact(contribution),
                raw_value=raw,
                normalized_value=quantize(clip(normalized)),
                weight=weight,
                contribution=contribution,
                confidence=confidence,
                warnings=component_warnings,
            )
        )
    return result


def pcr_signal(
    ratio: Decimal | None,
    options: OptionChainIntelligenceOptions,
    *,
    invert: bool,
) -> Decimal:
    if ratio is None:
        return ZERO
    if options.pcr_neutral_lower <= ratio <= options.pcr_neutral_upper:
        return ZERO
    if ratio > options.pcr_neutral_upper:
        value = (ratio - options.pcr_neutral_upper) / options.pcr_normalization_scale
    else:
        value = (ratio - options.pcr_neutral_lower) / options.pcr_normalization_scale
    value = clip(value)
    return -value if invert else value


def wall_signal(
    walls: list[OptionChainOiWallV1],
    snapshot: OptionChainSnapshotObservation,
    option_type: str,
    options: OptionChainIntelligenceOptions,
) -> Decimal:
    if not walls:
        return ZERO
    wall = walls[0]
    proximity = ONE - min(
        ONE,
        wall.distance_fraction / options.oi_wall_moneyness_fraction,
    )
    value = wall.wall_strength * proximity
    if option_type == "CALL":
        return -value if wall.strike_price >= snapshot.underlying_price else ZERO
    return value if wall.strike_price <= snapshot.underlying_price else ZERO


def skew_signal(
    skew: Decimal | None,
    term_points: list[OptionChainIvTermPointV1],
    options: OptionChainIntelligenceOptions,
) -> Decimal:
    if skew is None:
        return ZERO
    bases = [
        point.atm_implied_volatility
        for point in term_points
        if point.atm_implied_volatility > ZERO
    ]
    base = Decimal(str(median(bases))) if bases else ONE
    relative = skew / base
    return clip(-relative / options.iv_skew_normalization_scale)


def agreement(evidence: list[OptionChainEvidenceV1], score: Decimal) -> Decimal:
    active = [item for item in evidence if item.contribution != ZERO]
    if not active or score == ZERO:
        return ZERO
    same = sum(
        (item.weight for item in active if item.contribution * score > ZERO),
        ZERO,
    )
    total = sum((item.weight for item in active), ZERO)
    return same / total if total > ZERO else ZERO


def direction(score: Decimal, threshold: Decimal) -> str:
    if score >= threshold:
        return "LONG"
    if score <= -threshold:
        return "SHORT"
    return "NEUTRAL"


def impact(contribution: Decimal) -> str:
    if contribution > ZERO:
        return "SUPPORTS_LONG"
    if contribution < ZERO:
        return "SUPPORTS_SHORT"
    return "NEUTRAL"
