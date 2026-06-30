import hashlib
import json
from dataclasses import dataclass
from datetime import UTC, datetime
from threading import RLock
from uuid import UUID

import pyodbc

from app.confirmation.calculator import (
    DeterministicMultiTimeframeConfirmationCalculator,
)
from app.confirmation.models import (
    ConfirmationInputBundle,
    ConfirmationStoreOutcome,
    ConfirmationStoreStatus,
    ExistingConfirmationRevision,
    StoredConfirmationOutput,
)
from app.contracts.v1.confirmation import MultiTimeframeConfirmationOutputV1


@dataclass(frozen=True, slots=True)
class _SourceRow:
    engine_output_id: int
    instrument_id: int
    engine_code: str
    timeframe: str
    message_uid: UUID
    correlation_id: UUID
    expires_at_utc: datetime
    is_current: bool
    is_eligible: bool
    data_quality_status: str
    is_stale: bool


class SqlServerMultiTimeframeConfirmationStore:
    provider_name = "SqlServer"

    def __init__(
        self,
        connection_string: str,
        *,
        actor: str = "ThesisPulse.AI.Confirmation",
        engine_code: str = "THESIS_PULSE_MULTI_TIMEFRAME_CONFIRMATION",
        directional_engine_code: str = "THESIS_PULSE_TECHNICAL_DIRECTION",
        regime_engine_code: str = "THESIS_PULSE_MARKET_REGIME",
        broker_code: str = "UPSTOX",
        service_version: str = "0.5.0",
        command_timeout_seconds: int = 30,
    ) -> None:
        if not connection_string.strip():
            raise ValueError("SQL Server connection string is required")
        self._connection_string = connection_string
        self._actor = actor
        self._engine_code = engine_code
        self._directional_engine_code = directional_engine_code
        self._regime_engine_code = regime_engine_code
        self._broker_code = broker_code
        self._service_version = service_version
        self._command_timeout_seconds = command_timeout_seconds
        self._status_sync = RLock()
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process(
        self,
        bundle: ConfirmationInputBundle,
        calculator: DeterministicMultiTimeframeConfirmationCalculator,
        processed_at_utc: datetime,
    ) -> ConfirmationStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        source_ids = _source_ids(bundle)
        if not source_ids:
            raise ValueError("Persisted directional and regime outputs are required")

        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            rows = self._resolve_sources(cursor, source_ids)
            instrument_id = self._validate_sources(bundle, rows, processed_at)
            engine_id = self._resolve_engine_id(cursor)
            source_identity = _source_identity(bundle)
            duplicate = self._read_by_source_identity(
                cursor,
                engine_id,
                instrument_id,
                source_identity,
            )
            if duplicate is not None:
                connection.rollback()
                return ConfirmationStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    engine_output_id=duplicate.engine_output_id,
                    reason="The same intelligence outputs were already confirmed",
                )

            primary = _primary_pair(bundle)
            existing = self._read_current_revision(
                cursor,
                engine_id,
                instrument_id,
                primary.directional.output.as_of_utc,
            )
            revision = 0 if existing is None else existing.revision + 1
            output = calculator.calculate(bundle, processed_at, revision)
            primary_row = rows[primary.directional.engine_output_id or -1]
            expires_at = min(row.expires_at_utc for row in rows.values())
            engine_run_id = self._insert_engine_run(
                cursor,
                engine_id,
                output,
                primary_row,
                len(rows),
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
                instrument_id,
                output,
                primary_row,
                expires_at,
                source_identity,
                existing,
            )
            self._insert_dependencies(
                cursor,
                output_id,
                instrument_id,
                bundle,
                processed_at,
            )
            self._insert_evidence(cursor, output_id, output)
            self._insert_warnings(cursor, output_id, output)
            connection.commit()
            with self._status_sync:
                self._latest_processed_at_utc = processed_at
                self._latest_error = None
            return ConfirmationStoreOutcome(
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

    def get_latest(self, instrument_key: str) -> StoredConfirmationOutput | None:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            instrument_id = self._resolve_instrument_id(cursor, instrument_key)
            row = cursor.execute(
                """
                SELECT TOP (1) output.[engine_output_id], output.[raw_contract_json]
                FROM [intelligence].[engine_outputs] output
                INNER JOIN [intelligence].[engines] engine
                    ON engine.[engine_id] = output.[engine_id]
                WHERE engine.[engine_code] = ?
                  AND output.[instrument_id] = ?
                  AND output.[timeframe] = '5m'
                  AND output.[is_current] = 1
                ORDER BY output.[as_of_utc] DESC, output.[revision] DESC;
                """,
                self._engine_code,
                instrument_id,
            ).fetchone()
            if row is None:
                return None
            dependencies = cursor.execute(
                """
                SELECT [upstream_engine_output_id]
                FROM [intelligence].[engine_output_dependencies]
                WHERE [downstream_engine_output_id] = ?
                ORDER BY [upstream_engine_output_id];
                """,
                int(row[0]),
            ).fetchall()
            return StoredConfirmationOutput(
                engine_output_id=int(row[0]),
                output=MultiTimeframeConfirmationOutputV1.model_validate_json(row[1]),
                source_engine_output_ids=tuple(int(item[0]) for item in dependencies),
            )
        finally:
            connection.close()

    def get_status(self) -> ConfirmationStoreStatus:
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
                return ConfirmationStoreStatus(
                    provider=self.provider_name,
                    output_count=int(row[0]),
                    latest_processed_at_utc=self._latest_processed_at_utc,
                    latest_error=self._latest_error,
                )
        finally:
            connection.close()

    def _connect(self) -> pyodbc.Connection:
        return pyodbc.connect(self._connection_string, autocommit=False)

    def _resolve_sources(
        self,
        cursor: pyodbc.Cursor,
        source_ids: tuple[int, ...],
    ) -> dict[int, _SourceRow]:
        placeholders = ",".join("?" for _ in source_ids)
        rows = cursor.execute(
            f"""
            SELECT output.[engine_output_id], output.[instrument_id], engine.[engine_code],
                output.[timeframe], output.[message_uid], output.[correlation_id],
                output.[expires_at_utc], output.[is_current],
                output.[is_eligible_for_fusion], output.[data_quality_status],
                output.[is_stale]
            FROM [intelligence].[engine_outputs] output WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN [intelligence].[engines] engine
                ON engine.[engine_id] = output.[engine_id]
            WHERE output.[engine_output_id] IN ({placeholders});
            """,
            *source_ids,
        ).fetchall()
        return {
            int(row[0]): _SourceRow(
                engine_output_id=int(row[0]),
                instrument_id=int(row[1]),
                engine_code=str(row[2]),
                timeframe=str(row[3]),
                message_uid=UUID(str(row[4])),
                correlation_id=UUID(str(row[5])),
                expires_at_utc=_as_utc(row[6]),
                is_current=bool(row[7]),
                is_eligible=bool(row[8]),
                data_quality_status=str(row[9]),
                is_stale=bool(row[10]),
            )
            for row in rows
        }

    def _validate_sources(
        self,
        bundle: ConfirmationInputBundle,
        rows: dict[int, _SourceRow],
        processed_at_utc: datetime,
    ) -> int:
        expected_ids = set(_source_ids(bundle))
        if set(rows) != expected_ids:
            raise RuntimeError("One or more persisted intelligence outputs were not found")
        instrument_ids = {row.instrument_id for row in rows.values()}
        if len(instrument_ids) != 1:
            raise ValueError("Confirmation inputs must belong to one instrument")
        for pair in bundle.pairs:
            directional_id = pair.directional.engine_output_id
            regime_id = pair.regime.engine_output_id
            if directional_id is None or regime_id is None:
                raise ValueError("SQL confirmation requires persisted intelligence outputs")
            directional_row = rows[directional_id]
            regime_row = rows[regime_id]
            if directional_row.engine_code != self._directional_engine_code:
                raise ValueError("Unexpected directional engine source")
            if regime_row.engine_code != self._regime_engine_code:
                raise ValueError("Unexpected regime engine source")
            if directional_row.timeframe != pair.timeframe:
                raise ValueError("Directional source timeframe mismatch")
            if regime_row.timeframe != pair.timeframe:
                raise ValueError("Regime source timeframe mismatch")
        for row in rows.values():
            if not row.is_current:
                raise RuntimeError("Intelligence source is no longer current")
            if not row.is_eligible or row.data_quality_status == "INVALID" or row.is_stale:
                raise ValueError("Intelligence source is not eligible for confirmation")
            if row.expires_at_utc <= processed_at_utc:
                raise ValueError("Intelligence source expired before confirmation")
        return next(iter(instrument_ids))

    def _resolve_engine_id(self, cursor: pyodbc.Cursor) -> int:
        row = cursor.execute(
            """
            SELECT TOP (1) [engine_id]
            FROM [intelligence].[engines]
            WHERE [engine_code] = ? AND [owner_service] = 'ThesisPulse.AI'
              AND [engine_role] = 'META_CONTROLLER'
              AND [can_create_signals] = 0 AND [can_execute_orders] = 0
              AND [is_active] = 1;
            """,
            self._engine_code,
        ).fetchone()
        if row is None:
            raise RuntimeError(
                f"Active confirmation engine '{self._engine_code}' was not found"
            )
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

    @staticmethod
    def _read_by_source_identity(
        cursor: pyodbc.Cursor,
        engine_id: int,
        instrument_id: int,
        source_identity: str,
    ) -> StoredConfirmationOutput | None:
        row = cursor.execute(
            """
            SELECT TOP (1) [engine_output_id], [raw_contract_json]
            FROM [intelligence].[engine_outputs]
            WHERE [engine_id] = ? AND [instrument_id] = ?
              AND JSON_VALUE([metadata_json], '$.sourceIdentity') = ?
            ORDER BY [revision] DESC;
            """,
            engine_id,
            instrument_id,
            source_identity,
        ).fetchone()
        if row is None:
            return None
        dependencies = cursor.execute(
            """
            SELECT [upstream_engine_output_id]
            FROM [intelligence].[engine_output_dependencies]
            WHERE [downstream_engine_output_id] = ?
            ORDER BY [upstream_engine_output_id];
            """,
            int(row[0]),
        ).fetchall()
        return StoredConfirmationOutput(
            engine_output_id=int(row[0]),
            output=MultiTimeframeConfirmationOutputV1.model_validate_json(row[1]),
            source_engine_output_ids=tuple(int(item[0]) for item in dependencies),
        )

    @staticmethod
    def _read_current_revision(
        cursor: pyodbc.Cursor,
        engine_id: int,
        instrument_id: int,
        as_of_utc: datetime,
    ) -> ExistingConfirmationRevision | None:
        row = cursor.execute(
            """
            SELECT TOP (1) [engine_output_id], [engine_output_uid], [revision]
            FROM [intelligence].[engine_outputs] WITH (UPDLOCK, HOLDLOCK)
            WHERE [engine_id] = ? AND [instrument_id] = ?
              AND [timeframe] = '5m' AND [as_of_utc] = ? AND [is_current] = 1
            ORDER BY [revision] DESC;
            """,
            engine_id,
            instrument_id,
            _as_utc(as_of_utc),
        ).fetchone()
        if row is None:
            return None
        return ExistingConfirmationRevision(
            engine_output_id=int(row[0]),
            engine_output_uid=UUID(str(row[1])),
            revision=int(row[2]),
        )

    def _insert_engine_run(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        output: MultiTimeframeConfirmationOutputV1,
        primary_row: _SourceRow,
        input_count: int,
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
            VALUES (?, 'PAPER', ?, ?, NULL, NULL, ?, ?, ?, 'SUCCEEDED', ?, ?, ?, 1, ?, ?, ?);
            """,
            engine_id,
            output.engine_version,
            output.policy_version,
            output.as_of_utc,
            output.generated_at_utc,
            output.generated_at_utc,
            str(primary_row.correlation_id),
            str(primary_row.message_uid),
            input_count,
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
        instrument_id: int,
        output: MultiTimeframeConfirmationOutputV1,
        primary_row: _SourceRow,
        expires_at_utc: datetime,
        source_identity: str,
        existing: ExistingConfirmationRevision | None,
    ) -> int:
        raw_json = output.model_dump_json(by_alias=True)
        contract_hash = hashlib.sha256(raw_json.encode("utf-8")).hexdigest().upper()
        metadata_json = json.dumps(
            {
                "policyVersion": output.policy_version,
                "sourceIdentity": source_identity,
                "alignmentScore": str(output.alignment_score),
                "contradictionScore": str(output.contradiction_score),
                "coverage": str(output.coverage),
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
            VALUES (?, ?, ?, ?, ?, '1.0.0', 'PAPER', 'ThesisPulse.AI', ?, ?, ?, '5m',
                    ?, ?, ?, ?, ?, ?, ?, ?, 0, N'[]', 0, ?, ?, ?, 1, ?, ?, ?, ?, ?, ?);
            """,
            str(output.output_uid),
            str(output.message_uid),
            engine_run_id,
            engine_id,
            instrument_id,
            self._service_version,
            self._engine_code,
            output.engine_version,
            output.as_of_utc,
            output.generated_at_utc,
            expires_at_utc,
            output.direction,
            output.score,
            output.confidence,
            output.data_quality_status,
            output.coverage,
            output.is_eligible_for_fusion,
            output.revision,
            str(existing.engine_output_uid) if existing else None,
            str(primary_row.correlation_id),
            str(primary_row.message_uid),
            metadata_json,
            raw_json,
            contract_hash,
            self._actor,
        ).fetchone()
        return int(row[0])

    def _insert_dependencies(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        instrument_id: int,
        bundle: ConfirmationInputBundle,
        consumed_at_utc: datetime,
    ) -> None:
        for pair in bundle.pairs:
            directional_id = pair.directional.engine_output_id
            regime_id = pair.regime.engine_output_id
            if directional_id is None or regime_id is None:
                raise ValueError("Persisted confirmation dependencies are required")
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_dependencies]
                ([downstream_engine_output_id], [upstream_engine_output_id],
                 [instrument_id], [dependency_role], [consumed_at_utc],
                 [metadata_json], [created_by])
                VALUES (?, ?, ?, 'CONFIRMATION', ?, ?, ?);
                """,
                output_id,
                directional_id,
                instrument_id,
                consumed_at_utc,
                json.dumps({"timeframe": pair.timeframe, "sourceType": "DIRECTIONAL"}),
                self._actor,
            )
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_dependencies]
                ([downstream_engine_output_id], [upstream_engine_output_id],
                 [instrument_id], [dependency_role], [consumed_at_utc],
                 [metadata_json], [created_by])
                VALUES (?, ?, ?, 'CONTEXT', ?, ?, ?);
                """,
                output_id,
                regime_id,
                instrument_id,
                consumed_at_utc,
                json.dumps({"timeframe": pair.timeframe, "sourceType": "REGIME"}),
                self._actor,
            )

    def _insert_evidence(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        output: MultiTimeframeConfirmationOutputV1,
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
        output: MultiTimeframeConfirmationOutputV1,
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


def _primary_pair(bundle: ConfirmationInputBundle):
    for pair in bundle.pairs:
        if pair.timeframe == "5m":
            return pair
    raise ValueError("Primary 5m intelligence pair is required")


def _source_ids(bundle: ConfirmationInputBundle) -> tuple[int, ...]:
    return tuple(
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


def _source_identity(bundle: ConfirmationInputBundle) -> str:
    return "|".join(
        sorted(
            f"{pair.timeframe}:{pair.directional.output.output_uid}:"
            f"{pair.regime.output.output_uid}"
            for pair in bundle.pairs
        )
    )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
