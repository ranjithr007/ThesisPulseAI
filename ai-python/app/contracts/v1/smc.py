from datetime import datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class SmcContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class SmcEvidenceV1(SmcContractModel):
    code: str = Field(min_length=1, max_length=100)
    message: str = Field(min_length=1, max_length=1000)
    impact: Literal["SUPPORTS_LONG", "SUPPORTS_SHORT", "NEUTRAL"]
    weight: Decimal = Field(ge=0, le=1)
    contribution: Decimal = Field(ge=-1, le=1)


class SmcZoneV1(SmcContractModel):
    zone_uid: UUID
    zone_type: Literal["BULLISH_ORDER_BLOCK", "BEARISH_ORDER_BLOCK", "BULLISH_FVG", "BEARISH_FVG"]
    lower_price: Decimal = Field(gt=0)
    upper_price: Decimal = Field(gt=0)
    formed_at_utc: datetime
    source_candle_open_at_utc: datetime
    is_mitigated: bool


class SmartMoneyConceptsOutputV1(SmcContractModel):
    output_uid: UUID
    message_uid: UUID
    source_candle_message_uid: UUID
    instrument_key: str = Field(min_length=1, max_length=200)
    timeframe: Literal["5m"]
    as_of_utc: datetime
    generated_at_utc: datetime
    engine_code: str = Field(min_length=1, max_length=100)
    engine_version: str = Field(min_length=1, max_length=50)
    policy_version: str = Field(min_length=1, max_length=100)
    direction: Literal["LONG", "SHORT", "NEUTRAL"]
    score: Decimal = Field(ge=-1, le=1)
    confidence: Decimal = Field(ge=0, le=1)
    structure_state: Literal["BULLISH", "BEARISH", "RANGING", "UNKNOWN"]
    structure_event: Literal["BOS_UP", "BOS_DOWN", "CHOCH_UP", "CHOCH_DOWN", "NONE"]
    liquidity_event: Literal["SWEEP_HIGH", "SWEEP_LOW", "NONE"]
    last_swing_high: Decimal | None = Field(default=None, gt=0)
    last_swing_low: Decimal | None = Field(default=None, gt=0)
    swing_high_at_utc: datetime | None = None
    swing_low_at_utc: datetime | None = None
    zones: list[SmcZoneV1]
    input_count: int = Field(ge=0)
    required_input_count: int = Field(ge=1)
    data_quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    is_stale: bool
    is_eligible_for_fusion: bool
    revision: int = Field(ge=0)
    evidence: list[SmcEvidenceV1]
    warnings: list[str] = Field(default_factory=list)


class SmcProcessingResultV1(SmcContractModel):
    outcome: Literal["CREATED", "REVISED", "DUPLICATE", "IGNORED_INELIGIBLE"]
    output: SmartMoneyConceptsOutputV1 | None = None
    reason: str | None = None
