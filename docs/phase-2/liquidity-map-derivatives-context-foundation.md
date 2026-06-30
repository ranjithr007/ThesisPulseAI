# Phase 2.6 — Liquidity Map and Derivatives Context Foundation

## Objective

Add an independent, deterministic directional voter that combines:

- point-in-time support and resistance liquidity pools;
- current price location within the observed range;
- canonical price and open-interest context when open interest is available.

The engine produces immutable evidence for Thesis/Fusion. It cannot create a canonical signal, approve risk, create a trade plan, mutate a portfolio, submit an order, or contact Upstox directly.

## Engine identity

```text
engine_code: THESIS_PULSE_LIQUIDITY_DERIVATIVES_CONTEXT
engine_role: DIRECTIONAL_VOTER
engine_version: 1.0.0
policy_version: liquidity-derivatives-context-v1.0.0
owner_service: ThesisPulse.AI
can_create_signals: false
can_execute_orders: false
```

Seed `S0014` registers the engine. Verification `S0010` rejects authority drift.

## Methodology boundary

Liquidity pools inferred from candles are price-structure heuristics. They are not exchange order-book depth and do not prove that resting orders exist at a level.

Every output includes:

```text
LIQUIDITY_MAP_IS_PRICE_STRUCTURE_HEURISTIC
```

Open-interest interpretation is also contextual rather than causal. Price and OI changes can describe participation, but cannot prove the identity or intent of market participants.

The following are intentionally unavailable in V1 because no canonical, versioned input exists yet:

```text
OPTION_CHAIN_CONTEXT_UNAVAILABLE_V1
FUTURES_BASIS_UNAVAILABLE_V1
```

V1 does not fabricate:

- put-call ratios;
- max pain;
- strike-level OI walls;
- implied volatility;
- Greeks;
- futures-to-underlying basis;
- rollover positioning.

Those capabilities require normalized option-chain, contract-selection, underlying-alignment and futures-basis contracts under ADR-0004.

## Processing path

```text
canonical market.candle.published.v1
  -> Feature Factory intake
  -> point-in-time candle revision window
  -> confirmed swing pivots
  -> clustered support/resistance liquidity pools
  -> range-location calculation
  -> price/open-interest state calculation
  -> immutable intelligence.engine_outputs
  -> exact intelligence.engine_output_market_inputs lineage
  -> optional LIQUIDITY_DERIVATIVES_CONTEXT Fusion vote
```

SQL Server remains the operational source of truth.

## Point-in-time rules

For source candle cutoff `T`, only candles satisfying these conditions may be used:

```text
candle.close_at_utc <= T
candle.received_at_utc <= source publication occurred_at_utc
candle.is_closed = true
```

For each candle open time, the highest revision available at the historical cutoff is selected.

The engine therefore cannot consume:

- candles closing after `T`;
- later corrections unavailable at `T`;
- provisional candles;
- future-confirmed pivots.

Every selected candle is linked to the output through `intelligence.engine_output_market_inputs`. The trigger candle is stored as `PRIMARY`; prior candles are stored as `CONTEXT`.

## Confirmed pivot policy

Default pivot width:

```text
left candles: 2
right candles: 2
```

A confirmed high requires its high to be strictly greater than every high in the configured left and right windows. A confirmed low uses the equivalent strict-low rule.

A pivot becomes usable only after the right-side candles close.

## Liquidity-pool construction

Confirmed pivots of the same type are clustered when the candidate price is within the configured fraction of the current cluster center.

Default cluster tolerance:

```text
0.0015 = 0.15%
```

A swing cluster requires at least two confirmed pivots.

The highest high and lowest low in the point-in-time window are also retained as session-extreme pools so the map can still expose context when no repeated pivot cluster exists.

Pool types:

| Pivot type | Side | Role |
|---|---|---|
| Confirmed high | `BUY_SIDE` | `RESISTANCE` |
| Confirmed low | `SELL_SIDE` | `SUPPORT` |

Pool width is deterministic:

```text
lower = center - center * pool_half_width_fraction
upper = center + center * pool_half_width_fraction
```

Default half-width:

```text
0.0005 = 0.05%
```

## Pool lifecycle

### Buy-side pool

- `ACTIVE`: price has not closed above the upper boundary and no wick sweep has occurred.
- `SWEPT`: a candle traded above the upper boundary but closed at or below it.
- `BROKEN`: a candle closed above the upper boundary.

### Sell-side pool

- `ACTIVE`: price has not closed below the lower boundary and no wick sweep has occurred.
- `SWEPT`: a candle traded below the lower boundary but closed at or above it.
- `BROKEN`: a candle closed below the lower boundary.

Broken pools are excluded from the active nearest-pool calculation but remain reproducible from the stored input lineage.

## Pool strength

Initial V1 strength uses only deterministic source information:

```text
base strength = 0.25
touch contribution = 0.15 * touch_count
swing-cluster bonus = 0.20
maximum = 1.00
```

No order-book quantity is implied by the strength value.

## Liquidity-attraction score

For the nearest active buy-side and sell-side pools:

```text
attraction = pool_strength / max(distance_fraction, 0.0001)
```

The normalized score is:

```text
(buy_attraction - sell_attraction)
/ (buy_attraction + sell_attraction)
```

Interpretation:

- positive: stronger or closer buy-side liquidity above price;
- negative: stronger or closer sell-side liquidity below price;
- zero: balanced or unavailable.

This describes geometric attraction under the V1 heuristic. It does not predict that price must reach a pool.

## Range-location score

For the observed point-in-time range:

```text
position = (current_price - range_low) / (range_high - range_low)
score = 1 - 2 * position
```

Interpretation:

- near range low: positive, because price is closer to observed support;
- near range high: negative, because price is closer to observed resistance;
- range midpoint: neutral.

## Open-interest context

The engine uses the configured number of latest point-in-time closed candles.

Default lookback:

```text
6 five-minute candles
```

When at least two non-null OI values exist, it calculates:

```text
price_change = (last_close - first_close) / first_close
oi_change = (last_oi - first_oi) / first_oi
```

Default activity thresholds:

```text
minimum absolute price change: 0.0005 = 0.05%
minimum absolute OI change:    0.0020 = 0.20%
```

State matrix:

| Price | OI | State | Directional value |
|---|---|---|---:|
| Up | Up | `LONG_BUILDUP` | +1.00 |
| Down | Up | `SHORT_BUILDUP` | -1.00 |
| Up | Down | `SHORT_COVERING` | +0.60 |
| Down | Down | `LONG_UNWINDING` | -0.60 |
| Below threshold | Any | `FLAT` | 0.00 |
| Missing/invalid OI | N/A | `NOT_AVAILABLE` | 0.00 |

For cash equities or feeds without OI, the output includes:

```text
OPEN_INTEREST_CONTEXT_UNAVAILABLE
```

Missing OI never becomes positive or negative evidence.

## Component weights

Initial V1 weights:

| Component | Weight |
|---|---:|
| Liquidity attraction | 0.35 |
| Range location | 0.15 |
| Price/OI derivatives context | 0.50 |

```text
score = liquidity_attraction * 0.35
      + range_location * 0.15
      + derivatives_score * 0.50
```

The score is bounded to `[-1, 1]`.

Direction policy:

```text
score >=  0.20 -> LONG
score <= -0.20 -> SHORT
otherwise       -> NEUTRAL
```

## Confidence

Confidence combines:

- candle-window completeness;
- valid-input ratio;
- availability of pools on both sides;
- OI availability;
- agreement between non-zero components;
- score magnitude.

Open-interest unavailability reduces confidence but does not invalidate the candle-based liquidity map.

## Fusion eligibility

The output enters Fusion only when:

- the source is a closed five-minute candle;
- the exact source revision exists in the point-in-time window;
- the required candle count is present;
- the valid-input ratio passes policy;
- no intraday candle gap is detected;
- the output is fresh;
- at least one active or swept pool exists;
- direction is non-neutral;
- confidence passes the configured threshold.

Default gates:

```text
required candles: 30
maximum candle window: 128
minimum valid-input ratio: 0.95
maximum output age: 420 seconds
minimum Fusion confidence: 0.55
```

An ineligible output contributes warnings but no directional vote. Instrument, timeframe or cutoff mismatch fails the complete workflow evidence build closed.

When the vote is added, the Fusion evidence UID is deterministically revised using the liquidity output UID. The enriched result cannot share an evidence identity with the pre-enrichment result.

## Idempotency and revisions

The same source candle publication UID returns the original output.

A corrected candle publication with a new message UID and higher revision:

- reconstructs the point-in-time input window;
- creates the next engine-output revision;
- marks the former output non-current;
- sets `supersedes_engine_output_uid`;
- retains both outputs for audit and replay.

## Configuration

The engine is disabled by default:

```text
THESISPULSE_LIQUIDITY_DERIVATIVES_ENGINE_ENABLED=false
```

PAPER activation:

```powershell
$env:THESISPULSE_FEATURE_FACTORY_ENABLED = "true"
$env:THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY = $featureKey
$env:THESISPULSE_FEATURE_FACTORY_PROVIDER = "SqlServer"
$env:THESISPULSE_OPERATIONAL_DATABASE = "<ODBC SQL Server connection string>"
$env:THESISPULSE_LIQUIDITY_DERIVATIVES_ENGINE_ENABLED = "true"
```

Optional settings:

```text
THESISPULSE_LIQUIDITY_DERIVATIVES_REQUIRED_INPUT_COUNT
THESISPULSE_LIQUIDITY_DERIVATIVES_MAXIMUM_INPUT_COUNT
THESISPULSE_LIQUIDITY_DERIVATIVES_SWING_LEFT_BARS
THESISPULSE_LIQUIDITY_DERIVATIVES_SWING_RIGHT_BARS
THESISPULSE_LIQUIDITY_POOL_CLUSTER_TOLERANCE_FRACTION
THESISPULSE_LIQUIDITY_POOL_HALF_WIDTH_FRACTION
THESISPULSE_LIQUIDITY_MAXIMUM_POOLS_PER_SIDE
THESISPULSE_LIQUIDITY_DERIVATIVES_OI_LOOKBACK_BARS
THESISPULSE_DERIVATIVES_MINIMUM_PRICE_CHANGE_FRACTION
THESISPULSE_DERIVATIVES_MINIMUM_OI_CHANGE_FRACTION
THESISPULSE_LIQUIDITY_DERIVATIVES_MINIMUM_VALID_INPUT_RATIO
THESISPULSE_LIQUIDITY_DERIVATIVES_MAXIMUM_OUTPUT_AGE_SECONDS
THESISPULSE_LIQUIDITY_DERIVATIVES_DIRECTIONAL_THRESHOLD
THESISPULSE_LIQUIDITY_DERIVATIVES_FUSION_CONFIDENCE_THRESHOLD
```

## APIs

```text
GET /api/v1/intelligence/liquidity-derivatives/status
GET /api/v1/intelligence/liquidity-derivatives/latest/{instrumentKey}?timeframe=5m
GET /api/v1/engines
```

Evaluation uses the existing internal candle endpoint:

```text
POST /internal/v1/market-data/candles
```

## Rollback

Disable the engine without stopping candle ingestion or other intelligence engines:

```text
THESISPULSE_LIQUIDITY_DERIVATIVES_ENGINE_ENABLED=false
```

Historical outputs remain immutable.

## Deferred derivatives capabilities

The next derivatives-data foundation must add versioned canonical contracts for:

- effective-dated derivative contract identity;
- underlying relationship;
- expiry and rollover classification;
- futures basis;
- normalized option chains;
- strike and moneyness;
- bid, ask, volume and OI by strike;
- IV and Greeks with calculation-source versioning;
- point-in-time chain snapshots.

Only after those contracts exist can ThesisPulse AI add PCR, OI walls, max pain, skew, gamma exposure and contract-selection evidence without look-ahead or broker-token leakage.

## Exit gate

- Liquidity pools are deterministic and point-in-time correct.
- Active, swept and broken states are reproducible.
- Long build-up, short build-up, short covering and long unwinding are deterministic.
- Missing OI contributes zero and remains explicit.
- Options-chain and futures-basis absence remains explicit.
- Duplicate source messages are idempotent.
- Corrected candles create output revisions.
- Exact candle lineage is persisted.
- Only eligible output enters Fusion.
- Fusion evidence identity changes when the vote is appended.
- Engine authority verification passes.
- Python, .NET, database and React CI are green.
