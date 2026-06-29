# Phase 0 — Product and Architecture Baseline

**Target duration:** 1–2 weeks  
**Started:** 2026-06-29  
**Goal:** Finalize the decisions and contracts that influence every later ThesisPulse AI module.

## Product baseline

- Initial universe: NIFTY 50, BANK NIFTY, FINNIFTY, and selected liquid equities.
- Universe membership and tradability are effective-dated and versioned.
- Primary trading timeframe: 5-minute intraday.
- Confirmation timeframes: 1-minute, 15-minute, hourly, and daily.
- Cash-equity execution is introduced before index-futures execution.
- Options begin with data, intelligence, backtesting, and paper trading; restricted-live single-leg options require separate evidence.
- Multi-leg options are excluded from the initial live scope.
- Overnight exposure is disabled until explicitly approved.

## Architecture baseline

- ASP.NET Core owns execution, portfolio, risk, and operational state.
- Python owns feature engineering, intelligence engines, inference, training, and backtesting.
- SQL Server is the operational source of truth.
- Broker-specific models remain behind an Upstox adapter.
- No AI engine can directly execute an order.
- No live loss can directly mutate production rules, models, weights, or risk limits.

## Workstreams

### Product and trading scope

- [x] Define versioned universe eligibility and instrument-state rules.
- [ ] Approve the first liquid-equity live allow-list.
- [x] Approve cash, futures, and options rollout sequence.
- [x] Define primary and confirmation timeframe responsibilities.
- [ ] Define trading-session and holiday-calendar behavior.
- [x] Define initial overnight and derivatives-scope restrictions.
- [ ] Define supported Upstox order, validity, and product capabilities through a verified broker capability matrix.

### Risk

- [ ] Review and accept ADR-0006 starting limits.
- [ ] Define sector and correlation limits.
- [ ] Define soft-limit restricted-state behavior.
- [ ] Define emergency close-only and exit policies.
- [ ] Define live capital-allocation approval.

### Environments and promotion

- [x] Define paper, shadow, and live boundaries.
- [ ] Define measurable promotion thresholds.
- [ ] Define restricted-live allocation and instrument allow-list.
- [ ] Define rollback ownership and operational approvals.

### Data and database

- [ ] Accept SQL Server schemas and naming conventions.
- [ ] Select a single database migration authority.
- [ ] Define decimal precision for price, quantity, P&L, percentages, and Greeks.
- [x] Define canonical instrument identity requirements.
- [ ] Define broker instrument mapping storage and refresh rules.
- [ ] Define UTC storage and Asia/Kolkata display/session rules.

### Contracts

- [x] Add engine-output v1 schema.
- [ ] Add signal v1 schema.
- [ ] Add thesis v1 schema.
- [ ] Add risk-decision v1 schema.
- [ ] Add trade-plan v1 schema.
- [ ] Add execution-command v1 schema.
- [ ] Add order-event and fill-event schemas.
- [ ] Validate all schemas in both .NET and Python tests.

### Execution and broker boundary

- [ ] Define canonical broker interface.
- [ ] Define order-state machine.
- [ ] Define command idempotency and duplicate-order policy.
- [ ] Define broker reconciliation workflow.
- [ ] Define stale broker-state and partial-fill behavior.
- [ ] Define credential and secret-management policy.

### Learning governance

- [ ] Define trade-outcome attribution contract.
- [ ] Define candidate model/rule/weight recommendation workflow.
- [ ] Define offline, walk-forward, paper, and shadow validation gates.
- [ ] Define production model and configuration promotion records.

## Required ADR register

| ADR | Topic | Status |
|---|---|---|
| ADR-0001 | System architecture and technology ownership | Accepted |
| ADR-0002 | Initial market and instrument universe | Accepted |
| ADR-0003 | Trading timeframes and confirmation hierarchy | Accepted |
| ADR-0004 | Cash, futures, and options rollout scope | Accepted |
| ADR-0005 | Paper, shadow, and live environment policy | Accepted |
| ADR-0006 | Capital and risk limits | Proposed |
| ADR-0007 | ASP.NET Core–Python integration model | Not started |
| ADR-0008 | SQL Server schema and naming conventions | Not started |
| ADR-0009 | Database migration ownership | Not started |
| ADR-0010 | Timestamp, timezone, and exchange calendar | Not started |
| ADR-0011 | Canonical engine-output and signal contracts | In progress |
| ADR-0012 | Thesis, risk-decision, and trade-plan contracts | Not started |
| ADR-0013 | Upstox broker-adapter boundary | Not started |
| ADR-0014 | Order idempotency and execution lifecycle | Not started |
| ADR-0015 | Model, engine, and configuration versioning | Not started |
| ADR-0016 | Live-loss learning and promotion governance | Not started |
| ADR-0017 | Audit, traceability, and decision lineage | Not started |
| ADR-0018 | Security, credentials, and secret management | Not started |
| ADR-0019 | Failure handling and kill-switch policy | Not started |
| ADR-0020 | Market-data quality, freshness, and stale-data policy | Not started |

## Exit gate

Phase 0 is complete only when:

- [ ] Every critical ADR is accepted and committed.
- [x] Initial universe eligibility and instrument-state rules are versioned.
- [ ] The first live instrument allow-list is approved.
- [ ] Risk limits are approved and represented by a versioned policy.
- [ ] Paper, shadow, restricted-live, and scaled-live rules have measurable promotion gates.
- [ ] A single SQL Server migration authority is established.
- [ ] UTC, Asia/Kolkata, and exchange-session behavior is unambiguous.
- [ ] Canonical contracts validate in ASP.NET Core and Python.
- [ ] Upstox-specific types do not leak into domain or application projects.
- [ ] AI services have no access to live execution credentials.
- [ ] Orders require approved, unexpired risk-decision and trade-plan lineage.
- [ ] Duplicate-order prevention and reconciliation policies are accepted.
- [ ] One example lifecycle is traceable end to end with correlation and causation IDs.
- [ ] Live-loss learning governance is accepted.
- [ ] The initial SQL Server migration succeeds on a clean database.
