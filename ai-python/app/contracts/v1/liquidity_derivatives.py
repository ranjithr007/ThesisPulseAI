from datetime import datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator
from pydantic.alias_generators import to_camel


class LiquidityDerivativesContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class LiquidityPoolV1(LiquidityDerivativesContractModel):
    side: Literal["BUY_SIDE", "SELL_SIDE"]
    role: Literal["RESISTANCE", "SUPPORT"]
    source_type: Literal["SWING_CLUSTER", "SESSION_EXTREME"]
    formed_at_utc: datetime
    last_touched_at_utc: datetime
    lower_price: Decimal = Field(gt=0)
    center_price: Decimal = Field(gt=0)
    upper_price: Decimal = Field(gt=0)
    touch_count: int = Field(ge=1)
    strength: Decimal = Field(ge=0, le=1)
    distance_fraction: Decimal = Field(ge=0)
    status: Literal["ACTIVE", "SWEPT", "BROKEN"]
    status_at_utc: datetime

    @model_validator(mode="after")
    def validate_pool(self) -> "LiquidityPoolV1":
        if not self.lower_price <= self.center_price <= self.upper_price:
            raise ValueError("Pool center must be within its price boundaries")
        return self


class LiquidityDerivativesEvidenceV1(LiquidityDerivativesContractModel):
    code: str = Field(min_length=1, max_length=100)
    message: str = Field(min_length=1, max_length=1000)
    impact: Literal["SUPPORTS_LONG", "SUPPORTS_SHORT", "NEUTRAL"]
    weight: Decimal = Field(ge=0, le=1)
    contribution: Decimal = Field(ge=-1, le=1)


class LiquidityDerivativesContextOutputV1(LiquidityDerivativesContractModel):
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
    current_price: Decimal = Field(gt=0)
    range_low: Decimal = Field(gt=0)
    range_high: Decimal = Field(gt=0)
    liquidity_attraction_score: Decimal = Field(ge=-1, le=1)
    range_location_score: Decimal = Field(ge=-1, le=1)
    derivatives_score: Decimal = Field(ge=-1, le=1)
    derivatives_state: Literal[
        "LONG_BUILDUP",
        "SHORT_BUILDUP",
        "SHORT_COVERING",
        "LONG_UNWINDING",
        "FLAT",
        "NOT_AVAILABLE",
    ]
    price_change_fraction: Decimal
    open_interest_start: Decimal | None = Field(default=None, ge=0)
    open_interest_end: Decimal | None = Field(default=None, ge=0)
    open_interest_change_fraction: Decimal | None = None
    nearest_buy_side_pool: LiquidityPoolV1 | None = None
    nearest_sell_side_pool: LiquidityPoolV1 | None = None
    liquidity_pools: list[LiquidityPoolV1] = Field(default_factory=list)
    input_count: int = Field(ge=0)
    required_input_count: int = Field(ge=1)
    completeness: Decimal = Field(ge=0, le=1)
    valid_input_ratio: Decimal = Field(ge=0, le=1)
    data_quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    is_stale: bool
    is_eligible_for_fusion: bool
    revision: int = Field(ge=0)
    evidence: list[LiquidityDerivativesEvidenceV1]
    warnings: list[str] = Field(default_factory=list)

    @model_validator(mode="after")
    def validate_range(self) -> "LiquidityDerivativesContextOutputV1":
        if self.range_high < self.range_low:
            raise ValueError("rangeHigh must be greater than or equal to rangeLow")
        if (
            self.open_interest_change_fraction is not None
            and (self.open_interest_start is None or self.open_interest_end is None)
        ):
            raise ValueError("Open-interest change requires start and end values")
        return self


class LiquidityDerivativesProcessingResultV1(LiquidityDerivativesContractModel):
    outcome: Literal[
        "CREATED",
        "REVISED",
        "DUPLICATE",
        "IGNORED_INELIGIBLE",
    ]
    output: LiquidityDerivativesContextOutputV1 | None = None
    reason: str | None = None
