# Phase 1 — Market Data Recovery and Reconciliation

## Scope

This slice adds recovery controls on top of the canonical Upstox Market Data foundation.

Implemented:

- SQL-backed live-feed subscription planning;
- configuration-backed plans for in-memory development;
- deterministic subscription-plan versioning;
- priority ordering and mode validation;
- session-aware closed-candle gap detection;
- weekend suppression;
- periodic REST historical backfill;
- durable gap lifecycle records;
- provisional live-candle revisions;
- closed historical candle reconciliation;
- recovery health telemetry;
- focused executable tests.

## New SQL tables

Migration `V0010__create_market_data_recovery_tables.sql` creates:

- `market.live_feed_subscriptions`
- `market.data_gap_events`

`live_feed_subscriptions` references canonical broker instrument mappings. Provider keys are never duplicated into an unrelated reference table.

`data_gap_events` records:

- instrument and timeframe;
- missing interval;
- expected record count;
- lifecycle status;
- recovery attempts;
- last recovery and error state;
- correlation identity.

## Subscription planning

### In-memory mode

The plan is generated from:

```text
Upstox:LiveFeed:Mode
Upstox:LiveFeed:InstrumentKeys
MarketData:Recovery:Timeframe
```

### SQL Server mode

The plan is generated from active rows in:

```text
market.live_feed_subscriptions
```

The SQL plan includes only:

- active broker mappings;
- active instruments;
- enabled subscriptions;
- subscriptions inside their validity window;
- rows matching the configured feed mode.

The generated version changes whenever instrument, mode, timeframe, or priority changes.

## Create a SQL-backed subscription

Run instrument synchronization first so the Upstox mapping exists. Then insert a subscription through SQL Server:

```sql
DECLARE @mapping_id bigint =
(
    SELECT mapping.broker_instrument_mapping_id
    FROM reference.broker_instrument_mappings mapping
    INNER JOIN reference.brokers broker
        ON broker.broker_id = mapping.broker_id
    WHERE broker.broker_code = 'UPSTOX'
      AND mapping.broker_instrument_key = 'NSE_INDEX|Nifty 50'
      AND mapping.is_active = 1
      AND mapping.valid_to_date IS NULL
);

IF @mapping_id IS NULL
    THROW 62001, 'Upstox instrument mapping was not found.', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM market.live_feed_subscriptions
    WHERE broker_instrument_mapping_id = @mapping_id
      AND feed_mode = 'full'
)
BEGIN
    INSERT INTO market.live_feed_subscriptions
    (
        broker_instrument_mapping_id,
        feed_mode,
        recovery_timeframe,
        priority,
        is_enabled,
        created_by,
        updated_by
    )
    VALUES
    (
        @mapping_id,
        'full',
        '5m',
        10,
        1,
        N'ThesisPulse.LocalSetup',
        N'ThesisPulse.LocalSetup'
    );
END;
```

Add BANK NIFTY, FINNIFTY, or selected equities with different priorities as required.

## Enable recovery

Store the Upstox access token in user secrets:

```powershell
dotnet user-secrets set `
  "Upstox:AccessToken" `
  "<current-access-token>" `
  --project src/ThesisPulse.MarketData.Service
```

Enable SQL persistence and recovery:

```powershell
dotnet user-secrets set `
  "MarketData:Persistence:Provider" `
  "SqlServer" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "MarketData:Recovery:Enabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service

dotnet user-secrets set `
  "Upstox:Enabled" `
  "true" `
  --project src/ThesisPulse.MarketData.Service
```

The live worker may also be enabled. In SQL mode, instrument keys are loaded from `market.live_feed_subscriptions`; duplicate key configuration is not required.

## Recovery policy

Default policy:

| Control | Default |
|---|---:|
| Poll interval | 60 seconds |
| Closed-candle grace | 45 seconds |
| Maximum candles per detected gap | 500 |
| Maximum backfill date span | 30 days |
| NSE regular open | 09:15 IST |
| NSE regular close | 15:30 IST |
| Recovery timeframe | 5 minutes |

The detector evaluates only closed intervals. It does not report an intraday gap before the first candle should have closed and suppresses weekend checks.

Exchange holiday awareness remains dependent on the canonical exchange calendar. The current fallback uses the configured NSE session window and weekdays; production activation must use an ACTIVE holiday calendar.

## Provisional candle reconciliation

Live V3 OHLC snapshots are persisted as candle revisions:

- each instrument, source, timeframe, and open time has one current revision;
- changed OHLCV values create the next revision;
- the prior revision becomes non-current;
- unfinished candles use `is_provisional = 1`;
- closed snapshots use `is_closed = 1`;
- provisional candles are never usable for new exposure;
- historical REST candles provide the final closed source during recovery.

The in-memory implementation replaces the current candle identity while SQL Server preserves immutable revision lineage.

## Gap lifecycle

Statuses:

```text
DETECTED
RECOVERING
RECOVERED
FAILED
IGNORED
```

The worker records detection before requesting historical data. After persistence it re-runs the detector. The event becomes `RECOVERED` only when the gap is no longer present.

## Health

The main service status now includes:

```text
recoveryWorkerEnabled
recovery.status
recovery.lastStartedAtUtc
recovery.lastCompletedAtUtc
recovery.nextRunAtUtc
recovery.subscriptionCount
recovery.gapsDetected
recovery.recoveryRequests
recovery.candlesAccepted
recovery.candlesDuplicated
recovery.candlesRejected
recovery.lastError
recovery.warnings
```

Recovery health states:

```text
DISABLED
STOPPED
RUNNING
HEALTHY
DEGRADED
```

## Safety

- Recovery is disabled by default.
- No recovery operation can place or modify an order.
- Subscription rows require an active canonical instrument mapping.
- Provider tokens remain runtime secrets.
- Backfill requests are bounded by configured limits.
- Invalid candles are rejected by the existing freshness and OHLC validation policy.
- A failed instrument recovery does not stop the remaining subscriptions.
- New exposure still requires a valid closed candle.

## Exit gate

This slice is complete when:

- migration and verification scripts pass;
- SQL and configuration subscription plans are deterministic;
- missing closed candles are detected during the regular session;
- weekends do not create false gaps;
- recovery uses the canonical historical provider and store;
- gap lifecycle records are durable in SQL mode;
- live OHLC changes create provisional revisions;
- historical candles replace provisional values for consumers;
- all .NET, Python, and React CI checks pass.
