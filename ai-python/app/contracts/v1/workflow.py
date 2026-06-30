from datetime import datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class WorkflowContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class FusionDirectionalEvidenceV1(WorkflowContractModel):
    output_uid: UUID
    engine_code: Literal[
        "TREND",
        "MOMENTUM",
        "ORDER_FLOW",
        "SMART_MONEY_CONCEPTS",
        "LIQUIDITY_DERIVATIVES_CONTEXT",
    ]
    engine_version: str = Field(min_length=1, max_length=50)
    timeframe: Literal["1m", "5m", "15m", "1h", "1d"]
    direction: Literal["LONG", "NEUTRAL", "SHORT"]
    score: Decimal = Field(ge=0, le=100)
    confidence: Decimal = Field(ge=0, le=100)
    observed_at_utc: datetime
    reasons: list[str] = Field(default_factory=list)


class FusionRegimeEvidenceV1(WorkflowContractModel):
    output_uid: UUID
    regime_code: str = Field(min_length=1, max_length=100)
    engine_version: str = Field(min_length=1, max_length=50)
    timeframe: Literal["5m"]
    directional_bias: Literal["LONG", "NEUTRAL", "SHORT"]
    confidence: Decimal = Field(ge=0, le=100)
    observed_at_utc: datetime
    reasons: list[str] = Field(default_factory=list)


class FusionTimeframeConfirmationV1(WorkflowContractModel):
    timeframe: Literal["1m", "5m", "15m", "1h", "1d"]
    directional_output_uid: UUID
    regime_output_uid: UUID
    direction: Literal["LONG", "NEUTRAL", "SHORT"]
    score: Decimal = Field(ge=0, le=100)
    confidence: Decimal = Field(ge=0, le=100)
    is_closed_candle: bool
    observed_at_utc: datetime
    reasons: list[str] = Field(default_factory=list)


class FusionTradeTargetProposalV1(WorkflowContractModel):
    sequence: int = Field(ge=1)
    price: Decimal = Field(gt=0)
    quantity_fraction: Decimal = Field(gt=0, le=1)


class FusionTradeProposalV1(WorkflowContractModel):
    direction: Literal["LONG", "SHORT"]
    reference_price: Decimal = Field(gt=0)
    minimum_acceptable_price: Decimal = Field(gt=0)
    maximum_acceptable_price: Decimal = Field(gt=0)
    stop_loss_price: Decimal = Field(gt=0)
    targets: list[FusionTradeTargetProposalV1]
    maximum_slippage_fraction: Decimal = Field(ge=0, le=1)
    proposal_policy_version: str = Field(min_length=1, max_length=100)


class FusionReadyEvidenceV1(WorkflowContractModel):
    evidence_uid: UUID
    source_candle_message_uid: UUID
    confirmation_output_uid: UUID
    confirmation_message_uid: UUID
    correlation_id: str = Field(min_length=1, max_length=128)
    instrument_key: str = Field(min_length=1, max_length=200)
    primary_timeframe: Literal["5m"]
    as_of_utc: datetime
    generated_at_utc: datetime
    weight_configuration_version: str = Field(min_length=1, max_length=100)
    directional_evidence: list[FusionDirectionalEvidenceV1]
    regime: FusionRegimeEvidenceV1
    timeframe_confirmations: list[FusionTimeframeConfirmationV1]
    trade_proposal: FusionTradeProposalV1
    is_eligible_for_workflow: bool
    warnings: list[str] = Field(default_factory=list)
