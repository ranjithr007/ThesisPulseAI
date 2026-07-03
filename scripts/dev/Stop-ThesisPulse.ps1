#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ThesisPulse.Development.ps1")

$repositoryRoot = Get-ThesisPulseRepositoryRoot
$manifestPath = Get-ThesisPulseProcessManifestPath -RepositoryRoot $repositoryRoot

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Write-Host "No ThesisPulse AI development process manifest exists. Nothing was stopped."
    exit 0
}

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
catch {
    Write-Error "The process manifest is unreadable. No processes were stopped. Remove it manually only after verifying the recorded processes are no longer running: $manifestPath"
    exit 1
}

if ($manifest.ContractVersion -ne "1.0.0" -or $manifest.Environment -ne "PAPER") {
    Write-Error "The process manifest is not a supported ThesisPulse AI PAPER manifest. No processes were stopped."
    exit 1
}

$failures = New-Object System.Collections.Generic.List[string]
$processes = @($manifest.Processes) | Sort-Object ProcessId -Descending
foreach ($entry in $processes) {
    $process = Get-Process -Id ([int]$entry.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        Write-Host "[NOT RUNNING] $($entry.Name) process $($entry.ProcessId)"
        continue
    }

    $actualStartTimeUtc = $process.StartTime.ToUniversalTime()
    $recordedStartTimeUtc = [DateTime]::Parse($entry.ProcessStartTimeUtc).ToUniversalTime()
    $difference = [Math]::Abs(($actualStartTimeUtc - $recordedStartTimeUtc).TotalSeconds)
    if ($difference -gt 2) {
        $failures.Add("Skipped $($entry.Name) PID $($entry.ProcessId) because the process start time does not match the launcher manifest.")
        continue
    }

    try {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        Write-Host "[STOPPED] $($entry.Name) process $($entry.ProcessId)"
    }
    catch {
        $failures.Add("Failed to stop $($entry.Name) PID $($entry.ProcessId): $($_.Exception.Message)")
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Warning $failure
    }
    Write-Error "One or more launcher-owned processes could not be safely stopped. The manifest was retained."
    exit 1
}

Remove-Item -LiteralPath $manifestPath -Force
Write-Host "ThesisPulse AI PAPER development processes stopped. Logs were retained under .thesispulse-dev/logs."
exit 0
