from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import UUID, uuid4

from app.contracts.v1.market_data import (
    MarketCandleDeliveryV1,
    MarketCandleEnvelopeV1,
    MarketCandlePublishedV1,
    MarketDataMessageMetadataV1,
)
from app.features.calculator import DeterministicFeatureCalculator
from app.features.definitions import FeatureFactoryOptions
from app.features.store import InMemoryFeatureFactoryStore


def test_warmup_is_degraded_and_not_eligible() -> None:
    store = InMemoryFeatureFactoryStore()
    calculator = DeterministicFeatureCalculator(FeatureFactoryOptions())
    delivery = _delivery(0)

    result = store.process(delivery, calculator, delivery.envelope.payload.received_at_utc)

    assert result.outcome == "CREATED"
    assert result.snapshot is not None
    assert result.snapshot.data_quality_status == "DEGRADED"
    assert result.snapshot.is_eligible_for_engines is False
    assert "INSUFFICIENT_WARMUP" in result.snapshot.warnings


def test_complete_window_is_valid_and_deterministic() -> None:
    store = InMemoryFeatureFactoryStore()
    calculator = DeterministicFeatureCalculator(FeatureFactoryOptions())
    latest = None

    for index in range(21):
        delivery = _delivery(index)
        latest = store.process(
            delivery,
            calculator,
            delivery.envelope.payload.received_at_utc,
        )

    assert latest is not None
    assert latest.snapshot is not None
    snapshot = latest.snapshot
    assert snapshot.data_quality_status == "VALID"
    assert snapshot.is_eligible_for_engines is True
    assert snapshot.completeness == Decimal("1.000000000000")
    assert not snapshot.missing_features
    feature_values = {item.name: item.value for item in snapshot.features}
    assert feature_values["sma_5"] == Decimal("118.000000000000")
    assert feature_values["sma_20"] == Decimal("110.500000000000")
    assert feature_values["trend_score"] == Decimal("1.000000000000")
    expected_uid = calculator.create_snapshot_uid(
        snapshot.message_uid,
        snapshot.instrument_key,
        snapshot.timeframe,
        snapshot.as_of_utc,
        snapshot.revision,
    )
    assert snapshot.snapshot_uid == expected_uid


def test_duplicate_message_is_idempotent() -> None:
    store = InMemoryFeatureFactoryStore()
    calculator = DeterministicFeatureCalculator(FeatureFactoryOptions())
    delivery = _delivery(0)

    first = store.process(delivery, calculator, delivery.envelope.payload.received_at_utc)
    second = store.process(delivery, calculator, delivery.envelope.payload.received_at_utc)

    assert first.outcome == "CREATED"
    assert second.outcome == "DUPLICATE"
    assert store.get_status().processed_messages == 1


def test_newer_candle_revision_revises_snapshot() -> None:
    store = InMemoryFeatureFactoryStore()
    calculator = DeterministicFeatureCalculator(FeatureFactoryOptions())

    for index in range(21):
        delivery = _delivery(index)
        store.process(delivery, calculator, delivery.envelope.payload.received_at_utc)

    corrected = _delivery(20, revision=1, close_price=Decimal("125"))
    result = store.process(
        corrected,
        calculator,
        corrected.envelope.payload.received_at_utc,
    )

    assert result.outcome == "REVISED"
    assert result.snapshot is not None
    assert result.snapshot.revision == 1
    assert result.snapshot.message_uid == corrected.envelope.metadata.message_id


def test_provisional_candle_is_ignored() -> None:
    store = InMemoryFeatureFactoryStore()
    calculator = DeterministicFeatureCalculator(FeatureFactoryOptions())
    delivery = _delivery(0, is_closed=False)

    result = store.process(delivery, calculator, delivery.envelope.payload.received_at_utc)

    assert result.outcome == "IGNORED_PROVISIONAL"
    assert result.snapshot is None


def test_same_session_gap_degrades_snapshot() -> None:
    store = InMemoryFeatureFactoryStore()
    calculator = DeterministicFeatureCalculator(FeatureFactoryOptions())

    for index in range(21):
        minute_offset = index if index < 10 else index + 3
        delivery = _delivery(index, minute_offset=minute_offset)
        result = store.process(
            delivery,
            calculator,
            delivery.envelope.payload.received_at_utc,
        )

    assert result.snapshot is not None
    assert result.snapshot.data_quality_status == "DEGRADED"
    assert "CANDLE_GAP_DETECTED" in result.snapshot.warnings
    assert result.snapshot.is_eligible_for_engines is False


def test_stale_processing_blocks_engine_eligibility() -> None:
    store = InMemoryFeatureFactoryStore()
    calculator = DeterministicFeatureCalculator(FeatureFactoryOptions())
    result = None

    for index in range(21):
        delivery = _delivery(index)
        processed_at = delivery.envelope.payload.received_at_utc
        if index == 20:
            processed_at += timedelta(minutes=10)
        result = store.process(delivery, calculator, processed_at)

    assert result is not None and result.snapshot is not None
    assert result.snapshot.is_stale is True
    assert result.snapshot.data_quality_status == "DEGRADED"
    assert result.snapshot.is_eligible_for_engines is False


def _delivery(
    index: int,
    *,
    revision: int = 0,
    close_price: Decimal | None = None,
    is_closed: bool = True,
    minute_offset: int | None = None,
) -> MarketCandleDeliveryV1:
    base = datetime(2026, 6, 30, 3, 45, tzinfo=UTC)
    offset = index if minute_offset is None else minute_offset
    open_at = base + timedelta(minutes=5 * offset)
    close_at = open_at + timedelta(minutes=5)
    open_price = Decimal(100 + index)
    resolved_close = close_price or Decimal(100 + index)
    high = max(open_price, resolved_close) + Decimal("1")
    low = min(open_price, resolved_close) - Decimal("1")
    message_id = UUID(int=(index + 1 + (revision * 1000)))
    payload = MarketCandlePublishedV1(
        provider_code="UPSTOX",
        instrument_key="NSE_INDEX|Nifty 50",
        timeframe="5m",
        open_at_utc=open_at,
        close_at_utc=close_at,
        open_price=open_price,
        high_price=high,
        low_price=low,
        close_price=resolved_close,
        volume_quantity=Decimal(1000 + index),
        open_interest=None,
        is_closed=is_closed,
        is_provisional=not is_closed,
        revision=revision,
        quality_status="VALID",
        is_usable_for_new_exposure=is_closed,
        received_at_utc=close_at,
        source_version="test-v1",
    )
    metadata = MarketDataMessageMetadataV1(
        message_id=message_id if revision == 0 else uuid4(),
        event_type="market.candle.published.v1",
        contract_version="1.0",
        occurred_at_utc=close_at,
        correlation_id="feature-factory-test",
        causation_id=None,
        producer="ThesisPulse.MarketData.Service",
        producer_version="test-v1",
        environment="PAPER",
        configuration_version="test-v1",
    )
    return MarketCandleDeliveryV1(
        stream_position=index + 1 + (revision * 1000),
        envelope=MarketCandleEnvelopeV1(metadata=metadata, payload=payload),
    )
