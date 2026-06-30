from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import NAMESPACE_URL, uuid5

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smart_money import (
    SmartMoneyConceptsOutputV1,
    SmartMoneyEvidenceV1,
    SmartMoneyLiquiditySweepV1,
    SmartMoneyPriceZoneV1,
    SmartMoneyStructureEventV1,
    SmartMoneySwingPointV1,
)
from app.smart_money.definitions import SmartMoneyOptions
from app.smart_money.models import SmartMoneyCandle

ZERO = Decimal("0")
ONE = Decimal("1")
QUANTUM = Decimal("0.000001")


@dataclass(frozen=True, slots=True)
class _Swing:
    index: int
    confirmed_index: int
    value: SmartMoneySwingPointV1


@dataclass(frozen=True, slots=True)
class _StructureEvent:
    index: int
    value: SmartMoneyStructureEventV1


@dataclass(frozen=True, slots=True)
class _Sweep:
    index: int
    value: SmartMoneyLiquiditySweepV1


@dataclass(frozen=True, slots=True)
class _Zone:
    index: int
    value: SmartMoneyPriceZoneV1


class DeterministicSmartMoneyCalculator:
    def __init__(self, options: SmartMoneyOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> SmartMoneyOptions:
        return self._options

    def calculate(
        self,
        delivery: MarketCandleDeliveryV1,
        window: list[SmartMoneyCandle],
        generated_at_utc: datetime,
        revision: int,
    ) -> SmartMoneyConceptsOutputV1:
        payload = delivery.envelope.payload
        if payload.timeframe != "5m":
            raise ValueError("Smart Money Concepts V1 only supports the 5m timeframe")
        if not payload.is_closed or payload.is_provisional:
            raise ValueError("Smart Money Concepts requires a closed candle")

        ordered = _deduplicate_window(window)
        valid = [item for item in ordered if _is_valid_candle(item)]
        warnings = ["SMC_RULESET_IS_DETERMINISTIC_HEURISTIC"]
        input_count = len(ordered)
        valid_input_ratio = (
            Decimal(len(valid)) / Decimal(input_count) if input_count else ZERO
        )
        completeness = min(
            ONE,
            Decimal(len(valid)) / Decimal(self._options.required_input_count),
        )
        if input_count < self._options.required_input_count:
            warnings.append("INSUFFICIENT_CANDLE_WINDOW")
        if valid_input_ratio < self._options.minimum_valid_input_ratio:
            warnings.append("LOW_VALID_CANDLE_RATIO")
        if _has_intraday_gap(valid):
            warnings.append("NON_CONTIGUOUS_INTRADAY_WINDOW")

        source_in_window = any(
            item.open_at_utc == _as_utc(payload.open_at_utc)
            and item.close_at_utc == _as_utc(payload.close_at_utc)
            and item.revision == payload.revision
            for item in ordered
        )
        if not source_in_window:
            warnings.append("SOURCE_CANDLE_NOT_IN_POINT_IN_TIME_WINDOW")

        swings = _find_swings(valid, self._options)
        swing_highs = [item for item in swings if item.value.kind == "HIGH"]
        swing_lows = [item for item in swings if item.value.kind == "LOW"]
        if not swing_highs:
            warnings.append("MISSING_CONFIRMED_SWING_HIGH")
        if not swing_lows:
            warnings.append("MISSING_CONFIRMED_SWING_LOW")

        structure_events, sweeps = _find_structure_activity(
            valid,
            swing_highs,
            swing_lows,
            self._options,
        )
        order_blocks = _build_order_blocks(valid, structure_events, self._options)
        fair_value_gaps = _build_fair_value_gaps(valid, self._options)

        last_index = len(valid) - 1
        latest_swing_high = _latest_confirmed_swing(swing_highs, last_index)
        latest_swing_low = _latest_confirmed_swing(swing_lows, last_index)
        structure_state = _structure_state_before(
            swing_highs,
            swing_lows,
            len(valid) + 1,
        )
        if structure_events:
            structure_state = (
                "BULLISH"
                if structure_events[-1].value.direction == "LONG"
                else "BEARISH"
            )

        recent_event = _latest_recent(structure_events, last_index, 6)
        recent_sweep = _latest_recent(sweeps, last_index, 3)
        recent_order_block = _latest_usable_zone(
            order_blocks,
            last_index,
            self._options.maximum_zone_age_bars,
        )
        recent_fair_value_gap = _latest_usable_zone(
            fair_value_gaps,
            last_index,
            self._options.maximum_zone_age_bars,
        )
        if recent_event is None and recent_sweep is None:
            warnings.append("NO_RECENT_STRUCTURE_TRIGGER")
        if recent_order_block is None:
            warnings.append("NO_ACTIVE_ORDER_BLOCK")
        if recent_fair_value_gap is None:
            warnings.append("NO_ACTIVE_FAIR_VALUE_GAP")

        components = _score_components(
            structure_state,
            recent_event,
            recent_sweep,
            recent_order_block,
            recent_fair_value_gap,
            self._options,
        )
        score = _quantize(_clip(sum((item[2] for item in components), ZERO), -ONE, ONE))
        direction = _direction(score, self._options.directional_threshold)
        pivot_coverage = _pivot_coverage(swing_highs, swing_lows)
        agreement = _agreement([item[2] for item in components], score)
        recency = _activity_recency(recent_event, recent_sweep, last_index)
        confidence = _quantize(
            _clip(
                completeness * Decimal("0.20")
                + valid_input_ratio * Decimal("0.20")
                + pivot_coverage * Decimal("0.15")
                + abs(score) * Decimal("0.20")
                + agreement * Decimal("0.15")
                + recency * Decimal("0.10"),
                ZERO,
                ONE,
            )
        )
        if structure_state in {"RANGE", "UNCONFIRMED"}:
            confidence = _quantize(confidence * Decimal("0.80"))

        generated_at = _as_utc(generated_at_utc)
        as_of = _as_utc(payload.close_at_utc)
        age = generated_at - as_of
        is_stale = (
            age < timedelta(0)
            or age.total_seconds() > self._options.maximum_output_age_seconds
        )
        if is_stale:
            warnings.append("SMART_MONEY_OUTPUT_STALE")

        minimum_calculation_count = (
            self._options.swing_left_bars + self._options.swing_right_bars + 3
        )
        if len(valid) < minimum_calculation_count or not source_in_window:
            data_quality_status = "INVALID"
        elif (
            len(valid) < self._options.required_input_count
            or valid_input_ratio < self._options.minimum_valid_input_ratio
            or "NON_CONTIGUOUS_INTRADAY_WINDOW" in warnings
        ):
            data_quality_status = "DEGRADED"
        else:
            data_quality_status = "VALID"

        is_eligible = (
            data_quality_status == "VALID"
            and not is_stale
            and direction != "NEUTRAL"
            and confidence >= self._options.fusion_confidence_threshold
            and latest_swing_high is not None
            and latest_swing_low is not None
            and (recent_event is not None or recent_sweep is not None)
        )
        evidence = [
            SmartMoneyEvidenceV1(
                code=code,
                message=message,
                impact=_impact(contribution),
                weight=weight,
                contribution=_quantize(contribution),
            )
            for code, message, contribution, weight in components
        ]

        output_uid = uuid5(
            NAMESPACE_URL,
            "|".join(
                [
                    "smart-money-output-v1",
                    str(delivery.envelope.metadata.message_id),
                    self._options.policy_version,
                    str(revision),
                ]
            ),
        )
        message_uid = uuid5(
            NAMESPACE_URL,
            f"smart-money-message-v1|{output_uid}",
        )
        return SmartMoneyConceptsOutputV1(
            output_uid=output_uid,
            message_uid=message_uid,
            source_candle_message_uid=delivery.envelope.metadata.message_id,
            instrument_key=payload.instrument_key,
            timeframe="5m",
            as_of_utc=as_of,
            generated_at_utc=generated_at,
            engine_code=self._options.engine_code,
            engine_version=self._options.engine_version,
            policy_version=self._options.policy_version,
            structure_state=structure_state,
            direction=direction,
            score=score,
            confidence=confidence,
            latest_swing_high=(
                None if latest_swing_high is None else latest_swing_high.value
            ),
            latest_swing_low=(
                None if latest_swing_low is None else latest_swing_low.value
            ),
            structure_events=[item.value for item in structure_events[-10:]],
            liquidity_sweeps=[item.value for item in sweeps[-10:]],
            order_blocks=[item.value for item in order_blocks[-self._options.maximum_zones_per_type :]],
            fair_value_gaps=[
                item.value
                for item in fair_value_gaps[-self._options.maximum_zones_per_type :]
            ],
            input_count=input_count,
            required_input_count=self._options.required_input_count,
            completeness=_quantize(completeness),
            valid_input_ratio=_quantize(valid_input_ratio),
            data_quality_status=data_quality_status,
            is_stale=is_stale,
            is_eligible_for_fusion=is_eligible,
            revision=revision,
            evidence=evidence,
            warnings=sorted(set(warnings)),
        )


def _deduplicate_window(window: list[SmartMoneyCandle]) -> list[SmartMoneyCandle]:
    by_open: dict[datetime, SmartMoneyCandle] = {}
    for item in sorted(
        window,
        key=lambda candle: (
            candle.open_at_utc,
            candle.revision,
            candle.received_at_utc,
        ),
    ):
        existing = by_open.get(item.open_at_utc)
        if existing is None or (
            item.revision,
            item.received_at_utc,
        ) > (
            existing.revision,
            existing.received_at_utc,
        ):
            by_open[item.open_at_utc] = item
    return sorted(by_open.values(), key=lambda candle: candle.open_at_utc)


def _is_valid_candle(item: SmartMoneyCandle) -> bool:
    return (
        item.quality_status == "VALID"
        and item.is_usable_for_new_exposure
        and item.open_price > ZERO
        and item.high_price >= max(item.open_price, item.close_price)
        and item.low_price <= min(item.open_price, item.close_price)
        and item.high_price >= item.low_price
    )


def _has_intraday_gap(candles: list[SmartMoneyCandle]) -> bool:
    for previous, current in zip(candles, candles[1:], strict=False):
        if (
            previous.close_at_utc.date() == current.open_at_utc.date()
            and current.open_at_utc != previous.close_at_utc
        ):
            return True
    return False


def _find_swings(
    candles: list[SmartMoneyCandle],
    options: SmartMoneyOptions,
) -> list[_Swing]:
    swings: list[_Swing] = []
    left = options.swing_left_bars
    right = options.swing_right_bars
    for index in range(left, len(candles) - right):
        candidate = candles[index]
        neighbours = candles[index - left : index] + candles[index + 1 : index + right + 1]
        confirmed_at = candles[index + right].close_at_utc
        if all(candidate.high_price > item.high_price for item in neighbours):
            swings.append(
                _Swing(
                    index=index,
                    confirmed_index=index + right,
                    value=SmartMoneySwingPointV1(
                        kind="HIGH",
                        candle_open_at_utc=candidate.open_at_utc,
                        candle_close_at_utc=candidate.close_at_utc,
                        confirmed_at_utc=confirmed_at,
                        price=candidate.high_price,
                        candle_revision=candidate.revision,
                    ),
                )
            )
        if all(candidate.low_price < item.low_price for item in neighbours):
            swings.append(
                _Swing(
                    index=index,
                    confirmed_index=index + right,
                    value=SmartMoneySwingPointV1(
                        kind="LOW",
                        candle_open_at_utc=candidate.open_at_utc,
                        candle_close_at_utc=candidate.close_at_utc,
                        confirmed_at_utc=confirmed_at,
                        price=candidate.low_price,
                        candle_revision=candidate.revision,
                    ),
                )
            )
    return sorted(swings, key=lambda item: (item.index, item.value.kind))


def _find_structure_activity(
    candles: list[SmartMoneyCandle],
    swing_highs: list[_Swing],
    swing_lows: list[_Swing],
    options: SmartMoneyOptions,
) -> tuple[list[_StructureEvent], list[_Sweep]]:
    events: list[_StructureEvent] = []
    sweeps: list[_Sweep] = []
    broken_highs: set[int] = set()
    broken_lows: set[int] = set()
    swept_highs: set[int] = set()
    swept_lows: set[int] = set()
    tolerance = options.break_tolerance_fraction

    for index, candle in enumerate(candles):
        available_highs = [item for item in swing_highs if item.confirmed_index < index]
        available_lows = [item for item in swing_lows if item.confirmed_index < index]
        if not available_highs or not available_lows:
            continue
        latest_high = available_highs[-1]
        latest_low = available_lows[-1]
        prior_structure = _structure_state_before(
            swing_highs,
            swing_lows,
            index,
        )
        high_level = latest_high.value.price
        low_level = latest_low.value.price
        bullish_break = (
            latest_high.index not in broken_highs
            and candle.close_price > high_level * (ONE + tolerance)
        )
        bearish_break = (
            latest_low.index not in broken_lows
            and candle.close_price < low_level * (ONE - tolerance)
        )

        if bullish_break:
            broken_highs.add(latest_high.index)
            event_type = "CHOCH" if prior_structure == "BEARISH" else "BOS"
            events.append(
                _StructureEvent(
                    index=index,
                    value=SmartMoneyStructureEventV1(
                        event_type=event_type,
                        direction="LONG",
                        event_at_utc=candle.close_at_utc,
                        broken_level=high_level,
                        close_price=candle.close_price,
                        prior_structure=prior_structure,
                        displacement_fraction=(candle.close_price - high_level) / high_level,
                    ),
                )
            )
        elif (
            latest_high.index not in swept_highs
            and candle.high_price > high_level * (ONE + tolerance)
            and candle.close_price <= high_level
        ):
            swept_highs.add(latest_high.index)
            sweeps.append(
                _Sweep(
                    index=index,
                    value=SmartMoneyLiquiditySweepV1(
                        sweep_type="BUY_SIDE_SWEEP",
                        direction="SHORT",
                        event_at_utc=candle.close_at_utc,
                        swept_level=high_level,
                        wick_extreme=candle.high_price,
                        close_price=candle.close_price,
                        rejection_fraction=(candle.high_price - high_level) / high_level,
                    ),
                )
            )

        if bearish_break:
            broken_lows.add(latest_low.index)
            event_type = "CHOCH" if prior_structure == "BULLISH" else "BOS"
            events.append(
                _StructureEvent(
                    index=index,
                    value=SmartMoneyStructureEventV1(
                        event_type=event_type,
                        direction="SHORT",
                        event_at_utc=candle.close_at_utc,
                        broken_level=low_level,
                        close_price=candle.close_price,
                        prior_structure=prior_structure,
                        displacement_fraction=(low_level - candle.close_price) / low_level,
                    ),
                )
            )
        elif (
            latest_low.index not in swept_lows
            and candle.low_price < low_level * (ONE - tolerance)
            and candle.close_price >= low_level
        ):
            swept_lows.add(latest_low.index)
            sweeps.append(
                _Sweep(
                    index=index,
                    value=SmartMoneyLiquiditySweepV1(
                        sweep_type="SELL_SIDE_SWEEP",
                        direction="LONG",
                        event_at_utc=candle.close_at_utc,
                        swept_level=low_level,
                        wick_extreme=candle.low_price,
                        close_price=candle.close_price,
                        rejection_fraction=(low_level - candle.low_price) / low_level,
                    ),
                )
            )
    return events, sweeps


def _build_order_blocks(
    candles: list[SmartMoneyCandle],
    events: list[_StructureEvent],
    options: SmartMoneyOptions,
) -> list[_Zone]:
    result: list[_Zone] = []
    for event in events:
        start = max(0, event.index - options.order_block_search_bars)
        candidate: SmartMoneyCandle | None = None
        if event.value.direction == "LONG":
            for index in range(event.index - 1, start - 1, -1):
                if candles[index].close_price < candles[index].open_price:
                    candidate = candles[index]
                    break
            if candidate is None:
                continue
            lower = candidate.low_price
            upper = candidate.open_price
        else:
            for index in range(event.index - 1, start - 1, -1):
                if candles[index].close_price > candles[index].open_price:
                    candidate = candles[index]
                    break
            if candidate is None:
                continue
            lower = candidate.open_price
            upper = candidate.high_price
        if upper <= lower:
            continue
        status, invalidated_at = _zone_status(
            event.value.direction,
            lower,
            upper,
            candles[event.index + 1 :],
        )
        result.append(
            _Zone(
                index=event.index,
                value=SmartMoneyPriceZoneV1(
                    zone_type="ORDER_BLOCK",
                    direction=event.value.direction,
                    formed_at_utc=event.value.event_at_utc,
                    source_open_at_utc=candidate.open_at_utc,
                    lower_price=lower,
                    upper_price=upper,
                    status=status,
                    invalidated_at_utc=invalidated_at,
                    origin_event_type=event.value.event_type,
                ),
            )
        )
    return result


def _build_fair_value_gaps(
    candles: list[SmartMoneyCandle],
    options: SmartMoneyOptions,
) -> list[_Zone]:
    result: list[_Zone] = []
    minimum = options.minimum_fair_value_gap_fraction
    for index in range(2, len(candles)):
        left = candles[index - 2]
        current = candles[index]
        if current.low_price > left.high_price:
            gap_fraction = (current.low_price - left.high_price) / left.high_price
            if gap_fraction >= minimum:
                status, invalidated_at = _zone_status(
                    "LONG",
                    left.high_price,
                    current.low_price,
                    candles[index + 1 :],
                )
                result.append(
                    _Zone(
                        index=index,
                        value=SmartMoneyPriceZoneV1(
                            zone_type="FAIR_VALUE_GAP",
                            direction="LONG",
                            formed_at_utc=current.close_at_utc,
                            source_open_at_utc=current.open_at_utc,
                            lower_price=left.high_price,
                            upper_price=current.low_price,
                            status=status,
                            invalidated_at_utc=invalidated_at,
                            origin_event_type="IMBALANCE",
                        ),
                    )
                )
        if current.high_price < left.low_price:
            gap_fraction = (left.low_price - current.high_price) / left.low_price
            if gap_fraction >= minimum:
                status, invalidated_at = _zone_status(
                    "SHORT",
                    current.high_price,
                    left.low_price,
                    candles[index + 1 :],
                )
                result.append(
                    _Zone(
                        index=index,
                        value=SmartMoneyPriceZoneV1(
                            zone_type="FAIR_VALUE_GAP",
                            direction="SHORT",
                            formed_at_utc=current.close_at_utc,
                            source_open_at_utc=current.open_at_utc,
                            lower_price=current.high_price,
                            upper_price=left.low_price,
                            status=status,
                            invalidated_at_utc=invalidated_at,
                            origin_event_type="IMBALANCE",
                        ),
                    )
                )
    return result


def _zone_status(
    direction: str,
    lower: Decimal,
    upper: Decimal,
    later: list[SmartMoneyCandle],
) -> tuple[str, datetime | None]:
    mitigated = False
    for candle in later:
        if direction == "LONG":
            if candle.close_price < lower:
                return "INVALIDATED", candle.close_at_utc
            if candle.low_price <= upper:
                mitigated = True
        else:
            if candle.close_price > upper:
                return "INVALIDATED", candle.close_at_utc
            if candle.high_price >= lower:
                mitigated = True
    return ("MITIGATED" if mitigated else "ACTIVE"), None


def _structure_state_before(
    swing_highs: list[_Swing],
    swing_lows: list[_Swing],
    before_index: int,
) -> str:
    highs = [item for item in swing_highs if item.confirmed_index < before_index]
    lows = [item for item in swing_lows if item.confirmed_index < before_index]
    if len(highs) < 2 or len(lows) < 2:
        return "UNCONFIRMED"
    higher_high = highs[-1].value.price > highs[-2].value.price
    higher_low = lows[-1].value.price > lows[-2].value.price
    lower_high = highs[-1].value.price < highs[-2].value.price
    lower_low = lows[-1].value.price < lows[-2].value.price
    if higher_high and higher_low:
        return "BULLISH"
    if lower_high and lower_low:
        return "BEARISH"
    return "RANGE"


def _latest_confirmed_swing(
    swings: list[_Swing],
    last_index: int,
) -> _Swing | None:
    available = [item for item in swings if item.confirmed_index <= last_index]
    return None if not available else available[-1]


def _latest_recent(items, last_index: int, maximum_age: int):
    if not items:
        return None
    latest = items[-1]
    return latest if last_index - latest.index <= maximum_age else None


def _latest_usable_zone(
    zones: list[_Zone],
    last_index: int,
    maximum_age: int,
) -> _Zone | None:
    usable = [
        item
        for item in zones
        if item.value.status != "INVALIDATED"
        and last_index - item.index <= maximum_age
    ]
    return None if not usable else usable[-1]


def _score_components(
    structure_state: str,
    event: _StructureEvent | None,
    sweep: _Sweep | None,
    order_block: _Zone | None,
    fair_value_gap: _Zone | None,
    options: SmartMoneyOptions,
) -> list[tuple[str, str, Decimal, Decimal]]:
    state_sign = _sign_from_state(structure_state)
    components: list[tuple[str, str, Decimal, Decimal]] = [
        (
            "MARKET_STRUCTURE_STATE",
            f"Confirmed swing structure is {structure_state}.",
            state_sign * options.structure_state_weight,
            options.structure_state_weight,
        )
    ]
    event_sign = ZERO
    event_message = "No recent close-confirmed structure break."
    if event is not None:
        multiplier = ONE if event.value.event_type == "CHOCH" else Decimal("0.80")
        event_sign = _sign(event.value.direction) * multiplier
        event_message = (
            f"{event.value.event_type} {event.value.direction} closed through "
            f"{event.value.broken_level}."
        )
    components.append(
        (
            "STRUCTURE_BREAK",
            event_message,
            event_sign * options.structure_event_weight,
            options.structure_event_weight,
        )
    )
    sweep_sign = ZERO
    sweep_message = "No recent liquidity sweep."
    if sweep is not None:
        sweep_sign = _sign(sweep.value.direction) * Decimal("0.80")
        sweep_message = (
            f"{sweep.value.sweep_type} rejected level {sweep.value.swept_level}."
        )
    components.append(
        (
            "LIQUIDITY_SWEEP",
            sweep_message,
            sweep_sign * options.liquidity_sweep_weight,
            options.liquidity_sweep_weight,
        )
    )
    order_block_sign = ZERO
    order_block_message = "No active or mitigated order block."
    if order_block is not None:
        factor = ONE if order_block.value.status == "ACTIVE" else Decimal("0.50")
        order_block_sign = _sign(order_block.value.direction) * factor
        order_block_message = (
            f"{order_block.value.status} {order_block.value.direction} order block "
            f"spans {order_block.value.lower_price} to {order_block.value.upper_price}."
        )
    components.append(
        (
            "ORDER_BLOCK_CONTEXT",
            order_block_message,
            order_block_sign * options.order_block_weight,
            options.order_block_weight,
        )
    )
    gap_sign = ZERO
    gap_message = "No active or mitigated fair-value gap."
    if fair_value_gap is not None:
        factor = ONE if fair_value_gap.value.status == "ACTIVE" else Decimal("0.50")
        gap_sign = _sign(fair_value_gap.value.direction) * factor
        gap_message = (
            f"{fair_value_gap.value.status} {fair_value_gap.value.direction} fair-value "
            f"gap spans {fair_value_gap.value.lower_price} to "
            f"{fair_value_gap.value.upper_price}."
        )
    components.append(
        (
            "FAIR_VALUE_GAP_CONTEXT",
            gap_message,
            gap_sign * options.fair_value_gap_weight,
            options.fair_value_gap_weight,
        )
    )
    return components


def _pivot_coverage(highs: list[_Swing], lows: list[_Swing]) -> Decimal:
    if len(highs) >= 2 and len(lows) >= 2:
        return ONE
    if highs and lows:
        return Decimal("0.75")
    if highs or lows:
        return Decimal("0.40")
    return ZERO


def _agreement(values: list[Decimal], score: Decimal) -> Decimal:
    non_zero = [value for value in values if value != ZERO]
    if not non_zero or score == ZERO:
        return ZERO
    direction = ONE if score > ZERO else -ONE
    agreeing = sum(1 for value in non_zero if value * direction > ZERO)
    return Decimal(agreeing) / Decimal(len(non_zero))


def _activity_recency(
    event: _StructureEvent | None,
    sweep: _Sweep | None,
    last_index: int,
) -> Decimal:
    indexes = [item.index for item in (event, sweep) if item is not None]
    if not indexes:
        return Decimal("0.20")
    age = last_index - max(indexes)
    if age <= 1:
        return ONE
    if age <= 3:
        return Decimal("0.75")
    return Decimal("0.50")


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


def _sign(direction: str) -> Decimal:
    return ONE if direction == "LONG" else -ONE


def _sign_from_state(state: str) -> Decimal:
    if state == "BULLISH":
        return ONE
    if state == "BEARISH":
        return -ONE
    return ZERO


def _clip(value: Decimal, minimum: Decimal, maximum: Decimal) -> Decimal:
    return min(maximum, max(minimum, value))


def _quantize(value: Decimal) -> Decimal:
    return value.quantize(QUANTUM)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
