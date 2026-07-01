import os
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation


@dataclass(frozen=True, slots=True)
class OptionChainRuntimeSettings:
    enabled: bool
    provider: str
    engine_code: str
    engine_version: str
    policy_version: str
    actor: str
    internal_api_key: str | None
    maximum_output_age_seconds: int
    minimum_contract_count: int
    minimum_strike_count: int
    oi_wall_count: int
    oi_wall_moneyness_fraction: Decimal
    minimum_premium_change_fraction: Decimal
    minimum_open_interest_change_fraction: Decimal
    directional_threshold: Decimal
    fusion_confidence_threshold: Decimal

    @classmethod
    def load(cls) -> "OptionChainRuntimeSettings":
        enabled = _read_bool("THESISPULSE_OPTION_CHAIN_ENGINE_ENABLED", False)
        provider = os.getenv("THESISPULSE_OPTION_CHAIN_PROVIDER", "InMemory").strip()
        if provider not in {"InMemory"}:
            raise RuntimeError(
                "THESISPULSE_OPTION_CHAIN_PROVIDER currently supports only InMemory"
            )
        internal_api_key = os.getenv("THESISPULSE_OPTION_CHAIN_INTERNAL_API_KEY")
        if enabled and not internal_api_key:
            raise RuntimeError(
                "THESISPULSE_OPTION_CHAIN_INTERNAL_API_KEY is required when enabled"
            )
        return cls(
            enabled=enabled,
            provider=provider,
            engine_code=os.getenv(
                "THESISPULSE_OPTION_CHAIN_ENGINE_CODE",
                "THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE",
            ),
            engine_version=os.getenv(
                "THESISPULSE_OPTION_CHAIN_ENGINE_VERSION",
                "1.0.0",
            ),
            policy_version=os.getenv(
                "THESISPULSE_OPTION_CHAIN_POLICY_VERSION",
                "option-chain-intelligence-v1.0.0",
            ),
            actor=os.getenv(
                "THESISPULSE_OPTION_CHAIN_ENGINE_ACTOR",
                "ThesisPulse.AI.OptionChain",
            ),
            internal_api_key=internal_api_key,
            maximum_output_age_seconds=_read_int(
                "THESISPULSE_OPTION_CHAIN_MAXIMUM_OUTPUT_AGE_SECONDS",
                120,
                minimum=1,
                maximum=3600,
            ),
            minimum_contract_count=_read_int(
                "THESISPULSE_OPTION_CHAIN_MINIMUM_CONTRACT_COUNT",
                10,
                minimum=2,
                maximum=10000,
            ),
            minimum_strike_count=_read_int(
                "THESISPULSE_OPTION_CHAIN_MINIMUM_STRIKE_COUNT",
                5,
                minimum=1,
                maximum=5000,
            ),
            oi_wall_count=_read_int(
                "THESISPULSE_OPTION_CHAIN_OI_WALL_COUNT",
                3,
                minimum=1,
                maximum=50,
            ),
            oi_wall_moneyness_fraction=_read_decimal(
                "THESISPULSE_OPTION_CHAIN_OI_WALL_MONEYNESS_FRACTION",
                Decimal("0.15"),
                minimum=Decimal("0.000001"),
                maximum=Decimal("1"),
            ),
            minimum_premium_change_fraction=_read_decimal(
                "THESISPULSE_OPTION_CHAIN_MINIMUM_PREMIUM_CHANGE_FRACTION",
                Decimal("0.0025"),
                minimum=Decimal("0.000001"),
                maximum=Decimal("1"),
            ),
            minimum_open_interest_change_fraction=_read_decimal(
                "THESISPULSE_OPTION_CHAIN_MINIMUM_OI_CHANGE_FRACTION",
                Decimal("0.01"),
                minimum=Decimal("0.000001"),
                maximum=Decimal("1"),
            ),
            directional_threshold=_read_decimal(
                "THESISPULSE_OPTION_CHAIN_DIRECTIONAL_THRESHOLD",
                Decimal("0.20"),
                minimum=Decimal("0.01"),
                maximum=Decimal("1"),
            ),
            fusion_confidence_threshold=_read_decimal(
                "THESISPULSE_OPTION_CHAIN_FUSION_CONFIDENCE_THRESHOLD",
                Decimal("0.60"),
                minimum=Decimal("0.000001"),
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
