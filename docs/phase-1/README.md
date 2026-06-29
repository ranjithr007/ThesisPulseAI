# Phase 1 — Platform Foundation

**Status:** In progress  
**Duration:** 3–4 weeks  
**Tracking issue:** #30  
**Working branch:** `phase-1/platform-foundation`

> Intelligent signals. Validated theses. Adaptive decisions.

## Objective

Establish the deployable, observable, secure, and testable foundation for ThesisPulse AI while preserving the Phase 0 architecture decisions, canonical contracts, database ownership, risk controls, and broker isolation boundaries.

## Phase 1 workstreams

1. **.NET platform** — API gateway, domain service shells, shared contracts, infrastructure, observability, health checks and tests.
2. **Python AI platform** — FastAPI shell, versioned contracts, engine interfaces, feature/fusion/thesis packages, persistence, messaging and monitoring.
3. **React frontend** — authentication shell, market navigation, signal scanner, instrument details, risk/P&L pages, paper/live banner and real-time connection framework.
4. **Infrastructure** — SQL Server migrations, distributed cache abstraction, event-bus abstraction, scheduler, configuration versioning, secret abstraction and CI.
5. **Quality and safety** — contract tests, architecture tests, integration tests, correlation IDs, fail-closed risk behaviour and paper-only execution.

## Implementation order

### Week 1 — Runtime baseline

- Create service and application project structures.
- Add versioned APIs and standard errors.
- Add structured logging and correlation IDs.
- Add `/health/live`, `/health/ready`, `/health/startup` and `/info` endpoints.
- Add the initial CI workflow.

### Week 2 — Persistence and communication

- Connect SQL Server migrations to service ownership boundaries.
- Add cache and event-bus abstractions.
- Add transactional outbox/inbox foundations.
- Add typed service clients and resilience policies.
- Validate .NET/Python contract compatibility.

### Week 3 — Frontend and operations

- Build the authenticated React shell and protected routes.
- Add signal scanner and instrument-detail routes.
- Add risk, portfolio, P&L and operations navigation.
- Add the permanent paper/live environment banner.
- Add WebSocket/SignalR connection management and operational scheduling.

### Week 4 — Hardening

- Complete unit, integration, architecture and contract tests.
- Test migrations from a clean database.
- Test idempotency, duplicate messages and dependency failures.
- Add dependency and secret scanning.
- Execute the complete Phase 1 demonstration flow.

## Non-negotiable safety rules

- Paper mode is the default and only enabled execution environment in Phase 1.
- No AI engine can directly invoke broker execution.
- Execution rejects an order intent without a valid approved risk decision.
- SQL Server remains the operational source of truth.
- Redis is never the source of truth for orders, positions, risk decisions, theses or P&L.
- Active configuration is immutable; changes require a new version.
- UTC is canonical for storage and service communication.
- Real Upstox credentials are not required during Phase 1.

## Exit gate

Phase 1 is complete only when:

- Every .NET service, the Python AI API and the React application start successfully.
- Every backend service exposes health and service-information endpoints.
- Versioned contracts work across .NET, Python and the frontend.
- SQL Server migrations succeed from a clean database.
- A mock signal flows from Python to Signal Service and appears in the frontend.
- One correlation ID traces the demonstration flow end to end.
- Live execution remains disabled and fail-closed controls are verified.
- CI passes from a clean checkout.
