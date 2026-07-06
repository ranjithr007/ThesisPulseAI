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
        $failures.Add("Missing required file: $RelativePath")
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
        "$programFile must register shared platform foundation."
    Assert-Contains $program "UseThesisPulsePlatformFoundation()" `
        "$programFile must activate shared security headers through platform foundation."
    Assert-Contains $program "MapThesisPulsePlatformEndpoints" `
        "$programFile must map platform endpoints."
    Assert-NotContains $program "AllowAnyOrigin" `
        "$programFile must not use AllowAnyOrigin."
    Assert-NotContains $program '"*"' `
        "$programFile must not hard-code wildcard origins."
}

$corsProgramFiles = @(
    "src/ThesisPulse.Trading.Api/Program.cs",
    "src/ThesisPulse.Signal.Service/Program.cs",
    "src/ThesisPulse.Execution.Service/Program.cs",
    "src/ThesisPulse.Operations.Service/Program.cs"
)

foreach ($programFile in $corsProgramFiles) {
    $program = Read-RepositoryFile $programFile
    Assert-Contains $program "CorsOriginValidator.ResolveAllowedOrigins(builder.Configuration)" `
        "$programFile must use the shared CORS origin validator."
    Assert-Contains $program "policy.WithOrigins(allowedOrigins)" `
        "$programFile must use explicit allowed origins."
}

$foundation = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Hosting/PlatformFoundationExtensions.cs"
Assert-Contains $foundation "AddThesisPulseSecurityHeaders()" `
    "Platform foundation must register security header options."
Assert-Contains $foundation "UseThesisPulseSecurityHeaders()" `
    "Platform foundation must run security header middleware."
Assert-Contains $foundation "UseMiddleware<CorrelationIdMiddleware>()" `
    "Correlation middleware must remain in the shared pipeline."
$securityIndex = $foundation.IndexOf("UseThesisPulseSecurityHeaders()", [StringComparison]::Ordinal)
$correlationIndex = $foundation.IndexOf("UseMiddleware<CorrelationIdMiddleware>()", [StringComparison]::Ordinal)
if (-not ($securityIndex -ge 0 -and $correlationIndex -gt $securityIndex)) {
    $failures.Add("Security headers must run before downstream platform middleware.")
}

$middleware = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Security/SecurityHeadersMiddleware.cs"
foreach ($header in @(
    "X-Content-Type-Options",
    "Referrer-Policy",
    "X-Frame-Options",
    "Cross-Origin-Opener-Policy",
    "Cross-Origin-Resource-Policy",
    "Permissions-Policy")) {
    Assert-Contains $middleware $header "Security header middleware must emit $header."
}
Assert-Contains $middleware "nosniff" "Security headers must include nosniff."
Assert-Contains $middleware "no-referrer" "Security headers must include no-referrer."
Assert-Contains $middleware "DENY" "Security headers must deny framing."
Assert-Contains $middleware "same-origin" "Security headers must include COOP same-origin."
Assert-Contains $middleware "same-site" "Security headers must include CORP same-site."
Assert-Contains $middleware "requestIsHttps" "HSTS emission must depend on HTTPS requests."
Assert-Contains $middleware "EnableStrictTransportSecurity" "HSTS must be opt-in."

$options = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Security/SecurityHeadersOptions.cs"
Assert-Contains $options "EnableStrictTransportSecurity" "Security header options must include HSTS flag."
Assert-Contains $options "environment.IsDevelopment()" "HSTS must reject Development."
Assert-Contains $options '"PAPER"' "HSTS must reject PAPER local configuration."
Assert-Contains $options "StrictTransportSecurityMaxAgeSeconds" "HSTS max age must be configurable and validated."

$cors = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Security/CorsOriginValidator.cs"
Assert-Contains $cors "DefaultLocalFrontendOrigin" "CORS validator must preserve local frontend default."
Assert-Contains $cors "Contains('*'" "CORS validator must reject wildcard origins."
Assert-Contains $cors "UriKind.Absolute" "CORS validator must require absolute origins."
Assert-Contains $cors "UserInfo" "CORS validator must reject user info."
Assert-Contains $cors "AbsolutePath" "CORS validator must reject paths."
Assert-Contains $cors "Query" "CORS validator must reject query strings."
Assert-Contains $cors "Fragment" "CORS validator must reject fragments."
Assert-Contains $cors "HashSet<string>(StringComparer.OrdinalIgnoreCase)" `
    "CORS validator must reject duplicate origins case-insensitively."

$tests = Read-RepositoryFile "tests/phase62-security-headers-cors/dotnet/Program.cs"
foreach ($requiredTest in @(
    "DefaultSecurityHeadersAreEmitted",
    "HstsIsDisabledByDefaultForLocalHttp",
    "HstsRequiresExplicitNonLocalEnvironment",
    "HstsEmitsOnlyOnHttpsWhenEnabled",
    "CorsDefaultsToLocalFrontendOrigin",
    "CorsRejectsWildcardOrigins",
    "CorsRejectsDuplicateOrigins",
    "CorsRejectsPathQueryFragmentAndUserInfo")) {
    Assert-Contains $tests $requiredTest "Executable security/CORS tests must include $requiredTest."
}

$allTextFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch "\\.git\\" -and
        $_.Extension -in @(".cs", ".ps1", ".yml", ".yaml", ".json", ".md", ".ts", ".tsx")
    }
foreach ($file in $allTextFiles) {
    $content = Get-Content -LiteralPath $file.FullName -Raw
    $relativePath = $file.FullName.Substring($repositoryRoot.Length + 1)
    if ($content.IndexOf("AllowAnyOrigin", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $failures.Add("AllowAnyOrigin is forbidden: $relativePath")
    }
    if ($relativePath -notlike "docs/*" -and
        $relativePath -notlike "scripts/security/Test-ThesisPulseSecurityHeadersCors.ps1" -and
        $content.IndexOf('Cors:AllowedOrigins:0 = "*"', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $failures.Add("Wildcard CORS configuration is forbidden: $relativePath")
    }
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
    [Console]::Error.WriteLine("Phase 6.2 security headers/CORS validation failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }
    exit 1
}

Write-Host "Phase 6.2 security headers/CORS validation passed."
Write-Host "Validated shared headers, opt-in HSTS safety, explicit CORS origins, and tests."
exit 0
