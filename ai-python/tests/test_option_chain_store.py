from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from uuid import UUID

from app.option_chain.calculator import DeterministicOptionChainCalculator
from app.option_chain.definitions import OptionChainIntelligenceOptions
from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)
from app.option_chain.store import InMemoryOptionChainIntelligenceStore

BASE_TIME = datetime(2026, 7, 1, 9, 0, tzinfo=UTC)
EXPIRY = date(2026, 7, 30)
UNDERLYING = "NSE_INDEX|Nifty 50"


def test_store_is_idempotent_and_revisions_corrected_cutoff() -> None:
    store = InMemoryOptionChainIntelligenceStore()
    calculator = _calculator()
    first = _snapshot(10, 100, BASE_TIME, BASE_TIME + timedelta(seconds=1), revision=0)

    created = store.process_snapshot(UUID(int=100), first, calculator, BASE_TIME)
    duplicate = store.process_snapshot(UUID(int=100), first, calculator, BASE_TIME)
    corrected = _snapshot(
        11,
        105,
        BASE_TIME,
        BASE_TIME + timedelta(seconds=2),
        revision=1,
    )
    revised = store.process_snapshot(
        UUID(int=101),
        corrected,
        calculator,
        BASE_TIME + timedelta(seconds=2),
    )

    assert created.outcome == "CREATED"
    assert created.output is not None and created.output.revision == 0
    assert duplicate.outcome == "DUPLICATE"
    assert revised.outcome == "REVISED"
    assert revised.output is not None and revised.output.revision == 1
    assert store.get_status().snapshot_count == 2
    assert store.get_status().output_count == 2


def test_prior_selection_rejects_information_received_after_current_cutoff() -> None:
    store = InMemoryOptionChainIntelligenceStore()
    calculator = _calculator()
    base = _snapshot(
        20,
        100,
        BASE_TIME,
        BASE_TIME + timedelta(minutes=1),
    )
    late = _snapshot(
        21,
        200,
        BASE_TIME + timedelta(minutes=10),
        BASE_TIME + timedelta(minutes=30),
    )
    current = _snapshot(
        22,
        120,
        BASE_TIME + timedelta(minutes=20),
        BASE_TIME + timedelta(minutes=21),
    )

    store.process_snapshot(UUID(int=200), base, calculator, BASE_TIME + timedelta(minutes=1))
    store.process_snapshot(UUID(int=201), late, calculator, BASE_TIME + timedelta(minutes=30))
    result = store.process_snapshot(
        UUID(int=202),
        current,
        calculator,
        BASE_TIME + timedelta(minutes=31),
    )

    assert result.outcome == "CREATED"
    assert result.output is not None
    call_flow = next(
        flow
        for flow in result.output.expiry_metrics[0].oi_flows
        if flow.option_type == "CALL"
    )
    assert call_flow.previous_open_interest == Decimal("100")
    assert call_flow.current_open_interest == Decimal("120")
    assert call_flow.state == "LONG_BUILDUP"


def test_historical_latest_read_honors_event_and_receipt_cutoffs() -> None:
    store = InMemoryOptionChainIntelligenceStore()
    calculator = _calculator()
    first = _snapshot(
        30,
        100,
        BASE_TIME,
        BASE_TIME + timedelta(minutes=1),
    )
    second = _snapshot(
        31,
        120,
        BASE_TIME + timedelta(minutes=5),
        BASE_TIME + timedelta(minutes=6),
    )

    store.process_snapshot(UUID(int=300), first, calculator, BASE_TIME + timedelta(minutes=1))
    store.process_snapshot(UUID(int=301), second, calculator, BASE_TIME + timedelta(minutes=6))

    historical = store.get_latest(
        UNDERLYING,
        EXPIRY,
        BASE_TIME + timedelta(minutes=4),
    )
    current = store.get_latest(UNDERLYING, EXPIRY)

    assert historical is not None
    assert historical.source_snapshot_uid == UUID(int=30)
    assert current is not None
    assert current.source_snapshot_uid == UUID(int=31)


def _calculator() -> DeterministicOptionChainCalculator:
    return DeterministicOptionChainCalculator(
        OptionChainIntelligenceOptions(
            maximum_output_age_seconds=3600,
            minimum_contract_count=2,
            minimum_strike_count=1,
            oi_wall_count=1,
        )
    )


def _snapshot(
    uid: int,
    call_oi: int,
    event_at: datetime,
    received_at: datetime,
    *,
    revision: int = 0,
) -> OptionChainSnapshotObservation:
    return OptionChainSnapshotObservation(
        snapshot_uid=UUID(int=uid),
        underlying_instrument_key=UNDERLYING,
        expiry_date=EXPIRY,
        event_at_utc=event_at,
        received_at_utc=received_at,
        underlying_price=Decimal("100"),
        snapshot_status="COMPLETE",
        quality_status="VALID",
        is_point_in_time_eligible=True,
        revision=revision,
        entries=(
            _entry(1, "CALL", call_oi, Decimal(call_oi) / Decimal("10")),
            _entry(2, "PUT", 100, Decimal("10")),
        ),
        calculation_source_version="provider-option-chain-v1",
    )


def _entry(
    uid: int,
    option_type: str,
    oi: int,
    premium: Decimal,
) -> OptionContractObservation:
    return OptionContractObservation(
        derivative_contract_uid=UUID(int=uid),
        instrument_key=f"NSE_FO|OPT-{uid}",
        expiry_date=EXPIRY,
        strike_price=Decimal("100"),
        option_type=option_type,  # type: ignore[arg-type]
        last_price=premium,
        volume_quantity=Decimal("50"),
        open_interest=Decimal(oi),
        implied_volatility=Decimal("0.20") if option_type == "CALL" else Decimal("0.22"),
        delta=Decimal("0.50") if option_type == "CALL" else Decimal("-0.50"),
        contract_multiplier=Decimal("50"),
        quality_status="VALID",
        greeks_source_version="provider-greeks-v1",
    )
