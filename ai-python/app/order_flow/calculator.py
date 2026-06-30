from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import NAMESPACE_URL, uuid5

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.order_flow import OrderFlowEngineOutputV1, OrderFlowEvidenceV1
from app.order_flow.definitions import OrderFlowOptions
from app.order_flow.models import QuoteSample

ZERO = Decimal("0")
ONE = Decimal("1")
TWO = Decimal("2")
QUANTUM = Decimal("0.000001")


class DeterministicOrderFlowCalculator:
    def __init__(self, options: OrderFlowOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> OrderFlowOptions:
        return self._options

    def calculate(
        self,
        delivery: MarketCandleDeliveryV1,
        samples: list[QuoteSample],
        generated_at_utc: datetime,
        revision: int,
    ) -> OrderFlowEngineOutputV1:
        payload = delivery.envelope.payload
        if payload.timeframe != "5m":
            raise ValueError("Order Flow V1 only supports the 5m timeframe")
        if not payload.is_closed or payload.is_provisional:
            raise ValueError("Order Flow requires a closed non-provisional candle")
        if payload.volume_quantity < ZERO:
            raise ValueError("Candle volume cannot be negative")

        ordered = sorted(samples, key=lambda item: (item.event_at_utc, item.message_uid.int))
        usable = [
            item
            for item in ordered
            if item.is_usable_for_new_exposure
            and item.quality_status == "VALID"
            and item.last_traded_price is not None
            and item.last_traded_price > ZERO
        ]
        warnings = ["PROXY_TICK_RULE_NOT_AGGRESSOR_FLOW"]
        if len(ordered) < self._options.minimum_quote_samples:
            warnings.append("INSUFFICIENT_QUOTE_SAMPLES")
        usable_ratio = (
            Decimal(len(usable)) / Decimal(len(ordered)) if ordered else ZERO
        )
        if usable_ratio < self._options.minimum_usable_ratio:
            warnings.append("LOW_USABLE_QUOTE_RATIO")

        book_imbalance = _weighted_book_imbalance(usable)
        tick_delta_quantity, total_traded_quantity, signed_parts = _tick_rule_delta(usable)
        tick_delta_ratio = _safe_ratio(tick_delta_quantity, total_traded_quantity)
        price_change_fraction = _price_change(usable)
        open_interest_change = _open_interest_change(usable)
        oi_signal = _open_interest_signal(price_change_fraction, open_interest_change)
        traded_quantity_coverage = (
            min(ONE, total_traded_quantity / payload.volume_quantity)
            if payload.volume_quantity > ZERO
            else ZERO
        )
        if traded_quantity_coverage < self._options.minimum_traded_quantity_coverage:
            warnings.append("LOW_QUOTE_VOLUME_COVERAGE")
        if open_interest_change is None:
            warnings.append("OPEN_INTEREST_UNAVAILABLE")

        absorption_score = _absorption_score(
            tick_delta_ratio,
            price_change_fraction,
            self._options,
        )
        exhaustion_score = _exhaustion_score(
            usable,
            signed_parts,
            price_change_fraction,
            self._options,
        )
        if absorption_score > ZERO:
            warnings.append(
                "BUY_PRESSURE_ABSORBED"
                if tick_delta_ratio > ZERO
                else "SELL_PRESSURE_ABSORBED"
            )
        if exhaustion_score > ZERO:
            warnings.append("ORDER_FLOW_EXHAUSTION")

        raw_score = _clip(
            book_imbalance * self._options.book_weight
            + tick_delta_ratio * self._options.tick_delta_weight
            + oi_signal * self._options.open_interest_weight,
            -ONE,
            ONE,
        )
        dampening = _clip(
            ONE - absorption_score * Decimal("0.35") - exhaustion_score * Decimal("0.25"),
            Decimal("0.40"),
            ONE,
        )
        score = _quantize(raw_score * dampening)
        direction = _direction(score, self._options.directional_threshold)
        component_values = [book_imbalance, tick_delta_ratio]
        if open_interest_change is not None:
            component_values.append(oi_signal)
        agreement = _component_agreement(component_values, score)
        sample_coverage = min(
            ONE,
            Decimal(len(usable)) / Decimal(self._options.minimum_quote_samples),
        )
        confidence = _clip(
            sample_coverage * Decimal("0.35")
            + usable_ratio * Decimal("0.20")
            + traded_quantity_coverage * Decimal("0.15")
            + agreement * Decimal("0.20")
            + abs(score) * Decimal("0.10"),
            ZERO,
            ONE,
        )
        confidence = _quantize(
            confidence
            * (ONE - absorption_score * Decimal("0.20"))
            * (ONE - exhaustion_score * Decimal("0.15"))
        )

        last_quote_age = (
            payload.close_at_utc - usable[-1].event_at_utc if usable else timedelta.max
        )
        is_stale = (
            last_quote_age < timedelta(0)
            or last_quote_age.total_seconds()
            > self._options.maximum_quote_age_seconds
        )
        if is_stale:
            warnings.append("ORDER_FLOW_QUOTES_STALE")

        data_quality_status = "VALID"
        if not usable:
            data_quality_status = "INVALID"
        elif warnings:
            data_quality_status = "DEGRADED"
        is_eligible = (
            len(usable) >= self._options.minimum_quote_samples
            and usable_ratio >= self._options.minimum_usable_ratio
            and traded_quantity_coverage
            >= self._options.minimum_traded_quantity_coverage
            and not is_stale
            and direction != "NEUTRAL"
            and confidence >= self._options.fusion_confidence_threshold
        )
        evidence = _evidence(
            book_imbalance,
            tick_delta_ratio,
            oi_signal,
            open_interest_change,
            absorption_score,
            exhaustion_score,
            self._options,
        )
        output_uid = uuid5(
            NAMESPACE_URL,
            "|".join(
                [
                    "order-flow-output-v1",
                    str(delivery.envelope.metadata.message_id),
                    self._options.policy_version,
                    str(revision),
                ]
            ),
        )
        message_uid = uuid5(
            NAMESPACE_URL,
            f"order-flow-message-v1|{output_uid}",
        )
        return OrderFlowEngineOutputV1(
            output_uid=output_uid,
            message_uid=message_uid,
            source_candle_message_uid=delivery.envelope.metadata.message_id,
            quote_message_uids=[item.message_uid for item in usable],
            instrument_key=payload.instrument_key,
            timeframe="5m",
            as_of_utc=_as_utc(payload.close_at_utc),
            generated_at_utc=_as_utc(generated_at_utc),
            engine_code=self._options.engine_code,
            engine_version=self._options.engine_version,
            policy_version=self._options.policy_version,
            direction=direction,
            score=score,
            confidence=confidence,
            book_imbalance=_quantize(book_imbalance),
            tick_rule_delta_quantity=_quantize(tick_delta_quantity),
            tick_rule_delta_ratio=_quantize(tick_delta_ratio),
            open_interest_change_fraction=(
                None if open_interest_change is None else _quantize(open_interest_change)
            ),
            price_change_fraction=_quantize(price_change_fraction),
            absorption_score=_quantize(absorption_score),
            exhaustion_score=_quantize(exhaustion_score),
            quote_sample_count=len(ordered),
            usable_quote_count=len(usable),
            traded_quantity_coverage=_quantize(traded_quantity_coverage),
            data_quality_status=data_quality_status,
            is_stale=is_stale,
            is_eligible_for_fusion=is_eligible,
            revision=revision,
            evidence=evidence,
            warnings=sorted(set(warnings)),
        )


def _weighted_book_imbalance(samples: list[QuoteSample]) -> Decimal:
    weighted = ZERO
    total_weight = ZERO
    for index, item in enumerate(samples, start=1):
        buy = item.total_buy_quantity
        sell = item.total_sell_quantity
        if buy is None or sell is None or buy < ZERO or sell < ZERO:
            continue
        total = buy + sell
        if total <= ZERO:
            continue
        weight = Decimal(index)
        weighted += ((buy - sell) / total) * weight
        total_weight += weight
    return ZERO if total_weight == ZERO else _clip(weighted / total_weight, -ONE, ONE)


def _tick_rule_delta(
    samples: list[QuoteSample],
) -> tuple[Decimal, Decimal, list[Decimal]]:
    prior_price: Decimal | None = None
    prior_sign = ZERO
    signed: list[Decimal] = []
    total = ZERO
    for item in samples:
        price = item.last_traded_price
        quantity = item.last_traded_quantity
        if price is None or price <= ZERO or quantity is None or quantity <= ZERO:
            continue
        if prior_price is None:
            sign = ZERO
        elif price > prior_price:
            sign = ONE
        elif price < prior_price:
            sign = -ONE
        else:
            sign = prior_sign
        signed_quantity = sign * quantity
        signed.append(signed_quantity)
        total += quantity
        prior_price = price
        if sign != ZERO:
            prior_sign = sign
    return sum(signed, ZERO), total, signed


def _price_change(samples: list[QuoteSample]) -> Decimal:
    prices = [item.last_traded_price for item in samples if item.last_traded_price]
    if len(prices) < 2 or prices[0] is None or prices[-1] is None:
        return ZERO
    return (prices[-1] - prices[0]) / prices[0]


def _open_interest_change(samples: list[QuoteSample]) -> Decimal | None:
    values = [item.open_interest for item in samples if item.open_interest is not None]
    if len(values) < 2 or values[0] is None or values[0] <= ZERO or values[-1] is None:
        return None
    return (values[-1] - values[0]) / values[0]


def _open_interest_signal(
    price_change: Decimal,
    open_interest_change: Decimal | None,
) -> Decimal:
    if open_interest_change is None:
        return ZERO
    price_sign = ONE if price_change > ZERO else -ONE if price_change < ZERO else ZERO
    oi_sign = ONE if open_interest_change > ZERO else -ONE if open_interest_change < ZERO else ZERO
    if price_sign == ONE and oi_sign == ONE:
        return ONE
    if price_sign == -ONE and oi_sign == ONE:
        return -ONE
    if price_sign == ONE and oi_sign == -ONE:
        return Decimal("0.40")
    if price_sign == -ONE and oi_sign == -ONE:
        return Decimal("-0.40")
    return ZERO


def _absorption_score(
    flow_ratio: Decimal,
    price_change: Decimal,
    options: OrderFlowOptions,
) -> Decimal:
    flow_excess = max(ZERO, abs(flow_ratio) - options.absorption_flow_threshold)
    if abs(price_change) >= options.absorption_price_threshold:
        return ZERO
    denominator = max(Decimal("0.000001"), ONE - options.absorption_flow_threshold)
    flow_strength = _clip(flow_excess / denominator, ZERO, ONE)
    price_flatness = _clip(
        ONE - abs(price_change) / options.absorption_price_threshold,
        ZERO,
        ONE,
    )
    return _clip(flow_strength * price_flatness, ZERO, ONE)


def _exhaustion_score(
    samples: list[QuoteSample],
    signed_parts: list[Decimal],
    price_change: Decimal,
    options: OrderFlowOptions,
) -> Decimal:
    if len(samples) < 4 or len(signed_parts) < 4:
        return ZERO
    if abs(price_change) < options.exhaustion_price_threshold:
        return ZERO
    midpoint = len(signed_parts) // 2
    first = sum((abs(value) for value in signed_parts[:midpoint]), ZERO)
    second = sum((abs(value) for value in signed_parts[midpoint:]), ZERO)
    if first <= ZERO:
        return ZERO
    participation_ratio = second / first
    if participation_ratio >= options.exhaustion_participation_ratio:
        return ZERO
    return _clip(
        ONE - participation_ratio / options.exhaustion_participation_ratio,
        ZERO,
        ONE,
    )


def _component_agreement(values: list[Decimal], score: Decimal) -> Decimal:
    direction = ONE if score > ZERO else -ONE if score < ZERO else ZERO
    if direction == ZERO or not values:
        return ZERO
    agreeing = sum(1 for value in values if value * direction > ZERO)
    neutral = sum(1 for value in values if value == ZERO)
    return _clip(
        (Decimal(agreeing) + Decimal(neutral) * Decimal("0.50"))
        / Decimal(len(values)),
        ZERO,
        ONE,
    )


def _evidence(
    book: Decimal,
    delta: Decimal,
    oi_signal: Decimal,
    oi_change: Decimal | None,
    absorption: Decimal,
    exhaustion: Decimal,
    options: OrderFlowOptions,
) -> list[OrderFlowEvidenceV1]:
    return [
        OrderFlowEvidenceV1(
            code="ORDER_BOOK_IMBALANCE_PROXY",
            message=f"Weighted buy/sell quantity imbalance is {book:.4f}.",
            impact=_impact(book),
            weight=options.book_weight,
            contribution=_quantize(book * options.book_weight),
        ),
        OrderFlowEvidenceV1(
            code="TICK_RULE_DELTA_PROXY",
            message=f"Tick-rule signed quantity ratio is {delta:.4f}.",
            impact=_impact(delta),
            weight=options.tick_delta_weight,
            contribution=_quantize(delta * options.tick_delta_weight),
        ),
        OrderFlowEvidenceV1(
            code="OPEN_INTEREST_CONTEXT",
            message=(
                "Open-interest context is unavailable."
                if oi_change is None
                else f"Open interest changed by {oi_change:.6f}."
            ),
            impact=_impact(oi_signal),
            weight=options.open_interest_weight,
            contribution=_quantize(oi_signal * options.open_interest_weight),
        ),
        OrderFlowEvidenceV1(
            code="ABSORPTION_PROXY",
            message=f"Absorption proxy score is {absorption:.4f}.",
            impact="NEUTRAL",
            weight=ZERO,
            contribution=ZERO,
        ),
        OrderFlowEvidenceV1(
            code="EXHAUSTION_PROXY",
            message=f"Exhaustion proxy score is {exhaustion:.4f}.",
            impact="NEUTRAL",
            weight=ZERO,
            contribution=ZERO,
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


def _safe_ratio(numerator: Decimal, denominator: Decimal) -> Decimal:
    return ZERO if denominator <= ZERO else _clip(numerator / denominator, -ONE, ONE)


def _clip(value: Decimal, minimum: Decimal, maximum: Decimal) -> Decimal:
    return min(maximum, max(minimum, value))


def _quantize(value: Decimal) -> Decimal:
    return value.quantize(QUANTUM)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
