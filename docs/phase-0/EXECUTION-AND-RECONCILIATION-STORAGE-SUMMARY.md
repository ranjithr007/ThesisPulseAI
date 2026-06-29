# Phase 0 Execution and Reconciliation Storage Summary

## Migration

`database/migrations/V0007__create_execution_and_reconciliation_tables.sql`

V0007 implements the ASP.NET Core execution persistence boundary after an approved, unexpired trade plan.

```text
Signal -> Thesis -> Risk Decision -> Trade Plan -> Execution Command -> Order Events -> Fill Events
```

The execution service persists intent before broker contact, owns canonical order state, and treats broker responses as evidence rather than domain state.

## Broker accounts

### `broker.broker_accounts`

Stores canonical, non-secret account references linked to `reference.brokers`.

It preserves environment isolation, account type, base currency, effective dates and operating permissions. A restricted account may block new exposure while keeping risk-reducing exits enabled.

Credentials, access tokens and app secrets are intentionally excluded.

## Versioned order transitions

### `execution.order_transition_policies`

Stores immutable, effective-dated transition-policy versions with approval metadata and checksums.

### `execution.order_transition_rules`

Defines allowed event-driven transitions, optional command context, reconciliation-only transitions and terminal-state classification.

The runtime transition validator must use the active policy. Invalid, late or contradictory events remain auditable but do not regress the current order projection.

## Immutable execution commands

### `execution.execution_commands`

Stores canonical `PLACE`, `MODIFY` and `CANCEL` command intent with:

- exact trade-plan and broker-account lineage;
- environment, source and execution-policy versions;
- environment/account-scoped idempotency key;
- place-order instrument, side, product, quantity and price fields;
- modify/cancel target order and expected version;
- validity, correlation and causation timestamps;
- canonical JSON and SHA-256 contract hash.

Database controls enforce:

- one command per environment, broker account and idempotency key;
- one client order ID per broker account;
- required fields by command type;
- order-type-specific limit and trigger fields;
- positive quantities and prices;
- expected versions for modifications and cancellation.

### Command history and projection

- `execution.execution_command_events` is append-only command lifecycle history.
- `execution.execution_command_states` is the mutable current projection returned for duplicate deliveries.

Only a proven pre-submission transient failure may be marked retryable without reconciliation. An unknown post-submission outcome requires broker contact evidence and reconciliation and cannot be blindly retried.

## Canonical orders

### `execution.orders`

Stores the mutable current projection of one internal order created before broker acknowledgement.

It tracks:

- original place command and trade plan;
- broker account, instrument, side and product intent;
- requested, filled and remaining quantity;
- weighted average fill price;
- canonical and broker identities;
- current normalized status and optimistic order version;
- terminal and reconciliation flags.

Filled quantity plus remaining quantity must equal requested quantity. Terminal classification is constrained to `FILLED`, `CANCELLED`, `REJECTED`, `FAILED` and `EXPIRED`.

### `execution.order_events`

Stores the canonical append-only order event contract, including original broker status, broker sequence, event/received/generated timestamps and projection disposition:

- `ACCEPTED`
- `IGNORED_LATE`
- `QUARANTINED`

Only accepted events may advance the `orders` projection. Uniqueness on order and order version prevents replay of the same canonical version.

### `execution.order_event_quarantines`

Records invalid-transition and contradictory-event investigations without deleting the original event.

## Idempotent fills

### `execution.fills`

Stores every distinct broker or simulated fill with complete command, order, plan, account and instrument lineage.

Each fill requires at least one stable identity:

- broker fill ID; or
- deterministic fill fingerprint.

Filtered unique indexes prevent duplicate processing under either identity. Quantities, prices, fees and taxes use fixed precision. A broker acknowledgement is never represented as a fill.

Partial-fill aggregation and protective-exit sizing remain transactional application responsibilities and must use actual filled quantity.

## Broker request evidence

### `broker.broker_requests`

Stores each redacted adapter attempt with:

- command and account lineage;
- attempt number and canonical operation;
- endpoint, adapter and capability versions;
- request and response timestamps;
- transport and normalized outcome;
- raw broker code and normalized error category;
- broker order ID and client tag;
- payload hashes and redacted JSON.

A timeout or uncertain connection after possible submission is classified as `UNKNOWN_OUTCOME`, not a normal retryable failure.

## Reconciliation

### `execution.reconciliation_runs`

Tracks account, command, order or session reconciliation triggered by:

- unknown outcome;
- startup or recovery;
- periodic checks;
- stream reconnection;
- quantity mismatch;
- session shutdown;
- operator request.

### `execution.reconciliation_observations`

Preserves broker order, history, trade, position, holding, funds and client-tag observations as immutable redacted evidence.

### `execution.reconciliation_discrepancies`

Records local-versus-broker conflicts without destructive overwrite. Material conflicts may block new exposure while preserving risk-reducing exits.

### `execution.reconciliation_resolutions`

Stores append-only approved resolutions, including compensating order events where required.

## Runtime semantic rules

The ASP.NET Core execution service must perform each command transition in one transaction and validate that:

- the trade plan is current, ready, unexpired and environment-compatible;
- broker account, instrument, side, product and environment match lineage;
- quantity never exceeds the plan or current unfilled quantity;
- price tolerance and stop protection are never widened;
- the active broker capability permits the order combination;
- tick size, lot size, freeze quantity and market session are valid;
- modify/cancel expected order version matches the current projection;
- command, current state and durable outbox record are committed before broker contact;
- duplicate command delivery returns the existing projection;
- unknown outcomes trigger reconciliation before any retry;
- event transitions match the active transition policy;
- older events are stored but cannot regress state;
- cumulative filled quantity never decreases or exceeds requested quantity;
- protective exits never exceed actual open position quantity;
- fills and compensating events update portfolio state atomically in later portfolio services.

## Verification

`database/verification/V0007__verify_execution_and_reconciliation_tables.sql`

The verification checks:

- 15 required tables;
- 35 trusted foreign keys;
- 22 required indexes, including filtered broker-order, client-order and fill identities;
- 43 selected trusted safety constraints;
- fixed precision for commands, orders and fills;
- the V0007 database baseline marker.

## Local acceptance

```powershell
cd "D:\00 Projects\ThesisPulseAI"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0007__create_execution_and_reconciliation_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0007__verify_execution_and_reconciliation_tables.sql"
```

Repeat both commands once. Acceptance requires `PASS V0007` without duplicate-object or filtered-index errors.

## Deferred implementation

V0007 provides storage and structural controls. Later application batches will add:

- reviewed broker-account and transition-policy seeds;
- execution-command persistence and idempotency service;
- durable outbox integration;
- fake broker and Upstox adapters;
- broker capability resolver;
- optimistic modify/cancel service;
- order projection worker;
- idempotent fill processor;
- reconciliation scheduler and operator workflow;
- end-to-end duplicate, timeout, partial-fill and out-of-order tests.

## Next migration

V0008 will add portfolio positions, lots, cash and exposure ledgers, realized/unrealized P&L and position reconciliation state.
