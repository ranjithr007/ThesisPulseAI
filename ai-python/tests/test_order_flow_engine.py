from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import UUID

from app.contracts.v1.market_data import (
    MarketCandleDeliveryV1,
    MarketCandleEnvelopeV1,
    MarketCandlePublishedV1,
    MarketDataMessageMetadataV1,
    MarketQuoteDeliveryV1,
    MarketQuoteEnvelopeV1,
    MarketQuotePublishedV1,
)
from app.core.settings import Settings
from app.order_flow.service import OrderFlowService
from app.order_flow.store import InMemoryOrderFlowStore

BASE_TIME = datetime(2026, 6, 30, 10, 0, tzinfo=UTC)
INSTRUMENT = "NSE_EQ|INE002A01018"


def test_order_flow_creates_deterministic_long_proxy_output() -> None:
    service = _service()
    for index in range(12):
        result = service.process_quote(
            _quote(
                index,
                price=Decimal("100") + Decimal(index) * Decimal("0.10"),
                buy=Decimal("2000"),
                sell=Decimal("500"),
                open_interest=Decimal("10000") + Decimal(index) * Decimal("50"),
            ),
            BASE_TIME,
        )
        assert result.outcome == "CREATED"

    first = service.process_candle(_candle(), BASE_TIME)
    duplicate = service.process_candle(_candle(), BASE_TIME)

    assert first.outcome == "CREATED"
    assert first.output is not None
    assert duplicate.outcome == "DUPLICATE"
    assert duplicate.output == first.output
    output = first.output
    assert output.direction == "LONG"
    assert output.score > Decimal("0.20")
    assert output.confidence >= Decimal("0.55")
    assert output.is_eligible_for_fusion is True
    assert output.quote_sample_count == 12
    assert output.usable_quote_count == 12
    assert output.book_imbalance > Decimal("0")
    assert output.tick_rule_delta_ratio > Decimal("0")
    assert output.open_interest_change_fraction is not None
    assert output.open_interest_change_fraction > Decimal("0")
    assert output.data_quality_status == "DEGRADED"
    assert "PROXY_TICK_RULE_NOT_AGGRESSOR_FLOW" in output.warnings
    assert len(output.quote_message_uids) == 12


def test_order_flow_creates_short_output() -> None:
    service = _service()
    for index in range(12):
        service.process_quote(
            _quote(
                index,
                price=Decimal("101.10") - Decimal(index) * Decimal("0.10"),
                buy=Decimal("500"),
                sell=Decimal("2000"),
                open_interest=Decimal("10000") + Decimal(index) * Decimal("50"),
            ),
            BASE_TIME,
        )

    result = service.process_candle(_candle(), BASE_TIME)

    assert result.output is not None
    assert result.output.direction == "SHORT"
    assert result.output.score < Decimal("-0.20")
    assert result.output.is_eligible_for_fusion is True


def test_insufficient_stale_quotes_fail_closed() -> None:
    service = _service(maximum_quote_age_seconds=5)
    for index in range(3):
        service.process_quote(
            _quote(
                index,
                price=Decimal("100") + Decimal(index) * Decimal("0.10"),
                buy=Decimal("2000"),
                sell=Decimal("500"),
                open_interest=None,
                seconds_before_close=60 - index,
            ),
            BASE_TIME,
        )

    result = service.process_candle(_candle(), BASE_TIME)

    assert result.output is not None
    output = result.output
    assert output.is_eligible_for_fusion is False
    assert output.is_stale is True
    assert "INSUFFICIENT_QUOTE_SAMPLES" in output.warnings
    assert "ORDER_FLOW_QUOTES_STALE" in output.warnings
    assert "OPEN_INTEREST_UNAVAILABLE" in output.warnings


def test_quote_delivery_is_idempotent_and_degraded_sample_is_retained() -> None:
    store = InMemoryOrderFlowStore()
    service = _service(store=store)
    valid = _quote(
        1,
        price=Decimal("100"),
        buy=Decimal("1000"),
        sell=Decimal("1000"),
        open_interest=None,
    )
    degraded = _quote(
        2,
        price=Decimal("100"),
        buy=Decimal("1000"),
        sell=Decimal("1000"),
        open_interest=None,
        quality_status="DEGRADED",
        usable=False,
    )

    assert service.process_quote(valid, BASE_TIME).outcome == "CREATED"
    assert service.process_quote(valid, BASE_TIME).outcome == "DUPLICATE"
    assert service.process_quote(degraded, BASE_TIME).outcome == "IGNORED_INELIGIBLE"
    assert service.get_status().quote_sample_count == 2


def _service(
    *,
    maximum_quote_age_seconds: int = 30,
    store: InMemoryOrderFlowStore | None = None,
) -> OrderFlowService:
    return OrderFlowService(
        Settings(
            feature_factory_enabled=True,
            feature_factory_internal_api_key="test-key",
            order_flow_engine_enabled=True,
            order_flow_minimum_quote_samples=10,
            order_flow_minimum_usable_ratio=Decimal("0.80"),
            order_flow_minimum_traded_quantity_coverage=Decimal("0.02"),
            order_flow_maximum_quote_age_seconds=maximum_quote_age_seconds,
            order_flow_directional_threshold=Decimal("0.20"),
            order_flow_fusion_confidence_threshold=Decimal("0.55"),
        ),
        store=store,
    )


def _quote(
    index: int,
    *,
    price: Decimal,
    buy: Decimal,
    sell: Decimal,
    open_interest: Decimal | None,
    seconds_before_close: int | None = None,
    quality_status: str = "VALID",
    usable: bool = True,
) -> MarketQuoteDeliveryV1:
    seconds = seconds_before_close if seconds_before_close is not None else 24 - index * 2
    event_at = BASE_TIME - timedelta(seconds=max(1, seconds))
    message_uid = UUID(int=1000 + index)
    return MarketQuoteDeliveryV1(
        stream_position=index + 1,
        envelope=MarketQuoteEnvelopeV1(
            metadata=MarketDataMessageMetadataV1(
                message_id=message_uid,
                event_type="market.quote.published.v1",
                contract_version="1.0",
                occurred_at_utc=event_at,
                correlation_id=str(UUID(int=9000)),
                producer="ThesisPulse.MarketData.Service",
                producer_version="1.0.0",
                environment="PAPER",
                configuration_version="market-data-publication-v1.0.0",
            ),
            payload=MarketQuotePublishedV1(
                provider_code="UPSTOX",
                instrument_key=INSTRUMENT,
                event_at_utc=event_at,
                received_at_utc=event_at,
                last_traded_price=price,
                last_traded_quantity=Decimal("100"),
                previous_close_price=Decimal("99"),
                open_interest=open_interest,
                total_buy_quantity=buy,
                total_sell_quantity=sell,
                quality_status=quality_status,
                is_usable_for_new_exposure=usable,
                source_version="upstox-v3",
            ),
        ),
    )


def _candle() -> MarketCandleDeliveryV1:
    return MarketCandleDeliveryV1(
        stream_position=500,
        envelope=MarketCandleEnvelopeV1(
            metadata=MarketDataMessageMetadataV1(
                message_id=UUID(int=5000),
                event_type="market.candle.published.v1",
                contract_version="1.0",
                occurred_at_utc=BASE_TIME + timedelta(seconds=1),
                correlation_id=str(UUID(int=9000)),
                producer="ThesisPulse.MarketData.Service",
                producer_version="1.0.0",
                environment="PAPER",
                configuration_version="market-data-publication-v1.0.0",
            ),
            payload=MarketCandlePublishedV1(
                provider_code="UPSTOX",
                instrument_key=INSTRUMENT,
                timeframe="5m",
                open_at_utc=BASE_TIME - timedelta(minutes=5),
                close_at_utc=BASE_TIME,
                open_price=Decimal("100"),
                high_price=Decimal("102"),
                low_price=Decimal("99"),
                close_price=Decimal("101"),
                volume_quantity=Decimal("1200"),
                open_interest=Decimal("10550"),
                is_closed=True,
                is_provisional=False,
                revision=0,
                quality_status="VALID",
                is_usable_for_new_exposure=True,
                received_at_utc=BASE_TIME,
                source_version="upstox-v3",
            ),
        ),
    )
