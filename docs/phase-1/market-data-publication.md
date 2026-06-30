# Phase 1 — Canonical Market Data Publication

## Scope

This slice publishes canonical quote and candle changes from Market Data Service to Signal Service and Trading API.

Implemented:

- `market.quote.published.v1`;
- `market.candle.published.v1`;
- transactional SQL outbox creation;
- monotonic stream positions based on `outbox_message_id`;
- inbox-idempotent consumers;
- durable consumer checkpoints;
- protected replay queries;
- Signal Service latest-market buffer;
- Trading API SignalR delivery.

No publication path can submit, modify, or cancel an order.

## Event contracts

Quote events contain the latest traded price, quantity, previous close, open interest, aggregate buy/sell quantities, quality status, freshness usability, timestamps, and source version.

Candle events contain instrument, timeframe, OHLCV, open interest when available, provisional/closed state, revision, quality status, new-exposure usability, timestamps, and source version.

Both contracts use version `1.0` and are wrapped in the canonical `EventEnvelope<TPayload>`.

## Transactional publication

Live quote observations and their outbox records are inserted through the same SQL transaction.

Candle inserts are observed by `market.tr_candles_publish_v1`, so historical and live revisions use the same transactional publication path. The trigger is installed but disabled by default by migration `V0012`.

Enable candle publication only after all three services are configured and healthy:

```sql
ENABLE TRIGGER [market].[tr_candles_publish_v1]
ON [market].[candles];
```

Disable it immediately when publication must stop:

```sql
DISABLE TRIGGER [market].[tr_candles_publish_v1]
ON [market].[candles];
```

Application quote publication is independently controlled by `MarketData:Publication:Enabled`.

## Required shared SQL configuration

Market Data Service, Signal Service, and Trading API must use the same operational SQL Server database and SQL messaging provider:

```powershell
dotnet user-secrets set `
  "ConnectionStrings:OperationalDatabase" `
  "<operational SQL Server connection string>" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "Messaging:Provider" `
  "SqlServer" `
  --project src/ThesisPulse.MarketData.Service
```

Apply the same connection string and `Messaging:Provider=SqlServer` to:

```text
src/ThesisPulse.Signal.Service
src/ThesisPulse.Trading.Api
```

## Internal service key

Generate one internal key and store it independently in all three projects:

```powershell
$marketDataKey = [guid]::NewGuid().ToString("N")

dotnet user-secrets set `
  "MarketData:Publication:InternalApiKey" `
  $marketDataKey `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketDataConsumer:InternalApiKey" `
  $marketDataKey `
  --project src/ThesisPulse.Signal.Service

dotnet user-secrets set `
  "MarketDataConsumer:InternalApiKey" `
  $marketDataKey `
  --project src/ThesisPulse.Trading.Api
```

Never commit this key.

## Enable consumers first

```powershell
dotnet user-secrets set `
  "MarketDataConsumer:Enabled" `
  "true" `
  --project src/ThesisPulse.Signal.Service

dotnet user-secrets set `
  "MarketDataConsumer:Enabled" `
  "true" `
  --project src/ThesisPulse.Trading.Api
```

Consumers reject requests while disabled, even when the correct key is supplied.

## Enable Market Data publication

```powershell
dotnet user-secrets set `
  "MarketData:Publication:Enabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Publication:DispatchEnabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Publication:SignalServiceBaseUrl" `
  "http://localhost:5102" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Publication:TradingApiBaseUrl" `
  "http://localhost:5100" `
  --project src/ThesisPulse.MarketData.Service
```

Start Signal Service and Trading API before enabling the dispatcher.

## Dispatch semantics

The Market Data dispatcher:

1. reads `PENDING` or retryable `FAILED` records whose destination is `MARKET_DATA_FANOUT`;
2. sends the event to Signal Service;
3. sends the same event to Trading API;
4. marks the outbox record `PUBLISHED` only after both calls succeed;
5. marks the record `FAILED` when either call fails.

Duplicate retries are safe because each consumer uses `InboxMessageProcessor` and `operations.inbox_messages`.

Phase 1 assumes one active Market Data dispatcher instance. Multi-instance leasing is deferred to the deployment-hardening slice.

## Consumer checkpoints

`operations.consumer_checkpoints` records the highest successfully processed outbox position for each consumer, stream, and partition.

Checkpoints move forward only. A retry of an older event cannot rewind a consumer.

Consumer identities:

```text
ThesisPulse.Signal.Service.MarketData.v1
ThesisPulse.Trading.Api.MarketData.v1
```

Stream identity:

```text
market-data.v1
```

## Replay

Query published Market Data events after a position:

```powershell
$headers = @{ "X-ThesisPulse-Internal-Key" = $marketDataKey }

Invoke-RestMethod `
  -Method Get `
  -Uri "http://localhost:5101/internal/v1/publications/replay?afterPosition=0&limit=100" `
  -Headers $headers
```

The response includes `lastPosition` and `hasMore`. Use `lastPosition` as the next `afterPosition` value.

Replay retrieval does not alter consumer checkpoints or redeliver events automatically.

## Signal Service endpoints

```text
POST /internal/v1/market-data/quotes
POST /internal/v1/market-data/candles
GET  /api/v1/market-data/consumer/status
GET  /api/v1/market-data/latest/{instrumentKey}
```

The buffer is an operational input seam for later signal-engine processing. It does not generate a signal by itself.

## Trading API SignalR stream

Hub:

```text
/hubs/market-data
```

Client methods:

```text
quoteUpdated
candleUpdated
```

Status:

```text
GET /api/v1/stream/market-data/status
```

## Activation order

1. Apply database migrations.
2. Configure the shared SQL database and SQL messaging provider.
3. Configure the internal key in all three services.
4. Enable and start Signal Service consumer.
5. Enable and start Trading API consumer.
6. Confirm both consumer status endpoints are healthy.
7. Enable Market Data application publication and dispatch.
8. Start Market Data Service.
9. Enable the SQL candle publication trigger.
10. Verify outbox, inbox, checkpoints, and SignalR delivery.

## Safety and rollback

- All publication and consumer switches default to disabled.
- The SQL candle trigger defaults to disabled.
- Consumer endpoints fail closed while disabled.
- Outbox events are immutable.
- Checkpoints are monotonic.
- Invalid Market Data remains rejected before publication.
- Disable dispatch and the candle trigger before changing contracts or endpoints.
