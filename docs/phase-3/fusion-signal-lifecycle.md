# Phase 3.1 — Authoritative Fusion-to-Signal Lifecycle

## Purpose

Phase 3.1 establishes the authoritative boundary between deterministic Thesis/Fusion output and the existing Signal Service.

```text
eligible FusionReadyEvidenceV1
    -> deterministic ThesisFusionResultV1
    -> canonical signal.generated.v1 projection
    -> Signal Service inbox and idempotency
    -> immutable signal, evidence, status and Fusion lineage
    -> scanner read model
    -> read-only latest Risk decision projection
```

No parallel signal store is introduced. Signal Service remains the owner of signal lifecycle status and expiry.

## Projection gates

A canonical signal is projected only when:

- the Thesis decision is `CANDIDATE`;
- the Thesis result has no Fusion gate failures;
- Fusion evidence is eligible for workflow use;
- candidate, thesis, Fusion evidence, source candle and confirmation identifiers are present;
- candidate instrument and timeframe match the Fusion evidence;
- candidate direction matches the deterministic trade proposal;
- strength and confidence are bounded;
- the correlation identifier is valid;
- entry and validity windows are ordered and future-valid;
- the environment is `PAPER`.

Rejected, neutral, mismatched or malformed results return a rejected projection and create no signal.

## Deterministic identity

The candidate signal UID is preserved as the canonical signal UID. The `signal.generated.v1` message UID is deterministic from:

```text
candidate signal UID
Fusion evidence UID
signal.generated.v1 contract identity
```

Repeated projection therefore produces the same signal and message identities.

## Exact lineage

`intelligence.signal_fusion_lineage` stores one immutable lineage record per signal:

- thesis UID;
- thesis request UID;
- candidate signal UID;
- Fusion evidence UID;
- source candle message UID;
- confirmation output UID;
- confirmation message UID;
- Fusion engine version;
- Fusion policy version;
- weight configuration version.

The signal and lineage record are inserted in the same serializable SQL transaction. Duplicate retries are accepted only when stored lineage matches exactly.

## Signal authority

The authoritative creator engine is:

```text
engine_code: THESIS_PULSE_THESIS_FUSION
engine_role: FUSION
owner_service: ThesisPulse.Thesis.Service
can_create_signals: true
can_execute_orders: false
```

The Phase 1 mock Fusion engine remains only for backward compatibility. New Signal Service configuration uses the authoritative engine.

## Service APIs

### Thesis Service

```text
POST /api/v1/theses/project-signal
```

Produces a strict `FusionSignalProjectionResultV1`. A rejected projection contains reasons and no intake payload.

### Signal Service

```text
POST /api/v1/signals/fusion-intake
```

Validates the canonical signal contract and exact Fusion lineage, persists the signal through the existing Signal Service store and publishes the current signal state when real-time publication is enabled.

```text
GET /api/v1/signals/scanner
```

Supported filters:

- `instrumentKey`;
- `direction`;
- `status`;
- `minimumConfidence`;
- `generatedFromUtc`;
- `generatedToUtc`;
- `activeOnly`;
- `limit`;
- `asOfUtc`.

`activeOnly` includes only `CANDIDATE` or `VALIDATED` signals whose validity window has not elapsed at `asOfUtc`.

## Risk projection

The scanner reads the latest linked `risk.risk_decisions` row without changing it.

```text
APPROVE  -> APPROVED
REJECT   -> REJECTED
RESTRICT -> RESTRICTED
missing  -> NOT_EVALUATED
```

A missing Risk decision never becomes positive confirmation. Signal status does not imply Risk approval.

## Authority boundary

Signal Service can:

- persist canonical candidate signals;
- manage signal lifecycle transitions;
- expire elapsed signals;
- expose scanner and real-time signal views.

Signal Service cannot:

- approve or reject portfolio Risk;
- construct or approve a trade plan;
- select an executable option contract;
- mutate portfolio state;
- submit an order.

## Activation

Apply database migrations through `V0019`, then apply the local PAPER seed pack or the authoritative Fusion seed directly.

```text
database/migrations/V0019__create_signal_fusion_lineage.sql
database/seeds/intelligence/S0016__seed_thesis_fusion_signal_creator.sql
```

For SQL Server Signal Service persistence:

```text
SignalPersistence:Provider=SqlServer
SignalPersistence:CreatorEngineCode=THESIS_PULSE_THESIS_FUSION
ConnectionStrings:OperationalDatabase=<SQL Server connection>
```

The in-memory provider preserves equivalent projection, lineage, idempotency, scanner and active-window behavior for local PAPER tests.

## Tests

The CI slice verifies:

- eligible deterministic projection;
- rejected Thesis exclusion;
- canonical signal contract validity;
- deterministic message identity;
- exact Fusion lineage;
- retry idempotency;
- changed-lineage duplicate rejection;
- scanner filtering and active-only expiry behavior;
- `NOT_EVALUATED` Risk default;
- database migration and seed authority checks;
- all existing Market Data, Risk, trade-plan, execution, portfolio, PAPER workflow, Python and React regressions.
