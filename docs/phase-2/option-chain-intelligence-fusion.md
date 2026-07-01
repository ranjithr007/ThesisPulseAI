# Phase 2.8 — Option-Chain Intelligence Fusion

## Purpose

The deterministic Option-Chain Intelligence Engine contributes one independent directional voter to Thesis/Fusion when its own quality and freshness gates pass.

```text
market.option_chain.published.v1
        ↓
Option-Chain Intelligence output
        ↓
quality + freshness + lineage gates
        ↓
OPTION_CHAIN directional evidence
        ↓
Thesis/Fusion Engine
```

The adapter does not create a canonical signal, approve risk, build a trade plan, select an executable option contract or submit an order.

## Fusion evidence contract

```text
engineCode = OPTION_CHAIN
timeframe = OPTION_CHAIN
score = abs(option_chain_score) * 100
confidence = option_chain_confidence * 100
observedAtUtc = option_chain_as_of_utc
```

Only one option-chain vote is added to a workflow evidence package. Component evidence such as PCR, OI walls, OI flows, max pain and IV skew remains inside the option-chain output and is represented through the vote reasons; components do not become separate cross-engine votes.

## Inclusion gates

The vote is included only when:

- the underlying instrument matches the workflow instrument;
- the output observation time is not after the workflow cutoff;
- the output generation time is not after the workflow cutoff;
- the output age is within the configured option-chain freshness window;
- data quality is `VALID`;
- `isStale` is false;
- `isEligibleForFusion` is true;
- direction is `LONG` or `SHORT`;
- `selectionAuthority` and `executionAuthority` remain false.

A hard lineage, future-knowledge or authority mismatch fails workflow evidence construction closed.

A stale, degraded, invalid, neutral or low-confidence option-chain output contributes warnings only and never positive confirmation.

## Point-in-time safety

Historical reads require all three cutoffs:

```text
output.as_of_utc <= workflow cutoff
source_snapshot.received_at_utc <= workflow cutoff
output.generated_at_utc <= workflow cutoff
```

This prevents an option-chain output calculated later from leaking into an earlier historical candle workflow.

When multiple expiries share the same observation time, the deterministic read order is:

1. latest observation time;
2. highest immutable output revision;
3. latest source receipt time;
4. nearest expiry date.

## Fusion weights

The Thesis/Fusion policy recognizes the emitted engine codes directly:

```text
TREND                         0.20
MOMENTUM                      0.15
ORDER_FLOW                    0.15
SMART_MONEY_CONCEPTS          0.15
LIQUIDITY_DERIVATIVES_CONTEXT 0.15
OPTION_CHAIN                  0.20
```

`OPTION_CHAIN` uses a dedicated timeframe multiplier of `1.00`; it is not treated as a synthetic 5-minute candle.

Legacy `SMC`, `LIQUIDITY`, `DERIVATIVES` and `WHALE_FLOW` aliases remain recognized for previously recorded requests.

## Warnings

The adapter may add:

```text
OPTION_CHAIN_WORKFLOW_STALE
OPTION_CHAIN_DATA_QUALITY_NOT_VALID
OPTION_CHAIN_NOT_ELIGIBLE_FOR_FUSION
```

Engine warnings are preserved without converting them into positive evidence.

## Tests

The Phase 2.8 test matrix covers:

- deterministic independent vote creation;
- deterministic workflow evidence UID;
- stale output exclusion;
- invalid or neutral output exclusion;
- future generation-time rejection;
- authority-drift rejection;
- historical knowledge-time cutoff;
- Python, .NET, database, Market Data and React regression suites.

## Authority boundary

```text
engineRole = DIRECTIONAL_VOTER
canCreateSignals = false
selectionAuthority = false
canExecuteOrders = false
```
