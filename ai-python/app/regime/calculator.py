from datetime import UTC, datetime
from decimal import Decimal
from uuid import NAMESPACE_URL, UUID, uuid5

from app.contracts.v1.market_data import FeatureSnapshotV1
from app.contracts.v1.regime import MarketRegimeOutputV1, RegimeEvidenceV1
from app.regime.definitions import (
    NEGATIVE_ONE,
    ONE,
    QUANTUM,
    REGIME_EVIDENCE_WEIGHTS,
    TREND_COMPONENT_WEIGHTS,
    VOLATILITY_BANDS,
    ZERO,
    MarketRegimeOptions,
)


class DeterministicMarketRegimeCalculator:
    def __init__(self, options: MarketRegimeOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> MarketRegimeOptions:
        return self._options

    def calculate(
        self,
        snapshot: FeatureSnapshotV1,
        generated_at_utc: datetime,
        revision: int,
    ) -> MarketRegimeOutputV1:
        if not snapshot.is_eligible_for_engines:
            raise ValueError("Feature snapshot is not eligible for intelligence engines")
        if snapshot.data_quality_status != "VALID" or snapshot.is_stale:
            raise ValueError("Feature snapshot must be valid and fresh")

        values = {item.name: item.value for item in snapshot.features}
        required = {
            "trend_score",
            "trend_spread_5_20",
            "momentum_5",
            "close_return_3",
            "realized_volatility_20",
            "atr_14",
            "sma_20",
            "volume_ratio_20",
        }
        missing = sorted(name for name in required if values.get(name) is None)
        if missing:
            raise ValueError(
                "Required regime features are missing: " + ", ".join(missing)
            )

        sma_20 = values["sma_20"] or ZERO
        if sma_20 <= ZERO:
            raise ValueError("sma_20 must be positive for volatility normalization")

        components = {
            "TREND_SCORE": _clamp(values["trend_score"] or ZERO),
            "TREND_SPREAD": _clamp(
                (values["trend_spread_5_20"] or ZERO) * Decimal("40")
            ),
            "MOMENTUM": _clamp((values["momentum_5"] or ZERO) * Decimal("20")),
            "RETURN_3": _clamp((values["close_return_3"] or ZERO) * Decimal("25")),
        }
        score = _clamp(
            sum(
                components[name] * weight
                for name, weight in TREND_COMPONENT_WEIGHTS.items()
            )
        )
        score_sign = _sign(score)
        alignment = _alignment(score_sign, components)
        trend_strength = _clamp_unit(
            (abs(score) * Decimal("0.70"))
            + (alignment * Decimal("0.30"))
        )

        realized_volatility = abs(values["realized_volatility_20"] or ZERO)
        atr_ratio = abs((values["atr_14"] or ZERO) / sma_20)
        effective_volatility = max(realized_volatility, atr_ratio)
        volatility_regime, volatility_score = _volatility(
            snapshot.timeframe,
            effective_volatility,
        )
        volume_expansion = _clamp_unit(
            abs((values["volume_ratio_20"] or ONE) - ONE) / Decimal("2")
        )
        range_score = _clamp_unit(
            ONE
            - (
                abs(components["TREND_SCORE"]) * Decimal("0.30")
                + abs(components["TREND_SPREAD"]) * Decimal("0.20")
                + abs(components["MOMENTUM"]) * Decimal("0.15")
                + abs(components["RETURN_3"]) * Decimal("0.10")
                + volatility_score * Decimal("0.15")
                + volume_expansion * Decimal("0.10")
            )
        )
        disagreement = ONE - alignment if score_sign != 0 else ZERO
        transition_score = _clamp_unit(
            disagreement * Decimal("0.45")
            + abs(components["MOMENTUM"] - components["TREND_SCORE"])
            * Decimal("0.20")
            + abs(components["RETURN_3"] - components["TREND_SPREAD"])
            * Decimal("0.15")
            + volatility_score * Decimal("0.10")
            + volume_expansion * Decimal("0.10")
        )
        structure_regime = self._structure(
            score,
            alignment,
            range_score,
            transition_score,
        )
        direction_bias = self._direction_bias(structure_regime, trend_strength)
        confidence = self._confidence(
            structure_regime,
            trend_strength,
            range_score,
            transition_score,
            volatility_score,
        )
        generated_at = _as_utc(generated_at_utc)
        output_uid = self.create_output_uid(
            snapshot.snapshot_uid,
            self._options.policy_version,
            revision,
        )
        evidence = _evidence(
            structure_regime=structure_regime,
            volatility_regime=volatility_regime,
            score=score,
            alignment=alignment,
            range_score=range_score,
            transition_score=transition_score,
            volatility_score=volatility_score,
        )
        warnings: list[str] = []
        if structure_regime == "TRANSITION":
            warnings.append("REGIME_TRANSITION_DETECTED")
        if volatility_regime == "HIGH":
            warnings.append("HIGH_VOLATILITY_REGIME")
        elif volatility_regime == "EXTREME":
            warnings.append("EXTREME_VOLATILITY_REGIME")
        if confidence < Decimal("0.45"):
            warnings.append("LOW_REGIME_CONFIDENCE")

        return MarketRegimeOutputV1(
            output_uid=output_uid,
            message_uid=self.create_message_uid(output_uid),
            source_feature_snapshot_uid=snapshot.snapshot_uid,
            instrument_key=snapshot.instrument_key,
            timeframe=snapshot.timeframe,
            as_of_utc=snapshot.as_of_utc,
            generated_at_utc=generated_at,
            engine_code=self._options.engine_code,
            engine_version=self._options.engine_version,
            policy_version=self._options.policy_version,
            feature_set_version=snapshot.feature_set_version,
            structure_regime=structure_regime,
            volatility_regime=volatility_regime,
            direction_bias=direction_bias,
            score=_quantize(score),
            confidence=_quantize(confidence),
            trend_strength=_quantize(trend_strength),
            range_score=_quantize(range_score),
            transition_score=_quantize(transition_score),
            volatility_score=_quantize(volatility_score),
            data_quality_status="VALID",
            is_stale=False,
            is_eligible_for_fusion=True,
            revision=revision,
            evidence=evidence,
            warnings=warnings,
        )

    def _structure(
        self,
        score: Decimal,
        alignment: Decimal,
        range_score: Decimal,
        transition_score: Decimal,
    ) -> str:
        if (
            abs(score) >= self._options.trend_threshold
            and alignment >= self._options.alignment_threshold
        ):
            return "TRENDING_UP" if score > ZERO else "TRENDING_DOWN"
        if (
            range_score >= self._options.range_threshold
            and transition_score < Decimal("0.45")
        ):
            return "RANGE_BOUND"
        return "TRANSITION"

    def _direction_bias(self, structure_regime: str, trend_strength: Decimal) -> str:
        if structure_regime == "TRENDING_UP":
            if trend_strength >= self._options.strong_trend_threshold:
                return "STRONG_LONG"
            return "LONG"
        if structure_regime == "TRENDING_DOWN":
            if trend_strength >= self._options.strong_trend_threshold:
                return "STRONG_SHORT"
            return "SHORT"
        return "NEUTRAL"

    @staticmethod
    def _confidence(
        structure_regime: str,
        trend_strength: Decimal,
        range_score: Decimal,
        transition_score: Decimal,
        volatility_score: Decimal,
    ) -> Decimal:
        if structure_regime in {"TRENDING_UP", "TRENDING_DOWN"}:
            return _clamp_unit(
                Decimal("0.15")
                + trend_strength * Decimal("0.55")
                + (ONE - transition_score) * Decimal("0.30")
            )
        if structure_regime == "RANGE_BOUND":
            return _clamp_unit(
                Decimal("0.15")
                + range_score * Decimal("0.60")
                + (ONE - volatility_score) * Decimal("0.25")
            )
        return _clamp_unit(
            Decimal("0.15")
            + transition_score * Decimal("0.60")
            + volatility_score * Decimal("0.25")
        )

    @staticmethod
    def create_output_uid(
        feature_snapshot_uid: UUID,
        policy_version: str,
        revision: int,
    ) -> UUID:
        return uuid5(
            NAMESPACE_URL,
            f"market-regime-output|{feature_snapshot_uid}|{policy_version}|{revision}",
        )

    @staticmethod
    def create_message_uid(output_uid: UUID) -> UUID:
        return uuid5(NAMESPACE_URL, f"market-regime-message|{output_uid}")


def _volatility(timeframe: str, effective_volatility: Decimal) -> tuple[str, Decimal]:
    band = VOLATILITY_BANDS[timeframe]
    score = _clamp_unit(effective_volatility / band.extreme)
    if effective_volatility <= band.low:
        return "LOW", score
    if effective_volatility <= band.high:
        return "NORMAL", score
    if effective_volatility <= band.extreme:
        return "HIGH", score
    return "EXTREME", score


def _alignment(sign: int, components: dict[str, Decimal]) -> Decimal:
    if sign == 0:
        magnitude = sum(
            abs(components[name]) * weight
            for name, weight in TREND_COMPONENT_WEIGHTS.items()
        )
        return _clamp_unit(ONE - magnitude)
    return _clamp_unit(
        sum(
            weight
            for name, weight in TREND_COMPONENT_WEIGHTS.items()
            if _sign(components[name]) == sign
        )
    )


def _evidence(
    *,
    structure_regime: str,
    volatility_regime: str,
    score: Decimal,
    alignment: Decimal,
    range_score: Decimal,
    transition_score: Decimal,
    volatility_score: Decimal,
) -> list[RegimeEvidenceV1]:
    sign = _sign(score)
    alignment_impact = (
        _directional_impact(sign)
        if structure_regime.startswith("TRENDING")
        else "NEUTRAL"
    )
    return [
        RegimeEvidenceV1(
            code="REGIME_TREND_BIAS",
            message=f"Weighted technical trend bias is {_quantize(score)}",
            impact=_directional_impact(sign),
            weight=REGIME_EVIDENCE_WEIGHTS["REGIME_TREND_BIAS"],
            contribution=_quantize(score),
        ),
        RegimeEvidenceV1(
            code="REGIME_TREND_ALIGNMENT",
            message=f"Directional component alignment is {_quantize(alignment)}",
            impact=alignment_impact,
            weight=REGIME_EVIDENCE_WEIGHTS["REGIME_TREND_ALIGNMENT"],
            contribution=_quantize(alignment * Decimal(sign)),
        ),
        RegimeEvidenceV1(
            code="REGIME_RANGE_COMPRESSION",
            message=f"Range compression score is {_quantize(range_score)}",
            impact="NEUTRAL",
            weight=REGIME_EVIDENCE_WEIGHTS["REGIME_RANGE_COMPRESSION"],
            contribution=_quantize(range_score),
        ),
        RegimeEvidenceV1(
            code="REGIME_TRANSITION_RISK",
            message=f"Transition risk score is {_quantize(transition_score)}",
            impact=(
                "CONTRADICTS" if structure_regime == "TRANSITION" else "NEUTRAL"
            ),
            weight=REGIME_EVIDENCE_WEIGHTS["REGIME_TRANSITION_RISK"],
            contribution=_quantize(-transition_score),
        ),
        RegimeEvidenceV1(
            code="REGIME_VOLATILITY_STATE",
            message=(
                f"Volatility regime is {volatility_regime} with normalized score "
                f"{_quantize(volatility_score)}"
            ),
            impact="NEUTRAL",
            weight=REGIME_EVIDENCE_WEIGHTS["REGIME_VOLATILITY_STATE"],
            contribution=_quantize(volatility_score),
        ),
    ]


def _directional_impact(sign: int) -> str:
    if sign > 0:
        return "SUPPORTS_LONG"
    if sign < 0:
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
