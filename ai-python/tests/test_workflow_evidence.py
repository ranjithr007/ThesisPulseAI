from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import UUID

import pytest

from app.contracts.v1.confirmation import (
    MultiTimeframeConfirmationOutputV1,
    TimeframeConfirmationV1,
)
from app.contracts.v1.directional import (
    DirectionalEngineOutputV1,
    DirectionalEvidenceV1,
)
from app.contracts.v1.market_data import (
    FeatureSnapshotV1,
    FeatureValueV1,
    MarketCandleDeliveryV1,
    MarketCandleEnvelopeV1,
    MarketCandlePublishedV1,
    MarketDataMessageMetadataV1,
)
from app.contracts.v1.regime import MarketRegimeOutputV1, RegimeEvidenceV1
from app.workflow.calculator import (
    FusionReadyEvidenceCalculator,
    FusionReadyEvidenceOptions,
)

BASE_TIME = datetime(2026, 6, 30, 9, 45, tzinfo=UTC)
INSTRUMENT = "NSE_EQ|INE002A01018"
TIMEFRAMES = ("5m", "15m", "1h")


def test_long_evidence_uses_exact_candle_close_and_point_in_time_atr() -> None:
    calculator = FusionReadyEvidenceCalculator(FusionReadyEvidenceOptions())
    delivery = _delivery()
    feature = _feature()
    directional = {timeframe: _directional(timeframe) for timeframe in TIMEFRAMES}
    regimes = {timeframe: _regime(timeframe) for timeframe in TIMEFRAMES}
    confirmation = _confirmation(directional, regimes)

    first = calculator.calculate(
        delivery,
        feature,
        confirmation,
        directional,
        regimes,
        BASE_TIME,
    )
    second = calculator.calculate(
        delivery,
        feature,
        confirmation,
        directional,
        regimes,
        BASE_TIME,
    )

    assert first == second
    assert first.source_candle_message_uid == delivery.envelope.metadata.message_id
    assert first.as_of_utc == delivery.envelope.payload.close_at_utc
    assert first.trade_proposal.direction == "LONG"
    assert first.trade_proposal.reference_price == Decimal("100.000000")
    assert first.trade_proposal.stop_loss_price == Decimal("97.000000")
    assert first.trade_proposal.targets[0].price == Decimal("106.000000")
    assert first.trade_proposal.minimum_acceptable_price == Decimal("99.500000")
    assert first.trade_proposal.maximum_acceptable_price == Decimal("100.500000")
    assert len(first.directional_evidence) == 6
    assert {item.engine_code for item in first.directional_evidence} == {
        "TREND",
        "MOMENTUM",
    }
    assert {item.timeframe for item in first.timeframe_confirmations} == set(
        TIMEFRAMES
    )


def test_non_primary_candle_cannot_trigger_workflow_evidence() -> None:
    calculator = FusionReadyEvidenceCalculator(FusionReadyEvidenceOptions())
    delivery = _delivery(timeframe="15m")
    directional = {timeframe: _directional(timeframe) for timeframe in TIMEFRAMES}
    regimes = {timeframe: _regime(timeframe) for timeframe in TIMEFRAMES}

    with pytest.raises(ValueError, match="closed 5m candle"):
        calculator.calculate(
            delivery,
            _feature(),
            _confirmation(directional, regimes),
            directional,
            regimes,
            BASE_TIME,
        )


def test_confirmation_lineage_mismatch_fails_closed() -> None:
    calculator = FusionReadyEvidenceCalculator(FusionReadyEvidenceOptions())
    directional = {timeframe: _directional(timeframe) for timeframe in TIMEFRAMES}
    regimes = {timeframe: _regime(timeframe) for timeframe in TIMEFRAMES}
    confirmation = _confirmation(directional, regimes)
    confirmation = confirmation.model_copy(
        update={
            "timeframe_confirmations": [
                confirmation.timeframe_confirmations[0].model_copy(
                    update={"directional_output_uid": UUID(int=9999)}
                ),
                *confirmation.timeframe_confirmations[1:],
            ]
        }
    )

    with pytest.raises(ValueError, match="Directional lineage mismatch"):
        calculator.calculate(
            _delivery(),
            _feature(),
            confirmation,
            directional,
            regimes,
            BASE_TIME,
        )


def _delivery(timeframe: str = "5m") -> MarketCandleDeliveryV1:
    duration = {
        "5m": timedelta(minutes=5),
        "15m": timedelta(minutes=15),
    }[timeframe]
    return MarketCandleDeliveryV1(
        stream_position=100,
        envelope=MarketCandleEnvelopeV1(
            metadata=MarketDataMessageMetadataV1(
                message_id=UUID(int=1),
                event_type="market.candle.published.v1",
                contract_version="1.0",
                occurred_at_utc=BASE_TIME,
                correlation_id=str(UUID(int=2)),
                producer="ThesisPulse.MarketData.Service",
                producer_version="1.0.0",
                environment="PAPER",
                configuration_version="market-data-publication-v1.0.0",
            ),
            payload=MarketCandlePublishedV1(
                provider_code="UPSTOX",
                instrument_key=INSTRUMENT,
                timeframe=timeframe,
                open_at_utc=BASE_TIME - duration,
                close_at_utc=BASE_TIME,
                open_price=Decimal("99"),
                high_price=Decimal("101"),
                low_price=Decimal("98"),
                close_price=Decimal("100"),
                volume_quantity=Decimal("10000"),
                is_closed=True,
                is_provisional=False,
                revision=0,
                quality_status="VALID",
                is_usable_for_new_exposure=True,
                received_at_utc=BASE_TIME,
                source_version="1.0.0",
            ),
        ),
    )


def _feature() -> FeatureSnapshotV1:
    return FeatureSnapshotV1(
        snapshot_uid=UUID(int=10),
        message_uid=UUID(int=1),
        instrument_key=INSTRUMENT,
        timeframe="5m",
        as_of_utc=BASE_TIME,
        data_cutoff_utc=BASE_TIME,
        generated_at_utc=BASE_TIME,
        feature_set_version="feature-set-v1.0.0",
        revision=0,
        input_count=21,
        required_input_count=21,
        completeness=Decimal("1"),
        data_quality_status="VALID",
        freshness_milliseconds=0,
        is_stale=False,
        is_eligible_for_engines=True,
        features=[FeatureValueV1(name="atr_14", version="1.0.0", value=Decimal("2"))],
    )


def _directional(timeframe: str) -> DirectionalEngineOutputV1:
    offset = {"5m": 100, "15m": 200, "1h": 300}[timeframe]
    evidence = [
        DirectionalEvidenceV1(
            code="TREND_SCORE",
            message="Trend supports long",
            impact="SUPPORTS_LONG",
            weight=Decimal("0.25"),
            contribution=Decimal("0.80"),
        ),
        DirectionalEvidenceV1(
            code="TREND_SPREAD",
            message="Spread supports long",
            impact="SUPPORTS_LONG",
            weight=Decimal("0.20"),
            contribution=Decimal("0.60"),
        ),
        DirectionalEvidenceV1(
            code="MOMENTUM",
            message="Momentum supports long",
            impact="SUPPORTS_LONG",
            weight=Decimal("0.20"),
            contribution=Decimal("0.70"),
        ),
        DirectionalEvidenceV1(
            code="CLOSE_LOCATION",
            message="Close location supports long",
            impact="SUPPORTS_LONG",
            weight=Decimal("0.10"),
            contribution=Decimal("0.50"),
        ),
        DirectionalEvidenceV1(
            code="SHORT_RETURN",
            message="Return supports long",
            impact="SUPPORTS_LONG",
            weight=Decimal("0.10"),
            contribution=Decimal("0.60"),
        ),
        DirectionalEvidenceV1(
            code="VOLUME_CONFIRMATION",
            message="Volume supports long",
            impact="SUPPORTS_LONG",
            weight=Decimal("0.15"),
            contribution=Decimal("0.50"),
        ),
    ]
    return DirectionalEngineOutputV1(
        output_uid=UUID(int=offset + 1),
        message_uid=UUID(int=offset + 2),
        source_feature_snapshot_uid=UUID(int=offset + 3),
        instrument_key=INSTRUMENT,
        timeframe=timeframe,
        as_of_utc=BASE_TIME,
        generated_at_utc=BASE_TIME,
        engine_code="THESIS_PULSE_TECHNICAL_DIRECTION",
        engine_version="1.0.0",
        policy_version="technical-direction-v1.0.0",
        feature_set_version="feature-set-v1.0.0",
        direction="STRONG_LONG",
        score=Decimal("0.8"),
        confidence=Decimal("0.85"),
        data_quality_status="VALID",
        is_stale=False,
        is_eligible_for_fusion=True,
        revision=0,
        evidence=evidence,
    )


def _regime(timeframe: str) -> MarketRegimeOutputV1:
    offset = {"5m": 400, "15m": 500, "1h": 600}[timeframe]
    return MarketRegimeOutputV1(
        output_uid=UUID(int=offset + 1),
        message_uid=UUID(int=offset + 2),
        source_feature_snapshot_uid=UUID(int=offset + 3),
        instrument_key=INSTRUMENT,
        timeframe=timeframe,
        as_of_utc=BASE_TIME,
        generated_at_utc=BASE_TIME,
        engine_code="THESIS_PULSE_MARKET_REGIME",
        engine_version="1.0.0",
        policy_version="market-regime-v1.0.0",
        feature_set_version="feature-set-v1.0.0",
        structure_regime="TRENDING_UP",
        volatility_regime="NORMAL",
        direction_bias="LONG",
        score=Decimal("0.7"),
        confidence=Decimal("0.8"),
        trend_strength=Decimal("0.8"),
        range_score=Decimal("0.1"),
        transition_score=Decimal("0.1"),
        volatility_score=Decimal("0.5"),
        data_quality_status="VALID",
        is_stale=False,
        is_eligible_for_fusion=True,
        revision=0,
        evidence=[
            RegimeEvidenceV1(
                code="TREND",
                message="Regime supports long",
                impact="SUPPORTS_LONG",
                weight=Decimal("1"),
                contribution=Decimal("0.7"),
            )
        ],
    )


def _confirmation(
    directional: dict[str, DirectionalEngineOutputV1],
    regimes: dict[str, MarketRegimeOutputV1],
) -> MultiTimeframeConfirmationOutputV1:
    confirmations = [
        TimeframeConfirmationV1(
            timeframe=timeframe,
            directional_output_uid=directional[timeframe].output_uid,
            regime_output_uid=regimes[timeframe].output_uid,
            direction="STRONG_LONG",
            directional_score=Decimal("0.8"),
            regime_bias="LONG",
            structure_regime="TRENDING_UP",
            volatility_regime="NORMAL",
            effective_weight=Decimal("0.3"),
            signed_contribution=Decimal("0.24"),
            agrees_with_primary=True,
            is_fresh=True,
        )
        for timeframe in TIMEFRAMES
    ]
    return MultiTimeframeConfirmationOutputV1(
        output_uid=UUID(int=700),
        message_uid=UUID(int=701),
        instrument_key=INSTRUMENT,
        primary_timeframe="5m",
        as_of_utc=BASE_TIME,
        generated_at_utc=BASE_TIME,
        engine_code="THESIS_PULSE_MULTI_TIMEFRAME_CONFIRMATION",
        engine_version="1.0.0",
        policy_version="multi-timeframe-confirmation-v1.0.0",
        direction="STRONG_LONG",
        score=Decimal("0.8"),
        confidence=Decimal("0.9"),
        alignment_score=Decimal("1"),
        contradiction_score=Decimal("0"),
        coverage=Decimal("0.75"),
        required_timeframes_present=True,
        data_quality_status="VALID",
        is_stale=False,
        is_eligible_for_fusion=True,
        revision=0,
        timeframe_confirmations=confirmations,
        evidence=[],
    )
