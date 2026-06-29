# ThesisPulse AI

**Intelligent signals. Validated theses. Adaptive decisions.**

ThesisPulse AI is an AI-assisted Indian stock-market intelligence and trading platform. The platform separates market intelligence from execution so that every trade progresses through versioned signal, thesis, risk-decision, and trade-plan contracts before broker execution.

## Architecture baseline

- ASP.NET Core owns execution, portfolio, risk, broker orchestration, and operational state.
- Python owns feature engineering, intelligence engines, inference, training, and backtesting.
- SQL Server is the operational source of truth.
- Broker-specific behavior remains behind an Upstox adapter.
- AI engines cannot directly execute orders.
- Live losses cannot directly mutate production rules, models, or weights.

## Current phase

Phase 0 — Product and Architecture Baseline.

See `docs/adr/` for versioned architecture decisions and `contracts/` for cross-service schemas.
