from datetime import datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator
from pydantic.alias_generators import to_camel

from app.contracts.v1.confirmation import ConfirmationProcessingResultV1
from app.contracts.v1.directional import DirectionalProcessingResultV1
from app.contracts.v1.liquidity_derivatives import (
    LiquidityDerivativesProcessingResultV1,
)
from app.contracts.v1.order_flow import (
    OrderFlowProcessingResultV1,
    OrderFlowQuoteProcessingResultV1,
)
from app.contracts.v1.regime import RegimeProcessingResultV1
from app.contracts.v1.smart_money import SmartMoneyProcessingResultV1
from app.contracts.v1.workflow import FusionReadyEvidenceV1


class ContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class MarketDataMessageMetadataV1(ContractModel):
    message_id: UUID
    event_type: Literal[
        "market.candle.published.v1",
        "market.quote.published.v1",
    ]
    contract_version: Literal["1.0"]
    occurred_at_utc: datetime
    correlation_id: str = Field(min_length=1, max_length=128)
    causation_id: str | None = Field(default=None, max_length=128)
    producer: str = Field(min_length=1, max_length=100)
    producer_version: str = Field(min_length=1, max_length=50)
    environment: Literal["PAPER"]
    configuration_version: str = Field(min_length=1, max_length=100)


class MarketQuotePublishedV1(ContractModel):
    provider_code: str = Field(min_length=1, max_length=50)
    instrument_key: str = Field(min_length=1, max_length=200)
    event_at_utc: datetime
    received_at_utc: datetime
    last_traded_price: Decimal | None = Field(default=None, gt=0)
    last_traded_quantity: Decimal | None = Field(default=None, ge=0)
    previous_close_price: Decimal | None = Field(default=None, gt=0)
    open_interest: Decimal | None = Field(default=None, ge=0)
    total_buy_quantity: Decimal | None = Field(default=None, ge=0)
    total_sell_quantity: Decimal | None = Field(default=None, ge=0)
    quality_status: str = Field(min_length=1, max_length=30)
    is_usable_for_new_exposure: bool
    source_version: str = Field(min_length=1, max_length=100)

    @model_validator(mode="after")
    def validate_quote(self) -> "MarketQuotePublishedV1":
        if self.received_at_utc < self.event_at_utc:
            raise ValueError("receivedAtUtc cannot precede eventAtUtc")
        if self.total_buy_quantity is None and self.total_sell_quantity is not None:
            raise ValueError("totalBuyQuantity is required when totalSellQuantity exists")
        if self.total_sell_quantity is None and self.total_buy_quantity is not None:
            raise ValueError("totalSellQuantity is required when totalBuyQuantity exists")
        return self


class MarketQuoteEnvelopeV1(ContractModel):
    metadata: MarketDataMessageMetadataV1
    payload: MarketQuotePublishedV1


class MarketQuoteDeliveryV1(ContractModel):
    stream_position: int = Field(ge=1)
    envelope: MarketQuoteEnvelopeV1


class MarketCandlePublishedV1(ContractModel):
    provider_code: str = Field(min_length=1, max_length=50)
    instrument_key: str = Field(min_length=1, max_length=200)
    timeframe: Literal["1m", "5m", "15m", "1h", "1d"]
    open_at_utc: datetime
    close_at_utc: datetime
    open_price: Decimal = Field(gt=0)
    high_price: Decimal = Field(gt=0)
    low_price: Decimal = Field(gt=0)
    close_price: Decimal = Field(gt=0)
    volume_quantity: Decimal = Field(ge=0)
    open_interest: Decimal | None = Field(default=None, ge=0)
    is_closed: bool
    is_provisional: bool
    revision: int = Field(ge=0)
    quality_status: str = Field(min_length=1, max_length=30)
    is_usable_for_new_exposure: bool
    received_at_utc: datetime
    source_version: str = Field(min_length=1, max_length=100)

    @model_validator(mode="after")
    def validate_market_values(self) -> "MarketCandlePublishedV1":
        if self.close_at_utc <= self.open_at_utc:
            raise ValueError("closeAtUtc must be after openAtUtc")
        if self.high_price < self.low_price:
            raise ValueError("highPrice must be greater than or equal to lowPrice")
        if self.high_price < max(self.open_price, self.close_price):
            raise ValueError("highPrice must include openPrice and closePrice")
        if self.low_price > min(self.open_price, self.close_price):
            raise ValueError("lowPrice must include openPrice and closePrice")
        if self.is_closed == self.is_provisional:
            raise ValueError(
                "A candle must be either closed/non-provisional or open/provisional"
            )
        return self


class MarketCandleEnvelopeV1(ContractModel):
    metadata: MarketDataMessageMetadataV1
    payload: MarketCandlePublishedV1


class MarketCandleDeliveryV1(ContractModel):
    stream_position: int = Field(ge=1)
    envelope: MarketCandleEnvelopeV1


class FeatureValueV1(ContractModel):
    name: str = Field(min_length=1, max_length=200)
    version: str = Field(min_length=1, max_length=100)
    value: Decimal | None = None


class FeatureSnapshotV1(ContractModel):
    snapshot_uid: UUID
    message_uid: UUID
    instrument_key: str
    timeframe: str
    as_of_utc: datetime
    data_cutoff_utc: datetime
    generated_at_utc: datetime
    feature_set_version: str
    revision: int = Field(ge=0)
    input_count: int = Field(ge=0)
    required_input_count: int = Field(ge=1)
    completeness: Decimal = Field(ge=0, le=1)
    data_quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    freshness_milliseconds: int = Field(ge=0)
    is_stale: bool
    is_eligible_for_engines: bool
    features: list[FeatureValueV1]
    missing_features: list[str] = Field(default_factory=list)
    warnings: list[str] = Field(default_factory=list)


class FeatureProcessingResultV1(ContractModel):
    outcome: Literal[
        "CREATED",
        "REVISED",
        "DUPLICATE",
        "IGNORED_PROVISIONAL",
        "IGNORED_OUT_OF_ORDER",
        "REJECTED",
    ]
    stream_position: int
    message_uid: UUID
    snapshot: FeatureSnapshotV1 | None = None
    regime: RegimeProcessingResultV1 | None = None
    directional: DirectionalProcessingResultV1 | None = None
    order_flow: OrderFlowProcessingResultV1 | None = None
    smart_money: SmartMoneyProcessingResultV1 | None = None
    liquidity_derivatives: LiquidityDerivativesProcessingResultV1 | None = None
    confirmation: ConfirmationProcessingResultV1 | None = None
    workflow_evidence: FusionReadyEvidenceV1 | None = None
    reason: str | None = None


class QuoteProcessingResponseV1(ContractModel):
    stream_position: int
    message_uid: UUID
    order_flow: OrderFlowQuoteProcessingResultV1
