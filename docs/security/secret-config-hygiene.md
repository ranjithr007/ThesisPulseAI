# Secret and configuration hygiene

Phase 6.3 adds repository-level guardrails so ThesisPulse AI does not accidentally commit credentials, broker keys, signing material, access tokens, private keys, or raw operational connection strings.

## Where secrets belong

Use approved secret locations only:

- local process environment variables;
- Windows user secret stores approved for local development;
- GitHub Actions Secrets for CI/CD;
- managed cloud or enterprise secret stores for non-local deployments.

Do not commit real values into source, docs, scripts, JSON, YAML, SQL, PowerShell, TypeScript, Python, or Markdown.

## Local Development/PAPER credentials

The Windows launcher generates a one-time operator password and signing key for each local process group. These values are injected into child process environments and are not persisted by the launcher.

Restarting the local stack rotates the generated local token material.

Use this pattern for local startup:

```powershell
.\scripts\dev\Start-ThesisPulse.ps1
```

Only use explicit local credentials for controlled local testing, and keep them outside source control:

```powershell
$env:Authentication__Local__Password = "<local-secret-store-value>"
$env:Authentication__Local__SigningKeyBase64 = "<local-secret-store-value>"
```

## Placeholder-only template

The file below is intentionally placeholder-only:

```text
config/thesispulse.local.example.env
```

Copy it outside the repository or into an ignored local path before replacing placeholders.

## Ignored local files

The repository ignores common local secret-bearing files and artifacts, including:

- `.env` and `.env.*` at repository root;
- local appsettings files;
- certificate/key containers;
- database dumps/backups;
- logs/traces/dumps;
- local secret folders.

The committed React development environment remains explicitly allowed because it contains only non-secret local development defaults.

## Scanner

Run the scanner from the repository root:

```powershell
.\scripts\security\Test-ThesisPulseSecretHygiene.ps1
```

The scanner reports only:

- relative file path;
- line number;
- finding type.

It does not print suspected secret values.

## If the scanner fails

1. Remove the secret from the committed file.
2. Move the value into an approved secret store.
3. Rotate the exposed credential if it was real.
4. Re-run the scanner.
5. For Git history exposure, follow the repository security incident process before merging further changes.

## CI

Phase 6.3 CI runs the secret hygiene scanner plus the existing security regressions for authentication, operator audit, security headers/CORS, .NET, Python, and React.

## Safety boundaries

This phase does not add or change:

- execution authority;
- broker authority;
- portfolio mutation authority;
- risk override authority;
- order authority;
- LIVE trading authority;
- authentication or authorization rules.
