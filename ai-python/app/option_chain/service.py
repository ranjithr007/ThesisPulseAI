from datetime import UTC, date, datetime

from app.contracts.v1.option_chain import (
    OptionChainIntelligenceOutputV1,
    OptionChainProcessingResultV1,
)
from app.contracts.v1.option_chain_intake import OptionChainSnapshotObservationV1
from app.option_chain.calculator import DeterministicOptionChainCalculator
from app.option_chain.config import OptionChainRuntimeSettings
from app.option_chain.definitions import OptionChainIntelligenceOptions
from app.option_chain.models import (
    OptionChainSnapshotObservation,
    OptionContractObservation,
)
from app.option_chain.sql_partitioned_store import (
    PartitionedSqlServerOptionChainIntelligenceStore,
)
from app.option_chain.store import (
    InMemoryOptionChainIntelligenceStore,
    OptionChainStoreStatus,
)
from app.option_chain.store_protocol import OptionChainIntelligenceStore

_registered_option_chain_service: "OptionChainIntelligenceService | None" = None


class OptionChainIntelligenceService:
    def __init__(
        self,
        store: OptionChainIntelligenceStore | None = None,
        runtime: OptionChainRuntimeSettings | None = None,
    ) -> None:
        global _registered_option_chain_service
        use_application_instance = store is None and runtime is None
        if use_application_instance and _registered_option_chain_service is not None:
            registered = _registered_option_chain_service
            self._runtime = registered._runtime
            self._calculator = registered._calculator
            self._store = registered._store
            return

        self._runtime = runtime or OptionChainRuntimeSettings.load()
        self._calculator = DeterministicOptionChainCalculator(
            OptionChainIntelligenceOptions(
                engine_code=self._runtime.engine_code,
                engine_version=self._runtime.engine_version,
                policy_version=self._runtime.policy_version,
                maximum_output_age_seconds=self._runtime.maximum_output_age_seconds,
                minimum_contract_count=self._runtime.minimum_contract_count,
                minimum_strike_count=self._runtime.minimum_strike_count,
                oi_wall_count=self._runtime.oi_wall_count,
                oi_wall_moneyness_fraction=self._runtime.oi_wall_moneyness_fraction,
                minimum_premium_change_fraction=(
                    self._runtime.minimum_premium_change_fraction
                ),
                minimum_open_interest_change_fraction=(
                    self._runtime.minimum_open_interest_change_fraction
                ),
                directional_threshold=self._runtime.directional_threshold,
                fusion_confidence_threshold=self._runtime.fusion_confidence_threshold,
            )
        )
        self._store = store or _create_store(self._runtime)
        if use_application_instance:
            _registered_option_chain_service = self

    @property
    def enabled(self) -> bool:
        return self._runtime.enabled

    @property
    def provider(self) -> str:
        return self._runtime.provider

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
    def maximum_output_age_seconds(self) -> int:
        return self._runtime.maximum_output_age_seconds

    @property
    def internal_api_key(self) -> str | None:
        return self._runtime.internal_api_key

    def process_snapshot(
        self,
        request: OptionChainSnapshotObservationV1,
        processed_at_utc: datetime | None = None,
    ) -> OptionChainProcessingResultV1:
        if not self.enabled:
            return OptionChainProcessingResultV1(
                outcome="IGNORED_INELIGIBLE",
                reason="Option-Chain Intelligence Engine is disabled",
            )
        snapshot = _to_snapshot(request)
        outcome = self._store.process_snapshot(
            request.source_message_uid,
            snapshot,
            self._calculator,
            processed_at_utc or datetime.now(UTC),
        )
        return OptionChainProcessingResultV1(
            outcome=outcome.outcome,
            output=outcome.output,
            reason=outcome.reason,
        )

    def get_latest(
        self,
        underlying_instrument_key: str,
        expiry_date: date | None = None,
        as_of_utc: datetime | None = None,
    ) -> OptionChainIntelligenceOutputV1 | None:
        stored = self._store.get_latest(
            underlying_instrument_key,
            expiry_date,
            as_of_utc,
        )
        return None if stored is None else stored.output

    def get_status(self) -> OptionChainStoreStatus:
        return self._store.get_status()


def register_option_chain_service(service: OptionChainIntelligenceService) -> None:
    global _registered_option_chain_service
    _registered_option_chain_service = service


def get_registered_option_chain_service() -> OptionChainIntelligenceService | None:
    return _registered_option_chain_service


def _create_store(runtime: OptionChainRuntimeSettings) -> OptionChainIntelligenceStore:
    if runtime.provider == "SqlServer":
        return PartitionedSqlServerOptionChainIntelligenceStore(
            runtime.database_connection_string or "",
            actor=runtime.actor,
            engine_code=runtime.engine_code,
            broker_code=runtime.broker_code,
            service_version=runtime.service_version,
            maximum_output_age_seconds=runtime.maximum_output_age_seconds,
            command_timeout_seconds=runtime.command_timeout_seconds,
        )
    return InMemoryOptionChainIntelligenceStore()


def _to_snapshot(
    request: OptionChainSnapshotObservationV1,
) -> OptionChainSnapshotObservation:
    return OptionChainSnapshotObservation(
        snapshot_uid=request.snapshot_uid,
        underlying_instrument_key=request.underlying_instrument_key,
        expiry_date=request.expiry_date,
        event_at_utc=request.event_at_utc,
        received_at_utc=request.received_at_utc,
        underlying_price=request.underlying_price,
        snapshot_status=request.snapshot_status,
        quality_status=request.quality_status,
        is_point_in_time_eligible=request.is_point_in_time_eligible,
        revision=request.revision,
        entries=tuple(
            OptionContractObservation(
                derivative_contract_uid=entry.derivative_contract_uid,
                instrument_key=entry.instrument_key,
                expiry_date=entry.expiry_date,
                strike_price=entry.strike_price,
                option_type=entry.option_type,
                last_price=entry.last_price,
                volume_quantity=entry.volume_quantity,
                open_interest=entry.open_interest,
                implied_volatility=entry.implied_volatility,
                delta=entry.delta,
                contract_multiplier=entry.contract_multiplier,
                quality_status=entry.quality_status,
                greeks_source_version=entry.greeks_source_version,
            )
            for entry in request.entries
        ),
        calculation_source_version=request.calculation_source_version,
    )
