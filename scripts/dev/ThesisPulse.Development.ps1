#requires -Version 5.1

Set-StrictMode -Version Latest

function Get-ThesisPulseRepositoryRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

function Get-ThesisPulseDevelopmentServices {
    return @(
        [pscustomobject]@{
            Name = "Trading API"
            Key = "trading-api"
            Project = "src/ThesisPulse.Trading.Api/ThesisPulse.Trading.Api.csproj"
            Port = 60515
            HealthPath = "/health/ready"
            FrontendVariable = "VITE_TRADING_API_BASE_URL"
            FrontendPath = "/local/trading"
        },
        [pscustomobject]@{
            Name = "Market Data Service"
            Key = "market-data"
            Project = "src/ThesisPulse.MarketData.Service/ThesisPulse.MarketData.Service.csproj"
            Port = 5101
            HealthPath = "/health/ready"
            FrontendVariable = $null
            FrontendPath = $null
        },
        [pscustomobject]@{
            Name = "Signal Service"
            Key = "signal"
            Project = "src/ThesisPulse.Signal.Service/ThesisPulse.Signal.Service.csproj"
            Port = 59479
            HealthPath = "/health/ready"
            FrontendVariable = "VITE_SIGNAL_API_BASE_URL"
            FrontendPath = "/local/signal"
        },
        [pscustomobject]@{
            Name = "Thesis Service"
            Key = "thesis"
            Project = "src/ThesisPulse.Thesis.Service/ThesisPulse.Thesis.Service.csproj"
            Port = 59475
            HealthPath = "/health/ready"
            FrontendVariable = "VITE_THESIS_API_BASE_URL"
            FrontendPath = "/local/thesis"
        },
        [pscustomobject]@{
            Name = "Risk Service"
            Key = "risk"
            Project = "src/ThesisPulse.Risk.Service/ThesisPulse.Risk.Service.csproj"
            Port = 59477
            HealthPath = "/health/ready"
            FrontendVariable = "VITE_RISK_API_BASE_URL"
            FrontendPath = "/local/risk"
        },
        [pscustomobject]@{
            Name = "Execution Service"
            Key = "execution"
            Project = "src/ThesisPulse.Execution.Service/ThesisPulse.Execution.Service.csproj"
            Port = 59482
            HealthPath = "/health/ready"
            FrontendVariable = "VITE_EXECUTION_API_BASE_URL"
            FrontendPath = "/local/execution"
        },
        [pscustomobject]@{
            Name = "Portfolio Service"
            Key = "portfolio"
            Project = "src/ThesisPulse.Portfolio.Service/ThesisPulse.Portfolio.Service.csproj"
            Port = 59483
            HealthPath = "/health/ready"
            FrontendVariable = "VITE_PORTFOLIO_API_BASE_URL"
            FrontendPath = "/local/portfolio"
        },
        [pscustomobject]@{
            Name = "Operations Service"
            Key = "operations"
            Project = "src/ThesisPulse.Operations.Service/ThesisPulse.Operations.Service.csproj"
            Port = 59485
            HealthPath = "/health/ready"
            FrontendVariable = "VITE_OPERATIONS_API_BASE_URL"
            FrontendPath = "/local/operations"
        }
    )
}

function Get-ThesisPulseFrontendDefinition {
    return [pscustomobject]@{
        Name = "React Frontend"
        Key = "frontend"
        WorkingDirectory = "frontend-react"
        Port = 5173
        HealthPath = "/"
        Url = "http://localhost:5173"
    }
}

function Get-ThesisPulseRuntimeDirectory {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    return Join-Path $RepositoryRoot ".thesispulse-dev"
}

function Get-ThesisPulseProcessManifestPath {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    return Join-Path (Get-ThesisPulseRuntimeDirectory -RepositoryRoot $RepositoryRoot) "processes.json"
}

function Get-ThesisPulseServiceUrl {
    param([Parameter(Mandatory = $true)]$Service)
    return "http://localhost:$($Service.Port)"
}

function Test-ThesisPulseTcpPort {
    param(
        [Parameter(Mandatory = $true)][int]$Port,
        [string]$HostName = "127.0.0.1",
        [int]$TimeoutMilliseconds = 500
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMilliseconds, $false)) {
            return $false
        }
        $client.EndConnect($async)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Wait-ThesisPulseHttpEndpoint {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [int]$TimeoutSeconds = 90,
        [int]$PollMilliseconds = 1000
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                return $true
            }
        }
        catch {
            Start-Sleep -Milliseconds $PollMilliseconds
        }
    } while ((Get-Date) -lt $deadline)

    return $false
}

function Write-ThesisPulseProcessManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object[]]$Processes
    )

    $payload = [pscustomobject]@{
        ContractVersion = "1.0.0"
        Environment = "PAPER"
        CreatedAtUtc = [DateTime]::UtcNow.ToString("o")
        Processes = $Processes
    }
    $payload | ConvertTo-Json -Depth 8 | Set-Content -Path $Path -Encoding UTF8
}
