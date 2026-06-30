from datetime import UTC, datetime
from decimal import Decimal
from uuid import NAMESPACE_URL, UUID, uuid5

from app.contracts.v1.directional import (
    DirectionalEngineOutputV1,
    DirectionalEvidenceV1,
)
from app.contracts.v1.market_data import FeatureSnapshotV1
from app.directional.definitions import (
    COMPONENT_WEIGHTS,
    NEGATIVE_ONE,
    ONE,
    QUANTUM,
    ZERO,
    DirectionalEngineOptions,
)


class DeterministicDirectionalCalculator:
    def __init__(self, options: DirectionalEngineOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> DirectionalEngineOptions:
        return self._options

    def calculate(
        self,
        snapshot: FeatureSnapshotV1,
        generated_at_utc: datetime,
        revision: int,
    ) -> DirectionalEngineOutputV1:
        if not snapshot.is_eligible_for_engines:
            raise ValueError("Feature snapshot is not eligible for intelligence engines")
        if snapshot.data_quality_status != "VALID" or snapshot.is_stale:
            raise ValueError("Feature snapshot must be valid and fresh")

        values = {item.name: item.value for item in snapshot.features}
        required = {
            "trend_score",
            "trend_spread_5_20",
            "close_return_1",
            "close_return_3",
            "momentum_5",
            "close_location_value",
            "volume_ratio_20",
        }
        missing = sorted(name for name in required if values.get(name) is None)
        if missing:
            raise ValueError(
                "Required directional features are missing: " + ", ".join(missing)
            )

        components = {
            "TREND_SCORE": _clamp(values["trend_score"] or ZERO),
            "TREND_SPREAD": _clamp((values["trend_spread_5_20"] or ZERO) * Decimal("40")),
            "MOMENTUM": _clamp(
                (((values["close_return_3"] or ZERO) * Decimal("18"))
                + ((values["momentum_5"] or ZERO) * Decimal("12")))
                / Decimal("2")
            ),
            "CLOSE_LOCATION": _clamp(values["close_location_value"] or ZERO),
            "SHORT_RETURN": _clamp((values["close_return_1"] or ZERO) * Decimal("30")),
        }
        provisional_without_volume = sum(
            components[name] * COMPONENT_WEIGHTS[name]
            for name in (
                "TREND_SCORE",
                "TREND_SPREAD",
                "MOMENTUM",
                "CLOSE_LOCATION",
                "SHORT_RETURN",
            )
        )
        directional_sign = _sign(provisional_without_volume)
        volume_ratio = values["volume_ratio_20"] or ONE
        volume_strength = _clamp((volume_ratio - ONE) / Decimal("1.5"))
        components["VOLUME_CONFIRMATION"] = volume_strength * Decimal(directional_sign)

        score = _quantize(
            sum(
                components[name] * weight
                for name, weight in COMPONENT_WEIGHTS.items()
            )
        )
        direction = self._direction(score)
        agreement = self._agreement(score, components)
        confidence = _quantize(
            _clamp_unit(
                Decimal("0.15")
                + (abs(score) * Decimal("0.65"))
                + (agreement * Decimal("0.20"))
            )
        )
        generated_at = _as_utc(generated_at_utc)
        output_uid = self.create_output_uid(
            snapshot.snapshot_uid,
            self._options.policy_version,
            revision,
        )
        message_uid = self.create_message_uid(output_uid)
        evidence = [
            _evidence(name, components[name], COMPONENT_WEIGHTS[name])
            for name in COMPONENT_WEIGHTS
        ]
        warnings: list[str] = []
        if direction == "NEUTRAL":
            warnings.append("DIRECTIONAL_CONVICTION_BELOW_THRESHOLD")

        return DirectionalEngineOutputV1(
            output_uid=output_uid,
            message_uid=message_uid,
            source_feature_snapshot_uid=snapshot.snapshot_uid,
            instrument_key=snapshot.instrument_key,
            timeframe=snapshot.timeframe,
            as_of_utc=snapshot.as_of_utc,
            generated_at_utc=generated_at,
            engine_code=self._options.engine_code,
            engine_version=self._options.engine_version,
            policy_version=self._options.policy_version,
            feature_set_version=snapshot.feature_set_version,
            direction=direction,
            score=score,
            confidence=confidence,
            data_quality_status="VALID",
            is_stale=False,
            is_eligible_for_fusion=True,
            revision=revision,
            evidence=evidence,
            warnings=warnings,
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
    def _agreement(
        score: Decimal,
        components: dict[str, Decimal],
    ) -> Decimal:
        score_sign = _sign(score)
        if score_sign == 0:
            aligned = sum(
                weight
                for name, weight in COMPONENT_WEIGHTS.items()
                if abs(components[name]) <= Decimal("0.15")
            )
        else:
            aligned = sum(
                weight
                for name, weight in COMPONENT_WEIGHTS.items()
                if _sign(components[name]) == score_sign
            )
        return _clamp_unit(aligned)

    @staticmethod
    def create_output_uid(
        feature_snapshot_uid: UUID,
        policy_version: str,
        revision: int,
    ) -> UUID:
        return uuid5(
            NAMESPACE_URL,
            f"directional-output|{feature_snapshot_uid}|{policy_version}|{revision}",
        )

    @staticmethod
    def create_message_uid(output_uid: UUID) -> UUID:
        return uuid5(NAMESPACE_URL, f"directional-message|{output_uid}")


def _evidence(
    code: str,
    contribution: Decimal,
    weight: Decimal,
) -> DirectionalEvidenceV1:
    if contribution > Decimal("0.05"):
        impact = "SUPPORTS_LONG"
    elif contribution < Decimal("-0.05"):
        impact = "SUPPORTS_SHORT"
    else:
        impact = "NEUTRAL"
    readable = code.replace("_", " ").title()
    return DirectionalEvidenceV1(
        code=code,
        message=f"{readable} normalized contribution is {_quantize(contribution)}",
        impact=impact,
        weight=weight,
        contribution=_quantize(contribution),
    )


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
