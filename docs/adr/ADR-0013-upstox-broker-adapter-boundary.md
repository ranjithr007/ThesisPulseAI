# ADR-0013: Upstox Broker Adapter Boundary

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, execution and operations

## Context

ThesisPulse AI uses canonical execution contracts while the broker exposes provider-specific request fields, status values, instrument identifiers, authentication, rate limits and operational restrictions. Direct use of Upstox models outside infrastructure would tightly couple risk, execution and portfolio logic to one broker.

## Decision

All Upstox-specific behavior remains inside `ThesisPulse.Infrastructure.Brokers.Upstox` behind canonical broker interfaces.

Domain and application projects must not reference Upstox SDK classes, endpoint models, product codes, order-status strings, access-token structures or instrument tokens.

## Canonical interface

The adapter implements operations equivalent to:

- validate capability;
- place order;
- modify order;
- cancel order;
- get order;
- get order history;
- get order trades;
- get order book;
- get positions;
- get holdings;
- get funds and margin context;
- subscribe to or receive order updates where supported;
- reconcile orders, trades and positions;
- refresh broker instrument mappings.

The exact C# interface is defined in the application layer and uses only ThesisPulse AI canonical commands and results.

## Translation boundary

The adapter translates canonical values into broker values.

Examples include:

- `INTRADAY` to broker product `I`;
- `DELIVERY` or approved carry intent to broker product `D` where supported;
- canonical `STOP_LIMIT` to broker `SL`;
- canonical `STOP_MARKET` to broker `SL-M`;
- canonical `DAY` and `IOC` to broker validity values;
- internal `instrument_id` to the current effective Upstox instrument key;
- broker order statuses to ThesisPulse AI normalized order states.

Mappings are versioned, tested and never embedded in strategy code.

## Capability checks

Before creating a broker request, the adapter validates the active capability matrix for:

- environment;
- account;
- exchange segment;
- instrument class;
- product;
- order type;
- validity;
- market session;
- quantity and lot rules;
- tick size;
- freeze quantity;
- market-protection requirements;
- after-market behavior;
- regulatory or account restrictions.

Unsupported combinations are rejected before network submission.

## Authentication and secrets

- Access tokens and app secrets are available only to the Upstox adapter runtime.
- Credentials are never stored in contracts, logs, model artifacts or Python services.
- Token acquisition and rotation are isolated behind a credential provider.
- Logs contain redacted broker account references and no authorization headers.
- Live, shadow and paper environments use isolated configurations.

## Broker request identity

The adapter sends a stable client tag where supported, derived from the ThesisPulse AI order or command identity within broker field limits.

The adapter persists:

- execution command ID;
- internal order ID;
- idempotency key;
- broker order ID;
- broker tag;
- request and response timestamps;
- endpoint and adapter version;
- normalized result;
- redacted archived payload where permitted.

A successful HTTP response is not treated as a fill. It is an acknowledgement or accepted submission result that must continue through order-event and reconciliation processing.

## Error normalization

Broker errors are mapped into stable categories:

- validation failure;
- unsupported capability;
- authentication failure;
- authorization or account restriction;
- rate limited;
- market closed or session restriction;
- instrument unavailable;
- insufficient funds or margin;
- exchange rejection;
- transient broker failure;
- transport timeout;
- unknown outcome;
- reconciliation conflict.

Raw error codes are retained for diagnostics but do not leak into domain decisions as business enums.

## Timeout behavior

A timeout after submission is an unknown outcome. The adapter must query order state using the persisted command, client tag and broker account before any retry.

Blind retries of place, modify or cancel operations are prohibited.

## Status normalization

The adapter maps broker status into canonical order states while retaining the original status.

Unknown or contradictory statuses produce `RECONCILIATION_REQUIRED` rather than being guessed as failed or cancelled.

## Portfolio ownership

Broker positions, holdings and funds are external observations. SQL Server remains the operational source of truth, but broker observations are authoritative evidence during reconciliation.

The platform must not overwrite internal history destructively when broker state differs. It records the discrepancy and applies approved compensating events or operational resolution.

## Regulatory and capability drift

Broker and exchange behavior may change. Therefore:

- capability data is versioned and effective-dated;
- documentation is reviewed before production promotion;
- sandbox or restricted-live probes validate critical paths;
- regulatory headers and app settings are configuration, not hard-coded domain behavior;
- a capability change can disable affected actions without redeploying strategy code.

## Alternatives considered

### Use Upstox SDK types throughout the solution

Rejected because it couples domain logic to one provider and makes testing and broker replacement difficult.

### Let Python call Upstox directly

Rejected because it bypasses execution, risk, credential and reconciliation boundaries.

### Treat broker status as internal state

Rejected because provider statuses can change and may not represent the ThesisPulse AI lifecycle precisely.

## Consequences

- Adapter translation and contract tests are required.
- Broker-specific changes remain isolated.
- Execution and risk logic stay portable and testable.
- Unknown outcomes are handled through reconciliation instead of duplicate submission.
