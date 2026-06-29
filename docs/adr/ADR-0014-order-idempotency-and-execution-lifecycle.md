# ADR-0014: Order Idempotency and Execution Lifecycle

- **Status:** Accepted
- **Date:** 2026-06-29

## Decision

Every execution command references one approved, unexpired trade plan and carries a globally unique `execution_command_id` plus an `idempotency_key`.

The execution service owns the order state machine and persists intent before contacting the broker.

## State machine

```text
CREATED -> VALIDATED -> SUBMISSION_PENDING -> SUBMITTED -> ACKNOWLEDGED
ACKNOWLEDGED -> PARTIALLY_FILLED -> FILLED
ACKNOWLEDGED/PARTIALLY_FILLED -> CANCEL_PENDING -> CANCELLED
Any non-terminal state -> REJECTED or FAILED when applicable
Unknown broker outcome -> RECONCILIATION_REQUIRED
```

Terminal states are `FILLED`, `CANCELLED`, `REJECTED`, `FAILED` and `EXPIRED`.

## Idempotency

- One command identity represents one intended side effect.
- Duplicate delivery returns the original result and never creates a second broker order.
- SQL Server enforces uniqueness on environment, broker account and idempotency key.
- A timeout is an unknown outcome; reconciliation occurs before retry.
- Modify and cancel commands use their own idempotency keys and expected order version.

## Safety rules

- Quantity cannot exceed the trade plan.
- Execution cannot widen stop or price tolerance.
- Expired plans are rejected.
- Environment and broker account must match.
- Broker-specific states are normalized by the adapter.
- Order events are append-only.
- Current order state is a projection of accepted events.

## Partial fills

Partial fills update position and remaining quantity. Cancellation of the remainder follows policy. Protective exits must use actual filled quantity, not requested quantity.

## Reconciliation

Broker status is periodically and event-driven reconciled against SQL Server. Conflicts enter `RECONCILIATION_REQUIRED`; new related exposure may be blocked until resolved.

## Consequences

Retries are safe, duplicate orders are prevented, and every state transition is traceable to a command, trade plan and broker event.
