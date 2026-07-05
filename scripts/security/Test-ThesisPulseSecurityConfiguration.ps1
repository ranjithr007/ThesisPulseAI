#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$failures = New-Object System.Collections.Generic.List[string]

function Read-RepositoryFile {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("Missing required security file: $RelativePath")
        return ""
    }

    return Get-Content -LiteralPath $path -Raw
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Content.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        $failures.Add($Message)
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Forbidden,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Content.IndexOf($Forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $failures.Add($Message)
    }
}

$codeql = Read-RepositoryFile ".github/workflows/security-codeql.yml"
Assert-Contains $codeql "language: csharp" "CodeQL must analyze C#."
Assert-Contains $codeql "language: javascript-typescript" "CodeQL must analyze JavaScript/TypeScript."
Assert-Contains $codeql "language: python" "CodeQL must analyze Python."
Assert-Contains $codeql "security-events: write" "CodeQL requires security-events write permission."
Assert-Contains $codeql "contents: read" "CodeQL contents permission must remain read-only."
Assert-NotContains $codeql "contents: write" "CodeQL must not receive contents write permission."
Assert-NotContains $codeql "pull-requests: write" "CodeQL must not receive pull-request write permission."
Assert-Contains $codeql "github/codeql-action/init@v3" "CodeQL init action must use v3."
Assert-Contains $codeql "github/codeql-action/analyze@v3" "CodeQL analyze action must use v3."

$dependencyReview = Read-RepositoryFile ".github/workflows/security-dependency-review.yml"
Assert-Contains $dependencyReview "actions/dependency-review-action@v5" "Dependency review action must use v5."
Assert-Contains $dependencyReview "fail-on-severity: high" "Dependency review must fail on high severity findings."
Assert-Contains $dependencyReview "AGPL-3.0-only" "Dependency review must deny AGPL-3.0-only dependencies."
Assert-Contains $dependencyReview "AGPL-3.0-or-later" "Dependency review must deny AGPL-3.0-or-later dependencies."
Assert-Contains $dependencyReview "SSPL-1.0" "Dependency review must deny SSPL dependencies."
Assert-Contains $dependencyReview "retry-on-snapshot-warnings: true" "Dependency review must retry bounded snapshot warnings."
Assert-Contains $dependencyReview "continue-on-error: true" "Dependency review diagnostics must preserve action outputs without ending the job early."
Assert-Contains $dependencyReview "actions/github-script@v7" "Dependency review must use the supported GitHub API diagnostic action."
Assert-Contains $dependencyReview "dependency-graph/compare/{basehead}" "Dependency graph diagnostics must query the official compare endpoint."
Assert-Contains $dependencyReview "actions/upload-artifact@v4" "Dependency review must upload sanitized diagnostics."
Assert-Contains $dependencyReview "dependency-review-diagnostics.json" "Dependency review diagnostics must use the bounded policy JSON artifact."
Assert-Contains $dependencyReview "dependency-graph-api.json" "Dependency graph diagnostics must use the bounded API JSON artifact."
Assert-Contains $dependencyReview "if: steps.review.outcome == 'failure'" "Dependency review must explicitly restore fail-closed enforcement."
Assert-Contains $dependencyReview "exit 1" "Dependency review enforcement must return a non-zero exit code."
Assert-Contains $dependencyReview "contents: read" "Dependency review must use read-only contents permission."
Assert-NotContains $dependencyReview "warn-only: true" "Dependency review must never convert findings into warnings only."
Assert-NotContains $dependencyReview "security-events: write" "Dependency review does not need security-events write permission."

$ecosystemAudit = Read-RepositoryFile ".github/workflows/security-ecosystem-audit.yml"
Assert-Contains $ecosystemAudit "dotnet list ThesisPulseAI.sln package --vulnerable --include-transitive" "NuGet audit command is missing."
Assert-Contains $ecosystemAudit "npm audit --audit-level=high" "npm audit must reject high or critical vulnerabilities."
Assert-Contains $ecosystemAudit "pip-audit ./ai-python --strict --progress-spinner off" "Python dependency audit command is missing or not strict."
Assert-NotContains $ecosystemAudit "--fix" "Security audits must never modify dependencies automatically."
Assert-Contains $ecosystemAudit "contents: read" "Ecosystem audit must use read-only contents permission."

$secretScan = Read-RepositoryFile ".github/workflows/security-secrets.yml"
Assert-Contains $secretScan "fetch-depth: 0" "Secret scanning must inspect full Git history."
Assert-Contains $secretScan "gitleaks/gitleaks-action@v2" "Secret scanning must use Gitleaks v2 action."
Assert-Contains $secretScan "GITLEAKS_CONFIG: .gitleaks.toml" "Secret scanning must load the repository configuration."
Assert-Contains $secretScan "contents: read" "Secret scanning must use read-only contents permission."
Assert-NotContains $secretScan "contents: write" "Secret scanning must not receive contents write permission."

$gitleaks = Read-RepositoryFile ".gitleaks.toml"
Assert-Contains $gitleaks "useDefault = true" "Gitleaks default detection rules must remain enabled."
Assert-Contains $gitleaks "Documented non-secret local development placeholders" "Gitleaks allowlist must remain documented."
Assert-NotContains $gitleaks "tests/.*" "Gitleaks must not broadly exclude test directories."
Assert-NotContains $gitleaks "docs/.*" "Gitleaks must not broadly exclude documentation directories."

$dependabot = Read-RepositoryFile ".github/dependabot.yml"
foreach ($ecosystem in @("nuget", "npm", "pip", "github-actions")) {
    Assert-Contains $dependabot "package-ecosystem: $ecosystem" "Dependabot must cover $ecosystem."
}
Assert-NotContains $dependabot "auto-merge" "Dependabot configuration must not enable automatic merging."
Assert-Contains $dependabot "open-pull-requests-limit: 5" "Dependabot pull-request volume must remain bounded."

$pythonProject = Read-RepositoryFile "ai-python/pyproject.toml"
Assert-Contains $pythonProject '"pip-audit==2.10.1"' "The Python audit tool must be pinned in the development dependency set."

$workflowFiles = @(
    ".github/workflows/security-codeql.yml",
    ".github/workflows/security-dependency-review.yml",
    ".github/workflows/security-ecosystem-audit.yml",
    ".github/workflows/security-secrets.yml",
    ".github/workflows/phase59-security-configuration-ci.yml"
)
foreach ($relativePath in $workflowFiles) {
    $content = Read-RepositoryFile $relativePath
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        Assert-NotContains $content "permissions: write-all" "$relativePath must not use write-all permissions."
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Phase 5.9 security configuration validation failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }
    exit 1
}

Write-Host "Phase 5.9 security configuration validation passed."
Write-Host "Validated CodeQL language coverage, least-privilege permissions, dependency audits, secret scanning, and Dependabot ecosystems."
exit 0
