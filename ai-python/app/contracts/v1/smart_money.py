from datetime import datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator
from pydantic.alias_generators import to_camel


class SmartMoneyContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class SmartMoneySwingPointV1(SmartMoneyContractModel):
    kind: Literal["HIGH", "LOW"]
    candle_open_at_utc: datetime
    candle_close_at_utc: datetime
    confirmed_at_utc: datetime
    price: Decimal = Field(gt=0)
    candle_revision: int = Field(ge=0)


class SmartMoneyStructureEventV1(SmartMoneyContractModel):
    event_type: Literal["BOS", "CHOCH"]
    direction: Literal["LONG", "SHORT"]
    event_at_utc: datetime
    broken_level: Decimal = Field(gt=0)
    close_price: Decimal = Field(gt=0)
    prior_structure: Literal["BULLISH", "BEARISH", "RANGE", "UNCONFIRMED"]
    displacement_fraction: Decimal = Field(ge=0)


class SmartMoneyLiquiditySweepV1(SmartMoneyContractModel):
    sweep_type: Literal["SELL_SIDE_SWEEP", "BUY_SIDE_SWEEP"]
    direction: Literal["LONG", "SHORT"]
    event_at_utc: datetime
    swept_level: Decimal = Field(gt=0)
    wick_extreme: Decimal = Field(gt=0)
    close_price: Decimal = Field(gt=0)
    rejection_fraction: Decimal = Field(ge=0)


class SmartMoneyPriceZoneV1(SmartMoneyContractModel):
    zone_type: Literal["ORDER_BLOCK", "FAIR_VALUE_GAP"]
    direction: Literal["LONG", "SHORT"]
    formed_at_utc: datetime
    source_open_at_utc: datetime
    lower_price: Decimal = Field(gt=0)
    upper_price: Decimal = Field(gt=0)
    status: Literal["ACTIVE", "MITIGATED", "INVALIDATED"]
    invalidated_at_utc: datetime | None = None
    origin_event_type: Literal["BOS", "CHOCH", "IMBALANCE"]

    @model_validator(mode="after")
    def validate_zone(self) -> "SmartMoneyPriceZoneV1":
        if self.upper_price <= self.lower_price:
            raise ValueError("upperPrice must be greater than lowerPrice")
        if self.status == "INVALIDATED" and self.invalidated_at_utc is None:
            raise ValueError("Invalidated zones require invalidatedAtUtc")
        if self.status != "INVALIDATED" and self.invalidated_at_utc is not None:
            raise ValueError("Only invalidated zones may include invalidatedAtUtc")
        return self


class SmartMoneyEvidenceV1(SmartMoneyContractModel):
    code: str = Field(min_length=1, max_length=100)
    message: str = Field(min_length=1, max_length=1000)
    impact: Literal["SUPPORTS_LONG", "SUPPORTS_SHORT", "NEUTRAL"]
    weight: Decimal = Field(ge=0, le=1)
    contribution: Decimal = Field(ge=-1, le=1)


class SmartMoneyConceptsOutputV1(SmartMoneyContractModel):
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
    structure_state: Literal["BULLISH", "BEARISH", "RANGE", "UNCONFIRMED"]
    direction: Literal["LONG", "SHORT", "NEUTRAL"]
    score: Decimal = Field(ge=-1, le=1)
    confidence: Decimal = Field(ge=0, le=1)
    latest_swing_high: SmartMoneySwingPointV1 | None = None
    latest_swing_low: SmartMoneySwingPointV1 | None = None
    structure_events: list[SmartMoneyStructureEventV1] = Field(default_factory=list)
    liquidity_sweeps: list[SmartMoneyLiquiditySweepV1] = Field(default_factory=list)
    order_blocks: list[SmartMoneyPriceZoneV1] = Field(default_factory=list)
    fair_value_gaps: list[SmartMoneyPriceZoneV1] = Field(default_factory=list)
    input_count: int = Field(ge=0)
    required_input_count: int = Field(ge=1)
    completeness: Decimal = Field(ge=0, le=1)
    valid_input_ratio: Decimal = Field(ge=0, le=1)
    data_quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    is_stale: bool
    is_eligible_for_fusion: bool
    revision: int = Field(ge=0)
    evidence: list[SmartMoneyEvidenceV1]
    warnings: list[str] = Field(default_factory=list)


class SmartMoneyProcessingResultV1(SmartMoneyContractModel):
    outcome: Literal[
        "CREATED",
        "REVISED",
        "DUPLICATE",
        "IGNORED_INELIGIBLE",
    ]
    output: SmartMoneyConceptsOutputV1 | None = None
    reason: str | None = None
