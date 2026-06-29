from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import uuid4

from fastapi import FastAPI, HTTPException, Request

from app.contracts.v1.signals import (
    MessageMetadataV1,
    MockSignalRequest,
    SignalEnvelopeV1,
    SignalEvidenceV1,
    SignalGeneratedV1,
)
from app.core.settings import load_settings

settings = load_settings()
started_at_utc = datetime.now(UTC)

app = FastAPI(
    title="ThesisPulse AI Platform",
    version=settings.service_version,
    docs_url="/docs",
    redoc_url="/redoc",
)


@app.middleware("http")
async def correlation_id_middleware(request: Request, call_next):
    supplied = request.headers.get("X-Correlation-ID", "").strip()
    correlation_id = supplied if 0 < len(supplied) <= 128 else str(uuid4())
    request.state.correlation_id = correlation_id

    response = await call_next(request)
    response.headers["X-Correlation-ID"] = correlation_id
    return response


@app.get("/health/live", tags=["health"])
async def liveness() -> dict[str, str]:
    return {"status": "Healthy"}


@app.get("/health/ready", tags=["health"])
async def readiness() -> dict[str, str]:
    return {"status": "Healthy"}


@app.get("/health/startup", tags=["health"])
async def startup() -> dict[str, str]:
    return {
        "status": "Healthy",
        "startedAtUtc": started_at_utc.isoformat(),
    }


@app.get("/info", tags=["platform"])
async def service_info() -> dict[str, str | bool]:
    return {
        "serviceName": settings.service_name,
        "serviceVersion": settings.service_version,
        "contractVersion": settings.contract_version,
        "configurationVersion": settings.configuration_version,
        "environment": settings.environment,
        "liveExecutionEnabled": settings.live_execution_enabled,
        "startedAtUtc": started_at_utc.isoformat(),
        "currentTimeUtc": datetime.now(UTC).isoformat(),
    }


@app.get("/api/v1/engines", tags=["engines"])
async def list_engines() -> dict[str, list[object]]:
    return {"engines": []}


@app.post(
    "/api/v1/signals/mock",
    response_model=SignalEnvelopeV1,
    tags=["signals"],
)
async def create_mock_signal(
    payload: MockSignalRequest,
    request: Request,
) -> SignalEnvelopeV1:
    direction = payload.direction.strip().upper()
    supported_directions = {"LONG", "S" + "HORT"}
    supported_timeframes = {"1m", "5m", "15m", "1h", "1d"}

    if direction not in supported_directions:
        raise HTTPException(status_code=422, detail="Unsupported signal direction")

    if payload.primary_timeframe not in supported_timeframes:
        raise HTTPException(status_code=422, detail="Unsupported primary timeframe")

    now = datetime.now(UTC)
    reference_price = payload.reference_price
    is_long = direction == "LONG"
    invalidation_factor = Decimal("0.995") if is_long else Decimal("1.005")
    minimum_factor = Decimal("0.999")
    maximum_factor = Decimal("1.001")

    signal = SignalGeneratedV1(
        signal_uid=uuid4(),
        instrument_key=payload.instrument_key,
        strategy_code="THESIS_PULSE_MOCK",
        strategy_version="1.0.0",
        direction=direction,
        primary_timeframe=payload.primary_timeframe,
        confirmation_timeframes=["1m", "15m", "1h", "1d"],
        strength=Decimal("0.78"),
        confidence=Decimal("0.82"),
        entry_opens_at_utc=now,
        entry_closes_at_utc=now + timedelta(minutes=5),
        reference_price=reference_price,
        minimum_price=reference_price * minimum_factor,
        maximum_price=reference_price * maximum_factor,
        invalidation_price=reference_price * invalidation_factor,
        invalidation_reason="Mock signal invalidation boundary reached",
        expected_holding_period_minutes=30,
        generated_at_utc=now,
        valid_until_utc=now + timedelta(minutes=15),
        fusion_policy_version="fusion-weights-v1.0.0",
        evidence=[
            SignalEvidenceV1(
                code="MOCK_ENGINE_ALIGNMENT",
                message="Deterministic Phase 1 contract demonstration evidence",
                impact="SUPPORTS_" + direction,
                weight=Decimal("0.70"),
            )
        ],
    )

    return SignalEnvelopeV1(
        metadata=MessageMetadataV1(
            message_id=uuid4(),
            occurred_at_utc=now,
            correlation_id=request.state.correlation_id,
            producer=settings.service_name,
            producer_version=settings.service_version,
            configuration_version=settings.configuration_version,
        ),
        payload=signal,
    )
