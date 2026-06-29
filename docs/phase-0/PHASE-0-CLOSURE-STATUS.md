# Phase 0 Closure Status

## Completed and accepted

- Product universe boundaries and timeframe hierarchy.
- Cash-first instrument rollout and environment promotion sequence.
- ASP.NET Core/Python ownership boundary.
- SQL Server naming, precision, UTC and migration ownership policies.
- ADR-0001 through ADR-0021 architecture decisions.
- Canonical contracts from engine output through fill events, deployment, learning, operations, data quality and risk policy.
- SQL Server migrations V0001 through V0009.
- Structural verification for V0001 through V0009.
- Dedicated .NET database migrator with exclusive locking and immutable checksums.
- Local solution build and migrator acceptance.
- Migration-ledger bootstrap and zero-work repeat run.
- Deterministic seed foundation and SQLCMD batch-isolation correction.
- PAPER risk ceilings, exposure ceilings, operating modes and reset conditions.
- Measurable offline, PAPER, SHADOW and restricted-live promotion gates.
- Restricted-live capital boundary.
- Portfolio event/snapshot cross-service contract boundary.

## Local acceptance still to record

The following results must be pasted or recorded before the Phase 0 completion marker is set:

1. Corrected deterministic seed pack succeeds twice.
2. Reference seed verification returns `PASS`.
3. PAPER `risk-policy-1.0.0` is ACTIVE with one active global PAPER assignment.
4. Draft transition policy remains `DRAFT` with zero rules.
5. .NET contract validator passes.
6. Python contract validator passes with the same fixture outcomes.

## Fail-closed promotion prerequisites

These remain intentionally unresolved and do not grant execution authority:

- exact initial NSE liquid-equity symbol membership;
- active exchange holiday and special-session calendar;
- current Upstox capability re-verification;
- external broker instrument mappings;
- broker account and portfolio provisioning;
- approved active PAPER order-transition rules;
- shadow and live capital assignments.

No equity, account, portfolio or broker route may be assumed active from the Phase 0 architecture alone.

## First Phase 1 deliverables

After the local acceptance items above are recorded, Phase 1 begins with:

1. canonical `position-event`, `portfolio-snapshot` and `pnl-snapshot` schemas and fixtures;
2. active risk-policy resolver and parent/child ceiling validator;
3. environment-aware simulator account and portfolio provisioning;
4. reviewed PAPER order-transition rules;
5. transactional outbox and idempotent inbox persistence services.

## Closure rule

Phase 0 is complete when the six local acceptance results are recorded and the repository tracker is updated. Current-market and broker activation work remains a prerequisite for later promotion, not a reason to enable execution during Phase 0.
