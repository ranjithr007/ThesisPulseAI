# ADR-0017: Audit, Traceability and Decision Lineage

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, risk, execution and operations

## Context

Every signal, thesis, risk decision, trade plan, execution command, order, fill, model promotion and operational override must be explainable after the fact. Mutable logs alone are not sufficient because they may be incomplete, unstructured or unavailable during incident review.

## Decision

ThesisPulse AI maintains immutable, queryable lineage across the complete decision and execution lifecycle.

```text
Market Snapshot
  -> Engine Outputs
  -> Signal
  -> Thesis
  -> Risk Decision
  -> Trade Plan
  -> Execution Command
  -> Order Events
  -> Fill Events
  -> Position and P&L
  -> Outcome Attribution
  -> Learning Candidate
  -> Promotion or Rejection
```

## Required lineage

Every record carries, where applicable:

- stable record identity;
- contract version;
- correlation ID;
- causation ID;
- parent record IDs;
- environment;
- instrument and timeframe;
- event, generated, received and persisted timestamps;
- source service and service version;
- engine, model, feature, strategy and configuration versions;
- risk and execution policy versions;
- universe, calendar and broker-capability versions;
- user, service or pipeline identity responsible for the action.

## Audit events

Administrative and operational changes create append-only audit events for:

- configuration activation or rollback;
- model and policy promotion;
- risk-limit changes;
- universe or capability changes;
- secret rotation metadata;
- manual suspension or resume;
- close-only activation;
- kill-switch activation or reset;
- reconciliation decisions;
- incident actions;
- data correction acceptance;
- privileged access.

An audit event stores before and after references or redacted values, reason code, actor, timestamp, approval record and correlation context.

## Immutability

Operational history is never silently rewritten. Corrections create compensating or superseding records. Current-state tables are projections and may be rebuilt from accepted events and immutable source records.

Deletion of records referenced by a trade, decision, incident or promotion is prohibited. Retention and archival policies must preserve retrievability and checksum verification.

## End-to-end trace

Given any fill or position change, the platform must be able to retrieve:

1. the originating trade plan;
2. the approved risk decision;
3. the thesis and signal;
4. all contributing engine outputs and market snapshots;
5. active model, feature, strategy and policy versions;
6. execution commands and broker observations;
7. all order and fill events;
8. applicable operational overrides and incidents.

The same trace must work in reverse from signal to realized outcome.

## Integrity

- Audit and event records include content checksum where appropriate.
- Event ordering uses version and sequence, not timestamps alone.
- Duplicate events are de-duplicated by stable identity.
- Missing lineage is a validation failure for operational promotion.
- Broken references trigger an incident and may block new exposure.

## Access and privacy

Audit visibility follows least privilege. Sensitive values such as access tokens, private keys and full account identifiers are never written to audit payloads. Redaction must preserve enough context to identify the affected secret or account without exposing it.

## Alternatives considered

### Depend only on application logs

Rejected because logs are not a reliable relational source for full decision reconstruction.

### Store only the latest state

Rejected because historical reasoning, corrections and broker discrepancies would be lost.

## Consequences

- Storage and indexing requirements increase.
- Incident analysis, compliance review and model attribution become deterministic.
- Every trade can be reconstructed from market evidence through realized outcome.
