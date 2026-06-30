from dataclasses import dataclass
from decimal import Decimal

ZERO = Decimal("0")
ONE = Decimal("1")
NEGATIVE_ONE = Decimal("-1")
QUANTUM = Decimal("0.00000001")

TIMEFRAME_WEIGHTS: dict[str, Decimal] = {
    "1m": Decimal("0.10"),
    "5m": Decimal("0.30"),
    "15m": Decimal("0.25"),
    "1h": Decimal("0.20"),
    "1d": Decimal("0.15"),
}

REQUIRED_TIMEFRAMES = {"5m", "15m", "1h"}


@dataclass(frozen=True, slots=True)
class MultiTimeframeConfirmationOptions:
    engine_code: str = "THESIS_PULSE_MULTI_TIMEFRAME_CONFIRMATION"
    engine_version: str = "1.0.0"
    policy_version: str = "multi-timeframe-confirmation-v1.0.0"
    strong_threshold: Decimal = Decimal("0.65")
    directional_threshold: Decimal = Decimal("0.25")
    minimum_coverage: Decimal = Decimal("0.75")
    maximum_contradiction: Decimal = Decimal("0.40")

    def validate(self) -> None:
        if not self.engine_code.strip():
            raise ValueError("engine_code is required")
        if not self.engine_version.strip():
            raise ValueError("engine_version is required")
        if not self.policy_version.strip():
            raise ValueError("policy_version is required")
        if not ZERO < self.directional_threshold < self.strong_threshold <= ONE:
            raise ValueError("Confirmation thresholds are invalid")
        if not ZERO < self.minimum_coverage <= ONE:
            raise ValueError("minimum_coverage must be between 0 and 1")
        if not ZERO <= self.maximum_contradiction <= ONE:
            raise ValueError("maximum_contradiction must be between 0 and 1")
