# Repository security and supply-chain controls

Phase 5.9 establishes repository-level security gates for ThesisPulse AI before runtime authentication, shadow trading, or restricted-live work begins.

## Security workflows

### CodeQL

`.github/workflows/security-codeql.yml` analyzes:

- C# and ASP.NET Core;
- JavaScript and TypeScript;
- Python.

It runs for pull requests to `main`, pushes to `main`, manual dispatch, and a weekly schedule. The workflow has read-only repository permissions plus the required `security-events: write` permission.

### Dependency review

`.github/workflows/security-dependency-review.yml` evaluates dependency changes in pull requests. It fails for high or critical known vulnerabilities and rejects AGPL or SSPL package licenses.

The workflow does not approve, merge, or update dependencies automatically.

### Ecosystem audits

`.github/workflows/security-ecosystem-audit.yml` runs independent audits for:

- direct and transitive NuGet packages in `ThesisPulseAI.sln`;
- the exact npm dependency graph in `frontend-react/package-lock.json`;
- Python dependencies resolved from `ai-python/pyproject.toml`.

NuGet findings fail for any reported vulnerable package. npm fails at high severity or above. Python uses the pinned `pip-audit` development dependency in strict mode.

### Secret scanning

`.github/workflows/security-secrets.yml` checks full Git history with Gitleaks. The checkout uses `fetch-depth: 0` so deleted or older commits remain in scope.

`.gitleaks.toml` extends the default Gitleaks rules. Its allowlist contains only exact, documented local-development placeholders. Entire folders such as `tests` or `docs` must never be excluded.

### Dependabot

`.github/dependabot.yml` monitors:

- NuGet from the repository root;
- npm under `frontend-react`;
- pip under `ai-python`;
- GitHub Actions under `.github/workflows`.

Minor and patch updates are grouped, open pull requests are bounded, and automatic merge is intentionally disabled.

## Local validation

From the repository root:

```powershell
.\scripts\security\Test-ThesisPulseSecurityConfiguration.ps1
```

The command validates security workflow presence, CodeQL language coverage, least-privilege permissions, audit thresholds, Gitleaks configuration, Dependabot ecosystems, and the pinned Python audit tool.

Manual ecosystem checks can also be run independently:

```powershell
dotnet restore .\ThesisPulseAI.sln
dotnet list .\ThesisPulseAI.sln package --vulnerable --include-transitive

Push-Location .\frontend-react
npm ci
npm audit --audit-level=high
Pop-Location

python -m pip install -e ".\ai-python[dev]"
pip-audit .\ai-python --strict --progress-spinner off
```

These commands inspect dependencies only. They do not update or fix packages automatically.

## Handling a dependency finding

1. Confirm the affected direct or transitive dependency and advisory identifier.
2. Determine whether the vulnerable code path is reachable in ThesisPulse AI.
3. Prefer an upstream fixed version that preserves contract and runtime compatibility.
4. Update the authoritative manifest or lockfile in a dedicated pull request.
5. Run the full affected ecosystem tests and security workflows.
6. Document temporary risk acceptance only when no fixed version exists and the exposure is demonstrably unreachable.

Vulnerability suppressions must identify the advisory, owner, reason, compensating control, and expiration date. Permanent blanket suppressions are not allowed.

## Handling a secret finding

Treat every credible finding as compromised, even when the repository is private or the commit was deleted.

1. Revoke or rotate the credential immediately at its issuing system.
2. Identify services, accounts, repositories, logs, caches, and environments that received the credential.
3. Review access and audit logs for unauthorized use.
4. Replace the secret through the approved environment or secret provider.
5. Remove the value from the active branch and, when required, rewrite Git history only after rotation.
6. Open an incident record containing the credential type and affected systems, but never the secret value.
7. Re-run full-history scanning before closing the incident.

Adding a leaked credential to `.gitleaks.toml` is prohibited. Allowlists are only for verified non-secret fixtures or placeholders.

## Handling a CodeQL finding

1. Review the complete data flow and affected endpoint or worker.
2. Classify whether the finding crosses an execution, risk, portfolio, broker, or operational-control boundary.
3. Fix the root cause rather than silencing the query.
4. Add a focused regression test where practical.
5. Dismiss a result only with a documented false-positive reason and reviewer approval.

## Branch protection

After the workflows are merged and have completed successfully, configure `main` branch protection to require the relevant security and build checks. Branch protection is a repository setting and is not inferred from workflow files alone.

Required checks should include the Phase 5.9 configuration gate, dependency review, ecosystem audits, secret scanning, CodeQL, and the existing platform CI checks.
