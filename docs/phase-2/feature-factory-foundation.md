# Phase 2 — Point-in-Time Feature Factory Foundation

## Objective

Start deterministic intelligence after the canonical Market Data path. This slice consumes closed candle publications and creates reproducible, versioned, point-in-time feature snapshots with exact SQL candle lineage.

It does not train a model, generate a canonical signal, approve risk, or submit an order.

## Architecture boundary

```text
Upstox adapter
  -> canonical Market Data persistence
  -> market.candle.published.v1
  -> Python Feature Factory
  -> intelligence.engine_runs
  -> intelligence.engine_outputs
  -> intelligence.engine_output_market_inputs
  -> intelligence.engine_output_features
```

The implementation reuses the Phase 0 intelligence schema. It does not create a parallel feature database.

## Initial deterministic feature set

Feature-set version: `feature-set-v1.0.0`

- one-candle and three-candle close returns;
- 5-period and 20-period simple moving averages;
- 5-period and 20-period exponential moving averages;
- 5-period momentum;
- true range and 14-period ATR;
- 20-return realized volatility;
- 20-period average volume and volume ratio;
- close-location value;
- 5/20 trend spread;
- bounded trend score.

All calculations use Python `Decimal`. The Feature Factory does not use current wall-clock market values that arrived after the event cutoff.

## Point-in-time rules

For each published closed candle, SQL Server loads only candle revisions where:

```text
candle.close_at_utc <= published candle close
candle.received_at_utc <= publication occurred_at_utc
```

When multiple revisions exist for one candle opening time, the highest revision available at the cutoff is selected.

Every output stores the exact candle IDs in `intelligence.engine_output_market_inputs`. The latest candle is `PRIMARY`; warm-up candles are `CONTEXT`.

## Quality and eligibility

A feature snapshot is `VALID` only when:

- at least 21 closed candles are available;
- all initial features are calculable;
- no same-session candle gap is detected;
- the source candle quality is `VALID`;
- the event is within the timeframe freshness limit;
- the source candle is closed, non-provisional, and usable for new exposure.

Otherwise the result is `DEGRADED` or `INVALID` and cannot be used by later intelligence engines.

Warnings include:

```text
INSUFFICIENT_WARMUP
CANDLE_GAP_DETECTED
SOURCE_DATA_STALE
SOURCE_QUALITY_<STATUS>
```

## Idempotency and revisions

`operations.inbox_messages` enforces idempotency by consumer and message UID.

A corrected candle publication with a new message UID creates a new current feature revision and supersedes the prior `intelligence.engine_outputs` record for the same engine, instrument, timeframe, and as-of timestamp.

Provisional candles are acknowledged but do not create feature snapshots.

## Engine authority

Seed `S0008` registers:

```text
engine_code: THESIS_PULSE_FEATURE_FACTORY
engine_role: CONTEXT_PROVIDER
owner_service: ThesisPulse.AI
can_create_signals: false
can_execute_orders: false
```

The database constraints and verification script reject any authority drift.

## Python configuration

Defaults are disabled and in-memory:

```text
THESISPULSE_FEATURE_FACTORY_ENABLED=false
THESISPULSE_FEATURE_FACTORY_PROVIDER=InMemory
```

For local SQL Server activation:

```powershell
$featureKey = [guid]::NewGuid().ToString("N")

$env:THESISPULSE_FEATURE_FACTORY_ENABLED = "true"
$env:THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY = $featureKey
$env:THESISPULSE_FEATURE_FACTORY_PROVIDER = "SqlServer"
$env:THESISPULSE_OPERATIONAL_DATABASE = "Driver={ODBC Driver 18 for SQL Server};Server=localhost\SQLEXPRESS;Database=ThesisPulseAI;Trusted_Connection=yes;TrustServerCertificate=yes;"
$env:THESISPULSE_FEATURE_SET_VERSION = "feature-set-v1.0.0"
```

Apply the local PAPER seed pack before starting SQL mode.

Run the AI service:

```powershell
cd ai-python
python -m uvicorn app.main:app --reload --port 8100
```

## Market Data Service fan-out

Configure the same internal key in Market Data Service:

```powershell
dotnet user-secrets set `
  "MarketData:Publication:InternalApiKey" `
  $featureKey `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Publication:AiFeatureFactoryEnabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Publication:AiServiceBaseUrl" `
  "http://localhost:8100" `
  --project src/ThesisPulse.MarketData.Service
```

Only candle publications are sent to the Feature Factory. Quote publications continue to Signal Service and Trading API but are not used by this initial deterministic feature set.

The outbox record is marked published only after every enabled consumer accepts the event.

## APIs

```text
POST /internal/v1/market-data/candles
GET  /api/v1/features/status
GET  /api/v1/features/latest/{instrumentKey}?timeframe=5m
GET  /api/v1/engines
```

The internal intake endpoint requires `X-ThesisPulse-Internal-Key` and returns `503` while the Feature Factory is disabled.

## Activation order

1. Apply all database migrations.
2. Apply the local PAPER seed pack including `S0008`.
3. Synchronize instrument mappings and ingest/backfill at least 21 closed candles.
4. Start Python with SQL Server Feature Factory enabled.
5. Confirm `/health/ready` and `/api/v1/features/status`.
6. Configure the same internal key in Market Data Service.
7. Enable AI Feature Factory fan-out.
8. Enable Market Data publication and the closed-candle publication trigger.
9. Verify inbox, engine run, engine output, feature, warning, and input-lineage records.

## Safety and rollback

- Feature Factory and AI fan-out default to disabled.
- PAPER is the only accepted environment.
- Provisional, stale, incomplete, invalid, or gapped data cannot become eligible.
- Feature Factory has no signal or execution authority.
- Disable `AiFeatureFactoryEnabled` to stop new AI deliveries without affecting Signal Service or Trading API publication.
- No feature weight or production rule is changed automatically after a loss.

## Exit gate

- Python and .NET contracts serialize compatibly.
- Twenty-one sequential valid candles produce a complete eligible snapshot.
- Warm-up, stale, gapped, provisional, duplicate, and corrected inputs behave deterministically.
- SQL writes preserve exact candle lineage and revision supersession.
- Engine authority seed verification passes.
- .NET, Python, React, migration, and executable tests are green.
