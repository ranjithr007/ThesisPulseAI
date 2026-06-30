from dataclasses import dataclass
from decimal import Decimal


@dataclass(frozen=True, slots=True)
class OrderFlowOptions:
    engine_code: str = "THESIS_PULSE_ORDER_FLOW"
    engine_version: str = "1.0.0"
    policy_version: str = "order-flow-proxy-v1.0.0"
    minimum_quote_samples: int = 10
    minimum_usable_ratio: Decimal = Decimal("0.80")
    minimum_traded_quantity_coverage: Decimal = Decimal("0.02")
    maximum_quote_age_seconds: int = 30
    directional_threshold: Decimal = Decimal("0.20")
    fusion_confidence_threshold: Decimal = Decimal("0.55")
    absorption_flow_threshold: Decimal = Decimal("0.55")
    absorption_price_threshold: Decimal = Decimal("0.0010")
    exhaustion_price_threshold: Decimal = Decimal("0.0020")
    exhaustion_participation_ratio: Decimal = Decimal("0.60")
    book_weight: Decimal = Decimal("0.45")
    tick_delta_weight: Decimal = Decimal("0.40")
    open_interest_weight: Decimal = Decimal("0.15")

    def validate(self) -> None:
        if not self.engine_code.strip():
            raise ValueError("engine_code is required")
        if not self.engine_version.strip():
            raise ValueError("engine_version is required")
        if not self.policy_version.strip():
            raise ValueError("policy_version is required")
        if self.minimum_quote_samples < 2:
            raise ValueError("minimum_quote_samples must be at least two")
        if not Decimal("0") <= self.minimum_usable_ratio <= Decimal("1"):
            raise ValueError("minimum_usable_ratio must be between zero and one")
        if not Decimal("0") <= self.minimum_traded_quantity_coverage <= Decimal("1"):
            raise ValueError(
                "minimum_traded_quantity_coverage must be between zero and one"
            )
        if self.maximum_quote_age_seconds < 1:
            raise ValueError("maximum_quote_age_seconds must be positive")
        if not Decimal("0") < self.directional_threshold <= Decimal("1"):
            raise ValueError("directional_threshold must be in (0, 1]")
        if not Decimal("0") <= self.fusion_confidence_threshold <= Decimal("1"):
            raise ValueError(
                "fusion_confidence_threshold must be between zero and one"
            )
        total_weight = (
            self.book_weight + self.tick_delta_weight + self.open_interest_weight
        )
        if total_weight != Decimal("1"):
            raise ValueError("Order Flow component weights must total one")
