# Phase 0 Model and Learning Governance Summary

## Versioned decision system

ThesisPulse AI versions every component that can change a decision:

- service build;
- intelligence engine;
- model artifact;
- feature set;
- training dataset snapshot;
- strategy and fusion policy;
- thresholds and configuration;
- risk and execution policy;
- broker capability matrix;
- universe and exchange calendar;
- contract schema.

Each operational record retains the versions that influenced it.

## Deployment manifest

Every paper, shadow, restricted-live and live environment uses an immutable deployment manifest. The manifest pins exact artifact IDs, semantic versions, checksums and compatibility results.

Mutable `latest` references are prohibited in shadow and live environments.

Rollback activates a previously approved manifest; it does not rewrite history or delete the failed version.

## Live-loss learning

A stopped-out trade becomes evidence, not an automatic production change.

```text
Trade Outcome
  -> Attribution
  -> Root Cause
  -> Learning Candidate
  -> Offline Test
  -> Walk Forward
  -> Paper
  -> Shadow
  -> Restricted Live
  -> Scaled Live
```

## Repeated stop-loss correction

Comparable losses are grouped by strategy, regime, setup, direction, entry, stop methodology, volatility, time of day and execution conditions.

A repeated failure pattern can create a candidate restriction or correction. The candidate must still pass validation and promotion gates before it affects production.

## Automatic actions allowed

- trigger outcome attribution;
- generate a candidate recommendation;
- suspend unsafe strategies or models;
- move a component to paper, shadow or close-only;
- alert operators and collect diagnostics.

## Automatic actions prohibited

- increasing live capital;
- loosening risk limits;
- changing live weights or thresholds;
- replacing the active model;
- enabling instruments or broker capabilities;
- resuming a hard-suspended component without approval.

## Champion and challenger

The current approved deployment is the champion. Proposed versions are challengers. Comparisons use the same point-in-time universe, costs and evaluation windows.

Promotion requires out-of-sample evidence or a documented safety improvement, not only a better in-sample result.

## Added contracts

- `contracts/v1/deployment-manifest.schema.json`
- `contracts/v1/learning-candidate.schema.json`

## Remaining implementation work

- model and artifact registry tables;
- deployment manifest persistence and compatibility checker;
- outcome-attribution contract and jobs;
- candidate workflow and promotion service;
- walk-forward, paper and shadow evaluation workers;
- approval and rollback APIs;
- shared .NET and Python contract fixtures.
