from decimal import Decimal

from app.contracts.v1.option_chain import OptionChainOiFlowV1
from app.option_chain.common import ONE, ZERO, eligible_entries, is_snapshot_eligible, quantize
from app.option_chain.definitions import OptionChainIntelligenceOptions
from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)


def classify_oi_flows(
    current: OptionChainSnapshotObservation,
    previous: OptionChainSnapshotObservation | None,
    current_entries: list[OptionContractObservation],
    options: OptionChainIntelligenceOptions,
) -> tuple[list[OptionChainOiFlowV1], list[str]]:
    if previous is None or not is_snapshot_eligible(previous):
        return [], ["PRIOR_SNAPSHOT_UNAVAILABLE"]
    if previous.expiry_date != current.expiry_date:
        return [], ["PRIOR_SNAPSHOT_EXPIRY_MISMATCH"]

    prior_by_uid = {
        entry.derivative_contract_uid: entry for entry in eligible_entries(previous)
    }
    result: list[OptionChainOiFlowV1] = []
    warnings: list[str] = []
    for entry in current_entries:
        prior = prior_by_uid.get(entry.derivative_contract_uid)
        premium_change = fractional_change(
            entry.last_price,
            None if prior is None else prior.last_price,
        )
        oi_change = fractional_change(
            entry.open_interest,
            None if prior is None else prior.open_interest,
            allow_zero_base=True,
        )
        state = flow_state(premium_change, oi_change, options)
        contribution = (
            ZERO
            if state == "FLAT_OR_UNKNOWN"
            else flow_contribution(entry.option_type, state)
        )
        result.append(
            OptionChainOiFlowV1(
                derivative_contract_uid=entry.derivative_contract_uid,
                instrument_key=entry.instrument_key,
                expiry_date=entry.expiry_date,
                option_type=entry.option_type,
                strike_price=entry.strike_price,
                previous_premium=None if prior is None else prior.last_price,
                current_premium=entry.last_price,
                previous_open_interest=None if prior is None else prior.open_interest,
                current_open_interest=entry.open_interest,
                premium_change_fraction=(
                    None if premium_change is None else quantize(premium_change)
                ),
                open_interest_change_fraction=(
                    None if oi_change is None else quantize(oi_change)
                ),
                state=state,
                normalized_contribution=contribution,
            )
        )
    if not any(item.state != "FLAT_OR_UNKNOWN" for item in result):
        warnings.append("INSUFFICIENT_OI_CHANGE")
    return result, warnings


def fractional_change(
    current: Decimal | None,
    previous: Decimal | None,
    *,
    allow_zero_base: bool = False,
) -> Decimal | None:
    if current is None or previous is None:
        return None
    if previous == ZERO:
        if allow_zero_base and current > ZERO:
            return ONE
        return None
    return (current - previous) / previous


def flow_state(
    premium_change: Decimal | None,
    oi_change: Decimal | None,
    options: OptionChainIntelligenceOptions,
) -> str:
    if premium_change is None or oi_change is None:
        return "FLAT_OR_UNKNOWN"
    if abs(premium_change) < options.minimum_premium_change_fraction:
        return "FLAT_OR_UNKNOWN"
    if abs(oi_change) < options.minimum_open_interest_change_fraction:
        return "FLAT_OR_UNKNOWN"
    if premium_change > ZERO and oi_change > ZERO:
        return "LONG_BUILDUP"
    if premium_change < ZERO and oi_change > ZERO:
        return "SHORT_BUILDUP"
    if premium_change > ZERO and oi_change < ZERO:
        return "SHORT_COVERING"
    if premium_change < ZERO and oi_change < ZERO:
        return "LONG_UNWINDING"
    return "FLAT_OR_UNKNOWN"


def flow_contribution(option_type: str, state: str) -> Decimal:
    call_map = {
        "LONG_BUILDUP": Decimal("1.0"),
        "SHORT_BUILDUP": Decimal("-1.0"),
        "SHORT_COVERING": Decimal("0.6"),
        "LONG_UNWINDING": Decimal("-0.6"),
    }
    value = call_map[state]
    return value if option_type == "CALL" else -value


def aggregate_flow(flows: list[OptionChainOiFlowV1], option_type: str) -> Decimal:
    side = [flow for flow in flows if flow.option_type == option_type]
    if not side:
        return ZERO
    total = sum((flow.current_open_interest or ZERO for flow in side), ZERO)
    if total == ZERO:
        return quantize(
            sum((flow.normalized_contribution for flow in side), ZERO)
            / Decimal(len(side))
        )
    return quantize(
        sum(
            (
                flow.normalized_contribution * (flow.current_open_interest or ZERO)
                for flow in side
            ),
            ZERO,
        )
        / total
    )


def flow_evidence_warnings(flows: list[OptionChainOiFlowV1]) -> list[str]:
    if not flows:
        return ["PRIOR_SNAPSHOT_UNAVAILABLE"]
    if not any(flow.state != "FLAT_OR_UNKNOWN" for flow in flows):
        return ["INSUFFICIENT_OI_CHANGE"]
    return []
