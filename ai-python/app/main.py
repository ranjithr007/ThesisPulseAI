import hmac
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from uuid import uuid4

from fastapi import FastAPI, Header, HTTPException, Request
from fastapi.responses import JSONResponse

from app.confirmation.service import MultiTimeframeConfirmationService
from app.contracts.v1.confirmation import MultiTimeframeConfirmationOutputV1
from app.contracts.v1.directional import DirectionalEngineOutputV1
from app.contracts.v1.liquidity_derivatives import (
    LiquidityDerivativesContextOutputV1,
)
from app.contracts.v1.market_data import (
    FeatureProcessingResultV1,
    FeatureSnapshotV1,
    MarketCandleDeliveryV1,
    MarketQuoteDeliveryV1,
    QuoteProcessingResponseV1,
)
from app.contracts.v1.order_flow import OrderFlowEngineOutputV1
from app.contracts.v1.regime import MarketRegimeOutputV1
from app.contracts.v1.signals import (
    MessageMetadataV1,
    MockSignalRequest,
    SignalEnvelopeV1,
    SignalEvidenceV1,
    SignalGeneratedV1,
)
from app.contracts.v1.smart_money import SmartMoneyConceptsOutputV1
from app.core.settings import load_settings
from app.directional.service import DirectionalIntelligenceService
from app.features.service import FeatureFactoryService
from app.liquidity_derivatives.service import (
    LiquidityDerivativesContextService,
)
from app.option_chain.router import create_option_chain_router
from app.option_chain.service import OptionChainIntelligenceService
from app.order_flow.service import OrderFlowService
from app.regime.service import MarketRegimeService
from app.smart_money.service import SmartMoneyConceptsService

settings = load_settings()
directional_intelligence = DirectionalIntelligenceService(settings)
market_regime = MarketRegimeService(settings)
order_flow = OrderFlowService(settings)
smart_money = SmartMoneyConceptsService(settings)
liquidity_derivatives = LiquidityDerivativesContextService(settings)
option_chain = OptionChainIntelligenceService()
multi_timeframe_confirmation = MultiTimeframeConfirmationService(
    settings,
    directional_intelligence,
    market_regime,
)
feature_factory = FeatureFactoryService(
    settings,
    directional_service=directional_intelligence,
    regime_service=market_regime,
    confirmation_service=multi_timeframe_confirmation,
    order_flow_service=order_flow,
    smart_money_service=smart_money,
    liquidity_derivatives_service=liquidity_derivatives,
)
started_at_utc = datetime.now(UTC)

app = FastAPI(
    title="ThesisPulse AI Platform",
    version=settings.service_version,
    docs_url="/docs",
    redoc_url="/redoc",
)
app.include_router(create_option_chain_router(option_chain))


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
    try:
        if feature_factory.enabled:
            feature_factory.get_status()
            if directional_intelligence.enabled:
                directional_intelligence.get_status()
            if market_regime.enabled:
                market_regime.get_status()
            if order_flow.enabled:
                order_flow.get_status()
            if smart_money.enabled:
                smart_money.get_status()
            if liquidity_derivatives.enabled:
                liquidity_derivatives.get_status()
            if multi_timeframe_confirmation.enabled:
                multi_timeframe_confirmation.get_status()
        if option_chain.enabled:
            option_chain.get_status()
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
        "regimeEngineEnabled": settings.regime_engine_enabled,
        "regimePolicyVersion": settings.regime_policy_version,
        "orderFlowEngineEnabled": settings.order_flow_engine_enabled,
        "orderFlowPolicyVersion": settings.order_flow_policy_version,
        "smartMoneyEngineEnabled": smart_money.enabled,
        "smartMoneyPolicyVersion": smart_money.policy_version,
        "liquidityDerivativesEngineEnabled": liquidity_derivatives.enabled,
        "liquidityDerivativesPolicyVersion": liquidity_derivatives.policy_version,
        "optionChainEngineEnabled": option_chain.enabled,
        "optionChainProvider": option_chain.provider,
        "optionChainPolicyVersion": option_chain.policy_version,
        "confirmationEngineEnabled": settings.confirmation_engine_enabled,
        "confirmationPolicyVersion": settings.confirmation_policy_version,
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
                "engineCode": settings.regime_engine_code,
                "engineRole": "CONTEXT_PROVIDER",
                "enabled": settings.regime_engine_enabled,
                "engineVersion": settings.regime_engine_version,
                "policyVersion": settings.regime_policy_version,
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
            {
                "engineCode": settings.order_flow_engine_code,
                "engineRole": "DIRECTIONAL_VOTER",
                "enabled": settings.order_flow_engine_enabled,
                "engineVersion": settings.order_flow_engine_version,
                "policyVersion": settings.order_flow_policy_version,
                "methodology": "PROXY_TICK_RULE_AND_BOOK_TOTALS",
                "canCreateSignals": False,
                "canExecuteOrders": False,
            },
            {
                "engineCode": smart_money.engine_code,
                "engineRole": "DIRECTIONAL_VOTER",
                "enabled": smart_money.enabled,
                "engineVersion": smart_money.engine_version,
                "policyVersion": smart_money.policy_version,
                "methodology": "CONFIRMED_PIVOT_STRUCTURE_HEURISTIC",
                "canCreateSignals": False,
                "canExecuteOrders": False,
            },
            {
                "engineCode": liquidity_derivatives.engine_code,
                "engineRole": "DIRECTIONAL_VOTER",
                "enabled": liquidity_derivatives.enabled,
                "engineVersion": liquidity_derivatives.engine_version,
                "policyVersion": liquidity_derivatives.policy_version,
                "methodology": "PRICE_POOLS_AND_CANONICAL_OPEN_INTEREST",
                "optionsChainAvailable": False,
                "futuresBasisAvailable": False,
                "canCreateSignals": False,
                "canExecuteOrders": False,
            },
            {
                "engineCode": option_chain.engine_code,
                "engineRole": "DIRECTIONAL_VOTER",
                "enabled": option_chain.enabled,
                "engineVersion": option_chain.engine_version,
                "policyVersion": option_chain.policy_version,
                "methodology": "DETERMINISTIC_CANONICAL_OPTION_CHAIN",
                "selectionAuthority": False,
                "canCreateSignals": False,
                "canExecuteOrders": False,
            },
            {
                "engineCode": settings.confirmation_engine_code,
                "engineRole": "META_CONTROLLER",
                "enabled": settings.confirmation_engine_enabled,
                "engineVersion": settings.confirmation_engine_version,
                "policyVersion": settings.confirmation_policy_version,
                "canCreateSignals": False,
                "canExecuteOrders": False,
            },
        ]
    }


@app.post(
    "/internal/v1/market-data/quotes",
    response_model=QuoteProcessingResponseV1,
    tags=["order-flow"],
)
def process_market_quote(
    delivery: MarketQuoteDeliveryV1,
    internal_key: str | None = Header(
        default=None,
        alias="X-ThesisPulse-Internal-Key",
    ),
) -> QuoteProcessingResponseV1:
    _authorize_feature_factory(internal_key)
    result = order_flow.process_quote(delivery)
    return QuoteProcessingResponseV1(
        stream_position=delivery.stream_position,
        message_uid=delivery.envelope.metadata.message_id,
        order_flow=result,
    )


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


@app.get("/api/v1/intelligence/regime/status", tags=["market-regime"])
def market_regime_status() -> dict[str, object]:
    status = market_regime.get_status()
    return {
        "enabled": market_regime.enabled,
        "provider": status.provider,
        "engineCode": settings.regime_engine_code,
        "engineVersion": settings.regime_engine_version,
        "policyVersion": settings.regime_policy_version,
        "outputCount": status.output_count,
        "latestProcessedAtUtc": status.latest_processed_at_utc,
        "latestError": status.latest_error,
        "canCreateSignals": False,
        "canExecuteOrders": False,
    }


@app.get(
    "/api/v1/intelligence/regime/latest/{instrument_key:path}",
    response_model=MarketRegimeOutputV1,
    tags=["market-regime"],
)
def latest_market_regime(
    instrument_key: str,
    timeframe: str = "5m",
) -> MarketRegimeOutputV1:
    _validate_timeframe(timeframe)
    output = market_regime.get_latest(instrument_key, timeframe)
    if output is None:
        raise HTTPException(status_code=404, detail="Market regime output was not found")
    return output


@app.get(
    "/api/v1/intelligence/directional/status",
    tags=["directional-intelligence"],
)
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


@app.get("/api/v1/intelligence/order-flow/status", tags=["order-flow"])
def order_flow_status() -> dict[str, object]:
    status = order_flow.get_status()
    return {
        "enabled": order_flow.enabled,
        "provider": status.provider,
        "engineCode": settings.order_flow_engine_code,
        "engineVersion": settings.order_flow_engine_version,
        "policyVersion": settings.order_flow_policy_version,
        "methodology": "PROXY_TICK_RULE_AND_BOOK_TOTALS",
        "quoteSampleCount": status.quote_sample_count,
        "outputCount": status.output_count,
        "latestProcessedAtUtc": status.latest_processed_at_utc,
        "latestError": status.latest_error,
        "canCreateSignals": False,
        "canExecuteOrders": False,
    }


@app.get(
    "/api/v1/intelligence/order-flow/latest/{instrument_key:path}",
    response_model=OrderFlowEngineOutputV1,
    tags=["order-flow"],
)
def latest_order_flow_output(
    instrument_key: str,
    timeframe: str = "5m",
) -> OrderFlowEngineOutputV1:
    if timeframe != "5m":
        raise HTTPException(status_code=422, detail="Order Flow V1 supports only 5m")
    output = order_flow.get_latest(instrument_key, timeframe)
    if output is None:
        raise HTTPException(status_code=404, detail="Order Flow output was not found")
    return output


@app.get("/api/v1/intelligence/smart-money/status", tags=["smart-money"])
def smart_money_status() -> dict[str, object]:
    status = smart_money.get_status()
    return {
        "enabled": smart_money.enabled,
        "provider": status.provider,
        "engineCode": smart_money.engine_code,
        "engineVersion": smart_money.engine_version,
        "policyVersion": smart_money.policy_version,
        "methodology": "CONFIRMED_PIVOT_STRUCTURE_HEURISTIC",
        "requiredInputCount": smart_money.required_input_count,
        "maximumInputCount": smart_money.maximum_input_count,
        "candleCount": status.candle_count,
        "outputCount": status.output_count,
        "latestProcessedAtUtc": status.latest_processed_at_utc,
        "latestError": status.latest_error,
        "canCreateSignals": False,
        "canExecuteOrders": False,
    }


@app.get(
    "/api/v1/intelligence/smart-money/latest/{instrument_key:path}",
    response_model=SmartMoneyConceptsOutputV1,
    tags=["smart-money"],
)
def latest_smart_money_output(
    instrument_key: str,
    timeframe: str = "5m",
) -> SmartMoneyConceptsOutputV1:
    if timeframe != "5m":
        raise HTTPException(
            status_code=422,
            detail="Smart Money Concepts V1 supports only 5m",
        )
    output = smart_money.get_latest(instrument_key, timeframe)
    if output is None:
        raise HTTPException(status_code=404, detail="Smart Money output was not found")
    return output


@app.get(
    "/api/v1/intelligence/liquidity-derivatives/status",
    tags=["liquidity-derivatives"],
)
def liquidity_derivatives_status() -> dict[str, object]:
    status = liquidity_derivatives.get_status()
    return {
        "enabled": liquidity_derivatives.enabled,
        "provider": status.provider,
        "engineCode": liquidity_derivatives.engine_code,
        "engineVersion": liquidity_derivatives.engine_version,
        "policyVersion": liquidity_derivatives.policy_version,
        "methodology": "PRICE_POOLS_AND_CANONICAL_OPEN_INTEREST",
        "requiredInputCount": liquidity_derivatives.required_input_count,
        "maximumInputCount": liquidity_derivatives.maximum_input_count,
        "candleCount": status.candle_count,
        "outputCount": status.output_count,
        "optionsChainAvailable": False,
        "futuresBasisAvailable": False,
        "latestProcessedAtUtc": status.latest_processed_at_utc,
        "latestError": status.latest_error,
        "canCreateSignals": False,
        "canExecuteOrders": False,
    }


@app.get(
    "/api/v1/intelligence/liquidity-derivatives/latest/{instrument_key:path}",
    response_model=LiquidityDerivativesContextOutputV1,
    tags=["liquidity-derivatives"],
)
def latest_liquidity_derivatives_output(
    instrument_key: str,
    timeframe: str = "5m",
) -> LiquidityDerivativesContextOutputV1:
    if timeframe != "5m":
        raise HTTPException(
            status_code=422,
            detail="Liquidity Derivatives Context V1 supports only 5m",
        )
    output = liquidity_derivatives.get_latest(instrument_key, timeframe)
    if output is None:
        raise HTTPException(
            status_code=404,
            detail="Liquidity Derivatives output was not found",
        )
    return output


@app.get(
    "/api/v1/intelligence/confirmation/status",
    tags=["multi-timeframe-confirmation"],
)
def confirmation_status() -> dict[str, object]:
    status = multi_timeframe_confirmation.get_status()
    return {
        "enabled": multi_timeframe_confirmation.enabled,
        "provider": status.provider,
        "engineCode": settings.confirmation_engine_code,
        "engineVersion": settings.confirmation_engine_version,
        "policyVersion": settings.confirmation_policy_version,
        "outputCount": status.output_count,
        "latestProcessedAtUtc": status.latest_processed_at_utc,
        "latestError": status.latest_error,
        "canCreateSignals": False,
        "canExecuteOrders": False,
    }


@app.get(
    "/api/v1/intelligence/confirmation/latest/{instrument_key:path}",
    response_model=MultiTimeframeConfirmationOutputV1,
    tags=["multi-timeframe-confirmation"],
)
def latest_confirmation_output(
    instrument_key: str,
) -> MultiTimeframeConfirmationOutputV1:
    output = multi_timeframe_confirmation.get_latest(instrument_key)
    if output is None:
        raise HTTPException(status_code=404, detail="Confirmation output was not found")
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
