from dataclasses import dataclass, field
from decimal import Decimal


@dataclass(frozen=True, slots=True)
class OptionChainIntelligenceOptions:
    engine_code: str = "THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE"
    engine_version: str = "1.0.0"
    policy_version: str = "option-chain-intelligence-v1.0.0"
    maximum_output_age_seconds: int = 120
    minimum_contract_count: int = 10
    minimum_strike_count: int = 5
    minimum_expiry_count_for_term_structure: int = 2
    oi_wall_count: int = 3
    oi_wall_moneyness_fraction: Decimal = Decimal("0.15")
    minimum_premium_change_fraction: Decimal = Decimal("0.0025")
    minimum_open_interest_change_fraction: Decimal = Decimal("0.01")
    pcr_neutral_lower: Decimal = Decimal("0.90")
    pcr_neutral_upper: Decimal = Decimal("1.10")
    pcr_normalization_scale: Decimal = Decimal("1.00")
    directional_threshold: Decimal = Decimal("0.20")
    fusion_confidence_threshold: Decimal = Decimal("0.60")
    iv_flat_relative_slope_threshold: Decimal = Decimal("0.03")
    iv_skew_normalization_scale: Decimal = Decimal("0.25")
    delta_match_tolerance: Decimal = Decimal("0.08")
    atm_pair_strike_tolerance_fraction: Decimal = Decimal("0.02")
    weights: dict[str, Decimal] = field(
        default_factory=lambda: {
            "PCR_OI": Decimal("0.15"),
            "PCR_VOLUME": Decimal("0.10"),
            "CALL_OI_WALL": Decimal("0.10"),
            "PUT_OI_WALL": Decimal("0.10"),
            "CALL_OI_FLOW": Decimal("0.10"),
            "PUT_OI_FLOW": Decimal("0.10"),
            "MAX_PAIN_POSITION": Decimal("0.10"),
            "IV_TERM_STRUCTURE": Decimal("0.15"),
            "IV_ATM_SKEW": Decimal("0.075"),
            "IV_RR25_SKEW": Decimal("0.025"),
        }
    )

    def validate(self) -> None:
        if not self.engine_code.strip():
            raise ValueError("engine_code is required")
        if not self.engine_version.strip() or not self.policy_version.strip():
            raise ValueError("engine_version and policy_version are required")
        if self.maximum_output_age_seconds <= 0:
            raise ValueError("maximum_output_age_seconds must be positive")
        if self.minimum_contract_count < 1 or self.minimum_strike_count < 1:
            raise ValueError("minimum contract and strike counts must be positive")
        if self.minimum_expiry_count_for_term_structure < 2:
            raise ValueError("term structure requires at least two expiries")
        if self.oi_wall_count < 1:
            raise ValueError("oi_wall_count must be positive")
        fractions = (
            self.oi_wall_moneyness_fraction,
            self.minimum_premium_change_fraction,
            self.minimum_open_interest_change_fraction,
            self.pcr_normalization_scale,
            self.directional_threshold,
            self.fusion_confidence_threshold,
            self.iv_flat_relative_slope_threshold,
            self.iv_skew_normalization_scale,
            self.delta_match_tolerance,
            self.atm_pair_strike_tolerance_fraction,
        )
        if any(value <= 0 for value in fractions):
            raise ValueError("configured fractions and thresholds must be positive")
        if self.pcr_neutral_lower <= 0 or self.pcr_neutral_upper <= self.pcr_neutral_lower:
            raise ValueError("PCR neutral band is invalid")
        if self.directional_threshold > 1 or self.fusion_confidence_threshold > 1:
            raise ValueError("direction and confidence thresholds cannot exceed 1")
        if set(self.weights) != {
            "PCR_OI",
            "PCR_VOLUME",
            "CALL_OI_WALL",
            "PUT_OI_WALL",
            "CALL_OI_FLOW",
            "PUT_OI_FLOW",
            "MAX_PAIN_POSITION",
            "IV_TERM_STRUCTURE",
            "IV_ATM_SKEW",
            "IV_RR25_SKEW",
        }:
            raise ValueError("weights must define every V1 component exactly once")
        if any(value < 0 or value > 1 for value in self.weights.values()):
            raise ValueError("component weights must be within [0, 1]")
        if sum(self.weights.values(), Decimal("0")) != Decimal("1"):
            raise ValueError("component weights must sum to 1")
