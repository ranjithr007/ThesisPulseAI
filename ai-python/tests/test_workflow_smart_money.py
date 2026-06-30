from decimal import Decimal
from uuid import UUID

import pytest

from app.contracts.v1.smart_money import (
    SmartMoneyConceptsOutputV1,
    SmartMoneyEvidenceV1,
)
from app.workflow.calculator import (
    FusionReadyEvidenceCalculator,
    FusionReadyEvidenceOptions,
)
from test_workflow_evidence import (
    BASE_TIME,
    INSTRUMENT,
    TIMEFRAMES,
    _confirmation,
    _delivery,
    _directional,
    _feature,
    _regime,
)


def test_eligible_smart_money_output_is_added_as_independent_vote() -> None:
    calculator = FusionReadyEvidenceCalculator(FusionReadyEvidenceOptions())
    directional = {timeframe: _directional(timeframe) for timeframe in TIMEFRAMES}
    regimes = {timeframe: _regime(timeframe) for timeframe in TIMEFRAMES}

    result = calculator.calculate(
        _delivery(),
        _feature(),
        _confirmation(directional, regimes),
        directional,
        regimes,
        BASE_TIME,
        smart_money=_smart_money(),
    )

    votes = [
        item
        for item in result.directional_evidence
        if item.engine_code == "SMART_MONEY_CONCEPTS"
    ]
    assert len(votes) == 1
    assert votes[0].direction == "LONG"
    assert votes[0].score == Decimal("70.00")
    assert votes[0].confidence == Decimal("80.00")


def test_smart_money_cutoff_mismatch_fails_closed() -> None:
    calculator = FusionReadyEvidenceCalculator(FusionReadyEvidenceOptions())
    directional = {timeframe: _directional(timeframe) for timeframe in TIMEFRAMES}
    regimes = {timeframe: _regime(timeframe) for timeframe in TIMEFRAMES}
    mismatched = _smart_money().model_copy(
        update={"as_of_utc": BASE_TIME.replace(minute=BASE_TIME.minute - 5)}
    )

    with pytest.raises(ValueError, match="cutoff"):
        calculator.calculate(
            _delivery(),
            _feature(),
            _confirmation(directional, regimes),
            directional,
            regimes,
            BASE_TIME,
            smart_money=mismatched,
        )


def _smart_money() -> SmartMoneyConceptsOutputV1:
    return SmartMoneyConceptsOutputV1(
        output_uid=UUID(int=900),
        message_uid=UUID(int=901),
        source_candle_message_uid=UUID(int=1),
        instrument_key=INSTRUMENT,
        timeframe="5m",
        as_of_utc=BASE_TIME,
        generated_at_utc=BASE_TIME,
        engine_code="THESIS_PULSE_SMART_MONEY_CONCEPTS",
        engine_version="1.0.0",
        policy_version="smart-money-structure-v1.0.0",
        structure_state="BULLISH",
        direction="LONG",
        score=Decimal("0.70"),
        confidence=Decimal("0.80"),
        latest_swing_high=None,
        latest_swing_low=None,
        structure_events=[],
        liquidity_sweeps=[],
        order_blocks=[],
        fair_value_gaps=[],
        input_count=30,
        required_input_count=30,
        completeness=Decimal("1"),
        valid_input_ratio=Decimal("1"),
        data_quality_status="VALID",
        is_stale=False,
        is_eligible_for_fusion=True,
        revision=0,
        evidence=[
            SmartMoneyEvidenceV1(
                code="STRUCTURE_BREAK",
                message="Bullish structure break confirmed",
                impact="SUPPORTS_LONG",
                weight=Decimal("0.35"),
                contribution=Decimal("0.35"),
            )
        ],
        warnings=["SMC_RULESET_IS_DETERMINISTIC_HEURISTIC"],
    )
