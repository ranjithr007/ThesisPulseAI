from datetime import UTC, datetime
from threading import RLock
from typing import Protocol

from app.confirmation.calculator import (
    DeterministicMultiTimeframeConfirmationCalculator,
)
from app.confirmation.models import (
    ConfirmationInputBundle,
    ConfirmationStoreOutcome,
    ConfirmationStoreStatus,
    StoredConfirmationOutput,
)


class MultiTimeframeConfirmationStore(Protocol):
    provider_name: str

    def process(
        self,
        bundle: ConfirmationInputBundle,
        calculator: DeterministicMultiTimeframeConfirmationCalculator,
        processed_at_utc: datetime,
    ) -> ConfirmationStoreOutcome: ...

    def get_latest(
        self,
        instrument_key: str,
    ) -> StoredConfirmationOutput | None: ...

    def get_status(self) -> ConfirmationStoreStatus: ...


class InMemoryMultiTimeframeConfirmationStore:
    provider_name = "InMemory"

    def __init__(self) -> None:
        self._sync = RLock()
        self._outputs: dict[
            tuple[str, datetime],
            list[StoredConfirmationOutput],
        ] = {}
        self._source_identities: dict[str, StoredConfirmationOutput] = {}
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process(
        self,
        bundle: ConfirmationInputBundle,
        calculator: DeterministicMultiTimeframeConfirmationCalculator,
        processed_at_utc: datetime,
    ) -> ConfirmationStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        source_identity = _source_identity(bundle)
        primary_as_of = _primary_as_of(bundle)
        output_key = (
            bundle.instrument_key.casefold(),
            primary_as_of,
        )
        with self._sync:
            duplicate = self._source_identities.get(source_identity)
            if duplicate is not None:
                return ConfirmationStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    engine_output_id=duplicate.engine_output_id,
                    reason="The same directional and regime outputs were already confirmed",
                )

            revisions = self._outputs.setdefault(output_key, [])
            revision = len(revisions)
            output = calculator.calculate(bundle, processed_at, revision)
            source_ids = tuple(
                sorted(
                    output_id
                    for pair in bundle.pairs
                    for output_id in (
                        pair.directional.engine_output_id,
                        pair.regime.engine_output_id,
                    )
                    if output_id is not None
                )
            )
            stored = StoredConfirmationOutput(
                engine_output_id=None,
                output=output,
                source_engine_output_ids=source_ids,
            )
            revisions.append(stored)
            self._source_identities[source_identity] = stored
            self._latest_processed_at_utc = processed_at
            return ConfirmationStoreOutcome(
                outcome="CREATED" if revision == 0 else "REVISED",
                output=output,
            )

    def get_latest(
        self,
        instrument_key: str,
    ) -> StoredConfirmationOutput | None:
        normalized = instrument_key.casefold()
        with self._sync:
            candidates = [
                revisions[-1]
                for (key, _), revisions in self._outputs.items()
                if key == normalized and revisions
            ]
            return max(
                candidates,
                key=lambda item: item.output.as_of_utc,
                default=None,
            )

    def get_status(self) -> ConfirmationStoreStatus:
        with self._sync:
            return ConfirmationStoreStatus(
                provider=self.provider_name,
                output_count=sum(len(items) for items in self._outputs.values()),
                latest_processed_at_utc=self._latest_processed_at_utc,
                latest_error=self._latest_error,
            )


def _primary_as_of(bundle: ConfirmationInputBundle) -> datetime:
    for pair in bundle.pairs:
        if pair.timeframe == "5m":
            return _as_utc(pair.directional.output.as_of_utc)
    raise ValueError("Primary 5m intelligence pair is required")


def _source_identity(bundle: ConfirmationInputBundle) -> str:
    sources = sorted(
        f"{pair.timeframe}:{pair.directional.output.output_uid}:"
        f"{pair.regime.output.output_uid}"
        for pair in bundle.pairs
    )
    return f"{bundle.instrument_key.casefold()}|{'|'.join(sources)}"


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
