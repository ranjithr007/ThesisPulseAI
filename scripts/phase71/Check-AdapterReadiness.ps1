#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$requiredFiles = @(
    "docs/phase-7/phase71-adapter-readiness.md",
    "src/ThesisPulse.Execution.Service/AdapterReadinessOptions.cs",
    "src/ThesisPulse.Execution.Service/AdapterReadinessContracts.cs",
    "src/ThesisPulse.Execution.Service/AdapterReadinessStatusBuilder.cs",
    ".github/workflows/phase71-adapter-readiness-ci.yml"
)

$missing = @()
foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $missing += $relativePath
    }
}

if ($missing.Count -gt 0) {
    foreach ($relativePath in $missing) {
        [Console]::Error.WriteLine("Missing required file: $relativePath")
    }
    exit 1
}

Write-Host "Phase 7.1 adapter readiness validation passed."
exit 0
