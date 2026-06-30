import os
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Settings:
    service_name: str = "ThesisPulse.AI"
    service_version: str = "0.3.0"
    contract_version: str = "v1"
    configuration_version: str = "directional-intelligence-v1.0.0"
    environment: str = "PAPER"
    live_execution_enabled: bool = False
    feature_factory_enabled: bool = False
    feature_factory_internal_api_key: str | None = None
    feature_factory_provider: str = "InMemory"
    operational_database_connection_string: str | None = None
    feature_set_version: str = "feature-set-v1.0.0"
    feature_version: str = "1.0.0"
    feature_required_input_count: int = 21
    feature_maximum_input_count: int = 64
    feature_factory_engine_code: str = "THESIS_PULSE_FEATURE_FACTORY"
    feature_factory_actor: str = "ThesisPulse.AI.FeatureFactory"
    feature_factory_broker_code: str = "UPSTOX"
    directional_engine_enabled: bool = False
    directional_engine_code: str = "THESIS_PULSE_TECHNICAL_DIRECTION"
    directional_engine_version: str = "1.0.0"
    directional_policy_version: str = "technical-direction-v1.0.0"
    directional_engine_actor: str = "ThesisPulse.AI.Directional"
    sql_command_timeout_seconds: int = 30


def load_settings() -> Settings:
    environment = os.getenv("THESISPULSE_ENVIRONMENT", "PAPER").upper()
    live_execution_enabled = _read_bool(
        "THESISPULSE_LIVE_EXECUTION_ENABLED",
        False,
    )

    if environment != "PAPER" or live_execution_enabled:
        raise RuntimeError(
            "ThesisPulse AI must run in PAPER mode with live execution disabled"
        )

    feature_enabled = _read_bool("THESISPULSE_FEATURE_FACTORY_ENABLED", False)
    directional_enabled = _read_bool(
        "THESISPULSE_DIRECTIONAL_ENGINE_ENABLED",
        False,
    )
    internal_key = _optional("THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY")
    provider = os.getenv(
        "THESISPULSE_FEATURE_FACTORY_PROVIDER",
        "InMemory",
    ).strip()
    connection_string = _optional("THESISPULSE_OPERATIONAL_DATABASE")
    required_input_count = _read_int(
        "THESISPULSE_FEATURE_REQUIRED_INPUT_COUNT",
        21,
        minimum=21,
        maximum=5000,
    )
    maximum_input_count = _read_int(
        "THESISPULSE_FEATURE_MAXIMUM_INPUT_COUNT",
        64,
        minimum=required_input_count,
        maximum=5000,
    )
    command_timeout = _read_int(
        "THESISPULSE_SQL_COMMAND_TIMEOUT_SECONDS",
        30,
        minimum=1,
        maximum=300,
    )

    if provider.casefold() not in {"inmemory", "sqlserver"}:
        raise RuntimeError(
            "THESISPULSE_FEATURE_FACTORY_PROVIDER must be InMemory or SqlServer"
        )
    if feature_enabled and not internal_key:
        raise RuntimeError(
            "Feature Factory requires THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY"
        )
    if directional_enabled and not feature_enabled:
        raise RuntimeError(
            "Directional intelligence requires the Feature Factory to be enabled"
        )
    if provider.casefold() == "sqlserver" and not connection_string:
        raise RuntimeError(
            "SqlServer intelligence requires THESISPULSE_OPERATIONAL_DATABASE"
        )

    return Settings(
        configuration_version=os.getenv(
            "THESISPULSE_CONFIGURATION_VERSION",
            "directional-intelligence-v1.0.0",
        ),
        environment=environment,
        live_execution_enabled=False,
        feature_factory_enabled=feature_enabled,
        feature_factory_internal_api_key=internal_key,
        feature_factory_provider=(
            "SqlServer" if provider.casefold() == "sqlserver" else "InMemory"
        ),
        operational_database_connection_string=connection_string,
        feature_set_version=os.getenv(
            "THESISPULSE_FEATURE_SET_VERSION",
            "feature-set-v1.0.0",
        ),
        feature_version=os.getenv(
            "THESISPULSE_FEATURE_VERSION",
            "1.0.0",
        ),
        feature_required_input_count=required_input_count,
        feature_maximum_input_count=maximum_input_count,
        feature_factory_engine_code=os.getenv(
            "THESISPULSE_FEATURE_FACTORY_ENGINE_CODE",
            "THESIS_PULSE_FEATURE_FACTORY",
        ),
        feature_factory_actor=os.getenv(
            "THESISPULSE_FEATURE_FACTORY_ACTOR",
            "ThesisPulse.AI.FeatureFactory",
        ),
        feature_factory_broker_code=os.getenv(
            "THESISPULSE_FEATURE_FACTORY_BROKER_CODE",
            "UPSTOX",
        ),
        directional_engine_enabled=directional_enabled,
        directional_engine_code=os.getenv(
            "THESISPULSE_DIRECTIONAL_ENGINE_CODE",
            "THESIS_PULSE_TECHNICAL_DIRECTION",
        ),
        directional_engine_version=os.getenv(
            "THESISPULSE_DIRECTIONAL_ENGINE_VERSION",
            "1.0.0",
        ),
        directional_policy_version=os.getenv(
            "THESISPULSE_DIRECTIONAL_POLICY_VERSION",
            "technical-direction-v1.0.0",
        ),
        directional_engine_actor=os.getenv(
            "THESISPULSE_DIRECTIONAL_ENGINE_ACTOR",
            "ThesisPulse.AI.Directional",
        ),
        sql_command_timeout_seconds=command_timeout,
    )


def _read_bool(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    normalized = value.strip().casefold()
    if normalized in {"true", "1", "yes", "on"}:
        return True
    if normalized in {"false", "0", "no", "off"}:
        return False
    raise RuntimeError(f"{name} must be a boolean value")


def _read_int(name: str, default: int, *, minimum: int, maximum: int) -> int:
    raw = os.getenv(name)
    try:
        value = default if raw is None else int(raw)
    except ValueError as exception:
        raise RuntimeError(f"{name} must be an integer") from exception
    if value < minimum or value > maximum:
        raise RuntimeError(f"{name} must be between {minimum} and {maximum}")
    return value


def _optional(name: str) -> str | None:
    value = os.getenv(name)
    if value is None:
        return None
    normalized = value.strip()
    return normalized or None
