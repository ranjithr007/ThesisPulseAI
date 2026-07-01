from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from uuid import UUID

import pytest

from app.contracts.v1.option_chain import (
    OptionChainEvidenceV1,
    OptionChainExpiryMetricsV1,
    OptionChainIntelligenceOutputV1,
)
from app.contracts.v1.workflow import (
    FusionDirectionalEvidenceV1,
    FusionReadyEvidenceV1,
    FusionRegimeEvidenceV1,
    FusionTimeframeConfirmationV1,
    FusionTradeProposalV1,
    FusionTradeTargetProposalV1,
)
from app.workflow.option_chain import append_option_chain_evidence

BASE_TIME = datetime(2026, 7, 1, 10, 0, tzinfo=UTC)
INSTRUMENT = "NSE_INDEX|Nifty 50"


def test_eligible_option_chain_is_added_as_one_independent_vote() -> None:
    base = _base_evidence()
    output = _option_chain_output()

    first = append_option_chain_evidence(
        base,
        output,
        INSTRUMENT,
        BASE_TIME,
        maximum_age_seconds=120,
    )
    second = append_option_chain_evidence(
        base,
        output,
        INSTRUMENT,
        BASE_TIME,
        maximum_age_seconds=120,
    )

    assert first.evidence_uid == second.evidence_uid
    assert first.evidence_uid != base.evidence_uid
    assert len(first.directional_evidence) == 2
    vote = first.directional_evidence[-1]
    assert vote.engine_code == "OPTION_CHAIN"
    assert vote.timeframe == "OPTION_CHAIN"
    assert vote.direction == "LONG"
    assert vote.score == Decimal("72.00")
    assert vote.confidence == Decimal("81.00")
    assert vote.output_uid == output.output_uid
    assert vote.reasons == ["Put-side positioning supports the long side"]


def test_ineligible_option_chain_adds_warnings_but_no_vote() -> None:
    base = _base_evidence()
    output = _option_chain_output().model_copy(
        update={
            "direction": "NEUTRAL",
            "is_eligible_for_fusion": False,
            "warnings": ["OPTION_CHAIN_FUSION_CONFIDENCE_BELOW_THRESHOLD"],
        }
    )

    result = append_option_chain_evidence(
        base,
        output,
        INSTRUMENT,
        BASE_TIME,
        maximum_age_seconds=120,
    )

    assert result.evidence_uid == base.evidence_uid
    assert len(result.directional_evidence) == 1
    assert "OPTION_CHAIN_NOT_ELIGIBLE_FOR_FUSION" in result.warnings
    assert "OPTION_CHAIN_FUSION_CONFIDENCE_BELOW_THRESHOLD" in result.warnings


def test_stale_option_chain_cannot_enter_fusion() -> None:
    base = _base_evidence()
    output = _option_chain_output().model_copy(
        update={
            "as_of_utc": BASE_TIME - timedelta(minutes=5),
            "generated_at_utc": BASE_TIME - timedelta(minutes=5) + timedelta(seconds=1),
        }
    )

    result = append_option_chain_evidence(
        base,
        output,
        INSTRUMENT,
        BASE_TIME,
        maximum_age_seconds=120,
    )

    assert len(result.directional_evidence) == 1
    assert "OPTION_CHAIN_WORKFLOW_STALE" in result.warnings


def test_future_knowledge_cutoff_fails_closed() -> None:
    output = _option_chain_output().model_copy(
        update={"generated_at_utc": BASE_TIME + timedelta(seconds=1)}
    )

    with pytest.raises(ValueError, match="knowledge cutoff"):
        append_option_chain_evidence(
            _base_evidence(),
            output,
            INSTRUMENT,
            BASE_TIME,
            maximum_age_seconds=120,
        )


def test_authority_drift_fails_closed() -> None:
    output = _option_chain_output().model_copy(update={"execution_authority": True})

    with pytest.raises(ValueError, match="authority drift"):
        append_option_chain_evidence(
            _base_evidence(),
            output,
            INSTRUMENT,
            BASE_TIME,
            maximum_age_seconds=120,
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
            reference_price=Decimal("25000"),
            minimum_acceptable_price=Decimal("24990"),
            maximum_acceptable_price=Decimal("25010"),
            stop_loss_price=Decimal("24900"),
            targets=[
                FusionTradeTargetProposalV1(
                    sequence=1,
                    price=Decimal("25200"),
                    quantity_fraction=Decimal("1"),
                )
            ],
            maximum_slippage_fraction=Decimal("0.001"),
            proposal_policy_version="atr-trade-proposal-v1.0.0",
        ),
        is_eligible_for_workflow=True,
        warnings=[],
    )


def _option_chain_output() -> OptionChainIntelligenceOutputV1:
    observed = BASE_TIME - timedelta(seconds=30)
    expiry = date(2026, 7, 30)
    return OptionChainIntelligenceOutputV1(
        output_uid=UUID(int=100),
        message_uid=UUID(int=101),
        source_snapshot_uids=[UUID(int=102)],
        underlying_instrument_key=INSTRUMENT,
        as_of_utc=observed,
        generated_at_utc=observed + timedelta(seconds=1),
        engine_code="THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE",
        engine_version="1.0.0",
        policy_version="option-chain-intelligence-v1.0.0",
        direction="LONG",
        score=Decimal("0.72"),
        confidence=Decimal("0.81"),
        expiry_metrics=[
            OptionChainExpiryMetricsV1(
                snapshot_uid=UUID(int=102),
                expiry_date=expiry,
                underlying_price=Decimal("25000"),
                call_open_interest=Decimal("1000"),
                put_open_interest=Decimal("1300"),
                pcr_open_interest=Decimal("1.30"),
                call_volume=Decimal("800"),
                put_volume=Decimal("1000"),
                pcr_volume=Decimal("1.25"),
                call_walls=[],
                put_walls=[],
                oi_flows=[],
                max_pain_strike=Decimal("25000"),
                max_pain_distance_fraction=Decimal("0"),
                max_pain_magnet_strength=Decimal("1"),
                max_pain_curve=[],
                atm_call_implied_volatility=Decimal("0.20"),
                atm_put_implied_volatility=Decimal("0.22"),
                atm_put_call_skew=Decimal("0.02"),
                rr25_skew=None,
                accepted_contract_count=40,
                accepted_strike_count=20,
                component_coverage=Decimal("0.90"),
                warnings=[],
            )
        ],
        iv_term_structure=[],
        near_to_next_iv_slope=None,
        near_to_far_iv_slope=None,
        iv_term_structure_state="INSUFFICIENT",
        input_snapshot_count=1,
        accepted_contract_count=40,
        accepted_strike_count=20,
        component_coverage=Decimal("0.90"),
        data_quality_status="VALID",
        is_stale=False,
        is_eligible_for_fusion=True,
        revision=0,
        evidence=[
            OptionChainEvidenceV1(
                code="PCR_OI",
                message="Put-side positioning supports the long side",
                impact="SUPPORTS_LONG",
                raw_value=Decimal("1.30"),
                normalized_value=Decimal("0.72"),
                weight=Decimal("0.15"),
                contribution=Decimal("0.108"),
                confidence=Decimal("0.90"),
                warnings=[],
            )
        ],
        warnings=[],
        selection_authority=False,
        execution_authority=False,
    )
