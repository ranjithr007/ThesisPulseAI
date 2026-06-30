from dataclasses import dataclass
from datetime import datetime
from uuid import UUID

from app.contracts.v1.directional import DirectionalEngineOutputV1


@dataclass(frozen=True, slots=True)
class StoredDirectionalOutput:
    engine_output_id: int | None
    output: DirectionalEngineOutputV1
    source_engine_output_id: int | None


@dataclass(frozen=True, slots=True)
class DirectionalStoreOutcome:
    outcome: str
    output: DirectionalEngineOutputV1 | None
    engine_output_id: int | None = None
    reason: str | None = None


@dataclass(frozen=True, slots=True)
class DirectionalStoreStatus:
    provider: str
    output_count: int
    latest_processed_at_utc: datetime | None
    latest_error: str | None


@dataclass(frozen=True, slots=True)
class ExistingDirectionalRevision:
    engine_output_id: int
    engine_output_uid: UUID
    revision: int
