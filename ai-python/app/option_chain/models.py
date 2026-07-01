from dataclasses import dataclass
from datetime import date, datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID


@dataclass(frozen=True, slots=True)
class OptionContractObservation:
    derivative_contract_uid: UUID
    instrument_key: str
    expiry_date: date
    strike_price: Decimal
    option_type: Literal["CALL", "PUT"]
    last_price: Decimal | None
    volume_quantity: Decimal | None
    open_interest: Decimal | None
    implied_volatility: Decimal | None
    delta: Decimal | None
    contract_multiplier: Decimal | None
    quality_status: str = "VALID"
    greeks_source_version: str | None = None


@dataclass(frozen=True, slots=True)
class OptionChainSnapshotObservation:
    snapshot_uid: UUID
    underlying_instrument_key: str
    expiry_date: date
    event_at_utc: datetime
    received_at_utc: datetime
    underlying_price: Decimal
    snapshot_status: str
    quality_status: str
    is_point_in_time_eligible: bool
    revision: int
    entries: tuple[OptionContractObservation, ...]
    calculation_source_version: str | None = None
