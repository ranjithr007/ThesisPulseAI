# ADR-0006: Capital and Risk Limits

- **Status:** Proposed
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture and risk
- **Supersedes:** None

## Context

Trading decisions must be constrained by deterministic, versioned risk rules before execution. Risk controls must remain independent of model confidence and must apply consistently across paper, shadow, and live environments.

## Decision

Risk limits will be stored as versioned policy records in SQL Server and evaluated by ASP.NET Core. No Python intelligence engine may approve position size, bypass a limit, or directly execute a trade.

## Initial baseline

| Control | Initial value |
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

These are starting values and require explicit approval before live activation.

## Mandatory rules

1. Position size is calculated from approved risk, entry, stop-loss distance, instrument lot size, contract multiplier, available margin, and applicable exposure limits.
2. A trade with missing, invalid, stale, or directionally inconsistent entry, stop, or target values is rejected.
3. Averaging down and martingale sizing are prohibited.
4. A stop-loss may be tightened by an approved policy but may not be removed or widened beyond the approved trade-plan boundary without a new risk decision.
5. Model confidence cannot increase risk beyond the active policy limit.
6. Daily hard-loss and drawdown limits block new exposure.
7. Correlated exposure must be evaluated across indices, sectors, equities, futures, and options.
8. Risk evaluation must use an immutable capital and portfolio snapshot reference.
9. Every approval or rejection must include machine-readable reason codes.
10. Expired risk decisions and trade plans cannot be executed.

## Risk evaluation order

```text
Signal validity
  -> Thesis validity
  -> Market-data freshness and quality
  -> Instrument/session eligibility
  -> Existing position and duplicate-intent checks
  -> Capital and margin availability
  -> Portfolio and correlation exposure
  -> Daily/weekly loss and drawdown controls
  -> Stop and target validation
  -> Position sizing
  -> Risk/reward and slippage validation
  -> Approve or reject
```

## Soft limit behavior

A soft limit does not automatically authorize continued trading. Crossing a soft limit changes the account or strategy to a restricted state. The policy may:

- reduce allowed risk;
- reduce concurrent positions;
- block selected strategies;
- require an explicit operational approval;
- move the system to close-only mode.

The exact action must be encoded in the policy version.

## Hard limit behavior

A hard limit blocks new opening exposure. It does not automatically liquidate existing positions unless a separately approved emergency-exit policy requires it.

## Required policy dimensions

Risk policies must support overrides that only reduce risk at these scopes:

- environment;
- broker account;
- strategy;
- instrument;
- sector;
- product type;
- session;
- model version.

A more specific policy may reduce but may not exceed the parent policy ceiling.

## Required decision evidence

Every risk decision must persist:

- policy version;
- signal and thesis identifiers;
- capital snapshot identifier;
- portfolio snapshot identifier;
- current daily and weekly P&L;
- current strategy and portfolio drawdown;
- existing and proposed exposure;
- approved risk percentage and amount;
- requested and approved quantity;
- entry, stop, targets, and estimated slippage;
- decision and reason codes;
- evaluation and expiry timestamps in UTC.

## Alternatives considered

### Risk controlled inside each strategy

Rejected because strategies could implement inconsistent controls and bypass portfolio-level exposure limits.

### Confidence-weighted risk without a hard ceiling

Rejected because confidence is not a reliable substitute for deterministic risk boundaries.

### Immediate automatic rule changes after a loss

Rejected because this creates uncontrolled production learning and can amplify noise or overfit recent events.

## Consequences

- Some high-confidence signals will be rejected or downsized.
- Portfolio and capital snapshots become mandatory dependencies.
- Risk policy changes require versioning, validation, and audit.
- Live activation is blocked until the proposed values are reviewed and accepted.
