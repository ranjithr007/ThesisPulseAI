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

**Phase 5.8 — candidate-thesis lineage bridge for automatic PAPER execution.**

The modern automatic path now preserves the candidate-thesis UID from `intelligence.signal_fusion_lineage` and the risk-decision UID from `risk.signal_risk_evaluations` without fabricating legacy thesis or risk rows. Trade-plan persistence, execution authorization, lifecycle views, and acceptance evidence support both the modern lineage and existing legacy records.

The operator-facing React application includes read-only Market, Signals, Theses, Risk, Portfolio, P&L, Execution, and Operations workspaces. The Execution workspace traces the authoritative PAPER lifecycle and evaluates correlation, causation, stage completion, quantities, position/P&L evidence, freshness, and applicable operational controls as `PASS`, `FAIL`, or `INCOMPLETE`.

## Windows quick start

Prerequisites: Git for Windows, .NET 8 SDK or later, Node.js 22 or later with npm, and PowerShell. SQL Server is required for SQL-backed persistence and the complete authoritative PAPER lifecycle.

From the repository root:

```powershell
.\scripts\dev\Test-ThesisPulsePrerequisites.ps1
.\scripts\dev\Start-ThesisPulse.ps1
```

Open `http://localhost:5173` when the launcher reports that the stack is ready.

Validate one authoritative lifecycle using its correlation UID:

```powershell
.\scripts\dev\Test-ThesisPulsePaperLifecycle.ps1 `
  -CorrelationUid <correlation-guid>
```

The lifecycle command exits successfully only when the authoritative acceptance outcome is `PASS`. Missing or unfinished evidence returns `INCOMPLETE`; violated safety invariants return `FAIL`.

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

The current local launcher, automatic candidate-thesis bridge, acceptance proof, and UI are PAPER-only. They do not grant browser order controls, risk overrides, operational-control resets, broker submission authority, automatic LIVE enablement, or destructive database operations.
