import pytest

from app.core.settings import load_settings


def test_confirmation_requires_directional_engine(monkeypatch) -> None:
    _set_base_environment(monkeypatch)
    monkeypatch.setenv("THESISPULSE_REGIME_ENGINE_ENABLED", "true")
    monkeypatch.setenv("THESISPULSE_CONFIRMATION_ENGINE_ENABLED", "true")

    with pytest.raises(RuntimeError, match="requires directional intelligence"):
        load_settings()


def test_confirmation_requires_regime_engine(monkeypatch) -> None:
    _set_base_environment(monkeypatch)
    monkeypatch.setenv("THESISPULSE_DIRECTIONAL_ENGINE_ENABLED", "true")
    monkeypatch.setenv("THESISPULSE_CONFIRMATION_ENGINE_ENABLED", "true")

    with pytest.raises(RuntimeError, match="requires the Market Regime Engine"):
        load_settings()


def test_confirmation_loads_when_all_dependencies_are_enabled(monkeypatch) -> None:
    _set_base_environment(monkeypatch)
    monkeypatch.setenv("THESISPULSE_DIRECTIONAL_ENGINE_ENABLED", "true")
    monkeypatch.setenv("THESISPULSE_REGIME_ENGINE_ENABLED", "true")
    monkeypatch.setenv("THESISPULSE_CONFIRMATION_ENGINE_ENABLED", "true")

    settings = load_settings()

    assert settings.feature_factory_enabled is True
    assert settings.directional_engine_enabled is True
    assert settings.regime_engine_enabled is True
    assert settings.confirmation_engine_enabled is True
    assert settings.live_execution_enabled is False


def _set_base_environment(monkeypatch) -> None:
    monkeypatch.setenv("THESISPULSE_ENVIRONMENT", "PAPER")
    monkeypatch.setenv("THESISPULSE_LIVE_EXECUTION_ENABLED", "false")
    monkeypatch.setenv("THESISPULSE_FEATURE_FACTORY_ENABLED", "true")
    monkeypatch.setenv("THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY", "test-key")
    monkeypatch.setenv("THESISPULSE_FEATURE_FACTORY_PROVIDER", "InMemory")
    monkeypatch.setenv("THESISPULSE_DIRECTIONAL_ENGINE_ENABLED", "false")
    monkeypatch.setenv("THESISPULSE_REGIME_ENGINE_ENABLED", "false")
    monkeypatch.setenv("THESISPULSE_CONFIRMATION_ENGINE_ENABLED", "false")
    monkeypatch.delenv("THESISPULSE_OPERATIONAL_DATABASE", raising=False)
