#requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$ValidationOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ThesisPulse.Development.ps1")

$repositoryRoot = Get-ThesisPulseRepositoryRoot
$failures = New-Object System.Collections.Generic.List[string]
$checks = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$Details,
        [string]$Remediation = ""
    )

    $checks.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Details = $Details
        Remediation = $Remediation
    })
    if (-not $Passed) {
        $failures.Add("${Name}: $Details")
    }
}

function Get-CommandVersionText {
    param([Parameter(Mandatory = $true)][string]$CommandName)
    try {
        return (& $CommandName --version 2>$null | Select-Object -First 1).ToString().Trim()
    }
    catch {
        return ""
    }
}

$git = Get-Command git -ErrorAction SilentlyContinue
Add-Check -Name "Git" -Passed ($null -ne $git) `
    -Details $(if ($git) { Get-CommandVersionText -CommandName $git.Source } else { "git is not available on PATH." }) `
    -Remediation "Install Git for Windows, enable the command-line PATH option, then reopen PowerShell."

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetVersion = if ($dotnet) { Get-CommandVersionText -CommandName $dotnet.Source } else { "" }
$dotnetMajor = 0
if ($dotnetVersion -match "^(\d+)") { $dotnetMajor = [int]$Matches[1] }
Add-Check -Name ".NET SDK" -Passed ($dotnet -and $dotnetMajor -ge 8) `
    -Details $(if ($dotnet) { ".NET SDK $dotnetVersion" } else { "dotnet is not available on PATH." }) `
    -Remediation "Install the .NET 8 SDK or later and reopen PowerShell."

$node = Get-Command node -ErrorAction SilentlyContinue
$nodeVersion = if ($node) { Get-CommandVersionText -CommandName $node.Source } else { "" }
$nodeMajor = 0
if ($nodeVersion -match "^v?(\d+)") { $nodeMajor = [int]$Matches[1] }
Add-Check -Name "Node.js" -Passed ($node -and $nodeMajor -ge 22) `
    -Details $(if ($node) { "Node.js $nodeVersion" } else { "node is not available on PATH." }) `
    -Remediation "Install Node.js 22 LTS or later and reopen PowerShell."

$npm = Get-Command npm -ErrorAction SilentlyContinue
Add-Check -Name "npm" -Passed ($null -ne $npm) `
    -Details $(if ($npm) { Get-CommandVersionText -CommandName $npm.Source } else { "npm is not available on PATH." }) `
    -Remediation "Repair the Node.js installation so npm is available on PATH."

$requiredFiles = @(
    "ThesisPulseAI.sln",
    "frontend-react/package.json",
    "frontend-react/package-lock.json",
    "frontend-react/.env.development",
    "src/ThesisPulse.DatabaseMigrator/ThesisPulse.DatabaseMigrator.csproj"
)
foreach ($relativePath in $requiredFiles) {
    $fullPath = Join-Path $repositoryRoot $relativePath
    Add-Check -Name "File $relativePath" -Passed (Test-Path -LiteralPath $fullPath -PathType Leaf) `
        -Details $(if (Test-Path -LiteralPath $fullPath -PathType Leaf) { "Present" } else { "Required file is missing." }) `
        -Remediation "Run git status and git pull origin main from the repository root."
}

$services = Get-ThesisPulseDevelopmentServices
$duplicatePorts = $services | Group-Object Port | Where-Object Count -gt 1
Add-Check -Name "Unique backend ports" -Passed ($duplicatePorts.Count -eq 0) `
    -Details $(if ($duplicatePorts.Count -eq 0) { "All backend ports are unique." } else { "Duplicate ports: $($duplicatePorts.Name -join ', ')" }) `
    -Remediation "Correct scripts/dev/ThesisPulse.Development.ps1."

foreach ($service in $services) {
    $projectPath = Join-Path $repositoryRoot $service.Project
    Add-Check -Name $service.Name -Passed (Test-Path -LiteralPath $projectPath -PathType Leaf) `
        -Details $(if (Test-Path -LiteralPath $projectPath -PathType Leaf) { "$($service.Project) on port $($service.Port)" } else { "Project file $($service.Project) is missing." }) `
        -Remediation "Restore the missing project from source control."
}

$frontend = Get-ThesisPulseFrontendDefinition
$allPorts = @($services.Port) + @($frontend.Port)
foreach ($port in $allPorts) {
    $occupied = Test-ThesisPulseTcpPort -Port $port
    Add-Check -Name "Port $port" -Passed (-not $occupied) `
        -Details $(if ($occupied) { "Port is already occupied." } else { "Available" }) `
        -Remediation "Stop the process using port $port or run scripts/dev/Stop-ThesisPulse.ps1 if it belongs to ThesisPulse AI."
}

foreach ($check in $checks) {
    $prefix = if ($check.Passed) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1} - {2}" -f $prefix, $check.Name, $check.Details)
    if (-not $check.Passed -and $check.Remediation) {
        Write-Host ("       Fix: {0}" -f $check.Remediation)
    }
}

if ($ValidationOnly) {
    Write-Host "Validation-only mode completed. No restore, migration, or process launch was performed."
}

if ($failures.Count -gt 0) {
    Write-Error ("Prerequisite validation failed with {0} issue(s)." -f $failures.Count)
    exit 1
}

Write-Host "All ThesisPulse AI local-development prerequisites passed."
exit 0
