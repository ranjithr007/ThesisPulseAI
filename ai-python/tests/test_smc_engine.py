from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import UUID

from app.contracts.v1.market_data import (
    MarketCandleDeliveryV1,
    MarketCandleEnvelopeV1,
    MarketCandlePublishedV1,
    MarketDataMessageMetadataV1,
)
from app.features.models import CandleInput
from app.smc.calculator import DeterministicSmcCalculator
from app.smc.definitions import SmcOptions
from app.smc.service import SmartMoneyConceptsService
from app.smc.store import InMemorySmcStore
from app.core.settings import Settings

BASE_TIME = datetime(2026, 6, 30, 10, 0, tzinfo=UTC)
INSTRUMENT = "NSE_EQ|INE002A01018"


def test_bullish_break_of_structure_is_deterministic() -> None:
    calculator = DeterministicSmcCalculator(SmcOptions())
    candles = _bos_up_candles()
    delivery = _delivery(candles[-1], message_int=900)

    first = calculator.calculate(delivery, candles, candles[-1].close_at_utc, 0)
    second = calculator.calculate(delivery, candles, candles[-1].close_at_utc, 0)

    assert first == second
    assert first.structure_event == "BOS_UP"
    assert first.direction == "LONG"
    assert first.score >= Decimal("0.20")
    assert first.last_swing_high is not None
    assert first.is_eligible_for_fusion is True
    assert any(item.code == "MARKET_STRUCTURE" for item in first.evidence)


def test_low_liquidity_sweep_supports_long_without_future_candles() -> None:
    calculator = DeterministicSmcCalculator(SmcOptions())
    candles = _sweep_low_candles()
    delivery = _delivery(candles[-1], message_int=901)

    output = calculator.calculate(delivery, candles, candles[-1].close_at_utc, 0)

    assert output.liquidity_event == "SWEEP_LOW"
    assert output.direction == "LONG"
    assert output.swing_low_at_utc is not None
    assert output.swing_low_at_utc < output.as_of_utc


def test_three_candle_imbalance_creates_bullish_fvg_zone() -> None:
    calculator = DeterministicSmcCalculator(SmcOptions(required_input_count=7))
    candles = _fvg_candles()
    delivery = _delivery(candles[-1], message_int=902)

    output = calculator.calculate(delivery, candles, candles[-1].close_at_utc, 0)

    bullish = [item for item in output.zones if item.zone_type == "BULLISH_FVG"]
    assert bullish
    assert all(item.lower_price < item.upper_price for item in bullish)
    assert all(item.formed_at_utc <= output.as_of_utc for item in bullish)


def test_insufficient_history_fails_closed() -> None:
    calculator = DeterministicSmcCalculator(SmcOptions())
    candles = _bos_up_candles()[:6]
    delivery = _delivery(candles[-1], message_int=903)

    output = calculator.calculate(delivery, candles, candles[-1].close_at_utc, 0)

    assert output.data_quality_status == "INVALID"
    assert output.is_eligible_for_fusion is False
    assert "INSUFFICIENT_STRUCTURE_HISTORY" in output.warnings


def test_service_is_idempotent_for_same_source_candle() -> None:
    store = InMemorySmcStore()
    service = SmartMoneyConceptsService(
        Settings(),
        store=store,
        enabled=True,
    )
    results = []
    for index, candle in enumerate(_bos_up_candles(), start=1):
        results.append(
            service.process_candle(
                _delivery(candle, message_int=1000 + index),
                candle.close_at_utc,
            )
        )

    duplicate = service.process_candle(
        _delivery(_bos_up_candles()[-1], message_int=1000 + len(_bos_up_candles())),
        _bos_up_candles()[-1].close_at_utc,
    )

    assert results[-1].output is not None
    assert results[-1].output.structure_event == "BOS_UP"
    assert duplicate.outcome == "DUPLICATE"
    assert duplicate.output == results[-1].output


def _bos_up_candles() -> list[CandleInput]:
    values = [
        (100, 102, 99, 101),
        (101, 103, 100, 102),
        (102, 106, 101, 103),
        (103, 104, 100, 101),
        (101, 103, 98, 100),
        (100, 102, 97, 101),
        (101, 104, 99, 103),
        (103, 105, 101, 104),
        (104, 104.5, 100, 101),
        (101, 103, 96, 99),
        (99, 102, 97, 101),
        (101, 104, 100, 103),
        (103, 108, 102, 107),
    ]
    return [_candle(index, *row) for index, row in enumerate(values)]


def _sweep_low_candles() -> list[CandleInput]:
    values = [
        (100, 102, 99, 101),
        (101, 103, 100, 102),
        (102, 104, 98, 101),
        (101, 102, 96, 97),
        (97, 101, 95, 100),
        (100, 103, 98, 102),
        (102, 104, 99, 103),
        (103, 105, 100, 104),
        (104, 104.5, 99, 100),
        (100, 102, 94, 98),
        (98, 101, 96, 100),
        (100, 103, 97, 102),
        (102, 104, 93, 99),
    ]
    return [_candle(index, *row) for index, row in enumerate(values)]


def _fvg_candles() -> list[CandleInput]:
    values = [
        (100, 101, 99, 100),
        (100, 102, 99.5, 101),
        (103, 104, 103, 103.5),
        (103.5, 105, 103.2, 104),
        (104, 106, 103.5, 105),
        (105, 107, 104, 106),
        (106, 108, 105, 107),
    ]
    return [_candle(index, *row) for index, row in enumerate(values)]


def _candle(index: int, open_price, high, low, close) -> CandleInput:
    open_at = BASE_TIME + timedelta(minutes=index * 5)
    return CandleInput(
        candle_id=index + 1,
        instrument_key=INSTRUMENT,
        timeframe="5m",
        open_at_utc=open_at,
        close_at_utc=open_at + timedelta(minutes=5),
        open_price=Decimal(str(open_price)),
        high_price=Decimal(str(high)),
        low_price=Decimal(str(low)),
        close_price=Decimal(str(close)),
        volume_quantity=Decimal("1000"),
        open_interest=None,
        revision=0,
        received_at_utc=open_at + timedelta(minutes=5),
        quality_status="VALID",
        is_usable_for_new_exposure=True,
    )


def _delivery(candle: CandleInput, message_int: int) -> MarketCandleDeliveryV1:
    return MarketCandleDeliveryV1(
        stream_position=message_int,
        envelope=MarketCandleEnvelopeV1(
            metadata=MarketDataMessageMetadataV1(
                message_id=UUID(int=message_int),
                event_type="market.candle.published.v1",
                contract_version="1.0",
                occurred_at_utc=candle.close_at_utc + timedelta(seconds=1),
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
                open_at_utc=candle.open_at_utc,
                close_at_utc=candle.close_at_utc,
                open_price=candle.open_price,
                high_price=candle.high_price,
                low_price=candle.low_price,
                close_price=candle.close_price,
                volume_quantity=candle.volume_quantity,
                open_interest=None,
                is_closed=True,
                is_provisional=False,
                revision=0,
                quality_status="VALID",
                is_usable_for_new_exposure=True,
                received_at_utc=candle.received_at_utc,
                source_version="upstox-v3",
            ),
        ),
    )
