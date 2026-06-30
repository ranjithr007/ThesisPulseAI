import os
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation

from app.core.settings import Settings


@dataclass(frozen=True, slots=True)
class SmartMoneyRuntimeSettings:
    enabled: bool
    engine_code: str
    engine_version: str
    policy_version: str
    actor: str
    required_input_count: int
    maximum_input_count: int
    swing_left_bars: int
    swing_right_bars: int
    order_block_search_bars: int
    maximum_zones_per_type: int
    maximum_zone_age_bars: int
    break_tolerance_fraction: Decimal
    minimum_fair_value_gap_fraction: Decimal
    minimum_valid_input_ratio: Decimal
    maximum_output_age_seconds: int
    directional_threshold: Decimal
    fusion_confidence_threshold: Decimal

    @classmethod
    def load(cls, platform: Settings) -> "SmartMoneyRuntimeSettings":
        enabled = _read_bool("THESISPULSE_SMART_MONEY_ENGINE_ENABLED", False)
        if enabled and not platform.feature_factory_enabled:
            raise RuntimeError(
                "Smart Money Concepts Engine requires canonical candle intake"
            )
        required = _read_int(
            "THESISPULSE_SMART_MONEY_REQUIRED_INPUT_COUNT",
            30,
            minimum=10,
            maximum=5000,
        )
        maximum = _read_int(
            "THESISPULSE_SMART_MONEY_MAXIMUM_INPUT_COUNT",
            128,
            minimum=required,
            maximum=5000,
        )
        return cls(
            enabled=enabled,
            engine_code=os.getenv(
                "THESISPULSE_SMART_MONEY_ENGINE_CODE",
                "THESIS_PULSE_SMART_MONEY_CONCEPTS",
            ),
            engine_version=os.getenv(
                "THESISPULSE_SMART_MONEY_ENGINE_VERSION",
                "1.0.0",
            ),
            policy_version=os.getenv(
                "THESISPULSE_SMART_MONEY_POLICY_VERSION",
                "smart-money-structure-v1.0.0",
            ),
            actor=os.getenv(
                "THESISPULSE_SMART_MONEY_ENGINE_ACTOR",
                "ThesisPulse.AI.SmartMoney",
            ),
            required_input_count=required,
            maximum_input_count=maximum,
            swing_left_bars=_read_int(
                "THESISPULSE_SMART_MONEY_SWING_LEFT_BARS",
                2,
                minimum=1,
                maximum=10,
            ),
            swing_right_bars=_read_int(
                "THESISPULSE_SMART_MONEY_SWING_RIGHT_BARS",
                2,
                minimum=1,
                maximum=10,
            ),
            order_block_search_bars=_read_int(
                "THESISPULSE_SMART_MONEY_ORDER_BLOCK_SEARCH_BARS",
                8,
                minimum=1,
                maximum=100,
            ),
            maximum_zones_per_type=_read_int(
                "THESISPULSE_SMART_MONEY_MAXIMUM_ZONES_PER_TYPE",
                5,
                minimum=1,
                maximum=50,
            ),
            maximum_zone_age_bars=_read_int(
                "THESISPULSE_SMART_MONEY_MAXIMUM_ZONE_AGE_BARS",
                24,
                minimum=1,
                maximum=500,
            ),
            break_tolerance_fraction=_read_decimal(
                "THESISPULSE_SMART_MONEY_BREAK_TOLERANCE_FRACTION",
                Decimal("0.0002"),
                minimum=Decimal("0"),
                maximum=Decimal("0.05"),
            ),
            minimum_fair_value_gap_fraction=_read_decimal(
                "THESISPULSE_SMART_MONEY_MINIMUM_FAIR_VALUE_GAP_FRACTION",
                Decimal("0.0005"),
                minimum=Decimal("0"),
                maximum=Decimal("0.05"),
            ),
            minimum_valid_input_ratio=_read_decimal(
                "THESISPULSE_SMART_MONEY_MINIMUM_VALID_INPUT_RATIO",
                Decimal("0.95"),
                minimum=Decimal("0"),
                maximum=Decimal("1"),
            ),
            maximum_output_age_seconds=_read_int(
                "THESISPULSE_SMART_MONEY_MAXIMUM_OUTPUT_AGE_SECONDS",
                420,
                minimum=1,
                maximum=3600,
            ),
            directional_threshold=_read_decimal(
                "THESISPULSE_SMART_MONEY_DIRECTIONAL_THRESHOLD",
                Decimal("0.20"),
                minimum=Decimal("0.01"),
                maximum=Decimal("1"),
            ),
            fusion_confidence_threshold=_read_decimal(
                "THESISPULSE_SMART_MONEY_FUSION_CONFIDENCE_THRESHOLD",
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
