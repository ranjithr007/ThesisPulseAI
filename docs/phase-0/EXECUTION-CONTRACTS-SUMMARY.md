# Phase 0 Execution Contracts Summary

## Completed architecture decisions

- ADR-0013 isolates Upstox behind a canonical broker adapter.
- ADR-0014 defines command idempotency, order state, partial-fill handling and reconciliation.

## Canonical execution flow

```text
Approved Trade Plan
  -> Execution Command
  -> Internal Order Intent
  -> Broker Adapter Submission
  -> Order Events
  -> Fill Events
  -> Position and P&L Projections
  -> Reconciliation
```

## Execution command

Each place, modify or cancel action has a unique command ID and idempotency key.

- Place requires an approved and unexpired trade plan.
- Modify and cancel require target order and expected order version.
- Duplicate commands return the existing result.
- A timeout after submission is treated as an unknown outcome.
- Unknown outcomes must be reconciled before retry.

## Order lifecycle

Canonical states include:

- `CREATED`;
- `VALIDATED`;
- `SUBMISSION_PENDING`;
- `SUBMITTED`;
- `ACKNOWLEDGED`;
- `PARTIALLY_FILLED`;
- `FILLED`;
- `MODIFY_PENDING`;
- `MODIFIED`;
- `CANCEL_PENDING`;
- `CANCELLED`;
- `REJECTED`;
- `FAILED`;
- `EXPIRED`;
- `RECONCILIATION_REQUIRED`.

Order events are append-only. Current state is a projection of accepted events and cannot regress because of a late broker update.

## Fill safety

- Fill identities are de-duplicated using broker fill ID or deterministic fingerprint.
- Cumulative fill cannot exceed order quantity without reconciliation escalation.
- Positions use actual fills, not requested quantity.
- Protective exits use actual open quantity.
- Fees and taxes remain separate from gross trade value.

## Broker boundary

- Strategy and domain code use canonical enums and identifiers.
- Broker tokens and status values stay inside the adapter.
- Missing or stale instrument mappings block submission.
- The capability matrix controls supported order combinations.
- Paper and shadow runtimes cannot use live placement credentials.

## Remaining implementation work

- shared valid and invalid execution fixtures;
- .NET and Python schema-validation tests;
- ASP.NET Core state-machine validator;
- SQL Server command, order, event, fill, inbox and outbox tables;
- fake broker adapter;
- Upstox adapter contract tests;
- reconciliation worker;
- runtime kill switches and close-only controls.
