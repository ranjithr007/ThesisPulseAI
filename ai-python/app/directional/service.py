from datetime import UTC, datetime

from app.contracts.v1.directional import (
    DirectionalEngineOutputV1,
    DirectionalProcessingResultV1,
)
from app.core.settings import Settings
from app.directional.calculator import DeterministicDirectionalCalculator
from app.directional.definitions import DirectionalEngineOptions
from app.directional.models import DirectionalStoreStatus, StoredDirectionalOutput
from app.directional.sql_store import SqlServerDirectionalIntelligenceStore
from app.directional.store import (
    DirectionalIntelligenceStore,
    InMemoryDirectionalIntelligenceStore,
)
from app.features.models import StoredFeatureSnapshot


class DirectionalIntelligenceService:
    def __init__(
        self,
        settings: Settings,
        store: DirectionalIntelligenceStore | None = None,
    ) -> None:
        self._settings = settings
        self._calculator = DeterministicDirectionalCalculator(
            DirectionalEngineOptions(
                engine_code=settings.directional_engine_code,
                engine_version=settings.directional_engine_version,
                policy_version=settings.directional_policy_version,
            )
        )
        self._store = store or _create_store(settings)

    @property
    def enabled(self) -> bool:
        return self._settings.directional_engine_enabled

    def process_feature(
        self,
        source: StoredFeatureSnapshot,
        processed_at_utc: datetime | None = None,
    ) -> DirectionalProcessingResultV1:
        snapshot = source.snapshot
        if not self.enabled:
            return self._ignored(source, "Directional intelligence engine is disabled")
        if (
            not snapshot.is_eligible_for_engines
            or snapshot.data_quality_status != "VALID"
            or snapshot.is_stale
        ):
            return self._ignored(
                source,
                "Feature snapshot is not valid, fresh, and eligible",
            )

        processed_at = processed_at_utc or datetime.now(UTC)
        try:
            outcome = self._store.process(source, self._calculator, processed_at)
        except ValueError as exception:
            return self._ignored(source, str(exception))
        except RuntimeError as exception:
            if str(exception) == "Feature output is no longer current":
                return self._ignored(source, str(exception))
            raise

        return DirectionalProcessingResultV1(
            outcome=outcome.outcome,
            source_feature_snapshot_uid=snapshot.snapshot_uid,
            output=outcome.output,
            reason=outcome.reason,
        )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> DirectionalEngineOutputV1 | None:
        stored = self.get_latest_stored(instrument_key, timeframe)
        return None if stored is None else stored.output

    def get_latest_stored(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> StoredDirectionalOutput | None:
        return self._store.get_latest(instrument_key, timeframe)

    def get_status(self) -> DirectionalStoreStatus:
        return self._store.get_status()

    @staticmethod
    def _ignored(
        source: StoredFeatureSnapshot,
        reason: str,
    ) -> DirectionalProcessingResultV1:
        return DirectionalProcessingResultV1(
            outcome="IGNORED_INELIGIBLE",
            source_feature_snapshot_uid=source.snapshot.snapshot_uid,
            reason=reason,
        )


def _create_store(settings: Settings) -> DirectionalIntelligenceStore:
    if settings.feature_factory_provider == "SqlServer":
        return SqlServerDirectionalIntelligenceStore(
            settings.operational_database_connection_string or "",
            actor=settings.directional_engine_actor,
            engine_code=settings.directional_engine_code,
            feature_engine_code=settings.feature_factory_engine_code,
            broker_code=settings.feature_factory_broker_code,
            service_version=settings.service_version,
            command_timeout_seconds=settings.sql_command_timeout_seconds,
        )
    return InMemoryDirectionalIntelligenceStore()
