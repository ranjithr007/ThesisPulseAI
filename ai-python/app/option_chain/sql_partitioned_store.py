from datetime import date, datetime
from threading import local

import pyodbc

from app.option_chain.sql_store import (
    PersistedOptionChainSnapshot,
    SqlServerOptionChainIntelligenceStore,
    _as_utc,
)
from app.option_chain.store import StoredOptionChainIntelligenceOutput


class PartitionedSqlServerOptionChainIntelligenceStore(
    SqlServerOptionChainIntelligenceStore
):
    def __init__(self, *args, **kwargs) -> None:
        super().__init__(*args, **kwargs)
        self._partition_context = local()

    def get_latest(
        self,
        underlying_instrument_key: str,
        expiry_date: date | None = None,
        as_of_utc: datetime | None = None,
    ) -> StoredOptionChainIntelligenceOutput | None:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            instrument_id = self._resolve_instrument_id(
                cursor,
                underlying_instrument_key,
            )
            cutoff = None if as_of_utc is None else _as_utc(as_of_utc)
            row = cursor.execute(
                """
                SELECT TOP (1)
                    output.[raw_contract_json], inbox.[message_uid],
                    snapshot.[option_chain_snapshot_uid], snapshot.[received_at_utc]
                FROM [intelligence].[engine_outputs] output
                INNER JOIN [intelligence].[engines] engine
                    ON engine.[engine_id] = output.[engine_id]
                INNER JOIN [intelligence].[option_chain_output_expiries] expiry_output
                    ON expiry_output.[engine_output_id] = output.[engine_output_id]
                INNER JOIN [intelligence].[option_chain_output_snapshot_inputs] input
                    ON input.[engine_output_id] = output.[engine_output_id]
                   AND input.[input_role] = 'PRIMARY'
                INNER JOIN [market].[option_chain_snapshots] snapshot
                    ON snapshot.[option_chain_snapshot_id] = input.[option_chain_snapshot_id]
                INNER JOIN [intelligence].[engine_output_message_inputs] message_input
                    ON message_input.[engine_output_id] = output.[engine_output_id]
                   AND message_input.[input_role] = 'PRIMARY'
                INNER JOIN [operations].[inbox_messages] inbox
                    ON inbox.[inbox_message_id] = message_input.[inbox_message_id]
                WHERE engine.[engine_code] = ?
                  AND output.[instrument_id] = ?
                  AND output.[timeframe] = 'OPTION_CHAIN'
                  AND (? IS NULL OR expiry_output.[expiry_date] = ?)
                  AND
                  (
                      (? IS NULL AND output.[is_current] = 1)
                      OR
                      (? IS NOT NULL
                       AND output.[as_of_utc] <= ?
                       AND output.[generated_at_utc] <= ?
                       AND snapshot.[received_at_utc] <= ?)
                  )
                ORDER BY output.[as_of_utc] DESC, output.[revision] DESC,
                         snapshot.[received_at_utc] DESC,
                         expiry_output.[expiry_date] ASC;
                """,
                self._engine_code,
                instrument_id,
                expiry_date,
                expiry_date,
                cutoff,
                cutoff,
                cutoff,
                cutoff,
                cutoff,
            ).fetchone()
            return None if row is None else self._stored_from_row(row)
        finally:
            connection.close()

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
