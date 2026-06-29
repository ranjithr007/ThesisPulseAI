# ADR-0021 — Portfolio Events and Snapshot Contract Boundary

## Status

Accepted

## Context

ASP.NET Core owns portfolio state, execution, risk and operational controls. Python owns analytics, feature generation, intelligence, training and backtesting.

Python processes may need portfolio outcomes and point-in-time state for attribution, validation, learning and risk analysis. Exposing mutable SQL Server projections directly would couple Python to ASP.NET Core persistence, weaken point-in-time guarantees and make replay semantics ambiguous.

## Decision

### Mutable portfolio aggregate

The mutable portfolio aggregate remains internal to ASP.NET Core.

This includes current database projections such as:

- current positions;
- remaining lot quantities;
- cash balances;
- exposure states;
- reconciliation state;
- current operational flags.

Other services must not treat these mutable tables as integration contracts.

### Canonical cross-service contracts

Three immutable, versioned contracts will define the cross-service boundary:

1. `position-event`
2. `portfolio-snapshot`
3. `pnl-snapshot`

### Position event

A position event represents an accepted portfolio transition caused by a fill, approved reconciliation adjustment or another governed portfolio event.

It must include:

- contract version;
- event ID and ordered position sequence;
- environment;
- portfolio, position and instrument identity;
- event type;
- before and after side, quantity, average price and cost basis;
- realised P&L, allocated fees and taxes;
- exact fill, order or reconciliation lineage;
- event and persistence times in UTC;
- source service and version;
- correlation and causation IDs.

### Portfolio snapshot

A portfolio snapshot is an immutable point-in-time view suitable for risk, analytics and deterministic replay.

It must include:

- contract version and snapshot ID;
- environment and portfolio identity;
- snapshot time in UTC;
- capital, cash, gross exposure and net exposure;
- active position count;
- exact component position-valuation references;
- source service and version;
- correlation and causation IDs;
- canonical content hash.

### P&L snapshot

A P&L snapshot provides immutable performance evidence.

It must include:

- contract version and snapshot ID;
- environment and portfolio identity;
- snapshot time and trading date;
- realised, unrealised, gross and net P&L;
- fees and taxes;
- net liquidation value;
- strategy and portfolio drawdown;
- exact valuation and portfolio-snapshot lineage;
- source service and version;
- correlation and causation IDs;
- canonical content hash.

## Numeric and time rules

- Quantities, prices, fees, taxes, capital, exposure and P&L use fixed-precision decimals.
- UTC is canonical.
- Trading-day interpretation uses the versioned exchange calendar and `Asia/Kolkata` where applicable.
- Snapshot consumers must use the exact supplied valuation lineage and must not silently replace it with current prices.

## Delivery rules

- Contracts are delivered using the durable outbox/inbox integration model.
- Delivery is at least once.
- Consumers de-duplicate by contract message identity.
- A duplicate message must not create duplicate P&L, learning or risk side effects.
- Contract schema validation occurs before business processing.

## Consequences

### Positive

- Python remains independent of mutable ASP.NET Core persistence.
- Portfolio analytics become point-in-time and replay safe.
- Outcome attribution retains exact execution, valuation and policy lineage.
- Contract evolution can be versioned and tested across .NET and Python.

### Cost

- Three additional schemas and fixture sets are required.
- ASP.NET Core must project canonical immutable messages from internal state.
- Consumers must retain idempotency and schema-version handling.

## Implementation status

The boundary decision is accepted in Phase 0. The JSON schemas, semantic validators and fixtures are the first canonical-contract task before portfolio events are integrated with Python services.
