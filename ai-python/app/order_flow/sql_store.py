import hashlib
import json
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from threading import RLock
from uuid import UUID

import pyodbc

from app.contracts.v1.market_data import MarketCandleDeliveryV1, MarketQuoteDeliveryV1
from app.contracts.v1.order_flow import OrderFlowEngineOutputV1
from app.order_flow.models import (
    OrderFlowStoreOutcome,
    OrderFlowStoreStatus,
    QuoteSample,
    StoredOrderFlowOutput,
)


class SqlServerOrderFlowStore:
    provider_name = "SqlServer"

    def __init__(
        self,
        connection_string: str,
        *,
        actor: str = "ThesisPulse.AI.OrderFlow",
        engine_code: str = "THESIS_PULSE_ORDER_FLOW",
        broker_code: str = "UPSTOX",
        service_version: str = "0.7.0",
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

    def process_quote(
        self,
        delivery: MarketQuoteDeliveryV1,
        processed_at_utc: datetime,
    ) -> str:
        processed_at = _as_utc(processed_at_utc)
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            status, _ = self._claim_inbox(cursor, delivery, processed_at)
            if status == "PROCESSED":
                connection.rollback()
                return "DUPLICATE"
            payload = delivery.envelope.payload
            outcome = (
                "CREATED"
                if payload.quality_status == "VALID"
                and payload.is_usable_for_new_exposure
                else "IGNORED_INELIGIBLE"
            )
            self._complete_inbox(
                cursor,
                delivery.envelope.metadata.message_id,
                outcome,
                processed_at,
            )
            connection.commit()
            self._mark_processed(processed_at)
            return outcome
        except Exception as exception:
            connection.rollback()
            self._mark_error(exception)
            raise
        finally:
            connection.close()

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator,
        processed_at_utc: datetime,
    ) -> OrderFlowStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            inbox_status, candle_inbox_id = self._claim_inbox(
                cursor,
                delivery,
                processed_at,
            )
            if inbox_status == "PROCESSED":
                duplicate = self._read_by_source_message(
                    cursor,
                    delivery.envelope.metadata.message_id,
                )
                connection.rollback()
                return OrderFlowStoreOutcome(
                    outcome="DUPLICATE",
                    output=None if duplicate is None else duplicate.output,
                    engine_output_id=(
                        None if duplicate is None else duplicate.engine_output_id
                    ),
                    reason="The source candle was already processed",
                )

            payload = delivery.envelope.payload
            if (
                payload.timeframe != "5m"
                or not payload.is_closed
                or payload.is_provisional
                or not payload.is_usable_for_new_exposure
                or payload.quality_status != "VALID"
            ):
                self._complete_inbox(
                    cursor,
                    delivery.envelope.metadata.message_id,
                    "IGNORED_INELIGIBLE",
                    processed_at,
                )
                connection.commit()
                self._mark_processed(processed_at)
                return OrderFlowStoreOutcome(
                    outcome="IGNORED_INELIGIBLE",
                    reason="Order Flow requires an eligible closed 5m candle",
                )

            engine_id = self._resolve_engine_id(cursor)
            instrument_id = self._resolve_instrument_id(
                cursor,
                payload.instrument_key,
            )
            quote_rows = self._load_quote_inputs(
                cursor,
                payload.instrument_key,
                payload.open_at_utc,
                payload.close_at_utc,
                delivery.envelope.metadata.occurred_at_utc,
            )
            samples = [row[1] for row in quote_rows]
            existing = self._read_current_revision(
                cursor,
                engine_id,
                instrument_id,
                payload.close_at_utc,
            )
            revision = 0 if existing is None else existing[2] + 1
            generated_at = max(processed_at, _as_utc(payload.close_at_utc))
            output = calculator.calculate(
                delivery,
                samples,
                generated_at,
                revision,
            )
            run_id = self._insert_engine_run(
                cursor,
                engine_id,
                output,
                delivery,
            )
            if existing is not None:
                cursor.execute(
                    "UPDATE [intelligence].[engine_outputs] "
                    "SET [is_current] = 0 WHERE [engine_output_id] = ?",
                    existing[0],
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
            self._insert_message_inputs(
                cursor,
                output_id,
                candle_inbox_id,
                quote_rows,
                generated_at,
            )
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
            return OrderFlowStoreOutcome(
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
    ) -> StoredOrderFlowOutput | None:
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
                SELECT inbox.[message_uid]
                FROM [intelligence].[engine_output_message_inputs] input
                INNER JOIN [operations].[inbox_messages] inbox
                    ON inbox.[inbox_message_id] = input.[inbox_message_id]
                WHERE input.[engine_output_id] = ?
                  AND input.[input_role] = 'QUOTE_CONTEXT'
                ORDER BY input.[input_sequence];
                """,
                output_id,
            ).fetchall()
            return StoredOrderFlowOutput(
                engine_output_id=output_id,
                output=OrderFlowEngineOutputV1.model_validate_json(row[1]),
                quote_message_uids=tuple(UUID(str(item[0])) for item in inputs),
            )
        finally:
            connection.close()

    def get_status(self) -> OrderFlowStoreStatus:
        connection = self._connect()
        try:
            cursor = connection.cursor()
            cursor.timeout = self._command_timeout_seconds
            quote_count = cursor.execute(
                """
                SELECT COUNT_BIG(*)
                FROM [operations].[inbox_messages]
                WHERE [consumer_name] = ?
                  AND [message_type] = 'market.quote.published.v1'
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
                return OrderFlowStoreStatus(
                    provider=self.provider_name,
                    quote_sample_count=int(quote_count[0]),
                    output_count=int(output_count[0]),
                    latest_processed_at_utc=self._latest_processed_at_utc,
                    latest_error=self._latest_error,
                )
        finally:
            connection.close()

    def _connect(self) -> pyodbc.Connection:
        return pyodbc.connect(self._connection_string, autocommit=False)

    def _claim_inbox(self, cursor, delivery, processed_at: datetime) -> tuple[str, int]:
        metadata = delivery.envelope.metadata
        row = cursor.execute(
            """
            SELECT [inbox_message_id], [status], [attempt_count], [max_attempts]
            FROM [operations].[inbox_messages] WITH (UPDLOCK, HOLDLOCK)
            WHERE [consumer_name] = ? AND [message_uid] = ?;
            """,
            self._actor,
            str(metadata.message_id),
        ).fetchone()
        if row is not None and str(row[1]) == "PROCESSED":
            return "PROCESSED", int(row[0])

        payload_json = delivery.model_dump_json(by_alias=True)
        payload_hash = hashlib.sha256(payload_json.encode("utf-8")).hexdigest().upper()
        occurred_at = _as_utc(metadata.occurred_at_utc)
        received_at = max(processed_at, occurred_at)
        lease_expires = received_at + timedelta(minutes=5)
        correlation_id = _uuid(metadata.correlation_id)
        causation_id = _uuid(metadata.causation_id) if metadata.causation_id else None

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
            ).fetchone()
            return "PROCESSING", int(inserted[0])

        if int(row[2]) >= int(row[3]):
            raise RuntimeError("Order Flow inbox retry limit was reached")
        cursor.execute(
            """
            UPDATE [operations].[inbox_messages]
            SET [status] = 'PROCESSING', [attempt_count] = [attempt_count] + 1,
                [lease_owner] = ?, [lease_expires_at_utc] = ?,
                [last_error_code] = NULL, [last_error_message] = NULL,
                [updated_at_utc] = SYSUTCDATETIME(), [updated_by] = ?
            WHERE [inbox_message_id] = ?;
            """,
            self._actor,
            lease_expires,
            self._actor,
            int(row[0]),
        )
        return "PROCESSING", int(row[0])

    def _load_quote_inputs(
        self,
        cursor,
        instrument_key: str,
        open_at_utc: datetime,
        close_at_utc: datetime,
        cutoff_utc: datetime,
    ) -> list[tuple[int, QuoteSample]]:
        rows = cursor.execute(
            """
            SELECT [inbox_message_id], [payload_json]
            FROM [operations].[inbox_messages]
            WHERE [consumer_name] = ?
              AND [message_type] = 'market.quote.published.v1'
              AND [status] = 'PROCESSED'
              AND JSON_VALUE([payload_json], '$.envelope.payload.instrumentKey') = ?
              AND TRY_CONVERT(datetime2(7),
                    JSON_VALUE([payload_json], '$.envelope.payload.eventAtUtc'), 127) > ?
              AND TRY_CONVERT(datetime2(7),
                    JSON_VALUE([payload_json], '$.envelope.payload.eventAtUtc'), 127) <= ?
              AND [received_at_utc] <= ?
            ORDER BY TRY_CONVERT(datetime2(7),
                        JSON_VALUE([payload_json], '$.envelope.payload.eventAtUtc'), 127),
                     [inbox_message_id];
            """,
            self._actor,
            instrument_key,
            _as_utc(open_at_utc),
            _as_utc(close_at_utc),
            _as_utc(cutoff_utc),
        ).fetchall()
        result: list[tuple[int, QuoteSample]] = []
        for row in rows:
            delivery = MarketQuoteDeliveryV1.model_validate_json(str(row[1]))
            payload = delivery.envelope.payload
            result.append(
                (
                    int(row[0]),
                    QuoteSample(
                        message_uid=delivery.envelope.metadata.message_id,
                        instrument_key=payload.instrument_key,
                        event_at_utc=_as_utc(payload.event_at_utc),
                        received_at_utc=_as_utc(payload.received_at_utc),
                        last_traded_price=payload.last_traded_price,
                        last_traded_quantity=payload.last_traded_quantity,
                        open_interest=payload.open_interest,
                        total_buy_quantity=payload.total_buy_quantity,
                        total_sell_quantity=payload.total_sell_quantity,
                        quality_status=payload.quality_status,
                        is_usable_for_new_exposure=payload.is_usable_for_new_exposure,
                    ),
                )
            )
        return result

    def _resolve_engine_id(self, cursor) -> int:
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
                f"Active Order Flow engine '{self._engine_code}' was not found"
            )
        return int(row[0])

    def _resolve_instrument_id(self, cursor, instrument_key: str) -> int:
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
    def _read_current_revision(cursor, engine_id, instrument_id, as_of_utc):
        return cursor.execute(
            """
            SELECT TOP (1) [engine_output_id], [engine_output_uid], [revision]
            FROM [intelligence].[engine_outputs] WITH (UPDLOCK, HOLDLOCK)
            WHERE [engine_id] = ? AND [instrument_id] = ?
              AND [timeframe] = '5m' AND [as_of_utc] = ? AND [is_current] = 1;
            """,
            engine_id,
            instrument_id,
            _as_utc(as_of_utc),
        ).fetchone()

    @staticmethod
    def _read_by_source_message(cursor, source_message_uid: UUID):
        row = cursor.execute(
            """
            SELECT TOP (1) [engine_output_id], [raw_contract_json]
            FROM [intelligence].[engine_outputs]
            WHERE JSON_VALUE([metadata_json], '$.sourceCandleMessageUid') = ?
            ORDER BY [revision] DESC;
            """,
            str(source_message_uid),
        ).fetchone()
        if row is None:
            return None
        output = OrderFlowEngineOutputV1.model_validate_json(row[1])
        return StoredOrderFlowOutput(
            engine_output_id=int(row[0]),
            output=output,
            quote_message_uids=tuple(output.quote_message_uids),
        )

    def _insert_engine_run(self, cursor, engine_id, output, delivery) -> int:
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
                len(output.quote_message_uids) + 1,
                len(output.warnings),
                self._actor,
                self._actor,
            ).fetchone()[0]
        )

    def _insert_engine_output(
        self,
        cursor,
        run_id,
        engine_id,
        instrument_id,
        output,
        delivery,
        existing,
    ) -> int:
        raw_json = output.model_dump_json(by_alias=True)
        contract_hash = hashlib.sha256(raw_json.encode("utf-8")).hexdigest().upper()
        completeness = min(
            Decimal("1"),
            Decimal(output.usable_quote_count)
            / Decimal(max(1, output.quote_sample_count)),
        )
        freshness_ms = 0
        if output.quote_message_uids:
            freshness_ms = max(
                0,
                int(
                    (
                        output.generated_at_utc - output.as_of_utc
                    ).total_seconds()
                    * 1000
                ),
            )
        metadata_json = json.dumps(
            {
                "sourceCandleMessageUid": str(output.source_candle_message_uid),
                "quoteMessageCount": len(output.quote_message_uids),
                "methodology": "PROXY_TICK_RULE_AND_BOOK_TOTALS",
                "policyVersion": output.policy_version,
            },
            separators=(",", ":"),
        )
        missing = []
        if output.open_interest_change_fraction is None:
            missing.append("openInterest")
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
                        ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, ?, ?, ?);
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
                completeness,
                freshness_ms,
                json.dumps(missing) if missing else None,
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

    def _insert_message_inputs(
        self,
        cursor,
        output_id: int,
        candle_inbox_id: int,
        quote_rows: list[tuple[int, QuoteSample]],
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
            candle_inbox_id,
            consumed_at_utc,
            self._actor,
        )
        for sequence, (inbox_id, _) in enumerate(quote_rows, start=2):
            cursor.execute(
                """
                INSERT INTO [intelligence].[engine_output_message_inputs]
                ([engine_output_id], [inbox_message_id], [input_role], [input_sequence],
                 [consumed_at_utc], [created_by])
                VALUES (?, ?, 'QUOTE_CONTEXT', ?, ?, ?);
                """,
                output_id,
                inbox_id,
                sequence,
                consumed_at_utc,
                self._actor,
            )

    def _insert_evidence(self, cursor, output_id: int, output) -> None:
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

    def _insert_warnings(self, cursor, output_id: int, output) -> None:
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
        cursor,
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
