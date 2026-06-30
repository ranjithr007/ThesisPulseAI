# Phase 2 — Deterministic Directional Intelligence Foundation

## Objective

Consume eligible point-in-time Feature Factory snapshots and produce a versioned technical directional vote with score, confidence, evidence, warnings, revision history, and exact upstream lineage.

This engine is a `DIRECTIONAL_VOTER`. It cannot create a canonical signal, approve risk, create a trade plan, or submit an order.

## Processing path

```text
closed canonical candle
  -> Feature Factory snapshot
  -> feature eligibility gate
  -> technical directional engine
  -> intelligence.engine_outputs
  -> intelligence.engine_output_dependencies
  -> intelligence.engine_output_evidence
  -> intelligence.engine_output_warnings
```

The directional engine runs only after the Feature Factory transaction commits. SQL Server remains the operational source of truth.

## Engine identity

```text
engine_code: THESIS_PULSE_TECHNICAL_DIRECTION
engine_role: DIRECTIONAL_VOTER
owner_service: ThesisPulse.AI
engine_version: 1.0.0
policy_version: technical-direction-v1.0.0
can_create_signals: false
can_execute_orders: false
```

Seed `S0009` and verification `S0005` enforce these permissions.

## Scoring policy

The initial deterministic score is bounded to `[-1, 1]`.

| Component | Weight |
|---|---:|
| Feature Factory trend score | 0.35 |
| 5/20 trend spread | 0.20 |
| 3-period return and 5-period momentum | 0.20 |
| Close-location value | 0.10 |
| Volume confirmation | 0.10 |
| One-period return | 0.05 |

Direction thresholds:

```text
score >=  0.65  STRONG_LONG
score >=  0.25  LONG
score <= -0.65  STRONG_SHORT
score <= -0.25  SHORT
otherwise       NEUTRAL
```

Confidence combines absolute directional strength and component agreement. It is not a probability of profit and must not be interpreted as risk approval.

## Required features

The engine requires:

```text
trend_score
trend_spread_5_20
close_return_1
close_return_3
momentum_5
close_location_value
volume_ratio_20
```

The source snapshot must be:

- current;
- `VALID`;
- not stale;
- eligible for intelligence engines;
- unexpired at processing time;
- complete for every required directional feature.

Ineligible Feature Factory snapshots are acknowledged without creating a directional output.

## Evidence

Every output persists evidence for all six weighted components. Each record contains:

- stable evidence code;
- human-readable explanation;
- `SUPPORTS_LONG`, `SUPPORTS_SHORT`, or `NEUTRAL` impact;
- configured component weight;
- normalized contribution in the raw versioned contract.

Neutral outputs add `DIRECTIONAL_CONVICTION_BELOW_THRESHOLD` as a warning.

## Lineage migration

Migration `V0013` adds `intelligence.engine_output_dependencies`.

Each directional output has a `FEATURE_SET` dependency on the exact Feature Factory `engine_output_id` it consumed. Composite foreign keys enforce that upstream and downstream outputs refer to the same instrument. Self-dependency is rejected.

This creates a complete lineage chain:

```text
market candle IDs
  -> Feature Factory engine output
  -> directional engine output
```

## Idempotency and corrections

Directional output and message UIDs are deterministic from:

```text
source feature snapshot UID
policy version
output revision
```

A Feature Factory output can be processed only once by this engine. Duplicate delivery returns the existing directional output.

A corrected candle creates a new Feature Factory revision. The directional engine then creates a new directional revision for the same instrument, timeframe, and as-of timestamp and marks the earlier directional output non-current.

## Configuration

Both stages are disabled by default.

```text
THESISPULSE_FEATURE_FACTORY_ENABLED=false
THESISPULSE_DIRECTIONAL_ENGINE_ENABLED=false
```

Local PAPER activation:

```powershell
$env:THESISPULSE_FEATURE_FACTORY_ENABLED = "true"
$env:THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY = $featureKey
$env:THESISPULSE_FEATURE_FACTORY_PROVIDER = "SqlServer"
$env:THESISPULSE_OPERATIONAL_DATABASE = "Driver={ODBC Driver 18 for SQL Server};Server=localhost\SQLEXPRESS;Database=ThesisPulseAI;Trusted_Connection=yes;TrustServerCertificate=yes;"
$env:THESISPULSE_DIRECTIONAL_ENGINE_ENABLED = "true"
$env:THESISPULSE_DIRECTIONAL_ENGINE_VERSION = "1.0.0"
$env:THESISPULSE_DIRECTIONAL_POLICY_VERSION = "technical-direction-v1.0.0"
```

Directional intelligence cannot be enabled unless the Feature Factory is enabled.

## APIs

```text
GET /api/v1/intelligence/directional/status
GET /api/v1/intelligence/directional/latest/{instrumentKey}?timeframe=5m
GET /api/v1/engines
```

The existing internal candle endpoint returns both the Feature Factory result and the optional directional-processing result.

## Activation order

1. Apply migrations through `V0013`.
2. Apply the local PAPER seed pack through `S0009`.
3. Verify Feature Factory production of complete eligible snapshots.
4. Start the Python service with Feature Factory enabled.
5. Enable the directional engine.
6. Confirm `/health/ready` and both status endpoints.
7. Verify engine runs, outputs, dependencies, evidence, and warnings.
8. Confirm no rows were created in `intelligence.signals` by this engine.

## Rollback

Set:

```text
THESISPULSE_DIRECTIONAL_ENGINE_ENABLED=false
```

Feature Factory processing continues, but no new directional outputs are created. Existing immutable outputs and lineage remain available for audit and backtesting.

## Exit gate

- Strong-long, strong-short, and neutral policies are deterministic.
- Ineligible, stale, invalid, incomplete, and expired sources fail closed.
- Duplicate processing returns the original output.
- Corrected feature snapshots create revisioned directional outputs.
- Every SQL output has a `FEATURE_SET` dependency.
- The engine has no signal or execution authority.
- Python, .NET, database, and React CI are green.
