# Phase 0 Canonical Contracts Summary

## Completed

- ADR-0011: canonical engine-output and signal contracts.
- ADR-0012: thesis, risk-decision and trade-plan contracts.
- `contracts/v1/signal.schema.json`.
- `contracts/v1/thesis.schema.json`.
- `contracts/v1/risk-decision.schema.json`.
- `contracts/v1/trade-plan.schema.json`.
- `contracts/README.md` with semantic validation rules.

## Lifecycle

```text
Engine Outputs
  -> Signal
  -> Thesis
  -> Risk Decision
  -> Trade Plan
  -> Execution Command
```

Each stage has a separate identity, contract version, environment, status, timestamps and correlation lineage.

## Safety boundaries

- An engine output is not an executable signal.
- A signal does not authorize a trade.
- A validated thesis does not approve risk.
- Only ASP.NET Core creates the operational risk decision.
- A trade plan requires an approved and unexpired risk decision.
- Execution may reduce quantity or improve price, but cannot increase quantity, widen approved risk or remove the mandatory stop.
- Expired or superseded lifecycle records cannot be reused.
- Confidence cannot override the active risk policy.

## Validation layers

1. JSON Schema validates structure, required fields, enums, ranges and timestamp formats.
2. Application validators enforce cross-field direction, time ordering, lineage, freshness, quantity, exposure and policy rules.
3. Shared fixtures must produce equivalent accept or reject results in .NET and Python.

## Remaining contract work

- execution-command schema;
- order-event schema;
- fill-event schema;
- shared valid and invalid fixtures;
- .NET and Python contract test harnesses.
