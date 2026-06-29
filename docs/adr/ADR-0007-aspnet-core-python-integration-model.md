# ADR-0007: ASP.NET Core–Python Integration Model

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture
- **Supersedes:** None

## Context

ThesisPulse AI separates operational trading responsibilities from market intelligence and research. ASP.NET Core owns execution, portfolio, risk, and operational state, while Python owns feature engineering, intelligence engines, inference, training, and backtesting.

The integration model must preserve these ownership boundaries, provide durable delivery and idempotency, prevent direct execution by AI components, and remain practical for an initial five-minute intraday platform.

## Decision

ThesisPulse AI adopts a contract-first hybrid integration model:

1. versioned internal REST APIs for bounded request/response operations;
2. durable asynchronous commands and events for work that must survive retries or service restarts;
3. transactional outbox and idempotent inbox processing around SQL Server state changes;
4. ASP.NET Core as the operational orchestration and persistence boundary;
5. Python as an isolated intelligence provider with no live broker credentials and no permission to mutate execution, portfolio, or risk state.

The initial implementation uses JSON over HTTPS for internal APIs. gRPC is not required for the first release and may be introduced only after measured latency or throughput evidence justifies it.

## Ownership boundary

### ASP.NET Core may

- schedule or request inference work;
- submit immutable market and context references to Python;
- validate cross-service contract versions;
- ingest and validate engine outputs;
- persist accepted operational records;
- create signals, theses, risk decisions, and trade plans through approved workflows;
- reject stale, duplicate, malformed, or unsupported outputs;
- expose execution, portfolio, risk, and operational APIs;
- publish lifecycle events through the outbox.

### Python may

- read approved market, feature, and research data through supported data-access paths;
- calculate features and engine outputs;
- return versioned intelligence results;
- run training, backtesting, attribution, and candidate evaluation;
- publish candidate recommendations for controlled review and promotion.

### Python may not

- place, modify, or cancel broker orders;
- access live broker execution credentials;
- approve risk or position size;
- mutate orders, fills, positions, portfolio, risk-policy, or active production-configuration tables;
- directly promote model, rule, feature, or weight changes into live production;
- silently rewrite previously accepted engine outputs.

## Integration flows

### Synchronous inference flow

Used when an intelligence response is expected within a bounded time window.

```text
ASP.NET Core
  -> POST versioned inference request
  -> Python validates request and contract version
  -> Python returns engine output
  -> ASP.NET Core validates freshness, lineage, and schema
  -> ASP.NET Core persists or rejects the output
```

Synchronous failure must not block risk controls or cause fallback execution. A missing intelligence response results in a controlled no-decision, degraded, or rejected state according to the active policy.

### Asynchronous work flow

Used for feature batches, scheduled analysis, training, backtesting, attribution, and other durable workloads.

```text
ASP.NET Core transaction
  -> persist work request and outbox message
  -> dispatcher delivers command
  -> Python processes idempotently
  -> Python returns versioned result/event
  -> ASP.NET Core inbox de-duplicates and persists accepted output
```

The logical contracts must remain independent of the eventual transport. The initial durable transport may be implemented with SQL Server-backed queue/outbox tables. A dedicated message broker may replace the transport later without changing domain contracts.

## Contract requirements

Every cross-runtime request, command, result, and event must include:

- `contract_version`;
- `message_id`;
- `correlation_id`;
- `causation_id` where applicable;
- `environment`;
- `source_service`;
- `source_version`;
- `generated_at_utc`;
- instrument and timeframe identity where applicable;
- expiry or validity timestamp where applicable;
- model, engine, feature, policy, and configuration versions as relevant.

Unsupported major contract versions are rejected. Minor additive changes must remain backward compatible within the same major version.

## Idempotency and delivery semantics

The platform assumes at-least-once delivery for durable messages. Exactly-once side effects are achieved through application-level idempotency.

Required controls:

- globally unique `message_id`;
- inbox uniqueness on consumer and message ID;
- idempotency key for commands that create state;
- immutable accepted engine-output identity;
- retry classification for transient and permanent failures;
- dead-letter or quarantined state for repeatedly failing messages;
- observable delivery attempts and final disposition.

A retry must not create a duplicate signal, risk decision, trade plan, or order intent.

## Persistence boundary

ASP.NET Core owns writes to operational schemas, including:

- `thesis`;
- `risk`;
- `execution`;
- `portfolio`;
- `broker`;
- `operations`;
- production configuration and promotion state.

Python services may write only to explicitly assigned analytical, staging, research, or model-artifact tables where required. Shared-table ownership is prohibited. Table ownership must be documented and enforced through separate database principals and permissions.

Python engine outputs intended for operational use must pass through the approved ingestion contract before they are treated as accepted operational state.

## Timeouts and resilience

Internal calls must define:

- connect timeout;
- request timeout;
- maximum retry count;
- exponential backoff with jitter;
- circuit-breaker policy;
- maximum message age;
- cancellation behavior;
- fallback status.

Retries are permitted only for operations defined as idempotent. A timeout is an unknown outcome, not proof of failure; the caller must reconcile using request or message identity before retrying state-changing work.

## Security

- Internal APIs require authenticated service identities.
- Transport encryption is mandatory outside local development.
- Python receives only the minimum market and context data required for its task.
- Secrets are environment-specific and never embedded in contracts, logs, or model artifacts.
- Service principals receive least-privilege database permissions.
- Live execution credentials remain accessible only to the ASP.NET Core broker-adapter boundary.

## Observability

Each integration operation must emit structured telemetry containing:

- correlation and causation IDs;
- contract and service versions;
- start and completion timestamps;
- latency;
- retry count;
- outcome status;
- rejection or failure code;
- message age;
- environment.

Logs must not contain access tokens, broker credentials, or sensitive account details.

## Alternatives considered

### Shared database with unrestricted writes from both runtimes

Rejected because it creates unclear ownership, race conditions, accidental coupling, and opportunities for intelligence code to bypass operational controls.

### Synchronous REST for every operation

Rejected because training, backtesting, batch analysis, and resilient scheduled work require durable asynchronous processing.

### Message broker for every interaction from the first release

Rejected because bounded request/response inference is simpler over an internal API and the first release does not yet require all operations to be broker-mediated.

### Direct Python-to-Upstox integration

Rejected because it violates the execution and risk boundary.

## Consequences

- Cross-runtime schemas and compatibility tests are mandatory.
- ASP.NET Core becomes the authoritative operational ingestion and orchestration boundary.
- Python remains independently deployable and scalable without access to live execution.
- Durable work can survive restarts and retries.
- Transport can evolve later without redefining domain contracts.
