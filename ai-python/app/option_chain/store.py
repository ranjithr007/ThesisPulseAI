from dataclasses import dataclass
from datetime import UTC, date, datetime
from threading import RLock
from uuid import UUID

from app.contracts.v1.option_chain import OptionChainIntelligenceOutputV1
from app.option_chain.common import is_snapshot_eligible
from app.option_chain.models import OptionChainSnapshotObservation


@dataclass(frozen=True, slots=True)
class StoredOptionChainIntelligenceOutput:
    output: OptionChainIntelligenceOutputV1
    source_message_uid: UUID
    source_snapshot_uid: UUID
    source_received_at_utc: datetime


@dataclass(frozen=True, slots=True)
class OptionChainStoreOutcome:
    outcome: str
    output: OptionChainIntelligenceOutputV1 | None = None
    reason: str | None = None


@dataclass(frozen=True, slots=True)
class OptionChainStoreStatus:
    provider: str
    snapshot_count: int
    output_count: int
    latest_processed_at_utc: datetime | None
    latest_error: str | None


class InMemoryOptionChainIntelligenceStore:
    provider_name = "InMemory"

    def __init__(self) -> None:
        self._snapshots_by_uid: dict[UUID, OptionChainSnapshotObservation] = {}
        self._source_to_output: dict[UUID, StoredOptionChainIntelligenceOutput] = {}
        self._snapshot_to_output: dict[UUID, StoredOptionChainIntelligenceOutput] = {}
        self._outputs_by_cutoff: dict[
            tuple[str, date, datetime],
            StoredOptionChainIntelligenceOutput,
        ] = {}
        self._outputs: list[StoredOptionChainIntelligenceOutput] = []
        self._sync = RLock()
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process_snapshot(
        self,
        source_message_uid: UUID,
        snapshot: OptionChainSnapshotObservation,
        calculator,
        processed_at_utc: datetime,
    ) -> OptionChainStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        with self._sync:
            duplicate = self._source_to_output.get(source_message_uid)
            if duplicate is not None:
                return OptionChainStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    reason="The source message was already processed",
                )
            duplicate = self._snapshot_to_output.get(snapshot.snapshot_uid)
            if duplicate is not None:
                self._source_to_output[source_message_uid] = duplicate
                return OptionChainStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    reason="The source snapshot was already processed",
                )

            cutoff_key = (
                snapshot.underlying_instrument_key,
                snapshot.expiry_date,
                _as_utc(snapshot.event_at_utc),
            )
            current_at_cutoff = self._outputs_by_cutoff.get(cutoff_key)
            if current_at_cutoff is not None:
                current_snapshot = self._snapshots_by_uid[
                    current_at_cutoff.source_snapshot_uid
                ]
                if snapshot.revision <= current_snapshot.revision:
                    return OptionChainStoreOutcome(
                        outcome="IGNORED_INELIGIBLE",
                        reason="A same-cutoff snapshot with an equal or newer revision exists",
                    )

            self._snapshots_by_uid[snapshot.snapshot_uid] = snapshot
            prior = self._select_prior(snapshot)
            term_snapshots = self._select_term_snapshots(snapshot)
            revision = (
                0 if current_at_cutoff is None else current_at_cutoff.output.revision + 1
            )
            try:
                output = calculator.calculate(
                    current=snapshot,
                    previous=prior,
                    term_snapshots=term_snapshots,
                    generated_at_utc=max(processed_at, _as_utc(snapshot.event_at_utc)),
                    revision=revision,
                )
            except Exception as exception:
                self._snapshots_by_uid.pop(snapshot.snapshot_uid, None)
                self._latest_error = str(exception)[:1000]
                raise

            stored = StoredOptionChainIntelligenceOutput(
                output=output,
                source_message_uid=source_message_uid,
                source_snapshot_uid=snapshot.snapshot_uid,
                source_received_at_utc=_as_utc(snapshot.received_at_utc),
            )
            self._source_to_output[source_message_uid] = stored
            self._snapshot_to_output[snapshot.snapshot_uid] = stored
            self._outputs_by_cutoff[cutoff_key] = stored
            self._outputs.append(stored)
            self._latest_processed_at_utc = processed_at
            self._latest_error = None
            return OptionChainStoreOutcome(
                outcome="CREATED" if current_at_cutoff is None else "REVISED",
                output=output,
            )

    def get_latest(
        self,
        underlying_instrument_key: str,
        expiry_date: date | None = None,
        as_of_utc: datetime | None = None,
    ) -> StoredOptionChainIntelligenceOutput | None:
        cutoff = None if as_of_utc is None else _as_utc(as_of_utc)
        with self._sync:
            candidates = [
                stored
                for stored in self._outputs
                if stored.output.underlying_instrument_key == underlying_instrument_key
                and (
                    expiry_date is None
                    or stored.output.expiry_metrics[0].expiry_date == expiry_date
                )
                and (cutoff is None or stored.output.as_of_utc <= cutoff)
                and (cutoff is None or stored.source_received_at_utc <= cutoff)
            ]
            if not candidates:
                return None
            return max(
                candidates,
                key=lambda stored: (
                    stored.output.as_of_utc,
                    stored.output.revision,
                    stored.source_received_at_utc,
                ),
            )

    def get_status(self) -> OptionChainStoreStatus:
        with self._sync:
            return OptionChainStoreStatus(
                provider=self.provider_name,
                snapshot_count=len(self._snapshots_by_uid),
                output_count=len(self._outputs),
                latest_processed_at_utc=self._latest_processed_at_utc,
                latest_error=self._latest_error,
            )

    def _select_prior(
        self,
        current: OptionChainSnapshotObservation,
    ) -> OptionChainSnapshotObservation | None:
        candidates = [
            snapshot
            for snapshot in self._snapshots_by_uid.values()
            if snapshot.snapshot_uid != current.snapshot_uid
            and snapshot.underlying_instrument_key
            == current.underlying_instrument_key
            and snapshot.expiry_date == current.expiry_date
            and _as_utc(snapshot.event_at_utc) < _as_utc(current.event_at_utc)
            and _as_utc(snapshot.received_at_utc) <= _as_utc(current.received_at_utc)
            and is_snapshot_eligible(snapshot)
        ]
        if not candidates:
            return None
        return max(
            candidates,
            key=lambda snapshot: (
                _as_utc(snapshot.event_at_utc),
                snapshot.revision,
                _as_utc(snapshot.received_at_utc),
            ),
        )

    def _select_term_snapshots(
        self,
        current: OptionChainSnapshotObservation,
    ) -> list[OptionChainSnapshotObservation]:
        eligible = [
            snapshot
            for snapshot in self._snapshots_by_uid.values()
            if snapshot.underlying_instrument_key
            == current.underlying_instrument_key
            and _as_utc(snapshot.event_at_utc) <= _as_utc(current.event_at_utc)
            and _as_utc(snapshot.received_at_utc) <= _as_utc(current.received_at_utc)
            and is_snapshot_eligible(snapshot)
        ]
        by_expiry: dict[date, OptionChainSnapshotObservation] = {}
        for snapshot in eligible:
            existing = by_expiry.get(snapshot.expiry_date)
            if existing is None or (
                _as_utc(snapshot.event_at_utc),
                snapshot.revision,
                _as_utc(snapshot.received_at_utc),
            ) > (
                _as_utc(existing.event_at_utc),
                existing.revision,
                _as_utc(existing.received_at_utc),
            ):
                by_expiry[snapshot.expiry_date] = snapshot
        return sorted(by_expiry.values(), key=lambda snapshot: snapshot.expiry_date)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
