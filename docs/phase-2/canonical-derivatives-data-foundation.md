# Phase 2.7 — Canonical Derivatives Data Foundation

## Objective

Create the point-in-time reference and observation layer required before ThesisPulse AI can calculate futures-basis, option-chain or contract-selection intelligence safely.

This slice adds data contracts and persistence only. It does not add:

- put-call ratio signals;
- max-pain calculations;
- strike-level OI-wall signals;
- volatility-skew signals;
- gamma-exposure signals;
- automatic futures rollover;
- option strike selection;
- derivatives order construction;
- derivatives execution authority.

## Authority boundary

The Market Data service owns normalization and storage of derivatives observations.

It does not own:

- directional voting;
- thesis creation;
- risk approval;
- trade-plan construction;
- contract selection for execution;
- order submission;
- position management.

Every synchronized derivative contract is created with:

```text
selection_eligible = false
```

Existing `reference.instruments.is_trade_allowed` and `is_short_allowed` values remain unchanged.

## Canonical lifecycle

```text
Upstox instrument master
  -> canonical reference.instruments
  -> effective-dated derivative_contracts
  -> derivative_expiry_schedules

Provider basis observation
  -> canonical underlying + future lineage validation
  -> deterministic basis calculation
  -> immutable futures_basis_observations

Provider option-chain snapshot
  -> canonical underlying validation
  -> contract-level lineage validation
  -> normalized option_chain_snapshot
  -> normalized option_chain_entries
```

Broker instrument keys are accepted only at the Market Data adapter boundary. Public query results expose canonical instrument and derivative-contract UIDs.

## Database structures

### `reference.derivative_contracts`

Stores effective-dated metadata for one canonical future or option instrument:

- canonical instrument and underlying IDs;
- contract class;
- expiry date and type;
- last-trading, settlement and rollover dates;
- settlement type;
- contract multiplier and lot size;
- strike and option type for options;
- current status;
- explicit contract-selection eligibility;
- effective-date validity;
- source metadata.

Supported contract classes:

```text
INDEX_FUTURE
STOCK_FUTURE
INDEX_OPTION
STOCK_OPTION
```

### `reference.derivative_expiry_schedules`

Stores effective-dated expiry calendars by underlying and segment:

```text
underlying + FUTURES/OPTIONS + expiry date + calendar version
```

Expiry classification remains `UNKNOWN` when the provider does not supply authoritative metadata. The platform does not infer weekly or monthly expiry solely from calendar position.

### `market.futures_basis_observations`

Stores immutable source observations and deterministic calculated fields:

```text
basis_amount = future_price - underlying_price
basis_fraction = basis_amount / underlying_price
annualized_basis_fraction = basis_fraction * 365 / days_to_expiry
```

Annualization is omitted on expiry day.

### `market.option_chain_snapshots`

Stores one immutable, point-in-time chain header:

- underlying and expiry;
- source event identity and revision;
- event, publication and receipt timestamps;
- underlying price;
- complete, partial or invalid status;
- quality and point-in-time eligibility;
- accepted contract and strike counts;
- source and calculation versions;
- normalization warnings;
- raw payload hash and optional raw JSON.

### `market.option_chain_entries`

Stores one canonical option contract per snapshot:

- strike and option type;
- bid, ask and last price;
- bid and ask quantity;
- volume;
- current and previous open interest;
- normalized OI change;
- implied volatility;
- delta, gamma, theta, vega and rho;
- Greeks calculation-source version;
- quality status and metadata.

Greeks are stored only when provided with an explicit source version. The foundation does not calculate missing Greeks.

## Point-in-time identity

Provider observations are idempotent by:

```text
data_source + source_event_id + revision
```

Historical reads apply both conditions:

```text
event_at_utc <= asOfUtc
received_at_utc <= asOfUtc
```

Results are ordered by:

```text
event_at_utc descending
revision descending
received_at_utc descending
```

A correction therefore becomes visible only after its actual receipt time.

## Contract synchronization

The existing instrument-master synchronization remains the source of canonical instrument identities.

After `reference.instruments` and broker mappings are synchronized, the Market Data orchestrator materializes derivative contracts and expiry schedules from the same snapshot.

A derivative is skipped when:

- the underlying provider key is missing;
- the underlying canonical mapping is unavailable;
- expiry is missing;
- an option has no positive strike;
- an option type is not `CALL` or `PUT`;
- canonical underlying lineage does not match.

Synchronizing reference data never grants trading, shorting or execution permission.

## Option-chain normalization policy

Each chain requires:

- provider and source-event identity;
- non-negative revision;
- canonical underlying key;
- expiry date;
- valid event and receipt timestamps;
- positive underlying price;
- at least one contract entry;
- valid correlation ID;
- valid raw JSON.

Each entry requires:

- canonical provider contract key;
- quote timestamp no later than snapshot receipt;
- positive strike;
- `CALL` or `PUT` option type;
- non-negative prices, quantities, volume, OI, IV and gamma;
- ask price not below bid price;
- delta between `-1` and `1`;
- supported quality status;
- exact underlying, expiry, strike and option-type lineage against the catalog.

Snapshot status:

| Result | Status | Point-in-time eligible |
|---|---|---:|
| Every entry accepted | `COMPLETE` | Yes |
| Some entries accepted | `PARTIAL` | No |
| No entries accepted | `INVALID` | No |

Rejected entries and their reasons remain in `warnings_json`.

## Futures-basis policy

A basis observation requires:

- positive spot and future prices;
- different underlying and future keys;
- canonical future-to-underlying lineage;
- valid event, publication and receipt timestamps;
- non-negative revision;
- valid correlation ID and raw JSON.

Missing publication time does not fabricate a timestamp. The observation is stored as `DEGRADED` with:

```text
SOURCE_PUBLISHED_TIME_UNAVAILABLE
```

## APIs

### Internal ingestion

```text
POST /internal/v1/derivatives/futures-basis
POST /internal/v1/derivatives/option-chain
POST /internal/v1/instruments/synchronize
```

Internal writes require the existing `X-ThesisPulse-Internal-Key` authorization policy.

### Public canonical reads

```text
GET /api/v1/derivatives/contracts
    ?underlyingInstrumentKey={key}
    &expiryDate={yyyy-MM-dd}
    &contractClass={class}

GET /api/v1/derivatives/expiries
    ?underlyingInstrumentKey={key}
    &marketSegment=FUTURES|OPTIONS

GET /api/v1/derivatives/futures-basis/latest
    ?futureInstrumentKey={key}
    &asOfUtc={timestamp}

GET /api/v1/derivatives/option-chain/latest
    ?underlyingInstrumentKey={key}
    &expiryDate={yyyy-MM-dd}
    &asOfUtc={timestamp}
```

The contract endpoint reports:

```text
selectionAuthority = false
```

## In-memory and SQL Server parity

Both providers implement the same contract:

- automatic derivative catalog synchronization;
- source-event idempotency;
- deterministic basis math;
- contract-level option validation;
- complete/partial/invalid chain status;
- OI-change normalization;
- historical cutoff reads.

SQL Server remains the operational source of truth outside isolated tests and local demonstrations.

## Safety

- PAPER environment boundary remains unchanged.
- No derivatives execution authority is introduced.
- No broker contract token is exposed as an execution decision.
- No analytics are produced from partial chains.
- Missing expiry classification remains `UNKNOWN`.
- Missing Greeks remain null.
- Partial chains fail closed for downstream intelligence.
- Raw corrections remain immutable revisions.

## Activation

No separate engine switch is required because this is a Market Data storage foundation.

Use the existing persistence configuration:

```text
MarketData:Persistence:Provider = InMemory | SqlServer
MarketData:Operations:Enabled = true
MarketData:Operations:InternalApiKey = <secret>
```

SQL Server activation requires migrations V0015 and V0016.

## Rollback

Stop new derivatives ingestion by disabling Market Data operations:

```text
MarketData:Operations:Enabled = false
```

Public historical reads may remain available. Stored observations and reference rows are retained for audit.

## Exit gate

- Derivative contracts are effective-dated and linked to canonical underlyings.
- Expiry schedules are versioned and queryable.
- Contract selection remains disabled.
- Futures basis is deterministic and idempotent.
- Option chains are normalized by canonical contract identity.
- Partial and invalid chains fail closed.
- OI changes are calculated without fabricating missing values.
- Greeks preserve calculation-source versioning.
- Historical reads honor event and receipt cutoffs.
- Warning lineage survives later reads.
- In-memory and SQL Server providers share one contract.
- Market Data, database, .NET and platform regression tests pass.

## Next slice

Build the first option-chain intelligence engine on top of complete, point-in-time snapshots:

- call and put OI aggregation;
- PCR by OI and volume;
- strike-level OI walls;
- OI additions and unwinding;
- max-pain calculation with policy versioning;
- IV term structure and skew;
- quality and liquidity gates;
- independent Fusion evidence with no execution authority.
