# ADR-0006: Capital and Risk Limits

- **Status:** Accepted
- **Date:** 2026-06-29
- **Accepted baseline version:** `risk-policy-1.0.0`
- **Decision owners:** ThesisPulse AI architecture and risk
- **Supersedes:** None

## Context

Trading decisions must be constrained by deterministic, versioned risk rules before execution. Risk controls remain independent of model confidence and apply consistently across paper, shadow and live environments.

## Decision

Risk limits are stored as immutable, versioned policy records in SQL Server and evaluated by ASP.NET Core.

No Python intelligence engine may approve position size, bypass a limit, loosen a risk control or execute a trade.

## Accepted baseline

| Control | Accepted value |
|---|---:|
| Standard risk per trade | 0.25% of eligible capital |
| Maximum configurable risk per trade | 0.50% |
| Maximum total open risk | 1.00% |
| Daily soft-loss limit | 1.00% |
| Daily hard-loss limit | 1.50% |
| Weekly loss limit | 3.00% |
| Maximum strategy drawdown | 6.00% |
| Maximum portfolio drawdown | 8.00% |
| Consecutive-loss pause | 3 closed losses |
| Maximum trades per symbol per session | 2 |

Percentages are stored as fractions: for example, `0.25%` is persisted as `0.0025`.

These values are accepted as the initial ceiling policy. They do not by themselves authorize live trading or capital deployment.

## Environment application

### Paper

Paper trading may evaluate the full accepted policy. Simulated capital and costs must be realistic and versioned.

### Shadow

Shadow evaluates the same policy against live market and portfolio observations but cannot submit broker orders.

### Restricted live

Restricted live uses the accepted ceiling policy together with a separately approved capital allocation, instrument allow-list and deployment manifest.

A restricted-live child policy may reduce any limit but cannot exceed the accepted parent ceiling.

### Scaled live

Scaled live requires a separate promotion and capital approval. Promotion cannot automatically loosen the parent risk ceiling.

## Mandatory rules

1. Position size is calculated from approved risk, entry, stop distance, lot size, contract multiplier, available margin and exposure limits.
2. A trade with missing, invalid, stale or directionally inconsistent entry, stop or target values is rejected.
3. Averaging down and martingale sizing are prohibited.
4. A stop may be tightened but cannot be removed or widened beyond the approved plan without a new risk decision.
5. Model confidence cannot increase risk beyond policy.
6. Daily hard-loss and drawdown limits block new exposure.
7. Correlated exposure is evaluated across indices, sectors, equities, futures and options.
8. Risk evaluation uses immutable capital and portfolio snapshots.
9. Every decision includes machine-readable reason codes.
10. Expired risk decisions and trade plans cannot be executed.
11. More specific policies may only reduce risk.
12. No automatic learning process may loosen these limits.

## Risk evaluation order

```text
Signal validity
  -> Thesis validity
  -> Market-data freshness and quality
  -> Instrument and session eligibility
  -> Existing position and duplicate-intent checks
  -> Capital and margin availability
  -> Portfolio, sector and correlation exposure
  -> Daily and weekly loss controls
  -> Strategy and portfolio drawdown controls
  -> Stop and target validation
  -> Position sizing
  -> Risk-reward, fees and slippage validation
  -> Approve, restrict or reject
```

## Soft-limit behavior

Crossing the daily soft-loss limit changes the affected account or strategy from `NORMAL` to `RESTRICTED`.

The baseline response is:

- block automatic risk increases;
- cap new-trade risk at no more than half the standard per-trade risk;
- allow at most one new position at a time for the affected scope;
- require all other hard limits to remain satisfied;
- permit policy or operator escalation to `CLOSE_ONLY`.

The exact response is stored in the policy version and may be made stricter by environment or strategy.

## Hard-limit behavior

Crossing the daily hard-loss, weekly loss, strategy drawdown or portfolio drawdown ceiling blocks new opening exposure for the applicable scope.

Hard-limit activation does not automatically liquidate existing positions unless the separately approved emergency-exit policy requires it. Protective exits and risk-reducing actions remain permitted.

## Consecutive-loss behavior

After three consecutive closed losing trades within the configured strategy scope:

- new entries are paused;
- active positions remain managed by their approved plans;
- outcome attribution and diagnostics are triggered;
- resumption requires the configured cooling-off and health checks;
- the pause cannot be bypassed by model confidence.

## Trade-frequency behavior

A maximum of two new trades per symbol per exchange session is permitted. Re-entry after a stop counts as a new trade.

Exit orders, protective-order replacement and broker reconciliation do not count as new trades.

## Required policy dimensions

Policies support stricter child scopes for:

- environment;
- broker account;
- strategy;
- instrument;
- sector;
- product type;
- session;
- model version.

A more specific policy may reduce but never exceed its parent ceiling.

## Pending quantitative extensions

The following require separate policy values before relevant live instruments are enabled:

- sector exposure ceiling;
- correlated-basket exposure ceiling;
- index-equivalent exposure ceiling;
- derivatives margin-utilization ceiling;
- single-instrument notional ceiling;
- gross and net portfolio exposure ceilings;
- restricted-live capital allocation.

Until defined, affected live capabilities remain disabled or use a stricter approved child policy.

## Required decision evidence

Every risk decision persists:

- policy ID and version;
- signal and thesis IDs;
- capital and portfolio snapshot IDs;
- current daily and weekly P&L;
- current strategy and portfolio drawdown;
- consecutive-loss count;
- existing and proposed exposure;
- approved risk fraction and amount;
- requested and approved quantity;
- entry, stop, targets, estimated fees and slippage;
- decision and reason codes;
- evaluation and expiry timestamps in UTC.

## Change governance

A change to any accepted ceiling requires:

1. new immutable policy version;
2. documented rationale;
3. offline and paper validation;
4. risk approval;
5. deployment-manifest update;
6. audit record;
7. rollback target.

Risk ceilings cannot be loosened automatically after losses or favorable performance.

## Alternatives considered

### Risk controlled inside each strategy

Rejected because strategies could implement inconsistent controls and bypass portfolio-level limits.

### Confidence-weighted risk without a hard ceiling

Rejected because confidence is not a substitute for deterministic risk boundaries.

### Immediate automatic rule changes after a loss

Rejected because this creates uncontrolled production learning and may overfit recent outcomes.

## Consequences

- Some high-confidence signals are rejected or downsized.
- Portfolio and capital snapshots are mandatory dependencies.
- Risk-policy changes require versioning, validation, approval and audit.
- Restricted-live activation still requires approved capital, allow-list, exposure extensions and runtime enforcement.
