from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import UUID

import pytest

from app.confirmation.calculator import (
    DeterministicMultiTimeframeConfirmationCalculator,
)
from app.confirmation.definitions import MultiTimeframeConfirmationOptions
from app.confirmation.models import (
    ConfirmationInputBundle,
    ConfirmationStoreStatus,
    TimeframeIntelligencePair,
)
from app.confirmation.service import MultiTimeframeConfirmationService
from app.confirmation.store import InMemoryMultiTimeframeConfirmationStore
from app.contracts.v1.directional import DirectionalEngineOutputV1
from app.contracts.v1.regime import MarketRegimeOutputV1
from app.core.settings import Settings
from app.directional.models import StoredDirectionalOutput
from app.regime.models import StoredRegimeOutput


INSTRUMENT = "NSE_INDEX|Nifty 50"
BASE_TIME = datetime(2026, 6, 30, 10, 0, tzinfo=UTC)
ALL_TIMEFRAMES = ("1m", "5m", "15m", "1h", "1d")


def test_aligned_long_confirmation_is_deterministic() -> None:
    calculator = _calculator()
    bundle = _bundle({timeframe: Decimal("0.8") for timeframe in ALL_TIMEFRAMES})

    first = calculator.calculate(bundle, BASE_TIME, 0)
    second = calculator.calculate(bundle, BASE_TIME, 0)

    assert first == second
    assert first.direction == "STRONG_LONG"
    assert first.score == Decimal("0.80000000")
    assert first.confidence == Decimal("0.89000000")
    assert first.alignment_score == Decimal("1.00000000")
    assert first.contradiction_score == Decimal("0E-8")
    assert first.coverage == Decimal("1.00000000")
    assert first.required_timeframes_present is True
    assert first.is_eligible_for_fusion is True
    assert first.data_quality_status == "VALID"
    assert sum(item.weight for item in first.evidence) == Decimal("1.00")


def test_aligned_short_confirmation_is_symmetric() -> None:
    calculator = _calculator()
    bundle = _bundle({timeframe: Decimal("-0.8") for timeframe in ALL_TIMEFRAMES})

    output = calculator.calculate(bundle, BASE_TIME, 0)

    assert output.direction == "STRONG_SHORT"
    assert output.score == Decimal("-0.80000000")
    assert output.confidence == Decimal("0.89000000")
    assert output.alignment_score == Decimal("1.00000000")
    assert output.contradiction_score == Decimal("0E-8")
    assert output.is_eligible_for_fusion is True


def test_cross_timeframe_contradiction_fails_closed() -> None:
    calculator = _calculator()
    bundle = _bundle(
        {
            "5m": Decimal("0.8"),
            "15m": Decimal("-0.8"),
            "1h": Decimal("-0.8"),
        }
    )

    output = calculator.calculate(bundle, BASE_TIME, 0)

    assert output.direction == "NEUTRAL"
    assert output.score == Decimal("-0.16000000")
    assert output.coverage == Decimal("0.75000000")
    assert output.alignment_score == Decimal("0.40000000")
    assert output.contradiction_score == Decimal("0.60000000")
    assert output.is_eligible_for_fusion is False
    assert output.data_quality_status == "DEGRADED"
    assert "TIMEFRAME_CONTRADICTION_ABOVE_THRESHOLD" in output.warnings
    assert "CONFIRMATION_CONVICTION_BELOW_THRESHOLD" in output.warnings


def test_regime_bias_dampens_directional_conviction() -> None:
    calculator = _calculator()
    bundle = _bundle(
        {timeframe: Decimal("0.8") for timeframe in ALL_TIMEFRAMES},
        regime_scores={timeframe: Decimal("-0.8") for timeframe in ALL_TIMEFRAMES},
    )

    output = calculator.calculate(bundle, BASE_TIME, 0)

    assert output.direction == "LONG"
    assert output.score == Decimal("0.40000000")
    assert output.confidence == Decimal("0.67000000")
    assert output.is_eligible_for_fusion is True


def test_range_and_extreme_volatility_reduce_effective_weight() -> None:
    calculator = _calculator()
    bundle = _bundle(
        {timeframe: Decimal("0.8") for timeframe in ALL_TIMEFRAMES},
        structures={timeframe: "RANGE_BOUND" for timeframe in ALL_TIMEFRAMES},
        volatilities={timeframe: "EXTREME" for timeframe in ALL_TIMEFRAMES},
    )

    output = calculator.calculate(bundle, BASE_TIME, 0)

    assert output.direction == "LONG"
    assert output.score == Decimal("0.31200000")
    assert output.confidence == Decimal("0.62160000")
    assert all(
        item.effective_weight
        in {
            Decimal("0.03900000"),
            Decimal("0.11700000"),
            Decimal("0.09750000"),
            Decimal("0.07800000"),
            Decimal("0.05850000"),
        }
        for item in output.timeframe_confirmations
    )


def test_calculator_rejects_mismatched_directional_and_regime_cutoffs() -> None:
    calculator = _calculator()
    pair = _pair(
        "5m",
        Decimal("0.8"),
        Decimal("0.8"),
        BASE_TIME,
        1,
        regime_as_of=BASE_TIME - timedelta(minutes=5),
    )
    bundle = ConfirmationInputBundle(instrument_key=INSTRUMENT, pairs=(pair,))

    with pytest.raises(ValueError, match="cutoffs differ"):
        calculator.calculate(bundle, BASE_TIME, 0)


def test_store_is_idempotent_and_revisions_are_scoped_by_cutoff() -> None:
    calculator = _calculator()
    store = InMemoryMultiTimeframeConfirmationStore()
    first_bundle = _bundle(
        {timeframe: Decimal("0.8") for timeframe in ALL_TIMEFRAMES},
        identity_offset=0,
    )

    created = store.process(first_bundle, calculator, BASE_TIME)
    duplicate = store.process(first_bundle, calculator, BASE_TIME)
    corrected_bundle = _bundle(
        {timeframe: Decimal("0.7") for timeframe in ALL_TIMEFRAMES},
        identity_offset=100,
    )
    revised = store.process(corrected_bundle, calculator, BASE_TIME)
    next_cutoff = BASE_TIME + timedelta(minutes=5)
    next_bundle = _bundle(
        {timeframe: Decimal("0.8") for timeframe in ALL_TIMEFRAMES},
        as_of=next_cutoff,
        identity_offset=200,
    )
    next_created = store.process(next_bundle, calculator, next_cutoff)

    assert created.outcome == "CREATED"
    assert created.output is not None
    assert created.output.revision == 0
    assert duplicate.outcome == "DUPLICATE"
    assert duplicate.output == created.output
    assert revised.outcome == "REVISED"
    assert revised.output is not None
    assert revised.output.revision == 1
    assert next_created.outcome == "CREATED"
    assert next_created.output is not None
    assert next_created.output.revision == 0
    assert store.get_status().output_count == 3
    latest = store.get_latest(INSTRUMENT)
    assert latest is not None
    assert latest.output.as_of_utc == next_cutoff


def test_service_returns_incomplete_when_required_timeframe_is_missing() -> None:
    directional, regime = _lookup_services(
        _bundle(
            {
                "5m": Decimal("0.8"),
                "15m": Decimal("0.8"),
            }
        )
    )
    service = MultiTimeframeConfirmationService(
        _enabled_settings(),
        directional,
        regime,
        store=InMemoryMultiTimeframeConfirmationStore(),
    )

    result = service.process_instrument(INSTRUMENT, BASE_TIME)

    assert result.outcome == "IGNORED_INCOMPLETE"
    assert result.output is None
    assert "1h" in (result.reason or "")


def test_service_excludes_future_context_instead_of_looking_ahead() -> None:
    primary_pairs = list(
        _bundle(
            {
                "5m": Decimal("0.8"),
                "1h": Decimal("0.8"),
            }
        ).pairs
    )
    future_15m = _pair(
        "15m",
        Decimal("0.8"),
        Decimal("0.8"),
        BASE_TIME + timedelta(minutes=15),
        500,
    )
    bundle = ConfirmationInputBundle(
        instrument_key=INSTRUMENT,
        pairs=tuple(primary_pairs + [future_15m]),
    )
    directional, regime = _lookup_services(bundle)
    service = MultiTimeframeConfirmationService(
        _enabled_settings(),
        directional,
        regime,
        store=InMemoryMultiTimeframeConfirmationStore(),
    )

    result = service.process_instrument(INSTRUMENT, BASE_TIME)

    assert result.outcome == "IGNORED_INCOMPLETE"
    assert result.output is None
    assert "15m" in (result.reason or "")


def test_service_rejects_primary_timestamp_mismatch() -> None:
    pair = _pair(
        "5m",
        Decimal("0.8"),
        Decimal("0.8"),
        BASE_TIME,
        600,
        regime_as_of=BASE_TIME - timedelta(minutes=5),
    )
    directional, regime = _lookup_services(
        ConfirmationInputBundle(instrument_key=INSTRUMENT, pairs=(pair,))
    )
    service = MultiTimeframeConfirmationService(
        _enabled_settings(),
        directional,
        regime,
        store=InMemoryMultiTimeframeConfirmationStore(),
    )

    result = service.process_instrument(INSTRUMENT, BASE_TIME)

    assert result.outcome == "IGNORED_INCOMPLETE"
    assert "timestamps do not match" in (result.reason or "")


def test_service_acknowledges_expected_source_rejection() -> None:
    directional, regime = _lookup_services(
        _bundle({timeframe: Decimal("0.8") for timeframe in ALL_TIMEFRAMES})
    )
    service = MultiTimeframeConfirmationService(
        _enabled_settings(),
        directional,
        regime,
        store=_FailingStore(ValueError("Intelligence source expired")),
    )

    result = service.process_instrument(INSTRUMENT, BASE_TIME)

    assert result.outcome == "IGNORED_INELIGIBLE"
    assert "expired" in (result.reason or "")


def test_service_reraises_infrastructure_failure_for_retry() -> None:
    directional, regime = _lookup_services(
        _bundle({timeframe: Decimal("0.8") for timeframe in ALL_TIMEFRAMES})
    )
    service = MultiTimeframeConfirmationService(
        _enabled_settings(),
        directional,
        regime,
        store=_FailingStore(RuntimeError("Operational database is unavailable")),
    )

    with pytest.raises(RuntimeError, match="database is unavailable"):
        service.process_instrument(INSTRUMENT, BASE_TIME)


def _calculator() -> DeterministicMultiTimeframeConfirmationCalculator:
    return DeterministicMultiTimeframeConfirmationCalculator(
        MultiTimeframeConfirmationOptions()
    )


def _enabled_settings() -> Settings:
    return Settings(
        feature_factory_enabled=True,
        directional_engine_enabled=True,
        regime_engine_enabled=True,
        confirmation_engine_enabled=True,
    )


def _bundle(
    directional_scores: dict[str, Decimal],
    *,
    regime_scores: dict[str, Decimal] | None = None,
    structures: dict[str, str] | None = None,
    volatilities: dict[str, str] | None = None,
    as_of: datetime = BASE_TIME,
    identity_offset: int = 0,
) -> ConfirmationInputBundle:
    regime_values = regime_scores or directional_scores
    structure_values = structures or {}
    volatility_values = volatilities or {}
    pairs = tuple(
        _pair(
            timeframe,
            score,
            regime_values.get(timeframe, score),
            as_of,
            identity_offset + index * 10,
            structure=structure_values.get(timeframe),
            volatility=volatility_values.get(timeframe, "NORMAL"),
        )
        for index, (timeframe, score) in enumerate(directional_scores.items(), start=1)
    )
    return ConfirmationInputBundle(instrument_key=INSTRUMENT, pairs=pairs)


def _pair(
    timeframe: str,
    directional_score: Decimal,
    regime_score: Decimal,
    as_of: datetime,
    identity: int,
    *,
    regime_as_of: datetime | None = None,
    structure: str | None = None,
    volatility: str = "NORMAL",
) -> TimeframeIntelligencePair:
    directional = StoredDirectionalOutput(
        engine_output_id=identity + 1,
        output=_directional_output(
            timeframe,
            directional_score,
            as_of,
            identity + 1,
        ),
        source_engine_output_id=identity + 1001,
    )
    regime = StoredRegimeOutput(
        engine_output_id=identity + 2,
        output=_regime_output(
            timeframe,
            regime_score,
            regime_as_of or as_of,
            identity + 2,
            structure=structure,
            volatility=volatility,
        ),
        source_engine_output_id=identity + 1002,
    )
    return TimeframeIntelligencePair(
        timeframe=timeframe,
        directional=directional,
        regime=regime,
    )


def _directional_output(
    timeframe: str,
    score: Decimal,
    as_of: datetime,
    identity: int,
) -> DirectionalEngineOutputV1:
    return DirectionalEngineOutputV1(
        output_uid=UUID(int=identity),
        message_uid=UUID(int=identity + 10000),
        source_feature_snapshot_uid=UUID(int=identity + 20000),
        instrument_key=INSTRUMENT,
        timeframe=timeframe,
        as_of_utc=as_of,
        generated_at_utc=as_of,
        engine_code="THESIS_PULSE_TECHNICAL_DIRECTION",
        engine_version="1.0.0",
        policy_version="technical-direction-v1.0.0",
        feature_set_version="feature-set-v1.0.0",
        direction=_direction(score),
        score=score,
        confidence=Decimal("0.8"),
        data_quality_status="VALID",
        is_stale=False,
        is_eligible_for_fusion=True,
        revision=0,
        evidence=[],
        warnings=[],
    )


def _regime_output(
    timeframe: str,
    score: Decimal,
    as_of: datetime,
    identity: int,
    *,
    structure: str | None,
    volatility: str,
) -> MarketRegimeOutputV1:
    resolved_structure = structure or (
        "TRENDING_UP" if score > 0 else "TRENDING_DOWN" if score < 0 else "RANGE_BOUND"
    )
    return MarketRegimeOutputV1(
        output_uid=UUID(int=identity),
        message_uid=UUID(int=identity + 30000),
        source_feature_snapshot_uid=UUID(int=identity + 40000),
        instrument_key=INSTRUMENT,
        timeframe=timeframe,
        as_of_utc=as_of,
        generated_at_utc=as_of,
        engine_code="THESIS_PULSE_MARKET_REGIME",
        engine_version="1.0.0",
        policy_version="market-regime-v1.0.0",
        feature_set_version="feature-set-v1.0.0",
        structure_regime=resolved_structure,
        volatility_regime=volatility,
        direction_bias=_direction(score),
        score=score,
        confidence=Decimal("0.8"),
        trend_strength=abs(score),
        range_score=Decimal("1") if resolved_structure == "RANGE_BOUND" else Decimal("0"),
        transition_score=(
            Decimal("1") if resolved_structure == "TRANSITION" else Decimal("0")
        ),
        volatility_score=Decimal("0.5"),
        data_quality_status="VALID",
        is_stale=False,
        is_eligible_for_fusion=True,
        revision=0,
        evidence=[],
        warnings=[],
    )


def _direction(score: Decimal) -> str:
    if score >= Decimal("0.65"):
        return "STRONG_LONG"
    if score >= Decimal("0.25"):
        return "LONG"
    if score <= Decimal("-0.65"):
        return "STRONG_SHORT"
    if score <= Decimal("-0.25"):
        return "SHORT"
    return "NEUTRAL"


def _lookup_services(bundle: ConfirmationInputBundle):
    directional = {
        (INSTRUMENT, pair.timeframe): pair.directional for pair in bundle.pairs
    }
    regime = {(INSTRUMENT, pair.timeframe): pair.regime for pair in bundle.pairs}
    return _LookupService(directional), _LookupService(regime)


class _LookupService:
    def __init__(self, values: dict[tuple[str, str], object]) -> None:
        self._values = values

    def get_latest_stored(self, instrument_key: str, timeframe: str):
        return self._values.get((instrument_key, timeframe))


class _FailingStore:
    provider_name = "Test"

    def __init__(self, exception: Exception) -> None:
        self._exception = exception

    def process(self, bundle, calculator, processed_at_utc):
        raise self._exception

    def get_latest(self, instrument_key: str):
        return None

    def get_status(self) -> ConfirmationStoreStatus:
        return ConfirmationStoreStatus(
            provider=self.provider_name,
            output_count=0,
            latest_processed_at_utc=None,
            latest_error=None,
        )
