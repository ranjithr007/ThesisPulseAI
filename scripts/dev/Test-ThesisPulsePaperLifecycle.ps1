#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Guid]$CorrelationUid,

    [string]$ExecutionApiBaseUrl = "http://localhost:59482",

    [string]$PortfolioCode = "PAPER-DEFAULT",

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$baseUrl = $ExecutionApiBaseUrl.TrimEnd("/")
$encodedPortfolioCode = [Uri]::EscapeDataString($PortfolioCode)
$url = "$baseUrl/api/v1/execution/lifecycles/$CorrelationUid/acceptance?portfolioCode=$encodedPortfolioCode"

try {
    $report = Invoke-RestMethod -Method Get -Uri $url -Headers @{ Accept = "application/json" } -TimeoutSec 30
}
catch {
    [Console]::Error.WriteLine("Unable to read PAPER lifecycle acceptance from '$url': $($_.Exception.Message)")
    exit 4
}

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $resolvedOutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resolvedOutputPath -Encoding UTF8
    Write-Host "Acceptance report written to $resolvedOutputPath"
}

Write-Host "ThesisPulse AI PAPER lifecycle acceptance"
Write-Host "Correlation UID: $($report.correlationUid)"
Write-Host "Portfolio: $($report.portfolioCode)"
Write-Host "Outcome: $($report.outcome)"
Write-Host "Evaluated: $($report.evaluatedAtUtc)"
Write-Host ""

foreach ($check in @($report.checks)) {
    Write-Host ("[{0}] {1} - {2}" -f $check.outcome, $check.code, $check.message)
}

switch ($report.outcome) {
    "PASS" { exit 0 }
    "INCOMPLETE" {
        [Console]::Error.WriteLine("Lifecycle acceptance is incomplete. Authoritative evidence is missing or unfinished.")
        exit 2
    }
    "FAIL" {
        [Console]::Error.WriteLine("Lifecycle acceptance failed one or more safety checks.")
        exit 3
    }
    default {
        [Console]::Error.WriteLine("Lifecycle acceptance returned an unsupported outcome '$($report.outcome)'.")
        exit 5
    }
}
