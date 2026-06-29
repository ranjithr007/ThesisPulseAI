# ADR-0012: Thesis, Risk Decision and Trade Plan Contracts

- **Status:** Accepted
- **Date:** 2026-06-29

## Context

A trading signal is not sufficient for execution. ThesisPulse AI requires explicit reasoning, deterministic risk evaluation and an immutable executable plan.

## Decision

The canonical lifecycle is:

```text
Signal -> Thesis -> Risk Decision -> Trade Plan -> Execution Command
```

Each stage is a separate versioned contract with its own identity, validity, status and lineage.

## Thesis

A thesis explains why a signal may succeed and how it can fail. It references one signal and records market regime, primary hypothesis, supporting evidence, contradicting evidence, assumptions, scenarios, invalidation conditions, confidence and expiry.

A thesis may be `DRAFT`, `VALIDATED`, `REJECTED`, `EXPIRED` or `SUPERSEDED`.

A validated thesis does not authorize execution.

## Risk decision

ASP.NET Core creates the risk decision from the signal, thesis, capital snapshot, portfolio snapshot and active policy version.

The decision records approval or rejection, reason codes, approved risk, approved quantity, exposure, margin context, current loss and drawdown state, evaluation time and expiry.

Canonical decisions are `APPROVE`, `REJECT` and `RESTRICT`.

A risk decision is immutable. Material changes to price, portfolio, capital, policy or data freshness require a new evaluation.

## Trade plan

A trade plan is created only from an approved and unexpired risk decision. It defines the permitted execution envelope:

- instrument and side;
- entry order type and permitted price range;
- approved quantity;
- stop-loss;
- one or more targets;
- maximum slippage;
- time in force;
- session and expiry constraints;
- exit policy;
- execution-policy version.

The execution layer may improve price or reduce quantity, but cannot increase quantity, widen risk or remove the stop without a new risk decision and trade plan.

## Lineage

Every trade plan references its risk decision, thesis and signal. Every execution command references one trade plan. Correlation and causation IDs connect the full lifecycle.

## Validity

- Expired signals, theses, risk decisions and trade plans cannot be reused.
- A rejected stage cannot be bypassed by a later stage.
- A superseded record remains available for audit.
- Environment must remain consistent throughout the lifecycle.
- Contract and policy versions are retained with every record.

## Numeric rules

Prices, quantities, capital, margin, P&L and risk use decimal values. Ratios and percentages use fractional representation unless explicitly named otherwise.

## Alternatives considered

### Put thesis, risk and execution fields in one signal record

Rejected because it mixes intelligence, approval and execution responsibilities.

### Allow execution to recalculate quantity or stop

Rejected because it would bypass the approved risk decision.

## Consequences

The lifecycle is more explicit and requires additional records, but every order can be traced to the exact signal, reasoning, policy and approved execution envelope that produced it.
