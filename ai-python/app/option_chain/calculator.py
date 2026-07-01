from datetime import datetime, timedelta
from decimal import Decimal
from uuid import NAMESPACE_URL, uuid5

from app.contracts.v1.option_chain import (
    OptionChainExpiryMetricsV1,
    OptionChainIntelligenceOutputV1,
)
from app.option_chain.common import (
    ONE,
    ZERO,
    as_utc,
    clip,
    deduplicate_snapshots,
    eligible_entries,
    is_snapshot_eligible,
    quantize,
    source_snapshot_uids,
)
from app.option_chain.definitions import OptionChainIntelligenceOptions
from app.option_chain.evidence import agreement, build_evidence, direction
from app.option_chain.flows import classify_oi_flows
from app.option_chain.max_pain import calculate_max_pain
from app.option_chain.models import OptionChainSnapshotObservation
from app.option_chain.pcr_walls import calculate_pcr, rank_oi_walls
from app.option_chain.volatility import calculate_skew, calculate_term_structure


class DeterministicOptionChainCalculator:
    def __init__(self, options: OptionChainIntelligenceOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> OptionChainIntelligenceOptions:
        return self._options

    def calculate(
        self,
        current: OptionChainSnapshotObservation,
        previous: OptionChainSnapshotObservation | None,
        term_snapshots: list[OptionChainSnapshotObservation],
        generated_at_utc: datetime,
        revision: int,
    ) -> OptionChainIntelligenceOutputV1:
        generated_at = as_utc(generated_at_utc)
        warnings: list[str] = []
        current_entries = eligible_entries(current)
        call_entries = [entry for entry in current_entries if entry.option_type == "CALL"]
        put_entries = [entry for entry in current_entries if entry.option_type == "PUT"]
        strike_count = len({entry.strike_price for entry in current_entries})

        if not is_snapshot_eligible(current):
            warnings.append("OPTION_CHAIN_PARTIAL_OR_INVALID")
        if not call_entries or not put_entries:
            warnings.append("INSUFFICIENT_CALL_PUT_COVERAGE")
        if len(current_entries) < self._options.minimum_contract_count:
            warnings.append("INSUFFICIENT_CONTRACT_COVERAGE")
        if strike_count < self._options.minimum_strike_count:
            warnings.append("INSUFFICIENT_STRIKE_COVERAGE")

        call_oi, put_oi, pcr_oi, pcr_oi_warning = calculate_pcr(
            current_entries,
            "open_interest",
        )
        call_volume, put_volume, pcr_volume, pcr_volume_warning = calculate_pcr(
            current_entries,
            "volume_quantity",
        )
        warnings.extend(item for item in (pcr_oi_warning, pcr_volume_warning) if item)

        call_walls, put_walls = rank_oi_walls(
            current,
            current_entries,
            self._options,
        )
        flows, flow_warnings = classify_oi_flows(
            current,
            previous,
            current_entries,
            self._options,
        )
        warnings.extend(flow_warnings)

        (
            max_pain_strike,
            max_pain_distance,
            max_pain_strength,
            max_pain_curve,
            max_pain_warnings,
        ) = calculate_max_pain(current, current_entries)
        warnings.extend(max_pain_warnings)

        (
            atm_call_iv,
            atm_put_iv,
            atm_skew,
            rr25_skew,
            skew_warnings,
        ) = calculate_skew(current, current_entries, self._options)
        warnings.extend(skew_warnings)

        all_term_snapshots = deduplicate_snapshots([current, *term_snapshots])
        (
            term_points,
            near_to_next_slope,
            near_to_far_slope,
            term_state,
            term_warnings,
        ) = calculate_term_structure(all_term_snapshots, self._options)
        warnings.extend(term_warnings)

        evidence = build_evidence(
            current=current,
            pcr_oi=pcr_oi,
            pcr_volume=pcr_volume,
            call_walls=call_walls,
            put_walls=put_walls,
            flows=flows,
            max_pain_distance=max_pain_distance,
            max_pain_strength=max_pain_strength,
            term_state=term_state,
            term_points=term_points,
            atm_skew=atm_skew,
            rr25_skew=rr25_skew,
            options=self._options,
        )
        score = quantize(clip(sum((item.contribution for item in evidence), ZERO)))
        resolved_direction = direction(score, self._options.directional_threshold)
        component_coverage = quantize(
            sum(
                (
                    item.weight
                    for item in evidence
                    if item.confidence > ZERO and not item.warnings
                ),
                ZERO,
            )
        )
        confidence = self._confidence(
            evidence=evidence,
            score=score,
            component_coverage=component_coverage,
            contract_count=len(current_entries),
            strike_count=strike_count,
        )

        age = generated_at - as_utc(current.event_at_utc)
        is_stale = age < timedelta(0) or age.total_seconds() > (
            self._options.maximum_output_age_seconds
        )
        if is_stale:
            warnings.append("OPTION_CHAIN_STALE")

        quality = self._quality(
            current=current,
            call_count=len(call_entries),
            put_count=len(put_entries),
            contract_count=len(current_entries),
            strike_count=strike_count,
            component_coverage=component_coverage,
        )
        is_eligible = (
            quality == "VALID"
            and not is_stale
            and resolved_direction != "NEUTRAL"
            and confidence >= self._options.fusion_confidence_threshold
        )
        if (
            resolved_direction != "NEUTRAL"
            and confidence < self._options.fusion_confidence_threshold
        ):
            warnings.append("OPTION_CHAIN_FUSION_CONFIDENCE_BELOW_THRESHOLD")

        expiry_metrics = OptionChainExpiryMetricsV1(
            snapshot_uid=current.snapshot_uid,
            expiry_date=current.expiry_date,
            underlying_price=current.underlying_price,
            call_open_interest=call_oi,
            put_open_interest=put_oi,
            pcr_open_interest=pcr_oi,
            call_volume=call_volume,
            put_volume=put_volume,
            pcr_volume=pcr_volume,
            call_walls=call_walls,
            put_walls=put_walls,
            oi_flows=flows,
            max_pain_strike=max_pain_strike,
            max_pain_distance_fraction=max_pain_distance,
            max_pain_magnet_strength=max_pain_strength,
            max_pain_curve=max_pain_curve,
            atm_call_implied_volatility=atm_call_iv,
            atm_put_implied_volatility=atm_put_iv,
            atm_put_call_skew=atm_skew,
            rr25_skew=rr25_skew,
            accepted_contract_count=len(current_entries),
            accepted_strike_count=strike_count,
            component_coverage=component_coverage,
            warnings=sorted(set(warnings)),
        )

        source_snapshots = source_snapshot_uids(
            current,
            previous,
            all_term_snapshots,
        )
        output_uid = uuid5(
            NAMESPACE_URL,
            "|".join(
                [
                    "option-chain-intelligence-output-v1",
                    str(current.snapshot_uid),
                    self._options.policy_version,
                    str(revision),
                ]
            ),
        )
        message_uid = uuid5(
            NAMESPACE_URL,
            f"option-chain-intelligence-message-v1|{output_uid}",
        )

        return OptionChainIntelligenceOutputV1(
            output_uid=output_uid,
            message_uid=message_uid,
            source_snapshot_uids=source_snapshots,
            underlying_instrument_key=current.underlying_instrument_key,
            as_of_utc=as_utc(current.event_at_utc),
            generated_at_utc=generated_at,
            engine_code=self._options.engine_code,
            engine_version=self._options.engine_version,
            policy_version=self._options.policy_version,
            direction=resolved_direction,
            score=score,
            confidence=confidence,
            expiry_metrics=[expiry_metrics],
            iv_term_structure=term_points,
            near_to_next_iv_slope=near_to_next_slope,
            near_to_far_iv_slope=near_to_far_slope,
            iv_term_structure_state=term_state,
            input_snapshot_count=len(source_snapshots),
            accepted_contract_count=len(current_entries),
            accepted_strike_count=strike_count,
            component_coverage=component_coverage,
            data_quality_status=quality,
            is_stale=is_stale,
            is_eligible_for_fusion=is_eligible,
            revision=revision,
            evidence=evidence,
            warnings=sorted(set(warnings)),
            selection_authority=False,
            execution_authority=False,
        )

    def _confidence(
        self,
        *,
        evidence,
        score: Decimal,
        component_coverage: Decimal,
        contract_count: int,
        strike_count: int,
    ) -> Decimal:
        contract_coverage = min(
            ONE,
            Decimal(contract_count) / Decimal(self._options.minimum_contract_count),
        )
        strike_coverage = min(
            ONE,
            Decimal(strike_count) / Decimal(self._options.minimum_strike_count),
        )
        return quantize(
            clip(
                component_coverage * Decimal("0.35")
                + contract_coverage * Decimal("0.15")
                + strike_coverage * Decimal("0.15")
                + agreement(evidence, score) * Decimal("0.20")
                + abs(score) * Decimal("0.15")
            )
        )

    def _quality(
        self,
        *,
        current: OptionChainSnapshotObservation,
        call_count: int,
        put_count: int,
        contract_count: int,
        strike_count: int,
        component_coverage: Decimal,
    ) -> str:
        if not is_snapshot_eligible(current) or call_count == 0 or put_count == 0:
            return "INVALID"
        if (
            contract_count < self._options.minimum_contract_count
            or strike_count < self._options.minimum_strike_count
            or component_coverage < Decimal("0.50")
        ):
            return "DEGRADED"
        return "VALID"
