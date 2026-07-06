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
        $failures.Add("Missing required operator audit file: $RelativePath")
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
        "$programFile must register the shared platform foundation."
    Assert-Contains $program "UseThesisPulsePlatformFoundation()" `
        "$programFile must activate shared operator audit middleware through platform foundation."
    Assert-Contains $program "MapThesisPulsePlatformEndpoints" `
        "$programFile must map shared platform endpoints including operator audit retrieval."
}

$foundation = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Hosting/PlatformFoundationExtensions.cs"
Assert-Contains $foundation "AddThesisPulseOperatorAccessAudit()" `
    "Platform foundation must register operator access audit storage."
Assert-Contains $foundation "MapThesisPulseOperatorAccessAudit()" `
    "Platform foundation must map the operator access audit endpoint."
Assert-Contains $foundation "UseThesisPulseOperatorAuthentication()" `
    "Platform foundation must keep authentication and audit middleware in the protected pipeline."

$auth = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Authentication/OperatorAuthenticationExtensions.cs"
Assert-Contains $auth "app.UseAuthentication();" "Authentication middleware must run before audit capture."
Assert-Contains $auth "app.UseThesisPulseOperatorAccessAudit();" `
    "Operator access audit middleware must be in the authentication pipeline."
Assert-Contains $auth "app.UseAuthorization();" "Authorization middleware must run after audit capture."
$authenticationIndex = $auth.IndexOf("app.UseAuthentication();", [StringComparison]::Ordinal)
$auditIndex = $auth.IndexOf("app.UseThesisPulseOperatorAccessAudit();", [StringComparison]::Ordinal)
$authorizationIndex = $auth.IndexOf("app.UseAuthorization();", [StringComparison]::Ordinal)
if (-not ($authenticationIndex -ge 0 -and $auditIndex -gt $authenticationIndex -and $authorizationIndex -gt $auditIndex)) {
    $failures.Add("Middleware order must be authentication -> operator access audit -> authorization.")
}

$entry = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/OperatorAccessAuditEntry.cs"
foreach ($requiredField in @(
    "AuditUid",
    "ObservedAtUtc",
    "ServiceName",
    "Method",
    "Path",
    "StatusCode",
    "CorrelationId",
    "OperatorSubject",
    "OperatorName",
    "Permissions",
    "RequestClass",
    "AuthorizationOutcome")) {
    Assert-Contains $entry $requiredField "Audit entry must include $requiredField."
}
Assert-NotContains $entry "Body" "Audit entries must not include request bodies."
Assert-NotContains $entry "Token" "Audit entries must not include bearer tokens."
Assert-NotContains $entry "Password" "Audit entries must not include passwords."
Assert-NotContains $entry "Cookie" "Audit entries must not include cookies."
Assert-NotContains $entry "Query" "Audit entries must not include query strings."

$middleware = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/OperatorAccessAuditMiddleware.cs"
Assert-Contains $middleware "OperatorAccessAuditClassifier.ShouldAudit" `
    "Middleware must use centralized audit inclusion rules."
Assert-Contains $middleware "CorrelationIdMiddleware.HeaderName" `
    "Middleware must preserve correlation ID evidence."
Assert-Contains $middleware "OperatorAuthorization.GetPermissions" `
    "Middleware must capture permission evidence."
Assert-Contains $middleware "OperatorAccessAuditClassifier.Outcome" `
    "Middleware must classify authorization outcomes centrally."
Assert-NotContains $middleware "Request.Body" "Middleware must not read request bodies."
Assert-NotContains $middleware "QueryString" "Middleware must not store query strings."
Assert-NotContains $middleware "Authorization" "Middleware must not read or log Authorization headers."
Assert-NotContains $middleware "Cookie" "Middleware must not read or log cookies."

$classifier = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/OperatorAccessAuditClassifier.cs"
Assert-Contains $classifier "/health" "Classifier must exclude health endpoints."
Assert-Contains $classifier '"/info"' "Classifier must exclude service info."
Assert-Contains $classifier '"/api/v1/auth/token"' "Classifier must exclude token issuance."
Assert-Contains $classifier "HttpMethods.IsOptions" "Classifier must identify preflight requests."
Assert-Contains $classifier "Status401Unauthorized" "Classifier must identify unauthenticated results."
Assert-Contains $classifier "Status403Forbidden" "Classifier must identify forbidden results."

$extensions = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/OperatorAccessAuditExtensions.cs"
Assert-Contains $extensions "/api/v1/security/operator-audit/recent" `
    "Recent operator audit endpoint is missing."
Assert-Contains $extensions "RequireAuthorization(OperatorAuthenticationConstants.AdminPolicy)" `
    "Recent operator audit endpoint must be admin-only."
Assert-Contains $extensions "OperatorAccessAuditReport" "Recent operator audit endpoint must return a contract report."

$options = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/OperatorAccessAuditOptions.cs"
Assert-Contains $options "Capacity" "Audit options must include bounded capacity."
Assert-Contains $options "MaximumReadLimit" "Audit options must include maximum read limits."
Assert-Contains $options "Validate()" "Audit options must validate retention limits."

$store = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Auditing/InMemoryOperatorAccessAuditStore.cs"
Assert-Contains $store "while (_entries.Count > _options.Capacity)" `
    "In-memory audit store must evict entries beyond capacity."
Assert-Contains $store "Math.Clamp" "In-memory audit read limits must be clamped."

$launcher = Read-RepositoryFile "scripts/dev/Start-ThesisPulse.ps1"
Assert-Contains $launcher 'Authentication__Local__Permissions__0 = "thesispulse.read"' `
    "Default local operator must remain read-only."
Assert-NotContains $launcher 'Authentication__Local__Permissions__1 = "thesispulse.admin"' `
    "Default launcher must not grant admin audit access."

$tests = Read-RepositoryFile "tests/phase61-operator-access-audit/dotnet/Program.cs"
foreach ($requiredTest in @(
    "ClassifierExcludesPlatformObservability",
    "ClassifierExcludesLocalTokenIssuance",
    "MiddlewareCapturesAllowedRequestWithoutQueryString",
    "MiddlewareCapturesUnauthenticatedDenial",
    "MiddlewareCapturesForbiddenDenial",
    "MiddlewareCapturesFailureAndRethrows",
    "StoreRetainsBoundedRecentEntries")) {
    Assert-Contains $tests $requiredTest "Executable audit tests must include $requiredTest."
}

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
    [Console]::Error.WriteLine("Phase 6.1 operator access audit validation failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }
    exit 1
}

Write-Host "Phase 6.1 operator access audit validation passed."
Write-Host "Validated shared middleware, redaction boundaries, admin-only retrieval, bounded retention, and test coverage."
exit 0
