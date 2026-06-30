# Phase 2 — Multi-Timeframe Confirmation Foundation

## Objective

Combine current directional intelligence and market-regime context across the supported
intraday and higher-timeframe hierarchy without creating a canonical trading signal.

The confirmation engine is a `META_CONTROLLER`. It can aggregate and qualify intelligence,
but it cannot create signals, approve risk, create a trade plan, or submit an order.

## Processing path

```text
canonical candles
  -> Feature Factory snapshots
  -> directional outputs + market-regime outputs
  -> exact point-in-time timeframe pairs
  -> multi-timeframe confirmation output
  -> later fusion engine
```

The engine evaluates:

```text
1m   10%
5m   30%   primary timeframe
15m  25%   required confirmation
1h   20%   required confirmation
1d   15%
```

The required set is `5m`, `15m`, and `1h`. Missing required context returns
`IGNORED_INCOMPLETE`; no degraded substitute is promoted to fusion eligibility.

## Engine identity

```text
engine_code: THESIS_PULSE_MULTI_TIMEFRAME_CONFIRMATION
engine_role: META_CONTROLLER
owner_service: ThesisPulse.AI
engine_version: 1.0.0
policy_version: multi-timeframe-confirmation-v1.0.0
can_create_signals: false
can_execute_orders: false
```

Seed `S0011` and verification `S0007` enforce these permissions.

## Point-in-time rules

The primary cutoff is the current 5-minute directional output timestamp.

For every included timeframe:

- directional and regime outputs must refer to the same instrument;
- their timeframe values must match the requested timeframe;
- their `asOfUtc` values must be identical;
- their cutoff cannot be later than the primary 5-minute cutoff;
- both outputs must be current, fresh, valid, unexpired, and fusion-eligible.

The current provider query returns the latest output for each timeframe. If that latest output
is later than the primary cutoff, the timeframe is excluded. The engine fails closed instead
of using future context. Historical at-or-before fallback can be added later as an optimized
query without changing the confirmation contract.

## Scoring policy

Each timeframe begins with its configured base weight. Directional and regime scores are
combined as:

```text
blended score = directional score × 0.75 + regime score × 0.25
```

The effective weight is attenuated by structural and volatility context:

### Structure modifier

```text
TRENDING_UP    1.00
TRENDING_DOWN  1.00
RANGE_BOUND    0.65
TRANSITION     0.50
```

### Volatility modifier

```text
LOW      0.90
NORMAL   1.00
HIGH     0.80
EXTREME  0.60
```

The final score remains bounded to `[-1, 1]`.

```text
score >=  0.65  STRONG_LONG
score >=  0.25  LONG
score <= -0.65  STRONG_SHORT
score <= -0.25  SHORT
otherwise       NEUTRAL
```

## Eligibility gates

A confirmation output is eligible for later fusion only when:

- all required timeframes are present;
- weighted timeframe coverage is at least `0.75`;
- weighted contradiction is no more than `0.40`;
- every consumed directional and regime output passes freshness and quality validation.

A valid confirmation vote is still intelligence only. It does not authorize exposure.

## Output contract

The versioned contract contains:

- primary 5-minute cutoff;
- final direction, score, and confidence;
- alignment and contradiction scores;
- coverage and required-timeframe state;
- per-timeframe directional and regime identities;
- per-timeframe effective weights and signed contributions;
- structure and volatility context;
- warnings and evidence;
- revision and fusion-eligibility state.

Confidence combines absolute score strength, timeframe alignment, and coverage. It is not a
probability of profit.

## SQL Server lineage

Every persisted confirmation output records exact upstream dependencies in
`intelligence.engine_output_dependencies`:

```text
directional engine output -> CONFIRMATION dependency
market-regime output       -> CONTEXT dependency
```

Dependencies exist for every included timeframe. Composite foreign keys retain instrument
consistency across the lineage graph.

The complete intelligence lineage is:

```text
market candle revisions
  -> Feature Factory output
  -> directional and regime outputs
  -> multi-timeframe confirmation output
```

## Idempotency and revisions

Confirmation output identity is deterministic from:

- instrument key;
- primary 5-minute cutoff;
- exact directional and regime output UIDs;
- policy version;
- revision.

Reprocessing the same source set returns `DUPLICATE`.

A corrected upstream output for the same 5-minute cutoff creates the next confirmation
revision and supersedes the previous current output. A new 5-minute cutoff starts again at
revision zero.

## Configuration

All intelligence stages remain disabled by default.

```text
THESISPULSE_FEATURE_FACTORY_ENABLED=false
THESISPULSE_DIRECTIONAL_ENGINE_ENABLED=false
THESISPULSE_REGIME_ENGINE_ENABLED=false
THESISPULSE_CONFIRMATION_ENGINE_ENABLED=false
```

Local PAPER activation:

```powershell
$env:THESISPULSE_FEATURE_FACTORY_ENABLED = "true"
$env:THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY = $featureKey
$env:THESISPULSE_FEATURE_FACTORY_PROVIDER = "SqlServer"
$env:THESISPULSE_OPERATIONAL_DATABASE = "Driver={ODBC Driver 18 for SQL Server};Server=localhost\SQLEXPRESS;Database=ThesisPulseAI;Trusted_Connection=yes;TrustServerCertificate=yes;"
$env:THESISPULSE_DIRECTIONAL_ENGINE_ENABLED = "true"
$env:THESISPULSE_REGIME_ENGINE_ENABLED = "true"
$env:THESISPULSE_CONFIRMATION_ENGINE_ENABLED = "true"
$env:THESISPULSE_CONFIRMATION_ENGINE_VERSION = "1.0.0"
$env:THESISPULSE_CONFIRMATION_POLICY_VERSION = "multi-timeframe-confirmation-v1.0.0"
```

The application refuses startup when confirmation is enabled without both directional and
regime engines.

## APIs

```text
GET /api/v1/intelligence/confirmation/status
GET /api/v1/intelligence/confirmation/latest/{instrumentKey}
GET /api/v1/engines
```

The internal candle-processing response also includes the latest confirmation attempt after
feature, regime, and directional processing complete.

## Activation order

1. Apply database migrations through `V0013`.
2. Apply the local PAPER seed pack through `S0011`.
3. Enable Feature Factory and verify eligible snapshots for all required timeframes.
4. Enable directional and regime engines.
5. Verify matching directional and regime `asOfUtc` values per timeframe.
6. Enable the confirmation engine.
7. Confirm `/health/ready` and the confirmation status endpoint.
8. Verify output dependencies, evidence, warnings, revisions, and contract hashes.
9. Confirm this engine created no rows in `intelligence.signals`.

## Rollback

Set:

```text
THESISPULSE_CONFIRMATION_ENGINE_ENABLED=false
```

Feature, directional, and regime processing continue. Existing immutable confirmation
outputs and lineage remain available for audit and backtesting.

## Exit gate

- Aligned long and short classifications are deterministic.
- Regime bias contributes to each timeframe vote.
- Range, transition, high-volatility, and extreme-volatility modifiers reduce conviction.
- Required-timeframe absence fails closed.
- Cross-timeframe contradiction blocks fusion eligibility.
- Future or timestamp-mismatched context is never consumed.
- Duplicate source sets are idempotent.
- Corrections create revisions scoped to the same primary cutoff.
- The engine has no signal or execution authority.
- Python, .NET, database, Market Data, and React CI are green.
