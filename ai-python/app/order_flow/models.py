from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Protocol
from uuid import UUID

from app.contracts.v1.market_data import MarketCandleDeliveryV1, MarketQuoteDeliveryV1
from app.contracts.v1.order_flow import OrderFlowEngineOutputV1


@dataclass(frozen=True, slots=True)
class QuoteSample:
    message_uid: UUID
    instrument_key: str
    event_at_utc: datetime
    received_at_utc: datetime
    last_traded_price: Decimal | None
    last_traded_quantity: Decimal | None
    open_interest: Decimal | None
    total_buy_quantity: Decimal | None
    total_sell_quantity: Decimal | None
    quality_status: str
    is_usable_for_new_exposure: bool


@dataclass(frozen=True, slots=True)
class ExistingOrderFlowRevision:
    engine_output_id: int | None
    revision: int


@dataclass(frozen=True, slots=True)
class StoredOrderFlowOutput:
    engine_output_id: int | None
    output: OrderFlowEngineOutputV1
    quote_message_uids: tuple[UUID, ...]


@dataclass(frozen=True, slots=True)
class OrderFlowStoreOutcome:
    outcome: str
    output: OrderFlowEngineOutputV1 | None = None
    engine_output_id: int | None = None
    reason: str | None = None


@dataclass(frozen=True, slots=True)
class OrderFlowStoreStatus:
    provider: str
    quote_sample_count: int
    output_count: int
    latest_processed_at_utc: datetime | None
    latest_error: str | None


class OrderFlowStore(Protocol):
    provider_name: str

    def process_quote(
        self,
        delivery: MarketQuoteDeliveryV1,
        processed_at_utc: datetime,
    ) -> str:
        ...

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        calculator,
        processed_at_utc: datetime,
    ) -> OrderFlowStoreOutcome:
        ...

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredOrderFlowOutput | None:
        ...

    def get_status(self) -> OrderFlowStoreStatus:
        ...
