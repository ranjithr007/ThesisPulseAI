# Phase 0 Operational Controls Summary

## Audit and lineage

Every trade is traceable from market snapshot through engine outputs, signal, thesis, risk decision, trade plan, execution command, order events, fills, position, P&L and outcome attribution.

Corrections create compensating or superseding records. Historical operational evidence is not silently rewritten.

## Security

- Secrets are stored outside source control in an approved secret manager.
- Each service and environment uses a separate identity.
- Python intelligence services receive no live broker credentials.
- Paper and shadow cannot submit live orders.
- Runtime identities do not receive schema-owner permissions.
- Privileged actions are authorized and audited.
- Tokens, passwords, keys and full sensitive account values are excluded from logs and events.

## Operating modes

- `NORMAL`
- `RESTRICTED`
- `CLOSE_ONLY`
- `PAUSED`
- `HALTED`
- `RECOVERY`

The most restrictive active control wins. New exposure fails closed, while approved risk-reducing exits remain available where safe.

## Kill switches

Controls may apply to the platform, environment, broker account, strategy, model, engine, instrument, segment or action type.

Triggers include loss and drawdown breaches, stale data, broker failure, unresolved order outcomes, reconciliation conflicts, abnormal slippage, invalid deployments, clock drift, service failure and suspected credential compromise.

Hard controls require reconciliation, health verification and accountable approval before reset.

## Market-data quality

Mandatory market inputs are evaluated for freshness, completeness, uniqueness, ordering, session alignment, validity, revision and point-in-time eligibility.

Stale, conflicted or invalid mandatory data cannot create new exposure. Historical decisions retain the exact data revision they consumed.

## Added contracts

- `contracts/v1/operational-control.schema.json`
- `contracts/v1/data-quality-assessment.schema.json`

## Remaining Phase 0 exit work

- approve ADR-0006 risk-limit values;
- approve the initial live allow-list;
- define numeric strategy promotion thresholds;
- implement shared .NET and Python contract fixtures;
- implement the initial SQL Server migration;
- prove one complete lifecycle with runtime lineage;
- implement service identities, secret storage, kill switches and data-quality enforcement.
