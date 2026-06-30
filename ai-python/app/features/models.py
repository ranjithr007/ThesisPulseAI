from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from uuid import UUID

from app.contracts.v1.market_data import FeatureSnapshotV1


@dataclass(frozen=True, slots=True)
class CandleInput:
    candle_id: int | None
    instrument_key: str
    timeframe: str
    open_at_utc: datetime
    close_at_utc: datetime
    open_price: Decimal
    high_price: Decimal
    low_price: Decimal
    close_price: Decimal
    volume_quantity: Decimal
    open_interest: Decimal | None
    revision: int
    received_at_utc: datetime
    quality_status: str
    is_usable_for_new_exposure: bool


@dataclass(frozen=True, slots=True)
class StoredFeatureSnapshot:
    engine_output_id: int | None
    snapshot: FeatureSnapshotV1
    input_candle_ids: tuple[int, ...]


@dataclass(frozen=True, slots=True)
class FeatureStoreStatus:
    provider: str
    processed_messages: int
    snapshot_count: int
    latest_processed_at_utc: datetime | None
    latest_error: str | None


@dataclass(frozen=True, slots=True)
class FeatureStoreProcessOutcome:
    outcome: str
    snapshot: FeatureSnapshotV1 | None
    source_engine_output_id: int | None = None
    reason: str | None = None


@dataclass(frozen=True, slots=True)
class ExistingSnapshotRevision:
    engine_output_id: int
    engine_output_uid: UUID
    revision: int
