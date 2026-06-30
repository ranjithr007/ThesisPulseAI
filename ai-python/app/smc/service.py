from datetime import UTC, datetime

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smc import SmcProcessingResultV1, SmartMoneyConceptsOutputV1
from app.core.settings import Settings
from app.smc.calculator import DeterministicSmcCalculator
from app.smc.definitions import SmcOptions
from app.smc.models import SmcStore, SmcStoreStatus
from app.smc.store import InMemorySmcStore


class SmartMoneyConceptsService:
    def __init__(self, settings: Settings, store: SmcStore | None = None) -> None:
        self._settings = settings
        self._calculator = DeterministicSmcCalculator(
            SmcOptions(
                engine_code=settings.smc_engine_code,
                engine_version=settings.smc_engine_version,
                policy_version=settings.smc_policy_version,
                required_input_count=settings.smc_required_input_count,
                maximum_input_count=settings.smc_maximum_input_count,
                swing_left_bars=settings.smc_swing_left_bars,
                swing_right_bars=settings.smc_swing_right_bars,
                minimum_break_fraction=settings.smc_minimum_break_fraction,
                directional_threshold=settings.smc_directional_threshold,
                fusion_confidence_threshold=settings.smc_fusion_confidence_threshold,
            )
        )
        self._store = store or InMemorySmcStore()

    @property
    def enabled(self) -> bool:
        return self._settings.smc_engine_enabled

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        processed_at_utc: datetime | None = None,
    ) -> SmcProcessingResultV1:
        if not self.enabled:
            return SmcProcessingResultV1(
                outcome="IGNORED_INELIGIBLE",
                reason="Smart Money Concepts Engine is disabled",
            )
        outcome = self._store.process(
            delivery,
            self._calculator,
            processed_at_utc or datetime.now(UTC),
        )
        return SmcProcessingResultV1(
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

    def get_status(self) -> SmcStoreStatus:
        return self._store.get_status()
