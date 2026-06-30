# Phase 2.6 — Deterministic Order Flow Engine Foundation

## Objective

Add an independent directional voter using the live quote information currently available from the Upstox Market Data feed.

The engine consumes canonical quote publications and evaluates them only when an eligible closed five-minute candle is published. It produces immutable, versioned, point-in-time Order Flow evidence for Thesis/Fusion.

It cannot create a canonical signal, approve risk, construct a trade plan, mutate a portfolio, or submit an order.

## Important methodology limitation

Order Flow V1 is a **proxy-based engine**.

The current canonical quote contract provides:

- last-traded price;
- last-traded quantity;
- total buy quantity;
- total sell quantity;
- open interest when available.

It does not currently provide a complete exchange trade tape with aggressor-side classification or full order-book depth. Therefore:

- tick-rule signed quantity is not true buyer-initiated/seller-initiated volume;
- cumulative tick-rule delta is not exchange-confirmed CVD;
- total buy/sell quantity imbalance is not level-by-level order-book imbalance;
- absorption and exhaustion are deterministic proxies rather than microstructure truth.

Every output includes `PROXY_TICK_RULE_NOT_AGGRESSOR_FLOW`. Outputs are stored as `DEGRADED` rather than `VALID` because this limitation is structural, although a sufficiently complete and fresh output may still be eligible for Fusion.

Future trade-tape or market-depth contracts can replace the proxy inputs under a new policy version without rewriting historical V1 outputs.

## Processing path

```text
Upstox live update
  -> canonical market.quote.published.v1
  -> Market Data transactional outbox
  -> Order Flow quote inbox
  -> eligible closed 5m candle
  -> point-in-time quote window
  -> deterministic Order Flow calculation
  -> intelligence.engine_outputs
  -> intelligence.engine_output_message_inputs
  -> intelligence.engine_output_evidence
  -> intelligence.engine_output_warnings
  -> optional Fusion evidence
```

SQL Server remains the operational source of truth.

## Engine identity

```text
engine_code: THESIS_PULSE_ORDER_FLOW
engine_role: DIRECTIONAL_VOTER
owner_service: ThesisPulse.AI
engine_version: 1.0.0
policy_version: order-flow-proxy-v1.0.0
can_create_signals: false
can_execute_orders: false
```

Seed `S0012` registers the engine. Verification `S0008` rejects authority drift.

## Point-in-time input rules

For a five-minute source candle, the engine uses only quote messages where:

```text
candle.open_at_utc < quote.event_at_utc <= candle.close_at_utc
quote inbox received_at_utc <= candle publication occurred_at_utc
```

The exact source candle message and every quote message are linked through `intelligence.engine_output_message_inputs`.

The same candle publication UID is idempotent. A corrected candle publication creates a new output revision and supersedes the previous current output for the same instrument, timeframe and cutoff.

## Deterministic components

### Weighted buy/sell quantity imbalance

For every usable quote:

```text
imbalance = (total_buy_quantity - total_sell_quantity)
            / (total_buy_quantity + total_sell_quantity)
```

Later quotes receive higher deterministic weights. The final value is bounded to `[-1, 1]`.

### Tick-rule signed quantity proxy

For each usable last trade:

```text
price > previous price   -> +last_traded_quantity
price < previous price   -> -last_traded_quantity
price = previous price   -> reuse the previous non-zero sign
```

The signed total is divided by total observed last-traded quantity and bounded to `[-1, 1]`.

### Open-interest context

When open interest is available:

| Price | Open interest | Interpretation | Contribution |
|---|---|---|---:|
| Up | Up | Long build-up | +1.00 |
| Down | Up | Short build-up | -1.00 |
| Up | Down | Short covering | +0.40 |
| Down | Down | Long unwinding | -0.40 |

Equities or feeds without open interest receive `OPEN_INTEREST_UNAVAILABLE`; the component contribution becomes zero.

### Initial component weights

| Component | Weight |
|---|---:|
| Buy/sell quantity imbalance proxy | 0.45 |
| Tick-rule signed quantity proxy | 0.40 |
| Open-interest context | 0.15 |

```text
raw_score = book_imbalance * 0.45
          + tick_delta_ratio * 0.40
          + open_interest_signal * 0.15
```

Absorption and exhaustion proxies reduce the score and confidence; they never create a direction by themselves.

Direction policy:

```text
score >=  0.20 -> LONG
score <= -0.20 -> SHORT
otherwise       -> NEUTRAL
```

## Quality and Fusion eligibility

An output is eligible only when:

- the source is an eligible closed non-provisional five-minute candle;
- at least the configured minimum quote count is present;
- the usable-quote ratio satisfies the configured threshold;
- observed last-traded quantity coverage satisfies the configured threshold;
- the latest usable quote is fresh;
- direction is non-neutral;
- confidence satisfies the configured Fusion threshold.

Default thresholds:

```text
minimum quote samples: 10
minimum usable ratio: 0.80
minimum traded-quantity coverage: 0.02
maximum latest-quote age: 30 seconds
minimum Fusion confidence: 0.55
```

Common warnings:

```text
PROXY_TICK_RULE_NOT_AGGRESSOR_FLOW
INSUFFICIENT_QUOTE_SAMPLES
LOW_USABLE_QUOTE_RATIO
LOW_QUOTE_VOLUME_COVERAGE
OPEN_INTEREST_UNAVAILABLE
ORDER_FLOW_QUOTES_STALE
BUY_PRESSURE_ABSORBED
SELL_PRESSURE_ABSORBED
ORDER_FLOW_EXHAUSTION
```

Missing or ineligible Order Flow evidence does not silently become positive confirmation. The workflow evidence carries the warnings, and Fusion receives an `ORDER_FLOW` vote only when the output passes its own eligibility gate.

## Configuration

All functionality is disabled by default.

```text
THESISPULSE_FEATURE_FACTORY_ENABLED=false
THESISPULSE_ORDER_FLOW_ENGINE_ENABLED=false
MarketData:Publication:AiOrderFlowEnabled=false
```

Local PAPER activation requires:

```powershell
$env:THESISPULSE_FEATURE_FACTORY_ENABLED = "true"
$env:THESISPULSE_FEATURE_FACTORY_INTERNAL_API_KEY = $featureKey
$env:THESISPULSE_FEATURE_FACTORY_PROVIDER = "SqlServer"
$env:THESISPULSE_OPERATIONAL_DATABASE = "<ODBC SQL Server connection string>"
$env:THESISPULSE_ORDER_FLOW_ENGINE_ENABLED = "true"

 dotnet user-secrets set `
   "MarketData:Publication:AiOrderFlowEnabled" `
   "true" `
   --project src/ThesisPulse.MarketData.Service
```

The Market Data outbox message is marked published only after every enabled consumer accepts the quote. A failed AI quote delivery remains retryable.

## APIs

```text
POST /internal/v1/market-data/quotes
GET  /api/v1/intelligence/order-flow/status
GET  /api/v1/intelligence/order-flow/latest/{instrumentKey}?timeframe=5m
GET  /api/v1/engines
```

The internal quote endpoint requires `X-ThesisPulse-Internal-Key`.

## Rollback

Disable either side independently:

```text
THESISPULSE_ORDER_FLOW_ENGINE_ENABLED=false
MarketData:Publication:AiOrderFlowEnabled=false
```

Feature, regime, technical directional, confirmation and existing workflow processing continue. Historical Order Flow outputs remain immutable for audit and replay.

## Exit gate

- Long, short and neutral proxy policies are deterministic.
- Duplicate quote and candle messages are idempotent.
- Point-in-time windows reject future and late-arriving quote information.
- Insufficient, stale and low-coverage inputs fail closed.
- Every SQL output stores exact source message lineage.
- Corrected candles produce revisioned outputs.
- Eligible outputs appear as independent `ORDER_FLOW` Fusion evidence.
- Engine authority verification passes.
- Python, .NET, database and React CI are green.
