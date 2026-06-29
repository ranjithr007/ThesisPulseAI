# ADR-0020: Market-Data Quality, Freshness and Stale-Data Policy

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, data, intelligence, risk and operations

## Context

Signals and risk decisions are unsafe when market data is stale, duplicated, incomplete, out of order, structurally invalid or aligned to the wrong exchange session.

## Decision

Every market observation and derived dataset carries source, timestamp, quality, revision and freshness metadata. Mandatory data that fails policy blocks new exposure for the affected scope.

## Quality dimensions

The platform evaluates:

- freshness;
- completeness;
- uniqueness;
- ordering;
- price and quantity validity;
- session alignment;
- source availability;
- correction state;
- point-in-time eligibility;
- instrument mapping validity;
- clock plausibility.

Canonical states are `VALID`, `DEGRADED`, `STALE`, `INCOMPLETE`, `DUPLICATE`, `OUT_OF_ORDER`, `CONFLICTED`, `INVALID` and `UNKNOWN`.

## Required metadata

Records retain instrument, source, source version, event time, published time, received time, processed time, session, trade date, sequence, timeframe, revision, quality reasons and freshness-policy version.

## Freshness

Thresholds are versioned by data type, timeframe, session, source, strategy and environment. Evaluation records the basis timestamp, evaluation time, measured age, maximum age, result and policy version.

Quotes, trades, candles, open interest, reference data and broker state do not share one hard-coded threshold.

## Candle rules

A candle is usable only when identity is unique, exchange-session boundaries are correct, OHLC values are consistent, volume is non-negative, the candle is closed and mandatory source data is complete.

Corrections create a new revision. Historical decisions retain the exact revision they consumed.

## Point-in-time correctness

Training, backtesting and live inference may use only information available at the evaluation timestamp. Event time, received time and revision history are preserved to prevent late data from leaking into earlier decisions.

Universe, instrument mappings and calendars are effective-dated.

## Missing or degraded data

Inputs are classified as mandatory, optional or substitutable.

- Missing mandatory data rejects or pauses the decision.
- Missing optional data lowers quality according to a versioned rule.
- Substitute sources require explicit configuration and retained provenance.
- Silent forward-fill of critical data is prohibited unless the versioned feature definition explicitly permits it.

## Conflicts and anomalies

Material source conflicts create `CONFLICTED` status. The platform never selects the value most favorable to a trade.

Anomaly checks cover impossible prices, invalid bid/ask relationships, extreme spreads, future timestamps, sequence gaps, duplicate events and values outside known market constraints. Suspect data is marked and routed; it is not silently rewritten.

## Consumer behavior

- Engines include quality state in every output.
- Fusion does not treat missing evidence as positive evidence.
- Risk independently validates market and broker freshness.
- Execution revalidates required prices, mappings and broker state before submission.
- Existing exits may use a separately approved degraded-data policy.

## Operational response

Quality incidents may trigger alerting, instrument pause, source quarantine, strategy pause, close-only or environment halt. The most restrictive applicable policy wins.

## Testing

Tests cover stale and future timestamps, missing candles, duplicates, out-of-order events, holiday boundaries, corrections, source conflicts, clock skew, source failover and point-in-time isolation.

## Consequences

More metadata and revisions are stored, but stale or corrupt data cannot silently create new exposure and historical decisions remain reproducible.
