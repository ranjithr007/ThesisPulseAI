# Phase 0 — Product and Architecture Baseline

**Target duration:** 1–2 weeks  
**Started:** 2026-06-29

## Accepted baseline

- Universe: NIFTY 50, BANK NIFTY, FINNIFTY and selected liquid equities.
- Trading style: five-minute intraday with 1-minute, 15-minute, hourly and daily context.
- Rollout: cash first, index futures next, options research and paper before live options.
- ASP.NET Core owns execution, portfolio, risk and operational state.
- Python owns features, intelligence, inference, training and backtesting.
- SQL Server is the operational source of truth.
- Upstox remains behind a canonical broker adapter.
- UTC is canonical; Indian sessions use `Asia/Kolkata` and a versioned calendar.
- Shared database changes use one script-based migration authority.
- Lifecycle: `Signal -> Thesis -> Risk Decision -> Trade Plan -> Execution Command -> Order Events -> Fill Events`.

## Workstream status

### Product

- [x] Versioned universe and instrument-state rules.
- [x] Timeframe hierarchy.
- [x] Cash, futures and options rollout sequence.
- [x] Intraday and initial overnight restrictions.
- [ ] First live liquid-equity allow-list.
- [x] Upstox capability-matrix structure and promotion gates.
- [ ] Re-verify active Upstox capabilities before restricted live.

### Architecture and integration

- [x] ASP.NET Core and Python ownership boundaries.
- [x] REST plus durable outbox/inbox integration model.
- [x] SQL Server naming, precision and schema conventions.
- [x] Centralized migration ownership.
- [x] UTC, timezone and exchange-calendar policy.
- [ ] Initial durable transport implementation.

### Risk and environments

- [x] Paper, shadow and live boundaries.
- [ ] Accept ADR-0006 starting risk limits.
- [ ] Sector and correlation limits.
- [ ] Promotion thresholds and restricted-live allocation.
- [ ] Emergency close-only and exit policy.

### Canonical contracts

- [x] Engine-output schema.
- [x] Signal schema.
- [x] Thesis schema.
- [x] Risk-decision schema.
- [x] Trade-plan schema.
- [x] Execution-command schema.
- [x] Order-event schema.
- [x] Fill-event schema.
- [x] Semantic validation rules for decision and execution lifecycles.
- [ ] Shared valid and invalid fixtures.
- [ ] Equivalent .NET and Python validation tests.

### Execution and governance

- [x] Canonical broker interface and mapping boundary.
- [x] Order state machine and idempotency policy.
- [x] Unknown-outcome reconciliation policy.
- [x] Partial-fill and actual-position quantity rules.
- [ ] Implement SQL Server execution tables and constraints.
- [ ] Implement fake and Upstox broker adapters.
- [ ] Implement reconciliation worker.
- [ ] Security and secret-management policy.
- [ ] Model and configuration versioning.
- [ ] Live-loss learning and promotion governance.
- [ ] Audit, failure handling and kill switches.

## ADR register

| ADR | Topic | Status |
|---|---|---|
| 0001 | System ownership | Accepted |
| 0002 | Market universe | Accepted |
| 0003 | Timeframe hierarchy | Accepted |
| 0004 | Instrument rollout | Accepted |
| 0005 | Environment policy | Accepted |
| 0006 | Capital and risk limits | Proposed |
| 0007 | .NET–Python integration | Accepted |
| 0008 | SQL Server conventions | Accepted |
| 0009 | Migration ownership | Accepted |
| 0010 | Time and calendar rules | Accepted |
| 0011 | Engine output and signal contracts | Accepted |
| 0012 | Thesis, risk decision and trade plan contracts | Accepted |
| 0013 | Upstox broker-adapter boundary | Accepted |
| 0014 | Order idempotency and execution lifecycle | Accepted |
| 0015–0020 | Versioning, learning, audit, security and operations | Not started |

## Exit gate

- [ ] All critical ADRs accepted.
- [ ] Risk policy approved and versioned.
- [ ] Live allow-list and promotion gates approved.
- [ ] Initial SQL Server migration succeeds on a clean database.
- [ ] All contracts validate in .NET and Python.
- [x] Architecture prevents Upstox types from entering domain and application layers.
- [ ] Runtime tests prove the broker adapter boundary.
- [x] Contract rules require complete signal-to-execution lineage.
- [x] Duplicate prevention and reconciliation policies are accepted.
- [ ] Runtime and database constraints enforce duplicate prevention.
- [ ] One complete lifecycle is traceable with correlation and causation IDs.
- [ ] Live-loss learning governance is accepted.
