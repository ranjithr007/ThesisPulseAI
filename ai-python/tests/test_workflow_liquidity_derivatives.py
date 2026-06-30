from datetime import UTC, datetime
from decimal import Decimal
from uuid import UUID

import pytest

from app.contracts.v1.liquidity_derivatives import (
    LiquidityDerivativesContextOutputV1,
    LiquidityDerivativesEvidenceV1,
)
from app.contracts.v1.workflow import (
    FusionDirectionalEvidenceV1,
    FusionReadyEvidenceV1,
    FusionRegimeEvidenceV1,
    FusionTimeframeConfirmationV1,
    FusionTradeProposalV1,
    FusionTradeTargetProposalV1,
)
from app.features.service import FeatureFactoryService

BASE_TIME = datetime(2026, 6, 30, 10, 0, tzinfo=UTC)
INSTRUMENT = "NSE_FO|NIFTY26JULFUT"


def test_eligible_context_is_added_as_independent_vote() -> None:
    base = _base_evidence()
    output = _context_output()

    result = FeatureFactoryService._append_liquidity_derivatives_evidence(
        base,
        output,
        INSTRUMENT,
        BASE_TIME,
    )

    assert result.evidence_uid != base.evidence_uid
    assert len(result.directional_evidence) == 2
    vote = result.directional_evidence[-1]
    assert vote.engine_code == "LIQUIDITY_DERIVATIVES_CONTEXT"
    assert vote.direction == "LONG"
    assert vote.score == Decimal("70.00")
    assert vote.confidence == Decimal("80.00")
    assert "OPTION_CHAIN_CONTEXT_UNAVAILABLE_V1" in result.warnings


def test_ineligible_context_adds_warnings_but_no_vote() -> None:
    base = _base_evidence()
    output = _context_output().model_copy(
        update={"is_eligible_for_fusion": False, "direction": "NEUTRAL"}
    )

    result = FeatureFactoryService._append_liquidity_derivatives_evidence(
        base,
        output,
        INSTRUMENT,
        BASE_TIME,
    )

    assert result.evidence_uid == base.evidence_uid
    assert len(result.directional_evidence) == 1
    assert "OPTION_CHAIN_CONTEXT_UNAVAILABLE_V1" in result.warnings


def test_context_cutoff_mismatch_fails_closed() -> None:
    mismatched = _context_output().model_copy(
        update={"as_of_utc": BASE_TIME.replace(minute=55)}
    )

    with pytest.raises(ValueError, match="cutoff"):
        FeatureFactoryService._append_liquidity_derivatives_evidence(
            _base_evidence(),
            mismatched,
            INSTRUMENT,
            BASE_TIME,
        )


def _base_evidence() -> FusionReadyEvidenceV1:
    return FusionReadyEvidenceV1(
        evidence_uid=UUID(int=1),
        source_candle_message_uid=UUID(int=2),
        confirmation_output_uid=UUID(int=3),
        confirmation_message_uid=UUID(int=4),
        correlation_id=str(UUID(int=5)),
        instrument_key=INSTRUMENT,
        primary_timeframe="5m",
        as_of_utc=BASE_TIME,
        generated_at_utc=BASE_TIME,
        weight_configuration_version="fusion-weights-v1.0.0",
        directional_evidence=[
            FusionDirectionalEvidenceV1(
                output_uid=UUID(int=6),
                engine_code="TREND",
                engine_version="1.0.0",
                timeframe="5m",
                direction="LONG",
                score=Decimal("60"),
                confidence=Decimal("70"),
                observed_at_utc=BASE_TIME,
                reasons=["Trend supports long"],
            )
        ],
        regime=FusionRegimeEvidenceV1(
            output_uid=UUID(int=7),
            regime_code="TRENDING_NORMAL",
            engine_version="1.0.0",
            timeframe="5m",
            directional_bias="LONG",
            confidence=Decimal("70"),
            observed_at_utc=BASE_TIME,
            reasons=["Trending regime"],
        ),
        timeframe_confirmations=[
            FusionTimeframeConfirmationV1(
                timeframe="5m",
                directional_output_uid=UUID(int=8),
                regime_output_uid=UUID(int=9),
                direction="LONG",
                score=Decimal("60"),
                confidence=Decimal("70"),
                is_closed_candle=True,
                observed_at_utc=BASE_TIME,
                reasons=["Primary confirmation"],
            )
        ],
        trade_proposal=FusionTradeProposalV1(
            direction="LONG",
            reference_price=Decimal("100"),
            minimum_acceptable_price=Decimal("99.9"),
            maximum_acceptable_price=Decimal("100.1"),
            stop_loss_price=Decimal("98"),
            targets=[
                FusionTradeTargetProposalV1(
                    sequence=1,
                    price=Decimal("104"),
                    quantity_fraction=Decimal("1"),
                )
            ],
            maximum_slippage_fraction=Decimal("0.001"),
            proposal_policy_version="atr-trade-proposal-v1.0.0",
        ),
        is_eligible_for_workflow=True,
        warnings=[],
    )


def _context_output() -> LiquidityDerivativesContextOutputV1:
    return LiquidityDerivativesContextOutputV1(
        output_uid=UUID(int=100),
        message_uid=UUID(int=101),
        source_candle_message_uid=UUID(int=2),
        instrument_key=INSTRUMENT,
        timeframe="5m",
        as_of_utc=BASE_TIME,
        generated_at_utc=BASE_TIME,
        engine_code="THESIS_PULSE_LIQUIDITY_DERIVATIVES_CONTEXT",
        engine_version="1.0.0",
        policy_version="liquidity-derivatives-context-v1.0.0",
        direction="LONG",
        score=Decimal("0.70"),
        confidence=Decimal("0.80"),
        current_price=Decimal("100"),
        range_low=Decimal("95"),
        range_high=Decimal("105"),
        liquidity_attraction_score=Decimal("0.50"),
        range_location_score=Decimal("0.10"),
        derivatives_score=Decimal("1.00"),
        derivatives_state="LONG_BUILDUP",
        price_change_fraction=Decimal("0.01"),
        open_interest_start=Decimal("10000"),
        open_interest_end=Decimal("10500"),
        open_interest_change_fraction=Decimal("0.05"),
        nearest_buy_side_pool=None,
        nearest_sell_side_pool=None,
        liquidity_pools=[],
        input_count=30,
        required_input_count=30,
        completeness=Decimal("1"),
        valid_input_ratio=Decimal("1"),
        data_quality_status="VALID",
        is_stale=False,
        is_eligible_for_fusion=True,
        revision=0,
        evidence=[
            LiquidityDerivativesEvidenceV1(
                code="OPEN_INTEREST_CONTEXT",
                message="Long build-up confirmed",
                impact="SUPPORTS_LONG",
                weight=Decimal("0.50"),
                contribution=Decimal("0.50"),
            )
        ],
        warnings=["OPTION_CHAIN_CONTEXT_UNAVAILABLE_V1"],
    )
