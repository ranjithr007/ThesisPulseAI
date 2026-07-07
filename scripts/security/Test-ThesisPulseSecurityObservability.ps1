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
        $failures.Add("Missing required Phase 6.5 file: $RelativePath")
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

$programFiles = @(
    "src/ThesisPulse.Trading.Api/Program.cs",
    "src/ThesisPulse.MarketData.Service/Program.cs",
    "src/ThesisPulse.Signal.Service/Program.cs",
    "src/ThesisPulse.Thesis.Service/Program.cs",
    "src/ThesisPulse.Risk.Service/Program.cs",
    "src/ThesisPulse.Execution.Service/Program.cs",
    "src/ThesisPulse.Portfolio.Service/Program.cs",
    "src/ThesisPulse.Operations.Service/Program.cs"
)

foreach ($programFile in $programFiles) {
    $program = Read-RepositoryFile $programFile
    Assert-Contains $program "AddThesisPulsePlatformFoundation()" `
        "$programFile must register the shared platform security foundation."
    Assert-Contains $program "UseThesisPulsePlatformFoundation()" `
        "$programFile must activate shared security middleware."
    Assert-Contains $program "MapThesisPulsePlatformEndpoints" `
        "$programFile must map bounded platform observability endpoints."
}

$foundation = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Hosting/PlatformFoundationExtensions.cs"
foreach ($required in @(
    "AddThesisPulseSecurityHeaders()",
    "AddThesisPulseOperatorAccessAudit()",
    "AddThesisPulseOperatorAuthentication()",
    "UseThesisPulseSecurityHeaders()",
    "UseThesisPulseOperatorAuthentication()",
    "MapThesisPulseOperatorAccessAudit()")) {
    Assert-Contains $foundation $required "Platform foundation must include $required."
}
Assert-Contains $foundation ".AllowAnonymous();" "Health and info endpoints must remain explicitly anonymous."

$auth = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Authentication/OperatorAuthenticationExtensions.cs"
Assert-Contains $auth "jwt.SaveToken = false" "JWT bearer configuration must not save raw session material."
Assert-Contains $auth "jwt.IncludeErrorDetails = false" "Authentication errors must not reveal detailed validation data."
Assert-Contains $auth "ValidateLifetime = true" "Operator session validation must enforce lifetime."
Assert-Contains $auth "RequireExpirationTime = true" "Operator sessions must require expiration."
Assert-Contains $auth "RequireSignedTokens = true" "Operator sessions must require signed material."
Assert-Contains $auth "app.UseAuthentication();" "Authentication must run before audit capture."
Assert-Contains $auth "app.UseThesisPulseOperatorAccessAudit();" "Audit capture must remain in the protected pipeline."
Assert-Contains $auth "app.UseAuthorization();" "Authorization must run after audit capture."

$auditEntry = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/OperatorAccessAuditEntry.cs"
foreach ($field in @(
    "AuditUid",
    "ObservedAtUtc",
    "ServiceName",
    "Method",
    "Path",
    "StatusCode",
    "CorrelationId",
    "IsAuthenticated",
    "OperatorSubject",
    "OperatorName",
    "Permissions",
    "RequestClass",
    "AuthorizationOutcome")) {
    Assert-Contains $auditEntry $field "Audit entry must keep bounded metadata field $field."
}
foreach ($forbidden in @("Request.Body", "Cookie", "QueryString", "Headers.Authorization", "CredentialValue", "RawSession")) {
    Assert-NotContains $auditEntry $forbidden "Audit entry must not expose $forbidden."
}

$auditExtensions = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/OperatorAccessAuditExtensions.cs"
Assert-Contains $auditExtensions "/api/v1/security/operator-audit/recent" "Operator audit observability endpoint must exist."
Assert-Contains $auditExtensions "RequireAuthorization(OperatorAuthenticationConstants.AdminPolicy)" `
    "Operator audit observability endpoint must require admin authorization."
Assert-Contains $auditExtensions "GetRecent(limit.GetValueOrDefault(50))" `
    "Operator audit observability endpoint must use bounded default reads."

$auditOptions = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/OperatorAccessAuditOptions.cs"
Assert-Contains $auditOptions "Capacity" "Audit options must keep bounded capacity."
Assert-Contains $auditOptions "MaximumReadLimit" "Audit options must keep bounded read limits."
Assert-Contains $auditOptions "Validate()" "Audit options must validate limits."

$auditStore = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/InMemoryOperatorAccessAuditStore.cs"
Assert-Contains $auditStore "while (_entries.Count > _options.Capacity)" "Audit store must evict beyond capacity."
Assert-Contains $auditStore "Math.Clamp" "Audit store reads must clamp requested limits."

$launcher = Read-RepositoryFile "scripts/dev/Start-ThesisPulse.ps1"
Assert-Contains $launcher 'Authentication__Local__Permissions__0 = "thesispulse.read"' `
    "Local default operator must remain read-only."
Assert-NotContains $launcher 'Authentication__Local__Permissions__1 = "thesispulse.admin"' `
    "Local default operator must not receive admin access."

$sessionRunbook = Read-RepositoryFile "docs/security/operator-session-hardening.md"
foreach ($requiredText in @(
    "Security observability must never emit sensitive values.",
    "Operator sessions should use short lifetimes and explicit renewal.",
    "Rotate operator session material when:",
    "The existing operator audit endpoint is admin-only and returns a bounded report.",
    "Run from the repository root:")) {
    Assert-Contains $sessionRunbook $requiredText "Operator session runbook must include: $requiredText"
}

$workflow = Read-RepositoryFile ".github/workflows/phase65-security-observability-ci.yml"
foreach ($requiredStep in @(
    "Test-ThesisPulseSecurityObservability.ps1",
    "Test-ThesisPulseAuthenticationConfiguration.ps1",
    "Test-ThesisPulseOperatorAccessAudit.ps1",
    "Test-ThesisPulseSecurityHeadersCors.ps1",
    "Test-ThesisPulseSecretHygiene.ps1",
    "Test-ThesisPulseSecurityConfiguration.ps1")) {
    Assert-Contains $workflow $requiredStep "Phase 6.5 workflow must run $requiredStep."
}
Assert-Contains $workflow "contents: read" "Phase 6.5 workflow must use read-only contents permission."
Assert-NotContains $workflow "permissions: write-all" "Phase 6.5 workflow must not use write-all permissions."
Assert-NotContains $workflow "warn-only" "Phase 6.5 workflow must not convert security checks to warnings."

$powerShellFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -Filter "*.ps1" -File
foreach ($file in $powerShellFiles) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName,
        [ref]$tokens,
        [ref]$errors) | Out-Null
    foreach ($parseError in @($errors)) {
        $relativePath = $file.FullName.Substring($repositoryRoot.Length + 1)
        $failures.Add("PowerShell parse error in ${relativePath}: $($parseError.Message)")
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Phase 6.5 security observability validation failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }
    exit 1
}

Write-Host "Phase 6.5 security observability validation passed."
Write-Host "Validated shared security registration, redaction boundaries, bounded operator audit observability, local default access, runbook coverage, and CI regression coverage."
exit 0
