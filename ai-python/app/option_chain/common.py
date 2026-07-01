from datetime import UTC, datetime
from decimal import ROUND_HALF_UP, Decimal
from uuid import UUID

from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)

ZERO = Decimal("0")
ONE = Decimal("1")
TWO = Decimal("2")
QUANTUM = Decimal("0.000001")


def eligible_entries(
    snapshot: OptionChainSnapshotObservation,
) -> list[OptionContractObservation]:
    return sorted(
        [
            entry
            for entry in snapshot.entries
            if entry.quality_status == "VALID"
            and entry.expiry_date == snapshot.expiry_date
            and entry.strike_price > ZERO
            and entry.option_type in {"CALL", "PUT"}
        ],
        key=lambda entry: (entry.strike_price, entry.option_type, entry.instrument_key),
    )


def is_snapshot_eligible(snapshot: OptionChainSnapshotObservation) -> bool:
    return (
        snapshot.snapshot_status == "COMPLETE"
        and snapshot.quality_status == "VALID"
        and snapshot.is_point_in_time_eligible
        and snapshot.received_at_utc >= snapshot.event_at_utc
        and snapshot.underlying_price > ZERO
    )


def deduplicate_snapshots(
    snapshots: list[OptionChainSnapshotObservation],
) -> list[OptionChainSnapshotObservation]:
    by_expiry: dict = {}
    for snapshot in sorted(
        snapshots,
        key=lambda item: (item.expiry_date, item.revision, item.received_at_utc),
    ):
        existing = by_expiry.get(snapshot.expiry_date)
        if existing is None or (
            snapshot.revision,
            snapshot.received_at_utc,
        ) > (
            existing.revision,
            existing.received_at_utc,
        ):
            by_expiry[snapshot.expiry_date] = snapshot
    return sorted(by_expiry.values(), key=lambda item: item.expiry_date)


def source_snapshot_uids(
    current: OptionChainSnapshotObservation,
    previous: OptionChainSnapshotObservation | None,
    term_snapshots: list[OptionChainSnapshotObservation],
) -> list[UUID]:
    ordered = [current]
    if previous is not None:
        ordered.append(previous)
    ordered.extend(term_snapshots)
    seen: set[UUID] = set()
    result: list[UUID] = []
    for snapshot in ordered:
        if snapshot.snapshot_uid not in seen:
            seen.add(snapshot.snapshot_uid)
            result.append(snapshot.snapshot_uid)
    return result


def clip(value: Decimal, lower: Decimal = -ONE, upper: Decimal = ONE) -> Decimal:
    return max(lower, min(upper, value))


def quantize(value: Decimal) -> Decimal:
    return value.quantize(QUANTUM, rounding=ROUND_HALF_UP)


def as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        raise ValueError("datetime values must be timezone-aware")
    return value.astimezone(UTC)
