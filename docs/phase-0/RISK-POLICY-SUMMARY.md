# Phase 0 Risk Policy Summary

## Accepted policy

The initial ThesisPulse AI risk ceiling is accepted as `risk-policy-1.0.0`.

| Control | Value |
|---|---:|
| Standard risk per trade | 0.25% |
| Maximum risk per trade | 0.50% |
| Maximum total open risk | 1.00% |
| Daily soft loss | 1.00% |
| Daily hard loss | 1.50% |
| Weekly loss | 3.00% |
| Strategy drawdown | 6.00% |
| Portfolio drawdown | 8.00% |
| Consecutive-loss pause | 3 losses |
| Trades per symbol per session | 2 |

Percentages are stored as fractions. For example, 0.25% is stored as `0.0025`.

## Important boundaries

- The accepted values are ceilings, not targets.
- Child policies may reduce risk but cannot exceed the parent policy.
- Model confidence cannot raise the approved risk limit.
- Python engines cannot approve quantity or loosen risk controls.
- Daily hard loss and drawdown breaches block new exposure.
- Existing protective exits remain permitted.
- Averaging down and martingale sizing are prohibited.
- Stops cannot be removed or widened without a new risk decision.
- Live-loss learning cannot automatically loosen the policy.

## Soft-limit behavior

Crossing the daily soft-loss threshold moves the affected scope to `RESTRICTED`.

The baseline response reduces new-trade risk to no more than half the standard amount, permits at most one new concurrent position and allows escalation to `CLOSE_ONLY`.

## Consecutive-loss behavior

After three consecutive closed losses:

- new entries pause;
- active trades continue under their approved plans;
- outcome attribution and diagnostics start;
- resumption requires cooling-off and health checks.

## Live activation remains gated

This policy does not authorize live trading. Restricted live still requires:

- approved capital allocation;
- live instrument allow-list;
- sector and correlation limits;
- margin and notional exposure limits;
- verified broker capabilities;
- runtime and database enforcement;
- active kill switches and reconciliation;
- deployment-manifest approval.

## Added contract

- `contracts/v1/risk-policy.schema.json`

## Next Phase 0 implementation work

- shared contract fixtures and .NET/Python validation;
- initial SQL Server schema and migration;
- risk-policy seed and activation records;
- capital and portfolio snapshot tables;
- exposure and correlation rules;
- restricted-live capital and instrument policy.
