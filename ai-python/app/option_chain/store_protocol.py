from datetime import date, datetime
from typing import Protocol
from uuid import UUID

from app.option_chain.models import OptionChainSnapshotObservation
from app.option_chain.store import (
    OptionChainStoreOutcome,
    OptionChainStoreStatus,
    StoredOptionChainIntelligenceOutput,
)


class OptionChainIntelligenceStore(Protocol):
    provider_name: str

    def process_snapshot(
        self,
        source_message_uid: UUID,
        snapshot: OptionChainSnapshotObservation,
        calculator,
        processed_at_utc: datetime,
    ) -> OptionChainStoreOutcome: ...

    def get_latest(
        self,
        underlying_instrument_key: str,
        expiry_date: date | None = None,
        as_of_utc: datetime | None = None,
    ) -> StoredOptionChainIntelligenceOutput | None: ...

    def get_status(self) -> OptionChainStoreStatus: ...
