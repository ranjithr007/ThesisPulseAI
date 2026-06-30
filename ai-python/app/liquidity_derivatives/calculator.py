from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import NAMESPACE_URL, uuid5

from app.contracts.v1.liquidity_derivatives import (
    LiquidityDerivativesContextOutputV1,
    LiquidityDerivativesEvidenceV1,
    LiquidityPoolV1,
)
from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.liquidity_derivatives.definitions import LiquidityDerivativesOptions
from app.liquidity_derivatives.models import LiquidityDerivativesCandle

ZERO = Decimal("0")
ONE = Decimal("1")
TWO = Decimal("2")
QUANTUM = Decimal("0.000001")


@dataclass(frozen=True, slots=True)
class _Pivot:
    index: int
    kind: str
    price: Decimal
    confirmed_index: int
    formed_at_utc: datetime


@dataclass(frozen=True, slots=True)
class _PoolCandidate:
    kind: str
    source_type: str
    indexes: tuple[int, ...]
    prices: tuple[Decimal, ...]
    formed_at_utc: datetime
    last_touched_at_utc: datetime


class DeterministicLiquidityDerivativesCalculator:
    def __init__(self, options: LiquidityDerivativesOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> LiquidityDerivativesOptions:
        return self._options

    def calculate(
        self,
        delivery: MarketCandleDeliveryV1,
        window: list[LiquidityDerivativesCandle],
        generated_at_utc: datetime,
        revision: int,
    ) -> LiquidityDerivativesContextOutputV1:
        payload = delivery.envelope.payload
        if payload.timeframe != "5m":
            raise ValueError("Liquidity Context V1 only supports the 5m timeframe")
        if not payload.is_closed or payload.is_provisional:
            raise ValueError("Liquidity Context requires a closed candle")

        ordered = _deduplicate_window(window)
        valid = [item for item in ordered if _is_valid_candle(item)]
        warnings = [
            "LIQUIDITY_MAP_IS_PRICE_STRUCTURE_HEURISTIC",
            "OPTION_CHAIN_CONTEXT_UNAVAILABLE_V1",
            "FUTURES_BASIS_UNAVAILABLE_V1",
        ]
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

        current_price = payload.close_price
        if valid:
            range_low = min(item.low_price for item in valid)
            range_high = max(item.high_price for item in valid)
        else:
            range_low = payload.low_price
            range_high = payload.high_price

        pivots = _find_pivots(valid, self._options)
        candidates = _cluster_pivots(pivots, self._options)
        candidates.extend(_session_extremes(valid))
        pools = _materialize_pools(
            candidates,
            valid,
            current_price,
            self._options,
        )
        buy_side = sorted(
            [
                pool
                for pool in pools
                if pool.side == "BUY_SIDE" and pool.center_price >= current_price
            ],
            key=lambda item: (item.distance_fraction, -item.strength),
        )
        sell_side = sorted(
            [
                pool
                for pool in pools
                if pool.side == "SELL_SIDE" and pool.center_price <= current_price
            ],
            key=lambda item: (item.distance_fraction, -item.strength),
        )
        nearest_buy = buy_side[0] if buy_side else None
        nearest_sell = sell_side[0] if sell_side else None
        if nearest_buy is None:
            warnings.append("NO_ACTIVE_BUY_SIDE_LIQUIDITY_POOL")
        if nearest_sell is None:
            warnings.append("NO_ACTIVE_SELL_SIDE_LIQUIDITY_POOL")

        liquidity_score = _liquidity_attraction_score(nearest_buy, nearest_sell)
        location_score = _range_location_score(current_price, range_low, range_high)
        (
            derivatives_state,
            derivatives_score,
            price_change,
            oi_start,
            oi_end,
            oi_change,
        ) = _derivatives_context(valid, self._options)
        if derivatives_state == "NOT_AVAILABLE":
            warnings.append("OPEN_INTEREST_CONTEXT_UNAVAILABLE")
        elif derivatives_state == "FLAT":
            warnings.append("OPEN_INTEREST_CONTEXT_FLAT")

        components = [
            (
                "LIQUIDITY_ATTRACTION",
                _liquidity_message(nearest_buy, nearest_sell, liquidity_score),
                liquidity_score * self._options.liquidity_attraction_weight,
                self._options.liquidity_attraction_weight,
            ),
            (
                "RANGE_LOCATION",
                (
                    f"Current price {current_price} is positioned within the "
                    f"observed range {range_low} to {range_high}."
                ),
                location_score * self._options.range_location_weight,
                self._options.range_location_weight,
            ),
            (
                "OPEN_INTEREST_CONTEXT",
                _derivatives_message(derivatives_state, price_change, oi_change),
                derivatives_score * self._options.derivatives_weight,
                self._options.derivatives_weight,
            ),
        ]
        score = _quantize(
            _clip(sum((item[2] for item in components), ZERO), -ONE, ONE)
        )
        direction = _direction(score, self._options.directional_threshold)
        pool_coverage = _pool_coverage(nearest_buy, nearest_sell)
        agreement = _agreement([item[2] for item in components], score)
        oi_coverage = ONE if oi_change is not None else Decimal("0.35")
        confidence = _quantize(
            _clip(
                completeness * Decimal("0.20")
                + valid_input_ratio * Decimal("0.20")
                + pool_coverage * Decimal("0.20")
                + oi_coverage * Decimal("0.15")
                + agreement * Decimal("0.15")
                + abs(score) * Decimal("0.10"),
                ZERO,
                ONE,
            )
        )

        generated_at = _as_utc(generated_at_utc)
        as_of = _as_utc(payload.close_at_utc)
        age = generated_at - as_of
        is_stale = (
            age < timedelta(0)
            or age.total_seconds() > self._options.maximum_output_age_seconds
        )
        if is_stale:
            warnings.append("LIQUIDITY_DERIVATIVES_OUTPUT_STALE")

        minimum_count = (
            self._options.swing_left_bars + self._options.swing_right_bars + 3
        )
        if len(valid) < minimum_count or not source_in_window:
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
            and (nearest_buy is not None or nearest_sell is not None)
        )
        evidence = [
            LiquidityDerivativesEvidenceV1(
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
                    "liquidity-derivatives-output-v1",
                    str(delivery.envelope.metadata.message_id),
                    self._options.policy_version,
                    str(revision),
                ]
            ),
        )
        message_uid = uuid5(
            NAMESPACE_URL,
            f"liquidity-derivatives-message-v1|{output_uid}",
        )
        return LiquidityDerivativesContextOutputV1(
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
            direction=direction,
            score=score,
            confidence=confidence,
            current_price=current_price,
            range_low=range_low,
            range_high=range_high,
            liquidity_attraction_score=_quantize(liquidity_score),
            range_location_score=_quantize(location_score),
            derivatives_score=_quantize(derivatives_score),
            derivatives_state=derivatives_state,
            price_change_fraction=_quantize(price_change),
            open_interest_start=oi_start,
            open_interest_end=oi_end,
            open_interest_change_fraction=(
                None if oi_change is None else _quantize(oi_change)
            ),
            nearest_buy_side_pool=nearest_buy,
            nearest_sell_side_pool=nearest_sell,
            liquidity_pools=pools,
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


def _deduplicate_window(
    window: list[LiquidityDerivativesCandle],
) -> list[LiquidityDerivativesCandle]:
    by_open: dict[datetime, LiquidityDerivativesCandle] = {}
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


def _is_valid_candle(item: LiquidityDerivativesCandle) -> bool:
    return (
        item.quality_status == "VALID"
        and item.is_usable_for_new_exposure
        and item.open_price > ZERO
        and item.high_price >= max(item.open_price, item.close_price)
        and item.low_price <= min(item.open_price, item.close_price)
        and item.high_price >= item.low_price
    )


def _has_intraday_gap(candles: list[LiquidityDerivativesCandle]) -> bool:
    for previous, current in zip(candles, candles[1:], strict=False):
        if (
            previous.close_at_utc.date() == current.open_at_utc.date()
            and current.open_at_utc != previous.close_at_utc
        ):
            return True
    return False


def _find_pivots(
    candles: list[LiquidityDerivativesCandle],
    options: LiquidityDerivativesOptions,
) -> list[_Pivot]:
    result: list[_Pivot] = []
    left = options.swing_left_bars
    right = options.swing_right_bars
    for index in range(left, len(candles) - right):
        candidate = candles[index]
        neighbours = (
            candles[index - left : index]
            + candles[index + 1 : index + right + 1]
        )
        confirmed_index = index + right
        if all(candidate.high_price > item.high_price for item in neighbours):
            result.append(
                _Pivot(
                    index=index,
                    kind="HIGH",
                    price=candidate.high_price,
                    confirmed_index=confirmed_index,
                    formed_at_utc=candidate.close_at_utc,
                )
            )
        if all(candidate.low_price < item.low_price for item in neighbours):
            result.append(
                _Pivot(
                    index=index,
                    kind="LOW",
                    price=candidate.low_price,
                    confirmed_index=confirmed_index,
                    formed_at_utc=candidate.close_at_utc,
                )
            )
    return result


def _cluster_pivots(
    pivots: list[_Pivot],
    options: LiquidityDerivativesOptions,
) -> list[_PoolCandidate]:
    result: list[_PoolCandidate] = []
    for kind in ("HIGH", "LOW"):
        typed = sorted(
            [item for item in pivots if item.kind == kind],
            key=lambda item: item.price,
        )
        clusters: list[list[_Pivot]] = []
        for pivot in typed:
            matched = None
            for cluster in clusters:
                center = sum((item.price for item in cluster), ZERO) / Decimal(
                    len(cluster)
                )
                if abs(pivot.price - center) / center <= (
                    options.pool_cluster_tolerance_fraction
                ):
                    matched = cluster
                    break
            if matched is None:
                clusters.append([pivot])
            else:
                matched.append(pivot)
        for cluster in clusters:
            if len(cluster) < 2:
                continue
            result.append(
                _PoolCandidate(
                    kind=kind,
                    source_type="SWING_CLUSTER",
                    indexes=tuple(item.index for item in cluster),
                    prices=tuple(item.price for item in cluster),
                    formed_at_utc=min(item.formed_at_utc for item in cluster),
                    last_touched_at_utc=max(
                        item.formed_at_utc for item in cluster
                    ),
                )
            )
    return result


def _session_extremes(
    candles: list[LiquidityDerivativesCandle],
) -> list[_PoolCandidate]:
    if not candles:
        return []
    highest_index = max(range(len(candles)), key=lambda index: candles[index].high_price)
    lowest_index = min(range(len(candles)), key=lambda index: candles[index].low_price)
    highest = candles[highest_index]
    lowest = candles[lowest_index]
    return [
        _PoolCandidate(
            kind="HIGH",
            source_type="SESSION_EXTREME",
            indexes=(highest_index,),
            prices=(highest.high_price,),
            formed_at_utc=highest.close_at_utc,
            last_touched_at_utc=highest.close_at_utc,
        ),
        _PoolCandidate(
            kind="LOW",
            source_type="SESSION_EXTREME",
            indexes=(lowest_index,),
            prices=(lowest.low_price,),
            formed_at_utc=lowest.close_at_utc,
            last_touched_at_utc=lowest.close_at_utc,
        ),
    ]


def _materialize_pools(
    candidates: list[_PoolCandidate],
    candles: list[LiquidityDerivativesCandle],
    current_price: Decimal,
    options: LiquidityDerivativesOptions,
) -> list[LiquidityPoolV1]:
    pools: list[LiquidityPoolV1] = []
    for candidate in candidates:
        center = sum(candidate.prices, ZERO) / Decimal(len(candidate.prices))
        half_width = center * options.pool_half_width_fraction
        lower = center - half_width
        upper = center + half_width
        after_index = max(candidate.indexes) + 1
        status, status_at = _pool_status(
            candidate.kind,
            lower,
            upper,
            candles[after_index:],
            candidate.last_touched_at_utc,
        )
        distance = abs(center - current_price) / current_price
        touch_count = len(candidate.prices)
        source_bonus = (
            Decimal("0.20")
            if candidate.source_type == "SWING_CLUSTER"
            else ZERO
        )
        strength = _clip(
            Decimal("0.25")
            + Decimal(touch_count) * Decimal("0.15")
            + source_bonus,
            ZERO,
            ONE,
        )
        pools.append(
            LiquidityPoolV1(
                side="BUY_SIDE" if candidate.kind == "HIGH" else "SELL_SIDE",
                role="RESISTANCE" if candidate.kind == "HIGH" else "SUPPORT",
                source_type=candidate.source_type,
                formed_at_utc=candidate.formed_at_utc,
                last_touched_at_utc=candidate.last_touched_at_utc,
                lower_price=_quantize(lower),
                center_price=_quantize(center),
                upper_price=_quantize(upper),
                touch_count=touch_count,
                strength=_quantize(strength),
                distance_fraction=_quantize(distance),
                status=status,
                status_at_utc=status_at,
            )
        )
    active = [pool for pool in pools if pool.status != "BROKEN"]
    buy = sorted(
        [pool for pool in active if pool.side == "BUY_SIDE"],
        key=lambda item: (item.distance_fraction, -item.strength),
    )[: options.maximum_pools_per_side]
    sell = sorted(
        [pool for pool in active if pool.side == "SELL_SIDE"],
        key=lambda item: (item.distance_fraction, -item.strength),
    )[: options.maximum_pools_per_side]
    return sorted(
        buy + sell,
        key=lambda item: (item.center_price, item.side),
    )


def _pool_status(
    kind: str,
    lower: Decimal,
    upper: Decimal,
    later: list[LiquidityDerivativesCandle],
    default_time: datetime,
) -> tuple[str, datetime]:
    status = "ACTIVE"
    status_at = default_time
    for candle in later:
        if kind == "HIGH":
            if candle.close_price > upper:
                return "BROKEN", candle.close_at_utc
            if candle.high_price > upper and candle.close_price <= upper:
                status = "SWEPT"
                status_at = candle.close_at_utc
        else:
            if candle.close_price < lower:
                return "BROKEN", candle.close_at_utc
            if candle.low_price < lower and candle.close_price >= lower:
                status = "SWEPT"
                status_at = candle.close_at_utc
    return status, status_at


def _liquidity_attraction_score(
    buy_side: LiquidityPoolV1 | None,
    sell_side: LiquidityPoolV1 | None,
) -> Decimal:
    minimum_distance = Decimal("0.0001")
    buy_attraction = (
        ZERO
        if buy_side is None
        else buy_side.strength / max(buy_side.distance_fraction, minimum_distance)
    )
    sell_attraction = (
        ZERO
        if sell_side is None
        else sell_side.strength / max(sell_side.distance_fraction, minimum_distance)
    )
    total = buy_attraction + sell_attraction
    if total <= ZERO:
        return ZERO
    return _clip((buy_attraction - sell_attraction) / total, -ONE, ONE)


def _range_location_score(
    current_price: Decimal,
    range_low: Decimal,
    range_high: Decimal,
) -> Decimal:
    width = range_high - range_low
    if width <= ZERO:
        return ZERO
    position = _clip((current_price - range_low) / width, ZERO, ONE)
    return _clip(ONE - TWO * position, -ONE, ONE)


def _derivatives_context(
    candles: list[LiquidityDerivativesCandle],
    options: LiquidityDerivativesOptions,
) -> tuple[str, Decimal, Decimal, Decimal | None, Decimal | None, Decimal | None]:
    lookback = candles[-options.derivatives_lookback_bars :]
    if len(lookback) < 2:
        return "NOT_AVAILABLE", ZERO, ZERO, None, None, None
    price_start = lookback[0].close_price
    price_end = lookback[-1].close_price
    price_change = (
        ZERO if price_start <= ZERO else (price_end - price_start) / price_start
    )
    oi_values = [
        item.open_interest
        for item in lookback
        if item.open_interest is not None and item.open_interest >= ZERO
    ]
    if len(oi_values) < 2 or oi_values[0] is None or oi_values[-1] is None:
        return "NOT_AVAILABLE", ZERO, price_change, None, None, None
    oi_start = oi_values[0]
    oi_end = oi_values[-1]
    if oi_start <= ZERO:
        return "NOT_AVAILABLE", ZERO, price_change, oi_start, oi_end, None
    oi_change = (oi_end - oi_start) / oi_start
    price_active = abs(price_change) >= options.minimum_price_change_fraction
    oi_active = abs(oi_change) >= options.minimum_open_interest_change_fraction
    if not price_active or not oi_active:
        return "FLAT", ZERO, price_change, oi_start, oi_end, oi_change
    if price_change > ZERO and oi_change > ZERO:
        return "LONG_BUILDUP", ONE, price_change, oi_start, oi_end, oi_change
    if price_change < ZERO and oi_change > ZERO:
        return "SHORT_BUILDUP", -ONE, price_change, oi_start, oi_end, oi_change
    if price_change > ZERO and oi_change < ZERO:
        return (
            "SHORT_COVERING",
            Decimal("0.60"),
            price_change,
            oi_start,
            oi_end,
            oi_change,
        )
    return (
        "LONG_UNWINDING",
        Decimal("-0.60"),
        price_change,
        oi_start,
        oi_end,
        oi_change,
    )


def _liquidity_message(
    buy_side: LiquidityPoolV1 | None,
    sell_side: LiquidityPoolV1 | None,
    score: Decimal,
) -> str:
    buy_text = (
        "none"
        if buy_side is None
        else f"{buy_side.center_price} strength {buy_side.strength}"
    )
    sell_text = (
        "none"
        if sell_side is None
        else f"{sell_side.center_price} strength {sell_side.strength}"
    )
    return (
        f"Nearest buy-side pool: {buy_text}; nearest sell-side pool: "
        f"{sell_text}; normalized attraction score: {score:.6f}."
    )


def _derivatives_message(
    state: str,
    price_change: Decimal,
    oi_change: Decimal | None,
) -> str:
    if oi_change is None:
        return (
            "Canonical open interest is unavailable; derivatives direction "
            "contributes zero."
        )
    return (
        f"Derivatives state is {state}; price changed by {price_change:.6f} "
        f"and open interest changed by {oi_change:.6f}."
    )


def _pool_coverage(
    buy_side: LiquidityPoolV1 | None,
    sell_side: LiquidityPoolV1 | None,
) -> Decimal:
    if buy_side is not None and sell_side is not None:
        return ONE
    if buy_side is not None or sell_side is not None:
        return Decimal("0.60")
    return ZERO


def _agreement(values: list[Decimal], score: Decimal) -> Decimal:
    non_zero = [value for value in values if value != ZERO]
    if not non_zero or score == ZERO:
        return ZERO
    sign = ONE if score > ZERO else -ONE
    agreeing = sum(1 for value in non_zero if value * sign > ZERO)
    return Decimal(agreeing) / Decimal(len(non_zero))


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


def _quantize(value: Decimal) -> Decimal:
    return value.quantize(QUANTUM)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
