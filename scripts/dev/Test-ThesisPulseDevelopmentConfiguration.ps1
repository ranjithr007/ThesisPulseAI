#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ThesisPulse.Development.ps1")

$repositoryRoot = Get-ThesisPulseRepositoryRoot
$failures = New-Object System.Collections.Generic.List[string]
$services = @(Get-ThesisPulseDevelopmentServices)
$frontend = Get-ThesisPulseFrontendDefinition

function Assert-Configuration {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        $failures.Add($Message)
    }
}

Assert-Configuration -Condition ($services.Count -eq 8) `
    -Message "The development manifest must contain exactly eight backend services."
Assert-Configuration -Condition (@($services | Group-Object Key | Where-Object Count -gt 1).Count -eq 0) `
    -Message "Backend service keys must be unique."
Assert-Configuration -Condition (@($services | Group-Object Port | Where-Object Count -gt 1).Count -eq 0) `
    -Message "Backend service ports must be unique."
Assert-Configuration -Condition (-not ($services.Port -contains $frontend.Port)) `
    -Message "The frontend port must not collide with a backend service port."

foreach ($service in $services) {
    Assert-Configuration -Condition (Test-Path -LiteralPath (Join-Path $repositoryRoot $service.Project) -PathType Leaf) `
        -Message "Missing project file: $($service.Project)"
    Assert-Configuration -Condition ($service.Port -ge 1024 -and $service.Port -le 65535) `
        -Message "Invalid port for $($service.Name): $($service.Port)"
    Assert-Configuration -Condition ($service.HealthPath -eq "/health/ready") `
        -Message "$($service.Name) must use the standard /health/ready endpoint."
}

$environmentPath = Join-Path $repositoryRoot "frontend-react/.env.development"
Assert-Configuration -Condition (Test-Path -LiteralPath $environmentPath -PathType Leaf) `
    -Message "frontend-react/.env.development is missing."

$environmentValues = @{}
if (Test-Path -LiteralPath $environmentPath -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $environmentPath) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith("#")) { continue }
        $separator = $trimmed.IndexOf("=")
        if ($separator -le 0) {
            $failures.Add("Invalid frontend environment line: $trimmed")
            continue
        }
        $environmentValues[$trimmed.Substring(0, $separator)] = $trimmed.Substring($separator + 1)
    }
}

foreach ($service in $services | Where-Object { $null -ne $_.FrontendVariable }) {
    $expected = Get-ThesisPulseServiceUrl -Service $service
    Assert-Configuration -Condition ($environmentValues.ContainsKey($service.FrontendVariable)) `
        -Message "Missing frontend variable $($service.FrontendVariable)."
    if ($environmentValues.ContainsKey($service.FrontendVariable)) {
        Assert-Configuration -Condition ($environmentValues[$service.FrontendVariable] -eq $expected) `
            -Message "$($service.FrontendVariable) must equal $expected."
    }
}

$requiredFrontendValues = @{
    VITE_PORTFOLIO_CODE = "PAPER-DEFAULT"
    VITE_PORTFOLIO_CURRENCY = "INR"
    VITE_EXECUTION_LIFECYCLE_LIMIT = "50"
    VITE_PNL_MAXIMUM_AGE_MINUTES = "10"
}
foreach ($key in $requiredFrontendValues.Keys) {
    Assert-Configuration -Condition ($environmentValues.ContainsKey($key)) `
        -Message "Missing frontend variable $key."
    if ($environmentValues.ContainsKey($key)) {
        Assert-Configuration -Condition ($environmentValues[$key] -eq $requiredFrontendValues[$key]) `
            -Message "$key must equal $($requiredFrontendValues[$key])."
    }
}

$scriptPaths = @(
    "scripts/dev/ThesisPulse.Development.ps1",
    "scripts/dev/Test-ThesisPulsePrerequisites.ps1",
    "scripts/dev/Start-ThesisPulse.ps1",
    "scripts/dev/Stop-ThesisPulse.ps1",
    "scripts/dev/Test-ThesisPulseDevelopmentConfiguration.ps1"
)
foreach ($relativePath in $scriptPaths) {
    $path = Join-Path $repositoryRoot $relativePath
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors) | Out-Null
    foreach ($parseError in @($parseErrors)) {
        $failures.Add("PowerShell parse error in ${relativePath}: $($parseError.Message)")
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Phase 5.6 development configuration validation failed:")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }
    exit 1
}

Write-Host "Phase 5.6 development configuration validation passed."
Write-Host "Validated $($services.Count) backend services, one frontend, unique ports, typed endpoints, and PowerShell syntax."
exit 0
