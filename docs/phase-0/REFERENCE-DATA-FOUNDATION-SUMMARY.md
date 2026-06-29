# Phase 0 Reference Data Foundation Summary

## Migration

`database/migrations/V0002__create_reference_tables.sql`

V0002 adds the versioned reference model required before market, intelligence, risk and execution data can be stored safely.

## Tables

- `reference.exchanges`
- `reference.exchange_calendars`
- `reference.calendar_days`
- `reference.trading_sessions`
- `reference.instruments`
- `reference.universe_versions`
- `reference.universe_members`
- `reference.brokers`
- `reference.broker_instrument_mappings`

## Key controls

- stable internal numeric keys and external UUIDs;
- fixed-precision tick sizes, lot sizes and strike prices;
- versioned exchange calendars and universes;
- one active calendar per exchange;
- one active universe version per code and environment;
- calendar-day and trading-session consistency checks;
- derivative field checks for futures and options;
- self-referencing underlying-instrument lineage;
- one open broker mapping per broker/instrument and broker key;
- JSON validation for optional broker metadata;
- optimistic concurrency tokens on mutable reference tables;
- no embedded production instruments, holidays or broker tokens.

## Verification

`database/verification/V0002__verify_reference_tables.sql`

The verification script checks nine tables, nine foreign keys, eleven indexes, selected trusted check constraints and the V0002 database metadata marker.

## Local acceptance

Run from the repository root using SQL Server LocalDB:

```powershell
sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -i ".\database\migrations\V0002__create_reference_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -i ".\database\verification\V0002__verify_reference_tables.sql"
```

Repeat both commands once. The acceptance result is `PASS V0002` with no duplicate-object errors.

## Deferred reference data

Reviewed seed versions will later provide:

- NSE exchange identity and timezone;
- exchange calendar versions and holidays;
- regular and special trading sessions;
- NIFTY 50, BANK NIFTY and FINNIFTY instruments;
- selected liquid-equity allow-lists;
- Upstox instrument mappings.

These values are deliberately excluded from V0002 because they are independently versioned operational data.

## Next migration

V0003 will add normalized market candles, observation lineage, ingestion state and data-quality assessments.
