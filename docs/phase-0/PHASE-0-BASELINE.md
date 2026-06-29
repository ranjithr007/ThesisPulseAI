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
- Lifecycle: `Signal -> Thesis -> Risk Decision -> Trade Plan -> Execution Command -> Order Events -> Fill Events -> Position/P&L Events`.
- Live outcomes create governed learning candidates; they never mutate production directly.
- Audit and decision lineage are immutable and queryable end to end.
- Durable messages use at-least-once delivery with transactional outbox and idempotent inbox processing.
- Failure controls block new exposure while preserving approved exits.
- The most restrictive applicable operational control wins.
- Stale or invalid mandatory market data cannot create new exposure or risk-eligible valuations.
- Risk ceilings are defined by accepted policy `risk-policy-1.0.0`.
- Broker submission is idempotent and unknown outcomes require reconciliation.
- Portfolio state is derived from accepted fills; broker positions remain reconciliation evidence.

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
- [x] Author and locally verify V0001 business schemas and migration metadata.
- [x] Author and locally verify V0002 versioned reference tables.
- [x] Author and locally verify V0003 market observations, candles, ingestion state and quality assessments.
- [x] Author and locally verify V0004 engine registry, outputs, evidence and canonical signals.
- [x] Author and locally verify V0005 immutable theses and falsification lifecycle.
- [x] Author and locally verify V0006 risk policies, snapshots, decisions and trade plans.
- [x] Author and locally verify V0007 execution commands, orders, events, fills and reconciliation evidence.
- [x] Author V0008 portfolios, positions, lots, cash/exposure ledgers, valuations and P&L.
- [x] Add V0008 structural verification script.
- [ ] Execute V0008 locally twice and confirm verification passes.
- [x] Author V0009 durable transport, jobs, controls, incidents, alerts and audit storage.
- [x] Add V0009 structural verification script.
- [ ] Execute V0009 locally twice and confirm verification passes.
- [x] Complete the initial V0001–V0009 shared SQL Server storage design.
- [ ] Implement the initial durable transport services.

### Risk and environments

- [x] Paper, shadow and live boundaries.
- [x] Accept ADR-0006 starting risk limits.
- [x] Add a versioned risk-policy contract.
- [x] Implement immutable risk-policy storage and active-scope assignment.
- [x] Implement immutable capital and portfolio snapshot storage.
- [x] Implement risk-decision and trade-plan storage controls.
- [ ] Seed and approve `risk-policy-1.0.0` in SQL Server.
- [ ] Sector and correlation limits.
- [x] Promotion sequence from offline to scaled live.
- [ ] Strategy-specific measurable promotion thresholds.
- [x] Close-only, pause, halt and recovery operating modes.
- [x] Emergency exit and kill-switch architecture policy.
- [x] Implement operational-control, approval and effective-scope storage.
- [ ] Restricted-live capital allocation.

### Canonical contracts

- [x] Engine-output schema.
- [x] Signal schema.
- [x] Thesis schema.
- [x] Risk-decision schema.
- [x] Trade-plan schema.
- [x] Execution-command schema.
- [x] Order-event schema.
- [x] Fill-event schema.
- [x] Deployment-manifest schema.
- [x] Learning-candidate schema.
- [x] Operational-control schema.
- [x] Data-quality-assessment schema.
- [x] Risk-policy schema.
- [x] Semantic validation rules for decision and execution lifecycles.
- [x] Initial shared valid and invalid fixture manifest.
- [x] Equivalent .NET and Python schema-validation runners.
- [ ] Define canonical portfolio, position-event and P&L snapshot contracts if they cross service boundaries.
- [ ] Expand fixtures to every contract and semantic rule.
- [ ] Confirm equivalent local .NET and Python results.
- [ ] Add automated CI validation later; CI is intentionally deferred during local development.

### Execution, portfolio and governance

- [x] Canonical broker interface and mapping boundary.
- [x] Order state machine and idempotency policy.
- [x] Unknown-outcome reconciliation policy.
- [x] Partial-fill and actual-position quantity rules.
- [x] Implement canonical broker-account storage without credentials.
- [x] Implement immutable execution-command and status-event storage.
- [x] Implement current command and order projections with optimistic versions.
- [x] Implement append-only order events and event quarantine storage.
- [x] Implement idempotent fill storage using broker IDs or fingerprints.
- [x] Implement redacted broker-request and reconciliation evidence storage.
- [x] Implement current position projections with optimistic versions.
- [x] Implement idempotent fill-to-position event storage.
- [x] Implement lot opening, closure and realized-P&L storage.
- [x] Implement current cash/exposure projections and append-only ledgers.
- [x] Implement valuation marks, position valuations and immutable P&L snapshots.
- [x] Implement broker-position observations and append-only reconciliation resolution.
- [x] Implement durable inbox/outbox and transport-attempt storage.
- [x] Implement scheduled-job, leased-run and run-event storage.
- [x] Implement incident, incident-event and entity-link storage.
- [x] Implement immutable operational-control activations, approvals and current-state projections.
- [x] Implement alert aggregation and delivery-attempt storage.
- [x] Implement hash-linked audit-event and entity-lineage storage.
- [x] Model, engine, feature and configuration versioning policy.
- [x] Immutable deployment manifests and deterministic rollback.
- [x] Live-loss attribution and candidate-learning governance.
- [x] Champion-challenger and staged promotion policy.
- [x] Audit and decision-lineage policy.
- [x] Security, credential and secret-management policy.
- [x] Failure handling, operating modes and kill-switch policy.
- [x] Market-data quality, freshness and stale-data policy.
- [x] Implement SQL Server schema ownership boundaries and migration metadata tables.
- [x] Implement SQL Server reference-table foundation.
- [x] Implement SQL Server market-data and data-quality storage foundation.
- [x] Implement SQL Server engine-output and canonical-signal storage foundation.
- [x] Implement SQL Server thesis and falsification-lifecycle storage foundation.
- [x] Implement SQL Server risk and trade-plan storage foundation.
- [x] Implement SQL Server execution and reconciliation storage foundation.
- [x] Implement SQL Server portfolio and P&L storage foundation.
- [x] Implement SQL Server operational and audit storage foundation.
- [ ] Add reviewed exchange, calendar, universe, broker, account and portfolio seeds.
- [ ] Seed approved risk and order-transition policies.
- [ ] Implement market-data ingestion and candle-normalization services.
- [ ] Implement engine registry seeds and intelligence persistence adapters.
- [ ] Implement fusion and signal-creation service with engine-authority validation.
- [ ] Implement thesis creation, validation, expiry and invalidation workers.
- [ ] Implement failure-fingerprint review to learning-candidate workflow.
- [ ] Implement active risk-policy resolver and parent-ceiling validation.
- [ ] Implement immutable capital and portfolio snapshot writers.
- [ ] Implement deterministic risk evaluation and trade-plan services.
- [ ] Implement command idempotency, durable outbox and order projection services.
- [ ] Implement idempotent inbox processing and dead-letter workflow.
- [ ] Implement SQL-backed scheduler, leases, heartbeat and recovery workers.
- [ ] Implement operational-control evaluator and effective-scope projector.
- [ ] Implement kill-switch APIs, authorization and reset approvals.
- [ ] Implement incident, alert-delivery and audit hash-chain services.
- [ ] Implement fake and Upstox broker adapters.
- [ ] Implement transactional fill, lot, cash and exposure processor.
- [ ] Implement mark-to-market and portfolio P&L workers.
- [ ] Implement protective-exit synchronization using actual filled quantity.
- [ ] Implement execution and position reconciliation schedulers and operator workflows.
- [ ] Implement model registry and deployment manifest storage.
- [ ] Implement learning-candidate validation jobs.
- [ ] Implement secret manager and production service identities.

## ADR register

| ADR | Topic | Status |
|---|---|---|
| 0001 | System ownership | Accepted |
| 0002 | Market universe | Accepted |
| 0003 | Timeframe hierarchy | Accepted |
| 0004 | Instrument rollout | Accepted |
| 0005 | Environment policy | Accepted |
| 0006 | Capital and risk limits | Accepted |
| 0007 | .NET–Python integration | Accepted |
| 0008 | SQL Server conventions | Accepted |
| 0009 | Migration ownership | Accepted |
| 0010 | Time and calendar rules | Accepted |
| 0011 | Engine output and signal contracts | Accepted |
| 0012 | Thesis, risk decision and trade plan contracts | Accepted |
| 0013 | Upstox broker-adapter boundary | Accepted |
| 0014 | Order idempotency and execution lifecycle | Accepted |
| 0015 | Model, engine and configuration versioning | Accepted |
| 0016 | Live-loss learning and promotion governance | Accepted |
| 0017 | Audit, traceability and decision lineage | Accepted |
| 0018 | Security, credentials and secret management | Accepted |
| 0019 | Failure handling and kill-switch policy | Accepted |
| 0020 | Market-data quality and freshness policy | Accepted |

## Exit gate

- [x] All architecture ADRs are accepted.
- [x] Initial risk policy is approved and versioned as a contract.
- [ ] Seed and activate the accepted policy in SQL Server.
- [ ] Live allow-list, capital allocation and measurable promotion gates approved.
- [ ] Sector, correlation, margin and notional exposure extensions approved.
- [ ] V0001 through V0009 succeed on a clean database and pass repeat execution.
- [ ] All contracts validate locally in .NET and Python; CI automation is deferred.
- [x] Architecture prevents Upstox types from entering domain and application layers.
- [ ] Runtime tests prove the broker adapter boundary.
- [x] Contract rules require complete signal-to-execution lineage.
- [x] Database storage enforces core command, order-event, fill, position-event and message uniqueness.
- [x] Database storage supports scoped operating modes, accountable reset approval and immutable audit evidence.
- [ ] Runtime tests prove no duplicate broker, position, cash, P&L or message side effects across retries.
- [ ] Runtime tests prove partial-fill, reversal, lot closure and broker-position reconciliation.
- [ ] Runtime tests prove kill-switch precedence, close-only behavior, lease recovery and independent reset approval.
- [x] Model, engine and configuration versions are required for reproducibility.
- [x] Live-loss learning governance is accepted.
- [x] End-to-end audit and lineage requirements are accepted.
- [x] Security, failure, kill-switch and stale-data policies are accepted.
- [ ] One implemented lifecycle is traceable with correlation and causation IDs through P&L and operational controls.
