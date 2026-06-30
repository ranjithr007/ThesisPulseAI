from datetime import datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class RegimeContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class RegimeEvidenceV1(RegimeContractModel):
    code: str = Field(min_length=1, max_length=100)
    message: str = Field(min_length=1, max_length=1000)
    impact: Literal["SUPPORTS_LONG", "SUPPORTS_SHORT", "CONTRADICTS", "NEUTRAL"]
    weight: Decimal = Field(ge=0, le=1)
    contribution: Decimal = Field(ge=-1, le=1)


class MarketRegimeOutputV1(RegimeContractModel):
    output_uid: UUID
    message_uid: UUID
    source_feature_snapshot_uid: UUID
    instrument_key: str = Field(min_length=1, max_length=200)
    timeframe: Literal["1m", "5m", "15m", "1h", "1d"]
    as_of_utc: datetime
    generated_at_utc: datetime
    engine_code: str = Field(min_length=1, max_length=100)
    engine_version: str = Field(min_length=1, max_length=50)
    policy_version: str = Field(min_length=1, max_length=100)
    feature_set_version: str = Field(min_length=1, max_length=100)
    structure_regime: Literal[
        "TRENDING_UP",
        "TRENDING_DOWN",
        "RANGE_BOUND",
        "TRANSITION",
    ]
    volatility_regime: Literal["LOW", "NORMAL", "HIGH", "EXTREME"]
    direction_bias: Literal[
        "STRONG_LONG",
        "LONG",
        "NEUTRAL",
        "SHORT",
        "STRONG_SHORT",
    ]
    score: Decimal = Field(ge=-1, le=1)
    confidence: Decimal = Field(ge=0, le=1)
    trend_strength: Decimal = Field(ge=0, le=1)
    range_score: Decimal = Field(ge=0, le=1)
    transition_score: Decimal = Field(ge=0, le=1)
    volatility_score: Decimal = Field(ge=0, le=1)
    data_quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    is_stale: bool
    is_eligible_for_fusion: bool
    revision: int = Field(ge=0)
    evidence: list[RegimeEvidenceV1]
    warnings: list[str] = Field(default_factory=list)


class RegimeProcessingResultV1(RegimeContractModel):
    outcome: Literal[
        "CREATED",
        "REVISED",
        "DUPLICATE",
        "IGNORED_INELIGIBLE",
        "REJECTED",
    ]
    source_feature_snapshot_uid: UUID
    output: MarketRegimeOutputV1 | None = None
    reason: str | None = None
