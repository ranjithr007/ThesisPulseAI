# ADR-0003: Trading Timeframes and Confirmation Hierarchy

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI product, architecture, and intelligence
- **Supersedes:** None

## Context

ThesisPulse AI is initially an intraday system. It needs one authoritative decision timeframe and explicit roles for lower and higher timeframes. Without a hierarchy, engines may double-count correlated evidence, produce contradictory signals, use incomplete candles, or allow a fast timeframe to override higher-timeframe risk context.

## Decision

The initial trading style is five-minute intraday trading with confirmation from one-minute, fifteen-minute, hourly, and daily data.

The timeframe hierarchy is:

| Timeframe | Primary responsibility |
|---|---|
| Daily (`1d`) | structural context, broad trend, major support/resistance, volatility state, gap context |
| Hourly (`1h`) | market regime, directional bias, session context, higher-timeframe invalidation |
| Fifteen-minute (`15m`) | trend confirmation, structure confirmation, setup quality |
| Five-minute (`5m`) | primary signal and thesis decision timeframe |
| One-minute (`1m`) | entry timing, microstructure checks, spread/slippage checks, execution refinement |

## Authoritative signal timeframe

The five-minute timeframe is the authoritative initial signal timeframe.

A primary signal must reference:

- the closed five-minute candle that generated it;
- the most recent eligible closed confirmation candles;
- all engine-output versions used;
- the applicable market session;
- generation and expiry timestamps.

One-minute observations may refine execution but must not independently convert a rejected five-minute setup into an executable trade.

## Closed-candle rule

Strategy and model decisions use closed candles by default.

Incomplete candles may be used only by explicitly approved intrabar or microstructure components, and their output must:

- be marked as provisional;
- carry event-time and received-time timestamps;
- expire rapidly;
- never replace the stored closed candle;
- not be used as final training labels without an approved transformation.

## Confirmation policy

Each strategy version defines a machine-readable confirmation policy. A policy must specify:

- required timeframes;
- optional timeframes;
- directional alignment rules;
- contradiction rules;
- maximum permitted data age;
- missing-data behavior;
- weight or veto behavior;
- session-opening exceptions;
- expiry behavior.

Higher timeframes provide context and constraints; they do not automatically create entries.

## Recommended initial behavior

### Daily

Daily context is advisory for intraday direction and mandatory for structural levels, prior-day range, gap context, and broad volatility conditions. A daily contradiction should reduce confidence or block specific strategies according to their versioned policy.

### Hourly

Hourly regime is mandatory for strategies that depend on trend, range, or volatility classification. A stale or unavailable hourly regime blocks those strategies rather than silently defaulting to neutral.

### Fifteen-minute

Fifteen-minute structure is the primary setup confirmation. Strong contradiction generally blocks trend-following five-minute entries unless the strategy is explicitly a reversal strategy.

### Five-minute

The five-minute candle produces the actionable signal candidate and thesis trigger. Entry, invalidation, stop placement, expected holding period, and signal expiry are anchored here.

### One-minute

One-minute data may:

- delay an entry due to unfavorable spread or short-lived adverse flow;
- improve limit-price selection;
- identify immediate invalidation;
- refine execution timing;
- measure realized latency and slippage.

It may not widen risk or increase approved quantity.

## Candle alignment

All candle boundaries are derived from the official exchange session in `Asia/Kolkata` and stored using UTC timestamps.

For each signal timestamp, the platform must resolve the exact closed candle used for every timeframe. It must not use a higher-timeframe candle whose close occurred after the signal decision time.

## Point-in-time correctness

Features, signals, theses, training examples, and backtests must use only information available at the decision timestamp.

The implementation must prevent:

- using the completed daily candle during the same trading day;
- using a completed hourly or fifteen-minute candle before its actual close;
- forward-filled future values;
- revised data silently replacing the version used by a historical decision;
- current support/resistance calculations leaking into prior observations.

## Data freshness

Freshness thresholds are configurable by timeframe and environment. A signal cannot be executable when any mandatory timeframe exceeds its allowed age or is marked invalid.

The risk decision must record the freshness state of the primary and mandatory confirmation timeframes.

## Signal expiry

Each strategy defines a maximum signal lifetime. The initial five-minute intraday policy should normally expire a signal after a small configurable number of primary candles or when:

- entry conditions no longer hold;
- the invalidation level is breached;
- a required confirmation changes materially;
- the session reaches a configured cutoff;
- data becomes stale;
- risk or portfolio state changes materially.

Expired signals require a new signal, thesis, risk decision, and trade plan.

## Multi-timeframe scoring

Engines may emit separate timeframe outputs. Fusion must avoid counting the same underlying feature multiple times merely because it appears across correlated timeframes.

A fusion policy must expose:

- per-timeframe contribution;
- contradiction penalties;
- veto conditions;
- final confidence calibration;
- missing-data treatment.

## Alternatives considered

### One-minute as the primary signal timeframe

Rejected initially because it is more sensitive to noise, spread, latency, and transient microstructure behavior.

### Independent signals on every timeframe

Rejected because it creates conflicting trade intents and unclear risk ownership.

### Require all timeframes to point in the same direction

Rejected because strict unanimity can eliminate valid setups and hide the difference between context, setup, signal, and execution timing.

## Consequences

- Five-minute strategies have a clear authoritative decision boundary.
- Point-in-time data alignment becomes mandatory.
- Strategy policies must explicitly describe confirmation and contradiction behavior.
- One-minute components can improve execution without taking ownership of trade direction or risk.
