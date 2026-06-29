# Phase 0 Operational Storage Summary

## Migration

`database/migrations/V0009__create_operational_foundation_tables.sql`

V0009 completes the initial SQL Server operational foundation for durable integration, job execution, fail-safe controls, incident handling, alerts and audit evidence.

## Durable transport

### `operations.outbox_messages`

Stores messages in the same transaction as the operational state change that created them. It includes contract and service versions, environment, message identity, destination, aggregate lineage, idempotency identity, correlation and causation IDs, validity timestamps, canonical JSON, hash, retry limits, lease state and final disposition.

A filtered unique index prevents duplicate state-creating messages when an idempotency key is supplied.

### `operations.outbox_delivery_attempts`

Stores every delivery attempt and its dispatcher, timing, transport outcome, error, retry schedule and redacted response metadata.

### `operations.inbox_messages`

Provides consumer-and-message uniqueness for at-least-once delivery. Duplicate deliveries return the existing result rather than repeating the side effect.

### `operations.inbox_processing_attempts`

Stores every processing attempt, retry classification, failure and result reference.

Messages are leased during active work. Leases, bounded attempts and terminal states prevent indefinite processing loops.

## Scheduled jobs

### `operations.scheduled_jobs`

Stores versionable scheduler configuration for cron, interval, exchange-calendar and on-demand jobs. It includes concurrency, misfire, retry, timeout, lease and next-run policies.

### `operations.job_runs`

Stores one idempotent run per job and trigger identity with optimistic concurrency, lease ownership, heartbeat, input/output evidence and terminal result.

### `operations.job_run_events`

Stores append-only lifecycle events such as queued, leased, started, heartbeat, retry, success, failure, timeout, dead-letter and recovery.

## Incidents

### `operations.incidents`

Stores current incident state with:

- `INFO`, `WARNING`, `MAJOR` or `CRITICAL` severity;
- affected scope and environment;
- detection source;
- first and latest occurrence;
- operating-mode changes;
- reconciliation state;
- owner, acknowledgement, containment, resolution and prevention actions;
- optimistic concurrency.

### `operations.incident_events`

Stores append-only incident history. Automatic or operator containment, reconciliation, escalation, resolution and reopening remain independently auditable.

### `operations.incident_entity_links`

Links an incident to orders, positions, services, jobs, instruments, strategies, versions and other stable references without weakening schema ownership boundaries.

## Operational controls and kill switches

### `operations.operational_controls`

Stores the immutable `operational-control` v1 activation contract:

- control and policy versions;
- environment, type and scope;
- operating mode;
- reason and trigger source;
- activation, expiry and actor;
- approval requirement;
- incident and correlation lineage;
- canonical JSON and SHA-256 hash.

Supported modes are:

- `NORMAL`
- `RESTRICTED`
- `CLOSE_ONLY`
- `PAUSED`
- `HALTED`
- `RECOVERY`

### `operations.operational_control_states`

Stores the current status of each immutable activation: `ACTIVE`, `EXPIRED`, `RESET` or `SUPERSEDED`.

### `operations.operational_control_events`

Stores append-only activation, mode-change, extension, expiry, reset and supersession events.

### `operations.operational_control_approvals`

Stores activation/reset governance. Independent approval can be required, and the approving actor cannot equal the triggering actor when that requirement applies.

### `operations.scope_operating_states`

Stores the materialized result of evaluating all applicable controls for one scope. The runtime evaluator applies the most restrictive active control. A scope that permits new exposure must also preserve risk-reducing exits.

Hard controls block new exposure while keeping approved risk-reducing or emergency exits available according to policy.

## Alerts

### `operations.alerts`

Stores current alert aggregation by environment and alert key, including severity, scope, occurrence count, acknowledgement, resolution, suppression, incident linkage and redacted details.

### `operations.alert_deliveries`

Stores every email, SMS, push, Slack, webhook or dashboard delivery attempt and provider outcome.

## Audit evidence

### `audit.audit_events`

Stores immutable administrative and operational audit events with:

- partition and ordered sequence;
- event and action type;
- environment and primary entity;
- actor identity and service version;
- reason and approval reference;
- redacted before/after JSON;
- event, persistence, correlation and causation timestamps;
- incident and operational-control lineage;
- previous and current content hashes.

The partition sequence and previous hash support deterministic ordering and tamper-evident chains without depending on timestamps alone.

### `audit.audit_event_entity_links`

Adds parent, child, affected, approval, evidence and related-record references to the primary audit event.

## Runtime semantic rules

ASP.NET Core services must enforce that:

- business-state change and outbox creation commit in one transaction;
- inbox side effect and processed result commit in one transaction;
- lease acquisition uses atomic compare-and-update semantics;
- retries occur only for classified, idempotent operations;
- unknown state-changing outcomes are reconciled before retry;
- dead-letter messages and exhausted jobs create alerts or incidents;
- job overlap obeys the configured concurrency policy;
- scheduler recovery does not silently submit live orders;
- the most restrictive applicable operational control wins;
- no degraded mode can increase exposure;
- high-severity live resets use independent approval;
- recovery requires reconciliation, health, data, deployment and risk verification;
- stale evidence cannot clear a control or reconciliation state;
- incident and alert deduplication uses stable keys and configured windows;
- sensitive tokens, credentials and full account values are redacted before persistence;
- audit hashes are calculated over canonical content and previous-chain identity;
- corrections create compensating events instead of rewriting history.

## Verification

`database/verification/V0009__verify_operational_foundation_tables.sql`

The verification checks:

- 19 required tables;
- 18 trusted foreign keys;
- 21 operational and filtered indexes;
- selected trusted transport, lifecycle, control, incident, alert and audit constraints;
- eight mutable projections with `rowversion`;
- the V0009 database baseline marker.

## Local acceptance

```powershell
cd "D:\00 Projects\ThesisPulseAI"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0009__create_operational_foundation_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0009__verify_operational_foundation_tables.sql"
```

Repeat both commands once. Acceptance requires `PASS V0009` without duplicate-object, filtered-index, foreign-key or trusted-constraint errors.

## Deferred implementation

V0009 provides storage and structural controls. Later application batches will add:

- transactional outbox writer and dispatcher;
- idempotent inbox middleware;
- SQL Server-backed scheduler and lease recovery;
- job retry and dead-letter workers;
- operational-control evaluator and scope-state projector;
- kill-switch APIs with authorization and approval workflow;
- incident and alert services and delivery adapters;
- audit canonicalization and hash-chain writer;
- startup readiness and recovery orchestration;
- end-to-end duplicate-message, lease-loss, outage, kill-switch and reset tests.

## Phase 0 database sequence complete

V0001 through V0009 now define the initial shared SQL Server storage baseline. The next engineering batch should move from schema foundation to reviewed reference/policy seeds and ASP.NET Core persistence services rather than adding another broad foundation migration.
