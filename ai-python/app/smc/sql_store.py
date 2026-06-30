import hashlib
import json
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from threading import RLock
from uuid import UUID

import pyodbc

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smc import SmartMoneyConceptsOutputV1
from app.features.models import CandleInput
from app.smc.models import SmcStoreOutcome, SmcStoreStatus, StoredSmcOutput


class SqlServerSmcStore:
    provider_name = "SqlServer"

    def __init__(
        self,
        connection_string: str,
        *,
        actor: str = "ThesisPulse.AI.SMC",
        engine_code: str = "THESIS_PULSE_SMC",
        broker_code: str = "UPSTOX",
        service_version: str = "0.8.0",
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
        calculator,
        processed_at_utc: datetime,
    ) -> SmcStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        payload = delivery.envelope.payload
        if (
            payload.timeframe != "5m"
            or not payload.is_closed
            or payload.is_provisional
            or not payload.is_usable_for_new_exposure
            or payload.quality_status != "VALID"
        ):
            return SmcStoreOutcome(
                outcome="IGNORED_INELIGIBLE",
                reason="SMC requires an eligible closed 5m candle",
            )

        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            duplicate = self._read_by_source_message(
                cursor,
                delivery.envelope.metadata.message_id,
            )
            if duplicate is not None:
                connection.rollback()
                return SmcStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    engine_output_id=duplicate.engine_output_id,
                    reason="The source candle was already processed",
                )

            engine_id = self._resolve_engine_id(cursor)
            instrument_id = self._resolve_instrument_id(
                cursor,
                payload.instrument_key,
            )
            window = self._load_candle_window(
                cursor,
                instrument_id,
                payload.instrument_key,
                payload.timeframe,
                payload.close_at_utc,
                delivery.envelope.metadata.occurred_at_utc,
                calculator.options.maximum_input_count,
            )
            existing = self._read_current_revision(
                cursor,
                engine_id,
                instrument_id,
                payload.close_at_utc,
            )
            revision = 0 if existing is None else int(existing[2]) + 1
            generated_at = max(
                processed_at,
                _as_utc(payload.close_at_utc),
            )
            output = calculator.calculate(
                delivery,
                window,
                generated_at,
                revision,
            )
            run_id = self._insert_run(
                cursor,
                engine_id,
                output,
                delivery,
                len(window),
            )
            if existing is not None:
                cursor.execute(
                    "UPDATE [intelligence].[engine_outputs] "
                    "SET [is_current] = 0 "
                    "WHERE [engine_output_id] = ?",
                    int(existing[0]),
                )
            output_id = self._insert_output(
                cursor,
                run_id,
                engine_id,
                instrument_id,
                output,
                delivery,
                existing,
            )
            self._insert_inputs(
                cursor,
                output_id,
                window,
                generated_at,
            )
            self._insert_evidence(cursor, output_id, output)
            self._insert_warnings(cursor, output_id, output)
            connection.commit()
            self._mark_processed(processed_at)
            return SmcStoreOutcome(
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
    ) -> StoredSmcOutput | None:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            instrument_id = self._resolve_instrument_id(
                cursor,
                instrument_key,
            )
            row = cursor.execute(
                """
                SELECT TOP (1)
                    output.[engine_output_id],
                    output.[raw_contract_json]
                FROM [intelligence].[engine_outputs] output
                INNER JOIN [intelligence].[engines] engine
                    ON engine.[engine_id] = output.[engine_id]
                WHERE engine.[engine_code] = ?
                  AND output.[instrument_id] = ?
                  AND output.[timeframe] = ?
                  AND output.[is_current] = 1
                ORDER BY
                    output.[as_of_utc] DESC,
                    output.[revision] DESC;
                """,
                self._engine_code,
                instrument_id,
                timeframe,
            ).fetchone()
            if row is None:
                return None
            input_rows = cursor.execute(
                """
                SELECT [candle_id]
                FROM [intelligence].[engine_output_market_inputs]
                WHERE [engine_output_id] = ?
                  AND [candle_id] IS NOT NULL
                ORDER BY [engine_output_market_input_id];
                """,
                int(row[0]),
            ).fetchall()
            return StoredSmcOutput(
                engine_output_id=int(row[0]),
                output=SmartMoneyConceptsOutputV1.model_validate_json(row[1]),
                input_candle_ids=tuple(
                    int(item[0]) for item in input_rows
                ),
            )
        finally:
            connection.close()

    def get_status(self) -> SmcStoreStatus:
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
                return SmcStoreStatus(
                    provider=self.provider_name,
                    output_count=int(row[0]),
                    latest_processed_at_utc=self._latest_processed_at_utc,
                    latest_error=self._latest_error,
                )
        finally:
            connection.close()

    def _connect(self) -> pyodbc.Connection:
        return pyodbc.connect(
            self._connection_string,
            autocommit=False,
        )

    def _resolve_engine_id(self, cursor: pyodbc.Cursor) -> int:
        row = cursor.execute(
            """
            SELECT TOP (1) [engine_id]
            FROM [intelligence].[engines]
            WHERE [engine_code] = ?
              AND [owner_service] = 'ThesisPulse.AI'
              AND [engine_role] = 'DIRECTIONAL_VOTER'
              AND [can_create_signals] = 0
              AND [can_execute_orders] = 0
              AND [is_active] = 1;
            """,
            self._engine_code,
        ).fetchone()
        if row is None:
            raise RuntimeError(
                f"Active SMC engine '{self._engine_code}' was not found"
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
            WHERE broker.[broker_code] = ?
              AND broker.[is_active] = 1
              AND mapping.[broker_instrument_key] = ?
              AND mapping.[is_active] = 1
              AND mapping.[valid_to_date] IS NULL;
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
        instrument_key: str,
        timeframe: str,
        as_of_utc: datetime,
        cutoff_utc: datetime,
        maximum_count: int,
    ) -> list[CandleInput]:
        rows = cursor.execute(
            """
            SELECT TOP (?)
                [candle_id],
                [open_at_utc],
                [close_at_utc],
                [open_price],
                [high_price],
                [low_price],
                [close_price],
                [volume_qty],
                [revision],
                [received_at_utc],
                [quality_status],
                [is_usable_for_new_exposure]
            FROM [market].[candles]
            WHERE [instrument_id] = ?
              AND [timeframe] = ?
              AND [close_at_utc] <= ?
              AND [received_at_utc] <= ?
              AND [is_current] = 1
              AND [is_closed] = 1
              AND [is_provisional] = 0
              AND [is_point_in_time_eligible] = 1
            ORDER BY [close_at_utc] DESC;
            """,
            maximum_count,
            instrument_id,
            timeframe,
            _as_utc(as_of_utc),
            _as_utc(cutoff_utc),
        ).fetchall()
        return [
            CandleInput(
                candle_id=int(row[0]),
                instrument_key=instrument_key,
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
            for row in reversed(rows)
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
            SELECT TOP (1)
                [engine_output_id],
                [engine_output_uid],
                [revision]
            FROM [intelligence].[engine_outputs]
                WITH (UPDLOCK, HOLDLOCK)
            WHERE [engine_id] = ?
              AND [instrument_id] = ?
              AND [timeframe] = '5m'
              AND [as_of_utc] = ?
              AND [is_current] = 1;
            """,
            engine_id,
            instrument_id,
            _as_utc(as_of_utc),
        ).fetchone()

    @staticmethod
    def _read_by_source_message(
        cursor: pyodbc.Cursor,
        source_message_uid: UUID,
    ) -> StoredSmcOutput | None:
        row = cursor.execute(
            """
            SELECT TOP (1)
                [engine_output_id],
                [raw_contract_json]
            FROM [intelligence].[engine_outputs]
            WHERE JSON_VALUE(
                [metadata_json],
                '$.sourceCandleMessageUid'
            ) = ?
            ORDER BY [revision] DESC;
            """,
            str(source_message_uid),
        ).fetchone()
        if row is None:
            return None
        output = SmartMoneyConceptsOutputV1.model_validate_json(row[1])
        return StoredSmcOutput(
            engine_output_id=int(row[0]),
            output=output,
            input_candle_ids=tuple(),
        )

    def _insert_run(
        self,
        cursor: pyodbc.Cursor,
        engine_id: int,
        output: SmartMoneyConceptsOutputV1,
        delivery: MarketCandleDeliveryV1,
        input_count: int,
    ) -> int:
        row = cursor.execute(
            """
            INSERT INTO [intelligence].[engine_runs]
            (
                [engine_run_uid],
                [engine_id],
                [environment],
                [engine_version],
                [configuration_version],
                [data_cutoff_utc],
                [started_at_utc],
                [completed_at_utc],
                [status],
                [correlation_id],
                [causation_id],
                [input_count],
                [output_count],
                [warning_count],
                [created_by],
                [updated_by]
            )
            OUTPUT INSERTED.[engine_run_id]
            VALUES
            (
                NEWID(), ?, 'PAPER', ?, ?, ?, ?, ?, 'SUCCEEDED',
                ?, ?, ?, 1, ?, ?, ?
            );
            """,
            engine_id,
            output.engine_version,
            output.policy_version,
            output.as_of_utc,
            output.generated_at_utc,
            output.generated_at_utc,
            str(UUID(delivery.envelope.metadata.correlation_id)),
            str(delivery.envelope.metadata.message_id),
            input_count,
            len(output.warnings),
            self._actor,
            self._actor,
        ).fetchone()
        return int(row[0])

    def _insert_output(
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
        contract_hash = hashlib.sha256(
            raw_json.encode("utf-8")
        ).hexdigest().upper()
        completeness = min(
            Decimal("1"),
            Decimal(output.input_count)
            / Decimal(output.required_input_count),
        )
        metadata_json = json.dumps(
            {
                "sourceCandleMessageUid": str(
                    output.source_candle_message_uid
                ),
                "policyVersion": output.policy_version,
                "structureEvent": output.structure_event,
                "liquidityEvent": output.liquidity_event,
                "zoneCount": len(output.zones),
            },
            separators=(",", ":"),
        )
        row = cursor.execute(
            """
            INSERT INTO [intelligence].[engine_outputs]
            (
                [engine_output_uid],
                [message_uid],
                [engine_run_id],
                [engine_id],
                [instrument_id],
                [contract_version],
                [environment],
                [source_service],
                [source_version],
                [engine_name_snapshot],
                [engine_version],
                [timeframe],
                [as_of_utc],
                [generated_at_utc],
                [expires_at_utc],
                [direction],
                [score],
                [confidence],
                [data_quality_status],
                [data_completeness],
                [freshness_milliseconds],
                [missing_fields_json],
                [is_stale],
                [is_eligible_for_fusion],
                [revision],
                [supersedes_engine_output_uid],
                [is_current],
                [correlation_id],
                [causation_id],
                [metadata_json],
                [raw_contract_json],
                [contract_hash],
                [created_by]
            )
            OUTPUT INSERTED.[engine_output_id]
            VALUES
            (
                ?, ?, ?, ?, ?, '1.0.0', 'PAPER',
                'ThesisPulse.AI', ?, ?, ?, '5m',
                ?, ?, ?, ?, ?, ?, ?, ?,
                0, NULL, ?, ?, ?, ?, 1,
                ?, ?, ?, ?, ?, ?
            );
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
            output.generated_at_utc + timedelta(minutes=15),
            output.direction,
            output.score,
            output.confidence,
            output.data_quality_status,
            completeness,
            output.is_stale,
            output.is_eligible_for_fusion,
            output.revision,
            None if existing is None else str(existing[1]),
            str(UUID(delivery.envelope.metadata.correlation_id)),
            str(delivery.envelope.metadata.message_id),
            metadata_json,
            raw_json,
            contract_hash,
            self._actor,
        ).fetchone()
        return int(row[0])

    def _insert_inputs(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        candles: list[CandleInput],
        consumed_at_utc: datetime,
    ) -> None:
        for index, candle in enumerate(candles):
            if candle.candle_id is None:
                continue
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_market_inputs]
                (
                    [engine_output_id],
                    [input_role],
                    [candle_id],
                    [consumed_at_utc],
                    [created_by]
                )
                VALUES (?, ?, ?, ?, ?);
                """,
                output_id,
                "PRIMARY" if index == len(candles) - 1 else "CONTEXT",
                candle.candle_id,
                consumed_at_utc,
                self._actor,
            )

    def _insert_evidence(
        self,
        cursor: pyodbc.Cursor,
        output_id: int,
        output: SmartMoneyConceptsOutputV1,
    ) -> None:
        for item in output.evidence:
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_evidence]
                (
                    [engine_output_id],
                    [evidence_code],
                    [evidence_message],
                    [impact],
                    [weight],
                    [created_by]
                )
                VALUES (?, ?, ?, ?, ?, ?);
                """,
                output_id,
                item.code,
                item.message,
                item.impact,
                item.weight,
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
                (
                    [engine_output_id],
                    [warning_code],
                    [warning_message],
                    [created_by]
                )
                VALUES (?, ?, ?, ?);
                """,
                output_id,
                warning,
                warning.replace("_", " ").title(),
                self._actor,
            )

    def _mark_processed(self, processed_at: datetime) -> None:
        with self._status_sync:
            self._latest_processed_at_utc = processed_at
            self._latest_error = None

    def _mark_error(self, exception: Exception) -> None:
        with self._status_sync:
            self._latest_error = str(exception)[:2000]


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
