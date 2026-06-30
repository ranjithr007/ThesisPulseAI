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
