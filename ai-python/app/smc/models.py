from dataclasses import dataclass
from datetime import datetime
from typing import Protocol

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smc import SmartMoneyConceptsOutputV1
from app.features.models import CandleInput


@dataclass(frozen=True, slots=True)
class StoredSmcOutput:
    engine_output_id: int | None
    output: SmartMoneyConceptsOutputV1
    input_candle_ids: tuple[int, ...]


@dataclass(frozen=True, slots=True)
class SmcStoreOutcome:
    outcome: str
    output: SmartMoneyConceptsOutputV1 | None = None
    engine_output_id: int | None = None
    reason: str | None = None


@dataclass(frozen=True, slots=True)
class SmcStoreStatus:
    provider: str
    output_count: int
    latest_processed_at_utc: datetime | None
    latest_error: str | None


class SmcStore(Protocol):
    provider_name: str

    def process(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator,
        processed_at_utc: datetime,
    ) -> SmcStoreOutcome:
        ...

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredSmcOutput | None:
        ...

    def get_status(self) -> SmcStoreStatus:
        ...


def candle_identity(candles: list[CandleInput]) -> tuple[int, ...]:
    return tuple(
        item.candle_id for item in candles if item.candle_id is not None
    )
