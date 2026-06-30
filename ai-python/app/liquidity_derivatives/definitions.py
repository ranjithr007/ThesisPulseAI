from dataclasses import dataclass
from decimal import Decimal


@dataclass(frozen=True, slots=True)
class LiquidityDerivativesOptions:
    engine_code: str = "THESIS_PULSE_LIQUIDITY_DERIVATIVES_CONTEXT"
    engine_version: str = "1.0.0"
    policy_version: str = "liquidity-derivatives-context-v1.0.0"
    required_input_count: int = 30
    maximum_input_count: int = 128
    swing_left_bars: int = 2
    swing_right_bars: int = 2
    pool_cluster_tolerance_fraction: Decimal = Decimal("0.0015")
    pool_half_width_fraction: Decimal = Decimal("0.0005")
    maximum_pools_per_side: int = 5
    derivatives_lookback_bars: int = 6
    minimum_price_change_fraction: Decimal = Decimal("0.0005")
    minimum_open_interest_change_fraction: Decimal = Decimal("0.0020")
    minimum_valid_input_ratio: Decimal = Decimal("0.95")
    maximum_output_age_seconds: int = 420
    directional_threshold: Decimal = Decimal("0.20")
    fusion_confidence_threshold: Decimal = Decimal("0.55")
    liquidity_attraction_weight: Decimal = Decimal("0.35")
    range_location_weight: Decimal = Decimal("0.15")
    derivatives_weight: Decimal = Decimal("0.50")

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
        if not Decimal("0") < self.pool_cluster_tolerance_fraction <= Decimal("0.05"):
            raise ValueError("pool_cluster_tolerance_fraction must be in (0, 0.05]")
        if not Decimal("0") < self.pool_half_width_fraction <= Decimal("0.05"):
            raise ValueError("pool_half_width_fraction must be in (0, 0.05]")
        if self.maximum_pools_per_side < 1:
            raise ValueError("maximum_pools_per_side must be positive")
        if self.derivatives_lookback_bars < 2:
            raise ValueError("derivatives_lookback_bars must be at least two")
        if self.derivatives_lookback_bars > self.maximum_input_count:
            raise ValueError("derivatives_lookback_bars exceeds maximum_input_count")
        if not Decimal("0") <= self.minimum_price_change_fraction <= Decimal("1"):
            raise ValueError("minimum_price_change_fraction must be between zero and one")
        if not Decimal("0") <= self.minimum_open_interest_change_fraction <= Decimal("1"):
            raise ValueError(
                "minimum_open_interest_change_fraction must be between zero and one"
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
        total_weight = (
            self.liquidity_attraction_weight
            + self.range_location_weight
            + self.derivatives_weight
        )
        if total_weight != Decimal("1"):
            raise ValueError("Context component weights must total one")
