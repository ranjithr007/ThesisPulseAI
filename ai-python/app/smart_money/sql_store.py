import hashlib
import json
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from threading import RLock
from uuid import UUID

import pyodbc

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smart_money import SmartMoneyConceptsOutputV1
from app.smart_money.models import (
    SmartMoneyCandle,
    SmartMoneyStoreOutcome,
    SmartMoneyStoreStatus,
    StoredSmartMoneyOutput,
)


class SqlServerSmartMoneyStore:
    provider_name = "SqlServer"

    def __init__(
        self,
        connection_string: str,
        *,
        actor: str = "ThesisPulse.AI.SmartMoney",
        engine_code: str = "THESIS_PULSE_SMART_MONEY_CONCEPTS",
        broker_code: str = "UPSTOX",
        service_version: str = "0.8.0",
        maximum_input_count: int = 128,
        command_timeout_seconds: int = 30,
    ) -> None:
        if not connection_string.strip():
            raise ValueError("SQL Server connection string is required")
        if maximum_input_count < 10 or maximum_input_count > 5000:
            raise ValueError("maximum_input_count must be between 10 and 5000")
        self._connection_string = connection_string
        self._actor = actor
        self._engine_code = engine_code
        self._broker_code = broker_code
        self._service_version = service_version
        self._maximum_input_count = maximum_input_count
        self._command_timeout_seconds = command_timeout_seconds
        self._status_sync = RLock()
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator,
        processed_at_utc: datetime,
    ) -> SmartMoneyStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            inbox_status = self._claim_inbox(cursor, delivery, processed_at)
            if inbox_status == "PROCESSED":
                duplicate = self._read_by_source_message(
                    cursor,
                    delivery.envelope.metadata.message_id,
                )
                connection.rollback()
                return SmartMoneyStoreOutcome(
                    outcome="DUPLICATE",
                    output=None if duplicate is None else duplicate.output,
                    engine_output_id=(
                        None if duplicate is None else duplicate.engine_output_id
                    ),
                    reason="The source candle was already processed",
                )

            payload = delivery.envelope.payload
            if payload.timeframe != "5m" or not payload.is_closed or payload.is_provisional:
                self._complete_inbox(
                    cursor,
                    delivery.envelope.metadata.message_id,
                    "IGNORED_INELIGIBLE",
                    processed_at,
                )
                connection.commit()
                self._mark_processed(processed_at)
                return SmartMoneyStoreOutcome(
                    outcome="IGNORED_INELIGIBLE",
                    reason="Smart Money Concepts requires a closed 5m candle",
                )

            engine_id = self._resolve_engine_id(cursor)
            instrument_id = self._resolve_instrument_id(cursor, payload.instrument_key)
            window = self._load_candle_window(
                cursor,
                instrument_id,
                payload.timeframe,
                payload.close_at_utc,
                delivery.envelope.metadata.occurred_at_utc,
            )
            existing = self._read_current_revision(
                cursor,
                engine_id,
                instrument_id,
                payload.close_at_utc,
            )
            revision = 0 if existing is None else int(existing[2]) + 1
            generated_at = max(processed_at, _as_utc(payload.close_at_utc))
            output = calculator.calculate(
                delivery,
                window,
                generated_at,
                revision,
            )
            run_id = self._insert_engine_run(
                cursor,
                engine_id,
                output,
                delivery,
                len(window),
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
                delivery,
                existing,
            )
            self._insert_market_inputs(cursor, output_id, window, payload, generated_at)
            self._insert_evidence(cursor, output_id, output)
            self._insert_warnings(cursor, output_id, output)
            self._complete_inbox(
                cursor,
                delivery.envelope.metadata.message_id,
                str(output.output_uid),
                processed_at,
            )
            connection.commit()
            self._mark_processed(processed_at)
            return SmartMoneyStoreOutcome(
                outcome="CREATED" if existing is None else "REVISED",
                output=output,
                engine_output_id=output_id,
            )
        except Exception as exception:
            connection.rollback()
            self._mark_error(exception)
            raise
        finally:
            connection.close()

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredSmartMoneyOutput | None:
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
            output_id = int(row[0])
            inputs = cursor.execute(
                """
                SELECT [candle_id]
                FROM [intelligence].[engine_output_market_inputs]
                WHERE [engine_output_id] = ? AND [candle_id] IS NOT NULL
                ORDER BY [engine_output_market_input_id];
                """,
                output_id,
            ).fetchall()
            return StoredSmartMoneyOutput(
                engine_output_id=output_id,
                output=SmartMoneyConceptsOutputV1.model_validate_json(row[1]),
                input_candle_ids=tuple(int(item[0]) for item in inputs),
            )
        finally:
            connection.close()

    def get_status(self) -> SmartMoneyStoreStatus:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            candle_count = cursor.execute(
                """
                SELECT COUNT_BIG(*)
                FROM [operations].[inbox_messages]
                WHERE [consumer_name] = ?
                  AND [message_type] = 'market.candle.published.v1'
                  AND [status] = 'PROCESSED';
                """,
                self._actor,
            ).fetchone()
            output_count = cursor.execute(
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
                return SmartMoneyStoreStatus(
                    provider=self.provider_name,
                    candle_count=int(candle_count[0]),
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
        delivery: MarketCandleDeliveryV1,
        processed_at: datetime,
    ) -> str:
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
        received_at = max(processed_at, occurred_at)
        lease_expires = received_at + timedelta(minutes=5)
        correlation_id = _uuid(metadata.correlation_id)
        causation_id = _uuid(metadata.causation_id) if metadata.causation_id else None

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
            return "PROCESSING"

        if int(row[1]) >= int(row[2]):
            raise RuntimeError("Smart Money inbox retry limit was reached")
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
        return "PROCESSING"

    def _load_candle_window(
        self,
        cursor: pyodbc.Cursor,
        instrument_id: int,
        timeframe: str,
        as_of_utc: datetime,
        cutoff_utc: datetime,
    ) -> list[SmartMoneyCandle]:
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
            SELECT TOP ({self._maximum_input_count}) [candle_id], [open_at_utc],
                [close_at_utc], [open_price], [high_price], [low_price],
                [close_price], [volume_qty], [revision], [received_at_utc],
                [quality_status], [is_usable_for_new_exposure]
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
            SmartMoneyCandle(
                candle_id=int(row[0]),
                source_message_uid=None,
                instrument_key="",
                timeframe=timeframe,
                open_at_utc=_as_utc(row[1]),
                close_at_utc=_as_utc(row[2]),
                open_price=Decimal(str(row[3])),
                high_price=Decimal(str(row[4])),
                low_price=Decimal(str(row[5])),
                close_price=Decimal(str(row[6])),
                volume_quantity=Decimal(str(row[7])),
                revision=int(row[8]),
                received_at_utc=_as_utc(row[9]),
                quality_status=str(row[10]),
                is_usable_for_new_exposure=bool(row[11]),
            )
            for row in rows
        ]
        result.reverse()
        return result

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
                f"Active Smart Money engine '{self._engine_code}' was not found"
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
    def _read_current_revision(
        cursor: pyodbc.Cursor,
        engine_id: int,
        instrument_id: int,
        as_of_utc: datetime,
    ):
        return cursor.execute(
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

    def _read_by_source_message(
        self,
        cursor: pyodbc.Cursor,
        source_message_uid: UUID,
    ) -> StoredSmartMoneyOutput | None:
        row = cursor.execute(
            """
            SELECT TOP (1) output.[engine_output_id], output.[raw_contract_json]
            FROM [intelligence].[engine_outputs] output
            INNER JOIN [intelligence].[engines] engine
                ON engine.[engine_id] = output.[engine_id]
            WHERE engine.[engine_code] = ?
              AND JSON_VALUE(output.[metadata_json], '$.sourceCandleMessageUid') = ?
            ORDER BY output.[revision] DESC;
            """,
            self._engine_code,
            str(source_message_uid),
        ).fetchone()
        if row is None:
            return None
        output_id = int(row[0])
        inputs = cursor.execute(
            """
            SELECT [candle_id]
            FROM [intelligence].[engine_output_market_inputs]
            WHERE [engine_output_id] = ? AND [candle_id] IS NOT NULL
            ORDER BY [engine_output_market_input_id];
            """,
            output_id,
        ).fetchall()
        return StoredSmartMoneyOutput(
            engine_output_id=output_id,
            output=SmartMoneyConceptsOutputV1.model_validate_json(row[1]),
            input_candle_ids=tuple(int(item[0]) for item in inputs),
        )

    def _insert_engine_run(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        output: SmartMoneyConceptsOutputV1,
        delivery: MarketCandleDeliveryV1,
        input_count: int,
    ) -> int:
        return int(
            cursor.execute(
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
                str(_uuid(delivery.envelope.metadata.correlation_id)),
                str(delivery.envelope.metadata.message_id),
                input_count,
                len(output.warnings),
                self._actor,
                self._actor,
            ).fetchone()[0]
        )

    def _insert_engine_output(
        self,
        cursor: pyodbc.Cursor,
        run_id: int,
        engine_id: int,
        instrument_id: int,
        output: SmartMoneyConceptsOutputV1,
        delivery: MarketCandleDeliveryV1,
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
                "sourceCandleMessageUid": str(output.source_candle_message_uid),
                "structureEventCount": len(output.structure_events),
                "liquiditySweepCount": len(output.liquidity_sweeps),
                "orderBlockCount": len(output.order_blocks),
                "fairValueGapCount": len(output.fair_value_gaps),
                "policyVersion": output.policy_version,
            },
            separators=(",", ":"),
        )
        return int(
            cursor.execute(
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
                        ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, ?, ?, ?, ?, 1, ?, ?, ?, ?, ?, ?);
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
                output.generated_at_utc + timedelta(minutes=7),
                output.direction,
                output.score,
                output.confidence,
                output.data_quality_status,
                output.completeness,
                freshness_ms,
                output.is_stale,
                output.is_eligible_for_fusion,
                output.revision,
                None if existing is None else str(existing[1]),
                str(_uuid(delivery.envelope.metadata.correlation_id)),
                str(delivery.envelope.metadata.message_id),
                metadata_json,
                raw_json,
                contract_hash,
                self._actor,
            ).fetchone()[0]
        )

    def _insert_market_inputs(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        window: list[SmartMoneyCandle],
        payload,
        consumed_at_utc: datetime,
    ) -> None:
        for item in window:
            if item.candle_id is None:
                continue
            role = (
                "PRIMARY"
                if item.open_at_utc == _as_utc(payload.open_at_utc)
                and item.revision == payload.revision
                else "CONTEXT"
            )
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_market_inputs]
                ([engine_output_id], [input_role], [candle_id],
                 [consumed_at_utc], [created_by])
                VALUES (?, ?, ?, ?, ?);
                """,
                output_id,
                role,
                item.candle_id,
                consumed_at_utc,
                self._actor,
            )

    def _insert_evidence(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        output: SmartMoneyConceptsOutputV1,
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
        output: SmartMoneyConceptsOutputV1,
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


def _uuid(value: str) -> UUID:
    return UUID(value)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
