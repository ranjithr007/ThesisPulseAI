# Phase 0 Upstox Adapter Summary

## Completed

- ADR-0013: Upstox broker-adapter boundary.
- Current capability matrix.
- Instrument mapping and refresh policy.
- Broker reconciliation policy.

## Core decisions

- Upstox-specific types stay inside infrastructure.
- Domain and application layers use only canonical ThesisPulse AI contracts.
- Internal instrument IDs are stable; Upstox instrument keys are effective-dated mappings.
- Capability checks happen before submission.
- Broker timeouts are unknown outcomes, not automatic failures.
- SQL Server keeps immutable internal history; broker observations drive reconciliation evidence.
- Unknown or contradictory broker states become `RECONCILIATION_REQUIRED`.
- Failed or stale BOD mapping refresh blocks affected live orders.

## Initial enabled scope

- Intraday cash equities first.
- Index futures after cash restricted-live validation.
- Options remain research, paper and shadow until separately promoted.
- MTF, GTT, AMO and multi-leg live execution remain disabled initially.

## Remaining work

- Implement the canonical C# broker interfaces.
- Implement Upstox request and response translators.
- Create capability and mapping tables.
- Add sandbox, shadow and restricted-live adapter tests.
- Implement reconciliation jobs and incidents.
- Add security and secret-management controls.
