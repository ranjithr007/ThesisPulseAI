# Phase 0 Execution Lifecycle Summary

## Completed

- ADR-0014: order idempotency and execution lifecycle.
- `execution-command.schema.json`.
- `order-event.schema.json`.
- `fill-event.schema.json`.

## Execution sequence

```text
Approved Trade Plan
  -> Execution Command
  -> Persisted Order Intent
  -> Broker Adapter Submission
  -> Order Events
  -> Fill Events
  -> Position and P&L Projections
  -> Reconciliation
```

## Core controls

- Every command references one approved, unexpired trade plan.
- Every state-changing command has a unique idempotency key.
- Intent is persisted before broker submission.
- Duplicate delivery returns the existing result rather than placing another order.
- Timeouts are treated as unknown outcomes and reconciled before retry.
- Partial fills use actual filled quantity for positions and protective exits.
- Order events and fill events are append-only.
- Broker states are normalized behind the adapter.
- SQL Server remains the operational source of truth.

## Remaining work

- Upstox broker-adapter ADR and capability matrix.
- Reconciliation and stale broker-state policy.
- Shared valid and invalid contract fixtures.
- .NET and Python schema validation tests.
- Runtime state-machine implementation and database constraints.
