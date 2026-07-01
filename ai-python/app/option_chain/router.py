import hmac
from datetime import date, datetime
from typing import Annotated

from fastapi import APIRouter, Header, HTTPException, Query

from app.contracts.v1.option_chain import (
    OptionChainIntelligenceOutputV1,
    OptionChainProcessingResultV1,
)
from app.contracts.v1.option_chain_intake import OptionChainSnapshotObservationV1
from app.option_chain.service import OptionChainIntelligenceService


def create_option_chain_router(
    service: OptionChainIntelligenceService,
) -> APIRouter:
    router = APIRouter()

    @router.post(
        "/internal/v1/market-data/option-chain",
        response_model=OptionChainProcessingResultV1,
        tags=["option-chain-intelligence"],
    )
    def process_option_chain_snapshot(
        snapshot: OptionChainSnapshotObservationV1,
        internal_key: str | None = Header(
            default=None,
            alias="X-ThesisPulse-Internal-Key",
        ),
    ) -> OptionChainProcessingResultV1:
        _authorize(service, internal_key)
        try:
            return service.process_snapshot(snapshot)
        except ValueError as exception:
            raise HTTPException(status_code=422, detail=str(exception)) from exception
        except RuntimeError as exception:
            raise HTTPException(status_code=409, detail=str(exception)) from exception

    @router.get(
        "/api/v1/intelligence/option-chain/status",
        tags=["option-chain-intelligence"],
    )
    def option_chain_status() -> dict[str, object]:
        status = service.get_status()
        return {
            "enabled": service.enabled,
            "provider": status.provider,
            "engineCode": service.engine_code,
            "engineVersion": service.engine_version,
            "policyVersion": service.policy_version,
            "methodology": "DETERMINISTIC_CANONICAL_OPTION_CHAIN",
            "snapshotCount": status.snapshot_count,
            "outputCount": status.output_count,
            "latestProcessedAtUtc": status.latest_processed_at_utc,
            "latestError": status.latest_error,
            "selectionAuthority": False,
            "canCreateSignals": False,
            "canExecuteOrders": False,
        }

    @router.get(
        "/api/v1/intelligence/option-chain/term-structure/{underlying_instrument_key:path}",
        response_model=OptionChainIntelligenceOutputV1,
        tags=["option-chain-intelligence"],
    )
    def latest_option_chain_term_structure(
        underlying_instrument_key: str,
        as_of_utc: Annotated[
            datetime | None,
            Query(alias="asOfUtc"),
        ] = None,
    ) -> OptionChainIntelligenceOutputV1:
        output = service.get_latest(
            underlying_instrument_key,
            expiry_date=None,
            as_of_utc=as_of_utc,
        )
        if output is None:
            raise HTTPException(
                status_code=404,
                detail="Option-chain term structure was not found",
            )
        return output

    @router.get(
        "/api/v1/intelligence/option-chain/latest/{underlying_instrument_key:path}",
        response_model=OptionChainIntelligenceOutputV1,
        tags=["option-chain-intelligence"],
    )
    def latest_option_chain_output(
        underlying_instrument_key: str,
        expiry_date: Annotated[
            date | None,
            Query(alias="expiryDate"),
        ] = None,
        as_of_utc: Annotated[
            datetime | None,
            Query(alias="asOfUtc"),
        ] = None,
    ) -> OptionChainIntelligenceOutputV1:
        output = service.get_latest(
            underlying_instrument_key,
            expiry_date=expiry_date,
            as_of_utc=as_of_utc,
        )
        if output is None:
            raise HTTPException(
                status_code=404,
                detail="Option-chain intelligence output was not found",
            )
        return output

    return router


def _authorize(
    service: OptionChainIntelligenceService,
    supplied_key: str | None,
) -> None:
    if not service.enabled:
        raise HTTPException(
            status_code=503,
            detail="Option-Chain Intelligence Engine is disabled",
        )
    expected = service.internal_api_key
    if not supplied_key or not expected or not hmac.compare_digest(supplied_key, expected):
        raise HTTPException(status_code=401, detail="Unauthorized")
