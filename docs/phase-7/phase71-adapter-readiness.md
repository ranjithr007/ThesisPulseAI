# Phase 7.1 adapter readiness

This slice prepares a read-only readiness model for the external market adapter boundary.

The first implementation target is evidence only:

- configuration shape is present or missing;
- canonical instrument coverage is present or missing;
- market calendar support is present or missing;
- output stays bounded and redacted;
- existing PAPER behavior remains unchanged.

Canonical instruments for the first pass:

- NIFTY 50
- BANK NIFTY
- FINNIFTY

No external write behavior is introduced in this phase. No state mutation is introduced from readiness evidence.

## Initial status model

Phase 7.1 will extend the existing SHADOW readiness status with adapter evidence:

- adapter name
- boundary mode
- readiness version
- configuration shape check
- instrument mapping check
- market calendar check
- deterministic evidence identity

Checks that are not wired yet must remain `NOT_EVALUATED` or `FAIL`, never silently `PASS`.

## Validation plan

The structural validator for this phase should prove:

- the adapter evidence model is read-only;
- canonical instruments are included;
- readiness status exposes only bounded metadata;
- existing Phase 6 and Phase 7 security/readiness validations remain in CI;
- no external write path is added.
