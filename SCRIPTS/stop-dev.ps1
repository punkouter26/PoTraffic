# stop-dev.ps1 — Stop local Docker containers (Azurite).
# Run from repo root: ./SCRIPTS/stop-dev.ps1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "`n=== PoTraffic Dev Shutdown ===" -ForegroundColor Cyan

Set-Location (Join-Path $PSScriptRoot '..')
docker compose down
Write-Host "Azurite stopped." -ForegroundColor Green
