from datetime import date, datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator
from pydantic.alias_generators import to_camel


class OptionChainIntakeContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class OptionChainEntryObservationV1(OptionChainIntakeContractModel):
    derivative_contract_uid: UUID
    instrument_key: str = Field(min_length=1, max_length=200)
    expiry_date: date
    strike_price: Decimal = Field(gt=0)
    option_type: Literal["CALL", "PUT"]
    last_price: Decimal | None = Field(default=None, ge=0)
    volume_quantity: Decimal | None = Field(default=None, ge=0)
    open_interest: Decimal | None = Field(default=None, ge=0)
    implied_volatility: Decimal | None = Field(default=None, ge=0)
    delta: Decimal | None = Field(default=None, ge=-1, le=1)
    contract_multiplier: Decimal | None = Field(default=None, gt=0)
    quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    greeks_source_version: str | None = Field(default=None, max_length=100)

    @model_validator(mode="after")
    def validate_greeks_lineage(self) -> "OptionChainEntryObservationV1":
        if self.delta is not None and not self.greeks_source_version:
            raise ValueError("delta requires greeksSourceVersion")
        return self


class OptionChainSnapshotObservationV1(OptionChainIntakeContractModel):
    source_message_uid: UUID
    snapshot_uid: UUID
    underlying_instrument_key: str = Field(min_length=1, max_length=200)
    expiry_date: date
    event_at_utc: datetime
    received_at_utc: datetime
    underlying_price: Decimal = Field(gt=0)
    snapshot_status: Literal["COMPLETE", "PARTIAL", "INVALID"]
    quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    is_point_in_time_eligible: bool
    revision: int = Field(ge=0)
    entries: list[OptionChainEntryObservationV1] = Field(min_length=1)
    calculation_source_version: str | None = Field(default=None, max_length=100)

    @model_validator(mode="after")
    def validate_snapshot(self) -> "OptionChainSnapshotObservationV1":
        if self.received_at_utc < self.event_at_utc:
            raise ValueError("receivedAtUtc cannot precede eventAtUtc")
        if any(entry.expiry_date != self.expiry_date for entry in self.entries):
            raise ValueError("Every entry must match the snapshot expiryDate")
        if self.snapshot_status == "COMPLETE" and self.quality_status != "VALID":
            raise ValueError("COMPLETE snapshots require VALID qualityStatus")
        if self.is_point_in_time_eligible and self.snapshot_status != "COMPLETE":
            raise ValueError("Only COMPLETE snapshots may be point-in-time eligible")
        return self
