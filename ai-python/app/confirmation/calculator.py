from datetime import UTC, datetime
from decimal import Decimal
from uuid import NAMESPACE_URL, UUID, uuid5

from app.confirmation.definitions import (
    NEGATIVE_ONE,
    ONE,
    QUANTUM,
    REQUIRED_TIMEFRAMES,
    TIMEFRAME_WEIGHTS,
    ZERO,
    MultiTimeframeConfirmationOptions,
)
from app.confirmation.models import ConfirmationInputBundle
from app.contracts.v1.confirmation import (
    ConfirmationEvidenceV1,
    MultiTimeframeConfirmationOutputV1,
    TimeframeConfirmationV1,
)


class DeterministicMultiTimeframeConfirmationCalculator:
    def __init__(self, options: MultiTimeframeConfirmationOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> MultiTimeframeConfirmationOptions:
        return self._options

    def calculate(
        self,
        bundle: ConfirmationInputBundle,
        generated_at_utc: datetime,
        revision: int,
    ) -> MultiTimeframeConfirmationOutputV1:
        pairs = {pair.timeframe: pair for pair in bundle.pairs}
        if "5m" not in pairs:
            raise ValueError("Primary 5m directional and regime outputs are required")
        if any(pair.directional.output.instrument_key != bundle.instrument_key for pair in bundle.pairs):
            raise ValueError("Directional inputs must belong to one instrument")
        if any(pair.regime.output.instrument_key != bundle.instrument_key for pair in bundle.pairs):
            raise ValueError("Regime inputs must belong to one instrument")

        primary = pairs["5m"].directional.output
        primary_sign = _sign(primary.score)
        confirmations: list[TimeframeConfirmationV1] = []
        weighted_sum = ZERO
        present_weight = ZERO
        aligned_weight = ZERO
        contradiction_weight = ZERO
        warnings: list[str] = []

        for timeframe in TIMEFRAME_WEIGHTS:
            pair = pairs.get(timeframe)
            if pair is None:
                warnings.append(f"MISSING_TIMEFRAME_{timeframe.upper()}")
                continue
            directional = pair.directional.output
            regime = pair.regime.output
            if directional.is_stale or regime.is_stale:
                warnings.append(f"STALE_TIMEFRAME_{timeframe.upper()}")
                continue
            if not directional.is_eligible_for_fusion or not regime.is_eligible_for_fusion:
                warnings.append(f"INELIGIBLE_TIMEFRAME_{timeframe.upper()}")
                continue

            weight = TIMEFRAME_WEIGHTS[timeframe]
            modifier = _regime_modifier(regime.structure_regime, regime.volatility_regime)
            contribution = _clamp(directional.score * modifier)
            effective_weight = _quantize(weight)
            signed_contribution = _quantize(contribution)
            contribution_sign = _sign(contribution)
            agrees = primary_sign == 0 or contribution_sign in {0, primary_sign}
            if agrees:
                aligned_weight += weight
            elif contribution_sign != 0:
                contradiction_weight += weight

            present_weight += weight
            weighted_sum += contribution * weight
            confirmations.append(
                TimeframeConfirmationV1(
                    timeframe=timeframe,
                    directional_output_uid=directional.output_uid,
                    regime_output_uid=regime.output_uid,
                    direction=directional.direction,
                    directional_score=directional.score,
                    regime_bias=regime.direction_bias,
                    structure_regime=regime.structure_regime,
                    volatility_regime=regime.volatility_regime,
                    effective_weight=effective_weight,
                    signed_contribution=signed_contribution,
                    agrees_with_primary=agrees,
                    is_fresh=True,
                )
            )

        required_present = REQUIRED_TIMEFRAMES.issubset(
            {item.timeframe for item in confirmations}
        )
        coverage = _quantize(present_weight)
        alignment = _quantize(
            ZERO if present_weight == ZERO else aligned_weight / present_weight
        )
        contradiction = _quantize(
            ZERO if present_weight == ZERO else contradiction_weight / present_weight
        )
        score = _quantize(
            ZERO if present_weight == ZERO else weighted_sum / present_weight
        )
        direction = self._direction(score)
        confidence = _quantize(
            _clamp_unit(
                (abs(score) * Decimal("0.55"))
                + (alignment * Decimal("0.30"))
                + (coverage * Decimal("0.15"))
            )
        )
        eligible = (
            required_present
            and coverage >= self._options.minimum_coverage
            and contradiction <= self._options.maximum_contradiction
        )
        quality = "VALID" if eligible else "DEGRADED"
        if not required_present:
            warnings.append("REQUIRED_TIMEFRAMES_MISSING")
        if coverage < self._options.minimum_coverage:
            warnings.append("TIMEFRAME_COVERAGE_BELOW_THRESHOLD")
        if contradiction > self._options.maximum_contradiction:
            warnings.append("TIMEFRAME_CONTRADICTION_ABOVE_THRESHOLD")
        if direction == "NEUTRAL":
            warnings.append("CONFIRMATION_CONVICTION_BELOW_THRESHOLD")

        generated_at = _as_utc(generated_at_utc)
        source_identity = "|".join(
            sorted(
                f"{item.timeframe}:{item.directional_output_uid}:{item.regime_output_uid}"
                for item in confirmations
            )
        )
        output_uid = self.create_output_uid(
            bundle.instrument_key,
            primary.as_of_utc,
            source_identity,
            self._options.policy_version,
            revision,
        )
        evidence = self._evidence(
            direction,
            alignment,
            contradiction,
            coverage,
            required_present,
        )
        return MultiTimeframeConfirmationOutputV1(
            output_uid=output_uid,
            message_uid=self.create_message_uid(output_uid),
            instrument_key=bundle.instrument_key,
            primary_timeframe="5m",
            as_of_utc=primary.as_of_utc,
            generated_at_utc=generated_at,
            engine_code=self._options.engine_code,
            engine_version=self._options.engine_version,
            policy_version=self._options.policy_version,
            direction=direction,
            score=score,
            confidence=confidence,
            alignment_score=alignment,
            contradiction_score=contradiction,
            coverage=coverage,
            required_timeframes_present=required_present,
            data_quality_status=quality,
            is_stale=False,
            is_eligible_for_fusion=eligible,
            revision=revision,
            timeframe_confirmations=confirmations,
            evidence=evidence,
            warnings=sorted(set(warnings)),
        )

    def _direction(self, score: Decimal) -> str:
        if score >= self._options.strong_threshold:
            return "STRONG_LONG"
        if score >= self._options.directional_threshold:
            return "LONG"
        if score <= -self._options.strong_threshold:
            return "STRONG_SHORT"
        if score <= -self._options.directional_threshold:
            return "SHORT"
        return "NEUTRAL"

    @staticmethod
    def _evidence(
        direction: str,
        alignment: Decimal,
        contradiction: Decimal,
        coverage: Decimal,
        required_present: bool,
    ) -> list[ConfirmationEvidenceV1]:
        impact = _impact(direction)
        return [
            ConfirmationEvidenceV1(
                code="TIMEFRAME_ALIGNMENT",
                message=f"Weighted timeframe alignment is {alignment}",
                impact=impact if alignment >= Decimal("0.60") else "CONTRADICTS",
                weight=Decimal("0.40"),
            ),
            ConfirmationEvidenceV1(
                code="TIMEFRAME_CONTRADICTION",
                message=f"Weighted timeframe contradiction is {contradiction}",
                impact="CONTRADICTS" if contradiction > Decimal("0.40") else "NEUTRAL",
                weight=Decimal("0.30"),
            ),
            ConfirmationEvidenceV1(
                code="TIMEFRAME_COVERAGE",
                message=f"Available timeframe coverage is {coverage}",
                impact="NEUTRAL" if coverage < Decimal("0.75") else impact,
                weight=Decimal("0.20"),
            ),
            ConfirmationEvidenceV1(
                code="REQUIRED_TIMEFRAMES",
                message=(
                    "Primary and confirmation timeframes are present"
                    if required_present
                    else "One or more required timeframes are missing"
                ),
                impact=impact if required_present else "CONTRADICTS",
                weight=Decimal("0.10"),
            ),
        ]

    @staticmethod
    def create_output_uid(
        instrument_key: str,
        as_of_utc: datetime,
        source_identity: str,
        policy_version: str,
        revision: int,
    ) -> UUID:
        identity = (
            f"multi-timeframe-confirmation|{instrument_key}|{_as_utc(as_of_utc).isoformat()}|"
            f"{source_identity}|{policy_version}|{revision}"
        )
        return uuid5(NAMESPACE_URL, identity)

    @staticmethod
    def create_message_uid(output_uid: UUID) -> UUID:
        return uuid5(NAMESPACE_URL, f"multi-timeframe-confirmation-message|{output_uid}")


def _regime_modifier(structure: str, volatility: str) -> Decimal:
    structure_modifier = {
        "TRENDING_UP": ONE,
        "TRENDING_DOWN": ONE,
        "RANGE_BOUND": Decimal("0.65"),
        "TRANSITION": Decimal("0.50"),
    }[structure]
    volatility_modifier = {
        "LOW": Decimal("0.90"),
        "NORMAL": ONE,
        "HIGH": Decimal("0.80"),
        "EXTREME": Decimal("0.60"),
    }[volatility]
    return structure_modifier * volatility_modifier


def _impact(direction: str) -> str:
    if direction in {"LONG", "STRONG_LONG"}:
        return "SUPPORTS_LONG"
    if direction in {"SHORT", "STRONG_SHORT"}:
        return "SUPPORTS_SHORT"
    return "NEUTRAL"


def _sign(value: Decimal) -> int:
    if value > ZERO:
        return 1
    if value < ZERO:
        return -1
    return 0


def _clamp(value: Decimal) -> Decimal:
    return max(NEGATIVE_ONE, min(ONE, value))


def _clamp_unit(value: Decimal) -> Decimal:
    return max(ZERO, min(ONE, value))


def _quantize(value: Decimal) -> Decimal:
    return value.quantize(QUANTUM)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
