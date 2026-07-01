from datetime import UTC, datetime
from decimal import Decimal
from uuid import NAMESPACE_URL, uuid5

from app.contracts.v1.option_chain import OptionChainIntelligenceOutputV1
from app.contracts.v1.workflow import (
    FusionDirectionalEvidenceV1,
    FusionReadyEvidenceV1,
)

ONE_HUNDRED = Decimal("100")
SCORE_QUANTUM = Decimal("0.01")


def append_option_chain_evidence(
    evidence: FusionReadyEvidenceV1,
    output: OptionChainIntelligenceOutputV1 | None,
    instrument_key: str,
    workflow_cutoff_utc: datetime,
    maximum_age_seconds: int,
) -> FusionReadyEvidenceV1:
    if output is None:
        return evidence
    cutoff = _as_utc(workflow_cutoff_utc)
    observed = _as_utc(output.as_of_utc)
    generated = _as_utc(output.generated_at_utc)

    if output.underlying_instrument_key != instrument_key:
        raise ValueError("Option Chain instrument lineage mismatch")
    if observed > cutoff:
        raise ValueError("Option Chain output is future-dated")
    if generated > cutoff:
        raise ValueError("Option Chain knowledge cutoff exceeds the workflow cutoff")
    if output.selection_authority or output.execution_authority:
        raise ValueError("Option Chain authority drift detected")
    if maximum_age_seconds < 1:
        raise ValueError("Option Chain freshness policy is invalid")

    warnings = set(evidence.warnings + output.warnings)
    age_seconds = (cutoff - observed).total_seconds()
    if age_seconds < 0 or age_seconds > maximum_age_seconds:
        warnings.add("OPTION_CHAIN_WORKFLOW_STALE")
        return evidence.model_copy(update={"warnings": sorted(warnings)})
    if output.data_quality_status != "VALID":
        warnings.add("OPTION_CHAIN_DATA_QUALITY_NOT_VALID")
        return evidence.model_copy(update={"warnings": sorted(warnings)})
    if output.is_stale:
        warnings.add("OPTION_CHAIN_STALE")
        return evidence.model_copy(update={"warnings": sorted(warnings)})
    if not output.is_eligible_for_fusion or output.direction == "NEUTRAL":
        warnings.add("OPTION_CHAIN_NOT_ELIGIBLE_FOR_FUSION")
        return evidence.model_copy(update={"warnings": sorted(warnings)})

    vote = FusionDirectionalEvidenceV1(
        output_uid=output.output_uid,
        engine_code="OPTION_CHAIN",
        engine_version=output.engine_version,
        timeframe="OPTION_CHAIN",
        direction=output.direction,
        score=(abs(output.score) * ONE_HUNDRED).quantize(SCORE_QUANTUM),
        confidence=(output.confidence * ONE_HUNDRED).quantize(SCORE_QUANTUM),
        observed_at_utc=observed,
        reasons=[item.message for item in output.evidence],
    )
    evidence_uid = uuid5(
        NAMESPACE_URL,
        "|".join(
            [
                "fusion-ready-evidence-option-chain-v1",
                str(evidence.evidence_uid),
                str(output.output_uid),
                output.policy_version,
                evidence.weight_configuration_version,
            ]
        ),
    )
    return evidence.model_copy(
        update={
            "evidence_uid": evidence_uid,
            "directional_evidence": evidence.directional_evidence + [vote],
            "warnings": sorted(warnings),
        }
    )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
