from datetime import datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class ConfirmationContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class TimeframeConfirmationV1(ConfirmationContractModel):
    timeframe: Literal["1m", "5m", "15m", "1h", "1d"]
    directional_output_uid: UUID
    regime_output_uid: UUID
    direction: Literal[
        "STRONG_LONG",
        "LONG",
        "NEUTRAL",
        "SHORT",
        "STRONG_SHORT",
    ]
    directional_score: Decimal = Field(ge=-1, le=1)
    regime_bias: Literal[
        "STRONG_LONG",
        "LONG",
        "NEUTRAL",
        "SHORT",
        "STRONG_SHORT",
    ]
    structure_regime: Literal[
        "TRENDING_UP",
        "TRENDING_DOWN",
        "RANGE_BOUND",
        "TRANSITION",
    ]
    volatility_regime: Literal["LOW", "NORMAL", "HIGH", "EXTREME"]
    effective_weight: Decimal = Field(ge=0, le=1)
    signed_contribution: Decimal = Field(ge=-1, le=1)
    agrees_with_primary: bool
    is_fresh: bool


class ConfirmationEvidenceV1(ConfirmationContractModel):
    code: str = Field(min_length=1, max_length=100)
    message: str = Field(min_length=1, max_length=1000)
    impact: Literal["SUPPORTS_LONG", "SUPPORTS_SHORT", "CONTRADICTS", "NEUTRAL"]
    weight: Decimal = Field(ge=0, le=1)


class MultiTimeframeConfirmationOutputV1(ConfirmationContractModel):
    output_uid: UUID
    message_uid: UUID
    instrument_key: str = Field(min_length=1, max_length=200)
    primary_timeframe: Literal["5m"]
    as_of_utc: datetime
    generated_at_utc: datetime
    engine_code: str = Field(min_length=1, max_length=100)
    engine_version: str = Field(min_length=1, max_length=50)
    policy_version: str = Field(min_length=1, max_length=100)
    direction: Literal[
        "STRONG_LONG",
        "LONG",
        "NEUTRAL",
        "SHORT",
        "STRONG_SHORT",
        "NO_SIGNAL",
    ]
    score: Decimal = Field(ge=-1, le=1)
    confidence: Decimal = Field(ge=0, le=1)
    alignment_score: Decimal = Field(ge=0, le=1)
    contradiction_score: Decimal = Field(ge=0, le=1)
    coverage: Decimal = Field(ge=0, le=1)
    required_timeframes_present: bool
    data_quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    is_stale: bool
    is_eligible_for_fusion: bool
    revision: int = Field(ge=0)
    timeframe_confirmations: list[TimeframeConfirmationV1]
    evidence: list[ConfirmationEvidenceV1]
    warnings: list[str] = Field(default_factory=list)


class ConfirmationProcessingResultV1(ConfirmationContractModel):
    outcome: Literal[
        "CREATED",
        "REVISED",
        "DUPLICATE",
        "IGNORED_INCOMPLETE",
        "IGNORED_INELIGIBLE",
        "REJECTED",
    ]
    instrument_key: str
    output: MultiTimeframeConfirmationOutputV1 | None = None
    reason: str | None = None
