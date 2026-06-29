# Upstox Capability Matrix

- **Verified against official documentation:** 2026-06-29
- **Purpose:** Adapter configuration baseline, not a permanent broker guarantee
- **Owner:** ThesisPulse AI execution and broker integration

## Principle

Canonical ThesisPulse AI concepts are mapped to current Upstox capabilities only inside the adapter. Every capability is effective-dated and may be disabled by account, regulatory, exchange, segment, session or broker restrictions.

## Current documented order capabilities

| Canonical concept | Upstox mapping | Baseline status | Notes |
|---|---|---|---|
| Buy | `BUY` | Supported | Adapter validates instrument and session |
| Sell | `SELL` | Supported | Short-selling eligibility remains policy-controlled |
| Market | `MARKET` | Conditional | Exchange restrictions and market-protection rules can apply |
| Limit | `LIMIT` | Supported | Price must follow tick-size rules |
| Stop limit | `SL` | Supported | Trigger and limit relationship is side-dependent |
| Stop market | `SL-M` | Conditional | Market-protection and exchange restrictions can apply |
| Day validity | `DAY` | Supported | Default validity in current docs |
| Immediate or cancel | `IOC` | Supported | Must be enabled by instrument/order policy |
| Intraday product | `I` | Supported | Canonical `INTRADAY` |
| Delivery product | `D` | Supported | Canonical delivery/carry mapping depends on instrument |
| Margin trading facility | `MTF` | Out of initial scope | Broker-documented but not enabled for ThesisPulse AI v1 |
| After-market order | Broker-determined/session-aware | Out of initial live scope | Intraday platform should reject AMO initially |
| Modify order | Broker modify API | Supported conditionally | Requires current modifiable state and optimistic version check |
| Cancel order | Broker cancel API | Supported conditionally | Requires current cancellable state |
| Multi-order placement | Broker API exists | Out of initial scope | Not equivalent to atomic multi-leg execution |
| GTT | Broker API group exists | Out of initial scope | Requires separate lifecycle design |
| Exit all positions | Broker API exists | Emergency use only | Must not be called by strategy code |

## Initial ThesisPulse AI enablement

### Cash equities

- `BUY` and policy-approved `SELL`;
- `MARKET`, `LIMIT`, `STOP_LIMIT`, `STOP_MARKET` only when capability checks pass;
- `DAY` and `IOC`;
- `INTRADAY` initially;
- delivery remains disabled for automatic live strategies until separately approved;
- no AMO;
- no MTF;
- no GTT.

### Index futures

- enabled after cash restricted-live validation;
- unit quantity must be a valid multiple of lot size;
- expiry, freeze quantity, margin and contract liquidity must pass;
- carry-forward is disabled initially;
- market orders remain conditional on current exchange/broker rules.

### Options

- data, research, paper and shadow first;
- restricted-live single-leg options require separate approval;
- multi-order APIs do not imply safe multi-leg atomicity;
- GTT and multi-leg execution remain disabled.

## Broker request fields to translate

The adapter owns translation for fields such as:

- quantity;
- product;
- validity;
- price;
- tag;
- instrument token or instrument key;
- order type;
- transaction type;
- disclosed quantity;
- trigger price;
- after-market indicator or broker-inferred session behavior;
- market protection;
- regulatory or approved-algo headers where applicable.

No field above is exposed directly to strategy or intelligence code.

## Runtime capability record

A capability record should include:

- broker code;
- adapter version;
- environment;
- broker account scope;
- exchange and segment;
- instrument class;
- canonical product;
- canonical order type;
- canonical validity;
- support status;
- restrictions;
- source documentation version or review date;
- effective-from and effective-to UTC;
- verification status;
- approved-by identity.

## Verification gates

Before enabling a capability in restricted live:

1. official documentation review;
2. adapter unit tests;
3. sandbox or non-live validation where supported;
4. shadow request construction and response parsing;
5. restricted-live small-quantity probe;
6. reconciliation validation;
7. explicit promotion record.

## Official references reviewed

- Upstox Developer API documentation hub
- Place Order and Orders documentation
- Instruments and BOD contract files
- Order details, order history, trade and portfolio API groups
- Realtime, WebSocket and webhook documentation groups

The adapter team must re-check official documentation before every live capability promotion because broker and regulatory behavior can change.
