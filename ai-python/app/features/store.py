from collections.abc import Protocol
from datetime import UTC, datetime
from threading import RLock

from app.contracts.v1.market_data import (
    FeatureSnapshotV1,
    MarketCandleDeliveryV1,
)
from app.features.calculator import DeterministicFeatureCalculator
from app.features.models import (
    CandleInput,
    FeatureStoreProcessOutcome,
    FeatureStoreStatus,
    StoredFeatureSnapshot,
)


class FeatureFactoryStore(Protocol):
    provider_name: str

    def process(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator: DeterministicFeatureCalculator,
        processed_at_utc: datetime,
    ) -> FeatureStoreProcessOutcome: ...

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredFeatureSnapshot | None: ...

    def get_status(self) -> FeatureStoreStatus: ...


class InMemoryFeatureFactoryStore:
    provider_name = "InMemory"

    def __init__(self) -> None:
        self._sync = RLock()
        self._messages: set[str] = set()
        self._candles: dict[tuple[str, str, datetime], CandleInput] = {}
        self._snapshots: dict[
            tuple[str, str, datetime],
            list[StoredFeatureSnapshot],
        ] = {}
        self._processed_messages = 0
        self._latest_processed_at_utc: datetime | None = None
        self._latest_error: str | None = None

    def process(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator: DeterministicFeatureCalculator,
        processed_at_utc: datetime,
    ) -> FeatureStoreProcessOutcome:
        processed_at_utc = _as_utc(processed_at_utc)
        payload = delivery.envelope.payload
        message_key = str(delivery.envelope.metadata.message_id)

        with self._sync:
            if message_key in self._messages:
                return FeatureStoreProcessOutcome(
                    outcome="DUPLICATE",
                    snapshot=None,
                    reason="Message was already processed",
                )

            self._messages.add(message_key)
            self._processed_messages += 1
            self._latest_processed_at_utc = processed_at_utc

            if not payload.is_closed or payload.is_provisional:
                return FeatureStoreProcessOutcome(
                    outcome="IGNORED_PROVISIONAL",
                    snapshot=None,
                    reason="Feature snapshots require closed non-provisional candles",
                )

            candle_key = (
                payload.instrument_key.casefold(),
                payload.timeframe,
                _as_utc(payload.open_at_utc),
            )
            existing_candle = self._candles.get(candle_key)
            if existing_candle is not None and payload.revision < existing_candle.revision:
                return FeatureStoreProcessOutcome(
                    outcome="IGNORED_OUT_OF_ORDER",
                    snapshot=None,
                    reason="A newer candle revision is already present",
                )
            if existing_candle is not None and payload.revision == existing_candle.revision:
                return FeatureStoreProcessOutcome(
                    outcome="DUPLICATE",
                    snapshot=None,
                    reason="Candle revision is already present",
                )

            self._candles[candle_key] = _to_candle_input(delivery)
            window = [
                candle
                for key, candle in self._candles.items()
                if key[0] == payload.instrument_key.casefold()
                and key[1] == payload.timeframe
                and candle.close_at_utc <= _as_utc(payload.close_at_utc)
            ]
            snapshot_key = (
                payload.instrument_key.casefold(),
                payload.timeframe,
                _as_utc(payload.close_at_utc),
            )
            revisions = self._snapshots.setdefault(snapshot_key, [])
            snapshot_revision = len(revisions)
            snapshot = calculator.calculate(
                delivery,
                window,
                processed_at_utc,
                snapshot_revision,
            )
            revisions.append(
                StoredFeatureSnapshot(
                    engine_output_id=None,
                    snapshot=snapshot,
                    input_candle_ids=tuple(),
                )
            )
            return FeatureStoreProcessOutcome(
                outcome="CREATED" if snapshot_revision == 0 else "REVISED",
                snapshot=snapshot,
            )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredFeatureSnapshot | None:
        normalized = instrument_key.casefold()
        with self._sync:
            candidates = [
                revisions[-1]
                for (key, frame, _), revisions in self._snapshots.items()
                if key == normalized and frame == timeframe and revisions
            ]
            return max(
                candidates,
                key=lambda item: item.snapshot.as_of_utc,
                default=None,
            )

    def get_status(self) -> FeatureStoreStatus:
        with self._sync:
            snapshot_count = sum(len(items) for items in self._snapshots.values())
            return FeatureStoreStatus(
                provider=self.provider_name,
                processed_messages=self._processed_messages,
                snapshot_count=snapshot_count,
                latest_processed_at_utc=self._latest_processed_at_utc,
                latest_error=self._latest_error,
            )


def _to_candle_input(delivery: MarketCandleDeliveryV1) -> CandleInput:
    payload = delivery.envelope.payload
    return CandleInput(
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


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
