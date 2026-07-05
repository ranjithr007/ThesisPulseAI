# Operator authentication and protected routes

Phase 6.0 protects ThesisPulse AI business APIs and operator workspaces with a shared JWT policy while preserving the platform's PAPER-only safety boundaries.

## Access model

The eight ASP.NET Core APIs use one shared authentication scheme and fallback authorization policy.

- `GET` and `HEAD` business requests require `thesispulse.read`, `thesispulse.operate`, or `thesispulse.admin`.
- `POST`, `PUT`, `PATCH`, and `DELETE` business requests require `thesispulse.operate` or `thesispulse.admin`.
- `OPTIONS` requests are allowed anonymously for browser CORS preflight only.
- `/health/live`, `/health/ready`, `/health/startup`, and `/info` remain anonymous.
- All other routes fail closed unless an endpoint explicitly declares a stronger named policy or anonymous access.

Authentication never grants execution, broker, risk-override, or LIVE authority. Existing service-level and domain-level authority checks remain mandatory.

## Windows local Development/PAPER sign-in

Start the platform normally:

```powershell
.\scripts\dev\Start-ThesisPulse.ps1
```

The launcher creates a fresh cryptographic signing key and a one-time operator password for that process group. After all services are ready, it prints:

```text
Operator username: operator
Operator password for this launch: <generated-value>
```

Open `http://localhost:5173` and enter those credentials. The generated password and signing key are injected only into child-process environments. They are not written to the process manifest, logs, source control, or application configuration files.

To choose a username or supply a temporary password explicitly:

```powershell
.\scripts\dev\Start-ThesisPulse.ps1 `
  -OperatorUsername "ranjith" `
  -OperatorPassword "<temporary-local-password>"
```

A command-line password can remain in PowerShell history. Prefer the generated password for normal local work.

The default local operator receives only `thesispulse.read`. The browser can inspect all protected operator workspaces but cannot call state-changing API endpoints.

## Browser session handling

The React application stores the access token in `sessionStorage` only.

- Closing the browser tab clears the session according to browser session-storage behavior.
- Tokens are never placed in `localStorage`.
- A centralized request layer adds the bearer token only to configured ThesisPulse API origins or same-origin `/local/*` proxy paths.
- A `401 Unauthorized` response clears the session and returns the user to sign-in.
- The header displays operator identity, granted permissions, expiry, and a sign-out control.

## Internal service identity

Background workers use a separate short-lived service JWT in local Development/PAPER mode. Service tokens contain read and operate permissions because orchestration workers must call protected intake endpoints.

The shared HTTP handler attaches that token only when the destination hostname is in `Authentication:InternalServiceHosts`. The Windows launcher permits only loopback hosts by default:

- `localhost`
- `127.0.0.1`
- `::1`

Tokens are not attached to Upstox, market-data vendors, or arbitrary external hosts.

In `ExternalJwt` mode, service-to-service access requires an independently supplied `Authentication:ServiceAccessToken` and an explicit internal-host allowlist. A missing service token fails when an internal authenticated call is attempted.

## External issuer configuration

Non-local environments must use `ExternalJwt` mode and a real identity provider. Configure through an approved secret/configuration provider:

```text
Authentication__Mode=ExternalJwt
Authentication__Authority=https://identity.example.com/
Authentication__Audience=ThesisPulse.Operator
Authentication__RequireHttpsMetadata=true
Authentication__InternalServiceHosts__0=operations.internal.example.com
Authentication__ServiceAccessToken=<short-lived-service-token>
```

The service fails startup when authentication mode, authority, audience, or required HTTPS settings are invalid. Authentication cannot be silently disabled.

Do not use `LocalDevelopment` outside both of these conditions:

- `ASPNETCORE_ENVIRONMENT=Development`
- `Platform__Environment=PAPER`

The startup validator rejects every other combination.

## Local validation

From the repository root:

```powershell
.\scripts\security\Test-ThesisPulseAuthenticationConfiguration.ps1

dotnet run --project `
  .\tests\phase60-auth\dotnet\ThesisPulse.Phase60Authentication.Tests.csproj `
  --configuration Release
```

The structural validation verifies all eight API programs, anonymous platform endpoints, package pinning, fail-closed configuration, permission rules, internal-host restrictions, launcher secret generation, PowerShell syntax, and React session handling.

The executable .NET tests validate configuration rejection, token claims, credential failure, signing-key strength, permission hierarchy, mutating-request authorization, anonymous preflight, and broker-host exclusion.

## Troubleshooting

### Service fails during startup with `Authentication:Mode`

The service has no valid authentication configuration. Use the Windows launcher for local work or supply the complete external JWT configuration.

### Local authentication rejected outside Development/PAPER

This is expected fail-closed behavior. Local token issuance cannot be used in Production, Staging, SHADOW, or LIVE environments.

### Browser immediately returns to sign-in

Check the Trading API logs and confirm:

- the service is healthy;
- the one-time credentials are from the current launcher run;
- the token has not expired;
- all services were launched together and share the same ephemeral signing key.

Restarting the stack invalidates prior local tokens because the signing key changes.

### Internal worker receives 401

Confirm the destination host is explicitly trusted and all services were started in the same process environment. Do not solve this by broadening the allowlist to external domains.

## Security response

A leaked Development/PAPER password or token is invalidated by stopping and restarting the local stack. For external issuer credentials, revoke or rotate them at the issuing identity provider and follow the repository security incident process.
