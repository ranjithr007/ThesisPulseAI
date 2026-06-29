# ThesisPulse AI Canonical Contracts

The `contracts/v1/` directory contains the language-neutral JSON Schemas used between ASP.NET Core and Python services.

## Contract set

- `engine-output.schema.json`
- `signal.schema.json`
- `thesis.schema.json`
- `risk-decision.schema.json`
- `trade-plan.schema.json`

## Versioning

Contracts use semantic versions.

- Major: breaking changes.
- Minor: backward-compatible additions.
- Patch: validation or documentation corrections that do not change meaning.

Consumers reject unsupported major versions.

## Validation layers

### Schema validation

JSON Schema validates required properties, primitive types, enumerations, formats, ranges and unknown fields.

### Semantic validation

Application validators must additionally enforce cross-field rules that JSON Schema cannot fully express.

#### Common rules

- All lifecycle records use the same environment.
- `generated_at_utc` and validity timestamps are UTC and logically ordered.
- The referenced instrument exists and is eligible for the environment.
- Correlation and causation lineage resolves to existing records.
- Records are not stale, expired or superseded when consumed.
- Decimal values are parsed without binary floating-point conversion in operational calculations.

#### Signal rules

- Entry-window close occurs after entry-window open.
- `valid_until_utc` does not exceed the strategy's maximum signal lifetime.
- Source engine outputs exist, are accepted, are not stale and belong to the same instrument.
- Required confirmation timeframes are present.
- For a long signal, the invalidation price is below the approved reference entry unless the strategy explicitly defines another structure.
- For a short signal, the invalidation price is above the approved reference entry unless explicitly defined otherwise.

#### Thesis rules

- The thesis references exactly one compatible signal.
- At least one scenario is marked primary.
- Scenario probabilities are non-negative and follow the active thesis-policy normalization rule.
- Invalidation conditions are machine-evaluable or have an approved manual classification.
- Evidence source identifiers resolve to immutable snapshots or outputs.

#### Risk-decision rules

- `approved_qty` is not greater than `requested_qty`.
- Approved risk amount and fraction do not exceed the active policy.
- `REJECT` implies approved quantity and approved risk are zero.
- Entry, stop and targets are directionally consistent.
- Capital and portfolio snapshots existed at evaluation time.
- Policy, signal and thesis were valid at evaluation time.
- Margin, exposure, daily loss, weekly loss and drawdown limits are satisfied.

#### Trade-plan rules

- The referenced risk decision is `APPROVE` or an allowed `RESTRICT` result.
- `approved_qty` does not exceed risk-approved quantity.
- A trade plan cannot widen the risk-decision stop or enlarge the entry envelope.
- The stop is mandatory and directionally valid.
- Target quantity fractions total no more than `1.0`; the execution policy defines residual quantity behavior.
- Entry order type fields are consistent: limit orders require a limit price and stop orders require a trigger price.
- The plan expires no later than the originating risk decision.
- The plan's session and time-in-force are supported by the active broker capability matrix.

## Required implementation tests

Both .NET and Python must run the same fixture suite:

- valid minimum payload;
- valid complete payload;
- missing required field;
- unknown field;
- invalid UUID;
- timezone-naive timestamp;
- unsupported contract version;
- out-of-range score or confidence;
- invalid lifecycle status;
- expired payload;
- broken lineage;
- cross-field directional failure;
- quantity or risk escalation attempt;
- environment mismatch.

A contract is not considered complete until both runtimes produce equivalent accept/reject results for shared fixtures.

## Operational ingestion

Schema-valid input is not automatically accepted. ASP.NET Core performs semantic, freshness, policy, lineage and authorization validation before persisting an operational record.
