from collections import defaultdict
from datetime import UTC, datetime
from threading import RLock
from uuid import UUID

from app.contracts.v1.market_data import MarketCandleDeliveryV1, MarketQuoteDeliveryV1
from app.order_flow.models import (
    OrderFlowStoreOutcome,
    OrderFlowStoreStatus,
    QuoteSample,
    StoredOrderFlowOutput,
)


class InMemoryOrderFlowStore:
    provider_name = "InMemory"

    def __init__(self, maximum_samples_per_instrument: int = 10000) -> None:
        if maximum_samples_per_instrument < 100:
            raise ValueError("maximum_samples_per_instrument must be at least 100")
        self._maximum_samples = maximum_samples_per_instrument
        self._samples: dict[str, list[QuoteSample]] = defaultdict(list)
        self._seen_quote_messages: set[UUID] = set()
        self._outputs_by_source: dict[UUID, StoredOrderFlowOutput] = {}
        self._latest_by_scope: dict[tuple[str, str], StoredOrderFlowOutput] = {}
        self._sync = RLock()
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process_quote(
        self,
        delivery: MarketQuoteDeliveryV1,
        processed_at_utc: datetime,
    ) -> str:
        processed_at = _as_utc(processed_at_utc)
        metadata = delivery.envelope.metadata
        payload = delivery.envelope.payload
        with self._sync:
            if metadata.message_id in self._seen_quote_messages:
                return "DUPLICATE"
            self._seen_quote_messages.add(metadata.message_id)
            samples = self._samples[payload.instrument_key]
            samples.append(
                QuoteSample(
                    message_uid=metadata.message_id,
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
                )
            )
            samples.sort(key=lambda item: (item.event_at_utc, item.message_uid.int))
            if len(samples) > self._maximum_samples:
                del samples[: len(samples) - self._maximum_samples]
            self._latest_processed_at_utc = processed_at
            self._latest_error = None
            return (
                "CREATED"
                if payload.quality_status == "VALID"
                and payload.is_usable_for_new_exposure
                else "IGNORED_INELIGIBLE"
            )

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator,
        processed_at_utc: datetime,
    ) -> OrderFlowStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        metadata = delivery.envelope.metadata
        payload = delivery.envelope.payload
        with self._sync:
            duplicate = self._outputs_by_source.get(metadata.message_id)
            if duplicate is not None:
                return OrderFlowStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    engine_output_id=duplicate.engine_output_id,
                    reason="The source candle was already processed",
                )
            if (
                payload.timeframe != "5m"
                or not payload.is_closed
                or payload.is_provisional
                or not payload.is_usable_for_new_exposure
                or payload.quality_status != "VALID"
            ):
                return OrderFlowStoreOutcome(
                    outcome="IGNORED_INELIGIBLE",
                    reason="Order Flow requires an eligible closed 5m candle",
                )
            samples = [
                item
                for item in self._samples.get(payload.instrument_key, [])
                if payload.open_at_utc < item.event_at_utc <= payload.close_at_utc
                and item.received_at_utc <= metadata.occurred_at_utc
            ]
            scope = (payload.instrument_key, payload.timeframe)
            existing = self._latest_by_scope.get(scope)
            revision = 0 if existing is None else existing.output.revision + 1
            output = calculator.calculate(
                delivery,
                samples,
                max(processed_at, _as_utc(payload.close_at_utc)),
                revision,
            )
            stored = StoredOrderFlowOutput(
                engine_output_id=None,
                output=output,
                quote_message_uids=tuple(output.quote_message_uids),
            )
            self._outputs_by_source[metadata.message_id] = stored
            self._latest_by_scope[scope] = stored
            self._latest_processed_at_utc = processed_at
            self._latest_error = None
            return OrderFlowStoreOutcome(
                outcome="CREATED" if existing is None else "REVISED",
                output=output,
            )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredOrderFlowOutput | None:
        with self._sync:
            return self._latest_by_scope.get((instrument_key, timeframe))

    def get_status(self) -> OrderFlowStoreStatus:
        with self._sync:
            return OrderFlowStoreStatus(
                provider=self.provider_name,
                quote_sample_count=sum(len(items) for items in self._samples.values()),
                output_count=len(self._outputs_by_source),
                latest_processed_at_utc=self._latest_processed_at_utc,
                latest_error=self._latest_error,
            )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
