import hmac
from datetime import UTC, datetime
from uuid import uuid4

from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.responses import JSONResponse

from app.contracts.v1.market_data import MarketCandleDeliveryV1
from app.contracts.v1.smc import SmcProcessingResultV1, SmartMoneyConceptsOutputV1
from app.core.settings import load_settings
from app.smc.service import SmartMoneyConceptsService

settings = load_settings()
smc = SmartMoneyConceptsService(settings)
started_at_utc = datetime.now(UTC)

app = FastAPI(
    title="ThesisPulse Smart Money Concepts Engine",
    version=smc.options.engine_version,
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
def liveness() -> dict[str, str]:
    return {"status": "Healthy"}


@app.get("/health/ready", tags=["health"])
def readiness() -> JSONResponse:
    if not smc.enabled:
        return JSONResponse(
            status_code=503,
            content={"status": "Unhealthy", "detail": "SMC engine is disabled"},
        )
    try:
        smc.get_status()
    except Exception as exception:
        return JSONResponse(
            status_code=503,
            content={
                "status": "Unhealthy",
                "dependency": "SmcStore",
                "detail": str(exception)[:500],
            },
        )
    return JSONResponse(status_code=200, content={"status": "Healthy"})


@app.get("/info", tags=["platform"])
def service_info() -> dict[str, object]:
    return {
        "serviceName": "ThesisPulse.AI.SMC",
        "engineCode": smc.options.engine_code,
        "engineVersion": smc.options.engine_version,
        "policyVersion": smc.options.policy_version,
        "environment": settings.environment,
        "enabled": smc.enabled,
        "canCreateSignals": False,
        "canExecuteOrders": False,
        "startedAtUtc": started_at_utc,
        "currentTimeUtc": datetime.now(UTC),
    }


@app.post(
    "/internal/v1/smc/candles",
    response_model=SmcProcessingResultV1,
    tags=["smc"],
)
def process_candle(
    delivery: MarketCandleDeliveryV1,
    internal_key: str | None = Header(
        default=None,
        alias="X-ThesisPulse-Internal-Key",
    ),
) -> SmcProcessingResultV1:
    _authorize(internal_key)
    try:
        return smc.process_candle(delivery)
    except ValueError as exception:
        raise HTTPException(status_code=422, detail=str(exception)) from exception
    except RuntimeError as exception:
        raise HTTPException(status_code=409, detail=str(exception)) from exception


@app.get("/api/v1/intelligence/smc/status", tags=["smc"])
def smc_status() -> dict[str, object]:
    status = smc.get_status()
    return {
        "enabled": smc.enabled,
        "provider": status.provider,
        "engineCode": smc.options.engine_code,
        "engineVersion": smc.options.engine_version,
        "policyVersion": smc.options.policy_version,
        "outputCount": status.output_count,
        "latestProcessedAtUtc": status.latest_processed_at_utc,
        "latestError": status.latest_error,
        "canCreateSignals": False,
        "canExecuteOrders": False,
    }


@app.get(
    "/api/v1/intelligence/smc/latest/{instrument_key:path}",
    response_model=SmartMoneyConceptsOutputV1,
    tags=["smc"],
)
def latest_output(
    instrument_key: str,
    timeframe: str = "5m",
) -> SmartMoneyConceptsOutputV1:
    if timeframe != "5m":
        raise HTTPException(status_code=422, detail="SMC V1 supports only 5m")
    output = smc.get_latest(instrument_key, timeframe)
    if output is None:
        raise HTTPException(status_code=404, detail="SMC output was not found")
    return output


def _authorize(supplied_key: str | None) -> None:
    expected = settings.feature_factory_internal_api_key
    if not supplied_key or not expected or not hmac.compare_digest(supplied_key, expected):
        raise HTTPException(status_code=401, detail="Unauthorized")
