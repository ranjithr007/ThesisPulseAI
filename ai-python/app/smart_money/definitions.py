from dataclasses import dataclass
from decimal import Decimal


@dataclass(frozen=True, slots=True)
class SmartMoneyOptions:
    engine_code: str = "THESIS_PULSE_SMART_MONEY_CONCEPTS"
    engine_version: str = "1.0.0"
    policy_version: str = "smart-money-structure-v1.0.0"
    required_input_count: int = 30
    maximum_input_count: int = 128
    swing_left_bars: int = 2
    swing_right_bars: int = 2
    order_block_search_bars: int = 8
    maximum_zones_per_type: int = 5
    maximum_zone_age_bars: int = 24
    break_tolerance_fraction: Decimal = Decimal("0.0002")
    minimum_fair_value_gap_fraction: Decimal = Decimal("0.0005")
    minimum_valid_input_ratio: Decimal = Decimal("0.95")
    maximum_output_age_seconds: int = 420
    directional_threshold: Decimal = Decimal("0.20")
    fusion_confidence_threshold: Decimal = Decimal("0.55")
    structure_state_weight: Decimal = Decimal("0.20")
    structure_event_weight: Decimal = Decimal("0.35")
    liquidity_sweep_weight: Decimal = Decimal("0.20")
    order_block_weight: Decimal = Decimal("0.15")
    fair_value_gap_weight: Decimal = Decimal("0.10")

    def validate(self) -> None:
        if not self.engine_code.strip():
            raise ValueError("engine_code is required")
        if not self.engine_version.strip():
            raise ValueError("engine_version is required")
        if not self.policy_version.strip():
            raise ValueError("policy_version is required")
        if self.required_input_count < 10:
            raise ValueError("required_input_count must be at least ten")
        if self.maximum_input_count < self.required_input_count:
            raise ValueError("maximum_input_count cannot be below required_input_count")
        if self.swing_left_bars < 1 or self.swing_right_bars < 1:
            raise ValueError("Swing confirmation bars must be positive")
        if self.order_block_search_bars < 1:
            raise ValueError("order_block_search_bars must be positive")
        if self.maximum_zones_per_type < 1:
            raise ValueError("maximum_zones_per_type must be positive")
        if self.maximum_zone_age_bars < 1:
            raise ValueError("maximum_zone_age_bars must be positive")
        if not Decimal("0") <= self.break_tolerance_fraction <= Decimal("0.05"):
            raise ValueError("break_tolerance_fraction must be between zero and 0.05")
        if not Decimal("0") <= self.minimum_fair_value_gap_fraction <= Decimal("0.05"):
            raise ValueError(
                "minimum_fair_value_gap_fraction must be between zero and 0.05"
            )
        if not Decimal("0") <= self.minimum_valid_input_ratio <= Decimal("1"):
            raise ValueError("minimum_valid_input_ratio must be between zero and one")
        if self.maximum_output_age_seconds < 1:
            raise ValueError("maximum_output_age_seconds must be positive")
        if not Decimal("0") < self.directional_threshold <= Decimal("1"):
            raise ValueError("directional_threshold must be in (0, 1]")
        if not Decimal("0") <= self.fusion_confidence_threshold <= Decimal("1"):
            raise ValueError(
                "fusion_confidence_threshold must be between zero and one"
            )
        weight_total = (
            self.structure_state_weight
            + self.structure_event_weight
            + self.liquidity_sweep_weight
            + self.order_block_weight
            + self.fair_value_gap_weight
        )
        if weight_total != Decimal("1"):
            raise ValueError("Smart Money component weights must total one")
