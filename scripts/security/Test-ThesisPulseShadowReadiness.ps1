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
        $failures.Add("Missing required Phase 7.0 file: $RelativePath")
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

$solution = Read-RepositoryFile "ThesisPulseAI.sln"
Assert-Contains $solution "ThesisPulse.Infrastructure.Brokers.Upstox" `
    "Solution must keep the Upstox adapter as an explicit boundary project."

$executionProgram = Read-RepositoryFile "src/ThesisPulse.Execution.Service/Program.cs"
Assert-Contains $executionProgram "Platform:LiveExecutionEnabled" `
    "Execution Service must keep the live-execution guard."
Assert-Contains $executionProgram "Phase 1 Execution Service must run in PAPER mode with live execution disabled." `
    "Execution Service must fail startup outside PAPER with live execution disabled."
Assert-Contains $executionProgram "ShadowReadinessOptions" `
    "Execution Service must register Phase 7.0 readiness options."
Assert-Contains $executionProgram "MapShadowReadinessEndpoints(liveExecutionEnabled)" `
    "Execution Service must map the SHADOW readiness status endpoint."
Assert-Contains $executionProgram "shadowReadinessStatusAvailable = true" `
    "Execution Service status must advertise read-only SHADOW readiness availability."
Assert-Contains $executionProgram "shadowReadinessAuthority = false" `
    "Execution Service status must keep SHADOW authority disabled."
Assert-Contains $executionProgram "brokerSubmissionAuthority = false" `
    "Execution Service must keep broker submission authority disabled."
Assert-Contains $executionProgram "liveExecutionAuthority = false" `
    "Execution Service must keep live execution authority disabled."

$options = Read-RepositoryFile "src/ThesisPulse.Execution.Service/ShadowReadinessOptions.cs"
foreach ($required in @(
    "SectionName = \"ShadowReadiness\"",
    "public bool Enabled",
    "public string Mode",
    "public string Environment",
    "public string BrokerAdapter",
    "public string ReadinessVersion",
    "AllowBrokerOrderSubmission",
    "AllowBrokerOrderModification",
    "AllowBrokerOrderCancellation",
    "AllowPortfolioMutation",
    "InstrumentUniverse",
    "NIFTY 50",
    "BANK NIFTY",
    "FINNIFTY",
    "Validate()")) {
    Assert-Contains $options $required "SHADOW readiness options must include $required."
}
Assert-Contains $options "Phase 7.0 SHADOW readiness is non-executing" `
    "SHADOW readiness options must reject execution authority."

$contracts = Read-RepositoryFile "src/ThesisPulse.Execution.Service/ShadowReadinessContracts.cs"
foreach ($required in @(
    "shadow-readiness.v1",
    "StatusReady",
    "StatusDisabled",
    "StatusNotReady",
    "CheckPass",
    "CheckFail",
    "CheckNotEvaluated",
    "ShadowReadinessStatusV1",
    "ShadowReadinessAuthorityV1")) {
    Assert-Contains $contracts $required "SHADOW readiness contracts must include $required."
}

$endpoints = Read-RepositoryFile "src/ThesisPulse.Execution.Service/ShadowReadinessEndpoints.cs"
Assert-Contains $endpoints "/api/v1/shadow/readiness/status" `
    "SHADOW readiness status endpoint is missing."
Assert-Contains $endpoints "Results.Ok(BuildStatus(options, liveExecutionEnabled))" `
    "SHADOW readiness endpoint must return a read-only status projection."
Assert-Contains $endpoints "BrokerOrderSubmission: false" `
    "SHADOW readiness authority must disable broker submission."
Assert-Contains $endpoints "BrokerOrderModification: false" `
    "SHADOW readiness authority must disable broker modification."
Assert-Contains $endpoints "BrokerOrderCancellation: false" `
    "SHADOW readiness authority must disable broker cancellation."
Assert-Contains $endpoints "PortfolioMutation: false" `
    "SHADOW readiness authority must disable portfolio mutation."
Assert-Contains $endpoints "RiskOverride: false" `
    "SHADOW readiness authority must disable risk override."
Assert-Contains $endpoints "LiveExecution: false" `
    "SHADOW readiness authority must disable live execution."
Assert-Contains $endpoints "CheckNotEvaluated" `
    "Phase 7.0 status must show unmapped broker checks as not evaluated."
Assert-NotContains $endpoints "MapPost" `
    "SHADOW readiness slice must not add mutation endpoints."

$combinedPhaseFiles = $options + $contracts + $endpoints
foreach ($forbidden in @(
    "PlaceOrder",
    "ModifyOrder",
    "CancelOrder",
    "SubmitOrder",
    "PortfolioMutation: true",
    "RiskOverride: true",
    "LiveExecution: true")) {
    Assert-NotContains $combinedPhaseFiles $forbidden `
        "Phase 7.0 contracts and status must not contain $forbidden."
}

$workflow = Read-RepositoryFile ".github/workflows/phase70-shadow-readiness-ci.yml"
foreach ($requiredStep in @(
    "Test-ThesisPulseShadowReadiness.ps1",
    "Test-ThesisPulseSecurityObservability.ps1",
    "Test-ThesisPulseSecurityConfiguration.ps1",
    "dotnet restore ThesisPulseAI.sln",
    "dotnet build ThesisPulseAI.sln")) {
    Assert-Contains $workflow $requiredStep "Phase 7.0 workflow must run $requiredStep."
}
Assert-Contains $workflow "contents: read" "Phase 7.0 workflow must use read-only contents permission."
Assert-NotContains $workflow "permissions: write-all" "Phase 7.0 workflow must not use write-all permissions."
Assert-NotContains $workflow "warn-only" "Phase 7.0 workflow must not convert checks into warnings."

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
    [Console]::Error.WriteLine("Phase 7.0 SHADOW readiness validation failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }
    exit 1
}

Write-Host "Phase 7.0 SHADOW readiness validation passed."
Write-Host "Validated read-only contracts, status endpoint, broker boundary, disabled authority flags, and CI coverage."
exit 0
