# ADR-0019: Failure Handling and Kill-Switch Policy

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, risk, execution and operations

## Context

Trading failures can arise from stale data, broker outages, reconciliation conflicts, risk breaches, clock drift, service degradation, invalid model behavior or credential compromise. The platform needs deterministic fail-safe behavior rather than ad hoc retries or operator guesswork.

## Decision

ThesisPulse AI uses explicit operating modes, versioned failure policies and independent kill switches. Safety controls fail closed for new exposure while preserving the ability to reduce or exit existing exposure.

## Operating modes

Canonical operating modes are:

- `NORMAL`: approved new entries and exits are permitted;
- `RESTRICTED`: reduced universe, capital, order types or size;
- `CLOSE_ONLY`: no new exposure; risk-reducing actions remain permitted;
- `PAUSED`: strategy or subsystem stops creating new decisions;
- `HALTED`: execution submission is disabled except approved emergency exits;
- `RECOVERY`: reconciliation and health verification are in progress.

Mode transitions are audited and include scope, cause, actor, policy version and expiry or reset condition.

## Kill-switch scopes

Kill switches may apply to:

- entire platform;
- environment;
- broker account;
- strategy;
- model or engine version;
- instrument or instrument class;
- market segment;
- execution action type;
- new entries only.

The most restrictive applicable control wins.

## Automatic triggers

Versioned policies may activate controls for:

- daily, weekly, strategy or portfolio loss breach;
- drawdown breach;
- repeated loss threshold;
- stale or missing mandatory market data;
- market-data integrity failure;
- broker authentication or availability failure;
- unresolved order outcome;
- position or fill reconciliation conflict;
- abnormal reject, slippage or partial-fill rate;
- clock skew;
- invalid deployment manifest or checksum;
- model output outside validated bounds;
- service health or dependency failure;
- suspected secret compromise;
- exchange or regulatory restriction.

## Fail-safe behavior

- Mandatory risk or data validation failure rejects new entries.
- Broker submission timeout enters reconciliation and blocks duplicate submission.
- Loss of intelligence services does not create fallback trades.
- Loss of market data blocks new exposure for affected scope.
- Existing protective orders and exits remain active where safe.
- A broken stop-protection path escalates immediately and may trigger emergency flattening according to policy.
- No automatic action may increase exposure during degraded operation.

## Emergency exits

Emergency exit policies define:

- who or what may initiate the exit;
- affected accounts, strategies and instruments;
- permitted order types;
- quantity derived from reconciled actual positions;
- price and slippage protection;
- retry and unknown-outcome behavior;
- operator notification;
- post-action reconciliation.

An `exit all` broker capability is never called directly by strategy code. It is available only through a separately authorized operational workflow.

## Reset and recovery

A hard kill switch does not reset automatically unless the policy explicitly allows it for a low-risk transient condition.

Recovery requires:

1. cause identified or safely contained;
2. broker orders, fills and positions reconciled;
3. data freshness and integrity restored;
4. clocks and dependencies healthy;
5. active deployment manifest verified;
6. risk and capital snapshots refreshed;
7. required approval recorded;
8. controlled transition through `RECOVERY` or `RESTRICTED` before `NORMAL`.

The actor who triggered a manual high-severity halt cannot be the sole approver for live reset.

## Retry policy

Failures are classified as permanent, transient before side effect, unknown after possible side effect, or invariant violation.

- Permanent failures are not retried.
- Proven pre-side-effect transient failures may be retried with bounded backoff.
- Unknown outcomes require reconciliation.
- Invariant violations quarantine the workflow and create an incident.
- Retry exhaustion produces a terminal or reconciliation state; it never loops indefinitely.

## Incident handling

Severity levels are `INFO`, `WARNING`, `MAJOR` and `CRITICAL`.

An incident records:

- affected scope;
- detection source;
- first and latest occurrence;
- operating-mode change;
- orders, positions, strategies and versions involved;
- containment actions;
- reconciliation status;
- owner and acknowledgements;
- resolution and prevention actions.

## Testing

Before restricted live, the platform must test:

- global and scoped kill switches;
- close-only behavior;
- broker outage and timeout;
- market-data outage;
- stale data;
- database or queue interruption;
- duplicate messages;
- order reconciliation conflict;
- secret revocation;
- recovery and reset approval;
- emergency exit using a fake broker and controlled live probe.

## Alternatives considered

### Stop the entire application process

Rejected as the primary mechanism because protective exits, reconciliation and monitoring may still be needed.

### Automatically resume after every transient recovery

Rejected because unresolved broker or position state can make automatic resumption unsafe.

## Consequences

- Failure handling becomes explicit and testable.
- New exposure is blocked during uncertainty while exits remain available.
- Operational recovery requires reconciliation and accountable approval.
