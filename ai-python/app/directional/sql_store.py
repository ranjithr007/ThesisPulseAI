import hashlib
import json
from dataclasses import dataclass
from datetime import UTC, datetime
from decimal import Decimal
from threading import RLock
from uuid import UUID

import pyodbc

from app.contracts.v1.directional import DirectionalEngineOutputV1
from app.directional.calculator import DeterministicDirectionalCalculator
from app.directional.models import (
    DirectionalStoreOutcome,
    DirectionalStoreStatus,
    ExistingDirectionalRevision,
    StoredDirectionalOutput,
)
from app.features.models import StoredFeatureSnapshot


@dataclass(frozen=True, slots=True)
class _SourceOutput:
    instrument_id: int
    message_uid: UUID
    correlation_id: UUID
    expires_at_utc: datetime
    is_current: bool
    is_eligible: bool
    data_quality_status: str
    is_stale: bool


class SqlServerDirectionalIntelligenceStore:
    provider_name = "SqlServer"

    def __init__(
        self,
        connection_string: str,
        *,
        actor: str = "ThesisPulse.AI.Directional",
        engine_code: str = "THESIS_PULSE_TECHNICAL_DIRECTION",
        feature_engine_code: str = "THESIS_PULSE_FEATURE_FACTORY",
        broker_code: str = "UPSTOX",
        service_version: str = "0.3.0",
        command_timeout_seconds: int = 30,
    ) -> None:
        if not connection_string.strip():
            raise ValueError("SQL Server connection string is required")
        self._connection_string = connection_string
        self._actor = actor
        self._engine_code = engine_code
        self._feature_engine_code = feature_engine_code
        self._broker_code = broker_code
        self._service_version = service_version
        self._command_timeout_seconds = command_timeout_seconds
        self._status_sync = RLock()
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process(
        self,
        source: StoredFeatureSnapshot,
        calculator: DeterministicDirectionalCalculator,
        processed_at_utc: datetime,
    ) -> DirectionalStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        if source.engine_output_id is None:
            raise ValueError("SQL directional processing requires a persisted feature output")

        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            source_row = self._resolve_source(cursor, source.engine_output_id)
            self._validate_source(source, source_row, processed_at)
            engine_id = self._resolve_engine_id(cursor)
            duplicate = self._read_by_source(cursor, engine_id, source.engine_output_id)
            if duplicate is not None:
                connection.rollback()
                return DirectionalStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    engine_output_id=duplicate.engine_output_id,
                    reason="Feature output was already processed",
                )

            existing = self._read_current_revision(
                cursor,
                engine_id,
                source_row.instrument_id,
                source.snapshot.timeframe,
                source.snapshot.as_of_utc,
            )
            revision = 0 if existing is None else existing.revision + 1
            output = calculator.calculate(source.snapshot, processed_at, revision)
            engine_run_id = self._insert_engine_run(
                cursor,
                engine_id,
                source,
                output,
                source_row,
            )
            if existing is not None:
                cursor.execute(
                    "UPDATE [intelligence].[engine_outputs] "
                    "SET [is_current] = 0 WHERE [engine_output_id] = ?",
                    existing.engine_output_id,
                )
            output_id = self._insert_engine_output(
                cursor,
                engine_run_id,
                engine_id,
                source_row,
                source,
                output,
                existing,
            )
            self._insert_dependency(
                cursor,
                output_id,
                source.engine_output_id,
                source_row.instrument_id,
                output,
                processed_at,
            )
            self._insert_evidence(cursor, output_id, output)
            self._insert_warnings(cursor, output_id, output)
            connection.commit()
            with self._status_sync:
                self._latest_processed_at_utc = processed_at
                self._latest_error = None
            return DirectionalStoreOutcome(
                outcome="CREATED" if existing is None else "REVISED",
                output=output,
                engine_output_id=output_id,
            )
        except Exception as exception:
            connection.rollback()
            with self._status_sync:
                self._latest_error = str(exception)[:2000]
            raise
        finally:
            connection.close()

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredDirectionalOutput | None:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            instrument_id = self._resolve_instrument_id(cursor, instrument_key)
            row = cursor.execute(
                """
                SELECT TOP (1) output.[engine_output_id], output.[raw_contract_json],
                    dependency.[upstream_engine_output_id]
                FROM [intelligence].[engine_outputs] output
                INNER JOIN [intelligence].[engines] engine
                    ON engine.[engine_id] = output.[engine_id]
                LEFT JOIN [intelligence].[engine_output_dependencies] dependency
                    ON dependency.[downstream_engine_output_id] = output.[engine_output_id]
                   AND dependency.[dependency_role] = 'FEATURE_SET'
                WHERE engine.[engine_code] = ?
                  AND output.[instrument_id] = ?
                  AND output.[timeframe] = ?
                  AND output.[is_current] = 1
                ORDER BY output.[as_of_utc] DESC, output.[revision] DESC;
                """,
                self._engine_code,
                instrument_id,
                timeframe,
            ).fetchone()
            return None if row is None else self._stored_from_row(row)
        finally:
            connection.close()

    def get_status(self) -> DirectionalStoreStatus:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            row = cursor.execute(
                """
                SELECT COUNT_BIG(*)
                FROM [intelligence].[engine_outputs] output
                INNER JOIN [intelligence].[engines] engine
                    ON engine.[engine_id] = output.[engine_id]
                WHERE engine.[engine_code] = ?;
                """,
                self._engine_code,
            ).fetchone()
            with self._status_sync:
                return DirectionalStoreStatus(
                    provider=self.provider_name,
                    output_count=int(row[0]),
                    latest_processed_at_utc=self._latest_processed_at_utc,
                    latest_error=self._latest_error,
                )
        finally:
            connection.close()

    def _connect(self) -> pyodbc.Connection:
        return pyodbc.connect(self._connection_string, autocommit=False)

    def _resolve_source(self, cursor: pyodbc.Cursor, output_id: int) -> _SourceOutput:
        row = cursor.execute(
            """
            SELECT output.[instrument_id], output.[message_uid], output.[correlation_id],
                output.[expires_at_utc], output.[is_current],
                output.[is_eligible_for_fusion], output.[data_quality_status],
                output.[is_stale]
            FROM [intelligence].[engine_outputs] output WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [intelligence].[engines] engine
                ON engine.[engine_id] = output.[engine_id]
            WHERE output.[engine_output_id] = ? AND engine.[engine_code] = ?;
            """,
            output_id,
            self._feature_engine_code,
        ).fetchone()
        if row is None:
            raise RuntimeError("Persisted Feature Factory output was not found")
        return _SourceOutput(
            instrument_id=int(row[0]),
            message_uid=UUID(str(row[1])),
            correlation_id=UUID(str(row[2])),
            expires_at_utc=_as_utc(row[3]),
            is_current=bool(row[4]),
            is_eligible=bool(row[5]),
            data_quality_status=str(row[6]),
            is_stale=bool(row[7]),
        )

    @staticmethod
    def _validate_source(
        source: StoredFeatureSnapshot,
        source_row: _SourceOutput,
        processed_at_utc: datetime,
    ) -> None:
        snapshot = source.snapshot
        if not source_row.is_current:
            raise RuntimeError("Feature output is no longer current")
        if not snapshot.is_eligible_for_engines:
            raise ValueError("Feature output is not eligible for directional processing")
        if source_row.data_quality_status != "VALID" or source_row.is_stale:
            raise ValueError("Feature output must be valid and fresh")
        if source_row.expires_at_utc <= processed_at_utc:
            raise ValueError("Feature output expired before directional processing")

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
            raise RuntimeError(f"Active directional engine '{self._engine_code}' was not found")
        return int(row[0])

    def _resolve_instrument_id(self, cursor: pyodbc.Cursor, instrument_key: str) -> int:
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
            raise RuntimeError(f"No active canonical mapping exists for '{instrument_key}'")
        return int(row[0])

    def _read_by_source(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        source_output_id: int,
    ) -> StoredDirectionalOutput | None:
        row = cursor.execute(
            """
            SELECT TOP (1) output.[engine_output_id], output.[raw_contract_json],
                dependency.[upstream_engine_output_id]
            FROM [intelligence].[engine_output_dependencies] dependency
            INNER JOIN [intelligence].[engine_outputs] output
                ON output.[engine_output_id] = dependency.[downstream_engine_output_id]
            WHERE dependency.[upstream_engine_output_id] = ?
              AND dependency.[dependency_role] = 'FEATURE_SET'
              AND output.[engine_id] = ?;
            """,
            source_output_id,
            engine_id,
        ).fetchone()
        return None if row is None else self._stored_from_row(row)

    @staticmethod
    def _read_current_revision(
        cursor: pyodbc.Cursor,
        engine_id: int,
        instrument_id: int,
        timeframe: str,
        as_of_utc: datetime,
    ) -> ExistingDirectionalRevision | None:
        row = cursor.execute(
            """
            SELECT TOP (1) [engine_output_id], [engine_output_uid], [revision]
            FROM [intelligence].[engine_outputs] WITH (UPDLOCK, HOLDLOCK)
            WHERE [engine_id] = ? AND [instrument_id] = ? AND [timeframe] = ?
              AND [as_of_utc] = ? AND [is_current] = 1
            ORDER BY [revision] DESC;
            """,
            engine_id,
            instrument_id,
            timeframe,
            _as_utc(as_of_utc),
        ).fetchone()
        if row is None:
            return None
        return ExistingDirectionalRevision(
            engine_output_id=int(row[0]),
            engine_output_uid=UUID(str(row[1])),
            revision=int(row[2]),
        )

    def _insert_engine_run(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        source: StoredFeatureSnapshot,
        output: DirectionalEngineOutputV1,
        source_row: _SourceOutput,
    ) -> int:
        row = cursor.execute(
            """
            INSERT INTO [intelligence].[engine_runs]
            ([engine_id], [environment], [engine_version], [configuration_version],
             [feature_set_version], [model_version], [data_cutoff_utc],
             [started_at_utc], [completed_at_utc], [status], [correlation_id],
             [causation_id], [input_count], [output_count], [warning_count],
             [created_by], [updated_by])
            OUTPUT INSERTED.[engine_run_id]
            VALUES (?, 'PAPER', ?, ?, ?, NULL, ?, ?, ?, 'SUCCEEDED', ?, ?, 1, 1, ?, ?, ?);
            """,
            engine_id,
            output.engine_version,
            output.policy_version,
            source.snapshot.feature_set_version,
            source.snapshot.data_cutoff_utc,
            output.generated_at_utc,
            output.generated_at_utc,
            str(source_row.correlation_id),
            str(source_row.message_uid),
            len(output.warnings),
            self._actor,
            self._actor,
        ).fetchone()
        return int(row[0])

    def _insert_engine_output(
        self,
        cursor: pyodbc.Cursor,
        engine_run_id: int,
        engine_id: int,
        source_row: _SourceOutput,
        source: StoredFeatureSnapshot,
        output: DirectionalEngineOutputV1,
        existing: ExistingDirectionalRevision | None,
    ) -> int:
        raw_json = output.model_dump_json(by_alias=True)
        contract_hash = hashlib.sha256(raw_json.encode("utf-8")).hexdigest().upper()
        elapsed_seconds = Decimal(
            str(
                (
                    output.generated_at_utc - source.snapshot.generated_at_utc
                ).total_seconds()
            )
        )
        delay_ms = max(0, int(elapsed_seconds * Decimal("1000")))
        freshness_ms = source.snapshot.freshness_milliseconds + delay_ms
        metadata_json = json.dumps(
            {
                "policyVersion": output.policy_version,
                "sourceFeatureSnapshotUid": str(source.snapshot.snapshot_uid),
                "sourceFeatureEngineOutputId": source.engine_output_id,
                "featureSetVersion": source.snapshot.feature_set_version,
            },
            separators=(",", ":"),
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
            VALUES (?, ?, ?, ?, ?, '1.0.0', 'PAPER', 'ThesisPulse.AI', ?, ?, ?, ?,
                    ?, ?, ?, ?, ?, ?, 'VALID', 1, ?, N'[]', 0, 1, ?, ?, 1, ?, ?, ?, ?, ?, ?);
            """,
            str(output.output_uid),
            str(output.message_uid),
            engine_run_id,
            engine_id,
            source_row.instrument_id,
            self._service_version,
            self._engine_code,
            output.engine_version,
            output.timeframe,
            output.as_of_utc,
            output.generated_at_utc,
            source_row.expires_at_utc,
            output.direction,
            output.score,
            output.confidence,
            freshness_ms,
            output.revision,
            str(existing.engine_output_uid) if existing else None,
            str(source_row.correlation_id),
            str(source_row.message_uid),
            metadata_json,
            raw_json,
            contract_hash,
            self._actor,
        ).fetchone()
        return int(row[0])

    def _insert_dependency(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        source_output_id: int,
        instrument_id: int,
        output: DirectionalEngineOutputV1,
        consumed_at_utc: datetime,
    ) -> None:
        metadata = json.dumps(
            {
                "policyVersion": output.policy_version,
                "sourceFeatureSnapshotUid": str(output.source_feature_snapshot_uid),
            },
            separators=(",", ":"),
        )
        cursor.execute(
            """
            INSERT INTO [intelligence].[engine_output_dependencies]
            ([downstream_engine_output_id], [upstream_engine_output_id],
             [instrument_id], [dependency_role], [consumed_at_utc],
             [metadata_json], [created_by])
            VALUES (?, ?, ?, 'FEATURE_SET', ?, ?, ?);
            """,
            output_id,
            source_output_id,
            instrument_id,
            consumed_at_utc,
            metadata,
            self._actor,
        )

    def _insert_evidence(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        output: DirectionalEngineOutputV1,
    ) -> None:
        for evidence in output.evidence:
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_evidence]
                ([engine_output_id], [evidence_code], [evidence_message],
                 [impact], [weight], [created_by])
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
        output: DirectionalEngineOutputV1,
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

    @staticmethod
    def _stored_from_row(row: pyodbc.Row) -> StoredDirectionalOutput:
        return StoredDirectionalOutput(
            engine_output_id=int(row[0]),
            output=DirectionalEngineOutputV1.model_validate_json(row[1]),
            source_engine_output_id=None if row[2] is None else int(row[2]),
        )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
