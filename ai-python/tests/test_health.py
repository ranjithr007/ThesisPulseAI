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
