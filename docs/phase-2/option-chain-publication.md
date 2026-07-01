# Phase 2.8 — Option-Chain Publication

## Purpose

Market Data publishes a normalized, complete and point-in-time-eligible option-chain snapshot to the deterministic Option-Chain Intelligence Engine.

```text
canonical derivative intake
        ↓
market.option_chain_snapshots + entries
        ↓ same SQL transaction
operations.outbox_messages
        ↓
POST /internal/v1/market-data/option-chain
        ↓
Option-Chain Intelligence Engine
```

The publication does not create a signal, select an option contract, approve risk, construct a trade plan or execute an order.

## Event contract

```text
market.option_chain.published.v1
```

The publication carries:

- immutable normalized snapshot UID;
- provider underlying instrument key;
- expiry and observation timestamps;
- normalized snapshot status and quality;
- source revision;
- canonical derivative-contract UID per entry;
- provider option instrument key;
- strike, option type, premium, volume, OI, IV and delta;
- canonical contract multiplier;
- Greeks calculation-source lineage.

The outbox message UID is deterministic from:

```text
provider + sourceEventId + source revision
```

## Publication gate

A snapshot is published only when all conditions pass:

```text
snapshotStatus = COMPLETE
qualityStatus = VALID
isPointInTimeEligible = true
accepted entry count > 0
MarketData:Publication:Enabled = true
MarketData:Publication:OptionChainEnabled = true
```

Partial and invalid snapshots remain available in canonical Market Data storage for audit and troubleshooting, but they do not enter intelligence.

## Transaction boundary

For SQL Server, snapshot rows, option entries and the outbox record are written in the same serializable transaction. A transaction failure leaves neither a partially persisted snapshot nor an orphan publication.

A publication is marked complete only after the AI endpoint accepts it. Delivery failure keeps the original outbox record retryable.

## Consumer boundary

Option-chain publications are delivered only to ThesisPulse AI:

```text
POST /internal/v1/market-data/option-chain
```

They are not sent to:

- Signal Service;
- Trading API;
- broker adapters;
- Operations automatic-trading workflows.

The internal service key and correlation ID are attached to every request.

## Activation

All switches default to `false`.

```json
{
  "MarketData": {
    "Publication": {
      "Enabled": true,
      "OptionChainEnabled": true,
      "DispatchEnabled": true,
      "AiOptionChainEnabled": true,
      "AiServiceBaseUrl": "http://localhost:8100"
    }
  }
}
```

Provide the internal key through an approved secret source:

```text
MarketData:Publication:InternalApiKey
```

Enable the AI engine and use the same key:

```text
THESISPULSE_OPTION_CHAIN_ENGINE_ENABLED=true
THESISPULSE_OPTION_CHAIN_INTERNAL_API_KEY=<secret>
```

For durable processing:

```text
MarketData:Persistence:Provider=SqlServer
Messaging:Provider=SqlServer
THESISPULSE_OPTION_CHAIN_PROVIDER=SqlServer
```

## Rollback

Disable new event creation first:

```text
MarketData:Publication:OptionChainEnabled=false
```

Then disable AI dispatch:

```text
MarketData:Publication:AiOptionChainEnabled=false
```

Finally disable the AI engine if required:

```text
THESISPULSE_OPTION_CHAIN_ENGINE_ENABLED=false
```

Existing canonical snapshots and immutable intelligence outputs are retained. No destructive rollback or automated backfill is performed.

## Authority boundary

```text
canCreateSignals = false
selectionAuthority = false
canExecuteOrders = false
```
