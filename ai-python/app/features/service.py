from datetime import UTC, datetime

from app.confirmation.service import MultiTimeframeConfirmationService
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
from app.order_flow.service import OrderFlowService
from app.regime.service import MarketRegimeService
from app.workflow.calculator import (
    FusionReadyEvidenceCalculator,
    FusionReadyEvidenceOptions,
)


class FeatureFactoryService:
    def __init__(
        self,
        settings: Settings,
        store: FeatureFactoryStore | None = None,
        directional_service: DirectionalIntelligenceService | None = None,
        regime_service: MarketRegimeService | None = None,
        confirmation_service: MultiTimeframeConfirmationService | None = None,
        order_flow_service: OrderFlowService | None = None,
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
        self._confirmation = confirmation_service or MultiTimeframeConfirmationService(
            settings,
            self._directional,
            self._regime,
        )
        self._order_flow = order_flow_service or OrderFlowService(settings)
        self._workflow_calculator = FusionReadyEvidenceCalculator(
            FusionReadyEvidenceOptions(
                weight_configuration_version=(
                    settings.workflow_weight_configuration_version
                ),
                proposal_policy_version=settings.workflow_proposal_policy_version,
                stop_atr_multiple=settings.workflow_stop_atr_multiple,
                first_target_risk_reward=(
                    settings.workflow_first_target_risk_reward
                ),
                entry_band_atr_fraction=(
                    settings.workflow_entry_band_atr_fraction
                ),
                minimum_entry_band_fraction=(
                    settings.workflow_minimum_entry_band_fraction
                ),
                maximum_entry_band_fraction=(
                    settings.workflow_maximum_entry_band_fraction
                ),
                maximum_slippage_fraction=(
                    settings.workflow_maximum_slippage_fraction
                ),
            )
        )

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

    @property
    def confirmation(self) -> MultiTimeframeConfirmationService:
        return self._confirmation

    @property
    def order_flow(self) -> OrderFlowService:
        return self._order_flow

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
        order_flow = None
        confirmation = None
        workflow_evidence = None
        if stored is not None:
            regime = self._regime.process_feature(stored, processed_at)
            directional = self._directional.process_feature(stored, processed_at)
            order_flow = self._order_flow.process_candle(delivery, processed_at)
            confirmation = self._confirmation.process_instrument(
                delivery.envelope.payload.instrument_key,
                processed_at,
            )
            workflow_evidence = self._build_workflow_evidence(
                delivery,
                confirmation,
                order_flow,
                processed_at,
            )
        return FeatureProcessingResultV1(
            outcome=outcome.outcome,
            stream_position=delivery.stream_position,
            message_uid=delivery.envelope.metadata.message_id,
            snapshot=outcome.snapshot,
            regime=regime,
            directional=directional,
            order_flow=order_flow,
            confirmation=confirmation,
            workflow_evidence=workflow_evidence,
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

    def _build_workflow_evidence(
        self,
        delivery: MarketCandleDeliveryV1,
        confirmation_result,
        order_flow_result,
        processed_at: datetime,
    ):
        payload = delivery.envelope.payload
        if not self._settings.workflow_evidence_enabled:
            return None
        if payload.timeframe != "5m":
            return None
        if confirmation_result is None or confirmation_result.output is None:
            return None
        confirmation = confirmation_result.output
        if not confirmation.is_eligible_for_fusion:
            return None
        primary_feature = self.get_latest(payload.instrument_key, "5m")
        if primary_feature is None:
            return None

        directional_by_timeframe = {}
        regime_by_timeframe = {}
        for item in confirmation.timeframe_confirmations:
            directional = self._directional.get_latest(
                payload.instrument_key,
                item.timeframe,
            )
            regime = self._regime.get_latest(
                payload.instrument_key,
                item.timeframe,
            )
            if directional is not None:
                directional_by_timeframe[item.timeframe] = directional
            if regime is not None:
                regime_by_timeframe[item.timeframe] = regime

        order_flow_output = (
            None if order_flow_result is None else order_flow_result.output
        )
        try:
            return self._workflow_calculator.calculate(
                delivery,
                primary_feature,
                confirmation,
                directional_by_timeframe,
                regime_by_timeframe,
                processed_at,
                order_flow_output,
            )
        except ValueError:
            return None

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
