from dataclasses import dataclass
from datetime import datetime
from uuid import UUID

from app.contracts.v1.confirmation import MultiTimeframeConfirmationOutputV1
from app.directional.models import StoredDirectionalOutput
from app.regime.models import StoredRegimeOutput


@dataclass(frozen=True, slots=True)
class TimeframeIntelligencePair:
    timeframe: str
    directional: StoredDirectionalOutput
    regime: StoredRegimeOutput


@dataclass(frozen=True, slots=True)
class ConfirmationInputBundle:
    instrument_key: str
    pairs: tuple[TimeframeIntelligencePair, ...]


@dataclass(frozen=True, slots=True)
class StoredConfirmationOutput:
    engine_output_id: int | None
    output: MultiTimeframeConfirmationOutputV1
    source_engine_output_ids: tuple[int, ...]


@dataclass(frozen=True, slots=True)
class ConfirmationStoreOutcome:
    outcome: str
    output: MultiTimeframeConfirmationOutputV1 | None
    engine_output_id: int | None = None
    reason: str | None = None


@dataclass(frozen=True, slots=True)
class ConfirmationStoreStatus:
    provider: str
    output_count: int
    latest_processed_at_utc: datetime | None
    latest_error: str | None


@dataclass(frozen=True, slots=True)
class ExistingConfirmationRevision:
    engine_output_id: int
    engine_output_uid: UUID
    revision: int
