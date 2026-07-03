#requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$SkipRestore,
    [switch]$SkipBuild,
    [switch]$SkipFrontendInstall,
    [switch]$RunMigrations,
    [string]$DatabaseConnection = $env:THESISPULSE_DATABASE_CONNECTION,
    [ValidateRange(15, 600)][int]$StartupTimeoutSeconds = 90,
    [switch]$NoBrowser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ThesisPulse.Development.ps1")

$repositoryRoot = Get-ThesisPulseRepositoryRoot
$runtimeDirectory = Get-ThesisPulseRuntimeDirectory -RepositoryRoot $repositoryRoot
$manifestPath = Get-ThesisPulseProcessManifestPath -RepositoryRoot $repositoryRoot
$logDirectory = Join-Path $runtimeDirectory "logs"
$prerequisiteScript = Join-Path $PSScriptRoot "Test-ThesisPulsePrerequisites.ps1"
$startedProcesses = New-Object System.Collections.Generic.List[object]

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage,
        [string]$WorkingDirectory = $repositoryRoot
    )

    Push-Location $WorkingDirectory
    try {
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FailureMessage Exit code: $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Save-StartedProcesses {
    $manifestProcesses = @($startedProcesses | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            Key = $_.Key
            ProcessId = $_.Process.Id
            ProcessStartTimeUtc = $_.Process.StartTime.ToUniversalTime().ToString("o")
            Url = $_.Url
            HealthUrl = $_.HealthUrl
            StandardOutputLog = $_.StandardOutputLog
            StandardErrorLog = $_.StandardErrorLog
        }
    })
    Write-ThesisPulseProcessManifest -Path $manifestPath -Processes $manifestProcesses
}

function Stop-StartedProcesses {
    foreach ($entry in @($startedProcesses | Sort-Object { $_.Process.Id } -Descending)) {
        try {
            if (-not $entry.Process.HasExited) {
                Stop-Process -Id $entry.Process.Id -Force -ErrorAction Stop
            }
        }
        catch {
            Write-Warning "Could not stop $($entry.Name) process $($entry.Process.Id): $($_.Exception.Message)"
        }
    }
    if (Test-Path -LiteralPath $manifestPath) {
        Remove-Item -LiteralPath $manifestPath -Force
    }
}

function Start-ManagedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Key,
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$HealthUrl
    )

    $stdout = Join-Path $logDirectory "$Key.stdout.log"
    $stderr = Join-Path $logDirectory "$Key.stderr.log"
    $process = Start-Process -FilePath $Executable `
        -ArgumentList $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru `
        -WindowStyle Hidden

    Start-Sleep -Milliseconds 250
    if ($process.HasExited) {
        $errorText = if (Test-Path -LiteralPath $stderr) {
            (Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue)
        } else {
            "No error log was produced."
        }
        throw "$Name exited during startup. $errorText"
    }

    $entry = [pscustomobject]@{
        Name = $Name
        Key = $Key
        Process = $process
        Url = $Url
        HealthUrl = $HealthUrl
        StandardOutputLog = $stdout
        StandardErrorLog = $stderr
    }
    $startedProcesses.Add($entry)
    Save-StartedProcesses
    return $entry
}

if (Test-Path -LiteralPath $manifestPath) {
    throw "A ThesisPulse AI process manifest already exists at '$manifestPath'. Run scripts/dev/Stop-ThesisPulse.ps1 first."
}

& $prerequisiteScript -ValidationOnly
if ($LASTEXITCODE -ne 0) {
    throw "Prerequisite validation failed. Correct the reported issues before startup."
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $logDirectory -File -ErrorAction SilentlyContinue | Remove-Item -Force

$dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source
$npmCommand = (Get-Command npm -ErrorAction Stop).Source
$nodeCommand = (Get-Command node -ErrorAction Stop).Source

try {
    if (-not $SkipRestore) {
        Write-Host "Restoring .NET solution..."
        Invoke-CheckedCommand -Command $dotnetCommand -Arguments @("restore", "ThesisPulseAI.sln") `
            -FailureMessage ".NET restore failed."
    }

    if (-not $SkipBuild) {
        Write-Host "Building .NET solution..."
        $buildArguments = @("build", "ThesisPulseAI.sln", "--configuration", "Debug")
        if (-not $SkipRestore) { $buildArguments += "--no-restore" }
        Invoke-CheckedCommand -Command $dotnetCommand -Arguments $buildArguments `
            -FailureMessage ".NET build failed."
    }

    if (-not $SkipFrontendInstall) {
        Write-Host "Installing frontend dependencies with npm ci..."
        Invoke-CheckedCommand -Command $npmCommand -Arguments @("ci") `
            -FailureMessage "Frontend dependency installation failed." `
            -WorkingDirectory (Join-Path $repositoryRoot "frontend-react")
    }

    if ($RunMigrations) {
        if ([string]::IsNullOrWhiteSpace($DatabaseConnection)) {
            throw "-RunMigrations requires -DatabaseConnection or THESISPULSE_DATABASE_CONNECTION."
        }
        Write-Host "Applying SQL Server migrations in PAPER mode..."
        $env:THESISPULSE_DATABASE_CONNECTION = $DatabaseConnection
        $env:THESISPULSE_MIGRATION_ENVIRONMENT = "PAPER"
        Invoke-CheckedCommand -Command $dotnetCommand `
            -Arguments @("run", "--project", "src/ThesisPulse.DatabaseMigrator/ThesisPulse.DatabaseMigrator.csproj", "--no-build") `
            -FailureMessage "Database migration failed."
    }

    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:Platform__Environment = "PAPER"
    $env:Platform__LiveExecutionEnabled = "false"
    if (-not [string]::IsNullOrWhiteSpace($DatabaseConnection)) {
        $env:ConnectionStrings__OperationalDatabase = $DatabaseConnection
    }

    $services = Get-ThesisPulseDevelopmentServices
    foreach ($service in $services) {
        $projectDirectory = Split-Path $service.Project -Parent
        $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($service.Project)
        $assemblyPath = Join-Path $projectDirectory "bin/Debug/net8.0/$assemblyName.dll"
        $assemblyFullPath = Join-Path $repositoryRoot $assemblyPath
        if (-not (Test-Path -LiteralPath $assemblyFullPath -PathType Leaf)) {
            throw "Build output '$assemblyPath' is missing. Run without -SkipBuild."
        }

        $url = Get-ThesisPulseServiceUrl -Service $service
        $healthUrl = "$url$($service.HealthPath)"
        Write-Host "Starting $($service.Name) at $url..."
        Start-ManagedProcess -Name $service.Name -Key $service.Key -Executable $dotnetCommand `
            -Arguments @($assemblyPath, "--urls", $url) `
            -WorkingDirectory $repositoryRoot -Url $url -HealthUrl $healthUrl | Out-Null
    }

    $frontend = Get-ThesisPulseFrontendDefinition
    foreach ($service in $services | Where-Object { -not [string]::IsNullOrWhiteSpace($_.FrontendVariable) }) {
        [Environment]::SetEnvironmentVariable($service.FrontendVariable, (Get-ThesisPulseServiceUrl -Service $service), "Process")
    }
    $env:VITE_PORTFOLIO_CODE = "PAPER-DEFAULT"
    $env:VITE_PORTFOLIO_CURRENCY = "INR"
    $env:VITE_EXECUTION_LIFECYCLE_LIMIT = "50"
    $env:VITE_PNL_MAXIMUM_AGE_MINUTES = "10"

    $frontendDirectory = Join-Path $repositoryRoot $frontend.WorkingDirectory
    $viteScript = Join-Path $frontendDirectory "node_modules/vite/bin/vite.js"
    if (-not (Test-Path -LiteralPath $viteScript -PathType Leaf)) {
        throw "Vite is not installed at '$viteScript'. Run without -SkipFrontendInstall."
    }

    Write-Host "Starting React frontend at $($frontend.Url)..."
    Start-ManagedProcess -Name $frontend.Name -Key $frontend.Key -Executable $nodeCommand `
        -Arguments @("node_modules/vite/bin/vite.js", "--host", "127.0.0.1", "--port", "$($frontend.Port)", "--strictPort") `
        -WorkingDirectory $frontendDirectory `
        -Url $frontend.Url -HealthUrl "$($frontend.Url)$($frontend.HealthPath)" | Out-Null

    foreach ($entry in $startedProcesses) {
        Write-Host "Waiting for $($entry.Name) health endpoint $($entry.HealthUrl)..."
        if (-not (Wait-ThesisPulseHttpEndpoint -Url $entry.HealthUrl -TimeoutSeconds $StartupTimeoutSeconds)) {
            throw "$($entry.Name) did not become healthy within $StartupTimeoutSeconds seconds. Review '$($entry.StandardErrorLog)' and '$($entry.StandardOutputLog)'."
        }
        Write-Host "[READY] $($entry.Name)"
    }

    Write-Host ""
    Write-Host "ThesisPulse AI PAPER stack is ready."
    Write-Host "Frontend: $($frontend.Url)"
    Write-Host "Process manifest: $manifestPath"
    Write-Host "Logs: $logDirectory"
    Write-Host "Stop command: .\scripts\dev\Stop-ThesisPulse.ps1"

    if (-not $NoBrowser) {
        Start-Process $frontend.Url | Out-Null
    }
}
catch {
    $failureMessage = $_.Exception.Message
    Stop-StartedProcesses
    [Console]::Error.WriteLine("ThesisPulse AI startup failed: $failureMessage")
    exit 1
}

exit 0
