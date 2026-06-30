from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Protocol
from uuid import UUID

from app.contracts.v1.liquidity_derivatives import (
    LiquidityDerivativesContextOutputV1,
)
from app.contracts.v1.market_data import MarketCandleDeliveryV1


@dataclass(frozen=True, slots=True)
class LiquidityDerivativesCandle:
    candle_id: int | None
    source_message_uid: UUID | None
    instrument_key: str
    timeframe: str
    open_at_utc: datetime
    close_at_utc: datetime
    open_price: Decimal
    high_price: Decimal
    low_price: Decimal
    close_price: Decimal
    volume_quantity: Decimal
    open_interest: Decimal | None
    revision: int
    received_at_utc: datetime
    quality_status: str
    is_usable_for_new_exposure: bool


@dataclass(frozen=True, slots=True)
class StoredLiquidityDerivativesOutput:
    engine_output_id: int | None
    output: LiquidityDerivativesContextOutputV1
    input_candle_ids: tuple[int, ...]


@dataclass(frozen=True, slots=True)
class LiquidityDerivativesStoreOutcome:
    outcome: str
    output: LiquidityDerivativesContextOutputV1 | None = None
    engine_output_id: int | None = None
    reason: str | None = None


@dataclass(frozen=True, slots=True)
class LiquidityDerivativesStoreStatus:
    provider: str
    candle_count: int
    output_count: int
    latest_processed_at_utc: datetime | None
    latest_error: str | None


class LiquidityDerivativesStore(Protocol):
    provider_name: str

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator,
        processed_at_utc: datetime,
    ) -> LiquidityDerivativesStoreOutcome:
        ...

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredLiquidityDerivativesOutput | None:
        ...

    def get_status(self) -> LiquidityDerivativesStoreStatus:
        ...
