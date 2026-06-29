# Week 2 — Durable SQL Messaging Stores

## Decision

ThesisPulse AI reuses the Phase 0 operational messaging tables created by migration `V0009__create_operational_foundation_tables.sql`:

- `operations.outbox_messages`
- `operations.outbox_delivery_attempts`
- `operations.inbox_messages`
- `operations.inbox_processing_attempts`

No duplicate Phase 1 messaging tables are introduced.

## Implemented

- `SqlServerOutboxStore` implements durable outbox insertion, pending-message reads, publication completion, retry failure and dead-letter transition.
- `SqlServerInboxStore` implements idempotent insert, failed-message reacquisition, processing leases, completion and dead-letter transition.
- `SqlServerMessagingOptions` validates connection, instance, actor, timeout, lease and retry settings.
- Payloads receive a SHA-256 integrity hash before persistence.
- Non-GUID correlation IDs are deterministically converted to database GUID values while remaining stable for the same input.
- Configuration version is retained in message header JSON because the Phase 0 table stores transport headers separately.
- In-memory inbox and outbox implementations remain available for local tests and now follow the same terminal-state rules.

## Reliability behaviour

### Outbox

1. A new message starts as `PENDING`.
2. Dispatch candidates include only `PENDING` and retryable `FAILED` rows.
3. Success transitions the message to `PUBLISHED`.
4. Failure increments `attempt_count`.
5. The final failed attempt transitions to `DEAD_LETTER`.

### Inbox

1. The unique `(consumer_name, message_uid)` key prevents duplicate inserts.
2. A new or retryable message is leased as `PROCESSING`.
3. Success transitions the message to `PROCESSED`.
4. Failure transitions to `FAILED`, or `DEAD_LETTER` when attempts are exhausted.
5. A processed or actively processing message cannot be acquired again.

## Security

Connection strings are supplied through `SqlServerMessagingOptions` at runtime. They must come from the secret-management abstraction or local user secrets and must not be committed to source control.

## Next slice

- Register the durable stores in service startup configuration.
- Add outbox dispatcher and inbox consumer orchestration.
- Implement the first mock Python-to-Signal-Service versioned signal flow.
