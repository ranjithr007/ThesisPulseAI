from datetime import UTC, datetime
from decimal import Decimal
from uuid import UUID

import pytest

from app.contracts.v1.market_data import FeatureSnapshotV1, FeatureValueV1
from app.directional.calculator import DeterministicDirectionalCalculator
from app.directional.definitions import DirectionalEngineOptions
from app.directional.store import InMemoryDirectionalIntelligenceStore
from app.features.models import StoredFeatureSnapshot


def test_strong_long_output_is_deterministic() -> None:
    calculator = DeterministicDirectionalCalculator(DirectionalEngineOptions())
    snapshot = _snapshot(
        1,
        {
            "trend_score": "0.90",
            "trend_spread_5_20": "0.02",
            "close_return_1": "0.01",
            "close_return_3": "0.02",
            "momentum_5": "0.03",
            "close_location_value": "0.80",
            "volume_ratio_20": "2.50",
        },
    )

    first = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)
    second = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)

    assert first == second
    assert first.direction == "STRONG_LONG"
    assert first.score == Decimal("0.74200000")
    assert first.confidence == Decimal("0.83230000")
    assert first.is_eligible_for_fusion is True
    assert sum(item.weight for item in first.evidence) == Decimal("1.00")
    assert all(item.impact != "SUPPORTS_SHORT" for item in first.evidence)


def test_strong_short_output_is_symmetric() -> None:
    calculator = DeterministicDirectionalCalculator(DirectionalEngineOptions())
    snapshot = _snapshot(
        2,
        {
            "trend_score": "-0.90",
            "trend_spread_5_20": "-0.02",
            "close_return_1": "-0.01",
            "close_return_3": "-0.02",
            "momentum_5": "-0.03",
            "close_location_value": "-0.80",
            "volume_ratio_20": "2.50",
        },
    )

    output = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)

    assert output.direction == "STRONG_SHORT"
    assert output.score == Decimal("-0.74200000")
    assert output.confidence == Decimal("0.83230000")
    assert all(item.impact != "SUPPORTS_LONG" for item in output.evidence)


def test_conflicted_components_produce_neutral_vote() -> None:
    calculator = DeterministicDirectionalCalculator(DirectionalEngineOptions())
    snapshot = _snapshot(
        3,
        {
            "trend_score": "0",
            "trend_spread_5_20": "0",
            "close_return_1": "0",
            "close_return_3": "0",
            "momentum_5": "0",
            "close_location_value": "0",
            "volume_ratio_20": "1",
        },
    )

    output = calculator.calculate(snapshot, snapshot.generated_at_utc, 0)

    assert output.direction == "NEUTRAL"
    assert output.score == Decimal("0E-8")
    assert output.confidence == Decimal("0.35000000")
    assert output.warnings == ["DIRECTIONAL_CONVICTION_BELOW_THRESHOLD"]


def test_ineligible_feature_snapshot_is_rejected_by_calculator() -> None:
    calculator = DeterministicDirectionalCalculator(DirectionalEngineOptions())
    snapshot = _snapshot(4, {}, eligible=False)

    with pytest.raises(ValueError, match="not eligible"):
        calculator.calculate(snapshot, snapshot.generated_at_utc, 0)


def test_store_is_idempotent_and_revisions_corrections() -> None:
    calculator = DeterministicDirectionalCalculator(DirectionalEngineOptions())
    store = InMemoryDirectionalIntelligenceStore()
    first_source = StoredFeatureSnapshot(
        engine_output_id=None,
        snapshot=_snapshot(5, _long_values()),
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
        snapshot=_snapshot(6, _long_values()),
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


def test_store_ignores_ineligible_feature_without_output() -> None:
    calculator = DeterministicDirectionalCalculator(DirectionalEngineOptions())
    store = InMemoryDirectionalIntelligenceStore()
    source = StoredFeatureSnapshot(
        engine_output_id=None,
        snapshot=_snapshot(7, {}, eligible=False),
        input_candle_ids=tuple(),
    )

    result = store.process(source, calculator, source.snapshot.generated_at_utc)

    assert result.outcome == "IGNORED_INELIGIBLE"
    assert result.output is None
    assert store.get_status().output_count == 0


def _long_values() -> dict[str, str]:
    return {
        "trend_score": "0.90",
        "trend_spread_5_20": "0.02",
        "close_return_1": "0.01",
        "close_return_3": "0.02",
        "momentum_5": "0.03",
        "close_location_value": "0.80",
        "volume_ratio_20": "2.50",
    }


def _snapshot(
    identity: int,
    values: dict[str, str],
    *,
    eligible: bool = True,
) -> FeatureSnapshotV1:
    now = datetime(2026, 6, 30, 10, 0, tzinfo=UTC)
    defaults = {
        "trend_score": "0",
        "trend_spread_5_20": "0",
        "close_return_1": "0",
        "close_return_3": "0",
        "momentum_5": "0",
        "close_location_value": "0",
        "volume_ratio_20": "1",
    }
    defaults.update(values)
    features = [
        FeatureValueV1(name=name, version="1.0.0", value=Decimal(value))
        for name, value in defaults.items()
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
