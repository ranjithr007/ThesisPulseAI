from datetime import datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class OrderFlowContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class OrderFlowEvidenceV1(OrderFlowContractModel):
    code: str = Field(min_length=1, max_length=100)
    message: str = Field(min_length=1, max_length=1000)
    impact: Literal["SUPPORTS_LONG", "SUPPORTS_SHORT", "NEUTRAL"]
    weight: Decimal = Field(ge=0, le=1)
    contribution: Decimal = Field(ge=-1, le=1)


class OrderFlowEngineOutputV1(OrderFlowContractModel):
    output_uid: UUID
    message_uid: UUID
    source_candle_message_uid: UUID
    quote_message_uids: list[UUID]
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
    book_imbalance: Decimal = Field(ge=-1, le=1)
    tick_rule_delta_quantity: Decimal
    tick_rule_delta_ratio: Decimal = Field(ge=-1, le=1)
    open_interest_change_fraction: Decimal | None = None
    price_change_fraction: Decimal
    absorption_score: Decimal = Field(ge=0, le=1)
    exhaustion_score: Decimal = Field(ge=0, le=1)
    quote_sample_count: int = Field(ge=0)
    usable_quote_count: int = Field(ge=0)
    traded_quantity_coverage: Decimal = Field(ge=0, le=1)
    data_quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    is_stale: bool
    is_eligible_for_fusion: bool
    revision: int = Field(ge=0)
    evidence: list[OrderFlowEvidenceV1]
    warnings: list[str] = Field(default_factory=list)


class OrderFlowQuoteProcessingResultV1(OrderFlowContractModel):
    outcome: Literal["CREATED", "DUPLICATE", "IGNORED_INELIGIBLE"]
    message_uid: UUID
    reason: str | None = None


class OrderFlowProcessingResultV1(OrderFlowContractModel):
    outcome: Literal[
        "CREATED",
        "REVISED",
        "DUPLICATE",
        "IGNORED_INELIGIBLE",
    ]
    output: OrderFlowEngineOutputV1 | None = None
    reason: str | None = None
