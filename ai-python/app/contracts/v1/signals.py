from datetime import datetime
from decimal import Decimal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class ContractModel(BaseModel):
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        serialize_by_alias=True,
        extra="forbid",
    )


class MessageMetadataV1(ContractModel):
    message_id: UUID
    event_type: str = "signal.generated.v1"
    contract_version: str = "1.0.0"
    occurred_at_utc: datetime
    correlation_id: str
    causation_id: str | None = None
    producer: str
    producer_version: str
    environment: str = "PAPER"
    configuration_version: str
