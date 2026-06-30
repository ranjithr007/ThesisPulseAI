from datetime import UTC, datetime

from app.contracts.v1.market_data import MarketCandleDeliveryV1, MarketQuoteDeliveryV1
from app.contracts.v1.order_flow import (
    OrderFlowEngineOutputV1,
    OrderFlowProcessingResultV1,
    OrderFlowQuoteProcessingResultV1,
)
from app.core.settings import Settings
from app.order_flow.calculator import DeterministicOrderFlowCalculator
from app.order_flow.definitions import OrderFlowOptions
from app.order_flow.models import OrderFlowStore, OrderFlowStoreStatus
from app.order_flow.sql_store import SqlServerOrderFlowStore
from app.order_flow.store import InMemoryOrderFlowStore


class OrderFlowService:
    def __init__(
        self,
        settings: Settings,
        store: OrderFlowStore | None = None,
    ) -> None:
        self._settings = settings
        self._calculator = DeterministicOrderFlowCalculator(
            OrderFlowOptions(
                engine_code=settings.order_flow_engine_code,
                engine_version=settings.order_flow_engine_version,
                policy_version=settings.order_flow_policy_version,
                minimum_quote_samples=settings.order_flow_minimum_quote_samples,
                minimum_usable_ratio=settings.order_flow_minimum_usable_ratio,
                minimum_traded_quantity_coverage=(
                    settings.order_flow_minimum_traded_quantity_coverage
                ),
                maximum_quote_age_seconds=settings.order_flow_maximum_quote_age_seconds,
                directional_threshold=settings.order_flow_directional_threshold,
                fusion_confidence_threshold=(
                    settings.order_flow_fusion_confidence_threshold
                ),
            )
        )
        self._store = store or _create_store(settings)

    @property
    def enabled(self) -> bool:
        return self._settings.order_flow_engine_enabled

    def process_quote(
        self,
        delivery: MarketQuoteDeliveryV1,
        processed_at_utc: datetime | None = None,
    ) -> OrderFlowQuoteProcessingResultV1:
        if not self.enabled:
            return OrderFlowQuoteProcessingResultV1(
                outcome="IGNORED_INELIGIBLE",
                message_uid=delivery.envelope.metadata.message_id,
                reason="Order Flow Engine is disabled",
            )
        outcome = self._store.process_quote(
            delivery,
            processed_at_utc or datetime.now(UTC),
        )
        return OrderFlowQuoteProcessingResultV1(
            outcome=outcome,
            message_uid=delivery.envelope.metadata.message_id,
            reason=(
                "Quote is not eligible for Order Flow"
                if outcome == "IGNORED_INELIGIBLE"
                else None
            ),
        )

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        processed_at_utc: datetime | None = None,
    ) -> OrderFlowProcessingResultV1:
        if not self.enabled:
            return OrderFlowProcessingResultV1(
                outcome="IGNORED_INELIGIBLE",
                reason="Order Flow Engine is disabled",
            )
        outcome = self._store.process_candle(
            delivery,
            self._calculator,
            processed_at_utc or datetime.now(UTC),
        )
        return OrderFlowProcessingResultV1(
            outcome=outcome.outcome,
            output=outcome.output,
            reason=outcome.reason,
        )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str = "5m",
    ) -> OrderFlowEngineOutputV1 | None:
        stored = self._store.get_latest(instrument_key, timeframe)
        return None if stored is None else stored.output

    def get_status(self) -> OrderFlowStoreStatus:
        return self._store.get_status()


def _create_store(settings: Settings) -> OrderFlowStore:
    if settings.feature_factory_provider == "SqlServer":
        return SqlServerOrderFlowStore(
            settings.operational_database_connection_string or "",
            actor=settings.order_flow_engine_actor,
            engine_code=settings.order_flow_engine_code,
            broker_code=settings.feature_factory_broker_code,
            service_version=settings.service_version,
            command_timeout_seconds=settings.sql_command_timeout_seconds,
        )
    return InMemoryOrderFlowStore()
