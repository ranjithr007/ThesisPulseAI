from datetime import UTC, datetime

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smc import SmartMoneyConceptsOutputV1, SmcProcessingResultV1
from app.core.settings import Settings
from app.smc.calculator import DeterministicSmcCalculator
from app.smc.models import SmcStore, SmcStoreStatus
from app.smc.runtime import load_smc_options, smc_enabled
from app.smc.sql_store import SqlServerSmcStore
from app.smc.store import InMemorySmcStore


class SmartMoneyConceptsService:
    def __init__(
        self,
        settings: Settings,
        store: SmcStore | None = None,
        *,
        enabled: bool | None = None,
    ) -> None:
        self._settings = settings
        self._enabled = smc_enabled() if enabled is None else enabled
        self._calculator = DeterministicSmcCalculator(load_smc_options())
        self._store = store or _create_store(
            settings,
            self._calculator.options.engine_code,
        )

    @property
    def enabled(self) -> bool:
        return self._enabled

    @property
    def options(self):
        return self._calculator.options

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


def _create_store(settings: Settings, engine_code: str) -> SmcStore:
    if settings.feature_factory_provider == "SqlServer":
        return SqlServerSmcStore(
            settings.operational_database_connection_string or "",
            engine_code=engine_code,
            broker_code=settings.feature_factory_broker_code,
            service_version=settings.service_version,
            command_timeout_seconds=settings.sql_command_timeout_seconds,
        )
    return InMemorySmcStore()
