# Operator access audit and authorization evidence

Phase 6.1 adds redacted operator access audit evidence to the shared ThesisPulse AI platform foundation. Every API receives the same capture behavior through `AddThesisPulsePlatformFoundation`, `UseThesisPulsePlatformFoundation`, and `MapThesisPulsePlatformEndpoints`.

## What is captured

Audit entries are metadata-only. A protected request can produce an entry with:

- audit UID;
- UTC timestamp;
- service name;
- HTTP method;
- route path only;
- status code;
- correlation ID;
- authenticated/anonymous flag;
- operator subject;
- operator display name;
- granted permission claims;
- request class;
- authorization outcome.

The request class is one of:

- `READ`;
- `MUTATE`;
- `PREFLIGHT`;
- `AUTHENTICATION`;
- `PLATFORM_OBSERVABILITY`.

Stored audit entries exclude authentication and platform observability routes. `AUTHENTICATION` and `PLATFORM_OBSERVABILITY` exist as classifier evidence, but those requests are not stored in the audit ring buffer.

The authorization outcome is one of:

- `ALLOWED`;
- `UNAUTHENTICATED`;
- `FORBIDDEN`;
- `FAILED`.

## What is never captured

The audit middleware does not read or store:

- request bodies;
- bearer tokens;
- passwords;
- query strings;
- cookies;
- API keys;
- broker credentials;
- market-data vendor credentials.

Only the route path is stored. For example, a request to `/api/v1/signals?token=abc` records `/api/v1/signals`.

## Middleware order

The shared pipeline order is:

```text
Correlation ID -> Authentication -> Operator access audit -> Authorization -> Endpoint
```

This ordering lets audit capture see authenticated identity when available and still observe `401 Unauthorized` and `403 Forbidden` results emitted by authorization.

## Excluded routes

The following routes are not stored in the audit buffer:

- `/health/live`;
- `/health/ready`;
- `/health/startup`;
- `/info`;
- `/api/v1/auth/token`.

The token endpoint is excluded because the request contains credentials. Health and info endpoints remain anonymous observability surfaces.

## Storage model

Phase 6.1 uses a bounded in-memory ring buffer per service. This is for Development/PAPER visibility and CI validation, not a production SIEM replacement.

Default retention:

```text
OperatorAccessAudit:Capacity = 500
OperatorAccessAudit:MaximumReadLimit = 200
```

Both settings are validated at startup.

## Retrieval endpoint

Each API exposes:

```text
GET /api/v1/security/operator-audit/recent?limit=50
```

The endpoint requires `thesispulse.admin` through the `ThesisPulse.Admin` policy.

The Windows launcher still grants the local operator only:

```text
thesispulse.read
```

That means the default local operator can use the protected read-only workspaces but cannot retrieve audit history. Granting admin permission must be explicit and intentional.

## Correlation with logs

Every audit entry includes the same correlation ID used by the correlation middleware and response header:

```text
X-Correlation-ID
```

Use this value to connect:

- operator access audit entries;
- structured application logs;
- HTTP responses;
- UI/network diagnostics.

## Validation

Run the structural security check:

```powershell
.\scripts\security\Test-ThesisPulseOperatorAccessAudit.ps1
```

Run executable audit tests:

```powershell
dotnet run --project `
  .\tests\phase61-operator-access-audit\dotnet\ThesisPulse.Phase61OperatorAccessAudit.Tests.csproj `
  --configuration Release
```

The tests verify classifier exclusions, redaction, bounded retention, permission evidence, allowed outcomes, unauthenticated outcomes, forbidden outcomes, and failed outcomes.

## Safety boundaries

This phase does not add execution, broker, risk override, portfolio mutation, or LIVE authority. It only records redacted access evidence for protected surfaces.
