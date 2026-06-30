from dataclasses import dataclass
from datetime import datetime
from uuid import UUID

from app.contracts.v1.regime import MarketRegimeOutputV1


@dataclass(frozen=True, slots=True)
class StoredRegimeOutput:
    engine_output_id: int | None
    output: MarketRegimeOutputV1
    source_engine_output_id: int | None


@dataclass(frozen=True, slots=True)
class RegimeStoreOutcome:
    outcome: str
    output: MarketRegimeOutputV1 | None
    engine_output_id: int | None = None
    reason: str | None = None


@dataclass(frozen=True, slots=True)
class RegimeStoreStatus:
    provider: str
    output_count: int
    latest_processed_at_utc: datetime | None
    latest_error: str | None


@dataclass(frozen=True, slots=True)
class ExistingRegimeRevision:
    engine_output_id: int
    engine_output_uid: UUID
    revision: int
