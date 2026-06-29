# Upstox Reconciliation Policy

## Purpose

Reconciliation resolves differences among ThesisPulse AI commands, internal order state, Upstox order state, trades, positions, holdings, funds and received streaming or webhook events.

SQL Server remains the operational source of truth for ThesisPulse AI history. Upstox is the authoritative external observation for what the broker and exchange accepted or executed.

## Reconciliation triggers

Reconciliation runs:

- after submission timeout or ambiguous response;
- after modify or cancel timeout;
- when a broker event is missing, duplicated, late or out of order;
- when internal and broker order states disagree;
- after partial fills;
- when position or holding quantities disagree;
- at configured intraday intervals;
- before and after the trading session;
- before system restart recovery is declared complete;
- on operator request.

## Reconciliation inputs

- internal execution commands;
- internal orders and append-only order events;
- internal fills and position projections;
- Upstox order details and order history;
- Upstox order book and order trades;
- broker trades and trade history where applicable;
- broker positions and holdings;
- account funds and margin observations;
- streaming or webhook order updates;
- broker instrument mapping version.

## Matching hierarchy

Prefer matching by:

1. broker order ID;
2. internal order ID stored with broker response;
3. broker tag or client reference;
4. execution command and idempotency identity;
5. controlled fallback using account, instrument, side, quantity and time window.

Fallback matching never automatically merges ambiguous orders. Ambiguity creates an incident.

## Resolution classes

### In sync

Internal and broker observations are compatible. Update reconciliation timestamps and evidence only.

### Broker ahead

The broker reports a valid later state or fill not yet processed internally. Append the missing normalized event and update projections idempotently.

### Internal ahead

Internal state claims a broker transition that cannot be confirmed. Move the order to `RECONCILIATION_REQUIRED`, block unsafe related actions and retry observation according to policy.

### Unknown broker order

An internal submitted order has no broker match. Do not blindly resubmit. Investigate using tag, history and trade endpoints until the unknown-outcome window expires.

### Unknown internal order

The broker reports an order not recognized internally. Record an external-order incident. Depending on policy, mark the account close-only until classified as manual, imported, stale or unauthorized.

### Quantity or fill mismatch

Rebuild the broker-observed fill set, compare deduplicated fill identities and create compensating projection events. Never overwrite fill history destructively.

### Position mismatch

Compare expected positions derived from accepted fills with broker positions. Block new exposure on affected instruments when the difference is material or unexplained.

## Safety states

Reconciliation can place the following scopes into restricted mode:

- order;
- instrument;
- strategy;
- broker account;
- environment.

Available actions include:

- allow normal operation;
- block modifications;
- block new entries;
- close-only;
- operator review required;
- platform kill switch.

Automatic forced exit is not the default response to uncertainty. It requires an approved emergency-exit policy.

## Idempotency

- Broker order and fill observations are deduplicated.
- Reprocessing the same broker event does not duplicate fills, positions or P&L.
- Reconciliation writes append-only evidence and compensating events.
- Every run has a unique reconciliation ID and records its input observation versions.

## Freshness

A broker observation has:

- broker event time where available;
- received time;
- retrieved time;
- source endpoint or channel;
- age at decision time.

Stale observations cannot clear a reconciliation-required state.

## Operational records

Recommended records include:

- `broker.reconciliation_runs`;
- `broker.reconciliation_observations`;
- `broker.reconciliation_differences`;
- `broker.external_orders`;
- `operations.incidents`;
- append-only execution and portfolio adjustment events.

## Required alerts

Alert on:

- unresolved submission timeout;
- unknown broker order;
- unknown internal order;
- duplicate broker fill identity;
- filled quantity greater than requested quantity;
- position mismatch;
- holdings mismatch;
- funds or margin anomaly;
- stale broker observation;
- repeated authentication or rate-limit failures;
- mapping mismatch;
- reconciliation run failure.

## Recovery after restart

Before live execution resumes:

1. load non-terminal internal orders;
2. retrieve current broker orders and trades;
3. reconcile fills and states;
4. rebuild positions and risk exposure;
5. verify account and instrument mappings;
6. confirm no unresolved material differences;
7. explicitly mark execution readiness.

The scheduler must not submit new live orders merely because the application process has restarted successfully.
