from datetime import UTC, datetime

from app.confirmation.calculator import (
    DeterministicMultiTimeframeConfirmationCalculator,
)
from app.confirmation.definitions import (
    REQUIRED_TIMEFRAMES,
    TIMEFRAME_WEIGHTS,
    MultiTimeframeConfirmationOptions,
)
from app.confirmation.models import (
    ConfirmationInputBundle,
    ConfirmationStoreStatus,
    TimeframeIntelligencePair,
)
from app.confirmation.sql_store import SqlServerMultiTimeframeConfirmationStore
from app.confirmation.store import (
    InMemoryMultiTimeframeConfirmationStore,
    MultiTimeframeConfirmationStore,
)
from app.contracts.v1.confirmation import (
    ConfirmationProcessingResultV1,
    MultiTimeframeConfirmationOutputV1,
)
from app.core.settings import Settings
from app.directional.service import DirectionalIntelligenceService
from app.regime.service import MarketRegimeService


class MultiTimeframeConfirmationService:
    def __init__(
        self,
        settings: Settings,
        directional_service: DirectionalIntelligenceService,
        regime_service: MarketRegimeService,
        store: MultiTimeframeConfirmationStore | None = None,
    ) -> None:
        self._settings = settings
        self._directional = directional_service
        self._regime = regime_service
        self._calculator = DeterministicMultiTimeframeConfirmationCalculator(
            MultiTimeframeConfirmationOptions(
                engine_code=settings.confirmation_engine_code,
                engine_version=settings.confirmation_engine_version,
                policy_version=settings.confirmation_policy_version,
            )
        )
        self._store = store or _create_store(settings)

    @property
    def enabled(self) -> bool:
        return self._settings.confirmation_engine_enabled

    def process_instrument(
        self,
        instrument_key: str,
        processed_at_utc: datetime | None = None,
    ) -> ConfirmationProcessingResultV1:
        if not self.enabled:
            return ConfirmationProcessingResultV1(
                outcome="IGNORED_INELIGIBLE",
                instrument_key=instrument_key,
                reason="Multi-timeframe confirmation engine is disabled",
            )

        primary_directional = self._directional.get_latest_stored(instrument_key, "5m")
        primary_regime = self._regime.get_latest_stored(instrument_key, "5m")
        if primary_directional is None or primary_regime is None:
            return ConfirmationProcessingResultV1(
                outcome="IGNORED_INCOMPLETE",
                instrument_key=instrument_key,
                reason="Primary 5m directional and regime outputs are required",
            )
        if primary_directional.output.as_of_utc != primary_regime.output.as_of_utc:
            return ConfirmationProcessingResultV1(
                outcome="IGNORED_INCOMPLETE",
                instrument_key=instrument_key,
                reason="Primary 5m directional and regime timestamps do not match",
            )

        primary_as_of = primary_directional.output.as_of_utc
        pairs: list[TimeframeIntelligencePair] = []
        for timeframe in TIMEFRAME_WEIGHTS:
            directional = self._directional.get_latest_stored(instrument_key, timeframe)
            regime = self._regime.get_latest_stored(instrument_key, timeframe)
            if directional is None or regime is None:
                continue
            if directional.output.as_of_utc != regime.output.as_of_utc:
                continue
            if directional.output.as_of_utc > primary_as_of:
                continue
            pairs.append(
                TimeframeIntelligencePair(
                    timeframe=timeframe,
                    directional=directional,
                    regime=regime,
                )
            )

        available = {pair.timeframe for pair in pairs}
        missing_required = sorted(REQUIRED_TIMEFRAMES - available)
        if missing_required:
            return ConfirmationProcessingResultV1(
                outcome="IGNORED_INCOMPLETE",
                instrument_key=instrument_key,
                reason=(
                    "Required timeframes are unavailable: "
                    + ", ".join(missing_required)
                ),
            )

        bundle = ConfirmationInputBundle(
            instrument_key=instrument_key,
            pairs=tuple(pairs),
        )
        processed_at = processed_at_utc or datetime.now(UTC)
        try:
            outcome = self._store.process(bundle, self._calculator, processed_at)
        except ValueError as exception:
            return ConfirmationProcessingResultV1(
                outcome="IGNORED_INELIGIBLE",
                instrument_key=instrument_key,
                reason=str(exception),
            )
        except RuntimeError as exception:
            if str(exception) == "Intelligence source is no longer current":
                return ConfirmationProcessingResultV1(
                    outcome="IGNORED_INELIGIBLE",
                    instrument_key=instrument_key,
                    reason=str(exception),
                )
            raise

        return ConfirmationProcessingResultV1(
            outcome=outcome.outcome,
            instrument_key=instrument_key,
            output=outcome.output,
            reason=outcome.reason,
        )

    def get_latest(
        self,
        instrument_key: str,
    ) -> MultiTimeframeConfirmationOutputV1 | None:
        stored = self._store.get_latest(instrument_key)
        return None if stored is None else stored.output

    def get_status(self) -> ConfirmationStoreStatus:
        return self._store.get_status()


def _create_store(settings: Settings) -> MultiTimeframeConfirmationStore:
    if settings.feature_factory_provider == "SqlServer":
        return SqlServerMultiTimeframeConfirmationStore(
            settings.operational_database_connection_string or "",
            actor=settings.confirmation_engine_actor,
            engine_code=settings.confirmation_engine_code,
            directional_engine_code=settings.directional_engine_code,
            regime_engine_code=settings.regime_engine_code,
            broker_code=settings.feature_factory_broker_code,
            service_version=settings.service_version,
            command_timeout_seconds=settings.sql_command_timeout_seconds,
        )
    return InMemoryMultiTimeframeConfirmationStore()
