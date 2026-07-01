from datetime import date, datetime
from decimal import Decimal
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator
from pydantic.alias_generators import to_camel


class OptionChainContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class OptionChainOiWallV1(OptionChainContractModel):
    expiry_date: date
    option_type: Literal["CALL", "PUT"]
    role: Literal["RESISTANCE", "SUPPORT"]
    strike_price: Decimal = Field(gt=0)
    open_interest: Decimal = Field(ge=0)
    same_side_oi_share: Decimal = Field(ge=0, le=1)
    wall_strength: Decimal = Field(ge=0, le=1)
    distance_fraction: Decimal = Field(ge=0)
    rank: int = Field(ge=1)

    @model_validator(mode="after")
    def validate_role(self) -> "OptionChainOiWallV1":
        expected = "RESISTANCE" if self.option_type == "CALL" else "SUPPORT"
        if self.role != expected:
            raise ValueError(f"{self.option_type} walls must use role {expected}")
        return self


class OptionChainOiFlowV1(OptionChainContractModel):
    derivative_contract_uid: UUID
    instrument_key: str = Field(min_length=1, max_length=200)
    expiry_date: date
    option_type: Literal["CALL", "PUT"]
    strike_price: Decimal = Field(gt=0)
    previous_premium: Decimal | None = Field(default=None, ge=0)
    current_premium: Decimal | None = Field(default=None, ge=0)
    previous_open_interest: Decimal | None = Field(default=None, ge=0)
    current_open_interest: Decimal | None = Field(default=None, ge=0)
    premium_change_fraction: Decimal | None = None
    open_interest_change_fraction: Decimal | None = None
    state: Literal[
        "LONG_BUILDUP",
        "SHORT_BUILDUP",
        "SHORT_COVERING",
        "LONG_UNWINDING",
        "FLAT_OR_UNKNOWN",
    ]
    normalized_contribution: Decimal = Field(ge=-1, le=1)

    @model_validator(mode="after")
    def validate_changes(self) -> "OptionChainOiFlowV1":
        if self.premium_change_fraction is not None and (
            self.previous_premium is None or self.current_premium is None
        ):
            raise ValueError("Premium change requires previous and current premiums")
        if self.open_interest_change_fraction is not None and (
            self.previous_open_interest is None or self.current_open_interest is None
        ):
            raise ValueError("OI change requires previous and current OI")
        return self


class OptionChainIvTermPointV1(OptionChainContractModel):
    snapshot_uid: UUID
    expiry_date: date
    days_to_expiry: int = Field(ge=0)
    atm_strike_price: Decimal = Field(gt=0)
    call_implied_volatility: Decimal = Field(ge=0)
    put_implied_volatility: Decimal = Field(ge=0)
    atm_implied_volatility: Decimal = Field(ge=0)
    pair_method: Literal["EXACT_ATM", "NEAREST_MATCHED_PAIR"]


class OptionChainEvidenceV1(OptionChainContractModel):
    code: str = Field(min_length=1, max_length=100)
    message: str = Field(min_length=1, max_length=1000)
    impact: Literal["SUPPORTS_LONG", "SUPPORTS_SHORT", "NEUTRAL"]
    raw_value: Decimal | None = None
    normalized_value: Decimal = Field(ge=-1, le=1)
    weight: Decimal = Field(ge=0, le=1)
    contribution: Decimal = Field(ge=-1, le=1)
    confidence: Decimal = Field(ge=0, le=1)
    warnings: list[str] = Field(default_factory=list)


class OptionChainMaxPainPointV1(OptionChainContractModel):
    settlement_strike: Decimal = Field(gt=0)
    call_payout: Decimal = Field(ge=0)
    put_payout: Decimal = Field(ge=0)
    total_payout: Decimal = Field(ge=0)


class OptionChainExpiryMetricsV1(OptionChainContractModel):
    snapshot_uid: UUID
    expiry_date: date
    underlying_price: Decimal = Field(gt=0)
    call_open_interest: Decimal = Field(ge=0)
    put_open_interest: Decimal = Field(ge=0)
    pcr_open_interest: Decimal | None = Field(default=None, ge=0)
    call_volume: Decimal = Field(ge=0)
    put_volume: Decimal = Field(ge=0)
    pcr_volume: Decimal | None = Field(default=None, ge=0)
    call_walls: list[OptionChainOiWallV1] = Field(default_factory=list)
    put_walls: list[OptionChainOiWallV1] = Field(default_factory=list)
    oi_flows: list[OptionChainOiFlowV1] = Field(default_factory=list)
    max_pain_strike: Decimal | None = Field(default=None, gt=0)
    max_pain_distance_fraction: Decimal | None = None
    max_pain_magnet_strength: Decimal | None = Field(default=None, ge=0, le=1)
    max_pain_curve: list[OptionChainMaxPainPointV1] = Field(default_factory=list)
    atm_call_implied_volatility: Decimal | None = Field(default=None, ge=0)
    atm_put_implied_volatility: Decimal | None = Field(default=None, ge=0)
    atm_put_call_skew: Decimal | None = None
    rr25_skew: Decimal | None = None
    accepted_contract_count: int = Field(ge=0)
    accepted_strike_count: int = Field(ge=0)
    component_coverage: Decimal = Field(ge=0, le=1)
    warnings: list[str] = Field(default_factory=list)


class OptionChainIntelligenceOutputV1(OptionChainContractModel):
    output_uid: UUID
    message_uid: UUID
    source_snapshot_uids: list[UUID] = Field(min_length=1)
    underlying_instrument_key: str = Field(min_length=1, max_length=200)
    as_of_utc: datetime
    generated_at_utc: datetime
    engine_code: str = Field(min_length=1, max_length=100)
    engine_version: str = Field(min_length=1, max_length=50)
    policy_version: str = Field(min_length=1, max_length=100)
    direction: Literal["LONG", "SHORT", "NEUTRAL"]
    score: Decimal = Field(ge=-1, le=1)
    confidence: Decimal = Field(ge=0, le=1)
    expiry_metrics: list[OptionChainExpiryMetricsV1] = Field(min_length=1)
    iv_term_structure: list[OptionChainIvTermPointV1] = Field(default_factory=list)
    near_to_next_iv_slope: Decimal | None = None
    near_to_far_iv_slope: Decimal | None = None
    iv_term_structure_state: Literal[
        "CONTANGO", "BACKWARDATION", "FLAT", "INSUFFICIENT"
    ]
    input_snapshot_count: int = Field(ge=1)
    accepted_contract_count: int = Field(ge=0)
    accepted_strike_count: int = Field(ge=0)
    component_coverage: Decimal = Field(ge=0, le=1)
    data_quality_status: Literal["VALID", "DEGRADED", "INVALID"]
    is_stale: bool
    is_eligible_for_fusion: bool
    revision: int = Field(ge=0)
    evidence: list[OptionChainEvidenceV1]
    warnings: list[str] = Field(default_factory=list)
    selection_authority: Literal[False] = False
    execution_authority: Literal[False] = False

    @model_validator(mode="after")
    def validate_output(self) -> "OptionChainIntelligenceOutputV1":
        if self.generated_at_utc < self.as_of_utc:
            raise ValueError("generatedAtUtc cannot precede asOfUtc")
        if self.input_snapshot_count != len(self.source_snapshot_uids):
            raise ValueError("inputSnapshotCount must equal sourceSnapshotUids length")
        if self.is_eligible_for_fusion and (
            self.data_quality_status != "VALID"
            or self.is_stale
            or self.direction == "NEUTRAL"
        ):
            raise ValueError(
                "Fusion eligibility requires VALID, fresh, non-neutral output"
            )
        if self.iv_term_structure_state != "INSUFFICIENT" and len(self.iv_term_structure) < 2:
            raise ValueError("Term structure classification requires at least two points")
        return self


class OptionChainProcessingResultV1(OptionChainContractModel):
    outcome: Literal[
        "CREATED",
        "REVISED",
        "DUPLICATE",
        "IGNORED_INELIGIBLE",
    ]
    output: OptionChainIntelligenceOutputV1 | None = None
    reason: str | None = None
