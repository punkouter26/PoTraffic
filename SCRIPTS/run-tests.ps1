# run-tests.ps1 — Run Unit, Integration, and E2E tests in sequence.
# Requires Docker Desktop (for integration Testcontainers) and the app running on port 5000 for E2E.
# Run from repo root: ./SCRIPTS/run-tests.ps1

[CmdletBinding()]
param(
    [switch]$UnitOnly,
    [switch]$IntegrationOnly,
    [switch]$E2eOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Join-Path $PSScriptRoot '..'
$e2eBaseUrl = $env:E2E_BASE_URL ?? 'http://localhost:5000'

function Run-Suite {
    param([string]$Name, [string]$Project)
    Write-Host "`n--- $Name ---" -ForegroundColor Cyan
    dotnet test (Join-Path $root $Project) --no-build --logger 'trx' --results-directory (Join-Path $root 'TestResults' $Name)
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

Write-Host "`n=== PoTraffic Test Run ===" -ForegroundColor Cyan

# Build everything first
Write-Host "`nBuilding solution..." -ForegroundColor Yellow
dotnet build (Join-Path $root 'PoTraffic.slnx') --configuration Debug
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (!$IntegrationOnly -and !$E2eOnly) { Run-Suite 'UnitTests'        'tests/PoTraffic.UnitTests' }
if (!$UnitOnly        -and !$E2eOnly) { Run-Suite 'IntegrationTests' 'tests/PoTraffic.IntegrationTests' }
if (!$UnitOnly -and !$IntegrationOnly) {
    Write-Host "`n--- E2E (Playwright) ---" -ForegroundColor Cyan
    Write-Host "  NOTE: API must be running on $e2eBaseUrl (start-dev.ps1)." -ForegroundColor Yellow

    # Warm-up ping to trigger JIT/AOT compilation before Playwright launches.
    # Prevents first-test timeout failures from cold-start latency.
    $ready = Warmup-App -Url $e2eBaseUrl
    if (!$ready) {
        Write-Warning "E2E tests may fail — app is not responding at $e2eBaseUrl."
    }

    Run-Suite 'E2ETests' 'tests/PoTraffic.E2ETests'
}

Write-Host "`nAll selected test suites passed." -ForegroundColor Green
