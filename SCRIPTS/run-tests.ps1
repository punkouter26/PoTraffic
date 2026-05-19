# run-tests.ps1 — Run Unit, Integration, and E2E tests in sequence.
# Requires Docker Desktop (Testcontainers) and the app running on port 5150 for E2E.
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

function Run-Suite {
    param([string]$Name, [string]$Project)
    Write-Host "`n--- $Name ---" -ForegroundColor Cyan
    dotnet test (Join-Path $root $Project) --no-build --logger 'trx' --results-directory (Join-Path $root 'TestResults' $Name)
    if ($LASTEXITCODE -ne 0) { throw "$Name tests failed." }
    Write-Host "$Name PASSED" -ForegroundColor Green
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
    Write-Host "  NOTE: API must be running on http://localhost:5150 (Testing profile)." -ForegroundColor Yellow
    Run-Suite 'E2ETests' 'tests/PoTraffic.E2ETests'
}

Write-Host "`nAll selected test suites passed." -ForegroundColor Green
