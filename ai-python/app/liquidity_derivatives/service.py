from datetime import UTC, datetime

from app.contracts.v1.liquidity_derivatives import (
    LiquidityDerivativesContextOutputV1,
    LiquidityDerivativesProcessingResultV1,
)
from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.core.settings import Settings
from app.liquidity_derivatives.calculator import (
    DeterministicLiquidityDerivativesCalculator,
)
from app.liquidity_derivatives.config import (
    LiquidityDerivativesRuntimeSettings,
)
from app.liquidity_derivatives.definitions import LiquidityDerivativesOptions
from app.liquidity_derivatives.models import (
    LiquidityDerivativesStore,
    LiquidityDerivativesStoreStatus,
)
from app.liquidity_derivatives.sql_store import (
    SqlServerLiquidityDerivativesStore,
)
from app.liquidity_derivatives.store import InMemoryLiquidityDerivativesStore


class LiquidityDerivativesContextService:
    def __init__(
        self,
        settings: Settings,
        store: LiquidityDerivativesStore | None = None,
        runtime: LiquidityDerivativesRuntimeSettings | None = None,
    ) -> None:
        self._platform = settings
        self._runtime = runtime or LiquidityDerivativesRuntimeSettings.load(settings)
        self._calculator = DeterministicLiquidityDerivativesCalculator(
            LiquidityDerivativesOptions(
                engine_code=self._runtime.engine_code,
                engine_version=self._runtime.engine_version,
                policy_version=self._runtime.policy_version,
                required_input_count=self._runtime.required_input_count,
                maximum_input_count=self._runtime.maximum_input_count,
                swing_left_bars=self._runtime.swing_left_bars,
                swing_right_bars=self._runtime.swing_right_bars,
                pool_cluster_tolerance_fraction=(
                    self._runtime.pool_cluster_tolerance_fraction
                ),
                pool_half_width_fraction=(
                    self._runtime.pool_half_width_fraction
                ),
                maximum_pools_per_side=self._runtime.maximum_pools_per_side,
                derivatives_lookback_bars=(
                    self._runtime.derivatives_lookback_bars
                ),
                minimum_price_change_fraction=(
                    self._runtime.minimum_price_change_fraction
                ),
                minimum_open_interest_change_fraction=(
                    self._runtime.minimum_open_interest_change_fraction
                ),
                minimum_valid_input_ratio=(
                    self._runtime.minimum_valid_input_ratio
                ),
                maximum_output_age_seconds=(
                    self._runtime.maximum_output_age_seconds
                ),
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
    ) -> LiquidityDerivativesProcessingResultV1:
        if not self.enabled:
            return LiquidityDerivativesProcessingResultV1(
                outcome="IGNORED_INELIGIBLE",
                reason="Liquidity Derivatives Context Engine is disabled",
            )
        outcome = self._store.process_candle(
            delivery,
            self._calculator,
            processed_at_utc or datetime.now(UTC),
        )
        return LiquidityDerivativesProcessingResultV1(
            outcome=outcome.outcome,
            output=outcome.output,
            reason=outcome.reason,
        )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str = "5m",
    ) -> LiquidityDerivativesContextOutputV1 | None:
        stored = self._store.get_latest(instrument_key, timeframe)
        return None if stored is None else stored.output

    def get_status(self) -> LiquidityDerivativesStoreStatus:
        return self._store.get_status()


def _create_store(
    settings: Settings,
    runtime: LiquidityDerivativesRuntimeSettings,
) -> LiquidityDerivativesStore:
    if settings.feature_factory_provider == "SqlServer":
        return SqlServerLiquidityDerivativesStore(
            settings.operational_database_connection_string or "",
            actor=runtime.actor,
            engine_code=runtime.engine_code,
            broker_code=settings.feature_factory_broker_code,
            service_version=settings.service_version,
            maximum_input_count=runtime.maximum_input_count,
            command_timeout_seconds=settings.sql_command_timeout_seconds,
        )
    return InMemoryLiquidityDerivativesStore(runtime.maximum_input_count)
