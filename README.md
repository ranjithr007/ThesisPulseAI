# ThesisPulse AI

**Intelligent signals. Validated theses. Adaptive decisions.**

ThesisPulse AI is an AI-assisted Indian stock-market intelligence and trading platform. The platform separates market intelligence from execution so that every trade progresses through versioned signal, thesis, risk-decision, and trade-plan contracts before broker execution.

## Architecture baseline

- ASP.NET Core owns execution, portfolio, risk, broker orchestration, and operational state.
- Python owns feature engineering, intelligence engines, inference, training, and backtesting.
- SQL Server is the operational source of truth.
- Broker-specific behavior remains behind an Upstox adapter.
- AI engines cannot directly execute orders.
- Live losses cannot directly mutate production rules, models, weights, or risk limits.

## Initial product baseline

- Market universe: NIFTY 50, BANK NIFTY, FINNIFTY, and selected liquid equities.
- Trading style: five-minute intraday.
- Confirmation hierarchy: one-minute execution refinement, fifteen-minute setup confirmation, hourly regime, and daily structural context.
- Rollout: cash equities first, index futures next, options intelligence and paper trading before live options execution.
- Overnight exposure and multi-leg live options are disabled until separately approved.

## Current phase

Phase 0 — Product and Architecture Baseline.

See:

- `docs/phase-0/PHASE-0-BASELINE.md` for workstreams and exit gates;
- `docs/adr/` for versioned architecture decisions;
- `contracts/` for cross-service schemas.
