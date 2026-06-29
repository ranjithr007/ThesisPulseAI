# Phase 0 Risk and Trade Plan Storage Summary

## Migration

`database/migrations/V0006__create_risk_and_trade_plan_tables.sql`

V0006 implements the deterministic ASP.NET Core risk boundary between a validated thesis and execution.

```text
Signal -> Thesis -> Risk Decision -> Trade Plan -> Execution Command
```

Neither an intelligence signal nor a validated thesis can create a broker instruction. A trade plan remains an execution envelope and does not itself submit an order.

## Immutable risk policy definitions

### `risk.risk_policies`

Stores the complete versioned policy contract, including:

- policy identity, version, environment and parent policy;
- global or scoped applicability;
- effective window;
- per-trade, open-risk, loss and drawdown ceilings;
- optional sector, correlation, margin, notional, gross and net exposure limits;
- soft-limit, hard-limit and consecutive-loss responses;
- approval metadata, checksum and canonical JSON.

The accepted baseline values are stored as fractions:

| Control | Fraction |
|---|---:|
| Standard risk per trade | `0.0025` |
| Maximum risk per trade | `0.0050` |
| Maximum total open risk | `0.0100` |
| Daily soft loss | `0.0100` |
| Daily hard loss | `0.0150` |
| Weekly loss | `0.0300` |
| Maximum strategy drawdown | `0.0600` |
| Maximum portfolio drawdown | `0.0800` |

The baseline also pauses after three consecutive losses and permits no more than two new trades per symbol per session.

Hard-limit policy rows must preserve risk-reducing exits. Intelligence confidence cannot override these limits.

### Supporting policy tables

- `risk.risk_policy_mandatory_rules` stores normalized mandatory rules.
- `risk.risk_policy_status_events` stores append-only lifecycle events.
- `risk.active_policy_assignments` maps one currently active policy to an environment and scope without modifying the immutable definition.

A child-policy ceiling comparison remains a transactional risk-service responsibility because it requires comparing two policy rows. A child policy may only tighten its parent.

## Immutable decision evidence

### `risk.capital_snapshots`

Stores the exact capital evidence used during evaluation:

- eligible and available capital;
- cash and buying power;
- available and utilized margin;
- realized and unrealized P&L;
- accrued fees;
- source identity, timestamps, canonical JSON and hash.

### `risk.portfolio_snapshots`

Stores portfolio-level risk state:

- gross and net exposure;
- open risk amount and fraction;
- daily and weekly P&L;
- strategy and portfolio drawdown;
- consecutive losses;
- open-position and session-trade counts.

### Snapshot details

- `risk.portfolio_snapshot_positions` preserves exact position, stop, P&L and open-risk evidence per instrument.
- `risk.portfolio_snapshot_exposures` preserves instrument, sector, correlated, product, gross and net exposure buckets.

Snapshots are immutable inputs. A material change to capital, margin, portfolio, prices, policy or data quality requires a new risk evaluation.

## Deterministic risk decisions

### `risk.risk_decisions`

Stores the canonical risk-decision contract with exact lineage to:

- signal;
- thesis;
- instrument;
- risk policy;
- capital snapshot;
- portfolio snapshot.

It also stores:

- `APPROVE`, `RESTRICT` or `REJECT`;
- requested and approved risk fraction and amount;
- requested and approved quantity;
- entry, stop and target context;
- estimated margin, fees and slippage;
- risk-reward ratio;
- loss, drawdown, open-risk and exposure state;
- evaluation and expiry timestamps;
- canonical JSON, hash, correlation and causation IDs.

Database checks ensure:

- approved risk never exceeds requested risk;
- approved quantity never exceeds requested quantity;
- a rejection has zero approved risk and quantity;
- approval or restriction has positive approved risk and quantity;
- estimates and exposure measurements remain valid;
- decisions expire and cannot be reused indefinitely.

### Decision evidence tables

- `risk.risk_decision_reason_codes` stores unique machine-readable reason codes.
- `risk.risk_decision_targets` stores the evaluated target sequence.
- `risk.risk_decision_limit_checks` stores every policy check with scope, current/projected value, result and hard-limit classification.

## Immutable trade plans

### `risk.trade_plans`

Stores the maximum execution envelope approved by risk:

- exact risk-decision, thesis, signal and instrument lineage;
- side and position intent;
- entry order type and acceptable price band;
- approved and minimum execution quantity;
- partial-fill permission;
- mandatory stop-loss definition;
- maximum slippage;
- time in force and session boundaries;
- exit-policy permissions;
- execution-policy version;
- expiry, status and supersession lineage.

Database checks enforce:

- valid order-type-specific limit and trigger fields;
- positive approved quantity;
- mandatory stop protection;
- stop below entry for `BUY` and above entry for `SELL`;
- valid session ordering;
- one current trade-plan version per risk decision.

The execution layer may improve price or reduce quantity. It may not increase quantity, widen the stop, exceed slippage, add exposure, or remove protection without a new risk decision and trade plan.

### Trade-plan child tables

- `risk.trade_plan_targets` stores ordered prices and quantity fractions.
- `risk.trade_plan_status_events` stores append-only lifecycle events from `CREATED` through completion, cancellation, expiry or supersession.

## Runtime semantic rules

The ASP.NET Core risk service must write each aggregate in one transaction and validate that:

- environment is consistent across signal, thesis, snapshots, policy, decision and plan;
- the signal and thesis are current, valid and unexpired;
- the selected active policy is applicable to the exact scope;
- child policies do not exceed parent ceilings;
- snapshots are fresh enough for the environment;
- approved quantity respects lot size, margin and all exposure limits;
- targets are directionally consistent;
- target quantity fractions total exactly `1.0` under the active policy;
- a trade plan is created only from an eligible, unexpired `APPROVE` or permitted `RESTRICT` decision;
- trade-plan values do not widen any approved decision value;
- the initial status event matches the contract status;
- rejected or expired decisions and plans cannot be bypassed.

## Verification

`database/verification/V0006__verify_risk_and_trade_plan_tables.sql`

The verification checks:

- 15 required tables;
- 21 trusted foreign keys;
- 17 required operational and filtered indexes;
- 37 selected trusted check constraints;
- fixed precision for risk fractions, monetary amounts, quantities and prices;
- the V0006 database baseline marker.

## Local acceptance

```powershell
cd "D:\00 Projects\ThesisPulseAI"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\migrations\V0006__create_risk_and_trade_plan_tables.sql"

sqlcmd `
  -S "(localdb)\MSSQLLocalDB" `
  -d "ThesisPulseAI" `
  -E `
  -b `
  -I `
  -i ".\database\verification\V0006__verify_risk_and_trade_plan_tables.sql"
```

Repeat both commands once. Acceptance requires `PASS V0006` without duplicate-object or filtered-index errors.

## Deferred implementation

V0006 provides storage and structural controls. Later application batches will add:

- accepted baseline risk-policy seed;
- active-policy resolver;
- immutable snapshot writers;
- deterministic risk-evaluation service;
- parent/child policy ceiling validator;
- lot-size and margin-aware position sizing;
- trade-plan semantic validator;
- plan expiry and status workers;
- end-to-end thesis-to-plan integration tests.

## Next migration

V0007 will add execution commands, broker orders, order events, fills, idempotency state and reconciliation evidence.
