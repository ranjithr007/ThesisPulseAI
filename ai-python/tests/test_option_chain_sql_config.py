from decimal import Decimal

import pytest

from app.option_chain.config import OptionChainRuntimeSettings
from app.option_chain.service import OptionChainIntelligenceService


def test_sql_provider_requires_operational_database(monkeypatch) -> None:
    monkeypatch.setenv("THESISPULSE_OPTION_CHAIN_PROVIDER", "SqlServer")
    monkeypatch.delenv("THESISPULSE_OPERATIONAL_DATABASE", raising=False)

    with pytest.raises(RuntimeError, match="THESISPULSE_OPERATIONAL_DATABASE"):
        OptionChainRuntimeSettings.load()


def test_service_selects_sql_store_without_connecting() -> None:
    runtime = OptionChainRuntimeSettings(
        enabled=True,
        provider="SqlServer",
        engine_code="THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE",
        engine_version="1.0.0",
        policy_version="option-chain-intelligence-v1.0.0",
        actor="tests.option-chain",
        internal_api_key="test-key",
        maximum_output_age_seconds=120,
        minimum_contract_count=2,
        minimum_strike_count=1,
        oi_wall_count=1,
        oi_wall_moneyness_fraction=Decimal("0.15"),
        minimum_premium_change_fraction=Decimal("0.0025"),
        minimum_open_interest_change_fraction=Decimal("0.01"),
        directional_threshold=Decimal("0.20"),
        fusion_confidence_threshold=Decimal("0.60"),
        database_connection_string="test-connection",
    )

    service = OptionChainIntelligenceService(runtime=runtime)

    assert service.provider == "SqlServer"
    assert service.enabled is True
