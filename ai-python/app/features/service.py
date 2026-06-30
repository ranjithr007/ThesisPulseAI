from datetime import UTC, datetime

from app.contracts.v1.market_data import (
    FeatureProcessingResultV1,
    FeatureSnapshotV1,
    MarketCandleDeliveryV1,
)
from app.core.settings import Settings
from app.directional.service import DirectionalIntelligenceService
from app.features.calculator import DeterministicFeatureCalculator
from app.features.definitions import FeatureFactoryOptions
from app.features.models import FeatureStoreStatus, StoredFeatureSnapshot
from app.features.sql_store import SqlServerFeatureFactoryStore
from app.features.store import FeatureFactoryStore, InMemoryFeatureFactoryStore
from app.regime.service import MarketRegimeService


class FeatureFactoryService:
    def __init__(
        self,
        settings: Settings,
        store: FeatureFactoryStore | None = None,
        directional_service: DirectionalIntelligenceService | None = None,
        regime_service: MarketRegimeService | None = None,
    ) -> None:
        self._settings = settings
        options = FeatureFactoryOptions(
            feature_set_version=settings.feature_set_version,
            feature_version=settings.feature_version,
            required_input_count=settings.feature_required_input_count,
            maximum_input_count=settings.feature_maximum_input_count,
        )
        self._calculator = DeterministicFeatureCalculator(options)
        self._store = store or _create_store(settings)
        self._directional = directional_service or DirectionalIntelligenceService(settings)
        self._regime = regime_service or MarketRegimeService(settings)

    @property
    def enabled(self) -> bool:
        return self._settings.feature_factory_enabled

    @property
    def internal_api_key(self) -> str | None:
        return self._settings.feature_factory_internal_api_key

    @property
    def directional(self) -> DirectionalIntelligenceService:
        return self._directional

    @property
    def regime(self) -> MarketRegimeService:
        return self._regime

    def process_candle(
        self,
        delivery: MarketCandleDeliveryV1,
        processed_at_utc: datetime | None = None,
    ) -> FeatureProcessingResultV1:
        processed_at = processed_at_utc or datetime.now(UTC)
        outcome = self._store.process(delivery, self._calculator, processed_at)
        stored = self._resolve_stored_snapshot(
            delivery,
            outcome.snapshot,
            outcome.outcome,
        )
        regime = None
        directional = None
        if stored is not None:
            regime = self._regime.process_feature(stored, processed_at)
            directional = self._directional.process_feature(stored, processed_at)
        return FeatureProcessingResultV1(
            outcome=outcome.outcome,
            stream_position=delivery.stream_position,
            message_uid=delivery.envelope.metadata.message_id,
            snapshot=outcome.snapshot,
            regime=regime,
            directional=directional,
            reason=outcome.reason,
        )

    def get_latest(
        self,
        instrument_key: str,
        timeframe: str,
    ) -> FeatureSnapshotV1 | None:
        stored = self._store.get_latest(instrument_key, timeframe)
        return None if stored is None else stored.snapshot

    def get_status(self) -> FeatureStoreStatus:
        return self._store.get_status()

    def _resolve_stored_snapshot(
        self,
        delivery: MarketCandleDeliveryV1,
        snapshot: FeatureSnapshotV1 | None,
        outcome: str,
    ) -> StoredFeatureSnapshot | None:
        if snapshot is None and outcome != "DUPLICATE":
            return None

        latest = self._store.get_latest(
            delivery.envelope.payload.instrument_key,
            delivery.envelope.payload.timeframe,
        )
        if snapshot is None:
            if latest is None:
                return None
            if latest.snapshot.message_uid != delivery.envelope.metadata.message_id:
                return None
            return latest
        if latest is None or latest.snapshot.snapshot_uid != snapshot.snapshot_uid:
            return StoredFeatureSnapshot(
                engine_output_id=None,
                snapshot=snapshot,
                input_candle_ids=tuple(),
            )
        return latest


def _create_store(settings: Settings) -> FeatureFactoryStore:
    if settings.feature_factory_provider == "SqlServer":
        return SqlServerFeatureFactoryStore(
            settings.operational_database_connection_string or "",
            actor=settings.feature_factory_actor,
            engine_code=settings.feature_factory_engine_code,
            broker_code=settings.feature_factory_broker_code,
            service_version=settings.service_version,
            command_timeout_seconds=settings.sql_command_timeout_seconds,
        )
    return InMemoryFeatureFactoryStore()
