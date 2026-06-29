# ADR-0002: Initial Market and Instrument Universe

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI product, architecture, and risk
- **Supersedes:** None

## Context

ThesisPulse AI requires a controlled initial universe so that data quality, feature behavior, liquidity, risk, execution, and broker reconciliation can be validated before expanding coverage. A broad unrestricted universe would increase stale-data risk, spread and slippage variability, corporate-action complexity, and operational load.

## Decision

The initial market scope is Indian exchange-traded instruments associated with:

- NIFTY 50;
- BANK NIFTY;
- FINNIFTY;
- selected highly liquid equities.

The universe is versioned and effective-dated. Strategies and execution services consume an approved universe version rather than independently discovering tradable instruments.

## Universe categories

### Core indices

The following index underlyings are in scope from Phase 1:

- NIFTY 50;
- BANK NIFTY;
- FINNIFTY.

Indices may be used for market context, regime analysis, confirmation, futures trading, and later options analysis. An index itself is not assumed to be directly tradable; executable instruments must be mapped to valid broker and exchange contracts.

### Index constituents

Current constituents of the approved indices are eligible for data ingestion and analysis when they satisfy the liquidity and data-quality rules in this ADR.

Index composition is not hard-coded. Membership must be maintained as effective-dated reference data so that historical backtests use the universe that existed at the evaluated point in time and avoid survivorship bias.

### Selected liquid equities

Additional equities may be admitted through a documented eligibility process. The initial target is a controlled allow-list rather than all listed equities.

## Eligibility rules

An equity or derivative contract is eligible only when all mandatory checks pass.

### Reference-data requirements

- active exchange listing;
- valid internal instrument identity;
- current broker instrument mapping;
- known tick size, lot size, exchange, segment, and trading status;
- no unresolved duplicate instrument mapping;
- effective-from and, where applicable, effective-to timestamps.

### Liquidity requirements

The universe-selection job must evaluate configurable measures such as:

- median traded value;
- median traded quantity;
- median bid-ask spread;
- percentage spread relative to price;
- quote and trade update frequency;
- futures open interest and traded volume where applicable;
- frequency of price gaps and zero-volume candles.

The exact thresholds are stored in a versioned universe policy and may differ by cash, futures, and options.

### Data-quality requirements

- required timeframes are available;
- candle completeness satisfies the active policy;
- timestamps are valid and ordered;
- no unresolved split, bonus, symbol-change, or other corporate-action discontinuity;
- stale-data rate remains below the approved threshold;
- required session boundaries can be reconstructed.

### Operational requirements

- instrument is not blocked by an operational or risk policy;
- instrument is supported by the active broker adapter;
- execution-relevant metadata has not expired;
- instrument is not in a restricted, suspended, or otherwise unsupported state.

## Universe states

Each instrument has one of the following states within a universe version:

- `ANALYSIS_ONLY` — data and intelligence are permitted; no trade plan may be executed;
- `PAPER_ELIGIBLE` — paper trading is permitted;
- `SHADOW_ELIGIBLE` — shadow order intent is permitted;
- `LIVE_ELIGIBLE` — restricted or scaled live execution is permitted;
- `CLOSE_ONLY` — no new exposure; approved exits are permitted;
- `SUSPENDED` — neither new exposure nor normal strategy activity is permitted.

Promotion between states requires evidence appropriate to the target environment.

## Initial live allow-list policy

The initial live universe must be smaller than the paper and shadow universe. Live eligibility requires:

- successful paper and shadow observations;
- acceptable spread and slippage behavior;
- successful instrument mapping and reconciliation tests;
- no unresolved data-quality alerts;
- explicit risk-policy approval;
- inclusion in a versioned live allow-list.

## Point-in-time universe requirements

Backtests and model training must use effective-dated membership. The system must retain:

- universe version;
- instrument state;
- inclusion and exclusion reasons;
- effective timestamps;
- policy version;
- source reference;
- approval identity.

A current constituent list must never be applied retroactively to historical periods.

## Exclusions for the initial release

Unless separately approved, the initial release excludes:

- illiquid small-cap and micro-cap equities;
- SME segment instruments;
- physical-delivery-sensitive derivatives close to expiry without approved handling;
- instruments with unresolved corporate actions;
- contracts with incomplete broker mappings;
- instruments with materially degraded or stale market data;
- multi-exchange duplicate symbols without a unique canonical identity.

## Canonical instrument identity

The internal identity must not be the broker token or display symbol. Each instrument record must distinguish:

- internal `instrument_id`;
- exchange;
- segment;
- security or contract type;
- underlying instrument;
- trading symbol;
- expiry where applicable;
- strike and option type where applicable;
- contract multiplier and lot size;
- broker-specific mappings with their own effective dates.

## Alternatives considered

### All exchange-listed equities from day one

Rejected because it would increase operational complexity and produce unreliable signals for illiquid instruments.

### Hard-coded list in application configuration

Rejected because index membership, instrument status, derivatives contracts, and broker mappings change over time.

### Current-universe-only history

Rejected because it introduces survivorship bias into training and backtesting.

## Consequences

- Universe management becomes a first-class versioned capability.
- Paper and shadow coverage can be wider than live coverage.
- Reference data and corporate-action processing are required before reliable backtesting.
- Strategies can reproduce the exact eligible universe used for any historical decision.
