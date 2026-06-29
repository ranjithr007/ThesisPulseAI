# ADR-0015: Model, Engine and Configuration Versioning

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, intelligence, risk and operations

## Context

Signals, theses, risk decisions and trade plans are only reproducible when the exact code, model, feature set, parameters, policy and runtime configuration that produced them can be identified.

A single application version is not sufficient because intelligence engines, trained models, feature definitions, thresholds, fusion weights and risk policies may evolve independently.

## Decision

Every decision-producing component is versioned independently and every operational record retains the versions that influenced it.

Versioned assets include:

- service build;
- engine implementation;
- model artifact;
- feature-set definition;
- training dataset snapshot;
- strategy policy;
- fusion policy and weights;
- threshold configuration;
- risk policy;
- execution policy;
- broker capability matrix;
- instrument universe;
- market-calendar version;
- contract schema.

## Version identity

Human-readable semantic versions are used for compatibility and release communication. Immutable identifiers and checksums are used for exact artifact identity.

A versioned artifact record includes:

- artifact type and stable name;
- semantic version;
- immutable artifact ID;
- content checksum;
- source commit SHA;
- build or training run ID;
- created timestamp;
- creator or pipeline identity;
- status;
- environment eligibility;
- dependencies and their versions;
- effective-from and effective-to timestamps;
- approval and promotion records.

A semantic version must never be reused for different content.

## Model registry

Each trained model records:

- model family and algorithm;
- serialized artifact location and checksum;
- training code commit;
- training configuration;
- feature-set version;
- dataset snapshot and point-in-time query definition;
- training window;
- validation and test windows;
- labels and horizon;
- hyperparameters;
- evaluation metrics;
- calibration method;
- supported instruments, timeframes and regimes;
- runtime dependencies;
- limitations and known failure modes.

The model artifact is immutable after registration.

## Engine and feature versioning

An engine output must retain:

- engine name and version;
- feature-set version;
- model version where applicable;
- configuration version;
- source-service version;
- market-data snapshot references;
- generated timestamp.

Feature definitions are versioned separately from feature values. A change to formula, lookback, normalization, missing-data treatment, data source or point-in-time behavior creates a new feature-set version.

## Configuration management

Production configuration is not edited in place. A configuration bundle is immutable and includes all decision-relevant values such as:

- engine enablement;
- weights;
- thresholds;
- timeframe confirmation rules;
- universe version;
- risk limits;
- execution tolerances;
- freshness limits;
- promotion state.

A new configuration bundle receives a new version and checksum. Secrets are referenced through secret identifiers and are never included in the bundle.

## Environment pinning

Each environment resolves an explicit deployment manifest that pins approved versions.

Example responsibilities of a manifest:

- service image or build versions;
- enabled engine and model versions;
- feature and strategy versions;
- risk and execution policy versions;
- contract major versions;
- broker capability version;
- universe and calendar versions.

Using `latest`, unversioned files or mutable artifact paths is prohibited in shadow and live environments.

## Compatibility

Each artifact declares compatibility constraints. Promotion validation must reject incompatible combinations, including:

- model requiring a different feature-set version;
- strategy using an unsupported engine-output schema;
- risk policy expecting fields absent from a contract version;
- execution policy using a broker capability not enabled in the active matrix;
- configuration referencing an inactive universe or calendar version.

## Lifecycle states

Canonical artifact states are:

- `DRAFT`;
- `CANDIDATE`;
- `VALIDATED`;
- `PAPER_APPROVED`;
- `SHADOW_APPROVED`;
- `RESTRICTED_LIVE_APPROVED`;
- `LIVE_APPROVED`;
- `SUSPENDED`;
- `RETIRED`;
- `REJECTED`.

State transition requires an immutable promotion record. Approval for one environment does not imply approval for another.

## Rollback

Rollback activates a previously approved deployment manifest. It does not mutate or delete the failed version.

Rollback requirements:

- target version is still compatible and approved;
- artifact and configuration checksums match;
- database and contract compatibility are verified;
- reason and operator are recorded;
- affected decisions and trades remain linked to the versions active when they were created.

## Audit and retention

Artifacts, manifests, promotion records and decision lineage are retained long enough to reproduce every regulated or operationally relevant trade.

Deletion of an artifact referenced by an operational decision is prohibited. Cold archival may be used when retrieval and checksum verification remain possible.

## Alternatives considered

### Version only the trained model

Rejected because feature, threshold, policy and execution changes can alter decisions without changing the model.

### Store configuration in mutable environment files

Rejected because historical decisions would become irreproducible and rollback would be ambiguous.

### Use Git commit alone as the version

Rejected because trained artifacts and runtime configuration can vary independently from source code.

## Consequences

- More metadata and artifact management are required.
- Every decision becomes reproducible from pinned dependencies.
- Rollback is deterministic.
- Model, rule and configuration changes can be evaluated and promoted independently without losing lineage.
