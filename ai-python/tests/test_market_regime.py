from datetime import UTC, datetime
from decimal import Decimal
from uuid import UUID

import pytest

from app.contracts.v1.market_data import FeatureSnapshotV1, FeatureValueV1
from app.core.settings import Settings
from app.features.models import StoredFeatureSnapshot
from app.regime.calculator import DeterministicMarketRegimeCalculator
from app.regime.definitions import MarketRegimeOptions
from app.regime.models import RegimeStoreStatus
from app.regime.service import MarketRegimeService
from app.regime.store import InMemoryMarketRegimeStore


def test_trending_up_regime_is_deterministic() -> None:
    calculator = DeterministicMarketRegimeCalculator(MarketRegimeOptions())
    snapshot = _snapshot(1, _uptrend_values())

    first = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)
    second = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)

    assert first == second
    assert first.structure_regime == "TRENDING_UP"
    assert first.volatility_regime == "NORMAL"
    assert first.direction_bias == "STRONG_LONG"
    assert first.score == Decimal("0.64500000")
    assert first.confidence == Decimal("0.82475357")
    assert first.trend_strength == Decimal("0.75150000")
    assert first.is_eligible_for_fusion is True
    assert sum(item.weight for item in first.evidence) == Decimal("1.00")


def test_trending_down_regime_is_symmetric() -> None:
    calculator = DeterministicMarketRegimeCalculator(MarketRegimeOptions())
    snapshot = _snapshot(2, _downtrend_values())

    output = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)

    assert output.structure_regime == "TRENDING_DOWN"
    assert output.volatility_regime == "NORMAL"
    assert output.direction_bias == "STRONG_SHORT"
    assert output.score == Decimal("-0.64500000")
    assert output.confidence == Decimal("0.82475357")


def test_low_volatility_range_is_classified_independently() -> None:
    calculator = DeterministicMarketRegimeCalculator(MarketRegimeOptions())
    snapshot = _snapshot(
        3,
        {
            "trend_score": "0",
            "trend_spread_5_20": "0",
            "momentum_5": "0",
            "close_return_3": "0",
            "realized_volatility_20": "0.0005",
            "atr_14": "0.4",
            "sma_20": "1000",
            "volume_ratio_20": "1",
        },
    )

    output = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)

    assert output.structure_regime == "RANGE_BOUND"
    assert output.volatility_regime == "LOW"
    assert output.direction_bias == "NEUTRAL"
    assert output.score == Decimal("0E-8")
    assert output.range_score == Decimal("0.98928571")
    assert output.confidence == Decimal("0.97571429")
    assert output.warnings == []


def test_directional_disagreement_creates_transition_regime() -> None:
    calculator = DeterministicMarketRegimeCalculator(MarketRegimeOptions())
    snapshot = _snapshot(
        4,
        {
            "trend_score": "0.6",
            "trend_spread_5_20": "-0.012",
            "momentum_5": "0.02",
            "close_return_3": "-0.015",
            "realized_volatility_20": "0.005",
            "atr_14": "5",
            "sma_20": "1000",
            "volume_ratio_20": "2.5",
        },
    )

    output = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)

    assert output.structure_regime == "TRANSITION"
    assert output.volatility_regime == "HIGH"
    assert output.direction_bias == "NEUTRAL"
    assert output.transition_score == Decimal("0.38217857")
    assert output.confidence == Decimal("0.55787857")
    assert output.warnings == [
        "REGIME_TRANSITION_DETECTED",
        "HIGH_VOLATILITY_REGIME",
    ]
    transition_evidence = next(
        item for item in output.evidence if item.code == "REGIME_TRANSITION_RISK"
    )
    assert transition_evidence.impact == "CONTRADICTS"


def test_extreme_volatility_is_an_orthogonal_overlay() -> None:
    calculator = DeterministicMarketRegimeCalculator(MarketRegimeOptions())
    snapshot = _snapshot(
        5,
        {
            "trend_score": "0.1",
            "trend_spread_5_20": "0.001",
            "momentum_5": "0.001",
            "close_return_3": "0.001",
            "realized_volatility_20": "0.01",
            "atr_14": "10",
            "sma_20": "1000",
            "volume_ratio_20": "3",
        },
    )

    output = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)

    assert output.structure_regime == "RANGE_BOUND"
    assert output.volatility_regime == "EXTREME"
    assert output.volatility_score == Decimal("1.00000000")
    assert output.direction_bias == "NEUTRAL"
    assert output.warnings == ["EXTREME_VOLATILITY_REGIME"]


def test_ineligible_feature_snapshot_is_rejected_by_calculator() -> None:
    calculator = DeterministicMarketRegimeCalculator(MarketRegimeOptions())
    snapshot = _snapshot(6, _uptrend_values(), eligible=False)

    with pytest.raises(ValueError, match="not eligible"):
        calculator.calculate(snapshot, snapshot.generated_at_utc, 0)


def test_store_is_idempotent_and_revisions_follow_feature_corrections() -> None:
    calculator = DeterministicMarketRegimeCalculator(MarketRegimeOptions())
    store = InMemoryMarketRegimeStore()
    first_source = StoredFeatureSnapshot(
        engine_output_id=None,
        snapshot=_snapshot(7, _uptrend_values()),
        input_candle_ids=tuple(),
    )

    created = store.process(
        first_source,
        calculator,
        first_source.snapshot.generated_at_utc,
    )
    duplicate = store.process(
        first_source,
        calculator,
        first_source.snapshot.generated_at_utc,
    )
    corrected_source = StoredFeatureSnapshot(
        engine_output_id=None,
        snapshot=_snapshot(8, _uptrend_values()),
        input_candle_ids=tuple(),
    )
    revised = store.process(
        corrected_source,
        calculator,
        corrected_source.snapshot.generated_at_utc,
    )

    assert created.outcome == "CREATED"
    assert duplicate.outcome == "DUPLICATE"
    assert duplicate.output == created.output
    assert revised.outcome == "REVISED"
    assert revised.output is not None
    assert revised.output.revision == 1
    assert revised.output.output_uid != created.output.output_uid
    assert store.get_status().output_count == 2


def test_service_precheck_acks_ineligible_without_calling_store() -> None:
    store = _FailingStore(AssertionError("store should not be called"))
    service = MarketRegimeService(_enabled_settings(), store=store)
    source = StoredFeatureSnapshot(
        engine_output_id=1,
        snapshot=_snapshot(9, _uptrend_values(), eligible=False),
        input_candle_ids=tuple(),
    )

    result = service.process_feature(source, source.snapshot.generated_at_utc)

    assert result.outcome == "IGNORED_INELIGIBLE"
    assert result.output is None
    assert store.process_calls == 0


@pytest.mark.parametrize(
    ("exception", "reason"),
    [
        (ValueError("Feature output expired before regime processing"), "expired"),
        (RuntimeError("Feature output is no longer current"), "no longer current"),
    ],
)
def test_service_acks_expected_source_rejections(
    exception: Exception,
    reason: str,
) -> None:
    store = _FailingStore(exception)
    service = MarketRegimeService(_enabled_settings(), store=store)
    source = StoredFeatureSnapshot(
        engine_output_id=1,
        snapshot=_snapshot(10, _uptrend_values()),
        input_candle_ids=tuple(),
    )

    result = service.process_feature(source, source.snapshot.generated_at_utc)

    assert result.outcome == "IGNORED_INELIGIBLE"
    assert result.output is None
    assert reason in (result.reason or "")
    assert store.process_calls == 1


def test_service_reraises_infrastructure_failure_for_retry() -> None:
    store = _FailingStore(RuntimeError("Operational database is unavailable"))
    service = MarketRegimeService(_enabled_settings(), store=store)
    source = StoredFeatureSnapshot(
        engine_output_id=1,
        snapshot=_snapshot(11, _uptrend_values()),
        input_candle_ids=tuple(),
    )

    with pytest.raises(RuntimeError, match="database is unavailable"):
        service.process_feature(source, source.snapshot.generated_at_utc)


def _enabled_settings() -> Settings:
    return Settings(
        feature_factory_enabled=True,
        regime_engine_enabled=True,
    )


class _FailingStore:
    provider_name = "Test"

    def __init__(self, exception: Exception) -> None:
        self._exception = exception
        self.process_calls = 0

    def process(self, source, calculator, processed_at_utc):
        self.process_calls += 1
        raise self._exception

    def get_latest(self, instrument_key: str, timeframe: str):
        return None

    def get_status(self) -> RegimeStoreStatus:
        return RegimeStoreStatus(
            provider=self.provider_name,
            output_count=0,
            latest_processed_at_utc=None,
            latest_error=None,
        )


def _uptrend_values() -> dict[str, str]:
    return {
        "trend_score": "0.8",
        "trend_spread_5_20": "0.015",
        "momentum_5": "0.025",
        "close_return_3": "0.02",
        "realized_volatility_20": "0.002",
        "atr_14": "2",
        "sma_20": "1000",
        "volume_ratio_20": "1.5",
    }


def _downtrend_values() -> dict[str, str]:
    return {
        "trend_score": "-0.8",
        "trend_spread_5_20": "-0.015",
        "momentum_5": "-0.025",
        "close_return_3": "-0.02",
        "realized_volatility_20": "0.002",
        "atr_14": "2",
        "sma_20": "1000",
        "volume_ratio_20": "1.5",
    }


def _snapshot(
    identity: int,
    values: dict[str, str],
    *,
    eligible: bool = True,
) -> FeatureSnapshotV1:
    now = datetime(2026, 6, 30, 10, 0, tzinfo=UTC)
    features = [
        FeatureValueV1(name=name, version="1.0.0", value=Decimal(value))
        for name, value in values.items()
    ]
    return FeatureSnapshotV1(
        snapshot_uid=UUID(int=identity),
        message_uid=UUID(int=identity + 100),
        instrument_key="NSE_INDEX|Nifty 50",
        timeframe="5m",
        as_of_utc=now,
        data_cutoff_utc=now,
        generated_at_utc=now,
        feature_set_version="feature-set-v1.0.0",
        revision=0,
        input_count=21,
        required_input_count=21,
        completeness=Decimal("1"),
        data_quality_status="VALID" if eligible else "DEGRADED",
        freshness_milliseconds=0,
        is_stale=False,
        is_eligible_for_engines=eligible,
        features=features,
        missing_features=[],
        warnings=[] if eligible else ["INSUFFICIENT_WARMUP"],
    )
