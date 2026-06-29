from datetime import datetime
from decimal import Decimal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class ContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class MessageMetadataV1(ContractModel):
    message_id: UUID
    event_type: str = "signal.generated.v1"
    contract_version: str = "1.0.0"
    occurred_at_utc: datetime
    correlation_id: str
    causation_id: str | None = None
    producer: str
    producer_version: str
    environment: str = "PAPER"
    configuration_version: str


class SignalEvidenceV1(ContractModel):
    code: str = Field(min_length=1, max_length=100)
    message: str = Field(min_length=1, max_length=1000)
    impact: str = Field(min_length=1, max_length=30)
    weight: Decimal | None = Field(default=None, ge=0, le=1)


class SignalGeneratedV1(ContractModel):
    signal_uid: UUID
    instrument_key: str = Field(min_length=1, max_length=200)
    strategy_code: str = Field(min_length=1, max_length=100)
    strategy_version: str = Field(min_length=1, max_length=50)
    direction: str = Field(min_length=1, max_length=10)
    primary_timeframe: str = Field(min_length=1, max_length=20)
    confirmation_timeframes: list[str] = Field(default_factory=list)
    strength: Decimal = Field(ge=0, le=1)
    confidence: Decimal = Field(ge=0, le=1)
    entry_opens_at_utc: datetime
    entry_closes_at_utc: datetime
    reference_price: Decimal = Field(gt=0)
    minimum_price: Decimal | None = Field(default=None, gt=0)
    maximum_price: Decimal | None = Field(default=None, gt=0)
    invalidation_price: Decimal = Field(gt=0)
    invalidation_reason: str = Field(min_length=1, max_length=1000)
    expected_holding_period_minutes: int = Field(ge=1)
    generated_at_utc: datetime
    valid_until_utc: datetime
    fusion_policy_version: str | None = Field(default=None, max_length=50)
    evidence: list[SignalEvidenceV1] = Field(default_factory=list)


class SignalEnvelopeV1(ContractModel):
    metadata: MessageMetadataV1
    payload: SignalGeneratedV1


class MockSignalRequest(ContractModel):
    instrument_key: str = "NSE_INDEX|Nifty 50"
    direction: str = "LONG"
    primary_timeframe: str = "5m"
    reference_price: Decimal = Field(default=25000, gt=0)
