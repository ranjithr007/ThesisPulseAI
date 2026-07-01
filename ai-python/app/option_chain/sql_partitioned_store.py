from datetime import date, datetime
from threading import local

import pyodbc

from app.option_chain.sql_store import (
    PersistedOptionChainSnapshot,
    SqlServerOptionChainIntelligenceStore,
    _as_utc,
)


class PartitionedSqlServerOptionChainIntelligenceStore(
    SqlServerOptionChainIntelligenceStore
):
    def __init__(self, *args, **kwargs) -> None:
        super().__init__(*args, **kwargs)
        self._partition_context = local()

    def _load_snapshot_by_uid(
        self,
        cursor: pyodbc.Cursor,
        snapshot_uid,
        underlying_instrument_key: str,
    ) -> PersistedOptionChainSnapshot:
        result = super()._load_snapshot_by_uid(
            cursor,
            snapshot_uid,
            underlying_instrument_key,
        )
        self._partition_context.expiry_date = result.snapshot.expiry_date
        return result

    def _read_current_revision(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        instrument_id: int,
        as_of_utc: datetime,
    ):
        expiry_date: date | None = getattr(
            self._partition_context,
            "expiry_date",
            None,
        )
        if expiry_date is None:
            raise RuntimeError("Option-chain expiry partition was not initialized")
        return cursor.execute(
            """
            SELECT TOP (1) output.[engine_output_id], output.[engine_output_uid],
                   output.[revision], snapshot.[revision]
            FROM [intelligence].[engine_outputs] output WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [intelligence].[option_chain_output_snapshot_inputs] input
                ON input.[engine_output_id] = output.[engine_output_id]
               AND input.[input_role] = 'PRIMARY'
            INNER JOIN [market].[option_chain_snapshots] snapshot
                ON snapshot.[option_chain_snapshot_id] = input.[option_chain_snapshot_id]
            WHERE output.[engine_id] = ? AND output.[instrument_id] = ?
              AND output.[timeframe] = 'OPTION_CHAIN'
              AND output.[as_of_utc] = ?
              AND output.[output_partition_key] = ?
              AND output.[is_current] = 1
            ORDER BY output.[revision] DESC;
            """,
            engine_id,
            instrument_id,
            _as_utc(as_of_utc),
            expiry_date.isoformat(),
        ).fetchone()
