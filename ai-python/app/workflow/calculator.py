from dataclasses import dataclass
from datetime import UTC, datetime
from decimal import Decimal
from uuid import NAMESPACE_URL, uuid5

from app.contracts.v1.confirmation import MultiTimeframeConfirmationOutputV1
from app.contracts.v1.directional import DirectionalEngineOutputV1
from app.contracts.v1.market_data import FeatureSnapshotV1, MarketCandleDeliveryV1
from app.contracts.v1.regime import MarketRegimeOutputV1
from app.contracts.v1.workflow import (
    FusionDirectionalEvidenceV1,
    FusionReadyEvidenceV1,
    FusionRegimeEvidenceV1,
    FusionTimeframeConfirmationV1,
    FusionTradeProposalV1,
    FusionTradeTargetProposalV1,
)

ZERO = Decimal("0")
ONE_HUNDRED = Decimal("100")
PRICE_QUANTUM = Decimal("0.000001")
SCORE_QUANTUM = Decimal("0.01")


@dataclass(frozen=True, slots=True)
class FusionReadyEvidenceOptions:
    weight_configuration_version: str = "fusion-weights-v1.0.0"
    proposal_policy_version: str = "atr-trade-proposal-v1.0.0"
    stop_atr_multiple: Decimal = Decimal("1.50")
    first_target_risk_reward: Decimal = Decimal("2.00")
    entry_band_atr_fraction: Decimal = Decimal("0.25")
    minimum_entry_band_fraction: Decimal = Decimal("0.0005")
    maximum_entry_band_fraction: Decimal = Decimal("0.0050")
    maximum_slippage_fraction: Decimal = Decimal("0.0010")

    def validate(self) -> None:
        if not self.weight_configuration_version.strip():
            raise ValueError("weight_configuration_version is required")
        if not self.proposal_policy_version.strip():
            raise ValueError("proposal_policy_version is required")
        if self.stop_atr_multiple <= ZERO:
            raise ValueError("stop_atr_multiple must be positive")
        if self.first_target_risk_reward <= ZERO:
            raise ValueError("first_target_risk_reward must be positive")
        if self.entry_band_atr_fraction < ZERO:
            raise ValueError("entry_band_atr_fraction cannot be negative")
        if self.minimum_entry_band_fraction < ZERO:
            raise ValueError("minimum_entry_band_fraction cannot be negative")
        if self.maximum_entry_band_fraction <= ZERO:
            raise ValueError("maximum_entry_band_fraction must be positive")
        if self.minimum_entry_band_fraction > self.maximum_entry_band_fraction:
            raise ValueError("entry band fractions are invalid")
        if self.maximum_slippage_fraction < ZERO:
            raise ValueError("maximum_slippage_fraction cannot be negative")


class FusionReadyEvidenceCalculator:
    def __init__(self, options: FusionReadyEvidenceOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> FusionReadyEvidenceOptions:
        return self._options

    def calculate(
        self,
        delivery: MarketCandleDeliveryV1,
        primary_feature: FeatureSnapshotV1,
        confirmation: MultiTimeframeConfirmationOutputV1,
        directional_by_timeframe: dict[str, DirectionalEngineOutputV1],
        regime_by_timeframe: dict[str, MarketRegimeOutputV1],
        generated_at_utc: datetime,
    ) -> FusionReadyEvidenceV1:
        payload = delivery.envelope.payload
        if payload.timeframe != "5m":
            raise ValueError("Only a closed 5m candle can trigger workflow evidence")
        if not payload.is_closed or payload.is_provisional:
            raise ValueError("Workflow evidence requires a closed non-provisional candle")
        if not payload.is_usable_for_new_exposure:
            raise ValueError("Source candle is not usable for new exposure")
        if primary_feature.timeframe != "5m":
            raise ValueError("Primary feature snapshot must use the 5m timeframe")
        if primary_feature.as_of_utc != payload.close_at_utc:
            raise ValueError("Primary feature cutoff must match the source candle")
        if confirmation.primary_timeframe != "5m":
            raise ValueError("Confirmation primary timeframe must be 5m")
        if confirmation.as_of_utc != payload.close_at_utc:
            raise ValueError("Confirmation cutoff must match the source candle")
        if not confirmation.is_eligible_for_fusion:
            raise ValueError("Confirmation output is not eligible for fusion")

        normalized_direction = _direction(confirmation.direction)
        if normalized_direction == "NEUTRAL":
            raise ValueError("Neutral confirmation cannot create workflow evidence")

        required_timeframes = {
            item.timeframe for item in confirmation.timeframe_confirmations
        }
        if not {"5m", "15m", "1h"}.issubset(required_timeframes):
            raise ValueError("Required 5m, 15m, and 1h confirmations are missing")

        directional_evidence: list[FusionDirectionalEvidenceV1] = []
        timeframe_evidence: list[FusionTimeframeConfirmationV1] = []
        for item in confirmation.timeframe_confirmations:
            directional = directional_by_timeframe.get(item.timeframe)
            regime = regime_by_timeframe.get(item.timeframe)
            if directional is None or regime is None:
                raise ValueError(f"Missing source intelligence for {item.timeframe}")
            if directional.output_uid != item.directional_output_uid:
                raise ValueError(f"Directional lineage mismatch for {item.timeframe}")
            if regime.output_uid != item.regime_output_uid:
                raise ValueError(f"Regime lineage mismatch for {item.timeframe}")
            if directional.as_of_utc != regime.as_of_utc:
                raise ValueError(f"Source cutoffs differ for {item.timeframe}")
            if directional.as_of_utc > confirmation.as_of_utc:
                raise ValueError(f"Future intelligence detected for {item.timeframe}")

            directional_evidence.extend(_directional_votes(directional))
            timeframe_evidence.append(
                FusionTimeframeConfirmationV1(
                    timeframe=item.timeframe,
                    directional_output_uid=directional.output_uid,
                    regime_output_uid=regime.output_uid,
                    direction=_direction(item.direction),
                    score=_percent(abs(item.directional_score)),
                    confidence=_percent(directional.confidence),
                    is_closed_candle=True,
                    observed_at_utc=directional.as_of_utc,
                    reasons=[
                        evidence.message for evidence in directional.evidence
                    ]
                    + [
                        evidence.message for evidence in regime.evidence
                    ],
                )
            )

        primary_regime = regime_by_timeframe.get("5m")
        if primary_regime is None:
            raise ValueError("Primary 5m regime output is missing")
        regime_evidence = FusionRegimeEvidenceV1(
            output_uid=primary_regime.output_uid,
            regime_code=(
                f"{primary_regime.structure_regime}_{primary_regime.volatility_regime}"
            ),
            engine_version=primary_regime.engine_version,
            timeframe="5m",
            directional_bias=_direction(primary_regime.direction_bias),
            confidence=_percent(primary_regime.confidence),
            observed_at_utc=primary_regime.as_of_utc,
            reasons=[item.message for item in primary_regime.evidence],
        )

        feature_values = {item.name: item.value for item in primary_feature.features}
        atr = feature_values.get("atr_14")
        if atr is None or atr <= ZERO:
            raise ValueError("A positive point-in-time ATR is required")
        proposal = self._proposal(
            normalized_direction,
            payload.close_price,
            atr,
        )

        evidence_uid = uuid5(
            NAMESPACE_URL,
            "|".join(
                [
                    "fusion-ready-evidence-v1",
                    str(delivery.envelope.metadata.message_id),
                    str(confirmation.output_uid),
                    self._options.weight_configuration_version,
                    self._options.proposal_policy_version,
                ]
            ),
        )
        warnings = sorted(
            set(
                confirmation.warnings
                + primary_feature.warnings
                + [
                    warning
                    for output in directional_by_timeframe.values()
                    for warning in output.warnings
                ]
                + [
                    warning
                    for output in regime_by_timeframe.values()
                    for warning in output.warnings
                ]
            )
        )
        return FusionReadyEvidenceV1(
            evidence_uid=evidence_uid,
            source_candle_message_uid=delivery.envelope.metadata.message_id,
            confirmation_output_uid=confirmation.output_uid,
            confirmation_message_uid=confirmation.message_uid,
            correlation_id=delivery.envelope.metadata.correlation_id,
            instrument_key=payload.instrument_key,
            primary_timeframe="5m",
            as_of_utc=confirmation.as_of_utc,
            generated_at_utc=_as_utc(generated_at_utc),
            weight_configuration_version=self._options.weight_configuration_version,
            directional_evidence=directional_evidence,
            regime=regime_evidence,
            timeframe_confirmations=timeframe_evidence,
            trade_proposal=proposal,
            is_eligible_for_workflow=True,
            warnings=warnings,
        )

    def _proposal(
        self,
        direction: str,
        reference_price: Decimal,
        atr: Decimal,
    ) -> FusionTradeProposalV1:
        minimum_band = reference_price * self._options.minimum_entry_band_fraction
        maximum_band = reference_price * self._options.maximum_entry_band_fraction
        atr_band = atr * self._options.entry_band_atr_fraction
        band = min(max(atr_band, minimum_band), maximum_band)
        risk_distance = atr * self._options.stop_atr_multiple
        if direction == "LONG":
            stop = reference_price - risk_distance
            target = reference_price + (
                risk_distance * self._options.first_target_risk_reward
            )
        else:
            stop = reference_price + risk_distance
            target = reference_price - (
                risk_distance * self._options.first_target_risk_reward
            )
        if stop <= ZERO or target <= ZERO:
            raise ValueError("ATR proposal produced a non-positive price")
        return FusionTradeProposalV1(
            direction=direction,
            reference_price=_price(reference_price),
            minimum_acceptable_price=_price(reference_price - band),
            maximum_acceptable_price=_price(reference_price + band),
            stop_loss_price=_price(stop),
            targets=[
                FusionTradeTargetProposalV1(
                    sequence=1,
                    price=_price(target),
                    quantity_fraction=Decimal("1.0"),
                )
            ],
            maximum_slippage_fraction=self._options.maximum_slippage_fraction,
            proposal_policy_version=self._options.proposal_policy_version,
        )


def _directional_votes(
    output: DirectionalEngineOutputV1,
) -> list[FusionDirectionalEvidenceV1]:
    by_code = {item.code: item for item in output.evidence}
    groups = {
        "TREND": ("TREND_SCORE", "TREND_SPREAD"),
        "MOMENTUM": (
            "MOMENTUM",
            "CLOSE_LOCATION",
            "SHORT_RETURN",
            "VOLUME_CONFIRMATION",
        ),
    }
    votes: list[FusionDirectionalEvidenceV1] = []
    for engine_code, codes in groups.items():
        selected = [by_code[code] for code in codes if code in by_code]
        if not selected:
            continue
        contribution = sum((item.contribution for item in selected), ZERO) / Decimal(
            len(selected)
        )
        votes.append(
            FusionDirectionalEvidenceV1(
                output_uid=output.output_uid,
                engine_code=engine_code,
                engine_version=output.engine_version,
                timeframe=output.timeframe,
                direction=_signed_direction(contribution),
                score=_percent(abs(contribution)),
                confidence=_percent(output.confidence),
                observed_at_utc=output.as_of_utc,
                reasons=[item.message for item in selected],
            )
        )
    return votes


def _direction(value: str) -> str:
    normalized = value.strip().upper()
    if normalized in {"LONG", "STRONG_LONG"}:
        return "LONG"
    if normalized in {"SHORT", "STRONG_SHORT"}:
        return "SHORT"
    return "NEUTRAL"


def _signed_direction(value: Decimal) -> str:
    if value > ZERO:
        return "LONG"
    if value < ZERO:
        return "SHORT"
    return "NEUTRAL"


def _percent(value: Decimal) -> Decimal:
    return (value * ONE_HUNDRED).quantize(SCORE_QUANTUM)


def _price(value: Decimal) -> Decimal:
    return value.quantize(PRICE_QUANTUM)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
