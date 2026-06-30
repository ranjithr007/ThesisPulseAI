from collections import defaultdict
from datetime import UTC, datetime
from threading import RLock
from uuid import UUID

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.liquidity_derivatives.models import (
    LiquidityDerivativesCandle,
    LiquidityDerivativesStoreOutcome,
    LiquidityDerivativesStoreStatus,
    StoredLiquidityDerivativesOutput,
)


class InMemoryLiquidityDerivativesStore:
    provider_name = "InMemory"

    def __init__(self, maximum_input_count: int = 128) -> None:
        if maximum_input_count < 10:
            raise ValueError("maximum_input_count must be at least ten")
        self._maximum_input_count = maximum_input_count
        self._candles: dict[
            tuple[str, str],
            dict[datetime, LiquidityDerivativesCandle],
        ] = defaultdict(dict)
        self._outputs_by_source: dict[
            UUID,
            StoredLiquidityDerivativesOutput,
        ] = {}
        self._outputs_by_cutoff: dict[
            tuple[str, str, datetime],
            StoredLiquidityDerivativesOutput,
        ] = {}
        self._latest_by_scope: dict[
            tuple[str, str],
            StoredLiquidityDerivativesOutput,
        ] = {}
        self._sync = RLock()
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator,
        processed_at_utc: datetime,
    ) -> LiquidityDerivativesStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        metadata = delivery.envelope.metadata
        payload = delivery.envelope.payload
        with self._sync:
            duplicate = self._outputs_by_source.get(metadata.message_id)
            if duplicate is not None:
                return LiquidityDerivativesStoreOutcome(
                    outcome="DUPLICATE",
                    output=duplicate.output,
                    engine_output_id=duplicate.engine_output_id,
                    reason="The source candle was already processed",
                )
            if payload.timeframe != "5m" or not payload.is_closed or payload.is_provisional:
                return LiquidityDerivativesStoreOutcome(
                    outcome="IGNORED_INELIGIBLE",
                    reason="Liquidity Context requires a closed 5m candle",
                )

            scope = (payload.instrument_key, payload.timeframe)
            candle = LiquidityDerivativesCandle(
                candle_id=None,
                source_message_uid=metadata.message_id,
                instrument_key=payload.instrument_key,
                timeframe=payload.timeframe,
                open_at_utc=_as_utc(payload.open_at_utc),
                close_at_utc=_as_utc(payload.close_at_utc),
                open_price=payload.open_price,
                high_price=payload.high_price,
                low_price=payload.low_price,
                close_price=payload.close_price,
                volume_quantity=payload.volume_quantity,
                open_interest=payload.open_interest,
                revision=payload.revision,
                received_at_utc=_as_utc(payload.received_at_utc),
                quality_status=payload.quality_status,
                is_usable_for_new_exposure=payload.is_usable_for_new_exposure,
            )
            existing_candle = self._candles[scope].get(candle.open_at_utc)
            if existing_candle is None or (
                candle.revision,
                candle.received_at_utc,
            ) >= (
                existing_candle.revision,
                existing_candle.received_at_utc,
            ):
                self._candles[scope][candle.open_at_utc] = candle

            cutoff = _as_utc(metadata.occurred_at_utc)
            window = [
                item
                for item in self._candles[scope].values()
                if item.close_at_utc <= candle.close_at_utc
                and item.received_at_utc <= cutoff
            ]
            window.sort(key=lambda item: item.open_at_utc)
            window = window[-self._maximum_input_count :]
            cutoff_key = (
                payload.instrument_key,
                payload.timeframe,
                candle.close_at_utc,
            )
            previous = self._outputs_by_cutoff.get(cutoff_key)
            revision = 0 if previous is None else previous.output.revision + 1
            output = calculator.calculate(
                delivery,
                window,
                max(processed_at, candle.close_at_utc),
                revision,
            )
            stored = StoredLiquidityDerivativesOutput(
                engine_output_id=None,
                output=output,
                input_candle_ids=tuple(),
            )
            self._outputs_by_source[metadata.message_id] = stored
            self._outputs_by_cutoff[cutoff_key] = stored
            current = self._latest_by_scope.get(scope)
            if current is None or (
                output.as_of_utc,
                output.revision,
            ) >= (
                current.output.as_of_utc,
                current.output.revision,
            ):
                self._latest_by_scope[scope] = stored
            self._latest_processed_at_utc = processed_at
            self._latest_error = None
            return LiquidityDerivativesStoreOutcome(
                outcome="CREATED" if previous is None else "REVISED",
                output=output,
            )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredLiquidityDerivativesOutput | None:
        with self._sync:
            return self._latest_by_scope.get((instrument_key, timeframe))

    def get_status(self) -> LiquidityDerivativesStoreStatus:
        with self._sync:
            return LiquidityDerivativesStoreStatus(
                provider=self.provider_name,
                candle_count=sum(len(items) for items in self._candles.values()),
                output_count=len(self._outputs_by_source),
                latest_processed_at_utc=self._latest_processed_at_utc,
                latest_error=self._latest_error,
            )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
