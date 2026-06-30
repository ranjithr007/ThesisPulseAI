from dataclasses import dataclass
from decimal import Decimal

ZERO = Decimal("0")
ONE = Decimal("1")
NEGATIVE_ONE = Decimal("-1")
QUANTUM = Decimal("0.00000001")


@dataclass(frozen=True, slots=True)
class VolatilityBand:
    low: Decimal
    high: Decimal
    extreme: Decimal


@dataclass(frozen=True, slots=True)
class MarketRegimeOptions:
    engine_code: str = "THESIS_PULSE_MARKET_REGIME"
    engine_version: str = "1.0.0"
    policy_version: str = "market-regime-v1.0.0"
    trend_threshold: Decimal = Decimal("0.45")
    alignment_threshold: Decimal = Decimal("0.65")
    strong_trend_threshold: Decimal = Decimal("0.75")
    range_threshold: Decimal = Decimal("0.60")

    def validate(self) -> None:
        if not self.engine_code.strip():
            raise ValueError("engine_code is required")
        if not self.engine_version.strip():
            raise ValueError("engine_version is required")
        if not self.policy_version.strip():
            raise ValueError("policy_version is required")
        if not ZERO < self.trend_threshold < ONE:
            raise ValueError("trend_threshold must be between zero and one")
        if not ZERO < self.alignment_threshold <= ONE:
            raise ValueError("alignment_threshold must be between zero and one")
        if not self.trend_threshold < self.strong_trend_threshold <= ONE:
            raise ValueError("strong_trend_threshold is invalid")
        if not ZERO < self.range_threshold <= ONE:
            raise ValueError("range_threshold must be between zero and one")


TREND_COMPONENT_WEIGHTS: dict[str, Decimal] = {
    "TREND_SCORE": Decimal("0.40"),
    "TREND_SPREAD": Decimal("0.25"),
    "MOMENTUM": Decimal("0.20"),
    "RETURN_3": Decimal("0.15"),
}

REGIME_EVIDENCE_WEIGHTS: dict[str, Decimal] = {
    "REGIME_TREND_BIAS": Decimal("0.30"),
    "REGIME_TREND_ALIGNMENT": Decimal("0.20"),
    "REGIME_RANGE_COMPRESSION": Decimal("0.20"),
    "REGIME_TRANSITION_RISK": Decimal("0.15"),
    "REGIME_VOLATILITY_STATE": Decimal("0.15"),
}

VOLATILITY_BANDS: dict[str, VolatilityBand] = {
    "1m": VolatilityBand(
        low=Decimal("0.00050"),
        high=Decimal("0.00150"),
        extreme=Decimal("0.00350"),
    ),
    "5m": VolatilityBand(
        low=Decimal("0.00100"),
        high=Decimal("0.00300"),
        extreme=Decimal("0.00700"),
    ),
    "15m": VolatilityBand(
        low=Decimal("0.00180"),
        high=Decimal("0.00500"),
        extreme=Decimal("0.01200"),
    ),
    "1h": VolatilityBand(
        low=Decimal("0.00350"),
        high=Decimal("0.01000"),
        extreme=Decimal("0.02500"),
    ),
    "1d": VolatilityBand(
        low=Decimal("0.01000"),
        high=Decimal("0.02500"),
        extreme=Decimal("0.06000"),
    ),
}
