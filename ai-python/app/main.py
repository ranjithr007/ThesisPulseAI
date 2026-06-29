from datetime import UTC, datetime
from uuid import uuid4

from fastapi import FastAPI, Request

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
