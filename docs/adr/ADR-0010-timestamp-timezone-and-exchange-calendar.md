# ADR-0010: Timestamp, Timezone, and Exchange Calendar Rules

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, data, and trading
- **Supersedes:** None

## Context

Trading systems must distinguish market-event time, exchange time, platform-received time, processing time, and business-session dates. Timezone-naive values, server-local time, or incorrectly aligned higher-timeframe candles can create stale decisions, look-ahead bias, duplicate candles, invalid backtests, and unsafe execution.

ThesisPulse AI initially trades Indian markets and presents trading time in `Asia/Kolkata`, while services and databases may run in different locations.

## Decision

ThesisPulse AI uses UTC as the canonical timestamp standard for storage, contracts, events, logs, and internal comparisons.

`Asia/Kolkata` is used only for exchange-session interpretation, trading-date derivation, calendar rules, and user-facing display for the initial Indian-market scope.

Server-local time is never used to determine market sessions, candle boundaries, signal validity, or risk cutoffs.

## Storage standard

- SQL Server timestamps use `datetime2(7)`.
- Canonical timestamp columns end in `_at_utc` or otherwise clearly include `_utc`.
- Business dates use SQL Server `date` and are derived from the relevant exchange timezone and calendar.
- Database defaults may use `SYSUTCDATETIME()` where database-generated time is appropriate.
- Local timestamps without timezone or offset context are prohibited at integration boundaries.

Examples:

- `market_event_at_utc`;
- `exchange_published_at_utc`;
- `received_at_utc`;
- `processed_at_utc`;
- `generated_at_utc`;
- `valid_from_utc`;
- `valid_until_utc`;
- `submitted_at_utc`;
- `broker_acknowledged_at_utc`;
- `filled_at_utc`;
- `closed_at_utc`;
- `trade_date`.

## Contract standard

JSON timestamps use RFC 3339 / ISO 8601 with an explicit UTC `Z` suffix.

Example:

```json
{
  "generated_at_utc": "2026-06-29T06:30:15.123456Z"
}
```

A contract timestamp without an offset or `Z` is invalid.

## Application standards

### ASP.NET Core

- Use UTC-aware values at boundaries.
- Prefer `DateTimeOffset` for parsing and transport.
- Normalize to offset zero before persistence.
- When `DateTime` is used internally, `Kind` must be `Utc`.
- Do not call `DateTime.Now` for trading logic.
- Use an injectable clock abstraction for deterministic tests.

### Python

- Use timezone-aware `datetime` values.
- Normalize with `timezone.utc` before persistence or serialization.
- Reject or explicitly repair timezone-naive values at controlled ingestion boundaries.
- Do not use `datetime.now()` without a timezone for trading logic.
- Use an injectable clock abstraction in testable components.

## Time concepts

The platform distinguishes the following concepts rather than using one generic timestamp:

| Concept | Meaning |
|---|---|
| Event time | When the market or broker event actually occurred according to the source |
| Exchange-published time | When the exchange or vendor published the event, when available |
| Received time | When ThesisPulse AI first received the event |
| Processed time | When a service completed processing |
| Generated time | When an engine output, signal, thesis, or decision was created |
| Validity time | The interval during which an output or plan may be used |
| Persisted time | When the database committed the record, where separately required |

Latency is derived from these explicit values. Received or processed time must not silently replace event time.

## Exchange calendar

A versioned exchange-calendar service or reference dataset is authoritative for:

- regular trading sessions;
- holidays;
- special trading sessions;
- early closes or exceptional schedules;
- segment-specific session rules;
- session cutoffs for new entries;
- expiry dates and contract calendars.

Calendar rules are effective-dated and retain their source and version.

Weekday logic alone is insufficient and must not be used as the final market-open determination.

## Trading date

`trade_date` is the business date in the applicable exchange timezone, initially `Asia/Kolkata`.

It is not derived by truncating a UTC timestamp.

For example, a UTC event must first be converted to the exchange timezone and evaluated against the applicable session calendar before assigning its trading date.

## Candle boundaries

Candle boundaries are aligned to the official exchange session, not to arbitrary UTC clock boundaries or server startup time.

A candle identity is:

- `instrument_id`;
- `timeframe`;
- `candle_open_at_utc`;
- `data_source`.

Required candle timestamps include:

- `candle_open_at_utc`;
- `candle_close_at_utc`;
- source event or publication time where available;
- `received_at_utc`;
- `processed_at_utc`.

A candle is marked closed only after the exchange-aligned close boundary and the configured source-completion rule have passed.

## Multi-timeframe point-in-time rules

At any decision timestamp, the system may use only candles that were closed and available at that time.

Examples:

- the current daily candle is not a completed daily confirmation during the same session;
- an hourly candle is not available before its exchange-aligned close;
- a fifteen-minute candle cannot be used by a five-minute signal generated earlier than that fifteen-minute close;
- late corrections must be versioned and must not silently rewrite the inputs used by historical decisions.

Every signal and backtest observation must retain references to the exact candle or snapshot versions used.

## Freshness and staleness

Freshness is calculated using a defined source timestamp and the current UTC clock. Each data type and timeframe has a versioned maximum age.

A freshness decision records:

- timestamp used as the freshness basis;
- evaluation time;
- age in milliseconds;
- applicable threshold;
- resulting status;
- policy version.

Clock skew and source timestamp anomalies are surfaced as data-quality errors rather than converted into negative ages or silently accepted.

## Ordering and precision

Timestamp precision alone is not relied upon for total ordering when multiple events can share the same timestamp.

Where required, ordering also uses:

- source sequence number;
- broker event sequence;
- ingestion sequence;
- database identity;
- deterministic tie-breaker.

The system must not invent false sub-millisecond ordering by modifying timestamps.

## Clock synchronization

Production hosts must use reliable clock synchronization. Monitoring must detect material drift.

A service with clock drift beyond the approved threshold must enter a degraded or blocked state for time-sensitive trading operations according to the active policy.

## Display rules

- Indian-market dashboards default to `Asia/Kolkata`.
- Displayed values must identify the timezone when ambiguity is possible.
- User display conversion never changes stored UTC values.
- Exports include either UTC timestamps or explicit timezone metadata.

## Testing requirements

Tests must cover:

- UTC serialization and parsing;
- rejection of timezone-naive contract values;
- exchange holiday behavior;
- special sessions;
- session-open and session-close boundaries;
- UTC-to-`Asia/Kolkata` trade-date derivation;
- five-minute, fifteen-minute, hourly, and daily candle alignment;
- stale-data thresholds;
- late and out-of-order events;
- look-ahead prevention;
- clock abstraction behavior.

## Alternatives considered

### Store local `Asia/Kolkata` timestamps

Rejected because services may run in different regions and local timestamps are less interoperable and easier to misinterpret.

### Store offsets in every SQL timestamp using `datetimeoffset`

Rejected for the canonical operational model because all stored timestamps are normalized to UTC and named explicitly; exchange timezone and calendar context are modeled separately.

### Use server-local time

Rejected because deployment location or host configuration must not alter trading behavior.

### Derive market-open status from weekdays and fixed clock times

Rejected because holidays and special sessions require a versioned exchange calendar.

## Consequences

- All runtime and contract code must handle timezone-aware values.
- Exchange-calendar reference data becomes a required dependency.
- Historical decisions and backtests can reproduce exact time alignment.
- Data latency, staleness, and look-ahead checks become explicit and auditable.
