# Phase 0 Platform Foundations Summary

## Runtime ownership

### ASP.NET Core

Owns execution, portfolio, risk, broker orchestration, operational APIs, production configuration, reconciliation, and writes to operational state.

### Python

Owns feature engineering, intelligence engines, inference, training, backtesting, attribution, and candidate recommendations. Python does not receive live execution credentials and cannot mutate orders, positions, risk decisions, or active production configuration.

## Integration model

- Versioned JSON contracts over internal HTTPS for bounded request/response operations.
- Durable asynchronous commands and events for scheduled, batch, training, backtest, and attribution work.
- Transactional outbox and idempotent inbox processing.
- At-least-once delivery with application-level idempotency.
- ASP.NET Core validates and persists accepted operational engine outputs.
- Transport-neutral domain contracts allow a later message-broker migration without changing business contracts.

## SQL Server conventions

- SQL Server is the operational source of truth.
- Business schemas include `reference`, `market`, `intelligence`, `thesis`, `risk`, `execution`, `portfolio`, `broker`, `ml`, `backtest`, `operations`, and `audit`.
- Tables and columns use `snake_case`; table names are plural.
- Prices, quantities, capital, P&L, risk, fees, and decision-relevant Greeks use fixed-precision decimal types.
- Operational timestamps use UTC `datetime2(7)`.
- Mutable state uses `rowversion` where optimistic concurrency is required.
- Runtime principals have least-privilege access and no DDL authority.

## Migration authority

- `ThesisPulse.DatabaseMigrator` is the single shared-schema migration authority.
- Ordered SQL scripts are committed under `database/migrations/`.
- EF Core migrations and Alembic do not independently manage the shared operational schema.
- Applications do not migrate at startup.
- Applied scripts are immutable and checksum-verified.
- Production changes are forward-only by default and use expand-and-contract for breaking changes.

## Time and calendar

- UTC is canonical for storage, contracts, logs, comparisons, and event ordering.
- JSON timestamps require an explicit `Z` suffix.
- `Asia/Kolkata` is used for Indian exchange-session interpretation and display.
- `trade_date` is derived through the exchange timezone and versioned calendar, not by truncating UTC.
- Candle boundaries align to official exchange sessions.
- Closed-candle and point-in-time rules prevent higher-timeframe look-ahead.
- Event time, received time, processed time, generated time, and validity time remain distinct.

## Related ADRs

- `ADR-0007-aspnet-core-python-integration-model.md`
- `ADR-0008-sql-server-schema-and-naming-conventions.md`
- `ADR-0009-database-migration-ownership.md`
- `ADR-0010-timestamp-timezone-and-exchange-calendar.md`
