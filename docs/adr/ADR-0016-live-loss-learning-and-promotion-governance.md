# ADR-0016: Live-Loss Learning and Promotion Governance

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, intelligence, risk and operations

## Context

ThesisPulse AI must learn from stopped-out trades, failed theses and changing market regimes. However, directly changing live rules, weights or models after a loss can create unstable feedback loops, overfit recent noise and bypass risk governance.

The system therefore needs a controlled process that converts live outcomes into candidate improvements without allowing production behavior to mutate automatically.

## Decision

Live outcomes are immutable evidence. They may trigger analysis and candidate recommendations, but they cannot directly modify active models, thresholds, weights, rules, risk limits or execution policies.

The governed lifecycle is:

```text
Closed Trade
  -> Outcome Attribution
  -> Root-Cause Classification
  -> Candidate Recommendation
  -> Offline Validation
  -> Walk-Forward Validation
  -> Paper Validation
  -> Shadow Validation
  -> Restricted-Live Approval
  -> Scaled-Live Approval
```

Every stage is versioned, reviewable and auditable.

## Outcome attribution

Each closed trade is attributed against the exact versions that created it:

- signal;
- thesis;
- risk decision;
- trade plan;
- execution commands;
- engine outputs;
- model and feature versions;
- market regime;
- universe and calendar version;
- broker and execution observations;
- realized fees, taxes and slippage.

Attribution distinguishes decision quality from execution quality.

## Root-cause classification

A loss may have one or more canonical causes:

- valid thesis with normal probabilistic loss;
- wrong market-regime classification;
- weak or contradictory signal fusion;
- stale or incomplete data;
- incorrect entry timing;
- invalid stop placement;
- target or risk-reward design issue;
- excessive position size;
- spread or slippage deterioration;
- partial-fill or order-management issue;
- broker or infrastructure failure;
- unexpected news or event gap;
- model drift;
- feature drift;
- policy violation;
- unknown or insufficient evidence.

The classifier records confidence and evidence. A single stop loss is not proof that the prediction logic is wrong.

## Recommendation types

The learning system may create candidate recommendations for:

- engine weight adjustment;
- threshold adjustment;
- feature addition, removal or transformation;
- regime-specific policy;
- signal confirmation rule;
- entry or expiry rule;
- stop or target methodology;
- risk restriction;
- instrument or universe restriction;
- execution tolerance;
- model retraining;
- data-quality control;
- operational remediation.

A recommendation is not an approved production change.

## Candidate requirements

Each candidate includes:

- candidate ID and version;
- source outcome or incident IDs;
- hypothesis;
- proposed change;
- affected artifacts and environments;
- expected benefit;
- known risks;
- evaluation plan;
- acceptance and rejection criteria;
- minimum sample requirements;
- owner;
- creation timestamp;
- status.

## Validation gates

### Offline validation

Required checks include:

- point-in-time data correctness;
- no future leakage;
- transaction costs and slippage;
- representative instruments and regimes;
- comparison against the current approved baseline;
- sensitivity analysis;
- tail-loss and drawdown analysis;
- stability across parameter neighborhoods.

### Walk-forward validation

The candidate must be evaluated across multiple chronological folds without tuning on the final holdout period.

### Paper validation

The candidate runs on live market data without broker submission. It must produce complete lineage and simulated execution using realistic costs.

### Shadow validation

The candidate creates intended orders and compares them with broker-capability and market conditions but cannot place live orders.

### Restricted live

Restricted live requires explicit approval, limited capital, limited universe, hard loss limits and automatic rollback or suspension criteria.

### Scaled live

Scaling requires sustained evidence, no unresolved severe incidents and explicit capital-allocation approval.

## Promotion criteria

Promotion policy is versioned and defines minimum requirements such as:

- sufficient sample size;
- minimum observation duration;
- acceptable expectancy after costs;
- maximum drawdown;
- tail-loss limits;
- calibration quality;
- regime stability;
- slippage and fill quality;
- data-quality reliability;
- no material increase in correlated exposure;
- no unresolved critical reconciliation issue.

Exact thresholds are strategy and environment specific.

## Champion and challenger

The active production version is the champion. Candidates are challengers.

A challenger must be compared against the champion using the same point-in-time universe, cost model and evaluation window. Promotion requires evidence of material improvement or a documented safety improvement, not merely a higher in-sample return.

## Automatic actions allowed

The system may automatically:

- create attribution jobs;
- create candidate recommendations;
- suspend a model or strategy when a hard safety condition is met;
- move a component to paper, shadow or close-only state;
- alert owners;
- collect additional diagnostics.

The system may not automatically:

- increase live capital;
- loosen risk limits;
- change production weights or thresholds;
- replace the champion model;
- enable new instruments or broker capabilities;
- resume a hard-suspended component without the required approval path.

## Repeated stop-loss analysis

To reduce repeated failures, the platform groups comparable stopped-out trades by:

- strategy and version;
- instrument class;
- regime;
- direction;
- setup type;
- entry condition;
- stop methodology;
- volatility bucket;
- time of day;
- execution conditions;
- root-cause classification.

A repeated pattern may generate a candidate restriction or correction. The correction must still pass the normal validation and promotion pipeline.

## Guardrails against overfitting

- No rule change from one trade alone unless it addresses a proven safety or operational defect.
- Minimum sample and duration rules are mandatory.
- Post-loss tuning must include unaffected winning and losing periods.
- Multiple-testing and selection bias are documented.
- Risk limits cannot be loosened to improve backtest results.
- Holdout data remains untouched until final evaluation.
- Candidate rejection reasons are retained.

## Rollback and suspension

Every promotion defines rollback triggers, including:

- hard loss or drawdown breach;
- abnormal slippage;
- calibration failure;
- data-quality degradation;
- execution or reconciliation incident;
- behavior outside validated bounds.

Rollback activates a prior approved manifest. Suspension may be automatic; re-promotion follows the governed process.

## Human and automated approvals

Approval policy identifies required roles for each transition. A requester cannot be the sole approver for restricted or scaled live promotion.

Automated checks may approve technical validation gates, but live capital promotion requires an explicit accountable approval record.

## Alternatives considered

### Change weights immediately after a stopped-out trade

Rejected because it creates unstable, non-reproducible online learning and can overfit random outcomes.

### Retrain periodically without attribution

Rejected because performance changes could not be tied to specific failure modes or validated hypotheses.

### Disable all learning from live outcomes

Rejected because execution and market evidence are essential for improving the system.

## Consequences

- Learning is slower than uncontrolled online adaptation but substantially safer.
- Every production change has evidence, lineage and rollback capability.
- Repeated stop-loss patterns can be addressed without allowing one loss to rewrite live behavior.
- Model and policy governance becomes a first-class operational capability.
