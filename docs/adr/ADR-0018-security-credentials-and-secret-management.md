# ADR-0018: Security, Credentials and Secret Management

- **Status:** Accepted
- **Date:** 2026-06-29
- **Decision owners:** ThesisPulse AI architecture, security and operations

## Context

ThesisPulse AI handles broker credentials, account access, operational data and privileged actions. A compromised secret or overly broad service identity could create unauthorized orders or expose financial information.

## Decision

The platform uses environment-isolated identities, centralized secret storage, least privilege, short-lived credentials where supported and auditable privileged operations.

## Secret storage

Secrets are stored in an approved external secret manager. They are not committed to source control, embedded in deployment manifests, placed in database seed files or written to logs.

Secret references may appear in configuration, but secret values may not.

Managed secret categories include:

- broker app credentials and access tokens;
- database credentials;
- service-to-service authentication material;
- encryption keys;
- webhook signing secrets;
- notification credentials;
- artifact-store credentials.

## Environment isolation

Development, paper, shadow, restricted-live and live use separate credentials and access policies.

- Paper and shadow cannot submit live broker orders.
- Python intelligence services have no live broker credentials.
- Migration credentials are separate from runtime credentials.
- Live credentials are not copied into developer machines or test fixtures.

## Identity and authorization

Each service uses its own identity. Shared service accounts are prohibited for production workloads.

Authorization follows least privilege:

- market-data readers cannot mutate execution state;
- intelligence services cannot write orders, positions or risk decisions;
- execution services cannot promote models or loosen risk policy;
- operators receive role-scoped actions;
- migration identities receive DDL rights only during controlled deployment.

High-impact actions require explicit authorization and audit, including live promotion, capital changes, risk-limit changes, kill-switch reset and credential rotation.

## Rotation and expiry

Secrets have owners, creation time, rotation schedule, expiry where applicable and last-rotation metadata.

Rotation must support overlap or controlled cutover so services do not require unsafe emergency edits. Expired or revoked credentials fail closed.

## Logging and redaction

The following must never appear in logs, events, traces, error payloads or model artifacts:

- access tokens;
- passwords;
- private keys;
- authorization headers;
- complete connection strings;
- unredacted broker account identifiers;
- full sensitive request or response payloads.

Structured redaction occurs before data leaves the process boundary.

## Service-to-service security

Internal APIs require authenticated service identities and encrypted transport outside local development. Authorization is checked at the receiving service, not assumed from network location.

Requests include correlation identity but never use correlation IDs as credentials.

## Data protection

- Sensitive data is encrypted in transit and at rest.
- Database backups and exported artifacts follow the same access policy as the source.
- Secrets are excluded from analytical datasets.
- Retention minimizes unnecessary account and personal data.
- Production access is logged and reviewed.

## Break-glass access

Emergency access is time-limited, attributable and reviewed after use. Break-glass credentials are stored separately, require strong authentication and cannot bypass audit logging.

## Compromise response

Suspected credential compromise triggers:

1. affected secret revocation;
2. new-order suspension or close-only mode where relevant;
3. credential rotation;
4. broker and database session review;
5. audit and incident investigation;
6. reconciliation of orders, positions and privileged actions;
7. controlled service restoration.

## Development rules

- Local `.env` or user-secret stores remain outside source control.
- Example configuration contains placeholders only.
- Automated secret scanning runs in CI.
- Test fixtures use synthetic accounts and tokens.
- Pull requests containing suspected credentials are blocked and the secret is rotated even if the commit is later removed.

## Alternatives considered

### Store encrypted secrets in the application database

Rejected as the primary approach because runtime database access would also grant access to broker credentials and complicate rotation.

### Use one credential across environments

Rejected because a test or development fault could affect live trading.

## Consequences

- Secret-manager and identity infrastructure are required before restricted live.
- Services need explicit permission design.
- Credential compromise has a defined containment path.
- Intelligence and research workloads remain isolated from live execution authority.
