# ThesisPulse AI

**Intelligent signals. Validated theses. Adaptive decisions.**

ThesisPulse AI is an AI-assisted Indian stock-market intelligence and trading platform. The platform separates market intelligence from execution so that every trade progresses through versioned signal, thesis, risk-decision, trade-plan, execution, portfolio, and P&L contracts.

## Architecture baseline

- ASP.NET Core owns execution, portfolio, risk, broker orchestration, and operational state.
- Python owns feature engineering, intelligence engines, inference, training, and backtesting.
- SQL Server is the operational source of truth for persistent workflows.
- Broker-specific behavior remains behind an Upstox adapter.
- AI engines cannot directly execute orders.
- Live losses cannot directly mutate production rules, models, weights, or risk limits.

## Product baseline

- Market universe: NIFTY 50, BANK NIFTY, FINNIFTY, and selected liquid equities.
- Trading style: five-minute intraday.
- Confirmation hierarchy: one-minute execution refinement, fifteen-minute setup confirmation, hourly regime, and daily structural context.
- Rollout: cash equities first, index futures next, options intelligence and PAPER trading before live options execution.
- Overnight exposure and multi-leg live options remain disabled until separately approved.

## Current phase

**Phase 5.6 — Windows local startup, environment configuration, and operator readiness.**

The operator-facing React application now includes read-only Market, Signals, Theses, Risk, Portfolio, P&L, Execution, and Operations workspaces. The execution workspace traces the authoritative PAPER lifecycle from Signal through Thesis, Risk, Trade Plan, Order, Fill, Position, and P&L.

## Windows quick start

Prerequisites: Git for Windows, .NET 8 SDK or later, Node.js 22 or later with npm, and PowerShell. SQL Server is required for SQL-backed persistence and the complete authoritative PAPER lifecycle.

From the repository root:

```powershell
.\scripts\dev\Test-ThesisPulsePrerequisites.ps1
.\scripts\dev\Start-ThesisPulse.ps1
```

Open `http://localhost:5173` when the launcher reports that the stack is ready.

Stop only launcher-owned processes with:

```powershell
.\scripts\dev\Stop-ThesisPulse.ps1
```

The launcher keeps PAPER mode enforced, leaves LIVE execution disabled, checks service readiness, and stores logs under `.thesispulse-dev/logs`.

See `docs/phase-5/windows-local-development.md` for first-run setup, optional migrations, service addresses, logs, Visual Studio usage, and troubleshooting.

## Repository guide

- `src/` — ASP.NET Core services, shared contracts, infrastructure, and broker adapters;
- `frontend-react/` — React operator application;
- `database/migrations/` — versioned SQL Server migrations;
- `scripts/dev/` — supported Windows local-development launcher and validation tools;
- `docs/adr/` — versioned architecture decisions;
- `docs/phase-0/PHASE-0-BASELINE.md` — accepted product and architecture baseline;
- `docs/phase-5/windows-local-development.md` — current local-development runbook;
- `contracts/` — cross-service schemas.

## Safety boundary

The current local launcher and UI are PAPER-only. They do not grant browser order controls, risk overrides, broker submission authority, automatic LIVE enablement, or destructive database operations.
