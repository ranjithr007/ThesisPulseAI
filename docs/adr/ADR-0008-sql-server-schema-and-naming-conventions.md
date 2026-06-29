# ADR-0008: SQL Server Schema and Naming Conventions

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture and data
- **Supersedes:** None

## Context

ThesisPulse AI uses SQL Server as the operational source of truth and is accessed by ASP.NET Core and selected Python components. Without common schema, naming, type, and ownership conventions, ORM mappings will drift, financial precision may be lost, and cross-runtime integration will become fragile.

## Decision

The operational database uses explicit business schemas, plural `snake_case` table names, `snake_case` columns, UTC timestamp suffixes, fixed-precision numeric types, and documented table ownership.

## Database schemas

The initial logical schemas are:

| Schema | Responsibility |
|---|---|
| `reference` | instruments, exchanges, sessions, calendars, broker mappings, universe versions |
| `market` | raw and normalized market observations, candles, quotes, derivatives observations |
| `intelligence` | accepted engine outputs, signals, feature references, intelligence evidence |
| `thesis` | trade theses, scenarios, assumptions, invalidation conditions, lineage |
| `risk` | policies, snapshots, decisions, limits, breaches, trade plans |
| `execution` | order intents, commands, orders, order events, fills, execution state |
| `portfolio` | positions, exposure, capital, margin, realized and unrealized P&L |
| `broker` | broker accounts, reconciliations, capability mappings, external identifiers |
| `ml` | model metadata, datasets, feature sets, training and evaluation records |
| `backtest` | runs, configurations, simulated orders, fills, metrics, attribution |
| `operations` | jobs, inbox, outbox, service state, incidents, kill switches |
| `audit` | immutable business and administrative audit events |

Schemas are logical ownership boundaries. Cross-schema references are allowed only when the dependency is intentional and documented.

## Naming rules

### Tables

- plural `snake_case`;
- descriptive business names;
- no ORM-generated abbreviations;
- no technology or service prefix unless it is part of the domain concept.

Examples:

- `reference.instruments`;
- `market.candles`;
- `intelligence.engine_outputs`;
- `thesis.trade_theses`;
- `risk.risk_decisions`;
- `risk.trade_plans`;
- `execution.orders`;
- `execution.order_events`;
- `execution.fills`;
- `portfolio.positions`;
- `audit.audit_events`.

### Columns

- `snake_case`;
- primary key: `<entity_singular>_id`;
- foreign key: referenced entity singular name plus `_id`;
- UTC timestamp: suffix `_at_utc` for event timestamps or `_utc` when a domain term already ends in a timestamp concept;
- date without time: suffix `_date`;
- quantity: suffix `_qty`;
- percentage: suffix `_pct`;
- monetary amount: suffix `_amount` and include currency context where ambiguous;
- boolean: prefix `is_`, `has_`, `can_`, or `requires_`;
- version: suffix `_version`;
- external identifier: explicit source, such as `broker_order_id`.

Examples:

- `signal_id`;
- `instrument_id`;
- `generated_at_utc`;
- `valid_until_utc`;
- `trade_date`;
- `approved_qty`;
- `risk_per_trade_pct`;
- `realized_pnl_amount`;
- `is_stale`;
- `engine_version`;
- `broker_order_id`.

## Identifier strategy

- Internal high-volume entity keys use `bigint` identity where database locality and compact indexing are valuable.
- Cross-service message, correlation, causation, idempotency, and externally exposed immutable identifiers use `uniqueidentifier`.
- Broker identifiers remain strings because formats are broker-owned.
- Display symbols and broker tokens are never used as primary keys.
- Composite natural uniqueness is enforced through unique constraints where required.

Every table must have a stable primary key even when a natural business key also exists.

## Data types

| Domain value | SQL Server type |
|---|---|
| Internal numeric key | `bigint` |
| Cross-service UUID | `uniqueidentifier` |
| UTC timestamp | `datetime2(7)` |
| Business date | `date` |
| Price | `decimal(19,6)` |
| Quantity | `decimal(19,6)` unless an integer-only constraint applies |
| Currency, P&L, fees, margin | `decimal(19,4)` |
| Ratio, probability, confidence, percentage | `decimal(12,8)` |
| Greeks and model scores | `decimal(19,10)` where required |
| Short code or status | bounded `varchar`/`nvarchar` with check constraint |
| Human-readable text | bounded `nvarchar` |
| Structured evidence or archived payload | validated JSON in `nvarchar(max)` |
| Optimistic concurrency token | `rowversion` |

`float` and `real` are prohibited for prices, quantities, capital, P&L, fees, risk values, Greeks used in decisions, and accounting calculations.

## Decimal semantics

Contracts and application code must define whether a ratio is represented as:

- fraction: `0.0125` for 1.25%; or
- percentage points: `1.25` for 1.25%.

ThesisPulse AI uses fractional representation for calculations and contracts unless a field explicitly ends in `_percentage_points`. Database check constraints should enforce expected ranges where practical.

## Status and enumeration values

- Domain statuses are stored as stable uppercase codes.
- Tables use check constraints or reference tables for permitted values.
- Numeric enum ordinals are prohibited for persisted cross-service states.
- Renaming a persisted status requires an explicit data migration.

Examples include `PAPER`, `SHADOW`, `LIVE`, `APPROVED`, `REJECTED`, `OPEN`, `PARTIALLY_FILLED`, and `FILLED`.

## Required audit columns

Mutable operational tables should include, where meaningful:

- `created_at_utc`;
- `created_by`;
- `updated_at_utc`;
- `updated_by`;
- `row_version`.

Immutable event and ledger tables record creation metadata but are not updated in place.

## Immutability and ledgers

The following are append-only or corrected by compensating records rather than destructive updates:

- engine outputs accepted for operational use;
- signal and thesis versions;
- risk decisions;
- order events;
- fills;
- P&L ledger entries;
- audit events;
- model and configuration promotion records.

Current-state projection tables may be updated, but their source event lineage must remain available.

## Constraints and indexing

Each table design must consider:

- primary and foreign keys;
- business uniqueness;
- check constraints;
- environment isolation;
- effective-date overlap prevention where required;
- correlation and idempotency uniqueness;
- query-specific composite indexes;
- filtered indexes for active records;
- index impact on high-frequency writes.

Examples of required unique constraints include:

- one accepted inbox record per consumer and message ID;
- one order command per idempotency key and environment;
- one candle per instrument, timeframe, open timestamp, and data source;
- one effective broker mapping per broker, instrument, and validity interval;
- one active policy version for a defined policy scope and effective period.

## JSON usage

JSON is permitted for:

- archived external payloads;
- engine evidence whose shape is versioned externally;
- diagnostic metadata;
- forward-compatible optional attributes.

JSON must not replace query-critical relational columns such as instrument, environment, timestamp, decision, order state, price, quantity, or policy version. JSON documents require a schema or contract version.

## ORM conventions

- EF Core and SQLAlchemy models explicitly map schema, table, column, precision, constraints, and concurrency behavior.
- ORM default names are not accepted when they conflict with this ADR.
- Automatic database creation or schema mutation at service startup is prohibited.
- Lazy-loading behavior must not hide unbounded operational queries.
- Database constraints remain authoritative even when applications perform pre-validation.

## Database principals and ownership

Separate service principals use least privilege.

- ASP.NET Core receives read/write access only to the operational schemas it owns.
- Python receives read access to approved market/reference data and write access only to explicitly assigned analytical or staging tables.
- Migration credentials are separate from runtime credentials.
- No runtime service receives schema-owner privileges.

## Alternatives considered

### ORM-native naming per runtime

Rejected because EF Core and SQLAlchemy conventions differ and would create inconsistent schemas.

### `PascalCase` tables and columns

Rejected in favor of language-neutral `snake_case` mappings across C#, Python, SQL, and JSON contracts.

### Floating-point financial columns

Rejected because binary floating point can produce unacceptable precision and reconciliation differences.

### Single `dbo` schema for all tables

Rejected because explicit schemas improve ownership, security, discoverability, and migration review.

## Consequences

- ORM mappings require deliberate configuration.
- SQL remains readable and consistent across runtimes.
- Financial and risk calculations preserve deterministic precision.
- Security boundaries can align with business schema ownership.
- Database reviews can identify violations before deployment.
