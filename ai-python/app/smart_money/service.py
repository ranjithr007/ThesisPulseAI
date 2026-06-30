from datetime import UTC, datetime

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smart_money import (
    SmartMoneyConceptsOutputV1,
    SmartMoneyProcessingResultV1,
)
from app.core.settings import Settings
from app.smart_money.calculator import DeterministicSmartMoneyCalculator
from app.smart_money.config import SmartMoneyRuntimeSettings
from app.smart_money.definitions import SmartMoneyOptions
from app.smart_money.models import SmartMoneyStore, SmartMoneyStoreStatus
from app.smart_money.sql_store import SqlServerSmartMoneyStore
from app.smart_money.store import InMemorySmartMoneyStore


class SmartMoneyConceptsService:
    def __init__(
        self,
        settings: Settings,
        store: SmartMoneyStore | None = None,
        runtime: SmartMoneyRuntimeSettings | None = None,
    ) -> None:
        self._platform = settings
        self._runtime = runtime or SmartMoneyRuntimeSettings.load(settings)
        self._calculator = DeterministicSmartMoneyCalculator(
            SmartMoneyOptions(
                engine_code=self._runtime.engine_code,
                engine_version=self._runtime.engine_version,
                policy_version=self._runtime.policy_version,
                required_input_count=self._runtime.required_input_count,
                maximum_input_count=self._runtime.maximum_input_count,
                swing_left_bars=self._runtime.swing_left_bars,
                swing_right_bars=self._runtime.swing_right_bars,
                order_block_search_bars=self._runtime.order_block_search_bars,
                maximum_zones_per_type=self._runtime.maximum_zones_per_type,
                maximum_zone_age_bars=self._runtime.maximum_zone_age_bars,
                break_tolerance_fraction=self._runtime.break_tolerance_fraction,
                minimum_fair_value_gap_fraction=(
                    self._runtime.minimum_fair_value_gap_fraction
                ),
                minimum_valid_input_ratio=self._runtime.minimum_valid_input_ratio,
                maximum_output_age_seconds=self._runtime.maximum_output_age_seconds,
                directional_threshold=self._runtime.directional_threshold,
                fusion_confidence_threshold=(
                    self._runtime.fusion_confidence_threshold
                ),
            )
        )
        self._store = store or _create_store(settings, self._runtime)

    @property
    def enabled(self) -> bool:
        return self._runtime.enabled

    @property
    def engine_code(self) -> str:
        return self._runtime.engine_code

    @property
    def engine_version(self) -> str:
        return self._runtime.engine_version

    @property
    def policy_version(self) -> str:
        return self._runtime.policy_version

    @property
    def required_input_count(self) -> int:
        return self._runtime.required_input_count

    @property
    def maximum_input_count(self) -> int:
        return self._runtime.maximum_input_count

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


def _create_store(
    settings: Settings,
    runtime: SmartMoneyRuntimeSettings,
) -> SmartMoneyStore:
    if settings.feature_factory_provider == "SqlServer":
        return SqlServerSmartMoneyStore(
            settings.operational_database_connection_string or "",
            actor=runtime.actor,
            engine_code=runtime.engine_code,
            broker_code=settings.feature_factory_broker_code,
            service_version=settings.service_version,
            maximum_input_count=runtime.maximum_input_count,
            command_timeout_seconds=settings.sql_command_timeout_seconds,
        )
    return InMemorySmartMoneyStore(runtime.maximum_input_count)
