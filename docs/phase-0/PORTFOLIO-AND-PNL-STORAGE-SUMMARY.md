# Phase 0 Portfolio and P&L Storage Summary

## Migration

`database/migrations/V0008__create_portfolio_and_pnl_tables.sql`

V0008 implements the ASP.NET Core portfolio source of truth derived from accepted execution fills.

```text
Accepted fills
  -> position events
  -> position and lot projections
  -> realized P&L and cash/exposure ledgers
  -> valuation and portfolio P&L snapshots
  -> broker-position reconciliation
```

Broker positions and holdings are external observations. They do not replace internal fill-derived history.

## Portfolio identity

### `portfolio.portfolios`

Defines an environment-isolated strategy portfolio linked to one canonical broker account.

It stores:

- portfolio and strategy identity;
- environment and broker-account lineage;
- base currency;
- lot-accounting method: `FIFO`, `LIFO` or `WEIGHTED_AVERAGE`;
- effective dates and operating state;
- new-exposure and risk-reducing-exit permissions.

Credentials and broker access tokens are not stored.

## Current position projection

### `portfolio.positions`

Stores the mutable current net position per portfolio, instrument and product type.

It tracks:

- `LONG`, `SHORT` or `FLAT` state;
- current quantity and weighted average open price;
- cost basis and market value;
- cumulative realized and unrealized P&L;
- cumulative fees and taxes;
- protected exit quantity;
- current position version and latest event sequence;
- open, close, fill and valuation timestamps;
- reconciliation-required state.

Database checks ensure:

- flat positions have zero quantity and no average price;
- non-flat positions have positive quantity and average price;
- protective exit quantity cannot exceed actual position quantity;
- closed positions have zero quantity;
- current projections use `rowversion` for optimistic concurrency.

## Append-only position history

### `portfolio.position_events`

Stores every accepted portfolio transition:

- `OPENED`
- `INCREASED`
- `REDUCED`
- `CLOSED`
- `REVERSED`
- `ADJUSTED`
- `RECONCILIATION_REQUIRED`
- `RECONCILED`

Each event records complete before-and-after quantity, side, average price, cost basis, realized P&L, fees and taxes.

A filtered unique index permits each execution fill to affect the position ledger only once. Reprocessing a duplicate fill therefore cannot duplicate position quantity or P&L.

## Lot accounting

### `portfolio.position_lots`

Stores the current remainder of every opening lot with opening-fill lineage, quantity, price, costs, side, status and optimistic concurrency.

### `portfolio.position_lot_closures`

Stores immutable matching between a closing fill and one or more opening lots.

Each closure records:

- matched quantity;
- open and close price;
- gross realized P&L;
- allocated opening and closing fees and taxes;
- net realized P&L;
- accounting method used.

Lot remaining quantity is a projection. Closure history is never overwritten.

### `portfolio.realized_pnl_entries`

Provides an explicit realized-P&L ledger with one entry per lot closure, retaining portfolio, position, instrument, fill, trade date, currency and correlation lineage.

## Cash accounting

### `portfolio.cash_balances`

Stores current settled, unsettled-receivable, unsettled-payable, reserved, total and available cash per portfolio and currency.

The database enforces:

```text
total balance = settled + receivable - payable
available cash = total balance - reserved
```

### `portfolio.cash_ledger_entries`

Stores append-only cash changes for:

- deposits and withdrawals;
- trade settlement;
- fees and taxes;
- dividends and interest;
- margin blocks and releases;
- corporate actions;
- adjustments and reconciliation.

Entries use portfolio-scoped idempotency keys and ordered ledger sequences. Fill-derived entries are unique per fill, entry type and currency.

## Exposure accounting

### `portfolio.exposure_states`

Stores mutable current exposure for:

- gross;
- net;
- instrument;
- sector;
- correlation bucket;
- product type;
- strategy.

### `portfolio.exposure_ledger_entries`

Stores append-only before-and-after exposure values with fill, position, valuation or reconciliation lineage and portfolio-scoped idempotency.

Exposure projections are intended to feed future risk snapshots without reconstructing all historical events for each decision.

## Valuation and unrealized P&L

### `portfolio.valuation_marks`

Stores immutable price marks from:

- normalized market candles;
- broker observations;
- settlement values;
- controlled manual input.

Each mark includes quality, freshness, age and eligibility for risk calculations. Invalid or stale marks cannot be marked risk-eligible.

### `portfolio.position_valuations`

Stores immutable position-level valuation results using an exact position version and valuation mark:

- quantity and side;
- average open and mark price;
- cost basis and market value;
- unrealized P&L;
- gross and net exposure.

### `portfolio.pnl_snapshots`

Stores immutable portfolio-level snapshots with:

- realized, unrealized, gross and net P&L;
- fees and taxes;
- gross and net exposure;
- cash balance;
- net liquidation value;
- strategy and portfolio drawdown;
- canonical JSON and SHA-256 hash.

### `portfolio.pnl_snapshot_positions`

Links each portfolio snapshot to the exact position valuations included in the calculation.

## Broker-position reconciliation

### `portfolio.broker_position_observations`

Stores immutable, redacted broker position evidence with reconciliation-run lineage, broker account, instrument, product, side, quantity, prices, P&L, source endpoint, freshness and payload hash.

### `portfolio.position_reconciliation_states`

Stores the mutable current comparison between internal fill-derived position and broker-observed position.

Material differences may block new exposure while preserving risk-reducing exits.

### `portfolio.position_reconciliation_events`

Stores append-only investigation and resolution history:

- `DETECTED`
- `CONFIRMED`
- `RESOLUTION_PROPOSED`
- `ADJUSTMENT_APPLIED`
- `RESOLVED`
- `IGNORED`
- `REOPENED`

An applied adjustment must reference a resulting append-only position event. Broker state is therefore never copied over the internal position destructively.

## Runtime semantic rules

The ASP.NET Core portfolio service must process each accepted fill in one transaction and validate that:

- portfolio, broker account, execution order, fill, instrument and environment are consistent;
- the fill has not already been applied;
- side and product match execution lineage;
- lot matching follows the portfolio accounting method;
- total lot closures do not exceed the closing fill or any lot remainder;
- the sum of open lot remainders equals the current position quantity;
- weighted average price and cost basis are recomputed deterministically;
- realized P&L equals gross P&L less allocated fees and taxes;
- cash entry currency matches its balance and portfolio base/settlement rules;
- cash and exposure projections are updated with their ledgers atomically;
- valuation marks belong to the same instrument as the position;
- P&L snapshot totals exactly equal their position components and cash state;
- signed reconciliation differences are recalculated from sides and quantities;
- stale broker observations cannot clear reconciliation-required state;
- broker mismatches create evidence and approved compensating events rather than deleting history;
- protective order quantity never exceeds actual open quantity.

## Verification

`database/verification/V0008__verify_portfolio_and_pnl_tables.sql`

The verification checks:

- 17 required tables;
- 48 trusted foreign keys;
- 18 operational and filtered indexes;
- 45 selected trusted accounting and lifecycle constraints;
- six mutable projections with `rowversion`;
- fixed precision for positions, cash and P&L;
- the V0008 database baseline marker.

## Local acceptance

```powershell
cd "D:\00 Projects\ThesisPulseAI"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0008__create_portfolio_and_pnl_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0008__verify_portfolio_and_pnl_tables.sql"
```

Repeat both commands once. Acceptance requires `PASS V0008` without duplicate-object, filtered-index or foreign-key errors.

## Deferred implementation

V0008 provides storage and structural controls. Later application batches will add:

- reviewed portfolio seeds;
- transactional fill-to-position processor;
- FIFO/LIFO/weighted-average lot matcher;
- cash settlement and fee/tax posting service;
- exposure projection service;
- mark-to-market and P&L snapshot workers;
- protective-exit quantity synchronizer;
- position reconciliation scheduler and operator workflow;
- end-to-end duplicate fill, partial fill, reversal, settlement and restart tests.

## Next migration

V0009 will add durable inbox/outbox transport, scheduled jobs, operational controls, audit events, incidents, alerts and kill-switch state.
