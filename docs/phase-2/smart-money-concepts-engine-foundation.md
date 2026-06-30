# Phase 2.6 — Smart Money Concepts Engine Foundation

## Objective

Add an evidence-only Smart Money Concepts engine that converts canonical closed five-minute candles into deterministic market-structure and liquidity evidence.

The engine detects:

- confirmed swing highs and swing lows;
- break of structure (`BOS_UP`, `BOS_DOWN`);
- change of character (`CHOCH_UP`, `CHOCH_DOWN`);
- high-side and low-side liquidity sweeps;
- bullish and bearish order-block candidates;
- bullish and bearish fair-value-gap candidates;
- zone mitigation state.

It cannot create a canonical signal, approve risk, create a trade plan, mutate portfolio state, or submit an order.

## Deterministic and point-in-time boundary

The engine uses only canonical candles satisfying:

```text
candle.close_at_utc <= source_candle.close_at_utc
candle.received_at_utc <= source publication occurred_at_utc
is_current = true
is_closed = true
is_provisional = false
is_point_in_time_eligible = true
```

No candle after the evaluated candle is available to the calculation.

Swing pivots are confirmed only after the configured right-hand candle count is already closed. A candle cannot become a swing merely because later, unseen candles would eventually make it one.

Default pivot window:

```text
left bars:  2
right bars: 2
```

## Structure rules

### Swing high

A candle is a confirmed swing high when its high is strictly greater than the highs of the configured candles on both sides.

### Swing low

A candle is a confirmed swing low when its low is strictly lower than the lows of the configured candles on both sides.

### Break of structure

A bullish break requires the current close to exceed the latest confirmed swing high by the configured minimum break fraction.

A bearish break requires the current close to fall below the latest confirmed swing low by the configured minimum break fraction.

Default minimum break fraction:

```text
0.0005
```

This avoids treating a one-tick touch as a structural break.

### Change of character

A bullish break while the confirmed structure state is bearish becomes `CHOCH_UP`.

A bearish break while the confirmed structure state is bullish becomes `CHOCH_DOWN`.

Otherwise the event is classified as `BOS_UP` or `BOS_DOWN`.

## Liquidity-sweep rules

### High-side sweep

```text
current.high > latest_confirmed_swing_high
current.close <= latest_confirmed_swing_high
```

### Low-side sweep

```text
current.low < latest_confirmed_swing_low
current.close >= latest_confirmed_swing_low
```

A wick through a level is not enough by itself. The close must return inside the prior structure boundary.

## Fair-value-gap rules

A bullish FVG is created from a three-candle sequence when:

```text
third.low > first.high
```

The zone is:

```text
[first.high, third.low]
```

A bearish FVG is created when:

```text
third.high < first.low
```

The zone is:

```text
[third.high, first.low]
```

## Order-block candidate rules

After a bullish BOS or CHOCH, the most recent bearish candle before the break becomes the bullish order-block candidate.

After a bearish BOS or CHOCH, the most recent bullish candle before the break becomes the bearish order-block candidate.

This is a deterministic candidate rule, not a claim of institutional order placement.

## Zone mitigation

A zone is marked mitigated when a later candle overlaps its price interval.

Only active, unmitigated zones contribute directional context.

## Initial component weights

| Component | Weight |
|---|---:|
| Structure event | 0.50 |
| Liquidity sweep | 0.20 |
| Active order block | 0.15 |
| Active fair-value gap | 0.15 |

Direction policy:

```text
score >=  0.20 -> LONG
score <= -0.20 -> SHORT
otherwise       -> NEUTRAL
```

## Quality and eligibility

An output is Fusion-eligible only when:

- at least 12 eligible candles are available;
- confirmed swing structure exists;
- direction is not neutral;
- confidence is at least 0.55;
- data quality is not invalid;
- the output is not stale.

Common warnings:

```text
INSUFFICIENT_STRUCTURE_HISTORY
INCOMPLETE_SWING_STRUCTURE
NO_ACTIVE_SMC_ZONES
```

## Persistence

SQL Server remains the operational source of truth.

The engine stores:

- one immutable `intelligence.engine_outputs` row per revision;
- exact candle lineage in `intelligence.engine_output_market_inputs`;
- evidence in `intelligence.engine_output_evidence`;
- warnings in `intelligence.engine_output_warnings`;
- run state in `intelligence.engine_runs`.

Corrected source candles create a new revision and supersede the previous current output for the same instrument, timeframe and cutoff.

## Engine authority

```text
engine_code: THESIS_PULSE_SMC
engine_role: DIRECTIONAL_VOTER
owner_service: ThesisPulse.AI
can_create_signals: false
can_execute_orders: false
```

Seed `S0013` registers the engine. Verification `S0009` rejects authority drift.

## Runtime configuration

The engine is disabled by default.

```text
THESISPULSE_SMC_ENGINE_ENABLED=false
THESISPULSE_SMC_ENGINE_CODE=THESIS_PULSE_SMC
THESISPULSE_SMC_ENGINE_VERSION=1.0.0
THESISPULSE_SMC_POLICY_VERSION=smc-structure-v1.0.0
THESISPULSE_SMC_REQUIRED_INPUT_COUNT=12
THESISPULSE_SMC_MAXIMUM_INPUT_COUNT=64
THESISPULSE_SMC_SWING_LEFT_BARS=2
THESISPULSE_SMC_SWING_RIGHT_BARS=2
THESISPULSE_SMC_MINIMUM_BREAK_FRACTION=0.0005
THESISPULSE_SMC_DIRECTIONAL_THRESHOLD=0.20
THESISPULSE_SMC_FUSION_CONFIDENCE_THRESHOLD=0.55
```

## Standalone PAPER API

Run:

```powershell
uvicorn app.smc_main:app --host 0.0.0.0 --port 8101
```

Endpoints:

```text
GET  /health/live
GET  /health/ready
GET  /info
POST /internal/v1/smc/candles
GET  /api/v1/intelligence/smc/status
GET  /api/v1/intelligence/smc/latest/{instrumentKey}?timeframe=5m
```

The internal candle endpoint requires `X-ThesisPulse-Internal-Key`.

## Rollback

Disable the engine:

```text
THESISPULSE_SMC_ENGINE_ENABLED=false
```

Historical outputs remain immutable for replay and audit.

## Exit gate

- BOS and CHOCH fixtures are deterministic.
- Liquidity sweeps require a wick through and close back inside.
- FVG boundaries are deterministic.
- Insufficient history fails closed.
- Duplicate source messages are idempotent.
- SQL outputs include exact candle lineage.
- Engine authority remains evidence-only.
- Python, database, .NET and React CI are green.
