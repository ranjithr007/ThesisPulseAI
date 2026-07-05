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
        $failures.Add("Missing required authentication file: $RelativePath")
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
        "$programFile must register the shared authentication foundation."
    Assert-Contains $program "UseThesisPulsePlatformFoundation()" `
        "$programFile must activate shared authentication and authorization middleware."
    Assert-Contains $program "MapThesisPulsePlatformEndpoints" `
        "$programFile must expose the canonical anonymous health and info endpoints."
}

$foundation = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Hosting/PlatformFoundationExtensions.cs"
Assert-Contains $foundation "AddThesisPulseOperatorAuthentication()" `
    "Platform foundation must register operator authentication."
Assert-Contains $foundation "UseThesisPulseOperatorAuthentication()" `
    "Platform foundation must activate operator authentication."
foreach ($anonymousPath in @("/health/live", "/health/ready", "/health/startup", "/info")) {
    Assert-Contains $foundation $anonymousPath "Platform foundation is missing $anonymousPath."
}
if (([regex]::Matches($foundation, "\.AllowAnonymous\(\)")).Count -lt 4) {
    $failures.Add("All four health/info endpoint mappings must explicitly allow anonymous access.")
}

$extensions = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Authentication/OperatorAuthenticationExtensions.cs"
Assert-Contains $extensions "AddJwtBearer" "Shared authentication must use JWT bearer validation."
Assert-Contains $extensions "FallbackPolicy = BuildFallbackPolicy()" `
    "A fail-closed fallback authorization policy is required."
Assert-Contains $extensions "OperatorAuthorization.CanAccessRequest" `
    "Fallback authorization must be method-aware."
Assert-Contains $extensions "MapPost(" "The local token endpoint mapping is missing."
Assert-Contains $extensions "/api/v1/auth/token" "The local token endpoint is missing."
Assert-Contains $extensions ".AllowAnonymous()" "The local token endpoint must be explicitly anonymous."
Assert-Contains $extensions "/api/v1/auth/session" "The authenticated session endpoint is missing."
Assert-Contains $extensions "IncludeErrorDetails = false" "JWT error details must remain disabled."
Assert-Contains $extensions "StatusCodes.Status401Unauthorized" "Standard 401 handling is missing."
Assert-Contains $extensions "StatusCodes.Status403Forbidden" "Standard 403 handling is missing."
Assert-NotContains $extensions "ValidateLifetime = false" "JWT lifetime validation must never be disabled."
Assert-NotContains $extensions "ValidateIssuerSigningKey = false" `
    "JWT signature validation must never be disabled."

$configuration = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Authentication/OperatorAuthenticationConfiguration.cs"
Assert-Contains $configuration "environment.IsDevelopment()" `
    "Local token issuance must be restricted to Development."
Assert-Contains $configuration '"PAPER"' "Local token issuance must be restricted to PAPER."
Assert-Contains $configuration "signingKey.Length < 32" "Local signing keys must be at least 32 bytes."
Assert-Contains $configuration "Authentication cannot be disabled" `
    "Missing authentication mode must fail closed."
Assert-Contains $configuration "RequireHttpsMetadata" `
    "External issuer HTTPS metadata validation is missing."

$authorization = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Authentication/OperatorAuthorization.cs"
Assert-Contains $authorization "HttpMethods.IsOptions" "Anonymous CORS preflight handling is missing."
Assert-Contains $authorization "HasOperatePermission" "Mutating requests must require operate permission."
Assert-Contains $authorization "HasReadPermission" "Read requests must require read permission."

$internalHandler = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/Authentication/InternalServiceAuthenticationHandler.cs"
Assert-Contains $internalHandler "IsInternalHost" "Service tokens must be restricted by destination host."
Assert-Contains $internalHandler "request.Headers.Authorization is null" `
    "Internal service authentication must not overwrite an existing authorization header."
Assert-NotContains $internalHandler "upstox" "Broker hosts must never be hard-coded as internal token recipients."

$tradingProgram = Read-RepositoryFile "src/ThesisPulse.Trading.Api/Program.cs"
Assert-Contains $tradingProgram "MapThesisPulseAuthenticationEndpoints()" `
    "Trading API must expose the operator sign-in and session gateway."
foreach ($programFile in $programFiles | Where-Object { $_ -ne "src/ThesisPulse.Trading.Api/Program.cs" }) {
    $program = Read-RepositoryFile $programFile
    Assert-NotContains $program "MapThesisPulseAuthenticationEndpoints()" `
        "$programFile must not expose a second local token issuer."
}

$project = Read-RepositoryFile "src/ThesisPulse.Shared.Observability/ThesisPulse.Shared.Observability.csproj"
Assert-Contains $project 'Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.28"' `
    "The .NET 8 JWT bearer package must remain pinned to 8.0.28."

$launcher = Read-RepositoryFile "scripts/dev/Start-ThesisPulse.ps1"
Assert-Contains $launcher "RandomNumberGenerator" "The launcher must generate cryptographic credentials."
Assert-Contains $launcher "Authentication__Mode = \"LocalDevelopment\"" `
    "The launcher must explicitly select local Development authentication."
Assert-Contains $launcher "Authentication__Local__SigningKeyBase64" `
    "The launcher must inject the ephemeral signing key."
Assert-Contains $launcher "Authentication__Local__Permissions__0 = \"thesispulse.read\"" `
    "The local operator must remain read-only by default."
Assert-Contains $launcher "The generated password is not persisted" `
    "The launcher must state that generated passwords are not persisted."
Assert-NotContains $launcher "Password = \"password\"" "The launcher must not contain a default password."

$session = Read-RepositoryFile "frontend-react/src/auth/authSession.ts"
Assert-Contains $session "window.sessionStorage" "Operator tokens must use session storage."
Assert-Contains $session "Authorization" "The authenticated request layer must add bearer tokens."
Assert-Contains $session "response.status === 401" "401 responses must clear the operator session."
Assert-NotContains $session "localStorage" "Operator tokens must never use local storage."

$main = Read-RepositoryFile "frontend-react/src/main.tsx"
Assert-Contains $main "installAuthenticatedFetch()" `
    "The centralized authenticated request layer must be installed before rendering."
Assert-Contains $main "<AuthenticatedApp />" "The React application must use the protected shell."

$authenticatedApp = Read-RepositoryFile "frontend-react/src/auth/AuthenticatedApp.tsx"
Assert-Contains $authenticatedApp "/api/v1/auth/token" "The sign-in form must call the token endpoint."
Assert-Contains $authenticatedApp "/api/v1/auth/session" "Stored sessions must be validated server-side."
Assert-Contains $authenticatedApp "clearOperatorSession()" "The protected shell must support sign-out."

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
    [Console]::Error.WriteLine("Phase 6.0 authentication configuration validation failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }
    exit 1
}

Write-Host "Phase 6.0 authentication configuration validation passed."
Write-Host "Validated all eight APIs, fail-closed JWT behavior, local PAPER restrictions, internal service identity, and React session handling."
exit 0
