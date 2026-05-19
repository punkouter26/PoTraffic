# start-dev.ps1 — Kill stale dotnet processes on 5000/5001, start Azurite, then launch the API.
# Run from repo root: ./SCRIPTS/start-dev.ps1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host "`n=== PoTraffic Dev Startup ===" -ForegroundColor Cyan

# 1. Kill any dotnet processes already bound to 5000 or 5001
foreach ($port in @(5000, 5001)) {
    $pids = (netstat -ano 2>$null |
        Select-String ":$port\s" |
        ForEach-Object { ($_ -split '\s+')[-1] } |
        Where-Object { $_ -match '^\d+$' } |
        Sort-Object -Unique)

    foreach ($p in $pids) {
        try {
            $proc = Get-Process -Id $p -ErrorAction SilentlyContinue
            if ($proc -and $proc.Name -like '*dotnet*') {
                Write-Host "  Stopping stale dotnet process PID $p on port $port" -ForegroundColor Yellow
                Stop-Process -Id $p -Force
            }
        } catch { }
    }
}

# 2. Ensure Azurite is running via Docker Compose
Write-Host "`nStarting Azurite (docker compose up -d)..." -ForegroundColor Cyan
docker compose up -d
if ($LASTEXITCODE -ne 0) {
    Write-Error "docker compose up failed. Is Docker Desktop running?"
}

# 3. Launch API (Development profile, HTTPS)
Write-Host "`nLaunching PoTraffic API on https://localhost:5001 ..." -ForegroundColor Green
Set-Location (Join-Path $PSScriptRoot '..' 'src' 'PoTraffic.Api')
dotnet run --launch-profile https
