import hmac
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import uuid4

from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.responses import JSONResponse

from app.contracts.v1.directional import DirectionalEngineOutputV1
from app.contracts.v1.market_data import (
    FeatureProcessingResultV1,
    FeatureSnapshotV1,
    MarketCandleDeliveryV1,
)
from app.contracts.v1.signals import (
    MessageMetadataV1,
    MockSignalRequest,
    SignalEnvelopeV1,
    SignalEvidenceV1,
    SignalGeneratedV1,
)
from app.core.settings import load_settings
from app.directional.service import DirectionalIntelligenceService
from app.features.service import FeatureFactoryService

settings = load_settings()
directional_intelligence = DirectionalIntelligenceService(settings)
feature_factory = FeatureFactoryService(
    settings,
    directional_service=directional_intelligence,
)
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
async def readiness() -> JSONResponse:
    if feature_factory.enabled:
        try:
            feature_factory.get_status()
            if directional_intelligence.enabled:
                directional_intelligence.get_status()
        except Exception as exception:
            return JSONResponse(
                status_code=503,
                content={
                    "status": "Unhealthy",
                    "dependency": "IntelligenceStore",
                    "detail": str(exception)[:500],
                },
            )
    return JSONResponse(status_code=200, content={"status": "Healthy"})


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
        "featureFactoryEnabled": settings.feature_factory_enabled,
        "featureFactoryProvider": settings.feature_factory_provider,
        "featureSetVersion": settings.feature_set_version,
        "directionalEngineEnabled": settings.directional_engine_enabled,
        "directionalPolicyVersion": settings.directional_policy_version,
        "startedAtUtc": started_at_utc.isoformat(),
        "currentTimeUtc": datetime.now(UTC).isoformat(),
    }


@app.get("/api/v1/engines", tags=["engines"])
async def list_engines() -> dict[str, list[object]]:
    return {
        "engines": [
            {
                "engineCode": settings.feature_factory_engine_code,
                "engineRole": "CONTEXT_PROVIDER",
                "enabled": settings.feature_factory_enabled,
                "featureSetVersion": settings.feature_set_version,
                "canCreateSignals": False,
                "canExecuteOrders": False,
            },
            {
                "engineCode": settings.directional_engine_code,
                "engineRole": "DIRECTIONAL_VOTER",
                "enabled": settings.directional_engine_enabled,
                "engineVersion": settings.directional_engine_version,
                "policyVersion": settings.directional_policy_version,
                "canCreateSignals": False,
                "canExecuteOrders": False,
            },
        ]
    }


@app.post(
    "/internal/v1/market-data/candles",
    response_model=FeatureProcessingResultV1,
    tags=["feature-factory"],
)
def process_market_candle(
    delivery: MarketCandleDeliveryV1,
    internal_key: str | None = Header(
        default=None,
        alias="X-ThesisPulse-Internal-Key",
    ),
) -> FeatureProcessingResultV1:
    _authorize_feature_factory(internal_key)
    try:
        return feature_factory.process_candle(delivery)
    except ValueError as exception:
        raise HTTPException(status_code=422, detail=str(exception)) from exception
    except RuntimeError as exception:
        raise HTTPException(status_code=409, detail=str(exception)) from exception


@app.get("/api/v1/features/status", tags=["feature-factory"])
def feature_factory_status() -> dict[str, object]:
    status = feature_factory.get_status()
    return {
        "enabled": feature_factory.enabled,
        "provider": status.provider,
        "featureSetVersion": settings.feature_set_version,
        "requiredInputCount": settings.feature_required_input_count,
        "maximumInputCount": settings.feature_maximum_input_count,
        "processedMessages": status.processed_messages,
        "snapshotCount": status.snapshot_count,
        "latestProcessedAtUtc": status.latest_processed_at_utc,
        "latestError": status.latest_error,
    }


@app.get(
    "/api/v1/features/latest/{instrument_key:path}",
    response_model=FeatureSnapshotV1,
    tags=["feature-factory"],
)
def latest_feature_snapshot(
    instrument_key: str,
    timeframe: str = "5m",
) -> FeatureSnapshotV1:
    _validate_timeframe(timeframe)
    snapshot = feature_factory.get_latest(instrument_key, timeframe)
    if snapshot is None:
        raise HTTPException(status_code=404, detail="Feature snapshot was not found")
    return snapshot


@app.get("/api/v1/intelligence/directional/status", tags=["directional-intelligence"])
def directional_status() -> dict[str, object]:
    status = directional_intelligence.get_status()
    return {
        "enabled": directional_intelligence.enabled,
        "provider": status.provider,
        "engineCode": settings.directional_engine_code,
        "engineVersion": settings.directional_engine_version,
        "policyVersion": settings.directional_policy_version,
        "outputCount": status.output_count,
        "latestProcessedAtUtc": status.latest_processed_at_utc,
        "latestError": status.latest_error,
        "canCreateSignals": False,
        "canExecuteOrders": False,
    }


@app.get(
    "/api/v1/intelligence/directional/latest/{instrument_key:path}",
    response_model=DirectionalEngineOutputV1,
    tags=["directional-intelligence"],
)
def latest_directional_output(
    instrument_key: str,
    timeframe: str = "5m",
) -> DirectionalEngineOutputV1:
    _validate_timeframe(timeframe)
    output = directional_intelligence.get_latest(instrument_key, timeframe)
    if output is None:
        raise HTTPException(status_code=404, detail="Directional output was not found")
    return output


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


def _validate_timeframe(timeframe: str) -> None:
    if timeframe not in {"1m", "5m", "15m", "1h", "1d"}:
        raise HTTPException(status_code=422, detail="Unsupported timeframe")


def _authorize_feature_factory(supplied_key: str | None) -> None:
    if not feature_factory.enabled:
        raise HTTPException(status_code=503, detail="Feature Factory is disabled")
    expected = feature_factory.internal_api_key
    if not supplied_key or not expected or not hmac.compare_digest(supplied_key, expected):
        raise HTTPException(status_code=401, detail="Unauthorized")
