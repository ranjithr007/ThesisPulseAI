# ADR-0011: Canonical Engine Output and Signal Contracts

- **Status:** Accepted
- **Date:** 2026-06-29

## Context

Python intelligence engines evolve independently, while downstream services require stable, language-neutral and auditable contracts.

## Decision

ThesisPulse AI uses versioned JSON contracts for engine outputs and signals.

An engine output is one component's observation or recommendation. A signal is a normalized directional candidate created only after approved fusion and validation. Neither is a risk approval, trade plan or broker instruction.

## Common envelope

Every contract includes `contract_version`, `message_id`, `correlation_id`, `causation_id`, `environment`, `source_service`, `source_version` and `generated_at_utc`.

All timestamps are UTC. Unsupported major versions are rejected.

## Engine output

An engine output identifies its engine and version, instrument, timeframe, point-in-time timestamp, direction, score, confidence, data quality, freshness, expiry, evidence and warnings.

Accepted operational outputs are immutable. Corrections create a new output with lineage.

## Signal

Only an approved fusion or signal-creation component may create a canonical signal. A signal records strategy version, instrument, direction, primary timeframe, confirmation timeframes, strength, confidence, entry window, invalidation context, holding period, expiry and source-output lineage.

## Vocabularies

Engine directions are `STRONG_LONG`, `LONG`, `NEUTRAL`, `SHORT`, `STRONG_SHORT` and `NO_SIGNAL`.

Signal directions are `LONG` and `SHORT`.

Signal statuses are `CANDIDATE`, `VALIDATED`, `REJECTED`, `EXPIRED`, `SUPERSEDED` and `CONSUMED`.

## Numeric scales

- Engine score: `-1.0` to `1.0`.
- Confidence: `0.0` to `1.0`.
- Signal strength: `0.0` to `1.0`.
- Ratios and probabilities use fractional representation.

Confidence never overrides the active risk-policy ceiling.

## Freshness and lineage

Consumers independently verify freshness. Expired records cannot be reactivated. Signals reference the exact engine outputs and market context used to create them.

Evidence distinguishes long support, short support, contradictions, neutral context and warnings. The final result must be reproducible from a versioned fusion policy or model.

## Compatibility

Major versions may break compatibility. Minor versions are additive. Contract fixtures must validate in both .NET and Python.

## Consequences

Engines use a common vocabulary, signal creation is centralized, and historical decisions retain exact point-in-time evidence and lineage.
