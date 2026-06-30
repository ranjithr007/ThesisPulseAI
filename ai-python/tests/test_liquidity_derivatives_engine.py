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
from app.liquidity_derivatives.calculator import (
    DeterministicLiquidityDerivativesCalculator,
)
from app.liquidity_derivatives.config import (
    LiquidityDerivativesRuntimeSettings,
)
from app.liquidity_derivatives.definitions import LiquidityDerivativesOptions
from app.liquidity_derivatives.models import LiquidityDerivativesCandle
from app.liquidity_derivatives.service import (
    LiquidityDerivativesContextService,
)
from app.liquidity_derivatives.store import InMemoryLiquidityDerivativesStore

BASE_TIME = datetime(2026, 6, 30, 9, 15, tzinfo=UTC)
INSTRUMENT = "NSE_FO|NIFTY26JULFUT"

LONG_VALUES = [
    ("100", "101", "99", "100"),
    ("100", "105", "100", "104"),
    ("104", "104.5", "98", "99"),
    ("99", "104", "95", "96"),
    ("96", "102", "98", "100"),
    ("100", "105.05", "100", "104"),
    ("104", "104.3", "98", "99"),
    ("99", "103", "95.05", "96"),
    ("96", "102", "98", "100"),
    ("100", "104", "99", "103"),
    ("103", "103.5", "99", "102"),
    ("102", "104", "100", "103.5"),
]

SHORT_VALUES = [
    ("104", "105", "103", "104"),
    ("104", "105", "100", "101"),
    ("101", "104", "99", "103"),
    ("103", "105", "100", "101"),
    ("101", "103", "99", "102"),
    ("102", "105.05", "100", "101"),
    ("101", "103", "99", "102"),
    ("102", "104", "95.05", "96"),
    ("96", "101", "95", "100"),
    ("100", "101", "96", "98"),
    ("98", "99", "95", "96"),
    ("96", "97", "93", "94"),
]


def test_long_buildup_and_liquidity_clusters_create_long_output() -> None:
    calculator = _calculator()
    window = _window(LONG_VALUES, oi_direction=1)

    output = calculator.calculate(
        _delivery(LONG_VALUES, 11, open_interest=Decimal("11100")),
        window,
        window[-1].close_at_utc,
        0,
    )

    assert output.derivatives_state == "LONG_BUILDUP"
    assert output.derivatives_score == Decimal("1.000000")
    assert output.direction == "LONG"
    assert output.score >= Decimal("0.20")
    assert output.nearest_buy_side_pool is not None
    assert output.nearest_sell_side_pool is not None
    assert any(pool.touch_count >= 2 for pool in output.liquidity_pools)
    assert output.is_eligible_for_fusion is True
    assert output.data_quality_status == "VALID"
    assert "OPTION_CHAIN_CONTEXT_UNAVAILABLE_V1" in output.warnings
    assert "FUTURES_BASIS_UNAVAILABLE_V1" in output.warnings


def test_short_buildup_creates_short_output() -> None:
    calculator = _calculator()
    window = _window(SHORT_VALUES, oi_direction=1)

    output = calculator.calculate(
        _delivery(SHORT_VALUES, 11, open_interest=Decimal("11100")),
        window,
        window[-1].close_at_utc,
        0,
    )

    assert output.derivatives_state == "SHORT_BUILDUP"
    assert output.derivatives_score == Decimal("-1.000000")
    assert output.direction == "SHORT"
    assert output.score <= Decimal("-0.20")
    assert output.is_eligible_for_fusion is True


def test_missing_open_interest_is_explicit_and_contributes_zero() -> None:
    calculator = _calculator()
    window = _window(LONG_VALUES, oi_direction=0, include_oi=False)

    output = calculator.calculate(
        _delivery(LONG_VALUES, 11, open_interest=None),
        window,
        window[-1].close_at_utc,
        0,
    )

    assert output.derivatives_state == "NOT_AVAILABLE"
    assert output.derivatives_score == Decimal("0.000000")
    assert output.open_interest_change_fraction is None
    assert "OPEN_INTEREST_CONTEXT_UNAVAILABLE" in output.warnings


def test_insufficient_window_fails_closed() -> None:
    calculator = _calculator()
    values = LONG_VALUES[:5]
    window = _window(values, oi_direction=1)

    output = calculator.calculate(
        _delivery(values, 4, open_interest=Decimal("10400")),
        window,
        window[-1].close_at_utc,
        0,
    )

    assert output.data_quality_status == "INVALID"
    assert output.is_eligible_for_fusion is False
    assert "INSUFFICIENT_CANDLE_WINDOW" in output.warnings


def test_service_is_idempotent_and_correction_creates_revision() -> None:
    runtime = _runtime()
    service = LiquidityDerivativesContextService(
        Settings(
            feature_factory_enabled=True,
            feature_factory_internal_api_key="test-key",
        ),
        store=InMemoryLiquidityDerivativesStore(64),
        runtime=runtime,
    )
    deliveries = [
        _delivery(
            LONG_VALUES,
            index,
            open_interest=Decimal("10000") + Decimal(index) * Decimal("100"),
        )
        for index in range(len(LONG_VALUES))
    ]
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

    corrected = list(LONG_VALUES)
    corrected[-1] = ("102", "104.2", "100", "103.8")
    correction = _delivery(
        corrected,
        11,
        open_interest=Decimal("11200"),
        revision=1,
        message_int=999,
    )
    revised = service.process_candle(
        correction,
        correction.envelope.payload.close_at_utc,
    )

    assert revised.outcome == "REVISED"
    assert revised.output is not None
    assert revised.output.revision == 1
    assert revised.output.output_uid != result.output.output_uid


def _calculator() -> DeterministicLiquidityDerivativesCalculator:
    return DeterministicLiquidityDerivativesCalculator(
        LiquidityDerivativesOptions(
            required_input_count=10,
            maximum_input_count=64,
            swing_left_bars=1,
            swing_right_bars=1,
            pool_cluster_tolerance_fraction=Decimal("0.002"),
            pool_half_width_fraction=Decimal("0.0005"),
            derivatives_lookback_bars=6,
            minimum_price_change_fraction=Decimal("0.0001"),
            minimum_open_interest_change_fraction=Decimal("0.0001"),
            fusion_confidence_threshold=Decimal("0.50"),
        )
    )


def _runtime() -> LiquidityDerivativesRuntimeSettings:
    return LiquidityDerivativesRuntimeSettings(
        enabled=True,
        engine_code="THESIS_PULSE_LIQUIDITY_DERIVATIVES_CONTEXT",
        engine_version="1.0.0",
        policy_version="liquidity-derivatives-context-v1.0.0",
        actor="ThesisPulse.AI.LiquidityDerivatives",
        required_input_count=10,
        maximum_input_count=64,
        swing_left_bars=1,
        swing_right_bars=1,
        pool_cluster_tolerance_fraction=Decimal("0.002"),
        pool_half_width_fraction=Decimal("0.0005"),
        maximum_pools_per_side=5,
        derivatives_lookback_bars=6,
        minimum_price_change_fraction=Decimal("0.0001"),
        minimum_open_interest_change_fraction=Decimal("0.0001"),
        minimum_valid_input_ratio=Decimal("0.95"),
        maximum_output_age_seconds=420,
        directional_threshold=Decimal("0.20"),
        fusion_confidence_threshold=Decimal("0.50"),
    )


def _window(
    values: list[tuple[str, str, str, str]],
    *,
    oi_direction: int,
    include_oi: bool = True,
) -> list[LiquidityDerivativesCandle]:
    result = []
    for index, raw in enumerate(values):
        open_at = BASE_TIME + timedelta(minutes=5 * index)
        open_interest = None
        if include_oi:
            open_interest = Decimal("10000") + (
                Decimal(index) * Decimal("100") * Decimal(oi_direction)
            )
        result.append(
            LiquidityDerivativesCandle(
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
                open_interest=open_interest,
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
    open_interest: Decimal | None,
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
                correlation_id=str(UUID(int=8100)),
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
                open_interest=open_interest,
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
