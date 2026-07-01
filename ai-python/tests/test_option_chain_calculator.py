from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from uuid import UUID

from app.option_chain.calculator import DeterministicOptionChainCalculator
from app.option_chain.common import eligible_entries
from app.option_chain.definitions import OptionChainIntelligenceOptions
from app.option_chain.flows import flow_state
from app.option_chain.max_pain import calculate_max_pain
from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)
from app.option_chain.pcr_walls import calculate_pcr, rank_oi_walls
from app.option_chain.volatility import calculate_term_structure

BASE_TIME = datetime(2026, 7, 1, 9, 15, tzinfo=UTC)
EXPIRY = date(2026, 7, 30)
UNDERLYING = "NSE_INDEX|Nifty 50"


def test_pcr_calculates_oi_and_fails_closed_on_zero_denominator() -> None:
    entries = [
        _entry(1, "CALL", 100, oi=100, volume=50),
        _entry(2, "PUT", 100, oi=150, volume=75),
    ]

    calls, puts, ratio, warning = calculate_pcr(entries, "open_interest")

    assert calls == Decimal("100")
    assert puts == Decimal("150")
    assert ratio == Decimal("1.500000")
    assert warning is None

    calls, puts, ratio, warning = calculate_pcr(
        [_entry(3, "PUT", 100, oi=10, volume=10)],
        "open_interest",
    )

    assert calls == 0
    assert puts == 10
    assert ratio is None
    assert warning == "PCR_OI_DENOMINATOR_ZERO"


def test_oi_walls_use_stable_ranking_and_roles() -> None:
    snapshot = _snapshot(
        10,
        EXPIRY,
        Decimal("100"),
        (
            _entry(1, "CALL", 105, oi=500),
            _entry(2, "CALL", 110, oi=500),
            _entry(3, "CALL", 95, oi=200),
            _entry(4, "PUT", 95, oi=600),
            _entry(5, "PUT", 90, oi=600),
            _entry(6, "PUT", 105, oi=100),
        ),
    )

    calls, puts = rank_oi_walls(
        snapshot,
        eligible_entries(snapshot),
        OptionChainIntelligenceOptions(),
    )

    assert calls[0].strike_price == Decimal("105")
    assert calls[0].role == "RESISTANCE"
    assert calls[0].rank == 1
    assert puts[0].strike_price == Decimal("95")
    assert puts[0].role == "SUPPORT"
    assert puts[0].rank == 1


def test_all_four_oi_flow_states_are_deterministic() -> None:
    options = OptionChainIntelligenceOptions()

    assert flow_state(Decimal("0.05"), Decimal("0.10"), options) == "LONG_BUILDUP"
    assert flow_state(Decimal("-0.05"), Decimal("0.10"), options) == "SHORT_BUILDUP"
    assert flow_state(Decimal("0.05"), Decimal("-0.10"), options) == "SHORT_COVERING"
    assert flow_state(Decimal("-0.05"), Decimal("-0.10"), options) == "LONG_UNWINDING"
    assert flow_state(Decimal("0.001"), Decimal("0.10"), options) == "FLAT_OR_UNKNOWN"


def test_max_pain_uses_multiplier_and_stable_tie_breaking() -> None:
    snapshot = _snapshot(
        20,
        EXPIRY,
        Decimal("100"),
        tuple(
            _entry(index, option_type, strike, oi=10, multiplier=50)
            for index, (option_type, strike) in enumerate(
                [
                    ("CALL", 90),
                    ("CALL", 100),
                    ("CALL", 110),
                    ("PUT", 90),
                    ("PUT", 100),
                    ("PUT", 110),
                ],
                start=1,
            )
        ),
    )

    strike, distance, strength, curve, warnings = calculate_max_pain(
        snapshot,
        eligible_entries(snapshot),
    )

    assert strike == Decimal("100")
    assert distance == Decimal("0.000000")
    assert strength is not None and Decimal("0") <= strength <= Decimal("1")
    assert len(curve) == 3
    assert warnings == []


def test_iv_term_structure_classifies_contango() -> None:
    options = OptionChainIntelligenceOptions()
    near = _snapshot_with_atm_iv(30, date(2026, 7, 10), Decimal("0.20"))
    far = _snapshot_with_atm_iv(31, date(2026, 8, 27), Decimal("0.25"))

    points, near_next, near_far, state, warnings = calculate_term_structure(
        [near, far],
        options,
    )

    assert len(points) == 2
    assert near_next == Decimal("0.250000")
    assert near_far == Decimal("0.250000")
    assert state == "CONTANGO"
    assert warnings == []


def test_full_output_has_no_selection_or_execution_authority() -> None:
    options = OptionChainIntelligenceOptions()
    calculator = DeterministicOptionChainCalculator(options)
    previous = _full_snapshot(
        40,
        BASE_TIME - timedelta(minutes=5),
        price_shift=Decimal("-1"),
    )
    current = _full_snapshot(41, BASE_TIME)
    far = _snapshot_with_atm_iv(42, date(2026, 8, 27), Decimal("0.24"))

    output = calculator.calculate(
        current=current,
        previous=previous,
        term_snapshots=[far],
        generated_at_utc=BASE_TIME + timedelta(seconds=10),
        revision=0,
    )

    assert output.engine_code == "THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE"
    assert output.selection_authority is False
    assert output.execution_authority is False
    assert output.data_quality_status == "VALID"
    assert output.input_snapshot_count == len(output.source_snapshot_uids)
    assert len(output.evidence) == 10
    assert output.expiry_metrics[0].pcr_open_interest is not None
    assert output.iv_term_structure_state in {"CONTANGO", "BACKWARDATION", "FLAT"}


def _full_snapshot(
    uid: int,
    event_at: datetime,
    *,
    price_shift: Decimal = Decimal("0"),
) -> OptionChainSnapshotObservation:
    entries = []
    strikes = [90, 95, 100, 105, 110]
    for index, strike in enumerate(strikes, start=1):
        distance = Decimal(abs(strike - 100))
        entries.append(
            _entry(
                index,
                "CALL",
                strike,
                oi=Decimal("100") + Decimal(strike),
                volume=Decimal("50") + Decimal(strike),
                last=Decimal("12") - distance / Decimal("2") + price_shift,
                iv=Decimal("0.20") + distance / Decimal("1000"),
                delta=Decimal("0.25") if strike == 105 else Decimal("0.50"),
            )
        )
        entries.append(
            _entry(
                index + 100,
                "PUT",
                strike,
                oi=Decimal("140") + Decimal(110 - strike),
                volume=Decimal("70") + Decimal(110 - strike),
                last=Decimal("11") - distance / Decimal("2") - price_shift,
                iv=Decimal("0.22") + distance / Decimal("1000"),
                delta=Decimal("-0.25") if strike == 95 else Decimal("-0.50"),
            )
        )
    return _snapshot(uid, EXPIRY, Decimal("100"), tuple(entries), event_at=event_at)


def _snapshot_with_atm_iv(
    uid: int,
    expiry: date,
    atm_iv: Decimal,
) -> OptionChainSnapshotObservation:
    return _snapshot(
        uid,
        expiry,
        Decimal("100"),
        (
            _entry(
                1,
                "CALL",
                100,
                oi=100,
                iv=atm_iv,
                delta=Decimal("0.50"),
                expiry=expiry,
            ),
            _entry(
                2,
                "PUT",
                100,
                oi=100,
                iv=atm_iv,
                delta=Decimal("-0.50"),
                expiry=expiry,
            ),
        ),
    )


def _snapshot(
    uid: int,
    expiry: date,
    underlying_price: Decimal,
    entries: tuple[OptionContractObservation, ...],
    *,
    event_at: datetime = BASE_TIME,
) -> OptionChainSnapshotObservation:
    return OptionChainSnapshotObservation(
        snapshot_uid=UUID(int=uid),
        underlying_instrument_key=UNDERLYING,
        expiry_date=expiry,
        event_at_utc=event_at,
        received_at_utc=event_at + timedelta(seconds=1),
        underlying_price=underlying_price,
        snapshot_status="COMPLETE",
        quality_status="VALID",
        is_point_in_time_eligible=True,
        revision=0,
        entries=entries,
        calculation_source_version="upstox-option-chain-v1",
    )


def _entry(
    uid: int,
    option_type: str,
    strike: int,
    *,
    oi: Decimal | int = 100,
    volume: Decimal | int = 50,
    last: Decimal | int = 10,
    iv: Decimal | None = Decimal("0.20"),
    delta: Decimal | None = None,
    multiplier: Decimal | int | None = 50,
    expiry: date = EXPIRY,
) -> OptionContractObservation:
    return OptionContractObservation(
        derivative_contract_uid=UUID(int=uid),
        instrument_key=f"NSE_FO|OPT-{uid}",
        expiry_date=expiry,
        strike_price=Decimal(str(strike)),
        option_type=option_type,  # type: ignore[arg-type]
        last_price=Decimal(str(last)),
        volume_quantity=Decimal(str(volume)),
        open_interest=Decimal(str(oi)),
        implied_volatility=iv,
        delta=delta,
        contract_multiplier=None if multiplier is None else Decimal(str(multiplier)),
        quality_status="VALID",
        greeks_source_version="provider-greeks-v1" if delta is not None else None,
    )
