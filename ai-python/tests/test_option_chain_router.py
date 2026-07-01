from decimal import Decimal
from urllib.parse import quote

from fastapi import FastAPI
from fastapi.testclient import TestClient

from app.option_chain.config import OptionChainRuntimeSettings
from app.option_chain.router import create_option_chain_router
from app.option_chain.service import OptionChainIntelligenceService

UNDERLYING = "NSE_INDEX|Nifty 50"
INTERNAL_KEY = "option-chain-test-key"


def test_enabled_router_processes_and_reads_option_chain_output() -> None:
    client = _client()

    response = client.post(
        "/internal/v1/market-data/option-chain",
        json=_snapshot_payload(),
        headers={"X-ThesisPulse-Internal-Key": INTERNAL_KEY},
    )

    assert response.status_code == 200
    payload = response.json()
    assert payload["outcome"] == "CREATED"
    assert payload["output"]["executionAuthority"] is False
    assert payload["output"]["selectionAuthority"] is False

    latest = client.get(
        f"/api/v1/intelligence/option-chain/latest/{quote(UNDERLYING, safe='')}"
        "?expiryDate=2026-07-30"
    )

    assert latest.status_code == 200
    assert latest.json()["underlyingInstrumentKey"] == UNDERLYING
    assert latest.json()["expiryMetrics"][0]["pcrOpenInterest"] == "1.200000"


def test_enabled_router_rejects_invalid_internal_key() -> None:
    client = _client()

    response = client.post(
        "/internal/v1/market-data/option-chain",
        json=_snapshot_payload(),
        headers={"X-ThesisPulse-Internal-Key": "wrong-key"},
    )

    assert response.status_code == 401
    assert response.json()["detail"] == "Unauthorized"


def test_status_preserves_no_execution_authority() -> None:
    client = _client()

    response = client.get("/api/v1/intelligence/option-chain/status")
    payload = response.json()

    assert response.status_code == 200
    assert payload["enabled"] is True
    assert payload["selectionAuthority"] is False
    assert payload["canCreateSignals"] is False
    assert payload["canExecuteOrders"] is False


def _client() -> TestClient:
    service = OptionChainIntelligenceService(runtime=_runtime())
    app = FastAPI()
    app.include_router(create_option_chain_router(service))
    return TestClient(app)


def _runtime() -> OptionChainRuntimeSettings:
    return OptionChainRuntimeSettings(
        enabled=True,
        provider="InMemory",
        engine_code="THESIS_PULSE_OPTION_CHAIN_INTELLIGENCE",
        engine_version="1.0.0",
        policy_version="option-chain-intelligence-v1.0.0",
        actor="tests.option-chain",
        internal_api_key=INTERNAL_KEY,
        maximum_output_age_seconds=3600,
        minimum_contract_count=2,
        minimum_strike_count=1,
        oi_wall_count=1,
        oi_wall_moneyness_fraction=Decimal("0.15"),
        minimum_premium_change_fraction=Decimal("0.0025"),
        minimum_open_interest_change_fraction=Decimal("0.01"),
        directional_threshold=Decimal("0.01"),
        fusion_confidence_threshold=Decimal("0.01"),
    )


def _snapshot_payload() -> dict[str, object]:
    return {
        "sourceMessageUid": "00000000-0000-0000-0000-000000000101",
        "snapshotUid": "00000000-0000-0000-0000-000000000102",
        "underlyingInstrumentKey": UNDERLYING,
        "expiryDate": "2026-07-30",
        "eventAtUtc": "2026-07-01T09:15:00Z",
        "receivedAtUtc": "2026-07-01T09:15:01Z",
        "underlyingPrice": "25000",
        "snapshotStatus": "COMPLETE",
        "qualityStatus": "VALID",
        "isPointInTimeEligible": True,
        "revision": 0,
        "entries": [
            {
                "derivativeContractUid": (
                    "00000000-0000-0000-0000-000000000103"
                ),
                "instrumentKey": "NSE_FO|NIFTY-CALL",
                "expiryDate": "2026-07-30",
                "strikePrice": "25000",
                "optionType": "CALL",
                "lastPrice": "100",
                "volumeQuantity": "100",
                "openInterest": "100",
                "impliedVolatility": "0.20",
                "delta": "0.50",
                "contractMultiplier": "75",
                "qualityStatus": "VALID",
                "greeksSourceVersion": "provider-greeks-v1",
            },
            {
                "derivativeContractUid": (
                    "00000000-0000-0000-0000-000000000104"
                ),
                "instrumentKey": "NSE_FO|NIFTY-PUT",
                "expiryDate": "2026-07-30",
                "strikePrice": "25000",
                "optionType": "PUT",
                "lastPrice": "110",
                "volumeQuantity": "110",
                "openInterest": "120",
                "impliedVolatility": "0.22",
                "delta": "-0.50",
                "contractMultiplier": "75",
                "qualityStatus": "VALID",
                "greeksSourceVersion": "provider-greeks-v1",
            },
        ],
        "calculationSourceVersion": "provider-option-chain-v1",
    }
