# ADR-0001: System Architecture and Technology Ownership

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture
- **Supersedes:** None

## Context

ThesisPulse AI combines deterministic operational controls with Python-based market intelligence. Without strict ownership boundaries, intelligence components could bypass risk controls, broker-specific behavior could leak into the domain, and multiple runtimes could compete for operational state.

## Decision

ThesisPulse AI adopts the following technology and responsibility boundaries.

### ASP.NET Core owns

- execution orchestration;
- portfolio and position state;
- operational risk evaluation;
- capital allocation and exposure controls;
- trade-plan authorization;
- order, fill, cancellation, and reconciliation lifecycles;
- environment controls and kill switches;
- broker connectivity through adapters;
- production configuration state;
- operational APIs and audit coordination.

### Python owns

- market-data feature engineering;
- intelligence engines;
- signal and thesis candidate generation;
- model inference;
- model training;
- research backtesting and walk-forward validation;
- trade-outcome attribution;
- candidate model, rule, and weight recommendations.

### SQL Server owns

SQL Server is the operational source of truth for:

- instruments and market-data references;
- versioned engine outputs;
- signals and theses;
- risk decisions and trade plans;
- orders, order events, fills, positions, and P&L;
- model, strategy, feature, policy, and configuration versions;
- broker reconciliation state;
- audit and decision lineage.

### Broker boundary

All Upstox-specific request models, response models, status values, instrument identifiers, errors, authentication, and transport behavior remain inside an Upstox infrastructure adapter implementing canonical broker interfaces.

### Mandatory safety constraints

1. No AI or intelligence engine may directly place, modify, or cancel an order.
2. Every executable order must reference an approved and unexpired risk decision and trade plan.
3. Python services must not receive live broker execution credentials.
4. A live trading loss may create analysis and candidate recommendations but may not directly mutate active production models, rules, weights, or risk limits.
5. Production changes require version creation, offline validation, shadow validation, and an explicit promotion decision.
6. Broker acknowledgements never replace SQL Server operational state; they are reconciled into it.

## Logical flow

```text
Market data
  -> Python features and intelligence
  -> Versioned engine output
  -> Signal
  -> Thesis
  -> ASP.NET Core risk evaluation
  -> Approved trade plan
  -> Execution policy
  -> Upstox adapter
  -> Broker order lifecycle
  -> SQL Server reconciliation
```

## Integration principles

- Cross-runtime messages use versioned canonical contracts.
- Every message carries a message ID, correlation ID, causation ID, source, source version, environment, and UTC creation timestamp.
- Consumers reject unsupported major contract versions.
- Commands are idempotent.
- Event time and processing time are recorded separately.
- Stale or incomplete intelligence output cannot be promoted into an executable trade plan.

## Alternatives considered

### Python owns the complete platform

Rejected because execution, portfolio, reconciliation, and operational risk require strongly controlled application state and should not be coupled to research and model lifecycles.

### ASP.NET Core owns intelligence and execution

Rejected because Python provides the stronger ecosystem for feature engineering, model research, training, and backtesting.

### Both runtimes write and manage all operational tables

Rejected because overlapping ownership creates concurrency ambiguity, schema drift, and unclear failure recovery.

## Consequences

### Positive

- Clear security and operational boundaries.
- Intelligence can evolve without bypassing risk.
- Broker integrations can be replaced without changing the domain.
- Complete decision lineage is possible.
- Production learning remains controlled and reversible.

### Negative

- Canonical contracts and integration testing are required.
- Additional orchestration and observability are necessary.
- Cross-runtime version compatibility must be maintained.

## Compliance checks

- Architecture tests must prevent domain/application projects from referencing Upstox SDK types.
- Python deployments must contain no live order API credentials.
- Execution endpoints must reject commands without valid trade-plan lineage.
- Production configuration tables must be writable only through controlled promotion workflows.
