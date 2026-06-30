from dataclasses import dataclass
from decimal import Decimal

ZERO = Decimal("0")
ONE = Decimal("1")
NEGATIVE_ONE = Decimal("-1")
QUANTUM = Decimal("0.00000001")


@dataclass(frozen=True, slots=True)
class DirectionalEngineOptions:
    engine_code: str = "THESIS_PULSE_TECHNICAL_DIRECTION"
    engine_version: str = "1.0.0"
    policy_version: str = "technical-direction-v1.0.0"
    strong_threshold: Decimal = Decimal("0.65")
    directional_threshold: Decimal = Decimal("0.25")

    def validate(self) -> None:
        if not self.engine_code.strip():
            raise ValueError("engine_code is required")
        if not self.engine_version.strip():
            raise ValueError("engine_version is required")
        if not self.policy_version.strip():
            raise ValueError("policy_version is required")
        if not ZERO < self.directional_threshold < self.strong_threshold <= ONE:
            raise ValueError("Directional thresholds are invalid")


COMPONENT_WEIGHTS: dict[str, Decimal] = {
    "TREND_SCORE": Decimal("0.35"),
    "TREND_SPREAD": Decimal("0.20"),
    "MOMENTUM": Decimal("0.20"),
    "CLOSE_LOCATION": Decimal("0.10"),
    "VOLUME_CONFIRMATION": Decimal("0.10"),
    "SHORT_RETURN": Decimal("0.05"),
}
