# Repository security and supply-chain controls

Phase 5.9 establishes repository-level security gates for ThesisPulse AI before runtime authentication, shadow trading, or restricted-live work begins. Phase 6.4 hardens the dependency and supply-chain operator path so dependency review failures are actionable without weakening fail-closed behavior.

## Required repository setting

GitHub dependency review requires the repository Dependency graph to be enabled. Repository owners must configure it before the dependency-review gate can pass:

1. Open the ThesisPulseAI repository on GitHub.
2. Open **Settings**.
3. Select **Security** or **Code security and analysis**.
4. Locate **Dependency graph**.
5. Choose **Enable**.
6. Re-run the failed **Security Dependency Review** workflow.

The dependency-review workflow performs an explicit preflight against GitHub's official dependency-diff endpoint. HTTP 403 or any other unavailable response fails closed with an actionable message. It does not treat an unavailable graph as a clean dependency review.

## Security workflows

### CodeQL

`.github/workflows/security-codeql.yml` analyzes:

- C# and ASP.NET Core;
- JavaScript and TypeScript;
- Python.

It runs for pull requests to `main`, pushes to `main`, manual dispatch, and a weekly schedule. The workflow has read-only repository permissions plus the required `security-events: write` permission.

### Dependency review

`.github/workflows/security-dependency-review.yml` evaluates dependency changes in pull requests. It fails for high or critical known vulnerabilities and rejects AGPL or SSPL package licenses.

The workflow first confirms that GitHub's dependency graph comparison endpoint is available. It then runs the dependency policy review and uploads bounded diagnostics containing only HTTP status, API message, dependency counts, package identifiers, advisory identifiers, and license identifiers. Tokens and dependency contents are not written to artifacts.

The workflow prints the repository setting path, this runbook anchor, and the Phase 6.4 tracking issue when the dependency graph is unavailable. The failure is intentional and must be fixed through repository settings, not workflow bypass.

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

Dependabot pull requests must still pass the relevant build and security gates. A version bump is not safe merely because it is automated.

## Local validation

From the repository root:

```powershell
.\scripts\security\Test-ThesisPulseSecurityConfiguration.ps1
```

The command validates security workflow presence, CodeQL language coverage, least-privilege permissions, dependency-graph preflight behavior, audit thresholds, Gitleaks configuration, Dependabot ecosystems, bounded dependency-review diagnostics, and the pinned Python audit tool.

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

## Dependabot package-bump review path

Use this path for NuGet, npm, Python and GitHub Actions dependency PRs:

1. Confirm the PR changes only the intended manifest or lockfile plus expected generated metadata.
2. Read release notes for direct dependencies and check for breaking changes, especially auth, serialization, networking, broker, database and build-tool packages.
3. Confirm **Security Dependency Review** completed successfully after Dependency graph preflight.
4. Confirm ecosystem audits completed for the affected package ecosystem.
5. Confirm relevant application build and test workflows stayed green.
6. Prefer a dedicated follow-up PR for code changes required by a dependency bump.
7. Do not enable auto-merge for dependency updates that touch execution, broker, portfolio, risk, authentication or configuration boundaries.

## Handling a dependency finding

1. Confirm the affected direct or transitive dependency and advisory identifier.
2. Determine whether the vulnerable code path is reachable in ThesisPulse AI.
3. Prefer an upstream fixed version that preserves contract and runtime compatibility.
4. Update the authoritative manifest or lockfile in a dedicated pull request.
5. Run the full affected ecosystem tests and security workflows.
6. Document temporary risk acceptance only when no fixed version exists and the exposure is demonstrably unreachable.

Vulnerability suppressions must identify the advisory, owner, reason, compensating control, and expiration date. Permanent blanket suppressions are not allowed.

## Handling an unavailable dependency graph

An unavailable dependency graph is a repository configuration failure, not a dependency pass.

- HTTP 403 means the workflow cannot access the dependency comparison required for pull-request review.
- Enable the repository Dependency graph using the owner steps above.
- Re-run the failed dependency-review workflow after the setting is enabled.
- Do not add `warn-only`, skip the gate, or interpret zero action outputs as zero dependency risk.

The independent NuGet, npm, and Python audits remain useful baseline checks, but they do not replace pull-request dependency diff review.

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
