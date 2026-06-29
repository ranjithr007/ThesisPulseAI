# Phase 0 Thesis Storage Foundation Summary

## Migration

`database/migrations/V0005__create_thesis_tables.sql`

V0005 persists the falsifiable reasoning layer between a canonical signal and an independent ASP.NET Core risk decision.

A validated thesis does not authorize execution. The lifecycle remains:

```text
Signal -> Thesis -> Risk Decision -> Trade Plan -> Execution Command
```

## Tables

### `thesis.theses`

Stores the immutable canonical thesis contract with:

- exact signal and instrument lineage;
- exact market-regime engine output;
- contract, service and thesis versions;
- primary hypothesis;
- regime and thesis confidence;
- creation and expiry timestamps;
- initial lifecycle status;
- supersession lineage;
- correlation and causation IDs;
- raw canonical JSON and SHA-256 contract hash.

Only one thesis version may be current for a signal. A thesis version greater than one must identify the thesis it supersedes.

### `thesis.thesis_signal_relationships`

Records primary, supporting, contradicting and contextual signal relationships with optional effective weights. A filtered unique index allows only one primary relationship per thesis.

### `thesis.thesis_evidence`

Stores normalized supporting, contradicting and neutral evidence. Evidence must resolve to exactly one approved source form:

- engine output;
- normalized candle or data-quality assessment;
- signal;
- versioned policy reference;
- bounded external reference.

### `thesis.thesis_assumptions`

Stores ordered, explicit assumptions that can later be evaluated during outcome review.

### `thesis.thesis_invalidation_conditions`

Defines falsification conditions by type:

- `PRICE`
- `TIME`
- `VOLATILITY`
- `REGIME`
- `DATA_QUALITY`
- `OTHER`

Each condition is either a warning or a thesis-invalidating condition and may carry a price or typed threshold.

### `thesis.thesis_invalidation_events`

Stores append-only observations against invalidation conditions. States are:

- `OBSERVED`
- `WARNING`
- `TRIGGERED`
- `CLEARED`

Events retain market candle, engine-output or data-quality evidence when available, together with service, version, correlation and causation lineage.

### `thesis.thesis_scenarios`

Stores alternative scenario descriptions, fractional probabilities, the single primary scenario, optional confirmation deadline and a JSON expected path.

### `thesis.thesis_status_events`

Stores append-only thesis lifecycle events:

- `DRAFT`
- `VALIDATED`
- `REJECTED`
- `EXPIRED`
- `SUPERSEDED`

### `thesis.thesis_failure_fingerprints`

Records outcome evidence such as:

- failed scenario;
- wrong assumption;
- missed signal;
- regime-transition failure;
- incorrect weighting;
- stop-loss hit;
- timeout;
- data-quality failure.

A database check permanently requires `production_change_authorized = 0`. A failed thesis or stop-loss therefore becomes research evidence and cannot directly change production weights, models, thresholds, policies or risk limits.

## Runtime semantic rules

The thesis application service must write the thesis and child records in one transaction and verify that:

- the primary signal relationship matches `theses.signal_id`;
- signal, regime output, thesis and environment are consistent;
- the regime output belongs to an approved regime/context engine;
- a validated thesis has at least one supporting evidence record;
- a validated thesis has at least one `INVALIDATES` condition;
- scenario probabilities comply with the active scenario policy and exactly one scenario is primary;
- the initial status event matches `theses.initial_status`;
- an invalidation event references a condition belonging to the same thesis;
- expired, rejected or superseded theses cannot enter risk evaluation;
- failure fingerprints can create governed learning candidates only through the later review workflow.

## Verification

`database/verification/V0005__verify_thesis_tables.sql`

The verification checks:

- 9 required tables;
- 21 trusted foreign keys;
- 12 operational and filtered indexes;
- 26 selected trusted check constraints;
- fixed precision for confidence and invalidation prices;
- the V0005 database baseline marker.

## Local acceptance

```powershell
cd "D:\00 Projects\ThesisPulseAI"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0005__create_thesis_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0005__verify_thesis_tables.sql"
```

Repeat both commands once. Acceptance requires `PASS V0005` without duplicate-object or filtered-index errors.

## Deferred implementation

V0005 provides storage and structural controls. Later application batches will add:

- thesis contract persistence adapter;
- transactional semantic validator;
- expiry worker;
- invalidation evaluator;
- thesis status-transition service;
- failure attribution service;
- governed conversion of approved fingerprints into learning candidates;
- end-to-end signal-to-thesis integration tests.

## Next migration

V0006 will add active risk policies, immutable capital and portfolio snapshots, risk decisions and immutable trade plans.
