# Phase 0 Market Data Foundation Summary

## Migration

`database/migrations/V0003__create_market_data_tables.sql`

V0003 implements the SQL Server foundation required to ingest, normalize, revise and quality-gate market data without losing source or point-in-time lineage.

## Tables

### `market.data_sources`

Defines exchange, broker, vendor, derived and replay sources together with transport type and authoritative-source status.

### `market.ingestion_batches`

Tracks every live, historical, backfill or replay request, its scope, counts, status, correlation ID and bounded error context.

### `market.source_observations`

Stores immutable source envelopes with:

- ingestion batch and source identity;
- canonical instrument and broker mapping;
- source event and sequence identifiers;
- event, published, received and processed timestamps;
- timeframe, session and trade date;
- payload hash and optional archived JSON;
- revision and source version;
- quality state and point-in-time eligibility.

### `market.candles`

Stores normalized OHLCV candle revisions with exact source-observation lineage. It enforces:

- fixed-precision prices and quantities;
- valid OHLC relationships;
- non-negative volume and trade counts;
- official trading-session reference;
- closed versus provisional consistency;
- correction lineage through `supersedes_candle_id`;
- one revision number per source candle identity;
- one current revision per source candle identity;
- new-exposure usability only for closed, valid, point-in-time-eligible candles.

### `market.ingestion_cursors`

Maintains mutable source/instrument/data-type/timeframe progress and health, including last event, last sequence, successful batch, failure count and retry time.

### `market.data_quality_assessments`

Persists the canonical data-quality contract fields:

- environment;
- instrument, data type, timeframe and source;
- freshness basis and evaluation time;
- measured and permitted age;
- canonical quality state;
- reason codes;
- new-exposure and exit usability;
- revision and sequence-gap count;
- policy version and correlation ID.

## Canonical quality states

- `VALID`
- `DEGRADED`
- `STALE`
- `INCOMPLETE`
- `DUPLICATE`
- `OUT_OF_ORDER`
- `CONFLICTED`
- `INVALID`
- `UNKNOWN`

## Timeframes

The initial accepted hierarchy is stored using:

- `1m`
- `5m`
- `15m`
- `1h`
- `1d`

## Verification

`database/verification/V0003__verify_market_data_tables.sql`

The verification checks:

- six required tables;
- sixteen trusted foreign keys;
- fifteen operational and filtered indexes;
- twenty trusted quality and consistency checks;
- `decimal(19,6)` candle precision;
- the V0003 database baseline marker.

## Local acceptance

```powershell
cd "D:\00 Projects\ThesisPulseAI"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0003__create_market_data_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0003__verify_market_data_tables.sql"
```

Repeat both commands once. Acceptance requires `PASS V0003` without duplicate-object or filtered-index errors.

## Deferred implementation

V0003 creates storage only. Later application batches will add:

- Upstox source and reference seed versions;
- market-data collectors;
- source-event deduplication service;
- candle alignment and normalization service;
- freshness-policy evaluator;
- anomaly and conflict detection;
- ingestion cursor recovery;
- quality-triggered operational controls.

## Next migration

V0004 will persist intelligence engine outputs, evidence references and canonical signals with full market-data lineage.
