import os
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Settings:
    service_name: str = "ThesisPulse.AI"
    service_version: str = "0.1.0"
    contract_version: str = "v1"
    configuration_version: str = "platform-foundation-v1.0.0"
    environment: str = "PAPER"
    live_execution_enabled: bool = False


def load_settings() -> Settings:
    environment = os.getenv("THESISPULSE_ENVIRONMENT", "PAPER").upper()
    live_execution_enabled = os.getenv(
        "THESISPULSE_LIVE_EXECUTION_ENABLED",
        "false",
    ).lower() == "true"

    if environment != "PAPER" or live_execution_enabled:
        raise RuntimeError(
            "Phase 1 Python platform must run in PAPER mode with live execution disabled"
        )

    return Settings(
        configuration_version=os.getenv(
            "THESISPULSE_CONFIGURATION_VERSION",
            "platform-foundation-v1.0.0",
        ),
        environment=environment,
        live_execution_enabled=False,
    )
