import hashlib
import json
from dataclasses import dataclass
from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from threading import RLock
from uuid import UUID

import pyodbc

from app.contracts.v1.option_chain import OptionChainIntelligenceOutputV1
from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)
from app.option_chain.store import (
    OptionChainStoreOutcome,
    OptionChainStoreStatus,
    StoredOptionChainIntelligenceOutput,
)


@dataclass(frozen=True, slots=True)
class PersistedOptionChainSnapshot:
    snapshot_id: int
    snapshot: OptionChainSnapshotObservation


class SqlServerOptionChainIntelligenceStore:
    provider_name = "SqlServer"

    def __init__(
        self,
        connection_string: str,
        *,
        actor: str = "ThesisPulse.AI.OptionChain",
        engine_code: str = "THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE",
        broker_code: str = "UPSTOX",
        service_version: str = "1.0.0",
        maximum_output_age_seconds: int = 120,
        command_timeout_seconds: int = 30,
    ) -> None:
        if not connection_string.strip():
            raise ValueError("SQL Server connection string is required")
        if maximum_output_age_seconds < 1 or maximum_output_age_seconds > 3600:
            raise ValueError("maximum_output_age_seconds must be between 1 and 3600")
        if command_timeout_seconds < 1 or command_timeout_seconds > 300:
            raise ValueError("command_timeout_seconds must be between 1 and 300")
        self._connection_string = connection_string
        self._actor = actor
        self._engine_code = engine_code
        self._broker_code = broker_code
        self._service_version = service_version
        self._maximum_output_age_seconds = maximum_output_age_seconds
        self._command_timeout_seconds = command_timeout_seconds
        self._status_sync = RLock()
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
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            inbox_status, inbox_message_id = self._claim_inbox(
                cursor,
                source_message_uid,
                snapshot,
                processed_at,
            )
            if inbox_status == "PROCESSED":
                duplicate = self._read_by_source_message(cursor, source_message_uid)
                connection.rollback()
                return OptionChainStoreOutcome(
                    outcome="DUPLICATE",
                    output=None if duplicate is None else duplicate.output,
                    reason="The source message was already processed",
                )

            engine_id = self._resolve_engine_id(cursor)
            instrument_id = self._resolve_instrument_id(
                cursor,
                snapshot.underlying_instrument_key,
            )
            primary = self._load_snapshot_by_uid(
                cursor,
                snapshot.snapshot_uid,
                snapshot.underlying_instrument_key,
            )
            self._validate_publication_snapshot(snapshot, primary.snapshot, instrument_id, cursor)

            duplicate = self._read_by_snapshot_id(cursor, primary.snapshot_id)
            if duplicate is not None:
                self._complete_inbox(
                    cursor,
                    source_message_uid,
                    str(duplicate.output.output_uid),
                    processed_at,
                )
                connection.commit()
                self._mark_processed(processed_at)
                return OptionChainStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    reason="The normalized option-chain snapshot was already processed",
                )

            existing = self._read_current_revision(
                cursor,
                engine_id,
                instrument_id,
                primary.snapshot.event_at_utc,
            )
            if existing is not None and primary.snapshot.revision <= int(existing[3]):
                self._complete_inbox(
                    cursor,
                    source_message_uid,
                    "IGNORED_INELIGIBLE",
                    processed_at,
                )
                connection.commit()
                self._mark_processed(processed_at)
                return OptionChainStoreOutcome(
                    outcome="IGNORED_INELIGIBLE",
                    reason=(
                        "A same-cutoff normalized snapshot with an equal or newer "
                        "revision already produced an output"
                    ),
                )

            prior = self._load_prior_snapshot(cursor, primary)
            term_snapshots = self._load_term_snapshots(cursor, primary)
            revision = 0 if existing is None else int(existing[2]) + 1
            generated_at = max(
                processed_at,
                _as_utc(primary.snapshot.event_at_utc),
                _as_utc(primary.snapshot.received_at_utc),
            )
            output = calculator.calculate(
                current=primary.snapshot,
                previous=None if prior is None else prior.snapshot,
                term_snapshots=[item.snapshot for item in term_snapshots],
                generated_at_utc=generated_at,
                revision=revision,
            )
            lineage = self._build_snapshot_lineage(primary, prior, term_snapshots)
            run_id = self._insert_engine_run(
                cursor,
                engine_id,
                output,
                source_message_uid,
                primary.snapshot.snapshot_uid,
            )
            if existing is not None:
                cursor.execute(
                    "UPDATE [intelligence].[engine_outputs] "
                    "SET [is_current] = 0 WHERE [engine_output_id] = ?",
                    int(existing[0]),
                )
            output_id = self._insert_engine_output(
                cursor,
                run_id,
                engine_id,
                instrument_id,
                output,
                source_message_uid,
                primary.snapshot.snapshot_uid,
                existing,
            )
            self._insert_message_input(
                cursor,
                output_id,
                inbox_message_id,
                generated_at,
            )
            self._insert_snapshot_inputs(cursor, output_id, lineage, generated_at)
            self._insert_expiry_details(cursor, output_id, output)
            self._insert_iv_term_points(cursor, output_id, output)
            self._insert_evidence(cursor, output_id, output)
            self._insert_warnings(cursor, output_id, output)
            self._complete_inbox(
                cursor,
                source_message_uid,
                str(output.output_uid),
                processed_at,
            )
            connection.commit()
            self._mark_processed(processed_at)
            return OptionChainStoreOutcome(
                outcome="CREATED" if existing is None else "REVISED",
                output=output,
            )
        except Exception as exception:
            connection.rollback()
            self._mark_error(exception)
            raise
        finally:
            connection.close()

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
                       AND snapshot.[received_at_utc] <= ?)
                  )
                ORDER BY output.[as_of_utc] DESC, output.[revision] DESC,
                         snapshot.[received_at_utc] DESC;
                """,
                self._engine_code,
                instrument_id,
                expiry_date,
                expiry_date,
                cutoff,
                cutoff,
                cutoff,
                cutoff,
            ).fetchone()
            return None if row is None else self._stored_from_row(row)
        finally:
            connection.close()

    def get_status(self) -> OptionChainStoreStatus:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            snapshot_count = cursor.execute(
                """
                SELECT COUNT_BIG(DISTINCT input.[option_chain_snapshot_id])
                FROM [intelligence].[option_chain_output_snapshot_inputs] input
                INNER JOIN [intelligence].[engine_outputs] output
                    ON output.[engine_output_id] = input.[engine_output_id]
                INNER JOIN [intelligence].[engines] engine
                    ON engine.[engine_id] = output.[engine_id]
                WHERE engine.[engine_code] = ? AND input.[input_role] = 'PRIMARY';
                """,
                self._engine_code,
            ).fetchone()
            output_count = cursor.execute(
                """
                SELECT COUNT_BIG(*)
                FROM [intelligence].[engine_outputs] output
                INNER JOIN [intelligence].[engines] engine
                    ON engine.[engine_id] = output.[engine_id]
                WHERE engine.[engine_code] = ?
                  AND output.[timeframe] = 'OPTION_CHAIN';
                """,
                self._engine_code,
            ).fetchone()
            with self._status_sync:
                return OptionChainStoreStatus(
                    provider=self.provider_name,
                    snapshot_count=int(snapshot_count[0]),
                    output_count=int(output_count[0]),
                    latest_processed_at_utc=self._latest_processed_at_utc,
                    latest_error=self._latest_error,
                )
        finally:
            connection.close()

    def _connect(self) -> pyodbc.Connection:
        return pyodbc.connect(self._connection_string, autocommit=False)

    def _claim_inbox(
        self,
        cursor: pyodbc.Cursor,
        source_message_uid: UUID,
        snapshot: OptionChainSnapshotObservation,
        processed_at: datetime,
    ) -> tuple[str, int]:
        row = cursor.execute(
            """
            SELECT [inbox_message_id], [status], [attempt_count], [max_attempts]
            FROM [operations].[inbox_messages] WITH (UPDLOCK, HOLDLOCK)
            WHERE [consumer_name] = ? AND [message_uid] = ?;
            """,
            self._actor,
            str(source_message_uid),
        ).fetchone()
        if row is not None and str(row[1]) == "PROCESSED":
            return "PROCESSED", int(row[0])

        payload_json = _snapshot_json(source_message_uid, snapshot)
        payload_hash = hashlib.sha256(payload_json.encode("utf-8")).hexdigest().upper()
        received_at = max(
            processed_at,
            _as_utc(snapshot.event_at_utc),
            _as_utc(snapshot.received_at_utc),
        )
        lease_expires = received_at + timedelta(minutes=5)
        if row is None:
            inserted = cursor.execute(
                """
                INSERT INTO [operations].[inbox_messages]
                ([message_uid], [consumer_name], [contract_version], [environment],
                 [message_type], [source_service], [source_version], [correlation_id],
                 [causation_id], [generated_at_utc], [received_at_utc], [expires_at_utc],
                 [payload_json], [payload_hash], [headers_json], [status],
                 [attempt_count], [max_attempts], [lease_owner], [lease_expires_at_utc],
                 [created_by], [updated_by])
                OUTPUT INSERTED.[inbox_message_id]
                VALUES (?, ?, '1.0.0', 'PAPER', 'market.option_chain.published.v1',
                        'ThesisPulse.MarketData.Service', ?, ?, ?, ?, ?, NULL, ?, ?, NULL,
                        'PROCESSING', 1, 5, ?, ?, ?, ?);
                """,
                str(source_message_uid),
                self._actor,
                snapshot.calculation_source_version or self._service_version,
                str(source_message_uid),
                str(snapshot.snapshot_uid),
                _as_utc(snapshot.event_at_utc),
                received_at,
                payload_json,
                payload_hash,
                self._actor,
                lease_expires,
                self._actor,
                self._actor,
            ).fetchone()
            return "PROCESSING", int(inserted[0])

        if int(row[2]) >= int(row[3]):
            raise RuntimeError("Option-Chain Intelligence inbox retry limit was reached")
        cursor.execute(
            """
            UPDATE [operations].[inbox_messages]
            SET [status] = 'PROCESSING', [attempt_count] = [attempt_count] + 1,
                [lease_owner] = ?, [lease_expires_at_utc] = ?,
                [last_error_code] = NULL, [last_error_message] = NULL,
                [updated_at_utc] = SYSUTCDATETIME(), [updated_by] = ?
            WHERE [consumer_name] = ? AND [message_uid] = ?;
            """,
            self._actor,
            lease_expires,
            self._actor,
            self._actor,
            str(source_message_uid),
        )
        return "PROCESSING", int(row[0])

    def _resolve_engine_id(self, cursor: pyodbc.Cursor) -> int:
        row = cursor.execute(
            """
            SELECT TOP (1) [engine_id]
            FROM [intelligence].[engines]
            WHERE [engine_code] = ? AND [owner_service] = 'ThesisPulse.AI'
              AND [engine_role] = 'DIRECTIONAL_VOTER'
              AND [can_create_signals] = 0 AND [can_execute_orders] = 0
              AND [is_active] = 1;
            """,
            self._engine_code,
        ).fetchone()
        if row is None:
            raise RuntimeError(
                f"Active authority-free option-chain engine '{self._engine_code}' "
                "was not found"
            )
        return int(row[0])

    def _resolve_instrument_id(
        self,
        cursor: pyodbc.Cursor,
        instrument_key: str,
    ) -> int:
        row = cursor.execute(
            """
            SELECT TOP (1) mapping.[instrument_id]
            FROM [reference].[broker_instrument_mappings] mapping
            INNER JOIN [reference].[brokers] broker
                ON broker.[broker_id] = mapping.[broker_id]
            WHERE broker.[broker_code] = ? AND broker.[is_active] = 1
              AND mapping.[broker_instrument_key] = ?
              AND mapping.[is_active] = 1 AND mapping.[valid_to_date] IS NULL;
            """,
            self._broker_code,
            instrument_key,
        ).fetchone()
        if row is None:
            raise RuntimeError(
                f"No active canonical mapping exists for '{instrument_key}'"
            )
        return int(row[0])

    def _load_snapshot_by_uid(
        self,
        cursor: pyodbc.Cursor,
        snapshot_uid: UUID,
        underlying_instrument_key: str,
    ) -> PersistedOptionChainSnapshot:
        row = cursor.execute(
            """
            SELECT [option_chain_snapshot_id], [option_chain_snapshot_uid],
                   [underlying_instrument_id], [expiry_date], [event_at_utc],
                   [received_at_utc], [underlying_price], [snapshot_status],
                   [quality_status], [is_point_in_time_eligible], [revision],
                   [calculation_source_version]
            FROM [market].[option_chain_snapshots]
            WHERE [option_chain_snapshot_uid] = ?;
            """,
            str(snapshot_uid),
        ).fetchone()
        if row is None:
            raise RuntimeError(
                f"Normalized option-chain snapshot '{snapshot_uid}' was not found"
            )
        return self._snapshot_from_row(cursor, row, underlying_instrument_key)

    def _load_snapshot_by_id(
        self,
        cursor: pyodbc.Cursor,
        snapshot_id: int,
        underlying_instrument_key: str,
    ) -> PersistedOptionChainSnapshot:
        row = cursor.execute(
            """
            SELECT [option_chain_snapshot_id], [option_chain_snapshot_uid],
                   [underlying_instrument_id], [expiry_date], [event_at_utc],
                   [received_at_utc], [underlying_price], [snapshot_status],
                   [quality_status], [is_point_in_time_eligible], [revision],
                   [calculation_source_version]
            FROM [market].[option_chain_snapshots]
            WHERE [option_chain_snapshot_id] = ?;
            """,
            snapshot_id,
        ).fetchone()
        if row is None:
            raise RuntimeError(f"Option-chain snapshot id {snapshot_id} was not found")
        return self._snapshot_from_row(cursor, row, underlying_instrument_key)

    def _snapshot_from_row(
        self,
        cursor: pyodbc.Cursor,
        row,
        underlying_instrument_key: str,
    ) -> PersistedOptionChainSnapshot:
        snapshot_id = int(row[0])
        expiry_date = row[3]
        entries = self._load_snapshot_entries(cursor, snapshot_id, expiry_date)
        snapshot = OptionChainSnapshotObservation(
            snapshot_uid=UUID(str(row[1])),
            underlying_instrument_key=underlying_instrument_key,
            expiry_date=expiry_date,
            event_at_utc=_as_utc(row[4]),
            received_at_utc=_as_utc(row[5]),
            underlying_price=Decimal(str(row[6])),
            snapshot_status=str(row[7]),
            quality_status=str(row[8]),
            is_point_in_time_eligible=bool(row[9]),
            revision=int(row[10]),
            entries=tuple(entries),
            calculation_source_version=None if row[11] is None else str(row[11]),
        )
        return PersistedOptionChainSnapshot(snapshot_id=snapshot_id, snapshot=snapshot)

    def _load_snapshot_entries(
        self,
        cursor: pyodbc.Cursor,
        snapshot_id: int,
        expiry_date: date,
    ) -> list[OptionContractObservation]:
        rows = cursor.execute(
            """
            SELECT contract.[derivative_contract_uid],
                   COALESCE(broker_mapping.[broker_instrument_key],
                            CONCAT('INSTRUMENT_ID:', entry.[instrument_id])),
                   entry.[strike_price], entry.[option_type], entry.[last_price],
                   entry.[volume_quantity], entry.[open_interest],
                   entry.[implied_volatility], entry.[delta],
                   contract.[contract_multiplier], entry.[quality_status],
                   entry.[greeks_source_version]
            FROM [market].[option_chain_entries] entry
            INNER JOIN [reference].[derivative_contracts] contract
                ON contract.[derivative_contract_id] = entry.[derivative_contract_id]
            OUTER APPLY
            (
                SELECT TOP (1) mapping.[broker_instrument_key]
                FROM [reference].[broker_instrument_mappings] mapping
                INNER JOIN [reference].[brokers] broker
                    ON broker.[broker_id] = mapping.[broker_id]
                WHERE mapping.[instrument_id] = entry.[instrument_id]
                  AND broker.[broker_code] = ? AND broker.[is_active] = 1
                  AND mapping.[is_active] = 1 AND mapping.[valid_to_date] IS NULL
                ORDER BY mapping.[valid_from_date] DESC
            ) broker_mapping
            WHERE entry.[option_chain_snapshot_id] = ?
            ORDER BY entry.[strike_price], entry.[option_type];
            """,
            self._broker_code,
            snapshot_id,
        ).fetchall()
        return [
            OptionContractObservation(
                derivative_contract_uid=UUID(str(row[0])),
                instrument_key=str(row[1]),
                expiry_date=expiry_date,
                strike_price=Decimal(str(row[2])),
                option_type=str(row[3]),
                last_price=None if row[4] is None else Decimal(str(row[4])),
                volume_quantity=None if row[5] is None else Decimal(str(row[5])),
                open_interest=None if row[6] is None else Decimal(str(row[6])),
                implied_volatility=None if row[7] is None else Decimal(str(row[7])),
                delta=None if row[8] is None else Decimal(str(row[8])),
                contract_multiplier=Decimal(str(row[9])),
                quality_status=str(row[10]),
                greeks_source_version=None if row[11] is None else str(row[11]),
            )
            for row in rows
        ]

    def _validate_publication_snapshot(
        self,
        published: OptionChainSnapshotObservation,
        persisted: OptionChainSnapshotObservation,
        instrument_id: int,
        cursor: pyodbc.Cursor,
    ) -> None:
        persisted_instrument = cursor.execute(
            """
            SELECT [underlying_instrument_id]
            FROM [market].[option_chain_snapshots]
            WHERE [option_chain_snapshot_uid] = ?;
            """,
            str(persisted.snapshot_uid),
        ).fetchone()
        if persisted_instrument is None or int(persisted_instrument[0]) != instrument_id:
            raise RuntimeError("Option-chain underlying instrument lineage mismatch")
        comparisons = (
            (published.expiry_date, persisted.expiry_date, "expiryDate"),
            (
                _as_utc(published.event_at_utc),
                _as_utc(persisted.event_at_utc),
                "eventAtUtc",
            ),
            (
                _as_utc(published.received_at_utc),
                _as_utc(persisted.received_at_utc),
                "receivedAtUtc",
            ),
            (published.revision, persisted.revision, "revision"),
        )
        for published_value, persisted_value, field_name in comparisons:
            if published_value != persisted_value:
                raise RuntimeError(
                    f"Published {field_name} does not match the normalized snapshot"
                )

    def _load_prior_snapshot(
        self,
        cursor: pyodbc.Cursor,
        current: PersistedOptionChainSnapshot,
    ) -> PersistedOptionChainSnapshot | None:
        row = cursor.execute(
            """
            SELECT TOP (1) [option_chain_snapshot_id]
            FROM [market].[option_chain_snapshots]
            WHERE [underlying_instrument_id] =
                  (SELECT [underlying_instrument_id]
                   FROM [market].[option_chain_snapshots]
                   WHERE [option_chain_snapshot_id] = ?)
              AND [expiry_date] = ?
              AND [event_at_utc] < ?
              AND [received_at_utc] <= ?
              AND [snapshot_status] = 'COMPLETE'
              AND [quality_status] = 'VALID'
              AND [is_point_in_time_eligible] = 1
            ORDER BY [event_at_utc] DESC, [revision] DESC, [received_at_utc] DESC;
            """,
            current.snapshot_id,
            current.snapshot.expiry_date,
            current.snapshot.event_at_utc,
            current.snapshot.received_at_utc,
        ).fetchone()
        if row is None:
            return None
        return self._load_snapshot_by_id(
            cursor,
            int(row[0]),
            current.snapshot.underlying_instrument_key,
        )

    def _load_term_snapshots(
        self,
        cursor: pyodbc.Cursor,
        current: PersistedOptionChainSnapshot,
    ) -> list[PersistedOptionChainSnapshot]:
        rows = cursor.execute(
            """
            WITH ranked AS
            (
                SELECT [option_chain_snapshot_id], [expiry_date],
                       ROW_NUMBER() OVER
                       (
                           PARTITION BY [expiry_date]
                           ORDER BY [event_at_utc] DESC, [revision] DESC,
                                    [received_at_utc] DESC
                       ) AS [point_in_time_rank]
                FROM [market].[option_chain_snapshots]
                WHERE [underlying_instrument_id] =
                      (SELECT [underlying_instrument_id]
                       FROM [market].[option_chain_snapshots]
                       WHERE [option_chain_snapshot_id] = ?)
                  AND [expiry_date] >= CAST(? AS date)
                  AND [event_at_utc] <= ?
                  AND [received_at_utc] <= ?
                  AND [snapshot_status] = 'COMPLETE'
                  AND [quality_status] = 'VALID'
                  AND [is_point_in_time_eligible] = 1
            )
            SELECT TOP (12) [option_chain_snapshot_id]
            FROM ranked
            WHERE [point_in_time_rank] = 1
            ORDER BY [expiry_date];
            """,
            current.snapshot_id,
            current.snapshot.event_at_utc,
            current.snapshot.event_at_utc,
            current.snapshot.received_at_utc,
        ).fetchall()
        return [
            self._load_snapshot_by_id(
                cursor,
                int(row[0]),
                current.snapshot.underlying_instrument_key,
            )
            for row in rows
        ]

    @staticmethod
    def _read_current_revision(
        cursor: pyodbc.Cursor,
        engine_id: int,
        instrument_id: int,
        as_of_utc: datetime,
    ):
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
              AND output.[as_of_utc] = ? AND output.[is_current] = 1
            ORDER BY output.[revision] DESC;
            """,
            engine_id,
            instrument_id,
            _as_utc(as_of_utc),
        ).fetchone()

    def _read_by_source_message(
        self,
        cursor: pyodbc.Cursor,
        source_message_uid: UUID,
    ) -> StoredOptionChainIntelligenceOutput | None:
        row = cursor.execute(
            """
            SELECT TOP (1) output.[raw_contract_json], inbox.[message_uid],
                   snapshot.[option_chain_snapshot_uid], snapshot.[received_at_utc]
            FROM [intelligence].[engine_outputs] output
            INNER JOIN [intelligence].[engines] engine
                ON engine.[engine_id] = output.[engine_id]
            INNER JOIN [intelligence].[engine_output_message_inputs] message_input
                ON message_input.[engine_output_id] = output.[engine_output_id]
            INNER JOIN [operations].[inbox_messages] inbox
                ON inbox.[inbox_message_id] = message_input.[inbox_message_id]
            INNER JOIN [intelligence].[option_chain_output_snapshot_inputs] input
                ON input.[engine_output_id] = output.[engine_output_id]
               AND input.[input_role] = 'PRIMARY'
            INNER JOIN [market].[option_chain_snapshots] snapshot
                ON snapshot.[option_chain_snapshot_id] = input.[option_chain_snapshot_id]
            WHERE engine.[engine_code] = ? AND inbox.[consumer_name] = ?
              AND inbox.[message_uid] = ?
            ORDER BY output.[revision] DESC;
            """,
            self._engine_code,
            self._actor,
            str(source_message_uid),
        ).fetchone()
        return None if row is None else self._stored_from_row(row)

    def _read_by_snapshot_id(
        self,
        cursor: pyodbc.Cursor,
        snapshot_id: int,
    ) -> StoredOptionChainIntelligenceOutput | None:
        row = cursor.execute(
            """
            SELECT TOP (1) output.[raw_contract_json], inbox.[message_uid],
                   snapshot.[option_chain_snapshot_uid], snapshot.[received_at_utc]
            FROM [intelligence].[engine_outputs] output
            INNER JOIN [intelligence].[engines] engine
                ON engine.[engine_id] = output.[engine_id]
            INNER JOIN [intelligence].[option_chain_output_snapshot_inputs] input
                ON input.[engine_output_id] = output.[engine_output_id]
               AND input.[input_role] = 'PRIMARY'
            INNER JOIN [market].[option_chain_snapshots] snapshot
                ON snapshot.[option_chain_snapshot_id] = input.[option_chain_snapshot_id]
            INNER JOIN [intelligence].[engine_output_message_inputs] message_input
                ON message_input.[engine_output_id] = output.[engine_output_id]
            INNER JOIN [operations].[inbox_messages] inbox
                ON inbox.[inbox_message_id] = message_input.[inbox_message_id]
            WHERE engine.[engine_code] = ? AND input.[option_chain_snapshot_id] = ?
            ORDER BY output.[revision] DESC;
            """,
            self._engine_code,
            snapshot_id,
        ).fetchone()
        return None if row is None else self._stored_from_row(row)

    @staticmethod
    def _stored_from_row(row) -> StoredOptionChainIntelligenceOutput:
        return StoredOptionChainIntelligenceOutput(
            output=OptionChainIntelligenceOutputV1.model_validate_json(row[0]),
            source_message_uid=UUID(str(row[1])),
            source_snapshot_uid=UUID(str(row[2])),
            source_received_at_utc=_as_utc(row[3]),
        )

    @staticmethod
    def _build_snapshot_lineage(
        primary: PersistedOptionChainSnapshot,
        prior: PersistedOptionChainSnapshot | None,
        term_snapshots: list[PersistedOptionChainSnapshot],
    ) -> list[tuple[PersistedOptionChainSnapshot, str]]:
        candidates: list[tuple[PersistedOptionChainSnapshot, str]] = [(primary, "PRIMARY")]
        if prior is not None:
            candidates.append((prior, "PRIOR"))
        candidates.extend((item, "TERM_STRUCTURE") for item in term_snapshots)
        seen: set[UUID] = set()
        result: list[tuple[PersistedOptionChainSnapshot, str]] = []
        for item, role in candidates:
            if item.snapshot.snapshot_uid in seen:
                continue
            seen.add(item.snapshot.snapshot_uid)
            result.append((item, role))
        return result

    def _insert_engine_run(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        output: OptionChainIntelligenceOutputV1,
        source_message_uid: UUID,
        source_snapshot_uid: UUID,
    ) -> int:
        row = cursor.execute(
            """
            INSERT INTO [intelligence].[engine_runs]
            ([engine_run_uid], [engine_id], [environment], [engine_version],
             [configuration_version], [feature_set_version], [model_version],
             [data_cutoff_utc], [started_at_utc], [completed_at_utc], [status],
             [correlation_id], [causation_id], [input_count], [output_count],
             [warning_count], [created_by], [updated_by])
            OUTPUT INSERTED.[engine_run_id]
            VALUES (NEWID(), ?, 'PAPER', ?, ?, NULL, NULL, ?, ?, ?, 'SUCCEEDED',
                    ?, ?, ?, 1, ?, ?, ?);
            """,
            engine_id,
            output.engine_version,
            output.policy_version,
            output.as_of_utc,
            output.generated_at_utc,
            output.generated_at_utc,
            str(source_message_uid),
            str(source_snapshot_uid),
            output.input_snapshot_count,
            len(output.warnings),
            self._actor,
            self._actor,
        ).fetchone()
        return int(row[0])

    def _insert_engine_output(
        self,
        cursor: pyodbc.Cursor,
        run_id: int,
        engine_id: int,
        instrument_id: int,
        output: OptionChainIntelligenceOutputV1,
        source_message_uid: UUID,
        source_snapshot_uid: UUID,
        existing,
    ) -> int:
        raw_json = output.model_dump_json(by_alias=True)
        contract_hash = hashlib.sha256(raw_json.encode("utf-8")).hexdigest().upper()
        freshness_ms = max(
            0,
            int((output.generated_at_utc - output.as_of_utc).total_seconds() * 1000),
        )
        metadata_json = json.dumps(
            {
                "policyVersion": output.policy_version,
                "sourceSnapshotUid": str(source_snapshot_uid),
                "sourceMessageUid": str(source_message_uid),
                "ivTermStructureState": output.iv_term_structure_state,
                "selectionAuthority": False,
                "executionAuthority": False,
            },
            separators=(",", ":"),
        )
        expires_at = output.generated_at_utc + timedelta(
            seconds=self._maximum_output_age_seconds
        )
        row = cursor.execute(
            """
            INSERT INTO [intelligence].[engine_outputs]
            ([engine_output_uid], [message_uid], [engine_run_id], [engine_id],
             [instrument_id], [contract_version], [environment], [source_service],
             [source_version], [engine_name_snapshot], [engine_version], [timeframe],
             [as_of_utc], [generated_at_utc], [expires_at_utc], [direction], [score],
             [confidence], [data_quality_status], [data_completeness],
             [freshness_milliseconds], [missing_fields_json], [is_stale],
             [is_eligible_for_fusion], [revision], [supersedes_engine_output_uid],
             [is_current], [correlation_id], [causation_id], [metadata_json],
             [raw_contract_json], [contract_hash], [created_by])
            OUTPUT INSERTED.[engine_output_id]
            VALUES (?, ?, ?, ?, ?, '1.0.0', 'PAPER', 'ThesisPulse.AI', ?, ?, ?,
                    'OPTION_CHAIN', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1,
                    ?, ?, ?, ?, ?, ?);
            """,
            str(output.output_uid),
            str(output.message_uid),
            run_id,
            engine_id,
            instrument_id,
            self._service_version,
            self._engine_code,
            output.engine_version,
            output.as_of_utc,
            output.generated_at_utc,
            expires_at,
            output.direction,
            output.score,
            output.confidence,
            output.data_quality_status,
            output.component_coverage,
            freshness_ms,
            json.dumps(output.warnings),
            output.is_stale,
            output.is_eligible_for_fusion,
            output.revision,
            None if existing is None else str(existing[1]),
            str(source_message_uid),
            str(source_snapshot_uid),
            metadata_json,
            raw_json,
            contract_hash,
            self._actor,
        ).fetchone()
        return int(row[0])

    def _insert_message_input(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        inbox_message_id: int,
        consumed_at_utc: datetime,
    ) -> None:
        cursor.execute(
            """
            INSERT INTO [intelligence].[engine_output_message_inputs]
            ([engine_output_id], [inbox_message_id], [input_role], [input_sequence],
             [consumed_at_utc], [created_by])
            VALUES (?, ?, 'PRIMARY', 1, ?, ?);
            """,
            output_id,
            inbox_message_id,
            consumed_at_utc,
            self._actor,
        )

    def _insert_snapshot_inputs(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        lineage: list[tuple[PersistedOptionChainSnapshot, str]],
        consumed_at_utc: datetime,
    ) -> None:
        for sequence, (item, role) in enumerate(lineage, start=1):
            cursor.execute(
                """
                INSERT INTO [intelligence].[option_chain_output_snapshot_inputs]
                ([engine_output_id], [option_chain_snapshot_id], [input_role],
                 [input_sequence], [consumed_at_utc], [created_by])
                VALUES (?, ?, ?, ?, ?, ?);
                """,
                output_id,
                item.snapshot_id,
                role,
                sequence,
                consumed_at_utc,
                self._actor,
            )

    def _insert_expiry_details(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        output: OptionChainIntelligenceOutputV1,
    ) -> None:
        for expiry in output.expiry_metrics:
            row = cursor.execute(
                """
                INSERT INTO [intelligence].[option_chain_output_expiries]
                ([engine_output_id], [source_snapshot_uid], [expiry_date],
                 [underlying_price], [call_open_interest], [put_open_interest],
                 [pcr_open_interest], [call_volume], [put_volume], [pcr_volume],
                 [max_pain_strike], [max_pain_distance_fraction],
                 [max_pain_magnet_strength], [atm_call_implied_volatility],
                 [atm_put_implied_volatility], [atm_put_call_skew], [rr25_skew],
                 [accepted_contract_count], [accepted_strike_count],
                 [component_coverage], [warnings_json], [created_by])
                OUTPUT INSERTED.[option_chain_output_expiry_id]
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """,
                output_id,
                str(expiry.snapshot_uid),
                expiry.expiry_date,
                expiry.underlying_price,
                expiry.call_open_interest,
                expiry.put_open_interest,
                expiry.pcr_open_interest,
                expiry.call_volume,
                expiry.put_volume,
                expiry.pcr_volume,
                expiry.max_pain_strike,
                expiry.max_pain_distance_fraction,
                expiry.max_pain_magnet_strength,
                expiry.atm_call_implied_volatility,
                expiry.atm_put_implied_volatility,
                expiry.atm_put_call_skew,
                expiry.rr25_skew,
                expiry.accepted_contract_count,
                expiry.accepted_strike_count,
                expiry.component_coverage,
                json.dumps(expiry.warnings),
                self._actor,
            ).fetchone()
            expiry_id = int(row[0])
            self._insert_walls(cursor, expiry_id, expiry.call_walls)
            self._insert_walls(cursor, expiry_id, expiry.put_walls)
            self._insert_oi_flows(cursor, expiry_id, expiry.oi_flows)
            self._insert_max_pain_curve(cursor, expiry_id, expiry.max_pain_curve)

    def _insert_walls(self, cursor: pyodbc.Cursor, expiry_id: int, walls) -> None:
        for wall in walls:
            cursor.execute(
                """
                INSERT INTO [intelligence].[option_chain_output_walls]
                ([option_chain_output_expiry_id], [option_type], [wall_role],
                 [strike_price], [open_interest], [same_side_oi_share],
                 [wall_strength], [distance_fraction], [wall_rank], [created_by])
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """,
                expiry_id,
                wall.option_type,
                wall.role,
                wall.strike_price,
                wall.open_interest,
                wall.same_side_oi_share,
                wall.wall_strength,
                wall.distance_fraction,
                wall.rank,
                self._actor,
            )

    def _insert_oi_flows(self, cursor: pyodbc.Cursor, expiry_id: int, flows) -> None:
        for flow in flows:
            cursor.execute(
                """
                INSERT INTO [intelligence].[option_chain_output_oi_flows]
                ([option_chain_output_expiry_id], [derivative_contract_uid],
                 [instrument_key], [option_type], [strike_price], [previous_premium],
                 [current_premium], [previous_open_interest], [current_open_interest],
                 [premium_change_fraction], [open_interest_change_fraction],
                 [flow_state], [normalized_contribution], [created_by])
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """,
                expiry_id,
                str(flow.derivative_contract_uid),
                flow.instrument_key,
                flow.option_type,
                flow.strike_price,
                flow.previous_premium,
                flow.current_premium,
                flow.previous_open_interest,
                flow.current_open_interest,
                flow.premium_change_fraction,
                flow.open_interest_change_fraction,
                flow.state,
                flow.normalized_contribution,
                self._actor,
            )

    def _insert_max_pain_curve(self, cursor: pyodbc.Cursor, expiry_id: int, curve) -> None:
        for point in curve:
            cursor.execute(
                """
                INSERT INTO [intelligence].[option_chain_output_max_pain_points]
                ([option_chain_output_expiry_id], [settlement_strike], [call_payout],
                 [put_payout], [total_payout], [created_by])
                VALUES (?, ?, ?, ?, ?, ?);
                """,
                expiry_id,
                point.settlement_strike,
                point.call_payout,
                point.put_payout,
                point.total_payout,
                self._actor,
            )

    def _insert_iv_term_points(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        output: OptionChainIntelligenceOutputV1,
    ) -> None:
        for point in output.iv_term_structure:
            cursor.execute(
                """
                INSERT INTO [intelligence].[option_chain_output_iv_term_points]
                ([engine_output_id], [source_snapshot_uid], [expiry_date],
                 [days_to_expiry], [atm_strike_price], [call_implied_volatility],
                 [put_implied_volatility], [atm_implied_volatility], [pair_method],
                 [created_by])
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """,
                output_id,
                str(point.snapshot_uid),
                point.expiry_date,
                point.days_to_expiry,
                point.atm_strike_price,
                point.call_implied_volatility,
                point.put_implied_volatility,
                point.atm_implied_volatility,
                point.pair_method,
                self._actor,
            )

    def _insert_evidence(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        output: OptionChainIntelligenceOutputV1,
    ) -> None:
        for evidence in output.evidence:
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_evidence]
                ([engine_output_id], [evidence_code], [evidence_message], [impact],
                 [weight], [created_by])
                VALUES (?, ?, ?, ?, ?, ?);
                """,
                output_id,
                evidence.code,
                evidence.message,
                evidence.impact,
                evidence.weight,
                self._actor,
            )

    def _insert_warnings(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        output: OptionChainIntelligenceOutputV1,
    ) -> None:
        for warning in output.warnings:
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_warnings]
                ([engine_output_id], [warning_code], [warning_message], [created_by])
                VALUES (?, ?, ?, ?);
                """,
                output_id,
                warning,
                warning.replace("_", " ").title(),
                self._actor,
            )

    def _complete_inbox(
        self,
        cursor: pyodbc.Cursor,
        message_uid: UUID,
        result_reference: str,
        processed_at_utc: datetime,
    ) -> None:
        cursor.execute(
            """
            UPDATE [operations].[inbox_messages]
            SET [status] = 'PROCESSED', [processed_at_utc] = ?,
                [result_reference] = ?, [lease_owner] = NULL,
                [lease_expires_at_utc] = NULL, [updated_at_utc] = ?,
                [updated_by] = ?
            WHERE [consumer_name] = ? AND [message_uid] = ?;
            """,
            processed_at_utc,
            result_reference[:300],
            processed_at_utc,
            self._actor,
            self._actor,
            str(message_uid),
        )

    def _mark_processed(self, processed_at: datetime) -> None:
        with self._status_sync:
            self._latest_processed_at_utc = processed_at
            self._latest_error = None

    def _mark_error(self, exception: Exception) -> None:
        with self._status_sync:
            self._latest_error = str(exception)[:2000]


def _snapshot_json(
    source_message_uid: UUID,
    snapshot: OptionChainSnapshotObservation,
) -> str:
    return json.dumps(
        {
            "sourceMessageUid": str(source_message_uid),
            "snapshotUid": str(snapshot.snapshot_uid),
            "underlyingInstrumentKey": snapshot.underlying_instrument_key,
            "expiryDate": snapshot.expiry_date.isoformat(),
            "eventAtUtc": _as_utc(snapshot.event_at_utc).isoformat(),
            "receivedAtUtc": _as_utc(snapshot.received_at_utc).isoformat(),
            "revision": snapshot.revision,
            "entryCount": len(snapshot.entries),
        },
        separators=(",", ":"),
    )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
