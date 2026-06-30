import os
from decimal import Decimal, InvalidOperation

from app.smc.definitions import SmcOptions


def smc_enabled() -> bool:
    raw = os.getenv("THESISPULSE_SMC_ENGINE_ENABLED", "false").strip().casefold()
    if raw in {"true", "1", "yes", "on"}:
        return True
    if raw in {"false", "0", "no", "off"}:
        return False
    raise RuntimeError("THESISPULSE_SMC_ENGINE_ENABLED must be a boolean value")


def load_smc_options() -> SmcOptions:
    options = SmcOptions(
        engine_code=os.getenv("THESISPULSE_SMC_ENGINE_CODE", "THESIS_PULSE_SMC"),
        engine_version=os.getenv("THESISPULSE_SMC_ENGINE_VERSION", "1.0.0"),
        policy_version=os.getenv(
            "THESISPULSE_SMC_POLICY_VERSION",
            "smc-structure-v1.0.0",
        ),
        required_input_count=_integer(
            "THESISPULSE_SMC_REQUIRED_INPUT_COUNT", 12, 7, 5000
        ),
        maximum_input_count=_integer(
            "THESISPULSE_SMC_MAXIMUM_INPUT_COUNT", 64, 12, 5000
        ),
        swing_left_bars=_integer("THESISPULSE_SMC_SWING_LEFT_BARS", 2, 1, 20),
        swing_right_bars=_integer("THESISPULSE_SMC_SWING_RIGHT_BARS", 2, 1, 20),
        minimum_break_fraction=_decimal(
            "THESISPULSE_SMC_MINIMUM_BREAK_FRACTION",
            Decimal("0.0005"),
            Decimal("0.000001"),
            Decimal("0.10"),
        ),
        directional_threshold=_decimal(
            "THESISPULSE_SMC_DIRECTIONAL_THRESHOLD",
            Decimal("0.20"),
            Decimal("0.01"),
            Decimal("1"),
        ),
        fusion_confidence_threshold=_decimal(
            "THESISPULSE_SMC_FUSION_CONFIDENCE_THRESHOLD",
            Decimal("0.55"),
            Decimal("0"),
            Decimal("1"),
        ),
    )
    options.validate()
    return options


def _integer(name: str, default: int, minimum: int, maximum: int) -> int:
    raw = os.getenv(name)
    try:
        value = default if raw is None else int(raw)
    except ValueError as exception:
        raise RuntimeError(f"{name} must be an integer") from exception
    if value < minimum or value > maximum:
        raise RuntimeError(f"{name} must be between {minimum} and {maximum}")
    return value


def _decimal(
    name: str,
    default: Decimal,
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
