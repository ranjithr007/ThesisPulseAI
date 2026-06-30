from dataclasses import dataclass
from decimal import Decimal


@dataclass(frozen=True, slots=True)
class SmcOptions:
    engine_code: str = "THESIS_PULSE_SMC"
    engine_version: str = "1.0.0"
    policy_version: str = "smc-structure-v1.0.0"
    required_input_count: int = 12
    maximum_input_count: int = 64
    swing_left_bars: int = 2
    swing_right_bars: int = 2
    minimum_break_fraction: Decimal = Decimal("0.0005")
    directional_threshold: Decimal = Decimal("0.20")
    fusion_confidence_threshold: Decimal = Decimal("0.55")
    structure_weight: Decimal = Decimal("0.50")
    liquidity_weight: Decimal = Decimal("0.20")
    order_block_weight: Decimal = Decimal("0.15")
    fair_value_gap_weight: Decimal = Decimal("0.15")

    def validate(self) -> None:
        if self.required_input_count < 7:
            raise ValueError("required_input_count must be at least seven")
        if self.maximum_input_count < self.required_input_count:
            raise ValueError("maximum_input_count must cover required_input_count")
        if self.swing_left_bars < 1 or self.swing_right_bars < 1:
            raise ValueError("swing windows must be positive")
        if self.minimum_break_fraction <= Decimal("0"):
            raise ValueError("minimum_break_fraction must be positive")
        if not Decimal("0") < self.directional_threshold <= Decimal("1"):
            raise ValueError("directional_threshold must be in (0, 1]")
        if not Decimal("0") <= self.fusion_confidence_threshold <= Decimal("1"):
            raise ValueError("fusion_confidence_threshold must be between zero and one")
        total = (
            self.structure_weight
            + self.liquidity_weight
            + self.order_block_weight
            + self.fair_value_gap_weight
        )
        if total != Decimal("1"):
            raise ValueError("SMC component weights must total one")
