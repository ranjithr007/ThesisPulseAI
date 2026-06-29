# ThesisPulse AI Canonical Contracts

The `contracts/v1/` directory contains the language-neutral JSON Schemas shared by ASP.NET Core and Python services.

## Contract set

### Intelligence and decision lifecycle

- `engine-output.schema.json`
- `signal.schema.json`
- `thesis.schema.json`
- `risk-decision.schema.json`
- `trade-plan.schema.json`

### Execution lifecycle

- `execution-command.schema.json`
- `order-event.schema.json`
- `fill-event.schema.json`

## Versioning

Contracts use semantic versions.

- Major: breaking changes.
- Minor: backward-compatible additions.
- Patch: validation or documentation corrections that do not change meaning.

Consumers reject unsupported major versions.

## Validation layers

### Schema validation

JSON Schema validates required properties, primitive types, enumerations, formats, ranges, command-specific fields and unknown properties.

### Semantic validation

Application validators enforce cross-field, lifecycle, freshness, risk, state-transition and broker-capability rules that JSON Schema cannot fully express.

## Common rules

- All records in one lifecycle use the same environment.
- UTC timestamps are logically ordered.
- Instrument, account and lineage references exist and are compatible.
- Records are not stale, expired or superseded when consumed.
- Correlation and causation lineage resolves to immutable records.
- Decimal values are not converted through binary floating point for operational calculations.
- Duplicate message IDs are idempotently ignored per consumer.

## Signal rules

- Entry-window close occurs after entry-window open.
- Source engine outputs are accepted, current and belong to the same instrument.
- Required confirmation timeframes are present.
- Invalidation is directionally consistent with the strategy.

## Thesis rules

- The thesis references one compatible signal.
- At least one scenario is primary.
- Evidence sources resolve to immutable snapshots or outputs.
- Invalidation conditions are machine-evaluable or explicitly classified.

## Risk-decision rules

- Approved quantity is no greater than requested quantity.
- Approved risk does not exceed policy.
- `REJECT` implies zero approved quantity and risk.
- Entry, stop and targets are directionally consistent.
- Capital, portfolio, policy, signal and thesis were valid at evaluation time.

## Trade-plan rules

- The risk decision permits the plan.
- Quantity does not exceed risk approval.
- The plan cannot widen stop distance or enlarge the entry envelope.
- The stop is mandatory.
- Target quantity fractions do not exceed `1.0`.
- The plan expires no later than its risk decision.
- Product, order type and validity are supported by the active broker capability matrix.

## Execution-command rules

- The trade plan is ready, approved and unexpired.
- `PLACE` requires instrument, side, position intent, quantity, order type, validity and client-order identity.
- `MODIFY` requires target order, expected version and at least one permitted changed field.
- `CANCEL` requires target order and expected version.
- Quantity cannot exceed the plan or current unfilled quantity.
- Modify cannot increase risk or loosen the approved execution envelope.
- A duplicate idempotency key returns the original command result.
- Unknown post-submission outcome enters reconciliation before retry.

## Order-event rules

- State transition is allowed by the active transition policy.
- Order version increases monotonically.
- Cumulative fill quantity never decreases.
- Filled plus remaining quantity is consistent with requested quantity.
- Terminal states cannot regress.
- Older or duplicate events may be retained but cannot overwrite a newer projection.
- Contradictory broker state enters `RECONCILIATION_REQUIRED`.

## Fill-event rules

- A broker fill ID or deterministic fill fingerprint is required.
- Fill identity is unique per broker account.
- Quantity and price are positive.
- The fill belongs to the same order, trade plan, instrument, side and environment.
- Cumulative fills cannot exceed order quantity without reconciliation escalation.
- Position and protective-exit quantities are based on actual net fills.
- Gross amount, charges and net amount follow the active accounting policy.

## Required implementation tests

Both .NET and Python run the same fixture suite:

- valid minimum and complete payloads;
- missing required field;
- unknown field;
- invalid UUID or timestamp;
- unsupported contract version;
- invalid lifecycle status;
- expired payload;
- broken lineage;
- risk or quantity escalation;
- environment mismatch;
- invalid command-specific fields;
- invalid order-state transition;
- duplicate idempotency key;
- duplicate fill identity;
- late and out-of-order broker events;
- unknown broker submission outcome.

A contract is complete only when both runtimes produce equivalent accept or reject results for shared fixtures.

## Operational ingestion

Schema-valid input is not automatically accepted. ASP.NET Core performs semantic, freshness, policy, lineage, authorization, state-machine and broker-capability validation before persisting operational state or contacting the broker.
