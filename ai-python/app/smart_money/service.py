from datetime import UTC, datetime

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smart_money import (
    SmartMoneyConceptsOutputV1,
    SmartMoneyProcessingResultV1,
)
from app.core.settings import Settings
from app.smart_money.calculator import DeterministicSmartMoneyCalculator
from app.smart_money.definitions import SmartMoneyOptions
from app.smart_money.models import SmartMoneyStore, SmartMoneyStoreStatus
from app.smart_money.sql_store import SqlServerSmartMoneyStore
from app.smart_money.store import InMemorySmartMoneyStore


class SmartMoneyConceptsService:
    def __init__(
        self,
        settings: Settings,
        store: SmartMoneyStore | None = None,
    ) -> None:
        self._settings = settings
        self._calculator = DeterministicSmartMoneyCalculator(
            SmartMoneyOptions(
                engine_code=settings.smart_money_engine_code,
                engine_version=settings.smart_money_engine_version,
                policy_version=settings.smart_money_policy_version,
                required_input_count=settings.smart_money_required_input_count,
                maximum_input_count=settings.smart_money_maximum_input_count,
                swing_left_bars=settings.smart_money_swing_left_bars,
                swing_right_bars=settings.smart_money_swing_right_bars,
                order_block_search_bars=(
                    settings.smart_money_order_block_search_bars
                ),
                maximum_zones_per_type=(
                    settings.smart_money_maximum_zones_per_type
                ),
                maximum_zone_age_bars=settings.smart_money_maximum_zone_age_bars,
                break_tolerance_fraction=(
                    settings.smart_money_break_tolerance_fraction
                ),
                minimum_fair_value_gap_fraction=(
                    settings.smart_money_minimum_fair_value_gap_fraction
                ),
                minimum_valid_input_ratio=(
                    settings.smart_money_minimum_valid_input_ratio
                ),
                maximum_output_age_seconds=(
                    settings.smart_money_maximum_output_age_seconds
                ),
                directional_threshold=settings.smart_money_directional_threshold,
                fusion_confidence_threshold=(
                    settings.smart_money_fusion_confidence_threshold
                ),
            )
        )
        self._store = store or _create_store(settings)

    @property
    def enabled(self) -> bool:
        return self._settings.smart_money_engine_enabled

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        processed_at_utc: datetime | None = None,
    ) -> SmartMoneyProcessingResultV1:
        if not self.enabled:
            return SmartMoneyProcessingResultV1(
                outcome="IGNORED_INELIGIBLE",
                reason="Smart Money Concepts Engine is disabled",
            )
        outcome = self._store.process_candle(
            delivery,
            self._calculator,
            processed_at_utc or datetime.now(UTC),
        )
        return SmartMoneyProcessingResultV1(
            outcome=outcome.outcome,
            output=outcome.output,
            reason=outcome.reason,
        )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str = "5m",
    ) -> SmartMoneyConceptsOutputV1 | None:
        stored = self._store.get_latest(instrument_key, timeframe)
        return None if stored is None else stored.output

    def get_status(self) -> SmartMoneyStoreStatus:
        return self._store.get_status()


def _create_store(settings: Settings) -> SmartMoneyStore:
    if settings.feature_factory_provider == "SqlServer":
        return SqlServerSmartMoneyStore(
            settings.operational_database_connection_string or "",
            actor=settings.smart_money_engine_actor,
            engine_code=settings.smart_money_engine_code,
            broker_code=settings.feature_factory_broker_code,
            service_version=settings.service_version,
            maximum_input_count=settings.smart_money_maximum_input_count,
            command_timeout_seconds=settings.sql_command_timeout_seconds,
        )
    return InMemorySmartMoneyStore(settings.smart_money_maximum_input_count)
