from dataclasses import dataclass
from datetime import timedelta
from decimal import Decimal

ZERO = Decimal("0")
ONE = Decimal("1")
QUANTUM = Decimal("0.000000000001")

FEATURE_NAMES = (
    "close_return_1",
    "close_return_3",
    "sma_5",
    "sma_20",
    "ema_5",
    "ema_20",
    "momentum_5",
    "true_range_1",
    "atr_14",
    "realized_volatility_20",
    "volume_sma_20",
    "volume_ratio_20",
    "close_location_value",
    "trend_spread_5_20",
    "trend_score",
)

FRESHNESS_LIMITS = {
    "1m": timedelta(seconds=90),
    "5m": timedelta(minutes=7),
    "15m": timedelta(minutes=20),
    "1h": timedelta(minutes=75),
    "1d": timedelta(hours=36),
}

TIMEFRAME_DURATIONS = {
    "1m": timedelta(minutes=1),
    "5m": timedelta(minutes=5),
    "15m": timedelta(minutes=15),
    "1h": timedelta(hours=1),
    "1d": timedelta(days=1),
}


@dataclass(frozen=True, slots=True)
class FeatureFactoryOptions:
    feature_set_version: str = "feature-set-v1.0.0"
    feature_version: str = "1.0.0"
    required_input_count: int = 21
    maximum_input_count: int = 64

    def validate(self) -> None:
        if not self.feature_set_version.strip():
            raise ValueError("feature_set_version is required")
        if not self.feature_version.strip():
            raise ValueError("feature_version is required")
        if self.required_input_count < 21:
            raise ValueError("required_input_count must be at least 21")
        if self.maximum_input_count < self.required_input_count:
            raise ValueError(
                "maximum_input_count must be greater than or equal to required_input_count"
            )
        if self.maximum_input_count > 5000:
            raise ValueError("maximum_input_count cannot exceed 5000")
