from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import UUID

from app.contracts.v1.market_data import (
    MarketCandleDeliveryV1,
    MarketCandleEnvelopeV1,
    MarketCandlePublishedV1,
    MarketDataMessageMetadataV1,
)
from app.core.settings import Settings
from app.smart_money.calculator import DeterministicSmartMoneyCalculator
from app.smart_money.config import SmartMoneyRuntimeSettings
from app.smart_money.definitions import SmartMoneyOptions
from app.smart_money.models import SmartMoneyCandle
from app.smart_money.service import SmartMoneyConceptsService
from app.smart_money.store import InMemorySmartMoneyStore

BASE_TIME = datetime(2026, 6, 30, 9, 15, tzinfo=UTC)
INSTRUMENT = "NSE_EQ|INE002A01018"

BULLISH_BOS = [
    ("100", "101", "99", "100"),
    ("100", "103", "100", "102"),
    ("102", "102", "98", "99"),
    ("100", "104", "100", "103"),
    ("103", "103", "99", "100"),
    ("100", "105", "100", "104"),
    ("104", "104", "101", "102"),
    ("105.5", "107", "105.5", "106.5"),
    ("106.5", "108", "106", "107"),
    ("107", "109", "106.5", "108"),
]

BEARISH_CHOCH = [
    *BULLISH_BOS[:7],
    ("102", "103.5", "101", "103"),
    ("103", "104", "100.5", "101"),
    ("100", "101", "96", "97"),
]

BUY_SIDE_SWEEP = [
    *BULLISH_BOS[:7],
    ("104", "106", "103", "104.5"),
    ("104.5", "104.8", "103.5", "104"),
    ("104", "104.7", "103.8", "104.2"),
]


def test_bullish_bos_creates_order_block_and_fair_value_gap() -> None:
    calculator = _calculator()
    window = _window(BULLISH_BOS)

    output = calculator.calculate(
        _delivery(BULLISH_BOS, 9),
        window,
        window[-1].close_at_utc,
        0,
    )

    assert output.direction == "LONG"
    assert output.structure_state == "BULLISH"
    assert output.score >= Decimal("0.20")
    assert output.is_eligible_for_fusion is True
    assert output.structure_events[-1].event_type == "BOS"
    assert output.structure_events[-1].direction == "LONG"
    assert any(zone.direction == "LONG" for zone in output.order_blocks)
    assert any(zone.direction == "LONG" for zone in output.fair_value_gaps)
    assert output.latest_swing_high is not None
    assert output.latest_swing_low is not None
    assert output.data_quality_status == "VALID"


def test_bearish_break_against_bullish_structure_is_choch() -> None:
    calculator = _calculator()
    window = _window(BEARISH_CHOCH)

    output = calculator.calculate(
        _delivery(BEARISH_CHOCH, 9),
        window,
        window[-1].close_at_utc,
        0,
    )

    assert output.direction == "SHORT"
    assert output.structure_state == "BEARISH"
    assert output.structure_events[-1].event_type == "CHOCH"
    assert output.structure_events[-1].direction == "SHORT"
    assert output.structure_events[-1].prior_structure == "BULLISH"
    assert output.is_eligible_for_fusion is True


def test_wick_only_violation_is_liquidity_sweep_not_bos() -> None:
    calculator = _calculator()
    window = _window(BUY_SIDE_SWEEP)

    output = calculator.calculate(
        _delivery(BUY_SIDE_SWEEP, 9),
        window,
        window[-1].close_at_utc,
        0,
    )

    assert output.liquidity_sweeps
    assert output.liquidity_sweeps[-1].sweep_type == "BUY_SIDE_SWEEP"
    assert output.liquidity_sweeps[-1].direction == "SHORT"
    assert all(
        event.event_at_utc != output.liquidity_sweeps[-1].event_at_utc
        for event in output.structure_events
    )


def test_insufficient_window_fails_closed() -> None:
    calculator = _calculator()
    values = BULLISH_BOS[:4]
    window = _window(values)

    output = calculator.calculate(
        _delivery(values, 3),
        window,
        window[-1].close_at_utc,
        0,
    )

    assert output.data_quality_status == "INVALID"
    assert output.is_eligible_for_fusion is False
    assert "INSUFFICIENT_CANDLE_WINDOW" in output.warnings


def test_service_is_idempotent_and_correction_creates_revision() -> None:
    runtime = _runtime()
    service = SmartMoneyConceptsService(
        Settings(
            feature_factory_enabled=True,
            feature_factory_internal_api_key="test-key",
        ),
        store=InMemorySmartMoneyStore(64),
        runtime=runtime,
    )
    deliveries = [_delivery(BULLISH_BOS, index) for index in range(len(BULLISH_BOS))]
    result = None
    for delivery in deliveries:
        result = service.process_candle(
            delivery,
            delivery.envelope.payload.close_at_utc,
        )

    assert result is not None
    assert result.outcome == "CREATED"
    assert result.output is not None
    duplicate = service.process_candle(
        deliveries[-1],
        deliveries[-1].envelope.payload.close_at_utc,
    )
    assert duplicate.outcome == "DUPLICATE"
    assert duplicate.output == result.output

    corrected_values = list(BULLISH_BOS)
    corrected_values[-1] = ("107", "109.2", "106.5", "108.2")
    correction = _delivery(corrected_values, 9, revision=1, message_int=999)
    revised = service.process_candle(
        correction,
        correction.envelope.payload.close_at_utc,
    )

    assert revised.outcome == "REVISED"
    assert revised.output is not None
    assert revised.output.revision == 1
    assert revised.output.output_uid != result.output.output_uid


def _calculator() -> DeterministicSmartMoneyCalculator:
    return DeterministicSmartMoneyCalculator(
        SmartMoneyOptions(
            required_input_count=10,
            maximum_input_count=64,
            swing_left_bars=1,
            swing_right_bars=1,
            break_tolerance_fraction=Decimal("0"),
            minimum_fair_value_gap_fraction=Decimal("0.0001"),
            maximum_output_age_seconds=420,
            fusion_confidence_threshold=Decimal("0.50"),
        )
    )


def _runtime() -> SmartMoneyRuntimeSettings:
    return SmartMoneyRuntimeSettings(
        enabled=True,
        engine_code="THESIS_PULSE_SMART_MONEY_CONCEPTS",
        engine_version="1.0.0",
        policy_version="smart-money-structure-v1.0.0",
        actor="ThesisPulse.AI.SmartMoney",
        required_input_count=10,
        maximum_input_count=64,
        swing_left_bars=1,
        swing_right_bars=1,
        order_block_search_bars=8,
        maximum_zones_per_type=5,
        maximum_zone_age_bars=24,
        break_tolerance_fraction=Decimal("0"),
        minimum_fair_value_gap_fraction=Decimal("0.0001"),
        minimum_valid_input_ratio=Decimal("0.95"),
        maximum_output_age_seconds=420,
        directional_threshold=Decimal("0.20"),
        fusion_confidence_threshold=Decimal("0.50"),
    )


def _window(values: list[tuple[str, str, str, str]]) -> list[SmartMoneyCandle]:
    result = []
    for index, raw in enumerate(values):
        open_at = BASE_TIME + timedelta(minutes=5 * index)
        result.append(
            SmartMoneyCandle(
                candle_id=index + 1,
                source_message_uid=UUID(int=index + 1),
                instrument_key=INSTRUMENT,
                timeframe="5m",
                open_at_utc=open_at,
                close_at_utc=open_at + timedelta(minutes=5),
                open_price=Decimal(raw[0]),
                high_price=Decimal(raw[1]),
                low_price=Decimal(raw[2]),
                close_price=Decimal(raw[3]),
                volume_quantity=Decimal("1000"),
                revision=0,
                received_at_utc=open_at + timedelta(minutes=5),
                quality_status="VALID",
                is_usable_for_new_exposure=True,
            )
        )
    return result


def _delivery(
    values: list[tuple[str, str, str, str]],
    index: int,
    *,
    revision: int = 0,
    message_int: int | None = None,
) -> MarketCandleDeliveryV1:
    raw = values[index]
    open_at = BASE_TIME + timedelta(minutes=5 * index)
    close_at = open_at + timedelta(minutes=5)
    return MarketCandleDeliveryV1(
        stream_position=index + 1,
        envelope=MarketCandleEnvelopeV1(
            metadata=MarketDataMessageMetadataV1(
                message_id=UUID(int=message_int or index + 1),
                event_type="market.candle.published.v1",
                contract_version="1.0",
                occurred_at_utc=close_at + timedelta(seconds=1),
                correlation_id=str(UUID(int=8000)),
                producer="ThesisPulse.MarketData.Service",
                producer_version="1.0.0",
                environment="PAPER",
                configuration_version="market-data-publication-v1.0.0",
            ),
            payload=MarketCandlePublishedV1(
                provider_code="UPSTOX",
                instrument_key=INSTRUMENT,
                timeframe="5m",
                open_at_utc=open_at,
                close_at_utc=close_at,
                open_price=Decimal(raw[0]),
                high_price=Decimal(raw[1]),
                low_price=Decimal(raw[2]),
                close_price=Decimal(raw[3]),
                volume_quantity=Decimal("1000"),
                open_interest=None,
                is_closed=True,
                is_provisional=False,
                revision=revision,
                quality_status="VALID",
                is_usable_for_new_exposure=True,
                received_at_utc=close_at,
                source_version="upstox-v3",
            ),
        ),
    )
