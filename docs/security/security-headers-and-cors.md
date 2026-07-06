# Security headers and CORS hardening

Phase 6.2 adds shared defensive response headers and explicit CORS origin validation to ThesisPulse AI.

The goal is to harden API/browser interaction without changing execution, risk, broker, portfolio, authentication, authorization, or LIVE authority.

## Shared security headers

Every API receives the shared security-header middleware through:

```csharp
AddThesisPulsePlatformFoundation()
UseThesisPulsePlatformFoundation()
```

The middleware emits these headers on platform and API responses:

```text
X-Content-Type-Options: nosniff
Referrer-Policy: no-referrer
X-Frame-Options: DENY
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Resource-Policy: same-site
Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=(), fullscreen=(), interest-cohort=()
```

These headers are metadata-only response hardening. They do not record operator data and do not affect trading authority.

## Strict-Transport-Security

HSTS is intentionally disabled by default so local Windows HTTP startup remains usable.

```text
SecurityHeaders:EnableStrictTransportSecurity = false
```

HSTS can only be enabled explicitly and is rejected for Development/PAPER local environments. When enabled in a non-local environment, it is emitted only on HTTPS responses.

Example non-local configuration:

```json
{
  "Platform": {
    "Environment": "LIVE"
  },
  "SecurityHeaders": {
    "EnableStrictTransportSecurity": true,
    "StrictTransportSecurityMaxAgeSeconds": 31536000,
    "IncludeSubDomains": true,
    "Preload": false
  }
}
```

## CORS origin validation

Frontend-facing services use the shared validator:

```csharp
CorsOriginValidator.ResolveAllowedOrigins(builder.Configuration)
```

The validator preserves the local default:

```text
http://localhost:5173
```

It rejects unsafe or ambiguous origins:

- wildcard origins;
- empty origins;
- duplicate origins, case-insensitive;
- non-absolute origins;
- non-HTTP/HTTPS schemes;
- origins with a path;
- origins with query strings;
- origins with fragments;
- origins with user info.

Allowed origins must be explicit origins only, for example:

```json
{
  "Cors": {
    "AllowedOrigins": [
      "http://localhost:5173",
      "https://operator.thesispulse.example"
    ]
  }
}
```

## Validation commands

Run the structural validation:

```powershell
.\scripts\security\Test-ThesisPulseSecurityHeadersCors.ps1
```

Run the executable tests:

```powershell
dotnet run --project `
  .\tests\phase62-security-headers-cors\dotnet\ThesisPulse.Phase62SecurityHeadersCors.Tests.csproj `
  --configuration Release
```

The focused Phase 6.2 CI also runs .NET, Python, React, PowerShell, authentication, and operator-audit regressions.

## Safety boundaries

Phase 6.2 does not add or relax:

- execution authority;
- broker authority;
- portfolio mutation authority;
- risk override authority;
- authentication requirements;
- authorization requirements;
- LIVE trading authority.
