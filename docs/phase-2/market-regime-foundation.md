# Phase 2 — Deterministic Market Regime Foundation

## Objective

Classify market structure and volatility from eligible point-in-time Feature Factory snapshots. The result is versioned context for later confirmation and fusion engines.

The Market Regime Engine is a `CONTEXT_PROVIDER`. It cannot create a canonical signal, approve risk, create a trade plan, or submit an order.

## Processing path

```text
closed canonical candle
  -> Feature Factory snapshot
  -> feature eligibility gate
  -> Market Regime Engine
  -> intelligence.engine_outputs
  -> intelligence.engine_output_dependencies
  -> intelligence.engine_output_evidence
  -> intelligence.engine_output_warnings
```

The Feature Factory transaction commits before regime processing begins. SQL Server remains the operational source of truth.

## Engine identity

```text
engine_code: THESIS_PULSE_MARKET_REGIME
engine_role: CONTEXT_PROVIDER
owner_service: ThesisPulse.AI
engine_version: 1.0.0
policy_version: market-regime-v1.0.0
can_create_signals: false
can_execute_orders: false
```

Seed `S0010` and verification `S0006` enforce these permissions.

## Orthogonal classifications

Every output contains one structural regime and one volatility regime.

Structural regimes:

```text
TRENDING_UP
TRENDING_DOWN
RANGE_BOUND
TRANSITION
```

Volatility regimes:

```text
LOW
NORMAL
HIGH
EXTREME
```

Volatility does not overwrite the structural classification. A market may therefore be `RANGE_BOUND + EXTREME` or `TRENDING_UP + HIGH`.

## Required features

```text
trend_score
trend_spread_5_20
momentum_5
close_return_3
realized_volatility_20
atr_14
sma_20
volume_ratio_20
```

The source Feature Factory snapshot must be:

- current;
- `VALID`;
- not stale;
- eligible for intelligence engines;
- unexpired at processing time;
- complete for all required regime features.

Expected data rejections return `IGNORED_INELIGIBLE` and do not create outbox retry loops. Infrastructure and database failures still propagate for retry.

## Structural policy

The bounded trend-bias score combines:

| Component | Weight |
|---|---:|
| Feature Factory trend score | 0.40 |
| 5/20 trend spread | 0.25 |
| Five-period momentum | 0.20 |
| Three-period return | 0.15 |

The engine also calculates:

- directional component alignment;
- trend strength;
- range compression score;
- transition risk score;
- normalized volatility score;
- volume expansion.

A trend requires both sufficient absolute trend bias and component alignment. Range classification requires high compression and low transition risk. All remaining mixed states are classified as transition.

## Volatility policy

Realized volatility and `ATR(14) / SMA(20)` are compared using timeframe-specific bands. The larger of the two measurements controls the volatility regime.

| Timeframe | Low ceiling | Normal ceiling | High ceiling |
|---|---:|---:|---:|
| 1m | 0.00050 | 0.00150 | 0.00350 |
| 5m | 0.00100 | 0.00300 | 0.00700 |
| 15m | 0.00180 | 0.00500 | 0.01200 |
| 1h | 0.00350 | 0.01000 | 0.02500 |
| 1d | 0.01000 | 0.02500 | 0.06000 |

Values above the high ceiling are classified as `EXTREME`.

## Confidence and evidence

Confidence is regime-specific:

- trending confidence rewards trend strength and low transition risk;
- range confidence rewards compression and lower volatility;
- transition confidence rewards disagreement and volatility expansion.

Confidence is not a probability of profit and is not risk approval.

Every output persists five evidence records:

```text
REGIME_TREND_BIAS
REGIME_TREND_ALIGNMENT
REGIME_RANGE_COMPRESSION
REGIME_TRANSITION_RISK
REGIME_VOLATILITY_STATE
```

Warnings include transition detection, high or extreme volatility, and low regime confidence.

## Database representation

The immutable raw contract contains the complete structural and volatility classification.

The shared `intelligence.engine_outputs` columns contain:

- directional bias mapped to `STRONG_LONG`, `LONG`, `NEUTRAL`, `SHORT`, or `STRONG_SHORT`;
- bounded trend-bias score;
- regime confidence;
- freshness and quality state;
- revision and supersession lineage;
- fusion eligibility.

Each regime output has a `FEATURE_SET` dependency on the exact Feature Factory `engine_output_id` it consumed.

```text
market candle IDs
  -> Feature Factory engine output
  -> Market Regime Engine output
```

## Idempotency and corrections

Output and message UIDs are deterministic from:

```text
source feature snapshot UID
policy version
output revision
```

A Feature Factory output can be processed only once by this engine. Duplicate delivery returns the existing regime output.

A corrected candle creates a revised Feature Factory output. The regime engine creates a corresponding regime revision and marks the earlier regime output non-current.

## Configuration

The engine is disabled by default.

```text
THESISPULSE_REGIME_ENGINE_ENABLED=false
```

Local PAPER activation:

```powershell
$env:THESISPULSE_FEATURE_FACTORY_ENABLED = "true"
$env:THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY = $featureKey
$env:THESISPULSE_FEATURE_FACTORY_PROVIDER = "SqlServer"
$env:THESISPULSE_OPERATIONAL_DATABASE = "Driver={ODBC Driver 18 for SQL Server};Server=localhost\SQLEXPRESS;Database=ThesisPulseAI;Trusted_Connection=yes;TrustServerCertificate=yes;"
$env:THESISPULSE_REGIME_ENGINE_ENABLED = "true"
$env:THESISPULSE_REGIME_ENGINE_VERSION = "1.0.0"
$env:THESISPULSE_REGIME_POLICY_VERSION = "market-regime-v1.0.0"
```

The Market Regime Engine cannot be enabled unless the Feature Factory is enabled.

## APIs

```text
GET /api/v1/intelligence/regime/status
GET /api/v1/intelligence/regime/latest/{instrumentKey}?timeframe=5m
GET /api/v1/engines
```

The internal candle endpoint returns the Feature Factory result and optional regime and directional processing results.

## Activation order

1. Apply migrations through `V0013`.
2. Apply the local PAPER seed pack through `S0010`.
3. Verify complete eligible Feature Factory snapshots.
4. Start the Python service with Feature Factory enabled.
5. Enable the Market Regime Engine.
6. Confirm `/health/ready` and the regime status endpoint.
7. Verify engine runs, outputs, dependencies, evidence, and warnings.
8. Confirm the regime engine created no rows in `intelligence.signals`.

## Rollback

Set:

```text
THESISPULSE_REGIME_ENGINE_ENABLED=false
```

Feature Factory and directional processing can continue. Existing immutable regime outputs and lineage remain available for audit and backtesting.

## Exit gate

- Trend, range, transition, and volatility classifications are deterministic.
- Ineligible, stale, invalid, incomplete, expired, and superseded sources fail closed.
- Duplicate processing returns the original output.
- Corrected feature snapshots create revisioned regime outputs.
- Every SQL output has a `FEATURE_SET` dependency.
- The engine has no signal or execution authority.
- Python, .NET, database, and React CI are green.
