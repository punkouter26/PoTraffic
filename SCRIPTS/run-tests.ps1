# run-tests.ps1 - Run Unit, Integration, and E2E tests in sequence.
# Integration owns Azurite through Testcontainers. E2E starts the Testing host.
# Run from repo root: ./SCRIPTS/run-tests.ps1

[CmdletBinding()]
param(
    [switch]$UnitOnly,
    [switch]$IntegrationOnly,
    [switch]$E2eOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$e2eBaseUrl = $env:E2E_BASE_URL ?? 'http://localhost:5150'
$testResultsRoot = Join-Path $root 'TestResults'

function Run-Suite {
    param([string]$Name, [string]$Project)
    Write-Host "`n--- $Name ---" -ForegroundColor Cyan
    dotnet test (Join-Path $root $Project) --no-build --logger 'trx' --results-directory (Join-Path $testResultsRoot $Name)
    if ($LASTEXITCODE -ne 0) { throw "$Name tests failed." }
    Write-Host "$Name PASSED" -ForegroundColor Green
}

function Warmup-App {
    param([string]$Url, [int]$MaxRetries = 10, [int]$DelaySeconds = 3)
    Write-Host "`nWarming up app at $Url ..." -ForegroundColor Yellow
    for ($i = 1; $i -le $MaxRetries; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "$Url/health" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
            if ($response.StatusCode -lt 500) {
                Write-Host "  App is ready (attempt $i)." -ForegroundColor Green
                return $true
            }
        } catch {
            Write-Host "  Attempt $i/$MaxRetries — app not ready yet ($($_.Exception.Message))" -ForegroundColor DarkYellow
            Start-Sleep -Seconds $DelaySeconds
        }
    }
    Write-Warning "App at $Url did not become ready after $MaxRetries attempts."
    return $false
}

function Get-PortFromUrl {
    param([string]$Url)
    $uri = [Uri]$Url
    if ($uri.IsDefaultPort) {
        if ($uri.Scheme -eq 'https') { return 443 }
        return 80
    }
    return $uri.Port
}

function Stop-PortProcesses {
    param([int]$Port)
    $pids = (netstat -ano 2>$null |
        Select-String ":$Port\s" |
        ForEach-Object { ($_ -split '\s+')[-1] } |
        Where-Object { $_ -match '^\d+$' } |
        Sort-Object -Unique)

    foreach ($processId in $pids) {
        try {
            $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($proc -and ($proc.ProcessName -in @('dotnet', 'PoTraffic.Api'))) {
                Write-Host "  Stopping stale $($proc.ProcessName) PID $processId on port $Port" -ForegroundColor Yellow
                Stop-Process -Id $processId -Force
            }
        } catch { }
    }
}

function Install-PlaywrightChromium {
    $script = Join-Path $root 'tests/PoTraffic.E2ETests/bin/Debug/net10.0/playwright.ps1'
    if (!(Test-Path $script)) {
        throw "Playwright installer not found at $script. Build the E2E project first."
    }

    Write-Host "  Ensuring Playwright Chromium is installed..." -ForegroundColor Yellow
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $script install chromium
    if ($LASTEXITCODE -ne 0) {
        throw 'Playwright Chromium install failed.'
    }
}

function Start-TestingHost {
    param([string]$Url)

    $port = Get-PortFromUrl $Url
    Stop-PortProcesses -Port $port

    $hostLogDir = Join-Path $testResultsRoot 'E2EHost'
    New-Item -ItemType Directory -Force -Path $hostLogDir | Out-Null
    $stdout = Join-Path $hostLogDir 'stdout.log'
    $stderr = Join-Path $hostLogDir 'stderr.log'

    Write-Host "  Starting Testing host at $Url ..." -ForegroundColor Yellow
    $process = Start-Process -FilePath 'dotnet' `
        -ArgumentList @('run', '--project', (Join-Path $root 'src/PoTraffic.Api/PoTraffic.Api.csproj'), '--launch-profile', 'Testing', '--no-build') `
        -WorkingDirectory $root `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -PassThru

    return $process
}

function Test-DockerHealth {
    try {
        docker info *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

Write-Host "`n=== PoTraffic Test Run ===" -ForegroundColor Cyan

# Build everything first
Write-Host "`nBuilding solution..." -ForegroundColor Yellow
dotnet build (Join-Path $root 'PoTraffic.slnx') --configuration Debug
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (!$IntegrationOnly -and !$E2eOnly) { Run-Suite 'UnitTests'        'tests/PoTraffic.UnitTests' }
if (!$UnitOnly        -and !$E2eOnly) { Run-Suite 'IntegrationTests' 'tests/PoTraffic.IntegrationTests' }
if (!$UnitOnly -and !$IntegrationOnly) {
    Write-Host "`n--- E2E (Playwright) ---" -ForegroundColor Cyan
    if (!(Test-DockerHealth)) {
        throw 'Docker daemon is not reachable. Integration/E2E storage is managed by Testcontainers and requires Docker.'
    }

    Install-PlaywrightChromium
    $env:E2E_BASE_URL = $e2eBaseUrl

    $hostProcess = $null
    try {
        $hostProcess = Start-TestingHost -Url $e2eBaseUrl
        $ready = Warmup-App -Url $e2eBaseUrl -MaxRetries 30 -DelaySeconds 2
        if (!$ready) { throw "Testing host did not become ready at $e2eBaseUrl." }
        Run-Suite 'E2ETests' 'tests/PoTraffic.E2ETests'
    }
    finally {
        if ($hostProcess -and !$hostProcess.HasExited) {
            Write-Host "  Stopping Testing host PID $($hostProcess.Id)." -ForegroundColor Yellow
            Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "`nAll selected test suites passed." -ForegroundColor Green
