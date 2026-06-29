# ADR-0014: Order Idempotency and Execution Lifecycle

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, execution, risk and operations

## Context

Network retries, delayed broker responses, duplicate messages, partial fills and out-of-order updates can create duplicate exposure or incorrect order state. Execution must remain safe when the broker result is unknown or when the same command is delivered more than once.

## Decision

Every execution action is represented by an immutable command referencing one approved and unexpired trade plan.

The execution service persists command intent before calling the broker and owns the canonical order state machine. Broker responses and updates are translated into append-only order and fill events.

## Command identity

Every command carries:

- `execution_command_id`;
- `idempotency_key`;
- `command_type`;
- `trade_plan_id`;
- `broker_account_id`;
- `environment`;
- correlation and causation IDs;
- validity timestamp;
- expected order version for modification or cancellation.

One command identity represents one intended side effect.

SQL Server enforces uniqueness on environment, broker account and idempotency key. Duplicate delivery returns the previously recorded command result and does not contact the broker again unless the existing result is explicitly awaiting reconciliation.

## Place-order lifecycle

```text
CREATED
  -> VALIDATED
  -> SUBMISSION_PENDING
  -> SUBMITTED
  -> ACKNOWLEDGED
  -> PARTIALLY_FILLED
  -> FILLED
```

Alternative transitions include:

```text
CREATED/VALIDATED -> REJECTED
SUBMISSION_PENDING/SUBMITTED -> RECONCILIATION_REQUIRED
ACKNOWLEDGED/PARTIALLY_FILLED -> MODIFY_PENDING -> MODIFIED
ACKNOWLEDGED/PARTIALLY_FILLED -> CANCEL_PENDING -> CANCELLED
Any eligible non-terminal state -> EXPIRED
Operationally unrecoverable state -> FAILED
RECONCILIATION_REQUIRED -> reconciled canonical state
```

Terminal states are `FILLED`, `CANCELLED`, `REJECTED`, `FAILED` and `EXPIRED`.

`RECONCILIATION_REQUIRED` is non-terminal and blocks blind resubmission.

## Allowed-transition enforcement

State transitions are defined in a versioned transition policy. An event that proposes an invalid transition is quarantined and does not update the current-state projection.

Examples:

- `FILLED` cannot transition to `ACKNOWLEDGED`;
- `CANCELLED` cannot accept a new fill without reconciliation escalation;
- filled quantity cannot decrease;
- remaining quantity cannot become negative;
- a broker acknowledgement is not a fill;
- a modify or cancel command must target an order state that permits the action.

## Submission and unknown outcomes

Before network submission, the execution service transactionally stores:

- command;
- order intent;
- idempotency key;
- trade-plan lineage;
- outbox record;
- command status.

A timeout after broker submission is an unknown outcome. The system must query broker order history, client tag, order book and trades before retrying.

Blind retries of place, modify or cancel operations are prohibited.

## Modify and cancel

Modify and cancel are separate execution commands with separate idempotency keys.

They require:

- target internal order ID;
- expected order version;
- supported canonical state;
- active broker capability;
- unchanged environment and broker account;
- requested changes within the trade-plan and risk envelope.

Optimistic concurrency rejects stale commands whose expected version no longer matches.

## Safety invariants

- Quantity cannot exceed the trade plan or current unfilled quantity.
- Execution cannot widen the approved stop or price tolerance.
- Expired, rejected or superseded trade plans are rejected.
- Environment, broker account, instrument and side must match lineage.
- The adapter validates lot size, tick size, product, order type and time in force.
- Order events are append-only.
- Current order state is a projection of accepted events.
- Broker-specific status strings are retained as evidence but are not canonical state.

## Partial fills

Each distinct broker fill becomes an idempotent fill event.

Partial fills:

- increase cumulative filled quantity;
- reduce remaining quantity;
- update weighted average fill price;
- create or update actual position exposure;
- size protective exits using actual filled quantity;
- preserve the unfilled remainder for cancellation or further execution according to policy.

The platform must prevent protective-exit quantity from exceeding the actual open position.

## Event ordering

Broker events may arrive late or out of order. Consumers use broker sequence, event time, received time, order version and deterministic tie-breakers.

An older event may be stored for audit but must not regress the current state. Contradictory events trigger reconciliation.

## Reconciliation

Reconciliation runs:

- after unknown submission outcomes;
- on startup or recovery;
- periodically during the session;
- after stream disconnection;
- when local and broker quantities differ;
- before session shutdown;
- on operator request.

It compares commands, orders, broker history, trades, positions and funds. Differences are recorded rather than destructively overwritten.

Material conflicts enter `RECONCILIATION_REQUIRED`. New related exposure may be blocked while exits remain permitted.

## Persistence constraints

Required uniqueness includes:

- command ID;
- environment, broker account and idempotency key;
- internal order ID;
- broker order ID within broker account when available;
- broker fill ID within broker account when available;
- message ID per inbox consumer;
- order ID and order version;
- fill fingerprint fallback when the broker provides no stable fill ID.

## Failure classification

Failures are classified as:

- permanent validation or policy rejection;
- unsupported capability;
- transient pre-submission failure;
- unknown post-submission outcome;
- broker or exchange rejection;
- reconciliation conflict;
- internal invariant violation.

Only proven pre-submission transient failures may be retried without broker reconciliation.

## Alternatives considered

### Retry every timeout automatically

Rejected because a successful broker submission may have occurred before the timeout.

### Update one mutable order row without events

Rejected because historical transitions, late events and reconciliation corrections would not be auditable.

### Use broker order ID as the only identity

Rejected because the platform requires an internal identity before broker acknowledgement and must support paper and shadow environments.

## Consequences

- Retries are safe and duplicate orders are prevented.
- Unknown outcomes require explicit reconciliation work.
- Every order and fill remains traceable to its command, trade plan, risk decision, thesis and signal.
- Execution implementations must enforce state transitions and concurrency at both application and database layers.
