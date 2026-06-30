from datetime import UTC, datetime, timedelta
from decimal import Decimal, InvalidOperation, localcontext
from uuid import NAMESPACE_URL, UUID, uuid5

from app.contracts.v1.market_data import (
    FeatureSnapshotV1,
    FeatureValueV1,
    MarketCandleDeliveryV1,
)
from app.features.definitions import (
    FEATURE_NAMES,
    FRESHNESS_LIMITS,
    ONE,
    QUANTUM,
    TIMEFRAME_DURATIONS,
    ZERO,
    FeatureFactoryOptions,
)
from app.features.models import CandleInput


class DeterministicFeatureCalculator:
    def __init__(self, options: FeatureFactoryOptions) -> None:
        options.validate()
        self._options = options

    @property
    def options(self) -> FeatureFactoryOptions:
        return self._options

    def calculate(
        self,
        delivery: MarketCandleDeliveryV1,
        candles: list[CandleInput],
        generated_at_utc: datetime,
        revision: int,
    ) -> FeatureSnapshotV1:
        payload = delivery.envelope.payload
        generated_at_utc = _as_utc(generated_at_utc)
        ordered = self._prepare_window(candles, payload.close_at_utc)
        calculated = self._calculate_values(ordered)
        features = [
            FeatureValueV1(
                name=name,
                version=self._options.feature_version,
                value=_quantize(calculated.get(name)),
            )
            for name in FEATURE_NAMES
        ]
        missing = [feature.name for feature in features if feature.value is None]
        completeness = Decimal(len(features) - len(missing)) / Decimal(len(features))
        warnings = self._warnings(delivery, ordered, missing, generated_at_utc)
        freshness = max(
            timedelta(0),
            generated_at_utc - _as_utc(payload.received_at_utc),
        )
        freshness_milliseconds = int(freshness.total_seconds() * 1000)
        is_stale = freshness > FRESHNESS_LIMITS[payload.timeframe]
        source_quality = payload.quality_status.strip().upper()
        source_invalid = source_quality in {
            "INVALID",
            "CONFLICTED",
            "OUT_OF_ORDER",
            "UNKNOWN",
        }

        if source_invalid:
            quality = "INVALID"
        elif warnings:
            quality = "DEGRADED"
        else:
            quality = "VALID"

        is_eligible = (
            quality == "VALID"
            and payload.is_closed
            and not payload.is_provisional
            and payload.is_usable_for_new_exposure
            and len(ordered) >= self._options.required_input_count
            and not missing
            and not is_stale
        )

        return FeatureSnapshotV1(
            snapshot_uid=self.create_snapshot_uid(
                delivery.envelope.metadata.message_id,
                payload.instrument_key,
                payload.timeframe,
                payload.close_at_utc,
                revision,
            ),
            message_uid=delivery.envelope.metadata.message_id,
            instrument_key=payload.instrument_key,
            timeframe=payload.timeframe,
            as_of_utc=_as_utc(payload.close_at_utc),
            data_cutoff_utc=_as_utc(delivery.envelope.metadata.occurred_at_utc),
            generated_at_utc=generated_at_utc,
            feature_set_version=self._options.feature_set_version,
            revision=revision,
            input_count=len(ordered),
            required_input_count=self._options.required_input_count,
            completeness=_quantize(completeness) or ZERO,
            data_quality_status=quality,
            freshness_milliseconds=freshness_milliseconds,
            is_stale=is_stale,
            is_eligible_for_engines=is_eligible,
            features=features,
            missing_features=missing,
            warnings=warnings,
        )

    @staticmethod
    def create_snapshot_uid(
        message_uid: UUID,
        instrument_key: str,
        timeframe: str,
        as_of_utc: datetime,
        revision: int,
    ) -> UUID:
        identity = (
            f"feature-snapshot|{message_uid}|{instrument_key}|{timeframe}|"
            f"{_as_utc(as_of_utc).isoformat()}|{revision}"
        )
        return uuid5(NAMESPACE_URL, identity)

    def _prepare_window(
        self,
        candles: list[CandleInput],
        as_of_utc: datetime,
    ) -> list[CandleInput]:
        cutoff = _as_utc(as_of_utc)
        latest_by_open: dict[datetime, CandleInput] = {}
        for candle in candles:
            if _as_utc(candle.close_at_utc) > cutoff:
                continue
            current = latest_by_open.get(candle.open_at_utc)
            if current is None or candle.revision > current.revision:
                latest_by_open[candle.open_at_utc] = candle
        return sorted(
            latest_by_open.values(),
            key=lambda item: item.open_at_utc,
        )[-self._options.maximum_input_count :]

    def _warnings(
        self,
        delivery: MarketCandleDeliveryV1,
        candles: list[CandleInput],
        missing: list[str],
        generated_at_utc: datetime,
    ) -> list[str]:
        payload = delivery.envelope.payload
        warnings: list[str] = []
        if len(candles) < self._options.required_input_count or missing:
            warnings.append("INSUFFICIENT_WARMUP")
        if self._has_intraday_gap(candles, payload.timeframe):
            warnings.append("CANDLE_GAP_DETECTED")
        quality = payload.quality_status.strip().upper()
        if quality != "VALID":
            warnings.append(f"SOURCE_QUALITY_{quality}")
        if generated_at_utc - _as_utc(payload.received_at_utc) > FRESHNESS_LIMITS[
            payload.timeframe
        ]:
            warnings.append("SOURCE_DATA_STALE")
        return warnings

    def _calculate_values(
        self,
        candles: list[CandleInput],
    ) -> dict[str, Decimal | None]:
        closes = [item.close_price for item in candles]
        volumes = [item.volume_quantity for item in candles]
        true_ranges = _true_ranges(candles)
        values: dict[str, Decimal | None] = {
            name: None for name in FEATURE_NAMES
        }
        values["close_return_1"] = _return(closes, 1)
        values["close_return_3"] = _return(closes, 3)
        values["sma_5"] = _sma(closes, 5)
        values["sma_20"] = _sma(closes, 20)
        values["ema_5"] = _ema(closes, 5)
        values["ema_20"] = _ema(closes, 20)
        values["momentum_5"] = _return(closes, 5)
        values["true_range_1"] = true_ranges[-1] if true_ranges else None
        values["atr_14"] = _sma(true_ranges, 14)
        values["realized_volatility_20"] = _realized_volatility(closes, 20)
        values["volume_sma_20"] = _sma(volumes, 20)
        values["volume_ratio_20"] = _safe_divide(
            volumes[-1] if volumes else None,
            values["volume_sma_20"],
        )
        values["close_location_value"] = _close_location(candles)
        values["trend_spread_5_20"] = _safe_divide(
            _subtract(values["sma_5"], values["sma_20"]),
            values["sma_20"],
        )
        values["trend_score"] = _trend_score(
            closes[-1] if closes else None,
            values["sma_20"],
            values["atr_14"],
        )
        return values

    @staticmethod
    def _has_intraday_gap(
        candles: list[CandleInput],
        timeframe: str,
    ) -> bool:
        if timeframe == "1d" or len(candles) < 2:
            return False
        expected = TIMEFRAME_DURATIONS[timeframe]
        tolerance = expected + (expected / 2)
        for previous, current in zip(candles, candles[1:], strict=False):
            if previous.open_at_utc.date() != current.open_at_utc.date():
                continue
            if current.open_at_utc - previous.open_at_utc > tolerance:
                return True
        return False


def _return(values: list[Decimal], periods: int) -> Decimal | None:
    if len(values) <= periods or values[-(periods + 1)] == 0:
        return None
    return values[-1] / values[-(periods + 1)] - ONE


def _sma(values: list[Decimal], periods: int) -> Decimal | None:
    if len(values) < periods:
        return None
    return sum(values[-periods:], ZERO) / Decimal(periods)


def _ema(values: list[Decimal], periods: int) -> Decimal | None:
    if len(values) < periods:
        return None
    alpha = Decimal(2) / Decimal(periods + 1)
    result = sum(values[:periods], ZERO) / Decimal(periods)
    for value in values[periods:]:
        result = (value * alpha) + (result * (ONE - alpha))
    return result


def _true_ranges(candles: list[CandleInput]) -> list[Decimal]:
    ranges: list[Decimal] = []
    for index in range(1, len(candles)):
        candle = candles[index]
        previous_close = candles[index - 1].close_price
        ranges.append(
            max(
                candle.high_price - candle.low_price,
                abs(candle.high_price - previous_close),
                abs(candle.low_price - previous_close),
            )
        )
    return ranges


def _realized_volatility(
    closes: list[Decimal],
    return_count: int,
) -> Decimal | None:
    if len(closes) < return_count + 1:
        return None
    returns = [
        closes[index] / closes[index - 1] - ONE
        for index in range(len(closes) - return_count, len(closes))
        if closes[index - 1] != 0
    ]
    if len(returns) != return_count:
        return None
    mean = sum(returns, ZERO) / Decimal(return_count)
    variance = sum(((value - mean) ** 2 for value in returns), ZERO) / Decimal(
        return_count
    )
    with localcontext() as context:
        context.prec = 28
        try:
            return variance.sqrt()
        except InvalidOperation:
            return None


def _close_location(candles: list[CandleInput]) -> Decimal | None:
    if not candles:
        return None
    latest = candles[-1]
    candle_range = latest.high_price - latest.low_price
    if candle_range == 0:
        return ZERO
    return ((latest.close_price - latest.low_price) * Decimal(2) / candle_range) - ONE


def _trend_score(
    close: Decimal | None,
    sma_20: Decimal | None,
    atr_14: Decimal | None,
) -> Decimal | None:
    if close is None or sma_20 is None or atr_14 in (None, ZERO):
        return None
    raw = (close - sma_20) / atr_14
    return max(Decimal("-1"), min(Decimal("1"), raw))


def _safe_divide(
    numerator: Decimal | None,
    denominator: Decimal | None,
) -> Decimal | None:
    if numerator is None or denominator in (None, ZERO):
        return None
    return numerator / denominator


def _subtract(left: Decimal | None, right: Decimal | None) -> Decimal | None:
    if left is None or right is None:
        return None
    return left - right


def _quantize(value: Decimal | None) -> Decimal | None:
    return None if value is None else value.quantize(QUANTUM)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=UTC)
    return value.astimezone(UTC)
