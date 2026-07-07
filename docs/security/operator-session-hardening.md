# Operator session hardening and security observability

Phase 6.5 defines the operator-session and security-observability guardrails for ThesisPulse AI.

This runbook is intentionally read-only. It does not grant execution authority, broker authority, portfolio mutation authority, risk override authority, order authority, SHADOW authority, or LIVE authority.

## Principles

- Operator-facing APIs must use the shared platform foundation.
- Authentication, operator access audit, security headers and CORS validation must remain registered through shared infrastructure.
- Security observability must report configuration shape and registration health only.
- Security observability must never emit sensitive values.
- Operator audit output must remain bounded and redacted.
- Local development credentials are temporary and must not be reused for production-like environments.

## Session lifetime guidance

Operator sessions should use short lifetimes and explicit renewal.

Recommended defaults:

- Local development: short-lived local session suitable for manual testing only.
- Shared test environments: shorter lifetime with explicit operator identity and audit capture.
- Production-like environments: centrally issued identity, rotation policy and incident revocation path.

Long-lived local credentials must not be committed, printed in logs, embedded in scripts, or stored in generated diagnostics.

## Rotation and revocation

Rotate operator session material when:

- an operator leaves the project;
- a device is lost;
- local development material may have been copied;
- suspicious authentication or authorization failures appear in audit evidence;
- repository secret scanning reports a credible finding;
- environment ownership changes.

Revocation should be handled by the issuing identity system or local environment reset process. After revocation, rerun authentication, audit, security-header, secret-hygiene and dependency-regression checks before closing the incident.

## Local development handling

Local development setup may generate temporary credentials, but those values must stay outside the repository and outside diagnostics.

Allowed:

- local-only environment variables;
- local-only ignored configuration files;
- placeholder templates with non-secret example values;
- short-lived manual test sessions.

Not allowed:

- committed local credential values;
- copied production-like values in local scripts;
- diagnostic artifacts containing sensitive values;
- screenshots or logs containing raw session material.

## Security observability surface

Security observability may expose only bounded status and registration shape, such as:

- authentication mode is configured;
- operator audit middleware is registered;
- audit storage has bounded capacity and read limits;
- security headers are registered;
- CORS validation uses explicit origin rules;
- repository security workflows exist;
- relevant security regression checks are configured.

Security observability must not expose:

- raw session material;
- signing material;
- local credential values;
- broker credential values;
- database connection values;
- request or response bodies;
- cookies;
- authorization headers;
- query strings that can contain operator input.

## Operator audit review

The existing operator audit endpoint is admin-only and returns a bounded report. It is intended for operational evidence, not for session extraction or request replay.

Review steps:

1. Confirm the audit endpoint requires the admin policy.
2. Confirm returned entries contain only metadata such as method, path, outcome, correlation ID and permission evidence.
3. Confirm the output remains bounded by configured capacity and maximum read limits.
4. Confirm failed and denied requests are captured without raw credential material.
5. Confirm local defaults do not grant admin access by default.

## Incident handling

For a suspected session or security-observability issue:

1. Revoke or rotate affected operator access material through the issuing path.
2. Preserve sanitized audit evidence and correlation IDs.
3. Review recent authentication and authorization failures.
4. Confirm no sensitive values appeared in logs, artifacts or API output.
5. Run Phase 6.5 validation and the Phase 6.0 through Phase 6.4 security regressions.
6. Document the root cause and the guardrail that prevents recurrence.

## Validation

Run from the repository root:

```powershell
.\scripts\security\Test-ThesisPulseSecurityObservability.ps1
```

The validator checks that the shared security foundation remains registered, observability output stays bounded and redacted, local defaults avoid admin access, and the Phase 6.5 workflow keeps the required regression gates.
