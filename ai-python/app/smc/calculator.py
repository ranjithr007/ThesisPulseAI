from datetime import UTC, datetime
from decimal import Decimal
from uuid import NAMESPACE_URL, uuid5

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smc import (
    SmartMoneyConceptsOutputV1,
    SmcEvidenceV1,
    SmcZoneV1,
)
from app.features.models import CandleInput
from app.smc.definitions import SmcOptions

ZERO = Decimal("0")
ONE = Decimal("1")
Q = Decimal("0.000001")


class DeterministicSmcCalculator:
    def __init__(self, options: SmcOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> SmcOptions:
        return self._options

    def calculate(
        self,
        delivery: MarketCandleDeliveryV1,
        candles: list[CandleInput],
        generated_at_utc: datetime,
        revision: int,
    ) -> SmartMoneyConceptsOutputV1:
        payload = delivery.envelope.payload
        if payload.timeframe != "5m":
            raise ValueError("SMC V1 only supports the 5m timeframe")
        if not payload.is_closed or payload.is_provisional:
            raise ValueError("SMC requires a closed non-provisional candle")

        ordered = sorted(candles, key=lambda item: item.open_at_utc)
        warnings: list[str] = []
        if len(ordered) < self._options.required_input_count:
            warnings.append("INSUFFICIENT_STRUCTURE_HISTORY")

        pivots_high, pivots_low = _pivots(
            ordered,
            self._options.swing_left_bars,
            self._options.swing_right_bars,
        )
        last_high = pivots_high[-1] if pivots_high else None
        last_low = pivots_low[-1] if pivots_low else None
        prior = ordered[-2] if len(ordered) >= 2 else None
        current = ordered[-1] if ordered else None

        structure_state = _structure_state(pivots_high, pivots_low)
        structure_event = "NONE"
        liquidity_event = "NONE"
        structure_signal = ZERO
        liquidity_signal = ZERO

        if current and prior and last_high:
            threshold = last_high.high_price * self._options.minimum_break_fraction
            if prior.close_price <= last_high.high_price and current.close_price > last_high.high_price + threshold:
                structure_event = "CHOCH_UP" if structure_state == "BEARISH" else "BOS_UP"
                structure_signal = ONE
            elif current.high_price > last_high.high_price and current.close_price <= last_high.high_price:
                liquidity_event = "SWEEP_HIGH"
                liquidity_signal = -ONE

        if current and prior and last_low:
            threshold = last_low.low_price * self._options.minimum_break_fraction
            if prior.close_price >= last_low.low_price and current.close_price < last_low.low_price - threshold:
                structure_event = "CHOCH_DOWN" if structure_state == "BULLISH" else "BOS_DOWN"
                structure_signal = -ONE
            elif current.low_price < last_low.low_price and current.close_price >= last_low.low_price:
                liquidity_event = "SWEEP_LOW"
                liquidity_signal = ONE

        zones = _zones(ordered, structure_event)
        order_block_signal = _zone_signal(zones, "ORDER_BLOCK")
        fvg_signal = _zone_signal(zones, "FVG")

        score = _clip(
            structure_signal * self._options.structure_weight
            + liquidity_signal * self._options.liquidity_weight
            + order_block_signal * self._options.order_block_weight
            + fvg_signal * self._options.fair_value_gap_weight,
            -ONE,
            ONE,
        )
        direction = _direction(score, self._options.directional_threshold)
        completeness = min(
            ONE,
            Decimal(len(ordered)) / Decimal(self._options.required_input_count),
        )
        confidence = _clip(
            completeness * Decimal("0.40")
            + abs(score) * Decimal("0.35")
            + (Decimal("0.15") if structure_event != "NONE" else ZERO)
            + (Decimal("0.10") if liquidity_event != "NONE" else ZERO),
            ZERO,
            ONE,
        )
        if not pivots_high or not pivots_low:
            warnings.append("INCOMPLETE_SWING_STRUCTURE")
        if not zones:
            warnings.append("NO_ACTIVE_SMC_ZONES")

        data_quality_status = "VALID"
        if len(ordered) < self._options.required_input_count:
            data_quality_status = "INVALID"
        elif warnings:
            data_quality_status = "DEGRADED"
        is_eligible = (
            data_quality_status != "INVALID"
            and direction != "NEUTRAL"
            and confidence >= self._options.fusion_confidence_threshold
        )
        evidence = _evidence(
            structure_event,
            liquidity_event,
            zones,
            structure_signal,
            liquidity_signal,
            order_block_signal,
            fvg_signal,
            self._options,
        )
        output_uid = uuid5(
            NAMESPACE_URL,
            "|".join(
                [
                    "smc-output-v1",
                    str(delivery.envelope.metadata.message_id),
                    self._options.policy_version,
                    str(revision),
                ]
            ),
        )
        return SmartMoneyConceptsOutputV1(
            output_uid=output_uid,
            message_uid=uuid5(NAMESPACE_URL, f"smc-message-v1|{output_uid}"),
            source_candle_message_uid=delivery.envelope.metadata.message_id,
            instrument_key=payload.instrument_key,
            timeframe="5m",
            as_of_utc=_as_utc(payload.close_at_utc),
            generated_at_utc=_as_utc(generated_at_utc),
            engine_code=self._options.engine_code,
            engine_version=self._options.engine_version,
            policy_version=self._options.policy_version,
            direction=direction,
            score=_q(score),
            confidence=_q(confidence),
            structure_state=structure_state,
            structure_event=structure_event,
            liquidity_event=liquidity_event,
            last_swing_high=None if last_high is None else _q(last_high.high_price),
            last_swing_low=None if last_low is None else _q(last_low.low_price),
            swing_high_at_utc=None if last_high is None else last_high.open_at_utc,
            swing_low_at_utc=None if last_low is None else last_low.open_at_utc,
            zones=zones,
            input_count=len(ordered),
            required_input_count=self._options.required_input_count,
            data_quality_status=data_quality_status,
            is_stale=False,
            is_eligible_for_fusion=is_eligible,
            revision=revision,
            evidence=evidence,
            warnings=sorted(set(warnings)),
        )


def _pivots(candles: list[CandleInput], left: int, right: int):
    highs: list[CandleInput] = []
    lows: list[CandleInput] = []
    for index in range(left, len(candles) - right):
        item = candles[index]
        left_slice = candles[index - left:index]
        right_slice = candles[index + 1:index + right + 1]
        if all(item.high_price > other.high_price for other in left_slice + right_slice):
            highs.append(item)
        if all(item.low_price < other.low_price for other in left_slice + right_slice):
            lows.append(item)
    return highs, lows


def _structure_state(highs: list[CandleInput], lows: list[CandleInput]) -> str:
    if len(highs) < 2 or len(lows) < 2:
        return "UNKNOWN"
    higher_high = highs[-1].high_price > highs[-2].high_price
    higher_low = lows[-1].low_price > lows[-2].low_price
    lower_high = highs[-1].high_price < highs[-2].high_price
    lower_low = lows[-1].low_price < lows[-2].low_price
    if higher_high and higher_low:
        return "BULLISH"
    if lower_high and lower_low:
        return "BEARISH"
    return "RANGING"


def _zones(candles: list[CandleInput], structure_event: str) -> list[SmcZoneV1]:
    zones: list[SmcZoneV1] = []
    for index in range(2, len(candles)):
        first = candles[index - 2]
        third = candles[index]
        if third.low_price > first.high_price:
            zones.append(_zone("BULLISH_FVG", first.high_price, third.low_price, third, candles))
        elif third.high_price < first.low_price:
            zones.append(_zone("BEARISH_FVG", third.high_price, first.low_price, third, candles))
    if structure_event in {"BOS_UP", "CHOCH_UP"}:
        opposing = next((item for item in reversed(candles[:-1]) if item.close_price < item.open_price), None)
        if opposing:
            zones.append(_zone("BULLISH_ORDER_BLOCK", opposing.low_price, opposing.high_price, opposing, candles))
    elif structure_event in {"BOS_DOWN", "CHOCH_DOWN"}:
        opposing = next((item for item in reversed(candles[:-1]) if item.close_price > item.open_price), None)
        if opposing:
            zones.append(_zone("BEARISH_ORDER_BLOCK", opposing.low_price, opposing.high_price, opposing, candles))
    return zones[-8:]


def _zone(kind: str, lower: Decimal, upper: Decimal, source: CandleInput, candles: list[CandleInput]) -> SmcZoneV1:
    later = [item for item in candles if item.open_at_utc > source.open_at_utc]
    mitigated = any(item.low_price <= upper and item.high_price >= lower for item in later)
    zone_uid = uuid5(NAMESPACE_URL, f"{kind}|{source.instrument_key}|{source.timeframe}|{source.open_at_utc.isoformat()}|{lower}|{upper}")
    return SmcZoneV1(
        zone_uid=zone_uid,
        zone_type=kind,
        lower_price=_q(lower),
        upper_price=_q(upper),
        formed_at_utc=source.close_at_utc,
        source_candle_open_at_utc=source.open_at_utc,
        is_mitigated=mitigated,
    )


def _zone_signal(zones: list[SmcZoneV1], category: str) -> Decimal:
    active = [item for item in zones if not item.is_mitigated and category in item.zone_type]
    if not active:
        return ZERO
    latest = active[-1]
    return ONE if latest.zone_type.startswith("BULLISH") else -ONE


def _evidence(structure_event, liquidity_event, zones, structure_signal, liquidity_signal, ob_signal, fvg_signal, options):
    return [
        SmcEvidenceV1(
            code="MARKET_STRUCTURE",
            message=f"Structure event: {structure_event}.",
            impact=_impact(structure_signal),
            weight=options.structure_weight,
            contribution=_q(structure_signal * options.structure_weight),
        ),
        SmcEvidenceV1(
            code="LIQUIDITY_SWEEP",
            message=f"Liquidity event: {liquidity_event}.",
            impact=_impact(liquidity_signal),
            weight=options.liquidity_weight,
            contribution=_q(liquidity_signal * options.liquidity_weight),
        ),
        SmcEvidenceV1(
            code="ORDER_BLOCK_CONTEXT",
            message=f"Active order-block bias from {len([z for z in zones if 'ORDER_BLOCK' in z.zone_type and not z.is_mitigated])} zone(s).",
            impact=_impact(ob_signal),
            weight=options.order_block_weight,
            contribution=_q(ob_signal * options.order_block_weight),
        ),
        SmcEvidenceV1(
            code="FAIR_VALUE_GAP_CONTEXT",
            message=f"Active fair-value-gap bias from {len([z for z in zones if 'FVG' in z.zone_type and not z.is_mitigated])} zone(s).",
            impact=_impact(fvg_signal),
            weight=options.fair_value_gap_weight,
            contribution=_q(fvg_signal * options.fair_value_gap_weight),
        ),
    ]


def _direction(score: Decimal, threshold: Decimal) -> str:
    if score >= threshold:
        return "LONG"
    if score <= -threshold:
        return "SHORT"
    return "NEUTRAL"


def _impact(value: Decimal) -> str:
    if value > ZERO:
        return "SUPPORTS_LONG"
    if value < ZERO:
        return "SUPPORTS_SHORT"
    return "NEUTRAL"


def _clip(value: Decimal, minimum: Decimal, maximum: Decimal) -> Decimal:
    return min(maximum, max(minimum, value))


def _q(value: Decimal) -> Decimal:
    return value.quantize(Q)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
