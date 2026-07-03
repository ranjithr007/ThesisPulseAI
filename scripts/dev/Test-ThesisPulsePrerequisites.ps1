#requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$ValidationOnly,
    [switch]$SkipPortChecks
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
        $firstLine = & $CommandName --version 2>$null | Select-Object -First 1
        return $(if ($null -eq $firstLine) { "" } else { $firstLine.ToString().Trim() })
    }
    catch {
        return ""
    }
}

$git = Get-Command git -ErrorAction SilentlyContinue
Add-Check -Name "Git" -Passed ($null -ne $git) `
    -Details $(if ($null -ne $git) { Get-CommandVersionText -CommandName $git.Source } else { "git is not available on PATH." }) `
    -Remediation "Install Git for Windows, enable the command-line PATH option, then reopen PowerShell."

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetVersion = if ($null -ne $dotnet) { Get-CommandVersionText -CommandName $dotnet.Source } else { "" }
$dotnetMajor = 0
if ($dotnetVersion -match "^(\d+)") { $dotnetMajor = [int]$Matches[1] }
Add-Check -Name ".NET SDK" -Passed (($null -ne $dotnet) -and $dotnetMajor -ge 8) `
    -Details $(if ($null -ne $dotnet) { ".NET SDK $dotnetVersion" } else { "dotnet is not available on PATH." }) `
    -Remediation "Install the .NET 8 SDK or later and reopen PowerShell."

$node = Get-Command node -ErrorAction SilentlyContinue
$nodeVersion = if ($null -ne $node) { Get-CommandVersionText -CommandName $node.Source } else { "" }
$nodeMajor = 0
if ($nodeVersion -match "^v?(\d+)") { $nodeMajor = [int]$Matches[1] }
Add-Check -Name "Node.js" -Passed (($null -ne $node) -and $nodeMajor -ge 22) `
    -Details $(if ($null -ne $node) { "Node.js $nodeVersion" } else { "node is not available on PATH." }) `
    -Remediation "Install Node.js 22 LTS or later and reopen PowerShell."

$npm = Get-Command npm -ErrorAction SilentlyContinue
Add-Check -Name "npm" -Passed ($null -ne $npm) `
    -Details $(if ($null -ne $npm) { Get-CommandVersionText -CommandName $npm.Source } else { "npm is not available on PATH." }) `
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
    $present = Test-Path -LiteralPath $fullPath -PathType Leaf
    Add-Check -Name "File $relativePath" -Passed $present `
        -Details $(if ($present) { "Present" } else { "Required file is missing." }) `
        -Remediation "Run git status and git pull origin main from the repository root."
}

$services = @(Get-ThesisPulseDevelopmentServices)
$duplicatePorts = @($services | Group-Object Port | Where-Object Count -gt 1)
Add-Check -Name "Unique backend ports" -Passed ($duplicatePorts.Count -eq 0) `
    -Details $(if ($duplicatePorts.Count -eq 0) { "All backend ports are unique." } else { "Duplicate ports: $($duplicatePorts.Name -join ', ')" }) `
    -Remediation "Correct scripts/dev/ThesisPulse.Development.ps1."

foreach ($service in $services) {
    $projectPath = Join-Path $repositoryRoot $service.Project
    $present = Test-Path -LiteralPath $projectPath -PathType Leaf
    Add-Check -Name $service.Name -Passed $present `
        -Details $(if ($present) { "$($service.Project) on port $($service.Port)" } else { "Project file $($service.Project) is missing." }) `
        -Remediation "Restore the missing project from source control."
}

if (-not $SkipPortChecks) {
    $frontend = Get-ThesisPulseFrontendDefinition
    $allPorts = @($services | ForEach-Object { $_.Port }) + @($frontend.Port)
    foreach ($port in $allPorts) {
        $occupied = Test-ThesisPulseTcpPort -Port $port
        Add-Check -Name "Port $port" -Passed (-not $occupied) `
            -Details $(if ($occupied) { "Port is already occupied." } else { "Available" }) `
            -Remediation "Stop the application using port $port, then rerun the validation."
    }
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
    [Console]::Error.WriteLine("Prerequisite validation failed with $($failures.Count) issue(s).")
    exit 1
}

Write-Host "All ThesisPulse AI local-development prerequisites passed."
exit 0
