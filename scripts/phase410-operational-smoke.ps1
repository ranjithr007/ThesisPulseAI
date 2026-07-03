param(
    [Parameter(Mandatory = $false)]
    [string]$SignalServiceBaseUrl = "http://localhost:5200",

    [Parameter(Mandatory = $false)]
    [string]$PythonServiceBaseUrl = "http://localhost:8000"
)

$ErrorActionPreference = "Stop"

function Invoke-Check {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "[CHECK] $Name"
    & $Action
    Write-Host "[PASS]  $Name"
}

Invoke-Check "Signal service readiness" {
    Invoke-RestMethod -Method Get -Uri "$SignalServiceBaseUrl/health/ready" | Out-Null
}

Invoke-Check "Python service reachability" {
    Invoke-RestMethod -Method Get -Uri "$PythonServiceBaseUrl/health" | Out-Null
}

Invoke-Check "Controlled activation gate" {
    $report = Invoke-RestMethod -Method Get -Uri "$SignalServiceBaseUrl/api/v1/internal/option-chain/production-readiness"
    if (-not $report.ready) { throw "Production readiness is blocked: $($report.blockingReasons -join ', ')" }
    if ($report.selectionAuthority -or $report.executionAuthority) { throw "Authority boundary violation detected." }
}

Invoke-Check "Operational smoke endpoint" {
    $result = Invoke-RestMethod -Method Post -Uri "$SignalServiceBaseUrl/api/v1/internal/option-chain/operational-smoke"
    if ($result.outcome -ne "READY") { throw "Unexpected smoke outcome: $($result.outcome)" }
}

Write-Host "Phase 4.10 smoke verification completed without granting trading or execution authority."
