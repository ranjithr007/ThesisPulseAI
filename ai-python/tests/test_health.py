from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def test_liveness_returns_healthy() -> None:
    response = client.get("/health/live")

    assert response.status_code == 200
    assert response.json() == {"status": "Healthy"}
    assert response.headers["X-Correlation-ID"]


def test_info_is_locked_to_paper_mode() -> None:
    response = client.get("/info")
    payload = response.json()

    assert response.status_code == 200
    assert payload["environment"] == "PAPER"
    assert payload["liveExecutionEnabled"] is False
    assert payload["contractVersion"] == "v1"
    assert payload["featureFactoryEnabled"] is False
    assert payload["featureFactoryProvider"] == "InMemory"
    assert payload["optionChainEngineEnabled"] is False
    assert payload["optionChainProvider"] == "InMemory"


def test_mock_signal_uses_versioned_paper_metadata() -> None:
    response = client.post("/api/v1/signals/mock", json={})
    payload = response.json()

    assert response.status_code == 200
    assert payload["metadata"]["eventType"] == "signal.generated.v1"
    assert payload["metadata"]["contractVersion"] == "1.0.0"
    assert payload["metadata"]["environment"] == "PAPER"
    assert payload["payload"]["primaryTimeframe"] == "5m"


def test_feature_factory_status_is_safe_when_disabled() -> None:
    response = client.get("/api/v1/features/status")
    payload = response.json()

    assert response.status_code == 200
    assert payload["enabled"] is False
    assert payload["provider"] == "InMemory"
    assert payload["featureSetVersion"] == "feature-set-v1.0.0"


def test_option_chain_status_is_safe_when_disabled() -> None:
    response = client.get("/api/v1/intelligence/option-chain/status")
    payload = response.json()

    assert response.status_code == 200
    assert payload["enabled"] is False
    assert payload["provider"] == "InMemory"
    assert payload["selectionAuthority"] is False
    assert payload["canCreateSignals"] is False
    assert payload["canExecuteOrders"] is False


def test_option_chain_internal_intake_fails_closed_when_disabled() -> None:
    response = client.post(
        "/internal/v1/market-data/option-chain",
        json={
            "sourceMessageUid": "00000000-0000-0000-0000-000000000001",
            "snapshotUid": "00000000-0000-0000-0000-000000000002",
            "underlyingInstrumentKey": "NSE_INDEX|Nifty 50",
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
                        "00000000-0000-0000-0000-000000000003"
                    ),
                    "instrumentKey": "NSE_FO|NIFTY-CALL",
                    "expiryDate": "2026-07-30",
                    "strikePrice": "25000",
                    "optionType": "CALL",
                    "lastPrice": "100",
                    "volumeQuantity": "10",
                    "openInterest": "100",
                    "impliedVolatility": "0.20",
                    "contractMultiplier": "75",
                    "qualityStatus": "VALID",
                }
            ],
            "calculationSourceVersion": "provider-option-chain-v1",
        },
        headers={"X-ThesisPulse-Internal-Key": "not-used-while-disabled"},
    )

    assert response.status_code == 503
    assert response.json()["detail"] == "Option-Chain Intelligence Engine is disabled"
