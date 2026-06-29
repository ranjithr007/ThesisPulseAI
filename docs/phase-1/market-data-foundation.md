# Phase 1 — Canonical Market Data Foundation

## Scope

This slice activates the existing Phase 0 market-data storage model and keeps every provider-specific field behind the Upstox adapter boundary.

Implemented:

- canonical instrument, candle, live update and freshness contracts;
- isolated `ThesisPulse.Infrastructure.Brokers.Upstox` project;
- Upstox BOD instrument snapshot parsing;
- canonical instrument and broker-mapping synchronization;
- V3 historical candle retrieval and normalization;
- decoded V3 live-feed normalization;
- immutable source observations;
- normalized candle persistence;
- ingestion batches and cursors;
- persisted freshness and quality assessments;
- in-memory provider for local development;
- protected Market Data operations endpoints.

## Ownership boundary

Provider-specific URLs, authorization headers, field names, instrument keys, decoded feed models and response formats exist only in:

```text
src/ThesisPulse.Infrastructure.Brokers.Upstox
```

`ThesisPulse.MarketData.Service` consumes canonical interfaces from Shared Infrastructure. It cannot submit orders and does not expose the Upstox access token.

## Storage

The implementation reuses the Phase 0 tables:

- `market.data_sources`
- `market.ingestion_batches`
- `market.source_observations`
- `market.candles`
- `market.ingestion_cursors`
- `market.data_quality_assessments`
- `reference.instruments`
- `reference.broker_instrument_mappings`

No duplicate market-data tables are introduced.

## Safety defaults

- Provider operations are disabled by default.
- Persistence defaults to `InMemory`.
- Instruments synchronized from the provider are not automatically granted trading or short-selling permission.
- The live WebSocket transport worker is not started in this slice.
- Decoded live messages can be normalized and persisted through a protected internal endpoint.
- Invalid market data is rejected; stale or incomplete data is retained with explicit quality state.
- New-exposure usability requires valid, closed candle data.

## Local configuration

Generate an internal operations key:

```powershell
$marketDataKey = [guid]::NewGuid().ToString("N")

dotnet user-secrets set `
  "MarketData:Operations:InternalApiKey" `
  $marketDataKey `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Operations:Enabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service
```

For authenticated historical and live-feed authorization calls, store the Upstox token only in user secrets:

```powershell
dotnet user-secrets set `
  "Upstox:AccessToken" `
  "<current-access-token>" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "Upstox:Enabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service
```

Never commit the token.

## SQL Server mode

Apply migrations and the local PAPER seed pack, which now registers:

- broker `UPSTOX`;
- data source `UPSTOX_REST`;
- data source `UPSTOX_WS`.

Then configure:

```powershell
dotnet user-secrets set `
  "ConnectionStrings:OperationalDatabase" `
  "<local SQL Server connection string>" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Persistence:Provider" `
  "SqlServer" `
  --project src/ThesisPulse.MarketData.Service
```

## Run

```powershell
dotnet run `
  --project src/ThesisPulse.MarketData.Service `
  --urls http://localhost:5101
```

Status and read-only query endpoints:

```text
GET http://localhost:5101/api/v1/status
GET http://localhost:5101/api/v1/jobs
GET http://localhost:5101/api/v1/candles?instrumentKey=NSE_INDEX%7CNifty%2050&timeframe=5m&limit=200
```

## Instrument synchronization

```powershell
$headers = @{
    "X-ThesisPulse-Internal-Key" = $marketDataKey
    "X-Correlation-ID" = [guid]::NewGuid().ToString("D")
}

Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:5101/internal/v1/instruments/synchronize" `
  -Headers $headers
```

Instrument synchronization updates reference identity and provider mappings only. It does not activate a trading universe or grant execution permission.

## Historical backfill

```powershell
$body = @{
    providerInstrumentKey = "NSE_INDEX|Nifty 50"
    timeframe = "5m"
    fromDate = "2026-06-25"
    toDate = "2026-06-29"
    correlationId = [guid]::NewGuid().ToString("D")
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri "http://localhost:5101/internal/v1/candles/backfill" `
  -Headers @{ "X-ThesisPulse-Internal-Key" = $marketDataKey } `
  -ContentType "application/json" `
  -Body $body
```

## Live normalization boundary

The live endpoint accepts already-decoded Upstox V3 feed records:

```text
POST /internal/v1/live/normalize-and-ingest
```

The next slice will add the long-running WebSocket worker, protobuf decoding, subscription recovery, heartbeat monitoring and reconnect/backoff behavior. Until that worker exists, this endpoint provides a deterministic integration seam for decoded feed contract testing.

## Freshness policy

Default maximum ages:

| Data | Maximum age |
|---|---:|
| Live update | 5 seconds |
| 1-minute candle | 90 seconds |
| 5-minute candle | 7 minutes |
| 15-minute candle | 20 minutes |
| 1-hour candle | 75 minutes |
| Daily candle | 36 hours |

The exit-usage window is wider than the new-exposure window. Freshness values are configuration and policy-versioned.

## Exit gate

This slice is complete when:

- the solution builds with the isolated Upstox project;
- instrument synchronization creates canonical reference mappings;
- historical candles create batch, observation, candle, quality and cursor records;
- duplicate source events are idempotently ignored;
- decoded live updates create immutable observations and freshness assessments;
- invalid or stale data cannot be silently treated as valid for new exposure;
- all provider operations remain protected and PAPER-only.
