# Phase 0 Intelligence Storage Foundation Summary

## Migration

`database/migrations/V0004__create_intelligence_and_signal_tables.sql`

V0004 persists the canonical intelligence layer while keeping execution and risk authority outside Python intelligence engines.

## Engine governance

### `intelligence.engines`

Registers engines by stable code and classifies each engine as one of:

- `DIRECTIONAL_VOTER`
- `META_CONTROLLER`
- `HARD_GATE`
- `LEARNING_CONTROLLER`
- `CONTEXT_PROVIDER`
- `FUSION`

Only a fusion engine may be marked as able to create canonical signals. Every intelligence engine is constrained to `can_execute_orders = 0`.

### `intelligence.engine_runs`

Captures each engine invocation with:

- environment;
- engine, configuration, feature-set and model versions;
- point-in-time data cutoff;
- correlation and causation IDs;
- start/completion state;
- input, output and warning counts;
- bounded error context.

## Canonical engine outputs

### `intelligence.engine_outputs`

Stores immutable contract snapshots with:

- contract and message identifiers;
- engine run, engine and instrument lineage;
- engine/service versions;
- timeframe and as-of timestamp;
- direction, score and confidence;
- data quality, completeness and freshness;
- expiry and fusion eligibility;
- correction revision and supersession lineage;
- raw canonical JSON and SHA-256 contract hash.

The table enforces the contract vocabularies and numeric ranges:

- score: `-1.0` to `1.0`;
- confidence/completeness: `0.0` to `1.0`;
- directions: `STRONG_LONG`, `LONG`, `NEUTRAL`, `SHORT`, `STRONG_SHORT`, `NO_SIGNAL`;
- quality: `VALID`, `DEGRADED`, `INVALID`.

A filtered unique index permits only one current revision for the same engine, instrument, timeframe and point-in-time timestamp.

### Exact input lineage

`intelligence.engine_output_market_inputs` links each output to exactly one of:

- a normalized candle revision;
- a data-quality assessment;
- a raw source observation.

Input roles distinguish primary, confirmation, context, quality and approved substitute evidence.

### Features, evidence and warnings

- `intelligence.engine_output_features`
- `intelligence.engine_output_evidence`
- `intelligence.engine_output_warnings`

These tables preserve feature versions, supporting/contradicting evidence, effective weights and warning codes without flattening them into opaque text.

## Canonical signals

### `intelligence.signals`

Stores the complete signal contract:

- approved creator engine;
- strategy and source versions;
- direction and primary timeframe;
- strength and confidence;
- entry window and price range;
- invalidation level and reason;
- expected holding period;
- initial status, validity and supersession;
- fusion policy version;
- correlation and causation IDs;
- raw canonical JSON and contract hash.

A signal remains a directional candidate. It is not a risk decision, trade plan or broker instruction.

### Signal lineage and lifecycle

- `intelligence.signal_engine_outputs` links every signal to the exact engine outputs used by fusion and records their lineage role and effective weight.
- `intelligence.signal_confirmation_timeframes` stores the unique confirmation timeframe set.
- `intelligence.signal_evidence` stores normalized supporting and contradictory evidence.
- `intelligence.signal_status_events` stores append-only status transitions with source and correlation lineage.

Supported statuses are:

- `CANDIDATE`
- `VALIDATED`
- `REJECTED`
- `EXPIRED`
- `SUPERSEDED`
- `CONSUMED`

## Verification

`database/verification/V0004__verify_intelligence_and_signal_tables.sql`

The verification script checks:

- 12 required tables;
- 20 trusted foreign keys;
- 16 required indexes, including four filtered uniqueness controls;
- 26 selected trusted check constraints;
- decimal precision for engine scores/confidence and signal prices;
- the V0004 database baseline marker.

## Local acceptance

```powershell
cd "D:\00 Projects\ThesisPulseAI"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0004__create_intelligence_and_signal_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0004__verify_intelligence_and_signal_tables.sql"
```

Repeat both commands once. Acceptance requires `PASS V0004` without duplicate-object or filtered-index errors.

## Deferred implementation

V0004 provides storage and constraints. Later application batches will add:

- reviewed engine-registry seeds;
- Python persistence adapters;
- engine-run orchestration;
- exact input-lineage writers;
- fusion and signal creation with runtime authority validation;
- signal expiration and status-transition workers;
- contract-hash and idempotency services.

## Next migration

V0005 will persist theses, thesis evidence, invalidation state and complete signal-to-thesis decision lineage.
