import os
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation

from app.core.settings import Settings


@dataclass(frozen=True, slots=True)
class LiquidityDerivativesRuntimeSettings:
    enabled: bool
    engine_code: str
    engine_version: str
    policy_version: str
    actor: str
    required_input_count: int
    maximum_input_count: int
    swing_left_bars: int
    swing_right_bars: int
    pool_cluster_tolerance_fraction: Decimal
    pool_half_width_fraction: Decimal
    maximum_pools_per_side: int
    derivatives_lookback_bars: int
    minimum_price_change_fraction: Decimal
    minimum_open_interest_change_fraction: Decimal
    minimum_valid_input_ratio: Decimal
    maximum_output_age_seconds: int
    directional_threshold: Decimal
    fusion_confidence_threshold: Decimal

    @classmethod
    def load(
        cls,
        platform: Settings,
    ) -> "LiquidityDerivativesRuntimeSettings":
        enabled = _read_bool(
            "THESISPULSE_LIQUIDITY_DERIVATIVES_ENGINE_ENABLED",
            False,
        )
        if enabled and not platform.feature_factory_enabled:
            raise RuntimeError(
                "Liquidity Derivatives Context requires canonical candle intake"
            )
        required = _read_int(
            "THESISPULSE_LIQUIDITY_DERIVATIVES_REQUIRED_INPUT_COUNT",
            30,
            minimum=10,
            maximum=5000,
        )
        maximum = _read_int(
            "THESISPULSE_LIQUIDITY_DERIVATIVES_MAXIMUM_INPUT_COUNT",
            128,
            minimum=required,
            maximum=5000,
        )
        lookback = _read_int(
            "THESISPULSE_LIQUIDITY_DERIVATIVES_OI_LOOKBACK_BARS",
            6,
            minimum=2,
            maximum=maximum,
        )
        return cls(
            enabled=enabled,
            engine_code=os.getenv(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_ENGINE_CODE",
                "THESIS_PULSE_LIQUIDITY_DERIVATIVES_CONTEXT",
            ),
            engine_version=os.getenv(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_ENGINE_VERSION",
                "1.0.0",
            ),
            policy_version=os.getenv(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_POLICY_VERSION",
                "liquidity-derivatives-context-v1.0.0",
            ),
            actor=os.getenv(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_ENGINE_ACTOR",
                "ThesisPulse.AI.LiquidityDerivatives",
            ),
            required_input_count=required,
            maximum_input_count=maximum,
            swing_left_bars=_read_int(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_SWING_LEFT_BARS",
                2,
                minimum=1,
                maximum=10,
            ),
            swing_right_bars=_read_int(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_SWING_RIGHT_BARS",
                2,
                minimum=1,
                maximum=10,
            ),
            pool_cluster_tolerance_fraction=_read_decimal(
                "THESISPULSE_LIQUIDITY_POOL_CLUSTER_TOLERANCE_FRACTION",
                Decimal("0.0015"),
                minimum=Decimal("0.000001"),
                maximum=Decimal("0.05"),
            ),
            pool_half_width_fraction=_read_decimal(
                "THESISPULSE_LIQUIDITY_POOL_HALF_WIDTH_FRACTION",
                Decimal("0.0005"),
                minimum=Decimal("0.000001"),
                maximum=Decimal("0.05"),
            ),
            maximum_pools_per_side=_read_int(
                "THESISPULSE_LIQUIDITY_MAXIMUM_POOLS_PER_SIDE",
                5,
                minimum=1,
                maximum=50,
            ),
            derivatives_lookback_bars=lookback,
            minimum_price_change_fraction=_read_decimal(
                "THESISPULSE_DERIVATIVES_MINIMUM_PRICE_CHANGE_FRACTION",
                Decimal("0.0005"),
                minimum=Decimal("0"),
                maximum=Decimal("1"),
            ),
            minimum_open_interest_change_fraction=_read_decimal(
                "THESISPULSE_DERIVATIVES_MINIMUM_OI_CHANGE_FRACTION",
                Decimal("0.0020"),
                minimum=Decimal("0"),
                maximum=Decimal("1"),
            ),
            minimum_valid_input_ratio=_read_decimal(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_MINIMUM_VALID_INPUT_RATIO",
                Decimal("0.95"),
                minimum=Decimal("0"),
                maximum=Decimal("1"),
            ),
            maximum_output_age_seconds=_read_int(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_MAXIMUM_OUTPUT_AGE_SECONDS",
                420,
                minimum=1,
                maximum=3600,
            ),
            directional_threshold=_read_decimal(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_DIRECTIONAL_THRESHOLD",
                Decimal("0.20"),
                minimum=Decimal("0.01"),
                maximum=Decimal("1"),
            ),
            fusion_confidence_threshold=_read_decimal(
                "THESISPULSE_LIQUIDITY_DERIVATIVES_FUSION_CONFIDENCE_THRESHOLD",
                Decimal("0.55"),
                minimum=Decimal("0"),
                maximum=Decimal("1"),
            ),
        )


def _read_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    value = raw.strip().casefold()
    if value in {"true", "1", "yes", "on"}:
        return True
    if value in {"false", "0", "no", "off"}:
        return False
    raise RuntimeError(f"{name} must be a boolean value")


def _read_int(name: str, default: int, *, minimum: int, maximum: int) -> int:
    raw = os.getenv(name)
    try:
        value = default if raw is None else int(raw)
    except ValueError as exception:
        raise RuntimeError(f"{name} must be an integer") from exception
    if value < minimum or value > maximum:
        raise RuntimeError(f"{name} must be between {minimum} and {maximum}")
    return value


def _read_decimal(
    name: str,
    default: Decimal,
    *,
    minimum: Decimal,
    maximum: Decimal,
) -> Decimal:
    raw = os.getenv(name)
    try:
        value = default if raw is None else Decimal(raw)
    except InvalidOperation as exception:
        raise RuntimeError(f"{name} must be a decimal") from exception
    if value < minimum or value > maximum:
        raise RuntimeError(f"{name} must be between {minimum} and {maximum}")
    return value
