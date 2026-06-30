import hashlib
import json
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from threading import RLock
from uuid import NAMESPACE_URL, UUID, uuid5

import pyodbc

from app.contracts.v1.market_data import FeatureSnapshotV1, MarketCandleDeliveryV1
from app.features.calculator import DeterministicFeatureCalculator
from app.features.definitions import FRESHNESS_LIMITS
from app.features.models import (
    CandleInput,
    ExistingSnapshotRevision,
    FeatureStoreProcessOutcome,
    FeatureStoreStatus,
    StoredFeatureSnapshot,
)


class SqlServerFeatureFactoryStore:
    provider_name = "SqlServer"

    def __init__(
        self,
        connection_string: str,
        *,
        actor: str = "ThesisPulse.AI.FeatureFactory",
        engine_code: str = "THESIS_PULSE_FEATURE_FACTORY",
        broker_code: str = "UPSTOX",
        service_version: str = "0.2.0",
        command_timeout_seconds: int = 30,
    ) -> None:
        if not connection_string.strip():
            raise ValueError("SQL Server connection string is required")
        self._connection_string = connection_string
        self._actor = actor
        self._engine_code = engine_code
        self._broker_code = broker_code
        self._service_version = service_version
        self._command_timeout_seconds = command_timeout_seconds
        self._status_sync = RLock()
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator: DeterministicFeatureCalculator,
        processed_at_utc: datetime,
    ) -> FeatureStoreProcessOutcome:
        processed_at_utc = _as_utc(processed_at_utc)
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            existing_status = self._claim_inbox(cursor, delivery, processed_at_utc)
            if existing_status == "PROCESSED":
                connection.rollback()
                return FeatureStoreProcessOutcome(
                    outcome="DUPLICATE",
                    snapshot=None,
                    reason="Message was already processed",
                )

            payload = delivery.envelope.payload
            if not payload.is_closed or payload.is_provisional:
                self._complete_inbox(
                    cursor,
                    delivery.envelope.metadata.message_id,
                    "IGNORED_PROVISIONAL",
                    processed_at_utc,
                )
                connection.commit()
                self._mark_processed(processed_at_utc)
                return FeatureStoreProcessOutcome(
                    outcome="IGNORED_PROVISIONAL",
                    snapshot=None,
                    reason="Feature snapshots require closed non-provisional candles",
                )

            engine_id = self._resolve_engine_id(cursor)
            instrument_id = self._resolve_instrument_id(
                cursor,
                payload.instrument_key,
            )
            window = self._load_candle_window(
                cursor,
                instrument_id,
                payload.timeframe,
                payload.close_at_utc,
                delivery.envelope.metadata.occurred_at_utc,
                calculator.options.maximum_input_count,
            )
            if not window:
                raise RuntimeError(
                    "No normalized SQL candle was available for the published event"
                )

            existing = self._read_current_snapshot(
                cursor,
                engine_id,
                instrument_id,
                payload.timeframe,
                payload.close_at_utc,
            )
            revision = 0 if existing is None else existing.revision + 1
            generated_at_utc = max(
                processed_at_utc,
                _as_utc(payload.close_at_utc),
            )
            snapshot = calculator.calculate(
                delivery,
                window,
                generated_at_utc,
                revision,
            )
            engine_run_id = self._insert_engine_run(
                cursor,
                engine_id,
                snapshot,
                delivery,
                generated_at_utc,
            )
            if existing is not None:
                cursor.execute(
                    "UPDATE [intelligence].[engine_outputs] "
                    "SET [is_current] = 0 WHERE [engine_output_id] = ?",
                    existing.engine_output_id,
                )
            engine_output_id = self._insert_engine_output(
                cursor,
                engine_run_id,
                engine_id,
                instrument_id,
                snapshot,
                delivery,
                existing,
            )
            self._insert_input_lineage(
                cursor,
                engine_output_id,
                window,
                processed_at_utc,
            )
            self._insert_features(cursor, engine_output_id, snapshot)
            self._insert_warnings(cursor, engine_output_id, snapshot)
            self._complete_inbox(
                cursor,
                delivery.envelope.metadata.message_id,
                str(snapshot.snapshot_uid),
                processed_at_utc,
            )
            connection.commit()
            self._mark_processed(processed_at_utc)
            return FeatureStoreProcessOutcome(
                outcome="CREATED" if existing is None else "REVISED",
                snapshot=snapshot,
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
    ) -> StoredFeatureSnapshot | None:
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
                  AND output.[timeframe] = ?
                  AND output.[is_current] = 1
                ORDER BY output.[as_of_utc] DESC, output.[revision] DESC;
                """,
                self._engine_code,
                instrument_id,
                timeframe,
            ).fetchone()
            if row is None:
                return None
            snapshot = FeatureSnapshotV1.model_validate_json(row[1])
            input_rows = cursor.execute(
                """
                SELECT [candle_id]
                FROM [intelligence].[engine_output_market_inputs]
                WHERE [engine_output_id] = ? AND [candle_id] IS NOT NULL
                ORDER BY [engine_output_market_input_id];
                """,
                int(row[0]),
            ).fetchall()
            return StoredFeatureSnapshot(
                engine_output_id=int(row[0]),
                snapshot=snapshot,
                input_candle_ids=tuple(int(item[0]) for item in input_rows),
            )
        finally:
            connection.close()

    def get_status(self) -> FeatureStoreStatus:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            processed = cursor.execute(
                """
                SELECT COUNT_BIG(*)
                FROM [operations].[inbox_messages]
                WHERE [consumer_name] = ? AND [status] = 'PROCESSED';
                """,
                self._actor,
            ).fetchone()
            snapshots = cursor.execute(
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
                return FeatureStoreStatus(
                    provider=self.provider_name,
                    processed_messages=int(processed[0]),
                    snapshot_count=int(snapshots[0]),
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
        delivery: MarketCandleDeliveryV1,
        received_at_utc: datetime,
    ) -> str | None:
        metadata = delivery.envelope.metadata
        row = cursor.execute(
            """
            SELECT [status], [attempt_count], [max_attempts]
            FROM [operations].[inbox_messages] WITH (UPDLOCK, HOLDLOCK)
            WHERE [consumer_name] = ? AND [message_uid] = ?;
            """,
            self._actor,
            str(metadata.message_id),
        ).fetchone()
        if row is not None and str(row[0]) == "PROCESSED":
            return "PROCESSED"

        payload_json = delivery.model_dump_json(by_alias=True)
        payload_hash = hashlib.sha256(payload_json.encode("utf-8")).hexdigest().upper()
        occurred_at = _as_utc(metadata.occurred_at_utc)
        received_at = max(received_at_utc, occurred_at)
        correlation_id = _to_uuid(metadata.correlation_id)
        causation_id = (
            _to_uuid(metadata.causation_id) if metadata.causation_id else None
        )
        lease_expires = received_at + timedelta(minutes=5)

        if row is None:
            cursor.execute(
                """
                INSERT INTO [operations].[inbox_messages]
                ([message_uid], [consumer_name], [contract_version], [environment],
                 [message_type], [source_service], [source_version], [correlation_id],
                 [causation_id], [generated_at_utc], [received_at_utc], [expires_at_utc],
                 [payload_json], [payload_hash], [headers_json], [status],
                 [attempt_count], [max_attempts], [lease_owner], [lease_expires_at_utc],
                 [created_by], [updated_by])
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, ?, ?, NULL,
                        'PROCESSING', 1, 5, ?, ?, ?, ?);
                """,
                str(metadata.message_id),
                self._actor,
                metadata.contract_version,
                metadata.environment,
                metadata.event_type,
                metadata.producer,
                metadata.producer_version,
                str(correlation_id),
                str(causation_id) if causation_id else None,
                occurred_at,
                received_at,
                payload_json,
                payload_hash,
                self._actor,
                lease_expires,
                self._actor,
                self._actor,
            )
            return None

        if int(row[1]) >= int(row[2]):
            raise RuntimeError("Feature Factory inbox retry limit was reached")
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
            str(metadata.message_id),
        )
        return str(row[0])

    def _resolve_engine_id(self, cursor: pyodbc.Cursor) -> int:
        row = cursor.execute(
            """
            SELECT TOP (1) [engine_id]
            FROM [intelligence].[engines]
            WHERE [engine_code] = ? AND [owner_service] = 'ThesisPulse.AI'
              AND [engine_role] = 'CONTEXT_PROVIDER'
              AND [can_create_signals] = 0 AND [can_execute_orders] = 0
              AND [is_active] = 1;
            """,
            self._engine_code,
        ).fetchone()
        if row is None:
            raise RuntimeError(
                f"Active Feature Factory engine '{self._engine_code}' was not found"
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

    def _load_candle_window(
        self,
        cursor: pyodbc.Cursor,
        instrument_id: int,
        timeframe: str,
        as_of_utc: datetime,
        cutoff_utc: datetime,
        maximum_count: int,
    ) -> list[CandleInput]:
        sql = f"""
            WITH ranked AS
            (
                SELECT candle.[candle_id], candle.[open_at_utc],
                    candle.[close_at_utc], candle.[open_price], candle.[high_price],
                    candle.[low_price], candle.[close_price], candle.[volume_qty],
                    candle.[revision], candle.[received_at_utc],
                    candle.[quality_status], candle.[is_usable_for_new_exposure],
                    ROW_NUMBER() OVER
                    (
                        PARTITION BY candle.[open_at_utc]
                        ORDER BY candle.[revision] DESC, candle.[received_at_utc] DESC
                    ) AS [point_in_time_rank]
                FROM [market].[candles] candle
                WHERE candle.[instrument_id] = ? AND candle.[timeframe] = ?
                  AND candle.[close_at_utc] <= ? AND candle.[received_at_utc] <= ?
                  AND candle.[is_closed] = 1
            )
            SELECT TOP ({maximum_count}) [candle_id], [open_at_utc], [close_at_utc],
                [open_price], [high_price], [low_price], [close_price], [volume_qty],
                [revision], [received_at_utc], [quality_status],
                [is_usable_for_new_exposure]
            FROM ranked
            WHERE [point_in_time_rank] = 1
            ORDER BY [open_at_utc] DESC;
            """
        rows = cursor.execute(
            sql,
            instrument_id,
            timeframe,
            _as_utc(as_of_utc),
            _as_utc(cutoff_utc),
        ).fetchall()
        result = [
            CandleInput(
                candle_id=int(row[0]),
                instrument_key="",
                timeframe=timeframe,
                open_at_utc=_as_utc(row[1]),
                close_at_utc=_as_utc(row[2]),
                open_price=Decimal(str(row[3])),
                high_price=Decimal(str(row[4])),
                low_price=Decimal(str(row[5])),
                close_price=Decimal(str(row[6])),
                volume_quantity=Decimal(str(row[7])),
                open_interest=None,
                revision=int(row[8]),
                received_at_utc=_as_utc(row[9]),
                quality_status=str(row[10]),
                is_usable_for_new_exposure=bool(row[11]),
            )
            for row in rows
        ]
        result.reverse()
        return result

    def _read_current_snapshot(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        instrument_id: int,
        timeframe: str,
        as_of_utc: datetime,
    ) -> ExistingSnapshotRevision | None:
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
        return ExistingSnapshotRevision(
            engine_output_id=int(row[0]),
            engine_output_uid=UUID(str(row[1])),
            revision=int(row[2]),
        )

    def _insert_engine_run(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        snapshot: FeatureSnapshotV1,
        delivery: MarketCandleDeliveryV1,
        generated_at_utc: datetime,
    ) -> int:
        metadata = delivery.envelope.metadata
        correlation_id = _to_uuid(metadata.correlation_id)
        causation_id = (
            _to_uuid(metadata.causation_id) if metadata.causation_id else None
        )
        data_cutoff = min(snapshot.data_cutoff_utc, generated_at_utc)
        row = cursor.execute(
            """
            INSERT INTO [intelligence].[engine_runs]
            ([engine_id], [environment], [engine_version], [configuration_version],
             [feature_set_version], [model_version], [data_cutoff_utc],
             [started_at_utc], [completed_at_utc], [status], [correlation_id],
             [causation_id], [input_count], [output_count], [warning_count],
             [created_by], [updated_by])
            OUTPUT INSERTED.[engine_run_id]
            VALUES (?, 'PAPER', ?, ?, ?, NULL, ?, ?, ?, 'SUCCEEDED', ?, ?, ?, 1, ?, ?, ?);
            """,
            engine_id,
            self._service_version,
            metadata.configuration_version,
            snapshot.feature_set_version,
            data_cutoff,
            generated_at_utc,
            generated_at_utc,
            str(correlation_id),
            str(causation_id) if causation_id else None,
            snapshot.input_count,
            len(snapshot.warnings),
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
        snapshot: FeatureSnapshotV1,
        delivery: MarketCandleDeliveryV1,
        existing: ExistingSnapshotRevision | None,
    ) -> int:
        metadata = delivery.envelope.metadata
        raw_json = snapshot.model_dump_json(by_alias=True)
        contract_hash = hashlib.sha256(raw_json.encode("utf-8")).hexdigest().upper()
        expires_at = snapshot.generated_at_utc + FRESHNESS_LIMITS[snapshot.timeframe]
        correlation_id = _to_uuid(metadata.correlation_id)
        causation_id = (
            _to_uuid(metadata.causation_id) if metadata.causation_id else None
        )
        metadata_json = json.dumps(
            {
                "snapshotType": "POINT_IN_TIME_FEATURE_SET",
                "eligibleForEngines": snapshot.is_eligible_for_engines,
                "requiredInputCount": snapshot.required_input_count,
                "warnings": snapshot.warnings,
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
                    ?, ?, ?, 'NO_SIGNAL', 0, ?, ?, ?, ?, ?, ?, 0, ?, ?, 1, ?, ?, ?, ?, ?, ?);
            """,
            str(snapshot.snapshot_uid),
            str(snapshot.message_uid),
            engine_run_id,
            engine_id,
            instrument_id,
            self._service_version,
            self._engine_code,
            self._service_version,
            snapshot.timeframe,
            snapshot.as_of_utc,
            snapshot.generated_at_utc,
            expires_at,
            snapshot.completeness,
            snapshot.data_quality_status,
            snapshot.completeness,
            snapshot.freshness_milliseconds,
            json.dumps(snapshot.missing_features),
            snapshot.is_stale,
            snapshot.revision,
            str(existing.engine_output_uid) if existing else None,
            str(correlation_id),
            str(causation_id) if causation_id else None,
            metadata_json,
            raw_json,
            contract_hash,
            self._actor,
        ).fetchone()
        return int(row[0])

    def _insert_input_lineage(
        self,
        cursor: pyodbc.Cursor,
        engine_output_id: int,
        candles: list[CandleInput],
        consumed_at_utc: datetime,
    ) -> None:
        for index, candle in enumerate(candles):
            if candle.candle_id is None:
                continue
            input_role = "PRIMARY" if index == len(candles) - 1 else "CONTEXT"
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_market_inputs]
                ([engine_output_id], [input_role], [candle_id],
                 [data_quality_assessment_id], [source_observation_id],
                 [consumed_at_utc], [created_by])
                VALUES (?, ?, ?, NULL, NULL, ?, ?);
                """,
                engine_output_id,
                input_role,
                candle.candle_id,
                consumed_at_utc,
                self._actor,
            )

    def _insert_features(
        self,
        cursor: pyodbc.Cursor,
        engine_output_id: int,
        snapshot: FeatureSnapshotV1,
    ) -> None:
        for feature in snapshot.features:
            value_json = (
                json.dumps({"value": str(feature.value)}, separators=(",", ":"))
                if feature.value is not None
                else None
            )
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_features]
                ([engine_output_id], [feature_name], [feature_version],
                 [feature_value_json], [created_by])
                VALUES (?, ?, ?, ?, ?);
                """,
                engine_output_id,
                feature.name,
                feature.version,
                value_json,
                self._actor,
            )

    def _insert_warnings(
        self,
        cursor: pyodbc.Cursor,
        engine_output_id: int,
        snapshot: FeatureSnapshotV1,
    ) -> None:
        for warning in snapshot.warnings:
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_warnings]
                ([engine_output_id], [warning_code], [warning_message], [created_by])
                VALUES (?, ?, ?, ?);
                """,
                engine_output_id,
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
                [lease_expires_at_utc] = NULL, [updated_at_utc] = SYSUTCDATETIME(),
                [updated_by] = ?
            WHERE [consumer_name] = ? AND [message_uid] = ?;
            """,
            processed_at_utc,
            result_reference[:300],
            self._actor,
            self._actor,
            str(message_uid),
        )

    def _mark_processed(self, processed_at_utc: datetime) -> None:
        with self._status_sync:
            self._latest_processed_at_utc = processed_at_utc
            self._latest_error = None


def _to_uuid(value: str) -> UUID:
    try:
        return UUID(value)
    except ValueError:
        return uuid5(NAMESPACE_URL, value)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
