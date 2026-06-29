# ADR-0005: Paper, Shadow, and Live Environment Policy

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture and risk
- **Supersedes:** None

## Context

A trading strategy can appear profitable in research while failing under live data quality, latency, spreads, slippage, broker behavior, or operational faults. ThesisPulse AI therefore requires controlled environments and evidence-based promotion rather than direct movement from backtest to live trading.

## Decision

ThesisPulse AI will support three trading environments: `PAPER`, `SHADOW`, and `LIVE`. Environment is mandatory on every signal, thesis, risk decision, trade plan, order command, order event, fill, position, P&L record, model version, and configuration version.

## Paper environment

Paper trading validates trade lifecycle and portfolio behavior without broker execution.

Required characteristics:

- no broker order placement;
- deterministic or configurable simulated fills;
- configurable spread, slippage, brokerage, taxes, latency, and partial-fill assumptions;
- complete order, position, P&L, and risk state;
- historical replay and live-data modes;
- experimental models and candidate configurations allowed;
- synthetic failures supported for resilience testing.

Paper performance must not be presented as live performance.

## Shadow environment

Shadow trading uses live market inputs and production-equivalent decision paths but never submits broker orders.

Required characteristics:

- production-equivalent feature, signal, thesis, risk, and trade-plan flow;
- exact order intent recorded, including instrument, side, product, order type, quantity, price, stop, target, and validity;
- simulated execution compared with observable market prices;
- latency, spread, slippage, signal expiry, stale data, and rejected-plan metrics captured;
- production risk policies applied;
- broker market-data and instrument mapping validated where available;
- no execution credentials exposed to the shadow workflow.

Shadow results are the primary evidence for restricted-live promotion.

## Live environment

Live trading submits approved commands through the broker adapter.

Required characteristics:

- only approved strategy, model, feature, contract, and risk-policy versions;
- hard risk limits enforced by ASP.NET Core;
- order idempotency and duplicate prevention;
- broker reconciliation;
- complete immutable audit and decision lineage;
- operational dashboards and alerts;
- emergency kill switches;
- restricted instruments, products, order types, and capital allocation;
- no automatic production rule mutation from trade outcomes.

## Promotion path

```text
Development
  -> Backtest
  -> Paper
  -> Shadow
  -> Restricted Live
  -> Scaled Live
```

A candidate must not skip a promotion stage.

## Promotion evidence

Each promotion decision must reference a versioned evidence package containing, at minimum:

- strategy and model versions;
- feature and dataset versions;
- tested universe and trading sessions;
- sample size;
- win rate and expectancy;
- profit factor;
- maximum drawdown;
- tail-loss observations;
- transaction-cost assumptions;
- slippage and latency results;
- stale-data and missing-data behavior;
- risk-limit breaches;
- reconciliation exceptions;
- known limitations;
- approval identity and timestamp.

## Restricted-live policy

The first live release of any strategy, material model revision, or material execution-policy revision must use restricted-live controls:

- reduced capital allocation;
- reduced risk per trade;
- limited instrument allow-list;
- limited trading sessions;
- limited concurrent positions;
- enhanced monitoring;
- automatic halt on reconciliation uncertainty, repeated broker rejection, stale data, or risk-policy breach.

Scaling requires a separate promotion record.

## Rollback and halt policy

The platform must support immediate prevention of new orders independently at the following levels:

- platform;
- broker account;
- strategy;
- model version;
- instrument;
- product type;
- trading session.

A halt does not automatically close positions. Position-exit behavior must follow a separate controlled emergency-exit policy to avoid accidental liquidation.

## Data isolation

- Paper, shadow, and live records must be distinguishable through mandatory environment fields.
- Live and non-live orders must never share broker order identifiers.
- Live credentials must not be available in development, paper, or shadow workloads.
- Environment-specific configuration must be versioned and audited.

## Alternatives considered

### Backtest directly to live

Rejected because backtests cannot validate live latency, data quality, broker rejection, spread, slippage, or reconciliation.

### Paper and live only

Rejected because paper simulation does not prove production-equivalent behavior against live market conditions.

### Automatic promotion based only on performance thresholds

Rejected because operational safety, data quality, and reconciliation evidence are as important as returns.

## Consequences

- Delivery takes longer but production risk is materially reduced.
- The same canonical lifecycle must operate across all environments.
- Promotion and rollback become explicit, traceable decisions.
- Environment-specific testing and data retention are required.
