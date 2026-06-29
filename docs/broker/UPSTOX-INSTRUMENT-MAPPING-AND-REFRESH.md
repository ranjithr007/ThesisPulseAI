# Upstox Instrument Mapping and Refresh Policy

## Purpose

ThesisPulse AI uses a stable internal `instrument_id`. Upstox instrument keys, exchange tokens, trading symbols and contract metadata are external mappings and must never become internal primary keys.

## Source

The adapter imports the official Upstox beginning-of-day instrument files. JSON is the preferred source. CSV sources are not used for new implementation when the broker marks them deprecated.

## Mapping model

Each effective broker mapping records:

- internal `instrument_id`;
- broker code `UPSTOX`;
- Upstox instrument key;
- exchange token;
- exchange and segment;
- trading symbol;
- instrument type;
- underlying internal instrument ID where applicable;
- underlying broker key where applicable;
- expiry;
- strike price;
- option type;
- weekly-expiry indicator;
- lot size;
- minimum lot;
- freeze quantity;
- tick size;
- quantity multiplier;
- security type and tradability flags;
- source file identity and checksum;
- imported-at UTC;
- effective-from and effective-to UTC;
- mapping status and reason.

## Refresh schedule

- Download and stage the official BOD JSON before the trading session.
- Validate checksum, JSON structure and expected segment coverage.
- Compare with the currently active mapping set.
- Classify additions, removals and material changes.
- Publish a new version only after validation succeeds.
- Run a final pre-open eligibility check for live instruments.
- Permit an intraday emergency refresh only through an operationally approved workflow.

## Validation

The import rejects or quarantines records with:

- duplicate effective instrument keys;
- missing exchange, segment or instrument type;
- invalid expiry or strike data;
- invalid or zero lot size for executable derivatives;
- non-positive tick size for executable instruments;
- unresolved underlying mapping;
- unexpected reduction in segment coverage;
- conflicting active mappings for the same broker and internal instrument;
- malformed identifiers;
- unsupported instrument classes.

## Change handling

### New instrument

Create a candidate mapping. It remains analysis-only until universe, liquidity, data and broker eligibility checks pass.

### Contract expiry

Close the effective interval. Expired derivatives remain available for historical lineage but cannot be selected for new execution.

### Lot size, tick size or freeze quantity change

Create a new effective mapping version. Existing plans are revalidated; incompatible unsubmitted plans expire.

### Broker key change

Create a new mapping and close the old interval. Historical orders retain the broker key used at execution time.

### Missing active instrument

Move the mapping to suspended or close-only according to current positions. Block new orders and raise an operational alert.

## Point-in-time correctness

Backtests and historical decision reconstruction use the mapping version effective at the evaluated timestamp. Current broker keys must not be retroactively substituted into historical records.

## Execution lookup

At execution time the adapter resolves:

```text
internal instrument_id
+ broker account
+ environment
+ execution timestamp
-> one active Upstox mapping
```

Zero matches or multiple matches cause rejection. Strategy code cannot supply or override an Upstox instrument key.

## Storage

Recommended tables:

- `broker.broker_instrument_imports`;
- `broker.broker_instrument_mappings`;
- `broker.broker_instrument_mapping_changes`;
- `broker.broker_capabilities`;
- `operations.data_quality_incidents`.

The source payload may be archived with retention controls, but execution-critical attributes are stored relationally.

## Operational metrics

Track:

- import success and duration;
- source age;
- source checksum;
- records by segment and instrument type;
- added, removed and changed mappings;
- unresolved underlyings;
- duplicate or quarantined records;
- live-eligible instruments missing valid mappings;
- time since last successful refresh.

A stale or failed mapping refresh blocks new live orders for affected instruments.
