from collections import defaultdict
from datetime import UTC, datetime
from threading import RLock
from uuid import UUID

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.features.models import CandleInput
from app.smc.models import SmcStoreOutcome, SmcStoreStatus, StoredSmcOutput


class InMemorySmcStore:
    provider_name = "InMemory"

    def __init__(self, maximum_candles_per_scope: int = 5000) -> None:
        if maximum_candles_per_scope < 64:
            raise ValueError("maximum_candles_per_scope must be at least 64")
        self._maximum_candles = maximum_candles_per_scope
        self._candles: dict[tuple[str, str], list[CandleInput]] = defaultdict(list)
        self._outputs_by_source: dict[UUID, StoredSmcOutput] = {}
        self._latest_by_scope: dict[tuple[str, str], StoredSmcOutput] = {}
        self._sync = RLock()
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process(self, delivery: MarketCandleDeliveryV1, calculator, processed_at_utc: datetime) -> SmcStoreOutcome:
        processed_at = _as_utc(processed_at_utc)
        metadata = delivery.envelope.metadata
        payload = delivery.envelope.payload
        with self._sync:
            duplicate = self._outputs_by_source.get(metadata.message_id)
            if duplicate is not None:
                return SmcStoreOutcome(
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
                return SmcStoreOutcome(
                    outcome="IGNORED_INELIGIBLE",
                    reason="SMC requires an eligible closed 5m candle",
                )

            scope = (payload.instrument_key, payload.timeframe)
            candles = self._candles[scope]
            candles.append(
                CandleInput(
                    candle_id=None,
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
            )
            candles.sort(key=lambda item: (item.open_at_utc, item.revision))
            deduped: dict[datetime, CandleInput] = {}
            for item in candles:
                deduped[item.open_at_utc] = item
            candles[:] = list(deduped.values())[-self._maximum_candles:]

            window = [
                item
                for item in candles
                if item.close_at_utc <= payload.close_at_utc
                and item.received_at_utc <= metadata.occurred_at_utc
            ][-calculator.options.maximum_input_count:]
            existing = self._latest_by_scope.get(scope)
            revision = 0 if existing is None else existing.output.revision + 1
            output = calculator.calculate(
                delivery,
                window,
                max(processed_at, _as_utc(payload.close_at_utc)),
                revision,
            )
            stored = StoredSmcOutput(
                engine_output_id=None,
                output=output,
                input_candle_ids=tuple(),
            )
            self._outputs_by_source[metadata.message_id] = stored
            self._latest_by_scope[scope] = stored
            self._latest_processed_at_utc = processed_at
            self._latest_error = None
            return SmcStoreOutcome(
                outcome="CREATED" if existing is None else "REVISED",
                output=output,
            )

    def get_latest(self, instrument_key: str, timeframe: str) -> StoredSmcOutput | None:
        with self._sync:
            return self._latest_by_scope.get((instrument_key, timeframe))

    def get_status(self) -> SmcStoreStatus:
        with self._sync:
            return SmcStoreStatus(
                provider=self.provider_name,
                output_count=len(self._outputs_by_source),
                latest_processed_at_utc=self._latest_processed_at_utc,
                latest_error=self._latest_error,
            )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
