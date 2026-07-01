from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from uuid import UUID

from app.option_chain.models import OptionChainSnapshotObservation
from app.option_chain.sql_store import (
    PersistedOptionChainSnapshot,
    SqlServerOptionChainIntelligenceStore,
)

BASE_TIME = datetime(2026, 7, 1, 9, 15, tzinfo=UTC)
EXPIRY = date(2026, 7, 30)
UNDERLYING = "NSE_INDEX|Nifty 50"


def test_snapshot_lineage_prioritizes_primary_then_prior_then_term() -> None:
    primary = _persisted(1, 101, EXPIRY, BASE_TIME)
    prior = _persisted(2, 102, EXPIRY, BASE_TIME - timedelta(minutes=5))
    term = _persisted(3, 103, date(2026, 8, 27), BASE_TIME)

    lineage = SqlServerOptionChainIntelligenceStore._build_snapshot_lineage(
        primary,
        prior,
        [primary, term],
    )

    assert [(item.snapshot_id, role) for item, role in lineage] == [
        (1, "PRIMARY"),
        (2, "PRIOR"),
        (3, "TERM_STRUCTURE"),
    ]


def _persisted(
    snapshot_id: int,
    uid: int,
    expiry: date,
    event_at: datetime,
) -> PersistedOptionChainSnapshot:
    return PersistedOptionChainSnapshot(
        snapshot_id=snapshot_id,
        snapshot=OptionChainSnapshotObservation(
            snapshot_uid=UUID(int=uid),
            underlying_instrument_key=UNDERLYING,
            expiry_date=expiry,
            event_at_utc=event_at,
            received_at_utc=event_at + timedelta(seconds=1),
            underlying_price=Decimal("25000"),
            snapshot_status="COMPLETE",
            quality_status="VALID",
            is_point_in_time_eligible=True,
            revision=0,
            entries=tuple(),
            calculation_source_version="provider-option-chain-v1",
        ),
    )
