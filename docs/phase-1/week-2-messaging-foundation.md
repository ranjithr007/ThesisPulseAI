# Week 2 — Messaging and Cache Foundation

## Scope implemented

- Versioned event-envelope and message-metadata contracts.
- Transport-neutral event-bus abstraction.
- In-memory event-bus implementation for local development and tests.
- Distributed-cache abstraction.
- In-memory cache implementation with absolute expiry.
- Outbox message and inbox receipt models.
- Outbox and inbox persistence interfaces.
- In-memory outbox and inbox implementations for retry and idempotency flows.

## Reliability rules

- `MessageId` is the stable event identity.
- `CorrelationId` traces the complete business flow.
- `CausationId` identifies the message that caused another message.
- Consumers use the combination of `MessageId` and consumer name for idempotency.
- Published outbox messages are excluded from pending dispatch.
- Failed outbox messages remain available for controlled retry.
- Redis and the event bus are not operational sources of truth.
- SQL Server implementations will replace the in-memory stores for durable environments.

## Current limitation

The in-memory providers are process-local and intentionally non-durable. They support local development, unit tests, and contract validation only. They must not be used as the durable implementation for paper, shadow, or live trading environments.
