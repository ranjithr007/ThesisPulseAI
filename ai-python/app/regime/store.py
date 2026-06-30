from datetime import UTC, datetime
from threading import RLock
from typing import Protocol

from app.features.models import StoredFeatureSnapshot
from app.regime.calculator import DeterministicMarketRegimeCalculator
from app.regime.models import (
    RegimeStoreOutcome,
    RegimeStoreStatus,
    StoredRegimeOutput,
)


class MarketRegimeStore(Protocol):
    provider_name: str

    def process(
        self,
        source: StoredFeatureSnapshot,
        calculator: DeterministicMarketRegimeCalculator,
        processed_at_utc: datetime,
    ) -> RegimeStoreOutcome: ...

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredRegimeOutput | None: ...

    def get_status(self) -> RegimeStoreStatus: ...


class InMemoryMarketRegimeStore:
    provider_name = "InMemory"

    def __init__(self) -> None:
        self._sync = RLock()
        self._processed_sources: dict[str, StoredRegimeOutput | None] = {}
        self._outputs: dict[
            tuple[str, str, datetime],
            list[StoredRegimeOutput],
        ] = {}
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process(
        self,
        source: StoredFeatureSnapshot,
        calculator: DeterministicMarketRegimeCalculator,
        processed_at_utc: datetime,
    ) -> RegimeStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        snapshot = source.snapshot
        source_key = str(snapshot.snapshot_uid)
        with self._sync:
            if source_key in self._processed_sources:
                stored = self._processed_sources[source_key]
                return RegimeStoreOutcome(
                    outcome="DUPLICATE",
                    output=None if stored is None else stored.output,
                    engine_output_id=None if stored is None else stored.engine_output_id,
                    reason="Feature snapshot was already processed",
                )

            self._latest_processed_at_utc = processed_at
            if not snapshot.is_eligible_for_engines:
                self._processed_sources[source_key] = None
                return RegimeStoreOutcome(
                    outcome="IGNORED_INELIGIBLE",
                    output=None,
                    reason="Feature snapshot is not eligible for intelligence engines",
                )

            key = (
                snapshot.instrument_key.casefold(),
                snapshot.timeframe,
                _as_utc(snapshot.as_of_utc),
            )
            revisions = self._outputs.setdefault(key, [])
            revision = len(revisions)
            output = calculator.calculate(snapshot, processed_at, revision)
            stored = StoredRegimeOutput(
                engine_output_id=None,
                output=output,
                source_engine_output_id=source.engine_output_id,
            )
            revisions.append(stored)
            self._processed_sources[source_key] = stored
            return RegimeStoreOutcome(
                outcome="CREATED" if revision == 0 else "REVISED",
                output=output,
            )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredRegimeOutput | None:
        normalized = instrument_key.casefold()
        with self._sync:
            candidates = [
                revisions[-1]
                for (key, frame, _), revisions in self._outputs.items()
                if key == normalized and frame == timeframe and revisions
            ]
            return max(
                candidates,
                key=lambda item: item.output.as_of_utc,
                default=None,
            )

    def get_status(self) -> RegimeStoreStatus:
        with self._sync:
            return RegimeStoreStatus(
                provider=self.provider_name,
                output_count=sum(len(items) for items in self._outputs.values()),
                latest_processed_at_utc=self._latest_processed_at_utc,
                latest_error=self._latest_error,
            )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
